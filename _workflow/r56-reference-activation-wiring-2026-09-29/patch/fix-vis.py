import pathlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
old = "    private sealed class ScriptedEffectService : IMigrationEffectService"
new = "    internal sealed class ScriptedEffectService : IMigrationEffectService"
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("visibility fixed")
