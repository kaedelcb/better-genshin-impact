import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
old = '''   """                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);""",
   """                // MUTANT""",'''
new = '''   """                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);""",
   """                // MUTANT: 写集外改动检测（改写支 + 删除支）整体被跳过""",'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("M3 widened to whole-block weakening")
