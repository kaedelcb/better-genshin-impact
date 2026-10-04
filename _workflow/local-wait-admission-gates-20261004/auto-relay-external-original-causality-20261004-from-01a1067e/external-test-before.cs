using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5.2 **B3：外部启动准入（E3/E4/E5）宿主级验收夹具**（owner 0 点击，场景施工方内置）：
/// ①受理全链——§2.2 兼容候选（namespace=v2/触发出现身份回填）→ 门面占位 → 适配层执行**恰好一次**
///   → 受理接管台账 `external-start-ledger.json` 落盘并可跨重启重建 → Submission 关闭；
/// ②门禁无副作用——F11 激活时**不执行**适配层启动；
/// ③确定拒绝——适配层给出关联验证后的确定未受理时关闭 Submission 且**不写**台账；
/// ④§4.2a 准入读取规则——台账中「已受理未终结」记录使执行占用成立（快照为空也不失去占用意义），
///   台账标记终局后同一请求可获准。
/// </summary>
public class TaskCenterExternalStartAdmissionTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tcext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static TaskCenterHost NewHost(string root, TaskCenterAdmissionSeams? seams, Func<ControlStatus?>? status = null)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => null, log: null, runnerFactory: null, readinessOverride: () => (true, null),
            localExecutionCapability: () => true,
            statusSnapshotProvider: status ?? (() => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false }),
            admissionWired: true, admissionSeams: seams);

    private static BgiExternalClient NewClientWithEpoch()
    {
        var client = new BgiExternalClient();
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.ServerEpoch),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(client, new BgiEpoch { ProcessId = 9, StartTicksUtc = 900 });
        return client;
    }

    private static TaskCenterHost NewProductionHost(string root, BgiExternalClient client, Func<ControlStatus?> status)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => client, localExecutionCapability: () => true, statusSnapshotProvider: status);

    private static ExternalStartAdmissionRequest Request(Func<System.Threading.CancellationToken, Task<ExternalStartExecution>> execute,
        string ns = "v2", Func<ExternalStartCompletion?>? completion = null,
        Func<System.Threading.CancellationToken, Task<ExternalStartCompletion?>>? observer = null,
        ArbitrationTier tier = ArbitrationTier.Plan, int priority = 0,
        DateTimeOffset? scheduledAt = null)
        => new()
        {
            Namespace = ns,
            WorkflowId = "group:测试组",
            TriggerOccurrenceId = ns + ":remote:{requestIdentity}",
            ResourceRef = "group:测试组",
            SourceDetail = "fixture:external_start",
            Tier = tier,
            Priority = priority,
            ScheduledAt = scheduledAt,
            ExecuteAsync = execute,
            CompletionProvider = completion,
            CompletionObserver = observer,
        };

    private static IReadOnlyList<OperationRecord> Ops(string root)
        => new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff?.Operations ?? [];

    private static SubmissionRecord? ReadSubmission(string root)
        => new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff?.Submission;

    [Fact]
    public async Task ExternalStart_TrustedTierAndPriority_AreFrozenIntoCandidate()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var result = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => Task.FromResult(ExternalStartExecution.RejectedWith("fixture_stop")),
                    ns: "system", tier: ArbitrationTier.Fixed, priority: 17,
                    scheduledAt: DateTimeOffset.Parse("2026-09-23T10:00:00+08:00")), default);

            Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
            var op = Assert.Single(Ops(root));
            Assert.Equal(ArbitrationTier.Fixed, op.Candidate!.Tier);
            Assert.Equal(17, op.Candidate.Priority);
            Assert.Equal(DateTimeOffset.Parse("2026-09-23T10:00:00+08:00"), op.Candidate.ScheduledAt);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_UnavailableTaskStatus_IsUnknownAndNeverSent()
    {
        var root = NewRoot();
        var sends = 0;
        try
        {
            using var client = NewClientWithEpoch();
            var host = NewProductionHost(root, client,
                () => new ControlStatus { TaskStatusAvailable = false, TaskRunning = false });

            var result = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ =>
                {
                    System.Threading.Interlocked.Increment(ref sends);
                    return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-send"));
                }), default);

            Assert.True(result.Kind == AdmissionResultKind.NeedReconcile,
                $"Expected NeedReconcile; got {result.Kind}/{result.ReasonCode}: {result.Detail}");
            Assert.Equal("facts_unknown", result.ReasonCode);
            Assert.Equal(0, System.Threading.Volatile.Read(ref sends));
            Assert.Null(ReadSubmission(root));
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_FreshAvailableIdleStatus_MatchingBgiEpoch_AllowsExactlyOneSend()
    {
        var root = NewRoot();
        var sends = 0;
        try
        {
            using var client = NewClientWithEpoch();
            var host = NewProductionHost(root, client, () => new ControlStatus
            {
                TaskStatusAvailable = true,
                TaskStatusBgiEpoch = "9:900",
                TaskStatusObservedAtUtc = DateTimeOffset.UtcNow,
                TaskRunning = false,
            });

            var result = await host.SubmitExternalStartViaAdmissionAsync(Request(_ =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                return Task.FromResult(ExternalStartExecution.AcceptedWith("job-fresh-idle"));
            }), default);

            Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
            Assert.Equal(1, System.Threading.Volatile.Read(ref sends));
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("9:900", -21)]
    [InlineData("8:800", 0)]
    public async Task ExternalStart_StaleOrWrongEpochStatus_IsUnknownAndNeverSent(string statusEpoch, int observedSecondsAgo)
    {
        var root = NewRoot();
        var sends = 0;
        try
        {
            using var client = NewClientWithEpoch();
            var host = NewProductionHost(root, client, () => new ControlStatus
            {
                TaskStatusAvailable = true,
                TaskStatusBgiEpoch = statusEpoch,
                TaskStatusObservedAtUtc = DateTimeOffset.UtcNow.AddSeconds(observedSecondsAgo),
                TaskRunning = false,
            });

            var result = await host.SubmitExternalStartViaAdmissionAsync(Request(_ =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-send"));
            }), default);

            Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
            Assert.Equal("facts_unknown", result.ReasonCode);
            Assert.Equal(0, System.Threading.Volatile.Read(ref sends));
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_Accepted_ExecutesOnce_RecordsLedger_ClosesSubmission()
    {
        var root = NewRoot();
        var executed = 0; // 计数经 Interlocked 访问
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });

            var result = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith("job-ext-1")); }), default);

            Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
            Assert.Equal(1, System.Threading.Volatile.Read(ref executed)); // 适配层执行恰好一次
            var op = Assert.Single(Ops(root));
            Assert.Equal("v2", op.Candidate!.Namespace);              // §2.2 兼容候选：namespace=v2
            Assert.Equal("group:测试组", op.Candidate.WorkflowId);
            Assert.Equal("group:测试组", op.ResourceRef);
            Assert.Equal("start", op.Intent);
            // §2.2/I3：`{requestIdentity}` 占位符已由门面回填为本次操作身份
            Assert.DoesNotContain("{requestIdentity}", op.Candidate.TriggerOccurrenceId);
            Assert.EndsWith(op.RequestIdentity, op.Candidate.TriggerOccurrenceId!);
            Assert.Equal("9:900", op.TargetEpoch);                    // 固定纪元（生产 epoch 形状：含冒号不被截断）
            Assert.Null(new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File!.Handoff!.Submission);

            // 受理接管台账：E3/E4/E5 的接管记录是 external-start-ledger.json（含完整发送身份、resourceRef、纪元）
            var ledger = new ExternalStartLedger(root).Read();
            Assert.True(ledger.Valid);
            var entry = Assert.Single(ledger.File!.Entries);
            Assert.Equal(op.SubmissionIdentity, entry.SubmissionIdentity);
            Assert.Equal("group:测试组", entry.ResourceRef);
            Assert.Equal("9:900", entry.TargetBgiEpoch);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution, entry.State);
            Assert.True(new ExternalStartLedger(root).ConfirmRebuildable(entry));
            await host.ShutdownAsync(); // 显式关闭（释放心跳/门面生命周期）
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_HostShutdownDuringSend_CancelsSender_KeepsUnknownResponsibility()
    {
        var root = NewRoot();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var pending = host.SubmitExternalStartViaAdmissionAsync(Request(async ct =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                sendStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ExternalStartExecution.AcceptedWith("should-not-accept");
            }), default);
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, sends);
            // 宿主关停与门面结算交错：仍持租约时可回 NeedReconcile，先释放租约则回 Reconciling；
            // 两者均保持原发送责任，不能写成确定拒绝或已受理。
            Assert.True(result.Kind is AdmissionResultKind.NeedReconcile or AdmissionResultKind.Reconciling,
                $"关闭交错返回 {result.Kind}/{result.ReasonCode}");
            Assert.NotNull(ReadSubmission(root));
            Assert.Empty(new ExternalStartLedger(root).Read().File?.Entries ?? []);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_HostShutdownDuringCompletionObservation_CancelsObserver_KeepsUnknownResponsibility()
    {
        var root = NewRoot();
        var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var pending = host.AdmitExternalStartAsync(Request(
                _ =>
                {
                    System.Threading.Interlocked.Increment(ref sends);
                    return Task.FromResult(ExternalStartExecution.AcceptedWith("job-observe-shutdown"));
                },
                observer: async ct =>
                {
                    observerStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return null;
                }), default);

            await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, sends);
            Assert.Equal(ExternalStartAdmissionStatus.NeedReconcile, result.Status);
            Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
            var ledgerEntry = new ExternalStartLedger(root).Read().File!.Entries.Single();
            Assert.Equal("job-observe-shutdown", ledgerEntry.JobId);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution, ledgerEntry.State);
            Assert.Null(Ops(root).Single().ExecutionResult);
            Assert.Null(ReadSubmission(root));
        }
        finally
        {
            TryDelete(root);
        }
    }


    [Fact]
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, true, "unconfirmed")]
    [InlineData(true, true, "wrong-job")]
    [InlineData(true, true, "wrong-terminal")]
    [InlineData(true, true, "wrong-source")]
    [InlineData(true, true, "wrong-time")]
    [InlineData(true, true, "missing-terminal")]
    public async Task OriginalLateRound_HostArchivedResponsibility(bool archiveBeforeReceipt, bool archiveAfterTerminal, string fault = "valid")
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
            if (fault != "valid")
            {
                var hooks = (AdmissionHooks)typeof(ArbitrationAdmissionService).GetField("_hooks", fields)!.GetValue(facade)!;
                var originalScan = hooks.TakeoverLedgerScan;
                var originalConfirm = hooks.TakeoverTerminalPayloadConfirmed;
                var retainedBytes = System.Text.Json.JsonSerializer.Serialize(Store(currentHost).Read().File!.Handoff);
                try
                {
                    if (fault == "unconfirmed") hooks.TakeoverTerminalPayloadConfirmed = (_, _, _, _, _, _, _, _) => false;
                    else hooks.TakeoverLedgerScan = () =>
                    {
                        var scan = originalScan(); var fact = Assert.Single(scan.Facts);
                        fact = fault switch
                        {
                            "wrong-job" => fact with { JobId = "wrong-original-job" },
                            "wrong-terminal" => fact with { RawTerminal = "cancelled", TerminalKind = ExecutionResultKind.Cancelled },
                            "wrong-source" => fact with { TerminalEvidenceSource = "wrong-original-source" },
                            "wrong-time" => fact with { TerminalObservedAtUtc = fact.TerminalObservedAtUtc!.Value.AddSeconds(1) },
                            "missing-terminal" => fact with { Terminal = false },
                            _ => throw new InvalidOperationException(fault),
                        };
                        return scan with { Facts = new[] { fact } };
                    };
                    var denied = await facade.RecoverExternalStartObservationsAsync();
                    Assert.True(denied.ScanFactConflicts == 1, "unconfirmed/conflicting archived replay must remain unresolved: " + denied);
                    Assert.Equal(retainedBytes, System.Text.Json.JsonSerializer.Serialize(Store(currentHost).Read().File!.Handoff));
                    Assert.Equal(2, sends);
                }
                finally { hooks.TakeoverLedgerScan = originalScan; hooks.TakeoverTerminalPayloadConfirmed = originalConfirm; }
            }
            var replay = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(replay.ScanFactConflicts == 0, "original settled archived terminal replay: " + replay);
            if (archiveAfterTerminal) Assert.Single(Store(currentHost).Read().File!.Handoff!.ArchivedOperations);
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
                File.WriteAllText(Path.Combine(path, $"host-late-{archiveBeforeReceipt}-{archiveAfterTerminal}-{fault}.json"), System.Text.Json.JsonSerializer.Serialize(new { original, receipt, terminal = ledger.Read(), replay, handoff = all, sends }));
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

    [Fact]
    public async Task StaleOwner_AppendsLateAcceptedTerminalToOriginalRound_WithoutFinalizingOperation()
    {
        var root = NewRoot();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<ExternalStartExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource<ExternalStartCompletion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        TaskCenterHost? staleOwner = null;
        TaskCenterHost? currentOwner = null;
        TaskCenterHost? recoveryOwner = null;
        try
        {
            staleOwner = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var staleAttempt = staleOwner.AdmitExternalStartAsync(Request(
                _ =>
                {
                    System.Threading.Interlocked.Increment(ref sends);
                    sendStarted.TrySetResult();
                    return releaseSend.Task;
                },
                observer: async _ =>
                {
                    observerStarted.TrySetResult();
                    return await releaseObserver.Task;
                }));
            await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var inFlight = Assert.IsType<SubmissionRecord>(ReadSubmission(root));
            var requestIdentity = Assert.Single(Ops(root)).RequestIdentity;

            // Simulate the old process leaving while its transport call is still waiting on a late response.
            await staleOwner.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            currentOwner = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:901" });
            var takeover = await currentOwner.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
            {
                RequestIdentity = requestIdentity,
                Namespace = "v2",
                WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "group:测试组",
                SourceDetail = "fixture:takeover_reconcile",
                ExecuteAsync = _ =>
                {
                    System.Threading.Interlocked.Increment(ref sends);
                    return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-resend"));
                },
            });
            Assert.NotEqual(ExternalStartAdmissionStatus.Accepted, takeover.Status);
            Assert.Equal(1, System.Threading.Volatile.Read(ref sends));

            releaseSend.TrySetResult(ExternalStartExecution.AcceptedWith("job-old-round", "ext:task.queue"));
            await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var acceptedReceipt = Assert.Single(new ExternalStartLedger(root).Read().File!.Entries);
            Assert.Equal(inFlight.SubmissionIdentity, acceptedReceipt.SubmissionIdentity);
            Assert.Equal(inFlight.SendSeq, acceptedReceipt.SendSeq);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution, acceptedReceipt.State);
            Assert.Equal("job-old-round", acceptedReceipt.JobId);

            var terminalAt = DateTimeOffset.UtcNow;
            releaseObserver.TrySetResult(ExternalStartCompletion.SucceededWith(
                "completed", "ext:task.event", terminalAt, "job-old-round"));
            var staleResult = await staleAttempt.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ResponsibilityState.Pending, staleResult.ResponsibilityState);
            Assert.Equal(1, System.Threading.Volatile.Read(ref sends));
            var ledgerTerminal = Assert.Single(new ExternalStartLedger(root).Read().File!.Entries);
            Assert.Equal(LedgerEntryState.Terminal, ledgerTerminal.State);
            Assert.Equal(ExecutionResultKind.Succeeded, ledgerTerminal.TerminalKind);
            Assert.Equal("completed", ledgerTerminal.RawTerminal);
            Assert.Equal(terminalAt, ledgerTerminal.TerminalObservedAtUtc);

            var operation = Assert.Single(Ops(root));
            Assert.Equal(inFlight.SendSeq, operation.LastSendSeq);
            Assert.NotEqual(OperationRequestState.TerminalCompleted, operation.RequestState);
            Assert.Null(operation.ExecutionResult);
            Assert.Null(operation.PendingTerminal);

            await currentOwner.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            currentOwner = null;
            recoveryOwner = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:902" });
            await recoveryOwner.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
            {
                RequestIdentity = requestIdentity,
                Namespace = "v2",
                WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "group:测试组",
                SourceDetail = "fixture:recover_late_terminal",
                ExecuteAsync = _ =>
                {
                    System.Threading.Interlocked.Increment(ref sends);
                    return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-resend"));
                },
            });

            var recovered = Assert.Single(Ops(root));
            Assert.Equal(OperationRequestState.TerminalCompleted, recovered.RequestState);
            Assert.True(recovered.Zone is OperationZone.TerminalPendingTransfer or OperationZone.Tombstone);
            Assert.Equal("job-old-round", recovered.ExecutionResult!.JobId);
            Assert.Equal(inFlight.SubmissionIdentity, recovered.ExecutionResult.SubmissionIdentity);
            Assert.Equal(inFlight.SendSeq, recovered.ExecutionResult.SendSeq);
            Assert.Equal(1, System.Threading.Volatile.Read(ref sends));
            Assert.Null(ReadSubmission(root));
        }
        finally
        {
            releaseSend.TrySetResult(ExternalStartExecution.AcceptedWith("job-old-round", "ext:task.queue"));
            releaseObserver.TrySetResult(ExternalStartCompletion.SucceededWith(
                "completed", "ext:task.event", DateTimeOffset.UtcNow, "job-old-round"));
            if (currentOwner is not null) await currentOwner.ShutdownAsync();
            if (recoveryOwner is not null) await recoveryOwner.ShutdownAsync();
            if (staleOwner is not null) await staleOwner.ShutdownAsync();
            TryDelete(root);
        }
    }

    /// <summary>
    /// **[§24.17／批次四十四 验证会诊取证]** 外部启动入口的操作类型**由调用位置决定**：入参**伪报**其他类型
    /// （此处 `NodeExecution`）也必须落盘为 `ExternalStart`——否则受污染调用可制造「外部副作用已执行、
    /// 而接管按错误持久化类型分派」的不一致面。断言：操作获准、适配层执行恰一次、且在册类型为 `ExternalStart`。
    /// </summary>
    [Fact]
    public async Task ExternalStart_SelfReportedOtherType_PersistsExternalStart()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var result = await host.SubmitExternalStartViaAdmissionAsync(new ExternalStartAdmissionRequest
            {
                Namespace = "v2",
                WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "group:测试组",
                SourceDetail = "fixture:external_start_type_spoof",
                OperationType = OperationType.NodeExecution,   // 伪报类型：入口必须忽略
                ExecuteAsync = _ =>
                {
                    System.Threading.Interlocked.Increment(ref executed);
                    return Task.FromResult(ExternalStartExecution.AcceptedWith("job-ext-spoof"));
                },
            }, default);

            Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
            Assert.Equal(1, System.Threading.Volatile.Read(ref executed));
            var op = Assert.Single(Ops(root));
            Assert.Equal(OperationType.ExternalStart, op.OperationType);   // **入口写死：不采信入参自报**
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_F11Active_DoesNotExecute()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900", F11Active = true });

            var result = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); }), default);

            Assert.Equal(AdmissionResultKind.F11Blocked, result.Kind);
            Assert.Equal(0, System.Threading.Volatile.Read(ref executed));                                  // 门禁先于任何启动副作用
            Assert.False(Directory.Exists(Path.Combine(root, "arbitration"))); // 亦无租约副作用
            await host.ShutdownAsync(); // 显式关闭（释放心跳/门面生命周期）
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_AdapterDefiniteReject_ClosesSubmission_NoLedgerEntry()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });

            var result = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.RejectedWith("peer_rejected")); }), default);

            Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
            Assert.Equal(1, System.Threading.Volatile.Read(ref executed)); // 执行了一次（适配层给出关联验证后的确定未受理）
            Assert.Null(new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File!.Handoff!.Submission);
            var ledger = new ExternalStartLedger(root).Read();
            Assert.True(ledger.Valid);
            // 确定未受理＝**不得写台账**（含「不得创建空台账文件」——空文件同样是对台账面的写入）。
            Assert.False(File.Exists(Path.Combine(root, "external-start-ledger.json")));
            await host.ShutdownAsync(); // 显式关闭（释放心跳/门面生命周期）
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_LedgerUnfinishedRecord_CountsAsOccupied()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            // 先造一条「已受理未终结」台账记录（模拟上一进程 queued：BGI 快照为空但占用成立）
            var seed = new ExternalStartLedgerEntry
            {
                SubmissionIdentity = "sub:prior:1",
                SendSeq = 1,
                CandidateId = "cand-prior",
                ResourceRef = "group:测试组",
                ActionId = "act:cand-prior",
                TargetBgiEpoch = "9:900",
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                EvidenceSource = "fixture:prior_accepted",
                State = LedgerEntryState.AcceptedPendingExecution,
            };
            Assert.True(new ExternalStartLedger(root).RecordAccepted(seed).Success);

            // 事实面：BGI 快照报告空闲，但台账未终结记录必须使「执行占用」成立
            // （§4.2a 合并对事实接缝与生产事实面一律生效——接缝只覆盖 BGI 快照来源）。
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });

            var blocked = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); }), default);
            Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, blocked.Kind); // 占用→需安全交接确认
            Assert.Equal(0, System.Threading.Volatile.Read(ref executed));

            // 台账标记终局后，同一请求可获准（占用解除）
            Assert.True(new ExternalStartLedger(root)
                .MarkTerminal("sub:prior:1", 1, "fixture:terminal", DateTimeOffset.UtcNow).Success);
            var allowed = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); }), default);
            Assert.True(allowed.Kind == AdmissionResultKind.Accepted,
                "台账终局后应获准；实际 " + allowed.Kind + "/" + allowed.ReasonCode + "：" + allowed.Detail
                + "；台账=" + System.Text.Json.JsonSerializer.Serialize(new ExternalStartLedger(root).Read().File));
            Assert.Equal(1, System.Threading.Volatile.Read(ref executed));
            await host.ShutdownAsync(); // 显式关闭（释放心跳/门面生命周期）
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **B3 第 4 步：同身份续用（§2.1）**——续用必须复用首次身份：门面按该身份定位 Operations 记录，
    /// **缺失＝stale_operation_identity 响亮拒绝**（绝不回退为创建新操作/换身份/重新绑定）。
    /// </summary>
    [Fact]
    public async Task ExternalStart_ContinueUseWithUnknownIdentity_LoudlyRejected_NoNewOperation()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });

            var result = await host.SubmitExternalStartViaAdmissionAsync(
                new ExternalStartAdmissionRequest
                {
                    Namespace = "v2",
                    WorkflowId = "group:测试组",
                    TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                    ResourceRef = "group:测试组",
                    SourceDetail = "fixture:continue_use",
                    RequestIdentity = "ffffffffffffffffffffffffffffffff", // 从未登记的身份
                    ExecuteAsync = _ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); },
                }, default);

            Assert.Equal(AdmissionResultKind.Error, result.Kind);
            Assert.Equal("stale_operation_identity", result.ReasonCode);
            Assert.Equal(0, System.Threading.Volatile.Read(ref executed)); // 未执行
            Assert.Empty(Ops(root));                                        // 未新建任何操作
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **B3 第 4 步：同身份续用命中既有操作**——首次 `Create` 分配身份并回显；以同一身份＋同一模板发起
    /// `ContinueUse` ⇒ 命中**同一笔** Operations 记录（不新建第二笔），且触发出现身份的占位符由本层以该身份回填。
    /// </summary>
    [Fact]
    public async Task ExternalStart_ContinueUseSameIdentity_HitsExistingOperation_NoSecondOperation()
    {
        var root = NewRoot();
        var executed = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });

            var first = await host.SubmitExternalStartViaAdmissionAsync(
                Request(_ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); }), default);
            Assert.Equal(AdmissionResultKind.Accepted, first.Kind);
            Assert.False(string.IsNullOrEmpty(first.RequestIdentity));       // 首次身份已回显（§2.1）
            var beforeOps = Ops(root);
            Assert.Single(beforeOps);

            var second = await host.SubmitExternalStartViaAdmissionAsync(
                new ExternalStartAdmissionRequest
                {
                    Namespace = "v2",
                    WorkflowId = "group:测试组",
                    TriggerOccurrenceId = "v2:remote:{requestIdentity}",     // 同一模板：由本层以既有身份回填
                    ResourceRef = "group:测试组",
                    SourceDetail = "fixture:continue_use",
                    RequestIdentity = first.RequestIdentity,
                    ExecuteAsync = _ => { System.Threading.Interlocked.Increment(ref executed); return Task.FromResult(ExternalStartExecution.AcceptedWith()); },
                }, default);

            // 续用命中既有已受理操作 ⇒ 返回既有结论（不新增发送者、不再执行）
            Assert.Equal(AdmissionResultKind.Accepted, second.Kind);
            Assert.Equal(first.RequestIdentity, second.RequestIdentity);
            Assert.Equal(1, System.Threading.Volatile.Read(ref executed));
            var afterOps = Ops(root);
            Assert.Single(afterOps);                                        // 未新建第二笔
            Assert.Equal(beforeOps[0].Candidate!.TriggerOccurrenceId, afterOps[0].Candidate!.TriggerOccurrenceId);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **公开入口的身份回显（会诊重要项）**：以既有身份发起**续用**时，即使被前置阻断（本夹具用 F11），
    /// 返回值也必须回显**传入的该身份**（不得回显空串，否则调用方会把下一次续用误变成新建操作）。
    /// </summary>
    [Fact]
    public async Task AdmitExternalStart_ContinueUseBlocked_StillEchoesSuppliedIdentity()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900", F11Active = true });
            const string identity = "0123456789abcdef0123456789abcdef";

            var outcome = await host.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
            {
                Namespace = "v2",
                WorkflowId = "group:测试组",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "group:测试组",
                SourceDetail = "fixture:continue_use_blocked",
                RequestIdentity = identity,
                ExecuteAsync = _ => Task.FromResult(ExternalStartExecution.AcceptedWith()),
            });

            Assert.Equal(ExternalStartAdmissionStatus.Blocked, outcome.Status);
            Assert.Equal(identity, outcome.RequestIdentity); // 回显传入身份（不得为空串）
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 完成观察接线（R5.3 §24.10／§24.14／§24.2-5，[Batch B 收尾之二]）─────────────────

    [Fact]
    public async Task ExternalStart_CompletionCancelled_SettlesAndReportsCancelled()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-obs", "adapter:accepted")),
                completion: () => ExternalStartCompletion.CancelledWith("cancelled", "adapter:watch", DateTimeOffset.UtcNow, "job-obs")));

            Assert.Equal(ExternalStartAdmissionStatus.Cancelled, outcome.Status);   // 取消事实端到端保留
            Assert.Equal(ResponsibilityState.Settled, outcome.ResponsibilityState); // 已结算（台账 Terminal＋关闭＋终局）
            Assert.Equal(ExecutionDisposition.Cancelled, outcome.ExecutionDisposition);
            Assert.Equal("cancelled", outcome.RawTerminal);
            Assert.Equal("job-obs", outcome.JobId);
            // 台账终态 + 操作终局（§24.15 唯一顺序的结果）。
            var entry = new ExternalStartLedger(root).Read().File!.Entries.Single();
            Assert.Equal(LedgerEntryState.Terminal, entry.State);
            Assert.Contains(Ops(root), o => o.RequestState == OperationRequestState.TerminalCompleted);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_CompletionUnknown_KeepsPendingWithoutTerminalCarriers()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-u", "adapter:accepted")),
                completion: () => ExternalStartCompletion.UnknownWith("completion_not_observed", "adapter:watch")));

            Assert.Equal(ExternalStartAdmissionStatus.NeedReconcile, outcome.Status);
            Assert.Equal(ResponsibilityState.Pending, outcome.ResponsibilityState);
            var op = Ops(root).Single();
            Assert.Null(op.PendingTerminal);   // §24.19-2：未知不得写终态载体
            Assert.Null(op.ExecutionResult);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_AlreadyExecutedWithoutHandle_KeepsSubmissionUnknown_AndNeverResends()
    {
        var root = NewRoot();
        var sends = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var early = CommandExecutor.ClassifyQueueSubmitEarly(
                new BgiTaskSubmitResult { Success = true, Status = "already_executed" },
                "配置组「测试组」", 1, DateTimeOffset.UtcNow);
            var mapped = CommandExecutor.MapQueueEarlyToAdmission(early);

            var first = await host.AdmitExternalStartAsync(Request(_ =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                return Task.FromResult(mapped.Early);
            }), default);

            Assert.Equal(ExternalStartAdmissionStatus.NeedReconcile, first.Status);
            Assert.Equal(ResponsibilityState.Pending, first.ResponsibilityState);
            var originalSubmission = Assert.IsType<SubmissionRecord>(ReadSubmission(root));
            Assert.Equal(SubmissionState.Reconciling, originalSubmission.State);
            Assert.Equal(1, originalSubmission.SendSeq);
            var originalOperation = Ops(root).Single();
            Assert.Equal(originalSubmission.SendSeq, originalOperation.LastSendSeq);
            Assert.Equal(originalSubmission.SubmissionIdentity, originalOperation.SubmissionIdentity);
            Assert.Empty(new ExternalStartLedger(root).Read().File?.Entries ?? []);
            Assert.Equal(1, sends);

            var repeated = await host.AdmitExternalStartAsync(Request(_ =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-send"));
            }), default);

            Assert.Equal(ExternalStartAdmissionStatus.Rejected, repeated.Status);
            Assert.Equal("submission_conflict", repeated.Code);
            Assert.Equal(1, sends);
            var stillPending = Assert.IsType<SubmissionRecord>(ReadSubmission(root));
            Assert.Equal(SubmissionState.Reconciling, stillPending.State);
            Assert.Equal(originalSubmission.SubmissionIdentity, stillPending.SubmissionIdentity);
            Assert.Equal(originalSubmission.SendSeq, stillPending.SendSeq);
            Assert.Equal(originalOperation.LastSendSeq,
                Ops(root).Single(o => o.SubmissionIdentity == originalSubmission.SubmissionIdentity).LastSendSeq);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_AlreadyExecutedWithHandle_ObservesAndSettlesExistingNumberedJob()
    {
        var root = NewRoot();
        var sends = 0;
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var early = CommandExecutor.ClassifyQueueSubmitEarly(
                new BgiTaskSubmitResult { Success = true, Status = "already_executed", TaskHandle = "job-existing" },
                "配置组「测试组」", 1, DateTimeOffset.UtcNow);
            var prepared = CommandExecutor.PrepareQueueEarlyForAdmission(early);
            Assert.Equal(CommandExecutor.QueueStartEarlyKind.Accepted, prepared.Kind);
            Assert.Equal("job-existing", prepared.TaskHandle);

            var observedAt = DateTimeOffset.UtcNow;
            var observation = Task.FromResult(new CommandExecutor.QueueTerminalObservation(
                CommandExecutor.QueueTerminalSource.Event, "completed", false, null, null,
                "job-existing", false, null, observedAt));
            prepared = prepared with
            {
                Observation = observation,
                Reply = new ExternalStartReply.EarlyAccepted(
                    "job-existing", CommandExecutor.MapObservationTaskAsync(observation)),
            };
            var mapped = CommandExecutor.MapQueueEarlyToAdmission(prepared);
            Assert.Equal(ExternalStartExecutionKind.Accepted, mapped.Early.Kind);
            Assert.Equal("job-existing", mapped.Early.JobId);
            Assert.NotNull(mapped.Observer);

            var outcome = await host.AdmitExternalStartAsync(Request(_ =>
            {
                System.Threading.Interlocked.Increment(ref sends);
                return Task.FromResult(mapped.Early);
            }, observer: mapped.Observer), default);

            Assert.Equal(ResponsibilityState.Settled, outcome.ResponsibilityState);
            Assert.Equal("job-existing", outcome.JobId);
            Assert.Equal(1, sends);
            var ledger = new ExternalStartLedger(root).Read();
            Assert.True(ledger.Valid);
            Assert.Equal(LedgerEntryState.Terminal, Assert.Single(ledger.File!.Entries).State);
            Assert.Contains(Ops(root), o => o.RequestState == OperationRequestState.TerminalCompleted);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ExternalStart_NoCompletionProvider_KeepsAcceptedPending()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-n", "adapter:accepted"))));

            Assert.Equal(ExternalStartAdmissionStatus.Accepted, outcome.Status);    // 早期受理：保持非终态
            Assert.Equal(ResponsibilityState.Pending, outcome.ResponsibilityState);
            Assert.False(outcome.ExecutionDisposition == ExecutionDisposition.Cancelled);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 早期 ack 通道的「异步完成观察」接线（R5.3 §24.10／§24.14，[Batch B 收尾之三／P38]）──────

    /// <summary>
    /// **异步完成观察（早期 ack 通道）**：完成事实由观察委托在**门面锁外**等待后送达，
    /// 仍按 §24.15 唯一顺序结算（终态载体→台账 Terminal→关闭→终局），与同步提供者同口径。
    /// </summary>
    [Fact]
    public async Task ExternalStart_CompletionObserver_AsyncTerminalSettlesLikeSyncProvider()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-ob", "ext:task.queue")),
                observer: _ => Task.FromResult<ExternalStartCompletion?>(ExternalStartCompletion.CancelledWith(
                    "cancelled", "ext:task.event", DateTimeOffset.UtcNow, "job-ob"))));

            Assert.Equal(ExternalStartAdmissionStatus.Cancelled, outcome.Status);
            Assert.Equal(ResponsibilityState.Settled, outcome.ResponsibilityState);
            Assert.Equal(ExecutionDisposition.Cancelled, outcome.ExecutionDisposition);
            Assert.Equal("cancelled", outcome.RawTerminal);
            Assert.Equal("job-ob", outcome.JobId);
            var entry = new ExternalStartLedger(root).Read().File!.Entries.Single();
            Assert.Equal(LedgerEntryState.Terminal, entry.State);
            Assert.Contains(Ops(root), o => o.RequestState == OperationRequestState.TerminalCompleted);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **观察中断不得抹掉责任**（§24.6-2／§24.19-2-②）：观察委托抛异常 ⇒ 完成层 `Unknown` ⇒
    /// 保持 `Accepted/Pending`、**不写**终态载体（`PendingTerminal`／`ExecutionResult`）、台账不终局。
    /// </summary>
    [Fact]
    public async Task ExternalStart_CompletionObserverThrows_KeepsPendingWithoutTerminalCarriers()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-throw", "ext:task.queue")),
                observer: _ => throw new InvalidOperationException("observer exploded")));

            Assert.Equal(ExternalStartAdmissionStatus.NeedReconcile, outcome.Status);
            Assert.Equal(ResponsibilityState.Pending, outcome.ResponsibilityState);
            Assert.Equal("job-throw", outcome.JobId);                  // 已取得的句柄不得丢失
            var op = Ops(root).Single();
            Assert.Null(op.PendingTerminal);
            Assert.Null(op.ExecutionResult);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **观察返回 `null` ＝本通道不承载完成事实**（未配置/不适用）：保持**普通受理**（责任 `Pending`），
    /// **不得**走完成结算（否则既有 v2 发送成功会被误判成待对账）。
    /// </summary>
    [Fact]
    public async Task ExternalStart_ObserverReturnsNull_KeepsAcceptedPending()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-null", "ext:task.queue")),
                observer: _ => Task.FromResult<ExternalStartCompletion?>(null)));

            Assert.Equal(ExternalStartAdmissionStatus.Accepted, outcome.Status);
            Assert.Equal(ResponsibilityState.Pending, outcome.ResponsibilityState);
            Assert.Equal(ExecutionDisposition.None, outcome.ExecutionDisposition);
            var op = Ops(root).Single();
            Assert.Null(op.PendingTerminal);
            Assert.Null(op.ExecutionResult);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 恢复扫描集合②/③（R5.3 §24.12-3；[Batch B 收尾之五]）────────────────────────────────

    /// <summary>
    /// **集合②未终结台账：恢复只保留观察责任**（§24.12-3／§24.16-3）——重启后按完整发送身份对齐时，
    /// 未终结台账对应的操作**不得**被改写、**不得**释放占用、**不得**伪造终态（不可查询时长期保守停驻）。
    /// </summary>
    [Fact]
    public async Task Recovery_UnterminatedLedger_KeepsObservationResponsibility()
    {
        var root = NewRoot();
        try
        {
            var host1 = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            // 受理但**无完成事实**（普通受理）：台账保持未终结、操作保持 Accepted/Pending。
            var outcome = await host1.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-keep", "ext:task.queue"))));
            Assert.Equal(ExternalStartAdmissionStatus.Accepted, outcome.Status);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution, new ExternalStartLedger(root).Read().File!.Entries.Single().State);
            var keptSubmission = Ops(root).Single().SubmissionIdentity;
            await host1.ShutdownAsync();

            // 重启后（新宿主实例）触发恢复扫描：集合②只保留责任。
            var host2 = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            await host2.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
            {
                Namespace = "v2",
                WorkflowId = "onedragon:恢复探针",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "onedragon:恢复探针",
                SourceDetail = "fixture:recovery_probe",
                ExecuteAsync = _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-probe2", "ext:task.queue")),
            });

            // 集合②的记录仍在册且**责任未结清、未写终态载体**（只保留观察责任）。
            var keptOp = Ops(root).Single(o => string.Equals(o.SubmissionIdentity, keptSubmission, StringComparison.Ordinal));
            Assert.Equal(OperationRequestState.Accepted, keptOp.RequestState);
            Assert.Null(keptOp.PendingTerminal);                                                      // 无权威终态 ⇒ 不得写终态载体
            Assert.Null(keptOp.ExecutionResult);
            // [C 表 #6／批次四十六] **重启后重绑观察责任**：观察义务落盘（时点＋句柄＋单调次数），
            // 责任状态/发送身份不变 ⇒「停驻≠放弃」具备可追溯、可续扫载体。
            Assert.NotNull(keptOp.ObservationReboundAtUtc);
            Assert.Equal(1, keptOp.ObservationRebindCount);
            Assert.Equal("job-keep", keptOp.ObservationJobId);
            Assert.Equal(keptSubmission, keptOp.SubmissionIdentity);
            Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
                new ExternalStartLedger(root).Read().File!.Entries.First(e => e.JobId == "job-keep").State); // 台账未终结
            await host2.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **「已拒绝后收到权威终态」不得静默丢证据**（§24.2-2″／§24.15 末行；[验证会诊阻断处置] 新增）：
    /// 适配层给出确定未受理（本笔转 `RetryableRejected`）同时又交出**权威终态**时，宿主必须把它登记为
    /// **冲突证据**（`conflict.pending=true`、责任 `Pending`、禁止重发），不得保留原拒绝结论了事。
    /// </summary>
    [Fact]
    public async Task ExternalStart_RejectedThenAuthoritativeTerminal_RegistersConflictEvidence()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var outcome = await host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.RejectedWith(
                    "queue_full", retryable: true, evidenceSource: "ext:task.queue")),
                completion: () => ExternalStartCompletion.SucceededWith(
                    "completed", "ext:task.event", DateTimeOffset.UtcNow, "job-conflict")));

            Assert.Equal(ExternalStartAdmissionStatus.NeedReconcile, outcome.Status);   // 冲突待决：保守对账、禁重发
            Assert.Equal(ResponsibilityState.Pending, outcome.ResponsibilityState);
            var op = Ops(root).Single();
            Assert.True(op.ConflictPending);                                            // 冲突待决标志已置
            var evidence = Assert.Single(op.ConflictEvidence!);                          // 证据原样追加（不覆盖既有拒绝）
            Assert.Equal("completed", evidence!.RawTerminal);
            Assert.Equal("queue_full", evidence.SupersededReasonCode);                   // 既有拒绝事实保留为「被取代」
            // 冲突证据必须**携带完整发送身份与观察事实**（§24.2-2″：不得只留结论）
            Assert.Equal(op.SubmissionIdentity, evidence.SubmissionIdentity);
            Assert.Equal(op.LastSendSeq, evidence.SendSeq);
            Assert.Equal("ext:task.event", evidence.EvidenceSource);
            Assert.NotEqual(default, evidence.ObservedAtUtc);
            Assert.Contains("host-observed:", evidence.EvidenceId);                      // 确定性 evidenceId（可按同一事实重放）
            Assert.NotNull(op.LastResult);                                              // 拒绝本体未被改写
            Assert.Equal(OperationRequestState.RetryableRejected, op.RequestState);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§24.14-5／§24.18-2（观察等待不得占住门面锁）**：`CompletionTask` 未完成期间，**其他请求必须能取得门面锁**
    /// 并得到结论（这里用另一身份的第二笔准入验证）——若等待发生在 `_gate` 内，本夹具会在此超时失败。
    /// </summary>
    [Fact]
    public async Task ExternalStart_CompletionPending_OtherRequestStillAcquiresGate()
    {
        var root = NewRoot();
        try
        {
            var host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            var gate = new System.Threading.Tasks.TaskCompletionSource<ExternalStartCompletion?>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            var pending = host.AdmitExternalStartAsync(Request(
                _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-lock", "ext:task.queue")),
                observer: _ => gate.Task));
            // 让首笔先走到「等待完成」处（受理与观察都已开始）。
            await Task.Delay(50);

            var second = await host.AdmitExternalStartAsync(new ExternalStartAdmissionRequest
            {
                Namespace = "v2",
                WorkflowId = "onedragon:另一条龙",
                TriggerOccurrenceId = "v2:remote:{requestIdentity}",
                ResourceRef = "onedragon:另一条龙",
                SourceDetail = "fixture:second",
                ExecuteAsync = _ => Task.FromResult(ExternalStartExecution.AcceptedWith("job-2nd", "ext:task.queue")),
            }).WaitAsync(TimeSpan.FromSeconds(5));     // 不得被首笔的完成等待阻塞
            Assert.NotEqual(ExternalStartAdmissionStatus.Accepted, second.Status); // 第二笔应被既有占用拒绝（不重复启动）

            // 收尾：给出首笔完成事实，验证等待结束后仍按 §24.15 结算。
            gate.SetResult(ExternalStartCompletion.SucceededWith(
                "completed", "ext:task.event", DateTimeOffset.UtcNow, "job-lock"));
            var first = await pending;
            Assert.Equal(ExternalStartAdmissionStatus.Accepted, first.Status);
            Assert.Equal(ResponsibilityState.Settled, first.ResponsibilityState);
            await host.ShutdownAsync();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ExternalStartLedger_RejectsCandidateThatWouldDuplicateJobIdAcrossRounds()
    {
        var root = NewRoot();
        try
        {
            var ledger = new ExternalStartLedger(root);
            ExternalStartLedgerEntry Entry(string identity) => new()
            {
                SubmissionIdentity = identity,
                SendSeq = 1,
                CandidateId = "candidate:" + identity,
                ResourceRef = "group:测试组",
                ActionId = "action:" + identity,
                TargetBgiEpoch = "9:900",
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                EvidenceSource = "fixture:accepted",
                State = LedgerEntryState.AcceptedPendingExecution,
                JobId = "same-remote-job",
                OperationType = OperationType.ExternalStart,
            };

            Assert.True(ledger.RecordAccepted(Entry("sub:round-one:1")).Success);
            var revision = ledger.Read().File!.Revision;
            var duplicate = ledger.RecordAccepted(Entry("sub:round-two:1"));

            Assert.False(duplicate.Success);
            Assert.Equal("invalid_mutation_state", duplicate.Reason);
            var read = ledger.Read();
            Assert.True(read.Valid, read.Detail);
            Assert.Equal(revision, read.File!.Revision);
            Assert.Equal("sub:round-one:1", Assert.Single(read.File.Entries).SubmissionIdentity);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ExternalStartLedger_LateReceiptReplayPreservesFirstObservationAndEnrichesHandle()
    {
        var root = NewRoot();
        try
        {
            var ledger = new ExternalStartLedger(root);
            var firstAt = DateTimeOffset.UtcNow;
            var first = new ExternalStartLedgerEntry
            {
                SubmissionIdentity = "sub:late-replay:1",
                SendSeq = 1,
                CandidateId = "candidate:late-replay",
                ResourceRef = "group:测试组",
                ActionId = "action:late-replay",
                TargetBgiEpoch = "9:900",
                AcceptedAtUtc = firstAt,
                EvidenceSource = "sender:first-observation",
                State = LedgerEntryState.AcceptedPendingExecution,
                OperationType = OperationType.ExternalStart,
            };
            Assert.True(ledger.RecordLateAcceptedReceipt(first).Success);

            var replay = new ExternalStartLedgerEntry
            {
                SubmissionIdentity = first.SubmissionIdentity,
                SendSeq = first.SendSeq,
                CandidateId = first.CandidateId,
                ResourceRef = first.ResourceRef,
                ActionId = first.ActionId,
                TargetBgiEpoch = first.TargetBgiEpoch,
                AcceptedAtUtc = firstAt.AddMinutes(1),
                EvidenceSource = "recovery:second-observation",
                State = LedgerEntryState.AcceptedPendingExecution,
                RunId = "run-late-replay",
                JobId = "job-late-replay",
                OperationType = OperationType.ExternalStart,
            };
            Assert.True(ledger.RecordLateAcceptedReceipt(replay).Success);

            var stored = Assert.Single(ledger.Read().File!.Entries);
            Assert.Equal(firstAt, stored.AcceptedAtUtc);
            Assert.Equal(first.EvidenceSource, stored.EvidenceSource);
            Assert.Equal("run-late-replay", stored.RunId);
            Assert.Equal("job-late-replay", stored.JobId);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
