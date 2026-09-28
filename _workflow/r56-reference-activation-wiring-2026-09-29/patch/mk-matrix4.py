import json, pathlib, hashlib, subprocess, xml.etree.ElementTree as ET
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
def rows(p): return [(r.get("testId"), r.get("testName"), r.get("outcome")) for r in ET.parse(str(p)).getroot().iter(NS+"UnitTestResult")]
by={n:t for t,n,o in rows(W/"final/targeted-final-frozen.trx") if o=="Passed"}
def tid(sfx):
    h=[t for n,t in by.items() if n.endswith(sfx)]; assert len(h)==1,(sfx,h); return h[0]
mp=W/"risk-matrix.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
have={r["id"] for r in m["rows"]}
newrows=[
 ("REF-S13","state","激活写入必须绑定「已确认写集」的字节版本（前置检查之后、写入之前的锁外改动）",
  "activation_content_hash_mismatch 置 Blocked；漂移内容不被写入为 active",
  ["Activation_VersionBinding_RejectsDriftAfterPrecheck"],["M31-no-activation-content-binding"]),
 ("REF-F18","fault","新增文件作为激活目标（正常回滚路径）与他方替换新增文件",
  "合法回滚按**预核归属**清理新增并恢复旧态；他方文件在任何写入之前被识别 ⇒ 保留并阻断",
  ["Rollback_AddedActivationTarget_ConvergesAndRemovesIt","Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten","ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks"],["M29-no-ownership-precheck-before-write","M30-no-addition-ownership-enforcement"]),
 ("REF-F19","fault","提交面文件集合核对须为**相等**（写集外基线文件缺失/被删）",
  "missing_baseline_file 拒绝提交",
  ["Commit_RejectsMissingBaselineFile"],["M32-no-missing-baseline-half"]),
 ("REF-F20","fault","只快照+登记 Added、尚未写入即中止的事务",
  "可安全回滚（目标不存在无需归属证据），终态 RolledBack",
  ["AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely"],["M30-no-addition-ownership-enforcement"]),
 ("REF-F21","fault","端口回调内同实例重入变更入口（monitor 可重入）",
  "reentrant_mutation_rejected；外层不得因重入发布错误阶段或污染登记/写集",
  ["ReentrantMutationFromEffectCallback_IsRejected"],["M33-no-reentrancy-guard-on-rollback"]),
 ("REF-F22","fault","manifest 变更登记出现重复身份",
  "结构关系不变量拒绝（evidence_relation:duplicate_change_identity）",
  ["ManifestTamper_WithoutRealEvidence_IsRejected"],["M34-no-duplicate-change-identity-check"]),
]
for rid,dim,scen,exp,ts,ms in newrows:
    assert rid not in have, rid
    m["rows"].append({"id":rid,"dimension":dim,"scenario":scen,"expected":exp,"critical":True,"status":"planned",
                      "test_ids":[tid(s) for s in ts] if rid!="REF-F18" else [tid(s) for s in ts],
                      "mutation_ids":ms,"counterexample_ids":["targeted-final-frozen"]})
# 刷新既有行的 test_ids（夹具数量变化）并统一清掉旧的 mutation_note
refresh={
 "REF-F13":(["ForeignAddedFile_IsPreservedAndRollbackBlocks","AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback"],["M29-no-ownership-precheck-before-write","M30-no-addition-ownership-enforcement","M28-added-target-overwrite-allowed"]),
 "REF-S10":(["LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired"],["M6-no-stage-mark-gate"]),
 "REF-S2":(["Activation_RealWrite_RequiresConfirmedReferenceUpdate","CommittedRealTransaction_AuthorizesProductionExactlyOnce"],["M7-no-activation-writeset-hash-sync","M8-no-activation-stage-gate"]),
 "REF-C3":(["RollbackBeforeCommit_LeavesNoProductionPermit","ConcurrentCommitAndRollback_UpholdInvariants"],["M10-no-production-commit-gate"]),
 "REF-S1":(["ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback"],["M17-writeset-evidence-not-persisted"]),
 "REF-F12":(["NewFileOutsideWriteset_Blocks","CommitRejectsNewFilesOutsideWriteset"],["M20-no-new-file-detection"]),
 "REF-F16":(["EffectPortExceptions_BecomeBlockedWithoutRepeat","HostileDocumentShape_IsRejectedNotThrown"],["M25-no-port-exception-containment"]),
}
for r in m["rows"]:
    r.pop("mutation_note", None)
    r.pop("reason", None) if r["id"]!="REF-C4" else None
    if r["id"]=="REF-C4":
        r["status"]="not_applicable"; r["test_ids"]=[tid("ConcurrentCommitAndRollback_UpholdInvariants")]
        r["reason"]="同实例串行边界由代码结构承载（所有变更入口共用同一 monitor + 重入守卫）；真实交错的单点突变不可确定性构造。尝试记录：mutation M29/M33 分别去掉回滚前的归属预检与重入守卫，该并发夹具均未被杀（不变量在两种获胜顺序下仍成立），故本行判别力由不变量夹具 + REF-C2/REF-C3 的已验证突变承担，并如实登记为未由突变验证。"
        continue
    if r["id"] in refresh:
        ts,ms=refresh[r["id"]]; r["test_ids"]=[tid(s) for s in ts]; r["mutation_ids"]=ms
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("rows:",len(m["rows"]))
