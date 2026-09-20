using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 事务迁移切换 v2** 夹具（owner 0 点击；**全程只用独立临时配置根**，绝不触碰真实 User 目录）。
/// 覆盖第 1 轮会诊 8 项必改：强制闸门（单一检查点）、合法阶段转换（拒绝跳步/回滚后重提）、
/// 事务串行边界（独占锁）、静止窗口前置、回滚演练复用真实回滚核心且范围绑定变更、
/// 路径与根关系边界、**按变更归属回滚**（无记录不删）、manifest 全字段完整性与重启恢复收敛。
/// **能力边界（如实）**：仍**未接线**到真实引用服务/激活实现与生产消费侧（本类只提供强制检查点 API）；
/// 静止窗口由调用方提供委托，本夹具只用桩验证「未取得 ⇒ 拒绝提交」。
/// </summary>
public sealed class R56MigrationSwitchTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R56MigrationSwitchTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "independent-config-root");
        _txRoot = Path.Combine(_root, "transaction");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Seed(string rel, string text)
    {
        var full = Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private MigrationSwitchTransaction NewTx(Func<IDisposable>? quiesce = null, bool requireQuiescence = true,
        Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, quiesce, requireQuiescence, hook);

    /// <summary>完整走到「已激活、已演练」的公共前置（返回持有独占锁的实例）。</summary>
    private MigrationSwitchTransaction ArrangeActivated(string txId = "t1", IEnumerable<ChangeRecord>? changes = null)
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx(() => new NoopQuiet());
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        return tx;
    }

    private sealed class NoopQuiet : IDisposable
    {
        public void Dispose() { }
    }

    // ── ① 强制闸门：未提交零执行 ─────────────────────────────────────────

    [Fact]
    public void Gate_Uncommitted_ProductionActionNotInvoked()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();                        // 已激活但**未提交**

        var runs = 0;
        var blocked = tx.TryRunProduction(() => runs++);

        Assert.False(blocked.Success);
        Assert.StartsWith("not_committed", blocked.Reason, StringComparison.Ordinal);
        Assert.Equal(0, runs);                     // 未提交 ⇒ 零执行
    }

    [Fact]
    public void Gate_Committed_ProductionActionInvokedOnce()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var runs = 0;
        var ok = tx.TryRunProduction(() => runs++);

        Assert.True(ok.Success, ok.Reason);
        Assert.Equal(1, runs);
        Assert.True(tx.AuthorizeProductionExecution().Success);
    }

    // ── ② 合法阶段转换：拒绝跳步与回滚后重提 ─────────────────────────────

    [Fact]
    public void StateMachine_RejectsSkipCommit_AndCommitAfterRollback()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();

        // 未更新引用/未激活 ⇒ 既不能演练也不能提交
        Assert.Equal("illegal_stage:SnapshotReady", tx.RehearseRollback().Reason);
        Assert.Equal("illegal_stage:SnapshotReady", tx.Commit().Reason);

        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        // 回滚后不得再提交（演练资格与提交资格一并作废）
        Assert.True(tx.Rollback().Success);
        var after = tx.Commit();
        Assert.False(after.Success);
        Assert.StartsWith("illegal_stage", after.Reason, StringComparison.Ordinal);
        Assert.False(tx.AuthorizeProductionExecution().Success);
    }

    [Fact]
    public void Stage_IllegalAdvance_IsRejectedByTable()
    {
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.Snapshotting, MigrationStage.SnapshotReady));
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.Activated, MigrationStage.Committed));
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Committed));   // 跳步
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Activated));   // 跳步
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.Committed, MigrationStage.Blocked));
    }

    // ── ③ 事务串行边界（独占锁）─────────────────────────────────────────

    [Fact]
    public void ExclusiveLock_SecondInstanceRejected_UntilReleased()
    {
        Seed("a.json", "{\"v\":1}");
        using var first = NewTx(() => new NoopQuiet());
        Assert.True(first.BeginTransaction("t1").Success);

        using var second = NewTx(() => new NoopQuiet());
        var busy = second.BeginTransaction("t2");
        Assert.False(busy.Success);
        Assert.Equal("transaction_busy", busy.Reason);
    }

    // ── ④ 静止窗口前置 ────────────────────────────────────────────────

    [Fact]
    public void Quiescence_NotAcquired_BlocksCommit()
    {
        var quietCount = 0;
        using var tx = NewTx(quiesce: () => { quietCount++; return new NoopQuiet(); });
        Seed("a.json", "{\"v\":1}");
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.Equal(1, quietCount);               // 快照期取得静止窗口

        // 严格模式 + **快照期未取得静止窗口**（清掉标记模拟调用方未提供） ⇒ 拒绝提交
        var m = tx.LoadManifest()!;
        m.QuiescedAtUtc = null;
        m.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(m);
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("no_quiescence_window", commit.Reason);
    }

    // ── ⑤ 演练复用真实回滚核心 + 范围绑定变更 ────────────────────────────

    [Fact]
    public void Rehearsal_ScopeBindsChanges_ChangeInvalidatesRehearsal()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);          // 范围一致 ⇒ 可提交
    }

    [Fact]
    public void Rehearsal_ChangeSetChanged_InvalidatesRehearsal()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);

        // 变更归属改变（新增一条）⇒ 既有演练资格失效
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "added.json", Kind = ChangeKind.Added }]).Success);
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("rollback_not_rehearsed_for_current_scope", commit.Reason);

        Assert.True(tx.RehearseRollback().Success);   // 重新演练 ⇒ 恢复资格
        Assert.True(tx.Commit().Success);
    }

    // ── ⑥ 路径与根关系边界 ─────────────────────────────────────────────

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("nested/../../evil.json")]
    [InlineData("/abs.json")]
    [InlineData("C:/abs.json")]
    [InlineData("")]
    public void PathSafety_RejectsEscapes(string rel)
        => Assert.False(MigrationSwitchTransaction.IsSafeRelativePath(rel));

    [Fact]
    public void PathSafety_AcceptsNormalRelativePaths()
    {
        Assert.True(MigrationSwitchTransaction.IsSafeRelativePath("a.json"));
        Assert.True(MigrationSwitchTransaction.IsSafeRelativePath("nested/b.json"));
    }

    [Fact]
    public void Roots_Overlapping_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(_configRoot, Path.Combine(_configRoot, "tx")));
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(Path.Combine(_txRoot, "cfg"), _txRoot));
    }

    [Fact]
    public void RecordChanges_UnsafePath_Rejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();

        var bad = tx.RecordChanges([new ChangeRecord { Path = "../evil.json", Kind = ChangeKind.Added }]);
        Assert.False(bad.Success);
        Assert.StartsWith("unsafe_path", bad.Reason, StringComparison.Ordinal);
    }

    // ── ⑦ 按变更归属回滚（无记录不删）──────────────────────────────────

    [Fact]
    public void Rollback_DeletesOnlyRecordedAdditions_KeepsUnrecordedFiles()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        using var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "added/x.json", Kind = ChangeKind.Added },
        ]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Commit();

        // 事务期改动：改 a、加 added/x，并**由他方**新增 unrecorded/y.json
        Seed("a.json", "{\"v\":2}");
        Seed("added/x.json", "{\"tx\":true}");
        Seed("unrecorded/y.json", "{\"other\":true}");

        Assert.True(tx.Rollback().Success);

        Assert.Equal(original, HashOf(Full("a.json")));                    // 旧字节恢复
        Assert.False(File.Exists(Full("added/x.json")));                   // 本事务新增被清理
        Assert.True(File.Exists(Full("unrecorded/y.json")));               // **未记录者不删**（归属依据）
    }

    [Fact]
    public void Rollback_WithoutChangeRecords_DeletesNothing()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Commit();

        Seed("other/z.json", "{\"other\":true}");   // 无变更归属记录
        Assert.True(tx.Rollback().Success);

        Assert.True(File.Exists(Full("other/z.json")));   // 无记录 ⇒ 不删除任何文件
    }

    // ── ⑧ manifest 全字段完整性 + 闸门先校验 ───────────────────────────

    [Fact]
    public void ManifestIntegrity_TamperingStageOrMarker_IsRejected()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        // 单改阶段（不重算完整性）⇒ 校验失败、闸门拒绝、生产不执行
        var m = tx.LoadManifest()!;
        m.Stage = MigrationStage.Activated;
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Assert.Null(tx.LoadValidated());
        var auth = tx.AuthorizeProductionExecution();
        Assert.False(auth.Success);
        Assert.Equal("manifest_missing_or_invalid", auth.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    // ── ⑨ 重启恢复：中间态收敛为旧态；已提交保持新态 ─────────────────────

    [Fact]
    public void RecoverOnStart_PendingActivation_ConvergesToOldState()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Seed("a.json", "{\"v\":2}");                 // 事务期改动（未提交）
        tx.Dispose();                                // 模拟进程退出（释放独占锁）

        using var reopened = NewTx(() => new NoopQuiet());
        var rec = reopened.RecoverOnStart();

        Assert.True(rec.Success, rec.Reason);
        Assert.Equal(MigrationStage.RolledBack, rec.Stage);
        Assert.Equal(original, HashOf(Full("a.json")));          // 收敛为完整旧态
        Assert.False(reopened.AuthorizeProductionExecution().Success);
    }

    [Fact]
    public void RecoverOnStart_Committed_KeepsNewState()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        var rec = tx.RecoverOnStart();
        Assert.True(rec.Success);
        Assert.Equal(MigrationStage.Committed, rec.Stage);
        Assert.True(tx.AuthorizeProductionExecution().Success);   // 已提交 ⇒ 保持完整新态且可执行
    }

    // ── ⑩ 阶段持久化 + 崩溃注入不可执行 ─────────────────────────────────

    [Fact]
    public void Stage_Persisted_AcrossReopen()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx(() => new NoopQuiet());
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.Dispose();

        using var reopened = NewTx(() => new NoopQuiet());
        var m = reopened.LoadValidated()!;
        Assert.Equal(MigrationStage.ReferenceUpdating, m.Stage);
        Assert.Null(m.CommitMarker);
        Assert.False(reopened.AuthorizeProductionExecution().Success);
    }

    [Theory]
    [InlineData(MigrationStage.Snapshotting)]
    [InlineData(MigrationStage.SnapshotReady)]
    [InlineData(MigrationStage.ReferenceUpdating)]
    [InlineData(MigrationStage.Activated)]
    [InlineData(MigrationStage.Committed)]
    public void CrashAtAnyStage_NeverLeavesExecutableState(MigrationStage crashAt)
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        using var tx = NewTx(quiesce: () => new NoopQuiet(), hook: stage =>
        {
            if (stage == crashAt) throw new InvalidOperationException("模拟阶段崩溃：" + stage);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            tx.BeginTransaction("t1");
            tx.TakeSnapshot();
            tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
            tx.MarkReferenceUpdateCompleted();
            tx.MarkActivated();
            tx.RehearseRollback();
            tx.Commit();
        });

        using var probe = NewTx(() => new NoopQuiet());
        Assert.False(probe.AuthorizeProductionExecution().Success);      // 任何阶段崩溃都不可执行
        Assert.Equal(original, HashOf(Full("a.json")));                  // 事务本身未改配置根
    }
}