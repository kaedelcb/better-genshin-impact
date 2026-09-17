using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
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
        Assert.Equal(false, response.Data!["liveTask"]!.ToObject<bool>());
        Assert.Equal(true, response.Data["quiesceConfirmed"]!.ToObject<bool>());
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
            root.SetDragonNode(4);
            root.SetCheckpoint(new("group", "正在执行组", 2, "folder", "project", 0, null, null, false));
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
        Assert.Equal(4, victim.OneDragonTaskIndex);
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
