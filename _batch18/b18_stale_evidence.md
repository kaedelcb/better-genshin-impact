# 批次 18（D5 指针恢复核实批）证据文件——陈旧性证据链＋逐项对照＋回归证据

> 本文件随 KIMI 会诊送审（--file 直读）。编制：sess_7687f914（ZCode，2026-09-26）。
> 基准：HEAD `c41a2902e`，分支 `main-OldTeaBag-B168`。工作区＝owner 既有未提交文件（见 §5 材料外变更）
> ＋本批自有受控变更：`ClaimSurfaceManifest.txt` +2 行（ev5 漏再生修复，见 §4.1）与 `_batch18/` 新建证据
> （会诊第 3 轮建议 1 处置后的补充注明）。

## 1. 保留提示词 §四 与批次 13 交付逐项对照（判定 P1 的依据）

保留提示词＝`C:\Users\Administrator\.tools\zcode-relay\task-relays\batch18-d5-prompt-preserved.md`
（创建时间 2026-09-25 05:45:19，批次 17 收口 8499aa4c8＝2026-09-25 03:44 之后约 2 小时；
证据阶段收尾时被 `next-batch-prompt.md` 第 45 行指定为「恢复指针地位」对象）。

| 保留提示词 §四 项 | 批次 13 交付证据（先于提示词 11.5 小时） |
|---|---|
| 「D5 Complete 未知语义类型拆分（owner 决策单裁决 A：仅全成功产生 CompleteSucceeded；未知/失败终态产生 CompleteWithUnresolved，只有前者可执行 RunSpecified；具体命名由实现批确认）」 | 提交 `7336902d8`（2026-09-24 16:04:18）「feat(r5): D5 批次完成类型拆分（CompleteSucceeded/CompleteWithUnresolved，纯函数未接线）」：删除无载荷 `Complete`，新增 `CompleteSucceeded()`（BatchReconcilePlan.cs 现 127 行）与 `CompleteWithUnresolved(IReadOnlyList<BatchUnresolvedItem>)`（现 135 行）；命名即实现批确认；R5.3 §24.108 全节登记 |
| 「实现提示：需同时处理 BatchReconcilePlan.cs 第 166–170 行空批次早退与第 282–289 行『全部确认』分支；两处是否都归入『全成功』类须在本批可验证设计中明确，不得默认沿用旧语义」 | 两处均逐条裁定（§24.108「两处指定分支的归类裁定（逐条，含依据）」表）：空批次早退→`CompleteSucceeded`（vacuous truth＋调用方 `Started` 守卫）；「全部确认→完成」分支→按**本拍确认投影**逐项分两类。提示词行号 166–170/282–289 对应 **D5 实现前**的旧文件——现行文件同一逻辑位置已移至 195–199/312–366。**行号口径注（会诊第 2 轮 G1 处置，逐行核对 HEAD `c41a2902e`）**：本表按「语句行」计——195–199＝`if (items.Count == 0)` 块、312–366＝全部确认 `if` 块；§24.108 引文按「含前置注释行」计＝191–199（191 行起为 `[D5] 空批次归类` 注释块）、311–366（311 行为 `// 3) 全部确认 → 完成` 节注释）。两组数字均系 HEAD 同一文件的准确行号，差异仅为计数口径；「[D5 必改]」注解行段＝316–321，注解＋投影实现段＝316–335 |
| 「反例先行（先红夹具后实现）＋完整会诊闭环照常执行」 | 反例先行：`batch13_red_core.trx` 旧实现下 **13 红／64 绿／77**（两条原 `_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed` 夹具改新语义后变红）；实现后定向三类 **77/77**（`batch13_green_core4.trx`）；会诊 `gpt-6-sol`/medium 两轮各 1 次成功：首轮 1 必改／2 重要／1 建议全部处置（必改＝本拍确认投影，修复注解在现 316–321 行「[D5 必改（gpt-6-sol 评审第 1 项）]」，注解＋投影实现段 316–335 行），第二轮判**无必改、无未登记重要项**。交接稿页首批次 13 行与 §24.108 均登记 |
| 「完成判据基线：批次 17 后助手全量 1378 通过/2 跳过/0 失败/1380（_b17_full_final1/2.trx）」 | 该基线仅约束「本批改动引入的回归比较」。本批**零生产代码改动**，改用现行基线复验（§4）；该行同时暴露提示词的内部时序矛盾：它引用批次 17（2026-09-25）基线，却把批次 13（2026-09-24）已交付的工作当作待办 |

## 2. 时序证据链（判定 P2 的依据）

