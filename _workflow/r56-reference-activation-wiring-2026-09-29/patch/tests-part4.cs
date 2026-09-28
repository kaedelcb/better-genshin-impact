
/// <summary>
/// **会诊第 2 轮（验证轮）发现的修复夹具**：新增文件作为激活目标的回滚收敛、归属预检先于写入、
/// 激活版本绑定、提交面完整集合相等、重入防护、以及降级 manifest 不得绕过归属保护。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part3 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string OtherPath = "flows/other.flow.json";
    private const string AddedPath = "flows/generated.flow.json";

    public R56ReferenceActivationWiringTests_Part3()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w3-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void Seed(string rel, string text)
    {
        var full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static string FlowJson(string name, string status, string config) => $$"""
        {
          "schema": "mistletoe.workflow",
          "schemaVersion": 1,
          "name": "{{name}}",
          "activation": { "status": "{{status}}" },
          "nodes": [
            { "nodeId": "n-1", "kind": "resource.oneDragonConfig", "ref": { "config": "{{config}}", "revision": "rev-1" } }
          ]
        }
        """;

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, null, null, effects);

    private MigrationSwitchTransaction Begin(IMigrationEffectService effects, IEnumerable<ChangeRecord> changes, string txId = "t1")
    {
        var tx = NewTx(effects);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan AddedTargetPlan()
        => new([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(AddedPath, ChangeKind.Added, NewContent: FlowJson("生成", "candidate-ready", "配置B")),
        ]);

    private static ChangeRecord[] AddedTargetChanges() =>
    [
        new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
        new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added },
    ];

    private MigrationActivationRequest ActivationFor(MigrationSwitchTransaction tx, string path)
    {
        string hash = new('0', 64);
        var manifest = tx.LoadManifest();
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-1（正常路径收敛）**：新增文件被激活后回滚——归属预检在写入之前完成，
    /// 撤销激活改变字节后仍能按预核归属删除该新增文件，并恢复基线字节。
    /// </summary>
    [Fact]
    public void Rollback_AddedActivationTarget_ConvergesAndRemovesIt()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Contains("\"active\"", File.ReadAllText(Full(AddedPath)));

        var rollback = tx.Rollback();

        Assert.True(rollback.Success, rollback.Reason);
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.False(File.Exists(Full(AddedPath)));                       // 本事务新增（含激活撤销后）被清理
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-1（他方替换支）**：新增激活目标被他方替换后回滚 ⇒ 归属预检先于任何写入，
    /// 他方内容**不被改写**，回滚阻断。
    /// </summary>
    [Fact]
    public void Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var foreign = FlowJson("他人", "active", "配置X");
        Seed(AddedPath, foreign);                                        // 他方替换（状态仍 active）

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + AddedPath, rollback.Reason);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // **未被撤销写入改写**
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-2**：把 manifest 的 `realEffectsRequired` 降级为 false 并重算摘要，
    /// 归属保护**不得**失效（判据绑定本实例）；他方文件仍被保留。
    /// </summary>
    [Fact]
    public void DowngradedManifestFlag_DoesNotBypassRollbackOwnership()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, AddedPath)).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        var foreign = FlowJson("他人", "active", "配置X");
        Seed(AddedPath, foreign);

        var tampered = tx.LoadManifest()!;                              // 降级并重算摘要（伪造者知道算法）
        tampered.RealEffectsRequired = false;
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(tampered, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // 他方文件未被删除
        Assert.True(tx.LoadManifest()!.Stage is MigrationStage.Blocked or MigrationStage.RollingBack);
    }

    /// <summary>
    /// **会诊第 2 轮 MUST-3**：激活写入必须绑定「已确认写集」的字节版本——在事务前置检查**之后**
    /// 注入锁外改动（状态仍为 candidate-ready），端口必须按版本哈希拒绝，不得基于漂移内容激活。
    /// </summary>
    [Fact]
    public void Activation_VersionBinding_RejectsDriftAfterPrecheck()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);

        // 事务前置检查已完成之后、端口写入之前：锁外改动（状态不变）
        effects.OnStatusRead = _ => Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置X"));

        var activation = tx.ActivateCandidate(ActivationFor(tx, FlowPath));

        Assert.False(activation.Success);
        Assert.Contains("activation_content_hash_mismatch", activation.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Contains("配置X", File.ReadAllText(Full(FlowPath)));      // 漂移内容未被写入为 active
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**会诊第 2 轮 MUST-4**：提交面的文件集合必须**相等**——写集外基线文件缺失同样阻断。</summary>
    [Fact]
    public void Commit_RejectsMissingBaselineFile()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(OtherPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")])).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);
        Assert.True(tx.RehearseRollback().Success);

        File.Delete(Full(OtherPath));                                    // 写集外基线文件在确认后被删除

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.Equal("missing_baseline_file:" + OtherPath, commit.Reason);
    }

    /// <summary>**会诊第 2 轮 IMPORTANT-5**：只快照+登记 Added、尚未写入即中止 ⇒ 可安全回滚（不因缺归属证据被永久阻断）。</summary>
    [Fact]
    public void AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = AddedPath, Kind = ChangeKind.Added }]);
        Assert.False(File.Exists(Full(AddedPath)));

        var rollback = tx.Rollback();

        Assert.True(rollback.Success, rollback.Reason);
        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊第 2 轮 IMPORTANT-7**：端口回调内同实例重入（monitor 可重入）必须被拒绝，
    /// 且重入改变了阶段时外层**不得**再发布成功阶段。
    /// </summary>
    [Fact]
    public void ReentrantMutationFromEffectCallback_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        MigrationResult? inner = null;
        effects.ReentrancyTarget = tx;
        effects.OnApply = _ => inner = tx.Rollback();                    // 端口回调内的同实例重入

        var apply = tx.ApplyReferenceUpdate(new MigrationReferenceUpdatePlan(
            [new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B")]));

        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);       // 重入被拒
        Assert.False(apply.Success);                                     // 外层不得报告成功
        Assert.Contains("concurrent_state_change_after_effect", apply.Reason);
        Assert.NotEqual(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);
    }
}
