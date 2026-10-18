using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedHoeingEvidenceTests
{
    [Theory]
    [InlineData(TaskRunResult.Cancelled)]
    [InlineData(TaskRunResult.Preempted)]
    [InlineData(TaskRunResult.Ran)]
    public void FrozenNativePlanKeepsTheActualWholeJobOutcome(TaskRunResult result)
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        using var scope = new CoordinatedHoeingScope(true, observer: observations.Add);
        Assert.True(scope.TryRegisterNativePlan(1));
        using (var native = scope.BeginNative())
        {
            native.SetMultiplayerMode(true);
            native.Complete();
        }
        var final = scope.Seal(result);
        Assert.Equal(1, final.PlannedNativeCount);
        Assert.Equal(final.PlannedNativeCount, final.Started);
        Assert.Equal(final.Started, final.Completed);
        Assert.Equal(result, final.JobResult);
        Assert.True(final.Sealed);
        Assert.True(observations.Zip(observations.Skip(1), (a, b) => a.Revision < b.Revision).All(x => x));

        var response = ExternalInterfaceQueryPlane.MapTaskQueueStatusResponse(
            InstanceIpcEnvelope.Request("ext.task.queueStatus"), new("completed", result != TaskRunResult.Ran,
                null, null, final));
        Assert.Equal(1, (int?)response.Data!["hoeingEvidence"]!["plannedNativeCount"]);
        Assert.Equal(result.ToString(), (string?)response.Data["hoeingEvidence"]!["jobResult"]);
    }

    [Fact]
    public void LaterNativeAndUnexpectedExtraNativeDoNotMatchACompletedPlan()
    {
        using var scope = new CoordinatedHoeingScope(true);
        Assert.True(scope.TryRegisterNativePlan(2));
        using (var first = scope.BeginNative())
        {
            first.SetMultiplayerMode(true);
            first.Complete();
        }
        Assert.NotEqual(scope.Snapshot.PlannedNativeCount, scope.Snapshot.Completed);
        using (var second = scope.BeginNative())
        {
            second.SetMultiplayerMode(true);
            second.Complete();
        }
        using (var unexpected = scope.BeginNative())
        {
            unexpected.SetMultiplayerMode(true);
            unexpected.Complete();
        }
        var final = scope.Seal(TaskRunResult.Cancelled);
        Assert.Equal(2, final.PlannedNativeCount);
        Assert.Equal(3, final.Completed);
        Assert.NotEqual(final.PlannedNativeCount, final.Started);
    }

    [Fact]
    public void PlanCannotBeReplacedRegisteredLateOrRestoredAfterDynamicWork()
    {
        using var scope = new CoordinatedHoeingScope(true);
        Assert.True(scope.TryRegisterNativePlan(2));
        Assert.False(scope.TryRegisterNativePlan(1));
        Assert.Equal(2, scope.Snapshot.PlannedNativeCount);
        scope.InvalidateNativePlan();
        Assert.Null(scope.Snapshot.PlannedNativeCount);
        Assert.False(scope.TryRegisterNativePlan(1));
        var sealedResult = scope.Seal(TaskRunResult.Cancelled);
        scope.InvalidateNativePlan();
        Assert.False(scope.TryRegisterNativePlan(3));
        Assert.Equal(sealedResult, scope.Snapshot);

        using var late = new CoordinatedHoeingScope(true);
        using var active = late.BeginNative();
        Assert.False(late.TryRegisterNativePlan(1));
        active.SetMultiplayerMode(true);
        active.Complete();
        Assert.False(late.TryRegisterNativePlan(1));
        Assert.Null(late.Snapshot.PlannedNativeCount);
    }

    [Fact]
    public void DirectGroupPlanCountsEnabledRemainingNativeRepeatsAndAllowsAnOrdinaryJsTail()
    {
        var enabled = ScriptGroupProject.BuildSoloTaskProject("锄地一条龙（联机）");
        enabled.RunNum = 2;
        var legacy = ScriptGroupProject.BuildSoloTaskProject("锄地一条龙");
        var disabled = ScriptGroupProject.BuildSoloTaskProject("锄地一条龙（联机）");
        disabled.Status = "Disabled";
        var beforeResumeCursor = ScriptGroupProject.BuildSoloTaskProject("锄地一条龙（联机）");
        beforeResumeCursor.SkipFlag = true;
        var ordinaryJs = new ScriptGroupProject("老王妙妙屋", "ordinary-js", "Javascript");
        ScriptGroupProject[] list = [beforeResumeCursor, enabled, disabled, legacy, ordinaryJs];
        Assert.Equal(3, ScriptService.CountPlannedCoordinatedNatives(list, rootGroup: true));
        Assert.Equal(0, ScriptService.CountPlannedCoordinatedNatives([ordinaryJs], rootGroup: true));
        Assert.Null(ScriptService.CountPlannedCoordinatedNatives(list, rootGroup: false));
    }

    [Fact]
    public void PriorityGroupsAndAnUnrepresentablePlanStayUnknown()
    {
        var project = ScriptGroupProject.BuildSoloTaskProject("锄地一条龙（联机）");
        var group = new ScriptGroup();
        group.Config.PathingConfig.Enabled = true;
        group.Config.PathingConfig.PreExecutionPriorityConfig.Enabled = true;
        project.GroupInfo = group;
        Assert.Null(ScriptService.CountPlannedCoordinatedNatives([project], rootGroup: true));
        group.Config.PathingConfig.Enabled = false;
        Assert.Equal(1, ScriptService.CountPlannedCoordinatedNatives([project], rootGroup: true));
        project.RunNum = -1;
        Assert.Null(ScriptService.CountPlannedCoordinatedNatives([project], rootGroup: true));
        project.RunNum = int.MaxValue;
        Assert.Null(ScriptService.CountPlannedCoordinatedNatives([project, project], rootGroup: true));
    }

    [Fact]
    public void CompletedNativeDoesNotSealGroupOrHideTheNextNative()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using (var first = scope.BeginNative())
        {
            first.SetMultiplayerMode(true);
            first.Complete();
        }
        var firstResult = scope.Snapshot;
        Assert.Equal(1, firstResult.Completed);
        Assert.Equal(0, firstResult.Active);
        Assert.False(firstResult.Sealed);
        Assert.Null(firstResult.JobResult);

        using (var second = scope.BeginNative())
        {
            Assert.Equal(2, scope.Snapshot.Started);
            Assert.Equal(1, scope.Snapshot.Active);
            second.Fail(); // The second invocation can fail before its resources/configuration load.
        }
        var final = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, final.Completed);
        Assert.Equal(1, final.Failed);
        Assert.Equal(0, final.Active);
        Assert.Equal(TaskRunResult.Failed, final.JobResult);
        Assert.Equal(1, firstResult.Started); // Earlier snapshots are immutable.
    }

    [Fact]
    public void FailureInLaterScriptPreservesBodySuccessAndRealGroupFailure()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using (var native = scope.BeginNative())
        {
            native.SetMultiplayerMode(true);
            native.Complete();
        }
        var result = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, result.Completed);
        Assert.Equal(0, result.Failed); // This failure belongs to the later script, not the native.
        Assert.True(result.Sealed);
        Assert.Equal(TaskRunResult.Failed, result.JobResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void SoloOrUnknownModeCannotProveMultiplayerCompletion(bool? mode)
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        if (mode.HasValue) native.SetMultiplayerMode(mode.Value);
        native.Complete();
        Assert.Equal(1, scope.Snapshot.Started);
        Assert.Equal(0, scope.Snapshot.Active);
        Assert.Equal(0, scope.Snapshot.Completed);
        Assert.Equal(0, scope.Snapshot.Failed);
    }

    [Fact]
    public void ConfigurationOrResourceFailureBeforeModeIsKnownIsRecorded()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        native.Fail();
        var result = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, result.Started);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Active);
    }

    [Fact]
    public void UserCancellationCannotBecomeACompletedNative()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Cancel();
        native.Complete();
        native.Fail();
        var result = scope.Seal(TaskRunResult.Cancelled);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Active);
    }

    [Fact]
    public void AbandonedNativeIsNotSuccessAndTerminalCallbacksAreIdempotent()
    {
        using var scope = new CoordinatedHoeingScope(true);
        var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Dispose();
        native.Dispose();
        native.Complete();
        Assert.Equal(1, scope.Snapshot.Failed);
        Assert.Equal(0, scope.Snapshot.Completed);
        Assert.Equal(0, scope.Snapshot.Active);
    }

    [Fact]
    public void SealedJobIgnoresLateNativeAndConflictingFinalResults()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var pending = scope.BeginNative();
        pending.SetMultiplayerMode(true);
        var sealedResult = scope.Seal(TaskRunResult.Cancelled);
        pending.Complete();
        pending.Fail();
        CoordinatedHoeingScope.MarkIncomplete();
        using var late = scope.BeginNative();
        late.SetMultiplayerMode(true);
        late.Complete();
        Assert.Equal(sealedResult, scope.Seal(TaskRunResult.Ran));
        Assert.Equal(sealedResult, scope.Snapshot);
        Assert.False(scope.Incomplete);
    }

    [Fact]
    public void DisposalWithoutJobResultPreservesUnknownRatherThanAssumingSuccess()
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        var scope = new CoordinatedHoeingScope(true, observer: observations.Add);
        using var pending = scope.BeginNative();
        scope.Dispose();
        pending.SetMultiplayerMode(true);
        pending.Complete();
        var result = observations[^1];
        Assert.True(result.Sealed);
        Assert.Null(result.JobResult);
        Assert.Equal(0, result.Completed);
        Assert.Equal(result, scope.Snapshot);
    }

    [Fact]
    public async Task NestedAsyncInvocationsUseOneJobButDoNotLeakIntoTheNextJob()
    {
        using (var scope = new CoordinatedHoeingScope(true))
        {
            await Task.Run(async () =>
            {
                await Task.Yield();
                Assert.Same(scope, CoordinatedHoeingScope.Current);
                using var native = CoordinatedHoeingScope.Current!.BeginNative();
                native.SetMultiplayerMode(true);
                native.Complete();
            });
            using (var repeated = scope.BeginNative())
            {
                repeated.SetMultiplayerMode(true);
                repeated.Complete();
            }
            Assert.Equal(2, scope.Seal(TaskRunResult.Ran).Completed);
        }
        Assert.Null(CoordinatedHoeingScope.Current);
        using var next = new CoordinatedHoeingScope(true);
        Assert.Equal(0, next.Snapshot.Started);
        Assert.Equal(0, next.Snapshot.Completed);
        Assert.False(next.Snapshot.Sealed);
    }

    [Fact]
    public async Task ConcurrentObservationsHaveIncreasingRevisionsAndExactCounts()
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        using var scope = new CoordinatedHoeingScope(true, observer: observations.Add);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            using var native = scope.BeginNative();
            native.SetMultiplayerMode(i % 3 != 0);
            if (i % 3 == 1) native.Cancel();
            else native.Complete();
        })));
        var result = scope.Seal(TaskRunResult.Cancelled);
        Assert.Equal(24, result.Started);
        Assert.Equal(8, result.Completed);
        Assert.Equal(8, result.Cancelled);
        Assert.Equal(0, result.Active);
        Assert.Equal(result, observations[^1]);
        Assert.True(observations.Zip(observations.Skip(1), (a, b) => a.Revision < b.Revision).All(x => x));
    }

    [Fact]
    public void BrokenObserverCannotChangeExecutionOutcome()
    {
        using var scope = new CoordinatedHoeingScope(true,
            observer: _ => throw new IOException("observer unavailable"));
        using var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Complete();
        var result = scope.Seal(TaskRunResult.Ran);
        Assert.Equal(1, result.Completed);
        Assert.Equal(TaskRunResult.Ran, result.JobResult);
    }
}
