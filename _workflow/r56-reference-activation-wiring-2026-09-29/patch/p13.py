import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations.py")
s = p.read_text(encoding="utf-8")

old_m1 = '''  ("M1-stage-before-readback", WT,
   "读回确认之前就推进阶段（真实副作用读回被绕过）",
   """            // **读回确认先于阶段推进**：语义读回（引用已改写）+ 字节读回（写入后哈希）
            var writeSet = new Dictionary<string, string>(StringComparer.Ordinal);""",
   """            var writeSet = new Dictionary<string, string>(StringComparer.Ordinal);
            // MUTANT: 先推进阶段，读回被降级为事后步骤
            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);""",
   "R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback"),'''
new_m1 = '''  ("M1-no-readback-confirmation", WT,
   "不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）",
   """                if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);""",
   """                // MUTANT: 读回确认被跳过""",
   "R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback"),'''
assert s.count(old_m1) == 1
s = s.replace(old_m1, new_m1, 1)

old_m5 = '''  ("M5-no-commit-recheck", WT,
   "去掉提交前的真实证据复核（提交不再核对盘上漂移）",
   """                if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);""",
   """                if (false) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);   // MUTANT""",
   "R56ReferenceActivationWiringTests.Commit_RefusesWhenWrittenFileDriftsAfterActivation"),'''
new_m5 = '''  ("M5-no-commit-recheck", WT,
   "去掉提交前的写集/激活读回复核（提交不再核对盘上漂移）",
   """                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);""",
   """                // MUTANT: 提交前写集复核被跳过""",
   "R56ReferenceActivationWiringTests.Commit_RefusesWhenWrittenFileDriftsAfterActivation"),'''
assert s.count(old_m5) == 1
s = s.replace(old_m5, new_m5, 1)

# 只重跑 M1 与 M5
s = s.replace('''for mid, target, desc, old, new, test in MUTATIONS:''',
              '''ONLY = {"M1-no-readback-confirmation", "M5-no-commit-recheck"}
for mid, target, desc, old, new, test in MUTATIONS:
    if mid not in ONLY: continue''')
p.write_text(s, encoding="utf-8")
print("mutations redefined")
