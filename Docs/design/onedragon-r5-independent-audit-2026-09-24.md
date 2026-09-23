# 槲寄生调度器 R5 独立审计交接（2026-09-24；只读审计批次，未施工）

> 本稿是**先前会话**（入口 [onedragon-r5-next-session-2026-09-24.md](onedragon-r5-next-session-2026-09-24.md)）之后的
> 一个有界「独立审计」批次产物：核实生产接线关键调用路径的真实缺口。**本稿不构成 R5 完成、不解除任何门禁、不代替实机验收。**
> 审计期间未改产品代码、未动真实 User 目录与第三方 JS、未提交除本文件外的任何内容。

## 1. 审计基线（起草时实测，非永久状态）

- 分支 `main-OldTeaBag-B168`，HEAD `80ef0c5fa`（`docs(r5): add bounded next-session handoff`），相对 `origin-lcb/main-OldTeaBag-B168` **ahead 264**。
- 跟踪工作区**干净**；未跟踪 `.bak`／`.stale`／`TestResults`／探针目录/日志**原样保留**（未删未移）。
- 施工范围＝`94f7b4925^..HEAD` 共 **36 笔**提交（含本稿前一笔会话入口文档）／56 文件／约 11861 增、1073 删。
- 生产入口（E3/E4/E5、节点改道、S4b/S8b）与真实 User 门**继续关闭**；R5.8 未签署。**本批不报告 R5 完成。**

## 2. 已核实事实（代码路径证据）

| # | 结论 | 代码入口证据 |
|---|---|---|
| V1 | 实验物理槽未接线：`PhysicalSlotLedger` 全仓库引用**仅**其自身定义与 `PhysicalSlotLedgerTests`，无生产调用方 | `BetterGenshinImpact/Service/Execution/PhysicalSlotLedger.cs`（`internal sealed`，`ForCurrentWindowsSession` 只被测试叫用） |
| V2 | 耐久 `Prepared` 身份账未接线：`DurableSubmissionIdentityStore` 引用**仅**其自身与 `DurableSubmissionIdentityStoreTests` | `BetterGenshinImpact/Service/Execution/DurableSubmissionIdentityStore.cs` |
| V3 | 定向停止 API 未接线：`ExecutionScope.TryRequestPreempt(Guid,long)` 全仓库仅定义 + `TaskTakeoverIncidentTests`，**无生产调用方** | `BetterGenshinImpact/Service/Execution/ExecutionScope.cs` |
| V4 | §24.96 范围纠偏已贯穿：全仓库检索「整份旧备份／可信锚点／整域旧备份／独立 Windows 服务」**在 `Docs/design` 之外为零命中**；`Docs/design` 内所有命中均为撤销说明或显式标注「非本期目标」 | `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md` §24.91／§24.94／§24.96；交接稿页首纠偏段 |
| V5 | 助手全量 TRX：`r5_slot_assistant_full_20260924.trx` = total 1179／executed 1177／passed 1177／failed 0，2 条 `NotExecuted` 为受控 P50 诊断入口；BGI 全量 TRX：`r5_slot_init_bgi_final_20260924.trx` = total 1003／passed 989／failed 14，**14 项失败名与 `r410_bgi_full_c2_20260919.trx` 逐名相同（差集为空）** | 两份未跟踪 TRX（保留不清理） |
| V6 | 本次**新跑**定向切片均绿：BGI `PhysicalSlotLedgerTests`+`DurableSubmissionIdentityStoreTests`+`TaskTakeoverIncidentTests` = **38/38**；助手 `CommandExecutorExternalStartAdmissionTests`+`TaskCenterExternalStartAdmissionTests`+`ClaimSurfaceGuardTests`+`R58DualRunContestTests` = **77/77** | `dotnet test -p:DeployToBgiTools=false`（本会话，未部署 User） |

## 3. 缺口与风险（分类 D 设计缺口／I 实现错误／T 既有测试失败／C 会诊工具失败）

**审计专用编号 A1–A9，不替代设计稿 D 号。**

