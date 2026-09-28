import xml.etree.ElementTree as ET, json, pathlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
def rows(p): return [(r.get("testId"), r.get("testName"), r.get("outcome")) for r in ET.parse(str(p)).getroot().iter(NS+"UnitTestResult")]
by={n:t for t,n,o in rows(W/"final/targeted-final-frozen.trx") if o=="Passed"}
def tid(sfx):
    hits=[t for n,t in by.items() if n.endswith(sfx)]
    assert len(hits)==1,(sfx,hits); return hits[0]
mp=W/"risk-matrix.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
have={r["id"] for r in m["rows"]}
new=[
 ("REF-S11","state","激活只接受权威 D13 转换（candidate-ready→active），且盘上现值必须等于声明的 before 状态（已生效 ⇒ 拒绝，不得零写入伪成功）",
  "unsupported_activation_transition / activation_already_applied / activation_precondition_status_mismatch 置 Blocked，零副作用",
  ["Activation_RejectsAlreadyAppliedAndForeignTransitions"],["M26-no-activation-status-precheck","M27-no-d13-transition-restriction"]),
 ("REF-S12","state","真实证据门槛绑定「本实例是否接入真实副作用」，改 manifest 标记并重算摘要不得降级",
  "real_evidence_required_for_production 拒绝授权；结构关系不变量另行拒绝不一致证据",
  ["ManifestTamper_WithoutRealEvidence_IsRejected"],["M23-commit-gate-not-bound-to-instance","M14-no-real-evidence-invariants"]),
 ("REF-F11","fault","引用更新确认后、激活前该文件被锁外改动（引用漂移）",
  "activation_precondition_drifted 置 Blocked；激活不触发；写集不被漂移污染",
  ["Activation_AfterDrift_IsNotAbsorbed"],["M19-no-activation-pre-drift-check"]),
 ("REF-F12","fault","副作用在声明写集之外**新增**文件（更新面与提交面）",
  "unexpected_new_file_outside_writeset 置 Blocked（提交前同样拒绝）",
  ["NewFileOutsideWriteset_Blocks","CommitRejectsNewFilesOutsideWriteset"],["M20-no-new-file-detection"]),
 ("REF-F13","fault","登记为「本事务新增」的文件实为他方所有或被改动（含从未由本事务创建）",
  "回滚保留该文件并阻断（rollback_addition_not_owned / rollback_addition_without_ownership_evidence），不假报完整回滚；新增目标不得覆盖他方文件",
  ["ForeignAddedFile_IsPreservedAndRollbackBlocks","AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback"],["M22-no-addition-ownership-evidence","M28-added-target-overwrite-allowed"]),
 ("REF-F14","fault","回滚后旧态核对必须覆盖完整基线字节集（成功写集为空的部分写/阶段前失败路径）",
  "rollback_restore_bytes_differ 置 Blocked，不报告 RolledBack",
  ["Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet"],["M21-no-full-baseline-verification-on-rollback"]),
 ("REF-F15","fault","回滚必须真实撤销激活（读回→反向副作用→再读回），不得仅靠快照还原字节",
  "端口收到 active→candidate-ready 的撤销请求；状态与引用一并回退",
  ["Rollback_InvokesRealActivationUndo"],["M18-no-activation-undo"]),
 ("REF-F16","fault","副作用端口抛异常 / 敌意文档形状（标量节点）",
  "异常收敛为 Blocked（unknown）且不重复执行；形状异常结构化拒绝且原文未被改写",
  ["EffectPortExceptions_BecomeBlockedWithoutRepeat","HostileDocumentShape_IsRejectedNotThrown"],["M25-no-port-exception-containment"]),
 ("REF-F17","fault","确认引用更新后仍尝试改变更登记",
  "change_registry_frozen_after_reference_update 拒绝（精确写集对应关系不得事后变更）",
  ["ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate"],["M24-no-change-registry-freeze"]),
]
for rid,dim,scen,exp,tests,muts in new:
    assert rid not in have, rid
    m["rows"].append({"id":rid,"dimension":dim,"scenario":scen,"expected":exp,"critical":True,"status":"planned"})
m["rows"].append({"id":"REF-C4","dimension":"concurrency",
 "scenario":"同一实例上并发发起「提交」与「回滚」",
 "expected":"不变量：绝不出现「回滚报告完成却仍可生产执行」；终态只能是 Committed（新态）或 RolledBack（旧态），不出现半途混合",
 "critical":False,"status":"planned",
 "reason":"同实例串行边界由代码结构承载（所有变更入口共用同一 monitor），真实交错的单点突变不可确定性构造；尝试记录见 findings F-9：以「去掉回滚字节还原」构造的突变在该夹具上仍通过（提交可获胜 ⇒ 不变量仍满足），故本行以夹具不变量 + REF-C2/REF-C3 的已验证突变共同判别。"})
