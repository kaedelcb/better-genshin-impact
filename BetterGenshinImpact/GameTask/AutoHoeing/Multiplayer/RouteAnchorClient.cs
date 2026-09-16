#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>等待锚点放行的结果（route-anchor）。</summary>
public enum RouteAnchorWaitResult
{
    /// <summary>服务端已授权进入下一条路线。</summary>
    Released = 0,

    /// <summary>服务端判定无法确认全员安全收口：必须停止整队，禁止进入下一条路线。</summary>
    Stopped = 1,

    /// <summary>本地取消（用户停止 / 任务取消 / 房间关闭）。</summary>
    Cancelled = 2,

    /// <summary>通信失败、快照缺失、超时等无法确认：同样禁止进入下一条路线。</summary>
    Failed = 3,

    /// <summary>功能未启用（配置关闭或能力不可用）：调用方应走原有行为。</summary>
    Disabled = 4,

    /// <summary>
    /// 服务端明确表示"当前处于协作重跑阶段、推进权归重跑"（rerun_in_progress）：
    /// 此时锚点必须让位——调用方应**继续**本轮（由重跑负责收口），而不是停止。
    /// 这是唯一一个"非 Released 但允许继续"的结果，且只能由服务端的显式拒绝产生。
    /// </summary>
    YieldedToRerun = 5,
}

/// <summary>
/// 路线边界锚点客户端（route-anchor）· 阶段 2。
///
/// 职责：把"上一条路线是否全员收口"这件事做成**不可绕过的客户端门面**——
/// 调用方在进入下一条路线之前必须得到 <see cref="RouteAnchorWaitResult.Released"/>，
/// 其他一切结果都不允许继续。
///
/// 设计纪律（方案正文第 6、7 章）：
///   1. 权威来源是服务端查询；事件只用于"尽快反应"，不作为唯一正确性来源；
///   2. 先订阅再动作（subscribe-before-action），避免"服务端先放行、客户端后订阅"丢帧；
///   3. 本地不猜、不自报边界：边界索引一律以服务端返回的快照为准；
///   4. 未启用时所有方法直接返回 <see cref="RouteAnchorWaitResult.Disabled"/> / null，
///      不发送任何新协议消息（单机与未开启的房间行为零变化）。
///
/// 阶段 2 只做"正常边界"：Enroll → Report → Ready → 等 Released。
/// Pull（被拉回边界）在阶段 4 实现，本类预留 <see cref="TryGetPullCommand"/> 读取入口。
/// </summary>
public sealed class RouteAnchorClient : IDisposable
{
    private readonly ILogger<RouteAnchorClient> _logger = App.GetLogger<RouteAnchorClient>();
    private readonly CoordinatorClient _client;

    /// <summary>本地缓存的最近一次权威快照（服务端返回，客户端不修改其身份字段）。</summary>
    private RouteAnchorSnapshotDto? _current;

    /// <summary>待执行的 Pull 命令（同一时刻至多一条；不同 commandId 不覆盖在途命令）。</summary>
    private RouteAnchorPullCommand? _pendingPull;

    /// <summary>已回报过的 Pull commandId（幂等：同一条命令只回报一次）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ackedPullCommandIds = new();

    private readonly object _pullLock = new();

    public RouteAnchorClient(CoordinatorClient client)
    {
        _client = client;
        _client.RouteAnchorPullReceived += OnPullReceived;
    }

