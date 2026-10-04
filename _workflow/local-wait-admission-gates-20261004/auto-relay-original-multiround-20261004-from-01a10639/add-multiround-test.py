from pathlib import Path
import hashlib,json
r=Path.cwd(); d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639'
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
b=p.read_bytes(); s=b.decode('utf-8'); marker='    // ── ① 路径启用门'
assert s.count(marker)==1
addition=r'''
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest(bool handoff)
    {
        var root = NewRoot("original-multiround-");
        TaskCenterHost? originalHost = null;
        RoutingFakePort? originalPort = null;
        var hostReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloads = new List<string>();
        var observations = new List<string>();
        var sequence = new List<int>();
        var failures = new List<string>();
        var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, true, startViaHandoff: handoff,
                beforeSuccessorAdmission: () => hostReady.Task,
                afterRegistered: (host, runs, port) =>
                {
                    originalHost = host; hostReady.TrySetResult(); return Task.CompletedTask;
                },
                configurePort: port =>
                {
                    originalPort = port;
                    port.SendThrows = new BgiNotSentException(BgiNotSentException.ChannelNotReady, "original first round: no bytes");
                },
                onBeforeSendWithPayload: (_, _, payload) => payloads.Add(payload!),
                configureSeams: seams => seams.SuccessorAdmissionObservedForTest = (_, kind, reason, requestId, identity, seq) =>
                {
                    if (seq != 1) return;
                    try
                    {
                        Assert.Equal(AdmissionResultKind.RetryableRejected, kind);
                        var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(originalHost!)!;
                        var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(originalHost!)!;
                        var runs = new RunStore(Path.Combine(root, "runs"));
                        var originalRun = Assert.Single(runs.List());
                        var originalSub = originalRun.CurrentSubmission!;
                        var firstPermit = originalSub.SendPermit!;
                        var firstProof = originalSub.LocalNoSendProof;
                        var parent = originalRun.AdmissionParentSource;
                        var firstOp = store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == requestId);
                        var deadline = firstOp.RetryWindowDeadlineUtc;
                        sequence.Add(seq);
                        observations.Add(JsonSerializer.Serialize(originalRun));
                        var second = facade.RetryAsync(requestId).GetAwaiter().GetResult();
                        Assert.True(second.Kind == AdmissionResultKind.RetryableRejected, JsonSerializer.Serialize(second));
                        Assert.Equal(2, second.SendSeq);
                        sequence.Add(second.SendSeq);
                        var afterSecond = runs.Load(originalRun.RunId)!;
                        observations.Add(JsonSerializer.Serialize(afterSecond));
                        Assert.NotEqual(firstPermit.Nonce, afterSecond.CurrentSubmission!.SendPermit!.Nonce);
                        Assert.Equal(firstPermit, Assert.Single(afterSecond.CurrentSubmission.PreviousSendRounds!).Permit);
                        Assert.Equal(firstProof, afterSecond.CurrentSubmission.PreviousSendRounds[0].Proof);
                        originalPort!.SendThrows = null;
                        var third = facade.RetryAsync(requestId).GetAwaiter().GetResult();
                        Assert.True(third.Kind == AdmissionResultKind.Accepted, JsonSerializer.Serialize(third));
                        Assert.Equal(3, third.SendSeq);
                        sequence.Add(third.SendSeq);
                        var afterThird = runs.Load(originalRun.RunId)!;
                        observations.Add(JsonSerializer.Serialize(afterThird));
                        var sub = afterThird.CurrentSubmission!;
                        Assert.Equal(parent, afterThird.AdmissionParentSource);
                        Assert.Equal(originalSub.Key, sub.Key);
                        Assert.Equal(originalSub.ExpiresAtUtc, sub.ExpiresAtUtc);
                        Assert.Equal(originalSub.OriginalRequestEvidence, sub.OriginalRequestEvidence);
                        Assert.Equal(2, sub.PreviousSendRounds!.Count);
                        Assert.Equal(3, sub.PreviousSendRounds.Select(x => x.Permit.Nonce).Append(sub.SendPermit!.Nonce).Distinct().Count());
                        var current = store.Read().File!.Handoff!;
                        var op = current.Operations.Single(o => o.RequestIdentity == requestId);
                        Assert.Equal(2, op.RejectedSendRounds!.Count);
                        Assert.Equal(deadline, op.RetryWindowDeadlineUtc);
                        Assert.Null(current.Submission);
                        Assert.Equal(third.SubmissionIdentity, TaskCenterHost.ResolveOriginalNodeSendIdentity(afterThird, sub, current)!.SubmissionIdentity);
                    }
                    catch (Exception ex) { failures.Add(ex.ToString()); }
                });
            Assert.Empty(failures);
            Assert.Equal(new[] { 1, 2, 3 }, sequence);
            Assert.Equal(3, payloads.Count);
            Assert.Single(payloads.Distinct(StringComparer.Ordinal));
            Assert.Equal(3, probe.SendCount);
            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            var final = new RunStore(Path.Combine(root, "runs")).List().Single();
            Assert.True(TerminalReleaseEvidence.ValidRunSeal(final));
            Assert.Equal("sub:" + probe.Ops.Single(o => o.OperationType == OperationType.NodeExecution).RequestIdentity + ":3", final.CurrentSubmission!.AcceptedSendIdentity);
            var evidenceDir = Environment.GetEnvironmentVariable("BGI_MULTIROUND_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(evidenceDir)) File.WriteAllText(Path.Combine(evidenceDir, handoff + "-multiround.json"), JsonSerializer.Serialize(new { payloads, observations, sequence, final, probe }));
        }
        finally { hostReady.TrySetResult(); TryDelete(root); }
    }

'''
addition=addition.replace('\n','\r\n'); s=s.replace(marker,addition+marker)
p.write_bytes(s.encode('utf-8')); assert len(p.read_bytes())>len(b)
(d/'test-edit-observation.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before_bytes=len(b),after_bytes=p.stat().st_size,before_sha256=hashlib.sha256(b).hexdigest(),after_sha256=hashlib.sha256(p.read_bytes()).hexdigest()),indent=2),encoding='utf-8')
