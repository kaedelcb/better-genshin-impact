using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;
using Authority = BgiCoordinatorServer.Services.CoordinatedBatchState;
using AuthorityBinding = BgiCoordinatorServer.Services.CoordinatedBatchBinding;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>Actual client loops and server authority; the successful endpoint uses the production local finalizer.</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class CompletedHoeingLocalCancellationTests
{
    private sealed class Room : IAsyncDisposable
    {
        private readonly object _gate = new();
        internal readonly Authority Server;
        internal readonly Endpoint[] Members;
        private Task<bool>[] _runs = [];

        internal Room(BgiTaskQueueStatus? cancelledTerminal = null)
        {
            string[] uids = ["188128099", "shark", "third", "fourth"];
            Server = new(67, uids, DateTime.UtcNow, bindings: uids.ToDictionary(uid => uid,
                _ => (IReadOnlyList<AuthorityBinding>)[new("联机主体和后置", "group")]));
            Members = uids.Select(uid => new Endpoint(this, uid)).ToArray();
            Members[0].Terminal = cancelledTerminal ?? CancelledTail();
            Members[1].Terminal = new()
            {
                Status = "running",
                HoeingEvidence = NativeSuccess("Ran") with { Sealed = false }
            };
        }

        internal CoordinatedBatchSnapshot Exchange(Endpoint endpoint, string stage, string result, string reason,
            int index, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                return Client(Server.Apply(endpoint.Batch.SelfUid, endpoint.Batch.Token, endpoint.Batch.BatchId,
                    "group", index, attempt, result, DateTime.UtcNow, stage, reason));
        }

        internal CoordinatedBatchSnapshot Snapshot() { lock (_gate) return Client(Server.Snapshot()); }

        internal Task<bool>[] Start()
        {
            Assert.Empty(_runs);
            _runs = Members.Select(member => member.Run()).ToArray();
            return _runs;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var member in Members.Where(member => member.Batch.RunTask?.IsCompleted == false)) member.Batch.Cancel();
            if (_runs.Length > 0) await Task.WhenAll(_runs).WaitAsync(TimeSpan.FromSeconds(8));
            foreach (var member in Members) member.Batch.MarkSettled();
        }
    }

    private sealed class Endpoint
    {
        private readonly Room _room;
        private readonly Dictionary<string, BgiJobInfo> _jobs = new();
        internal readonly OnlineHoeingBatch Batch;
        internal readonly BgiEpoch Epoch = CurrentEpoch();
        internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile BgiTaskQueueStatus Terminal = Succeeded();
        internal Func<BgiEpoch, CancellationToken, Task>? Finalizer;
        internal bool CancelAtSubmit;
        internal int Submits, Cancels, Releases, Finalizes, CancellationConfirms;
        internal string Ticket => "ticket-" + Batch.SelfUid;

        internal Endpoint(Room room, string uid)
        {
            _room = room;
            Batch = new(Client(room.Server.Snapshot()), uid, new()
            {
                Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = "onedragon",
                SpecifiedTaskName = "收尾任务"
            });
        }

        internal Task<bool> Run()
        {
            var run = new CoordinatedBatchLoop(Batch, new()
            {
                Prepare = _ => Task.CompletedTask,
                Takeover = _ => { Batch.TakeoverTicket = Ticket; return Task.CompletedTask; },
                Exchange = (stage, result, reason, index, attempt, ct) =>
                    Task.FromResult(_room.Exchange(this, stage, result, reason, index, attempt, ct)),
                ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = Epoch, Jobs = _jobs.Values.ToArray() }),
                ReadTerminal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(Terminal),
                Submit = (item, epoch, _) =>
                {
                    Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                    Interlocked.Increment(ref Submits);
                    var jobId = Guid.NewGuid().ToString("N");
                    _jobs[jobId] = new()
                    {
                        JobId = jobId, IdempotencyKey = item.RequestKey, Epoch = Epoch,
                        State = Terminal.Cancelled ? "cancelled" : "succeeded", WasCancelled = Terminal.Cancelled,
                        ExecutionExitConfirmed = Terminal.Status == "completed"
                    };
                    Submitted.TrySetResult();
                    if (CancelAtSubmit) Batch.Cancel();
                    return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = jobId });
                },
                CancelOwned = (_, _) =>
                {
                    Interlocked.Increment(ref Cancels);
                    Terminal = new() { Status = "queueCancelled", Cancelled = true };
                    return Task.CompletedTask;
                },
                ReleaseTicket = (ticket, epoch, _) =>
                {
                    Assert.Equal(Ticket, ticket);
                    Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                    Interlocked.Increment(ref Releases);
                    return Task.FromResult(true);
                },
                FinalizeLocal = async (epoch, ct) =>
                {
                    Assert.True(Batch.LocalSucceeded);
                    Interlocked.Increment(ref Finalizes);
                    if (Finalizer is not null) await Finalizer(epoch, ct);
                },
                RequireFinishEvidence = true,
                RequireLocalTaskFailureIsolation = true,
                ConfirmLocalTasksCancellation = (jobId, epoch, key, _) =>
                {
                    Interlocked.Increment(ref CancellationConfirms);
                    return Task.FromResult<bool?>(CoordinatedBatchLoop.ConfirmsLocalTaskExit(
                        _jobs[jobId], jobId, epoch, key, cancelled: true));
                },
                ConfirmLocalTasksFailure = (_, _, _, _) =>
                    throw new InvalidOperationException("本场景必须核对取消事实，不能借普通失败确认放行"),
                PollInterval = TimeSpan.FromMilliseconds(2), StageTimeout = TimeSpan.FromSeconds(2),
                CleanupTimeout = TimeSpan.FromSeconds(1), ObservationReconcileTimeout = TimeSpan.FromMilliseconds(150),
                AuthorityContactTimeout = TimeSpan.FromSeconds(1)
            }).RunAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Batch.RunTask = run;
            return run;
        }
    }

    [Fact]
    public async Task CancelledPostTaskDoesNotInterruptPeerExitAndPeerRunsConfiguredOneDragonOnce()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "本用例必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExternal = BgiExternalClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        using var observerLifetime = new CancellationTokenSource();
        await using var room = new Room();
        var first = room.Members[0];
        var shark = room.Members[1];
        var policyJob = Guid.NewGuid().ToString("N");
        var released = false;
        var policyStarted = false;
        ExternalStartAdmissionRequest? admitted = null;
        pipe.BodyForTest = (operation, data) => operation switch
        {
            "task.status" or "ext.task.status" => (true, null, null, Boundary(shark.Epoch, released)),
            "task.resume" => Release(data),
            "config.list" or "ext.config.list" => (true, null, null,
                new { configGroups = Array.Empty<string>(), oneClickConfigs = new[] { "收尾任务" } }),
            "ext.task.start" => StartPolicy(data),
            _ => null
        };
        void AssertCompletionSubscription()
        {
            var frame = Assert.Single(pipe.Received.Where(frame => frame.Operation == "ext.event.subscribe"));
            Assert.Contains(frame.Data!.Value.GetProperty("events").EnumerateArray(),
                value => value.GetString() == "task.completed");
        }
        (bool, string?, string?, object?) Release(JsonElement? data)
        {
            Assert.True(data!.Value.GetProperty("cancel").GetBoolean());
            Assert.Equal(shark.Ticket, data.Value.GetProperty("takeoverTicket").GetString());
            released = true;
            return (true, null, null, new { status = "cleared_not_resumed" });
        }
        (bool, string?, string?, object?) StartPolicy(JsonElement? data)
        {
            Assert.True(released);
            Assert.False(policyStarted);
            AssertCompletionSubscription();
            Assert.Equal("收尾任务", data!.Value.GetProperty("configName").GetString());
            policyStarted = true;
            return (true, null, null, new { status = "queued", taskHandle = policyJob, queuePosition = 0 });
        }
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            using var external = new BgiExternalClient();
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            // Mirror the production Ready handler: wire subscription precedes any queued start.
            // CompletionObserver only observes an existing waiter; it does not subscribe the pipe.
            await external.SubscribeAsync(["task.completed"]).WaitAsync(TimeSpan.FromSeconds(3));
            AssertCompletionSubscription();
            var executor = new CommandExecutor(null!, "unused", () => external,
                externalStartAdmission: async (request, ct) =>
                {
                    Assert.Null(admitted);
                    admitted = request;
                    request.HostLifetimeToken = observerLifetime.Token;
                    var sent = await request.ExecuteAsync(ct);
                    Assert.Equal(ExternalStartExecutionKind.Accepted, sent.Kind);
                    Assert.Equal(policyJob, sent.JobId);
                    return new(ExternalStartAdmissionStatus.Accepted, "accepted", "隔离收尾已受理", JobId: sent.JobId);
                }) { BgiRunningProbeOverride = () => true };
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("禁止停止真实进程");
            executor.SeedTakeoverStateForTest(shark.Ticket, shark.Epoch);
            var vm = new MainViewModel();
            Set(vm, "_activeBatch", shark.Batch);
            Set(vm, "_commandExecutor", executor);
            Set(vm, "_externalClient", external);
            Set(vm, "_reportStatusRunning", 1);
            Set(vm, "_config", new AssistConfig { PlayerUid = shark.Batch.SelfUid });
            shark.Finalizer = (epoch, ct) => (Task)typeof(MainViewModel)
                .GetMethod("FinalizeLocalCoordinatedBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [shark.Batch, executor, epoch, ct])!;

            var runs = room.Start();
            Assert.False(await runs[0]);
            await shark.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(first.Batch.LocalTasksFailed);
            Assert.True(first.Batch.LocalTasksFailureAccepted);
            Assert.False(first.Batch.LocalSucceeded);
            Assert.Equal("failed", first.Batch.Outcome);
            Assert.Equal(1, first.CancellationConfirms);
            Assert.Equal(1, first.Submits);
            Assert.Equal(1, first.Releases);
            Assert.Equal(0, first.Finalizes);
            Assert.Equal(0, first.Cancels);
            var pending = room.Snapshot();
            Assert.Equal("running", pending.Phase);
            Assert.Equal("local_tasks_failed", pending.Members.Single(member => member.Uid == first.Batch.SelfUid).Result);
            Assert.Equal("", pending.Members.Single(member => member.Uid == shark.Batch.SelfUid).Result);
            Assert.False(shark.Batch.IsCancellationRequested);
            Assert.False(shark.Batch.LocalSucceeded);
            Assert.False(shark.Terminal.HoeingEvidence!.Sealed);
            Assert.Equal(1, shark.Submits);
            Assert.Equal(0, shark.Cancels);
            Assert.Equal(0, shark.Finalizes);
            Assert.Equal(0, pipe.CountOf("ext.task.start"));

            // Finish the same peer job after its native/exit tail, rather than replacing or replaying it.
            shark.Terminal = Succeeded();
            var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(results[0]);
            for (var index = 1; index < results.Length; index++)
            {
                var endpoint = room.Members[index];
                Assert.True(results[index],
                    $"uid={endpoint.Batch.SelfUid}; outcome={endpoint.Batch.Outcome}; "
                    + $"failure={endpoint.Batch.FailureReason}; teamFailure={endpoint.Batch.TeamFailureReason}; "
                    + $"finalization={endpoint.Batch.LocalFinalizationTask?.Exception}; "
                    + "pipeOperations=" + string.Join(",", pipe.Received.Select(request => request.Operation)));
            }
            Assert.True(shark.Batch.LocalSucceeded);
            Assert.True(shark.Batch.LocalExecutionReleased);
            Assert.True(shark.Batch.LocalCleanupConfirmed);
            Assert.Equal("succeeded", shark.Batch.Outcome);
            Assert.False(shark.Batch.CoordinatedSucceeded); // Team failure remains truthful.
            Assert.False(shark.Batch.IsCancellationRequested);
            Assert.Equal(1, shark.Submits);
            Assert.Equal(1, shark.Finalizes);
            Assert.Equal(0, shark.Cancels);
            Assert.Equal("onedragon:收尾任务", admitted!.ResourceRef);
            Assert.True(policyStarted);
            Assert.Equal(policyJob, shark.Batch.PolicyExecution!.JobId);
            Assert.True(shark.Batch.PolicyExecution.RegisteredJob);
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.Equal(1, pipe.CountOf("ext.config.list"));
            Assert.Equal(0, pipe.CountOf("config.list"));
            Assert.Equal(1, pipe.CountOf("ext.task.start"));
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("task.stop"));
            Assert.Equal(0, pipe.CountOf("ext.task.cancel"));
            var ended = room.Snapshot();
            Assert.Equal("aborted", ended.Phase);
            Assert.StartsWith("local_tasks_failed:", ended.Reason);
            Assert.Equal(0, ended.Index);
            Assert.Equal(0, ended.Attempt);
            Assert.All(room.Members, endpoint => Assert.Equal(1, endpoint.Submits));
            var completion = admitted.CompletionObserver!(CancellationToken.None);
            Assert.False(completion.IsCompleted);
            AssertCompletionSubscription();
            Assert.Equal(1, pipe.CountOf("ext.event.subscribe"));
            await pipe.PushEventAsync("task.completed", policyJob, new { cancelled = false });
            Assert.Equal(ExternalStartCompletionKind.Succeeded,
                (await completion.WaitAsync(TimeSpan.FromSeconds(3)))!.Kind);
            Assert.Equal(1, pipe.CountOf("ext.task.start"));
        }
        finally
        {
            observerLifetime.Cancel();
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExternal;
        }
    }

    [Fact]
    public async Task ExplicitBatchStopWinsOverAvailableCompletedNativeCancellationEvidence()
    {
        await using var room = new Room();
        var first = room.Members[0];
        first.CancelAtSubmit = true;
        var runs = room.Start();
        Assert.False(await runs[0]);
        await Task.WhenAll(runs);
        Assert.True(first.Batch.IsCancellationRequested);
        Assert.False(first.Batch.LocalTasksFailed);
        Assert.False(first.Batch.LocalSucceeded);
        Assert.Equal("cancelled", first.Batch.Outcome);
        Assert.Equal(0, first.CancellationConfirms);
        Assert.Equal(0, first.Finalizes);
        Assert.Equal(1, first.Submits);
        Assert.Equal(1, first.Releases);
        Assert.Equal("aborted", room.Snapshot().Phase);
        Assert.DoesNotContain(room.Snapshot().Members, member => member.Result == "local_tasks_failed");
    }

    [Theory]
    [InlineData("native_cancelled")]
    [InlineData("planned_count_mismatch")]
    public async Task IncompleteOrUnplannedNativeWorkCannotUseLocalCancellationIsolation(string kind)
    {
        var evidence = kind == "native_cancelled"
            ? NativeSuccess("Cancelled") with { Completed = 0, Cancelled = 1 }
            : NativeSuccess("Cancelled") with { PlannedNativeCount = 2 };
        await using var room = new Room(new() { Status = "completed", Cancelled = true, HoeingEvidence = evidence });
        var runs = room.Start();
        Assert.False(await runs[0]);
        await Task.WhenAll(runs);
        var first = room.Members[0];
        Assert.False(first.Batch.LocalTasksFailed);
        Assert.False(first.Batch.LocalSucceeded);
        Assert.Equal(0, first.CancellationConfirms);
        Assert.Equal(0, first.Finalizes);
        Assert.Equal(1, first.Submits);
        Assert.Equal("aborted", room.Snapshot().Phase);
        Assert.DoesNotContain(room.Snapshot().Members, member => member.Result == "local_tasks_failed");
    }

    private static BgiTaskQueueStatus CancelledTail() =>
        new() { Status = "completed", Cancelled = true, HoeingEvidence = NativeSuccess("Cancelled") };
    private static BgiTaskQueueStatus Succeeded() =>
        new() { Status = "completed", HoeingEvidence = NativeSuccess("Ran") };
    private static CoordinatedHoeingEvidence NativeSuccess(string result) => new()
    {
        Started = 1, Completed = 1, Failed = 0, Cancelled = 0, Active = 0,
        Sealed = true, JobResult = result, PlannedNativeCount = 1, Revision = 3
    };
    private static object Boundary(BgiEpoch epoch, bool released) => new
    {
        bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
        running = false, slotOccupied = false, executionSlotOccupied = false, executionIdentityAvailable = false,
        takeoverActive = !released, hasSuspendedTaskContext = !released
    };
    private static BgiEpoch CurrentEpoch()
    {
        using var process = Process.GetCurrentProcess();
        return new() { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
    }
    private static CoordinatedBatchSnapshot Client(BgiCoordinatorServer.Services.CoordinatedBatchSnapshot snapshot) =>
        JsonSerializer.Deserialize<CoordinatedBatchSnapshot>(JsonSerializer.Serialize(snapshot))!;
    private static void Set(MainViewModel vm, string field, object value) => typeof(MainViewModel)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
}
