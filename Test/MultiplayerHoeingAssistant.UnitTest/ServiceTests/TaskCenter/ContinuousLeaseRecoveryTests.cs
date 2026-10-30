using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("TaskCenterHeavyE2E")]
public sealed class ContinuousLeaseRecoveryTests
{
    private sealed class Port : IBgiExecutionPort
    {
        internal readonly ConcurrentDictionary<string, BgiJobInfo> Jobs = new();
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Polling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RecoveryObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts, Cancels;
        private int _activeStatusProbe;
        internal bool WrongPayload;
        internal volatile bool StillActive;
        internal long StopVersion;
        internal TaskCompletionSource? QueryEntered;
        internal TaskCompletionSource? QueryRelease;
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch { get; } = new() { ProcessId = 123, StartTicksUtc = 456 };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            if (operation == WorkflowStopAuthority.Operation)
                return Task.FromResult(new BgiExternalResponse { Success = true, Data = JsonSerializer.Serialize(new
                { bgiEpoch = new { processId = 123, startTicksUtc = 456 }, stopVersion = StopVersion, lastManualStopTimestamp = StopVersion == 0 ? (long?)null : Stopwatch.GetTimestamp(), monotonicFrequency = Stopwatch.Frequency }) });
            Assert.Equal(BgiExternalClient.ExternalOperations.TaskStart, operation);
            Interlocked.Increment(ref Starts);
            var json = JsonSerializer.Serialize(payload);
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            var id = Guid.NewGuid().ToString("N");
            Jobs[id] = new()
            {
                JobId = id, Epoch = ServerEpoch, State = "running", ExecutionExitConfirmed = false,
                IdempotencyKey = data.GetProperty("idempotencyKey").GetString(), WorkflowRunId = data.GetProperty("workflowRunId").GetString(),
                NodeId = data.GetProperty("nodeId").GetString(), Occurrence = data.GetProperty("occurrence").GetInt32(),
                Iteration = data.GetProperty("iteration").GetInt32(), Attempt = data.GetProperty("attempt").GetInt32(),
                RequestFingerprintVersion = 1, RequestFingerprint = BgiOriginalRequestFingerprint.Compute(operation, json),
                RequestOperation = operation, ConfigRevision = data.GetProperty("expectedConfigRevision").GetString(),
            };
            return Task.FromResult(new BgiExternalResponse { Success = true,
                Data = JsonSerializer.Serialize(new { status = "queued", taskHandle = id }) });
        }
        private BgiJobInfo Finished(BgiJobInfo job, bool statusProbe = false)
        {
            var json = JsonSerializer.SerializeToNode(job)!;
            json[nameof(BgiJobInfo.State)] = StillActive ? (statusProbe && Interlocked.Increment(ref _activeStatusProbe) == 1 ? "result_unknown" : "running") : "succeeded";
            json[nameof(BgiJobInfo.ExecutionExitConfirmed)] = !StillActive;
            json[nameof(BgiJobInfo.ExecutionExitDisposition)] = StillActive ? null : "execution_exited";
            if (WrongPayload) json[nameof(BgiJobInfo.RequestFingerprint)] = new string('F', 64);
            return json.Deserialize<BgiJobInfo>()!;
        }
        public async Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            Polling.TrySetResult();
            await Release.Task.WaitAsync(ct);
            var job = Jobs[jobId] = Finished(Jobs[jobId], statusProbe: true);
            return (job.State, job);
        }
        public async Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
        {
            if (Release.Task.IsCompleted && QueryRelease is { } gate)
            {
                QueryEntered!.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
            var jobs = Jobs.Values.Select(j => Release.Task.IsCompleted ? Finished(j) : j).ToList();
            if (Release.Task.IsCompleted && StillActive) RecoveryObserved.TrySetResult();
            return new() { Epoch = ServerEpoch, Jobs = jobs };
        }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) { Interlocked.Increment(ref Cancels); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("f11")]
    [InlineData("pause")]
    [InlineData("wrong-payload")]
    [InlineData("other-owner")]
    [InlineData("stop-during-recovery")]
    [InlineData("still-running")]
    [InlineData("online-wake-after-recovery")]
    [InlineData("stop-during-query")]
    [InlineData("pause-during-query")]
    [InlineData("f11-cooled-down")]
    public async Task LiveDriverLeaseExpiry_ContinuesOnlyProvenOriginal_AndRespectsControls(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "continuous-lease-" + Guid.NewGuid().ToString("N"));
        using var client = new BgiExternalClient();
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.State))!.SetValue(client, BgiExternalLinkState.Ready);
        var port = new Port();
        var logs = new ConcurrentQueue<string>();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seams = new TaskCenterAdmissionSeams { Epoch = "123:456", F11Active = false, Occupied = false, FactsUnknown = false,
            OwnershipTtlSeconds = 2, SuppressLeaseHeartbeatForTest = true,
            OnTakeoverObservationEntered = () => observing.TrySetResult(),
            ProductionBoundaryFactory = (_, runs) => new BgiWorkflowExecutionBoundary(port, runs) };
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => client, logs.Enqueue, null, () => (true, null), localExecutionCapability: () => true,
            statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "123:456",
                TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
            admissionWired: true, successorAdmissionWired: true, admissionSeams: seams);
        try
        {
            var flow = new WorkflowDocument { Name = "连续两项", Activation = new() { Status = "active" }, Nodes =
            [ new() { NodeId = "first", Kind = "resource.configGroup", Ref = new() { Config = "狗粮", Revision = "rev-1" } },
              new() { NodeId = "next", Kind = "resource.configGroup", Ref = new() { Config = "下一项", Revision = "rev-2" } } ] };
            host.Workflows.Save(flow, null);
            Assert.True((await host.StartWorkflowAsync(flow.WorkflowId!)).Ok);
            await port.Polling.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var original = Assert.Single(host.Runs.List());
            var oldStore = host.Runs;
            await Task.Delay(2300);
            seams.SuppressLeaseHeartbeatForTest = false;
            if (scenario == "f11") seams.F11Active = true;
            if (scenario == "pause") await host.RequestRunActionAsync(original.RunId, WorkflowRunAction.Pause);
            if (scenario == "wrong-payload") port.WrongPayload = true;
            if (scenario == "still-running") port.StillActive = true;
            if (scenario == "other-owner")
            {
                var competing = new ArbitrationLeaseStore(Path.Combine(root, "arbitration"));
                var mono = TimeSpan.Zero;
                var observer = new LeaseTakeoverObserver(() => mono);
                Assert.Null(observer.Observe(competing.Read()));
                mono = TimeSpan.FromSeconds(3);
                Assert.True(competing.TryAcquire("another-owner", 30, observer.Observe(competing.Read())).Success);
            }
            if (scenario == "f11-cooled-down") port.StopVersion = 1; // Cooldown is already inactive; the original fence must still win.
            if (scenario is "stop-during-query" or "pause-during-query")
            {
                port.QueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                port.QueryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            port.Release.TrySetResult();
            if (scenario is "stop-during-query" or "pause-during-query")
            {
                await port.QueryEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var control = host.RequestRunActionAsync(original.RunId,
                    scenario == "stop-during-query" ? WorkflowRunAction.Stop : WorkflowRunAction.Pause);
                port.QueryRelease!.TrySetResult();
                await control;
            }
            if (scenario == "still-running")
            {
                await port.RecoveryObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1, port.Starts);
                port.StillActive = false;
            }
            if (scenario == "stop-during-recovery")
            {
                await observing.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.RequestRunActionAsync(original.RunId, WorkflowRunAction.Stop);
            }
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (host.IsDriving(flow.WorkflowId!)
                || (scenario is "complete" or "still-running" or "online-wake-after-recovery") && host.Runs.Load(original.RunId)?.IsTerminal != true
                || (scenario is "f11" or "wrong-payload") && !logs.Any(l => l.Contains("自动接续未确认"))
                || (scenario is "stop-during-recovery" or "stop-during-query" or "pause-during-query")
                    && !logs.Any(l => l.Contains("自动接续已取消") || l.Contains("自动接续未确认"))
                || scenario == "f11-cooled-down" && host.Runs.Load(original.RunId)?.StopRequested != true)
                await Task.Delay(25, budget.Token);
            if (scenario is "complete" or "still-running" or "online-wake-after-recovery")
            {
                Assert.Equal(WorkflowRunState.Succeeded, host.Runs.Load(original.RunId)!.State);
                Assert.Equal(2, port.Starts);
                Assert.Single(port.Jobs.Values.Where(j => j.NodeId == "first"));
                Assert.Single(port.Jobs.Values.Where(j => j.NodeId == "next"));
                Assert.NotSame(oldStore, host.Runs);
                Assert.Throws<RunRecordConflictException>(() => oldStore.Update(oldStore.Load(original.RunId)!));
            }
            else Assert.Equal(1, port.Starts);
            Assert.Single(host.Runs.List());
            if (!scenario.StartsWith("stop-", StringComparison.Ordinal) && scenario != "f11-cooled-down") Assert.Equal(0, port.Cancels);
            if (scenario == "online-wake-after-recovery")
            {
                // Exercise the actual heartbeat born during automatic lease recovery,
                // after that recovery has finished and an explicit control invalidated
                // its old revision. It must still wake unrelated online waiters.
                await host.RequestRunActionAsync(original.RunId, WorkflowRunAction.Stop);
                var waitingFlow = new WorkflowDocument { Name = "锄地后等待计划", Activation = new() { Status = "active" },
                    Nodes = [new() { NodeId = "end", Kind = "control.end" }] };
                var revision = host.Workflows.Save(waitingFlow, null);
                var waiting = host.Runs.CreateRun(waitingFlow.WorkflowId!, revision, handoff: new HandoffIdentity
                {
                    IntentKey = "online-original", ExecutionId = "online-execution", StepId = "start", Mode = StartupHandoffModes.Start,
                }, admissionSourceScope: "bgi:local:123:456", stopAuthority: host.Runs.Load(original.RunId)!.StopAuthority);
                waiting.State = WorkflowRunState.Interrupted;
                waiting.OnlineHoeingGateParked = true;
                waiting.TriggerConsumed = true;
                waiting.Cursor = new() { NodeId = "end", Attempt = 1 };
                host.Runs.Update(waiting);
                typeof(TaskCenterHost).GetField("_priorityWaiterWakePending",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(host, 1);
                using var wakeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    while (host.Runs.Load(waiting.RunId)?.State != WorkflowRunState.Succeeded)
                        await Task.Delay(25, wakeBudget.Token);
                }
                catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(string.Join("\n", logs)); }
                Assert.False(host.Runs.Load(waiting.RunId)!.OnlineHoeingGateParked);
                Assert.Equal(2, port.Starts); // A local waiter never replays either original job.
            }
        }
        finally { port.Release.TrySetResult(); port.QueryRelease?.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task SlowReadOfPublishedSnapshot_DoesNotHoldRenewalLock_AndSeesOnePublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "lease-snapshot-" + Guid.NewGuid().ToString("N"));
        var owner = new ArbitrationLeaseStore(root);
        var acquired = owner.TryAcquire("original", 30);
        Assert.True(acquired.Success);
        var observer = new ArbitrationLeaseStore(root);
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        observer.SnapshotOpenedForTest = () => { opened.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var reading = Task.Run(observer.Read);
        try
        {
            Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(owner.TryRenewLatest(acquired.Lease!.LeaseId, "original").Success);
        }
        finally { release.Set(); }
        var snapshot = await reading;
        Assert.Equal(acquired.Lease!.HeartbeatSeq, snapshot.File!.Lease!.HeartbeatSeq);
        Assert.Equal(acquired.Lease.HeartbeatSeq + 1, owner.Read().File!.Lease!.HeartbeatSeq);
    }
}
