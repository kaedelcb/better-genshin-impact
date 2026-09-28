import pathlib, ast
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
i = s.index("M28-added-target-overwrite-allowed")
close = s.index("\n]\n", i) + 1          # 列表结尾的 ']' 行
entry = '''  {"id": 'M8-no-reference-gate-for-activation',
   "desc": '\u53bb\u6389\u300c\u6ce8\u5165\u771f\u5b9e\u7aef\u53e3\u540e\u9636\u6bb5\u6807\u8bb0\u5165\u53e3\u5fc5\u987b\u62d2\u7edd\u300d\u7684\u95e8\u7981',
   "old": '        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)\\n                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);',
   "new": '        => Advance(MigrationStage.ReferenceUpdating);   // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
'''
s2 = s[:close] + entry + s[close:]
p.write_text(s2, encoding="utf-8")
tree = ast.parse(s2)
for node in tree.body:
    if isinstance(node, ast.Assign) and getattr(node.targets[0], "id", "") == "MUTATIONS":
        print("entries:", len(ast.literal_eval(node.value)))
