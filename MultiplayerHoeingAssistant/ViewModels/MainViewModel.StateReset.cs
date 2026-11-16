using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _resetRequestGate = new(1, 1);
    private readonly Dictionary<string, Task<CommandResult>> _handledStateResets = new(StringComparer.Ordinal);

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
    private Task<CommandResult> ResetLocalExecutionStateAsync(string? resetId = null, string? expectedBatchId = null)
    {
        if (string.IsNullOrWhiteSpace(resetId))
            return ResetLocalExecutionStateCoreAsync(resetId, expectedBatchId);
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
            try { completion.TrySetResult(await ResetLocalExecutionStateCoreAsync(resetId, expectedBatchId)); }
            catch (Exception ex)
            {
                completion.TrySetResult(new() { ErrorCode = "reset_unconfirmed", Message = "状态重置未完成：" + ex.Message });
            }
        }
    }
}
