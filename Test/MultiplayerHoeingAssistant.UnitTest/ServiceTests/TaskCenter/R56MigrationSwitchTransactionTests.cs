using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 事务迁移切换** 夹具（owner 0 点击；**全程只用独立配置根**，绝不触碰真实 User 目录）：
/// 快照字节+SHA256 清单完整性、manifest 含快照路径与回滚入口、回滚字节级一致、
/// 回滚失败阻断提交、「未提交绝不生产执行」闸门、逐阶段崩溃注入只留完整旧态/新态。
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

    private MigrationSwitchTransaction NewTx(Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, hook);

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void Snapshot_Manifest_ContainsPathAndRollbackEntry_AndHashesVerify()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("nested/b.json", "{\"w\":2}");
        var tx = NewTx();

        var snap = tx.TakeSnapshotAndWriteManifest("t1");

        Assert.True(snap.Success, snap.Reason);
        var m = tx.LoadManifest()!;
        Assert.Equal("t1", m.TransactionId);
        Assert.Equal(tx.SnapshotPathOf("t1"), m.SnapshotPath);
        Assert.Contains("Rollback", m.RollbackEntry, StringComparison.Ordinal);
        Assert.Equal(2, m.FileHashes.Count);
        Assert.Equal(MigrationSwitchTransaction.ComputeManifestHash(m.FileHashes), m.ManifestHash); // 清单哈希自洽
        Assert.Null(m.CommitMarker);                                                                // 提交前无标记
        Assert.Equal("", tx.VerifySnapshot());                                                      // 快照完整性通过
        Assert.False(tx.IsProductionExecutable());                                                  // 未提交 ⇒ 不可生产执行
    }

    [Fact]
    public void VerifySnapshot_DetectsByteTamper_ManifestTamper_AndUntrackedFiles()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.TakeSnapshotAndWriteManifest("t1");
        var m = tx.LoadManifest()!;

        // ① 快照文件字节被改
        var snapFile = Path.Combine(m.SnapshotPath, "a.json");
        File.WriteAllText(snapFile, "{\"v\":999}");
        Assert.StartsWith("snapshot_hash_mismatch", tx.VerifySnapshot(), StringComparison.Ordinal);

        // ② 恢复字节后追加未登记文件
        File.WriteAllText(snapFile, "{\"v\":1}");
        File.WriteAllText(Path.Combine(m.SnapshotPath, "extra.json"), "{}");
        Assert.StartsWith("snapshot_untracked_file", tx.VerifySnapshot(), StringComparison.Ordinal);

        // ③ 清单被改写（哈希不自洽）
        File.Delete(Path.Combine(m.SnapshotPath, "extra.json"));
        var raw = File.ReadAllText(tx.ManifestPath).Replace(m.ManifestHash, new string('0', m.ManifestHash.Length));
        File.WriteAllText(tx.ManifestPath, raw);
        Assert.Equal("manifest_hash_mismatch", tx.VerifySnapshot());
    }

    [Fact]
    public void Commit_RequiresRollbackRehearsal_ThenGatesProductionExecution()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.TakeSnapshotAndWriteManifest("t1");

        var early = tx.Commit("t1");
        Assert.False(early.Success);
        Assert.Equal("rollback_not_rehearsed", early.Reason);   // 未演练 ⇒ 不得提交
        Assert.False(tx.IsProductionExecutable());

        Assert.True(tx.BeginReferenceUpdateAndActivation("t1").Success);
        Assert.False(tx.IsProductionExecutable());              // 激活中仍不可执行
        Assert.True(tx.RehearseRollback().Success);

        var commit = tx.Commit("t1");
        Assert.True(commit.Success, commit.Reason);
        Assert.True(tx.IsProductionExecutable());               // 已提交（唯一提交标记＝事务号）
        Assert.Equal("t1", tx.LoadManifest()!.CommitMarker);
    }

    [Fact]
    public void Rollback_RestoresBytes_DeletesAddedFiles_AndRevertsActivation()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("nested/b.json", "{\"w\":2}");
        var originalA = HashOf(Path.Combine(_configRoot, "a.json"));
        var tx = NewTx();
        tx.TakeSnapshotAndWriteManifest("t1");
        tx.BeginReferenceUpdateAndActivation("t1");
        tx.RehearseRollback();
        tx.Commit("t1");

        // 事务期间：改了旧文件 + 新增文件
        Seed("a.json", "{\"v\":2}");
        Seed("added/c.json", "{\"new\":true}");

        var rb = tx.Rollback("t1");

        Assert.True(rb.Success, rb.Reason);
        Assert.Equal(originalA, HashOf(Path.Combine(_configRoot, "a.json")));   // 字节级一致
        Assert.False(File.Exists(Path.Combine(_configRoot, "added", "c.json"))); // 清理本事务新增
        var m = tx.LoadManifest()!;
        Assert.Null(m.CommitMarker);                                            // 撤销激活
        Assert.Equal(MigrationStage.RolledBack, m.Stage);
        Assert.False(tx.IsProductionExecutable());
    }

    [Fact]
    public void RollbackFailure_BlocksCommit()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.TakeSnapshotAndWriteManifest("t1");
        tx.BeginReferenceUpdateAndActivation("t1");
        tx.RehearseRollback();

        // 快照被破坏 ⇒ 回滚失败
        var m = tx.LoadManifest()!;
        File.Delete(Path.Combine(m.SnapshotPath, "a.json"));
        var rb = tx.Rollback("t1");
        Assert.False(rb.Success);
        Assert.StartsWith("snapshot_invalid", rb.Reason, StringComparison.Ordinal);

        // 回滚失败 ⇒ 提交必须被阻断（同一校验前置）
        var commit = tx.Commit("t1");
        Assert.False(commit.Success);
        Assert.StartsWith("snapshot_invalid", commit.Reason, StringComparison.Ordinal);
        Assert.False(tx.IsProductionExecutable());
    }

    /// <summary>**逐阶段崩溃注入**：每个阶段写入前后抛错 ⇒ 恢复后**只有完整旧态**（不得出现可执行的新态）。</summary>
    [Theory]
    [InlineData(MigrationStage.Snapshotting)]
    [InlineData(MigrationStage.SnapshotReady)]
    [InlineData(MigrationStage.UpdatingReferences)]
    [InlineData(MigrationStage.Activating)]
    [InlineData(MigrationStage.Committed)]
    public void CrashAtAnyStage_LeavesNonExecutableState(MigrationStage crashAt)
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Path.Combine(_configRoot, "a.json"));
        var tx = NewTx(stage =>
        {
            if (stage == crashAt) throw new InvalidOperationException("模拟阶段崩溃：" + stage);
        });

        // 驱动完整四步：命中注入阶段即抛（其余阶段照常推进），从而覆盖 Snapshotting→Committed 各点
        Assert.Throws<InvalidOperationException>(() =>
        {
            tx.TakeSnapshotAndWriteManifest("t1");
            tx.BeginReferenceUpdateAndActivation("t1");
            tx.RehearseRollback();
            tx.Commit("t1");
        });
        Assert.False(tx.IsProductionExecutable());                                  // 任何阶段崩溃都不可生产执行
        Assert.Equal(original, HashOf(Path.Combine(_configRoot, "a.json")));        // 配置根保持完整旧态
    }

    /// <summary>阶段标记持久化：崩溃后重新读到的是**已落盘**阶段（不臆断提交）。</summary>
    [Fact]
    public void Stage_Persisted_AcrossReopen()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.TakeSnapshotAndWriteManifest("t1");
        Assert.Equal(MigrationStage.SnapshotReady, NewTx().LoadManifest()!.Stage);   // 新实例读到同一阶段

        NewTx().BeginReferenceUpdateAndActivation("t1");
        var reopened = NewTx().LoadManifest()!;
        Assert.Equal(MigrationStage.Activating, reopened.Stage);
        Assert.Null(reopened.CommitMarker);
        Assert.False(NewTx().IsProductionExecutable());
    }
}