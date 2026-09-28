import io, json, hashlib, os
d='_workflow/wave3-bo6bo7-receive'
def sha(p): return hashlib.sha256(open(p,'rb').read()).hexdigest()

context = '''# 接收批目标（objective）

把已独立交付、并按原等级闭环的 Wave3 BO-6/BO-7 修复**接收进茶包主线**，并在主线 HEAD 上重新取得定向回归、助手全量回归、反向突变与声明面证据；只做接收及由集成引起的最小适配，不施工 BO-8/BO-9、R5.6、R6.1、R6 diff guard，不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。

来源交付：`wave3-bo6-bo7-2026-09-28`，隔离 worktree `C:\\\\Users\\\\Administrator\\\\.codex\\\\worktrees\\\\wave3-bo6-bo7\\\\better-genshin-impact-LCB`，交付 HEAD `e009068e22b18d89f4eb9f8947fa937dfa8c925c`，开工基线 `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`。
本批开工 HEAD：`6fd6207e58ec93dcc38645c12131a9a512f5fc69`（`main-OldTeaBag-B168`）。主线相对来源基线只多 3 个文档提交，与来源增量无文件重叠。

接收方式：不合并隔离分支；按 `git checkout e009068e2 -- <路径>` 逐字节导入来源相对基线的 8 个产品/测试/状态文档文件，另收录少量权威证据（检查点、两份独立会诊报告与元数据、会诊台账对账、突变证据索引、testId 对照、风险矩阵）。6 份 closeout 快照的 report.json/packet.md 与逐突变 TRX 日志包留在来源 worktree，不搬入。

完成判据（本批）：BO-6/7 修复确实进入主线；集成版本定向 93/93 与助手全量回归通过且与来源逐 testId 对照；8 项反向突变在集成字节上重新执行为 baseline Passed / mutant Failed / restored Passed 且精确恢复；四项原级义务闭环状态未被削弱；声明面按规则再生并评审；台账回填；生产门保持关闭。
'''

findings = '''# 发现、处置与残余（findings）

## 本批性质

接收批（mode=code）：把来源独立交付的内容逐字节导入主线并**重跑**验证，不复用来源数字。导入后 8 个产品/文档文件的主线 `git diff HEAD` 与来源 `git diff e2613a851 e009068e2` 逐字节相同 —— 无任何改写或适配，因此本批没有"集成引起的语义变更"。

## 主线工作区行尾与来源记录的对应

主线按 `core.autocrlf=true` 检出，工作区为 CRLF，仓库 blob 为 LF。两者内容逐字节相同（仅行尾差异）：
- `WorkflowRunner.cs`：仓库 blob（LF）`181aa93e000be99d8eed9ce192587a56194d270e47f67bd561297a57476f14c9`（即两名来源审查者核验时引用的"当前源码"），工作区（CRLF）`5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`。
- `ClaimSurfaceManifest.txt`：导入时（LF/blob 形态）`84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`；主线声明面再生后为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
本批 manifest 的所有 `source_sha256` 记录**工作区 CRLF 形态**，内部自洽；来源清单记录的是混合形态（original/restored 为 LF、mutant 为 CRLF），仅作对照。

## 发现 F-1（建议级 · 声明面版本引用陈旧）——已登记，不改写来源工件

**事实**：§24.125.3、`_workflow/wave3-bo6-bo7/owner-checkpoint.md`、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划均把声明面清单 SHA 记为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`；随交付提交的清单实际为 `84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`。

**已核清原因**：来源目录 `regression/claim-surface-post-consultation/` 记录 `manifest-before=9EAA1A7F…`、再生后 `manifest-after-regen=manifest-after-no-env=84E1D93D…`。即 `9EAA1A7F…` 是"修正 §24.125 状态/预算声明"那一步的中间值；其后新增的 §24.125.5（owner 批准追加会诊的闭环文本）再次改变声明面，来源侧执行了再生却未回改前述散文引用。

**处置**：不改写已与检查点登记哈希 `A4AB7747…` 绑定的来源工件（改写会使登记失效），改在 R5.3 §24.126.4 登记权威口径（最终值以随提交清单为准，`9EAA1A7F…` 只作该步中间值）。**级别判定说明**：这是散文中的证据指针版本问题，不影响源码、夹具、突变、四项义务闭环或清单内容本身；两名来源审查者与来源批均未据此发现运行正确性缺陷。本批不据此升级为重要项，但也不隐藏：它进入本批的受审材料与最终报告。

## 发现 F-2（建议级 · 残留，来源已登记）BO-6/7-D1

`WorkflowRunner.cs` 中两处注释与实现不一致（`:1486` 仍写"取最后一条可定位停驻标记"，`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"为下界理由）。两名来源审查者独立指出、均评为建议级。本批**不为它单独改写已审源码**：改动会作废 manifest 源哈希与 8 项突变绑定、需重跑整套构建/回归并重取同等级复审，而该处不影响行为正确性。按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正并按该批质量门验证。

## 8 项反向突变（在集成后的主线字节上重新执行）

全部为 baseline Passed / mutant Failed / restored Passed，三阶段构建 exit 0、测试 exit 0→1→0，命中具名目标断言，源码逐字节精确恢复。`原始/恢复` SHA-256 = 工作区 `5470cfcb…`；8 个 `mutant_sha256` 与**来源交付记录完全相同**。

| id | target test | target testId | 断言位置 | mutant sha (前12位) |
|---|---|---|---|---|
| bo6-resume-live-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 96212d01e57a |
| bo6-completed-filter-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 3115f2c3a4d8 |
| bo6-tail-fail-closed-v3 | Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1071 | ff0ff5bf3594 |
| bo6-tail-persisted-failure-v4 | 同上 | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1080 | a04531dc4871 |
| bo7-candidate-first-v3 | Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner | 1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc | WorkflowRunnerTests.cs:1131 | ef6ed36b1b8b |
| bo7-rescue-first-v3 | Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor | 7a665a8f-f8e9-11b4-598f-a1f8d24b805a | WorkflowRunnerTests.cs:1173 | e5fcb0af5aec |
| bo7-earliest-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 8668d00c7f7e |
| bo7-stable-identity-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | f04db2ad7111 |

各突变的 baseline/mutant/restored 构建日志与 TRX、`mutation.patch`、`source-original.cs` 位于 `_workflow/wave3-bo6bo7-receive/mutations/<id>/`；机读记录见 `mutation-records.json`。突变执行后 `WorkflowRunner.cs` 与 `WorkflowRunnerTests.cs` 的 `git diff` 为空，即导入字节被逐字节还原。

## 回归与对照

- 接收前同条件基线（HEAD `6fd6207e5`）：定向三类 89/89；助手全量 1558 passed / 2 skipped / 0 failed / 1560。
- 集成后：定向三类 93/93（93 个 testId/名称/结果与来源交付最终 TRX **完全相同**）；助手全量 1562 passed / 2 skipped / 0 failed / 1564。
- testId 差集（基线→集成）：1556 unchanged / 8 added / 4 removed / 0 changed，与来源交付逐 ID 相同。`0 changed` **不**表示既有测试语义未变：两个保留 testId 的 helper（`ec3b4575…` `RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate`、`14addbd1…` `RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed`）断言由"期望 null（保守链尾）"改为"期望返回停驻标记"。
- 声明面：再生 1/1 通过（清单 599→602 行，**+3 行 / -0 行**，仅新增 §24.126 中三行承载声明关键词的文本；无既有声明行被改或消失），清除变量复跑 1/1 通过，两次 SHA 稳定为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
- 部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`（及非 x64 同名目录）在全部构建与测试前后 LastWriteTimeUtc 与文件数均未变化（1158 个文件，09/26 21:41:14 UTC）。

## 范围边界与未验证项

- 只接收 BO-6/BO-7；**BO-8 R29 IMPORTANT 与 BO-9 R34 F5 IMPORTANT 仍未并入、仍未闭合**；R5.6、R6.1、R6 diff guard 未提前集成；BO-13、SB21-3 BO-4、SB21-2 BO-10/12 未重开。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；**未做实机、生产或真实 User 验证**。外部执行边界仍为 fake。
- 本批不声称除上述同步之外的四项义务仍然为 closed；那由来源侧两次独立会诊按原等级裁定，本批只证明其被接收的字节未变且判别力仍在。
'''

