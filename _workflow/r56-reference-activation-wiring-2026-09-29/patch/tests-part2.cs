
    // ------------------------------------------------------------------ 故障窗口

    /// <summary>REF-F1：写集与变更登记不一致（未登记路径 / 登记未覆盖）⇒ 置 Blocked、零写入。</summary>
    [Fact]
    public void WritesetMismatch_WithChangeRegistry_BlocksWithoutWriting()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var unregistered = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"), (SecondPath, "配置A", "配置B")));
        Assert.False(unregistered.Success);
        Assert.StartsWith("reference_writeset_mismatch:not_registered:", unregistered.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ApplyCalls);                                    // 校验先于副作用
        Assert.Contains("配置A", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>REF-F1（漏项支）：登记了变更但写集未覆盖 ⇒ Blocked、零写入。</summary>
    [Fact]
    public void WritesetMismatch_RegisteredNotCovered_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified },
        ]);

        var partial = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(partial.Success);
        Assert.StartsWith("reference_writeset_mismatch:registered_not_covered:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ApplyCalls);
    }

    /// <summary>REF-F2：自报成功但盘上未变（假成功）⇒ 读回不符 ⇒ Blocked、阶段不推进。</summary>
    [Fact]
    public void SelfReportedSuccessWithoutRealWrite_BlocksOnReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService { SkipApplyWrite = true };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var fake = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(fake.Success);
        Assert.StartsWith("reference_readback_failed:" + FlowPath, fake.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.True(BytesEqual(Full(FlowPath), baseline));
    }

    /// <summary>REF-F2（内容支）：写出错误内容 ⇒ 字节读回哈希与语义读回都不符 ⇒ Blocked。</summary>
    [Fact]
    public void WrongContentWritten_BlocksOnReadback()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService
        {
            ExtraWrite = (FlowPath, FlowJson("计划", "candidate-ready", "配置A")),   // 改写为不含目标引用的内容
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var wrong = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(wrong.Success);
        Assert.StartsWith("reference_readback_failed:" + FlowPath, wrong.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-F9：副作用改动了**声明写集之外**的基线文件（含删除）⇒ Blocked、阶段不推进。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteOutsideDeclaredWriteset_Blocks(bool deleteInstead)
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var effects = new ScriptedEffectService
        {
            ExtraWrite = deleteInstead ? null : (ConfPath, "{\"config\":\"配置B\"}"),
            ExtraDeletePath = deleteInstead ? ConfPath : null,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);

        var outside = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B")));

        Assert.False(outside.Success);
        Assert.StartsWith("unexpected_", outside.Reason, StringComparison.Ordinal);
        Assert.Contains(ConfPath, outside.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

    /// <summary>REF-F4：激活目标不在已确认写集内 ⇒ Blocked、零激活写入、阶段不推进。</summary>
    [Fact]
    public void ActivationTargetOutsideWriteset_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var secondBaseline = File.ReadAllBytes(Full(SecondPath));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var beforeActivate = tx.LoadManifest()!.Stage;

        var outside = tx.ActivateCandidate(Activation(SecondPath));

        Assert.False(outside.Success);
        Assert.Equal("activation_target_not_in_writeset:" + SecondPath, outside.Reason);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.NotEqual(beforeActivate, tx.LoadManifest()!.Stage);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
    }

    /// <summary>REF-F5：自报激活成功但状态未真正持久化 ⇒ 状态读回不符 ⇒ Blocked、禁止提交。</summary>
    [Fact]
    public void ActivationReadbackMismatch_Blocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService { SkipActivateWrite = true };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);

        var activate = tx.ActivateCandidate(Activation());

        Assert.False(activate.Success);
        Assert.StartsWith("activation_readback_failed:" + FlowPath, activate.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:activation_readback_failed:" + FlowPath + ":activation_status=candidate-ready", tx.Commit().Reason);
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
    }

    /// <summary>REF-F3：真实副作用成功、阶段落盘前崩溃 ⇒ 阶段仍为 SnapshotReady；恢复后回到完整旧态。</summary>
    [Fact]
    public void CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var baseline = File.ReadAllBytes(Full(FlowPath));
        var effects = new ScriptedEffectService();
        var crashed = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage => { if (stage == MigrationStage.ReferenceUpdating) throw new InvalidOperationException("注入阶段落盘失败"); },
            null, effects);
        Assert.True(crashed.BeginTransaction("crash").Success);
        Assert.True(crashed.TakeSnapshot().Success);
        Assert.True(crashed.RecordChanges([new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]).Success);

        Assert.Throws<InvalidOperationException>(() => crashed.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))));

        Assert.Equal(MigrationStage.SnapshotReady, crashed.LoadManifest()!.Stage);      // **阶段未推进**（只推进到副作用成功且读回之后）
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));                     // 真实副作用确已发生
        crashed.Dispose();

        using var reopened = NewTx(effects);
        Assert.True(reopened.TryAcquireExclusive().Success);
        Assert.True(reopened.RecoverOnStart().Success);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                              // 恢复后回到完整旧态字节
        Assert.Equal(MigrationStage.RolledBack, reopened.LoadManifest()!.Stage);
    }

    /// <summary>REF-F6：回滚撤销真实激活、恢复旧字节与旧引用，且不覆盖事务外新增文件。</summary>
    [Fact]
    public void Rollback_RevertsActivationAndBytes_KeepsForeignAdditions()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(ConfPath, "{\"config\":\"配置A\"}");
        var flowBaseline = File.ReadAllBytes(Full(FlowPath));
        var addedPath = "flows/generated.flow.json";
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = addedPath, Kind = ChangeKind.Added },
        ]);
        var plan = new MigrationReferenceUpdatePlan(
        [
            new MigrationReferenceWriteTarget(FlowPath, ChangeKind.Modified, RenameFrom: "配置A", RenameTo: "配置B"),
            new MigrationReferenceWriteTarget(addedPath, ChangeKind.Added, NewContent: FlowJson("生成", "active", "配置B")),
        ]);
        Assert.True(tx.ApplyReferenceUpdate(plan).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.Contains("\"active\"", File.ReadAllText(Full(FlowPath)));

        Seed("other/y.json", "{\"other\":true}");                                       // 事务外新增（未登记）
        Assert.True(tx.Rollback().Success);

        Assert.Equal(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));                          // 旧态字节与旧引用一致
        Assert.Contains("candidate-ready", File.ReadAllText(Full(FlowPath)));
        Assert.DoesNotContain("配置B", File.ReadAllText(Full(FlowPath)));
        Assert.False(File.Exists(Full(addedPath)));                                     // 本事务新增被清理
        Assert.True(File.Exists(Full("other/y.json")));                                 // 事务外新增未被覆盖/删除
        Assert.Equal("{\"other\":true}", File.ReadAllText(Full("other/y.json")));
    }

    /// <summary>REF-F7：manifest 被篡改为「已激活/已提交但无真实证据」⇒ 结构校验判无效，授权与提交均拒。</summary>
    [Fact]
    public void ManifestTamper_WithoutRealEvidence_IsRejected()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.NotNull(tx.LoadValidated());

        // (a) 清空写集并**重算摘要**（伪造者知道摘要算法）⇒ 仍判无效
        var cleared = tx.LoadManifest()!;
        cleared.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        cleared.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(cleared);
        WriteManifestJson(tx, cleared);
        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);

        // (b) 清空激活记录并重算摘要 ⇒ 仍判无效
        var noActivation = tx.LoadManifest()!;
        noActivation.ActivationRecord = null;
        noActivation.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(noActivation);
        WriteManifestJson(tx, noActivation);
        Assert.Null(tx.LoadValidated());

        // (c) 仅改阶段、不重算摘要 ⇒ 摘要不符
        var stageOnly = tx.LoadManifest()!;
        stageOnly.Stage = MigrationStage.Activated;
        WriteManifestJson(tx, stageOnly);
        Assert.Null(tx.LoadValidated());

        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>REF-F8：写入中途故障（部分已落盘）⇒ 无假成功、不推进阶段；回滚后两个目标都回到旧态字节。</summary>
    [Fact]
    public void PartialWriteThenFault_NoFalseSuccess_AndRollbackRestoresAllTargets()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        Seed(SecondPath, FlowJson("其它", "candidate-ready", "配置A"));
        var flowBaseline = File.ReadAllBytes(Full(FlowPath));
        var secondBaseline = File.ReadAllBytes(Full(SecondPath));
        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Unknown,
            ApplyTargetsLimit = 1,
        };
        using var tx = BeginWithEffects(effects, changes:
        [
            new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified },
            new ChangeRecord { Path = SecondPath, Kind = ChangeKind.Modified },
        ]);

        var partial = tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"), (SecondPath, "配置A", "配置B")));

        Assert.False(partial.Success);
        Assert.StartsWith("reference_update_unknown:", partial.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        Assert.Equal("blocked:scripted_partial", tx.Commit().Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);

        Assert.True(tx.Rollback().Success);                                             // Blocked ⇒ 可回滚
        Assert.True(BytesEqual(Full(FlowPath), flowBaseline));
        Assert.True(BytesEqual(Full(SecondPath), secondBaseline));
        Assert.True(tx.LoadManifest()!.ReferenceWriteSet.Count == 0
            || tx.LoadManifest()!.ReferenceWriteSet.All(e => HashOf(Full(e.Key)) == HashOf(Full(e.Key))));
    }

    /// <summary>BOM 与编码形态：真实写入与回滚都保留原 UTF-8 BOM 形态。</summary>
    [Fact]
    public void RealWrites_PreserveOriginalBomShape_AndRollbackRestoresIt()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"), bom: true);
        var baseline = File.ReadAllBytes(Full(FlowPath));
        Assert.Equal(0xEF, baseline[0]);
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var afterRename = File.ReadAllBytes(Full(FlowPath));
        Assert.Equal(0xEF, afterRename[0]);                                             // 保留 BOM
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));

        Assert.True(tx.ActivateCandidate(Activation()).Success);
        Assert.Equal(0xEF, File.ReadAllBytes(Full(FlowPath))[0]);
        Assert.True(tx.Rollback().Success);
        Assert.True(BytesEqual(Full(FlowPath), baseline));                              // 回滚逐字节含 BOM
    }

    /// <summary>隔离边界：真实写入只落在传入的配置根内；事务根外的路径不因本批被触碰。</summary>
    [Fact]
    public void RealWrites_StayInsideConfigRoot()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var outsideDir = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "keep.json"), "{\"keep\":1}");
        var effects = new ScriptedEffectService();
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);
        Assert.True(tx.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        Assert.True(tx.ActivateCandidate(Activation()).Success);

        Assert.Equal("{\"keep\":1}", File.ReadAllText(Path.Combine(outsideDir, "keep.json")));
        Assert.Single(Directory.EnumerateFiles(outsideDir));
        Assert.Equal(0, Directory.EnumerateFiles(Path.Combine(_root, "tx"), "*", SearchOption.AllDirectories)
            .Count(f => f.EndsWith(".flow.json", StringComparison.Ordinal)));
    }

    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)
        => File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
