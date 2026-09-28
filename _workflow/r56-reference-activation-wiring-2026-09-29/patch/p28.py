import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
WT='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
SV='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs'
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:110])
    s = s.replace(old, new, cnt)

# M1 / M9 / M12 / M13：源文本随第 1 轮会诊修复移动，补丁目标同步更新
rep('''   """                if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);""",
   """                // MUTANT: 读回确认被跳过""",''',
'''   """                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);""",
   """                    // MUTANT: 读回确认被跳过""",''')

rep('''   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }""",
   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);   // MUTANT: 失败/未知/取消一律继续""",''',
'''   """            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }""",
   """            // MUTANT: 失败/未知/取消一律继续""",''')

rep('''   ("M12-stage-persisted-before-effect", "副作用之前就持久化 ReferenceUpdating 阶段",
   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);""",''',
'''   ("M12-stage-persisted-before-effect", "副作用之前就持久化 ReferenceUpdating 阶段",
   """            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);""",''')

rep('''   """            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);""",
   "R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes"),''',
'''   """            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);                    // MUTANT: 阶段先于副作用持久化""",
   "R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes"),''')

rep('''   """            if (!_effects.TryReadActivationStatus(_configRoot, request!.Path, out var status, out var detail)
                || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);""",''',
'''   """                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)
                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);""",''')

# 追加 M18–M28（第 1 轮会诊修复对应的判别突变）
anchor = '''  ("M8-no-reference-gate-for-activation", "去掉「激活须先有已确认真实引用更新」的阶段前置",'''
extra = '''  ("M18-no-activation-undo", "回滚不再撤销真实激活（只还原字节）",
   """                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }""",
   """                // MUTANT: 撤销激活被跳过""",
   "R56ReferenceActivationWiringTests_Part2.Rollback_InvokesRealActivationUndo"),
  ("M19-no-activation-pre-drift-check", "激活前不再核对已确认写集的字节版本",
   """            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_drifted:" + request.Path);""",
   """            // MUTANT: 漂移核对被跳过""",
   "R56ReferenceActivationWiringTests_Part2.Activation_AfterDrift_IsNotAbsorbed"),
  ("M20-no-new-file-detection", "不再检测写集外**新增**文件",
   """            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);""",
   """            // MUTANT: 写集外新增文件检测被跳过""",
   "R56ReferenceActivationWiringTests_Part2.NewFileOutsideWriteset_Blocks"),
  ("M21-no-full-baseline-verification-on-rollback", "回滚后只核对成功写集，不核对完整基线字节",
   """                foreach (var baseline in m.FileHashes)
                {
                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))
                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);
                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))
                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);
                }""",
   """                // MUTANT: 完整基线字节核对被跳过""",
   "R56ReferenceActivationWiringTests_Part2.Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet"),
  ("M22-no-addition-ownership-evidence", "回滚不再核对「新增」文件的归属证据（按登记直接删除）",
   """                if (m.RealEffectsRequired && OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);""",
   """                // MUTANT: 归属预检被跳过""",
   "R56ReferenceActivationWiringTests_Part2.ForeignAddedFile_IsPreservedAndRollbackBlocks"),
  ("M23-commit-gate-not-bound-to-instance", "提交门槛不再绑定「本实例接入真实副作用」",
   """            if (m.RealEffectsRequired || _effects is not null)
            {
                // **真实事务的提交前置（会诊 MUST-4）**：门槛绑定「本实例是否接入真实副作用」与持久化标记的**并集**，
                // 故把 `realEffectsRequired` 改成 false 不能降级绕过；引用写入与激活都必须由真实副作用 + 读回确认产生""",
   """            if (m.RealEffectsRequired)
            {
                // MUTANT: 门槛只依赖可被改写并重算摘要的持久化标记""",
   "R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected"),
  ("M24-no-change-registry-freeze", "确认引用更新后仍允许改变更登记",
   """            if (m.ReferenceWriteSet.Count > 0)
                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);""",
   """            // MUTANT: 登记冻结被跳过""",
   "R56ReferenceActivationWiringTests_Part2.ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate"),
  ("M25-no-port-exception-containment", "副作用端口抛异常不再收敛为 Blocked（异常外泄、可重复执行）",
   """            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }""",
   """            finally { }   // MUTANT: 异常不再收敛为 Blocked""",
   "R56ReferenceActivationWiringTests_Part2.EffectPortExceptions_BecomeBlockedWithoutRepeat"),
  ("M26-no-activation-status-precheck", "激活前不再观测盘上现值与声明的 before 状态是否一致",
   """            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_already_applied:" + request.Path);
            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);""",
   """            // MUTANT: 前置状态观测核对被跳过""",
   "R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions"),
  ("M27-no-d13-transition-restriction", "激活不再限定权威的 candidate-ready→active 转换",
   """        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;""",
   """        // MUTANT: 转换白名单被删除""",
   "R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions"),
  ("M28-added-target-overwrite-allowed", "新增目标允许覆盖同名既有文件（不设 overwrite:false）",
   """                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);""",
   """                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: true);   // MUTANT""",
   "R56ReferenceActivationWiringTests_Part2.AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback", SRC_SVC),
'''
assert s.count(anchor) == 1
s = s.replace(anchor, extra + anchor, 1)
s = s.replace('''import hashlib, json, pathlib, subprocess, xml.etree.ElementTree as ET''',
              '''import hashlib, json, pathlib, subprocess, xml.etree.ElementTree as ET
SRC_TX = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
SRC_SVC = "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"''')
# 5 元组 → 支持可选第 6 项（源文件）
s = s.replace('''for mid, desc, old, new, target_name in MUTATIONS:
    case = CASE_SELECTOR.get(mid, "")''',
'''for entry in MUTATIONS:
    mid, desc, old, new, target_name = entry[:5]
    entry_src = entry[5] if len(entry) > 5 else SRC_TX
    case = CASE_SELECTOR.get(mid, "")''')
s = s.replace('''    src_path = ROOT / WT
    src_text = src_path.read_text(encoding="utf-8")''',
'''    src_path = ROOT / entry_src
    src_text = src_path.read_text(encoding="utf-8")''')
s = s.replace('''        "id": mid, "source": WT, "description": desc,''',
              '''        "id": mid, "source": entry_src, "description": desc,''')
p.write_text(s, encoding="utf-8")
print("mutations updated: M1/M9/M12/M13 patches moved; M18-M28 added")
