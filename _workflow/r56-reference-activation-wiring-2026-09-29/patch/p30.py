import re, pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
start = s.index("MUTATIONS = [")
end = s.index("\n]\n", start) + 3
head, body, tail = s[:start], s[start:end], s[end:]
blocks = re.split(r"\n  \(\"(?=M[0-9])", body)
assert blocks[0].strip() == "MUTATIONS = ["
entries = []
for blk in blocks[1:]:
    mid, rest = blk.split('", "', 1)
    desc = rest.split('",\n', 1)[0]
    literals = re.findall(r'"""((?:[^"]|"(?!""))*?)"""', rest, re.S)
    assert len(literals) >= 2, (mid, len(literals))
    old, new = literals[0], literals[1]
    test = re.search(r'"(R56[A-Za-z0-9_.]+(?:\([^"]*\))?)"', rest).group(1)
    src = re.search(r"'(MultiplayerHoeingAssistant/[^']+)'", rest)
    entries.append({"id": mid, "desc": desc, "old": old, "new": new, "test": test,
                    "src": src.group(1) if src else None})
print("parsed entries:", len(entries))
out = ["MUTATIONS = [\n"]
for e in entries:
    out.append('  {"id": %r, "desc": %r,\n   "old": %r,\n   "new": %r,\n   "test": %r,\n   "src": %r},\n'
               % (e["id"], e["desc"], e["old"], e["new"], e["test"], e["src"]))
out.append("]\n")
s2 = head + "".join(out) + tail
s2 = s2.replace('''for entry in MUTATIONS:
    mid, desc, old, new, target_name = entry[:5]
    entry_src = entry[5] if len(entry) > 5 else SRC_TX
    case = CASE_SELECTOR.get(mid, "")''',
'''for entry in MUTATIONS:
    mid, desc, old, new, target_name = entry["id"], entry["desc"], entry["old"], entry["new"], entry["test"]
    entry_src = entry["src"] or SRC_TX
    case = CASE_SELECTOR.get(mid, "")''')
p.write_text(s2, encoding="utf-8")
import subprocess
r = subprocess.run(["python","-B","-c","import ast,pathlib; src=pathlib.Path(r'%s').read_text(encoding='utf-8'); ast.parse(src); print('syntax ok')" % str(p).replace('\\','\\\\')], capture_output=True, text=True, encoding="utf-8")
print(r.stdout or r.stderr)
