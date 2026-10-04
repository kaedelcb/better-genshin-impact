from pathlib import Path
import json,hashlib
r=Path.cwd(); d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1'
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']
facts=[]
for p in paths:
 b=(r/p).read_bytes(); facts.append(dict(path=p,bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')))
(d/'capacity-source-before.json').write_text(json.dumps(facts,indent=2))
p=r/paths[1]; b=p.read_bytes(); s=b.decode('utf-8'); newline='\r\n' if b.count(b'\r\n') else '\n'; s=s.replace('\r\n','\n')
anchor='    [Fact]\n    public async Task NodeSubmit_33NodeFlow_NoCapacityExhaustion()'
assert s.count(anchor)==1
test='''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capacity_33ActualAdmissions_OriginalIdentitySurvivesArchiveAndReopen(bool handoff)
    {
        var root = NewRoot("tc-cap-original-");
        try
        {
            var before = new List<LeaseHandoffSegment>();
            var results = new List<(string Node, AdmissionResultKind Kind, string Reason, string Request, string? Identity, int Seq)>();
            var store = new ArbitrationLeaseStore(Path.Combine(root, "arbitration"));
            Action<string, AdmissionResultKind, string, string, string?, int> observe =
                (node, kind, reason, request, identity, seq) => results.Add((node, kind, reason, request, identity, seq));
            var probe = await ProbeNodeSubmitRoutingAsync(root, true,
                nodeIds: Enumerable.Range(1, 33).Select(i => "n-" + i).ToArray(),
                startViaHandoff: handoff,
                beforeSuccessorAdmission: () =>
                {
                    var read = store.Read();
                    Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
                    before.Add(read.File!.Handoff!);
                    return Task.CompletedTask;
                },
                configureSeams: seams => typeof(TaskCenterAdmissionSeams)
                    .GetField("SuccessorAdmissionObservedForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(seams, observe),
                afterConverged: async (host, runs, port, boundary) =>
                {
                    Assert.Equal(33, results.Count);
                    Assert.Equal(33, before.Count);
                    var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost)
                        .GetField("_admission", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                    for (var i = 0; i < 33; i++)
                    {
                        var returned = results[i];
                        Assert.Equal("n-" + (i + 1), returned.Node);
                        Assert.Equal(AdmissionResultKind.Accepted, returned.Kind);
                        Assert.False(string.IsNullOrWhiteSpace(returned.Reason));
                        Assert.DoesNotContain("operations_capacity_full", returned.Reason);
                        Assert.False(string.IsNullOrWhiteSpace(returned.Identity));
                        Assert.Equal(1, returned.Seq);
                        Assert.True(before[i].Operations.Count(o => o.Zone is OperationZone.Active or OperationZone.TerminalPendingTransfer) < ArbitrationAdmissionService.PrimarySlotLimit);
                        var previous = before[i].Operations.Where(o => o.OperationType == OperationType.NodeExecution).ToList();
                        Assert.Equal(i, previous.Count);
                        Assert.All(previous, o =>
                        {
                            Assert.Equal(OperationRequestState.TerminalCompleted, o.RequestState);
                            Assert.Equal(OperationZone.Tombstone, o.Zone);
                            Assert.False(string.IsNullOrEmpty(o.TerminalReleaseEvidence));
                        });
                    }
                    Assert.Equal(33, results.Select(o => o.Request).Distinct().Count());
                    Assert.Equal(33, results.Select(o => o.Identity).Distinct().Count());
                    LeaseReadResult terminal = store.Read();
                    for (var i = 0; i < 200 && terminal.File!.Handoff!.Operations.Any(o => o.RequestState != OperationRequestState.TerminalCompleted); i++)
                    {
                        await Task.Delay(10);
                        terminal = store.Read();
                    }
                    var originals = terminal.File!.Handoff!.Operations.ToList();
                    Assert.Equal(handoff ? 33 : 34, originals.Count);
                    Assert.All(originals, o => Assert.Equal(OperationRequestState.TerminalCompleted, o.RequestState));
                    var nodes = originals.Where(o => o.OperationType == OperationType.NodeExecution).ToList();
                    var parent = JsonSerializer.Serialize(nodes[0].ParentSource);
                    Assert.All(nodes, o => Assert.Equal(parent, JsonSerializer.Serialize(o.ParentSource)));
                    foreach (var result in results)
                    {
                        var original = Assert.Single(nodes.Where(o => o.RequestIdentity == result.Request));
                        Assert.Equal(result.Identity, original.SubmissionIdentity);
                        Assert.Equal(result.Seq, original.LastSendSeq);
                        Assert.NotNull(original.ExecutionResult);
                        Assert.False(string.IsNullOrEmpty(original.TerminalReleaseEvidence));
                    }
                    var lease = terminal.File.Lease!;
                    var aged = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                    {
                        foreach (var op in file.Handoff!.Operations) op.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-25);
                        return null;
                    });
                    Assert.True(aged.Success, aged.Reason);
                    var agedJson = store.Read().File!.Handoff!.Operations.ToDictionary(o => o.RequestIdentity, o => JsonSerializer.Serialize(o));
                    facade.RecoverAfterRestart(); // actual migration/retention algorithm, not fixture-made archive rows
                    var archived = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read();
                    Assert.Equal(ArbitrationLeaseStatus.Valid, archived.Status);
                    Assert.Empty(archived.File!.Handoff!.Operations);
                    Assert.Equal(agedJson.Count, archived.File.Handoff.ArchivedOperations.Count);
                    foreach (var entry in archived.File.Handoff.ArchivedOperations)
                        Assert.Equal(agedJson[entry.Operation.RequestIdentity], JsonSerializer.Serialize(entry.Operation));
                    var sends = port.SendCount;
                    foreach (var op in nodes)
                    {
                        var replay = await facade.SubmitAsync(new AdmissionRequest
                        {
                            Kind = AdmissionKind.ContinueUse, RequestIdentity = op.RequestIdentity,
                            Candidate = op.Candidate!, ParentSource = op.ParentSource,
                            RunBinding = op.RunBinding, OperationType = op.OperationType,
                        });
                        Assert.Equal("stale_operation_identity", replay.ReasonCode);
                    }
                    facade.RecoverAfterRestart();
                    var reopened = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read();
                    Assert.Equal(ArbitrationLeaseStatus.Valid, reopened.Status);
                    Assert.Empty(reopened.File!.Handoff!.Operations);
                    foreach (var entry in reopened.File.Handoff.ArchivedOperations)
                        Assert.Equal(agedJson[entry.Operation.RequestIdentity], JsonSerializer.Serialize(entry.Operation));
                    Assert.Equal(sends, port.SendCount);
                    var runId = nodes[0].RunBinding!;
                    var reloaded = new RunStore(Path.Combine(root, "runs")).Load(runId)!;
                    Assert.Equal(WorkflowRunState.Succeeded, reloaded.State);
                    Assert.Equal(33, reloaded.NodeOutcomes.Count);
                });
            Assert.True(probe.Converged, Diag("33 original admissions must converge", probe));
            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(33, probe.SendCount);
        }
        finally { TryDelete(root); }
    }

'''
s=s.replace(anchor,test+anchor); out=s.replace('\n',newline).encode('utf-8'); assert len(out)>len(b)
p.write_bytes(out)
