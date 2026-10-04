from pathlib import Path
import json,hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
p=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'; b=p.read_bytes()
(base/'round-host-test-before.cs.txt').write_bytes(b)
(base/'round-host-test-before.json').write_text(json.dumps(dict(path=str(p.relative_to(root)),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
anchor='''                    if (!scenario.StartsWith("third-", StringComparison.Ordinal)) return;
                    var unknown = runs.List().Single();'''
addition='''                    if (scenario == "valid")
                    {
                        await WaitForOriginalTerminalObserverAsync(host);
                        var completed = runs.List().Single();
                        var original = TerminalReleaseEvidence.Submissions(completed).Single(s => s.PreviousSendRounds is { Count: 2 });
                        Assert.True(original.NodeAdmissionRequired);
                        Assert.Equal(3, port.SendCount);
                        var lease = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(host)!;
                        var operations = lease.Read().File!.Handoff!.Operations
                            .Concat(lease.Read().File!.Handoff!.ArchivedOperations.Select(a => a.Operation)).ToList();
                        var relation = typeof(TaskCenterHost).GetMethod("OriginalAdmissionMappingsPresent", fields)!;
                        Assert.True((bool)relation.Invoke(host, [completed, operations])!);
                        var damaged = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(completed))!;
                        var historical = TerminalReleaseEvidence.Submissions(damaged).Single(s => s.Key == original.Key);
                        // Retain the actual first two no-byte rounds; remove only current anchors in this read-only fault candidate.
                        historical.NodeAdmissionRequired = false;
                        historical.SendPermit = null;
                        historical.AcceptedSendIdentity = null;
                        damaged.RecoveryAssociations.Clear();
                        var remaining = operations.Where(o => o.OperationType != OperationType.NodeExecution).ToList();
                        Assert.False((bool)relation.Invoke(host, [damaged, remaining])!, "actual prior node rounds forbid a direct-route waiver");
                        Assert.Equal(2, historical.PreviousSendRounds!.Count);
                        Assert.True((bool)relation.Invoke(host, [completed, operations])!);
                        Assert.Equal(3, port.SendCount);
                    }
                    if (!scenario.StartsWith("third-", StringComparison.Ordinal)) return;
                    var unknown = runs.List().Single();'''
nl='\r\n' if b'\r\n' in b else '\n'; old=anchor.replace('\n',nl).encode(); new=addition.replace('\n',nl).encode(); assert b.count(old)==1
p.write_bytes(b.replace(old,new)); print('actual three-round historical predicate oracle added to both valid entry cases')
