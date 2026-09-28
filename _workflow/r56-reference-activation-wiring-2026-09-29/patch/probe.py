import pathlib
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
old='                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）'
new='                var ownerBound = m.RealEffectsRequired;   // TEMP-PROBE'
assert s.count(old)==1
p.write_text(s.replace(old,new,1),encoding="utf-8")
print("probe applied")
