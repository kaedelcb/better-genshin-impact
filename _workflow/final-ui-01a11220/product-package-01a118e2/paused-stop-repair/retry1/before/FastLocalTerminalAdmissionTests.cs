using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("TaskCenterHeavyE2E")]
public sealed class FastLocalTerminalAdmissionTests
{
    private const string Epoch="123:456";
    private static string Root()=>Path.Combine(Environment.GetEnvironmentVariable("FORMAL_TERMINAL_EVIDENCE_DIR")
        ?? Path.GetTempPath(),"fast-local-"+Guid.NewGuid().ToString("N"));

    private static string SaveFlow(string root,bool answer,bool condition=true)
    {
        var doc=JsonSerializer.Deserialize<WorkflowDocument>("""
        {"name":"本机终局","activation":{"status":"active"},"nodes":[
         {"nodeId":"choice","kind":"control.condition","strategies":[{"kind":"flow.route"}],
          "path":{"yes":"end","no":"$end","condition":{"kind":"constant","value":VALUE}}},
         {"nodeId":"end","kind":"control.end","strategies":[{"kind":"flow.route"}],"path":{"next":"$end"}}]}
        """.Replace("VALUE",answer?"true":"false"))!;
        if(!condition)doc.Nodes.RemoveAt(0);
        new WorkflowStore(Path.Combine(root,"flows")).Save(doc,null);
        return doc.WorkflowId!;
    }

    private static TaskCenterHost Host(string root,BgiExternalClient client)=>new(
        Path.Combine(root,"flows"),Path.Combine(root,"runs"),Path.Combine(root,"catalog.json"),()=>client,
        log:null,runnerFactory:null,readinessOverride:()=> (true,null),localExecutionCapability:()=>true,
        statusSnapshotProvider:()=>new ControlStatus{TaskStatusAvailable=true,TaskStatusBgiEpoch=Epoch,
            TaskStatusObservedAtUtc=DateTimeOffset.UtcNow,TaskRunning=false},
        admissionWired:true,admissionSeams:new(){Epoch=Epoch,F11Active=false,Occupied=false,FactsUnknown=false,
            ProductionBoundaryFactory=(_,runs)=>new BgiWorkflowExecutionBoundary(new LocalPort(),runs)});

