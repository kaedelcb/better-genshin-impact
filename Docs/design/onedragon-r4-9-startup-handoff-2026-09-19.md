# R4.9 启动移交设计稿（2026-09-19，一轮会诊修订版）

承接《onedragon-r4-implementation-breakdown-2026-09-19.md》R4.9 行与 D1 定案（B4 修订）：
允许改造**移交适配边界**；StartupFlowRunner 节点目录/分支模型/拖拽编辑器框架不动。

## 1. 范围

启动中心「进入任务中心执行」节点从空转占位改造为真实移交：
带身份提交 → 任务中心宿主**受理入口**结构化回执 → 语义三选一 → 成功移交后终止当前启动链（含分支回溯）。

不在范围：UI/热键/CLI/v2/ext/网页全入口仲裁（R5）；任务中心内部执行语义（R4.1–R4.8 已定）。

## 2. 移交适配边界（接口合同，一轮 B1/B3 修订）

```
StartupHandoffRequest  = { ExecutionId, StepId, TriggerKind, TriggerInstanceId, FireDate,
                           IntentKey, WorkflowId, Mode, IntentNote }
StartupHandoffResult   = Accepted(runId) | AlreadyAccepted(runId) | Rejected(reason, reasonCode, terminateChain)
委托                  = Func<StartupHandoffRequest, CancellationToken, Task<StartupHandoffResult>>
```

**双重身份（一轮 B1）**：
- `ExecutionId`：每次 `RunAsync` 调用生成的 GUID——一次启动链执行的身份（四入口并发共存，参数透传，不用共享字段）。
- `IntentKey`：**业务计划出现身份**（跨 ExecutionId 的去重键）——
  - 定时/电子狗/日志触发入口：`"{TriggerKind}:{TriggerInstanceId}:{FireDate:yyyy-MM-dd}"`（同一触发器同一次日程出现，无论从哪个入口到达、重试多少次，共享身份）；
  - 手动/自动主流程执行：`"manual:{ExecutionId}"`（用户明确每次执行=新意图，不拦有意重复）。
- 台账查找顺序：IntentKey 命中 → 内容核对（workflowId/mode 一致 → AlreadyAccepted 附原 runId 与当前状态；**不一致 → Rejected 身份冲突**）；未命中 → 新受理。

## 3. 宿主受理入口（一轮 B2/B3 修订）

宿主新增 `RegisterHandoffAsync(request, ct)` ——**受理点 = RunStore 落盘**，不发明独立台账文件：

1. `_gate` 内：关闭检查 → 本地执行能力守卫（§6.2）→ 台账查询（RunStore 按 `Handoffs[].IntentKey` 查全部记录——二轮 B1 多绑定修订，十轮建议3 同步本文）→ 命中按 §2 回执；
2. 预检（就绪/同流程互斥/Unknown/候选/隔离/mode 语义预检）失败 → **Rejected 不留运行记录**；
3. `CreateRun`（携带首条 `Handoffs[]` 绑定：IntentKey/ExecutionId/StepId/TriggerKind/Mode——二轮 B1/B2 多绑定：resume/arm 幂等挂载原子追加，绝不替换旧绑定）落盘 = **持久化受理提交点**，runId 直接来自该记录（绝不倒推查询）；
4. 驱动激活（复用 Start/Resume 驱动路径）；激活后执行失败只更新运行状态（Failed 可见于运行状态卡），**不撤回受理**（受理≠执行成功，回执文案区分）；
5. resume 模式：不 CreateRun，绑定既有运行（最新一条 Interrupted/Paused；重放同 IntentKey 直接 AlreadyAccepted 不重选）。

**回执结构**：`HandoffRegisterResult { Outcome, RunId, Reason, ReasonCode, CurrentRunState }`；
`Outcome = Accepted / AlreadyAccepted / Rejected`。启动链 CTS 仅覆盖受理点之前；受理提交后任务归宿主（启动链取消不撤销已受理运行）。

**台账 = RunStore 记录的 `Handoffs[]` 绑定列表**（一轮 B2 设立、二轮 B1 改多绑定）：受理记录随运行记录永存，无 FIFO 淘汰权威依据问题；
崩溃窗口（CreateRun 后驱动激活前崩溃）→ 恢复扫描标 Interrupted（无提交意图，不自动重跑）；
同 IntentKey 重放 → 查到 Interrupted/Paused → **AlreadyAccepted**（返回该 runId，提示可显式恢复，禁止自动换键重跑）；
终态 → AlreadyAccepted（该计划出现已完结）。

## 4. 语义三选一（一轮 B4 修订：精确口径）

| Mode | 宿主行为 | 已有运行时的回执 |
|---|---|---|
| `start`（立即执行） | StartWorkflowAsync 语义（遵守流程既有触发器：有 trigger.time → Waiting 等待不占槽位；无 → 立即推进） | 活动/Unknown → Rejected（原因可读） |
| `resume`（恢复既有运行） | 绑定最新（UpdatedAt 最大）Interrupted/Paused 运行 → ResumeAsync | 无可恢复记录 → Rejected；Unknown 存在 → Rejected（需对账）；同 IntentKey 重放 → AlreadyAccepted 不重选 |
| `armTrigger`（挂载流程触发器） | 同 start 路径（流程须带 trigger.time，否则 Rejected「流程无可挂载触发器」；四轮 重要5：结构性循环首轮立即执行——DriveAsync 轮次等待仅 LoopIteration>0 生效——不算可挂载，防止 arm 绕过混用/快照守卫却立即提交） | 存在 **Waiting 且有待触发时刻**的运行 → AlreadyAccepted（真·幂等挂载）；其他活动态（Planned/Running/Completing/Paused）→ Rejected「已有非挂载中的活动运行」（不假报已挂载）；Unknown → Rejected 优先 |

