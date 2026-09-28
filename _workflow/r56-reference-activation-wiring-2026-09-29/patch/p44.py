import pathlib, ast
old = """public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);"""
new = """public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);   // MUTANT: 重入守卫被删除"""
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
i=s.index('"id": \'M33-no-reentrancy-guard-on-rollback\'')
start=s.rindex('  {', 0, i); end=s.index('},\n', i)+3
entry=('  {"id": %r, "desc": %r,\n   "old": %r,\n   "new": %r,\n   "test": %r,\n   "src": %r},\n' % (
 "M33-no-reentrancy-guard-on-rollback", "回滚入口不再拒绝端口回调内的同实例重入",
 old, new, "R56ReferenceActivationWiringTests_Part3.ReentrantMutationFromEffectCallback_IsRejected",
 "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"))
s=s[:start]+entry+s[end:]
p.write_text(s,encoding="utf-8"); ast.parse(s); print("M33 redefined")
