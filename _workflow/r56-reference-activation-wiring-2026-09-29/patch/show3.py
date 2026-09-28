import pathlib
s=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs").read_text(encoding="utf-8")
a=s.index("        if (!string.IsNullOrEmpty(request.ExpectedContentHash)")
b=s.index("AtomicWrite(full, EncodeText(text!, hasBom), hasBom);", a)
pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/region2.txt").write_text(s[a:b+55],encoding="utf-8")
print("written", b-a)
