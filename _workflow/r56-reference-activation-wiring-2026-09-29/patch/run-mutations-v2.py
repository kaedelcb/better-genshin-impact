import hashlib, json, pathlib, subprocess, xml.etree.ElementTree as ET

ROOT = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
WT = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
SRC_SVC = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
OUT = ROOT / "_workflow" / "r56-reference-activation-wiring-2026-09-29" / "mutations"

MUTATIONS = [
  {"id": 'M1-no-readback-confirmation', "desc": '不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）',
   "old": '                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))\n                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);',
   "new": '                    // MUTANT: 读回确认被跳过',
   "test": 'R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback',
   "src": None},
  {"id": 'M2-no-cross-check-with-change-registry', "desc": '去掉写集与变更登记的交叉核对（漏项不再拒绝）',
   "old": '            if (!seen.Contains(PathKey(record.Path)))\n                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项',
   "new": '            // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.WritesetMismatch_RegisteredNotCovered_Blocks',
   "src": None},
  {"id": 'M3-no-outside-write-detection', "desc": '去掉「写集外不得改动」检测',
   "old": '                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))\n                {\n                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);\n                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);\n                }\n                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))\n                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);',
   "new": '                // MUTANT: 写集外改动检测（改写支 + 删除支）整体被跳过',
   "test": 'R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks',
   "src": None},
  {"id": 'M4-no-activation-target-membership', "desc": '去掉「激活目标必须在已确认写集内」校验',
   "old": '        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))\n            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4',
   "new": '        // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.ActivationTargetOutsideWriteset_Blocks',
   "src": None},
  {"id": 'M5-no-commit-recheck', "desc": '去掉提交前的写集/激活读回复核',
   "old": '                var rechecked = RecheckReferenceWriteSet(m);\n                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);\n                var activationRecheck = RecheckActivationRecord(m);\n                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);',
   "new": '                // MUTANT: 提交前的写集/激活读回复核整体被跳过',
   "test": 'R56ReferenceActivationWiringTests.Commit_RefusesWhenWrittenFileDriftsAfterActivation',
   "src": None},
  {"id": 'M6-no-stage-mark-gate', "desc": '去掉「注入真实端口后阶段标记入口必须拒绝」的门禁',
   "old": '        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)\n                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);',
   "new": '        => Advance(MigrationStage.ReferenceUpdating);   // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired',
   "src": None},
  {"id": 'M7-no-activation-writeset-hash-sync', "desc": '激活后不同步更新写集哈希（写集与盘上状态脱钩）',
   "old": '            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())\n                m.ReferenceWriteSet[key] = hash;',
   "new": '            // MUTANT',
   "test": 'R56ReferenceActivationWiringTests.CommittedRealTransaction_AuthorizesProductionExactlyOnce',
   "src": None},
  {"id": 'M9-fail-open-on-non-success', "desc": '非成功结果（拒绝/未知/取消）被当作成功继续（fail-open）',
   "old": '            if (result.Outcome != MigrationEffectOutcome.Succeeded)\n            {\n                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)\n                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);\n                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"\n                    + result.Reason + ";writes=" + result.CompletedWrites);\n            }',
   "new": '            // MUTANT: 失败/未知/取消一律继续',
   "test": 'R56ReferenceActivationWiringTests.ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite',
   "src": None},
  {"id": 'M10-no-production-commit-gate', "desc": '未提交也可生产执行（生产闸门被移除）',
   "old": '            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);',
   "new": '            // MUTANT: 未提交也可生产执行',
   "test": 'R56ReferenceActivationWiringTests.ActivatedButUncommitted_ProductionStillRunsNothing',
   "src": None},
  {"id": 'M11-no-lock-guard-on-real-effects', "desc": '真实副作用入口不再要求持有独占锁',
   "old": '            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);\n            var m = LoadValidated();\n            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);\n\n            if (m.Stage == MigrationStage.ReferenceUpdating)\n                return RecheckReferenceWriteSet(m);',
   "new": '            var m = LoadValidated();\n            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);\n\n            if (m.Stage == MigrationStage.ReferenceUpdating)\n                return RecheckReferenceWriteSet(m);',
   "test": 'R56ReferenceActivationWiringTests.WithoutExclusiveLock_RealSideEffectsAreRejected',
   "src": None},
  {"id": 'M12-stage-persisted-before-effect', "desc": '副作用之前就持久化 ReferenceUpdating 阶段',
   "old": '            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);',
   "new": '            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);\n            m.Stage = MigrationStage.ReferenceUpdating;   // MUTANT: 阶段先于副作用持久化\n            WriteManifest(m);\n            return MigrationResult.Ok(m.Stage);',
   "test": 'R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes',
   "src": None},
  {"id": 'M13-no-activation-readback', "desc": '激活后不读回状态就推进阶段',
   "old": '                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)\n                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))\n                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);',
   "new": '                    // MUTANT: 激活状态读回被跳过',
   "test": 'R56ReferenceActivationWiringTests.ActivationReadbackMismatch_Blocks',
   "src": None},
  {"id": 'M14-no-real-evidence-invariants', "desc": '结构校验不再要求真实写集/激活证据',
   "old": '        if (m.RealEffectsRequired)\n        {\n            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed\n                && m.ReferenceWriteSet.Count == 0) return false;\n            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;\n        }',
   "new": '        // MUTANT: 真实证据不变量被删除',
   "test": 'R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected',
   "src": None},
  {"id": 'M15-no-reference-idempotent-recheck', "desc": '重复调用不再幂等复核，而是重新触发副作用',
   "old": '            if (m.Stage == MigrationStage.ReferenceUpdating)\n                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入',
   "new": '            // MUTANT: 幂等复核被跳过',
   "test": 'R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting',
   "src": None},
  {"id": 'M16-no-activation-idempotent-recheck', "desc": '已激活后的重复调用不再幂等复核',
   "old": '            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**',
   "new": '            // MUTANT: 激活幂等复核被跳过',
   "test": 'R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting',
   "src": None},
  {"id": 'M17-writeset-evidence-not-persisted', "desc": '真实写入后不持久化写集证据',
   "old": '            m.ReferenceWriteSet = writeSet;\n            m.Stage = MigrationStage.ReferenceUpdating;',
   "new": '            // MUTANT: 写集证据不被持久化\n            m.Stage = MigrationStage.ReferenceUpdating;',
   "test": 'R56ReferenceActivationWiringTests.ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback',
   "src": None},
  {"id": 'M18-no-activation-undo', "desc": '回滚不再撤销真实激活（只还原字节）',
   "old": '                if (m.ActivationRecord is { } activation)\n                {\n                    var undone = UndoActivation(m, activation);\n                    if (!undone.Success) return undone;\n                }',
   "new": '                // MUTANT: 撤销激活被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.Rollback_InvokesRealActivationUndo',
   "src": None},
  {"id": 'M19-no-activation-pre-drift-check', "desc": '激活前不再核对已确认写集的字节版本',
   "old": '            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))\n                return MarkBlocked("activation_precondition_drifted:" + request.Path);',
   "new": '            // MUTANT: 漂移核对被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.Activation_AfterDrift_IsNotAbsorbed',
   "src": None},
  {"id": 'M20-no-new-file-detection', "desc": '不再检测写集外**新增**文件',
   "old": '            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);',
   "new": '            // MUTANT: 写集外新增文件检测被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.NewFileOutsideWriteset_Blocks',
   "src": None},
  {"id": 'M21-no-full-baseline-verification-on-rollback', "desc": '回滚后只核对成功写集，不核对完整基线字节',
   "old": '                foreach (var baseline in m.FileHashes)\n                {\n                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))\n                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);\n                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))\n                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);\n                }',
   "new": '                // MUTANT: 完整基线字节核对被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet',
   "src": None},
  {"id": 'M23-commit-gate-not-bound-to-instance', "desc": '提交门槛不再绑定「本实例接入真实副作用」',
   "old": '            if ((m.RealEffectsRequired || _effects is not null)\n                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))\n                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);',
   "new": '            // MUTANT: 授权不再要求真实证据（只依赖可改写并重算摘要的持久化标记）',
   "test": 'R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected',
   "src": None},
  {"id": 'M24-no-change-registry-freeze', "desc": '确认引用更新后仍允许改变更登记',
   "old": '            if (m.ReferenceWriteSet.Count > 0)\n                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);',
   "new": '            // MUTANT: 登记冻结被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate',
   "src": None},
  {"id": 'M25-no-port-exception-containment', "desc": '副作用端口抛异常不再收敛为 Blocked（异常外泄、可重复执行）',
   "old": '            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）\n            {\n                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);\n            }',
   "new": '            catch (Exception) { throw; }   // MUTANT: 异常不再收敛为 Blocked',
   "test": 'R56ReferenceActivationWiringTests_Part2.EffectPortExceptions_BecomeBlockedWithoutRepeat',
   "src": None},
  {"id": 'M26-no-activation-status-precheck', "desc": '激活前不再观测盘上现值与声明的 before 状态是否一致',
   "old": '            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))\n                return MarkBlocked("activation_already_applied:" + request.Path);\n            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))\n                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);',
   "new": '            // MUTANT: 前置状态观测核对被跳过',
   "test": 'R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions',
   "src": None},
  {"id": 'M27-no-d13-transition-restriction', "desc": '激活不再限定权威的 candidate-ready→active 转换',
   "old": '        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)\n            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))\n            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;',
   "new": '        // MUTANT: 转换白名单被删除',
   "test": 'R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions',
   "src": None},
  {"id": 'M28-added-target-overwrite-allowed',
   "desc": '新增目标允许覆盖同名既有文件（覆盖他方文件＝静默数据破坏）',
   "old": '                    if (File.Exists(full))\n                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);\n                    try\n                    {\n                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);\n                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）\n                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);',
   "new": '                    try\n                    {\n                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);\n                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: true);   // MUTANT',
   "test": 'R56ReferenceActivationWiringTests_Part2.AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs'},
  {"id": 'M8-no-activation-stage-gate',
   "desc": '去掉「激活须先有已确认真实引用更新」的阶段前置（与 M6 的旧阶段标记门禁不同点）',
   "old": '            if (m.Stage != MigrationStage.ReferenceUpdating)\n                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);',
   "new": '            // MUTANT: 激活的阶段前置被删除',
   "test": 'R56ReferenceActivationWiringTests.Activation_RealWrite_RequiresConfirmedReferenceUpdate',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},

  {"id": 'M29-no-ownership-precheck-before-write', "desc": '回滚前不再预检归属（撤销激活先写入，可能改写他方文件）',
   "old": '                if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);',
   "new": '                // MUTANT: 归属预检被跳过（先写后查）',
   "test": 'R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
  {"id": 'M30-no-addition-ownership-enforcement', "desc": '归属预检与删除时的归属过滤整体被去除（他方「新增」文件会被删除）',
   "old": '                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）\n                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）\n                if (ownerBound)\n                {\n                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);\n                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);\n                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))\n                        ownershipVerified.Add(PathKey(added.Path));\n                }\n                if (m.ActivationRecord is { } activation)\n                {\n                    var undone = UndoActivation(m, activation);\n                    if (!undone.Success) return undone;\n                }\n                RestoreFromSnapshot(m, _configRoot);\n                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)',
   "new": '                var ownerBound = m.RealEffectsRequired || _effects is not null;\n                if (m.ActivationRecord is { } activation)\n                {\n                    var undone = UndoActivation(m, activation);\n                    if (!undone.Success) return undone;\n                }\n                RestoreFromSnapshot(m, _configRoot);\n                if (DeleteRecordedAdditions(m, _configRoot, null) > 0)   // MUTANT: 归属保护整体被移除',
   "test": 'R56ReferenceActivationWiringTests_Part3.ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
  {"id": 'M31-no-activation-content-binding', "desc": '激活不再绑定写入所依据的字节版本（两处哈希核对整体删除）',
   "old": '        if (!string.IsNullOrEmpty(request.ExpectedContentHash)\n            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))\n            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）\n        var hasBom = HasUtf8Bom(bytes);\n        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,\n                out var reasonText, out var text, out var alreadyTarget))\n            return MigrationEffectResult.Rejected(reasonText, 0);\n        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入\n        try\n        {\n            var preWrite = File.ReadAllBytes(full);\n            if (!preWrite.AsSpan().SequenceEqual(bytes))\n                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);\n            if (!string.IsNullOrEmpty(request.ExpectedContentHash)\n                && !string.Equals(Sha256Hex(preWrite), request.ExpectedContentHash, StringComparison.Ordinal))\n                return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);\n            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);',
   "new": '        var hasBom = HasUtf8Bom(bytes);\n        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,\n                out var reasonText, out var text, out var alreadyTarget))\n            return MigrationEffectResult.Rejected(reasonText, 0);\n        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入\n        try\n        {\n            var preWrite = File.ReadAllBytes(full);\n            if (!preWrite.AsSpan().SequenceEqual(bytes))\n                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);\n            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);   // MUTANT: 版本绑定（两处）整体被删除',
   "test": 'R56ReferenceActivationWiringTests_Part3.Activation_VersionBinding_RejectsDriftAfterPrecheck',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs'},
  {"id": 'M32-no-missing-baseline-half', "desc": '文件集合核对只查多余、不查缺失',
   "old": '        foreach (var baseline in m.FileHashes.Keys)\n            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半',
   "new": '        // MUTANT: 缺失一半被删除',
   "test": 'R56ReferenceActivationWiringTests_Part3.Commit_RejectsMissingBaselineFile',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
  {"id": 'M33-no-reentrancy-guard-on-rollback', "desc": '回滚入口不再拒绝端口回调内的同实例重入',
   "old": 'public MigrationResult Rollback()\n    {\n        lock (_sync)\n        {\n            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);\n            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);',
   "new": 'public MigrationResult Rollback()\n    {\n        lock (_sync)\n        {\n            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);   // MUTANT: 重入守卫被删除',
   "test": 'R56ReferenceActivationWiringTests_Part3.ReentrantMutationFromEffectCallback_IsRejected',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
  {"id": 'M34-no-duplicate-change-identity-check', "desc": '变更登记身份唯一性不再校验',
   "old": '        if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;',
   "new": '        changeIdentities.Add(PathKey(change.Path));   // MUTANT: 重复身份不再拒绝',
   "test": 'R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected',
   "src": 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'},
]

def sh_b(path): return hashlib.sha256(path.read_bytes()).hexdigest()

def run(args):
    p = subprocess.run(args, cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.returncode, (p.stdout or "") + (p.stderr or "")

def build(logpath, tag):
    rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                   "-p:DeployToBgiTools=false","-v","q","--nologo"])
    logpath.write_text("$ dotnet build ... (<=> %s)\n%s\n[exit=%d]\n" % (tag, out, rc), encoding="utf-8")
    return rc

def test_run(mdir, logname, trxname, testfilter):
    rc, out = run(["dotnet","test","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                   "-p:DeployToBgiTools=false","--no-build","--filter","FullyQualifiedName~"+testfilter,
                   "--logger","trx;LogFileName="+trxname,"--results-directory",str(mdir)])
    (mdir/logname).write_text("$ dotnet test --filter FullyQualifiedName~%s\n%s\n[exit=%d]\n" % (testfilter, out, rc), encoding="utf-8")
    return rc, mdir/trxname

def trx_row(trxpath, name, case=""):
    """Match by suffix: the --filter value is a short name, the TRX carries the fully-qualified name."""
    for r in ET.parse(str(trxpath)).getroot().iter(NS+"UnitTestResult"):
        actual = r.get("testName") or ""
        base = actual.split("(")[0]          # theory 用例名带参数：先去掉参数再比方法名
        if (base == name or base.endswith(name)) and (not case or actual.endswith(case)):
            msg = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"Message", default="")
            st = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"StackTrace", default="")
            return r.get("testId"), r.get("outcome"), msg, st, actual
    return None, None, "", "", None

CASE_SELECTOR = {"M3-no-outside-write-detection": "(deleteInstead: False)"}

records = []
for entry in MUTATIONS:
    mid = entry["id"]; desc = entry["desc"]; old = entry["old"]; new = entry["new"]; target_name = entry["test"]
    entry_src = entry["src"] or WT
    case = CASE_SELECTOR.get(mid, "")
    md = OUT / mid; md.mkdir(parents=True, exist_ok=True)
    src_path = ROOT / entry_src
    src_text = src_path.read_text(encoding="utf-8")
    assert src_text.count(old) == 1, (mid, src_text.count(old))
    original_sha = sh_b(src_path)                      # **原始字节**哈希

    build_exit = build(md/"build.log", "baseline")
    assert build_exit == 0, mid
    base_exit, base_trx = test_run(md, "baseline.log", "baseline.trx", target_name)
    tid, base_outcome, _, _, full_name = trx_row(base_trx, target_name, case)
    assert base_outcome == "Passed", (mid, base_outcome)

    try:
      src_path.write_text(src_text.replace(old, new, 1), encoding="utf-8")
      mutant_sha = sh_b(src_path)
      with (md/"mutant-build.log").open("w", encoding="utf-8") as fh:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"])
        fh.write("$ dotnet build ...\n%s\n[exit=%d]\n" % (out, rc))
      mutant_build_exit = rc
      mutant_exit, mutant_trx = test_run(md, "mutant.log", "mutant.trx", target_name)
      _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name, case)
    finally:
      src_path.write_text(src_text, encoding="utf-8")     # **无论如何先还原源码**
    assert mutant_outcome == "Failed" and mutant_exit > 0, (mid, mutant_outcome, mutant_exit)

    restored_sha = sh_b(src_path)
    restored_build_exit = build(md/"restored-build.log", "restored")
    restored_exit, restored_trx = test_run(md, "restored.log", "restored.trx", target_name)
    _, restored_outcome, _, _, _ = trx_row(restored_trx, target_name, case)
    assert restored_outcome == "Passed" and restored_exit == 0, (mid, restored_outcome, restored_exit)

    failure_marker = next((ln.strip() for ln in mutant_msg.splitlines() if ln.strip()), "")[:80]
    assertion_marker = full_name.split(".")[-1].split("(")[0]
    assert failure_marker and assertion_marker in mutant_stack, (mid, failure_marker, mutant_stack[:200])

    rec = {
        "id": mid, "source": entry_src, "description": desc,
        "original_sha256": original_sha, "mutant_sha256": mutant_sha, "restored_sha256": restored_sha,
        "baseline_trx": str((md/"baseline.trx").relative_to(ROOT)).replace("\\","/"),
        "mutant_trx": str((md/"mutant.trx").relative_to(ROOT)).replace("\\","/"),
        "restored_trx": str((md/"restored.trx").relative_to(ROOT)).replace("\\","/"),
        "target_test_id": tid, "target_name": full_name,
        "failure_contains": failure_marker, "assertion_contains": assertion_marker,
        "build_log": str((md/"build.log").relative_to(ROOT)).replace("\\","/"),
        "build_exit": 0, "baseline_exit": base_exit, "mutant_exit": mutant_exit, "restored_exit": restored_exit,
        "mutant_build_exit": mutant_build_exit, "restored_build_exit": restored_build_exit,
        "outcomes": {"baseline": base_outcome, "mutant": mutant_outcome, "restored": restored_outcome},
        "mutant_message": mutant_msg[:400], "mutant_stack_head": mutant_stack[:300],
        "restored_exact_bytes": original_sha == restored_sha,
    }
    (md/"record.json").write_text(json.dumps(rec, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    records.append(rec)
    print(mid, base_outcome, mutant_outcome, restored_outcome, original_sha[:12], mutant_sha[:12], restored_sha[:12], flush=True)

(OUT/"records.json").write_text(json.dumps(records, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
print("ALL_VALID=", all(r["outcomes"]=={"baseline":"Passed","mutant":"Failed","restored":"Passed"} and r["restored_exact_bytes"] for r in records))