未知 mode 值 → **Rejected**（未支持执行语义，不回落 start）；缺省（旧配置无字段）= `"start"`。
Waiting 不占 BGI 执行槽（执行资源）与同 workflowId 运行唯一性（流程互斥）是两个维度，互不矛盾。

## 5. 节点参数（一轮 I9 修订）

- `TaskCenterFlowId`（JSON `taskCenterFlowId`，默认 ""）：目标流程稳定身份；空 → Rejected(`ConfigMissing`)「未配置目标流程」，节点 Failed、链继续（旧配置不炸）。
- `TaskCenterHandoffMode`（JSON `taskCenterHandoffMode`，默认 `"start"`）：start/resume/armTrigger；显式未知值 → Rejected（不静默改语义）；缺失 → 默认 start。
- 编辑器：参数区两行——流程下拉（显示名称/保存稳定身份；候选来源宿主 ListFlows 的 active 条目，**仅选择体验，执行时宿主权威校验**；目标缺失/隔离保留原 ID 显示「不可用」，不自动改选）+ 语义下拉。方案保存/导入导出经既有整对象序列化往返（夹具验证字段不丢）。
- 节点目录描述更新（去占位字样）。

## 6. 冲突与守卫（一轮 B5/I8 修订）

### 6.1 混用检测（两层证据，快照不作授权依据）

- **启动链自身事实（主证据）**：Runner 执行上下文逐执行记录本链内 StartGroup/StartOneClick/带任务参数 StartBgi 的**提交事实**（成功或结果不确定均计）；移交时本执行已有提交事实 + mode=start/resume → Rejected(`MixedUsage`)「本启动链已直接提交 BGI 任务（节点「X」），与任务中心移交混用，请二选一」。
- **执行端快照（辅助）**：LatestLocalStatus 显示 BGI 任务在跑（非本执行提交）+ mode=start/resume → Rejected(`BgiBusy`)含当前任务名；**快照不可考（无快照）→ start/resume 一律 Rejected(`StatusUncertain`)**（授权判断必须可考，锚点 3）；armTrigger 不受快照/混用限制（挂载等待不占槽位，到点执行由引擎边界权威裁决）。
  三轮 B1 修订：快照判定**在宿主内、台账查询之后、仅对未命中新受理执行**（受理事实优先——同 IntentKey 重放即使快照忙也回 AlreadyAccepted）；VM 层不做快照预拒。快照提供方生产必传（MainViewModel LatestLocalStatus）。
- 边界纪律：快照可能陈旧——BGI 侧受保护提交/占用机制是最终权威，本层是显式前置留痕，不替代执行边界。

### 6.2 本地执行能力守卫（宿主服务边界）

宿主构造注入 `Func<bool> localExecutionCapability`（生产 = MainViewModel.IsExecutorMode；监控端 false）：
移交入口与 Start/Resume 公共就绪判定统一检查，**先于台账写入与任何副作用**；VM 层仅提前提示，不以请求自声明为准（绕过 VM 的调用同样被拦，夹具验证）。

## 7. 终止链与回报（一轮 I6/I7 修订）

- **Accepted/AlreadyAccepted** → 节点 Report Success 一次（附 runId + 「已移交，任务中心接管（受理≠执行成功）」/「此前已受理同一计划出现」）→ 抛 `HandoffCompletedException`（ExecuteActionAsync 明确重抛，仅最外层 RunAsync 捕获）逐层终止当前启动链及全部分支回溯——**后续节点（含 StopBgi）一律不执行**。
- **Rejected** → 节点 Report Failed 一次（单处回报，原因可读）→ 链策略按原因码：
  - `ConfigMissing`（旧配置未配置目标）→ **链继续**（兼容语义，同动作失败）；
  - `MixedUsage` / `BgiBusy` / `StatusUncertain` / `AlreadyRunning` / `Unknown` / `IdentityConflict` / 受理事实未决 → **终止本次启动链**（防止继续执行 StopBgi 破坏宿主管理的在册运行；A 接受 B 被拒后 B 不得杀 A 的任务）；
  - 其他（流程缺失/隔离/候选/无触发器/能力守卫）→ 终止本次启动链（移交是链的明确意图终点，拒绝后继续执行残余节点无意义且危险）。
- 已挂载后台触发器（定时/电子狗/日志）生命周期不变（各自 Cts 独立）；同一触发器同一日程日（yyyy-MM-dd）的多次触发共享 IntentKey（按同一计划出现去重，保守口径）；跨天=新日程出现=新 IntentKey。定时触发取计划触发时刻的日期（跨零点阻塞不漂），电子狗/日志取事件发生时刻。
- 日志与节点回报附 ExecutionId 短码（并发执行可区分；NodeStateSink 显示覆盖为已知限制，入档）。

## 8. 验收口径（按不变量矩阵，一轮 S11）

| # | 场景 | 必须证明 |
|---|---|---|
| 1 | 同 IntentKey 受理后重放 / 并发双提交 | 同 runId AlreadyAccepted / 仅一运行 |
| 2 | 同 IntentKey 不同 workflowId/mode | Rejected 身份冲突，不返回旧 runId |
| 3 | 同触发器同日两入口（不同 ExecutionId） | 不重复提交（IntentKey 去重） |
| 4 | 手动再执行（新 IntentKey，前次终态） | 允许新运行（有意重复不静默删） |
| 5 | 受理点崩溃窗（CreateRun 后未驱动） | 重放 → AlreadyAccepted 不新建不换键 |
| 6 | arm 遇 Waiting(有待触发) / Paused / Unknown / 无触发器流程 | AlreadyAccepted / Rejected / Rejected / Rejected |
| 7 | resume 绑定最新 Interrupted；重放不重选 | 同一 runId |
| 8 | 多层条件分支内移交成功 | 子链剩余+全部外层后继不执行 |
| 9 | A 接受 B 被拒（B 后有 StopBgi） | B 终止链，不破坏 A 的运行 |
| 10 | 旧配置无参节点 / 未知 mode | Rejected 链继续不炸 / Rejected 不静默回落 |
| 11 | 本执行已提交 BGI 任务（成功或不确定）+ start | Rejected MixedUsage（不依赖快照） |
| 12 | 快照不可考 + start / 监控端直接调宿主 | Rejected StatusUncertain / Rejected 能力守卫 |
| 13 | 方案保存/导入导出往返 | 新字段不丢 |

