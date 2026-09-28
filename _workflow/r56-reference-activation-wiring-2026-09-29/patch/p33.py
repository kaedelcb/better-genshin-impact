import pathlib, ast
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
i = s.index('"id": \'M28-added-target-overwrite-allowed\'')
j = s.index('"src"', i)
block = s[i:j]
old_new = '''"id": 'M28-added-target-overwrite-allowed', "desc": '新增目标允许覆盖同名既有文件（覆盖他方文件＝静默数据破坏）',
   "old": '                    if (File.Exists(full))\\n                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);\\n                    try\\n                    {\\n                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);\\n                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）\\n                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);',
   "new": '                    try\\n                    {\\n                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);\\n                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: true);   // MUTANT：覆盖他方文件',
   '''
s = s[:i] + old_new + s[j:]
p.write_text(s, encoding="utf-8")
ast.parse(s); print("M28 redefined as combined weakening (pre-check removed + overwrite allowed)")
