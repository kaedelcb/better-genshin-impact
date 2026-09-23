using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

[CollectionDefinition("TaskTakeoverIncident", DisableParallelization = true)]
public sealed class TaskTakeoverIncidentCollection;

// Real handlers, gate and lifecycle; only in-memory config is substituted.
// No application dispatcher, named pipe, screenshots, game or user-directory writes.
[Collection("TaskTakeoverIncident")]
public sealed class TaskTakeoverIncidentTests : IDisposable
{
    private readonly AllConfig? previous = ConfigService.Config;
    private readonly AllConfig config = new();
    private readonly DateTime? previousStopTime = CancellationContext.Instance.LastManualCancelAtUtc;
    private readonly InstanceRequestHandler handler = new(null!, null!, null!, _ => { }, _ => { }, NullLogger.Instance);
    private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;

    public TaskTakeoverIncidentTests()
    {
        ConfigProperty.SetValue(null, config);
        PreemptionGate.Disarm();
        CancellationContext.Instance.Set();
        typeof(CancellationContext).GetProperty(nameof(CancellationContext.LastManualCancelAtUtc))!.SetValue(CancellationContext.Instance, null);
    }

    [Fact]
    public void TaskStatus_IncludesCurrentProcessEpoch()
    {
        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus);
        var response = handler.HandleTaskStatus(null!, request);
        var epoch = JobRegistry.CurrentEpoch;
        var actual = response.Data!["bgiEpoch"]!;

        Assert.Equal(epoch.ProcessId, (int)actual["processId"]!);
        Assert.Equal(epoch.StartTicksUtc, (long)actual["startTicksUtc"]!);

        var endpoint = new InstanceContext(BetterGiInstanceType.Primary, "test-ping-identity", null).ToEndpoint();
        Assert.Equal(epoch.ProcessId, endpoint.ProcessId);
        Assert.Equal(epoch.StartTicksUtc, endpoint.ProcessStartTicks);

