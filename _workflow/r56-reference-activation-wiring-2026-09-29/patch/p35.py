import pathlib, ast
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
entry = '''  {"id": 'M8-no-reference-gate-for-activation',
   "desc": '去掉「注入真实端口后阶段标记入口必须拒绝」的门禁',
   "old": '        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)\\n                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);',
   "new": '        => Advance(MigrationStage.ReferenceUpdating);   // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
'''
marker = "]\n\nCASE_SELECTOR"
assert s.count(marker) == 1
s = s.replace(marker, entry + marker, 1)
p.write_text(s, encoding="utf-8")
tree=ast.parse(s)
for node in tree.body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],'id','')=='MUTATIONS':
        print("entries:", len(ast.literal_eval(node.value)))
