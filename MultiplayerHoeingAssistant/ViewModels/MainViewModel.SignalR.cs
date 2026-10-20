using System.IO;
using System.Windows;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Models;
using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    // 断线自愈与手动刷新共用：避免旧 SignalR 实例迟到回调把连接状态锁死。
    private Timer? _connectionRecoveryTimer;
    private int _connectionRecoveryRunning;
    private readonly CoordinatedControlAuthority _controlAuthority = new();
    private readonly SemaphoreSlim _connectionAttemptGate = new(1, 1);
    private SignalRClient? _pendingControlClient;
    private long _controlContextVersion;

    private static string ControlSettingsIdentity(AssistConfig? config, string room) => string.Join("\n",
        config?.ServerUrl?.Trim().TrimEnd('/'), room, config?.PlayerUid, config?.ClientInstanceId,
        config?.StandaloneMode == true || string.IsNullOrWhiteSpace(config?.ServerUrl), config?.ObserverMode == true);

    private string CurrentControlContext() => Interlocked.Read(ref _controlContextVersion) + "\n"
        + ControlSettingsIdentity(_config, RoomCode);

    private void InvalidateChangedControlSettings(AssistConfig config)
    {
        var room = AssistConfigManager.GenerateControlRoomCode(config.TeamUids);
        if (ControlSettingsIdentity(_config, RoomCode) != ControlSettingsIdentity(config, room))
            InvalidateCoordinatedControlContext();
    }

    private void InvalidateCoordinatedControlContext()
    {
        CancelPendingCoordinatedBegin();
        Interlocked.Increment(ref _controlContextVersion);
        _controlAuthority.Revoke();
        OnlineHoeingBatch? batch;
        lock (_batchGate) batch = _activeBatch;
        if (batch is not null)
            ObserveOnlineTask(Task.Run(() => batch.Cancel(userInitiated: !batch.StateContinuationEnabled)),
                "房间身份变化撤销原批次启动权限");
    }

    private void RequireCoordinatedControlContext(OnlineHoeingBatch batch, CancellationToken ct, bool terminalReport = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!terminalReport)
        {
            batch.Cts.Token.ThrowIfCancellationRequested();
            if (batch.ControlExecutionEnded)
                throw new ControlContextRevokedException("原批次执行循环已退出，不能重新取得运行授权", batch.Cts.Token);
        }
        if (_disposing || IsStandaloneMode || batch.ControlContext != CurrentControlContext()
            || !terminalReport && (batch.Generation <= Volatile.Read(ref _resetFenceGeneration) || _localResetGate.CurrentCount == 0))
            throw new ControlContextRevokedException("原批次控制房间或停止权限已撤销", batch.Cts.Token);
        lock (_batchGate)
            if (!ReferenceEquals(_activeBatch, batch))
                throw new ControlContextRevokedException("原批次已结束或被替换", batch.Cts.Token);
        if (!terminalReport) batch.RequireRunningControlBudget();
    }

    private async Task CoordinateControlRoomJoinAsync(SignalRClient client,
        Func<CancellationToken, Task> join, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        OnlineHoeingBatch? batch;
        lock (_batchGate) batch = _activeBatch;
        var remaining = batch?.RemainingRunningControlBudget;
        if (remaining is { } left)
        {
            if (left <= TimeSpan.Zero) throw new ControlChannelUnavailableException("原批次恢复预算已耗尽");
            budget.CancelAfter(left < TimeSpan.FromSeconds(20) ? left : TimeSpan.FromSeconds(20));
        }
        using (await _controlAuthority.EnterAsync(budget.Token))
        {
            if (client.IsRetired || client.ControlContext != CurrentControlContext()
                || !ReferenceEquals(client, _signalRClient) && !ReferenceEquals(client, _pendingControlClient))
                throw new ControlChannelUnavailableException("旧连接回调不能重新取得房间身份");
            if (ReferenceEquals(client, _pendingControlClient) && _signalRClient is { } old
                && !ReferenceEquals(old, client))
            {
                // Retire before join can replace the server-side connection ID.
                old.RetireForReplacement();
                _controlAuthority.Revoke();
            }
            await join(budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            if (client.IsRetired || client.ControlContext != CurrentControlContext())
                throw new ControlChannelUnavailableException("入房回应已属于旧连接上下文");
            if (ReferenceEquals(client, _signalRClient))
                _controlAuthority.Publish(client, client.ControlContext);
        }
        // Notifications/current queries are deliberately outside the transition gate.
    }

    private async Task<CoordinatedBatchSnapshot> ExchangeCoordinatedControlAsync(OnlineHoeingBatch batch,
        string stage, string result, string reason, int index, int attempt, CancellationToken ct)
    {
        using (await _controlAuthority.EnterAsync(ct))
        {
            var terminalReport = (batch.IsCancellationRequested || batch.ControlExecutionEnded) && result.Length > 0 || stage == "cleaned";
            RequireCoordinatedControlContext(batch, ct, terminalReport);
            var client = _signalRClient;
            if (client is null || !client.IsControlRoomReady)
                throw new ControlChannelUnavailableException("控制房间正在恢复，原任务保持原身份");
            var grant = _controlAuthority.Capture(client, batch.ControlContext);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Revoked);
            try
            {
                var state = await client.UpdateCoordinatedBatchRawAsync(batch.Generation, batch.Token, batch.BatchId,
                    string.Join(",", batch.Bindings.Select(b => b.Type)), index, attempt, result, call.Token, stage, reason);
                RequireCoordinatedControlContext(batch, ct, terminalReport);
                _controlAuthority.Verify(grant);
                if (state.BatchId == batch.BatchId && state.Generation == batch.Generation && state.Protocol == "hoeing.start.v2")
                    batch.ControlGrant = state.Phase == "running" ? grant : null;
                return state;
            }
            catch (Exception ex) when (!batch.IsCancellationRequested && IsTemporaryControlFailure(ex, client, grant))
            { throw new ControlChannelUnavailableException("控制通道暂不可用，按原批次等待恢复", ex); }
        }
    }

    private async Task<CoordinatedBatchSnapshot> CoordinateBatchUpdateAsync(SignalRClient client,
        Func<CancellationToken, Task<CoordinatedBatchSnapshot>> update, CancellationToken ct)
    {
        using (await _controlAuthority.EnterAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(client, _signalRClient) || !client.IsControlRoomReady
                || client.ControlContext != CurrentControlContext())
                throw new ControlChannelUnavailableException("批次回执所属连接已变化");
            var grant = _controlAuthority.Capture(client, client.ControlContext);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, grant.Revoked);
            var state = await update(call.Token);
            ct.ThrowIfCancellationRequested();
            _controlAuthority.Verify(grant);
            return state;
        }
    }

    private static bool IsTemporaryControlFailure(Exception ex, SignalRClient client,
        CoordinatedControlAuthority.Grant grant) => ex is ControlChannelUnavailableException
        or IOException or TimeoutException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException
        || ex is OperationCanceledException && grant.Revoked.IsCancellationRequested
        || ex is InvalidOperationException && (!client.IsConnected || client.IsRetired || grant.Revoked.IsCancellationRequested);

    private async Task<BgiTaskSubmitResult> SubmitCoordinatedControlAsync(OnlineHoeingBatch batch,
        Func<CancellationToken, Task<BgiTaskSubmitResult>> send, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, batch.Cts.Token);
        if (batch.RemainingRunningControlBudget is { } remaining)
        {
            if (remaining <= TimeSpan.Zero)
                throw new ControlSubmissionDeferredException("原运行授权已过期，未发送任务");
            budget.CancelAfter(remaining);
        }
        return await _controlAuthority.SubmitAsync(batch.ControlGrant, () =>
        {
            RequireCoordinatedControlContext(batch, budget.Token);
            if (_signalRClient is not { IsControlRoomReady: true } client
                || !ReferenceEquals(client, batch.ControlGrant?.Owner))
                throw new ControlChannelUnavailableException("提交前控制连接已变化");
        }, send, budget.Token);
    }

    /// <summary>
    /// [F9-2] 三份复刻收拢的共用原语（首连 ConnectSignalRAsync / 刷新 RefreshAsync / 失败重试共用）：
    /// 创建已接线的 SignalR 客户端——事件订阅（WireSignalRClient）与连接状态回调统一在此，
    /// 新增回调只改这一处。历史上三处复刻各自打补丁（幽灵连接/假死/单机竞态）互不同步，是本族 bug 的温床。
    /// </summary>
    private SignalRClient CreateWiredSignalRClient()
    {
        var client = new SignalRClient();
        client.ControlContext = CurrentControlContext();
        client.CoordinateRoomJoin = CoordinateControlRoomJoinAsync;
        client.CoordinateBatchUpdate = CoordinateBatchUpdateAsync;
        client.RemainingBatchRecoveryBudget = () =>
        {
            lock (_batchGate) return _activeBatch?.RemainingRunningControlBudget;
        };
        client.ControlConnectionLost += () => _controlAuthority.Revoke(client);
        WireSignalRClient(client);
        // 连接状态变化（断开/重连）同步到 IsConnected → 标题栏连接徽章实时刷新
        client.OnConnectionStateChanged += connected =>
        {
            // 旧客户端在刷新/自愈换代后仍可能迟到回调；它无权覆盖新客户端的在线状态。
            if (!ReferenceEquals(_signalRClient, client))
                return;
            Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
            if (connected)
            {
                StopSignalRRecovery();
            }
            else
            {
                // Reconnecting、Closed 以及“传输已恢复但重新入房失败”都必须进入同一条
                // 有界重建链；不能把责任留给用户手动重启助手。
                ScheduleSignalRRecovery(client);
            }
            // 连接恢复后立即上报状态，无需等待 10 秒定时器
            if (connected) _ = ReportStatusAsync();
            // [B157] 断线时服务端已清零本成员上线事件，重连后补报上线意图（服务端按 gen 去重，安全）
            if (connected) _ = ReReportOnlineIntentIfNeededAsync();
        };
        return client;
    }

    /// <summary>[F9-2] 用当前配置发起连接（三处调用点的参数列表统一在此）。</summary>
    private async Task ConnectSignalRWithConfigAsync(SignalRClient client, CancellationToken cancellationToken = default,
        string message = "已连接控制房间", bool fireRefreshCompleted = false)
    {
        using var attemptBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OnlineHoeingBatch? batch;
        lock (_batchGate) batch = _activeBatch;
        if (batch?.RemainingRunningControlBudget is { } remaining)
        {
            if (remaining <= TimeSpan.Zero) attemptBudget.Cancel();
            else attemptBudget.CancelAfter(remaining);
        }
        cancellationToken = attemptBudget.Token;
        await _connectionAttemptGate.WaitAsync(cancellationToken);
        try
        {
            if (client.ControlContext != CurrentControlContext() || IsStandaloneMode)
                throw new OperationCanceledException("连接参数已变化", cancellationToken);
            _pendingControlClient = client;
            await client.ConnectAsync(
                _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
                _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
                _config.BypassSystemProxy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStandaloneMode || client.ControlContext != CurrentControlContext())
                throw new OperationCanceledException("连接完成时原房间身份已撤销", cancellationToken);
            await AdoptConnectedSignalRClientAsync(client, message, fireRefreshCompleted, cancellationToken);
        }
        catch
        {
            if (ReferenceEquals(_signalRClient, client))
            {
                _controlAuthority.Revoke(client);
                _signalRClient = null;
            }
            client.RetireForReplacement();
            throw;
        }
        finally
        {
            if (ReferenceEquals(_pendingControlClient, client)) _pendingControlClient = null;
            _connectionAttemptGate.Release();
        }
    }

    /// <summary>
    /// [F9-2] 连接成功后的统一接管：所有权移交 _signalRClient + 徽章/日志（Dispatcher 内）+
    /// 可选 RefreshCompleted（仅刷新语义的路径触发，首连不触发——保持原行为）+ 状态上报 + 定时器幂等兜底。
    /// </summary>
    private async Task AdoptConnectedSignalRClientAsync(SignalRClient client, string logMessage, bool fireRefreshCompleted,
        CancellationToken ct = default)
    {
        // 所有权移交与徽章/日志一起在 Dispatcher 内完成：Invoke 抛异常（关机竞态）时
        // _signalRClient 未被污染，调用方 finally 会正确销毁这个半成品客户端
        using (await _controlAuthority.EnterAsync(ct))
        {
        if (!client.IsControlRoomReady || client.ControlContext != CurrentControlContext())
            throw new ControlChannelUnavailableException("接管时连接已失效");
        _signalRClient = client;
        _controlAuthority.Publish(client, client.ControlContext);
        }
        // Never run callbacks, queries, or disposal while holding the transition gate.
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsConnected = true;
            AddLog(logMessage);
            if (fireRefreshCompleted) RefreshCompleted?.Invoke();
        });
        await client.RestoreCoordinatedAuthorityAsync(ct);
        // 启动定时上报（每10秒）；通常已在 InitializeAsync 启动，此处幂等兜底
        EnsureStatusTimerStarted();
        ObserveOnlineTask(ReportStatusAsync(), "连接恢复状态上报");
    }

    /// <summary>
    /// [F9-2] 连接失败后的 10s 重试定时器（首连失败 / 刷新失败共用）。
    /// WithAutomaticReconnect 不重试"首次连接失败"，故由本定时器每 10 秒重建直到连上。
    /// 语义取刷新重试的加固版：每轮新建客户端（复用失败实例是幽灵连接温床）；
    /// StartAsync 在飞期间（假死服务器协商可挂 ~100s）不叠加（_retryRunning）；
    /// 在飞期间用户切单机 → 本轮放弃；在飞期间已有在位连接接管 → 本定时器自我销毁（原刷新重试空转不自销）。
    /// </summary>
    private void StartSignalRRetryTimer(bool fireRefreshCompletedOnSuccess)
    {
        _retryTimer?.Dispose();
        _retryTimer = new Timer(async _ =>
        {
            if (!TryEnterConfigTransferOperation("connection_retry", out var transferOperation)) return;
            using var configTransferOperation = transferOperation;
            // 上次重试的 StartAsync 可能还挂着（假死服务器协商超时 ~100s），不叠加（详见 _retryRunning 注释）
            if (Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0) return;
            SignalRClient? client = null;
            try
            {
                // 重试在飞时用户切到离线：本轮放弃（定时器本身已由 GoStandaloneAsync 销毁）
                if (IsStandaloneMode) return;
                // 已有在位连接（刷新/自愈已接管）：本定时器使命完成，自我销毁
                if (_config == null || _signalRClient is { IsConnected: true })
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                    return;
                }
                // A disconnected client is a stale transport, not a valid owner. Reusing it
                // used to make the retry timer exit forever after Closed/Reconnected races.
                var stale = _signalRClient;
                _signalRClient = null;
                if (stale != null)
                {
                    try { await stale.DisposeAsync(); } catch { /* stale transport is disposable best effort */ }
                }
                client = CreateWiredSignalRClient();
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await ConnectSignalRWithConfigAsync(client, connectTimeout.Token,
                    "已重新连接控制房间", fireRefreshCompletedOnSuccess);
                client = null; // 所有权已移交 _signalRClient
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            catch (Exception retryEx)
            {
                // 重试失败保持定时器继续，但失败原因必须可见——"连不回来"时靠它区分
                // 服务器不可达（网络/DNS/服务挂）与服务器拒绝（密码/白名单/协议不兼容）
                AddLog($"连接重试失败: {retryEx.Message}（10 秒后继续）");
            }
            finally
            {
                // 重建失败（含 StartAsync 成功但入房抛异常）的半成品客户端必须销毁，防幽灵连接泄漏
                if (client != null)
                {
                    try { await client.DisposeAsync(); } catch { /* 销毁失败不影响下一轮重试 */ }
                }
                _retryRunning = 0;
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private async Task ConnectSignalRAsync()
    {
        // 离线优先：手动单机或服务器地址留空 = 单机模式，完全不发起连接（不连接/不重试/不刷失败日志）。
        // 本地状态采集循环独立于本方法运行（InitializeAsync 里已启动），槲寄生等本地功能不受影响。
        if (IsStandaloneMode)
        {
            IsConnected = false;
            AddLog("单机模式（手动单机或未填服务器地址）：跳过服务器连接，槲寄生等本地功能不受影响");
            return;
        }
        try
        {
            // [F9-2] 连接成功才接管 _signalRClient（原实现连接前就赋值，失败会留下非 null 的死实例）
            var client = CreateWiredSignalRClient();
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ConnectSignalRWithConfigAsync(client, connectTimeout.Token);
            // 连接在飞期间（最长 ~100s）用户切到离线：销毁刚到手的连接，不接管
            // （原首连路径缺此检查——刷新路径有，收拢时对齐；否则会切单机后仍被在飞首连接管）
            if (IsStandaloneMode)
            {
                await client.DisposeAsync();
                AddLog("单机模式已开启，放弃本次建立的连接");
                return;
            }
            // ConnectSignalRWithConfigAsync has already published the adopted client.
        }
        catch (Exception ex)
        {
            // 连接在飞时用户切到离线：失败属预期（连接被 Dispose），不再起重试
            if (IsStandaloneMode)
            {
                AddLog("单机模式已开启，停止服务器连接尝试");
                return;
            }
            AddLog($"连接失败: {ex.Message}，10 秒后自动重试");
            StartSignalRRetryTimer(fireRefreshCompletedOnSuccess: false);
        }
    }

    /// <summary>
    /// 总开关：手动切换单机/在线（标题栏按钮，弹窗确认）。切单机：断连 + 停重试 + 持久化；
    /// 切在线：持久化后立即发起连接。本地状态采集循环（_statusTimer）两种模式下都保持运行。
    /// </summary>
    private async Task ToggleStandaloneModeAsync()
    {
        if (!CanEditConfiguration) { AddLog(ConfigTransferBlockedMessage); return; }
        if (_config == null || _configManager == null) return;
        if (IsStandaloneMode)
        {
            if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            {
                AddLog("未配置服务器地址，无法切换在线；请先在「设置」中填写服务器地址");
                return;
            }
            if (!ShowSwitchConfirmDialog("切换为在线模式",
                "切换为在线模式后，将连接服务器，耕地机、嘟嘟可、槲寄生的联机功能恢复可用。是否继续切换？")) return;
            InvalidateCoordinatedControlContext();
            _config.StandaloneMode = false;
            _configManager.Save(_config);
            NotifyModeBindings();
            AddLog("已切换到在线模式，正在连接服务器...");
            await ConnectSignalRAsync();
        }
        else
        {
            if (!ShowSwitchConfirmDialog("切换为单机模式",
                "切换为单机模式后，将断开服务器连接，耕地机、嘟嘟可、槲寄生的联机功能全部不可用（本机功能照常）。是否继续切换？")) return;
            InvalidateCoordinatedControlContext();
            _config.StandaloneMode = true;
            _configManager.Save(_config);
            NotifyModeBindings();
            await GoStandaloneAsync("已手动切换到单机模式：服务器连接已断开，本地功能（槲寄生/本机日志）不受影响");
        }
    }

    /// <summary>转到单机态：停重试定时器并销毁 SignalR 客户端（复用 RefreshAsync 的 Dispose 路径语义）。
    /// 注意刻意不停 _statusTimer：本地状态采集单机时也必须继续（LatestLocalStatus 是槲寄生条件/电子狗的数据源）。</summary>
    private async Task GoStandaloneAsync(string reason)
    {
        InvalidateCoordinatedControlContext();
        StopSignalRRecovery();
        _retryTimer?.Dispose();
        _retryTimer = null;
        var old = _signalRClient;
        _signalRClient = null;
        if (old != null)
        {
            await old.DisposeAsync();
        }
        IsConnected = false;
        AddLog(reason);
    }

    /// <summary>设置保存后的单机态同步：清空服务器地址 = 恒定单机模式，若仍连着服务器则断开；
    /// 反向（补填了地址）不自动重连，保持既有"刷新/重启后生效"行为，仅提示。</summary>
    private void SyncStandaloneStateAfterSettingsSaved()
    {
        NotifyModeBindings();
        if (IsStandaloneMode && _signalRClient != null)
        {
            _ = GoStandaloneAsync("服务器地址已清空：已转入恒定单机模式，服务器连接已断开，本地功能不受影响");
        }
        else if (!IsStandaloneMode && _signalRClient == null && _retryTimer == null)
        {
            AddLog("配置已具备联机条件，点标题栏「切到在线」或「刷新」建立连接");
        }
    }

    /// <summary>
    /// 断线后的统一恢复入口。SignalR 内置重连与入房重试可能在旧连接对象上互相竞态；
    /// 这里延迟一小段时间让内置路径先完成，仍未恢复就走一次有界的完整重建。
    /// </summary>
    private void ScheduleSignalRRecovery(SignalRClient expected)
    {
        if (_disposing || IsStandaloneMode || !CanEditConfiguration) return;
        _connectionRecoveryTimer?.Dispose();
        _connectionRecoveryTimer = new Timer(async _ =>
        {
            if (Interlocked.CompareExchange(ref _connectionRecoveryRunning, 1, 0) != 0) return;
            try
            {
                if (_disposing || IsStandaloneMode || !ReferenceEquals(_signalRClient, expected)
                    || expected.IsControlRoomReady) return;
                if (Volatile.Read(ref _refreshRunning) != 0)
                {
                    ScheduleSignalRRecovery(expected);
                    return;
                }
                AddLog("[自愈] 控制房间在重连窗口内未恢复，开始重建连接（不需要重启助手）");
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddLog("[自愈] 控制房间重建失败，将继续自动重试: " + ex.Message);
                if (!IsStandaloneMode && !_disposing)
                    StartSignalRRetryTimer(fireRefreshCompletedOnSuccess: true);
            }
            finally
            {
                _connectionRecoveryRunning = 0;
            }
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void StopSignalRRecovery()
    {
        var timer = Interlocked.Exchange(ref _connectionRecoveryTimer, null);
        timer?.Dispose();
    }
}