        var readOnlyHandler = new InstanceRequestHandler(
            new InstanceContext(BetterGiInstanceType.Primary, "test-readonly-status", null),
            null!, null!, _ => { }, _ => { }, NullLogger.Instance);
        var readOnlySnapshot = readOnlyHandler.CreateReadOnlyStatusSnapshot();
        var readOnlyEpoch = readOnlySnapshot.GetType().GetProperty("bgiEpoch")!.GetValue(readOnlySnapshot)!;
        Assert.Equal(epoch.ProcessId, (int)readOnlyEpoch.GetType().GetProperty("processId")!.GetValue(readOnlyEpoch)!);
        Assert.Equal(epoch.StartTicksUtc, (long)readOnlyEpoch.GetType().GetProperty("startTicksUtc")!.GetValue(readOnlyEpoch)!);
    }

    [Fact]
    public void TaskStatus_ExposesVersionedExecutionIdentity_OnBothStatusChannels()
    {
        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using var scope = ExecutionScope.Start(new(JobKind.OneDragon, "抢占身份测试", JobSource.Hotkey,
            JobId: jobId, WorkflowRunId: runId));

        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus);
        var response = handler.HandleTaskStatus(null!, request);
        Assert.True(response.Success);
        var data = response.Data!;
        Assert.True(data["executionIdentityAvailable"]!.ToObject<bool>());
        var instanceId = Guid.Parse(data["executionInstanceId"]!.ToObject<string>()!);
        var firstRevision = data["executionStateRevision"]!.ToObject<long>();
        Assert.NotEqual(Guid.Empty, instanceId);
        Assert.True(firstRevision > 0);
        Assert.Equal(runId.ToString("N"), data["executionRunId"]!.ToObject<string>());
        Assert.Equal(jobId.ToString("N"), data["executionJobId"]!.ToObject<string>());
        Assert.Equal(nameof(JobKind.OneDragon), data["executionKind"]!.ToObject<string>());
        Assert.Equal(nameof(JobSource.Hotkey), data["executionSource"]!.ToObject<string>());
        Assert.Equal("抢占身份测试", data["executionName"]!.ToObject<string>());
        Assert.False(data["executionStopRequested"]!.ToObject<bool>());

        var readOnlyHandler = new InstanceRequestHandler(
            new InstanceContext(BetterGiInstanceType.Primary, "test-readonly-status", null),
            null!, null!, _ => { }, _ => { }, NullLogger.Instance);
        var snapshot = readOnlyHandler.CreateReadOnlyStatusSnapshot();
        Assert.Equal(instanceId.ToString("N"), snapshot.GetType().GetProperty("executionInstanceId")!.GetValue(snapshot));
        Assert.Equal(firstRevision, snapshot.GetType().GetProperty("executionStateRevision")!.GetValue(snapshot));

        scope.Cancel();
        var stopped = handler.HandleTaskStatus(null!, request).Data!;
        Assert.Equal(instanceId.ToString("N"), stopped["executionInstanceId"]!.ToObject<string>());
        Assert.True(stopped["executionStateRevision"]!.ToObject<long>() > firstRevision);
        Assert.True(stopped["executionStopRequested"]!.ToObject<bool>());
    }

    [Fact]
    public async Task TaskStatus_BusySemaphoreWithoutExecutionScope_LeavesIdentityUnknown()
    {
        Assert.True(await TaskControl.TaskSemaphore.WaitAsync(0));
        try
        {
            Assert.Null(ExecutionScope.GetActiveSnapshot());
            var response = handler.HandleTaskStatus(null!,
                InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus));

            Assert.True(response.Success);
            Assert.True(response.Data!["running"]!.ToObject<bool>());
            Assert.True(response.Data["slotOccupied"]!.ToObject<bool>());
            Assert.False(response.Data["executionIdentityAvailable"]!.ToObject<bool>());
            Assert.Null(response.Data["executionInstanceId"]?.ToObject<string>());
        }
        finally
        {
            TaskControl.TaskSemaphore.Release();
        }
    }

    [Fact]
    public async Task Dispose_FromInheritedExecutionContext_DoesNotSkipParentAmbientRestore()
    {
        var root = ExecutionScope.Start(new(JobKind.Group, "父作用域", JobSource.Ui));

        await Task.Run(root.Dispose);

        Assert.Same(root, ExecutionScope.Current);
        root.Dispose();
        Assert.Null(ExecutionScope.Current);
    }

    public void Dispose()
    {
        PreemptionGate.Disarm();
        config.SuspendedTaskContext = null;
        ConfigProperty.SetValue(null, previous);
        typeof(CancellationContext).GetProperty(nameof(CancellationContext.LastManualCancelAtUtc))!.SetValue(CancellationContext.Instance, previousStopTime);
    }

    private static InstanceIpcEnvelope Request(string op, string ticket, bool cancel = false)
        => InstanceIpcEnvelope.Request(op, new { takeoverTicket = ticket, cancel,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc } });

    [Fact]
    public async Task IdleSuspend_HasNoVictim_AndReleaseIsIdempotent()
    {
        var ticket = Guid.NewGuid().ToString("N");
        var response = await handler.HandleTaskSuspend(null!, Request("task.suspend", ticket));
        Assert.True(response.Success);
        Assert.False(response.Data!["liveTask"]!.ToObject<bool>());
        Assert.True(response.Data["quiesceConfirmed"]!.ToObject<bool>());
        Assert.Null(config.SuspendedTaskContext);
        var release = await handler.HandleTaskResume(null!, Request("task.resume", ticket));
        Assert.Equal("cleared_not_resumed", release.Data!["status"]!.ToString());
        var duplicate = await handler.HandleTaskResume(null!, Request("task.resume", ticket));
        Assert.True(duplicate.Success);
        Assert.Equal("cleared_not_resumed", duplicate.Data!["status"]!.ToString());
    }

    [Fact]
    public async Task ActiveDragonSuspend_PreservesAncestor_AndDuplicateCannotReplaceVictim()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Task.Run(async () =>
        {
            using var root = ExecutionScope.Start(new(JobKind.OneDragon, "原单机龙", JobSource.Ui, JobId: Guid.NewGuid()));
            root.SetDragonNode("task-guid-4");
            root.SetCheckpoint(new("group", "正在执行组", 2, "folder", "project", null, null, null, false));
            ready.SetResult();
            try { await Task.Delay(Timeout.Infinite, root.Token); } catch (OperationCanceledException) { }
        });
        await ready.Task;
        var ticket = Guid.NewGuid().ToString("N");
        var response = await handler.HandleTaskSuspend(null!, Request("task.suspend", ticket));
        await old;
        Assert.True(response.Success);
        var victim = config.SuspendedTaskContext!;
        Assert.Equal("原单机龙", victim.GroupName);
        Assert.Equal("正在执行组", victim.SubTaskGroupName);
        Assert.Equal("task-guid-4", victim.OneDragonTaskId);
        Assert.Equal(2, victim.TaskIndex);
        Assert.NotNull(victim.RootRunId);
        Assert.NotNull(victim.AttemptId);
        Assert.True((await handler.HandleTaskSuspend(null!, Request("task.suspend", ticket))).Success);
        Assert.Same(victim, config.SuspendedTaskContext);
    }

    [Fact]
    public async Task ForeignResume_CannotClearVictimOrReleaseTicket()
    {
        var owner = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(owner);
        var victim = new SuspendedTaskContext { TakeoverTicket = owner, StopVersion = ExecutionScope.StopVersionNow };
        config.SuspendedTaskContext = victim;
        var response = await handler.HandleTaskResume(null!, Request("task.resume", Guid.NewGuid().ToString("N"), true));
        Assert.False(response.Success);
        Assert.Equal("stale_ticket", response.ErrorCode);
        Assert.Same(victim, config.SuspendedTaskContext);
        Assert.True(PreemptionGate.Authorize(owner));
    }

    [Fact]
    public async Task RemovedConfiguration_RejectsResumeWithoutConsumingVictim()
    {
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        var victim = new SuspendedTaskContext {
            TaskType = "group", GroupName = "deleted", TakeoverTicket = ticket, StopVersion = ExecutionScope.StopVersionNow,
            ConfigurationRevisions = new() { [Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")] = "old" }
        };
        config.SuspendedTaskContext = victim;
        var response = await handler.HandleTaskResume(null!, Request("task.resume", ticket));
        Assert.False(response.Success);
        Assert.Equal("configuration_changed", response.ErrorCode);
        Assert.Same(victim, config.SuspendedTaskContext);
        Assert.False(ExecutionScope.HasActive);
    }

    [Fact]
    public async Task OldClientWithoutTicket_IsRejectedBeforeTakingOwnership()
    {
        var response = await handler.HandleTaskSuspend(null!, InstanceIpcEnvelope.Request("task.suspend"));
        Assert.False(response.Success);
        Assert.Equal("capability_required", response.ErrorCode);
        Assert.True(PreemptionGate.Authorize(null));
        Assert.Null(config.SuspendedTaskContext);
    }

    [Fact]
    public async Task PreviousProcessIntent_IsRejectedBeforeTakingOwnership()
    {
        var request = InstanceIpcEnvelope.Request("task.suspend", new { takeoverTicket = Guid.NewGuid().ToString("N"),
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc - 1 } });
        var response = await handler.HandleTaskSuspend(null!, request);
        Assert.False(response.Success);
        Assert.Equal("stale_epoch", response.ErrorCode);
        Assert.True(PreemptionGate.Authorize(null));
    }

    [Fact]
    public async Task ManualStop_DuringRequestDelay_DeniesTakeoverAndRemovesResumeIntent()
    {
        config.SuspendedTaskContext = new SuspendedTaskContext { GroupName = "old" };
        CancellationContext.Instance.Clear();
        CancellationContext.Instance.ManualCancel();
        var response = await handler.HandleTaskSuspend(null!, Request("task.suspend", Guid.NewGuid().ToString("N")));
        Assert.False(response.Success);
        Assert.Equal("manual_stop_cooldown", response.ErrorCode);
        Assert.Null(config.SuspendedTaskContext);
        Assert.True(PreemptionGate.Authorize(null));
    }
}
