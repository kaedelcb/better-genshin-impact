import json, pathlib, hashlib, subprocess
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
mp=W/"manifest.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
def sha(rel): return hashlib.sha256((root/rel).read_bytes()).hexdigest()
def sha_at(rev, rel):
    return hashlib.sha256(subprocess.run(["git","-C",str(root),"show",f"{rev}:{rel}"],capture_output=True).stdout).hexdigest()
OPEN="22ccd6ee2721abb1642535253997c57dbf9020d6"
TRANS="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
OLD_TEST="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs"
src_hashes={s:sha(s) for s in m["sources"]}; open_hashes={TRANS:sha_at(OPEN,TRANS), OLD_TEST:sha_at(OPEN,OLD_TEST)}
records=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
src_of_mut={}
for rec in records: src_of_mut[rec["id"]]=(rec["source"], rec["original_sha256"])
def ev(eid, rel, purpose, level, conditions, binding, hashes):
    return {"id":eid,"path":rel,"purpose":purpose,"level":level,"conditions":conditions,"binding":binding,"source_sha256":hashes}
evi=[
 ev("baseline-targeted","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx",
    "开工同条件定向基线（R56/R58 迁移夹具 70/70）","test","独立 baseline worktree @ 22ccd6ee2；-p:DeployToBgiTools=false","historical",open_hashes),
 ev("baseline-assistant-full","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
    "开工同条件助手全量基线（1570/2/0/1572）","test","独立 baseline worktree @ 22ccd6ee2","historical",open_hashes),
 ev("targeted-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx",
    "本批定向回归 111/111（含 41 条新夹具，含第 1 轮会诊修复的 12 条）","test",
    "主工作区最终源码（第 1 轮会诊修复后）；-p:DeployToBgiTools=false","current",src_hashes),
 ev("assistant-full-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx",
    "本批助手全量回归 1611/2/0/1613","test","主工作区最终源码；全量","current",src_hashes),
 ev("subagent-readonly-audit","_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md",
    "固定开工 ref 的只读子 Agent 独立核查","consult","只读、零写入；固定 ref 22ccd6ee2","historical",open_hashes),
 ev("field-audit","_workflow/r56-reference-activation-wiring-2026-09-29/findings.md",
    "现场审计 + 会诊发现处置 + 边界（F-1..F-12）","document","开工只读审计与会诊后修订","historical",open_hashes),
 ev("claims-regeneration","_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt",
    "声明面再生差异","component","CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过","historical",open_hashes),
 ev("mutations-summary","_workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md",
    "28 项反向突变的逐项补丁与三段判定（供审查者核对判别力）","component",
    "主工作区；每项含 build/baseline/mutant/restored 独立日志与 TRX","historical",{TRANS:src_of_mut["M1-no-readback-confirmation"][1]}),
 ev("testid-comparison","_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json",
    "逐 testId 差集 added=41/removed=0/changed=0/unchanged=1572","component","主工作区最终源码；TRX 解析","current",src_hashes),
 ev("deploy-target-post","_workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json",
    "部署目标事后清单（1158 文件；最新写入早于本批）","component","Get-FileHash 清单；全部构建带 -p:DeployToBgiTools=false","historical",open_hashes),
 ev("review-round1-report","_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md",
    "第 1 轮会诊报告（5 MUST + 4 IMPORTANT + 1 建议级）原文","consult",
    "gpt-6-astra / medium；只读；attempts=1（子批计数 1/8）","historical",open_hashes),
]
for rec in records:
    src, orig = rec["source"], rec["original_sha256"]
    evi.append(ev("mut-"+rec["id"], f"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/{rec['id']}/record.json",
        "突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复","component",
        "三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希","historical",{src: orig}))
m["evidence"]=evi
m["tests"]=[{"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx","expect_success":True},
            {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx","expect_success":True}]
m["comparison"]={"baseline":"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
                 "final":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx"}
m["mutations"]=records
rc=m["review_control"]
rc["review_round"]=2
rc["repair_batch_id"]="r56-refactivation-r1-repair-2026-09-29"
from collections import OrderedDict
sev={"M1-no-readback-confirmation":"must","M2-no-cross-check-with-change-registry":"important",
     "M3-no-outside-write-detection":"important","M4-no-activation-target-membership":"important",
     "M5-no-commit-recheck":"must","M6-no-stage-mark-gate":"suggestion"}
prior=[]
def finding(fid, severity, disposition, repair_ids=None, reason=None):
    d={"id":fid,"severity":severity,"source_review_evidence_id":"review-round1-report","disposition":disposition}
    if repair_ids: d["repair_evidence_ids"]=repair_ids
    if reason: d["reason"]=reason
    prior.append(d)
finding("R1-MUST-1","must","candidate_fixed",["mut-M19-no-activation-pre-drift-check"])
finding("R1-MUST-2","must","candidate_fixed",["mut-M22-no-addition-ownership-evidence","mut-M28-added-target-overwrite-allowed"])
finding("R1-MUST-3","must","candidate_fixed",["mut-M20-no-new-file-detection"])
finding("R1-MUST-4","must","candidate_fixed",["mut-M23-commit-gate-not-bound-to-instance","mut-M14-no-real-evidence-invariants"])
finding("R1-MUST-5","must","candidate_fixed",["mut-M21-no-full-baseline-verification-on-rollback"])
finding("R1-IMPORTANT-6","important","candidate_fixed",["mut-M24-no-change-registry-freeze"])
finding("R1-IMPORTANT-7","important","candidate_fixed",["mut-M26-no-activation-status-precheck","mut-M27-no-d13-transition-restriction"])
finding("R1-IMPORTANT-8","important","candidate_fixed",["mut-M25-no-port-exception-containment"])
finding("R1-IMPORTANT-9","important","candidate_fixed",["mut-M18-no-activation-undo","mut-M15-no-reference-idempotent-recheck","mut-M16-no-activation-idempotent-recheck","mut-M20-no-new-file-detection"])
finding("R1-SUGGESTION-1","suggestion","candidate_fixed",None,
        "已修订 MigrationRehearsal 的报告文案与 R5.3 §24.129 表述：该入口使用**无真实副作用端口**的事务框架，"
        "其「激活」步骤是阶段标记演练，不代表真实 candidate→active 写入。")
rc["prior_findings"]=prior
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("manifest refreshed: evidence",len(m["evidence"]),"mutations",len(m["mutations"]),"prior_findings",len(prior))
