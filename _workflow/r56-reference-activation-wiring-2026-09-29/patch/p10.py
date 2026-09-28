import pathlib, hashlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
old = '''        Assert.Equal("scripted", tx.LoadValidated()!.BlockedReason);'''
new = '''        Assert.StartsWith("reference_update_rejected:scripted;writes=", tx.LoadValidated()!.BlockedReason, StringComparison.Ordinal);'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("ok", hashlib.sha256(s.encode()).hexdigest()[:16])
