import pathlib
s=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs").read_text(encoding="utf-8")
a=s.index("HashSet<string>? ownershipVerified = null;")
b=s.index("DeleteRecordedAdditions(m, _configRoot, ownershipVerified)", a)
pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/region.txt").write_text(s[a:b+80], encoding="utf-8")
print("written")
