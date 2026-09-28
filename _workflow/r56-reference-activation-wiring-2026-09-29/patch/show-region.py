import pathlib
s=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs").read_text(encoding="utf-8")
a=s.index("HashSet<string>? ownershipVerified = null;")
b=s.index("DeleteRecordedAdditions(m, _configRoot, ownershipVerified)", a)
print(s[a:b+80])
