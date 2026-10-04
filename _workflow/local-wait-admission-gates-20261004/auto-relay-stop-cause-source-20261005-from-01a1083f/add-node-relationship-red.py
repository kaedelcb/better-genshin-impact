from pathlib import Path
import json, hashlib
base=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f')
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b=p.read_bytes(); s=b.decode('utf-8-sig'); nl='\r\n' if b'\r\n' in b else '\n'; s=s.replace('\r\n','\n')
(base/'node-test-before.json').write_text(json.dumps(dict(path=str(p),sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),newline=repr(nl)),indent=2))
marker='    [Theory]\n    [InlineData("valid")]\n    [InlineData("missing-anchor")]'
assert s.count(marker)==1
test='''    [Theory]
    [InlineData("missing")]
    [InlineData("key")]
    [InlineData("round")]
    public async Task OriginalTerminalStop_MissingOrChangedNodeMappingCannotHideBehindFlowRegistration(string fault)
    {
        var root = NewRoot("original-node-mapping-");
        try
        {
            await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var run = Assert.Single(runs.List());
                    Assert.Equal(WorkflowRunState.Succeeded, run.State);
                    Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)));
                    // Explicitly wait for the original terminal observer before changing the fixture.
                    var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                    Assert.True(SpinWait.SpinUntil(() => store.Read().File!.Handoff!.Operations
                        .Where(o => o.RunBinding == run.RunId).All(o => o.RequestState == OperationRequestState.TerminalCompleted), TimeSpan.FromSeconds(10)));
                    run = runs.Load(run.RunId)!;
                    var sub = Assert.Single(run.SubmissionHistory);
                    Assert.True(sub.SendAttempted);
                    Assert.NotNull(sub.SendPermit);
                    Assert.Equal(1, port.SendCount);
                    var before = store.Read().File!.Handoff!;
                    var node = Assert.Single(before.Operations.Where(o => o.RunBinding == run.RunId && o.OperationType == OperationType.NodeExecution));
                    var original = JsonSerializer.Serialize(node);
                    Assert.Equal(node.SubmissionIdentity, sub.AcceptedSendIdentity);
                    // Retained terminal Cancelled records use the real original sending facts.
                    run.State = WorkflowRunState.Cancelled; run.StopRequested = true; run.TerminalRelease = null;
                    var path = Path.Combine(root, "runs", run.RunId + ".run.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(run));
                    var owner = store.Read().File!.Lease!;
                    Assert.True(store.MutateHandoffLatest(owner.LeaseId, owner.OwnerEpoch, file =>
                    {
                        var current = file.Handoff!.Operations.Single(o => o.RequestIdentity == node.RequestIdentity);
                        if (fault == "missing") file.Handoff.Operations.Remove(current);
                        if (fault == "key") current.WireSubmitKey = "foreign-key";
                        if (fault == "round") { current.LastSendSeq++; current.SubmissionIdentity = $"sub:{current.RequestIdentity}:{current.LastSendSeq}"; }
                        return null;
                    }).Success);
                    var stopped = await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
                    Assert.Equal(HostActionStatus.Unavailable, stopped.Status);
                    Assert.Equal(1, port.SendCount);
                    Assert.True(store.MutateHandoffLatest(owner.LeaseId, owner.OwnerEpoch, file =>
                    {
                        file.Handoff!.Operations.RemoveAll(o => o.RequestIdentity == node.RequestIdentity);
                        file.Handoff.Operations.Add(JsonSerializer.Deserialize<OperationRecord>(original)!);
                        return null;
                    }).Success);
                    Assert.Equal(HostActionStatus.Effective, (await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
                    Assert.Equal(original, JsonSerializer.Serialize(store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == node.RequestIdentity)));
                    Assert.Equal(1, port.SendCount);
                });
        }
        finally { TryDelete(root); }
    }

'''
p.write_bytes((b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'')+s.replace(marker,test+marker).replace('\n',nl).encode())
# Reuse the established ordinary execution runner, with this relay as its only output root.
src=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-host-stop-integrity-20261005-from-01a1081b/run-stop-check.py').read_text()
src=src.replace('auto-relay-host-stop-integrity-20261005-from-01a1081b','auto-relay-stop-cause-source-20261005-from-01a1083f')
(base/'run-stop-check.py').write_text(src,encoding='utf-8')