## 9. ASTRA 会诊记录

### 一轮（方案会诊，2026-09-19，6 文件 allowlist）——不宜直接定案：5 阻断/5 重要/2 建议，处置：

| # | 发现 | 处置 |
|---|---|---|
| B1 | ExecutionId:StepId 不足以证明多触发源不重复提交同一计划；缺业务意图身份 | 采纳：§2 双重身份（ExecutionId + IntentKey 计划出现身份；触发器=种类:实例:日期窗口，手动=每次新意图）；同键内容不符 Rejected 冲突；TriggerKind 不入键（避免换入口绕过去重） |
| B2 | 启动后登记不耐崩溃；200 条 FIFO 主动删去重依据 | 采纳：§3 受理点=RunStore 落盘（CreateRun 携带 HandoffIdentity 持久化字段，台账=RunStore 无淘汰）；崩溃窗恢复 Interrupted → 重放 AlreadyAccepted 不换键重跑 |
| B3 | HostActionResult 无 runId 不能兑现 Accepted(runId)；Registered≠受理语义；关闭竞态 | 采纳：§3 RegisterHandoffAsync 受理入口（受理点持久化准确 runId；激活失败不撤回受理；CTS 仅覆盖受理点前；_shutdown 检查在锁内 CreateRun 前） |
| B4 | 三语义未完全区分；armTrigger AlreadyAccepted 过宽（Paused/Completing 假挂载）；未知 mode 回落 start | 采纳：§4 精确口径（arm 仅 Waiting+待触发时刻才算已挂载；其他活动态 Rejected；Unknown 优先；未知 mode Rejected 不回落） |
| B5 | 10s 快照不足以授权混用判断 | 采纳：§6.1 两层证据（启动链提交事实为主证据；快照不可考 start/resume 一律 Rejected；BGI 边界为最终权威） |
| I6 | HandoffCompletedException 会被通用 catch 吞掉；回报两处重复 | 采纳：§7 ExecuteActionAsync 明确重抛+仅外层捕获；Success/Failed 单处回报 |
| I7 | Rejected 一律继续会让 B 的 StopBgi 杀掉 A 的运行 | 采纳：§7 原因码分类（ConfigMissing 继续；其余一律终止本次链） |
| I8 | 监控端守卫应在宿主服务边界非 VM 层 | 采纳：§6.2 宿主注入能力提供方统一检查，先于副作用 |
| I9 | 加法字段零破坏需限定；编辑器不止两行控件 | 采纳：§5 显式 JSON 名/缺失默认/未知拒绝/目标缺失保留原 ID 显示不可用/往返夹具 |
| S10 | 并发执行显示覆盖需可区分 | 采纳：§7 日志与回报带 ExecutionId 短码，限制入档 |
| S11 | 验收应按不变量矩阵 | 采纳：§8 十三场景矩阵 |

### 二轮（Batch A 途中会诊，2026-09-19，7 文件 allowlist）——不宜直接验收：4 阻断/7 重要/2 建议，处置：

