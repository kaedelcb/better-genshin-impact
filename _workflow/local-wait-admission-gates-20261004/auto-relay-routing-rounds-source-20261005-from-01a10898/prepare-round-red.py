from pathlib import Path
import json, hashlib
root=Path.cwd()
base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
p=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs'
b=p.read_bytes()
(base/'round-test-before.cs.txt').write_bytes(b)
(base/'round-test-before.json').write_text(json.dumps(dict(path=str(p.relative_to(root)),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
anchor=b'    public void Dispose()'
assert b.count(anchor)==1
addition='''    [Theory]
    [InlineData(false, "sub:node-request:1")]
    [InlineData(true, null)]
    public void OriginalRouteRounds_PrepareCannotCrossDurableRouting(bool nodeRoute, string? nodeIdentity)
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("route-round-flow", "revision");
        store.RecordIntentForBoundary(run, new WorkflowSubmission { Key = "same-key", NodeId = "node", Attempt = 1 }, nodeRoute);
        var path = Path.Combine(_dir, run.RunId + ".run.json");
        var before = File.ReadAllBytes(path);
        var callbackCalled = false;
        Assert.False(store.TryPrepareSubmission(run.RunId, _ => { callbackCalled = true; return true; }, out _, nodeIdentity));
        Assert.False(callbackCalled);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(store.Load(run.RunId)!.CurrentSubmission!.SendPermit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalRouteRounds_SameKeyCannotSilentlyReuseDifferentBoundary(bool originalRoute)
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("route-round-flow", "revision");
        store.RecordIntentForBoundary(run, new WorkflowSubmission { Key = "same-key", NodeId = "node", Attempt = 1 }, originalRoute);
        // Deferred before any port call; no terminal or send fact is fabricated.
        run.CurrentSubmission!.Intent = SubmitIntentState.LocalWaitDeferred;
        store.Update(run);
        var path = Path.Combine(_dir, run.RunId + ".run.json");
        var before = File.ReadAllBytes(path);
        Assert.Throws<RunRecordConflictException>(() => store.RecordIntentForBoundary(run,
            new WorkflowSubmission { Key = "same-key", NodeId = "node", Attempt = 1 }, !originalRoute));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(originalRoute, store.Load(run.RunId)!.CurrentSubmission!.NodeAdmissionRequired);
    }

'''
p.write_bytes(b.replace(anchor,addition.replace('\n','\r\n').encode()+anchor))
print('added four routing boundary red cases; original tests unchanged')
