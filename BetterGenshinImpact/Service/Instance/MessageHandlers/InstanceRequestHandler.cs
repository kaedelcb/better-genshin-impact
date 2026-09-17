using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoOnline;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Execution;
using FriendshipProgress = BetterGenshinImpact.GameTask.AutoFriendship.FriendshipProgress;

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
    // [切片7] _lastExecutedTask（task.start generation+name 幂等去重）已迁入 BgiTaskCoordinator
    // （进程级单例，v2 handler 查询/登记改走协调器，检查与登记的位置、时机、语义逐字等价）。

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
            // [跨会话守卫] 命名管道按用户 SID 隔离，同 SID 多 Windows 会话时其他会话的
            // 助手/脚本也能连上本实例的管道。控制类与查询类操作只允许同一 Windows 会话
            // 的客户端（连接建立时已通过 GetNamedPipeClientProcessId + ProcessIdToSessionId
            // 捕获 ClientSessionId）；捕获失败（null）时 fail-closed，与助手端 Unknown 降级语义一致。
            if (IsSessionGuardedOperation(request.Operation)
                && !IsTrustedSessionClient(connection))
            {
                _logger.LogWarning(
                    "拒绝跨会话实例 IPC 请求：op={Operation} clientSession={ClientSession} serverSession={ServerSession}",
                    request.Operation,
                    connection.ClientSessionId,
                    _context.WindowsSessionId);
                return InstanceIpcEnvelope.Failure(
                    request,
                    "cross_session_rejected",
                    $"跨会话请求被拒绝（clientSession={connection.ClientSessionId?.ToString() ?? "unknown"}，serverSession={_context.WindowsSessionId}）");
            }

            // [切片1·挂载点①] ext.* 统一分发到模块一 ExternalInterfaceSession（v2 的 22 个操作走原 switch，逐字节不变）
            if (request.Operation.StartsWith(ExternalInterfaceOperations.Prefix, StringComparison.Ordinal))
            {
                return await ExternalInterfaceSession.GetOrCreate(connection)
                    .RouteAsync(this, request, cancellationToken)
                    .ConfigureAwait(false);
            }

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
                // 远程配置组编辑（remote-config-group-edit 契约 §2）
                InstanceOperations.ConfigPullGroup => HandleConfigPullGroup(connection, request),
                InstanceOperations.ConfigOpenRemoteEditor => HandleConfigOpenRemoteEditor(connection, request),
                InstanceOperations.ConfigRemoteEditorResult => HandleConfigRemoteEditorResult(connection, request),
                InstanceOperations.ConfigApplyGroup => await HandleConfigApplyGroup(connection, request),
                InstanceOperations.ConfigAbortRemoteEditor => HandleConfigAbortRemoteEditor(connection, request),
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
    /// 跨会话守卫拦截的操作集合：控制类指令（任务/热键/关游戏/配置下发）+ 状态查询。
    /// 实例间内部通信（connection.open / activation.dispatch / webview.* / relativeMouse.*）不在此列。
    /// </summary>
    private static bool IsSessionGuardedOperation(string operation) =>
        operation.StartsWith(ExternalInterfaceOperations.Prefix, StringComparison.Ordinal)
        || operation is
        InstanceOperations.TaskStart
        or InstanceOperations.TaskStop
        or InstanceOperations.TaskSuspend
        or InstanceOperations.TaskResume
        or InstanceOperations.ExecuteHotkey
        or InstanceOperations.CloseGame
        or InstanceOperations.SetTaskEnabled
        or InstanceOperations.ConfigApplyGroup
        or InstanceOperations.ConfigOpenRemoteEditor
        or InstanceOperations.ConfigPullGroup
        or InstanceOperations.ConfigAbortRemoteEditor
        or InstanceOperations.TaskStatus
        or InstanceOperations.ConfigList
        or InstanceOperations.Ping;

    /// <summary>客户端 SessionId 已捕获且与本实例一致才可信；null（P/Invoke 失败）视为不可信，fail-closed。</summary>
    private bool IsTrustedSessionClient(InstanceConnection connection) =>
        connection.ClientSessionId is { } clientSessionId
        && clientSessionId == _context.WindowsSessionId;

    /// <summary>
    /// 激活消息按 RequestId 去重，避免管道重试导致主窗口被重复激活。
    /// </summary>
    internal InstanceIpcEnvelope HandleActivationDispatch(
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
    internal async Task<InstanceIpcEnvelope> HandleConnectionOpenAsync(
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

    internal InstanceIpcEnvelope HandleWebViewList(
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

    internal async Task<InstanceIpcEnvelope> HandleWebViewSendAsync(
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

    internal InstanceIpcEnvelope HandleWebViewMessage(
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

    internal InstanceIpcEnvelope HandleTaskStop(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
            cancellationContext.ManualCancel();
            return InstanceIpcEnvelope.Response(request, new { status = "stopped" });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_stop_failed", $"停止任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// [批次名单 2026-09-13] 解析逗号分隔的批次绑定名单（助手批次下发 task.start 时携带，
    /// ext/v2 两通道共用）。空/缺失返回 null——无名单路径（手动/老助手）不跳过任何组。
    /// </summary>
    internal static List<string>? ParseBatchGroupNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length > 0 ? list.ToList() : null;
    }

    /// <summary>
    /// [手动停止冷却 2026-09-14] F11/停止热键后 30s 内拒绝外部 task.start（v2/ext 两通道共用）。
    /// 用户 F12 的语义是"全部停止"，但批次驱动端可能在 cancelled 后立即重发把任务又拉起来
    /// （实机事故：成员模式 F12 停止后一条龙被反复重启，用户连按 6 次 F12）。
    /// 窗口取 30s：足够吸收 +0.1s/+2.6s 量级的立即重发循环，又不至于让"F12 后房间才开好"的
    /// 合法批次广播长期失效；每次 ManualCancel 重新起算（滑动窗口）。
    /// </summary>
    internal static readonly TimeSpan ManualStopCooldownWindow = TimeSpan.FromSeconds(30);

    /// <summary>冷却窗口内返回无损拒绝信封；窗口外返回 null（调用方继续原流程）。拒绝零副作用：不 Cancel、不污染幂等登记、不占队列位。</summary>
    internal static InstanceIpcEnvelope? CheckManualStopCooldown(InstanceIpcEnvelope request, string channelTag)
    {
        var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
        if (!cancellationContext.IsInManualStopCooldown(ManualStopCooldownWindow, out var remainingSeconds))
        {
            return null;
        }

        return InstanceIpcEnvelope.Failure(request, "manual_stop_cooldown",
            $"检测到手动停止（F11/停止热键），{remainingSeconds:F0}s 冷却期内拒绝外部 task.start（{channelTag}）");
    }

    internal async Task<InstanceIpcEnvelope> HandleTaskStart(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            if (ExecutionRequestContract.Validate(request) is { } rejected) return rejected;
            // 显式 "key":null 归一化为 C# null（否则 taskName 被 "" 短路、幂等去重误判，见 GetStringOrNull 注释）
            var groupName = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
            var configName = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
            var startFromIndex = request.Data?["startFromIndex"]?.ToObject<int>() ?? 0;
            // 幂等保护：task.start 携带 generation 时，同一 generation 只执行一次
            var generation = request.Data?["generation"]?.ToObject<int>() ?? 0;
            // [批次名单] 批次绑定列表（纯加法协议字段）：一条龙据此判断内部哪些配置组由批次逐项驱动
            var batchGroupNames = ParseBatchGroupNames(request.Data?["batchGroupNames"]?.ToString());

            // [手动停止冷却] 先于幂等检查/幂等登记/Cancel：被拒绝的请求零副作用（同 task_already_running 无损拒绝纪律）
            if (CheckManualStopCooldown(request, "v2") is { } cooldownRejection)
            {
                return cooldownRejection;
            }

            // 幂等检查：同一 generation + 同一配置组名已执行过则跳过（避免 OnAllReady 重复广播导致配置组重复启动）
            // 注意：允许同一 generation 执行不同配置组（OnAllReady 依次执行多个配置组的场景）
            // [切片7] 去重状态查询 BgiTaskCoordinator（_lastExecutedTask 迁入，单一事实源）
            var lastExecuted = BgiTaskCoordinator.Instance.LastExecutedTask;
            var taskName = groupName ?? configName;
            if (generation > 0
                && generation == lastExecuted.Generation
                && taskName == lastExecuted.Name)
            {
                _logger.LogInformation("[IPC task.start] generation={Gen} name={Name} 已执行过，跳过重复执行", generation, taskName);
                return InstanceIpcEnvelope.Response(request, new { status = "already_executed", generation });
            }

            // 通过全局服务容器获取 IScriptService
            var scriptService = App.ServiceProvider.GetService<BetterGenshinImpact.Service.Interface.IScriptService>();
            if (scriptService == null)
                return InstanceIpcEnvelope.Failure(request, "service_unavailable", "脚本服务不可用");

            // [先问再杀] 有任务在跑时直接无损拒绝，绝不做 Cancel——
            // 旧逻辑无条件 Cancel 再等待，跨会话注入的 task.start 会杀死正在执行的任务。
            if (BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0)
            {
                _logger.LogWarning("[IPC task.start] 拒绝启动：当前存在正在运行中的独立任务");
                return InstanceIpcEnvelope.Failure(request, "task_already_running", "当前存在正在运行中的独立任务，请不要重复执行任务！");
            }

            // 幂等登记移到这里（切片1审查修复）：只有通过"无损拒绝"检查、真正进入启动流程才登记。
            // 原先登记在拒绝检查之前——被拒绝的请求也会污染 _lastExecutedTask，
            // 客户端按 task_already_running 重试时会被幂等检查吞掉（返回 already_executed 但任务从未启动）。
            // [切片7] 执行段已抽为 ExecuteTaskStartCoreAsync：v2 handler 与 BgiTaskCoordinator
            // 共用单一事实源，行为逐字节等价。返回 true = 配置组在 RunMulti 执行中被取消（F11 停止等）。
            var configGroupCancelled = await ExecuteTaskStartCoreAsync(scriptService, groupName, configName, startFromIndex, batchGroupNames, generation,
                takeoverTicket: InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"),
                executionRequest: request, executionIdentity: ExecutionRequestContract.ReadIdentity(request.Data));

            if (configGroupCancelled)
            {
                return InstanceIpcEnvelope.Response(request, new { status = "cancelled", message = "配置组 " + groupName + " 执行中被取消", groupName, configName, startFromIndex });
            }
            if (generation > 0) BgiTaskCoordinator.Instance.RegisterExecuted(generation, taskName);
            return InstanceIpcEnvelope.Response(request, new { status = "started", groupName, configName, startFromIndex });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_start_failed", $"启动任务失败: {ex.Message}");
        }
    }

    /// <summary>
    /// [切片7] task.start 执行段（从 HandleTaskStart 原样抽出，v2 handler 与 BgiTaskCoordinator
    /// pump 共用的单一事实源，行为逐字节等价）：先在主线程 Cancel()+CancelTokenOnly() 中断当前任务，
    /// 只读轮询等任务锁释放（200ms/15s 兜底），再走 Dispatcher.InvokeAsync 启动配置组（RunMulti）或
    /// 一条龙（OnOneKeyExecute）并同步等执行完。
    /// 返回 true = 配置组在 RunMulti 执行中被取消（F11 停止等），调用方据此返回 cancelled 状态。
    /// 前置条件（幂等检查、无损拒绝、幂等登记、scriptService 非空）由调用方负责。
    /// </summary>
    internal async Task<bool> ExecuteTaskStartCoreAsync(
        BetterGenshinImpact.Service.Interface.IScriptService scriptService,
        string? groupName, string? configName, int startFromIndex,
        IReadOnlyList<string>? batchGroupNames = null, int generation = 0, Guid? jobId = null,
        bool preempt = false, string? takeoverTicket = null,
        CancellationToken cancellationToken = default, Action? onAdmitted = null,
        JobSource source = JobSource.V2, Guid? workflowRunId = null,
        JobExecutionIdentity? executionIdentity = null, InstanceIpcEnvelope? executionRequest = null)
    {
        if (executionRequest != null && ExecutionRequestContract.Validate(executionRequest) is { } rejected)
            throw new InvalidOperationException(rejected.ErrorCode + ": " + rejected.ErrorMessage);
        workflowRunId ??= executionIdentity?.WorkflowRunId;
        cancellationToken.ThrowIfCancellationRequested();
        var stopVersion = ExecutionScope.StopVersionNow;
        if (!PreemptionGate.Authorize(takeoverTicket))
            throw new InvalidOperationException("takeover_conflict: 批次票据无效或需升级助手");
        if (ExecutionScope.HasActive || BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0)
            throw new InvalidOperationException("task_busy: 原流程尚未退出");
        var completion = new TaskCompletionSource<BetterGenshinImpact.GameTask.TaskRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Application.Current!.Dispatcher.InvokeAsync(async () =>
        {
            ExecutionScope? admittedRoot = null;
            using var cancellationRegistration = cancellationToken.Register(() => admittedRoot?.Cancel());
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopVersion != ExecutionScope.StopVersionNow) throw new OperationCanceledException("启动前已被用户停止");
                if (ExecutionScope.HasActive) throw new InvalidOperationException("task_busy");
                if (executionRequest != null && ExecutionRequestContract.Validate(executionRequest) is { } expired)
                    throw new InvalidOperationException(expired.ErrorCode + ": " + expired.ErrorMessage);
                var descriptor = new JobDescriptor(string.IsNullOrEmpty(groupName) ? JobKind.OneDragon : JobKind.Group,
                    groupName ?? configName ?? throw new ArgumentException("缺少任务名"),
                    jobId.HasValue && source != JobSource.Resume ? JobSource.Ext : source,
                    generation > 0 ? generation : null, JobId: jobId, TakeoverTicket: takeoverTicket,
                    OnAdmitted: () =>
                    {
                        admittedRoot = ExecutionScope.Current!;
                        cancellationToken.ThrowIfCancellationRequested();
                        onAdmitted?.Invoke();
                    }, ResumeIndex: startFromIndex > 0 ? startFromIndex : null, WorkflowRunId: workflowRunId,
                    NodeId: executionIdentity?.NodeId, Iteration: executionIdentity?.Iteration,
                    TaskId: InstanceIpcProtocol.GetStringOrNull(executionRequest?.Data, "taskId"),
                    ConfigRevision: InstanceIpcProtocol.GetStringOrNull(executionRequest?.Data, "expectedConfigRevision"));
                var prepared = executionRequest == null ? (Snapshot: (TaskConfigurationContract.Snapshot?)null, SingleIndex: (int?)null)
                    : await ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(executionRequest);
                if (executionRequest != null && ExecutionRequestContract.Validate(executionRequest) is { } staleBeforeExecution)
                    throw new InvalidOperationException(staleBeforeExecution.ErrorCode + ": " + staleBeforeExecution.ErrorMessage);
                if (!string.IsNullOrEmpty(groupName))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "User", "ScriptGroup", groupName + ".json");
                    var group = BetterGenshinImpact.Core.Script.Group.ScriptGroup.FromJson(
                        prepared.Snapshot?.Document.ToString() ?? await File.ReadAllTextAsync(path));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopVersion != ExecutionScope.StopVersionNow) throw new OperationCanceledException();
                    for (var i = 0; i < group.Projects.Count; i++) group.Projects[i].Index = i + 1;
                    if (group.Projects.Count == 0) throw new InvalidOperationException("no_work: 配置组为空");
                    if (startFromIndex > 0 && !group.Projects.Any(p => p.Index == startFromIndex))
                        throw new InvalidOperationException("恢复位置已不存在");
                    using var root = ExecutionScope.Start(descriptor);
                    if (startFromIndex > 0)
                    {
                        var project = group.Projects.FirstOrDefault(p => p.Index == startFromIndex)
                            ?? throw new InvalidOperationException("恢复位置已不存在");
                        BetterGenshinImpact.GameTask.TaskContext.Instance().Config.NextScheduledTask =
                            [(groupName, startFromIndex, project.FolderName, project.Name)];
                    }
                    var progress = new BetterGenshinImpact.GameTask.TaskProgress.TaskProgress { CurrentScriptGroupName = groupName };
                    BetterGenshinImpact.GameTask.RunnerContext.Instance.taskProgress = progress;
                    var projects = prepared.SingleIndex is { } only
                        ? group.Projects.Where(p => p.Index == only).ToList()
                        : BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel.GetNextProjects(group);
                    if (projects.Count == 0) throw new InvalidOperationException("no_work");
                    completion.TrySetResult(await scriptService.RunMulti(projects, groupName, progress, descriptor));
                }
                else
                {
                    var vm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>()
                        ?? throw new InvalidOperationException("一条龙执行器不可用");
                    vm.InitConfigList();
                    var config = vm.ConfigList.FirstOrDefault(c => c.Name == configName)
                        ?? throw new FileNotFoundException("一条龙配置不存在: " + configName);
                    if (!config.TaskEnabledList.Any(p => p.Value.Item1))
                        throw new InvalidOperationException("no_work: 一条龙没有启用的任务");
                    if (startFromIndex > 0 && !config.TaskEnabledList.ContainsKey(startFromIndex))
                        throw new InvalidOperationException("恢复位置已不存在");
                    vm.SelectedConfig = config;
                    BetterGenshinImpact.GameTask.TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = configName!;
                    BetterGenshinImpact.GameTask.RunnerContext.Instance.BatchGroupNames =
                        batchGroupNames == null ? null : new List<string>(batchGroupNames);
                    completion.TrySetResult(await vm.ExecuteOneDragonAsync(descriptor));
                }
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        var result = await completion.Task;
        if (result == BetterGenshinImpact.GameTask.TaskRunResult.Ran) return false;
        if (result == BetterGenshinImpact.GameTask.TaskRunResult.Cancelled) return true;
        throw new InvalidOperationException(result == BetterGenshinImpact.GameTask.TaskRunResult.Preempted
            ? "preempted: 原流程已让位" : "task_start_failed: 任务未成功完成: " + result);
    }

    internal InstanceIpcEnvelope HandleTaskStatus(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        PreemptionGate.Renew(InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"));
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
                if (!string.IsNullOrEmpty(ctx?.SoloTaskName))
                {
                    // 独立任务/一条龙默认条目：RunCurrentAsync 拿锁时写入，身份以此为准。
                    // 必须优先于 taskProgress——后者在配置组跑完后残留旧项目名（RunMulti 只写不清），
                    // 一条龙混合执行（先配置组后默认条目）时残留会盖住当前条目名。
                    taskName = ctx!.SoloTaskName;
                }
                else if (ctx?.taskProgress != null)
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
            if (isCancelled || (!ExecutionScope.HasActive && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount > 0))
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

            // [A3.3] 注册表只读视图（原则 5：派生视图向唯一事实源收敛）：
            // 既有链路（RunnerContext/taskProgress）为空时用在跑作业名兜底；
            // running/slotOccupied 做注册表并集。并集而非替换——龙内前导步骤、计划表等待等
            // 持锁路径尚未登记作业（A2.6 边界），纯读注册表会漏掉它们的占用。
            BetterGenshinImpact.Service.Execution.BgiJob? registryRunningJob = null;
            try
            {
                if (BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated)
                {
                    registryRunningJob = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.CurrentRunningJob();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JobRegistry] task.status 注册表读数失败（回退既有链路）");
            }
            if (!isCancelled && taskName == null && groupName == null && registryRunningJob != null)
            {
                // 格式与既有语义对齐：Group 作业名是组名，其余作业名是任务名
                if (registryRunningJob.Kind == BetterGenshinImpact.Service.Execution.JobKind.Group)
                {
                    groupName = registryRunningJob.Name;
                }
                else
                {
                    taskName = registryRunningJob.Name;
                }
            }

            // 联机锄地进度
            string? hoeingProgress = null;
            string? currentRouteDisplay = null;
            lock (AutoHoeingProgress.Sync)
            {
                if (hoeing)
                {
                    var tsRoute = TimeSpan.FromSeconds(Math.Max(0, AutoHoeingProgress.RouteEstimatedSeconds));
                    var tsRemain = TimeSpan.FromSeconds(Math.Max(0, AutoHoeingProgress.RoundRemainingSeconds));
                    hoeingProgress = $"{AutoHoeingProgress.RoundPrefix}当前进度：开始第 {AutoHoeingProgress.CurrentRouteIndex}/{AutoHoeingProgress.TotalRoutes} 条线路: {AutoHoeingProgress.RouteFileName}，本线路预计用时 {(int)tsRoute.TotalHours}时{tsRoute.Minutes}分{tsRoute.Seconds}秒，本轮预计剩余 {(int)tsRemain.TotalHours}时{tsRemain.Minutes}分{tsRemain.Seconds}秒";
                    if (AutoHoeingProgress.TotalRoutes > 0)
                    {
                        currentRouteDisplay = $"第{AutoHoeingProgress.CurrentRouteIndex}/{AutoHoeingProgress.TotalRoutes}条线路: {AutoHoeingProgress.RouteFileName}";
                    }
                }
            }

            // 配置组内脚本任务（JS/地图追踪）当前执行的具体线路名：
            // JS 脚本逐条跑路线时 taskName 恒为脚本名，只有 ScriptRouteProgress（AutoPathingScript 写入、
            // 项目边界清空）能给出线路名。与上面的 currentRouteDisplay（联机锄地进度）互不覆盖——
            // 联机锄地原生任务不经 AutoPathingScript，脚本线路名为 null；两者在助手端各占一个显示位。
            var scriptRouteName = isCancelled ? null : BetterGenshinImpact.Core.Script.ScriptRouteProgress.CurrentRouteName;
            var scriptTaskProgress = isCancelled ? null : BetterGenshinImpact.Core.Script.ScriptRouteProgress.ProgressText;

            // JS 脚本任务进度合成（纯增量）：原生联机锄地不在跑、但脚本任务正在跑路线时，
            // 优先取 JS 显式上报或 BGI 宿主适配器生成的进度文本，退化为仅线路名。
            // 原先「锄地进度」只由 AutoHoeingProgress 生产，JS 锄地一条龙等脚本任务此位恒空。
            // ProgressText 单独有值也可用（脚本尚未 runFile 到路线时也能显示 N/M 计数）。
            if (!hoeing && (scriptRouteName != null || scriptTaskProgress != null))
            {
                // 兼容旧监控端：新版本使用 scriptTaskProgress；旧版本仍可从 autoHoeingProgress 看到同一文本。
                hoeingProgress = scriptTaskProgress
                                 ?? $"脚本任务：当前线路 {scriptRouteName}";
            }

            // 好感任务进度合成（纯增量）：AutoFriendshipTask 主循环写 FriendshipProgress，
            // 文本与 ext 观察器共用 BuildDisplayText 单一口径（第X/Y轮 + 预计剩余 + 预计完成时刻）。
            string? friendshipProgress = isCancelled ? null : FriendshipProgress.BuildDisplayText();

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
            var suspendedCtx = BetterGenshinImpact.GameTask.TaskContext.Instance()?.Config?.SuspendedTaskContext;
            var hasSuspendedTaskContext = suspendedCtx != null;
            // [协议加法] 中断上下文身份（纯增量可选字段，旧助手忽略新字段，无兼容风险）：
            // 助手端用它做最后一道防线——若被中断的是「联机锄地上线」信号任务本身，恢复会重复触发上线，退化为停止。
            string? suspendedTaskType = suspendedCtx?.TaskType;
            string? suspendedTaskName = suspendedCtx?.TaskType switch
            {
                "group" or "onedragon" => suspendedCtx.GroupName,
                "solo" => !string.IsNullOrEmpty(suspendedCtx.ProjectName) ? suspendedCtx.ProjectName : suspendedCtx.GroupName,
                _ => null
            };

            // [切片7] 协调器字段（纯增量，老客户端忽略未知字段）：
            // IsCreated 守卫——从未使用过协调器（单机/纯 v2）时不为查询而创建单例，零感知。
            var coordinatorCreated = BgiTaskCoordinator.IsCreated;
            var queueDepth = coordinatorCreated ? BgiTaskCoordinator.Instance.QueueDepth : 0;
            var currentTaskHandle = coordinatorCreated
                ? BgiTaskCoordinator.Instance.CurrentTaskHandle?.ToString("N")
                : null;

            return InstanceIpcEnvelope.Response(request, new
            {
                // [A3.3] 并集：信号量占用 ∨ 注册表在跑作业（前者覆盖未登记持锁路径，后者覆盖已登记作业）
                executionIdle = !ExecutionScope.HasActive && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount > 0,
                running = ExecutionScope.HasActive || BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0
                          || registryRunningJob != null,
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
                // SignalR 房间当前人数（0=未知/未在房间）；桌宠"已联机"chip 显示用
                roomPlayerCount = AutoHoeingProgress.RoomPlayerCount,
                currentRouteDisplay,
                currentScriptRouteName = scriptRouteName,
                // JS 显式上报或 BGI 宿主适配器生成的通用任务进度；与原生锄地进度分开。
                scriptTaskProgress,
                // 好感任务进度文本（纯增量字段，旧助手忽略）；null=好感任务不在跑
                friendshipProgress,
                recentTaskName,
                recentTaskNameTime = _recentTaskNameTime, // 仅当 recentTaskName != null 时有效；null 时忽略
                onlineGeneration = NotifyOnlineTask.CurrentGeneration, // 新：上线事件代序号，无任务时返回 0
                onlineTriggeredAt = NotifyOnlineTask.LastTriggeredAt, // 新：上线事件触发时间
                hasSuspendedTaskContext,
                suspendedTaskType,
                suspendedTaskName,
                // [切片7] 任务协调层扩展（spec §4.4，纯增量字段）
                slotOccupied = BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0
                               || registryRunningJob != null, // [A3.3] 与 running 同并集口径
                queueDepth,        // 协调器在队任务数（未协商 task.queue 的老客户端不受影响）
                currentTaskHandle  // 在跑任务的 handle（协调器派发时登记，手动任务为 null）
            });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "task_status_failed", $"查询任务状态失败: {ex.Message}");
        }
    }

    internal object CreateReadOnlyStatusSnapshot()
    {
        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus);
        var response = HandleTaskStatus(null!, request);
        if (response.Success != true || response.Data is null)
            throw new InvalidOperationException(response.ErrorMessage ?? "任务状态读取失败");
        var data = response.Data;
        using var currentProcess = Process.GetCurrentProcess();
        return new
        {
            windowsSessionId = _context.WindowsSessionId,
            processId = _context.ProcessId,
            startedAt = _context.StartedAt,
            processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
            running = data["running"]?.ToObject<bool>() ?? false,
            taskName = data["taskName"]?.ToObject<string>(),
            groupName = data["groupName"]?.ToObject<string>(),
            // 任务详情字段（监控端桌宠面板按执行端显示完整信息用；旧客户端忽略未知字段无兼容风险）
            wasCancelled = data["wasCancelled"]?.ToObject<bool>() ?? false,
            autoHoeingRunning = data["autoHoeingRunning"]?.ToObject<bool>() ?? false,
            autoHoeingProgress = data["autoHoeingProgress"]?.ToObject<string>(),
            roomPlayerCount = data["roomPlayerCount"]?.ToObject<int>() ?? 0,
            currentRouteDisplay = data["currentRouteDisplay"]?.ToObject<string>(),
            currentScriptRouteName = data["currentScriptRouteName"]?.ToObject<string>(),
            scriptTaskProgress = data["scriptTaskProgress"]?.ToObject<string>(),
            // 好感任务进度文本（监控端桌宠"好感进度"行数据源；缺它监控模式永远看不到好感进度）
            friendshipProgress = data["friendshipProgress"]?.ToObject<string>()
        };
    }


    internal InstanceIpcEnvelope HandleConfigList(InstanceConnection connection, InstanceIpcEnvelope request)
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
    internal async Task<InstanceIpcEnvelope> HandleExecuteHotkey(InstanceConnection connection, InstanceIpcEnvelope request)
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
                using var authority = hotkeyConfigName is "CancelTaskHotkey" or "BgiEnabledHotkey" or "SuspendHotkey"
                    ? null : ExecutionScope.UseTakeoverTicket(InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"));
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
    internal InstanceIpcEnvelope HandleCloseGame(InstanceConnection connection, InstanceIpcEnvelope request)
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
    internal async Task<InstanceIpcEnvelope> HandleSetTaskEnabled(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        var response = await ExternalInterfaceConfigurationPlane.DispatchAsync(request);
        if (response.Success == true && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount > 0)
            _ = Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName") != null)
                        App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel>()?.ReloadScriptGroups();
                    else
                        App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>()?.InitConfigList();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "配置已应用，页面刷新失败"); }
            });
        return response;
    }

    /// <summary>
    /// 判定"当前在跑的项目"是否为「联机锄地上线」信号任务（见 HandleTaskSuspend 步骤 2.6）。
    /// 按项目内容识别：任务注册名（<see cref="NotifyOnlineTask.TaskName"/>）+ 独立任务项目恒为空的 FolderName
    /// （<c>ScriptGroupProject.BuildSoloTaskProject</c> 构造时 FolderName=""），不按组名——用户可以把组叫任何名字。
    /// JS/Pathing/KeyMouse 项目的 FolderName 均非空，不会误判；Shell 项目 FolderName 虽为空但 Name 是命令串，也不会撞名。
    /// </summary>
    internal static bool IsOnlineSignalTask(string? projectName, string? folderName)
        => BetterGenshinImpact.Service.Execution.SuspendContextCapture.IsOnlineSignalTask(projectName, folderName);

    internal async Task<InstanceIpcEnvelope> HandleTaskSuspend(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var ticket = InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket");
            if (string.IsNullOrWhiteSpace(ticket))
                return InstanceIpcEnvelope.Failure(request, "capability_required", "可靠接管需要升级助手并提供 takeoverTicket");
            var epoch = request.Data?["bgiEpoch"];
            if (epoch?["processId"]?.ToObject<int?>() != JobRegistry.CurrentEpoch.ProcessId
                || epoch?["startTicksUtc"]?.ToObject<long?>() != JobRegistry.CurrentEpoch.StartTicksUtc)
                return InstanceIpcEnvelope.Failure(request, "stale_epoch", "BGI 进程身份已改变或未知，不能重放旧接管意图");
            if (CheckManualStopCooldown(request, "task.suspend") is { } stopped) return stopped;
            PreemptionGate.Arm(ticket);
            var config = BetterGenshinImpact.GameTask.TaskContext.Instance().Config;
            if (PreemptionGate.TryMarkContextSaved())
            {
                config.SuspendedTaskContext = null;
                var snapshot = ExecutionScope.Suspend();
                if (snapshot != null && SuspendContextCapture.Save(_logger, snapshot, "IPC task.suspend"))
                {
                    config.SuspendedTaskContext!.TakeoverTicket = ticket;
                    config.SuspendedTaskContext.StopVersion = ExecutionScope.StopVersionNow;
                }
                BetterGenshinImpact.Core.Script.CancellationContext.Instance.Cancel();
            }
            var confirmed = await WaitSlotReleasedBoundedAsync("IPC task.suspend");
            var context = config.SuspendedTaskContext;
            return InstanceIpcEnvelope.Response(request, new
            {
                status = context == null ? "no_task" : "suspended",
                liveTask = context != null, quiesceConfirmed = confirmed, takeoverTicket = ticket,
                taskType = context?.TaskType, groupName = context?.GroupName, taskIndex = context?.TaskIndex
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "挂起失败");
            return InstanceIpcEnvelope.Failure(request, ex.Message == "stale_ticket" ? "stale_ticket" : "task_suspend_failed", ex.Message);
        }
    }

    private async Task<bool> WaitSlotReleasedBoundedAsync(string channelTag)
    {
        var deadline = DateTime.UtcNow + PreemptionGate.QuiesceBound;
        while (DateTime.UtcNow < deadline)
        {
            if (!ExecutionScope.HasActive && BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount > 0)
                return true;
            await Task.Delay(100);
        }
        _logger.LogWarning("{Channel}: 原流程未在有界时间内退出", channelTag);
        return false;
    }

    private readonly SemaphoreSlim _resumeGate = new(1, 1);
    private readonly Dictionary<string, (Guid AttemptId, string Status)> _resumeReceipts = new();
    internal async Task<InstanceIpcEnvelope> HandleTaskResume(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        await _resumeGate.WaitAsync();
        try
        {
            var ticket = InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket");
            if (ticket != null && _resumeReceipts.TryGetValue(ticket, out var receipt))
                return InstanceIpcEnvelope.Response(request, new { status = receipt.Status, attemptId = receipt.AttemptId });
            if (!PreemptionGate.Authorize(ticket))
                return InstanceIpcEnvelope.Failure(request, "stale_ticket", "恢复/释放票据无效");
            var config = BetterGenshinImpact.GameTask.TaskContext.Instance().Config;
            var context = config.SuspendedTaskContext;
            if (context != null && (context.TakeoverTicket != ticket || context.StopVersion != ExecutionScope.StopVersionNow))
                return InstanceIpcEnvelope.Failure(request, "stale_context", "恢复现场不属于本次接管或已被用户停止");
            if (request.Data?["cancel"]?.ToObject<bool?>() == true || context == null)
            {
                if (ExecutionScope.HasActive) return InstanceIpcEnvelope.Failure(request, "task_busy", "流程尚未结束，不能释放执行权");
                config.SuspendedTaskContext = null;
                PreemptionGate.Release(ticket);
                RememberResume(ticket, Guid.Empty, "cleared_not_resumed");
                return InstanceIpcEnvelope.Response(request, new { status = "cleared_not_resumed" });
            }
            if (ExecutionScope.HasActive || BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount == 0)
                return InstanceIpcEnvelope.Failure(request, "task_busy", "原流程尚未退出，恢复现场保留");
            if (context.ConfigurationRevisions != null)
                foreach (var revision in context.ConfigurationRevisions)
                    if (!File.Exists(revision.Key) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(revision.Key))) != revision.Value)
                        return InstanceIpcEnvelope.Failure(request, "configuration_changed", "恢复配置已删除或修改，现场保留，请核实后重新启动");
            var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admissionSync = new object();
            var expired = false;
            var attempt = Guid.NewGuid();
            void OnAdmitted()
            {
                lock (admissionSync)
                {
                    if (expired) throw new InvalidOperationException("resume_admission_timeout");
                    if (ReferenceEquals(config.SuspendedTaskContext, context))
                        config.SuspendedTaskContext = null;
                    PreemptionGate.Release(ticket);
                    RememberResume(ticket, attempt, "resumed");
                    admitted.TrySetResult(true);
                }
            }
            var service = App.ServiceProvider.GetRequiredService<BetterGenshinImpact.Service.Interface.IScriptService>();
            Task execution;
            if (context.TaskType is "group" or "onedragon")
            {
                if (context.TaskType == "onedragon" && !string.IsNullOrEmpty(context.SubTaskGroupName))
                    config.NextScheduledTask = [(context.SubTaskGroupName, context.TaskIndex + 1, context.FolderName, context.ProjectName)];
                execution = ExecuteTaskStartCoreAsync(service,
                    context.TaskType == "group" ? context.GroupName : null,
                    context.TaskType == "onedragon" ? context.GroupName : null,
                    context.TaskType == "group" ? context.TaskIndex + 1 : context.OneDragonTaskIndex,
                    jobId: attempt, takeoverTicket: ticket, onAdmitted: OnAdmitted, source: JobSource.Resume, workflowRunId: context.RootRunId,
                    executionIdentity: context.RootRunId is { } run && context.NodeId is { } node && context.Iteration is { } iteration
                        ? new JobExecutionIdentity(run, node, iteration, context.TaskId, context.ConfigRevision) : null,
                    executionRequest: InstanceIpcEnvelope.Request("task.start", new {
                        groupName = context.TaskType == "group" ? context.GroupName : null,
                        configName = context.TaskType == "onedragon" ? context.GroupName : null,
                        taskId = context.TaskId, expectedConfigRevision = context.ConfigRevision }));
            }
            else if (context.TaskType == "solo")
            {
                var settings = string.IsNullOrEmpty(context.SoloSettingsJson) ? null
                    : JsonConvert.DeserializeObject<Dictionary<string, object?>>(context.SoloSettingsJson);
                var task = BetterGenshinImpact.GameTask.SoloTaskRegistry.CreateTask(context.ProjectName, null, settings, context.GroupName)
                    ?? throw new InvalidOperationException("恢复任务已不存在");
                execution = Task.Run(async () =>
                {
                    using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, context.ProjectName, JobSource.Resume,
                        JobId: attempt, TakeoverTicket: ticket, OnAdmitted: OnAdmitted, WorkflowRunId: context.RootRunId));
                    await new BetterGenshinImpact.GameTask.TaskRunner().RunSoloTaskAsync(task, JobSource.Resume);
                });
            }
            else throw new InvalidOperationException("未知恢复类型");
            // Track this exact admission. An unrelated task taking the semaphore proves nothing.
            _ = ObserveResumeAsync(execution, admitted);
            var first = await Task.WhenAny(admitted.Task, execution, Task.Delay(TimeSpan.FromSeconds(30)));
            lock (admissionSync)
            {
                if (!admitted.Task.IsCompleted && !execution.IsCompleted)
                {
                    expired = true;
                    return InstanceIpcEnvelope.Failure(request, "resume_admission_timeout", "恢复未在期限内受理，现场保留，迟到启动已撤销");
                }
            }
            if (first == execution) await execution; // propagate missing config/admission failure
            if (!admitted.Task.IsCompletedSuccessfully)
                return InstanceIpcEnvelope.Failure(request, "resume_not_admitted", "恢复未受理，现场保留");
            return InstanceIpcEnvelope.Response(request, new { status = "resumed", attemptId = attempt });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复失败");
            return InstanceIpcEnvelope.Failure(request, "task_resume_failed", ex.Message);
        }
        finally { _resumeGate.Release(); }
    }

    private void RememberResume(string? ticket, Guid attempt, string status)
    {
        if (ticket == null) return;
        _resumeReceipts[ticket] = (attempt, status);
        if (_resumeReceipts.Count > 256) _resumeReceipts.Remove(_resumeReceipts.Keys.First());
    }

    private async Task ObserveResumeAsync(Task execution, TaskCompletionSource<bool> admitted)
    {
        try { await execution; }
        catch (Exception ex) { _logger.LogError(ex, "恢复尝试执行失败"); admitted.TrySetException(ex); }
    }

    // ===== 远程配置组编辑（remote-config-group-edit 契约 §2）=====

    /// <summary>config.pull_group：读取指定配置组文件原文 + 全局 AutoHoeingConfig + 策略文件清单 + 版本 + 运行状态 + MD5。</summary>
    internal InstanceIpcEnvelope HandleConfigPullGroup(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            // 显式 "key":null 归一化为 C# null（见 InstanceIpcProtocol.GetStringOrNull 注释）
            var groupName = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
            if (string.IsNullOrEmpty(groupName))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, error = "groupName 为空" });
            }

            var groupPath = Path.Combine(AppContext.BaseDirectory, "User", "ScriptGroup", $"{groupName}.json");
            if (!File.Exists(groupPath))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, error = $"配置组 {groupName} 不存在" });
            }

            var fileBytes = File.ReadAllBytes(groupPath);
            var scriptGroupJson = Encoding.UTF8.GetString(fileBytes);
            var fileMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(fileBytes)).ToLowerInvariant();

            // AutoHoeingConfig 随 AllConfig 走 System.Text.Json（ConfigService.JsonOptions）序列化
            var autoHoeingConfigJson = System.Text.Json.JsonSerializer.Serialize(
                BetterGenshinImpact.GameTask.TaskContext.Instance().Config.AutoHoeingConfig,
                BetterGenshinImpact.Service.ConfigService.JsonOptions);

            // 策略清单过滤规则与 AutoFightViewModel.LoadCustomScript 一致（*.txt + *.json，去扩展名相对路径）
            var autoFightFiles = ListStrategyFiles(Path.Combine(AppContext.BaseDirectory, "User", "AutoFight"));
            var autoGeniusFiles = ListStrategyFiles(Path.Combine(AppContext.BaseDirectory, "User", "AutoGeniusInvokation"));

            var groupRunning = IsGroupRunning(groupName);

            return InstanceIpcEnvelope.Response(request, new
            {
                ok = true,
                package = new
                {
                    groupName,
                    scriptGroupJson,
                    autoHoeingConfigJson,
                    autoFightStrategyFiles = autoFightFiles,
                    autoGeniusFiles,
                    bgiVersion = BetterGenshinImpact.Core.Config.Global.Version,
                    groupRunning,
                    fileMd5
                }
            });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "pull_group_failed", $"拉取配置组失败: {ex.Message}");
        }
    }

    /// <summary>与 AutoFightViewModel.LoadCustomScript 相同的过滤规则（只读，不创建目录）。</summary>
    private static List<string> ListStrategyFiles(string folder)
    {
        var list = new List<string>();
        if (!Directory.Exists(folder))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var extLower = Path.GetExtension(file).ToLowerInvariant();
            if (extLower != ".txt" && extLower != ".json")
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(folder, file);
            var strategyName = Path.ChangeExtension(relativePath, null);
            if (strategyName.StartsWith('\\') || strategyName.StartsWith('/'))
            {
                strategyName = strategyName[1..];
            }

            if (!string.IsNullOrWhiteSpace(strategyName))
            {
                list.Add(strategyName);
            }
        }

        return list;
    }

    /// <summary>任务运行状态判断：TaskSemaphore.CurrentCount==0 且 RunnerContext 组名匹配（同 HandleTaskStatus 口径）。</summary>
    private static bool IsGroupRunning(string groupName)
    {
        try
        {
            if (BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount != 0)
            {
                return false;
            }

            var ctx = BetterGenshinImpact.GameTask.RunnerContext.Instance;
            return ctx?.taskProgress?.CurrentScriptGroupName == groupName;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>config.open_remote_editor：Dispatcher 上弹 RemoteConfigEditWindow，单会话拒绝第二个。立即返回。</summary>
    internal InstanceIpcEnvelope HandleConfigOpenRemoteEditor(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            // 显式 "key":null 归一化为 C# null（见 InstanceIpcProtocol.GetStringOrNull 注释）；?? "" 兜底形态不变
            var targetName = InstanceIpcProtocol.GetStringOrNull(request.Data, "targetName") ?? "";
            var targetUid = InstanceIpcProtocol.GetStringOrNull(request.Data, "targetUid") ?? "";
            var groupName = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName") ?? "";
            var packageJson = InstanceIpcProtocol.GetStringOrNull(request.Data, "packageJson") ?? "";

            if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(packageJson))
            {
                return InstanceIpcEnvelope.Response(request, new { state = "rejected", error = "groupName/packageJson 为空" });
            }

            if (!RemoteEditSession.TryBegin(targetName, targetUid, groupName, packageJson))
            {
                // 同目标同组的重复开单（ext 通道已执行但响应丢失→v2 兜底重发/用户连点/重试）：
                // 现有会话就是这次请求创建的，采用它并回报 editing，避免误报"会话被占用"
                if (RemoteEditSession.IsSameInFlightSession(targetUid, groupName))
                {
                    return InstanceIpcEnvelope.Response(request, new { state = "editing", adopted = true });
                }
                // 拒绝必须带占用详情（哪个会话、什么状态、占了多久），否则助手/用户只能盲猜；
                // saved/cancelled 尸体已在 TryBegin 内回收放行，走到这里的一定是真实 editing 占用。
                var occ = RemoteEditSession.GetOccupyingInfo();
                return InstanceIpcEnvelope.Response(request, new
                {
                    state = "rejected",
                    error = $"已有进行中的远程编辑会话（正在编辑 {occ.TargetUid} 的「{occ.GroupName}」，已持续 {(int)occ.AgeSeconds} 秒；请先完成/关闭该编辑窗口，或由助手发送 config.abort_remote_editor 强制中止）",
                    occupying = new { state = occ.State, targetUid = occ.TargetUid, groupName = occ.GroupName, ageSeconds = occ.AgeSeconds }
                });
            }

            try
            {
                // 同步 Invoke：窗口创建/解析失败可立即回滚会话并告知助手
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var window = new BetterGenshinImpact.View.Windows.RemoteConfigEditWindow(
                        targetName, targetUid, groupName, packageJson);
                    window.Show();
                    // 登记窗口引用：助手主动中止（config.abort_remote_editor）时强制关窗用
                    RemoteEditSession.RegisterWindow(window);
                });
            }
            catch (Exception ex)
            {
                RemoteEditSession.AbortToIdle();
                _logger.LogWarning(ex, "[IPC config.open_remote_editor] 弹出远程编辑窗口失败");
                return InstanceIpcEnvelope.Response(request, new { state = "rejected", error = $"弹出编辑窗口失败: {ex.GetBaseException().Message}" });
            }

            return InstanceIpcEnvelope.Response(request, new { state = "editing" });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "open_editor_failed", $"打开远程编辑器失败: {ex.Message}");
        }
    }

    /// <summary>config.abort_remote_editor：助手主动中止当前远程编辑会话（含强制关窗），幂等——idle 时返回 aborted=false 不报错。</summary>
    internal InstanceIpcEnvelope HandleConfigAbortRemoteEditor(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            var aborted = RemoteEditSession.AbortActiveSession("助手请求中止（config.abort_remote_editor）");
            return InstanceIpcEnvelope.Response(request, new { state = "idle", aborted });
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "abort_editor_failed", $"中止远程编辑会话失败: {ex.Message}");
        }
    }

    /// <summary>config.remote_editor_result：助手轮询编辑结果；saved/cancelled 读取后会话关闭回 idle。</summary>
    internal InstanceIpcEnvelope HandleConfigRemoteEditorResult(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        var snapshot = RemoteEditSession.SnapshotAndConsumeIfDone();
        return snapshot.State switch
        {
            // editing 响应携带会话归属（targetUid/groupName）：助手在开窗响应丢失后的探测接管时核对，
            // 避免接管到上一轮遗留的别的配置组僵尸窗（[实机修复] 2026-09-05）
            "editing" => InstanceIpcEnvelope.Response(request, new
            {
                state = "editing",
                targetUid = snapshot.TargetUid,
                groupName = snapshot.GroupName
            }),
            "saved" => InstanceIpcEnvelope.Response(request, new
            {
                state = "saved",
                scriptGroupConfigJson = snapshot.ScriptGroupConfigJson,
                soloTaskName = snapshot.SoloTaskName,
                soloTaskSettingsJson = snapshot.SoloTaskSettingsJson
            }),
            "cancelled" => InstanceIpcEnvelope.Response(request, new { state = "cancelled" }),
            _ => InstanceIpcEnvelope.Response(request, new { state = "idle" })
        };
    }

    /// <summary>config.apply_group：合并远程编辑结果 → 原子写盘 → 刷新内存（全部在 Dispatcher 上执行）。</summary>
    internal async Task<InstanceIpcEnvelope> HandleConfigApplyGroup(InstanceConnection connection, InstanceIpcEnvelope request)
    {
        try
        {
            if (ExecutionRequestContract.Validate(request) is { } invalid) return invalid;
            if (!PreemptionGate.Authorize(InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket")))
                return InstanceIpcEnvelope.Failure(request, "takeover_conflict", "当前接管所有者不允许此次配置写入");
            // 显式 "key":null 归一化为 C# null（见 InstanceIpcProtocol.GetStringOrNull 注释）
            var groupName = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
            var baseMd5 = InstanceIpcProtocol.GetStringOrNull(request.Data, "baseMd5");
            var scriptGroupConfigJson = InstanceIpcProtocol.GetStringOrNull(request.Data, "scriptGroupConfigJson");
            var soloTaskName = InstanceIpcProtocol.GetStringOrNull(request.Data, "soloTaskName");
            var soloTaskSettingsJson = InstanceIpcProtocol.GetStringOrNull(request.Data, "soloTaskSettingsJson");

            if (string.IsNullOrEmpty(groupName))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, message = "groupName 为空", md5Changed = false, groupRunning = false });
            }

            if (string.IsNullOrEmpty(scriptGroupConfigJson) && string.IsNullOrEmpty(soloTaskSettingsJson))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, message = "无可应用内容（scriptGroupConfigJson 与 soloTaskSettingsJson 均为空）", md5Changed = false, groupRunning = false });
            }

            return await TaskConfigurationContract.Default.ExecuteLockedAsync(groupName, false, async () =>
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    ApplyRemoteGroup(request, groupName, baseMd5, scriptGroupConfigJson, soloTaskName, soloTaskSettingsJson)));
        }
        catch (Exception ex)
        {
            return InstanceIpcEnvelope.Failure(request, "apply_group_failed", $"应用远程配置失败: {ex.Message}");
        }
    }

    /// <summary>apply_group 的 UI 线程主体：合并而非整文件覆盖，写盘后刷新 ScriptControlViewModel / OneDragonFlowViewModel 内存。</summary>
    private InstanceIpcEnvelope ApplyRemoteGroup(
        InstanceIpcEnvelope request,
        string groupName,
        string? baseMd5,
        string? scriptGroupConfigJson,
        string? soloTaskName,
        string? soloTaskSettingsJson)
    {
        var groupPath = Path.Combine(AppContext.BaseDirectory, "User", "ScriptGroup", $"{groupName}.json");

        if (ExecutionRequestContract.Validate(request) is { } invalid) return invalid;
        if (!PreemptionGate.Authorize(InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket")))
            return InstanceIpcEnvelope.Failure(request, "takeover_conflict", "配置写入前接管所有权已改变");
        if (InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision") is { } expectedRevision
            && (!File.Exists(groupPath) || TaskConfigurationContract.Revision(File.ReadAllBytes(groupPath)) != expectedRevision))
            return InstanceIpcEnvelope.Failure(request, "configuration_changed", "配置已改变，未应用远程编辑");

        // 乐观并发提示：比较当前文件 MD5 与 pull 时的 baseMd5
        var md5Changed = false;
        if (File.Exists(groupPath) && !string.IsNullOrEmpty(baseMd5))
        {
            var currentMd5 = Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(groupPath)))
                .ToLowerInvariant();
            md5Changed = !string.Equals(currentMd5, baseMd5, StringComparison.OrdinalIgnoreCase);
        }

        // 1. 从 ScriptControlViewModel.ScriptGroups 找组（兜底从文件 FromJson）
        var scVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel>();
        BetterGenshinImpact.Core.Script.Group.ScriptGroup? group = null;
        var loadedFromFile = false;
        try
        {
            group = scVm?.ScriptGroups?.FirstOrDefault(g => g.Name == groupName);
        }
        catch
        {
            // ScriptGroups 未加载等异常时回退文件加载
        }

        if (group == null)
        {
            if (!File.Exists(groupPath))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, message = $"配置组 {groupName} 不存在", md5Changed, groupRunning = false });
            }

            group = BetterGenshinImpact.Core.Script.Group.ScriptGroup.FromJson(File.ReadAllText(groupPath));
            loadedFromFile = true;
        }

        // 2. 组级设置：反序列化 ScriptGroupConfig 替换 group.Config
        if (!string.IsNullOrEmpty(scriptGroupConfigJson))
        {
            var cfg = System.Text.Json.JsonSerializer.Deserialize<BetterGenshinImpact.Core.Script.Group.ScriptGroupConfig>(
                          scriptGroupConfigJson,
                          BetterGenshinImpact.Service.ConfigService.JsonOptions)
                      ?? throw new InvalidOperationException("scriptGroupConfigJson 反序列化失败");
            group.Config = cfg;
        }

        // 3. 锄地一条龙：SoloTaskSettingsObject 整体替换 + JsonElement→CLR 归一化（参考 ScriptGroup.NormalizeSoloTaskSettings）
        if (!string.IsNullOrEmpty(soloTaskSettingsJson))
        {
            if (string.IsNullOrEmpty(soloTaskName))
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, message = "缺少 soloTaskName", md5Changed, groupRunning = false });
            }

            var project = group.Projects?.FirstOrDefault(p => p.Name == soloTaskName);
            if (project == null)
            {
                return InstanceIpcEnvelope.Response(request, new { ok = false, message = $"组内未找到任务 {soloTaskName}", md5Changed, groupRunning = false });
            }

            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
                           soloTaskSettingsJson,
                           BetterGenshinImpact.Service.ConfigService.JsonOptions)
                       ?? new Dictionary<string, object?>();
            NormalizeSettingsDictionary(dict);
            project.SoloTaskSettingsObject = dict;
        }

        // 4. 原子写盘（WriteToFileAtomically）。
        // 内存组（来自 ScriptGroups，组名与文件名一致）走 TryWriteScriptGroupToDisk 以拿到成败；
        // 兜底从文件加载的组不走它——它按 group.Name（文件内 name）拼文件名，与请求 groupName（实际文件名）
        // 不一致时会写错文件，故直接按请求 groupName 拼出的实际路径写。
        string? writeError = null;
        if (!loadedFromFile && scVm != null)
        {
            if (!scVm.TryWriteScriptGroupToDisk(group, out writeError))
            {
                _logger.LogWarning("[IPC config.apply_group] 写盘失败（VM 路径）: {Error}", writeError);
                return InstanceIpcEnvelope.Response(request, new
                {
                    ok = false,
                    message = $"配置组 {groupName} 写盘失败: {writeError}",
                    md5Changed,
                    groupRunning = IsGroupRunning(groupName)
                });
            }
        }
        else
        {
            try
            {
                group.WriteToFileAtomically(groupPath);
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx, "[IPC config.apply_group] 写盘失败（文件路径）: {Path}", groupPath);
                return InstanceIpcEnvelope.Response(request, new
                {
                    ok = false,
                    message = $"配置组 {groupName} 写盘失败: {writeEx.Message}",
                    md5Changed,
                    groupRunning = IsGroupRunning(groupName)
                });
            }
        }

        // 5. 刷新内存，防止旧内存覆盖新文件
        try
        {
            scVm?.ReloadScriptGroups();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IPC config.apply_group] 刷新 ScriptControlViewModel 失败");
        }

        try
        {
            var oneDragonVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
            oneDragonVm?.ReadScriptGroup();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IPC config.apply_group] 刷新 OneDragonFlowViewModel 失败");
        }

        var groupRunning = IsGroupRunning(groupName);

        // 6. message 组装（契约 §1.4：覆盖期间变化 / 正在运行提示）
        var parts = new List<string> { "远程配置已应用" };
        if (md5Changed)
        {
            parts.Add("对方配置在编辑期间有变化，已按远程版本覆盖");
        }
        if (groupRunning)
        {
            parts.Add("该组正在运行，下次启动生效");
        }
        var message = string.Join("；", parts);

        return InstanceIpcEnvelope.Response(request, new { ok = true, message, md5Changed, groupRunning });
    }

    /// <summary>SoloTaskSettingsObject 的 JsonElement→CLR 归一化（与 ScriptGroup.NormalizeSoloTaskSettings 同逻辑）。</summary>
    private static void NormalizeSettingsDictionary(Dictionary<string, object?> dict)
    {
        try
        {
            var keys = new List<string>(dict.Keys);
            foreach (var key in keys)
            {
                if (dict[key] is System.Text.Json.JsonElement element)
                {
                    dict[key] = element.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.True => true,
                        System.Text.Json.JsonValueKind.False => false,
                        // UI 的 NumberBox.Value 为 double?，统一转 double（同 ScriptGroup.ConvertJsonNumber）
                        System.Text.Json.JsonValueKind.Number => element.GetDouble(),
                        System.Text.Json.JsonValueKind.String => element.GetString(),
                        System.Text.Json.JsonValueKind.Null => null,
                        _ => element // Array/Object 保留为 JsonElement
                    };
                }
            }
        }
        catch
        {
            // 规范化失败不影响使用，保留原始值
        }
    }
}
