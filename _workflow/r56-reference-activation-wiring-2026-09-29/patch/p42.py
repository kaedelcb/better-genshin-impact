import pathlib, ast
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
region_old = """                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
                HashSet<string>? ownershipVerified = null;   // null \u21d2 未接入真实副作用端口的旧路径：按登记删除（既有合同）
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownershipVerified.Add(PathKey(added.Path));
                }
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)"""
region_new = """                var ownerBound = m.RealEffectsRequired || _effects is not null;
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, null) > 0)   // MUTANT: 归属保护整体被移除"""
i=s.index('"id": \'M30-no-addition-ownership-enforcement\'')
start=s.rindex('  {', 0, i); end=s.index('},\n', i)+3
entry = ('  {"id": %r, "desc": %r,\n   "old": %r,\n   "new": %r,\n   "test": %r,\n   "src": %r},\n' % (
  "M30-no-addition-ownership-enforcement", "归属预检与删除时的归属过滤整体被去除（他方「新增」文件会被删除）",
  region_old, region_new,
  "R56ReferenceActivationWiringTests_Part3.ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks",
  "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"))
s = s[:start] + entry + s[end:]
p.write_text(s,encoding="utf-8")
ast.parse(s)
tree=ast.parse(s)
for node in tree.body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        print("entries:",len(ast.literal_eval(node.value)))
