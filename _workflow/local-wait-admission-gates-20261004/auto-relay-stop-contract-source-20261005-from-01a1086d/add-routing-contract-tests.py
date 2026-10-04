from pathlib import Path
import json,hashlib
base=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d')
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs'); b=p.read_bytes(); (base/'routing-store-test-before.cs.txt').write_bytes(b)
(base/'routing-store-test-before.json').write_text(json.dumps(dict(path=str(p),sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),crlf=b'\r\n'in b,bom=b[:3].hex())),encoding='utf-8')
s=b.decode('utf-8-sig').replace('\r\n','\n'); needle='    public void Dispose()'
test='''    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void OriginalNodeRouting_OrdinaryWriterCannotChangeOrBackfill(bool? originalRoute)
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("routing-flow", "revision");
        var submission = new WorkflowSubmission { Key = "routing-original-key", NodeId = "node", Attempt = 1 };
        if (originalRoute is { } route) store.RecordIntentForBoundary(run, submission, route);
        else store.RecordIntent(run, submission);
        var path = Path.Combine(_dir, run.RunId + ".run.json");
        var before = File.ReadAllBytes(path);
        var loaded = store.Load(run.RunId)!;
        Assert.Equal(originalRoute, loaded.CurrentSubmission!.NodeAdmissionRequired);
        loaded.CurrentSubmission.NodeAdmissionRequired = originalRoute != true;
        Assert.Throws<RunRecordConflictException>(() => store.Update(loaded));
        Assert.Equal(before, File.ReadAllBytes(path));
        loaded = store.Load(run.RunId)!;
        var next = new WorkflowSubmission { Key = "fabricated-key", NodeId = "next", NodeAdmissionRequired = false };
        loaded.CurrentSubmission = next;
        Assert.Throws<RunRecordConflictException>(() => store.Update(loaded));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

'''
assert s.count(needle)==1; s=s.replace(needle,test+needle); p.write_bytes((b'\xef\xbb\xbf'if b.startswith(b'\xef\xbb\xbf')else b'')+s.replace('\n','\r\n'if b'\r\n'in b else '\n').encode('utf-8'))
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'); b=p.read_bytes(); s=b.decode('utf-8').replace('\r\n','\n'); needle='    [Theory]\n    [InlineData("missing")]\n    [InlineData("key")]'
test='''    [Fact]
    public async Task HistoricalDirectStop_ReopenedNodeSwitchCannotAddNodeResponsibility()
    {
        var root = NewRoot("historical-direct-");
        try
        {
            await ProbeNodeSubmitRoutingAsync(root, successorWired: false, nodeIds: ["n-1"],
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var run = Assert.Single(runs.List());
                    Assert.Equal(WorkflowRunState.Succeeded, run.State);
                    Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)));
                    await WaitForOriginalTerminalObserverAsync(host);
                    var original = runs.Load(run.RunId)!;
                    Assert.False(Assert.Single(TerminalReleaseEvidence.Submissions(original)).NodeAdmissionRequired);
                    var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var client = ((Func<BgiExternalClient?>)typeof(TaskCenterHost).GetField("_clientAccessor", fields)!.GetValue(host)!)();
                    var seams = (TaskCenterAdmissionSeams)typeof(TaskCenterHost).GetField("_admissionSeams", fields)!.GetValue(host)!;
                    await host.ShutdownAsync();
                    var reopened = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                        () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null), localExecutionCapability: () => true,
                        admissionWired: true, admissionSeams: seams, successorAdmissionWired: true);
                    try
                    {
                        reopened.EnsureRecovered();
                        var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(reopened)!;
                        var ops = store.Read().File!.Handoff!.Operations.Where(o => o.RunBinding == run.RunId).ToList();
                        Assert.NotEmpty(ops);
                        Assert.DoesNotContain(ops, o => o.OperationType == OperationType.NodeExecution);
                        var relation = typeof(TaskCenterHost).GetMethod("OriginalAdmissionMappingsPresent", fields)!;
                        Assert.True((bool)relation.Invoke(reopened, [original, ops])!);
                        var reconcile = typeof(TaskCenterHost).GetMethod("ReconcileAdmissionTerminalForExplicitStopAsync", fields)!;
                        Assert.Equal(HostActionStatus.Effective, (await (Task<HostActionResult>)reconcile.Invoke(reopened, [run.RunId, "direct original retry"])!).Status);
                        Assert.Equal(1, port.SendCount);
                    }
                    finally { await reopened.ShutdownAsync(); }
                });
        }
        finally { TryDelete(root); }
    }

'''
assert s.count(needle)==1; s=s.replace(needle,test+needle); p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
print('added original routing storage and real direct reopen tests')
