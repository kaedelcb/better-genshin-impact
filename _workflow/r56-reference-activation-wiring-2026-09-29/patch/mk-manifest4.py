import json, pathlib, hashlib, subprocess
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
def sha(rel): return hashlib.sha256((root/rel).read_bytes()).hexdigest()
def sha_at(rev, rel): return hashlib.sha256(subprocess.run(["git","-C",str(root),"show",f"{rev}:{rel}"],capture_output=True).stdout).hexdigest()
def nlines(rel): return len((root/rel).read_text(encoding="utf-8-sig").splitlines())
def find_lines(rel, start_marker, end_marker=None):
    L=(root/rel).read_text(encoding="utf-8").splitlines()
    s=next(i+1 for i,l in enumerate(L) if l.startswith(start_marker))
    e=len(L) if end_marker is None else next(i for i,l in enumerate(L) if i+1>s and l.startswith(end_marker))
    return s,e
OPEN="22ccd6ee2721abb1642535253997c57dbf9020d6"
TRANS="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"
REH="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs"
OLD_TEST="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs"
CSM="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"
r53="Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"; plan="槲寄生调度器总计划.md"; idx="Docs/design/mistletoe-parallel-deliveries.md"
src_hashes={s:sha(s) for s in json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))["sources"]}
open_hashes={TRANS:sha_at(OPEN,TRANS), OLD_TEST:sha_at(OPEN,OLD_TEST)}
a=find_lines(r53,"## §24.129 R5.6 A 项主线施工子批"); b=find_lines(plan,"## 2026-09-29：R5.6 A 项主线施工","## 2026-09-28：R5.6 主线迁移集成批")
c=find_lines(idx,"### R5.6 A 项进展（主线施工，2026-09-29）","## 无需 owner 提醒的执行闭环")
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
records=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
mut_ev=[]
for rec in records:
    mut_ev.append({"id":"mut-"+rec["id"],"path":f"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/{rec['id']}/record.json",
      "purpose":"突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复","level":"component",
      "conditions":"三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希","binding":"historical",
      "source_sha256":{rec["source"]:rec["original_sha256"]}})