| # | 发现 | 处置 |
|---|---|---|
| B1 | resume 覆盖身份删除旧意图去重依据（A 重放会重建运行） | 采纳：运行记录改**追加式多绑定** `handoffs[]`（不可变绑定，resume/幂等挂载原子追加绝不替换）；台账查询返回运行+命中绑定，内容核对按该绑定 Mode；新夹具 Resume_AppendsBinding_OldIntentStillDeduped |
| B2 | arm 命中既有 Waiting 不登记本次 IntentKey（完结后重放会新建；同键改投无法识别冲突） | 采纳：幂等挂载成立前把本次身份原子追加到在等运行（落盘失败不得报成功）；补夹具 挂载→完结→重放 / 挂载→同键不同内容冲突 |
| B3 | 权威预检在受理后（不可执行流程消耗 IntentKey 还报 Accepted）；resume 无 candidate 守卫 | 采纳：WorkflowRunner.PreflightStartable 公开预检；start/resume 均在受理落盘前完成 组装+流程可用性+候选+计划预检——失败 Rejected 不消耗 IntentKey 不留记录（夹具 AssemblyFailure_Rejected_BeforeAcceptance_NoIntentBurned + Resume_CandidateReadyFlow_Rejected_BeforeStamping） |
| B4 | LaunchDrive 先启动任务后查关闭——关闭竞态下驱动未登记无人观察 | 采纳：关闭竞态路径转 ObserveOrphanAsync 册外观察收敛（取消令牌不证明远端已停）；DriveEntry 增加准确 RunId（移交路径），收敛按准确身份而非倒查；回执状态取已确认记录；删除 MarkAcceptedRunFailed（其仅剩的调用点会把可能有副作用的运行误标 Failed） |
| I1 | 异步异常不进同步 catch，收敛与回执不一致；Planned 无驱动僵死条件 | 采纳：随 B3/B4 结构性消除——预检前置后受理点之后只有驱动执行失败（观察器收敛 Unknown/Interrupted，无 Planned 无驱动路径）；LaunchDrive 注释更正（异步入口异常进 Task） |
| I2 | 能力守卫晚于恢复扫描（监控端先产生写副作用）；提交点未复核；生产注入不可证明 | 采纳：三个公共入口（Start/Resume/RegisterHandoff）关闭+能力预检提到恢复屏障之前，最终临界区再复核；生产构造 localExecutionCapability 改必传参数（MainViewModel 接线 IsExecutorMode），测试放行只留内部接缝 |
| I3 | 双检晚于就绪/预检，不能稳定兑现台账优先；取消检查离提交点远 | 采纳：最终临界区统一顺序 关闭→能力→台账双检→就绪→取消检查→互斥→预检→受理落盘→预留；锁外预检拒绝返回前经 RejectedWithLedgerRecheck 复核台账；夹具 Cancelled_BeforeAcceptance_NoRun |
| I4 | 恢复扫描重写 Interrupted 记录导致 UpdatedAt 重排 resume 候选；并建议前置未决一并标 Unknown | 部分采纳：Interrupted 幂等保持（不重写不留痕，夹具 RecoveryScan_Idempotent_DoesNotRewriteInterrupted + resume 排序回归）；**前置未决标 Unknown 回退不采纳**——R4.6 已验收合同是「前置在飞 → Interrupted，恢复时 ReconcileAsync 对账」（RecoverOnStart_PrerequisiteInFlight_InterruptedThenResumeReconciles 回归证明），HasUnresolvedExternalFact 不含前置子句并注释定案 |
| I5 | 台账查询把读不到记录当成没有受理（坏文件跳过后可能重复受理） | 采纳：RunStore.QueryHandoffLedger 三态（命中/确定未命中/不完整——存在无法解析文件即不完整），不完整拒绝新受理（LedgerIncomplete，终止链）；命中优先（该键受理事实已证实）；夹具 LedgerIncomplete_Rejected_NewIntentBlocked |
| I6 | 空 IntentKey 归 ConfigMissing 会被链继续放行 | 采纳：空 IntentKey/ExecutionId 改 HandoffError（终止链）；ConfigMissing 仅用于未配置目标流程；夹具 EmptyIntentKey_Rejected_TerminatesChain_NotConfigMissing |
| I7 | 夹具未证明关键不变量（真实扫描崩溃窗/arm 挂载断言/Resume 守卫/故障注入/并发屏障） | 采纳：崩溃窗改 Planned+真实恢复扫描；arm 新挂载断言 Waiting+NextTriggerAt+零提交；能力夹具补 ResumeRunAsync；新增驱动故障收敛（SubmitThrows→Unknown 按准确 RunId）；并发经锁内双检确定性覆盖（前置锁外查询+锁内复核两条路都已夹具） |
| S1 | 设计内部歧义：§2 命中即 AlreadyAccepted vs 代码 Unknown 拒绝；TriggerKind 入键与否 | 采纳定案：**台账命中=受理事实存在，Unknown 也 AlreadyAccepted**（附状态与对账提示，不重新驱动不新建；未命中请求遇同流程 Unknown 才拒绝）——§2 为准，已同步 §4 表格注记；IntentKey 格式={触发器种类}:{实例}:{日程}（§2 为准，B1 注的「入口」指 RunAsync 调用入口非触发器种类） |
| S2 | 崩溃原子性边界未写明；Persist 写入失败后内存对象携带未提交修订 | 采纳：验收边界=进程崩溃（同目录临时文件+替换，不含系统掉电 fsync）；Persist 失败恢复内存对象修订/时间戳 |

### 三轮（Batch B 终审，2026-09-19，8 文件 allowlist）——不通过：2 阻断/8 重要/5 建议，处置（全部采纳并回归 328/328）：

| # | 发现 | 处置 |
|---|---|---|
| 阻1 | VM 快照预检先于台账，遮蔽已受理事实（busy/不可考时重放拿不到 AlreadyAccepted） | 采纳：快照判定移入宿主（TaskCenterHost.SnapshotPrecheck），LedgerGate 之后仅对未命中新受理执行，拒绝走台账复核；快照提供方生产必传；夹具 SnapshotBusy_LedgerHitReplay_StillAlreadyAccepted + Busy 新意图/不可考/arm 绕过 |
| 阻2 | 候选 Clear+TwoWay SelectedValue 刷新瞬间可把 null 回写配置（FlowUnavailable→ConfigMissing 策略漂移） | 采纳：增量刷新（先补后删，选中身份全程不失配）+ setter 拒 null 纵深防御 + BuildFlowChoiceList 纯函数夹具；WPF STA 绑定级验证记入 R4.10 集成验收限制 |
| 重3 | 委托回 ConfigMissing 时 Failed 双重回报 | 采纳：TerminateChain=false 路径不在 ExecuteHandoffAsync 内 Report，由 RunChainAsync 经 FailureNote 单处回报；夹具断言恰 1 次 + 后续普通失败节点不继承原因 |
| 重4 | 提交事实只计 success/异常，返回式不确定漏记 | 采纳：三分法（success→计；failed 无 ErrorCode→不确定计入，依据 CommandExecutor at-least-once 合同；failed+ErrorCode→BGI 权威业务拒绝确定未提交不计）；补 StartOneClick 返回式/业务拒绝夹具 |
| 重5 | 空目标短路掩盖显式未知 mode（Runner/Host 顺序不一致） | 采纳：Runner 校验顺序与宿主一致（未知 mode 先于空目标）；夹具 EmptyFlowAndUnknownMode_UnsupportedModeWins |
| 重6 | 定时器用执行日而非计划日构 IntentKey | 采纳：定时取 timer.NextFireAt 计划日；OccurrenceDate 固定文化（InvariantCulture）；电子狗/日志取事件发生时点并注释 |
| 重7 | ExecutionId 短码未进节点回报 | 采纳：移交 Success/Failed 回报附执行短码；普通节点不刷格式（既有显示覆盖限制入档，R4.10 面板侧核） |
| 重8 | 流程下拉闭合态可能显示 record 文本 | 采纳：TaskCenterFlowChoiceItem 重写 ToString()=Display（同 SchemeItemViewModel 模式） |
| 重9 | RejectedWithLedgerRecheck 无能力复核；预检后落盘前无取消复核 | 采纳：快返回复核补能力检查（顺序同最终临界区）；start/resume 受理落盘前再查取消；夹具 CapabilityRevoked_DuringSnapshotRejection_RecheckWins |
| 重10 | 监控端宿主构造/只读列表零副作用缺证据 | 采纳：夹具 CtorAndListFlows_ZeroFileSideEffects（构造+ListFlows 不创建目录） |
| S1 | HasBgiTaskArgs 全串子串误伤 | 采纳：对齐 BGI 解析只看首 token（startOneDragon 子串/--startGroups/--TaskProgress 精确） |
| S2 | 两参数同一 WrapPanel 非确定两行 | 采纳：拆两个 FieldGroup 行 |
| S3 | 未知 mode 编辑器显示「立即执行」误导 | 采纳：getter 返回 -1 无选中（setter 负值守卫不回写）；摘要显示「未知语义」 |
| S4 | Accepted 回报丢宿主 Reason | 采纳：回报保留宿主回执说明 |
| S5 | 嵌套夹具 00:00-23:59 依赖运行时钟 | 采纳：改 ManualConfirm 恒 true 确认器，并补中间层后继不执行断言 |

