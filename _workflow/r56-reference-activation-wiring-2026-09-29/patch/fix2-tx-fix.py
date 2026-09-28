import pathlib
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/fix2-tx.py")
s=p.read_text(encoding="utf-8")
bad = """rep('''        if (!TryHashConfigFile(rel, out hash, out problem)) return false;''', '''        if (!TryHashConfigFile(rel, out hash, out problem)) return false;''')
"""
assert s.count(bad)==1
s=s.replace(bad, """rep('''        if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }''',
'''        try
        {
            if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
        }
        catch (Exception)     // 安全检查自身异常同样收敛为「不可哈希」（会诊第 2 轮 IMPORTANT-6）
        {
            problem = "unsafe_target_check_failed";
            return false;
        }''')
""",1)
p.write_text(s,encoding="utf-8"); print("noop removed, unsafe-target guard added")
