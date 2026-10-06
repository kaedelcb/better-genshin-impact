using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalObservationTests
{
    private static WorkflowDocument Document()=>JsonSerializer.Deserialize<WorkflowDocument>("""
    {"schemaVersion":1,"name":"observe","nodes":[
    {"nodeId":"host","kind":"resource.oneDragonConfig","ref":{"config":"Host"},"strategies":[{"kind":"observer.log","keyword":"hit"},{"kind":"flow.route"}],"path":{"next":"choice"}},
    {"nodeId":"choice","kind":"control.condition","path":{"condition":{"kind":"observation","sourceNodeId":"host"},"yes":"yes","no":"no"}},
    {"nodeId":"yes","kind":"resource.oneDragonConfig","ref":{"config":"Yes"},"strategies":[{"kind":"flow.route"}],"path":{"next":"$end"}},
    {"nodeId":"no","kind":"resource.oneDragonConfig","ref":{"config":"No"},"strategies":[{"kind":"flow.route"}],"path":{"next":"$end"}}]}
    """)!;

    [Theory]
    [InlineData(true,"yes")]
    [InlineData(false,"no")]
    public async Task Leaf_ArmsBeforeSend_FreezesAndClosesBeforeRouting(bool hit,string target)
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-observer-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));var doc=Document();flows.Save(doc,null);
        var source=new Source(hit);var boundary=new Boundary(source);var actions=new Actions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){ObservationSource=source});
        var run=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[]{"arm","send","freeze","close","route"},source.Order);
        Assert.Equal(WorkflowRunState.Succeeded,run.State);Assert.Equal(new[]{"host",target},boundary.Visited);
        var host=run.NodeOutcomes.Single(o=>o.NodeId=="host");Assert.NotNull(host.SubmissionKey);
        var saved=runs.Load(run.RunId)!;var fact=saved.ExtensionData!["observationSubmission:"+host.SubmissionKey].Deserialize<WorkflowObservationFact>()!;
        Assert.Equal("frozen",fact.State);Assert.Equal(hit?2:0,fact.Hits.Count);
    }

    [Fact]
    public async Task UnknownObservation_StopsWithoutChoosingMissBranch_AndCloses()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-observer-gap-"+Guid.NewGuid().ToString("N"));
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));var doc=Document();flows.Save(doc,null);
        var source=new Source(false){Unknown=true};var boundary=new Boundary(source);var actions=new Actions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){ObservationSource=source});
        var run=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Unknown,run.State);Assert.Equal(new[]{"host"},boundary.Visited);Assert.Contains("close",source.Order);
        Assert.Equal("succeeded",run.NodeOutcomes.Single(o=>o.NodeId=="host").Result);Assert.Equal("choice",run.Cursor!.NodeId);
    }

    [Fact]
    public void MissingOrArmedObservation_IsUnknownRatherThanFalse()
    {
        var doc=Document();var run=new WorkflowRunRecord{NodeOutcomes=[new(){NodeId="host",Result="succeeded",SubmissionKey="old"}]};
        var plan=new WorkflowPlan(doc);
        Assert.Throws<WorkflowObservationUnknownException>(()=>plan.EvaluatePathCondition(doc.Nodes[1],DateTimeOffset.Now,run));
        run.ExtensionData=new(){["observationSubmission:old"]=JsonSerializer.SerializeToElement(new WorkflowObservationFact("old","armed",[],null))};
        Assert.Throws<WorkflowObservationUnknownException>(()=>plan.EvaluatePathCondition(doc.Nodes[1],DateTimeOffset.Now,run));
    }

    [Fact]
    public async Task WaitingHost_HasNoObserver_AndStopDoesNotSend()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-observer-wait-"+Guid.NewGuid().ToString("N"));var doc=Document();
        doc.Nodes[0].Strategies.Add(new(){Kind="schedule.time",Params=new(){["mode"]=JsonSerializer.SerializeToElement("fixed"),["time"]=JsonSerializer.SerializeToElement("07:00")}});
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));flows.Save(doc,null);
        var local=DateTimeOffset.Now;var now=new DateTimeOffset(local.Year,local.Month,local.Day,6,0,0,local.Offset);
        var source=new Source(false);var boundary=new Boundary(source);var actions=new Actions();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){ObservationSource=source,Clock=()=>now,DelayAsync=async(_,ct)=>{entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);}});
        var driving=runner.StartAsync(doc.WorkflowId!);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(source.Order);Assert.Empty(boundary.Visited);var parked=Assert.Single(runs.List());
        runner.RequestAction(parked.RunId,WorkflowRunAction.Stop);var stopped=await driving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Cancelled,stopped.State);Assert.Empty(source.Order);Assert.Empty(boundary.Visited);
    }

    [Fact]
    public async Task StopDuringLeaf_FreezesAndClosesObserverWithoutRouting()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-observer-stop-"+Guid.NewGuid().ToString("N"));var doc=Document();
        var flows=new WorkflowStore(Path.Combine(dir,"flows"));var runs=new RunStore(Path.Combine(dir,"runs"));flows.Save(doc,null);
        var source=new Source(true);var boundary=new Boundary(source);var actions=new Actions();
        var runner=new WorkflowRunner(flows,runs,boundary,actions,actions,new(){ObservationSource=source});
        boundary.OnSend=request=>runner.RequestAction(request.Run.RunId,WorkflowRunAction.Stop);
        var stopped=await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Cancelled,stopped.State);Assert.Equal(new[]{"host"},boundary.Visited);
        Assert.Equal(new[]{"arm","send","freeze","close"},source.Order);
        Assert.Contains(stopped.ExtensionData!,entry=>entry.Key.StartsWith("observationSubmission:") && entry.Value.Deserialize<WorkflowObservationFact>()?.State=="frozen");
    }

    [Fact]
    public async Task RealLogPipeline_FlushesFinalBufferedHit_AndKeepsEveryHit()
    {
        var dir=Path.Combine(Path.GetTempPath(),"formal-log-drain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"better-genshin-impact.log");await File.WriteAllTextAsync(path,$"[{DateTime.Now:HH:mm:ss.fff}] [INF] [Primary:S1:P42:T1] Test\nhistory hit\n");
        using var tail=new BgiLogTailService(()=>dir);var epoch=new BgiEpoch{ProcessId=42,StartTicksUtc=1};
        var source=new WorkflowLogObservationSource(tail,()=>epoch);
        await using var session=await source.ArmAsync("run:node:0:0:1:observer","hit",CancellationToken.None);
        var stamp=DateTime.Now.ToString("HH:mm:ss.fff");
        await File.AppendAllTextAsync(path,$"[{stamp}] [INF] [Primary:S1:P42:T1] Test\nhit one\n[{stamp}] [INF] [Primary:S1:P42:T1] Test\nhit two\n");
        var fact=await session.FreezeAsync(CancellationToken.None);
        Assert.Equal("frozen",fact.State);Assert.Equal(new[]{"hit one","hit two"},fact.Hits.Select(h=>h.Value));
    }

    private sealed class Source(bool hit):IWorkflowObservationSource
    {
        public List<string> Order=[];public bool Unknown;
        public Task<IWorkflowObservationSession> ArmAsync(string identity,string keyword,CancellationToken ct)
        {Order.Add("arm");return Task.FromResult<IWorkflowObservationSession>(new Session(this,identity,hit));}
        private sealed class Session(Source owner,string identity,bool hit):IWorkflowObservationSession
        {
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
            {owner.Order.Add("freeze");return Task.FromResult(new WorkflowObservationFact(identity,owner.Unknown?"unknown":"frozen",hit?[new(DateTime.Now,"hit one","test",1),new(DateTime.Now,"hit two","test",2)]:[],owner.Unknown?"gap":null));}
            public ValueTask DisposeAsync(){owner.Order.Add("close");return ValueTask.CompletedTask;}
        }
    }
    private sealed class Boundary(Source source):IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported=>true;public List<string> Visited=[];
        public Action<WorkflowSubmitRequest>? OnSend;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request,CancellationToken ct)
        {
            Visited.Add(request.Node.NodeId);source.Order.Add(request.Node.NodeId=="host"?"send":"route");
            if(request.Node.NodeId=="host")Assert.Equal("arm",source.Order[0]);else Assert.Contains("close",source.Order);
            TerminalReleaseFixtureFacts.FreezeBody(request);OnSend?.Invoke(request);return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-"+Visited.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));}
    }
    private sealed class Actions:IWorkflowPrerequisiteAdapter,IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy,WorkflowRunRecord run,WorkflowNodeOccurrence occurrence,CancellationToken ct)=>Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action,WorkflowRunRecord run,CancellationToken ct)=>Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
