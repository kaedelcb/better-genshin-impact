#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public partial class CoordinatorClient
{
    private readonly CancellationTokenSource _worldPlanStopCts = new();
    private readonly SemaphoreSlim _worldPlanTransportGate = new(1, 1);
    private string? _worldPlanTransferId;
    private string? _worldPlanOriginRoom;
    private readonly HashSet<string> _retiredWorldPlanRooms = new(StringComparer.Ordinal);
    private bool _explicitRoomEventPending;
    private bool _strictWorldPlanEvents;
    private string? _explicitRoomEventCode;
    private readonly List<GatewayEnvelope> _explicitRoomEvents = new();

    public bool SupportsWorldPlan => _gateway?.SupportsCapability(GatewayProtocol.WorldPlanCapability) == true;
    internal bool UsesV1WorldPlanServer => !SupportsWorldPlan && _gateway?.SupportsCapability("hoeing.worldPlan.v1") == true;
    private bool IsWorldPlanTransferring
    {
        get
        {
            lock (_roomLock) return _worldPlanTransferId != null
                && (_roomSession == null || _roomSession.Ended || _roomSession.RoomCode == _worldPlanOriginRoom);
        }
    }

    public async Task<WorldPlanAdmission?> BeginWorldPlanAsync(CancellationToken ct, bool forceLegacy = false, bool freezeCurrentParty = false)
        => ReadAdmission(await WorldPlanRpcAsync(GatewayProtocol.Names.WorldPlanBegin, new { forceLegacy, freezeCurrentParty }, false, ct));

    public async Task<WorldPlanAdmission?> AcquireWorldPlanAsync(CancellationToken ct)
        => ReadAdmission(await WorldPlanRpcAsync(GatewayProtocol.Names.WorldPlanAcquire, null, true, ct));

    private WorldPlanAdmission? ReadAdmission(GatewayEnvelope response)
    {
        // The gateway serializes the successful null Acquire result as payload:null.
        // A non-null object without a snapshot is a malformed response, not proof of no plan.
        if (response.Payload == null) return null;
        var snapshot = response.Get<MultiWorldStartPlan>("snapshot")
            ?? throw new MultiplayerSessionFailedException("开队预判返回了无效的准入响应");
        var admission = new WorldPlanAdmission
        {
            PlanId = response.GetString("planId"), Ticket = response.GetString("ticket"), Snapshot = snapshot,
        };
        if (string.IsNullOrEmpty(admission.PlanId) || string.IsNullOrEmpty(admission.Ticket)
            || snapshot.PlanId != admission.PlanId)
            throw new MultiplayerSessionFailedException("开队预判凭据不完整");
        if (snapshot.Stage != "Legacy")
            lock (_roomLock) _strictWorldPlanEvents = true;
        return admission;
    }

    public Task<MultiWorldStartPlan> ReportWorldPlanAsync(WorldPlanAdmission admission, bool known, int remainingCount, CancellationToken ct)
        => PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanReport, admission,
            new { planId = admission.PlanId, ticket = admission.Ticket, known, remainingCount }, false, ct);

    public Task<MultiWorldStartPlan> ReadWorldPlanAsync(WorldPlanAdmission admission, CancellationToken ct)
        => PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanGet, admission, PlanCredential(admission), true, ct);

    public Task<MultiWorldStartPlan> AckWorldPlanAsync(WorldPlanAdmission admission, CancellationToken ct)
        => PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanAck, admission, PlanCredential(admission), false, ct);

    /// <summary>申请进世界前验证实际网络房间，而不是仅凭一份跨房计划快照放行。</summary>
    public Task<MultiWorldStartPlan> ConfirmWorldPlanTargetAsync(WorldPlanAdmission admission, CancellationToken ct)
        => PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanValidateTarget, admission, PlanCredential(admission), true, ct);

    public async Task<MultiWorldStartPlan> LeaveWorldPlanSourceAsync(WorldPlanAdmission admission, CancellationToken ct)
    {
        var remaining = admission.Snapshot.ExpiresAtUtc > DateTime.UtcNow
            ? admission.Snapshot.ExpiresAtUtc - DateTime.UtcNow : TimeSpan.FromSeconds(120);
        using var budget = CreateWorldPlanBudget(ct, _testStrictWaitBudget ?? remaining);
        var snapshot = await PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanLeaveSource, admission,
            PlanCredential(admission), false, budget.Token);
        while (!snapshot.SourceReleased)
        {
            if (snapshot.Stage == "Aborted") throw new MultiplayerSessionFailedException("开队交接已中止");
            await Task.Delay(_testWaitRetryIntervalMs ?? 250, budget.Token);
            snapshot = await ReadWorldPlanAsync(admission, budget.Token);
        }
        ThrowIfWorldPlanStopped(budget.Token);
        return snapshot;
    }

    public async Task<MultiWorldStartPlan> AbortWorldPlanAsync(WorldPlanAdmission admission, CancellationToken ct)
    {
        // 停止后的尽力撤销只用调用方给定的独立预算；绝不恢复房间、建立新房间或放行游戏操作。
        using var budget = CreateRpcBudget(ct, includeRoom: false);
        if (_gateway == null) throw new MultiplayerSessionFailedException("协调连接不存在");
        if (!IsConnected) await _gateway.ReconnectStartAsync(budget.Token);
        await _gateway.HelloAsync(budget.Token);
        var response = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.WorldPlanAbort,
            PlanCredential(admission), null, budget.Token);
        return RequireSnapshot(response, admission);
    }

    public async Task<MultiWorldStartPlan> CreateWorldPlanTargetAsync(WorldPlanAdmission admission, CancellationToken ct)
    {
        ThrowIfWorldPlanStopped(ct);
        using var budget = CreateWorldPlanBudget(ct, _testStrictWaitBudget ?? TimeSpan.FromSeconds(120));
        await _recoveryGate.WaitAsync(budget.Token);
        try
        {
            ThrowIfWorldPlanStopped(budget.Token);
            DetachRoomForExplicitChange();
            BeginExplicitRoomEvents(null);
            var snapshot = await PlanSnapshotAsync(GatewayProtocol.Names.WorldPlanCreateTarget, admission,
                PlanCredential(admission), false, budget.Token);
            ThrowIfWorldPlanStopped(budget.Token);
            if (snapshot.Stage != "Committed" || string.IsNullOrEmpty(snapshot.TargetRoomCode)
                || string.IsNullOrEmpty(snapshot.TargetRoomSessionId)
                || snapshot.RemainingHostUids.Count == 0 || snapshot.RemainingHostUids[0] != _playerUid)
                throw new MultiplayerSessionFailedException("本端未获得本轮建房许可");
            var session = BeginRoomSession(snapshot.TargetRoomCode, _playerName ?? "", _playerUid,
                snapshot.HostConfig?.HoeingBatchId);
            MarkRoomReady(session);
            CompleteExplicitRoomEvents();
            ThrowIfWorldPlanStopped(budget.Token);
            if (!IsCurrent(session)) throw new MultiplayerSessionFailedException("新房间准入已撤销");
            return snapshot;
        }
        finally { ClearExplicitRoomEvents(); _recoveryGate.Release(); }
    }

    private static object PlanCredential(WorldPlanAdmission admission)
        => new { planId = admission.PlanId, ticket = admission.Ticket };

    private async Task<MultiWorldStartPlan> PlanSnapshotAsync(string name, WorldPlanAdmission admission,
        object payload, bool query, CancellationToken ct)
        => RequireSnapshot(await WorldPlanRpcAsync(name, payload, query, ct), admission);

    private static MultiWorldStartPlan RequireSnapshot(GatewayEnvelope response, WorldPlanAdmission admission)
    {
        var snapshot = response.Get<MultiWorldStartPlan>("snapshot");
        if (snapshot == null || snapshot.PlanId != admission.PlanId)
            throw new MultiplayerSessionFailedException("开队预判返回了不匹配的计划");
        return snapshot;
    }

    private CancellationTokenSource CreateWorldPlanBudget(CancellationToken ct, TimeSpan? timeout = null)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token, _worldPlanStopCts.Token);
        budget.CancelAfter(timeout ?? _testAuxiliaryBudget ?? TimeSpan.FromSeconds(10));
        return budget;
    }

    private void ThrowIfWorldPlanStopped(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lifetimeCts.Token.ThrowIfCancellationRequested();
        _worldPlanStopCts.Token.ThrowIfCancellationRequested();
        lock (_roomLock)
            if (_taskStopped) throw new MultiplayerSessionFailedException("本次联机任务已经停止");
    }

    private async Task<GatewayEnvelope> WorldPlanRpcAsync(string name, object? payload, bool query, CancellationToken ct)
    {
        ThrowIfWorldPlanStopped(ct);
        bool transferring;
        lock (_roomLock) transferring = _worldPlanTransferId != null;
        using var budget = CreateWorldPlanBudget(ct, transferring ? TimeSpan.FromSeconds(120) : null);
        if (_gateway == null) throw new MultiplayerSessionFailedException("协调连接不存在");
        var needsHello = false;
        while (true)
        {
            try
            {
                using var attempt = CreateWorldPlanBudget(budget.Token);
                await EnsureWorldPlanTransportAsync(attempt.Token, needsHello);
                needsHello = false;
                var response = query
                    ? await _gateway.QueryAsync(name, payload, null, attempt.Token)
                    : await _gateway.InvokeCommandAsync(name, payload, null, attempt.Token);
                ThrowIfWorldPlanStopped(budget.Token);
                return response;
            }
            catch (GatewayErrorException ex) when (transferring && ex.Code == "handshake_required" && !budget.IsCancellationRequested)
            {
                needsHello = true;
            }
            catch (OperationCanceledException) when (transferring && !budget.IsCancellationRequested)
            {
                // 本次传输超时可以重发同一幂等命令；外层停止或总预算取消仍立即传播。
            }
            catch (Exception ex) when (transferring && !budget.IsCancellationRequested
                && ex is not GatewayErrorException && ex is IOException or TimeoutException or InvalidOperationException)
            {
                // 仅重试同一幂等计划命令；业务拒绝、取消和原计划终态不能变成重试/新建房间。
                _logger.LogDebug(ex, "[多世界续跑] 交接网络暂不可用，按原计划重试");
            }
            await Task.Delay(_testWaitRetryIntervalMs ?? 250, budget.Token);
            ThrowIfWorldPlanStopped(budget.Token);
        }
    }

    private async Task EnsureWorldPlanTransportAsync(CancellationToken ct, bool forceHello = false)
    {
        await _worldPlanTransportGate.WaitAsync(ct);
        try
        {
            ThrowIfWorldPlanStopped(ct);
            if (!IsConnected) { await _gateway!.ReconnectStartAsync(ct); forceHello = true; }
            if (forceHello) await _gateway!.HelloAsync(ct);
        }
        finally { _worldPlanTransportGate.Release(); }
    }

    private async Task RecoverWorldPlanTransportAsync(bool start)
    {
        try
        {
            using var budget = CreateWorldPlanBudget(CancellationToken.None);
            if (start) await _gateway!.ReconnectStartAsync(budget.Token);
            await EnsureWorldPlanTransportAsync(budget.Token, true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[多世界续跑] 交接连接恢复尚未确认，保持原计划等待重试"); }
    }

    /// <summary>仅在本次计划已获得参与凭据后进入；原房结束不再解释为本场锄地失败。</summary>
    public IDisposable EnterWorldPlanTransfer(string planId, string initialRoomCode)
    {
        ThrowIfWorldPlanStopped(CancellationToken.None);
        if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(initialRoomCode))
            throw new ArgumentException("交接计划或来源房间为空");
        lock (_roomLock)
        {
            if (_worldPlanTransferId != null || _roomSession?.RoomCode != initialRoomCode)
                throw new MultiplayerSessionFailedException("交接来源房间已变化");
            _strictWorldPlanEvents = true;
            _worldPlanTransferId = planId;
            _worldPlanOriginRoom = initialRoomCode;
            _retiredWorldPlanRooms.Add(initialRoomCode);
        }
        EndRoomSession("world-plan-transfer");
        return new WorldPlanTransferScope(this, planId);
    }

    private sealed class WorldPlanTransferScope(CoordinatorClient client, string planId) : IDisposable
    {
        public void Dispose()
        {
            lock (client._roomLock)
                if (client._worldPlanTransferId == planId)
                {
                    client._worldPlanTransferId = null;
                    client._worldPlanOriginRoom = null;
                }
        }
    }

    private void BeginExplicitRoomEvents(string? roomCode)
    {
        lock (_roomLock)
        {
            _explicitRoomEvents.Clear();
            _explicitRoomEventPending = true;
            _explicitRoomEventCode = roomCode;
        }
    }

    private void ClearExplicitRoomEvents()
    {
        lock (_roomLock)
        {
            _explicitRoomEventPending = false;
            _explicitRoomEventCode = null;
            _explicitRoomEvents.Clear();
        }
    }

    private void CompleteExplicitRoomEvents()
    {
        GatewayEnvelope[] events;
        lock (_roomLock)
        {
            events = _explicitRoomEvents.ToArray();
            _explicitRoomEvents.Clear();
            _explicitRoomEventPending = false;
            _explicitRoomEventCode = null;
        }
        foreach (var message in events) DispatchEvt(message);
    }

    private bool AcceptRoomEvent(GatewayEnvelope env)
    {
        // 原流程保留旧事件兼容；新计划关键状态只接受带房间身份的通知。
        if (string.IsNullOrEmpty(env.RoomCode))
        {
            lock (_roomLock)
                return !_strictWorldPlanEvents || env.Name == GatewayProtocol.Events.RoomVersionCheckRejected
                    || !(env.Name.StartsWith("room.", StringComparison.Ordinal)
                        || env.Name.StartsWith("world.", StringComparison.Ordinal)
                        || env.Name.StartsWith("sync.", StringComparison.Ordinal)
                        || env.Name.StartsWith("route.", StringComparison.Ordinal)
                        || env.Name.StartsWith("fight.", StringComparison.Ordinal)
                        || env.Name.StartsWith("kazuha.", StringComparison.Ordinal));
        }
        lock (_roomLock)
        {
            if (_retiredWorldPlanRooms.Contains(env.RoomCode)) return false;
            if (_explicitRoomEventPending && (_explicitRoomEventCode == null || env.RoomCode == _explicitRoomEventCode))
            {
                // 入房响应可能晚于名单/就绪广播；只缓存在途显式入房事件，确认成功后再消费。
                if (_explicitRoomEvents.Count < 128) _explicitRoomEvents.Add(env);
                return false;
            }
            return _roomSession == null || env.RoomCode == _roomSession.RoomCode;
        }
    }
}