    /// <summary>
    /// 接收 Pull 命令：只登记与校验，不在网络回调里做任何游戏动作
    /// （取消子任务/传送/跳转必须由调用方在安全点执行，方案第 6.2 节）。
    /// </summary>
    private void OnPullReceived(RouteAnchorPullCommand command)
    {
        if (command == null) return;
        if (!Enabled)
        {
            _logger.LogInformation("[联机][锚点] 锚点未启用，忽略 Pull 命令 {CommandId}", command.CommandId);
            return;
        }

        lock (_pullLock)
        {
            var anchor = _current;
            if (anchor == null || !anchor.HasAnchor)
            {
                _logger.LogWarning("[联机][锚点] 无活动锚点，忽略 Pull 命令 {CommandId}", command.CommandId);
                return;
            }
            if (!string.Equals(command.AnchorId, anchor.AnchorId, StringComparison.Ordinal))
            {
                _logger.LogWarning("[联机][锚点] Pull 命令锚点不匹配（收到={Got}, 本地={Local}），忽略",
                    command.AnchorId, anchor.AnchorId);
                return;
            }
            if (!string.IsNullOrEmpty(command.SessionId)
                && !string.Equals(command.SessionId, anchor.SessionId, StringComparison.Ordinal))
            {
                _logger.LogWarning("[联机][锚点] Pull 命令会话不匹配（收到={Got}, 本地={Local}），忽略",
                    command.SessionId, anchor.SessionId);
                return;
            }
            if (command.TargetRouteIndex < 0)
            {
                _logger.LogWarning("[联机][锚点] Pull 命令目标路线无效（{Target}），忽略", command.TargetRouteIndex);
                return;
            }
            if (_ackedPullCommandIds.ContainsKey(command.CommandId))
            {
                _logger.LogInformation("[联机][锚点] Pull 命令 {CommandId} 已回报过，忽略重复下发", command.CommandId);
                return;
            }
            if (_pendingPull != null && _pendingPull.CommandId != command.CommandId)
            {
                // 不覆盖在途命令：旧命令尚未执行完时，新命令等到旧命令被回报后再处理
                _logger.LogWarning("[联机][锚点] 已有在途 Pull 命令 {Old}，暂不接收 {New}",
                    _pendingPull.CommandId, command.CommandId);
                return;
            }

            _pendingPull = command;
            _logger.LogWarning("[联机][锚点] 已登记 Pull 命令 {CommandId}（目标路线 {Target}），等待调用方在安全点执行",
                command.CommandId, command.TargetRouteIndex);
        }
    }

    public void Dispose()
    {
        _client.RouteAnchorPullReceived -= OnPullReceived;
    }

    /// <summary>
    /// 门控：由调用方根据"配置开关 + 能力协商结果"设置。
    /// 默认 false（未启用时本类不发送任何新协议消息）。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 权威快照是否表明本机"已经提交过边界且未就绪"或"已就绪"——
    /// 用于重连/重复上报场景下区分"真实失败"与"只是重复提交"。
    /// </summary>
    private static bool IsAlreadyReportedOrReady(RouteAnchorSnapshotDto snapshot)
        => snapshot.MyState is BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.MemberStateReported
            or BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.MemberStatePullApplied
            or BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.MemberStateReady;

    /// <summary>当前权威快照（可能为 null）。</summary>
    public RouteAnchorSnapshotDto? Current => _current;

    /// <summary>是否已放行（本地缓存判断，仅用于短路；放行最终以服务端快照为准）。</summary>
    public bool IsReleased => _current?.Released == true;

    /// <summary>当前是否持有进行中的锚点。</summary>
    public bool HasActiveAnchor => _current?.HasAnchor == true && _current.IsTerminal == false;

    /// <summary>读取待执行的 Pull 命令（无则为 null）。</summary>
    public RouteAnchorPullCommand? TryGetPullCommand()
    {
        lock (_pullLock) return _pendingPull;
    }

    /// <summary>
    /// 回报 Pull 执行结果。成功后清除待执行命令（同一 commandId 不会重复回报）。
    /// 未登记命令 / 未启用 / 无命令时不发送。
    /// </summary>
    public async Task<bool> ReportPullAppliedAsync(bool success, string reason, CancellationToken ct)
    {
        if (!Enabled) return false;

        RouteAnchorPullCommand? command;
        lock (_pullLock)
        {
            command = _pendingPull;
            if (command == null || !_ackedPullCommandIds.TryAdd(command.CommandId, 0)) return false;
        }

        var sent = await _client.RouteAnchorPullAckAsync(command.AnchorId, command.CommandId, success, reason ?? "", ct);

        lock (_pullLock)
        {
            if (success && sent) _pendingPull = null;
            // 发送失败 → 撤下登记，允许后续重播再试
            if (!sent) _ackedPullCommandIds.TryRemove(command.CommandId, out _);
        }

        _logger.LogInformation("[联机][锚点] Pull 回报完成: commandId={CommandId}, success={Success}, sent={Sent}",
            command.CommandId, success, sent);
        return sent;
    }

    /// <summary>轮换/取消时清空本地状态（不发送协议消息）。</summary>
    public void Reset()
    {
        _current = null;
        lock (_pullLock)
        {
            _pendingPull = null;
            _ackedPullCommandIds.Clear();
        }
    }

