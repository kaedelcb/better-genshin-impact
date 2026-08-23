using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoOnline;

namespace BetterGenshinImpact.Service.Instance.MessageHandlers;

/// <summary>
/// 负责分发和处理 JSON 格式的实例 IPC 请求。
/// 连接建立、重连与关闭仍由 <see cref="InstanceService"/> 编排，本类只处理消息语义。
/// </summary>
internal sealed class InstanceRequestHandler
{
    private static readonly TimeSpan ForwardRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly InstanceContext _context;
    private readonly InstanceMessageState _state;
    private readonly RelativeMouseMessageHandler _relativeMouseMessageHandler;
    private readonly Action<string[]> _enqueueActivation;
    private readonly Action<WebViewMessage> _dispatchWebViewMessage;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, InstanceIpcEnvelope> _activationResponses = new();
    /// <summary>最近一次执行过的独立任务名（30 秒内保留，用于助手检测"联机锄地上线"等轻量任务）。</summary>
    private string? _recentTaskName;
    private DateTime _recentTaskNameTime = DateTime.MinValue;
    /// <summary>最近一次执行过的 task.start 代序号（幂等保护：同一 generation 只执行一次）。</summary>
    private int _lastExecutedTaskGeneration;

    internal InstanceRequestHandler(
        InstanceContext context,
        InstanceMessageState state,
        RelativeMouseMessageHandler relativeMouseMessageHandler,
        Action<string[]> enqueueActivation,
        Action<WebViewMessage> dispatchWebViewMessage,
        ILogger logger)
    {
        _context = context;
        _state = state;
        _relativeMouseMessageHandler = relativeMouseMessageHandler;
        _enqueueActivation = enqueueActivation;
        _dispatchWebViewMessage = dispatchWebViewMessage;
        _logger = logger;
    }