base_ev=[
 {"id":"baseline-targeted","path":"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx","purpose":"开工同条件定向基线（R56/R58 迁移夹具 70/70）","level":"test","conditions":"独立 baseline worktree @ 22ccd6ee2；-p:DeployToBgiTools=false","binding":"historical","source_sha256":open_hashes},
 {"id":"baseline-assistant-full","path":"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx","purpose":"开工同条件助手全量基线（1570/2/0/1572）","level":"test","conditions":"独立 baseline worktree @ 22ccd6ee2","binding":"historical","source_sha256":open_hashes},
 {"id":"targeted-final-frozen","path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx","purpose":"本批定向回归 111/111（第 1 轮会诊修复后）","level":"test","conditions":"主工作区最终源码；-p:DeployToBgiTools=false","binding":"current","source_sha256":src_hashes},
 {"id":"assistant-full-final-frozen","path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx","purpose":"本批助手全量回归 1611/2/0/1613","level":"test","conditions":"主工作区最终源码；全量","binding":"current","source_sha256":src_hashes},
 {"id":"subagent-readonly-audit","path":"_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md","purpose":"固定开工 ref 的只读子 Agent 独立核查","level":"consult","conditions":"只读、零写入；固定 ref 22ccd6ee2","binding":"historical","source_sha256":open_hashes},
 {"id":"field-audit","path":"_workflow/r56-reference-activation-wiring-2026-09-29/findings.md","purpose":"现场审计 + 会诊发现处置 + 边界（F-1..F-12）","level":"document","conditions":"开工只读审计与会诊后修订","binding":"historical","source_sha256":open_hashes},
 {"id":"claims-regeneration","path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt","purpose":"第 1 轮声明面再生差异（+4/-0）","level":"component","conditions":"CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过","binding":"historical","source_sha256":open_hashes},
 {"id":"claims-regeneration-r2","path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt","purpose":"第 2 轮声明面再生差异（+2/-0，627→629）","level":"component","conditions":"同上","binding":"historical","source_sha256":open_hashes},
 {"id":"mutations-summary","path":"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md","purpose":"28 项反向突变逐项补丁与三段判定（供审查者核对判别力）","level":"component","conditions":"主工作区；逐项含独立日志与 TRX","binding":"historical","source_sha256":{TRANS:records[0]["original_sha256"]}},
 {"id":"testid-comparison","path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json","purpose":"逐 testId 差集 added=41/removed=0/changed=0/unchanged=1572","level":"component","conditions":"主工作区最终源码","binding":"current","source_sha256":src_hashes},
 {"id":"deploy-target-post","path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json","purpose":"部署目标事后清单（1158 文件；最新写入早于本批）","level":"component","conditions":"Get-FileHash 清单；全部构建带 -p:DeployToBgiTools=false","binding":"historical","source_sha256":open_hashes},
 {"id":"review-round1-report","path":"_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md","purpose":"第 1 轮会诊报告原文（5 MUST + 4 IMPORTANT + 1 建议级）","level":"consult","conditions":"gpt-6-astra / medium；只读；attempts=1（子批计数 1/8）","binding":"historical","source_sha256":open_hashes},
]
m["evidence"]=base_ev+mut_ev
m["packet"]=[
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/context.md","role":"objective"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/findings.md","role":"findings"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/budget.md","role":"budget"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md","role":"contract",
  "coverage_notes":"第 1 轮会诊报告原文（10 项发现），本批逐条按原级修复；本轮为验证轮，请核对修复与新增反例/突变是否真正闭合这些发现。"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs","role":"source"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs","role":"source"},
 {"path":"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs","role":"source"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md","role":"source",
  "coverage_notes":"28 项反向突变的补丁原文与三段判定（含 exit code、命中标记、恢复哈希），用于核对关键断言的判别力。"},
 {"path":REH,"role":"source","start_line":1,"end_line":40,
  "coverage_notes":f"**节选**：MigrationRehearsal.cs 全文 {nlines(REH)} 行，本批仅按其能力边界修正**文案与注释**（建议级发现）；头部注释与签名段在此，其余未改动。"},
 {"path":OLD_TEST,"role":"source","start_line":1,"end_line":40,
  "coverage_notes":f"**节选**：R56MigrationSwitchTransactionTests.cs 全文 {nlines(OLD_TEST)} 行，本批**未改动**该文件；此处只给夹具头部注释。"},
 {"path":CSM,"role":"source","start_line":1,"end_line":6,
  "coverage_notes":f"**节选**：ClaimSurfaceManifest.txt 全文 {nlines(CSM)} 行（含本批新增 6 行）；本批逐行改动见 claims/claims-diff.txt 与 claims/claims-diff-r2.txt。"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt","role":"contract",
  "coverage_notes":"第 2 轮声明面再生的逐行差异（+2/-0）。"},
 {"path":r53,"role":"contract","start_line":a[0],"end_line":a[1],
  "coverage_notes":f"**节选**：本批新增的 §24.129 全节（含 §24.129.7 第 1 轮会诊逐项处置）（L{a[0]}-L{a[1]}，全文 {nlines(r53)} 行）。"},
 {"path":plan,"role":"contract","start_line":b[0],"end_line":b[1],
  "coverage_notes":f"**节选**：本批 2026-09-29 进度条目（L{b[0]}-L{b[1]}，全文 {nlines(plan)} 行）。"},
 {"path":idx,"role":"contract","start_line":c[0],"end_line":c[1],
  "coverage_notes":f"**节选**：本批「R5.6 A 项进展」段（L{c[0]}-L{c[1]}，全文 {nlines(idx)} 行）。"},
]
m["mutations"]=records
(W/"manifest.json").write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
missing=[e["path"] for e in m["evidence"] if not (root/e["path"]).exists()]+[p["path"] for p in m["packet"] if not (root/p["path"]).exists()]
print("evidence",len(m["evidence"]),"packet",len(m["packet"]),"missing",missing)
print("r53",a,"plan",b,"idx",c)
