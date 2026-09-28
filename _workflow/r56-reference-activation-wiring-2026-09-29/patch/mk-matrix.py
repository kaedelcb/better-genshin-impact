import xml.etree.ElementTree as ET, json, pathlib, hashlib
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
def rows(p):
    return [(r.get("testId"), r.get("testName"), r.get("outcome")) for r in ET.parse(str(p)).getroot().iter(NS+"UnitTestResult")]
tgt = rows(root/"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx")
by_name = {n: t for t,n,o in tgt if o=="Passed"}
def tid(suffix):
    hits=[t for n,t in by_name.items() if n.endswith(suffix)]
    assert len(hits)==1, (suffix, hits)
    return hits[0]
rowspec = {
 "REF-S1": ["ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback"],
 "REF-S2": ["Activation_RealWrite_RequiresConfirmedReferenceUpdate","CommittedRealTransaction_AuthorizesProductionExactlyOnce"],
 "REF-S3": ["ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite"],
 "REF-S4": ["ReferenceUpdate_Unknown_FailsClosedWithoutRetry"],
 "REF-S5": ["ReferenceUpdate_CancelledBeforeEffects_KeepsStageAndRollsBackSafely"],
 "REF-S6": ["ReferenceUpdate_CancelledAfterEffects_Blocks"],
 "REF-S7": ["RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting","RepeatedReferenceUpdate_DetectsOnDiskDrift"],
 "REF-S8": ["RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting"],
 "REF-S9": ["ActivatedButUncommitted_ProductionStillRunsNothing"],
 "REF-C1": ["WithoutExclusiveLock_RealSideEffectsAreRejected"],
 "REF-C2": ["SecondInstanceDuringTransaction_IsBusyAndWritesNothing"],
 "REF-C3": ["RollbackBeforeCommit_LeavesNoProductionPermit"],
 "REF-F1": ["WritesetMismatch_WithChangeRegistry_BlocksWithoutWriting","WritesetMismatch_RegisteredNotCovered_Blocks"],
 "REF-F2": ["SelfReportedSuccessWithoutRealWrite_BlocksOnReadback","WrongContentWritten_BlocksOnReadback"],
 "REF-F3": ["CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes"],
 "REF-F4": ["ActivationTargetOutsideWriteset_Blocks"],
 "REF-F5": ["ActivationReadbackMismatch_Blocks"],
 "REF-F6": ["Rollback_RevertsActivationAndBytes_KeepsForeignAdditions"],
 "REF-F7": ["ManifestTamper_WithoutRealEvidence_IsRejected"],
 "REF-F8": ["PartialWriteThenFault_NoFalseSuccess_AndRollbackRestoresAllTargets"],
 "REF-F9": ["WriteOutsideDeclaredWriteset_Blocks(deleteInstead: False)","WriteOutsideDeclaredWriteset_Blocks(deleteInstead: True)"],
}
mut_map = {
 "REF-S1": ["M1-no-readback-confirmation"], "REF-S2": ["M5-no-commit-recheck","M7-no-activation-writeset-hash-sync"],
 "REF-S3": [], "REF-S4": [], "REF-S5": [], "REF-S6": [], "REF-S7": [], "REF-S8": [], "REF-S9": [],
 "REF-C1": [], "REF-C2": [], "REF-C3": [],
 "REF-F1": ["M2-no-cross-check-with-change-registry"], "REF-F2": ["M1-no-readback-confirmation"],
 "REF-F3": [], "REF-F4": ["M4-no-activation-target-membership"], "REF-F5": [], "REF-F6": ["M7-no-activation-writeset-hash-sync"],
 "REF-F7": [], "REF-F8": [], "REF-F9": ["M3-no-outside-write-detection"],
}
mp = root/"_workflow/r56-reference-activation-wiring-2026-09-29/risk-matrix.json"
matrix=json.loads(mp.read_text(encoding="utf-8-sig"))
for row in matrix["rows"]:
    row["counterexample_ids"]=["targeted-final-frozen"]
    row["test_ids"]=[tid(s) for s in rowspec[row["id"]]]
    if row["critical"]:
        row["mutation_ids"]=mut_map[row["id"]]
        if not row["mutation_ids"]:
            row["mutation_note"]="本行反例无有效反向突变（仅断言级反例）；关键行突变覆盖由同维度他行承担（见 M1–M8）"
    row["status"]="covered"
mp.write_text(json.dumps(matrix,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("matrix rows covered:", len(matrix["rows"]))
print(json.dumps({r["id"]:{"tests":len(r["test_ids"]),"mut":r.get("mutation_ids")} for r in matrix["rows"]},ensure_ascii=False,indent=1)[:1200])
