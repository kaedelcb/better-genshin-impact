import io, json, os
d='_workflow/wave3-bo6bo7-receive'
f=io.open(d+'/findings.md',encoding='utf-8').read()

old='接收批（mode=code）：把来源独立交付的内容逐字节导入主线并**重跑**验证，不复用来源数字。导入后 8 个产品/文档文件的主线 `git diff HEAD` 与来源 `git diff e2613a851 e009068e2` 逐字节相同 —— 无任何改写或适配，因此本批没有"集成引起的语义变更"。'
new=('接收批（mode=code）：把来源独立交付的内容逐字节导入主线并**重跑**验证，不复用来源数字。\n\n'
 '**导入保真（含接收后改动，逐项区分）**：导入动作的暂存增量 `git diff --cached -- <8 文件>` 与来源增量 '
 '`git diff e2613a851 e009068e2 -- <8 文件>` **逐字节相同**（两侧 SHA-256 均为 `0642971729f3eff0…`）；逐文件核对索引 blob 等于交付 blob，全部为真。'
 '因此**导入时**8 个文件与来源已审增量逐字节相同，无改写、无适配。\n\n'
 '**但"逐字节相同"只是导入时点的事实，不是最终树的事实**：本批随后按收口要求对 4 个状态文档追加了登记内容（R5.3 §24.126、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划），并按 §17.4-A 第 1 条对 `ClaimSurfaceManifest.txt` 执行了强制再生。'
 '故最终工作区相对导入状态有改动；`verification/post-import-delta.diff` 完整列出这些改动，`verification/intake-equivalence.json` 逐文件记录"导入=交付"与"接收后是否修改"两项判定。'
 '**产品源码与两个夹具文件在接收后未被修改**（未暂存 diff 为空）。')
assert old in f; f=f.replace(old,new)

old2='- 声明面：再生 1/1 通过（清单 599→602 行，**+3 行 / -0 行**'
new2='- 结果口径更正：`Counters@notExecuted` 在这些 VSTest/xUnit 运行中为 0，而实际有 2 条 `NotExecuted` 结果行（两个 opt-in P50 诊断：`P50_LoadRepro_WholeClass_UnderControlledLoad`、`P50_DiagnosticRepeat_OptIn`）。**行级口径为准**：基线 1558 passed + 2 NotExecuted = 1560；集成 1562 passed + 2 NotExecuted = 1564。本批早先的 `testid-comparison-summary.json` 误用计数器属性写成 skipped=0，已按行级口径更正并附 `verification/regression-outcomes.json`。\n- 声明面：再生 1/1 通过（清单 599→602 行，**+3 行 / -0 行**'
assert old2 in f; f=f.replace(old2,new2,1)

old3='`WorkflowRunner.cs` 中两处注释与实现不一致（`:1486` 仍写"取最后一条可定位停驻标记"，`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"为下界理由）。两名来源审查者独立指出、均评为建议级。'
new3='`WorkflowRunner.cs` 中注释与实现不一致（`:1486` 仍写"取最后一条可定位停驻标记"，`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"为下界理由）。两名来源审查者独立指出、均评为建议级。**本接收批的第 1 轮会诊进一步指出同一族问题还包含约 `:1400–1403` 的"最后一条/重复提交"旧表述；该扩大范围已采纳并并入 D1 的残项描述**，仍不改动已审源码。'
assert old3 in f; f=f.replace(old3,new3)

f += '''
## 会诊轮次与逐项处置（本批子批计数）

见 `consultation/review-outcome-v1.md`。第 1 轮（既有 GPT 会诊工具，`gpt-6-astra` / `medium`，attempts=1，read-only，含自动附加 diff）给出 2 项 IMPORTANT 与 1 项建议：
- **IMPORTANT-1（送审材料未覆盖八文件导入保真）**：成立。原材料的 `scoped-*.diff` 只覆盖 manifest 的 `sources`（4 个代码/夹具文件），4 个状态文档的补丁未随材料提供，且"8 文件逐字节相同"缺少"导入时点"限定。**已修复**：新增 `verification/intake-equivalence.json`（逐文件导入=交付判定 + 导入增量与来源增量的 SHA-256 对照 + 接收后改动清单）、`verification/import-delta-8files.diff`（含 4 个文档补丁的完整导入增量）、`verification/post-import-delta.diff`（接收后改动），并修正本文件上文限定语。
- **IMPORTANT-2（替代摘要的结果口径不一致）**：成立。`testid-comparison-summary.json` 用 `Counters@notExecuted`（0）当 skipped，与两次运行各有 2 条 `NotExecuted` 行矛盾。**已修复**：按行级口径重写摘要，并新增 `verification/regression-outcomes.json` 记录每次运行的全部计数与 2 条 `NotExecuted` 的具体用例名。
- **建议（D1 注释范围更宽 + 夹具映射只有 10 条不足以独立证明 93 项完全一致）**：**采纳**。D1 范围扩大至约 `:1400–1403`（见上）；新增 `verification/targeted-93-ids.json`（完整 93 条 testId/名称/结果与来源交付 TRX 的逐条一致性与零 mismatch）与 `verification/mutation-verification.json`（8 项突变的 baseline/mutant/restored 结果、目标身份、断言行、文件摘要，以及与来源清单记录 mutant SHA 的逐项相等性）。

第 1 轮的收尾结论为"本接收批仍有 2 项未闭合 IMPORTANT"；按纪律 R1/R2 不得降级，已按同一修复批归集修复证据并执行验证轮会诊（见 `consultation/review-outcome-v2.md`）。
'''
io.open(d+'/findings.md','w',encoding='utf-8',newline='\n').write(f)

s=json.load(io.open(d+'/regression/testid-comparison-summary.json',encoding='utf-8'))
def fix(x): x['skipped']=2; x['not_executed_rows']=2; x['counters_note']='TRX Counters@notExecuted is 0 in this VSTest/xUnit shape; 2 result rows carry outcome NotExecuted (P50_LoadRepro_WholeClass_UnderControlledLoad, P50_DiagnosticRepeat_OptIn). Row-level accounting is authoritative: total = passed + failed + NotExecuted rows.'; return x
s['baseline']=fix(s['baseline']); s['final']=fix(s['final'])
s['counts']['not_executed_rows_baseline']=2; s['counts']['not_executed_rows_final']=2
s['corrected_note']='CORRECTED after review round 1: the earlier version of this summary read skipped from Counters@notExecuted, which is 0 despite two NotExecuted rows. Corrected accounting is in this file and in verification/regression-outcomes.json.'
io.open(d+'/regression/testid-comparison-summary.json','w',encoding='utf-8',newline='\n').write(json.dumps(s,ensure_ascii=False,indent=2)+"\n")
print('findings bytes',os.path.getsize(d+'/findings.md'),'| summary bytes',os.path.getsize(d+'/regression/testid-comparison-summary.json'))