| # | 类 | 风险 | 事实与代码入口 | 现有证据／缺失证据 |
|---|---|---|---|---|
| A1 | **I** | **P1** | 生产**未注入**外部启动准入：`MainViewModel.ApplyModeRuntime` 构造 `CommandExecutor` 只传 5 参，`externalStartAdmission` 默认 `null`。E3 `StartGroupAsync`／`StartOneClickAsync` 因此在委托为空时走 `StartGroupCoreAsync`／`StartOneClickCoreAsync`——该旧路径在 ext 队列 Ready 时**仍会提交带编号队列任务，但不经仲裁 owner 授权／优先级／抢占**；通道不可用才落 v2。E4 走旧热键核心；S4b `StartSpecifiedTaskViaAdmissionAsync` 委托为空时**响亮拒绝** `r5_external_start_admission_unwired` 且不回退 v2 | `MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs`（构造点）；`MultiplayerHoeingAssistant/Services/CommandExecutor.cs`（`StartGroupAsync`／`StartOneClickAsync`／`StartSpecifiedTaskViaAdmissionAsync`）。缺失：生产形态 E3/E4/E5 端到端贯通；对应设计 D1／E3 生产注入、D8 |
| A2 | **I** | **P1** | 节点后继提交改道在**生产关闭**：公共生产构造显式传 `admissionWired: true` 但**不传** `successorAdmissionWired`（默认 false），`CreateRunner` 需两门同时为真才包装 `ArbitrationWorkflowExecutionBoundary`。故 **E1/E2 入口走准入、节点后继仍走原执行边界** | `TaskCenterHost.cs`（公共构造与 `CreateRunner`）、`TaskCenterHost.Admission.cs`（`_successorAdmissionWired` 及「生产构造恒不传」注）。缺失：节点级端到端夹具（对应 §24.41-C#3/#12） |
| A3 | **I** | **P1** | 存在**绕过统一准入门**的生产提交点：联机全队批次直接 `ext.SubmitTaskStartAsync(..., preempt: true, idempotencyKey: item.RequestKey, coordinatedHoeing: true)`，自带请求键与房间授权，**不经过 `ArbitrationAdmissionService`** | `MultiplayerHoeingAssistant/ViewModels/MainViewModel.CoordinatedBatch.cs`。缺失：该路径与 owner「最高优先级互遇」合同的关系说明与交错夹具（D8） |
| A4 | **I** | **P1** | 「停止后实际退出」未闭合：`HandleTaskStop` 仅全局 `CancellationContext.ManualCancel()` 后回 `{ status = "stopped" }`，**不按执行实例／状态修订号核对、不产生退出或物理槽释放凭证**；`TryRequestPreempt` 未接线。消费侧确有把空闲当结束的真实点（如 `ExecuteHotkeyCoreAsync` 的 `running=false ⇒ confirmedStopped`；`WaitTaskSlotSettledAsync` 只接受 `executionIdle=true`） | `BetterGenshinImpact/Service/Instance/MessageHandlers/InstanceRequestHandler.cs`（`HandleTaskStop`）、`ExecutionScope.cs`、`CommandExecutor.cs`。缺失：`StopRequested`／`AlreadyExited` 分列、一次性退出确认票据、换实例/迟到 stop 反例（D6／D28／D11） |
| A5 | **I** | **P2** | 破坏性动作与部分入口**不经统一门**：`HandleCloseGame` 直接 `SystemControl.CloseGame()`，无根／信号量／槽准入；`ExecutionScope.Start` 另有 solo／脚本等入口 | `InstanceRequestHandler.cs`（`HandleCloseGame`）；`ExecutionScope.Start` 调用点：`TaskRunner.cs`、`ScriptService.cs`、`AutoTrackPathTask.cs`、`AutoTrackTask.cs`、`OneDragonFlowViewModel.cs`。缺失：共享排他域与副作用前准入（§24.86、D26/D27） |
| A6 | **D** | **P1** | 任务级优先级与运行中抢占合同未落地：生产事实源只有布尔占用，无法判定高／同／低；节点级 `PriorityOfNode` 仅被测试消费 | `TaskCenterHost.CurrentArbitrationFacts`、`TaskCenterMechanismPolicy.PriorityOfNode`（仅测试）。缺失：独立冻结「运行中比较／等待集合选择／恢复资格」纯函数与全格夹具（D5／D10） |
| A7 | **D** | **P2** | BGI 编号耐久受理事务未实现：现状仍是先入队/先执行后登记；`Prepared` 身份账独立且未接 `Submit`；`{槽预留＋受理＋原编号}` 未同一事务 | `BgiTaskCoordinator.Submit`／`SameSubmission`、未接线 `DurableSubmissionIdentityStore`。缺失：同键同载荷返原编号的权威查询与跨重启原键查证（D18／D26） |
| A8 | **D** | **P2** | 连续物理槽／继任预留与退出凭证未实现：`WaitSlotFreeAsync` 是「观察空闲→再取」，`_pending→_current` 后先发 `TaskStarted`／标 Running 再建根，实验 Ledger 不被任何操作持有 | `BgiTaskCoordinator.cs`、`PhysicalSlotLedger.cs`、`ExecutionScope.Start`。缺失：`Queued/Dispatching/Reserved/Executing` 分离与间歇插入反例（D14／D27）；`_current` 已置而根未建窗口 |
| A9 | **D** | **P3** | 抢占确认入口缺失：`PreemptConfirmPending` 已 fail-closed（普通续用／重试／占位零发送），但**无可信确认后继续入口**，故交接无法闭环；低级本地等待集合亦未实现 | `ArbitrationAdmissionService`（`PreemptConfirmPending` 检查、`ValidateAndOccupy`）、交接稿 owner 裁决。缺失：一次性证据绑定确认事务、等待登记与到期（§24.67#3／D23／D24） |
| T1 | **T** | 基线 | BGI 全量 14 项既有失败（AutoHoeing／WaitPointReport／OCR／AutoPathing）**身份与旧基线逐名相同**，非本批引入，仍**不称全绿、不豁免** | V5 |
| C1 | **C** | — | **本批无会诊工具失败**：两次只读会诊均成功（见下节），无重试 | — |

