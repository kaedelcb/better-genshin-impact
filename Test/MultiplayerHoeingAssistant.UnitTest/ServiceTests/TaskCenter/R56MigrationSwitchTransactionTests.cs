using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 事务迁移切换 v3** 夹具（owner 0 点击；**全程只用独立临时配置根**，绝不触碰真实 User 目录）。
/// 覆盖第 2 轮会诊 7 项必改：全入口持锁与「授权+执行」同临界区、RollingBack 幂等恢复、静止窗口全程且绑定会话、
/// 身份/路径/链接边界、未决事务拒绝开新、变更归属基线校验、结构+状态不变量与全字段完整性。
/// **能力边界（如实）**：仍未接线真实引用服务/激活实现与生产消费侧（本类只提供强制检查点 API）；
/// 静止窗口由调用方提供委托，夹具只用桩验证「未取得/非同会话 ⇒ 拒绝提交」。
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
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

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
        => new(_configRoot, _txRoot, () => Now, quiesce ?? (() => new NoopQuiet()), requireQuiescence, hook);

    private MigrationSwitchTransaction ArrangeActivated(string txId = "t1", IEnumerable<ChangeRecord>? changes = null,
        Action<MigrationStage>? hook = null)
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx(hook: hook);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        return tx;
    }

    [Fact]
    public void Gate_Uncommitted_ProductionNotInvoked()
    {
        using var tx = ArrangeActivated();
        var runs = 0;
        var r = tx.TryRunProduction(() => runs++);
        Assert.False(r.Success);
        Assert.StartsWith("not_committed", r.Reason, StringComparison.Ordinal);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Gate_Committed_ProductionInvokedOnce()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        var runs = 0;
        Assert.True(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Lock_NotHeld_MutationsRejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.Equal("lock_not_held", tx.TakeSnapshot().Reason);
        Assert.Equal("lock_not_held", tx.RecoverOnStart().Reason);
        Assert.Equal("lock_not_held", tx.AuthorizeProductionExecution().Reason);
        Assert.Equal("lock_not_held", tx.Rollback().Reason);
    }

    [Fact]
    public void ExclusiveLock_SecondInstanceRejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var first = NewTx();
        Assert.True(first.BeginTransaction("t1").Success);
        using var second = NewTx();
        Assert.Equal("transaction_busy", second.TryAcquireExclusive().Reason);
    }

    [Fact]
    public void StateMachine_RejectsSkip_AndAllowsRollbackFromCommitted()
    {
        using var tx = ArrangeActivated();
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Committed));
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Activated));
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.Committed, MigrationStage.RollingBack));
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.RollingBack, MigrationStage.RolledBack));

        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.True(tx.Rollback().Success);                     // 已提交仍可回滚
        Assert.StartsWith("illegal_stage", tx.Commit().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Quiescence_NotAcquired_BlocksCommit()
    {
        // 未提供静止窗口（requireQuiescence=true）⇒ 快照期未取得 ⇒ 禁止提交
        Seed("a.json", "{\"v\":1}");
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, quiesce: null, requireQuiescence: true);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]).Success);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.Equal("no_quiescence_window", tx.Commit().Reason);
    }

    [Fact]
    public void Quiescence_SessionBound_ReopenCannotCommitOnHistoricalTimestamp()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Dispose();                                   // 模拟进程退出（窗口应随之结束）

        using var reopened = NewTx();                   // 新会话：不得凭历史时间戳提交
        Assert.True(reopened.TryAcquireExclusive().Success);
        var commit = reopened.Commit();
        Assert.False(commit.Success);
        Assert.Equal("no_quiescence_window", commit.Reason);
    }
}
/// <summary>v3 夹具续（与上同类同文件，此块补齐其余必改项覆盖）。</summary>
public sealed class R56MigrationSwitchTransactionTests_Part2 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R56MigrationSwitchTransactionTests_Part2()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56b-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private void Seed(string rel, string text)
    {
        var full = Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private MigrationSwitchTransaction NewTx(Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, hook);

    private MigrationSwitchTransaction Activated(IEnumerable<ChangeRecord>? changes = null, string txId = "t1")
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        return tx;
    }

    [Fact]
    public void RecordChanges_AddedForExistingFile_Rejected()
    {
        using var tx = Activated();
        var bad = tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Added }]);
        Assert.False(bad.Success);
        Assert.StartsWith("change_baseline_mismatch", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordChanges_ModifiedForMissingFile_Rejected()
    {
        using var tx = Activated();
        var bad = tx.RecordChanges([new ChangeRecord { Path = "missing.json", Kind = ChangeKind.Modified }]);
        Assert.False(bad.Success);
        Assert.StartsWith("change_baseline_mismatch", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordChanges_UnsafeOrDuplicate_Rejected()
    {
        using var tx = Activated();
        Assert.StartsWith("unsafe_path", tx.RecordChanges([new ChangeRecord { Path = "../e.json", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
        Assert.StartsWith("duplicate_change_path",
            tx.RecordChanges([
                new ChangeRecord { Path = "b.json", Kind = ChangeKind.Added },
                new ChangeRecord { Path = "B.json", Kind = ChangeKind.Added },
            ]).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_DeletesOnlyRecordedAdditions_KeepsOtherWritersFiles()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("gone.json", "{\"gone\":1}");                 // 快照期存在的文件（事务期将被删除）
        var original = HashOf(Full("a.json"));
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "added/x.json", Kind = ChangeKind.Added },
            new ChangeRecord { Path = "gone.json", Kind = ChangeKind.Deleted },
        ]).Success, "变更归属应按基线通过");
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Assert.True(tx.RehearseRollback().Success, "演练应通过");
        Assert.True(tx.Commit().Success);

        Seed("a.json", "{\"v\":2}");
        Seed("added/x.json", "{\"tx\":true}");
        Seed("other/y.json", "{\"other\":true}");        // 他方新增（未记录）
        File.Delete(Full("gone.json"));                   // 事务期删除

        Assert.True(tx.Rollback().Success, "回滚应成功");

        Assert.Equal(original, HashOf(Full("a.json")));
        Assert.False(File.Exists(Full("added/x.json")));
        Assert.True(File.Exists(Full("other/y.json")));   // 未记录 ⇒ 保留
    }

    [Fact]
    public void Rehearsal_ScopeInvalidatedByChangeSet()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "b.json", Kind = ChangeKind.Added }]).Success);
        Assert.Equal("rollback_not_rehearsed_for_current_scope", tx.Commit().Reason);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
    }

    [Fact]
    public void PendingTransaction_BlocksNewBegin()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        var again = tx.BeginTransaction("t2");
        Assert.False(again.Success);
        Assert.StartsWith("pending_transaction_exists", again.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionId_Unsafe_Rejected()
    {
        Seed("a.json", "{}");
        using var tx = NewTx();
        Assert.Equal("invalid_transaction_id", tx.BeginTransaction("x/../../outside").Reason);
        Assert.Equal("invalid_transaction_id", tx.BeginTransaction("../evil").Reason);
    }

    [Fact]
    public void Roots_Overlapping_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new MigrationSwitchTransaction(_configRoot, Path.Combine(_configRoot, "tx")));
        Assert.Throws<InvalidOperationException>(() => new MigrationSwitchTransaction(Path.Combine(_txRoot, "cfg"), _txRoot));
    }

    [Fact]
    public void ManifestIntegrity_StageTamper_RejectedAndGateRefuses()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        var m = tx.LoadManifest()!;
        m.Stage = MigrationStage.Activated;                 // 单改阶段、不重算摘要
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void ManifestIntegrity_NullChangedFiles_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();

        var json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(tx.ManifestPath), "\"changedFiles\":\\s*\\[[\\s\\S]*?\\]", "\"changedFiles\": null");
        File.WriteAllText(tx.ManifestPath, json);
        Assert.Null(tx.LoadValidated());                    // null 字段 ⇒ 结构化拒绝，不抛异常
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);
    }

    [Fact]
    public void RecoverOnStart_RollingBack_IsIdempotent()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Seed("a.json", "{\"v\":2}");
        tx.Dispose();

        using var reopened = NewTx();
        Assert.True(reopened.TryAcquireExclusive().Success);
        Assert.True(reopened.RecoverOnStart().Success);      // 收敛为旧态
        Assert.Equal(original, HashOf(Full("a.json")));
        Assert.True(reopened.RecoverOnStart().Success);      // **重复启动幂等**
        Assert.Equal(MigrationStage.RolledBack, reopened.RecoverOnStart().Stage);
    }

    [Fact]
    public void RecoverOnStart_Committed_KeepsNewState()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        var rec = tx.RecoverOnStart();
        Assert.True(rec.Success);
        Assert.Equal(MigrationStage.Committed, rec.Stage);
        Assert.True(tx.AuthorizeProductionExecution().Success);
    }

    [Fact]
    public void CrashAfterStageWrite_NeverExecutable()
    {
        Seed("a.json", "{\"v\":1}");
        var seen = new Dictionary<MigrationStage, int>();
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage =>
            {
                seen[stage] = seen.TryGetValue(stage, out var n) ? n + 1 : 1;
                if (stage == MigrationStage.Activated && seen[stage] == 2)   // **写后**（第二次回调）崩溃
                    throw new InvalidOperationException("模拟写入完成后崩溃");
            });
        Assert.Throws<InvalidOperationException>(() =>
        {
            tx.BeginTransaction("t1");
            tx.TakeSnapshot();
            tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
            tx.MarkReferenceUpdateCompleted();
            tx.MarkActivated();
        });
        tx.Dispose();

        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.False(probe.AuthorizeProductionExecution().Success);
        probe.Dispose();
    }

    [Fact]
    public void RecoverFromPersistedRollingBack_ConvergesToOldState()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var phase = 0;
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage =>
            {
                if (stage == MigrationStage.RollingBack && ++phase == 2)    // RollingBack **已落盘**后崩溃（恢复尚未开始）
                    throw new InvalidOperationException("回滚中断");
            });
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Assert.Throws<InvalidOperationException>(() => tx.Rollback());
        tx.Dispose();

        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.Equal(MigrationStage.RollingBack, probe.LoadManifest()!.Stage);   // 持久化在回滚中
        Assert.True(probe.RecoverOnStart().Success);
        Assert.Equal(MigrationStage.RolledBack, probe.LoadManifest()!.Stage);
        Assert.Equal(original, HashOf(Full("a.json")));
        probe.Dispose();
    }

    [Fact]
    public void Baseline_CaseInsensitiveAlias_Rejected()
    {
        using var tx = Activated();
        Assert.StartsWith("change_baseline_mismatch",
            tx.RecordChanges([new ChangeRecord { Path = "A.json", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
        Assert.StartsWith("unsafe_path",
            tx.RecordChanges([new ChangeRecord { Path = "a.json.", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SameInstanceReLock_CannotReuseHistoricalQuiescence()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Dispose();                                     // 释放锁与窗口（代次前进）

        tx.TryAcquireExclusive();
        Assert.Equal("no_quiescence_window", tx.Commit().Reason);   // 同实例重取锁也不得复用历史资格
        tx.Dispose();
    }

    [Fact]
    public void Rollback_AfterCommit_AcquiresFreshQuiescence()
    {
        var quietCount = 0;
        Seed("a.json", "{\"v\":1}");
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () =>
        {
            quietCount++;
            return new NoopQuiet();
        }, true);
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        Assert.Equal(1, quietCount);                                 // 提交后窗口已释放

        Seed("a.json", "{\"v\":2}");
        Assert.True(tx.Rollback().Success, "提交后回滚应重新取得窗口并成功");
        Assert.Equal(2, quietCount);
        tx.Dispose();
    }

    [Fact]
    public void PendingCorruptManifest_BlocksNewBegin()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.True(tx.TryAcquireExclusive().Success);
        Directory.CreateDirectory(_txRoot);
        File.WriteAllText(tx.ManifestPath, "{ this is not json");
        Assert.Equal("pending_manifest_corrupt", tx.BeginTransaction("t2").Reason);
    }

    [Fact]
    public void TransactionId_HistoryReuse_Rejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        var again = tx.BeginTransaction("t1");               // 历史占用：事务号不得复用
        Assert.False(again.Success);
        Assert.Equal("transaction_id_in_use", again.Reason);
    }

    [Fact]
    public void ManifestIntegrity_NullSnapshotPath_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(tx.ManifestPath), "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": null");
        File.WriteAllText(tx.ManifestPath, json);
        Assert.Null(tx.LoadValidated());                     // 不得抛异常
    }

    [Fact]
    public void StateCombination_CommittedWithBlocked_Rejected()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var m = tx.LoadManifest()!;
        m.BlockedReason = "tampered";
        m.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(m);
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.Null(tx.LoadValidated());                     // Committed + blocked 组合非法
    }

    /// <summary>**第 4 轮必改⑥**：已提交后快照损坏 ⇒ 回滚**先封锁**再失败 ⇒ 必须撤销生产授权（不得继续可执行）。</summary>
    [Fact]
    public void Rollback_BrokenSnapshotAfterCommit_RevokesAuthorization()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        Assert.True(tx.AuthorizeProductionExecution().Success);

        var m = tx.LoadManifest()!;
        File.Delete(Path.Combine(m.SnapshotPath, "a.json"));       // 提交后快照损坏

        var rb = tx.Rollback();
        Assert.False(rb.Success);
        Assert.StartsWith("rollback_snapshot_invalid", rb.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);   // 已封锁
        Assert.Null(tx.LoadManifest()!.CommitMarker);                     // 授权已撤销
        Assert.False(tx.AuthorizeProductionExecution().Success);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>**第 4 轮必改①**：清理未完成（新增文件删除失败）⇒ **保持阻断**，不得落 `RolledBack` 假报成功。</summary>
    [Fact]
    public void Rollback_CleanupIncomplete_StaysBlocked()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "added/x.json", Kind = ChangeKind.Added },
        ]).Success);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        Seed("added/x.json", "{\"tx\":true}");
        var added = Full("added/x.json");
        File.SetAttributes(added, FileAttributes.ReadOnly);        // 使删除失败（清理未完成）
        try
        {
            var rb = tx.Rollback();
            Assert.False(rb.Success);
            Assert.Equal("rollback_cleanup_incomplete", rb.Reason);
            Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);   // 不得报告完整回滚
            Assert.False(tx.AuthorizeProductionExecution().Success);
        }
        finally
        {
            if (File.Exists(added)) File.SetAttributes(added, FileAttributes.Normal);
        }
    }

    /// <summary>**第 4 轮必改③**：快照归属**精确绑定**本事务——他事务（同前缀）的快照路径必须被拒。</summary>
    [Fact]
    public void SnapshotIdentity_OtherTransactionPrefix_Rejected()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = File.ReadAllText(tx.ManifestPath);
        var tampered = System.Text.RegularExpressions.Regex.Replace(json,
            "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": \"" + Path.Combine(_txRoot, "snapshot-t1-b-other").Replace("\\", "\\\\") + "\"");
        File.WriteAllText(tx.ManifestPath, tampered);
        Assert.Null(tx.LoadValidated());
    }

    /// <summary>**第 4 轮必改④**：占号历史**先于 manifest 发布** ⇒ 占号后崩溃（有历史、无 manifest）也不得复用事务号。</summary>
    [Fact]
    public void TransactionId_HistoryOccupiedBeforeManifest_BlocksReuse()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.True(tx.TryAcquireExclusive().Success);
        Directory.CreateDirectory(_txRoot);
        File.AppendAllText(tx.HistoryPath, "t9" + Environment.NewLine);   // 模拟「占号已落盘、manifest 未发布」的崩溃残件
        Assert.Equal("transaction_id_in_use", tx.BeginTransaction("t9").Reason);
    }

    /// <summary>**第 4 轮必改⑤**：含 NUL 的非法 snapshotPath ⇒ **结构化拒绝**（不得抛异常）。</summary>
    [Fact]
    public void ManifestIntegrity_NulInSnapshotPath_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = File.ReadAllText(tx.ManifestPath);
        var tampered = System.Text.RegularExpressions.Regex.Replace(json,
            "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": \"bad\\u0000path\"");
        File.WriteAllText(tx.ManifestPath, tampered);
        Assert.Null(tx.LoadValidated());     // 不抛异常
    }

    /// <summary>**第 4 轮必改⑥**：授权/执行与回滚**互斥**（同一临界区）——执行期间回滚不得并行介入。</summary>
    [Fact]
    public async Task Concurrency_AuthorizationAndRollback_AreSerialized()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var runs = 0;
        var exec = Task.Run(() => tx.TryRunProduction(() =>
        {
            runs++;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "生产执行应已进入临界区");

        var rollback = Task.Run(() => tx.Rollback());
        Assert.False(rollback.Wait(TimeSpan.FromMilliseconds(200)), "执行期间回滚不得并行完成（同临界区串行）");

        release.Set();
        Assert.True((await exec).Success);
        Assert.True((await rollback).Success);
        Assert.Equal(1, runs);
    }

    /// <summary>**第 4 轮必改⑥**：`.tmp` 半写残件**不污染**权威 manifest（读取只看正式文件）。</summary>
    [Fact]
    public void PartialTmpWrite_DoesNotCorruptManifest()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        File.WriteAllText(tx.ManifestPath + ".tmp", "{ half-written");      // 模拟半写残件
        Assert.NotNull(tx.LoadValidated());
        Assert.True(tx.AuthorizeProductionExecution().Success);
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
        var tx = NewTx(hook: stage =>
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

        tx.Dispose();                                     // 先释放锁（否则探针的失败只由 lock_not_held 解释）
        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success, "探针须真正取到锁，否则授权失败可能只由 lock_not_held 解释");
        Assert.False(probe.AuthorizeProductionExecution().Success);
        Assert.Equal(original, HashOf(Full("a.json")));
        probe.Dispose();
    }
}