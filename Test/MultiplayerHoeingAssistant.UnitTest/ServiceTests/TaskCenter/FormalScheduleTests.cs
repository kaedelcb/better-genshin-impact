using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalScheduleTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("fixed")]
    [InlineData("flexible")]
    public async Task ScheduledBackEdge_RepeatsAtSameValidTime_WithoutDayDeduplication(string mode)
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-repeat-time-"+Guid.NewGuid().ToString("N"));
        var local=DateTimeOffset.Now;var now=new DateTimeOffset(local.Year,local.Month,local.Day,6,0,0,local.Offset);
        var a=Timed("a","07:00",mode);a.Path=new(){Next="b"};a.Strategies.Add(new(){Kind="flow.route"});
        if(mode=="flexible")a.Strategies[0].Params!["until"]=JsonSerializer.SerializeToElement("08:00");
        var b=new WorkflowNode{NodeId="b",Kind="resource.oneDragonConfig",Ref=new(){Config="B"},Path=new(){Next="a"},Strategies=[new(){Kind="flow.route"}]};
        var doc=new WorkflowDocument{Name="repeat-time",Nodes=[a,b]};
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));flows.Save(doc,null);
        var boundary=new RepeatBoundary(()=>now);var actions=new NoActions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){Clock=()=>now,FlexibleFactsProvider=()=>new(),DelayAsync=(delay,ct)=>{ct.ThrowIfCancellationRequested();now+=delay;return Task.CompletedTask;}});
        boundary.OnSend=request=>{if(boundary.At.Count==4)runner.RequestAction(request.Run.RunId,WorkflowRunAction.Stop);};
        var result=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4,boundary.At.Count);Assert.All(boundary.At,time=>Assert.Equal(local.Date.AddHours(7),time.DateTime));
        Assert.Equal(4,boundary.Keys.Distinct().Count());Assert.Equal(WorkflowRunState.Cancelled,result.State);
    }

    private sealed class RepeatBoundary(Func<DateTimeOffset> clock):IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported=>true;public List<DateTimeOffset> At=[];public List<string> Keys=[];public Action<WorkflowSubmitRequest>? OnSend;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request,CancellationToken ct)
        {At.Add(clock());Keys.Add(request.Run.CurrentSubmission!.Key);TerminalReleaseFixtureFacts.FreezeBody(request);OnSend?.Invoke(request);return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-"+At.Count));}
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));}
    }

    [Fact]
    public void RepeatedSequence_AfterMidnightHasNoDailyGate_ExplicitCalendarRoundKeepsItsDay()
    {
        var day=new DateTimeOffset(2026,10,6,20,0,0,TimeSpan.FromHours(8));var node=Timed("a","23:00","sequence");
        var run=new WorkflowRunRecord{CreatedAt=day};var first=WorkflowNodeSchedule.Bind(run,node,new("a",0,0,0),day);
        run.NodeOutcomes.Add(new(){NodeId="a",Occurrence=0,LoopIteration=0,Result="succeeded"});
        var next=WorkflowNodeSchedule.Bind(run,node,new("a",0,0,1),day.AddHours(5));
        Assert.Equal(first.ScheduledAt,next.ScheduledAt);
        run.ExtensionData!["loopRoundStart:2"]=JsonSerializer.SerializeToElement(new DateTimeOffset(day.Date.AddDays(1).AddHours(7),day.Offset));
        var calendar=WorkflowNodeSchedule.Bind(run,node,new("a",0,0,2),day.AddDays(1));
        Assert.Equal(first.ScheduledAt.AddDays(1),calendar.ScheduledAt);
    }

    [Fact]
    public void RepeatedFlexible_CrossMidnightKeepsStillOpenWindow()
    {
        var day=new DateTimeOffset(2026,10,6,20,0,0,TimeSpan.FromHours(8));var node=Timed("a","23:00","flexible");
        node.Strategies[0].Params!["until"]=JsonSerializer.SerializeToElement("02:00");
        var run=new WorkflowRunRecord{CreatedAt=day};var first=WorkflowNodeSchedule.Bind(run,node,new("a",0,0,0),day);
        run.NodeOutcomes.Add(new(){NodeId="a",Occurrence=0,LoopIteration=0,Result="succeeded"});
        var next=WorkflowNodeSchedule.Bind(run,node,new("a",0,0,1),day.AddHours(5));
        Assert.Equal(first.ScheduledAt,next.ScheduledAt);Assert.Equal(first.WindowEndsAt,next.WindowEndsAt);
    }

    private static WorkflowNode Timed(string id, string time, string mode = "fixed") => new()
    {
        NodeId = id, Kind = "resource.oneDragonConfig", Ref = new() { Config = id },
        Strategies = [new() { Kind = "schedule.time", Params = new()
        {
            ["mode"] = JsonSerializer.SerializeToElement(mode), ["time"] = JsonSerializer.SerializeToElement(time),
        } }],
    };

    [Fact]
    public void NodeTiming_OverridesRootRank_AndMissingOrChangedBindingRejects()
    {
        var day = new DateTimeOffset(2026,10,6,9,0,0,TimeSpan.FromHours(8));
        var run = new WorkflowRunRecord { CreatedAt = day, TriggerTiming = new("trigger.timeFlexible", day, day.AddHours(4)) };
        var node = Timed("a", "10:00"); var at = new WorkflowNodeOccurrence("a",0,0,0);
        Assert.Throws<InvalidOperationException>(() => WorkflowNodeSchedule.Effective(run,node,at));
        var timing = WorkflowNodeSchedule.Bind(run,node,at,day);
        Assert.Equal(ArbitrationTier.Fixed, TaskCenterMechanismPolicy.TierOfTrigger(WorkflowNodeSchedule.Effective(run,node,at)!.Kind));
        Assert.Equal(day.Date.AddHours(10),timing.ScheduledAt.DateTime);
        node.Strategies[0].Params!["time"] = JsonSerializer.SerializeToElement("11:00");
        Assert.Throws<InvalidOperationException>(() => WorkflowNodeSchedule.Effective(run,node,at));
    }

    [Fact]
    public void PathMidnightAndRepeat_SelectNextDate_ColdResumeKeepsOriginalInstant()
    {
        var day = new DateTimeOffset(2026,10,6,20,0,0,TimeSpan.FromHours(8));
        var run = new WorkflowRunRecord { CreatedAt = day };
        var late = Timed("late","23:00"); var early = Timed("early","01:00");
        WorkflowNodeSchedule.Bind(run,late,new("late",0,0,0),day);
        run.NodeOutcomes.Add(new() { NodeId="late", Occurrence=0, LoopIteration=0, Result="succeeded" });
        var earlyAt = new WorkflowNodeOccurrence("early",1,0,0);
        var next = WorkflowNodeSchedule.Bind(run,early,earlyAt,day.AddHours(4));
        Assert.Equal(day.Date.AddDays(1).AddHours(1),next.ScheduledAt.DateTime);
        var restored = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(run))!;
        Assert.Equal(next,WorkflowNodeSchedule.Bind(restored,early,earlyAt,day.AddDays(3)));
        run.NodeOutcomes.Add(new() { NodeId="early", Occurrence=0, LoopIteration=0, Result="succeeded" });
        var repeat = WorkflowNodeSchedule.Bind(run,early,new("early",1,0,1),next.ScheduledAt.AddMinutes(10));
        Assert.Equal(next.ScheduledAt.AddDays(1),repeat.ScheduledAt);
        Assert.Equal(next,WorkflowNodeSchedule.Read(run,"early",0,0));
    }

    [Fact]
    public void DuplicateNodeOccurrences_HaveSeparateBindings_AndFixedDeclarationExpires()
    {
        var day = new DateTimeOffset(2026,10,6,9,0,0,TimeSpan.FromHours(8));
        var run = new WorkflowRunRecord { CreatedAt=day, State=WorkflowRunState.Waiting,
            Cursor=new() { NodeId="same",Occurrence=1,LoopIteration=0 } };
        var first = WorkflowNodeSchedule.Bind(run,Timed("same","10:00"),new("same",0,0,0),day);
        var second = WorkflowNodeSchedule.Bind(run,Timed("same","11:00"),new("same",1,1,0),day);
        Assert.NotEqual(first.ScheduledAt,second.ScheduledAt);
        Assert.True(WorkflowNodeSchedule.FixedDeclared(run,second.ScheduledAt));
        Assert.False(WorkflowNodeSchedule.FixedDeclared(run,second.ScheduledAt.AddMinutes(1)));
    }

    private static WorkflowEditVm Editor(WorkflowDocument doc) => new(doc,"r1",new ResourceCatalogService(()=>null,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"catalog.json")));
    [Fact]
    public async Task RunnerWaitRequest_UsesBoundNodeTimeInsteadOfRootTrigger()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-node-rank-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));
        var local=DateTimeOffset.Now;var now=new DateTimeOffset(local.Year,local.Month,local.Day,6,0,0,local.Offset);
        var doc=new WorkflowDocument {Name="node-time-rank",Nodes=[Timed("a","07:00")],Triggers=[new(){Kind="trigger.time",Params=new(){["time"]=JsonSerializer.SerializeToElement("06:00")}}]};
        flows.Save(doc,null);WaitDecisionRequest? actual=null;
        var source=new WaitDecisionSource(request=>
        {
            actual=request;
            return new(){Kind=LocalWaitDecisionKind.Hold,NoSendConfirmed=true,Reason="captured node rank",Context=new(){RunId=request.RunId,WorkflowId=request.WorkflowId,NodeId=request.NodeId}};
        });
        var boundary=new NeverSend();var actions=new NoActions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){Clock=()=>now,DelayAsync=(delay,ct)=>{ct.ThrowIfCancellationRequested();now+=delay;return Task.CompletedTask;}},waitDecisionSource:source);
        var parked=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(actual);Assert.Equal(ArbitrationTier.Fixed,actual.Tier);Assert.Equal(7,actual.ScheduledAt!.Value.Hour);
        Assert.Equal(WorkflowNodeSchedule.Read(runs.Load(parked.RunId)!,"a",0,0)!.ScheduledAt,actual.ScheduledAt);
        Assert.Equal(0,boundary.Sends);Assert.Equal(WorkflowRunState.LocalWaitParking,parked.State);
    }
    [Fact]
    public void Schedule_SaveUndo_PreservesStableNodesAndUnknownFields()
    {
        var doc=new WorkflowDocument { Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A",Revision="r1"},ExtensionData=new(){["custom"]=JsonSerializer.SerializeToElement("keep")}},new(){NodeId="b",Kind="resource.oneDragonConfig",Ref=new(){Config="B",Revision="r2"}}] };
        var vm=Editor(doc);var a=vm.Nodes[0];var b=vm.Nodes[1];
        vm.ScheduleNode(a,780);vm.ScheduleNode(b,720);
        var saved=vm.BuildSubmissionCopy();
        Assert.Equal(new[]{"b","a"},saved.Nodes.Select(n=>n.NodeId));
        Assert.Equal("13:00",saved.Nodes[1].Strategies.Single(s=>s.Kind=="schedule.time").GetString("time"));
        Assert.Equal("keep",saved.Nodes[1].ExtensionData!["custom"].GetString());
        Assert.True(vm.UndoSchedule());Assert.Same(a,vm.Nodes[0]);Assert.Same(b,vm.Nodes[1]);Assert.Null(b.ScheduleMinute);
        Assert.Equal("r2",vm.BuildSubmissionCopy().Nodes[1].Ref!.Revision);
    }
    [Fact]
    public void CancelTime_RemovesScheduleWithoutChangingResource()
    {
        var vm=Editor(new(){Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A"}}]});
        vm.ScheduleNode(vm.Nodes[0],720);vm.ScheduleNode(vm.Nodes[0],null);
        var saved=vm.BuildSubmissionCopy();Assert.Empty(saved.Nodes[0].Strategies);Assert.Equal("a",saved.Nodes[0].NodeId);
    }
    [Theory]
    [InlineData("fixed","12:30","",false)]
    [InlineData("flexible","23:00","02:00",true)]
    public void Resolve_UsesRunDayAndCrossMidnightWindow(string mode,string time,string end,bool crosses)
    {
        var day=new DateTimeOffset(2026,10,6,9,0,0,TimeSpan.FromHours(8));
        var schedule=new WorkflowStrategy {Kind="schedule.time",Params=new(){["mode"]=JsonSerializer.SerializeToElement(mode),["time"]=JsonSerializer.SerializeToElement(time),["until"]=JsonSerializer.SerializeToElement(end)}};
        var timing=WorkflowNodeSchedule.Resolve(schedule,day,out var error);
        Assert.Null(error);Assert.NotNull(timing);Assert.Equal(day.Date,timing.ScheduledAt.Date);
        if(crosses)Assert.Equal(day.Date.AddDays(1),timing.WindowEndsAt!.Value.Date);
    }
    [Fact]
    public void InvalidSchedule_StopsPreflightBeforeAnyExecution()
    {
        var doc=new WorkflowDocument {Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Strategies=[new(){Kind="schedule.time",Params=new(){["mode"]=JsonSerializer.SerializeToElement("fixed"),["time"]=JsonSerializer.SerializeToElement("bad")}}]}]};
        var plan=new WorkflowPlan(doc);Assert.False(plan.Preflight(true).Executable);
    }
    [Fact]
    public async Task WaitingNode_StopPersistsCancellationWithoutSubmittingOrCompleting()
    {
        var root=Path.Combine(Path.GetTempPath(),"formal-schedule-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(root,"flows"));var runs=new RunStore(Path.Combine(root,"runs"));
        var local=DateTimeOffset.Now;var now=new DateTimeOffset(local.Year,local.Month,local.Day,6,0,0,local.Offset);
        var doc=new WorkflowDocument {Name="schedule-stop",Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A"},Strategies=[new(){Kind="schedule.time",Params=new(){["mode"]=JsonSerializer.SerializeToElement("fixed"),["time"]=JsonSerializer.SerializeToElement("07:00")}}]}]};
        flows.Save(doc,null);var boundary=new NeverSend();var actions=new NoActions();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){Clock=()=>now,DelayAsync=async (_,ct)=>{entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);}});
        var driving=runner.StartAsync(doc.WorkflowId!);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var parked=Assert.Single(runs.List());
        Assert.Equal(WorkflowRunState.Waiting,parked.State);Assert.Equal(7,parked.Wait!.NextTriggerAt!.Value.Hour);Assert.Equal("a",parked.Cursor!.NodeId);
        runner.RequestAction(parked.RunId,WorkflowRunAction.Stop);var stopped=await driving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Cancelled,stopped.State);Assert.Equal(WorkflowRunState.Cancelled,runs.Load(parked.RunId)!.State);Assert.Equal(0,boundary.Sends);Assert.Equal(0,actions.Calls);
    }
    private sealed class NeverSend : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported=>true;public int Sends;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request,CancellationToken ct){Sends++;return Task.FromResult(BoundarySubmitResult.Rejected("unexpected send"));}
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId,CancellationToken ct)=>throw new InvalidOperationException();
    }
    private sealed class NoActions : IWorkflowPrerequisiteAdapter,IWorkflowTerminalExecutor
    {
        public int Calls;
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy,WorkflowRunRecord run,WorkflowNodeOccurrence occurrence,CancellationToken ct){Calls++;return Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Proceed,null,null));}
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action,WorkflowRunRecord run,CancellationToken ct){Calls++;return Task.FromResult(TerminalExecutionResult.Executed(null));}
    }
}