### 四轮（三轮处置验证 + 阶段终审，2026-09-19，10 文件 allowlist）——无新阻断：3 重要遗漏/3 证据缺口/4 建议，处置（回归 330/330）：

| # | 发现 | 处置 |
|---|---|---|
| 重1 | 日志触发用消费时刻构 IntentKey，跨日排队改变同一事件身份 | 采纳：ArmedLogTriggerViewModel 命中记录携带事件发生时刻（HitRecord(Line, OccurredAt)），消费取事件日；合并命中取最后一次事件（摘要与日期同源） |
| 重2 | 增量刷新不更新同 ID 展示（改名/可用性变化不反映） | 采纳：TaskCenterFlowChoiceItem 改可通知对象（WorkflowId 不可变 + Display INPC 原位更新，对象身份保留不失配） |
| 重3 | arm 幂等挂载分支落盘前无取消复核；resume 复核离身份修改不够近 | 采纳：挂载追加前 + resume fresh 状态复核后各补 ct 检查（贴近身份修改点） |
| 重4 | ErrorCode 非空是否足以证明「整个调用未提交」 | 已查证闭环：CommandExecutor 合同——连接建立后传输失败不重试（at-least-once 注释明示）；start_group 的「重试一次」仅在 Connect 失败（BGI 未运行=确定未送达）后裸拉起场景。failed+ErrorCode（BGI 业务信封）= 权威业务拒绝=确定未提交，合同成立 |
| 重5 | armTrigger 承认 loop 可挂载，immediate/scheduled 首轮立即执行绕过守卫 | 采纳：HasMountableTrigger 收窄为仅 trigger.time（DriveAsync 证据）；夹具 ArmTrigger_LoopOnlyFlow_Rejected_NoTrigger |
| 重6 | 并发屏障/驱动中追加绑定持久化证据不足 | 部分采纳：引擎写状态路径查证为 Load→改→Update 整对象写回（写前重新 Load——已由七轮重要2/八轮建议3 更正：引擎实为在飞期间持有加载对象写回），追加绑定不丢；夹具 HandoffBindings_SurviveEngineStyleLoadModifyUpdate。强制双 Miss 屏障竞态：锁内台账双检已确定性覆盖（首查 Miss+锁内复核 Hit 路径有夹具），并发 Task.WhenAll 夹具保留为烟测 |
| 建1 | ConfigMissing 链继续回报缺执行短码 | 采纳：FailureNote 附短码；夹具断言 |
| 建2 | 摘要空目标+未知 mode 只显示前者 | 采纳：两项问题同显（未知语义优先级与执行一致） |
| 建3 | 零副作用夹具名大于证明范围 | 采纳：改名 CtorAndListFlows_DoNotCreateMissingDirs 并收窄注释 |
| 建4 | 过时注释/Waiting 夹具宿主未关/23:59 时钟依赖 | 采纳：VM 移交摘要更新；SnapshotUnavailable 夹具补 ShutdownAsync；触发器种子改动态 +2h |

### 五轮（四轮处置验证 + 关闭判定，2026-09-19，11 文件 allowlist）——暂不关闭：重1 竞态未闭环/重6 证明缺口/矩阵1 证据缺口 + 3 收尾，处置（回归 334/334）：

| # | 发现 | 处置 |
|---|---|---|
| 重1 | 信号量与命中记录脱节：合法交错可凭空产生消费日兜底身份（跨日漂移） | 采纳：HitSignal 信号量 → 容量 1 通道（DropOldest，记录与通知同通道传输，结构性消除「有信号无记录」）；合并取最后一次事件；删除 TakeHit 兜底；夹具 LogTriggerHit_MergedHits_ConsumeLastEvent_NoPhantomIdentity |
| 重6 | 追加绑定并发写回交错（引擎旧对象覆盖丢绑定）证明缺口 | 采纳查证+收敛：RunStore.Persist 全盘修订守卫（RunRecordConflictException）——旧对象写回响亮冲突、受理事实不丢、状态不倒退（夹具 StaleWriterConflict_*）；宿主两个追加路径改 TryAppendHandoffBinding 原子读改写重试（有界 3 次，重读收敛，绑定不重复追加）；残余窗口=引擎在飞写回撞上宿主追加→旧对象写回响亮冲突、受理事实保全（七轮 重要2 更正：非偶发毫秒窗、收敛态为 Interrupted 非 Unknown——普通触发等待无未决外部事实；可显式恢复，已知限制入档） |
| 矩阵1 | 「锁内双检已确定性覆盖」超出夹具（无屏障强制双 Miss） | 采纳：Concurrent_SameKey_DoubleMissBarrier_SingleAcceptance（快照提供方内 Barrier(2) 强制双 Miss → 锁内双检 → 1 Accepted+1 AlreadyAccepted 同 runId 单运行单次驱动提交） |
| 收尾1 | ErrorCode 空串会落入「业务拒绝不计」 | 采纳：IsNullOrWhiteSpace 收紧（空白=无有效信封按不确定计入） |
| 收尾2 | GoldComboBox 闭合态实时刷新未证明 | 记录：ToString=Display 与 SchemeItemViewModel 既有模式一致；STA 绑定验证留 R4.10 |
| 收尾3 | 旧注释（I4 摘要 Unknown 误记/ HitSignal 引用/基线数） | 采纳：已全部更正 |

