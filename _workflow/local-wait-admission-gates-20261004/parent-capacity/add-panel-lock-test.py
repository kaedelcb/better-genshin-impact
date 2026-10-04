from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'); b=p.read_bytes()
needle=b'public class TaskCenterSuccessorPathGateTests\r\n{\r\n'
test='''    [Fact]
    public async Task OriginalHost_PanelStopReturnsWhileOriginalTerminalWritebackWaitsForGate()
    {
        var root = NewRoot("panel-stop-lock-");
        try
        {
            await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(host)!;
                    var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(host)!;
                    var gate = (SemaphoreSlim)typeof(ArbitrationAdmissionService).GetField("_gate", fields)!.GetValue(facade)!;
                    var run = runs.CreateRun("wf-panel-stop-fixture", "rev-1");
                    run.State = WorkflowRunState.Cancelled;
                    run.StopRequested = true;
                    runs.Update(run);
                    // Controlled original accepted parent responsibility; no external job/send is fabricated.
                    var identity = Guid.NewGuid().ToString("N");
                    var read = store.Read();
                    Assert.True(store.MutateHandoffLatest(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, file =>
                    {
                        file.Handoff!.Operations.Add(new OperationRecord
                        {
                            RequestIdentity = identity, RunBinding = run.RunId,
                            OperationType = OperationType.FlowRegistration, Intent = "start",
                            ResourceRef = "flow:" + run.WorkflowId, TargetEpoch = "9:900",
                            RequestState = OperationRequestState.Accepted, Zone = OperationZone.Active,
                            SubmissionIdentity = "sub:" + identity + ":1", LastSendSeq = 1,
                            Candidate = new ArbitrationCandidate { WorkflowId = run.WorkflowId, RunId = run.RunId,
                                Scope = "bgi:local:9:900", ResourceRef = "flow:" + run.WorkflowId, Intent = "start" },
                        });
                        return null;
                    }).Success);
                    var panel = new MultiplayerHoeingAssistant.ViewModels.TaskCenterPanelViewModel(host, autoRefresh: false);
                    var vm = MultiplayerHoeingAssistant.ViewModels.ActiveRunVm.Build(run, host);
                    var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var thread = new Thread(() =>
                    {
                        try { panel.StopRunCommand.Execute(vm); invoked.TrySetResult(); }
                        catch (Exception ex) { invoked.TrySetException(ex); }
                    }) { IsBackground = true };
                    await gate.WaitAsync();
                    bool returnedWhileHeld;
                    try
                    {
                        thread.Start();
                        returnedWhileHeld = await Task.WhenAny(invoked.Task, Task.Delay(1500)) == invoked.Task;
                        Assert.Equal(OperationRequestState.Accepted, store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == identity).RequestState);
                    }
                    finally { gate.Release(); }
                    await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    for (var i = 0; i < 200 && string.IsNullOrEmpty(panel.StatusMessage); i++) await Task.Delay(10);
                    Assert.True(returnedWhileHeld, "panel Stop command must return while original terminal writeback is waiting");
                    Assert.False(string.IsNullOrEmpty(panel.StatusMessage));
                    var settled = store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == identity);
                    Assert.Equal(OperationRequestState.TerminalCompleted, settled.RequestState);
                    Assert.Equal("sub:" + identity + ":1", settled.SubmissionIdentity);
                    Assert.Equal(WorkflowRunState.Cancelled, runs.Load(run.RunId)!.State);
                });
        }
        finally { TryDelete(root); }
    }

'''
assert b.count(needle)==1
p.write_bytes(b.replace(needle,needle+test.replace('\n','\r\n').encode()))
