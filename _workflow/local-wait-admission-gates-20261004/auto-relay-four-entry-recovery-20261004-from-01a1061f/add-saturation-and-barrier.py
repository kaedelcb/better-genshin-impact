from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');s=p.read_bytes().decode().replace('\r\n','\n')
s=s.replace('''        var root = NewRoot("tc-lifecycle-");
        try''','''        var root = NewRoot("tc-lifecycle-");
        var recoveryAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        try''')
s=s.replace('''                configurePort: p => p.HoldFirstStatus = true,
                afterRegistered:''','''                configurePort: p => p.HoldFirstStatus = true,
                configureSeams: seams => seams.Barriers!.AfterAcceptBeforeLedger = async () =>
                {
                    if (Interlocked.Increment(ref acceptedCount) == (handoff ? 2 : 3))
                    {
                        recoveryAccepted.TrySetResult();
                        await releaseRecovery.Task;
                    }
                },
                afterRegistered:''')
# Same gate guard on recreated host; it has only recovery acceptance before next-node.
s=s.replace('''                        admissionSeams: new TaskCenterAdmissionSeams { Epoch = RoutingFakePort.Epoch,
                            ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store) }) : first;''','''                        admissionSeams: new TaskCenterAdmissionSeams { Epoch = RoutingFakePort.Epoch,
                            ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store),
                            Barriers = new AdmissionBarriers { AfterAcceptBeforeLedger = async () =>
                            {
                                if (!recoveryAccepted.Task.IsCompleted)
                                {
                                    recoveryAccepted.TrySetResult();
                                    await releaseRecovery.Task;
                                }
                            } } }) : first;''')
s=s.replace('''                        var resumed = await second.ResumeRunAsync(original.RunId);
                        Assert.Equal(HostActionStatus.Registered, resumed.Status);''','''                        var resumeTask = second.ResumeRunAsync(original.RunId);
                        await recoveryAccepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        for (var i = 0; i < 1000 && secondRuns.Load(original.RunId)!.CurrentSubmission?.NodeId != "n-2"; i++) await Task.Delay(10);
                        Assert.Equal("n-2", secondRuns.Load(original.RunId)!.CurrentSubmission?.NodeId);
                        var held = ReadLeaseFileWithRetry(root)!.Handoff!;
                        Assert.NotNull(held.Submission);
                        Assert.Equal(OperationType.Recovery, Assert.Single(held.Operations.Where(o => o.SubmissionIdentity == held.Submission!.SubmissionIdentity)).OperationType);
                        Assert.Equal(1, port.SendCount); // forced overlap: runner progressed, original recovery slot is still held
                        Assert.DoesNotContain(held.Operations, o => o.Candidate?.NodeId == "n-2");
                        releaseRecovery.TrySetResult();
                        var resumed = await resumeTask;
                        Assert.Equal(HostActionStatus.Registered, resumed.Status);''')