### 六轮（五轮处置验证 + 关闭复核，2026-09-19，11 文件 allowlist）——暂不关闭：4 重要/2 建议，处置（回归 340/340）：

| # | 发现 | 处置 |
|---|---|---|
| 重要1 | arm/resume 回执在绑定提交后重读盘上记录组装：记录被并发删除时 NRE；引擎已推进时状态文案失真（把已受理假报为异常） | 采纳：TryAppendHandoffBinding 改 internal static（注入 load/update 委托 + out committed 提交时快照）；arm/resume 两调用点回执一律用提交时快照，不再重读——「追加成功时状态确为所报」（已由七轮重要1/八轮重要1-2 补全：本条仅覆盖挂载/绑定追加路径，受理后回执刷新与通知两条残余来源见其处置） |
| 重要2 | arm 路径 ConvergeDriveException（驱动在飞修订冲突）被吞即按挂载成功回报，留假挂载窗口 | 采纳：改重读重判有界重试（3 次），冲突重试、其他失败响亮留痕；注释同步更正（前置在飞按 R4.6 合同走 Interrupted 而非 Unknown） |
| 重要3 | Concurrent_SameKey_DoubleMissBarrier 夹具假阳性：两请求可串行退化成单 Miss、Barrier 超时静默放行、Delay(50) 不可靠 | 采纳重写：先 EnsureRecovered 排除首调用恢复抢跑；双 Task.Run 独立调度；Barrier(2) 超时即夹具失败（抛 TimeoutException 不放行）；断言 providerCalls==2 作双 Miss 直接证据；等运行终态后断言单次驱动提交（去 Delay） |
| 重要4 | TryAppendHandoffBinding 冲突重读重试路径无直接夹具（仅经并发夹具间接覆盖） | 采纳：静态助手 4 夹具确定性驱动——冲突后重读成功单次追加+committed 即落盘版本+并发留痕不丢、重读后 stateGuard 拒绝不追加（「不预留」措辞已由七轮建议2 更正——预留属宿主调用方）、重试间取消 OCE 响亮传播不追加、连续 3 次冲突有界放弃 HandoffError 绑定数不变 |
| 建议5 | 日志触发器无命中时等待读取的取消无夹具 | 采纳：LogTriggerHit_PendingRead_Cancellable（ReadAsync 响应令牌 OCE、取消不产生幻影记录） |
| 建议6 | 注释残留（RunLogTriggerAsync/OnLogEntry 旧信号量 Release/WaitAsync 口径、StartupStepKinds.EnterTaskCenter「占位」字样、基线数）+ ErrorCode 空白串夹具缺口 | 采纳：注释全部更正为通道口径；MixedUsage_BlankErrorCode_CountsAsUncertain（空白 ErrorCode 与 null 同口径按结果不确定计入） |

### 七轮（六轮处置验证 + 关闭复核，2026-09-19，11 文件 allowlist）——暂不关闭：4 重要/3 建议，处置（回归 345/345）：

| # | 发现 | 处置 |
|---|---|---|
| 重要1 | resume/start 回执仍有受理后 _runs.Load：读取抛 IOException/JsonException 时经 VM 映射为 Rejected(HandoffError)，把已受理假报为异常 | 采纳：新增 TryReadRunState 尽力刷新（读取失败/记录缺失→null 不抛出，internal 供夹具直证）；start 回执回落受理提交时快照（Planned）、resume 回执回落 committedBind 提交时快照（提升作用域）；夹具 TryReadRunState_CorruptFile_ReturnsNull_NoThrow |
| 重要2 | 向真实 Waiting 驱动追加绑定必然使其持有记录过期（结构性必然，非偶发毫秒窗）；五轮「响亮 Unknown 收敛」记录不准——普通触发等待无未决外部事实应收敛 Interrupted | 采纳：真实驱动夹具 ArmMount_AppendToRealWaitingDriver_StalesIt_ConvergesInterrupted_BindingsPreserved（进 Waiting→追加第二意图→暂停释放等待→Pause 写回修订冲突→观察器收敛 Interrupted 非 Unknown、绑定全保留、零提交、驱动出册不留假挂载）；五轮重6 记录同步更正；限制准确入档：追加挂载身份使在等驱动下一次写回冲突并收敛 Interrupted（受理事实不丢、可显式恢复）——元数据追加与执行推进共用整记录修订的已知代价；长期方向=分离绑定元数据并发版本/受控记录变更接口（挂账 R5+） |
| 重要3 | arm 前提未跨越「受理→驱动重载定义」窗口：StartExistingRunAsync 重载快照 B，触发器已被移除时 AwaitFlowTriggersAsync 直接返回并推进执行——arm 绕过混用/快照守卫直接提交 | 采纳：StartExistingRunAsync 新增 armTriggerLaunch 参数（宿主 start 路径按 mode 传入），在驱动实际使用的快照上复验 HasMountableTrigger（判定收编 WorkflowRunner.HasMountableTrigger 单一事实源，宿主委托调用）；前提失效→受理事实保留、运行收敛 Interrupted+留痕、绝不提交；夹具 ArmLaunch_TriggerRemovedBeforeDrive_ConvergesInterrupted_NoSubmission |
| 重要4 | §8 矩阵证明声明超范围：3/9 为分层论证非全链；7 受重要1 影响；13 缺方案存储证据定位 | 采纳：§8 证明状态按证据口径重写（3/9 标注分层组合论证、全链留 R4.10；7 由重要1 处置闭环；13 证据定位 Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/StartupFlowSchemeStoreTests.cs，纳入下轮会诊 allowlist） |
| 建议1 | TryAppendHandoffBinding 取消优先级：取消与状态失效并发时先回业务拒绝；耗尽后到达的取消表现为 HandoffError | 采纳：每轮入口+耗尽出口补 ThrowIfCancellationRequested（保留贴近身份修改的原检查）——检查点保证口径（八轮建议2 更正：入口/身份修改前/耗尽出口三道检查点；检查点间到达的取消仍可能先见业务拒绝，不构成提交后反转） |
| 建议2 | 夹具注释失真：RereadStateGuardRejects 把 Interrupted 写成「终态化」且声称「不预留」；SurviveEngineStyle 错误声称「WorkflowRunner 各写点前重新 Load」 | 采纳：两处注释更正（证明范围收窄为助手重读过守卫；引擎实为持有对象写回，并发交错指向 StaleWriterConflict_* 与七轮真实驱动夹具） |
| 建议3 | 空白 ErrorCode 夹具只测空串 | 采纳：改 Theory（空串/纯空格/制表符） |

