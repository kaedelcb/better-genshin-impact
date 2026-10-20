using System.Collections;
using System.Reflection;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

[Collection("TaskTakeoverIncident")]
public class StateContinuationTests
{
    private const string FrozenFingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < limit) await Task.Delay(5);
        Assert.True(condition());
    }

    private static BgiTaskCoordinator NewCoordinator(Func<bool>? free = null,
        Action<Guid, BgiTaskCoordinator.TaskSubmission>? register = null)
        => new(free ?? (() => true), (_, _) => { }, NullLogger.Instance,
            TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30), register);

    private static BgiTaskCoordinator.TaskSubmission Submission(string key,
        Func<Guid, CancellationToken, Task<bool>> execute, string fingerprint = FrozenFingerprint)
        => new(0, "state-continuation", null, 0, execute)
        {
            IdempotencyKey = key, PayloadFingerprint = fingerprint,
            RequestOperation = ExternalInterfaceOperations.TaskStart, RetainUntilAcknowledged = true,
        };

    [Fact]
    public async Task PriorityTakeover_FullOrdinaryQueue_AllowsSuccessivePriorityItems()
    {
        PreemptionGate.Disarm();
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        using var coordinator = NewCoordinator();
        var ordinaryStarts = 0;
        try
        {
            for (var i = 0; i < BgiTaskCoordinator.QueueCapacity; i++)
                Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued,
                    coordinator.Submit(Submission("waiting/" + i, (_, _) =>
                    { Interlocked.Increment(ref ordinaryStarts); return Task.FromResult(false); })).Status);
            var first = Submission("priority/1", (_, _) => Task.FromResult(false)) with
                { CoordinatedHoeing = true, TakeoverTicket = ticket };
            BgiTaskCoordinator.SubmitResult accepted = default;
            await Until(() => (accepted = coordinator.Submit(first)).Status is
                BgiTaskCoordinator.SubmitStatus.Queued or BgiTaskCoordinator.SubmitStatus.Adopted);
            await Until(() => coordinator.QueryItemStatus(accepted.TaskHandle).ExecutionExitConfirmed);
            var second = coordinator.Submit(Submission("priority/2", (_, _) => Task.FromResult(false)) with
                { CoordinatedHoeing = true, TakeoverTicket = ticket });
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, second.Status);
            await Until(() => coordinator.QueryItemStatus(second.TaskHandle).ExecutionExitConfirmed);
            Assert.Equal(0, ordinaryStarts);
            PreemptionGate.Release(ticket);
            await Until(() => Volatile.Read(ref ordinaryStarts) == BgiTaskCoordinator.QueueCapacity);
        }
        finally { PreemptionGate.Release(ticket); }
    }

    [Fact]
    public async Task PriorityTakeover_PassesQueuedOrdinaryWork_WithoutRunningTwoExecutors()
    {
        PreemptionGate.Disarm();
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        Assert.True(PreemptionGate.RetainUntilReleased(ticket));
        using var coordinator = NewCoordinator();
        var order = new List<string>();
        var active = 0;
        var maximum = 0;
        Task<bool> Execute(string name)
        {
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            lock (order) order.Add(name);
            Interlocked.Decrement(ref active);
            return Task.FromResult(false);
        }
        try
        {
            var ordinary = coordinator.Submit(Submission("ordinary-paused", (_, _) => Execute("ordinary")));
            var priority = coordinator.Submit(Submission("priority-hoeing", (_, _) => Execute("hoeing")) with
                { CoordinatedHoeing = true, TakeoverTicket = ticket });
            await Until(() => coordinator.QueryItemStatus(priority.TaskHandle).ExecutionExitConfirmed);
            lock (order) Assert.Equal(new[] { "hoeing" }, order);
            Assert.Equal("pending", coordinator.QueryItemStatus(ordinary.TaskHandle).Status);
            PreemptionGate.Release(ticket);
            await Until(() => coordinator.QueryItemStatus(ordinary.TaskHandle).ExecutionExitConfirmed);
            lock (order) Assert.Equal(new[] { "hoeing", "ordinary" }, order);
            Assert.Equal(1, maximum);
        }
        finally { PreemptionGate.Release(ticket); }
    }

    [Fact]
    public void PrioritySuspend_RecoverySaveFailureStillCancelsOnlyCapturedRoot()
    {
        PreemptionGate.Disarm();
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "priority-original", JobSource.Ui));
        var snapshot = ExecutionScope.GetActiveSnapshot();
        Assert.NotNull(snapshot);
        var ticket = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(ExecutionScope.TrySuspendTarget(ticket, snapshot!.ExecutionInstanceId,
                snapshot.StateRevision, ExecutionScope.StopVersionNow, TimeSpan.Zero,
                (_, _) => throw new System.IO.IOException("simulated recovery write failure"),
                out var target, out var refusal, continueOnRecoveryFailure: true));
            Assert.Null(refusal);
            Assert.NotNull(target);
            Assert.Equal("simulated recovery write failure", target!.RecoveryWarning);
            Assert.True(root.Token.IsCancellationRequested);
            Assert.False(target.ObserveQuiescence()); // Cancellation is not proof that the root exited.
        }
        finally { PreemptionGate.Release(ticket); }
    }

    [Fact]
    public void MissingRetainedRequest_EchoesOriginalQueryKey_WithoutInventingAcceptance()
    {
        using var coordinator = NewCoordinator();
        var result = coordinator.QueryItemStatus("request-not-delivered");
        Assert.Equal("not_found", result.Status);
        Assert.Equal("request-not-delivered", result.IdempotencyKey);
        Assert.Equal(Guid.Empty, result.TaskHandle);
        Assert.False(result.ExecutionExitConfirmed);
        Assert.Equal(0, coordinator.QueueDepth);
    }

    [Fact]
    public async Task FullReceiptWindow_DoesNotBlockExactResultAcknowledgement()
    {
        using var coordinator = NewCoordinator();
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var key = "ack-while-full-" + Guid.NewGuid().ToString("N");
        var actual = coordinator.Submit(Submission(key, (_, _) => Task.FromResult(false)));
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        var fill = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { retainUntilAcknowledged = true });
        for (var index = 0; index < JobRegistry.RequestReceiptCapacity; index++)
        {
            var saved = await registry.ExecuteRequestOnceAsync("fill/" + index, FrozenFingerprint, fill,
                () => Task.FromResult(InstanceIpcEnvelope.Response(fill, new { status = "queued" })), CancellationToken.None);
            Assert.True(saved.Success);
        }
        var ack = InstanceIpcEnvelope.Request("ext.task.acknowledge", new
        {
            submissionKey = key, taskHandle = actual.TaskHandle.ToString("N")
        });
        Task<InstanceIpcEnvelope> Confirm() => Task.FromResult(coordinator.Acknowledge(key, actual.TaskHandle)
            ? InstanceIpcEnvelope.Response(ack, new { status = "acknowledged" })
            : InstanceIpcEnvelope.Failure(ack, "not_terminal", "not yet terminal"));
        Assert.True((await registry.ExecuteRequestOnceAsync("ack/" + key, FrozenFingerprint, ack, Confirm, CancellationToken.None)).Success);
        Assert.True((await registry.ExecuteRequestOnceAsync("ack/" + key, FrozenFingerprint, ack, Confirm, CancellationToken.None)).Success);
        Assert.Equal(actual.TaskHandle, coordinator.Submit(Submission(key, (_, _) => throw new InvalidOperationException("must not restart"))).TaskHandle);
        var nextKey = key + "/next";
        var next = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = nextKey, retainUntilAcknowledged = true, groupName = "state-continuation" });
        var nextFingerprint = ExecutionRequestContract.Fingerprint(next);
        var executions = 0;
        Task<InstanceIpcEnvelope> DispatchNext() => Task.FromResult(ExternalInterfaceCommandPlane.MapTaskStartQueueResult(next,
            coordinator.Submit(Submission(nextKey, (_, _) => { Interlocked.Increment(ref executions); return Task.FromResult(false); }, nextFingerprint)), 0));
        BgiTaskCoordinator.SubmitResult? LookupNext(string originalKey, string originalFingerprint)
            => coordinator.TryReplayRetainedStart(originalKey, originalFingerprint, out var found) ? found : null;
        var first = await registry.ExecuteRequestOnceAsync("key:" + nextKey, nextFingerprint, next, DispatchNext, CancellationToken.None, LookupNext);
        var repeated = await registry.ExecuteRequestOnceAsync("key:" + nextKey, nextFingerprint, next, DispatchNext, CancellationToken.None, LookupNext);
        Assert.True(first.Success);
        Assert.Equal(first.Data?["taskHandle"]?.ToString(), repeated.Data?["taskHandle"]?.ToString());
        await Until(() => coordinator.QueryItemStatus(nextKey).ExecutionExitConfirmed);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task ConcurrentAndLateSameKey_AdoptOneRealQueueItem_EvenAfterAcknowledgement()
    {
        using var coordinator = NewCoordinator();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var key = Guid.NewGuid().ToString("N");
        var submission = Submission(key, async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return false;
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(_ => Task.Run(() => coordinator.Submit(submission))));
        var handle = results[0].TaskHandle;
        Assert.All(results, result => Assert.Equal(handle, result.TaskHandle));
        await Until(() => calls == 1);
        Assert.False(coordinator.Acknowledge(key, handle));
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict,
            coordinator.Submit(submission with { PayloadFingerprint = "changed" }).Status);
        release.SetResult(true);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.True(coordinator.Acknowledge(key, handle));
        Assert.True(coordinator.Acknowledge(key, handle));
        Assert.Equal(handle, coordinator.Submit(submission).TaskHandle);
        Assert.Equal("completed", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RegistrationFailure_DoesNotBlockQueue_RepairUsesOriginalHandleAndMetadata()
    {
        using var coordinator = NewCoordinator(register: (_, _) => throw new IOException("registry fault"));
        var key = Guid.NewGuid().ToString("N");
        var identity = new JobExecutionIdentity(Guid.NewGuid(), "original-node", 3, Occurrence: 2, Attempt: 1);
        Guid executed = default;
        var result = coordinator.Submit(Submission(key, (handle, _) =>
        {
            executed = handle;
            Assert.Null(JobRegistry.Instance.Query(handle));
            // The same repair performed by the native execution funnel: explicit original jobId.
            var repaired = JobRegistry.Instance.Submit(JobKind.Group, "state-continuation", JobSource.Ext,
                idempotencyKey: key, jobId: handle, identity: identity,
                requestFingerprint: FrozenFingerprint, requestOperation: ExternalInterfaceOperations.TaskStart,
                retainUntilAcknowledged: true).Job;
            Assert.Equal(handle, repaired.JobId);
            return Task.FromResult(false);
        }) with { Identity = identity });
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Equal(result.TaskHandle, executed);
        Assert.Equal("completed", coordinator.QueryItemStatus(key).Status);
        var job = JobRegistry.Instance.Query(executed)!;
        Assert.Equal(key, job.IdempotencyKey);
        Assert.Equal(FrozenFingerprint, job.RequestFingerprint);
        Assert.Equal(ExternalInterfaceOperations.TaskStart, job.RequestOperation);
        Assert.Equal(identity.WorkflowRunId, job.WorkflowRunId);
        Assert.Equal(3, job.Iteration);
    }

    [Fact]
    public async Task FiveHourReceiptAndOrdinaryHistoryEviction_DoNotForgetUnacknowledgedAction()
    {
        using var coordinator = NewCoordinator();
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var key = Guid.NewGuid().ToString("N");
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = key, retainUntilAcknowledged = true, groupName = "state-continuation" });
        var fingerprint = ExecutionRequestContract.Fingerprint(request);
        var calls = 0;
        var dispatches = 0;
        var submission = Submission(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); }, fingerprint);
        Task<InstanceIpcEnvelope> Dispatch()
        {
            Interlocked.Increment(ref dispatches);
            return Task.FromResult(ExternalInterfaceCommandPlane.MapTaskStartQueueResult(request, coordinator.Submit(submission), 0));
        }
        BgiTaskCoordinator.SubmitResult? Lookup(string originalKey, string originalFingerprint)
            => coordinator.TryReplayRetainedStart(originalKey, originalFingerprint, out var result) ? result : null;
        await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        var original = coordinator.QueryItemStatus(key).TaskHandle;
        var receipts = (IDictionary)typeof(JobRegistry).GetField("_requestReceipts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!;
        var receipt = receipts["key:" + key]!;
        receipt.GetType().GetProperty("CompletedAtUtc")!.SetValue(receipt, DateTime.UtcNow.AddHours(-5));
        for (var i = 0; i < 100; i++)
        {
            var other = coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(0, "other-" + i, null, 0, (_, _) => Task.FromResult(false)));
            await Until(() => coordinator.QueryItemStatus(other.TaskHandle).Status == "completed");
        }
        var replay = await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        Assert.Equal(original.ToString("N"), replay.Data?["taskHandle"]?.ToString());
        Assert.Equal("completed", coordinator.QueryItemStatus(key).Status);
        Assert.NotNull(JobRegistry.Instance.Query(original));
        Assert.Equal(1, calls);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public async Task ReplyConstructionFailure_IsRepairedFromAcceptedQueueItem_WithoutSecondDispatch()
    {
        using var coordinator = NewCoordinator();
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var key = Guid.NewGuid().ToString("N");
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = key, retainUntilAcknowledged = true, groupName = "state-continuation" });
        var fingerprint = ExecutionRequestContract.Fingerprint(request);
        var dispatches = 0;
        var calls = 0;
        Task<InstanceIpcEnvelope> Dispatch()
        {
            Interlocked.Increment(ref dispatches);
            coordinator.Submit(Submission(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); }, fingerprint));
            throw new IOException("response lost after real acceptance");
        }
        BgiTaskCoordinator.SubmitResult? Lookup(string originalKey, string originalFingerprint)
            => coordinator.TryReplayRetainedStart(originalKey, originalFingerprint, out var result) ? result : null;
        var response = await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        Assert.True(response.Success);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        var replay = await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        Assert.Equal(response.Data?["taskHandle"]?.ToString(), replay.Data?["taskHandle"]?.ToString());
        Assert.Equal(1, dispatches);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReplyFailureBeforeQueueAcceptance_AllowsSameRequestToContinue()
    {
        using var coordinator = NewCoordinator();
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var key = Guid.NewGuid().ToString("N");
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = key, retainUntilAcknowledged = true, groupName = "state-continuation" });
        var fingerprint = ExecutionRequestContract.Fingerprint(request);
        var attempts = 0;
        var calls = 0;
        Task<InstanceIpcEnvelope> Dispatch()
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("before actual acceptance");
            return Task.FromResult(ExternalInterfaceCommandPlane.MapTaskStartQueueResult(request,
                coordinator.Submit(Submission(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); }, fingerprint)), 0));
        }
        BgiTaskCoordinator.SubmitResult? Lookup(string originalKey, string originalFingerprint)
            => coordinator.TryReplayRetainedStart(originalKey, originalFingerprint, out var result) ? result : null;
        var interrupted = await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        Assert.Equal("result_unknown", interrupted.ErrorCode);
        Assert.Equal("not_found", coordinator.QueryItemStatus(key).Status);
        var resumed = await registry.ExecuteRequestOnceAsync("key:" + key, fingerprint, request, Dispatch, CancellationToken.None, Lookup);
        Assert.True(resumed.Success);
        await Until(() => calls == 1);
        Assert.Equal(2, attempts);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OccupiedSlot_DoesNotExpireAcceptedAction_AndExplicitCancelPreventsLateStart()
    {
        var free = false;
        using var coordinator = NewCoordinator(() => Volatile.Read(ref free));
        var calls = 0;
        var key = Guid.NewGuid().ToString("N");
        var submission = Submission(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); });
        var accepted = coordinator.Submit(submission);
        await Task.Delay(140); // > four original slot-wait budgets.
        Assert.Equal("pending", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(0, calls);
        Assert.Equal(BgiTaskCoordinator.CancelOutcome.CancelledQueued, coordinator.CancelByHandle(accepted.TaskHandle, ownedOnly: true));
        Volatile.Write(ref free, true);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Equal("queueCancelled", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(accepted.TaskHandle, coordinator.Submit(submission).TaskHandle);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SlotBecomesFreeAfterOldBudget_ExecutesOriginalItemOnce()
    {
        var free = false;
        using var coordinator = NewCoordinator(() => Volatile.Read(ref free));
        var calls = 0;
        var key = Guid.NewGuid().ToString("N");
        var submission = Submission(key, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(false); });
        var original = coordinator.Submit(submission);
        await Task.Delay(140);
        Assert.Equal(original.TaskHandle, coordinator.Submit(submission).TaskHandle);
        Volatile.Write(ref free, true);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Equal("completed", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RetainedTakeoverTicket_UsesActualRelease_NotNinetySecondExpiry()
    {
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        try
        {
            Assert.True(PreemptionGate.RetainUntilReleased(ticket));
            typeof(PreemptionGate).GetField("_armedAtUtc", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, DateTime.UtcNow.AddHours(-5));
            Assert.True(PreemptionGate.Authorize(ticket));
            Assert.True(PreemptionGate.Renew(ticket));
            Assert.True(PreemptionGate.Release(ticket));
            Assert.False(PreemptionGate.Authorize(ticket));
            Assert.Throws<InvalidOperationException>(() => PreemptionGate.Arm(ticket));
        }
        finally { PreemptionGate.Disarm(); }
    }
    [Fact]
    public async Task BeforeRootBusyRace_RetainsOriginalQueueItem_ThenStartsOnce()
    {
        using var coordinator = NewCoordinator();
        var key = Guid.NewGuid().ToString("N");
        var attempts = 0;
        var effects = 0;
        var firstHandle = Guid.Empty;
        var item = Submission(key, (handle, _) =>
        {
            if (firstHandle == Guid.Empty) firstHandle = handle;
            Assert.Equal(firstHandle, handle);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new ExecutionNotStartedException(handle, "task_busy: another root won the admission race");
            Interlocked.Increment(ref effects);
            return Task.FromResult(false);
        });
        var accepted = coordinator.Submit(item);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Equal(accepted.TaskHandle, firstHandle);
        Assert.Equal("completed", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(2, attempts);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task CancelAfterDispatchBeforeRoot_CleansOriginalResumeContextOnce()
    {
        using var coordinator = NewCoordinator();
        var key = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = 0;
        var effects = 0;
        var item = Submission(key, async (handle, token) =>
        {
            entered.SetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { throw new ExecutionNotStartedException(handle, "stopped before admission"); }
            Interlocked.Increment(ref effects);
            return false;
        }) with { OnCancelledBeforeStart = _ => Interlocked.Increment(ref cleanup) };
        var accepted = coordinator.Submit(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BgiTaskCoordinator.CancelOutcome.StopRequestedRunning,
            coordinator.CancelByHandle(accepted.TaskHandle, ownedOnly: true));
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Equal("queueCancelled", coordinator.QueryItemStatus(key).Status);
        Assert.Equal(1, cleanup);
        Assert.Equal(0, effects);
        Assert.Equal(accepted.TaskHandle, coordinator.Submit(item).TaskHandle);
    }

    [Fact]
    public async Task RegistryMissing_StatusProjectionUsesOriginalQueueIdentityAndExit()
    {
        using var coordinator = NewCoordinator(register: (_, _) => throw new IOException("registry unavailable"));
        var key = Guid.NewGuid().ToString("N");
        var identity = new JobExecutionIdentity(Guid.NewGuid(), "resume-original", 5, ConfigRevision: "revision", Occurrence: 2, Attempt: 4);
        var result = coordinator.Submit(Submission(key, (_, _) => Task.FromResult(false)) with
        { Identity = identity, RegistrySource = JobSource.Resume });
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        Assert.Null(JobRegistry.Instance.Query(result.TaskHandle));
        var response = ExternalInterfaceQueryPlane.MapRetainedJobStatusResponse(
            InstanceIpcEnvelope.Request(ExternalInterfaceOperations.JobStatus, new { jobId = result.TaskHandle }),
            coordinator.QueryRetainedJob(result.TaskHandle)!);
        Assert.Equal("succeeded", response.Data?["status"]?.ToString());
        Assert.Equal(result.TaskHandle.ToString("N"), response.Data?["job"]?["jobId"]?.ToString());
        Assert.Equal(key, response.Data?["job"]?["idempotencyKey"]?.ToString());
        Assert.Equal(identity.WorkflowRunId.ToString("N"), response.Data?["job"]?["workflowRunId"]?.ToString());
        Assert.True(response.Data?["job"]?["exitConfirmed"]?.Value<bool>());
        Assert.Equal("Resume", response.Data?["job"]?["source"]?.ToString());
    }

    [Fact]
    public void StateContinuationStart_RequiresFrozenStopFence_AndOldFenceRejectsLateStart()
    {
        var key = Guid.NewGuid().ToString("N");
        var missing = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = key, retainUntilAcknowledged = true, groupName = "g" });
        Assert.NotNull(ExternalInterfaceCommandPlane.ValidateStateContinuationRequest(missing));
        var previous = ExecutionScope.StopVersionNow;
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart, new
        {
            idempotencyKey = key, retainUntilAcknowledged = true, groupName = "g", expectedStopVersion = previous,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc },
        });
        Assert.Null(ExternalInterfaceCommandPlane.ValidateStateContinuationRequest(request));
        ExecutionScope.StopActive(manual: true);
        Assert.Equal("manual_stop_fence", ExecutionRequestContract.Validate(request, allowExpiredReplay: true)?.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedSoloResumeUsesOneHandleAndCanBeObservedOrExactlyStopped(bool stop)
    {
        PreemptionGate.Disarm();
        using var coordinator = NewCoordinator();
        var key = "resume/solo-" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var item = Submission(key, (handle, token) =>
            BetterGenshinImpact.Service.Instance.MessageHandlers.InstanceRequestHandler.ExecuteRetainedSoloResumeAsync(
                new JobDescriptor(JobKind.Solo, "原独立任务", JobSource.Resume, IdempotencyKey: key,
                    JobId: handle, RetainUntilAcknowledged: true, ExpectedStopVersion: ExecutionScope.StopVersionNow),
                async descriptor =>
                {
                    Assert.Equal(handle, ExecutionScope.Current!.Descriptor.JobId);
                    Assert.Equal(key, descriptor.IdempotencyKey);
                    Interlocked.Increment(ref runs); entered.SetResult(true);
                    if (stop) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    else await finish.Task;
                }, token)) with { RegistryKind = JobKind.Solo, RegistrySource = JobSource.Resume };
        var accepted = coordinator.Submit(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(accepted.TaskHandle, coordinator.Submit(item).TaskHandle);
        if (stop) Assert.Equal(BgiTaskCoordinator.CancelOutcome.StopRequestedRunning,
            coordinator.CancelByHandle(accepted.TaskHandle, ownedOnly: true));
        else finish.SetResult(true);
        await Until(() => coordinator.QueryItemStatus(key).ExecutionExitConfirmed);
        var ended = coordinator.QueryItemStatus(key);
        Assert.Equal(accepted.TaskHandle, ended.TaskHandle);
        Assert.Equal(key, ended.IdempotencyKey);
        Assert.Equal("completed", ended.Status);
        Assert.Equal(stop, ended.Cancelled);
        Assert.Equal(1, runs);
        Assert.True(coordinator.Acknowledge(key, accepted.TaskHandle));
        Assert.Equal(accepted.TaskHandle, coordinator.Submit(item).TaskHandle);
        Assert.Equal(1, runs);
    }

}
