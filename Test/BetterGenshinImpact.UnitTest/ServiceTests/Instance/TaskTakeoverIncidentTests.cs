using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.ExternalInterface;
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

    [Fact]
    public void Start_AfterManualStopBetweenCheckAndRootCreation_RejectsOldWatermark()
    {
        var observedStopVersion = ExecutionScope.StopVersionNow;
        ExecutionScope.StopActive(manual: true);
        var admitted = false;

        Assert.Throws<OperationCanceledException>(() => ExecutionScope.Start(
            new JobDescriptor(JobKind.Group, "旧停止水位", JobSource.V2,
                ExpectedStopVersion: observedStopVersion,
                OnAdmitted: () => admitted = true)));
        Assert.False(admitted);
        Assert.False(ExecutionScope.HasActive);

        using var current = ExecutionScope.Start(new JobDescriptor(
            JobKind.Group, "新停止水位", JobSource.V2,
            ExpectedStopVersion: ExecutionScope.StopVersionNow));
        Assert.True(current.IsCurrentOwner);
    }

    [Fact]
    public void TryRequestPreempt_StopsOnlyMatchingExecutionInstanceAndRevision()
    {
        using (var first = ExecutionScope.Start(new(JobKind.Group, "待抢占根", JobSource.Ui)))
        {
            var fact = ExecutionScope.GetActiveSnapshot()!;
            Assert.False(ExecutionScope.TryRequestPreempt(Guid.NewGuid(), fact.StateRevision));
            Assert.False(ExecutionScope.TryRequestPreempt(fact.ExecutionInstanceId, fact.StateRevision - 1));
            Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);

            first.Observe(TaskRunResult.Skipped);
            Assert.False(ExecutionScope.TryRequestPreempt(fact.ExecutionInstanceId, fact.StateRevision));
            fact = ExecutionScope.GetActiveSnapshot()!;

            Assert.True(ExecutionScope.TryRequestPreempt(fact.ExecutionInstanceId, fact.StateRevision));
            Assert.True(ExecutionScope.GetActiveSnapshot()!.StopRequested);
            Assert.True(first.Token.IsCancellationRequested);
            Assert.Equal(TaskRunResult.Preempted, first.Result);
            Assert.True(ExecutionScope.HasActive); // 请求已发不等于根退出
            Assert.False(ExecutionScope.TryRequestPreempt(fact.ExecutionInstanceId, fact.StateRevision));

            var oldFact = fact;
            first.Dispose();
            using var successor = ExecutionScope.Start(new(JobKind.Group, "继任根", JobSource.Ui));
            Assert.False(ExecutionScope.TryRequestPreempt(oldFact.ExecutionInstanceId, oldFact.StateRevision));
            var fresh = ExecutionScope.GetActiveSnapshot()!;
            Assert.Equal(successor.ExecutionInstanceId, fresh.ExecutionInstanceId);
            Assert.False(fresh.StopRequested);
            Assert.False(successor.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task TryRequestPreempt_RunsCallbacksOutsideRootLock_AndCannotCancelReplacement()
    {
        using var old = ExecutionScope.Start(new(JobKind.Group, "旧根", JobSource.Ui));
        var oldFact = ExecutionScope.GetActiveSnapshot()!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockWasFree = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = old.Token.Register(() =>
        {
            entered.TrySetResult();
            var read = Task.Run(ExecutionScope.GetActiveSnapshot);
            lockWasFree.TrySetResult(read.Wait(TimeSpan.FromSeconds(2)));
            release.Task.GetAwaiter().GetResult();
        });

        var request = Task.Run(() => ExecutionScope.TryRequestPreempt(oldFact.ExecutionInstanceId, oldFact.StateRevision));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(await lockWasFree.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            old.Dispose();
            using var successor = ExecutionScope.Start(new(JobKind.Group, "新根", JobSource.Ui));
            release.TrySetResult();
            Assert.True(await request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(successor.Token.IsCancellationRequested);
            Assert.Equal(successor.ExecutionInstanceId, ExecutionScope.GetActiveSnapshot()!.ExecutionInstanceId);
        }
        finally
        {
            release.TrySetResult();
        }
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

    // ===== R5 A4 第一步：task.stop 的定向身份绑定（反例先行）=====
    // 反例语义：带执行身份的停止只能作用于当时那一颗执行根；身份过期即零副作用拒绝，
    // 既不波及继任执行，也不得把"停止请求已登记"写成"已退出"。
    // 无身份的旧协议 task.stop 仍是"全部停止"，逐字节保留。

    private static InstanceIpcEnvelope StoppingRequest(Guid instanceId, long stateRevision,
        int? processId = null, long? startTicksUtc = null)
        => InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = instanceId.ToString("N"),
            executionStateRevision = stateRevision,
            bgiEpoch = new
            {
                processId = processId ?? JobRegistry.CurrentEpoch.ProcessId,
                startTicksUtc = startTicksUtc ?? JobRegistry.CurrentEpoch.StartTicksUtc
            }
        });

    [Fact]
    public void TaskStop_WithStaleExecutionIdentity_DoesNotCancelSuccessorRoot()
    {
        Guid staleInstanceId;
        long staleRevision;
        using (var staleRoot = ExecutionScope.Start(new(JobKind.Group, "已自然结束的根", JobSource.Ui)))
        {
            var stale = ExecutionScope.GetActiveSnapshot()!;
            staleInstanceId = stale.ExecutionInstanceId;
            staleRevision = stale.StateRevision;
        }

        using var successor = ExecutionScope.Start(new(JobKind.Group, "继任根", JobSource.Ui));
        var response = handler.HandleTaskStop(null!, StoppingRequest(staleInstanceId, staleRevision));

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        Assert.False(successor.Token.IsCancellationRequested);
        Assert.Equal(TaskRunResult.Ran, successor.Result);
        Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
        Assert.Null(CancellationContext.Instance.LastManualCancelAtUtc);
    }

    [Fact]
    public void TaskStop_WithMatchingExecutionIdentity_RequestsStopWithoutClaimingExit()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "定向停止目标", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var request = StoppingRequest(fact.ExecutionInstanceId, fact.StateRevision);

        var response = handler.HandleTaskStop(null!, request);

        Assert.True(response.Success);
        Assert.Equal("stop_requested", response.Data!["status"]!.ToString());
        Assert.False(response.Data["exitConfirmed"]!.ToObject<bool>());
        Assert.True(response.Data["stopRequested"]!.ToObject<bool>());
        Assert.Equal(fact.ExecutionInstanceId.ToString("N"), response.Data["executionInstanceId"]!.ToObject<string>());
        Assert.Equal(fact.StateRevision, response.Data["matchedExecutionStateRevision"]!.ToObject<long>());
        Assert.True(root.Token.IsCancellationRequested);
        Assert.True(ExecutionScope.HasActive); // 请求已登记 ≠ 根已退出
        Assert.False(CancellationContext.Instance.WasCancelled);
        Assert.Null(CancellationContext.Instance.LastManualCancelAtUtc);

        var status = handler.HandleTaskStatus(null!, InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus));
        Assert.False(status.Data!["executionIdle"]!.ToObject<bool>()); // 真实退出前不得报空闲
        Assert.True(status.Data["running"]!.ToObject<bool>());
        Assert.True(status.Data["executionStopRequested"]!.ToObject<bool>());
        Assert.Equal(fact.ExecutionInstanceId.ToString("N"), status.Data["executionInstanceId"]!.ToObject<string>());

        var repeated = handler.HandleTaskStop(null!, request);
        Assert.False(repeated.Success);
        Assert.Equal("not_current", repeated.ErrorCode);
    }

    [Fact]
    public void TaskStop_WithPreviousProcessEpoch_IsRejectedAsStaleEpoch()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "旧纪元停止", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;

        var response = handler.HandleTaskStop(null!, StoppingRequest(
            fact.ExecutionInstanceId, fact.StateRevision,
            startTicksUtc: JobRegistry.CurrentEpoch.StartTicksUtc - 1));

        Assert.False(response.Success);
        Assert.Equal("stale_epoch", response.ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
    }

    [Fact]
    public void TaskStop_WithMalformedExecutionIdentity_IsRejectedAsInvalidRequest()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "形状不合法", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;

        var missingRevision = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });
        var malformedInstance = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = "not-a-guid",
            executionStateRevision = fact.StateRevision,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        var first = handler.HandleTaskStop(null!, missingRevision);
        var second = handler.HandleTaskStop(null!, malformedInstance);

        Assert.False(first.Success);
        Assert.Equal("invalid_request", first.ErrorCode);
        Assert.False(second.Success);
        Assert.Equal("invalid_request", second.ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
    }

    [Fact]
    public async Task TaskStop_WithIdentityWhileSlotBusyWithoutRoot_ReportsIdentityUnavailable()
    {
        Assert.True(await TaskControl.TaskSemaphore.WaitAsync(0));
        try
        {
            Assert.Null(ExecutionScope.GetActiveSnapshot());
            var response = handler.HandleTaskStop(null!, StoppingRequest(Guid.NewGuid(), 7));

            Assert.False(response.Success);
            Assert.Equal("identity_unavailable", response.ErrorCode);
            Assert.False(CancellationContext.Instance.WasCancelled);
        }
        finally
        {
            TaskControl.TaskSemaphore.Release();
        }
    }

    [Fact]
    public void TaskStop_WithoutExecutionIdentity_KeepsLegacyGlobalStopSemantics()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "旧协议停止", JobSource.Ui));
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        config.SuspendedTaskContext = new SuspendedTaskContext { GroupName = "旧现场" };
        var stopVersionBefore = ExecutionScope.StopVersionNow;

        var response = handler.HandleTaskStop(null!, InstanceIpcEnvelope.Request(InstanceOperations.TaskStop));

        Assert.True(response.Success);
        Assert.Equal("stopped", response.Data!["status"]!.ToString());
        Assert.True(root.Token.IsCancellationRequested);
        Assert.Equal(TaskRunResult.Cancelled, root.Result);
        Assert.True(CancellationContext.Instance.WasCancelled);
        Assert.NotNull(CancellationContext.Instance.LastManualCancelAtUtc);
        Assert.True(CancellationContext.Instance.IsInManualStopCooldown(
            InstanceRequestHandler.ManualStopCooldownWindow, out var remainingSeconds));
        Assert.True(remainingSeconds > 29);
        Assert.Equal(stopVersionBefore + 1, ExecutionScope.StopVersionNow); // 旧协议仍是"全部停止"：推进停止水位
        Assert.Null(config.SuspendedTaskContext);                          // 撤销自动恢复意图
        Assert.True(PreemptionGate.Authorize(null));                       // 接管票据已撤销
    }

    [Fact]
    public void TaskStop_WithUnknownExecutionIdentity_ReportsNotCurrentWithoutExitEvidence()
    {
        var response = handler.HandleTaskStop(null!, StoppingRequest(Guid.NewGuid(), 11));

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        Assert.False(ExecutionScope.HasActive);
        Assert.False(CancellationContext.Instance.WasCancelled);
        Assert.Null(CancellationContext.Instance.LastManualCancelAtUtc);
    }

    [Fact]
    public void TaskStop_WithStaleRevision_DoesNotDisturbCurrentRoot()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "修订号过期", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;

        var response = handler.HandleTaskStop(null!, StoppingRequest(fact.ExecutionInstanceId, fact.StateRevision - 1));

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.Equal(TaskRunResult.Ran, root.Result);
        var after = ExecutionScope.GetActiveSnapshot()!;
        Assert.False(after.StopRequested);
        Assert.Equal(fact.StateRevision, after.StateRevision); // 拒绝路径不推进修订号
    }

    [Fact]
    public void TaskStop_RepeatedWithRefreshedRevision_StaysStopRequestedWithoutExitConfirmation()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "重复定向停止", JobSource.Ui));
        var first = ExecutionScope.GetActiveSnapshot()!;
        Assert.True(handler.HandleTaskStop(null!, StoppingRequest(first.ExecutionInstanceId, first.StateRevision)).Success);

        var refreshed = handler.HandleTaskStatus(null!, InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus)).Data!;
        var refreshedRevision = refreshed["executionStateRevision"]!.ToObject<long>();
        Assert.True(refreshedRevision > first.StateRevision);

        var second = handler.HandleTaskStop(null!,
            StoppingRequest(first.ExecutionInstanceId, refreshedRevision));

        Assert.True(second.Success);
        Assert.Equal("stop_requested", second.Data!["status"]!.ToString());
        Assert.False(second.Data["exitConfirmed"]!.ToObject<bool>());
        Assert.True(ExecutionScope.HasActive); // 仍然只是"请求已登记"
    }

    [Fact]
    public void TaskStop_DirectionalRequest_PreservesResumeIntentTicketAndCooldownWindow()
    {
        var ticket = Guid.NewGuid().ToString("N");
        PreemptionGate.Arm(ticket);
        var victim = new SuspendedTaskContext { GroupName = "待恢复现场", TakeoverTicket = ticket };
        config.SuspendedTaskContext = victim;
        var stopVersionBefore = ExecutionScope.StopVersionNow;
        using var admitted = ExecutionScope.UseTakeoverTicket(ticket);
        using var root = ExecutionScope.Start(new(JobKind.Group, "定向不越权", JobSource.Ui, TakeoverTicket: ticket));
        var fact = ExecutionScope.GetActiveSnapshot()!;

        var failure = handler.HandleTaskStop(null!, StoppingRequest(fact.ExecutionInstanceId, fact.StateRevision - 1));
        var success = handler.HandleTaskStop(null!, StoppingRequest(fact.ExecutionInstanceId, fact.StateRevision));

        Assert.Equal("not_current", failure.ErrorCode);
        Assert.True(success.Success);
        Assert.Same(victim, config.SuspendedTaskContext);          // 不清恢复现场
        Assert.True(PreemptionGate.Authorize(ticket));             // 不撤销票据
        Assert.Equal(stopVersionBefore, ExecutionScope.StopVersionNow); // 不推进"全部停止"水位
        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(
            InstanceRequestHandler.ManualStopCooldownWindow, out _)); // 不武装手动停止冷却
    }

    [Theory]
    [InlineData("executionStateRevision")]
    [InlineData("executionInstanceId")]
    public void TaskStop_WithPartialExecutionIdentity_IsRejectedWithoutGlobalStop(string scenario)
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "半截身份", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var data = scenario == "executionInstanceId"
            // 显式 null：字段出现但值不可用，仍必须按形状错误拒绝（不得退回全量停止）
            ? new { executionInstanceId = (string?)null, executionStateRevision = fact.StateRevision }
            : (object)new { executionStateRevision = fact.StateRevision };
        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, data);

        var response = handler.HandleTaskStop(null!, request);

        Assert.False(response.Success);
        Assert.Equal("invalid_request", response.ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
        Assert.Null(CancellationContext.Instance.LastManualCancelAtUtc);
        Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);
    }

    [Fact]
    public void TaskStop_WithWrongTypedRevision_IsRejectedAsInvalidRequest()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "修订号类型错误", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision.ToString(),  // 字符串而非 JSON 整数
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        var response = handler.HandleTaskStop(null!, request);

        Assert.False(response.Success);
        Assert.Equal("invalid_request", response.ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
    }

    // ===== ext 控制面：定向请求不得在身份校验前改变队列 =====

    private static BgiTaskCoordinator CreateQueueOnlyCoordinator(out List<string> queueCancelledEvents)
    {
        var events = new List<string>();
        queueCancelledEvents = events;
        return new BgiTaskCoordinator(
            isSlotFree: () => false,
            publish: (name, _) =>
            {
                if (name == ExternalInterfaceEventNames.TaskQueueCancelled)
                {
                    lock (events) { events.Add(name); }
                }
            },
            logger: NullLogger.Instance,
            slotPollInterval: TimeSpan.FromMilliseconds(10),
            slotWaitTimeout: TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExtTaskStop_WithDirectionalIdentity_DoesNotClearQueueBeforeIdentityCheck()
    {
        using var coordinator = CreateQueueOnlyCoordinator(out var queueCancelledEvents);
        var factoryCalls = 0;
        var submitted = coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            1, "在队组", null, 0, (_, __) => Task.FromResult(false)));
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, submitted.Status);
        Assert.Equal(1, coordinator.QueueDepth);

        var request = InstanceIpcEnvelope.Request("ext.task.stop", new
        {
            executionInstanceId = Guid.NewGuid().ToString("N"),
            executionStateRevision = 3,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        var response = ExternalInterfaceCommandPlane.DispatchTaskStop(
            handler, null!, request, () => { factoryCalls++; return coordinator; });

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        Assert.Equal(0, factoryCalls);                                 // 定向请求连协调器都不取得
        Assert.Equal(1, coordinator.QueueDepth);                       // 被拒绝的定向停止零副作用
        lock (queueCancelledEvents) { Assert.Empty(queueCancelledEvents); }
    }

    [Fact]
    public async Task ExtTaskStop_WithoutIdentity_StillClearsQueueBeforeGlobalStop()
    {
        using var coordinator = CreateQueueOnlyCoordinator(out var queueCancelledEvents);
        var factoryCalls = 0;
        var submitted = coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            1, "在队组", null, 0, (_, __) => Task.FromResult(false)));
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, submitted.Status);

        var response = ExternalInterfaceCommandPlane.DispatchTaskStop(
            handler, null!, InstanceIpcEnvelope.Request("ext.task.stop"),
            () => { factoryCalls++; return coordinator; });

        Assert.True(response.Success);
        Assert.Equal("stopped", response.Data!["status"]!.ToString());
        Assert.Equal(1, factoryCalls);                                 // 只有全量停止路径才取协调器
        Assert.Equal(0, coordinator.QueueDepth);                       // 全量停止语义不变：清空在队项
        lock (queueCancelledEvents)
        {
            Assert.Contains(ExternalInterfaceEventNames.TaskQueueCancelled, queueCancelledEvents);
        }
    }

    [Fact]
    public async Task ExtTaskCancel_WithDirectionalIdentity_LeavesRealQueuedItemUntouched()
    {
        // 真实在队项 + 真实单例：若回归成"先按句柄取消、再校验身份"，在队项会被移除。
        Assert.True(await TaskControl.TaskSemaphore.WaitAsync(0));
        try
        {
            var submitted = BgiTaskCoordinator.Instance.Submit(new BgiTaskCoordinator.TaskSubmission(
                9, "真实在队组", null, 0, (_, __) => Task.FromResult(false)));
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, submitted.Status);
            Assert.Equal(1, BgiTaskCoordinator.Instance.QueueDepth);

            var request = InstanceIpcEnvelope.Request("ext.task.cancel", new
            {
                taskHandle = submitted.TaskHandle.ToString("N"),
                executionInstanceId = Guid.NewGuid().ToString("N"),
                executionStateRevision = 1,
                bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
            });

            var response = await ExternalInterfaceCommandPlane.DispatchAsync(
                handler, null!, request, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Equal("invalid_request", response.ErrorCode);
            Assert.Equal(1, BgiTaskCoordinator.Instance.QueueDepth); // 句柄匹配的在队项必须原样保留
        }
        finally
        {
            BgiTaskCoordinator.Instance.ClearQueue();
            TaskControl.TaskSemaphore.Release();
        }
    }

    [Fact]
    public void TaskStop_WithEpochOnlyLegacyShape_StillPerformsGlobalStop()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "只带纪元元数据", JobSource.Ui));
        var request = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            // 只有公共元数据 bgiEpoch，没有定向身份键 ⇒ 仍是旧协议全量停止
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        var response = handler.HandleTaskStop(null!, request);

        Assert.True(response.Success);
        Assert.Equal("stopped", response.Data!["status"]!.ToString());
        Assert.True(root.Token.IsCancellationRequested);
        Assert.True(CancellationContext.Instance.WasCancelled);
        Assert.NotNull(CancellationContext.Instance.LastManualCancelAtUtc);
    }

    [Fact]
    public void TaskStop_WithMissingOrMalformedEpoch_IsRejectedAsInvalidRequest()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "纪元形状错误", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var missingEpoch = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision
        });
        var malformedEpoch = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision,
            bgiEpoch = "primary"
        });
        var nonIntegerEpochField = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId.ToString(), startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, missingEpoch).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, malformedEpoch).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, nonIntegerEpochField).ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
    }

    [Fact]
    public void TaskStop_WithNonIntegerStartTicksOrOutOfRangeProcessId_IsRejectedAsInvalidRequest()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "纪元子字段越界", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var nonIntegerTicks = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = "now" }
        });
        var pidOutOfRange = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop, new
        {
            executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
            executionStateRevision = fact.StateRevision,
            bgiEpoch = new { processId = (long)int.MaxValue + 5, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, nonIntegerTicks).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, pidOutOfRange).ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
    }

    [Fact]
    public void TaskStop_WithNonStringOrOverflowingIdentityFields_IsRejectedAsInvalidRequest()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "身份形状异常", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var epoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc };
        var guidAsObject = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop,
            new { executionInstanceId = new { value = fact.ExecutionInstanceId.ToString("N") }, executionStateRevision = fact.StateRevision, bgiEpoch = epoch });
        var guidAsNumber = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop,
            new { executionInstanceId = 12345, executionStateRevision = fact.StateRevision, bgiEpoch = epoch });
        var guidAsArray = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop,
            new { executionInstanceId = new[] { 1, 2 }, executionStateRevision = fact.StateRevision, bgiEpoch = epoch });
        var revisionOverflow = InstanceIpcEnvelope.Request(InstanceOperations.TaskStop,
            new
            {
                executionInstanceId = fact.ExecutionInstanceId.ToString("N"),
                executionStateRevision = System.Numerics.BigInteger.Parse("99999999999999999999999999"),
                bgiEpoch = epoch
            });

        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, guidAsObject).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, guidAsNumber).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, guidAsArray).ErrorCode);
        Assert.Equal("invalid_request", handler.HandleTaskStop(null!, revisionOverflow).ErrorCode);
        Assert.False(root.Token.IsCancellationRequested);
        Assert.False(ExecutionScope.GetActiveSnapshot()!.StopRequested);
        Assert.False(CancellationContext.Instance.WasCancelled);
    }

    [Fact]
    public void ExtTaskStop_ProductionWrapper_LeavesCoordinatorUncreatedForDirectionalRequest()
    {
        var coordinatorCreatedBefore = BgiTaskCoordinator.IsCreated;
        var request = InstanceIpcEnvelope.Request("ext.task.stop", new
        {
            executionInstanceId = Guid.NewGuid().ToString("N"),
            executionStateRevision = 2,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        // 生产包装层（不注入工厂）：定向请求不应在分流前取得协调器单例
        var response = ExternalInterfaceCommandPlane.DispatchTaskStop(handler, null!, request);

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        if (!coordinatorCreatedBefore)
        {
            Assert.False(BgiTaskCoordinator.IsCreated);
        }
    }

    [Fact]
    public async Task ExtTaskStop_DirectionalRequestWithMalformedClearQueue_IsStillClassifiedWithoutSideEffects()
    {
        using var coordinator = CreateQueueOnlyCoordinator(out var queueCancelledEvents);
        var factoryCalls = 0;
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            2, "在队组二", null, 0, (_, __) => Task.FromResult(false))).Status);
        var request = InstanceIpcEnvelope.Request("ext.task.stop", new
        {
            clearQueue = new { unexpected = "object" },   // 无关参数的畸形值不得阻断定向分类
            executionInstanceId = Guid.NewGuid().ToString("N"),
            executionStateRevision = 2,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });

        var response = ExternalInterfaceCommandPlane.DispatchTaskStop(
            handler, null!, request, () => { factoryCalls++; return coordinator; });

        Assert.False(response.Success);
        Assert.Equal("not_current", response.ErrorCode);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, coordinator.QueueDepth);
        lock (queueCancelledEvents) { Assert.Empty(queueCancelledEvents); }
    }

    [Fact]
    public async Task ExtTaskStop_LegacyWithClearQueueFalse_KeepsQueueAndDoesNotTakeCoordinator()
    {
        using var coordinator = CreateQueueOnlyCoordinator(out _);
        var factoryCalls = 0;
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            3, "在队组三", null, 0, (_, __) => Task.FromResult(false))).Status);
        var request = InstanceIpcEnvelope.Request("ext.task.stop", new { clearQueue = false });

        var response = ExternalInterfaceCommandPlane.DispatchTaskStop(
            handler, null!, request, () => { factoryCalls++; return coordinator; });

        Assert.True(response.Success);
        Assert.Equal("stopped", response.Data!["status"]!.ToString());
        Assert.Equal(0, factoryCalls);        // clearQueue=false 的旧语义：不触碰协调器
        Assert.Equal(1, coordinator.QueueDepth);
    }
}
