import pathlib, ast
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
TX='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
SVC='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs'
# M8 重新定义为「激活阶段前置门」（与 M6 去重）
i=s.index('"id": \'M8-no-reference-gate-for-activation\'')
j=s.index('},', s.index('"src"', i))+2
s = s[:i] + '''"id": 'M8-no-activation-stage-gate',
   "desc": '去掉「激活须先有已确认真实引用更新」的阶段前置（与 M6 的旧阶段标记门禁不同点）',
   "old": '            if (m.Stage != MigrationStage.ReferenceUpdating)\\n                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);',
   "new": '            // MUTANT: 激活的阶段前置被删除',
   "test": 'R56ReferenceActivationWiringTests.Activation_RealWrite_RequiresConfirmedReferenceUpdate',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
''' + s[j:]
new_entries = [
 ("M29-no-ownership-precheck-before-write", "回滚前不再预检归属（撤销激活先写入，可能改写他方文件）",
  '                if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);',
  '                // MUTANT: 归属预检被跳过（先写后查）',
  "R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten", TX),
 ("M30-no-addition-ownership-enforcement", "归属预检与删除时的归属过滤整体被去掉（他方文件会被删除）",
  '''                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownershipVerified.Add(PathKey(added.Path));
                }''',
  '''                // MUTANT: 归属保护整体被移除（先写后查 + 按登记删除）''',
  "R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten", TX),
 ("M31-no-activation-content-binding", "激活不再绑定写入所依据的字节版本",
  '''        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))
            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）''',
  '''        // MUTANT: 版本绑定被删除''',
  "R56ReferenceActivationWiringTests_Part3.Activation_VersionBinding_RejectsDriftAfterPrecheck", SVC),
 ("M32-no-missing-baseline-half", "文件集合核对只查多余、不查缺失",
  '''        foreach (var baseline in m.FileHashes.Keys)
            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半''',
  '''        // MUTANT: 缺失一半被删除''',
  "R56ReferenceActivationWiringTests_Part3.Commit_RejectsMissingBaselineFile", TX),
 ("M33-no-reentrancy-guard-on-rollback", "回滚入口不再拒绝端口回调内的同实例重入",
  '''            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);''',
  '''            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);''',
  "R56ReferenceActivationWiringTests_Part3.ReentrantMutationFromEffectCallback_IsRejected", TX),
 ("M34-no-duplicate-change-identity-check", "变更登记身份唯一性不再校验",
  '''        if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;''',
  '''        changeIdentities.Add(PathKey(change.Path));   // MUTANT: 重复身份不再拒绝''',
  "R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected", TX),
 ("M35-rollback-ownership-not-bound-to-instance", "回滚归属判据不再绑定本实例（降级 manifest 标记即可绕过）",
  '                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）',
  '                var ownerBound = m.RealEffectsRequired;   // MUTANT: 只依赖可改写的持久化标记',
  "R56ReferenceActivationWiringTests_Part3.DowngradedManifestFlag_DoesNotBypassRollbackOwnership", TX),
]
out=[]
for mid,desc,old,new,test,src in new_entries:
    out.append('  {"id": %r, "desc": %r,\n   "old": %r,\n   "new": %r,\n   "test": %r,\n   "src": %r},\n' % (mid,desc,old,new,test,src))
k=s.index("\n]\n", s.index("M28-added-target-overwrite-allowed"))+1
s=s[:k]+"".join(out)+s[k:]
p.write_text(s,encoding="utf-8")
tree=ast.parse(s)
for node in tree.body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        ids=[e["id"] for e in ast.literal_eval(node.value)]
        print("entries:",len(ids)); print(ids)
