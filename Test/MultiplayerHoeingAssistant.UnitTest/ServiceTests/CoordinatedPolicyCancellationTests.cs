using System.Diagnostics;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CoordinatedPolicyCancellationTests
{
    private static OnlineHoeingBatch Batch(string ticket, TaskConflictPolicy policy = TaskConflictPolicy.Resume) => new(
        new CoordinatedBatchSnapshot("policy-" + Guid.NewGuid().ToString("N"), 31, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", Source = "manual", ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> { ["self"] = [new("锄地", "group")] }
        }, "self", new TaskConflictPolicySettings { Policy = policy })
        { TakeoverTicket = ticket, TakenOver = true, CoordinatedSucceeded = true };

    [Fact]
    public async Task StopBeforeEarlyReceiptWaitsForTheSendAndCancelsThatLateReceiptOnce()
    {
        var batch = Batch("owned");
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellations = 0;
        batch.ConfigurePolicyCancellation(receipt =>
        {
            Interlocked.Increment(ref cancellations);
            cancelled.TrySetResult(receipt.JobId);
            return Task.FromResult(true);
        });
        batch.ConfigurePolicyCleanup(async () => await batch.WaitPolicyCancellationAsync());
        batch.Cancel();
        var cleanup = batch.WaitCancelledPolicyCleanupAsync();
        Assert.False(cleanup.IsCompleted);
        batch.RecordPolicyExecution(new HoeingPolicyExecution("late-owned-job", new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 }, true));
        Assert.Equal("late-owned-job", await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(cleanup.IsCompleted); // The send can still publish its receipt/ownership.
        batch.MarkPolicyPhaseFinished();
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(3)));
        batch.Cancel();
        Assert.Equal(1, cancellations);
        batch.MarkSettled();
    }

    [Fact]
    public void SettledBatchIgnoresLateCancellationAndCannotReclaimItsSuccessor()
    {
        var batch = Batch("owned");
        var calls = 0;
        batch.ConfigurePolicyCancellation(_ => { calls++; return Task.FromResult(true); });
        batch.RecordPolicyExecution(new HoeingPolicyExecution("released-successor", new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 }, true));
        batch.MarkSettled();
        batch.Cancel();
        Assert.False(batch.IsCancellationRequested);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StopAfterTeamCompletedButBeforePolicyDoesNotSendResumeOrSpecifiedStart()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var epoch = Epoch();
        var ticket = Guid.NewGuid().ToString("N");
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        var batch = Batch(ticket, TaskConflictPolicy.RunSpecified);
        var vm = Vm(executor, batch);
        pipe.BodyForTest = (operation, _) => operation == "task.resume"
            ? (true, null, null, new { status = "cleared_not_resumed" }) : null;
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            batch.Cancel();
            await Finalize(vm, batch, ticket).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await batch.WaitCancelledPolicyCleanupAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var resume = Assert.Single(pipe.Received.Where(r => r.Operation == "task.resume")).Data!.Value;
            Assert.True(resume.GetProperty("cancel").GetBoolean());
            Assert.Equal(ticket, resume.GetProperty("takeoverTicket").GetString());
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            Assert.True(batch.CleanupConfirmed);
        }
        finally { IpcClient.PipeNameOverrideForTest = previous; batch.MarkSettled(); }
    }

    [Fact]
    public async Task StopWhileNativeResumeIsInFlightKeepsReceiptAndStopsOnlyTheAcceptedAttempt()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        using var allowReceipt = new ManualResetEventSlim();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = Guid.NewGuid().ToString("N");
        var job = Guid.NewGuid();
        var root = Guid.NewGuid();
        var epoch = Epoch();
        var resumed = false;
        var exactStop = false;
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        var batch = Batch(ticket);
        var vm = Vm(executor, batch);
        pipe.BodyForTest = (operation, data) =>
        {
            if (operation == "task.resume")
            {
                Assert.False(data!.Value.GetProperty("cancel").GetBoolean());
                sent.TrySetResult();
                Assert.True(allowReceipt.Wait(TimeSpan.FromSeconds(10)));
                resumed = true;
                return (true, null, null, new { status = "resumed", attemptId = job.ToString("N") });
            }
            if (operation == "task.stop")
            {
                Assert.Equal(root.ToString("N"), data!.Value.GetProperty("executionInstanceId").GetString());
                Assert.Equal(9, data.Value.GetProperty("executionStateRevision").GetInt64());
                Assert.Equal(epoch.ProcessId, data.Value.GetProperty("bgiEpoch").GetProperty("processId").GetInt32());
                exactStop = true;
                return (true, null, null, new { status = "stop_requested" });
            }
            if (operation == "task.status" && data is { } query && query.TryGetProperty("executionInstanceId", out var target))
                return (true, null, null, new
                {
                    bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                    executionExitQueryInstanceId = target.GetString(), executionExitConfirmed = exactStop,
                    executionExitReason = "confirmed", executionExitAtUtc = DateTime.UtcNow,
                    executionExitOrder = 1L
                });
            if (operation == "task.status")
                return (true, null, null, new
                {
                    running = resumed, hasSuspendedTaskContext = !resumed, suspendedTaskType = "configGroup", suspendedTaskName = "原任务",
                    bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                    executionInstanceId = root.ToString("N"), executionJobId = job.ToString("N"), executionStateRevision = 9L
                });
            return null;
        };
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            var finalize = Finalize(vm, batch, ticket);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            batch.Cancel();
            Assert.False(finalize.IsCompleted);
            allowReceipt.Set();
            await finalize.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await batch.WaitCancelledPolicyCleanupAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(exactStop);
            Assert.Equal(job.ToString("D"), batch.PolicyExecution!.JobId);
            Assert.Equal(root, batch.PolicyExecution.NativeRoot!.Value.InstanceId);
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.Equal(1, pipe.CountOf("task.stop"));
            Assert.True(batch.CleanupConfirmed);
        }
        finally { allowReceipt.Set(); IpcClient.PipeNameOverrideForTest = previous; batch.MarkSettled(); }
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("stale_epoch")]
    [InlineData("unconfirmed_exit")]
    public async Task RegisteredPolicyCancellationUsesOnlyExactJobAndEpochAndRequiresExitProof(string scenario)
    {
        var previous = BgiExternalClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var epoch = Epoch();
        var job = Guid.NewGuid().ToString("N");
        pipe.BodyForTest = (operation, data) => operation switch
        {
            "ext.task.cancel" => (true, null, null, new { status = "stop_requested" }),
            "ext.job.status" => (true, null, null, new
            {
                status = "cancelled", bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                job = new { jobId = job, state = "cancelled", exitConfirmed = scenario == "confirmed", exitDisposition = "execution_exited" }
            }),
            _ => null
        };
        try
        {
            BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            using var ext = new BgiExternalClient();
            await ext.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BgiExternalLinkState.Ready, ext.State);
            var executor = new CommandExecutor(null!, "unused", () => ext);
            var expected = scenario == "stale_epoch"
                ? new BgiEpoch { ProcessId = epoch.ProcessId, StartTicksUtc = epoch.StartTicksUtc - 1 } : epoch;
            Assert.Equal(scenario == "confirmed", await executor.CancelHoeingPolicyExecutionAsync(
                new HoeingPolicyExecution(job, expected, true)).WaitAsync(TimeSpan.FromSeconds(18)));
            Assert.Equal(scenario == "stale_epoch" ? 0 : 1, pipe.CountOf("ext.task.cancel"));
            if (scenario != "stale_epoch")
            {
                var request = Assert.Single(pipe.Received.Where(r => r.Operation == "ext.task.cancel")).Data!.Value;
                Assert.Equal(job, request.GetProperty("taskHandle").GetString());
                Assert.Equal(epoch.ProcessId, request.GetProperty("bgiEpoch").GetProperty("processId").GetInt32());
                Assert.Equal(epoch.StartTicksUtc, request.GetProperty("bgiEpoch").GetProperty("startTicksUtc").GetInt64());
            }
            Assert.Equal(0, pipe.CountOf("task.stop"));
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
        }
        finally { BgiExternalClient.PipeNameOverrideForTest = previous; }
    }

    private static BgiEpoch Epoch()
    {
        using var process = Process.GetCurrentProcess();
        return new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
    }
    private static MainViewModel Vm(CommandExecutor executor, OnlineHoeingBatch batch)
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_commandExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, executor);
        typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, batch);
        return vm;
    }
    private static Task Finalize(MainViewModel vm, OnlineHoeingBatch batch, string ticket) =>
        (Task)typeof(MainViewModel).GetMethod("ApplyPolicyTeardownOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { batch, "联机锄地", false, ticket })!;
}
