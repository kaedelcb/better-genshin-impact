import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations.py")
s = p.read_text(encoding="utf-8")
old = '''   """                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);""",
   """                // MUTANT: 提交前写集复核被跳过""",'''
new = '''   """                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);""",
   """                // MUTANT: 提交前的写集/激活读回复核整体被跳过（两层复核对同一漂移各自独立判别，
                // 单独去掉任一层会被另一层拦下，故此处按「整块削弱」定义突变以取得判别力）""",'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("M5 redefined as whole-block weakening")
