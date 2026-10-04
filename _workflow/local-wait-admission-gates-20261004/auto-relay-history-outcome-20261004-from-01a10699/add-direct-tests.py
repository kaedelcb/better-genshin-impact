from pathlib import Path
import hashlib,json
r=Path.cwd();d=Path(__file__).parent;p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/HistoricalExecutionObservationTests.cs';b=p.read_bytes();(d/'historical-tests.before').write_bytes(b);s=b.decode('utf-8').replace('\r\n','\n');needle='    [Fact]\n    public void HistoricalObservation_UncertainWordCanBeAugmentedWithoutChangingOriginalOutcome()'
assert s.count(needle)==1
helper='''    [Theory]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-identity")]
    [InlineData("result")]
    public void HistoricalObservation_DirectBoundSealsRejectConflictingOutcomeSet(string fault)
    {
        var (store, run, _, op) = Seed(true, true);
        var sub = run.SubmissionHistory[0];
        sub.ObservedTerminal = "cancelled"; sub.EffectState = "cancelled";
        sub.ExecutionExitConfirmed = true; sub.ExecutionExitDisposition = "execution_exited";
        var outcome = run.NodeOutcomes[0]; outcome.RawTerminal = "cancelled"; outcome.Result = "cancelled";
        run.State = WorkflowRunState.Cancelled;
        if (fault == "result") outcome.Result = "succeeded";
        else
        {
            var conflict = JsonSerializer.Deserialize<WorkflowNodeOutcome>(JsonSerializer.Serialize(outcome))!;
            if (fault == "duplicate-key") conflict.AcceptedSendIdentity = "sub:foreign-request:1";
            else conflict.SubmissionKey = "foreign-key";
            run.NodeOutcomes.Add(conflict);
        }
        var path = Path.Combine(_root, run.RunId + ".run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run));
        var original = File.ReadAllBytes(path);
        Assert.Null(store.TrySealTerminalNode(run.RunId, op));
        Assert.Null(store.TrySealTerminalRun(run.RunId));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

'''
p.write_bytes(s.replace(needle,helper+needle).replace('\n','\r\n').encode('utf-8'));after=p.read_bytes();(d/'direct-test-edit.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before=hashlib.sha256(b).hexdigest(),after=hashlib.sha256(after).hexdigest(),bytes_before=len(b),bytes_after=len(after),lines_before=len(b.splitlines()),lines_after=len(after.splitlines())),indent=2),encoding='utf-8')
# Capture exact original cancellation and verify it in the multi-history active fixture.
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';s=p.read_bytes().decode('utf-8').replace('\r\n','\n');needle='        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;';assert s.count(needle)==1
s=s.replace(needle,'''        public List<(string JobId, BgiJobTerminalPolling.FrozenIdentity Identity)> OriginalCancels { get; } = [];
        public Task CancelOriginalJobAsync(string jobId, BgiJobTerminalPolling.FrozenIdentity identity, CancellationToken ct)
        {
            OriginalCancels.Add((jobId, identity));
            return Task.CompletedTask;
        }

'''+needle)
needle='                        else Assert.Equal(HostActionStatus.Effective, first.Status);';assert s.count(needle)==1
s=s.replace(needle,needle+'''
                        if (scenario == "history-multi3-active")
                        {
                            var cancel = Assert.Single(port.OriginalCancels);
                            Assert.Equal(sub.JobId, cancel.JobId);
                            Assert.Equal(new BgiJobTerminalPolling.FrozenIdentity(sub.Epoch, sub.Key, run.WireRunId,
                                sub.NodeId, sub.LoopIteration, sub.Occurrence, sub.Attempt), cancel.Identity);
                            Assert.True(after.RecoveryAssociations.Single().ObservedExecution!.ExecutionExitConfirmed);
                        }
''')
p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
