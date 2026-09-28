import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
anchor = '''  ("M8-no-reference-gate-for-activation", "去掉「激活须先有已确认真实引用更新」的阶段前置",'''
extra = '''  ("M9-fail-open-on-non-success", "非成功结果（拒绝/未知/取消）被当作成功继续（fail-open）",
   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }""",
   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);   // MUTANT: 失败/未知/取消一律继续""",
   "R56ReferenceActivationWiringTests.ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite"),
  ("M10-no-production-commit-gate", "未提交也可生产执行（生产闸门被移除）",
   """            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);""",
   """            // MUTANT: 未提交也可生产执行""",
   "R56ReferenceActivationWiringTests.ActivatedButUncommitted_ProductionStillRunsNothing"),
  ("M11-no-lock-guard-on-real-effects", "真实副作用入口不再要求持有独占锁",
   """            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);""",
   """            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);""",
   "R56ReferenceActivationWiringTests.WithoutExclusiveLock_RealSideEffectsAreRejected"),
  ("M12-stage-persisted-before-effect", "副作用之前就持久化 ReferenceUpdating 阶段",
   """            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);""",
   """            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);""",
   "R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes"),
  ("M13-no-activation-readback", "激活后不读回状态就推进阶段",
   """            if (!_effects.TryReadActivationStatus(_configRoot, request!.Path, out var status, out var detail)
                || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);""",
   """            // MUTANT: 激活状态读回被跳过""",
   "R56ReferenceActivationWiringTests.ActivationReadbackMismatch_Blocks"),
  ("M14-no-real-evidence-invariants", "结构校验不再要求真实写集/激活证据",
   """        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }""",
   """        // MUTANT: 真实证据不变量被删除""",
   "R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected"),
  ("M15-no-reference-idempotent-recheck", "重复调用不再幂等复核，而是重新触发副作用",
   """            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入""",
   """            // MUTANT: 幂等复核被跳过""",
   "R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting"),
  ("M16-no-activation-idempotent-recheck", "已激活后的重复调用不再幂等复核",
   """            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**""",
   """            // MUTANT: 激活幂等复核被跳过""",
   "R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting"),
  ("M17-writeset-evidence-not-persisted", "真实写入后不持久化写集证据",
   """            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;""",
   """            // MUTANT: 写集证据不被持久化
            m.Stage = MigrationStage.ReferenceUpdating;""",
   "R56ReferenceActivationWiringTests.ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback"),
'''
assert s.count(anchor) == 1
s = s.replace(anchor, extra + anchor, 1)
p.write_text(s, encoding="utf-8")
print("mutations M9-M17 added")
