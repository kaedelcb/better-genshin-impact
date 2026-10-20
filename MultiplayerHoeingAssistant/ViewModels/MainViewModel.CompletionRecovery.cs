using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    internal static async Task<bool> EnsureCompletionExecutorAsync(OnlineHoeingBatch batch, CommandExecutor executor,
        Func<BgiExternalClient?> externalClient, Func<CancellationToken, Task<string?>> ensureExecutionReady,
        Func<string?> completionRequestKey, bool nativeResume, Action requireAuthority, CancellationToken token)
    {
        var epoch = batch.ExecutionEpoch ?? throw new IOException("原完成动作进程身份缺失");
        var ticket = batch.TakeoverTicket;
        var policy = batch.Policy ?? throw new IOException("原完成策略缺失");
        BgiExternalClient External()
        {
            requireAuthority();
            return externalClient() is { State: BgiExternalLinkState.Ready } client
                && CoordinatedBatchLoop.SameEpoch(client.ServerEpoch, epoch) ? client
                : throw new IOException("等待同一恢复进程的有效通道");
        }
        requireAuthority();
        if (!batch.PriorityRecoveryEnabled) return true;
        var previous = batch.ExecutionEpoch ?? epoch;
        if (CommandExecutor.ProbeOriginalBgiProcess(previous) != false)
        {
            if (externalClient() is not { State: BgiExternalLinkState.Ready } live
                || !CoordinatedBatchLoop.SameEpoch(live.ServerEpoch, previous)) return false;
            epoch = previous;
            if (batch.ProcessRecoveryPreviousEpoch is null) return true;
        }
        else
        {
            batch.ObserveRecoveryStopSignal();
            token.ThrowIfCancellationRequested();
            if (batch.RecoveryStopSignal is null)
                throw new IOException("原停止事实已丢失，未猜测原安排仍获授权");
            if (executor.TakeoverTicketSnapshot is { } held && held != batch.TakeoverTicket) return false;
            if (!await executor.AbandonExitedCoordinatedOwnershipAsync(batch.TakeoverTicket, previous, token).ConfigureAwait(false))
                return false;
            batch.PolicyTicketReleased = true;
            if (batch.PolicyTerminalResult is { Status: "completed", Cancelled: false, ExecutionExitConfirmed: true }
                || policy.Policy == TaskConflictPolicy.Stop)
            {
                // Only the vanished process's retained-result obligation is discharged here.
                // An already observed completion stays completed; no new BGI is required for it.
                batch.PolicyResultAcknowledged = true;
                return true;
            }
            if (policy.Policy == TaskConflictPolicy.Resume && !nativeResume)
                throw new IOException("原计划恢复进程已退出，保留原Runner消费责任等待同计划接续");
            var ready = await ensureExecutionReady(token).ConfigureAwait(false);
            if (ready is not null) throw new IOException(ready);
            var replacement = externalClient() is { State: BgiExternalLinkState.Ready } connected
                ? connected.ServerEpoch : null;
            if (replacement is null || CoordinatedBatchLoop.SameEpoch(previous, replacement)) return false;
            var stop = await externalClient()!.ReadStateContinuationStopVersionAsync(replacement, token).ConfigureAwait(false);
            if (stop is null) return false;
            if (stop != 0) throw new BgiStateContinuationStoppedException("后继BGI已被明确停止，不恢复原完成动作");
            requireAuthority();
            token.ThrowIfCancellationRequested();
            if (!batch.BeginCompletionProcessRecovery(previous, replacement, completionRequestKey(), nativeResume,
                    CommandExecutor.ProbeOriginalBgiProcess)) return false;
            epoch = replacement;
            ticket = null;
            batch.PersistRecovery?.Invoke();
        }
        var current = External();
        await current.EnsureCoordinatedRecoveryStopAsync(batch, token).ConfigureAwait(false);
        var idle = await executor.CaptureExecutionBoundarySnapshotAsync().WaitAsync(token).ConfigureAwait(false);
        if (!idle.Known || idle.SlotOccupied || idle.Active is not null
            || idle.ProcessId != epoch.ProcessId || idle.StartTicksUtc != epoch.StartTicksUtc) return false;
        if (executor.TakeoverTicketSnapshot is { } reservation && reservation != batch.TakeoverTicket) return false;
        if (!batch.CompletionRecoveryReservationAcquired)
        {
            var acquired = await executor.ExecuteSuspendAsync("原完成动作进程退出后接续", idle,
                retainUntilAcknowledged: true, expectedStopVersion: batch.StateContinuationStopVersion,
                recoveryStopSignal: batch.RecoveryStopSignal?.Name).ConfigureAwait(false);
            ticket = batch.TakeoverTicket = executor.TakeoverTicketSnapshot;
            if (ticket is not null) batch.PolicyTicketReleased = false;
            batch.PersistRecovery?.Invoke();
            if (acquired.ErrorCode is "manual_stop_fence" or "manual_stop_cooldown")
                throw new BgiStateContinuationStoppedException("接续前已被明确停止");
            if (acquired.Status != "success") return false;
            batch.CompletionRecoveryReservationAcquired = true;
            batch.PersistRecovery?.Invoke();
        }
        if (!nativeResume)
        {
            await executor.ReconcileCompletionTicketAsync(batch, token).ConfigureAwait(false);
            if (!batch.PolicyTicketReleased)
            {
                var released = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: batch.TakeoverTicket,
                    cancellationToken: token).ConfigureAwait(false);
                if (released.Status != "success")
                {
                    await executor.ReconcileCompletionTicketAsync(batch, token).ConfigureAwait(false);
                    if (!batch.PolicyTicketReleased) return false;
                }
                batch.PolicyTicketReleased = true;
            }
        }
        batch.ObserveRecoveryStopSignal();
        token.ThrowIfCancellationRequested();
        batch.CompleteProcessRecovery();
        batch.PersistRecovery?.Invoke();
        return true;

    }
}