### 八轮（七轮处置验证 + 关闭复核，2026-09-19，12 文件 allowlist）——暂不可关闭：3 重要/3 建议，处置（回归 349/349）：

| # | 发现 | 处置 |
|---|---|---|
| 重要1 | arm 前提失效后宿主回执仍明确回报「流程触发器已挂载」（Accepted 正确但说明失真；启动节点实际显示该文案） | 采纳：arm 回执改「已受理挂载请求（挂载等待不占槽位；受理≠执行成功，挂载结果见运行状态）」，confirmed=Interrupted 时如实说明中断（九轮重要1 更正：Interrupted 是通用收敛态，回执不按状态反推「前提失效」，原因指向运行备注）；宿主全链夹具 ArmLaunch_PremiseLostBetweenPrecheckAndDrive_ReceiptHonest_NoSubmission（runnerFactory 接缝在受理预检后/受理落盘前确定性改写定义——九轮建议1 精确化时点；覆盖 mode 接线/零提交/Interrupted/回执诚实） |
| 重要2 | LaunchDrive 的 StateChanged 订阅者异常可越过受理方法，把已落盘受理反转为 VM 侧 Rejected(HandoffError) | 采纳：NotifyStateChanged 隔离助手（订阅者异常仅留痕；三处调用点全替换：RequestRunAction/LaunchDrive/ObserveDriveAsync finally）；九轮重要2 补：隔离边界自闭合（留痕日志异常同样隔离不外抛）；夹具 StateChangedSubscriberThrows_AcceptanceReceiptPreserved（Accepted 不反转 + 同键重放 AlreadyAccepted + 驱动出册） |
| 重要3 | 场景 13 的「SaveAll/Load 与导出共用同一序列化通道」缺真实通道证据（仅有导出/导入/Clone 夹具） | 采纳：StartupFlowSchemeStore 加 internal 路径接缝（生产无参构造不变，夹具不碰真实用户目录）；夹具 SaveAll_Load_RoundTrip_PreservesTaskCenterHandoffFields（新实例读盘，证明保存/加载无转换/过滤） |
| 建议1 | 真实驱动夹具「驱动出册」断言不足（Interrupted 落盘先于观察器 finally 移除登记） | 采纳：宿主加 internal HasDrive 实况；夹具改等「Interrupted 且出册」、补 RunRecordConflictException 冲突类型与双 IntentKey 逐条保全断言 |
| 建议2 | 取消优先级应表述为检查点保证；既有取消夹具注释过时；耗尽出口取消无覆盖 | 采纳：注释更正为三道检查点口径；新夹具 TryAppendBinding_CancelInFinalAttempt_ExitCheck_ThrowsOce（末次尝试内先于冲突抛出到达的取消以 OCE 表现——十轮建议3 正名）；不为绝对取消优先在提交后加检查（不反转已提交受理） |
| 建议3 | 历史处置条目（四轮重6「写前重新 Load」、六轮重要1「一律不再重读」、六轮重要4「不预留」）缺后续更正索引 | 采纳：三处原位标注「已由七轮/八轮对应项更正」（历史保留，不作现行依据） |

### 九轮（八轮处置验证 + 关闭复核，2026-09-19，13 文件 allowlist）——暂不可关闭：2 重要/3 建议，处置（回归 352/352）：

| # | 发现 | 处置 |
|---|---|---|
| 重要1 | confirmed==Interrupted 被当作「挂载前提失效」专用结果——任何无未决外部事实的驱动异常同态收敛，回执会误报前提失效 | 采纳：回执按状态如实描述「运行已中断（原因见运行备注），受理事实保留」，不按状态反推原因；反例回归 ArmLaunch_WaitDriveFault_ConvergesInterrupted_ReceiptDoesNotMisattribute（触发器合法+DelayAsync 故障注入→Interrupted，回执/留痕均不误报前提失效）；前提失效夹具断言同步改「运行已中断」（精确原因仍由 StartExistingRunAsync 写入运行备注） |
| 重要2 | NotifyStateChanged 的 catch 内 _log 委托异常未隔离——订阅者+日志双重异常仍可越过受理方法把已受理反转为 HandoffError | 采纳：隔离边界自闭合（留痕日志调用独立 try/catch，通知路径绝不向外抛）；夹具 StateChangedAndIsolationLogBothThrow_AcceptanceReceiptPreserved（双重抛出仍 Accepted+重放 AlreadyAccepted+驱动出册） |
| 建议1 | arm 全链夹具/耗尽取消夹具的故障注入时点描述不准（定义变更在受理落盘前；取消在末次尝试内先于冲突抛出） | 采纳：夹具重命名 ArmLaunch_PremiseLostBetweenPrecheckAndDrive_* + 时点注释精确化；取消夹具注释更正——证据效力不变 |
| 建议2 | 通知夹具未证明观察器无静默故障任务；暂停态 Stop 路径通知隔离无回归 | 采纳：StateChanged 夹具改日志捕获——隔离留痕 ≥2 条=LaunchDrive 与观察器 finally 两处通知均被隔离；新夹具 PausedStop_SubscriberThrows_EffectiveReceiptPreserved_RunCancelled（Effective 不反转+Cancelled 落盘） |
| 建议3 | §8/§9 两项可靠性声明超实现保证；R4.10 缺「追加身份后自然到点冲突」集成项 | 采纳：声明随重要1/重要2 同步收窄；R4.10 清单补第 8 项（真实 Waiting 运行追加身份后自然到点修订冲突→UI 展示 Interrupted、同键重放不重驱动、显式恢复可操作） |