tests_map={}
for rid,_,_,_,ts,_ in new: tests_map[rid]=ts
muts_map={}
for rid,_,_,_,_,ms in new: muts_map[rid]=ms
refreshed = {
 "REF-S1":["ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback"],
 "REF-S2":["Activation_RealWrite_RequiresConfirmedReferenceUpdate","CommittedRealTransaction_AuthorizesProductionExactlyOnce"],
 "REF-S3":["ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite"],
 "REF-S4":["ReferenceUpdate_Unknown_FailsClosedWithoutRetry"],
 "REF-S5":["ReferenceUpdate_CancelledBeforeEffects_KeepsStageAndRollsBackSafely"],
 "REF-S6":["ReferenceUpdate_CancelledAfterEffects_Blocks"],
 "REF-S7":["RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting","RepeatedReferenceUpdate_DetectsOnDiskDrift"],
 "REF-S8":["RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting"],
 "REF-S9":["ActivatedButUncommitted_ProductionStillRunsNothing"],
 "REF-S10":["LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired"],
 "REF-C1":["WithoutExclusiveLock_RealSideEffectsAreRejected"],
 "REF-C2":["SecondInstanceDuringTransaction_IsBusyAndWritesNothing"],
 "REF-C3":["RollbackBeforeCommit_LeavesNoProductionPermit","ConcurrentCommitAndRollback_UpholdInvariants"],
 "REF-F1":["WritesetMismatch_WithChangeRegistry_BlocksWithoutWriting","WritesetMismatch_RegisteredNotCovered_Blocks"],
 "REF-F2":["SelfReportedSuccessWithoutRealWrite_BlocksOnReadback","WrongContentWritten_BlocksOnReadback"],
 "REF-F3":["CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes"],
 "REF-F4":["ActivationTargetOutsideWriteset_Blocks"],
 "REF-F5":["ActivationReadbackMismatch_Blocks"],
 "REF-F6":["Rollback_RevertsActivationAndBytes_KeepsForeignAdditions"],
 "REF-F7":["ManifestTamper_WithoutRealEvidence_IsRejected"],
 "REF-F8":["PartialWriteThenFault_NoFalseSuccess_AndRollbackRestoresAllTargets"],
 "REF-F9":["WriteOutsideDeclaredWriteset_Blocks(deleteInstead: False)","WriteOutsideDeclaredWriteset_Blocks(deleteInstead: True)"],
 "REF-F10":["Commit_RefusesWhenWrittenFileDriftsAfterActivation"],
}
muts_refresh = {
 "REF-S1":["M17-writeset-evidence-not-persisted"],"REF-S2":["M7-no-activation-writeset-hash-sync","M8-no-reference-gate-for-activation"],
 "REF-S3":["M9-fail-open-on-non-success"],"REF-S4":["M9-fail-open-on-non-success"],"REF-S5":["M9-fail-open-on-non-success"],
 "REF-S6":["M9-fail-open-on-non-success"],"REF-S7":["M15-no-reference-idempotent-recheck"],"REF-S8":["M16-no-activation-idempotent-recheck"],
 "REF-S9":["M10-no-production-commit-gate"],"REF-S10":["M6-no-stage-mark-gate"],"REF-C1":["M11-no-lock-guard-on-real-effects"],
 "REF-C2":["M11-no-lock-guard-on-real-effects"],"REF-C3":["M10-no-production-commit-gate"],
 "REF-F1":["M2-no-cross-check-with-change-registry"],"REF-F2":["M1-no-readback-confirmation"],
 "REF-F3":["M12-stage-persisted-before-effect"],"REF-F4":["M4-no-activation-target-membership"],
 "REF-F5":["M13-no-activation-readback"],"REF-F6":["M7-no-activation-writeset-hash-sync"],
 "REF-F7":["M14-no-real-evidence-invariants"],"REF-F8":["M9-fail-open-on-non-success"],
 "REF-F9":["M3-no-outside-write-detection"],"REF-F10":["M5-no-commit-recheck"],
}
tests_map.update(refreshed); muts_map.update(muts_refresh)
for r in m["rows"]:
    r["counterexample_ids"]=["targeted-final-frozen"]
    if r["id"] == "REF-C4":
        r["status"]="not_applicable"
        r["test_ids"]=[tid(s) for s in ["ConcurrentCommitAndRollback_UpholdInvariants"]]
        continue
    r["status"]="covered"
    r["test_ids"]=[tid(s) for s in tests_map[r["id"]]]
    r["mutation_ids"]=muts_map[r["id"]]
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("rows:",len(m["rows"]))
print(json.dumps({r["id"]:r["status"] for r in m["rows"]},ensure_ascii=False))