## 4. 会诊记录（只读，未改代码）

| 次序 | 模型／强度 | 次数 | 结论要点 |
|---|---|---|---|
| 1（常规路径核实） | **GPT-6-SOL／medium** | **1 次成功（attempts=1）** | 独立确认 A1／A2／A4 与 A3；纠正「E3 旧路径一律 v2」的说法（ext 队列可用时仍带编号提交，只是不经 owner 授权） |
| 2（安全敏感：停止退出与无双跑） | **GPT-6-Astra／medium** | **1 次成功（attempts=1）** | 确认「stop 回执／逻辑根 Dispose 均非退出凭证」「空闲观察与派发非原子」「`running=false` 被用作结束判断」；补充 A5（`HandleCloseGame` 绕过统一门）；同时明确**否定**「两个遵守 `Scope` 生命周期的同进程根会因轮询窗口同时准入」（`Start` 有进程内互斥） |

两次均为静态只读分析，**不代替代码或实机验收**；未引入任何未获授权的模型降级。

## 5. 风险优先级与下一批建议（单一状态转换）

**优先级**：P1 = A1／A2／A3／A4／A6（无双跑与统一授权的真实前置）；P2 = A5／A7／A8；P3 = A9；T1 单列基线。

**下一批最小目标（推荐）＝ A4 的第一步：BGI「定向停止 + 退出确认」单状态转换（D6／D28 第一转移），反例先行。**

- 红夹具（先红后实现，`ExecutionScope`／handler 层）：
  1. A 自然完成后 B 已起步，**迟到的 A-stop 携旧 `{epoch, executionInstanceId, stateRevision}` 不得取消 B**，且回 `already_exited`／`not_current` 而非 `stopped`（当前全局 `ManualCancel` 会红）。
  2. 身份匹配的停止只回 `stop_requested`／`StopRequested`，**随后 `task.status` 在真实退出前不得报 `executionIdle`**（退出凭证据此后单列）。
  3. 旧 epoch 的退出回执**不授权新 epoch 提交**。
- 完成判据：上述三支红转绿；失败身份与 `r410_bgi_full_c2_20260919.trx` 比对不新增；`ClaimSurfaceGuardTests` 若动声明面须 `CLAIM_SURFACE_REGENERATE=1` 再生并复会诊（GPT-6-Astra／medium）。
- 备选（若 owner 更看重接线）：A1 的 E3 生产组合根注入——但需先冻结 A6（任务级优先级事实）合同，否则门开而无正确判定依据，不推荐先做。

## 6. 门禁与边界（保持关闭）

- 生产 E3/E4/E5、节点改道、S4b/S8b 开关与真实 User 目录门**全部继续关闭**；本批未开任何门、未改产品代码。
- 单测／完整回归／实机证据**分列**：本批只有**定向单测**为本次新跑（V6），完整回归引用**既有 TRX**（V5），**实机证据为零**。
- 不得据本稿宣称 R5 完成；R5.8 无双跑验收单仍未签署。

## 7. 下一会话可直接粘贴的提示词

```text
承接 E:\Program Files\better-genshin-impact-LCB 的「槲寄生调度器 R5 收口」，先读
Docs/design/onedragon-r5-next-session-2026-09-24.md 与
Docs/design/onedragon-r5-independent-audit-2026-09-24.md（本批审计结论与证据），
再核对实际分支、HEAD、工作区与 TRX，不直接相信任何快照。

本会话只做「A4 第一步」：BGI 定向停止 + 退出确认单状态转换（D6／D28 第一转移），反例先行。
红夹具：①A 自然完成、B 起步后迟到 A-stop 不得误停 B，且不回 stopped；②身份匹配停止只回
stop_requested，真实退出前 task.status 不得报 executionIdle；③旧 epoch 退出回执不授权新 epoch 提交。
先写红夹具→实现到绿→针对性回归→按失败用例身份比对全量基线→GPT-6-Astra／medium 差异复审→
无必改项后仅提交本批文件（git commit --only）。

约束：不改第三方 JS、不动真实 User 目录、生产入口与真实 User 门保持关闭、R5.8 未签署前不得报告
R5 完成；保留全部未跟踪文件；声明面变化须 CLAIM_SURFACE_REGENERATE=1 再生清单并复会诊。
为本批次新设 GOAL；约 45 分钟或每提交报一次证据进度，约 90 分钟或两笔提交开始交接。
```
