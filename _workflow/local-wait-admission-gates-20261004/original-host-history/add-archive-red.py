from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');s=p.read_bytes().decode();nl='\r\n'
s=s.replace('    [InlineData("history-publish")]','    [InlineData("history-publish")]'+nl+'    [InlineData("history-archive")]'+nl+'    [InlineData("history-archive-conflict")]')
s=s.replace('if (scenario != "history-exited")','if (scenario is "history-settle" or "history-publish")',1)
a='                        await reopenedHost.ShutdownAsync();'
z='''                        if (scenario.StartsWith("history-archive", StringComparison.Ordinal))
                        {
                            var leaseStore = new ArbitrationLeaseStore(Path.Combine(root, "arbitration"));
                            var lease = leaseStore.Read().File!.Lease!;
                            var archived = leaseStore.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                            {
                                var originals = file.Handoff!.Operations.Where(o => o.RunBinding == run.RunId).ToList();
                                Assert.NotEmpty(originals);
                                foreach (var op in originals)
                                {
                                    Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
                                    op.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-25);
                                    if (scenario == "history-archive-conflict" && op.OperationType == OperationType.NodeExecution)
                                        op.TerminalReleaseEvidence = "runstore-seal:wrong-original-seal";
                                    file.Handoff.ArchivedOperations.Add(new() { Operation = op, ArchivedAtUtc = DateTimeOffset.UtcNow });
                                    file.Handoff.Operations.Remove(op);
                                }
                                return null;
                            });
                            Assert.True(archived.Success, archived.Reason);
                            Assert.Equal(ArbitrationLeaseStatus.Valid, new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().Status);
                            var archivedStop = reopenedHost.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
                            Assert.Equal(scenario == "history-archive" ? HostActionStatus.Effective : HostActionStatus.Unavailable, archivedStop.Status);
                            Assert.Equal(originalHistory, JsonSerializer.Serialize(runs.Load(run.RunId)!.SubmissionHistory));
                            Assert.Equal(1, port.SendCount);
                        }
'''.replace('\n',nl)+a
assert s.count(a)==1;s=s.replace(a,z);p.write_bytes(s.encode())
