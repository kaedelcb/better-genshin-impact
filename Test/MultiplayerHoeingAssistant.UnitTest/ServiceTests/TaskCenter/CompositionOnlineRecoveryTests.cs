using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 实际Host/Runner登记联机接管、收票据、绑定恢复作业并观察终态。
/// BGI作业端口受控，不连接游戏；不是用普通Pause/Resume冒充联机恢复。
/// </summary>
public sealed class CompositionOnlineRecoveryTests
{
    [Fact]
    public async Task HostShutdownWithdrawsRetainedScopesEvenAfterOnlineDriverHasParked()
    {
        var timer = StartupStepKinds.Create(StartupStepKinds.TimerTrigger);
        timer.TriggerScope = "round"; timer.TriggerTime = DateTime.Now.AddHours(1).ToString("HH:mm");
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 10;
        var document = new WorkflowDocument { Name = "关闭时清理保留监视", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Steps = [timer, wait] } }] };
        await using var f = new Fixture(false, document);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.FlowRuntime.DelayAsync = (duration, ct) =>
        {
            if (duration <= TimeSpan.FromMilliseconds(100)) entered.TrySetResult();
            return Task.Delay(duration, ct);
        };
        Assert.True((await f.Host.StartWorkflowAsync(document.WorkflowId!)).Ok);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Host.CloseOnlineHoeingSendAsync();
        await Until(() => !f.Host.IsDriving(document.WorkflowId!));
        Assert.Single(f.Host.FlowRuntime.List());
        await f.Host.ShutdownAsync();
        Assert.Empty(f.Host.FlowRuntime.List());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SelectedLaneKeepsItsChoiceAndCompletedPrefixAcrossRealOnlineHandoff(bool legacy, bool missingResume)
    {
        await using var f = new Fixture(legacy);
        await f.StartAsync();
        var original = await f.SuspendB2Async();
        Assert.Equal(new[] { "B1", "B2" }, f.Body.Submitted.ToArray());
        Assert.Equal("B2", original.Cursor!.NodeId);
        Assert.Equal("branchYes", original.NodeOutcomes.Single(o => o.NodeId == f.DecisionOwner).Result);
        Assert.Equal("succeeded", original.NodeOutcomes.Single(o => o.NodeId == "B1").Result);

        // 联机期间寄存器发生变化；返回时绝不能重新判断而误走C。
        f.Registers.Set(new() { Values = Selection(0) });
        var resume = await f.Host.PreparePlanResumeAsync(original.RunId, Fixture.Ticket);
        Assert.NotNull(resume);
        Assert.Equal("B2", resume!["nodeId"]);
        Assert.Equal(original.CurrentSubmission!.JobId, resume["originalJobId"]);
        Assert.Equal(2, resume["resumeAttempt"]);
        var resumedJob = f.Port.SetResume(original, resume, missingResume);
        Assert.True(f.Host.AcceptPlanResumeJob(original.RunId, Fixture.Ticket, resumedJob));
        Assert.True(f.Host.AcceptPlanResumeJob(original.RunId, Fixture.Ticket, resumedJob));
        Assert.False(f.Host.AcceptPlanResumeJob(original.RunId, Fixture.Ticket, Guid.NewGuid().ToString("D")));
        await f.Host.OpenOnlineHoeingSendAsync();
        var finished = await f.FinishedAsync();

        Assert.Equal(missingResume ? WorkflowRunState.Unknown : WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(missingResume ? new[] { "B1", "B2" } : new[] { "B1", "B2", "B3", "D" }, f.Body.Submitted.ToArray());
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == f.DecisionOwner));
        Assert.Equal("branchYes", finished.NodeOutcomes.Single(o => o.NodeId == f.DecisionOwner).Result);
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "B1" && o.Result == "succeeded"));
        Assert.DoesNotContain(finished.NodeOutcomes, o => o.NodeId == "C");
        Assert.Equal(0, f.Port.Sends); // 恢复作业是外部已受理身份，宿主只观察、不二次派发。
        if (missingResume)
        {
            Assert.DoesNotContain(finished.NodeOutcomes, o => o.NodeId is "B3" or "D");
            return;
        }
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "D" && o.Result == "succeeded"));
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Contains(finished.SubmissionHistory, s => s.NodeId == "B2" && s.Attempt == 1 && s.JobId == original.CurrentSubmission.JobId);
        Assert.True(f.Port.Queries >= 2, "业务成功而执行退出未确认时，不能执行B3。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLaneMemberEntryKeepsItsTailAcrossRealOnlineHandoff(bool legacy)
    {
        await using var f = new Fixture(legacy);
        var snapshot = f.Host.Workflows.LoadSnapshot(f.Document.WorkflowId!);
        snapshot.Document.ExtensionData ??= new();
        snapshot.Document.ExtensionData["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "B1", once = true });
        f.Host.Workflows.Save(snapshot.Document, snapshot.Revision);

        await f.StartAsync(entryNodeId: "B2");
        var original = await f.SuspendB2Async();
        Assert.Equal("B2", original.ExplicitEntryNodeId);
        Assert.Equal(new[] { "B2" }, f.Body.Submitted.ToArray());
        Assert.Equal("B2", original.Cursor!.NodeId);
        Assert.DoesNotContain(original.NodeOutcomes, o => o.NodeId == f.DecisionOwner || o.NodeId is "B1" or "C");

        // 这里明确选择B2即明确选择B线；联机期间的寄存器变化及旧入口水位均不能重选路线。
        f.Registers.Set(new() { Values = Selection(0) });
        Assert.Equal("B1", f.Host.Workflows.LoadSnapshot(f.Document.WorkflowId!).Document.ExtensionData!["watermark"]
            .GetProperty("entryNodeId").GetString());
        var resume = await f.Host.PreparePlanResumeAsync(original.RunId, Fixture.Ticket);
        Assert.NotNull(resume);
        Assert.Equal("B2", resume!["nodeId"]);
        Assert.Equal(original.CurrentSubmission!.JobId, resume["originalJobId"]);
        var resumedJob = f.Port.SetResume(original, resume, missing: false);
        Assert.True(f.Host.AcceptPlanResumeJob(original.RunId, Fixture.Ticket, resumedJob));
        await f.Host.OpenOnlineHoeingSendAsync();
        var finished = await f.FinishedAsync();

        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal("B2", finished.ExplicitEntryNodeId);
        Assert.Null(finished.MigrationEntrySeedKey);
        Assert.Equal(new[] { "B2", "B3", "D" }, f.Body.Submitted.ToArray());
        Assert.DoesNotContain(finished.NodeOutcomes, o => o.NodeId == f.DecisionOwner || o.NodeId is "B1" or "C");
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "B2" && o.Result == "succeeded"));
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "D" && o.Result == "succeeded"));
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Contains(finished.SubmissionHistory, s => s.NodeId == "B2" && s.Attempt == 1 && s.JobId == original.CurrentSubmission.JobId);
        Assert.Equal(0, f.Port.Sends); // 外部恢复作业只按原身份观察，不能再次提交B2。
        Assert.True(f.Port.Queries >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStopWhileInOnlineHandoffDoesNotReopenEitherLane(bool legacy)
    {
        await using var f = new Fixture(legacy);
        await f.StartAsync();
        var parked = await f.SuspendB2Async();
        Assert.True((await f.Host.RequestRunActionAsync(parked.RunId, WorkflowRunAction.Stop)).Ok);
        var stopped = await f.FinishedAsync();
        Assert.True(stopped.StopRequested);
        Assert.Contains(stopped.State, new[] { WorkflowRunState.Cancelled, WorkflowRunState.Unknown });
        Assert.False(f.Host.AcceptPlanResumeJob(parked.RunId, Fixture.Ticket, Guid.NewGuid().ToString("D")));
        await f.Host.OpenOnlineHoeingSendAsync();
        await Task.Delay(50);
        Assert.False(f.Host.IsDriving(f.Document.WorkflowId!));
        Assert.Equal(new[] { "B1", "B2" }, f.Body.Submitted.ToArray());
        Assert.DoesNotContain(f.Host.Runs.Load(parked.RunId)!.NodeOutcomes, o => o.NodeId is "B3" or "C" or "D");
        Assert.Equal(0, f.Port.Sends);
    }

    private static List<int?> Selection(int value)
    { var values = RegisterBank.EmptySelection(); values[0] = value; return values; }

    [Theory]
    [InlineData("onlineOnly")]
    [InlineData("beforeOnline")]
    [InlineData("firstResumedLogBeforeAck")]
    public async Task TaskScopedLogExcludesOnlineSegmentAndObservesFirstResumedLogBeforeAck(string hitPhase)
    {
        var trigger = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        trigger.TriggerScope = "task"; trigger.TargetNodeId = "B2"; trigger.LogKeyword = "今天执行B线";
        trigger.WatchRepeat = false;
        trigger.BeforeAttemptSteps = [new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Selection(0) } }];
        trigger.FireSteps = [new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Selection(1) } }];
        var condition = StartupStepKinds.Create(StartupStepKinds.JudgeRegister);
        condition.RegisterCondition = new() { Values = Selection(1) };
        var doc = new WorkflowDocument
        {
            Name = "监听跨联机恢复", Activation = new() { Status = "active" },
            Nodes = [
                new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [trigger] } },
                new() { NodeId = "B2", Kind = "resource.configGroup", Ref = new() { Config = "A狗粮收集", Revision = "rev-1" } },
                new() { NodeId = "lane", Kind = "control.lanes", Lanes = new()
                {
                    Decision = new() { Steps = [condition], ExitStepId = condition.Id },
                    YesNodeIds = ["B3"], NoNodeIds = ["C"]
                } },
                new() { NodeId = "B3", Kind = "resource.configGroup", Ref = new() { Config = "B线", Revision = "rev-1" } },
                new() { NodeId = "C", Kind = "resource.configGroup", Ref = new() { Config = "C线", Revision = "rev-1" } },
                new() { NodeId = "D", Kind = "resource.configGroup", Ref = new() { Config = "收尾", Revision = "rev-1" } }
            ]
        };
        await using var f = new Fixture(false, doc);
        var source = new SegmentedLogSource();
        f.Host.FlowRuntime.ObservationSource = source;
        await f.StartAsync();
        Assert.Equal(0, f.Registers.GetSnapshot().Entries[0].Value);
        if (hitPhase == "beforeOnline")
        {
            source.Hit();
            await Until(() => f.Registers.GetSnapshot().Entries[0].Value == 1);
        }
        var original = await f.SuspendB2Async();
        var beforePrepare = f.Registers.GetSnapshot().Entries[0].Value;
        source.Hit(); // 同一关键字由联机锄地输出，不属于原A观察窗口。
        await Task.Delay(50);
        Assert.Equal(beforePrepare, f.Registers.GetSnapshot().Entries[0].Value);
        var resume = await f.Host.PreparePlanResumeAsync(original.RunId, Fixture.Ticket);
        Assert.NotNull(resume);
        var job = f.Port.SetResume(original, resume!, missing: false);
        Assert.Equal(beforePrepare, f.Registers.GetSnapshot().Entries[0].Value); // 续跑不是新尝试，不能重放清零初始化。
        if (hitPhase == "firstResumedLogBeforeAck")
        {
            // 受控BGI已经开始原A续跑，首日志先于恢复ACK返回；普通新任务发送门仍关闭。
            Assert.True(f.SendClosed);
            source.Hit();
            await Until(() => f.Registers.GetSnapshot().Entries[0].Value == 1);
        }
        Assert.True(f.Host.AcceptPlanResumeJob(original.RunId, Fixture.Ticket, job));
        // 沿Main的实际次序，先确认A续跑终态/写入，再重新打开一般任务发送。
        await Until(() => f.Host.Runs.Load(original.RunId)!.NodeOutcomes.Any(o => o.NodeId == "B2" && o.Result == "succeeded"),
            () => f.Host.Runs.Load(original.RunId)!.State + ": " + f.Host.Runs.Load(original.RunId)!.Note);
        await Until(() => !f.Host.IsDriving(doc.WorkflowId!) && f.Host.Runs.Load(original.RunId)!.State == WorkflowRunState.LocalWaitParking,
            () => f.Host.Runs.Load(original.RunId)!.State + ": " + f.Host.Runs.Load(original.RunId)!.Note);
        Assert.True(f.SendClosed);
        Assert.Equal(new[] { "B2" }, f.Body.Submitted.ToArray());
        await f.Host.OpenOnlineHoeingSendAsync();
        var finished = await f.FinishedAsync();
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        var expectedHit = hitPhase != "onlineOnly";
        Assert.Equal(new[] { "B2", expectedHit ? "B3" : "C", "D" }, f.Body.Submitted.ToArray());
        Assert.Equal(expectedHit ? "branchYes" : "branchNo", finished.NodeOutcomes.Single(o => o.NodeId == "lane").Result);
        Assert.True(source.Arms >= 2, "任务观察必须明确分为联机前和原任务续跑后两段。");
        Assert.Empty(f.Host.FlowRuntime.List());
        Assert.Equal(0, f.Port.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlowWaitYieldsToOnlineGapThenContinuesWithoutReplayingCompletedWrites(bool stop)
    {
        static StartupStep Write(int number)
        {
            var values = RegisterBank.EmptySelection(); values[number - 1] = 1;
            return new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = values } };
        }
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 2;
        var document = new WorkflowDocument
        {
            Name = "流程内部等待接续", Activation = new() { Status = "active" },
            Nodes = [
                new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Steps = [Write(2), wait, Write(3)] } },
                new() { NodeId = "D", Kind = "resource.configGroup", Ref = new() { Config = "D", ConfigKey = "D#k", Revision = "rev-1" } }
            ]
        };
        await using var f = new Fixture(false, document);
        Assert.True((await f.Host.StartWorkflowAsync(document.WorkflowId!)).Ok);
        await Until(() => f.Registers.GetSnapshot().Entries[1].Value == 1);
        await f.Host.CloseOnlineHoeingSendAsync();
        await Until(() => !f.Host.IsDriving(document.WorkflowId!) &&
            f.Host.Runs.List().Single().State == WorkflowRunState.LocalWaitParking,
            () => f.Host.Runs.List().Single().State + ": " + f.Host.Runs.List().Single().Note);
        var parked = f.Host.Runs.List().Single();
        Assert.Equal("flow", parked.Cursor!.NodeId);
        var prepared = f.Host.PrepareOnlineHoeing(7, new(true, false, 42, 99, null));
        Assert.True(prepared.Ready, prepared.Reason);
        Assert.Equal(parked.RunId, prepared.ManagedRunId);
        Assert.True(f.Host.ConfirmOnlineHoeingSuspend(7, Fixture.Ticket, JsonSerializer.Serialize(new { status = "no_task" })));
        await Task.Delay(100);
        Assert.Empty(f.Body.Submitted);
        Assert.Equal(0, f.Registers.GetSnapshot().Entries[2].Value);
        // 模拟联机期间的其他合法写入；完成的R2动作不得在返回后重放。
        var zeros = RegisterBank.EmptySelection(); zeros[1] = 0;
        f.Registers.Set(new() { Values = zeros });
        if (stop)
        {
            var stopped = await f.Host.RequestRunActionAsync(parked.RunId, WorkflowRunAction.Stop);
            Assert.True(stopped.Ok, stopped.Message);
            Assert.False(await f.Host.FinishOnlineGapAfterReleaseAsync(parked.RunId, Fixture.Ticket));
            await f.Host.OpenOnlineHoeingSendAsync();
            await Task.Delay(100);
            Assert.Empty(f.Body.Submitted);
            Assert.Equal(0, f.Registers.GetSnapshot().Entries[2].Value);
            Assert.True(f.Host.Runs.Load(parked.RunId)!.StopRequested);
            return;
        }
        Assert.True(await f.Host.FinishOnlineGapAfterReleaseAsync(parked.RunId, Fixture.Ticket),
            f.Host.Runs.Load(parked.RunId)?.Note);
        var finished = await f.FinishedAsync();
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(new[] { "D" }, f.Body.Submitted.ToArray());
        Assert.Equal(0, f.Registers.GetSnapshot().Entries[1].Value);
        Assert.Equal(1, f.Registers.GetSnapshot().Entries[2].Value);
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "flow" && o.Result == "succeeded"));
        Assert.Single(finished.NodeOutcomes.Where(o => o.NodeId == "D"));
        Assert.Equal(0, f.Port.Sends);
    }

    [Fact]
    public async Task GapReceiptCanArriveBeforeFlowWaitObservesInterruptionAndStillResumeItsSavedStep()
    {
        static StartupStep Write(int number)
        {
            var values = RegisterBank.EmptySelection(); values[number - 1] = 1;
            return new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = values } };
        }
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 2;
        var document = new WorkflowDocument
        {
            Name = "先登记票据后停驻",
            Activation = new() { Status = "active" },
            Nodes = [
                new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Steps = [Write(2), wait, Write(3)] } },
                new() { NodeId = "D", Kind = "resource.configGroup", Ref = new() { Config = "D", ConfigKey = "D#k", Revision = "rev-1" } }
            ]
        };
        await using var f = new Fixture(false, document);
        var enteredDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliverInterrupt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        f.Host.FlowRuntime.DelayAsync = async (span, ct) =>
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                enteredDelay.TrySetResult();
                // 受控异步等待延迟交付取消，让真实接管票据先到，未手工改写运行状态。
                await deliverInterrupt.Task;
                ct.ThrowIfCancellationRequested();
            }
            else await Task.Delay(span, ct);
        };
        try
        {
            Assert.True((await f.Host.StartWorkflowAsync(document.WorkflowId!)).Ok);
            await enteredDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Host.CloseOnlineHoeingSendAsync();
            var running = f.Host.Runs.List().Single();
            Assert.True(f.Host.IsDriving(document.WorkflowId!));
            Assert.Equal(WorkflowRunState.Running, running.State);
            var prepared = f.Host.PrepareOnlineHoeing(7, new(true, false, 42, 99, null));
            Assert.True(prepared.Ready, prepared.Reason); Assert.Equal(running.RunId, prepared.ManagedRunId);
            Assert.True(f.Host.ConfirmOnlineHoeingSuspend(7, Fixture.Ticket, JsonSerializer.Serialize(new { status = "no_task" })));
            Assert.Equal("gap", f.Host.Runs.Load(running.RunId)!.OnlineHoeingHandoff!.Phase);
            deliverInterrupt.TrySetResult();
            await Until(() => !f.Host.IsDriving(document.WorkflowId!) &&
                f.Host.Runs.Load(running.RunId)!.State == WorkflowRunState.LocalWaitParking,
                () => f.Host.Runs.Load(running.RunId)!.State + ": " + f.Host.Runs.Load(running.RunId)!.Note);
            var parked = f.Host.Runs.Load(running.RunId)!;
            Assert.Equal(Fixture.Ticket, parked.OnlineHoeingHandoff!.Ticket);
            Assert.Equal("flow", parked.Cursor!.NodeId);
            Assert.Empty(f.Body.Submitted);
            Assert.Equal(0, f.Registers.GetSnapshot().Entries[2].Value);
            var clear = RegisterBank.EmptySelection(); clear[1] = 0;
            f.Registers.Set(new() { Values = clear });
            Assert.True(await f.Host.FinishOnlineGapAfterReleaseAsync(running.RunId, Fixture.Ticket),
                f.Host.Runs.Load(running.RunId)?.Note);
            var completed = await f.FinishedAsync();
            Assert.Equal(WorkflowRunState.Succeeded, completed.State);
            Assert.Equal(new[] { "D" }, f.Body.Submitted.ToArray());
            Assert.Equal(0, f.Registers.GetSnapshot().Entries[1].Value);
            Assert.Equal(1, f.Registers.GetSnapshot().Entries[2].Value);
            Assert.Single(completed.NodeOutcomes.Where(o => o.NodeId == "flow" && o.Result == "succeeded"));
            Assert.Single(completed.OnlineHoeingHistory.Where(h => h.Phase == "released"));
        }
        finally { deliverInterrupt.TrySetResult(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Ticket = "composition-online-ticket";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "composition-online-" + Guid.NewGuid().ToString("N"));
        internal TaskCenterHost Host { get; }
        internal WorkflowDocument Document { get; }
        internal RegisterBankService Registers { get; }
        internal BodyBoundary Body { get; }
        internal ResumePort Port { get; } = new();
        internal string DecisionOwner { get; }
        private readonly TaskCenterOnlineSendGate _gate;
        internal bool SendClosed => _gate.IsClosed;

        internal Fixture(bool legacy, WorkflowDocument? supplied = null)
        {
            Registers = new(Path.Combine(Root, "registers.json"), startTimer: false);
            Registers.Set(new() { Values = Selection(1) });
            var condition = StartupStepKinds.Create(StartupStepKinds.JudgeRegister);
            condition.RegisterCondition = new() { Values = Selection(1) };
            var decision = new WorkflowFlowDefinition { Name = "进入B线", Steps = [condition], ExitStepId = condition.Id };
            var lane = new WorkflowNode { NodeId = "lane", Kind = "control.lanes", Lanes = new()
            {
                Name = "B/C路线", YesName = "B线", NoName = "C线",
                Decision = legacy ? null : decision, SourceFlowNodeId = legacy ? "judge" : "",
                YesNodeIds = ["B1", "B2", "B3"], NoNodeIds = ["C"]
            } };
            DecisionOwner = legacy ? "judge" : "lane";
            Document = new() { Name = "车道联机接续", Activation = new() { Status = "active" }, Nodes = [] };
            if (legacy) Document.Nodes.Add(new() { NodeId = "judge", Kind = "control.flow", Flow = decision });
            Document.Nodes.Add(lane);
            Document.Nodes.AddRange(new[] { "B1", "B2", "B3", "C", "D" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.configGroup",
                Ref = new() { Config = id, ConfigKey = id + "#k", Revision = "rev-1" }
            }));
            if (supplied is not null) Document = supplied;
            Body = new();
            TaskCenterHost? host = null;
            TaskCenterOnlineSendGate? gate = null;
            host = new TaskCenterHost(Path.Combine(Root, "flows"), Path.Combine(Root, "runs"),
                Path.Combine(Root, "catalog.json"), () => null, null,
                (_, workflows, runs) =>
                {
                    Body.Runs = runs;
                    var resumeBoundary = new BgiWorkflowExecutionBoundary(Port, runs);
                    return new WorkflowRunner(workflows, runs, Body, new Actions(), new Actions(), new()
                    {
                        FlowRuntime = host!.FlowRuntime,
                        OnlineHoeingSendClosed = () => gate!.IsClosed,
                        OnlineHoeingContinuation = (run, sub, result, ct) =>
                            host.AwaitOnlineHoeingContinuationAsync(resumeBoundary, run, sub, result, ct)
                    });
                }, () => (true, null),
                onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
            Host = host;
            _gate = gate = (TaskCenterOnlineSendGate)typeof(TaskCenterHost).GetField("_onlineSendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            Host.FlowRuntime.Registers = Registers;
            Host.FlowRuntime.OnlineGateClosed = () => _gate.IsClosed;
            Host.Workflows.Save(Document, null);
        }

        internal async Task StartAsync(string? entryNodeId = null)
        {
            var result = await Host.StartWorkflowAsync(Document.WorkflowId!, entryNodeId: entryNodeId);
            Assert.True(result.Ok, result.Message);
            await Body.B2Reached.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }

        internal async Task<WorkflowRunRecord> SuspendB2Async()
        {
            var original = Host.Runs.List().Single();
            var sub = original.CurrentSubmission!;
            Assert.Equal("B2", sub.NodeId); Assert.NotNull(sub.JobId);
            await Host.CloseOnlineHoeingSendAsync();
            var actual = new CommandExecutor.ExecutionBoundarySnapshot(true, true, 42, 99,
                new CommandExecutor.ExecutionIdentity(Guid.NewGuid(), 42, 99, Guid.Parse(original.WireRunId), Guid.Parse(sub.JobId!)));
            var prepared = Host.PrepareOnlineHoeing(7, actual);
            Assert.True(prepared.Ready, prepared.Reason);
            Assert.Equal(original.RunId, prepared.ManagedRunId);
            Assert.True(Host.ConfirmOnlineHoeingSuspend(7, Ticket, JsonSerializer.Serialize(new
            {
                status = "suspended", workflowRunId = original.WireRunId, nodeId = "B2", originalJobId = sub.JobId,
                occurrence = sub.Occurrence, iteration = sub.LoopIteration, attempt = sub.Attempt,
                planCheckpointPhase = "inside", planNextTaskIndex = 3
            })));
            Body.ReleasePreempt.TrySetResult();
            await Until(() => Host.Runs.Load(original.RunId)?.State == WorkflowRunState.OnlineHoeingParking,
                () => Host.Runs.Load(original.RunId)?.Note);
            var parked = Host.Runs.Load(original.RunId)!;
            Assert.Equal("inside", parked.OnlineHoeingHandoff!.CheckpointPhase);
            Assert.Equal(3, parked.OnlineHoeingHandoff.NextTaskIndex);
            Assert.Equal(sub.Key, parked.OnlineHoeingHandoff.OriginalSubmissionKey);
            Assert.Equal(sub.JobId, parked.OnlineHoeingHandoff.OriginalJobId);
            return parked;
        }

        internal async Task<WorkflowRunRecord> FinishedAsync()
        {
            await Until(() => !Host.IsDriving(Document.WorkflowId!) &&
                Host.Runs.List().Single().State is WorkflowRunState.Succeeded or WorkflowRunState.Unknown or WorkflowRunState.Cancelled,
                () => Host.Runs.List().Single().State + ": " + Host.Runs.List().Single().Note);
            return Host.Runs.List().Single();
        }

        public async ValueTask DisposeAsync()
        {
            Body.ReleasePreempt.TrySetResult();
            await Host.ShutdownAsync();
            Registers.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static async Task Until(Func<bool> ready, Func<string?>? detail = null)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!ready() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(ready(), detail?.Invoke());
    }

    private sealed class BodyBoundary : IWorkflowExecutionBoundary
    {
        internal RunStore Runs = null!;
        internal readonly ConcurrentQueue<string> Submitted = new();
        internal readonly TaskCompletionSource B2Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleasePreempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, string> _nodes = new();
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Submitted.Enqueue(request.Node.NodeId);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            var job = Guid.NewGuid().ToString("D"); _nodes[job] = request.Node.NodeId;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith(job));
        }
        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (_nodes[jobId] != "B2") return BoundaryTerminalResult.Observed("succeeded");
            B2Reached.TrySetResult();
            await ReleasePreempt.Task.WaitAsync(ct);
            return BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted");
        }
        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        { ReleasePreempt.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class ResumePort : IBgiExecutionPort
    {
        private BgiJobInfo? _resume;
        private bool _missing;
        internal int Queries;
        internal int Sends;
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch? ServerEpoch => new() { ProcessId = 42, StartTicksUtc = 99 };
        internal string SetResume(WorkflowRunRecord run, IReadOnlyDictionary<string, object?> request, bool missing)
        {
            var id = Guid.NewGuid().ToString("D"); _missing = missing;
            _resume = new()
            {
                Epoch = ServerEpoch, JobId = Guid.Parse(id).ToString("N"), State = "succeeded", Source = "Resume",
                WorkflowRunId = run.WireRunId, NodeId = "B2", Occurrence = run.Cursor!.Occurrence,
                Iteration = run.Cursor.LoopIteration, Attempt = 2, IdempotencyKey = (string)request["idempotencyKey"]!,
                ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited"
            };
            return id;
        }
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        { Interlocked.Increment(ref Sends); throw new InvalidOperationException("续接观察不应再次发送"); }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var query = Interlocked.Increment(ref Queries);
            if (_missing) return Task.FromResult<(string?, BgiJobInfo?)>(("not_found", null));
            if (query == 1 && _resume is not null)
                return Task.FromResult<(string?, BgiJobInfo?)>((_resume.State, new BgiJobInfo
                {
                    Epoch = _resume.Epoch, JobId = _resume.JobId, State = _resume.State, Source = _resume.Source,
                    WorkflowRunId = _resume.WorkflowRunId, NodeId = _resume.NodeId, Occurrence = _resume.Occurrence,
                    Iteration = _resume.Iteration, Attempt = _resume.Attempt, IdempotencyKey = _resume.IdempotencyKey,
                    ExecutionExitConfirmed = false
                }));
            return Task.FromResult((_resume?.State, _resume));
        }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("unused"));
    }

    private sealed class SegmentedLogSource : IWorkflowLiveObservationSource
    {
        private readonly ConcurrentBag<Session> _sessions = [];
        internal int Arms;
        internal void Hit()
        {
            foreach (var session in _sessions) session.Hit();
        }
        public Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct)
            => ArmLiveAsync(identity, keyword, _ => { }, ct);
        public Task<IWorkflowObservationSession> ArmLiveAsync(string identity, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var session = new Session(identity, hit);
            _sessions.Add(session); Interlocked.Increment(ref Arms);
            return Task.FromResult<IWorkflowObservationSession>(session);
        }
        private sealed class Session(string identity, Action<WorkflowObservationHit> callback) : IWorkflowObservationSession
        {
            private readonly object _gate = new();
            private readonly List<WorkflowObservationHit> _hits = [];
            private bool _closed;
            internal void Hit()
            {
                lock (_gate)
                {
                    if (_closed) return;
                    var hit = new WorkflowObservationHit(DateTime.Now, "今天执行B线", "controlled-bgi.log", _hits.Count + 1);
                    _hits.Add(hit); callback(hit);
                }
            }
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
            {
                lock (_gate)
                {
                    _closed = true;
                    return Task.FromResult(new WorkflowObservationFact(identity, "frozen", _hits.ToArray(), null));
                }
            }
            public ValueTask DisposeAsync() { lock (_gate) _closed = true; return ValueTask.CompletedTask; }
        }
    }
}
