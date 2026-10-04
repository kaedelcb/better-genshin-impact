from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs');b=p.read_bytes();s=b.decode('utf-8');assert 'OriginalLateRound_CurrentTerminalArchiveReplays' not in s
code='''    [Fact]
    public async Task OriginalLateRound_CurrentTerminalArchiveReplays()
    {
        var root = NewRoot(); TaskCenterHost? host = null, reopened = null; var sends = 0;
        var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var terminalAt = DateTimeOffset.UtcNow;
            var outcome = await host.AdmitExternalStartAsync(Request(_ =>
            {
                Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.AcceptedWith("original-current-job", "ext:task.queue"));
            }, completion: () => ExternalStartCompletion.SucceededWith("completed", "ext:task.event", terminalAt, "original-current-job")));
            Assert.Equal(ExternalStartAdmissionStatus.Accepted, outcome.Status);
            var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(host)!;
            var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(host)!;
            var original = Assert.Single(Ops(root)); Assert.Equal(OperationRequestState.TerminalCompleted, original.RequestState);
            var lease = store.Read().File!.Lease!;
            var moved = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op = Assert.Single(file.Handoff!.Operations); op.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-25);
                file.Handoff.ArchivedOperations.Add(new() { Operation = op, ArchivedAtUtc = DateTimeOffset.UtcNow });
                file.Handoff.Operations.Clear(); return null;
            });
            Assert.True(moved.Success, moved.Reason);
            var archivedBytes = System.Text.Json.JsonSerializer.Serialize(store.Read().File!.Handoff!.ArchivedOperations);
            var replay = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(replay.ScanFactConflicts == 0, "current original archived terminal replay: " + replay);
            Assert.Equal(archivedBytes, System.Text.Json.JsonSerializer.Serialize(store.Read().File!.Handoff!.ArchivedOperations));
            await host.ShutdownAsync();
            reopened = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            await reopened.SubmitExternalStartViaAdmissionAsync(new ExternalStartAdmissionRequest
            {
                RequestIdentity = original.RequestIdentity, Namespace = "v2", WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}", ResourceRef = "group:测试组", SourceDetail = "fixture:current-original-replay",
                ExecuteAsync = _ => { Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-resend")); },
            }, default);
            var final = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read();
            Assert.Equal(ArbitrationLeaseStatus.Valid, final.Status); Assert.Empty(final.File!.Handoff!.Operations);
            Assert.Equal(archivedBytes, System.Text.Json.JsonSerializer.Serialize(final.File.Handoff.ArchivedOperations));
            Assert.Equal(1, sends); Assert.Null(final.File.Handoff.Submission);
        }
        finally { if (host is not null) await host.ShutdownAsync(); if (reopened is not null) await reopened.ShutdownAsync(); TryDelete(root); }
    }

'''
needle='    [Theory]\n    [InlineData(false, false)]\n';assert s.count(needle)==1;p.write_bytes(s.replace(needle,code+needle).encode('utf-8'))
