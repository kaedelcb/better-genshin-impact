from pathlib import Path
import hashlib,json
r=Path.cwd(); d=Path(__file__).parent
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
b=p.read_bytes(); (d/'successor-tests.before').write_bytes(b)
def fact(x):return dict(bytes=len(x),lines=len(x.splitlines()),sha256=hashlib.sha256(x).hexdigest(),bom=x.startswith(b'\xef\xbb\xbf'),crlf=x.count(b'\r\n'),lf=x.count(b'\n'))
s=b.decode('utf-8').replace('\r\n','\n')
needle='    [InlineData("history-archive-conflict")]'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n    [InlineData("history-bound-valid")]\n    [InlineData("history-bound-duplicate-key")]\n    [InlineData("history-bound-duplicate-identity")]')
needle='                        run.SubmissionHistory.Add(sub); run.CurrentSubmission = null;'
assert s.count(needle)==1
s=s.replace(needle,needle+'''
                        var invalidBoundHistory = scenario is "history-bound-duplicate-key" or "history-bound-duplicate-identity";
                        if (scenario.StartsWith("history-bound-", StringComparison.Ordinal))
                        {
                            sub.AcceptedSendIdentity = originalIdentity;
                            sub.ObservedTerminal = "cancelled"; sub.EffectState = "cancelled";
                            sub.ExecutionExitConfirmed = true; sub.ExecutionExitDisposition = "execution_exited";
                            var outcome = Assert.Single(run.NodeOutcomes);
                            outcome.SubmissionKey = sub.Key; outcome.AcceptedSendIdentity = originalIdentity;
                            outcome.NodeId = sub.NodeId; outcome.Occurrence = sub.Occurrence;
                            outcome.LoopIteration = sub.LoopIteration; outcome.Attempt = sub.Attempt;
                            outcome.RawTerminal = "cancelled"; outcome.Result = "cancelled";
                            if (invalidBoundHistory)
                            {
                                var conflict = JsonSerializer.Deserialize<WorkflowNodeOutcome>(JsonSerializer.Serialize(outcome))!;
                                if (scenario == "history-bound-duplicate-key") conflict.AcceptedSendIdentity = "sub:foreign-original:1";
                                else conflict.SubmissionKey = "foreign-original-key";
                                run.NodeOutcomes.Add(conflict);
                            }
                        }
''')
needle='                        if (scenario is "history-settle" or "history-publish")'
assert s.count(needle)==1
s=s.replace(needle,'''
                        if (invalidBoundHistory)
                        {
                            Assert.True(first.Status == HostActionStatus.Unavailable,
                                "history outcome uniqueness must reject " + scenario + ": " + first.Status + "/" + first.Message);
                            Assert.Equal(WorkflowRunState.Unknown, after.State);
                            Assert.Null(after.TerminalRelease); Assert.Empty(after.NodeReleaseSeals);
                            Assert.Empty(after.RecoveryAssociations);
                            Assert.Equal(1, port.SendCount);
                            return;
                        }
'''+needle)
needle='                        Assert.Single(after.RecoveryAssociations);'
assert s.count(needle)==1
s=s.replace(needle,'                        if (scenario == "history-bound-valid") Assert.Empty(after.RecoveryAssociations);\n                        else Assert.Single(after.RecoveryAssociations);')
needle='            Assert.Equal(scenario == "stop-exited" || scenario.StartsWith("history-") ? WorkflowRunState.Cancelled : WorkflowRunState.Unknown, probe.State);'
assert s.count(needle)==1
s=s.replace(needle,'            Assert.Equal(scenario is "history-bound-duplicate-key" or "history-bound-duplicate-identity" ? WorkflowRunState.Unknown\n                : scenario == "stop-exited" || scenario.StartsWith("history-") ? WorkflowRunState.Cancelled : WorkflowRunState.Unknown, probe.State);')
out=s.replace('\n','\r\n').encode('utf-8') if b.count(b'\r\n') else s.encode('utf-8')
p.write_bytes(out)
(d/'test-edit-observation.json').write_text(json.dumps(dict(before=fact(b),after=fact(out)),indent=2),encoding='utf-8')
print(json.dumps(dict(before=fact(b),after=fact(out))))
