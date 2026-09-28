import pathlib
# 1) 重入守卫覆盖全部变更入口
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
guard='            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);\n'
targets=[
 ('    public MigrationResult TryAcquireExclusive()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult BeginTransaction(string transactionId)\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult TakeSnapshot()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult RecordChanges(IEnumerable<ChangeRecord> changes)\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult RehearseRollback()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult Commit()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult Rollback()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult RecoverOnStart()\n    {\n        lock (_sync)\n        {\n'),
 ('    public MigrationResult AuthorizeProductionExecution()\n    {\n        lock (_sync)\n        {\n'),
]
for t in targets:
    assert s.count(t)==1, t[:60]
    s=s.replace(t, t+guard, 1)
p.write_text(s,encoding="utf-8")
print("reentrancy guard extended to all mutating entries")

# 2) 夹具期望修正
q=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
u=q.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global u
    assert u.count(old)==cnt,(u.count(old),old[:110])
    u=u.replace(old,new,cnt)
rep('''        Assert.False(rollback.Success);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // 他方文件未被删除
        Assert.True(tx.LoadManifest()!.Stage is MigrationStage.Blocked or MigrationStage.RollingBack);''',
'''        Assert.False(rollback.Success);
        Assert.Equal(foreign, File.ReadAllText(Full(AddedPath)));        // 他方文件未被删除
        // 摘要不符 ⇒ manifest 不可验证 ⇒ 回滚拒绝（`manifest_missing_or_invalid`）；关键是**不得**报告 RolledBack
        Assert.NotEqual(MigrationStage.RolledBack, tx.LoadManifest()!.Stage);''')
rep('''        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);       // 重入被拒
        Assert.False(apply.Success);                                     // 外层不得报告成功
        Assert.Contains("concurrent_state_change_after_effect", apply.Reason);
        Assert.NotEqual(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);''',
'''        // 重入被守卫拒绝 ⇒ 内层变更**不产生任何效果**；外层操作随后正常完成（不得被重入破坏）
        Assert.NotNull(inner);
        Assert.Equal("reentrant_mutation_rejected", inner!.Reason);
        Assert.True(apply.Success, apply.Reason);
        Assert.Equal(MigrationStage.ReferenceUpdating, tx.LoadManifest()!.Stage);
        Assert.Equal(2, tx.LoadManifest()!.ChangedFiles.Count + tx.LoadManifest()!.ReferenceWriteSet.Count - 1);
        Assert.Contains("配置B", File.ReadAllText(Full(FlowPath)));''')
q.write_text(u,encoding="utf-8")
print("fixture expectations fixed")