budget = '''# 会诊预算与分级台账（budget）

## 子批计数（R7）

- **本接收批是一个新的子批**，拥有自己的累计会诊请求上限（8 次，含首审/复审/验证/收口，且所有模型与渠道；已发出但失败或超时也计次；本地预检拦截而未发出者不计）。
- **来源 BO-6/BO-7 子批的 8/8 + owner 批准追加 2/2 不被重置、不被继承、也不被本批消耗**：其原始记录（6 次 GPT 工具调用、8 次 provider/tool 报告尝试 → 保守 8/8、余额 0；另有 owner 批准的请求 9/10 各 1 次，2/2 用尽、余额 0）保持原样登记于 `_workflow/wave3-bo6-bo7/owner-checkpoint.md` 与 `consultation/current-ledger-reconciliation.json`。
- 本批计数：见文末「本批实际发出记录」。

## 固定强度与点位

- 固定模型/强度：`gpt-6-astra` / `medium`（与来源子批一致），不降配。
- 本批固定会诊点：**验证轮 1 次**——在冻结的集成快照上审"接收是否正确、集成版本证据是否支持接收结论、四项原级义务的字节绑定是否仍成立、F-1/F-2 的处置是否合规"。
- 渠道：默认既有 GPT 会诊工具；送审前按设施计划做容量预检（附件数/单文件字节/合计字节、附件全文 token 估算、工具另附 diff 完整性）。仅当满足设施计划的三条分流条件时才改走独立本地只读 CLI，并保持同模型同强度、记录触发依据与退出码。

## 原等级与未闭合项（不得降级）

| 项 | 原级 | 状态 |
|---|---|---|
| BO-6 R19 | MUST | 来源侧 closed；本批证明被接收字节未变、判别力仍在 |
| BO-6 R21 F4 | IMPORTANT | 同上 |
| BO-7 R21 F1 | MUST | 同上 |
| BO-7 R21 F2 | MUST | 同上 |
| BO-6/7-D1（注释陈旧） | 建议级 | 未修正，按既定处置留待下次绑定 `WorkflowRunner.cs` 的批次 |
| BO-8 R29 | IMPORTANT | **未闭合、未并入** |
| BO-9 R34 F5 | IMPORTANT | **未闭合、未并入** |

本批**不**对 BO-8/BO-9 作任何等级或状态变更；若会诊给出重要/必改发现，只能升级不能降级，且必须闭环或形成 owner 检查点。

## 本批实际发出记录

（收口时回填：发出的请求数、模型/强度、渠道、附件/字节与 token 估算、退出码、结论与逐项处置。）
'''

io.open(d+'/context.md','w',encoding='utf-8',newline='\n').write(context)
io.open(d+'/findings.md','w',encoding='utf-8',newline='\n').write(findings)
io.open(d+'/budget.md','w',encoding='utf-8',newline='\n').write(budget)
for f in ('context.md','findings.md','budget.md'):
    print(f, os.path.getsize(d+'/'+f), sha(d+'/'+f)[:16])
