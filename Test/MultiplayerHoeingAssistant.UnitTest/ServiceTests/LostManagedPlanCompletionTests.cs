using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class LostManagedPlanCompletionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingArrangementCanReleaseOnlyItsExitedExecutorAndPreservesUnknownFailure(bool living, bool stopped)
    {
        using var self = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = living ? self.Id : int.MaxValue,
            StartTicksUtc = living ? self.StartTime.ToUniversalTime().Ticks : 1 };
        var batch = new OnlineHoeingBatch(new("missing-original-plan", 7, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true, ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] },
        }, "self", new() { Policy = TaskConflictPolicy.Resume })
        { ExecutionEpoch = epoch, ManagedRunId = "lost-run", PolicyStartAttempted = true };
        batch.MarkLocalSuccess(epoch);
        batch.EnsureRecoveryStopSignal();
        var originalJob = Guid.NewGuid().ToString("N");
        batch.RecordPolicyExecution(new(originalJob, epoch, true));
        try
        {
            if (stopped) batch.RecoveryStopSignal!.Stop();
            var rejected = batch.RejectUnrecoverableManagedPlanAfterExit("原身份及内容均丢失");
            Assert.Equal(!living && !stopped, rejected);
            if (!rejected)
            {
                Assert.Equal(originalJob, batch.PolicyExecution!.JobId);
                Assert.Empty(batch.FailedPolicyAttempts);
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await MainViewModel.RunCompletionContinuationAsync(batch, new()
            {
                RequireAuthority = () => timeout.Token.ThrowIfCancellationRequested(),
                Prepare = _ => throw new InvalidOperationException("no arrangement may be invented"),
                Start = _ => throw new InvalidOperationException("no replacement task may be sent"),
                Query = _ => throw new InvalidOperationException("exited executor has no live result duty"),
                Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            }, timeout.Token);
            Assert.True(batch.LocalExecutionReleased);
            Assert.True(batch.PolicyRejectedBeforeStart);
            Assert.False(batch.IsCancellationRequested);
            Assert.Null(batch.PolicyTerminalResult);
            var failed = Assert.Single(batch.FailedPolicyAttempts);
            Assert.Equal(originalJob, failed.TaskHandle);
            Assert.Equal("original_plan_lost_result_unknown", failed.ErrorCode);
            Assert.Equal("failed", failed.Status);
        }
        finally { batch.RecoveryStopSignal?.Dispose(); }
    }
}
