import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
s = s.replace('WT = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"',
 'WT = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"\n'
 'SRC_SVC = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"')
s = s.replace('''for mid, desc, old, new, target_name in MUTATIONS:
    case = CASE_SELECTOR.get(mid, "")
''', '''for entry in MUTATIONS:
    mid = entry["id"]; desc = entry["desc"]; old = entry["old"]; new = entry["new"]; target_name = entry["test"]
    entry_src = entry["src"] or WT
    case = CASE_SELECTOR.get(mid, "")
''')
s = s.replace("    src_path = ROOT / WT\n    src_text = src_path.read_text(encoding=\"utf-8\")",
              "    src_path = ROOT / entry_src\n    src_text = src_path.read_text(encoding=\"utf-8\")")
s = s.replace('"id": mid, "source": WT, "description": desc,', '"id": mid, "source": entry_src, "description": desc,')
p.write_text(s, encoding="utf-8")
import ast; ast.parse(s); print("runner fixed and syntax ok")
