import hashlib, pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs")
src = p.read_text(encoding="utf-8")
src = src.replace("AtomicWrite(full, EncodeText(text, hasBom), hasBom);", "AtomicWrite(full, EncodeText(text!, hasBom), hasBom);")
p.write_text(src, encoding="utf-8")

# add the "no write outside the declared write set" check to ApplyReferenceUpdate
t = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = t.read_text(encoding="utf-8")
old = '''            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;'''
new = '''            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）
            foreach (var baseline in m.FileHashes)
            {
                if (writeSet.Keys.Any(k => PathKey(k) == PathKey(baseline.Key))) continue;
                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
            }
            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
t.write_text(s, encoding="utf-8")
print("stage5 ok", hashlib.sha256(s.encode()).hexdigest())
