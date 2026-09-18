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
        store.RecordIntent(rec);              // 提交意图先行落盘
        rec.SubmitIntent = SubmitIntentState.Submitted; // 已发出，受理回执未确认
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
        store.RecordIntent(rec);
        rec.SubmitIntent = SubmitIntentState.Accepted;
        rec.JobId = "job-123";
        rec.State = WorkflowRunState.Running;
        store.Update(rec);
        rec.ObservedTerminal = "succeeded"; // 终态已观察，水位尚未提交
        store.Update(rec);

        var recovered = Assert.Single(store.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State); // 有终态事实 → 不是 Unknown
        Assert.Equal("succeeded", recovered.ObservedTerminal);
        Assert.Equal("job-123", recovered.JobId);
    }

    [Fact]
    public void CrashWindow4_TerminalRecord_Untouched_PendingCompletionRetainedNotFired()
    {
        var store = new RunStore(_dir);
        var rec = store.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Succeeded;
        rec.PendingCompletionAction = "关闭游戏并关机"; // 待执行收尾
        store.Update(rec);
        var revBefore = rec.RecordRevision;

        Assert.Empty(store.RecoverOnStart()); // 终态记录不动
        var loaded = store.Load(rec.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, loaded.State);
        Assert.Equal("关闭游戏并关机", loaded.PendingCompletionAction); // 保留待显式处理，不自动触发
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
    public void RecordIntent_RequiresFixedIdempotencyKey()
    {
        var store = new RunStore(_dir);
        var rec = new WorkflowRunRecord { RunId = "run-manual001", WorkflowId = "wf-aaaaaaaa", WorkflowRevision = "rev-1" };
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(rec)); // 无幂等键拒绝
    }
}
