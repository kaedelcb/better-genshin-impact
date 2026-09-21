using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// RunStore（R4.2）验收夹具——B3 会诊四个崩溃窗口：
/// ① 提交前：Planned 记录恢复标 Interrupted；② 受理后回执前（提交在飞）：标 Unknown 禁止自动重跑；
/// ③ 终态后水位提交前：已观察终态不标 Unknown；④ 收尾提交后：终态记录不动、待执行收尾保留不自动触发。
/// 另有：记录修订单调守卫、坏记录拒绝覆盖（原件保留）、原子写备份、恢复绝不换幂等键。
/// 涉盘用例走临时目录，finally 清理。
/// </summary>
public class RunStoreTests : IDisposable
{
    private readonly string _dir;

    public RunStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "runstore-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// **G4a（[批次四十五 第三轮验证会诊处置]）`CreateRun` 的准入来源 Scope 写入边界**：
    /// ①空白 ⇒ 规范化为 `null`（无固定来源；旧记录缺字段同样反序列化为 null）；
    /// ②规范 `bgi:local:{非空完整 epoch}`（含冒号的完整 epoch）原样落盘；
    /// ③畸形非空（其它实例前缀／空 epoch 段）⇒ **响亮抛出**且**不创建运行记录**（不落权威字段）。
    /// </summary>
    [Fact]
    public void CreateRun_AdmissionSourceScope_WhitespaceNormalizedMalformedRejected()
    {
        var store = new RunStore(_dir);

        var blank = store.CreateRun("wf-g4a-a", "rev-1", admissionSourceScope: "   ");
        Assert.Null(blank.AdmissionSourceScope);
        Assert.Null(store.Load(blank.RunId)!.AdmissionSourceScope);   // 落盘同样为 null（缺字段=无来源）

        var canonical = store.CreateRun("wf-g4a-b", "rev-1",
            admissionSourceScope: "bgi:local:4821:638912345678901234");
        Assert.Equal("bgi:local:4821:638912345678901234", canonical.AdmissionSourceScope);
        Assert.Equal("bgi:local:4821:638912345678901234", store.Load(canonical.RunId)!.AdmissionSourceScope);

        var before = store.List().Count;
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRun("wf-g4a-c", "rev-1", admissionSourceScope: "bgi:other:ep"));
        Assert.Throws<InvalidOperationException>(() =>
            store.CreateRun("wf-g4a-d", "rev-1", admissionSourceScope: "bgi:local:"));
        Assert.Equal(before, store.List().Count);                      // 畸形值不产生运行记录
    }

    /// <summary>
    /// **[P7／§12.2 第 3 项] 字段合并：`UpdateMerging` 保留并发写入者的非自有字段改动**。
    /// 对照：旧对象整对象写回（`Update`）在修订漂移时**响亮冲突**（`RunRecordConflictException`）——
    /// 这正是「旧 Runner 对象覆盖接管事实」的既有护栏；`UpdateMerging` 则把自有字段合并进**最新记录**，
    /// 使并发改动被保留而非整笔失败。
    /// </summary>
    [Fact]
    public void UpdateMerging_PreservesConcurrentNonOwnedChange_WhileUpdateConflicts()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-merge", "rev-1");
        rec.Note = "初始";
        store.Update(rec);
        var stale = store.Load(rec.RunId)!;              // 旧对象（修订落后）

        // 并发写入者推进记录（自有字段：Note）
        var other = store.Load(rec.RunId)!;
        other.Note = "并发写入者的改动";
        store.Update(other);

        // ① 旧对象整写＝响亮冲突（不得静默覆盖）
        Assert.Throws<RunRecordConflictException>(() => store.Update(stale));

        // ② 合并写（前置条件成立）：自有字段（State）生效，且并发改动（Note）保留
        var applied = store.UpdateMergingIf(rec.RunId, latest =>
        {
            latest.State = WorkflowRunState.Running;
            return true;
        }, out var latest);
        Assert.True(applied);
        Assert.NotNull(latest);
        Assert.Equal(WorkflowRunState.Running, latest!.State);              // 自有字段已应用
        Assert.Equal("并发写入者的改动", latest.Note);                       // 非自有字段保留（未被旧对象覆盖）
        var reloaded = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Running, reloaded.State);
        Assert.Equal("并发写入者的改动", reloaded.Note);

        // ③ **旧对象 rebase**：把盘上最新字段整体同步回旧对象后，旧对象再整写**不再覆盖并发改动**
        RunStore.RebaseOnto(stale, reloaded);
        stale.State = WorkflowRunState.Waiting;      // 自有字段改动
        store.Update(stale);                          // rebase 后修订已对齐 ⇒ 不再冲突
        var after = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Waiting, after.State);
        Assert.Equal("并发写入者的改动", after.Note);   // **并发改动仍保留**（rebase 生效，未被旧字段洗回）

        // ④ 前置条件不成立 ⇒ **零发布、零修订推进**（返回 false，latest 为盘上原样）
        var revBefore = store.Load(rec.RunId)!.RecordRevision;
        var appliedNo = store.UpdateMergingIf(rec.RunId, _ => false, out var unchanged);
        Assert.False(appliedNo);
        Assert.NotNull(unchanged);
        Assert.Equal(revBefore, store.Load(rec.RunId)!.RecordRevision);   // 未推进修订

        // 不存在的记录：无副作用（返回 false）
        Assert.False(store.UpdateMergingIf("wf-not-exists", _ => true, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void CrashWindow1_BeforeSubmit_RecoveredAsInterrupted_IdemKeyUnchanged()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        store.Update(rec);
        var idemBefore = rec.IdempotencyKey;

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
        Assert.Equal(idemBefore, recovered.IdempotencyKey); // 绝不换键重跑
        Assert.Contains("Interrupted", recovered.Note);
    }

    [Fact]
    public void CrashWindow2_SubmitInFlight_RecoveredAsUnknown_NeverAutoResubmit()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        store.RecordIntent(rec, NewSubmission(rec));              // 提交意图先行落盘（一提交一身份，B2）
        rec.CurrentSubmission!.Intent = SubmitIntentState.Submitted; // 已发出，受理回执未确认
        store.Update(rec);
        var idemBefore = rec.IdempotencyKey;

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recovered.State); // 结果不确定 ≠ 成功
        Assert.Equal(idemBefore, recovered.IdempotencyKey);
        Assert.Contains("禁止自动重跑", recovered.Note);

        // Unknown 不是终态结论，但也绝不被再次扫描改动（幂等）
        var again = store.RecoverOnStart();
        Assert.Single(again); // 仍非终态，保持 Unknown 等待对账
        Assert.Equal(WorkflowRunState.Unknown, again[0].State);
    }

    [Fact]
    public void CrashWindow3_TerminalObserved_BeforeCursorCommit_RecoveredAsInterrupted_NotUnknown()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        store.RecordIntent(rec, NewSubmission(rec));
        rec.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
        rec.CurrentSubmission!.JobId = "job-123";
        rec.State = WorkflowRunState.Running;
        store.Update(rec);
        rec.CurrentSubmission!.ObservedTerminal = "succeeded"; // 终态已观察，水位尚未提交
        store.Update(rec);

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State); // 有终态事实 → 不是 Unknown
        Assert.Equal("succeeded", recovered.CurrentSubmission!.ObservedTerminal);
        Assert.Equal("job-123", recovered.CurrentSubmission!.JobId);
    }

    [Fact]
    public void CrashWindow4_TerminalRecord_Untouched_PendingCompletionRetainedNotFired()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Succeeded;
        rec.PendingCompletion = new PendingCompletionRecord // 待执行收尾（E3' 记录形态）
        {
            ActionId = "$flow#0", Kind = "terminal.completionAction", Action = "关闭游戏并关机", State = "pending",
        };
        store.Update(rec);
        var revBefore = rec.RecordRevision;

        Assert.Empty(store.RecoverOnStart()); // 终态记录不动
        var loaded = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, loaded.State);
        Assert.Equal("关闭游戏并关机", loaded.PendingCompletion!.Action); // 保留待显式处理，不自动触发
        Assert.Equal(revBefore, loaded.RecordRevision);
    }

    [Fact]
    public void Update_RecordRevisionGuard_RejectsStaleWrite()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");

        var stale = store.Load(rec.RunId)!;
        rec.State = WorkflowRunState.Running;
        store.Update(rec); // 推进到 RecordRevision=2

        stale.State = WorkflowRunState.Paused;
        Assert.Throws<RunRecordConflictException>(() => store.Update(stale)); // 并发旧副本拒绝覆盖
    }

    [Fact]
    public void CorruptedRecord_RefuseOverwrite_OriginalPreserved_AndListedAsUnknown()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        var file = Path.Combine(_dir, rec.RunId + ".run.json");
        File.WriteAllText(file, "{ 损坏");
        var bytesBefore = File.ReadAllBytes(file);

        Assert.Empty(store.List());
        var bad = Assert.Single(store.UnknownFiles);
        Assert.Equal(file, bad);

        // Load 对坏文件抛 JsonException（绝不回空对象；调用方按隔离处理）
        Assert.Throws<System.Text.Json.JsonException>(() => store.Load(rec.RunId));
        var replacement = new WorkflowRunRecord { RunId = rec.RunId, WorkflowId = "wf-aaaaaaaa", WorkflowRevision = "rev-1" };
        Assert.Throws<RunRecordConflictException>(() => store.Update(replacement)); // 拒绝静默覆盖坏文件
        Assert.Equal(bytesBefore, File.ReadAllBytes(file)); // 原件字节不动
    }

    [Fact]
    public void Persist_AtomicWrite_BackupKeepsPriorRecordRevision()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        store.Update(rec);

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(_dir, "_backup"), rec.RunId + ".*.run.json"));
        var prior = System.Text.Json.JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(backup));
        Assert.Equal(WorkflowRunState.Planned, prior!.State); // 上一版状态可回查
        Assert.Equal(1, prior.RecordRevision);
    }

    [Fact]
    public void RecordIntent_RequiresDerivedKey_AndRejectsOverlapWhileInFlight()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");

        // 无派生键拒绝
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(rec, new WorkflowSubmission { NodeId = "n-1" }));

        // B2：键按出现身份确定性派生——同身份同键（重复投递复用），不同身份不同键
        var key1 = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1);
        Assert.Equal(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-2", 0, 0, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 1, 1));
        Assert.NotEqual(key1, RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 2));

        // 前一提交在飞：拒绝重叠提交（防 jobId/终态跨节点残留）
        store.RecordIntent(rec, NewSubmission(rec, "n-1"));
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(rec, NewSubmission(rec, "n-2")));

        // 终态确认后允许下一提交
        rec.CurrentSubmission!.ObservedTerminal = "succeeded";
        store.Update(rec);
        store.RecordIntent(rec, NewSubmission(rec, "n-2"));
        Assert.Equal("n-2", rec.CurrentSubmission!.NodeId);
    }

    [Fact]
    public void CrashWindow5_CompletingInFlight_RecoveredAsUnknown_CompletionNeverAutoRefired()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Completing; // B5：收尾意图已落盘，执行结果未证实
        rec.PendingCompletion = new PendingCompletionRecord
        {
            ActionId = "$flow#0", Kind = "terminal.completionAction", Action = "关闭游戏并关机", State = "submitted",
        };
        store.Update(rec);

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recovered.State); // 收尾在飞 = 结果不确定
        Assert.Equal("terminal.completionAction", recovered.PendingCompletion!.Kind); // 保留待人工对账
        Assert.Equal("关闭游戏并关机", recovered.PendingCompletion!.Action);
        Assert.Contains("禁止自动补发收尾", recovered.Note);
    }

    private static WorkflowSubmission NewSubmission(WorkflowRunRecord rec, string nodeId = "n-1")
        => new()
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, nodeId, 0, 0, 1),
            NodeId = nodeId,
            Occurrence = 0,
            LoopIteration = 0,
            Attempt = 1,
        };
}