    /// <summary>
    /// 将请求路由到对应处理方法，并把可预期的请求错误转换为失败响应。
    /// </summary>
    internal async Task<InstanceIpcEnvelope?> HandleAsync(
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Operation switch
            {
                InstanceOperations.Ping => InstanceIpcEnvelope.Response(
                    request,
                    _context.ToEndpoint()),
                InstanceOperations.ConnectionOpen =>
                    await HandleConnectionOpenAsync(
                        connection,
                        request,
                        cancellationToken).ConfigureAwait(false),
                InstanceOperations.ActivationDispatch => HandleActivationDispatch(
                    connection,
                    request),
                InstanceOperations.RelativeMouseSubscribe =>
                    _relativeMouseMessageHandler.HandleSubscribe(connection, request),
                InstanceOperations.RelativeMouseUnsubscribe =>
                    _relativeMouseMessageHandler.HandleUnsubscribe(connection, request),
                InstanceOperations.WebViewList => HandleWebViewList(connection, request),
                InstanceOperations.WebViewSend =>
                    await HandleWebViewSendAsync(
                        connection,
                        request,
                        cancellationToken).ConfigureAwait(false),
                InstanceOperations.WebViewMessage => HandleWebViewMessage(connection, request),
                InstanceOperations.TaskStop => HandleTaskStop(connection, request),
                InstanceOperations.TaskStart => await HandleTaskStart(connection, request),
                InstanceOperations.TaskStatus => HandleTaskStatus(connection, request),
                InstanceOperations.ConfigList => HandleConfigList(connection, request),
                InstanceOperations.ExecuteHotkey => await HandleExecuteHotkey(connection, request),
                InstanceOperations.CloseGame => HandleCloseGame(connection, request),
                InstanceOperations.SetTaskEnabled => await HandleSetTaskEnabled(connection, request),
                InstanceOperations.TaskSuspend => await HandleTaskSuspend(connection, request),
                InstanceOperations.TaskResume => await HandleTaskResume(connection, request),
                _ => InstanceIpcEnvelope.Failure(
                    request,
                    "unsupported_operation",
                    $"不支持的实例 IPC 操作：{request.Operation}")
            };

            // TODO: 多实例独立任务入口预留。
            // 后续在此增加目标实例选择、任务下发与状态回传。
            // 当前版本不注册任何 task.* 操作。
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or IOException
                                          or TimeoutException
                                          or JsonException)
        {
            _logger.LogWarning(exception, "处理实例 IPC 请求失败：{Operation}", request.Operation);
            return InstanceIpcEnvelope.Failure(
                request,
                "invalid_request",
                exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// 激活消息按 RequestId 去重，避免管道重试导致主窗口被重复激活。
    /// </summary>
    private InstanceIpcEnvelope HandleActivationDispatch(
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        if (_context.InstanceType == BetterGiInstanceType.Primary
            || connection.RemoteEndpoint?.InstanceType != BetterGiInstanceType.Primary)
        {
            throw new InvalidOperationException("只有根实例可以向 BetterGI 客户端分发激活消息。");
        }

        if (_activationResponses.TryGetValue(request.RequestId, out var cachedResponse))
        {
            return cachedResponse;
        }

        var activation =
            request.Data?.ToObject<ActivationDispatchRequest>(InstanceIpcProtocol.Serializer)
            ?? throw new ArgumentException("激活请求缺少命令行参数。");
        _enqueueActivation(activation.Arguments);
        return CacheActivationResponse(
            request.RequestId,
            InstanceIpcEnvelope.Response(request));
    }

    /// <summary>
    /// 校验子实例身份和启动记录后，将当前连接登记为有效子连接。
    /// v2 不再校验父实例 ID 或启动记录，而是使用根管道客户端的真实 PID 和 Session。
    /// </summary>
    private async Task<InstanceIpcEnvelope> HandleConnectionOpenAsync(
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
    {
        if (_context.InstanceType != BetterGiInstanceType.Primary)
        {
            throw new InvalidOperationException("只有根实例可以接受客户端连接登记。");
        }
        if (connection.RemoteEndpoint is not null)
        {
            throw new InvalidOperationException("当前管道连接已经完成登记。");
        }
        if (connection.ClientProcessId is not { } processId
            || connection.ClientSessionId is not { } sessionId)
        {
            throw new InvalidOperationException("无法取得命名管道客户端的进程或 Session 信息。");
        }

        var open =
            request.Data?.ToObject<ConnectionOpenRequest>(InstanceIpcProtocol.Serializer)
            ?? throw new ArgumentException("连接登记请求缺少数据。");
        if (open.RequestedType == BetterGiInstanceType.WebView)
        {
            var endpoint = CreateEndpoint(
                BetterGiInstanceType.WebView,
                processId,
                sessionId);
            connection.RemoteEndpoint = endpoint;
            RegisteredInstanceConnection? replaced = null;
            lock (_state.RegistrationLock)
            {
                if (_state.WebViewConnectionsByProcessId.TryGetValue(
                        processId,
                        out var existing)
                    && !ReferenceEquals(existing.Connection, connection))
                {
                    replaced = existing;
                }
                _state.WebViewConnectionsByProcessId[processId] =
                    new RegisteredInstanceConnection(endpoint, connection);
            }
            if (replaced is not null)
            {
                _ = replaced.Connection.DisposeAsync().AsTask();
            }

            _logger.LogInformation(
                "WebView 已连接根实例：进程 {ProcessId}，Session {SessionId}",
                processId,
                sessionId);
            return CreateOpenResponse(
                request,
                ConnectionOpenDisposition.Accepted,
                BetterGiInstanceType.WebView);
        }

        if (open.RequestedType == BetterGiInstanceType.ChildSession
            && sessionId == _context.WindowsSessionId)
        {
            throw new InvalidOperationException(
                "ChildSession 不能与根实例位于相同 Windows Session。");
        }

        if (sessionId == _context.WindowsSessionId)
        {
            if (_activationResponses.TryGetValue(request.RequestId, out var cachedResponse))
            {
                return cachedResponse;
            }

            _enqueueActivation(open.Arguments);
            return CacheActivationResponse(
                request.RequestId,
                CreateOpenResponse(
                    request,
                    ConnectionOpenDisposition.ActivationForwarded,
                    BetterGiInstanceType.Primary));
        }

        RegisteredInstanceConnection? duplicate;
        RegisteredInstanceConnection? replacedConnection = null;
        var childEndpoint = CreateEndpoint(
            BetterGiInstanceType.ChildSession,
            processId,
            sessionId);
        lock (_state.RegistrationLock)
        {
            _state.BetterGiConnectionsBySession.TryGetValue(sessionId, out duplicate);
            var canReplace = duplicate is null
                             || duplicate.Endpoint.ProcessId == processId
                             || open.RestartFromProcessId == duplicate.Endpoint.ProcessId;
            if (canReplace)
            {
                if (duplicate is not null
                    && !ReferenceEquals(duplicate.Connection, connection))
                {
                    replacedConnection = duplicate;
                }
                connection.RemoteEndpoint = childEndpoint;
                _state.BetterGiConnectionsBySession[sessionId] =
                    new RegisteredInstanceConnection(childEndpoint, connection);
                duplicate = null;
            }
        }

        if (duplicate is not null)
        {
            if (_activationResponses.TryGetValue(request.RequestId, out var cachedResponse))
            {
                return cachedResponse;
            }

            try
            {
                var activationResponse = await duplicate.Connection.SendRequestAsync(
                    InstanceOperations.ActivationDispatch,
                    new ActivationDispatchRequest { Arguments = open.Arguments },
                    ForwardRequestTimeout,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccessfulResponse(activationResponse);
                return CacheActivationResponse(
                    request.RequestId,
                    CreateOpenResponse(
                        request,
                        ConnectionOpenDisposition.ActivationForwarded,
                        BetterGiInstanceType.ChildSession));
            }
            catch (Exception exception) when (exception is IOException
                                              or TimeoutException
                                              or OperationCanceledException)
            {
                _logger.LogDebug(
                    exception,
                    "向 Session {SessionId} 的现有 BetterGI 转发激活失败，改为接纳新连接",
                    sessionId);
                lock (_state.RegistrationLock)
                {
                    if (_state.BetterGiConnectionsBySession.TryGetValue(
                            sessionId,
                            out var current)
                        && ReferenceEquals(current.Connection, duplicate.Connection))
                    {
                        connection.RemoteEndpoint = childEndpoint;
                        _state.BetterGiConnectionsBySession[sessionId] =
                            new RegisteredInstanceConnection(childEndpoint, connection);
                        replacedConnection = duplicate;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Session {sessionId} 的 BetterGI 连接已发生变化。");
                    }
                }
            }
        }

        if (replacedConnection is not null)
        {
            _ = replacedConnection.Connection.DisposeAsync().AsTask();
        }

        _logger.LogInformation(
            "桌面分身 BetterGI 已连接根实例：进程 {ProcessId}，Session {SessionId}",
            processId,
            sessionId);
        return CreateOpenResponse(
            request,
            ConnectionOpenDisposition.Accepted,
            BetterGiInstanceType.ChildSession);
    }

    private InstanceIpcEnvelope HandleWebViewList(
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        var requester = RequireRegisteredEndpoint(connection);
        if (requester.InstanceType == BetterGiInstanceType.WebView)
        {
            throw new InvalidOperationException("WebView 不能枚举其他 WebView。");
        }

        var endpoints = _state.WebViewConnectionsByProcessId.Values
            .Where(x => requester.InstanceType == BetterGiInstanceType.Primary
                        || x.Endpoint.WindowsSessionId == requester.WindowsSessionId)
            .Select(x => x.Endpoint)
            .OrderBy(x => x.WindowsSessionId)
            .ThenBy(x => x.ProcessId)
            .ToArray();
        return InstanceIpcEnvelope.Response(
            request,
            new WebViewListResponse { Endpoints = endpoints });
    }

    private async Task<InstanceIpcEnvelope> HandleWebViewSendAsync(
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
    {
        var requester = RequireRegisteredEndpoint(connection);
        if (requester.InstanceType == BetterGiInstanceType.WebView)
        {
            throw new InvalidOperationException("WebView 不能通过根实例向其他 WebView 转发消息。");
        }

        var send = request.Data?.ToObject<WebViewSendRequest>(InstanceIpcProtocol.Serializer)
                   ?? throw new ArgumentException("WebView 转发请求缺少数据。");
        if (string.IsNullOrWhiteSpace(send.Operation))
        {
            throw new ArgumentException("WebView 转发请求缺少操作名称。");
        }
        if (!_state.WebViewConnectionsByProcessId.TryGetValue(
                send.TargetProcessId,
                out var target))
        {
            throw new InvalidOperationException(
                $"WebView 进程 {send.TargetProcessId} 当前不在线。");
        }
        if (requester.InstanceType == BetterGiInstanceType.ChildSession
            && target.Endpoint.WindowsSessionId != requester.WindowsSessionId)
        {
            throw new InvalidOperationException("桌面分身不能访问其他 Session 中的 WebView。");
        }

        var targetResponse = await target.Connection.SendRequestAsync(
            InstanceOperations.WebViewMessage,
            new WebViewMessage
            {
                SourceProcessId = requester.ProcessId,
                Operation = send.Operation,
                Data = send.Data
            },
            ForwardRequestTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessfulResponse(targetResponse);
        return InstanceIpcEnvelope.Response(request);
    }

    private InstanceIpcEnvelope HandleWebViewMessage(
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        if (_context.InstanceType != BetterGiInstanceType.WebView
            || connection.RemoteEndpoint?.InstanceType != BetterGiInstanceType.Primary)
        {
            throw new InvalidOperationException("只有根实例可以向 WebView 分发消息。");
        }

        var message = request.Data?.ToObject<WebViewMessage>(InstanceIpcProtocol.Serializer)
                      ?? throw new ArgumentException("WebView 消息缺少数据。");
        _dispatchWebViewMessage(message);
        return InstanceIpcEnvelope.Response(request);
    }

    private InstanceIpcEnvelope CreateOpenResponse(
        InstanceIpcEnvelope request,
        ConnectionOpenDisposition disposition,
        BetterGiInstanceType assignedType)
    {
        return InstanceIpcEnvelope.Response(
            request,
            new ConnectionOpenResponse
            {
                Disposition = disposition,
                AssignedType = assignedType,
                RootProcessId = _context.ProcessId,
                RootSessionId = _context.WindowsSessionId
            });
    }

    private static InstanceEndpoint CreateEndpoint(
        BetterGiInstanceType instanceType,
        int processId,
        int sessionId)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var process = Process.GetProcessById(processId);
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            // 连接已经证明进程存在；读取启动时间失败不影响连接登记。
        }

        return new InstanceEndpoint
        {
            InstanceType = instanceType,
            ProcessId = processId,
            WindowsSessionId = sessionId,
            StartedAt = startedAt
        };
    }

    private static InstanceEndpoint RequireRegisteredEndpoint(InstanceConnection connection)
    {
        return connection.RemoteEndpoint
               ?? throw new InvalidOperationException("当前管道连接尚未完成登记。");
    }

    private InstanceIpcEnvelope CacheActivationResponse(
        Guid requestId,
        InstanceIpcEnvelope response)
    {
        _activationResponses.TryAdd(requestId, response);
        if (_activationResponses.Count > 512)
        {
            _activationResponses.Clear();
            _activationResponses.TryAdd(requestId, response);
        }
        return response;
    }

    private static void EnsureSuccessfulResponse(InstanceIpcEnvelope response)
    {
        if (response.Success == true)
        {
            return;
        }

        throw new InvalidOperationException(
            response.ErrorMessage ?? response.ErrorCode ?? "实例 IPC 请求失败。");
    }

    private InstanceIpcEnvelope HandleTaskStop(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
            cancellationContext.Cancel();
            return InstanceIpcEnvelope.Response(request, new { status = "stopped" });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_stop_failed", $"停止任务失败: {ex.Message}");
        }
    }

    private async Task<InstanceIpcEnvelope> HandleTaskStart(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            // 标记配置组是否在 RunMulti 执行中被取消（F11 停止等），末尾据此返回 cancelled 状态
            var configGroupCancelled = false;
            var groupName = request.Data?["groupName"]?.ToString();
            var configName = request.Data?["configName"]?.ToString();
            var startFromIndex = request.Data?["startFromIndex"]?.ToObject<int>() ?? 0;
            // 幂等保护：task.start 携带 generation 时，同一 generation 只执行一次
            var generation = request.Data?["generation"]?.ToObject<int>() ?? 0;

            // 幂等检查：同一 generation 已执行过则跳过（避免 OnAllReady 重复广播导致配置组重复启动）
            if (generation > 0 && generation <= _lastExecutedTaskGeneration)
            {
                _logger.LogInformation("[IPC task.start] generation={Gen} 已执行过，跳过重复执行", generation);
                return InstanceIpcEnvelope.Response(request, new { status = "already_executed", generation });
            }
            if (generation > 0)
            {
                _lastExecutedTaskGeneration = generation;
            }

            // 通过全局服务容器获取 IScriptService
            var scriptService = App.ServiceProvider.GetService<BetterGenshinImpact.Service.Interface.IScriptService>();
            if (scriptService == null)
                return InstanceIpcEnvelope.Failure(request, "service_unavailable", "脚本服务不可用");

            // 先在主线程上停止当前任务。Cancel() 后立即 Set() 重建 Cts 清 WasCancelled，
            // 避免 Cts 取消标记残留到新配置组。RunMulti 内部也有 Set()，但在此之前
            // 的 IsCancellationRequested 检查会因残留 Cts 取消而误判。
            await Application.Current?.Dispatcher.InvokeAsync(async () =>
                {
                    var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
                    _logger.LogInformation("[IPC task.start] 配置组 {Group} 停止前: WasCancelled={W}, IsDisp={D}, CancelReq={R}", groupName, cancellationContext.WasCancelled, cancellationContext.IsDisposed, cancellationContext.IsCancellationRequested);
                    cancellationContext.Cancel();
                    cancellationContext.Set();
                    _logger.LogInformation("[IPC task.start] 配置组 {Group} Cancel()+Set()后: WasCancelled={W}, IsDisp={D}, CancelReq={R}", groupName, cancellationContext.WasCancelled, cancellationContext.IsDisposed, cancellationContext.IsCancellationRequested);
                })!;

            // 下发命令为最高优先级：等待旧任务真正释放任务锁（TaskSemaphore.CurrentCount 回到 1）再启动新任务。
            // 不能用固定 sleep——旧任务清理可能 >1s，等不到就启动会撞"当前存在正在运行中的独立任务"。
            // 这里是只读轮询 CurrentCount，不抢占锁，不会死锁；15s 为兜底超时，防旧任务卡死永久阻塞。
            var taskStopDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < taskStopDeadline
                   && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0)
            {
                await Task.Delay(200);
            }

            // 启动配置组或一条龙
            if (!string.IsNullOrEmpty(groupName))
            {
                // 读取配置组 JSON（文件 I/O 在后台线程执行，不阻塞 UI 线程消息泵）
                var groupPath = Path.Combine(AppContext.BaseDirectory, "User", "ScriptGroup", $"{groupName}.json");
                string? groupJson = null;
                if (File.Exists(groupPath))
                {
                    groupJson = await File.ReadAllTextAsync(groupPath);
                }
                else
                {
                    _logger.LogWarning("HandleTaskStart: 配置组 {Group} 不存在", groupName);
                }

                if (groupJson != null)
                {
                    // 通过主线程执行 ScriptService.RunMulti（含"从此处开始执行"处理）
                    // 使用 InvokeAsync 而非 Invoke，避免阻塞 UI 线程消息泵
                    var completionSource = new TaskCompletionSource();
                    _ = Application.Current!.Dispatcher.InvokeAsync(async () =>
                    {
                        _logger.LogInformation("[IPC task.start] 配置组 {Group} 的 Dispatcher 回调开始执行（RunMulti 即将开始）", groupName);
                        try
                        {
                            var group = BetterGenshinImpact.Core.Script.Group.ScriptGroup.FromJson(groupJson);

                            // 为 projects 手动设置 1-based Index（FromJson 读出的 Index 可能为 0 或无效），
                            // 确保 SetTaskContextNextFlag 的 nst.Item2 == item.Index 匹配必能命中。
                            for (var idx = 0; idx < (group.Projects?.Count ?? 0); idx++)
                                group.Projects[idx].Index = idx + 1;

                            // 处理"从此处开始执行"
                            // startFromIndex 是 1-based 项目索引（ScriptGroupProject.Index），
                            // 不是 projects 数组索引。需要用 item.Index 匹配查找。
                            if (startFromIndex > 0)
                            {
                                var config = BetterGenshinImpact.GameTask.TaskContext.Instance().Config;
                                var projects = group.Projects;
                                var sel = projects?.FirstOrDefault(p => p.Index == startFromIndex);
                                if (sel != null)
                                {
                                    config.NextScheduledTask =
                                    [
                                        (groupName, startFromIndex, sel.FolderName, sel.Name)
                                    ];
                                }
                            }

                            var projectsList = BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel.GetNextProjects(group);
                            // 启动新配置组前清空 RunnerContext 和 TaskContext.CurrentScriptProject 的残留，
                            // 避免 task.status 读到上一个配置组/suspend 的残留 taskName（如"联机锄地上线"），
                            // 导致助手端轮询 running 恒为 true、卡死在等待（只执行第一个配置组）。
                            // 注意：仅 RunnerContext.Clear() 不够，taskName 还会通过 ??= 从 TaskContext.CurrentScriptProject 补残留。
                            BetterGenshinImpact.GameTask.RunnerContext.Instance.Clear();
                            BetterGenshinImpact.GameTask.TaskContext.Instance().CurrentScriptProject = null;
                            await scriptService.RunMulti(projectsList, groupName);
                            // task.start 是同步等待 RunMulti 完成的。RunMulti 结束后检查 WasCancelled：
                            // 若为 true（用户 F11 停止等取消了配置组），标记 configGroupCancelled，方法末尾返回 cancelled 状态，
                            // 助手端据此停止后续配置组。否则返回 success，助手端继续执行下一个配置组。
                            var runWasCancelled = BetterGenshinImpact.Core.Script.CancellationContext.Instance.WasCancelled;
                            _logger.LogInformation("[IPC task.start] RunMulti 完成, group={Group}, wasCancelled={WasCancelled}", groupName, runWasCancelled);
                            if (runWasCancelled)
                            {
                                _logger.LogInformation("[IPC task.start] 配置组 {Group} 执行中被取消（WasCancelled=true）", groupName);
                                configGroupCancelled = true;
                            }
                            completionSource.SetResult();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "HandleTaskStart: IScriptService.RunMulti 失败");
                            completionSource.SetException(ex);
                        }
                    });
                    // 等待 RunMulti 真正完成（Dispatcher.InvokeAsync(...).Task 只等待调度完成，不等待内部 await）
                    await completionSource.Task;
                    _logger.LogInformation("[IPC task.start] 配置组 {Group} 的 RunMulti 已真正完成", groupName);
                }
            }
            else if (!string.IsNullOrEmpty(configName))
            {
                // 启动一条龙
                // 使用 InvokeAsync 而非 Invoke（同步），避免阻塞 UI 线程消息泵导致全局键盘钩子回调延迟
                await Application.Current!.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var vm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
                        if (vm != null)
                        {
                            // 强制初始化：主动加载配置列表（在 BGI 初始状态时，OneDragonFlowViewModel 未初始化，ConfigList 为空）
                            vm.InitConfigList();
                            var cfg = vm.ConfigList.FirstOrDefault(c => c.Name == configName);
                            if (cfg != null)
                            {
                                vm.SelectedConfig = cfg;
                                // 设置 startFromIndex 后先持久化到磁盘，再调用 OnOneKeyExecute。
                                // OnOneKeyExecute 开头会调 InitConfigList() 重新从磁盘反序列化配置，
                                // 如果不先持久化，之前设置的 cfg.NextTaskIndex 会因对象被替换而丢失。
                                if (startFromIndex > 0)
                                {
                                    cfg.NextTaskIndex = startFromIndex;
                                    vm.WriteConfig(cfg);
                                }
                                await vm.OnOneKeyExecute();
                            }
                            else
                            {
                                _logger.LogWarning("HandleTaskStart: 一条龙配置 {Config} 不存在", configName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("HandleTaskStart: OneDragonFlowViewModel 不可用");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "HandleTaskStart: 启动一条龙失败");
                    }
                }).Task;
            }

            if (configGroupCancelled)
            {
                _logger.LogInformation("[IPC task.start] 配置组 {Group} 返回 cancelled 状态", groupName);
                return InstanceIpcEnvelope.Response(request, new { status = "cancelled", message = "配置组 " + groupName + " 执行中被取消", groupName, configName, startFromIndex });
            }
            _logger.LogInformation("[IPC task.start] 配置组 {Group} 返回 started 状态", groupName);
            return InstanceIpcEnvelope.Response(request, new { status = "started", groupName, configName, startFromIndex });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_start_failed", $"启动任务失败: {ex.Message}");
        }
    }

    private InstanceIpcEnvelope HandleTaskStatus(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var isCancelled = !BetterGenshinImpact.Core.Script.CancellationContext.Instance.IsDisposed
                && BetterGenshinImpact.Core.Script.CancellationContext.Instance.IsCancellationRequested;

            var hoeing = AutoHoeingProgress.IsRunning;

            // 从 RunnerContext 获取当前脚本项目信息
            string? taskName = null;
            string? groupName = null;
            try
            {
                var ctx = BetterGenshinImpact.GameTask.RunnerContext.Instance;
                if (ctx?.taskProgress != null)
                {
                    groupName = ctx.taskProgress.CurrentScriptGroupName;
                    taskName = ctx.taskProgress.CurrentScriptGroupProjectInfo?.Name;
                }
                taskName ??= BetterGenshinImpact.GameTask.TaskContext.Instance()?.CurrentScriptProject?.Name;
            }
            catch
            {
                // 忽略
            }

            // 任务已取消时，taskName 可能有残留值，必须清空避免下游误报
            if (isCancelled)
            {
                taskName = null;
                groupName = null;
            }
            else if (!string.IsNullOrEmpty(taskName))
            {
                // 记录最近执行过的任务名（用于"联机锄地上线"等轻量任务检测）
                _recentTaskName = taskName;
                _recentTaskNameTime = DateTime.UtcNow;
            }

            // 联机锄地进度
            string? hoeingProgress = null;
            lock (AutoHoeingProgress.Sync)
            {
                if (hoeing)
                {
                    var tsRoute = TimeSpan.FromSeconds(Math.Max(0, AutoHoeingProgress.RouteEstimatedSeconds));
                    var tsRemain = TimeSpan.FromSeconds(Math.Max(0, AutoHoeingProgress.RoundRemainingSeconds));
                    hoeingProgress = $"{AutoHoeingProgress.RoundPrefix}当前进度：开始第 {AutoHoeingProgress.CurrentRouteIndex}/{AutoHoeingProgress.TotalRoutes} 条线路: {AutoHoeingProgress.RouteFileName}，本线路预计用时 {(int)tsRoute.TotalHours}时{tsRoute.Minutes}分{tsRoute.Seconds}秒，本轮预计剩余 {(int)tsRemain.TotalHours}时{tsRemain.Minutes}分{tsRemain.Seconds}秒";
                }
            }

            // 检查 _recentTaskName 是否在 30 秒内
            string? recentTaskName = null;
            if (_recentTaskName != null && (DateTime.UtcNow - _recentTaskNameTime).TotalSeconds < 30)
            {
                recentTaskName = _recentTaskName;
            }

            // 检查"联机锄地上线"独立任务是否在 30 秒内触发过（更可靠，不依赖 taskName 字符串匹配）
            if (recentTaskName == null
                && BetterGenshinImpact.GameTask.AutoOnline.NotifyOnlineTask.LastTriggeredAt != DateTime.MinValue
                && (DateTime.UtcNow - BetterGenshinImpact.GameTask.AutoOnline.NotifyOnlineTask.LastTriggeredAt).TotalSeconds < 30)
            {
                recentTaskName = "联机锄地上线";
            }

            // 检查是否有已保存的中断上下文
            var hasSuspendedTaskContext = BetterGenshinImpact.GameTask.TaskContext.Instance()?.Config?.SuspendedTaskContext != null;

            return InstanceIpcEnvelope.Response(request, new
            {
                running = BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0,
                // 说明：用单任务锁权威判断“是否有任务在跑”，不再依赖 taskName 是否残留。
                // 任务运行期间 TaskRunner.RunCurrentAsync 持有锁（CurrentCount==0），结束释放（CurrentCount==1）。
                // taskName 仅作展示名（ExecuteProject/P0 已清空残留）。防止任务正常结束后 running 恒 true → 任务名残留。
                // 暴露 wasCancelled：最近一次任务是否被用户手动取消（F11 BgiEnabledHotkey 走
                // CancellationContext.Cancel()，'取消当前脚本'热键走 ManualCancel()，两者都置 WasCancelled=true）。
                // Set()（任务启动）清 false，Clear() 不清，所以 F11 停止后即使 Cts 被 Dispose，
                // wasCancelled 仍是 true，助手端能稳定检测到"配置组被手动取消"，从而停止执行后续配置组。
                wasCancelled = BetterGenshinImpact.Core.Script.CancellationContext.Instance.WasCancelled,
                status = isCancelled ? "stopped" : "running",
                taskName,
                groupName,
                autoHoeingRunning = hoeing,
                autoHoeingProgress = hoeingProgress,
                recentTaskName,
                recentTaskNameTime = _recentTaskNameTime, // 仅当 recentTaskName != null 时有效；null 时忽略
                onlineGeneration = NotifyOnlineTask.CurrentGeneration, // 新：上线事件代序号，无任务时返回 0
                onlineTriggeredAt = NotifyOnlineTask.LastTriggeredAt, // 新：上线事件触发时间
                hasSuspendedTaskContext
            });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_status_failed", $"查询任务状态失败: {ex.Message}");
        }
    }

    private InstanceIpcEnvelope HandleConfigList(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            var scriptGroupPath = Path.Combine(basePath, "User", "ScriptGroup");
            var oneDragonPath = Path.Combine(basePath, "User", "OneDragon");

            // 读取配置组列表
            var configGroupNames = new List<string>();
            var configGroupTasks = new Dictionary<string, List<string>>();
            var configGroupTasksWithStatus = new Dictionary<string, List<object>>();
            if (Directory.Exists(scriptGroupPath))
            {
                foreach (var file in Directory.GetFiles(scriptGroupPath, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name == null) continue;
                    configGroupNames.Add(name);

                    try
                    {
                        var json = File.ReadAllText(file);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var tasks = new List<string>();
                        var tasksWithStatus = new List<object>();
                        if (root.TryGetProperty("projects", out var projects))
                        {
                            foreach (var project in projects.EnumerateArray())
                            {
                                var pName = project.TryGetProperty("name", out var taskName)
                                    ? taskName.GetString() ?? ""
                                    : "";
                                var pIndex = project.TryGetProperty("index", out var idx)
                                    ? idx.GetInt32() : tasks.Count + 1;
                                var pStatus = project.TryGetProperty("status", out var statusEl)
                                    ? statusEl.GetString() ?? "Enabled" : "Enabled";
                                tasks.Add(pName);
                                tasksWithStatus.Add(new { name = pName, index = pIndex, status = pStatus });
                            }
                        }
                        configGroupTasks[name] = tasks;
                        configGroupTasksWithStatus[name] = tasksWithStatus;
                    }
                    catch
                    {
                        // 单个文件解析失败不影响其他
                    }
                }
            }

            // 读取一条龙列表
            var oneClickConfigNames = new List<string>();
            var oneClickTasks = new Dictionary<string, List<string>>();
            var oneClickTasksWithStatus = new Dictionary<string, List<object>>();
            if (Directory.Exists(oneDragonPath))
            {
                foreach (var file in Directory.GetFiles(oneDragonPath, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name == null) continue;
                    oneClickConfigNames.Add(name);

                    try
                    {
                        var json = File.ReadAllText(file);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var tasks = new List<string>();
                        var tasksWithStatus = new List<object>();
                        if (root.TryGetProperty("taskEnabledList", out var taskList)
                            || root.TryGetProperty("TaskEnabledList", out taskList))
                        {
                            foreach (var entry in taskList.EnumerateObject())
                            {
                                var taskEntry = entry.Value;
                                var tIndex = int.TryParse(entry.Name, out var ti) ? ti : 0;
                                var tName = taskEntry.TryGetProperty("Item2", out var taskName)
                                    ? taskName.GetString() ?? $"任务{entry.Name}" : $"任务{entry.Name}";
                                var tEnabled = taskEntry.TryGetProperty("Item1", out var enabledEl)
                                    ? enabledEl.GetBoolean() : true;
                                tasks.Add(tName);
                                tasksWithStatus.Add(new { name = tName, index = tIndex, enabled = tEnabled });
                            }
                        }
                        oneClickTasks[name] = tasks;
                        oneClickTasksWithStatus[name] = tasksWithStatus;
                    }
                    catch
                    {
                        // 单个文件解析失败不影响其他
                    }
                }
            }

            // 读取快捷键列表（用栈遍历避免递归方法定义）
            var hotkeys = new List<object>();
            try
            {
                var hotkeyVm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.HotKeyPageViewModel>();
                if (hotkeyVm != null)
                {
                    var stack = new Stack<BetterGenshinImpact.Model.HotKeySettingModel>();
                    foreach (var m in hotkeyVm.HotKeySettingModels) stack.Push(m);
                    while (stack.Count > 0)
                    {
                        var current = stack.Pop();
                        if (!current.IsDirectory && !current.HotKey.IsEmpty)
                        {
                            hotkeys.Add(new { configName = current.ConfigPropertyName, functionName = current.FunctionName, hotkeyText = current.HotKey.ToString() });
                        }
                        if (current.Children != null)
                            foreach (var child in current.Children) stack.Push(child);
                    }
                }
            }
            catch
            {
                // 快捷键读取失败不影响其他功能
            }

            return InstanceIpcEnvelope.Response(request, new
            {
                configGroups = configGroupNames,
                configGroupTasks,
                configGroupTasksWithStatus,
                oneClickConfigs = oneClickConfigNames,
                oneClickTasks,
                oneClickTasksWithStatus,
                hotkeys
            });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "config_list_failed", $"读取配置列表失败: {ex.Message}");
        }
    }

    /// <summary>执行指定快捷键：通过 HotKeyPageViewModel 的 HotKeySettingModels 找到匹配模型并触发其 Action。</summary>
    private async Task<InstanceIpcEnvelope> HandleExecuteHotkey(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var hotkeyConfigName = request.Data?["hotkeyConfigName"]?.ToString();
            if (string.IsNullOrEmpty(hotkeyConfigName))
                return InstanceIpcEnvelope.Failure(request, "invalid_param", "hotkeyConfigName 为空");

            var hotkeyVm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.HotKeyPageViewModel>();
            if (hotkeyVm == null)
                return InstanceIpcEnvelope.Failure(request, "vm_unavailable", "快捷键服务不可用");

            var model = FindModelByConfigName(hotkeyVm.HotKeySettingModels, hotkeyConfigName);
            if (model == null || model.IsDirectory)
                return InstanceIpcEnvelope.Failure(request, "not_found", $"快捷键 {hotkeyConfigName} 未找到");

            var action = model.OnKeyPressAction ?? model.OnKeyDownAction ?? model.OnKeyUpAction;
            if (action == null)
                return InstanceIpcEnvelope.Failure(request, "no_action", $"快捷键 {hotkeyConfigName} 无执行回调");

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                action(null, new Fischless.HotkeyCapture.KeyPressedEventArgs(0, System.Windows.Forms.Keys.None));
            });
            return InstanceIpcEnvelope.Response(request, new { status = "executed", hotkeyConfigName });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "execute_failed", $"执行快捷键失败: {ex.Message}");
        }
    }

    private static BetterGenshinImpact.Model.HotKeySettingModel? FindModelByConfigName(
        System.Collections.ObjectModel.ObservableCollection<BetterGenshinImpact.Model.HotKeySettingModel> models,
        string configName)
    {
        foreach (var m in models)
        {
            if (!m.IsDirectory && m.ConfigPropertyName == configName)
                return m;
            if (m.Children != null)
            {
                var child = FindModelByConfigName(m.Children, configName);
                if (child != null)
                    return child;
            }
        }
        return null;
    }

    /// <summary>关闭游戏：调用 BGI 已有的 SystemControl.CloseGame()。</summary>
    private InstanceIpcEnvelope HandleCloseGame(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            BetterGenshinImpact.GameTask.SystemControl.CloseGame();
            return InstanceIpcEnvelope.Response(request, new { status = "closed" });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "close_failed", $"关闭游戏失败: {ex.Message}");
        }
    }

    /// <summary>设置任务启用状态：改 ScriptGroup.json（配置组）或 OneDragon 配置（一条龙）并写回。</summary>
    private async Task<InstanceIpcEnvelope> HandleSetTaskEnabled(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var groupName = request.Data?["groupName"]?.ToString();
            var configName = request.Data?["configName"]?.ToString();
            var taskIndex = request.Data?["taskIndex"]?.ToObject<int>() ?? 0;
            var enabled = request.Data?["enabled"]?.ToObject<bool>() ?? false;

            if (!string.IsNullOrEmpty(groupName))
            {
                // 配置组：改 ScriptGroup.json 中对应项目的 status
                var basePath = AppContext.BaseDirectory;
                var groupPath = Path.Combine(basePath, "User", "ScriptGroup", $"{groupName}.json");
                if (!File.Exists(groupPath))
                    return InstanceIpcEnvelope.Failure(request, "not_found", $"配置组 {groupName} 不存在");

                var json = await File.ReadAllTextAsync(groupPath);
                var group = BetterGenshinImpact.Core.Script.Group.ScriptGroup.FromJson(json);
                var project = group.Projects?.FirstOrDefault(p => p.Index == taskIndex);
                if (project == null)
                    return InstanceIpcEnvelope.Failure(request, "not_found", $"任务索引 {taskIndex} 未找到");

                project.Status = enabled ? "Enabled" : "Disabled";
                var newJson = group.ToJson();
                await File.WriteAllTextAsync(groupPath, newJson);
            }
            else if (!string.IsNullOrEmpty(configName))
            {
                // 一条龙：改 OneDragon 配置的 TaskEnabledList
                var basePath = AppContext.BaseDirectory;
                var oneDragonPath = Path.Combine(basePath, "User", "OneDragon", $"{configName}.json");
                if (!File.Exists(oneDragonPath))
                    return InstanceIpcEnvelope.Failure(request, "not_found", $"一条龙配置 {configName} 不存在");

                var json = await File.ReadAllTextAsync(oneDragonPath);
                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<BetterGenshinImpact.Core.Config.OneDragonFlowConfig>(json);
                if (config == null)
                    return InstanceIpcEnvelope.Failure(request, "parse_failed", "解析一条龙配置失败");

                if (config.TaskEnabledList.ContainsKey(taskIndex))
                    config.TaskEnabledList[taskIndex] = (enabled, config.TaskEnabledList[taskIndex].Item2);

                var newJson = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
                // 写文件时重试：一条龙正在运行时，JSON 文件可能被 BGI 进程锁定
                // 最多重试 5 次，每次 500ms，超时后抛出异常
                Exception? lastWriteEx = null;
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        await File.WriteAllTextAsync(oneDragonPath, newJson);
                        lastWriteEx = null;
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastWriteEx = ex;
                        if (retry < 4) await Task.Delay(500);
                    }
                }
                if (lastWriteEx != null) throw lastWriteEx;
            }
            else
            {
                return InstanceIpcEnvelope.Failure(request, "invalid_param", "groupName 和 configName 均为空");
            }

            return InstanceIpcEnvelope.Response(request, new { status = "saved", groupName, configName, taskIndex, enabled });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "save_failed", $"保存启用状态失败: {ex.Message}");
        }
    }

    private async Task<InstanceIpcEnvelope> HandleTaskSuspend(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            // 1. 读取当前任务上下文
            string? taskType = null;
            string? groupName = null;
            int taskIndex = 0;
            string? folderName = null;
            string? projectName = null;

            var ctx = BetterGenshinImpact.GameTask.RunnerContext.Instance;
            var progress = ctx?.taskProgress;

            if (progress?.CurrentScriptGroupProjectInfo != null)
            {
                var info = progress.CurrentScriptGroupProjectInfo;
                groupName = progress.CurrentScriptGroupName;
                taskIndex = info.Index;
                folderName = info.FolderName;
                projectName = info.Name;
                taskType = "group";
            }
            else if (progress?.CurrentScriptGroupName != null)
            {
                // 有 groupName 但没有 projectInfo → 一条龙
                groupName = progress.CurrentScriptGroupName;
                // 一条龙中断时，从 OneDragonFlowViewModel 获取当前配置的 NextTaskIndex
                try
                {
                    var oneDragonVm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
                    if (oneDragonVm?.SelectedConfig != null)
                    {
                        taskIndex = oneDragonVm.SelectedConfig.NextTaskIndex;
                    }
                }
                catch
                {
                    // ViewModel 不可用时，默认从头开始
                    taskIndex = 1;
                }
                taskType = "onedragon";
            }
            else
            {
                // 独立任务或 JS 脚本
                var taskName = BetterGenshinImpact.GameTask.TaskContext.Instance()?.CurrentScriptProject?.Name;
                if (!string.IsNullOrEmpty(taskName))
                {
                    projectName = taskName;
                    taskType = "solo";
                }
            }

            // 2. 如果没有任务在运行，直接返回"无需保存"
            if (taskType == null)
            {
                _logger.LogInformation("[IPC task.suspend] BGI 当前无任务运行，无需保存上下文");
                return InstanceIpcEnvelope.Response(request, new { status = "no_task" });
            }

            // 3. 保存上下文到 AllConfig
            var allConfig = BetterGenshinImpact.GameTask.TaskContext.Instance()?.Config;
            if (allConfig != null)
            {
                allConfig.SuspendedTaskContext = new BetterGenshinImpact.Core.Config.SuspendedTaskContext
                {
                    TaskType = taskType,
                    GroupName = groupName ?? "",
                    TaskIndex = taskIndex,
                    FolderName = folderName ?? "",
                    ProjectName = projectName ?? ""
                };
                _logger.LogInformation("[IPC task.suspend] 已保存中断上下文: Type={TaskType}, Group={GroupName}, Index={TaskIndex}",
                    taskType, groupName, taskIndex);
            }

            // 4. 停止当前任务
            var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
            cancellationContext.Cancel();

            // 5. 等待 TaskSemaphore 释放（最多 5 秒）
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline
                   && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0)
            {
                await Task.Delay(200);
            }

            return InstanceIpcEnvelope.Response(request, new
            {
                status = "suspended",
                taskType,
                groupName,
                taskIndex
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IPC task.suspend] 中断任务失败");
            return InstanceIpcEnvelope.Failure(request, "task_suspend_failed", $"中断任务失败: {ex.Message}");
        }
    }

    private async Task<InstanceIpcEnvelope> HandleTaskResume(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var allConfig = BetterGenshinImpact.GameTask.TaskContext.Instance()?.Config;
            if (allConfig?.SuspendedTaskContext == null)
            {
                return InstanceIpcEnvelope.Failure(request, "no_context", "没有已保存的中断上下文");
            }

            var context = allConfig.SuspendedTaskContext;
            _logger.LogInformation("[IPC task.resume] 开始恢复任务: Type={Type}, Group={Group}, Index={Index}",
                context.TaskType, context.GroupName, context.TaskIndex);

            var scriptService = App.ServiceProvider.GetService<BetterGenshinImpact.Service.Interface.IScriptService>();

            switch (context.TaskType)
            {
                case "group":
                    // 恢复配置组：写 NextScheduledTask 后调 RunMulti
                    if (scriptService != null && !string.IsNullOrEmpty(context.GroupName))
                    {
                        var groupPath = System.IO.Path.Combine(AppContext.BaseDirectory, "User", "ScriptGroup", $"{context.GroupName}.json");
                        if (System.IO.File.Exists(groupPath))
                        {
                            var json = await System.IO.File.ReadAllTextAsync(groupPath);
                            var group = BetterGenshinImpact.Core.Script.Group.ScriptGroup.FromJson(json);
                            for (var idx = 0; idx < (group.Projects?.Count ?? 0); idx++)
                                group.Projects[idx].Index = idx + 1;

                            if (context.TaskIndex > 0)
                            {
                                var projects = group.Projects;
                                var sel = projects?.FirstOrDefault(p => p.Index == context.TaskIndex);
                                if (sel != null)
                                {
                                    allConfig.NextScheduledTask =
                                    [
                                        (context.GroupName, context.TaskIndex, context.FolderName, context.ProjectName)
                                    ];
                                }
                            }

                            var projectsList = BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel.GetNextProjects(group);
                            _ = Application.Current?.Dispatcher.Invoke(async () =>
                            {
                                await scriptService.RunMulti(projectsList, context.GroupName);
                            });
                        }
                    }
                    break;

                case "onedragon":
                    // 恢复一条龙：写 NextTaskIndex 后调 OnOneKeyExecute
                    if (!string.IsNullOrEmpty(context.GroupName))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            var vm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
                            if (vm != null)
                            {
                                var cfg = vm.ConfigList.FirstOrDefault(c => c.Name == context.GroupName);
                                if (cfg != null)
                                {
                                    vm.SelectedConfig = cfg;
                                    if (context.TaskIndex > 0)
                                        cfg.NextTaskIndex = context.TaskIndex;
                                    _ = vm.OnOneKeyExecute();
                                }
                            }
                        });
                    }
                    break;

                case "solo":
                    // 恢复独立任务/JS 脚本：直接启动
                    if (!string.IsNullOrEmpty(context.ProjectName))
                    {
                        // 通过 SoloTaskRegistry 创建并执行
                        var soloTask = BetterGenshinImpact.GameTask.SoloTaskRegistry.CreateTask(
                            context.ProjectName, null, null, context.GroupName);
                        if (soloTask != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await soloTask.Start(System.Threading.CancellationToken.None);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[IPC task.resume] 恢复独立任务失败: {TaskName}", context.ProjectName);
                                }
                            });
                        }
                    }
                    break;
            }

            // 清除上下文（一次性消费）
            allConfig.SuspendedTaskContext = null;
            _logger.LogInformation("[IPC task.resume] 已清除中断上下文");

            return InstanceIpcEnvelope.Response(request, new { status = "resumed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IPC task.resume] 恢复任务失败");
            return InstanceIpcEnvelope.Failure(request, "task_resume_failed", $"恢复任务失败: {ex.Message}");
        }
    }
}
