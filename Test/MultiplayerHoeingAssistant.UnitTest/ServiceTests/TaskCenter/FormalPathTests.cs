using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalPathTests
{
    [Fact]
    public void LayoutEdit_PreservesUnknownConditionAndItsBlockedState()
    {
        var doc=Document("""[{"nodeId":"c","kind":"control.condition","path":{"yes":"$end","no":"$end","condition":{"kind":"future-condition","custom":"keep"}}}]""");
        doc.ExtensionData=new(){["scheduleLanes"]=JsonSerializer.SerializeToElement(new[]{"主车道","支线"})};
        var vm=new WorkflowEditVm(doc,"r1",new ResourceCatalogService(()=>null,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"catalog.json")));
        vm.Nodes[0].LaneSpan=2;var saved=vm.BuildSubmissionCopy();
        Assert.Equal("future-condition",saved.Nodes[0].Path!.Condition!.Kind);Assert.Equal("keep",saved.Nodes[0].Path!.Condition!.ExtensionData!["custom"].GetString());
        Assert.False(new WorkflowPlan(saved).Preflight(true).Executable);
    }
    [Fact]
    public void DeletingEmptyLane_ReindexesTargets_AndRefusesDeletingOccupiedLane()
    {
        var doc=Document("""
        [{"nodeId":"c","kind":"control.condition","path":{"yes":"lane:2","no":"$end","condition":{"kind":"constant","value":true}}},
         {"nodeId":"a","kind":"resource.oneDragonConfig","scheduleLane":2},
         {"nodeId":"b","kind":"resource.oneDragonConfig","scheduleLane":3}]
        """);
        doc.ExtensionData=new(){["scheduleLanes"]=JsonSerializer.SerializeToElement(new[]{"主车道","空车道","A车道","B车道"})};
        var vm=new WorkflowEditVm(doc,"r1",new ResourceCatalogService(()=>null,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"catalog.json")));
        vm.RemoveLane(1);var plan=new WorkflowPlan(vm.BuildSubmissionCopy());
        Assert.Equal("a",plan.Next(plan.FirstOccurrence()!,"branchYes")!.NodeId);
        Assert.Throws<InvalidOperationException>(()=>vm.RemoveLane(1));Assert.Equal(3,vm.Lanes.Count);
        Assert.True(vm.UndoSchedule());Assert.Equal(4,vm.Lanes.Count);Assert.Equal("lane:2",vm.BuildSubmissionCopy().Nodes[0].Path!.Yes);
    }
    [Fact]
    public async Task ChangedLaneLayout_RejectsColdResumeBeforeStateWriteOrSend()
    {
        var dir=Path.Combine(Path.GetTempPath(),"path-layout-resume-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));
        var doc=Document("""[{"nodeId":"a","kind":"resource.oneDragonConfig","path":{"next":"b"}},{"nodeId":"b","kind":"resource.oneDragonConfig","path":{"next":"$end"}}]""");
        flows.Save(doc,null);var port=new Boundary();var actions=new Actions();var runner=new WorkflowRunner(flows,runs,port,actions,actions);
        port.OnSend=request=>runner.RequestAction(request.Run.RunId,WorkflowRunAction.Pause);
        var paused=await runner.StartAsync(doc.WorkflowId!);var before=runs.Load(paused.RunId)!;
        var edit=flows.LoadSnapshot(doc.WorkflowId!);edit.Document.ExtensionData=new(){["scheduleLanes"]=JsonSerializer.SerializeToElement(new[]{"主车道","新车道"})};flows.Save(edit.Document,edit.Revision);
        var fresh=new Boundary();var resumed=new WorkflowRunner(flows,runs,fresh,actions,actions);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>resumed.ResumeAsync(paused.RunId));
        Assert.Empty(fresh.Visited);var after=runs.Load(paused.RunId)!;Assert.Equal(before.RecordRevision,after.RecordRevision);Assert.Equal(WorkflowRunState.Paused,after.State);Assert.Equal("b",after.Cursor!.NodeId);
        Assert.False(resumed.HasActiveControl(paused.RunId));
    }
    [Fact]
    public void Undo_RestoresWholePathConversion_AndAdvancedParameterEdit()
    {
        var doc=new WorkflowDocument{Name="undo",Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A"}},new(){NodeId="b",Kind="resource.oneDragonConfig",Ref=new(){Config="B"}}]};
        var vm=new WorkflowEditVm(doc,"r1",new ResourceCatalogService(()=>null,Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"catalog.json")));
        vm.Connect(vm.Nodes[0],vm.Nodes[1]);Assert.True(new WorkflowPlan(vm.BuildSubmissionCopy()).HasPaths);
        Assert.True(vm.UndoSchedule());Assert.False(new WorkflowPlan(vm.BuildSubmissionCopy()).HasPaths);
        var node=vm.Nodes[0];node.PriorityText="7";Assert.Equal(7,TaskCenterMechanismPolicy.PriorityOfNode(vm.BuildSubmissionCopy().Nodes[0]));
        Assert.True(vm.UndoSchedule());Assert.Equal("0",node.PriorityText);Assert.Equal(0,TaskCenterMechanismPolicy.PriorityOfNode(vm.BuildSubmissionCopy().Nodes[0]));
        node.AddAccountCommand.Execute(null);Assert.True(node.HasAccount);Assert.True(vm.UndoSchedule());Assert.False(node.HasAccount);
    }
    private static WorkflowDocument Document(string nodes)
    {
        var doc=JsonSerializer.Deserialize<WorkflowDocument>("{\"name\":\"path\",\"nodes\":"+nodes+"}")!;
        foreach(var node in doc.Nodes.Where(n=>n.Path is not null)) node.Strategies.Add(new(){Kind="flow.route"});
        return doc;
    }
    [Fact]
    public async Task ExplicitPath_DoesNotRunUnreachedArrayNeighbour()
    {
        var doc=Document("""
        [{"nodeId":"a","kind":"resource.oneDragonConfig","ref":{"config":"A"},"path":{"next":"c"}},
         {"nodeId":"b","kind":"resource.oneDragonConfig","ref":{"config":"B"}},
         {"nodeId":"c","kind":"resource.oneDragonConfig","ref":{"config":"C"},"path":{"next":"$end"}}]
        """);
        var (run,boundary,_,_)=await Run(doc);
        Assert.Equal(WorkflowRunState.Succeeded,run.State);
        Assert.Equal(new[]{"a","c"},boundary.Visited.Select(v=>v.NodeId));
    }
    [Fact]
    public async Task BackEdge_ExecutesFreshArrivalWithDistinctSubmission_AndStopPersists()
    {
        var doc=Document("""
        [{"nodeId":"a","kind":"resource.oneDragonConfig","ref":{"config":"A"},"path":{"next":"b"}},
         {"nodeId":"b","kind":"resource.oneDragonConfig","ref":{"config":"B"},"path":{"next":"a"}}]
        """);
        var (run,boundary,runs,_)=await Run(doc,stopAt:4);
        Assert.Equal(new[]{"a","b","a","b"},boundary.Visited.Select(v=>v.NodeId));
        Assert.Equal(4,boundary.Keys.Distinct().Count());
        Assert.Equal(WorkflowRunState.Cancelled,run.State);
        Assert.Equal(WorkflowRunState.Cancelled,runs.Load(run.RunId)!.State);
    }
    [Theory]
    [InlineData(true,"yes")]
    [InlineData(false,"no")]
    public async Task Condition_ChoosesOneActualSuccessor_WithoutSubmittingCondition(bool answer,string expected)
    {
        var doc=Document("""
        [{"nodeId":"choice","kind":"control.condition","path":{"yes":"yes","no":"no","condition":{"kind":"constant","value":VALUE}}},
         {"nodeId":"yes","kind":"resource.oneDragonConfig","ref":{"config":"Y"},"path":{"next":"$end"}},
         {"nodeId":"no","kind":"resource.oneDragonConfig","ref":{"config":"N"},"path":{"next":"$end"}}]
        """.Replace("VALUE",answer?"true":"false"));
        var (run,boundary,_,_)=await Run(doc);
        Assert.Equal(WorkflowRunState.Succeeded,run.State);
        Assert.Equal(expected,Assert.Single(boundary.Visited).NodeId);
        Assert.Equal(answer?"branchYes":"branchNo",run.NodeOutcomes[0].Result);
    }
    [Fact]
    public void MissingTarget_AndAmbiguousIdentity_ArePreflightBlocked()
    {
        var missing=Document("""[{"nodeId":"a","kind":"resource.oneDragonConfig","path":{"next":"missing"}}]""");
        Assert.False(new WorkflowPlan(missing).Preflight(true).Executable);
        var duplicate=Document("""[{"nodeId":"a","kind":"resource.oneDragonConfig","path":{"next":"a"}},{"nodeId":"a","kind":"resource.oneDragonConfig"}]""");
        Assert.False(new WorkflowPlan(duplicate).Preflight(true).Executable);
    }
    [Fact]
    public async Task LaneMerge_VisitsSharedNodeOnceAlongChosenPath_ThenKeepsArrivalLane()
    {
        var doc=Document("""
        [{"nodeId":"choice","kind":"control.condition","path":{"yes":"lane:1","no":"lane:0","condition":{"kind":"constant","value":true}}},
         {"nodeId":"left","kind":"resource.oneDragonConfig","ref":{"config":"L"},"scheduleLane":0},
         {"nodeId":"right","kind":"resource.oneDragonConfig","ref":{"config":"R"},"scheduleLane":1},
         {"nodeId":"shared","kind":"resource.oneDragonConfig","ref":{"config":"S"},"scheduleLane":0,"scheduleSpan":2},
         {"nodeId":"left-tail","kind":"resource.oneDragonConfig","ref":{"config":"LT"},"scheduleLane":0},
         {"nodeId":"right-tail","kind":"resource.oneDragonConfig","ref":{"config":"RT"},"scheduleLane":1}]
        """);
        doc.ExtensionData=new(){["scheduleLanes"]=JsonSerializer.SerializeToElement(new[]{"主车道","支线"})};
        var (run,boundary,_,_)=await Run(doc);
        Assert.Equal(WorkflowRunState.Succeeded,run.State);
        Assert.Equal(new[]{"right","shared","right-tail"},boundary.Visited.Select(v=>v.NodeId));
        Assert.All(boundary.Visited,v=>Assert.Equal(1,v.PathLane));
    }
    [Fact]
    public async Task PauseAndNewRunnerResume_UsesDurableChosenBranch_WithoutResendingCompletedNode()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-path-resume-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));
        var doc=Document("""
        [{"nodeId":"a","kind":"resource.oneDragonConfig","ref":{"config":"A"},"path":{"next":"c"}},
         {"nodeId":"b","kind":"resource.oneDragonConfig","ref":{"config":"B"}},
         {"nodeId":"c","kind":"resource.oneDragonConfig","ref":{"config":"C"},"path":{"next":"$end"}}]
        """);
        flows.Save(doc,null);var boundary=new Boundary();var actions=new Actions();
        var first=new WorkflowRunner(flows,runs,boundary,actions,actions);
        boundary.OnSend=request=>first.RequestAction(request.Run.RunId,WorkflowRunAction.Pause);
        var paused=await first.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Paused,paused.State);Assert.Equal("c",runs.Load(paused.RunId)!.Cursor!.NodeId);
        var freshBoundary=new Boundary();var resumed=new WorkflowRunner(flows,new RunStore(Path.Combine(dir,"runs")),freshBoundary,actions,actions);
        var completed=await resumed.ResumeAsync(paused.RunId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded,completed.State);Assert.Equal("c",Assert.Single(freshBoundary.Visited).NodeId);
    }
    private static async Task<(WorkflowRunRecord,Boundary,RunStore,WorkflowStore)> Run(WorkflowDocument doc,int stopAt=0)
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-path-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));flows.Save(doc,null);
        var boundary=new Boundary();var actions=new Actions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions);
        if(stopAt>0)boundary.OnSend=request=>{if(boundary.Visited.Count==stopAt)runner.RequestAction(request.Run.RunId,WorkflowRunAction.Stop);};
        var run=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(15));
        return (run,boundary,runs,flows);
    }
    private sealed class Boundary:IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported=>true;
        public List<WorkflowNodeOccurrence> Visited {get;}=[];
        public List<string> Keys {get;}=[];
        public Action<WorkflowSubmitRequest>? OnSend;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request,CancellationToken ct)
        {Visited.Add(request.Occurrence);Keys.Add(request.Run.CurrentSubmission!.Key);TerminalReleaseFixtureFacts.FreezeBody(request);OnSend?.Invoke(request);return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-"+Visited.Count));}
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));}
    }
    private sealed class Actions:IWorkflowPrerequisiteAdapter,IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy,WorkflowRunRecord run,WorkflowNodeOccurrence occurrence,CancellationToken ct)=>Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action,WorkflowRunRecord run,CancellationToken ct)=>Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
