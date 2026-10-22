using System;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>只允许有同身份零游戏输入证明的账号准备失败沿原运行再准备。</summary>
internal static class AccountPreparationRecovery
{
    internal const string NotStartedCode = "account_preparation_not_started";

    internal static bool IsNotStarted(PrerequisiteActionRecord? record)
        => record is { Kind: "prerequisite.account", State: PrerequisiteActionState.Failed,
                ErrorCode: NotStartedCode, SendAttempted: true, ExecutionExitConfirmed: true,
                ObservedTerminal: "failed", EffectState: "failed", ServerRejectionEvidence: null }
            && record.ExecutionExitDisposition is "execution_exited" or "never_started"
            && !string.IsNullOrWhiteSpace(record.JobId) && !string.IsNullOrWhiteSpace(record.AccountKey)
            && Guid.TryParse(record.WireRunId, out _) && WorkflowStopAuthority.TryEpoch(record.Epoch, out _)
            && record.StrategyIndex >= 0 && record.Occurrence >= 0 && record.LoopIteration >= 0
            && TerminalReleaseEvidence.PrerequisiteSettled(record);

    internal static PrerequisiteActionRecord? LatestNotStarted(WorkflowRunRecord run)
    {
        if (run.Cursor is not { } cursor) return null;
        var record = run.PrerequisiteActions.LastOrDefault(a => a.Kind == "prerequisite.account"
            && a.NodeId == cursor.NodeId && a.Occurrence == cursor.Occurrence
            && a.LoopIteration == cursor.LoopIteration);
        return IsNotStarted(record) && record!.WireRunId == run.WireRunId ? record : null;
    }

    internal static bool CanResume(WorkflowRunRecord run)
        => run.State == WorkflowRunState.Interrupted && !run.StopRequested && !run.NonExecutingDiagnostic
            && run.ExecutionResetRelease is null && !run.TailReached && run.TerminalRelease is null
            && run.CurrentSubmission is null && run.PendingCompletion is null
            && LatestNotStarted(run) is not null && !RunStore.HasUnresolvedTerminalResponsibility(run)
            && run.PrerequisiteActions.All(a =>
                !a.SendAttempted && string.IsNullOrEmpty(a.JobId) && a.State != PrerequisiteActionState.Unknown
                || TerminalReleaseEvidence.PrerequisiteSettled(run, a));
}
