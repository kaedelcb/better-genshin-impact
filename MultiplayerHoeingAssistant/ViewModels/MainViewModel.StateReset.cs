using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _resetRequestGate = new(1, 1);
    private readonly Dictionary<string, Task<CommandResult>> _handledStateResets = new(StringComparer.Ordinal);
    private string? _lastCoordinatedAdmissionContext;
    private int _lastCoordinatedAdmissionGeneration;

    // Server generations distinguish a lagging local batch from a real successor.
    // Batch-id inequality alone cannot tell these two cases apart.
    internal static bool ResetTargetsSuccessor(string? activeBatchId, int activeGeneration,
        string? resetBatchId, int resetGeneration, int lastAcceptedGeneration = 0)
    {
        if (resetGeneration > 0 && resetGeneration < lastAcceptedGeneration) return true;
        if (activeBatchId is null) return false;
        if (string.Equals(activeBatchId, resetBatchId, StringComparison.Ordinal))
            return resetGeneration > 0 && resetGeneration != activeGeneration;
        return resetGeneration <= 0 || resetGeneration <= activeGeneration;
    }

    // Caller holds _batchGate. Core checks again after acquiring the reset gate.
    private bool IsCoordinatedResetStale(string? batchId, int generation)
    {
        var active = _activeBatch;
        var latest = _lastCoordinatedAdmissionContext == CurrentControlContext()
            ? _lastCoordinatedAdmissionGeneration : 0;
        return active?.Start is not null
            ? ResetTargetsSuccessor(active.BatchId, active.Generation, batchId, generation, latest)
            : generation > 0 && Math.Max(active?.Generation ?? 0, latest) > generation;
    }

    // A reset receipt can be evicted or lost while the executor is rebuilding BGI.
    // Re-authorize cleanup only for the same old batch; never reset a successor.
    internal static async Task ConfirmManualResetCleanupAsync(string resetId, string batchId,
        string selfUid, Func<string, string, Task> confirm,
        Func<Task<Services.CoordinatedBatchSnapshot?>> current,
        Func<string, int, string, string, Task<Services.CoordinatedBatchResetResult>> authorize)
    {
        try { await confirm(resetId, batchId); return; }
        catch
        {
            var state = await current();
            if (state is null || state.BatchId != batchId) return;
            var repairId = "reset_cleanup_" + Guid.NewGuid().ToString("N");
            var repair = await authorize(repairId, state.Generation, batchId, selfUid);
            if (repair.Reason == "stale_reset")
            {
                state = await current();
                if (state is null || state.BatchId != batchId) return;
                throw new IOException("服务端原批次清理身份仍未确认");
            }
            if (!repair.Found) return;
            if (repair.BatchId != batchId)
                throw new IOException("服务端返回了不同的清理批次");
            await confirm(repairId, batchId);
        }
    }

    // A retransmitted reset joins its original operation, including while it is still running.
    // It must never restart BGI a second time after the user has started a new task.
    private Task<CommandResult> ResetLocalExecutionStateAsync(string? resetId = null, string? expectedBatchId = null,
        int expectedGeneration = 0)
    {
        if (string.IsNullOrWhiteSpace(resetId))
            return ResetLocalExecutionStateCoreAsync(resetId, expectedBatchId, expectedGeneration);
        TaskCompletionSource<CommandResult> completion;
        lock (_resetAckGate)
        {
            if (_handledStateResets.TryGetValue(resetId, out var existing)) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _handledStateResets[resetId] = completion.Task;
            if (_handledStateResets.Count > 256)
            {
                var finished = _handledStateResets.FirstOrDefault(item => item.Value.IsCompleted);
                if (finished.Key is not null) _handledStateResets.Remove(finished.Key);
            }
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { completion.TrySetResult(await ResetLocalExecutionStateCoreAsync(resetId, expectedBatchId, expectedGeneration)); }
            catch (Exception ex)
            {
                completion.TrySetResult(new() { ErrorCode = "reset_unconfirmed", Message = "状态重置未完成：" + ex.Message });
            }
        }
    }
}
