using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    internal bool AppendAccountPreparationProcessContinuation(string runId, string safeJobId, int safeAttempt,
        ManualStopFence fence, WorkflowStopAuthorityRecord source, out WorkflowRunRecord? latest)
    {
        latest = null;
        if (fence.Version < 0 || !WorkflowStopAuthority.TryEpoch(fence.Epoch, out _)
            || fence.Frequency != System.Diagnostics.Stopwatch.Frequency || string.IsNullOrWhiteSpace(source.IntentId)
            || source.Epoch != fence.Epoch || source.Version != fence.Version
            || source.MonotonicFrequency != fence.Frequency || source.IntentTimestamp <= 0
            || fence.LastManualStopTimestamp >= source.IntentTimestamp || IsStartupSourceRevoked(source)) return false;
        lock (_gate)
        {
            var original = Load(runId);
            if (original is null || !AccountPreparationRecovery.CanResume(original)
                || original.StopAuthority is not { } intent || IsStartupSourceRevoked(intent)
                || WorkflowStopAuthority.Current(original) is not { } previous
                || IsStartupSourceRevoked(previous) || previous.Epoch == fence.Epoch
                || previous.MonotonicFrequency != fence.Frequency
                || fence.LastManualStopTimestamp >= previous.IntentTimestamp
                || !WorkflowStopAuthority.TryEpoch(previous.Epoch, out var oldEpoch)
                || CommandExecutor.ProbeOriginalBgiProcess(oldEpoch) != false
                || AccountPreparationRecovery.LatestNotStarted(original) is not { } safe
                || safe.JobId != safeJobId || safe.Attempt != safeAttempt) return false;
            var continuation = new AccountPreparationProcessContinuation(previous.Epoch,
                intent with { Epoch = fence.Epoch, Version = fence.Version }, safeJobId, safeAttempt);
            return UpdateMergingCore(runId, current =>
            {
                var proof = AccountPreparationRecovery.LatestNotStarted(current);
                if (!AccountPreparationRecovery.CanResume(current) || proof?.JobId != safeJobId
                    || proof.Attempt != safeAttempt || WorkflowStopAuthority.Current(current) != previous
                    || IsStartupSourceRevoked(source) || IsStartupSourceRevoked(intent)
                    || CommandExecutor.ProbeOriginalBgiProcess(oldEpoch) != false) return false;
                current.AccountPreparationProcessContinuations.Add(continuation);
                return true;
            }, out latest, authorizedAccountPreparation: continuation);
        }
    }
}
