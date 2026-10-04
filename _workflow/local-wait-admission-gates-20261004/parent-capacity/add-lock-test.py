from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b=p.read_bytes()
needle=b'public class TaskCenterSuccessorPathGateTests\r\n{\r\n'
addition='''    [Fact]
    public async Task OriginalHost_TerminalSweepYieldsWhileFacadeGateIsHeld()
    {
        var root = NewRoot("terminal-sweep-lock-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(host)!;
                    var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(host)!;
                    var gate = (SemaphoreSlim)typeof(ArbitrationAdmissionService).GetField("_gate", fields)!.GetValue(facade)!;
                    var read = store.Read();
                    var original = read.File!.Handoff!.Operations.Single(o => o.OperationType == OperationType.NodeExecution);
                    // Reproduce a durable terminal run whose original accepted operation still awaits writeback.
                    Assert.True(store.MutateHandoffLatest(read.File.Lease!.LeaseId, read.File.Lease.OwnerEpoch, file =>
                    {
                        var op = file.Handoff!.Operations.Single(o => o.RequestIdentity == original.RequestIdentity);
                        op.RequestState = OperationRequestState.Accepted;
                        op.Zone = OperationZone.Active;
                        return null;
                    }).Success);
                    var method = typeof(TaskCenterHost).GetMethod("SweepTerminalNodeOperations", fields)!;
                    var invoked = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Exception? error = null;
                    await gate.WaitAsync();
                    var thread = new Thread(() =>
                    {
                        try { invoked.TrySetResult(method.Invoke(host, [original.RunBinding!])); }
                        catch (Exception ex) { error = ex; invoked.TrySetException(ex); }
                    }) { IsBackground = true };
                    bool returnedWhileHeld;
                    object? result;
                    try
                    {
                        thread.Start();
                        returnedWhileHeld = await Task.WhenAny(invoked.Task, Task.Delay(1500)) == invoked.Task;
                        Assert.Equal(OperationRequestState.Accepted,
                            store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == original.RequestIdentity).RequestState);
                    }
                    finally { gate.Release(); }
                    result = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (result is Task completion) await completion.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Null(error);
                    Assert.True(returnedWhileHeld, "terminal sweep must yield instead of blocking its caller on the facade gate");
                    Assert.IsAssignableFrom<Task>(result);
                    var settled = store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == original.RequestIdentity);
                    Assert.Equal(OperationRequestState.TerminalCompleted, settled.RequestState);
                    Assert.Equal(original.SubmissionIdentity, settled.SubmissionIdentity);
                    Assert.Equal(original.LastSendSeq, settled.LastSendSeq);
                    Assert.Equal(original.TerminalReleaseEvidence, settled.TerminalReleaseEvidence);
                });
            Assert.True(probe.Converged, Diag("lock contention fixture must finish its actual host run", probe));
            Assert.Equal(1, probe.SendCount);
        }
        finally { TryDelete(root); }
    }

'''
assert b.count(needle)==1
p.write_bytes(b.replace(needle,needle+addition.replace('\n','\r\n').encode()))