    /// <summary>
    /// 声明到达边界 N 并建立锚点（幂等：服务端对同一边界的重复 enroll 复用同一锚点）。
    /// 返回服务端权威快照；未启用或失败返回 null。
    /// </summary>
    public async Task<RouteAnchorSnapshotDto?> EnrollAsync(
        string planId, int completedRouteIndex, int nextRouteIndex, CancellationToken ct)
    {
        if (!Enabled) return null;
        if (string.IsNullOrEmpty(planId))
        {
            _logger.LogWarning("[联机][锚点] planId 为空，跳过 enroll（无法与重跑/多世界隔离）");
            return null;
        }

        var snapshot = await _client.RouteAnchorEnrollAsync(
            planId, completedRouteIndex, nextRouteIndex,
            _current?.SessionId ?? "", _current?.WorldEpoch ?? 0, ct);

        if (snapshot != null) _current = snapshot;
        return snapshot;
    }

    /// <summary>提交边界结果。必须已有锚点（否则返回 null 且不发送）。</summary>
    public async Task<RouteAnchorSnapshotDto?> ReportAsync(string outcome, CancellationToken ct)
    {
        if (!Enabled) return null;
        var anchor = _current;
        if (anchor == null || !anchor.HasAnchor) return null;

        var snapshot = await _client.RouteAnchorReportAsync(
            anchor.AnchorId, anchor.SessionId, anchor.WorldEpoch, anchor.PlanId,
            anchor.CompletedRouteIndex, outcome, ct);

        if (snapshot != null) _current = snapshot;
        return snapshot;
    }

    /// <summary>声明"已到达边界且可以继续"（Ready）。服务端在全部参与者 Ready 后放行。</summary>
    public async Task<RouteAnchorSnapshotDto?> ArriveAsync(CancellationToken ct)
    {
        if (!Enabled) return null;
        var anchor = _current;
        if (anchor == null || !anchor.HasAnchor) return null;

        var snapshot = await _client.RouteAnchorArriveAsync(
            anchor.AnchorId, anchor.SessionId, anchor.WorldEpoch, anchor.PlanId, ct);

        if (snapshot != null) _current = snapshot;
        return snapshot;
    }

    /// <summary>
    /// 主动取消当前锚点（用户停止 / 任务取消 / 关房收尾）。
    /// 未启用或无活动锚点时不发送；成功后用服务端返回的权威快照刷新本地状态。
    /// </summary>
    public async Task<bool> CancelAsync(string reason, CancellationToken ct)
    {
        if (!Enabled) return false;
        var anchor = _current;
        if (anchor == null || !anchor.HasAnchor || anchor.IsTerminal) return false;

        var snapshot = await _client.RouteAnchorCancelAsync(anchor.AnchorId, reason ?? "", ct);
        if (snapshot == null) return false;

        _current = snapshot;
        lock (_pullLock)
        {
            // 取消后不再执行任何待处理 Pull（避免"已停止却仍在跳路线"）
            _pendingPull = null;
        }
        _logger.LogWarning("[联机][锚点] 已取消锚点 {AnchorId}，原因={Reason}，服务端阶段={Phase}",
            snapshot.AnchorId, reason, snapshot.Phase);
        return true;
    }

    /// <summary>
    /// 重连后对账（方案 §9.2）：重连时可能错过广播，必须以权威查询恢复本地状态，
    /// 不能依赖"恰好收到事件"。返回查询到的快照（可能为 null = 查询失败，调用方按未知处理）。
    /// </summary>
    public async Task<RouteAnchorSnapshotDto?> ReconcileAfterReconnectAsync(CancellationToken ct)
    {
        if (!Enabled) return null;

        var snapshot = await RefreshAsync(ct);
        if (snapshot == null)
        {
            _logger.LogWarning("[联机][锚点] 重连对账失败：查询无响应（保持本地未知，不得据此继续）");
            return null;
        }

        if (!snapshot.HasAnchor)
        {
            _logger.LogInformation("[联机][锚点] 重连对账：服务端已无活动锚点（本轮边界已结束或已重置）");
            return snapshot;
        }

        _logger.LogWarning("[联机][锚点] 重连对账：锚点={AnchorId}, 阶段={Phase}, 本机状态={MyState}, 待执行 Pull={Pull}",
            snapshot.AnchorId, snapshot.Phase, snapshot.MyState,
            string.IsNullOrEmpty(snapshot.MyPullCommandId) ? "无" : snapshot.MyPullCommandId);

        // 服务端给出了 Pull 命令但本地没有登记（重连期间错过事件）→ 用快照补登记，避免漏掉恢复动作
        if (!string.IsNullOrEmpty(snapshot.MyPullCommandId))
        {
            lock (_pullLock)
            {
                if (_pendingPull == null && !_ackedPullCommandIds.ContainsKey(snapshot.MyPullCommandId))
                {
                    _pendingPull = new RouteAnchorPullCommand
                    {
                        AnchorId = snapshot.AnchorId,
                        SessionId = snapshot.SessionId,
                        WorldEpoch = snapshot.WorldEpoch,
                        PlanId = snapshot.PlanId,
                        CommandId = snapshot.MyPullCommandId,
                        TargetRouteIndex = snapshot.NextRouteIndex,
                    };
                    _logger.LogWarning("[联机][锚点] 重连对账补登记 Pull 命令 {CommandId}（目标路线 {Target}）",
                        snapshot.MyPullCommandId, snapshot.NextRouteIndex);
                }
            }
        }

        return snapshot;
    }