| 事实 | 证据 |
|---|---|
| 批次 13（D5）提交时间 2026-09-24 16:04:18 | `git log -1 --format=%ad 7336902d8` |
| 批次 17 收口提交 2026-09-25 03:44:35 | `git log -1 --format="%h %ad %s" 8499aa4c8`＝「test(r5): 批次17 测试基础设施——D3 偶发红机制查明+修复+裁决②两突变取证」 |
| 保留提示词创建 2026-09-25 05:45:19 | 文件 mtime（task-relays 目录，库外文件）；同目录 `next-batch-prompt.md` 05:59:20 改写为证据阶段续行提示词，其第 45 行：「批次 18（D5）提示词已备份于 task-relays\batch18-d5-prompt-preserved.md，证据阶段收尾后恢复其指针地位」 |
| 交接稿页首批次 13 行明载 D5 已完成 | 「2026-09-24 批次 13：D5 批次完成类型拆分（纯函数＋夹具，生产零消费点，未接线）……详见 R5.3 §24.108」 |
| R5.3／交接稿无任何「批次 18」字样 | `grep -n "批次 18" 两文档＝空`：批次 18 从未开工或登记 |
| 证据阶段检查点列出的两条下一步 | 「批次 18（D5）提示词＝batch18-d5-prompt-preserved.md；或 owner 对待决表 D-E2~D-E7 裁决后启动接线前合同批」——前一路径的声明目标已由批次 13 满足 |

## 3. D5 残余面全部属于 owner 待决／接线闸门（判定 P3 的依据）

- **D5 接线与否**＝owner 待决表 **D-E5**（SW-03：①接线并先定 IW-09 投影合同（C13 生效）／②维持休眠，C13 备案／③拆除 D5 组件；§24.115 SW-03/IW-09）。批次 13 审计确认 `BatchReconcileAction`/`BatchReconcileDecider` 生产源码**零消费点**（文本扫描口径，§24.108「调用方审计」表）。
- **等待→D5 投影**＝§24.117 **C13**（条件生效）；**等待队列合同草案 v2 其余条款**＝D-E2~D-E4 未裁决不实现（本批开工纪律原样生效）。
- 批次 13 残余中的非接线面（「空批次 Started 守卫仅静态读码」「真实收尾行为与服务端完成条件只由文本审计陈述」）均属**调用方/接线面**：调用方守卫的消费点本身未接线（零消费点），在 D-E5 裁决前为其堆行为夹具属「在未定语义上堆实现」，且不在保留提示词 §四 范围内。

## 4. 本批回归证据（当前 HEAD `c41a2902e`，零生产代码改动）

| 项 | 结果 | 证据 |
|---|---|---|
| 定向 D5 夹具类 `CoordinatedBatchAdmissionRelationTests` | **40/40 通过** | `Test/MultiplayerHoeingAssistant.UnitTest/TestResults/_batch18_targeted.trx` |
| 助手全量（首跑） | 1 失败／1392 过／2 跳过／1395——失败＝`ClaimSurfaceGuardTests.DesignDocs_ClaimSurface_MatchesReviewedManifest`，报「新增/变更声明 2 行」＝**ev5 文档行漏再生清单**（HEAD 既有缺陷，见 §4.1） | `_batch18_full.trx` |
| 声明面清单再生＋复跑 | 带 `CLAIM_SURFACE_REGENERATE=1` 再生通过；**不带 env** 复跑通过；清单 580→582 行（`5EC051FB`→`2D34494E`），+2 行经核确为 ev5 两处残留（§24.117 会诊记录行＋交接稿 ev5 页首行） | `_batch18_claim_regen.trx`／`_batch18_claim_verify.trx`；`git diff` 清单仅 +2 |
| 助手全量（终帧） | **1393 过／2 跳过／0 失败／1395**，与 ev1 基线 `_ev1_full_final.trx` 逐名差集**双侧空**（新增 0／移除 0／结果翻转 0；两例 Skip 两侧同形） | `_batch18_full_final.trx` |
| 生产目录 diff | 零（`git status` 生产目录无改动；唯一受控变更＝声明面清单 +2 行，属 ev5 修复，纳入本批提交评审） | `git status --porcelain`（见会诊材料） |

### 4.1 ev5 漏再生缺陷的定性与处置（如实登记）

- **定性**：ev5（`c41a2902e`）向 R5.3（§24.117）与交接稿（ev5 页首行）写入了含声明面词（会诊闸门绿/不接线/关闭等）的行，但未按规则执行 `CLAIM_SURFACE_REGENERATE=1` 再生；清单停留在 ev4 值（580/`5EC051FB`）。ev2/ev3 只跑 BGI 全量、ev4/ev5 为文档批未跑助手全量，故该失配直到本批首跑助手全量才暴露。
- **处置**：按守卫自身指示再生（+2 行＝ev5 已评审内容的机械同步，非新声明），不带 env 复跑通过，清单变更纳入本批提交评审并在此留痕。
- **全称否定自查（R3）**：本文件不断言「此后不再出现清单失配」；仅记录本次再生与复跑事实。

## 5. 材料区分（R6）

- **本批文件**：`_batch18/b18_objective.txt`、`_batch18/b18_stale_evidence.md`（本文件）、`_batch18/*.trx` 留档、`Test/.../ClaimSurfaceManifest.txt`（+2 行，ev5 修复）、拟新增的 R5.3 登记节与交接稿页首行（登记批交付物，会诊后落册）。
- **材料外变更（owner 既有，本批不动、不入提交）**：`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md` 两文档的未暂存修改；`*.bak`/`*.stale`/`*.r13snap`/`MultiplayerHoeingAssistant.dll`/`TestResults/` 等未跟踪残留。
- **relay 侧文件（库外）**：`task-relays/active-ledger.json`、`test/ledger-batch18.json`、`batch18-start.input.json`、`mistletoe-zcode-20260926--batch18.ownership.json`。
