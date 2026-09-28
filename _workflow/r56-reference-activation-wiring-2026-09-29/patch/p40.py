import pathlib
q=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
u=q.read_text(encoding="utf-8")
anchor='''    /// <summary>
    /// **会诊第 2 轮 MUST-2**：把 manifest 的 `realEffectsRequired` 降级为 false 并重算摘要，'''
new_test = '''    /// <summary>
    /// **会诊第 2 轮 MUST-1/MUST-2（非激活目标支）**：登记为「本事务新增」的文件被锁外替换后回滚 ⇒
    /// 归属预检**先于任何写入**，他方文件必须被保留并阻断；判别力由 M30（整块去掉归属保护）证明——
    /// 该突变下他方文件会被删除，本夹具变红。
    /// </summary>
    [Fact]
    public void ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks()
    {
        Seed(FlowPath, FlowJson("计划", "candidate-ready", "配置A"));
        var effects = new R56ReferenceActivationWiringTests.ScriptedEffectService();
        using var tx = Begin(effects, AddedTargetChanges());
        Assert.True(tx.ApplyReferenceUpdate(AddedTargetPlan()).Success);
        Assert.True(tx.ActivateCandidate(ActivationFor(tx, FlowPath)).Success);   // 激活目标是基线文件，不是新增文件
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);

        var foreign = FlowJson("他人", "candidate-ready", "配置X");
        Seed(AddedPath, foreign);                                              // 他方替换「本事务新增」文件

        var rollback = tx.Rollback();

        Assert.False(rollback.Success);
        Assert.Equal("rollback_addition_not_owned:" + AddedPath, rollback.Reason);
        Assert.True(File.Exists(Full(AddedPath)));                              // **他方文件被保留**
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
    }

''' + anchor
assert u.count(anchor)==1
q.write_text(u.replace(anchor,new_test,1),encoding="utf-8")

p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
old='"test": \'R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten\'},\n  {"id": \'M31-no-activation-content-binding\''
new2='"test": \'R56ReferenceActivationWiringTests_Part3.ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks\'},\n  {"id": \'M31-no-activation-content-binding\''
assert s.count(old)==1
p.write_text(s.replace(old,new2,1),encoding="utf-8")
print("fixture added; M30 retargeted")