s=s.replace('''                    finally { if (interrupted) await second.ShutdownAsync(); }''','''                    finally { releaseRecovery.TrySetResult(); if (interrupted) await second.ShutdownAsync(); }''')
s=s.replace('''        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capacity_33ActualAdmissions''','''        finally { releaseRecovery.TrySetResult(); TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalLifecycle_SaturatedPendingTransferKeepsCompleteOriginalRecords(bool handoff)
    {
        var root = NewRoot("tc-pending-original-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, true,
                nodeIds: Enumerable.Range(1, 32).Select(i => "n-" + i).ToArray(), startViaHandoff: handoff,
                afterConverged: async (host, runs, port, boundary) =>
                {
                    var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(host)!;
                    var owningStore = (ArbitrationLeaseStore)typeof(ArbitrationAdmissionService).GetField("_store", fields)!.GetValue(facade)!;
                    var file = owningStore.Read().File!;
                    // Wait for actual terminal seals, then simulate a full not-yet-mature tombstone region.
                    for (var i = 0; i < 1000 && file.Handoff!.Operations.Any(o => o.RequestState != OperationRequestState.TerminalCompleted); i++)
                    { await Task.Delay(10); file = owningStore.Read().File!; }
                    Assert.All(file.Handoff!.Operations, o => Assert.Equal(OperationRequestState.TerminalCompleted, o.RequestState));
                    var saturated = owningStore.MutateHandoffLatest(file.Lease!.LeaseId, file.Lease.OwnerEpoch, f =>
                    {
                        foreach (var op in f.Handoff!.Operations.Where(o => o.OperationType == OperationType.NodeExecution))
                            op.Zone = OperationZone.TerminalPendingTransfer;
                        var count = f.Handoff.Operations.Count(o => o.Zone == OperationZone.Tombstone);
                        for (var i = count; i < ArbitrationAdmissionService.TombstoneLimit; i++)
                            f.Handoff.Operations.Add(new OperationRecord { RequestIdentity = "capacity-fixture-tomb-" + i,
                                CandidateId = "capacity-fixture-cand-" + i, RequestState = OperationRequestState.TerminalRejected,
                                Zone = OperationZone.Tombstone, UpdatedAtUtc = DateTimeOffset.UtcNow, UpdatedRevision = f.Revision + 1 });
                        return null;
                    });
                    Assert.True(saturated.Success, saturated.Reason);
                    var before = owningStore.Read().File!.Handoff!;
                    var originals = before.Operations.Where(o => o.OperationType == OperationType.NodeExecution)
                        .ToDictionary(o => o.RequestIdentity, o => JsonSerializer.Serialize(o));
                    Assert.Equal(32, originals.Count);
                    var overflow = await facade.AdmitRecoveryAsync(new RecoveryAdmissionRequest { RunId = "fixture-overflow-run", WorkflowId = "fixture-overflow-flow",
                        Scope = "bgi:local:" + RoutingFakePort.Epoch, RestoreBranch = "interrupted-relocate" });
                    Assert.Equal(AdmissionResultKind.Error, overflow.Kind);
                    Assert.StartsWith("operations_capacity_full", overflow.ReasonCode);
                    Assert.Contains("pendingTransfer=32", overflow.ReasonCode);
                    Assert.Contains("tombstone=256", overflow.ReasonCode);
                    Assert.Equal(32, port.SendCount);
                    facade.RecoverAfterRestart();
                    await host.ShutdownAsync();
                    using var client = new BgiExternalClient();
                    var second = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                        () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null), localExecutionCapability: () => true,
                        admissionWired: true, successorAdmissionWired: true,
                        admissionSeams: new TaskCenterAdmissionSeams { Epoch = RoutingFakePort.Epoch,
                            ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store) });
                    try
                    {
                        await (Task)typeof(TaskCenterHost).GetMethod("EnsureAdmissionFacadeAsync", fields)!.Invoke(second, [CancellationToken.None])!;
                        var secondFacade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(second)!;
                        secondFacade.RecoverAfterRestart();
                        var after = ReadLeaseFileWithRetry(root)!.Handoff!;
                        Assert.Equal(32, after.Operations.Count(o => o.Zone == OperationZone.TerminalPendingTransfer));
                        Assert.Equal(256, after.Operations.Count(o => o.Zone == OperationZone.Tombstone));
                        foreach (var entry in originals)
                            Assert.Equal(entry.Value, JsonSerializer.Serialize(Assert.Single(after.Operations.Where(o => o.RequestIdentity == entry.Key))));
                        Assert.Empty(after.ArchivedOperations);
                        Assert.Equal(32, port.SendCount);
                        var evidenceDir = Environment.GetEnvironmentVariable("BGI_LIFECYCLE_EVIDENCE_DIR");
                        if (!string.IsNullOrEmpty(evidenceDir)) File.WriteAllText(Path.Combine(evidenceDir, $"{handoff}-pending-original.json"),
                            JsonSerializer.Serialize(new { before, overflow, after, sends = port.SendCount,
                                limitation = "actual original sends and new Host recovery; saturation zone/tombstones fixture; no IPC/game/User acceptance" }));
                    }
                    finally { await second.ShutdownAsync(); }
                });
            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(32, probe.SendCount);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capacity_33ActualAdmissions''')
p.write_bytes(s.replace('\n','\r\n').encode())
