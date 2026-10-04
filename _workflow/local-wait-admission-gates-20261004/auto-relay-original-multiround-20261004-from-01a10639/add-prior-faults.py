from pathlib import Path
r=Path.cwd();p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';s=p.read_bytes().decode('utf-8')
old='    [Theory]\r\n    [InlineData(false)]\r\n    [InlineData(true)]\r\n    public async Task OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest(bool handoff)'
scenarios=['valid','missing-rejection','duplicate-rejection','bad-nonce','missing-proof','payload-drift','old-acceptance']
new='    [Theory]\r\n'+''.join(f'    [InlineData({str(h).lower()}, "{c}")]\r\n' for h in [False,True] for c in scenarios)+'    public async Task OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest(bool handoff, string scenario)'
assert s.count(old)==1;s=s.replace(old,new)
old='                        originalPort!.SendThrows = null;\r\n                        var third = facade.RetryAsync(requestId).GetAwaiter().GetResult();'
new=r'''                        originalPort!.SendThrows = null;
                        if (scenario != "valid")
                        {
                            // Raw temporary-file faults simulate corrupted/missing durable anchors; normal writers reject them.
                            if (scenario is "bad-nonce" or "missing-proof" or "payload-drift")
                            {
                                var prior = afterSecond.CurrentSubmission!.PreviousSendRounds![0];
                                afterSecond.CurrentSubmission.PreviousSendRounds[0] = scenario switch
                                {
                                    "bad-nonce" => prior with { Permit = prior.Permit with { Nonce = Guid.NewGuid().ToString("N") } },
                                    "payload-drift" => prior with { RequestEvidence = prior.RequestEvidence with { Fingerprint = new string('F', 64) } },
                                    _ => prior,
                                };
                                if (scenario == "missing-proof") afterSecond.CurrentSubmission.PreviousSendRounds.Clear();
                                File.WriteAllText(Path.Combine(root, "runs", afterSecond.RunId + ".run.json"), JsonSerializer.Serialize(afterSecond));
                            }
                            else
                            {
                                var lease = store.Read().File!.Lease!;
                                var fault = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                                {
                                    var damaged = file.Handoff!.Operations.Single(o => o.RequestIdentity == requestId);
                                    if (scenario == "missing-rejection") damaged.RejectedSendRounds!.RemoveAt(0);
                                    if (scenario == "duplicate-rejection") damaged.RejectedSendRounds!.Add(damaged.RejectedSendRounds[0]);
                                    if (scenario == "old-acceptance")
                                    {
                                        damaged.ConflictPending = true;
                                        damaged.ConflictEvidence.Add(new() { EvidenceId = "old-original-accepted", RawTerminal = "accepted_receipt", EvidenceSource = "fixture:original-server", ObservedAtUtc = DateTimeOffset.UtcNow, SubmissionIdentity = firstPermit.OriginalSendIdentity!, SendSeq = 1, JobId = "late-original-job" });
                                    }
                                    return null;
                                });
                                Assert.True(fault.Success, fault.Reason);
                            }
                            var denied = facade.RetryAsync(requestId).GetAwaiter().GetResult();
                            Assert.False(denied.Kind == AdmissionResultKind.Accepted, JsonSerializer.Serialize(denied));
                            Assert.Equal(2, originalPort.SendCount);
                            return;
                        }
                        var third = facade.RetryAsync(requestId).GetAwaiter().GetResult();'''.replace('\n','\r\n')
assert s.count(old)==1;s=s.replace(old,new)
old='            Assert.Equal(new[] { 1, 2, 3 }, sequence);'
new=r'''            if (scenario != "valid")
            {
                Assert.Equal(2, probe.SendCount);
                Assert.NotEqual(WorkflowRunState.Succeeded, probe.State);
                var retained = new RunStore(Path.Combine(root, "runs")).List().Single();
                Assert.Null(retained.CurrentSubmission!.JobId);
                return;
            }
            Assert.Equal(new[] { 1, 2, 3 }, sequence);'''.replace('\n','\r\n')
assert s.count(old)==1;s=s.replace(old,new);p.write_bytes(s.encode('utf-8'))
