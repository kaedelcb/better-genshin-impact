import xml.etree.ElementTree as ET, json, pathlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
def rows(p): return [(r.get("testId"), r.get("testName"), r.get("outcome")) for r in ET.parse(str(p)).getroot().iter(NS+"UnitTestResult")]
tgt=rows(W/"final/targeted-final-frozen.trx")
by={n:t for t,n,o in tgt if o=="Passed"}
def tid(sfx):
    hits=[t for n,t in by.items() if n.endswith(sfx)]
    assert len(hits)==1,(sfx,hits); return hits[0]

mp=W/"risk-matrix.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
existing={r["id"] for r in m["rows"]}
def add(row):
    assert row["id"] not in existing
    m["rows"].append(row)
add({"id":"REF-S10","dimension":"state",
 "scenario":"注入真实副作用端口后，旧阶段标记入口（MarkReferenceUpdateCompleted/MarkActivated）必须被拒绝",
 "expected":"real_side_effects_required，阶段不推进、零写入；旧语义仅保留给未注入端口的只读夹具","critical":True,"status":"planned"})
add({"id":"REF-F10","dimension":"fault",
 "scenario":"已真实写入/激活的文件在提交前被锁外写方改动",
 "expected":"commit_recheck_failed 拒绝提交，未提交不产生生产许可","critical":True,"status":"planned"})

tests={
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
 "REF-C3":["RollbackBeforeCommit_LeavesNoProductionPermit"],
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
muts={
 "REF-S1":["M17-writeset-evidence-not-persisted"],
 "REF-S2":["M7-no-activation-writeset-hash-sync","M8-no-reference-gate-for-activation"],
 "REF-S3":["M9-fail-open-on-non-success"],"REF-S4":["M9-fail-open-on-non-success"],
 "REF-S5":["M9-fail-open-on-non-success"],"REF-S6":["M9-fail-open-on-non-success"],
 "REF-S7":["M15-no-reference-idempotent-recheck"],"REF-S8":["M16-no-activation-idempotent-recheck"],
 "REF-S9":["M10-no-production-commit-gate"],
 "REF-S10":["M6-no-stage-mark-gate"],
 "REF-C1":["M11-no-lock-guard-on-real-effects"],"REF-C2":["M11-no-lock-guard-on-real-effects"],
 "REF-C3":["M10-no-production-commit-gate"],
 "REF-F1":["M2-no-cross-check-with-change-registry"],"REF-F2":["M1-no-readback-confirmation"],
 "REF-F3":["M12-stage-persisted-before-effect"],"REF-F4":["M4-no-activation-target-membership"],
 "REF-F5":["M13-no-activation-readback"],"REF-F6":["M7-no-activation-writeset-hash-sync"],
 "REF-F7":["M14-no-real-evidence-invariants"],"REF-F8":["M9-fail-open-on-non-success"],
 "REF-F9":["M3-no-outside-write-detection"],"REF-F10":["M5-no-commit-recheck"],
}
for r in m["rows"]:
    r["counterexample_ids"]=["targeted-final-frozen"]
    r["test_ids"]=[tid(s) for s in tests[r["id"]]]
    r["mutation_ids"]=muts[r["id"]]
    r["critical"]=True
    r["status"]="covered"
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("rows:",len(m["rows"]))
print(json.dumps({r["id"]:{"tests":len(r["test_ids"]),"mut":r["mutation_ids"]} for r in m["rows"]},ensure_ascii=False))