    private sealed class LocalPort:IBgiExecutionPort
    {
        public bool IsReady=>true;
        public bool HasCapability(string name)=>true;
        public BgiEpoch? ServerEpoch {get;}=new(){ProcessId=123,StartTicksUtc=456};
        public Task<BgiExternalResponse> SendCommandAsync(string operation,object? payload,CancellationToken ct)
        {
            Assert.Equal(WorkflowStopAuthority.Operation,operation);
            return Task.FromResult(new BgiExternalResponse{Success=true,Data=JsonSerializer.Serialize(new{
                bgiEpoch=new{processId=123,startTicksUtc=456},stopVersion=0,
                lastManualStopTimestamp=(long?)null,monotonicFrequency=System.Diagnostics.Stopwatch.Frequency})});
        }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)=>throw new InvalidOperationException("local control must not query jobs");
        public Task<(string? Status,BgiJobInfo? Job)> QueryJobStatusAsync(string jobId,CancellationToken ct)=>throw new InvalidOperationException("local control must not poll jobs");
        public Task CancelOwnedTaskAsync(string jobId,CancellationToken ct)=>throw new InvalidOperationException("local control has no BGI task to cancel");
    }

    private static OperationRecord OriginalOperation(string root,string runId)=>Assert.Single(
        new ArbitrationLeaseStore(Path.Combine(root,"arbitration")).Read().File!.Handoff!.Operations
            .Where(o=>o.RunBinding==runId));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdPausedStop_ReconcilesOriginalAdmission_OrRetainsFailureForExplicitRetry(bool failSeal)
    {
        var root=Root();var flow=SaveFlow(root,true);using var client=new BgiExternalClient();
        var flows=new WorkflowStore(Path.Combine(root,"flows"));var doc=flows.Load(flow);
        doc.Nodes[0].Strategies.Add(new(){Kind="schedule.time",Params=new()
        {
            ["mode"]=JsonSerializer.SerializeToElement("sequence"),
            ["time"]=JsonSerializer.SerializeToElement(DateTimeOffset.Now.AddMinutes(30).ToString("HH:mm")),
        }});
        flows.Save(doc,null);var host=Host(root,client);string runId;
        try
        {
            var started=await host.StartWorkflowAsync(flow);
            Assert.True(started.Status==HostActionStatus.Registered,started.Message);
            runId=Assert.Single(host.Runs.List()).RunId;
            await WaitForRun(host,runId,r=>r.Wait is not null);
            Assert.Equal(HostActionStatus.Registered,(await host.RequestRunActionAsync(runId,WorkflowRunAction.Pause)).Status);
            var paused=await WaitForRun(host,runId,r=>r.State==WorkflowRunState.Paused);
            Assert.Empty(TerminalReleaseEvidence.Submissions(paused));Assert.Empty(paused.NodeOutcomes);
            Assert.Equal(OperationRequestState.Accepted,OriginalOperation(root,runId).RequestState);
        }
        finally{await host.ShutdownAsync();}
        var cold=Host(root,client);
        try
        {
            if(failSeal)cold.Runs.PublishFaultForTest=r=>r.TerminalRelease is null?null:new IOException("paused seal unavailable");
            var stopped=await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop);
            if(failSeal)
            {
                Assert.Equal(HostActionStatus.Unavailable,stopped.Status);
                var retained=cold.Runs.Load(runId)!;
                Assert.Equal(WorkflowRunState.Cancelled,retained.State);Assert.True(retained.StopRequested);
                Assert.Null(retained.TerminalRelease);
                Assert.Equal(OperationRequestState.Accepted,OriginalOperation(root,runId).RequestState);
                cold.Runs.PublishFaultForTest=null;
                stopped=await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop);
            }
            Assert.Equal(HostActionStatus.Effective,stopped.Status);
            var after=cold.Runs.Load(runId)!;
            Assert.Equal(WorkflowRunState.Cancelled,after.State);Assert.True(after.StopRequested);
            Assert.Empty(TerminalReleaseEvidence.Submissions(after));Assert.Empty(after.NodeOutcomes);
            Assert.True(TerminalReleaseEvidence.ValidRunSeal(after),"Paused Stop must seal before claiming Effective");
            var operation=OriginalOperation(root,runId);
            Assert.Equal(OperationRequestState.TerminalCompleted,operation.RequestState);
            Assert.Equal("runstore-seal:"+after.TerminalRelease!.Id,operation.TerminalReleaseEvidence);
            Assert.Equal(HostActionStatus.Effective,(await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop)).Status);
            Assert.Equal(after.RecordRevision,cold.Runs.Load(runId)!.RecordRevision);
        }
        finally{await cold.ShutdownAsync();}
    }

    private static async Task<WorkflowRunRecord> WaitForRun(TaskCenterHost host,string runId,Func<WorkflowRunRecord,bool> predicate)
    {
        for(var i=0;i<400;i++)
        {
            var run=host.Runs.Load(runId)!;if(predicate(run))return run;
            await Task.Delay(25);
        }
        throw new TimeoutException("Waiting for local schedule/pause boundary");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionRunner_LocalBranchCompletesOriginalAdmission_AndAllowsRepeat(bool answer)
    {
        var root=Root();var flow=SaveFlow(root,answer);using var client=new BgiExternalClient();
        var host=Host(root,client);
        try
        {
            var identities=new HashSet<string>();
            for(var round=0;round<2;round++)
            {
                var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.AdmissionTerminalReconciliationCompletedForTest=()=>finished.TrySetResult();
                var started=await host.StartWorkflowAsync(flow);
                Assert.True(started.Status==HostActionStatus.Registered,started.Message);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var run=host.Runs.List().OrderByDescending(r=>r.CreatedAt).First();
                Assert.True(identities.Add(run.RunId));
                Assert.Equal(WorkflowRunState.Succeeded,run.State);
                Assert.Empty(TerminalReleaseEvidence.Submissions(run));
                Assert.Empty(run.CompletionHistory);
                Assert.Equal(answer?"branchYes":"branchNo",run.NodeOutcomes[0].Result);
                Assert.True(TerminalReleaseEvidence.ValidRunSeal(run));
                var operation=OriginalOperation(root,run.RunId);
                Assert.Equal(OperationRequestState.TerminalCompleted,operation.RequestState);
                Assert.Equal("runstore-seal:"+run.TerminalRelease!.Id,operation.TerminalReleaseEvidence);
            }
            Assert.Equal(2,host.Runs.List().Count);
        }
        finally{await host.ShutdownAsync();}
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColdHost_ExplicitTerminalRetry_RepairsFailedSealWithoutChangingResultOrExecuting(bool condition)
    {
        var root=Root();var flow=SaveFlow(root,true,condition);using var client=new BgiExternalClient();
        var host=Host(root,client);WorkflowRunRecord before;
        try
        {
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Runs.PublishFaultForTest=r=>r.TerminalRelease is null?null:new IOException("seal publication unavailable");
            host.AdmissionTerminalReconciliationCompletedForTest=()=>finished.TrySetResult();
            var started=await host.StartWorkflowAsync(flow);
            Assert.True(started.Status==HostActionStatus.Registered,started.Message);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            before=Assert.Single(host.Runs.List());
            Assert.Equal(WorkflowRunState.Succeeded,before.State);
            Assert.Null(before.TerminalRelease);
            Assert.Equal(OperationRequestState.Accepted,OriginalOperation(root,before.RunId).RequestState);
        }
        finally{await host.ShutdownAsync();}
        var cold=Host(root,client);
        try
        {
            Assert.Equal(HostActionStatus.Effective,(await cold.RequestRunActionAsync(before.RunId,WorkflowRunAction.Stop)).Status);
            var after=Assert.Single(cold.Runs.List());
            Assert.Equal(before.State,after.State);Assert.False(after.StopRequested);
            Assert.Equal(JsonSerializer.Serialize(before.NodeOutcomes),JsonSerializer.Serialize(after.NodeOutcomes));
            Assert.Empty(TerminalReleaseEvidence.Submissions(after));Assert.Empty(after.CompletionHistory);
            Assert.True(TerminalReleaseEvidence.ValidRunSeal(after));
            Assert.Equal(OperationRequestState.TerminalCompleted,OriginalOperation(root,after.RunId).RequestState);
            Assert.Equal(HostActionStatus.Effective,(await cold.RequestRunActionAsync(after.RunId,WorkflowRunAction.Stop)).Status);
            Assert.Equal(after.RecordRevision,cold.Runs.Load(after.RunId)!.RecordRevision);
        }
        finally{await cold.ShutdownAsync();}
    }
}
