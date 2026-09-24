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
        ExecutionExitLedger.ResetForTest();
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

    // ===== R5 A4 第二步：退出凭证（反例先行）=====
    // 语义：停止请求登记 ≠ 退出；只有执行根生命周期真实结束才产生**一次性**凭证，
    // 且凭证只按执行身份匹配——不得由 executionIdle 反推，不得跨代复用。

    private static InstanceIpcEnvelope ExitQueryRequest(Guid instanceId, int? processId = null, long? startTicksUtc = null)
        => InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus, new
        {
            executionInstanceId = instanceId.ToString("N"),
            bgiEpoch = new
            {
                processId = processId ?? JobRegistry.CurrentEpoch.ProcessId,
                startTicksUtc = startTicksUtc ?? JobRegistry.CurrentEpoch.StartTicksUtc
            }
        });

    [Fact]
    public void TaskStatus_StopRequestedButRootNotExited_ReportsNotExitedInsteadOfConfirmed()
    {
        using var root = ExecutionScope.Start(new(JobKind.Group, "未退出的根", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        Assert.True(ExecutionScope.TryRequestPreempt(fact.ExecutionInstanceId, fact.StateRevision));
        Assert.True(ExecutionScope.HasActive); // 请求已登记，根尚未退出

        var data = handler.HandleTaskStatus(null!,
            ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.False(data["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("not_exited", data["executionExitReason"]!.ToString());
        Assert.False(data["executionIdle"]!.ToObject<bool>()); // 退出前不得报空闲
        Assert.True(data["executionStopRequested"]!.ToObject<bool>());
    }

    [Fact]
    public void TaskStatus_AfterRealRootExit_ConfirmsExitForThatInstanceOnly()
    {
        var root = ExecutionScope.Start(new(JobKind.Group, "真实退出的根", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Cancel();
        root.Dispose(); // 生命周期真实结束

        var confirmed = handler.HandleTaskStatus(null!,
            ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
        Assert.True(confirmed["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("confirmed", confirmed["executionExitReason"]!.ToString());
        Assert.Equal("Preempted", confirmed["executionExitResult"]!.ToString());
        Assert.NotNull(confirmed["executionExitAtUtc"]!.ToObject<DateTime?>());
        Assert.True(confirmed["executionExitObservedOutcome"]!.ToObject<bool>());
        Assert.True(confirmed["executionExitStopRequested"]!.ToObject<bool>());
        Assert.Equal("cancel_requested", confirmed["executionExitStopSource"]!.ToString()); // 来源归属，不是默认 StopReason
        Assert.True(confirmed["executionExitOrder"]!.ToObject<long>() > 0);
        Assert.Equal(fact.ExecutionInstanceId.ToString("N"),
            confirmed["executionExitQueryInstanceId"]!.ToObject<string>());

        var other = handler.HandleTaskStatus(null!,
            ExitQueryRequest(Guid.NewGuid())).Data!;
        Assert.False(other["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("unknown_instance", other["executionExitReason"]!.ToString());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_DoesNotCrossGenerations()
    {
        var first = ExecutionScope.Start(new(JobKind.Group, "第一代", JobSource.Ui));
        var firstFact = ExecutionScope.GetActiveSnapshot()!;
        first.Dispose();

        using (var second = ExecutionScope.Start(new(JobKind.Group, "第二代", JobSource.Ui)))
        {
            var secondFact = ExecutionScope.GetActiveSnapshot()!;
            var forSecond = handler.HandleTaskStatus(null!,
                ExitQueryRequest(secondFact.ExecutionInstanceId)).Data!;

            Assert.False(forSecond["executionExitConfirmed"]!.ToObject<bool>()); // 旧代凭证不得证明新代
            Assert.Equal("not_exited", forSecond["executionExitReason"]!.ToString());
            var forFirst = handler.HandleTaskStatus(null!,
                ExitQueryRequest(firstFact.ExecutionInstanceId)).Data!;
            Assert.True(forFirst["executionExitConfirmed"]!.ToObject<bool>()); // 第一代确实退出过：如实回答
        }

        // 第二代退出后单槽被覆盖：旧代查询返回"无凭证"（保守，不伪造成立也不伪造失败）
        var afterSecondExit = handler.HandleTaskStatus(null!,
            ExitQueryRequest(firstFact.ExecutionInstanceId)).Data!;
        Assert.False(afterSecondExit["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("unknown_instance", afterSecondExit["executionExitReason"]!.ToString());
    }

    [Fact]
    public void TaskStatus_ExitQuery_WithoutIdentityOrWithStaleEpoch_NeverClaimsExit()
    {
        // 完全空闲：executionIdle 可以为 true，但退出凭证不得因此成立
        var idleData = handler.HandleTaskStatus(null!,
            InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus)).Data!;
        Assert.True(idleData["executionIdle"]!.ToObject<bool>());
        Assert.False(idleData["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("identity_required", idleData["executionExitReason"]!.ToString());

        var root = ExecutionScope.Start(new(JobKind.Group, "旧纪元退出查询", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();

        var staleEpoch = handler.HandleTaskStatus(null!,
            ExitQueryRequest(fact.ExecutionInstanceId,
                startTicksUtc: JobRegistry.CurrentEpoch.StartTicksUtc - 1)).Data!;
        Assert.False(staleEpoch["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal("stale_epoch", staleEpoch["executionExitReason"]!.ToString());
    }

    [Fact]
    public void ExecutionExitLedger_SingleSlot_MatchesOnlySameExecutionInstance()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(ExecutionExitLedger.Record(new ExecutionExitReceipt(
            JobRegistry.CurrentEpoch.ProcessId, JobRegistry.CurrentEpoch.StartTicksUtc,
            first, Guid.NewGuid(), null, "台账A", "Group", "Ui",
            ObservedOutcome: true, Result: TaskRunResult.Ran, StopRequested: false, StopAttribution: null,
            StopVersion: 0, ExitedAtUtc: DateTime.UtcNow, SlotObservedFree: true, Order: 10,
            DescendantScanAvailable: true, OutstandingRegisteredDescendantJobIds: Array.Empty<Guid>())));
        Assert.True(ExecutionExitLedger.TryGet(first, out var firstReceipt));
        Assert.True(firstReceipt!.SlotObservedFree); // 槽状态是释放后采样值，如实记录

        Assert.True(ExecutionExitLedger.Record(firstReceipt with { ExecutionInstanceId = second, Order = 11 }));
        Assert.False(ExecutionExitLedger.TryGet(first, out _)); // 单槽覆盖：旧代不再有凭证
        Assert.True(ExecutionExitLedger.TryGet(second, out var secondReceipt));
        Assert.True(secondReceipt!.Order > firstReceipt.Order); // 顺序号单调

        // 顺序守卫：迟到但顺序更小的旧记录不得覆盖更晚的退出事实
        Assert.False(ExecutionExitLedger.Record(firstReceipt with { Order = 9 }));
        Assert.True(ExecutionExitLedger.TryGet(second, out var stillSecond));
        Assert.Equal(11, stillSecond!.Order);
        Assert.False(ExecutionExitLedger.TryGet(first, out _));
    }

    [Fact]
    public void ExecutionScope_AdmissionCallbackReleasingRoot_DoesNotDeliverDeadScopeNorRecordReceipt()
    {
        Guid instanceId = Guid.Empty;
        var start = () => ExecutionScope.Start(new JobDescriptor(JobKind.Group, "接纳期被释放", JobSource.Ui,
            OnAdmitted: () =>
            {
                instanceId = ExecutionScope.Current!.ExecutionInstanceId;
                ExecutionScope.Current!.Dispose(); // 接纳回调里释放本根
            }));

        var ex = Assert.Throws<InvalidOperationException>(start);
        Assert.Contains("task_busy", ex.Message);
        Assert.False(ExecutionScope.HasActive);

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(instanceId)).Data!;
        Assert.False(data["executionExitConfirmed"]!.ToObject<bool>()); // 从未交付使用的根不留退出凭证
    }

    [Fact]
    public void ExecutionScope_AdmissionCallbackThrowing_DoesNotRecordReceiptNorDisturbPreviousOne()
    {
        var witness = ExecutionScope.Start(new(JobKind.Group, "既有退出事实", JobSource.Ui));
        var witnessFact = ExecutionScope.GetActiveSnapshot()!;
        witness.Dispose();

        Assert.Throws<InvalidOperationException>(() => ExecutionScope.Start(new JobDescriptor(
            JobKind.Group, "接纳回调抛异常", JobSource.Ui,
            OnAdmitted: () => throw new InvalidOperationException("admission_failed"))));
        Assert.False(ExecutionScope.HasActive);

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(witnessFact.ExecutionInstanceId)).Data!;
        Assert.True(data["executionExitConfirmed"]!.ToObject<bool>()); // 既有凭证不被未接纳的根挤掉
    }

    [Fact]
    public void ExecutionScope_AdmissionCallbackReleasingRootInInheritedContext_DoesNotLeaveAmbientDangling()
    {
        // 子任务继承 AsyncLocal 并释放同一颗根：Dispose 只在**子上下文**恢复 Ambient，
        // 拒绝交付的路径必须把父上下文的悬挂引用一并恢复，否则父上下文会一直指向已释放的根。
        var start = () => ExecutionScope.Start(new JobDescriptor(JobKind.Group, "继承上下文释放", JobSource.Ui,
            OnAdmitted: () => Task.Run(() => ExecutionScope.Current!.Dispose()).GetAwaiter().GetResult()));

        Assert.Throws<InvalidOperationException>(start);

        Assert.Null(ExecutionScope.Current); // 父上下文不得残留已释放的根
        Assert.False(ExecutionScope.HasActive);
    }

    [Fact]
    public void TaskStatus_ExitQuery_WithMalformedIdentityShape_ReportsInvalidIdentityNotFailure()
    {
        var guidAsObject = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus, new
        {
            executionInstanceId = new { value = Guid.NewGuid().ToString("N") },
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });
        var epochAsString = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus, new
        {
            executionInstanceId = Guid.NewGuid().ToString("N"),
            bgiEpoch = "primary"
        });
        var epochFieldAsString = InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus, new
        {
            executionInstanceId = Guid.NewGuid().ToString("N"),
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId.ToString(), startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc }
        });
        // 显式 null 必须按**线上形状**构造：匿名对象经 NullValueHandling.Ignore 序列化会把 null 字段整条丢掉，
        // 那样测的是"缺字段"而不是"字段存在但为 null"（后者才是 fail-closed 要覆盖的线上情形）。
        var explicitNullIdentity = new InstanceIpcEnvelope
        {
            Operation = InstanceOperations.TaskStatus,
            Data = new Newtonsoft.Json.Linq.JObject
            {
                ["executionInstanceId"] = Newtonsoft.Json.Linq.JValue.CreateNull(),
                ["bgiEpoch"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["processId"] = JobRegistry.CurrentEpoch.ProcessId,
                    ["startTicksUtc"] = JobRegistry.CurrentEpoch.StartTicksUtc
                }
            }
        };
        Assert.True(explicitNullIdentity.Data!.ContainsKey("executionInstanceId"));

        foreach (var request in new[] { guidAsObject, epochAsString, epochFieldAsString, explicitNullIdentity })
        {
            var response = handler.HandleTaskStatus(null!, request);
            Assert.True(response.Success); // 形状错误不得落进通用 task_status_failed
            Assert.False(response.Data!["executionExitConfirmed"]!.ToObject<bool>());
            Assert.Equal("invalid_identity", response.Data["executionExitReason"]!.ToString());
        }
    }

    [Fact]
    public void TaskStatus_ExitReceipt_ReportsQueryIdentitySeparatelyFromActiveRoot()
    {
        var finished = ExecutionScope.Start(new(JobKind.Group, "已退出根", JobSource.Ui));
        var finishedFact = ExecutionScope.GetActiveSnapshot()!;
        finished.Cancel();
        finished.Dispose();

        using var current = ExecutionScope.Start(new(JobKind.Group, "当前活动根", JobSource.Ui));
        var currentFact = ExecutionScope.GetActiveSnapshot()!;
        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(finishedFact.ExecutionInstanceId)).Data!;

        Assert.True(data["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal(finishedFact.ExecutionInstanceId.ToString("N"),
            data["executionExitQueryInstanceId"]!.ToObject<string>());
        Assert.Equal(currentFact.ExecutionInstanceId.ToString("N"),
            data["executionInstanceId"]!.ToObject<string>()); // 两个身份字段必须分开读，不得混用
        Assert.False(data["executionIdle"]!.ToObject<bool>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_WithoutObservedOutcome_DoesNotPublishResultOrStopReason()
    {
        var root = ExecutionScope.Start(new(JobKind.Group, "无终态观测", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose(); // 既未取消也未观察结果：默认值不是事实

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.True(data["executionExitConfirmed"]!.ToObject<bool>());
        Assert.False(data["executionExitObservedOutcome"]!.ToObject<bool>());
        Assert.Null(data["executionExitResult"]?.ToObject<string>());
        Assert.False(data["executionExitStopRequested"]!.ToObject<bool>());
        Assert.Null(data["executionExitStopSource"]?.ToObject<string>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_StopSourceReflectsAttributedEntry_NotDefaultStopReason()
    {
        // 定向停止：来源应为 directional_stop_requested，而不是默认的 CancelledUser（伪事实）
        var preempted = ExecutionScope.Start(new(JobKind.Group, "定向来源", JobSource.Ui));
        var preemptedFact = ExecutionScope.GetActiveSnapshot()!;
        Assert.True(ExecutionScope.TryRequestPreempt(preemptedFact.ExecutionInstanceId, preemptedFact.StateRevision));
        preempted.Dispose();
        var preemptData = handler.HandleTaskStatus(null!, ExitQueryRequest(preemptedFact.ExecutionInstanceId)).Data!;
        Assert.Equal("directional_stop_requested", preemptData["executionExitStopSource"]!.ToString());

        // 手动停止（F11/停止热键语义）：来源应为 manual_stop
        var manual = ExecutionScope.Start(new(JobKind.Group, "手动来源", JobSource.Ui));
        var manualFact = ExecutionScope.GetActiveSnapshot()!;
        CancellationContext.Instance.ManualCancel();
        manual.Dispose();
        var manualData = handler.HandleTaskStatus(null!, ExitQueryRequest(manualFact.ExecutionInstanceId)).Data!;
        Assert.Equal("manual_stop", manualData["executionExitStopSource"]!.ToString());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_RepeatedDispose_DoesNotBumpOrder()
    {
        var root = ExecutionScope.Start(new(JobKind.Group, "重复释放", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();
        var first = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
        var firstOrder = first["executionExitOrder"]!.ToObject<long>();

        root.Dispose(); // 幂等：不得再产生一条凭证

        var second = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
        Assert.Equal(firstOrder, second["executionExitOrder"]!.ToObject<long>());
    }

    // ===== R5 批次 7：已登记派生作业的退出证据（**不覆盖**未登记叶子/逃逸任务）=====

    [Fact]
    public void TaskStatus_ExitReceipt_ReportsOutstandingRegisteredDescendants_AndDoesNotClaimLeafExit()
    {
        _ = JobRegistry.Instance; // 确保注册表可用（生产通常已创建）
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "带派生作业的根", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;

        // 已登记的派生子作业（ParentJobId 链可达）且未终局
        var child = JobRegistry.Instance.Submit(JobKind.Solo, "派生叶子", JobSource.OneDragonInternal,
            parentJobId: rootJobId, identity: new JobExecutionIdentity(runId, "node-child", 0)).Job;
        root.Dispose();

        var first = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
        Assert.True(first["executionExitConfirmed"]!.ToObject<bool>());
        Assert.Equal(1, first["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>());
        Assert.Equal(1, first["executionExitRegisteredSameRunDescendantsStillOpenNow"]!.ToObject<int>());

        // 派生作业终局后：仍在"退出时未终局"名单里，但当前已全部终局
        Assert.True(JobRegistry.Instance.TryMarkTerminal(child.JobId, JobState.Succeeded));
        var second = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;
        Assert.Equal(1, second["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>());
        Assert.Equal(0, second["executionExitRegisteredSameRunDescendantsStillOpenNow"]!.ToObject<int>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_WithoutRegisteredDescendants_IsNotEvidenceAboutUnregisteredLeaves()
    {
        _ = JobRegistry.Instance;
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "无登记派生的根", JobSource.Ui,
            JobId: Guid.NewGuid(), WorkflowRunId: Guid.NewGuid()));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        // 注册表证据：没有未终局的已登记派生作业（**这不等于**"所有叶子/逃逸任务都已退出"）
        Assert.Equal(0, data["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>());
        Assert.Equal(0, data["executionExitRegisteredSameRunDescendantsStillOpenNow"]!.ToObject<int>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_RunIdentitylessJobsAreOutOfScope_AndDoNotPoisonSameRunVerdict()
    {
        _ = JobRegistry.Instance;
        // run 身份缺失、父链缺失的无关作业（典型遗留形态）：**不在证据范围**，不得让本次同 run 结论降级为不可判定
        var leftover = JobRegistry.Instance.Submit(JobKind.Solo, "无身份遗留作业", JobSource.OneDragonInternal,
            parentJobId: Guid.NewGuid()).Job;
        Assert.Null(JobRegistry.Instance.Query(leftover.JobId)!.WorkflowRunId);

        var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "同 run 无派生", JobSource.Ui,
            JobId: Guid.NewGuid(), WorkflowRunId: Guid.NewGuid()));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Equal(0, data["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>());
        Assert.True(JobRegistry.Instance.TryMarkTerminal(leftover.JobId, JobState.Succeeded)); // 清理
    }

    [Fact]
    public void TaskStatus_ExitReceipt_WithoutRunIdentity_ReportsUnknownInsteadOfZero()
    {
        _ = JobRegistry.Instance;
        // 有根作业 ID 但**没有 run 身份**（`WorkflowRunId` 为空）⇒ 无法把派生作业与无关作业区分 ⇒ 不可判定
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "无 run 身份", JobSource.Ui, JobId: Guid.NewGuid()));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Null(data["executionExitRegisteredSameRunDescendantsAtExit"]?.ToObject<int?>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_ReachesSameRunDescendantThroughIdentitylessAncestor()
    {
        _ = JobRegistry.Instance;
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "穿过无身份祖先", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        // 反例形态：根 → A(无 run 身份、仍在表) → B(同一 run、未终局)。
        // 按 run 过滤收集会漏掉 B；按**可达性**收集必须把它算作存活派生。
        var a = JobRegistry.Instance.Submit(JobKind.Solo, "无身份中间节点", JobSource.OneDragonInternal,
            parentJobId: rootJobId).Job;
        var b = JobRegistry.Instance.Submit(JobKind.Solo, "同 run 存活孙作业", JobSource.OneDragonInternal,
            parentJobId: a.JobId, identity: new JobExecutionIdentity(runId, "node-b2", 0)).Job;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        // 可达的非终局派生子作业 ≥1（本例为 A 与 B，两者均未终局）
        Assert.True(data["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>() >= 1);
        Assert.True(JobRegistry.Instance.TryMarkTerminal(a.JobId, JobState.Succeeded)); // 清理
        Assert.True(JobRegistry.Instance.TryMarkTerminal(b.JobId, JobState.Succeeded));
    }

    [Fact]
    public void TaskStatus_ExitReceipt_WithoutRootJobId_ReportsUnknownInsteadOfZero()
    {
        _ = JobRegistry.Instance;
        // 根没有作业 ID ⇒ 无法按 ParentJobId 定界 ⇒ 三个字段都必须是"不可判定"（null），不得报 0/0/true
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "无根作业 ID", JobSource.Ui));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Null(data["executionExitRegisteredSameRunDescendantsAtExit"]?.ToObject<int?>());
        Assert.Null(data["executionExitRegisteredSameRunDescendantsStillOpenNow"]?.ToObject<int?>());
    }

    [Fact]
    public void TaskStatus_ExitReceipt_DetectsDeepOutstandingDescendant()
    {
        _ = JobRegistry.Instance;
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "深层派生", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        var child = JobRegistry.Instance.Submit(JobKind.Solo, "中间节点", JobSource.OneDragonInternal,
            parentJobId: rootJobId, identity: new JobExecutionIdentity(runId, "node-mid", 0)).Job;
        var grandChild = JobRegistry.Instance.Submit(JobKind.Solo, "孙作业", JobSource.OneDragonInternal,
            parentJobId: child.JobId, identity: new JobExecutionIdentity(runId, "node-grand", 0)).Job;
        Assert.True(JobRegistry.Instance.TryMarkTerminal(child.JobId, JobState.Succeeded)); // 中间节点已终局，孙作业仍未终局
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Equal(1, data["executionExitRegisteredSameRunDescendantsAtExit"]!.ToObject<int>());
        Assert.Equal(1, data["executionExitRegisteredSameRunDescendantsStillOpenNow"]!.ToObject<int>());
        Assert.True(JobRegistry.Instance.TryMarkTerminal(grandChild.JobId, JobState.Succeeded)); // 清理，避免污染其它用例
    }

    [Fact]
    public void TaskStatus_ExitReceipt_BrokenParentChainIsUnknownNotZero()
    {
        _ = JobRegistry.Instance;
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "父链断裂", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        // 同一 run 内、未终局、父 ID 不存在且不是本次的根 ⇒ 父节点可能已被淘汰 ⇒ 证据不可用
        var orphan = JobRegistry.Instance.Submit(JobKind.Solo, "孤儿派生", JobSource.OneDragonInternal,
            parentJobId: Guid.NewGuid(),
            identity: new JobExecutionIdentity(runId, "node-orphan", 0)).Job;
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Null(data["executionExitRegisteredSameRunDescendantsAtExit"]?.ToObject<int?>());
        Assert.True(JobRegistry.Instance.TryMarkTerminal(orphan.JobId, JobState.Succeeded)); // 清理污染
    }

    [Fact]
    public void TaskStatus_ExitReceipt_EvictedTerminalAncestorWithLiveGrandchildIsUnknown()
    {
        _ = JobRegistry.Instance;
        var rootJobId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var root = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "终局祖先被淘汰", JobSource.Ui,
            JobId: rootJobId, WorkflowRunId: runId));
        var fact = ExecutionScope.GetActiveSnapshot()!;
        // 形态：根 → A(未登记/已淘汰) → B(终局、仍在表中) → C(未终局)。
        // B 的父 A 缺失且不是本次根 ⇒ 链不完整 ⇒ 证据不可用（不得报 0/0/true）。
        var b = JobRegistry.Instance.Submit(JobKind.Solo, "终局中间节点", JobSource.OneDragonInternal,
            parentJobId: Guid.NewGuid(), identity: new JobExecutionIdentity(runId, "node-b", 0)).Job;
        var c = JobRegistry.Instance.Submit(JobKind.Solo, "存活孙作业", JobSource.OneDragonInternal,
            parentJobId: b.JobId, identity: new JobExecutionIdentity(runId, "node-c", 0)).Job;
        Assert.True(JobRegistry.Instance.TryMarkTerminal(b.JobId, JobState.Succeeded));
        root.Dispose();

        var data = handler.HandleTaskStatus(null!, ExitQueryRequest(fact.ExecutionInstanceId)).Data!;

        Assert.Null(data["executionExitRegisteredSameRunDescendantsAtExit"]?.ToObject<int?>());
        Assert.True(JobRegistry.Instance.TryMarkTerminal(c.JobId, JobState.Succeeded)); // 清理污染
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
