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

    private static TaskCenterHost NewHost(string root, TaskCenterAdmissionSeams seams, Func<ControlStatus?>? status = null)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => null, log: null, runnerFactory: null, readinessOverride: () => (true, null),
            localExecutionCapability: () => true,
            statusSnapshotProvider: status ?? (() => new ControlStatus { TaskRunning = false }),
            admissionWired: true, admissionSeams: seams);

    private static ExternalStartAdmissionRequest Request(Func<System.Threading.CancellationToken, Task<ExternalStartExecution>> execute,
        string ns = "v2", Func<ExternalStartCompletion?>? completion = null)
        => new()
        {
            Namespace = ns,
            WorkflowId = "group:测试组",
            TriggerOccurrenceId = ns + ":remote:{requestIdentity}",
            ResourceRef = "group:测试组",
            SourceDetail = "fixture:external_start",
            ExecuteAsync = execute,
            CompletionProvider = completion,
        };

    private static IReadOnlyList<OperationRecord> Ops(string root)
        => new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff?.Operations ?? [];

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
            Assert.True(new ExternalStartLedger(root).ConfirmRebuildable(entry.SubmissionIdentity, entry.SendSeq));
            await host.ShutdownAsync(); // 显式关闭（释放心跳/门面生命周期）
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
}
