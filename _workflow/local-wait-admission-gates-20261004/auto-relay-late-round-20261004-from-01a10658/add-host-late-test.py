from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs';b=p.read_bytes()
assert b'OriginalLateRound_HostArchivedResponsibility' not in b
(d/'external-host-test-before.cs').write_bytes(b)
code=r'''
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OriginalLateRound_HostArchivedResponsibility(bool archiveBeforeReceipt, bool archiveAfterTerminal)
    {
        var root = NewRoot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ExternalStartExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ExternalStartCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCenterHost? oldHost = null, currentHost = null, reopenedHost = null;
        Task<ExternalStartAdmissionOutcome>? first = null;
        var sends = 0;
        var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        ArbitrationAdmissionService Facade(TaskCenterHost host)
            => (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(host)!;
        ArbitrationLeaseStore Store(TaskCenterHost host)
            => (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(host)!;
        void Archive(TaskCenterHost host, string requestIdentity)
        {
            var store = Store(host); var lease = store.Read().File!.Lease!;
            var result = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op = file.Handoff!.Operations.Single(o => o.RequestIdentity == requestIdentity);
                Assert.False(op.ConflictPending);
                Assert.True(op.RequestState is OperationRequestState.TerminalRejected or OperationRequestState.TerminalCompleted);
                op.UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-25);
                file.Handoff.ArchivedOperations.Add(new() { Operation = op, ArchivedAtUtc = DateTimeOffset.UtcNow });
                file.Handoff.Operations.Remove(op);
                return null;
            });
            Assert.True(result.Success, result.Reason);
            Assert.Equal(ArbitrationLeaseStatus.Valid, new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().Status);
        }
        try
        {
            oldHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            first = oldHost.AdmitExternalStartAsync(Request(_ =>
            {
                Interlocked.Increment(ref sends); entered.TrySetResult(); return release.Task;
            }, observer: async _ => { observed.TrySetResult(); return await completion.Task; }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = ReadSubmission(root)!;
            var rid = Assert.Single(Ops(root)).RequestIdentity;
            await oldHost.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            currentHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900", SenderOverride = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Rejected("original-second-rejected", false, "ext:task.queue"));
            } });
            var takeover = await currentHost.SubmitExternalStartViaAdmissionAsync(new ExternalStartAdmissionRequest
            {
                RequestIdentity = rid, Namespace = "v2", WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}", ResourceRef = "group:测试组", SourceDetail = "fixture:original-late-round",
                ExecuteAsync = _ => { Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.RejectedWith("original-second-rejected", evidenceSource: "ext:task.queue")); },
            }, default);
            Assert.NotEqual(AdmissionResultKind.Accepted, takeover.Kind);
            var facade = Facade(currentHost);
            // Controlled authoritative adapter observation contradicts the later first-round Accepted.
            var noAccept = await facade.SettleReconciledAsync(rid, new ReconcileSettlement.NotAccepted(
                original.SubmissionIdentity, original.SendSeq, "original-not-accepted", true, "ext:reconcile"));
            Assert.Equal(AdmissionResultKind.RetryableRejected, noAccept.Kind);
            var second = await facade.RetryAsync(rid);
            Assert.True(second.Kind == AdmissionResultKind.TerminalRejected, second.ReasonCode + "/" + second.Detail);
            Assert.Equal(2, second.SendSeq); Assert.Equal(2, sends);
            var rejected = System.Text.Json.JsonSerializer.Serialize(Assert.Single(Ops(root)).LastResult);
            if (archiveBeforeReceipt) Archive(currentHost, rid);
            release.TrySetResult(ExternalStartExecution.AcceptedWith("original-late-job", "ext:task.queue"));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ledger = new ExternalStartLedger(root);
            var receipt = Assert.Single(ledger.Read().File!.Entries);
            Assert.Equal(original.SubmissionIdentity, receipt.SubmissionIdentity); Assert.Equal(1, receipt.SendSeq);
            Assert.Equal(OperationType.ExternalStart, receipt.OperationType); Assert.Equal("9:900", receipt.TargetBgiEpoch);
            var recovery = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(recovery.HistoricalAcceptanceReceiptsHeld == 1, recovery.ToString());
            var held = Assert.Single(Ops(root)); Assert.True(held.ConflictPending);
            Assert.Equal("AcceptedAwaitingTerminal", held.ConflictResolutionState);
            Assert.Equal(2, held.LastSendSeq); Assert.Null(held.ExecutionResult);
            Assert.Equal(rejected, System.Text.Json.JsonSerializer.Serialize(held.LastResult));
            Assert.Contains(held.ConflictEvidence, e => e.SendSeq == 1 && e.SubmissionIdentity == original.SubmissionIdentity && e.JobId == "original-late-job" && e.RawTerminal == "accepted_receipt");
            Assert.Equal(AdmissionResultKind.NeedReconcile, (await facade.RetryAsync(rid)).Kind); Assert.Equal(2, sends);
            var terminalAt = DateTimeOffset.UtcNow;
            completion.TrySetResult(ExternalStartCompletion.SucceededWith("completed", "ext:task.event", terminalAt, "original-late-job"));
            var stale = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ResponsibilityState.Pending, stale.ResponsibilityState);
            var terminal = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(terminal.HistoricalAcceptanceTerminalsFinalized == 1, terminal.ToString());
            var final = Assert.Single(Ops(root)); Assert.False(final.ConflictPending);
            Assert.Equal(OperationRequestState.TerminalCompleted, final.RequestState);
            Assert.Equal(original.SubmissionIdentity, final.ExecutionResult!.SubmissionIdentity);
            Assert.Equal(1, final.ExecutionResult.SendSeq); Assert.Equal("original-late-job", final.ExecutionResult.JobId);
            Assert.Equal(rejected, System.Text.Json.JsonSerializer.Serialize(final.LastResult));
            Assert.Equal("ResolvedHistoricalAcceptedTerminal", final.ConflictResolutionState);
            var audit = System.Text.Json.JsonSerializer.Serialize(Store(currentHost).Read().File!.Handoff!.ConflictResolutionAudits);
            if (archiveAfterTerminal) Archive(currentHost, rid);
            var replay = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(replay.ScanFactConflicts == 0, "original settled archived terminal replay: " + replay);
            await currentHost.ShutdownAsync();
            reopenedHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            await reopenedHost.SubmitExternalStartViaAdmissionAsync(new ExternalStartAdmissionRequest
            {
                RequestIdentity = rid, Namespace = "v2", WorkflowId = "group:测试组", TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "group:测试组", SourceDetail = "fixture:original-replay",
                ExecuteAsync = _ => { Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-resend")); },
            }, default);
            var all = Store(reopenedHost).Read().File!.Handoff!;
            var retained = Assert.Single(all.Operations.Concat(all.ArchivedOperations.Select(a => a.Operation)));
            Assert.False(retained.ConflictPending); Assert.Equal(original.SubmissionIdentity, retained.ExecutionResult!.SubmissionIdentity);
            Assert.Equal(audit, System.Text.Json.JsonSerializer.Serialize(all.ConflictResolutionAudits));
            Assert.Equal(2, sends); Assert.Null(all.Submission);
            var path = Environment.GetEnvironmentVariable("BGI_LATE_ROUND_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, $"host-late-{archiveBeforeReceipt}-{archiveAfterTerminal}.json"), System.Text.Json.JsonSerializer.Serialize(new { original, receipt, terminal = ledger.Read(), replay, handoff = all, sends }));
            }
        }
        finally
        {
            release.TrySetResult(ExternalStartExecution.AcceptedWith("original-late-job", "ext:task.queue"));
            completion.TrySetResult(ExternalStartCompletion.SucceededWith("completed", "ext:task.event", DateTimeOffset.UtcNow, "original-late-job"));
            if (first is not null) { try { await first.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
            if (oldHost is not null) await oldHost.ShutdownAsync();
            if (currentHost is not null) await currentHost.ShutdownAsync();
            if (reopenedHost is not null) await reopenedHost.ShutdownAsync();
            TryDelete(root);
        }
    }

'''
newline='\r\n' if b.count(b'\r\n') > len(b.splitlines())//2 else '\n'
code=code.replace('\n',newline).encode('utf-8');needle=('    [Fact]'+newline+'    public async Task StaleOwner_AppendsLateAcceptedTerminal').encode('utf-8')
assert b.count(needle)==1
after=b.replace(needle,code+needle);p.write_bytes(after)
(d/'test-edit-observation.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before=dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest()),after=dict(bytes=len(after),lines=len(after.splitlines()),sha256=hashlib.sha256(after).hexdigest())),indent=2),encoding='utf-8')
print(len(b),len(after))
