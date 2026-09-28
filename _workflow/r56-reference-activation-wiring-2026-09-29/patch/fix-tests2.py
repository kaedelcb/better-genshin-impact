import pathlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:110])
    s = s.replace(old, new, cnt)

# 增加「独立第二配置根」夹具辅助（同一配置根上不允许第二个未决事务，这正是既有合同）
rep('''    private static MigrationReferenceUpdatePlan RenamePlan(params (string Path, string From, string To)[] targets)
        => new(targets.Select(t => new MigrationReferenceWriteTarget(t.Path, ChangeKind.Modified,
            RenameFrom: t.From, RenameTo: t.To)).ToList());

    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();''',
'''    private static MigrationReferenceUpdatePlan RenamePlan(params (string Path, string From, string To)[] targets)
        => new(targets.Select(t => new MigrationReferenceWriteTarget(t.Path, ChangeKind.Modified,
            RenameFrom: t.From, RenameTo: t.To)).ToList());

    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();

    /// <summary>**独立第二配置根**（同一配置根上存在未决事务时不允许开新事务＝既有合同，故需要独立根）。</summary>
    private MigrationSwitchTransaction BeginIndependent(string name, IMigrationEffectService effects,
        IEnumerable<ChangeRecord> changes, string txId = "t2")
    {
        var cfg = Path.Combine(_root, "cfg-" + name);
        var txRoot = Path.Combine(_root, "tx-" + name);
        Directory.CreateDirectory(cfg);
        var tx = new MigrationSwitchTransaction(cfg, txRoot, () => Now, () => new NoopQuiet(), true, null, null, effects);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private string FullAt(MigrationSwitchTransaction tx, string rel)
        => Path.Combine(Path.GetDirectoryName(tx.ManifestPath)!.Replace(Path.GetFileName(Path.GetDirectoryName(tx.ManifestPath)!), "cfg"), "");
''' + '')

# 两个测试改用独立根
rep('''        // ② 盘上已是目标态（active）而请求 candidate-ready→active ⇒ 拒绝，不得零写入伪成功
        Seed(FlowPath, FlowJson("计划", "active", "配置A"));
        var effects2 = Effects();
        using var tx2 = Begin(effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "t2");
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var alreadyApplied = tx2.ActivateCandidate(Activation());
        Assert.Equal(MigrationStage.Blocked, alreadyApplied.Stage);
        Assert.Contains("activation_already_applied", alreadyApplied.Reason);
        Assert.Equal(0, effects2.ActivateCalls);
        Assert.Null(tx2.LoadManifest()!.ActivationRecord);''',
'''        // ② 盘上已是目标态（active）而请求 candidate-ready→active ⇒ 拒绝，不得零写入伪成功（独立第二配置根）
        var effects2 = Effects();
        using var tx2 = BeginIndependent("already", effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var alreadyApplied = tx2.ActivateCandidate(Activation());
        Assert.Equal(MigrationStage.Blocked, alreadyApplied.Stage);
        Assert.Contains("activation_already_applied", alreadyApplied.Reason);
        Assert.Equal(0, effects2.ActivateCalls);
        Assert.Null(tx2.LoadManifest()!.ActivationRecord);''')

rep('''        var throwing2 = Effects();
        throwing2.ThrowOnActivate = true;
        using var tx2 = Begin(throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "activate");''',
'''        var throwing2 = Effects();
        throwing2.ThrowOnActivate = true;
        using var tx2 = BeginIndependent("activate", throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);''')

# Part2 里不要那个多余的 FullAt 辅助（未使用）
rep('''    private string FullAt(MigrationSwitchTransaction tx, string rel)
        => Path.Combine(Path.GetDirectoryName(tx.ManifestPath)!.Replace(Path.GetFileName(Path.GetDirectoryName(tx.ManifestPath)!), "cfg"), "");
''', '')
p.write_text(s, encoding="utf-8")
print("independent roots helper added")
