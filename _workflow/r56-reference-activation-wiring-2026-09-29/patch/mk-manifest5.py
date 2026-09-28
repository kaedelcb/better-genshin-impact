import json, pathlib, hashlib, subprocess
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
def sha(rel): return hashlib.sha256((root/rel).read_bytes()).hexdigest()
def sha_at(rev, rel): return hashlib.sha256(subprocess.run(["git","-C",str(root),"show",f"{rev}:{rel}"],capture_output=True).stdout).hexdigest()
def nlines(rel): return len((root/rel).read_text(encoding="utf-8-sig").splitlines())
def fl(rel, start_marker, end_marker=None):
    L=(root/rel).read_text(encoding="utf-8").splitlines()
    s=next(i+1 for i,l in enumerate(L) if l.startswith(start_marker))
    e=len(L) if end_marker is None else next(i for i,l in enumerate(L) if i+1>s and l.startswith(end_marker))
    return s,e
OPEN="22ccd6ee2721abb1642535253997c57dbf9020d6"
TRANS="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
OLD_TEST="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs"
CSM="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"
REH="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs"
r53="Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"; plan="槲寄生调度器总计划.md"; idx="Docs/design/mistletoe-parallel-deliveries.md"
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
src_hashes={s:sha(s) for s in m["sources"]}; open_hashes={TRANS:sha_at(OPEN,TRANS), OLD_TEST:sha_at(OPEN,OLD_TEST)}
a=fl(r53,"## §24.129 R5.6 A 项主线施工子批"); b=fl(plan,"## 2026-09-29：R5.6 A 项主线施工","## 2026-09-28：R5.6 主线迁移集成批")
c=fl(idx,"### R5.6 A 项进展（主线施工，2026-09-29）","## 无需 owner 提醒的执行闭环")
records=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
base=[e for e in m["evidence"] if not e["id"].startswith("mut-")]
for e in base:
    if e["id"] in {"targeted-final-frozen","assistant-full-final-frozen","testid-comparison"}:
        e["source_sha256"]=src_hashes; e["binding"]="current"
byid={e["id"]:e for e in base}
byid["targeted-final-frozen"]["purpose"]="本批定向回归 119/119（含 49 条新夹具，两轮会诊修复后）"
byid["assistant-full-final-frozen"]["purpose"]="本批助手全量回归 1619/2/0/1621"
byid["testid-comparison"]["purpose"]="逐 testId 差集 added=49/removed=0/changed=0/unchanged=1572"
base.append({"id":"review-round2-report","path":"_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round2-report.md",
  "purpose":"第 2 轮（验证轮）会诊报告原文（已闭环 4 / 未闭环 5 + 新报 MUST 4 + IMPORTANT 4）","level":"consult",
  "conditions":"gpt-6-astra / medium；只读；attempts=1（子批计数 2/8）","binding":"historical","source_sha256":open_hashes})
base.append({"id":"claims-regeneration-r3","path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r3.txt",
  "purpose":"第 3 轮声明面再生差异（+3/-0，629→632）","level":"component","conditions":"同上","binding":"historical","source_sha256":open_hashes})
mut_ev=[{"id":"mut-"+r["id"],"path":f"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/{r['id']}/record.json",
         "purpose":"突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复","level":"component",
         "conditions":"三段独立日志与 TRX，绑定源文件哈希","binding":"historical","source_sha256":{r["source"]:r["original_sha256"]}} for r in records]
m["evidence"]=base+mut_ev; m["mutations"]=records
m["tests"]=[{"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx","expect_success":True},
            {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx","expect_success":True}]
rc=m["review_control"]; rc["review_round"]=3; rc["repair_batch_id"]="r56-refactivation-r2-repair-2026-09-29"
def f(fid,sev,disp,reps=None,reason=None):
    d={"id":fid,"severity":sev,"source_review_evidence_id":"review-round2-report","disposition":disp}
    if reps: d["repair_evidence_ids"]=reps
    if reason: d["reason"]=reason
    return d
rc["prior_findings"]=[
 f("R2-MUST-1","must","candidate_fixed",["mut-M29-no-ownership-precheck-before-write","mut-M30-no-addition-ownership-enforcement"]),
 f("R2-MUST-2","must","candidate_fixed",["mut-M30-no-addition-ownership-enforcement"]),
 f("R2-MUST-3","must","candidate_fixed",["mut-M31-no-activation-content-binding"]),
 f("R2-MUST-4","must","candidate_fixed",["mut-M32-no-missing-baseline-half","mut-M34-no-duplicate-change-identity-check"]),
 f("R2-IMPORTANT-5","important","candidate_fixed",["mut-M30-no-addition-ownership-enforcement"]),
 f("R2-IMPORTANT-6","important","candidate_fixed",["mut-M25-no-port-exception-containment"]),
 f("R2-IMPORTANT-7","important","candidate_fixed",["mut-M33-no-reentrancy-guard-on-rollback"]),
 f("R2-IMPORTANT-8","important","candidate_fixed",["mut-M30-no-addition-ownership-enforcement","mut-M31-no-activation-content-binding"]),
 f("R2-IMPORTANT-9-CLOSE","important","candidate_fixed",["mut-M8-no-activation-stage-gate"]),
 f("R2-RESIDUAL-1","suggestion","accepted",None,
   "残项（如实）：`REF-C4` 同实例并发与「降级 manifest 标记不得绕过归属保护」两条仅有夹具级反例；"
   "尝试以 M29/M33 与「判据只依赖标记」的探测突变击杀均未成功（记录在案），故不主张突变判别力。按 §17.4-A 第 ① 条，"
   "该情形属「证据等级如实登记」而非措辞类豁免；已写入 R5.3 §24.129.8 与矩阵行 reason。"),
]
mp=W/"manifest.json"; mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("evidence",len(m["evidence"]),"mutations",len(m["mutations"]),"prior",len(rc["prior_findings"]),"r53",a,"plan",b,"idx",c)
