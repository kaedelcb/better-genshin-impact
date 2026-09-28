import pathlib, ast
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
old = '''   "old": '            if (m.RealEffectsRequired || _effects is not null)\\n            {',\n   "new": '            if (m.RealEffectsRequired)\\n            {   // MUTANT: 门槛只依赖可被改写并重算摘要的持久化标记\','''
assert s.count(old) == 1, s.count(old)
new = '''   "old": '            if ((m.RealEffectsRequired || _effects is not null)\\n                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))\\n                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);',\n   "new": '            // MUTANT: 授权不再要求真实证据（只依赖可改写并重算摘要的持久化标记）','''
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
ast.parse(s)
print("M23 retargeted to the authorisation gate")
