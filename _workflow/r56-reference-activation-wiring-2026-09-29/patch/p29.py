import pathlib, re
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
lines = p.read_text(encoding="utf-8").splitlines(keepends=True)
SRC_TX='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
SRC_SVC='MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs'

def entry_text(mid, desc, old, new, test, src=None):
    src_part = "" if src is None else "\n   , " + repr(src) + ") ,"
    body = '  ("%s", "%s",\n   %s,\n   %s,\n   "%s")\n' % (mid, desc, old, new, test)
    if src is not None:
        body = body[:-2] + ",\n   " + repr(src) + ")\n"
    return body

NEW = {}
NEW["M1-no-readback-confirmation"] = entry_text(
 "M1-no-readback-confirmation", "不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）",
 '"""                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))\n                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);"""',
 '"""                    // MUTANT: 读回确认被跳过"""',
 "R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback")

NEW["M9-fail-open-on-non-success"] = entry_text(
 "M9-fail-open-on-non-success", "非成功结果（拒绝/未知/取消）被当作成功继续（fail-open）",
 '"""            if (result.Outcome != MigrationEffectOutcome.Succeeded)\n            {\n                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)\n                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);\n                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"\n                    + result.Reason + ";writes=" + result.CompletedWrites);\n            }"""',
 '"""            // MUTANT: 失败/未知/取消一律继续"""',
 "R56ReferenceActivationWiringTests.ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite")

NEW["M12-stage-persisted-before-effect"] = entry_text(
 "M12-stage-persisted-before-effect", "副作用之前就持久化 ReferenceUpdating 阶段",
 '"""            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);"""',
 '"""            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);\n            m.Stage = MigrationStage.ReferenceUpdating;   // MUTANT: 阶段先于副作用持久化\n            WriteManifest(m);\n            return MigrationResult.Ok(m.Stage);"""',
 "R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes")

NEW["M13-no-activation-readback"] = entry_text(
 "M13-no-activation-readback", "激活后不读回状态就推进阶段",
 '"""                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)\n                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))\n                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);"""',
 '"""                    // MUTANT: 激活状态读回被跳过"""',
 "R56ReferenceActivationWiringTests.ActivationReadbackMismatch_Blocks")

ADD = [
 ("M18-no-activation-undo", "回滚不再撤销真实激活（只还原字节）",
  '"""                if (m.ActivationRecord is { } activation)\n                {\n                    var undone = UndoActivation(m, activation);\n                    if (!undone.Success) return undone;\n                }"""',
  '"""                // MUTANT: 撤销激活被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.Rollback_InvokesRealActivationUndo", None),
 ("M19-no-activation-pre-drift-check", "激活前不再核对已确认写集的字节版本",
  '"""            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))\n                return MarkBlocked("activation_precondition_drifted:" + request.Path);"""',
  '"""            // MUTANT: 漂移核对被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.Activation_AfterDrift_IsNotAbsorbed", None),
 ("M20-no-new-file-detection", "不再检测写集外**新增**文件",
  '"""            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);"""',
  '"""            // MUTANT: 写集外新增文件检测被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.NewFileOutsideWriteset_Blocks", None),
 ("M21-no-full-baseline-verification-on-rollback", "回滚后只核对成功写集，不核对完整基线字节",
  '"""                foreach (var baseline in m.FileHashes)\n                {\n                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))\n                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);\n                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))\n                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);\n                }"""',
  '"""                // MUTANT: 完整基线字节核对被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet", None),
 ("M22-no-addition-ownership-evidence", "回滚不再核对「新增」文件的归属证据（按登记直接删除）",
  '"""                if (m.RealEffectsRequired && OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);"""',
  '"""                // MUTANT: 归属预检被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.ForeignAddedFile_IsPreservedAndRollbackBlocks", None),
 ("M23-commit-gate-not-bound-to-instance", "提交门槛不再绑定「本实例接入真实副作用」",
  '"""            if (m.RealEffectsRequired || _effects is not null)\n            {"""',
  '"""            if (m.RealEffectsRequired)\n            {   // MUTANT: 门槛只依赖可被改写并重算摘要的持久化标记"""',
  "R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected", None),
 ("M24-no-change-registry-freeze", "确认引用更新后仍允许改变更登记",
  '"""            if (m.ReferenceWriteSet.Count > 0)\n                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);"""',
  '"""            // MUTANT: 登记冻结被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate", None),
 ("M25-no-port-exception-containment", "副作用端口抛异常不再收敛为 Blocked（异常外泄、可重复执行）",
  '"""            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）\n            {\n                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);\n            }"""',
  '"""            catch (Exception) { throw; }   // MUTANT: 异常不再收敛为 Blocked"""',
  "R56ReferenceActivationWiringTests_Part2.EffectPortExceptions_BecomeBlockedWithoutRepeat", None),
 ("M26-no-activation-status-precheck", "激活前不再观测盘上现值与声明的 before 状态是否一致",
  '"""            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))\n                return MarkBlocked("activation_already_applied:" + request.Path);\n            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))\n                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);"""',
  '"""            // MUTANT: 前置状态观测核对被跳过"""',
  "R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions", None),
 ("M27-no-d13-transition-restriction", "激活不再限定权威的 candidate-ready→active 转换",
  '"""        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)\n            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))\n            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;"""',
  '"""        // MUTANT: 转换白名单被删除"""',
  "R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions", None),
 ("M28-added-target-overwrite-allowed", "新增目标允许覆盖同名既有文件（不设 overwrite:false）",
  '"""                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);"""',
  '"""                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: true);   // MUTANT"""',
  "R56ReferenceActivationWiringTests_Part2.AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback", SRC_SVC),
]

out, i, replaced, inserted = [], 0, set(), False
while i < len(lines):
    line = lines[i]
    m = re.match(r'\s*\("(M[0-9]+-[a-z0-9-]+)"', line)
    if m and m.group(1) in NEW:
        mid = m.group(1)
        while ")," not in lines[i] and not lines[i].rstrip().endswith(")"):
            i += 1
        if ")," in lines[i]:
            i += 1
        out.append(NEW[mid]); replaced.add(mid); continue
    if not inserted and '("M8-no-reference-gate-for-activation"' in line:
        for mid, desc, old, new, test, src in ADD:
            out.append(entry_text(mid, desc, old, new, test, src))
        inserted = True
    out.append(line); i += 1

assert replaced == set(NEW), replaced
assert inserted
p.write_text("".join(out), encoding="utf-8")
print("runner rewritten; replaced:", sorted(replaced), "| added:", len(ADD))
