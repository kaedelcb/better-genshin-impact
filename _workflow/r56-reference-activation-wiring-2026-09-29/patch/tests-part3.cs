
/// <summary>
/// **会诊第 1 轮（gpt-6-astra／medium）发现的修复与补强夹具**：5 项 MUST + 4 项 IMPORTANT 的逐条反例。
/// 与本批主夹具同文件、同隔离根策略。
/// </summary>
public sealed class R56ReferenceActivationWiringTests_Part2 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string SecondPath = "flows/other.flow.json";

    public R56ReferenceActivationWiringTests_Part2()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w2-" + Guid.NewGuid().ToString("N")[..8]);
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

    private MigrationSwitchTransaction NewTx(IMigrationEffectService effects, Action<MigrationStage>? hook = null,
        Action<string>? restoredHook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, hook, restoredHook, effects);

    private MigrationSwitchTransaction Begin(IMigrationEffectService effects, IEnumerable<ChangeRecord> changes,
        Action<MigrationStage>? hook = null, Action<string>? restoredHook = null, string txId = "t1")
    {
        var tx = NewTx(effects, hook, restoredHook);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges(changes).Success);
        return tx;
    }

    private static MigrationReferenceUpdatePlan RenamePlan(params (string Path, string From, string To)[] targets)
        => new(targets.Select(t => new MigrationReferenceWriteTarget(t.Path, ChangeKind.Modified,
            RenameFrom: t.From, RenameTo: t.To)).ToList());

    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();

    /// <summary>
    /// **会诊 MUST-1**：引用更新确认之后、激活之前，该文件被锁外改动（引用被换成 X、状态仍为 candidate-ready）
    /// ⇒ 激活必须 fail-closed，**不得把漂移连同哈希一起「合法化」**。
    /// </summary>
    [Fact]
    public void Activation_AfterDrift_IsNotAbsorbed()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var confirmed = tx.LoadValidated()!.ReferenceWriteSet;

        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置X"));      // 锁外改动（内容漂移）

        var activation = tx.ActivateCandidate(Activation());

        Assert.False(activation.Success);
        Assert.Equal("activation_precondition_drifted:" + FlowPath, activation.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ActivateCalls);                              // 未触发副作用
        Assert.Equal(confirmed[FlowPath], tx.LoadManifest()!.ReferenceWriteSet[FlowPath]);   // 写集未被漂移污染
        Assert.Contains("配置X", File.ReadAllText(Full(FlowPath)));           // 漂移文件未被改写为 active
        Assert.DoesNotContain("\"active\"", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>
    /// **会诊 IMPORTANT-7**：只接受权威 D13 转换；盘上已是目标态或与声明的 before 不一致 ⇒ 拒绝（不得零写入伪成功）。
    /// </summary>
    [Fact]
    public void Activation_RejectsAlreadyAppliedAndForeignTransitions()
    {
        Seed(FlowPath, FlowJson("计划", "active", "配置A"));                 // 盘上已是 active
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        var alreadyApplied = tx.ActivateCandidate(Activation());
        Assert.False(alreadyApplied.Success);
        Assert.Equal("activation_already_applied:" + FlowPath, alreadyApplied.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Null(tx.LoadManifest()!.ActivationRecord);                    // 未记录未经证实的前置状态

        var foreign = tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive"));
        Assert.False(foreign.Success);
        Assert.Equal("unsupported_activation_transition:active->inactive", foreign.Reason);
        Assert.Equal(0, effects.ActivateCalls);
    }

    /// <summary>**会诊 IMPORTANT-8**：副作用端口抛异常 ⇒ 收敛为 Blocked（未知态），且不重复执行。</summary>
    [Fact]
    public void EffectPortExceptions_BecomeBlockedWithoutRepeat()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var throwing = Effects();
        throwing.ThrowOnApply = true;
        using (var tx = Begin(throwing, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "apply"))
        {
            var apply = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));
            Assert.False(apply.Success);
            Assert.StartsWith("reference_update_exception_unknown:", apply.Reason, StringComparison.Ordinal);
            Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
            Assert.Equal(1, throwing.ApplyCalls);                            // 未盲目重试
            Assert.False(tx.Commit().Success);
            var runs = 0;
            Assert.False(tx.TryRunProduction(() => runs++).Success);
            Assert.Equal(0, runs);
        }

        var throwing2 = Effects();
        throwing2.ThrowOnActivate = true;
        using var tx2 = Begin(throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "activate");
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var activate = tx2.ActivateCandidate(Activation());
        Assert.False(activate.Success);
        Assert.StartsWith("activation_exception_unknown:", activate.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx2.LoadManifest()!.Stage);
        Assert.Equal(1, throwing2.ActivateCalls);
    }

    /// <summary>**会诊 IMPORTANT-8**：敌意文档形状（标量节点）⇒ 结构化拒绝而非抛异常，且原文件未被改写。</summary>
    [Fact]
    public void HostileDocumentShape_IsRejectedNotThrown()
    {
        Seed(FlowPath, "{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"name\":\"坏\",\"activation\":{\"status\":\"candidate-ready\"},\"nodes\":[1]}");
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var rejected = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(rejected.Success);
        Assert.Contains("reference_document_unusable", rejected.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));   // 形状异常文档未被改写
    }

    /// <summary>**会诊 MUST-3**：副作用在写集外**新建**文件 ⇒ 检测并阻断（只比较基线清单会漏掉该面）。</summary>
    [Fact]
    public void NewFileOutsideWriteset_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        effects.ExtraWrite = ("rogue/new.json", "{\"rogue\":true}");
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var outside = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(outside.Success);
        Assert.StartsWith("unexpected_new_file_outside_writeset:rogue/new.json", outside.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Null(tx.LoadManifest()!.ReferenceWriteSet.Count == 0 ? null : tx.LoadManifest()!.ReferenceWriteSet.Count.ToString());
    }

    /// <summary>**会诊 MUST-3（提交面）**：登记为 Added 的目标在**确认写集之外**，提交前文件集合核对同样拒绝。</summary>
    [Fact]
    public void CommitRejectsNewFilesOutsideWriteset()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);

        Seed("rogue/after.json", "{\"late\":true}");                         // 确认写集之后出现的新文件

        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.StartsWith("unexpected_new_file_outside_writeset:", commit.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// **会诊 MUST-2**：真实写入路径下，登记为「本事务新增」的文件被锁外改动（不再等于本事务所写字节）
    /// ⇒ 回滚**保留该文件**并阻断，绝不把他方文件当自己的新增删掉。
    /// </summary>
    [Fact]
    public void ForeignAddedFile_IsPreservedAndRollbackBlocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var addedPath = "flows/generated.flow.json";
        var effects = Effects();
        using var tx = Begin(effects, [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added }]);
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);
        Assert.True(tx.ApplyReferenceUpdate(plan).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        Seed(addedPath, FlowJson("他人", "candidate-ready", "配置X"));       // 锁外写方改写该「新增」文件

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + addedPath, rollback.Reason);
        Assert.True(File.Exists(Full(addedPath)));                            // **他方文件被保留**
        Assert.Contains("配置X", File.ReadAllText(Full(addedPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);       // 未报告完整回滚
    }

    /// <summary>
    /// **会诊 MUST-2（未创建即回滚支）**：登记 Added 但真实写入被拒（同名文件已存在）⇒ 回滚不得删除该文件。
    /// </summary>
    [Fact]
    public void AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var addedPath = "flows/foreign.flow.json";
        var effects = Effects();
        using var tx = Begin(effects, [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added }]);
        Seed(addedPath, FlowJson("他人", "candidate-ready", "配置A"));        // 他方先创建同名文件
        var plan = new MigrationReferenceUpdatePlan([
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);

        var apply = tx.ApplyReferenceUpdate(plan);
        Assert.False(apply.Success);
        Assert.Contains("added_target_already_exists", apply.Reason);         // 不覆盖他方文件
        Assert.Contains("他人", File.ReadAllText(Full(addedPath)));

        var rollback = tx.Rollback();
        Assert.False(rollback.Success);
        Assert.StartsWith("rollback_addition_without_ownership_evidence:", rollback.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(Full(addedPath)));
        Assert.Contains("他人", File.ReadAllText(Full(addedPath)));
    }

    /// <summary>
    /// **会诊 MUST-5**：回滚后的旧态核对必须覆盖**完整基线字节集**（不依赖成功写集）。
    /// 此处用逐文件恢复接缝在恢复后污染一个文件 ⇒ 回滚必须阻断，而不是报告 `RolledBack`。
    /// </summary>
    [Fact]
    public void Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = Effects();
        // 在所有真实配置根恢复完成后，把**未被写入集覆盖**的那份基线文件污染一次
        using var tx = NewTx(effects, restoredHook: rel =>
        {
            if (rel == SecondPath) File.WriteAllText(Full(SecondPath), FlowJson("其它", "candidate-ready", "配置ZZZ"), new UTF8Encoding(false));
        });
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_restore_bytes_differ:" + SecondPath, rollback.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);       // 未假报完整回滚
        Assert.NotEqual(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
    }

    /// <summary>
    /// **会诊 IMPORTANT-9**：回滚必须真的走「撤销真实激活」路径（端口收到 `active→candidate-ready` 的请求），
    /// 而不是仅靠快照还原字节而恰好满足最终字节断言。
    /// </summary>
    [Fact]
    public void Rollback_InvokesRealActivationUndo()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Single(effects.ActivationRequests);
        Assert.Equal(("candidate-ready", "active"),
            (effects.ActivationRequests[0].ExpectedBeforeStatus, effects.ActivationRequests[0].TargetStatus));

        Assert.True(tx.Rollback().Success);

        Assert.Equal(2, effects.ActivationRequests.Count);                    // 第二次＝撤销
        Assert.Equal(("active", "candidate-ready"),
            (effects.ActivationRequests[1].ExpectedBeforeStatus, effects.ActivationRequests[1].TargetStatus));
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
        Assert.DoesNotContain("配置B", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>**会诊 IMPORTANT-6**：确认引用更新后不得再改变更登记（会破坏精确写集对应关系）。</summary>
    [Fact]
    public void ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        var late = tx.RecordChanges([new ChangeRecord { Path = "flows/late.flow.json", Kind = ChangeKind.Added }]);

        Assert.False(late.Success);
        Assert.Equal("change_registry_frozen_after_reference_update", late.Reason);
        Assert.Single(tx.LoadManifest()!.ChangedFiles);
    }

    /// <summary>
    /// **并发交错（会诊 IMPORTANT-9 对 REF-C3 的补强）**：同一实例上并发发起「提交」与「回滚」。
    /// 事务内串行边界保证二者不重叠执行；断言不变量：绝不出现「回滚报告完成却仍可生产执行」，
    /// 且终态只可能是 Committed（新态）或 RolledBack（旧态），不会半途混合。
    /// </summary>
    [Fact]
    public void ConcurrentCommitAndRollback_UpholdInvariants()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = Effects();
        using var tx = Begin(effects, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);

        var gate = new ManualResetEventSlim(false);
        MigrationResult commitResult = null!, rollbackResult = null!;
        var commitTask = Task.Run(() => { gate.Wait(); commitResult = tx.Commit(); });
        var rollbackTask = Task.Run(() => { gate.Wait(); rollbackResult = tx.Rollback(); });
        gate.Set();
        Assert.True(Task.WaitAll([commitTask, rollbackTask], TimeSpan.FromSeconds(30)));

        var manifest = tx.LoadManifest()!;
        var text = File.ReadAllText(Full(FlowPath));
        var productionRuns = 0;
        tx.TryRunProduction(() => productionRuns++);

        // 不变量：至少一方成功；终态与盘上状态一致；回滚成功则旧态字节、提交成功则新态字节；生产许可不得先于提交
        Assert.True(commitResult.Success || rollbackResult.Success);
        Assert.Contains(manifest.Stage, new[] { MigrationStage.Committed, MigrationStage.RolledBack });
        if (manifest.Stage == MigrationStage.RolledBack)
        {
            Assert.True(File.ReadAllBytes(Full(FlowPath)).AsSpan().SequenceEqual(baseline));
            Assert.Equal(0, productionRuns);
            Assert.DoesNotContain("\"active\"", text);
        }
        else
        {
            Assert.True(commitResult.Success);
            Assert.Contains("\"active\"", text);
        }
    }
}
