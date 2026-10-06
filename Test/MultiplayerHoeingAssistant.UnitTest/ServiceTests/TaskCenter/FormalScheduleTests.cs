using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalScheduleTests
{
    private static WorkflowEditVm Editor(WorkflowDocument doc) => new(doc,"r1",new ResourceCatalogService(()=>null,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"catalog.json")));
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
