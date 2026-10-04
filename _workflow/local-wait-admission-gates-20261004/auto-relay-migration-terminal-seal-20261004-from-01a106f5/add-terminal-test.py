from pathlib import Path
import hashlib,json
root=Path.cwd(); out=Path(__file__).parent
path=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
b=path.read_bytes(); (out/'TaskCenterSuccessorPathGateTests.before.cs').write_bytes(b)
(out/'test-before.json').write_text(json.dumps(dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')),indent=2))
test='''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalHost_ShutdownWaitsForTerminalWritebackBeforeLeaseRelease(bool handoff)
    {
        var root = NewRoot("terminal-shutdown-");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        try
        {
            await ProbeNodeSubmitRoutingAsync(root, true, startViaHandoff: handoff,
                beforeSuccessorAdmission: () => registered.Task,
                afterRegistered: (host, runs, port) =>
                {
                    host.AdmissionTerminalReadFaultForTest = attempt =>
                    {
                        if (attempt == 1)
                        {
                            entered.TrySetResult();
                            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("terminal barrier not released");
                        }
                        return null;
                    };
                    host.AdmissionTerminalReconciliationCompletedForTest = () => finished.TrySetResult();
                    registered.TrySetResult();
                    return Task.CompletedTask;
                },
                afterConverged: async (host, runs, port, boundary) =>
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var store = new ArbitrationLeaseStore(Path.Combine(root, "arbitration"));
                    var before = store.Read().File!;
                    var run = Assert.Single(runs.List());
                    Assert.Equal(WorkflowRunState.Succeeded, run.State);
                    Assert.Contains(before.Handoff!.Operations, op => op.RunBinding == run.RunId && op.RequestState == OperationRequestState.Accepted);
                    var evidenceDir = Environment.GetEnvironmentVariable("BGI_TERMINAL_LIFECYCLE_EVIDENCE_DIR");
                    void Save(string phase)
                    {
                        if (string.IsNullOrEmpty(evidenceDir)) return;
                        File.Copy(Path.Combine(root, "runs", run.RunId + ".run.json"), Path.Combine(evidenceDir, handoff + "-" + phase + "-run.json"), true);
                        File.Copy(Path.Combine(root, "arbitration", "arbitration-lease.json"), Path.Combine(evidenceDir, handoff + "-" + phase + "-lease.json"), true);
                    }
                    Save("before");
                    var shutdown = host.ShutdownAsync();
                    var returnedBeforeWriteback = shutdown.IsCompleted;
                    var during = store.Read().File!;
                    Save("during");
                    release.Set();
                    await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Save("after");
                    Assert.False(returnedBeforeWriteback, "Shutdown returned while original terminal writeback was held");
                    Assert.Equal(before.Lease!.LeaseId, during.Lease!.LeaseId);
                    Assert.Equal(before.Lease.OwnerEpoch, during.Lease.OwnerEpoch);
                    var final = runs.Load(run.RunId)!;
                    Assert.True(TerminalReleaseEvidence.ValidRunSeal(final));
                    Assert.All(store.Read().File!.Handoff!.Operations.Where(op => op.RunBinding == run.RunId),
                        op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
                    Assert.Equal(1, port.SendCount);
                });
        }
        finally { registered.TrySetResult(); release.Set(); TryDelete(root); }
    }

'''
needle=b'    [Fact]\r\n    public async Task OriginalHost_PanelStopReturnsWhileOriginalTerminalWritebackWaitsForGate()'
assert b.count(needle)==1
path.write_bytes(b.replace(needle,test.replace('\n','\r\n').encode()+needle))
print('added deterministic lifecycle tests',flush=True)
