using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

[Collection("TaskTakeoverIncident")]
public class CoordinatedHoeingEvidenceQueueTests
{
    private static BgiTaskCoordinator Create(Func<bool>? slotFree = null, TimeSpan? slotTimeout = null)
        => new(slotFree ?? (() => true), (_, _) => { }, NullLogger.Instance,
            slotPollInterval: TimeSpan.FromMilliseconds(5), slotWaitTimeout: slotTimeout);

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static InstanceIpcEnvelope CoordinatedRequest() => InstanceIpcEnvelope.Request("ext.task.start", new
    {
        coordinatedHoeing = "v1", hoeingBatchId = "finish-batch",
        hoeingParticipantUids = new[] { "123", "456" }, groupName = "finish-group"
    });

    [Fact]
    public async Task DispatcherScopePublishesUnderOriginalHandleWhileGroupStillRuns()
    {
        using var queue = Create();
        var nativeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = queue.Submit(new(1, "finish-group", null, 0, async (handle, _) =>
        {
            using var scope = InstanceRequestHandler.CreateCoordinatedHoeingScope(CoordinatedRequest(), handle,
                (id, evidence) => queue.RecordHoeingEvidence(id, evidence));
            Assert.Equal("finish-batch", scope.BatchId);
            Assert.Equal(new[] { "123", "456" }, scope.ExpectedParticipantUids);
            using (var native = scope.BeginNative())
            {
                native.SetMultiplayerMode(true);
                native.Complete();
            }
            nativeDone.SetResult();
            await releaseTail.Task;
            scope.Seal(TaskRunResult.Ran);
            return false;
        }));
        await nativeDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var running = queue.QueryItemStatus(submission.TaskHandle);
            Assert.Equal("running", running.Status);
            Assert.Equal(1, running.HoeingEvidence!.Completed);
            Assert.False(running.HoeingEvidence.Sealed);
            Assert.Null(running.HoeingEvidence.JobResult);
        }
        finally { releaseTail.TrySetResult(); }
        await Until(() => queue.QueryItemStatus(submission.TaskHandle).Status == "completed");
        var finished = queue.QueryItemStatus(submission.TaskHandle);
        Assert.Equal(1, finished.HoeingEvidence!.Completed);
        Assert.True(finished.HoeingEvidence.Sealed);
        Assert.Equal(TaskRunResult.Ran, finished.HoeingEvidence.JobResult);
    }

    [Fact]
    public async Task TailFailureKeepsBodyEvidenceWithoutChangingFailedQueueResult()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "finish-group", null, 0, (handle, _) =>
        {
            using var scope = InstanceRequestHandler.CreateCoordinatedHoeingScope(CoordinatedRequest(), handle,
                (id, evidence) => queue.RecordHoeingEvidence(id, evidence));
            using var native = scope.BeginNative();
            native.SetMultiplayerMode(true);
            native.Complete();
            scope.Seal(TaskRunResult.Failed);
            return Task.FromException<bool>(new InvalidOperationException("later_script_failed"));
        }));
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        var failed = queue.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("task_start_failed", failed.ErrorCode);
        Assert.Equal(1, failed.HoeingEvidence!.Completed);
        Assert.Equal(0, failed.HoeingEvidence.Failed);
        Assert.Equal(TaskRunResult.Failed, failed.HoeingEvidence.JobResult);

        var response = ExternalInterfaceQueryPlane.MapTaskQueueStatusResponse(
            InstanceIpcEnvelope.Request("ext.task.queueStatus", new { taskHandle = submitted.TaskHandle }), failed);
        Assert.Equal("failed", (string?)response.Data!["status"]);
        var evidenceJson = Assert.IsType<JObject>(response.Data["hoeingEvidence"]);
        Assert.Equal(1, (int)evidenceJson["completed"]!);
        Assert.True((bool)evidenceJson["sealed"]!);
        Assert.Equal("Failed", (string?)evidenceJson["jobResult"]);
        Assert.Null(evidenceJson["Completed"]);
    }

    [Fact]
    public async Task CacheRejectsOldRegressingAndPostSealUpdates()
    {
        using var queue = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = queue.Submit(new(1, "monotonic", null, 0, async (_, _) =>
        { entered.SetResult(); await release.Task; return false; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var complete = new CoordinatedHoeingEvidence(1, 1, 0, 0, 0, false, null, 2);
            Assert.True(queue.RecordHoeingEvidence(submitted.TaskHandle, complete));
            Assert.False(queue.RecordHoeingEvidence(submitted.TaskHandle,
                new(1, 0, 0, 0, 1, false, null, 1)));
            Assert.False(queue.RecordHoeingEvidence(submitted.TaskHandle,
                new(1, 0, 0, 0, 0, false, null, 3)));
            var sealedResult = complete with { Sealed = true, JobResult = TaskRunResult.Ran, Revision = 4 };
            Assert.True(queue.RecordHoeingEvidence(submitted.TaskHandle, sealedResult));
            Assert.False(queue.RecordHoeingEvidence(submitted.TaskHandle,
                sealedResult with { JobResult = TaskRunResult.Failed, Revision = 5 }));
            Assert.Equal(sealedResult, queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence);
            Assert.False(queue.RecordHoeingEvidence(Guid.NewGuid(), complete));
        }
        finally { release.TrySetResult(); }
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "completed");
    }

    [Fact]
    public async Task NextOrdinaryJobDoesNotInheritFinishedJobEvidence()
    {
        using var queue = Create();
        var first = queue.Submit(new(1, "first", null, 0, (handle, _) =>
        {
            using var scope = InstanceRequestHandler.CreateCoordinatedHoeingScope(CoordinatedRequest(), handle,
                (id, evidence) => queue.RecordHoeingEvidence(id, evidence));
            using var native = scope.BeginNative();
            native.SetMultiplayerMode(true);
            native.Complete();
            scope.Seal(TaskRunResult.Ran);
            return Task.FromResult(false);
        }));
        await Until(() => queue.QueryItemStatus(first.TaskHandle).Status == "completed");
        var next = queue.Submit(new(2, "ordinary", null, 0, (_, _) => Task.FromResult(false)));
        await Until(() => queue.QueryItemStatus(next.TaskHandle).Status == "completed");
        Assert.Null(queue.QueryItemStatus(next.TaskHandle).HoeingEvidence);
        Assert.Equal(1, queue.QueryItemStatus(first.TaskHandle).HoeingEvidence!.Completed);
        Assert.False(queue.RecordHoeingEvidence(first.TaskHandle,
            new(2, 0, 2, 0, 0, true, TaskRunResult.Failed, 100)));
        Assert.Equal("completed", queue.QueryItemStatus(next.TaskHandle).Status);
    }

    [Fact]
    public async Task EvidenceExpiresWithTerminalRetentionAndCannotBeResurrected()
    {
        using var queue = Create();
        Guid firstHandle = default;
        Guid lastHandle = default;
        for (var i = 0; i < 34; i++)
        {
            var submitted = queue.Submit(new(i + 1, "retention-" + i, null, 0, (handle, _) =>
            {
                queue.RecordHoeingEvidence(handle, new(1, 1, 0, 0, 0, true, TaskRunResult.Ran, 2));
                return Task.FromResult(false);
            }));
            if (i == 0) firstHandle = submitted.TaskHandle;
            lastHandle = submitted.TaskHandle;
            await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "completed");
        }
        var expired = queue.QueryItemStatus(firstHandle);
        Assert.Equal("not_found", expired.Status);
        Assert.Null(expired.HoeingEvidence);
        Assert.False(queue.RecordHoeingEvidence(firstHandle, new(1, 1, 0, 0, 0, true, TaskRunResult.Ran, 99)));
        Assert.Equal(1, queue.QueryItemStatus(lastHandle).HoeingEvidence!.Completed);
    }

    [Fact]
    public void OrdinaryRequestDoesNotActivateOrPublishCoordinatedEvidence()
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        using var scope = InstanceRequestHandler.CreateCoordinatedHoeingScope(
            InstanceIpcEnvelope.Request("ext.task.start", new { groupName = "ordinary" }), Guid.NewGuid(),
            (_, evidence) => observations.Add(evidence));
        Assert.False(CoordinatedHoeingScope.IsActive);
        scope.Seal(TaskRunResult.Ran);
        Assert.Empty(observations);
    }

    [Fact]
    public void HelloAdvertisesFinishEvidenceAsAnIndependentCapability()
    {
        var hello = ExternalInterfaceProtocol.BuildHelloData(Guid.NewGuid(), null, null);
        Assert.True((bool)hello["capabilities"]!["hoeing.finishEvidence.v1"]!);
        Assert.True((bool)hello["capabilities"]!["execution.targetedSuspend.v1"]!);
        Assert.True((bool)hello["capabilities"]!["hoeing.coordinatedStart.v1"]!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCancelAndClearQueueCarryExactZeroNativeProof(bool clearAll)
    {
        using var queue = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.Submit(new(1, "hold-queue", null, 0, async (_, _) =>
        { entered.SetResult(); await release.Task; return false; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var executed = false;
        var pending = queue.Submit(new(2, "never-entered", null, 0, (_, _) =>
        { executed = true; return Task.FromResult(false); }) { CoordinatedHoeing = true });
        try
        {
            if (clearAll) Assert.Equal(1, queue.ClearQueue());
            else Assert.Equal(BgiTaskCoordinator.CancelOutcome.CancelledQueued,
                queue.CancelByHandle(pending.TaskHandle, ownedOnly: true));
            var cancelled = queue.QueryItemStatus(pending.TaskHandle);
            Assert.Equal("queueCancelled", cancelled.Status);
            AssertNeverStarted(cancelled.HoeingEvidence, TaskRunResult.Cancelled);
            Assert.False(executed);
        }
        finally { release.TrySetResult(); }
        await Until(() => queue.QueryItemStatus(first.TaskHandle).Status == "completed");
        AssertNeverStarted(queue.QueryItemStatus(pending.TaskHandle).HoeingEvidence, TaskRunResult.Cancelled);
        Assert.False(executed);
    }

    [Fact]
    public async Task SlotTimeoutProvesNoNativeWasEverInvoked()
    {
        using var queue = Create(() => false, TimeSpan.FromMilliseconds(20));
        var executed = false;
        var submitted = queue.Submit(new(1, "never-admitted", null, 0, (_, _) =>
        { executed = true; return Task.FromResult(false); }) { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        var failed = queue.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("task_busy", failed.ErrorCode);
        AssertNeverStarted(failed.HoeingEvidence, TaskRunResult.Failed);
        Assert.False(executed);
    }

    [Fact]
    public async Task TypedNeverStartedProofMustMatchTheOriginalJobHandle()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "typed-never-started", null, 0, (handle, _) =>
            Task.FromException<bool>(new ExecutionNotStartedException(handle, "configuration_changed", "configuration_changed")))
            { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        AssertNeverStarted(queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence, TaskRunResult.Failed);

        var mismatched = queue.Submit(new(2, "wrong-proof", null, 0, (_, _) =>
            Task.FromException<bool>(new ExecutionNotStartedException(Guid.NewGuid(), "different job")))
            { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(mismatched.TaskHandle).Status == "failed");
        Assert.Null(queue.QueryItemStatus(mismatched.TaskHandle).HoeingEvidence);
    }

    [Fact]
    public async Task GenericExceptionCannotBeReclassifiedAsZeroNativeExecution()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "unknown-escape", null, 0, (_, _) =>
            Task.FromException<bool>(new InvalidOperationException("execution may already have happened")))
            { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Null(queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence);
    }

    [Fact]
    public async Task NeverStartedFallbackCannotOverwriteExistingScopeFacts()
    {
        using var queue = Create();
        var recorded = new CoordinatedHoeingEvidence(1, 1, 0, 0, 0, true, TaskRunResult.Failed, 4);
        var submitted = queue.Submit(new(1, "existing-scope", null, 0, (handle, _) =>
        {
            Assert.True(queue.RecordHoeingEvidence(handle, recorded));
            return Task.FromException<bool>(new ExecutionNotStartedException(handle, "late conflicting refusal"));
        }) { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Equal(recorded, queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence);
    }

    [Fact]
    public async Task OrdinarySlotTimeoutKeepsItsOriginalProtocolWithoutHoeingEvidence()
    {
        using var queue = Create(() => false, TimeSpan.FromMilliseconds(20));
        var submitted = queue.Submit(new(1, "ordinary-never-admitted", null, 0, (_, _) => Task.FromResult(false)));
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Null(queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence);
    }

    [Fact]
    public async Task EscapingCancellationWithConfirmedScopeResultRemainsCancellation()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "confirmed-cancellation", null, 0, (handle, _) =>
        {
            using var scope = InstanceRequestHandler.CreateCoordinatedHoeingScope(CoordinatedRequest(), handle,
                (id, evidence) => queue.RecordHoeingEvidence(id, evidence));
            using var native = scope.BeginNative();
            native.SetMultiplayerMode(true);
            native.Cancel();
            scope.Seal(TaskRunResult.Cancelled);
            return Task.FromException<bool>(new OperationCanceledException("user cancelled"));
        }) { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "completed");
        var cancelled = queue.QueryItemStatus(submitted.TaskHandle);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.ErrorCode);
        Assert.Equal(TaskRunResult.Cancelled, cancelled.HoeingEvidence!.JobResult);
        Assert.Equal(1, cancelled.HoeingEvidence.Cancelled);
        Assert.Equal(0, cancelled.HoeingEvidence.Active);
        Assert.Equal(0, cancelled.HoeingEvidence.Completed);
    }

    [Fact]
    public async Task EscapingCancellationWithoutScopeProofKeepsUnknownNativeEvidence()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "unproven-cancellation", null, 0, (_, _) =>
            Task.FromException<bool>(new OperationCanceledException("no scope proof")))
            { CoordinatedHoeing = true });
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Null(queue.QueryItemStatus(submitted.TaskHandle).HoeingEvidence);
    }

    private static void AssertNeverStarted(CoordinatedHoeingEvidence? evidence, TaskRunResult result)
    {
        Assert.NotNull(evidence);
        Assert.Equal(0, evidence.Started);
        Assert.Equal(0, evidence.Completed);
        Assert.Equal(0, evidence.Failed);
        Assert.Equal(0, evidence.Cancelled);
        Assert.Equal(0, evidence.Active);
        Assert.True(evidence.Sealed);
        Assert.Equal(result, evidence.JobResult);
    }
}
