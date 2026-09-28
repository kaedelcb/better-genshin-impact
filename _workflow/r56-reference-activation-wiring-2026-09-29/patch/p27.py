import json, pathlib
root = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W = root/"_workflow/r56-reference-activation-wiring-2026-09-29"
def nlines(rel): return len((root/rel).read_text(encoding="utf-8-sig").splitlines())
def find_lines(rel, start_marker, end_marker=None):
    L = (root/rel).read_text(encoding="utf-8").splitlines()
    s = next(i+1 for i,l in enumerate(L) if l.startswith(start_marker))
    e = len(L) if end_marker is None else next(i for i,l in enumerate(L) if i+1 > s and l.startswith(end_marker))
    return s, e
r53="Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
plan="槲寄生调度器总计划.md"
idx="Docs/design/mistletoe-parallel-deliveries.md"
a=find_lines(r53,"## §24.129 R5.6 A 项主线施工子批"); b=find_lines(plan,"## 2026-09-29：R5.6 A 项主线施工","## 2026-09-28：R5.6 主线迁移集成批")
c=find_lines(idx,"### R5.6 A 项进展（主线施工，2026-09-29）","## 无需 owner 提醒的执行闭环")
OLD="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs"
REH="MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs"
CSM="Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
m["packet"]=[
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/context.md","role":"objective"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/findings.md","role":"findings"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/budget.md","role":"budget"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs","role":"source"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs","role":"source"},
 {"path":"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs","role":"source"},
 {"path":REH,"role":"source","start_line":1,"end_line":45,
  "coverage_notes":(f"**节选**：MigrationRehearsal.cs 全文 {nlines(REH)} 行，本批**未改动**该文件（git diff 为空）；"
                    "此处只给头部类注释与签名段以保留文件身份与调用面。其夹具 R58MigrationRehearsalTests 在本批定向 TRX 中全绿。")},
 {"path":OLD,"role":"source","start_line":1,"end_line":40,
  "coverage_notes":(f"**节选**：R56MigrationSwitchTransactionTests.cs 全文 {nlines(OLD)} 行，本批**未改动**该文件；"
                    "此处只给夹具头部注释（含其对「未接线」的能力边界声明）。其 63 条在基线 70/70 与最终 99/99 中均通过。")},
 {"path":CSM,"role":"source","start_line":1,"end_line":6,
  "coverage_notes":(f"**节选**：ClaimSurfaceManifest.txt 全文 {nlines(CSM)} 行（历史声明行 + 本批新增 4 行）；"
                    "本批的逐行改动（+4/-0，623→627）见同批 packet 的 claims/claims-diff.txt；"
                    "此处仅取样行以保留文件身份。")},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt","role":"contract",
  "coverage_notes":"本批声明面再生的逐行差异（新增 4 行，均来自 R5.3 §24.129）。"},
 {"path":r53,"role":"contract","start_line":a[0],"end_line":a[1],
  "coverage_notes":(f"**节选**：仅本批新增的 §24.129（L{a[0]}-L{a[1]}，全文共 {nlines(r53)} 行）；其余为历史批次记录，未随本批改动。")},
 {"path":plan,"role":"contract","start_line":b[0],"end_line":b[1],
  "coverage_notes":(f"**节选**：仅本批新增的 2026-09-29 进度条目（L{b[0]}-L{b[1]}，全文共 {nlines(plan)} 行）。")},
 {"path":idx,"role":"contract","start_line":c[0],"end_line":c[1],
  "coverage_notes":(f"**节选**：仅本批新增的「R5.6 A 项进展」段（L{c[0]}-L{c[1]}，全文共 {nlines(idx)} 行）。")},
]
m["packet_limit_bytes"]=524288
(W/"manifest.json").write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("packet entries:",len(m["packet"]))
