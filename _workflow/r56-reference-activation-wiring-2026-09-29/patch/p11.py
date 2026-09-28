import pathlib, hashlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
old = '''    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)'''
new = '''    /// <summary>
    /// 提交前复核：已真实写入/激活的文件在提交前被锁外写方改动 ⇒ 提交被拒（`commit_recheck_failed`），
    /// 且生产执行保持零动作——提交许可不与「已漂移的盘上状态」绑定。
    /// </summary>
    [Fact]
    public void Commit_RefusesWhenWrittenFileDriftsAfterActivation()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);

        File.WriteAllText(Full(FlowPath), FlowJson("计划", "active", "配置X"), new UTF8Encoding(false));   // 锁外写方改动

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.StartsWith("commit_recheck_failed:", commit.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
        Assert.Null(tx.LoadManifest()!.CommitMarker);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>提交成功路径的正面证据：真实写入+激活+演练+提交后，授权通过且生产动作恰好执行一次。</summary>
    [Fact]
    public void CommittedRealTransaction_AuthorizesProductionExactlyOnce()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        Assert.True(tx.AuthorizeProductionExecution().Success);
        var runs = 0;
        Assert.True(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(1, runs);
    }

    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("ok", hashlib.sha256(s.encode()).hexdigest()[:16])
