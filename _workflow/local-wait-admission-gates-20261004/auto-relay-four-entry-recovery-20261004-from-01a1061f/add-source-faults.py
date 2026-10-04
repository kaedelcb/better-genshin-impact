from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');s=p.read_bytes().decode().replace('\r\n','\n')
anchor='''    public async Task OriginalLifecycle_RealPausedAndNewHostInterruptedPreserveOriginalSend(bool handoff, bool interrupted)
    {'''
assert s.count(anchor)==1
s=s.replace(anchor,'''    public Task OriginalLifecycle_RealPausedAndNewHostInterruptedPreserveOriginalSend(bool handoff, bool interrupted)
        => ProbeOriginalLifecycleAsync(handoff, interrupted, sourceFault: false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task OriginalLifecycle_SourceFaultRejectsResumeWithoutSendingOrReplacingOriginal(bool handoff, bool interrupted)
        => ProbeOriginalLifecycleAsync(handoff, interrupted, sourceFault: true);

    private static async Task ProbeOriginalLifecycleAsync(bool handoff, bool interrupted, bool sourceFault)
    {''')
anchor='''                    using var secondClient = new BgiExternalClient();'''
fault='''                    if (sourceFault)
                    {
                        if (handoff)
                        {
                            // Fault only the original binding in this temporary record; ordinary RunStore writers cannot make this change.
                            var path = Path.Combine(root, "runs", original.RunId + ".run.json");
                            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                            json["handoffs"]![0]!["intentKey"] = "fixture-drifted-original-intent";
                            File.WriteAllText(path, json.ToJsonString());
                        }
                        else
                        {
                            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                            var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", flags)!.GetValue(first)!;
                            var store = (ArbitrationLeaseStore)typeof(ArbitrationAdmissionService).GetField("_store", flags)!.GetValue(facade)!;
                            var lease = store.Read().File!.Lease!;
                            // Performed before releasing the old owner if this is a new-Host case (see below).
                            var changed = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, f =>
                            {
                                f.Handoff!.Operations.Single(o => o.OperationType == OperationType.FlowRegistration).Candidate!.Scope = "";
                                return null;
                            });
                            Assert.True(changed.Success, changed.Reason);
                        }
                    }
'''
# Shutdown must follow source fault mutation; keep simulated running transition before new recovery.
s=s.replace('                    if (interrupted) await first.ShutdownAsync(); // old writer is terminal before new Host\n','')
s=s.replace(anchor,fault+'                    if (interrupted) await first.ShutdownAsync(); // old writer is terminal before new Host\n'+anchor,1)
anchor='''                        var resumeTask = second.ResumeRunAsync(original.RunId);'''
s=s.replace(anchor,'''                        if (sourceFault)
                        {
                            var denied = await second.ResumeRunAsync(original.RunId);
                            Assert.Equal(HostActionStatus.Unavailable, denied.Status);
                            Assert.Contains("缺少已登记固定来源", denied.Message);
                            Assert.Equal(1, port.SendCount);
                            var retained = ReadLeaseFileWithRetry(root)!.Handoff!;
                            var retainedNode = Assert.Single(retained.Operations.Where(o => o.RequestIdentity == nodeBefore.RequestIdentity));
                            Assert.Equal(identityBefore, OriginalSendBytes(retainedNode));
                            Assert.DoesNotContain(retained.Operations, o => o.Candidate?.NodeId == "n-2");
                            Assert.Equal(outcomesBefore, JsonSerializer.Serialize(secondRuns.Load(original.RunId)!.NodeOutcomes));
                            Assert.Equal(historyBefore, JsonSerializer.Serialize(secondRuns.Load(original.RunId)!.SubmissionHistory));
                            var faultDir = Environment.GetEnvironmentVariable("BGI_LIFECYCLE_EVIDENCE_DIR");
                            if (!string.IsNullOrEmpty(faultDir)) File.WriteAllText(Path.Combine(faultDir, $"{handoff}-{interrupted}-source-fault.json"),
                                JsonSerializer.Serialize(new { handoff, interrupted, before, retained, denied, run = secondRuns.Load(original.RunId), sends = port.SendCount }));
                            return;
                        }
'''+anchor)
# Allow a deliberate lifecycle callback to stop at a real Paused boundary, without changing production behavior.
s=s.replace('or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown);','or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown\n                    || afterRegistered is not null && r.State == WorkflowRunState.Paused);')
s=s.replace('or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown)))','or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown\n                        || afterRegistered is not null && r.State == WorkflowRunState.Paused)))')
# Isolate expected final state to lifecycle method only.
a=s.index('    private static async Task ProbeOriginalLifecycleAsync');z=s.index('    public async Task OriginalLifecycle_SaturatedPendingTransfer',a)
seg=s[a:z].replace('''            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(2, probe.SendCount);''','''            Assert.Equal(sourceFault ? (interrupted ? WorkflowRunState.Interrupted : WorkflowRunState.Paused) : WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(sourceFault ? 1 : 2, probe.SendCount);''')
s=s[:a]+seg+s[z:];p.write_bytes(s.replace('\n','\r\n').encode())
