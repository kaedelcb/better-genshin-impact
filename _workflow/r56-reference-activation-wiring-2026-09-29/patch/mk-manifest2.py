import json, pathlib, hashlib, subprocess
root = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W = root/"_workflow/r56-reference-activation-wiring-2026-09-29"
mp = W/"manifest.json"; m = json.loads(mp.read_text(encoding="utf-8-sig"))
def sha(rel): return hashlib.sha256((root/rel).read_bytes()).hexdigest()
def sha_at(rev, rel):
    b = subprocess.run(["git","-C",str(root),"show",f"{rev}:{rel}"],capture_output=True).stdout
    return hashlib.sha256(b).hexdigest()
OPEN="22ccd6ee2721abb1642535253997c57dbf9020d6"
TRANS="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
REFSVC="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"
TEST="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs"
OLD_TEST="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs"
src_hashes = {s: sha(s) for s in m["sources"]}
open_hashes = {TRANS: sha_at(OPEN,TRANS), OLD_TEST: sha_at(OPEN,OLD_TEST)}
mut_orig = json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))[0]["original_sha256"]

def ev(eid, rel, purpose, level, conditions, binding, hashes):
    return {"id":eid,"path":rel,"purpose":purpose,"level":level,"conditions":conditions,
            "binding":binding,"source_sha256":hashes}

evi = [
 ev("baseline-targeted","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx",
    "开工同条件定向基线（R56/R58 迁移夹具 70/70）","test",
    "独立 baseline worktree @ 22ccd6ee2；R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests；-p:DeployToBgiTools=false","historical",open_hashes),
 ev("baseline-assistant-full","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
    "开工同条件助手全量基线（1570/2/0/1572）","test",
    "独立 baseline worktree @ 22ccd6ee2；全量；-p:DeployToBgiTools=false","historical",open_hashes),
 ev("targeted-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx",
    "本批定向回归 99/99（含 29 条新夹具）；矩阵各行反例证据","test",
    "主工作区最终源码（文档更新后复跑）；R56|R58|R56ReferenceActivationWiring；-p:DeployToBgiTools=false","current",src_hashes),
 ev("assistant-full-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx",
    "本批助手全量回归 1599/2/0/1601","test",
    "主工作区最终源码（文档更新后复跑）；全量；-p:DeployToBgiTools=false","current",src_hashes),
 ev("subagent-readonly-audit","_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md",
    "固定开工 ref 的只读子 Agent 独立核查（跨程序集可达性/写方消费方/写集与回滚归属）","consult",
    "只读、零写入；固定 ref 22ccd6ee2；主执行者按最终源码复核","historical",open_hashes),
 ev("field-audit","_workflow/r56-reference-activation-wiring-2026-09-29/findings.md",
    "现场审计（旧阶段标记语义、跨程序集可达性、写方清单）","document","开工只读审计，绑定开工 HEAD","historical",open_hashes),
 ev("claims-regeneration","_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt",
    "声明面再生差异（+4 / -0，623→627 行）","component","CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过","historical",open_hashes),
 ev("testid-comparison","_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json",
    "逐 testId 差集 added=29/removed=0/changed=0/unchanged=1572","component","主工作区最终源码；TRX 解析","current",src_hashes),
 ev("deploy-target-post","_workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json",
    "部署目标事后清单（1158 文件；最新写入时间 2026-09-27 早于本批）","component",
    "Get-FileHash 清单；本批全部构建/测试带 -p:DeployToBgiTools=false","historical",open_hashes),
]
for mid in ["M1-no-readback-confirmation","M2-no-cross-check-with-change-registry","M3-no-outside-write-detection",
            "M4-no-activation-target-membership","M5-no-commit-recheck","M6-no-stage-mark-gate",
            "M7-no-activation-writeset-hash-sync","M8-no-reference-gate-for-activation",
            "M9-fail-open-on-non-success","M10-no-production-commit-gate","M11-no-lock-guard-on-real-effects",
            "M12-stage-persisted-before-effect","M13-no-activation-readback","M14-no-real-evidence-invariants",
            "M15-no-reference-idempotent-recheck","M16-no-activation-idempotent-recheck",
            "M17-writeset-evidence-not-persisted"]:
    evi.append(ev("mut-"+mid, f"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/{mid}/record.json",
        "突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复","component",
        "主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX","historical",{TRANS: mut_orig}))
m["evidence"] = evi
m["tests"] = [{"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx","expect_success":True},
              {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx","expect_success":True}]
m["comparison"] = {"baseline":"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
                   "final":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx"}
m["mutations"] = json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
sub = m["review_control"]["subagents"]["tasks"][0]
sub["opening_source_hashes"] = {TRANS: sha_at(OPEN,TRANS)}
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("manifest fixed. evidence:",len(m["evidence"]),"mutations:",len(m["mutations"]))
missing=[e["path"] for e in m["evidence"] if not (root/e["path"]).exists()]
print("missing evidence files:",missing)
