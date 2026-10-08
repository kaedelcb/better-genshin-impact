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
