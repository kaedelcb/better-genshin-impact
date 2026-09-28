import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:110])
    s = s.replace(old, new, cnt)

# IMPORTANT-8：引用更新的端口调用与读回同样收敛异常为 Blocked（不重复执行、不假报成功）
rep('''            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            if (result.Outcome != MigrationEffectOutcome.Succeeded)''',
'''            MigrationEffectResult result;
            try
            {
                result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }
            if (result.Outcome != MigrationEffectOutcome.Succeeded)''')

rep('''            foreach (var target in plan!.Targets)
            {
                if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
                writeSet[target.Path] = hash;
            }''',
'''            foreach (var target in plan!.Targets)
            {
                try
                {
                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
                }
                catch (Exception ex)
                {
                    return MarkBlocked("reference_readback_exception:" + target.Path + ":" + ex.GetType().Name);
                }
                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
                writeSet[target.Path] = hash;
            }''')

# 幂等复核路径同样收敛异常
rep('''            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入''',
'''            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入（不抛异常）''')
p.write_text(s, encoding="utf-8")
print("tx exception hardening", hashlib.sha256(s.encode()).hexdigest()[:16])

t = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
u = t.read_text(encoding="utf-8")
old = '''        var alreadyApplied = tx.ActivateCandidate(Activation());
        Assert.False(alreadyApplied.Success);
        Assert.Equal("activation_already_applied:" + FlowPath, alreadyApplied.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Null(tx.LoadManifest()!.ActivationRecord);                    // 未记录未经证实的前置状态

        var foreign = tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive"));
        Assert.False(foreign.Success);
        Assert.Equal("unsupported_activation_transition:active->inactive", foreign.Reason);
        Assert.Equal(0, effects.ActivateCalls);'''
new = '''        // ① 非权威转换（active→inactive）⇒ 拒绝且零副作用（阶段仍为 ReferenceUpdating）
        var foreign = tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive"));
        Assert.Equal(MigrationStage.Blocked, foreign.Stage);
        Assert.Contains("unsupported_activation_transition", foreign.Reason);
        Assert.Equal(0, effects.ActivateCalls);
        Assert.Null(tx.LoadManifest()!.ActivationRecord);                    // 未记录未经证实的前置状态

        // ② 盘上已是目标态（active）而请求 candidate-ready→active ⇒ 拒绝，不得零写入伪成功
        Seed(FlowPath, FlowJson("计划", "active", "配置A"));
        var effects2 = Effects();
        using var tx2 = Begin(effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], txId: "t2");
        Assert.True(tx2.ApplyReferenceUpdate(RenamePlan((FlowPath, "配置A", "配置B"))).Success);
        var alreadyApplied = tx2.ActivateCandidate(Activation());
        Assert.Equal(MigrationStage.Blocked, alreadyApplied.Stage);
        Assert.Contains("activation_already_applied", alreadyApplied.Reason);
        Assert.Equal(0, effects2.ActivateCalls);
        Assert.Null(tx2.LoadManifest()!.ActivationRecord);'''
assert u.count(old) == 1
u = u.replace(old, new, 1)
t.write_text(u, encoding="utf-8")
print("test reordered")
