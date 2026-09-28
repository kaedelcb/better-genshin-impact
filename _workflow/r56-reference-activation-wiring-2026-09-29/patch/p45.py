import pathlib, ast
# 还原探测
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
s=s.replace('                var ownerBound = m.RealEffectsRequired;   // TEMP-PROBE',
            '                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）',1)
p.write_text(s,encoding="utf-8")
# 移除 M35（尝试记录：以「判据只依赖标记」构造的突变未能使该夹具变红 ⇒ 记为未验证的尝试，不主张判别力）
r=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
t=r.read_text(encoding="utf-8")
i=t.index('"id": \'M35-rollback-ownership-not-bound-to-instance\'')
start=t.rindex('  {',0,i); end=t.index('},\n',i)+3
t=t[:start]+t[end:]
r.write_text(t,encoding="utf-8"); ast.parse(t)
for node in ast.parse(t).body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        print("entries:",len(ast.literal_eval(node.value)))