### 十轮（九轮处置验证 + 关闭判定，2026-09-19，13 文件 allowlist）——**可关闭**：阻断 0/重要 0/建议 3（不阻断，已全部处置，回归 352/352）：

| # | 发现 | 处置 |
|---|---|---|
| 建议1 | 「观察器无静默故障任务」表述超证明范围（观察器其他 _log 出口仍有非隔离残余边界） | 采纳：夹具注释收窄为「两处通知的订阅者异常均被隔离」；残余边界（ObserveDriveAsync/ObserveOrphanAsync/ConvergeDriveException 的 _log 非隔离）入档为已知限制——生产 AddLog 为诊断出口不预期抛出，统一不抛出诊断出口列 R5+ 可选治理 |
| 建议2 | Delay 故障夹具缺故障点直接证据（负向断言可被其他等待前异常碰巧满足） | 采纳：delayCalls 计数==1 + CurrentRunState==Interrupted + 「运行已中断」+ InvalidOperationException 异常类型留痕断言（故障任务在 LaunchDrive 内同步完成→观察器同步收敛，回执时确定性 Interrupted） |
| 建议3 | 文字残留：取消夹具名 AfterFinalConflict 与实际时点不符；§3 单 HandoffIdentity 旧口径；R4.10 清单标题过时 | 采纳：夹具正名 CancelInFinalAttempt；§3 三处改 `Handoffs[]` 多绑定口径（标注二轮 B1 修订）；清单标题改十轮终审 |

**十轮终审结论引用**：「按设计稿明确的组件级范围和已登记限制，R4.9 可关闭。R4.10 八项仍需独立验收，尤其是追加挂载身份后必然产生的旧修订写回冲突（结构性代价，非低概率时序问题）——本次组件关闭接受的是绑定保全、保守中断及显式恢复合同，不是无中断的挂载体验。」

### §8 十三场景证明状态（十轮终审口径，352 夹具基线）

组件级已证明（分层组合口径，非端到端全链）：
- 1（重放+双 Miss 屏障并发确定性）、2、5（模拟崩溃窗真实扫描）、8（Runner 三层分支回溯）、10、11（三分法提交事实，含空白 ErrorCode 三态）、12（宿主侧快照/能力守卫+台账优先回归）——夹具直接证明。
- 3：分层组合论证（Runner 同来源同日同键 + 宿主台账去重 + VM 计划日/事件日身份）——多入口全链留 R4.10。
- 4：Runner 手动新键 + 宿主终态后新运行（组件级闭环）——端到端留 R4.10。
- 6：四态 + 新挂载零提交 + loop-only 拒绝 + 七轮闭环：真实在等驱动被追加后的行为（写回冲突→Interrupted 收敛、绑定保全、零提交）与 arm 前提跨窗口复验（前提失效不提交）。
- 7：绑定最新 + 多绑定追加 + 重放不重选 + 受理后回执可靠性（七轮 重要1：提交时快照兜底，读取失败不反转结论；八轮+九轮：arm 回执不假报挂载且不按状态反推原因、通知订阅者异常与留痕日志异常双重隔离不反转受理）。
- 9：分层证明（Runner 拒绝后不执行 StopBgi + 宿主活动运行拒绝）——A/B 全链联动留 R4.10。
- 13：StartupStep 字段级往返 + 缺省默认（SerializationRoundTrip_*）+ 方案库 SaveAll→Load 真实通道往返（八轮 重要3 夹具 SaveAll_Load_RoundTrip_PreservesTaskCenterHandoffFields，路径接缝注入不碰真实用户目录）+ 导出/导入/Clone 往返（证据文件：Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/StartupFlowSchemeStoreTests.cs）。

留待 R4.10 集成验收（十轮终审清单）：
1. 4/9 端到端 UI 全链（新手动意图全链；A 已受理而 B 被拒后不执行 StopBgi 全链联动）。
2. 13 的方案 UI 入口（保存/恢复/导入导出）。
3. 主流程/定时/电子狗/日志四入口真实 VM→共享宿主接线（覆盖跨日排队与同日重复事件）。
4. WPF STA 绑定行为（候选刷新、同 ID 改名/不可用切换、闭合态显示、配置不被回写清空）。
5. 真实受理点崩溃恢复、关闭竞态、受理前取消与受理后启动链取消的所有权边界。
6. 生产能力提供方/快照提供方/宿主共享实例接线 + 监控端零副作用完整面。
7. 保留与去重边界入档口径：单宿主、运行台账永存、进程崩溃恢复——不扩大为跨进程仲裁或系统掉电持久性保证。
8. 真实 Waiting 运行追加身份后自然到点修订冲突的集成表现：UI 展示 Interrupted（不假报挂载中）、同键重放 AlreadyAccepted 不重驱动、显式恢复可操作（九轮 建议3）。