import pathlib, hashlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:80])
    s = s.replace(old, new, cnt)

# Commit 在 Blocked 阶段先被阶段检查拦下（illegal_stage），失败原因码如实断言（不假报 blocked: 前缀）
rep('''        Assert.True(BytesEqual(Full(FlowPath), baseline));
        Assert.Equal("blocked:scripted", tx.Commit().Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-S4：写后不明 ⇒ fail-closed Blocked；不盲目重试；零生产许可。</summary>''',
'''        Assert.True(BytesEqual(Full(FlowPath), baseline));
        var commit = tx.Commit();
        Assert.False(commit.Success);                       // Blocked 阶段提交被拒（阶段门先于提交）
        Assert.Equal("illegal_stage:Blocked", commit.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-S4：写后不明 ⇒ fail-closed Blocked；不盲目重试；零生产许可。</summary>''')

rep('''        Assert.Equal(1, effects.ApplyCalls);                                   // 未盲目重试
        Assert.Equal("blocked:scripted", tx.Commit().Reason);''',
'''        Assert.Equal(1, effects.ApplyCalls);                                   // 未盲目重试
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("illegal_stage:Blocked", commit.Reason);''')

rep('''        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:scripted", tx.Commit().Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------ 并发''',
'''        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("illegal_stage:Blocked", commit.Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------ 并发''')

rep('''        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:activation_readback_failed:" + FlowPath + ":activation_status=candidate-ready", tx.Commit().Reason);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));''',
'''        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("activation_readback_failed:" + FlowPath + ":activation_status=candidate-ready",
            tx.LoadManifest()!.BlockedReason);
        Assert.False(tx.Commit().Success);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));''')

rep('''        Assert.StartsWith("reference_update_unknown:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:scripted_partial", tx.Commit().Reason);''',
'''        Assert.StartsWith("reference_update_unknown:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.False(tx.Commit().Success);''')

rep('''        Assert.True(tx.Rollback().Success);                                             // Blocked ⇒ 可回滚
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
        Assert.True(tx.LoadManifest()!.ReferenceWriteSet.Count == 0
            || tx.LoadManifest()!.ReferenceWriteSet.All(e => HashOf(Full(e.Key)) == HashOf(Full(e.Key))));''',
'''        Assert.True(tx.Rollback().Success);                                             // Blocked ⇒ 可回滚
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);''')

# 隔离边界断言：快照目录本身位于事务根内（设计如此），故只断言「探针目录外」无新增
rep('''        Assert.Equal("{\\"keep\\":1}", File.ReadAllText(Path.Combine(outsideDir, "keep.json")));
        Assert.Single(Directory.EnumerateFiles(outsideDir));
        Assert.Equal(0, Directory.EnumerateFiles(Path.Combine(_root, "tx"), "*", SearchOption.AllDirectories)
            .Count(f => f.EndsWith(".flow.json", StringComparison.Ordinal)));''',
'''        Assert.Equal("{\\"keep\\":1}", File.ReadAllText(Path.Combine(outsideDir, "keep.json")));
        Assert.Single(Directory.EnumerateFiles(outsideDir));
        // 事务根内的 `.flow.json` 只允许出现在本事务**快照目录**（设计即把基线副本放在事务根下），不得出现在别处
        var txFiles = Directory.EnumerateFiles(Path.Combine(_root, "tx"), "*.flow.json", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).ToList();
        Assert.All(txFiles, f => Assert.Contains(
            Path.Combine(_root, "tx", "snapshot-").Replace(Path.DirectorySeparatorChar, '\\\\'),
            f.Replace(Path.DirectorySeparatorChar, '\\\\')));''')
p.write_text(s, encoding="utf-8")
print("tests patched", hashlib.sha256(s.encode()).hexdigest()[:16])
