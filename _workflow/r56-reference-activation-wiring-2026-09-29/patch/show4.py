import pathlib
s=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs").read_text(encoding="utf-8")
i=s.index("public MigrationResult Rollback()")
pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/region3.txt").write_text(s[i:i+560],encoding="utf-8")
print("ok")
