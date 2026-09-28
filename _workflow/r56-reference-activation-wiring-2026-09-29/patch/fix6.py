import pathlib
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
old='''                var ownedAdditions = new HashSet<string>(StringComparer.Ordinal);
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownedAdditions.Add(PathKey(added.Path));
                }'''
new='''                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownershipVerified.Add(PathKey(added.Path));
                }'''
assert s.count(old)==1
s=s.replace(old,new,1)
s=s.replace('''                if (DeleteRecordedAdditions(m, _configRoot, ownedAdditions) > 0)''',
            '''                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)''')
p.write_text(s,encoding="utf-8")
print("legacy/owner-bound split fixed")
