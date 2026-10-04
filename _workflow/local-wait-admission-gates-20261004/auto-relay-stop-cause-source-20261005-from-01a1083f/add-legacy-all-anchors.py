from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b'\r\n' in b else '\n';s=s.replace('\r\n','\n')
marker='    [Theory]\n    [InlineData("missing")]\n    [InlineData("key")]'
assert s.count(marker)==1
t='''    [Fact]
    public async Task LegacyTerminalStop_AllNewAnchorsMissingStillRetainsActuallySentResponsibility()
    {
        var root = NewRoot("legacy-no-anchors-");
        try
        {
            await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var run = Assert.Single(runs.List());
                    Assert.Equal(WorkflowRunState.Succeeded, run.State);
                    Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)));
                    var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                    Assert.True(SpinWait.SpinUntil(() => store.Read().File!.Handoff!.Operations
                        .Where(o => o.RunBinding == run.RunId).All(o => o.RequestState == OperationRequestState.TerminalCompleted), TimeSpan.FromSeconds(10)));
                    var runPath = Path.Combine(root, "runs", run.RunId + ".run.json");
                    var leasePath = Path.Combine(root, "arbitration", "arbitration-lease.json");
                    var originalRun = File.ReadAllBytes(runPath); var originalLease = File.ReadAllBytes(leasePath);
                    var legacy = JsonSerializer.Deserialize<WorkflowRunRecord>(originalRun)!;
                    legacy.AdmissionMappings = null; legacy.AdmissionParentSource = null; legacy.LocalWaitDecision = null;
                    legacy.TerminalRelease = null; legacy.NodeReleaseSeals.Clear(); legacy.RecoveryAssociations.Clear();
                    var sub = Assert.Single(TerminalReleaseEvidence.Submissions(legacy));
                    sub.SendPermit = null; sub.AcceptedSendIdentity = null; sub.OriginalRequestEvidence = null;
                    foreach (var outcome in legacy.NodeOutcomes) outcome.AcceptedSendIdentity = null;
                    Assert.True(sub.SendAttempted); Assert.NotNull(sub.JobId);
                    var lost = System.Text.Json.Nodes.JsonNode.Parse(originalLease)!;
                    lost["handoff"]!["operations"] = new System.Text.Json.Nodes.JsonArray();
                    lost["handoff"]!["archivedOperations"] = new System.Text.Json.Nodes.JsonArray();
                    lost["handoff"]!["preObservations"] = new System.Text.Json.Nodes.JsonArray();
                    try
                    {
                        File.WriteAllText(runPath, JsonSerializer.Serialize(legacy));
                        File.WriteAllBytes(leasePath, JsonSerializer.SerializeToUtf8Bytes(lost));
                        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);
                        Assert.True(TerminalReleaseEvidence.RunSettled(runs.Load(run.RunId)!));
                        var reconcile = typeof(TaskCenterHost).GetMethod("ReconcileAdmissionTerminalForExplicitStopAsync",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        var stopped = await (Task<HostActionResult>)reconcile.Invoke(host, [run.RunId, "legacy terminal probe"])!;
                        Assert.Equal(HostActionStatus.Unavailable, stopped.Status);
                        Assert.Equal(1, port.SendCount);
                    }
                    finally { File.WriteAllBytes(runPath, originalRun); File.WriteAllBytes(leasePath, originalLease); }
                    var retry = typeof(TaskCenterHost).GetMethod("ReconcileAdmissionTerminalForExplicitStopAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    Assert.Equal(HostActionStatus.Effective, (await (Task<HostActionResult>)retry.Invoke(host, [run.RunId, "original restored"])!).Status);
                    Assert.Equal(1, port.SendCount);
                });
        }
        finally { TryDelete(root); }
    }

'''
p.write_bytes(s.replace(marker,t+marker).replace('\n',nl).encode())
