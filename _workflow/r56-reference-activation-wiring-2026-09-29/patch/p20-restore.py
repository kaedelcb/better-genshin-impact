import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = p.read_text(encoding="utf-8")
old = """                // MUTANT
            }
            m.ReferenceWriteSet = writeSet;"""
new = """                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
            }
            m.ReferenceWriteSet = writeSet;"""
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
h = hashlib.sha256(p.read_bytes()).hexdigest()
print("restored sha256(raw bytes) =", h)
