#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public partial class CoordinatorClient
{
    // 在线等换轮时仍为旧房发送心跳，确实失去原连接后才结束旧房恢复上下文。
    internal void MarkPreviousRoundRoomForDeparture()
        => RetirePreviousRoundRoomIfTransportLost(markForDeparture: true);

    private void RetirePreviousRoundRoomIfTransportLost(bool markForDeparture = false)
    {
        long epoch;
        lock (_roomLock)
        {
            if (_roomSession is not { Ended: false } session) return;
            if (markForDeparture) session.LeavingForNextRound = true;
            if (!session.LeavingForNextRound || (IsConnected && session.TransportId == CurrentTransportId
                && session.TransportGeneration == session.ReadyTransportGeneration)) return;
            epoch = session.Epoch;
        }
        EndRoomSession("world-round-transport-lost", expectedEpoch: epoch);
    }

    /// <summary>换轮期间先恢复协调连接，再由调用方查房或建房。</summary>
    internal async Task<bool> WaitForNextRoundTransportAsync(DateTime deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RetirePreviousRoundRoomIfTransportLost();
        BgiGatewayClient gateway;
        lock (_roomLock)
        {
            if (_taskStopped || _gateway is not { } current) return false;
            gateway = current;
            if (IsConnected) return true;
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return false;
        using var budget = CreateRpcBudget(ct, remaining, includeRoom: false);
        try
        {
            while (true)
            {
                using var attempt = CreateRpcBudget(budget.Token, includeRoom: false);
                try
                {
                    await _recoveryGate.WaitAsync(attempt.Token);
                    try
                    {
                        RetirePreviousRoundRoomIfTransportLost();
                        lock (_roomLock)
                        {
                            if (_taskStopped || !ReferenceEquals(_gateway, gateway)) return false;
                            // 活动房间和计划交接使用各自的恢复路径。
                            if (_roomSession is { Ended: false } || IsWorldPlanTransferring) return true;
                        }

                        // Reconnecting 由 SignalR 负责；只有完全断开后才主动 Start。
                        if (gateway.IsDisconnected) await gateway.ReconnectStartAsync(attempt.Token);
                        attempt.Token.ThrowIfCancellationRequested();
                        if (IsConnected)
                        {
                            var transportId = CurrentTransportId;
                            await gateway.HelloAsync(attempt.Token);
                            attempt.Token.ThrowIfCancellationRequested();
                            lock (_roomLock)
                            {
                                if (_taskStopped || !ReferenceEquals(_gateway, gateway)) return false;
                                if (IsConnected && transportId == CurrentTransportId) return true;
                            }
                        }
                    }
                    finally { _recoveryGate.Release(); }
                }
                catch (Exception ex) when (!budget.IsCancellationRequested && IsTransportFailure(ex))
                {
                    _logger.LogDebug(ex, "[联机] 换轮协调连接尚未恢复");
                }

                await Task.Delay(_testWaitRetryIntervalMs ?? 1000, budget.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return false;
        }
    }
}