    /// <summary>查询权威快照并刷新本地缓存。</summary>
    public async Task<RouteAnchorSnapshotDto?> RefreshAsync(CancellationToken ct)
    {
        var snapshot = await _client.QueryRouteAnchorStateAsync(ct);
        if (snapshot != null) _current = snapshot;
        return snapshot;
    }

    /// <summary>
    /// 等待服务端放行当前边界。
    ///
    /// 实现要点：
    ///   1. 未启用 → Disabled（调用方走原行为）；
    ///   2. 本地已缓存 Released → 立即返回（不阻塞）；
    ///   3. 先订阅事件、再进入轮询（事件只用于尽早反应，轮询才是权威）；
    ///   4. 轮询权威快照：Released/Stopped 立即返回；锚点消失（HasAnchor=false）视为 Failed；
    ///   5. 上限取"服务端绝对截止时间 + 宽限"，避免本地无限等待；
    ///   6. 取消 → Cancelled（调用方必须停止，而不是继续下一条路线）。
    /// </summary>
    public async Task<RouteAnchorWaitResult> WaitForReleasedAsync(
        CancellationToken ct, TimeSpan? pollInterval = null, TimeSpan? fallbackTimeout = null)
    {
        if (!Enabled) return RouteAnchorWaitResult.Disabled;

        var anchor = _current;
        if (anchor == null || !anchor.HasAnchor) return RouteAnchorWaitResult.Failed;
        if (anchor.Released) return RouteAnchorWaitResult.Released;
        if (anchor.Stopped) return RouteAnchorWaitResult.Stopped;

        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var deadlineUtc = anchor.AbsoluteDeadlineUtc == default
            ? DateTime.UtcNow + (fallbackTimeout ?? TimeSpan.FromMinutes(10))
            : anchor.AbsoluteDeadlineUtc + (fallbackTimeout ?? TimeSpan.FromSeconds(30));

        // subscribe-before-action：先挂事件，再查询/轮询，避免服务端先放行而客户端后订阅
        var releaseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnReleased(RouteAnchorSnapshotDto snapshot)
        {
            if (string.Equals(snapshot.AnchorId, anchor.AnchorId, StringComparison.Ordinal))
                releaseTcs.TrySetResult(true);
        }

        void OnStopped(RouteAnchorSnapshotDto snapshot)
        {
            if (string.Equals(snapshot.AnchorId, anchor.AnchorId, StringComparison.Ordinal))
                stopTcs.TrySetResult(true);
        }

        _client.RouteAnchorReleasedReceived += OnReleased;
        _client.RouteAnchorStoppedReceived += OnStopped;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // 事件优先（低延迟）
                if (releaseTcs.Task.IsCompleted) return RouteAnchorWaitResult.Released;
                if (stopTcs.Task.IsCompleted) return RouteAnchorWaitResult.Stopped;

                var snapshot = await RefreshAsync(ct);

                if (snapshot != null)
                {
                    if (!snapshot.HasAnchor)
                    {
                        // 锚点已被替换/清空（例如轮次切换）：无法确认，按失败处理
                        _logger.LogWarning("[联机][锚点] 等待放行时锚点已消失（anchorId={AnchorId}）", anchor.AnchorId);
                        return RouteAnchorWaitResult.Failed;
                    }

                    if (snapshot.Released) return RouteAnchorWaitResult.Released;
                    if (snapshot.Stopped) return RouteAnchorWaitResult.Stopped;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    _logger.LogError("[联机][锚点] 等待放行超时（anchorId={AnchorId}, deadline={Deadline:O}）",
                        anchor.AnchorId, deadlineUtc);
                    return RouteAnchorWaitResult.Failed;
                }

                // 等事件或到下一个轮询点，谁先到都行
                var delay = Task.Delay(interval, ct);
                await Task.WhenAny(
                    delay,
                    releaseTcs.Task,
                    stopTcs.Task);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[联机][锚点] 等待放行被取消（anchorId={AnchorId}）", anchor.AnchorId);
            return RouteAnchorWaitResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][锚点] 等待放行异常（按失败处理）");
            return RouteAnchorWaitResult.Failed;
        }
        finally
        {
            _client.RouteAnchorReleasedReceived -= OnReleased;
            _client.RouteAnchorStoppedReceived -= OnStopped;
        }
    }

    /// <summary>
    /// 轮末边界收口（最后一条路线完成 → Finished）。
    /// 语义：仍需全员确认后才算本轮结束，避免个别成员提前关房/离场导致"表面继续、实际走散"。
    /// nextRouteIndex 使用共享协议里的轮末哨兵。
    /// </summary>
    public async Task<RouteAnchorWaitResult> CompleteRoundEndAsync(
        string planId, int completedRouteIndex, string outcome, CancellationToken ct)
        => await CompleteBoundaryAsync(
            planId, completedRouteIndex,
            BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.RoundEndNextRouteIndex,
            outcome, ct);

    /// <summary>
    /// 边界收口的完整流程（阶段 2 正常路径）：
    /// Enroll → Report → Ready → 等 Released。
    /// 返回 true 仅当拿到 Released；其余结果调用方必须停止，不得进入下一条路线。
    /// </summary>
    public async Task<RouteAnchorWaitResult> CompleteBoundaryAsync(
        string planId, int completedRouteIndex, int nextRouteIndex, string outcome, CancellationToken ct)
    {
        if (!Enabled) return RouteAnchorWaitResult.Disabled;

        var enrolled = await EnrollAsync(planId, completedRouteIndex, nextRouteIndex, ct);
        if (enrolled == null)
        {
            // 协作重跑窗口：服务端明确说"推进权归重跑"→ 让位并继续（方案 §9.4：不得两套权威并存）
            if (string.Equals(_client.LastRouteAnchorErrorCode, "rerun_in_progress", StringComparison.Ordinal))
            {
                _logger.LogInformation("[联机][锚点] 服务端处于协作重跑阶段（rerun_in_progress），锚点让位，本轮由重跑收口");
                return RouteAnchorWaitResult.YieldedToRerun;
            }

            _logger.LogWarning("[联机][锚点] enroll 失败，无法确认边界收口（边界={Boundary}）", completedRouteIndex);
            return RouteAnchorWaitResult.Failed;
        }

        var reported = await ReportAsync(outcome, ct);
        if (reported == null)
        {
            // 重连/重试场景：服务端可能已收到过本次报告（Duplicate），或本机已就绪（Ready）。
            // 这类"已具备前提"的情况不能当成失败——否则一次正常重连就会让整轮被误停。
            // 以权威快照为准判定（方案 §9.2：重连必须以查询恢复状态，不能依赖事件）。
            var current = await RefreshAsync(ct);
            if (current != null && current.Released) return RouteAnchorWaitResult.Released;
            if (current != null && current.Stopped) return RouteAnchorWaitResult.Stopped;
            if (current == null || !IsAlreadyReportedOrReady(current))
            {
                _logger.LogWarning("[联机][锚点] report 失败且快照未显示已就绪状态，无法确认边界收口（锚点={AnchorId}, 本机状态={State}）",
                    enrolled.AnchorId, current?.MyState ?? "<无快照>");
                return RouteAnchorWaitResult.Failed;
            }

            _logger.LogInformation("[联机][锚点] report 被拒但权威快照显示本机已就绪（本机状态={State}），继续走 Ready 与等待放行",
                current.MyState);
            reported = current;
        }

        // 服务端可能在最后一个成员报告前就已放行（本机迟到）：先看快照再决定是否要 Ready
        if (reported.Released) return RouteAnchorWaitResult.Released;
        if (reported.Stopped) return RouteAnchorWaitResult.Stopped;

        var arrived = await ArriveAsync(ct);
        if (arrived == null)
        {
            _logger.LogWarning("[联机][锚点] Ready 失败，无法确认边界收口（锚点={AnchorId}）", enrolled.AnchorId);
            return RouteAnchorWaitResult.Failed;
        }

        if (arrived.Released) return RouteAnchorWaitResult.Released;

        return await WaitForReleasedAsync(ct);
    }
}
