# 槲寄生 R5 owner 决策单（2026-09-24）

> 状态：**D1–D5 已于 2026-09-24 由 owner 全部裁决为推荐项 A**（见文末「集中裁决表」与「裁决生效范围与约束」）；本单初版作为决策材料提交于 `5e46cf232`，裁决登记随批次 12 提交。
>
> **裁决后约束不变**：生产入口、真实 User、部署/发布、R5.8 签署与节点改道门**继续关闭**——选 A 是选**实现方向**，不等于开门。本单原有的选项、证据与「能证明／不能证明」边界继续有效，不因裁决而升级为已验证事实。
>
> 阅读方式：D1–D5 每项先给**逐项证据索引**（代码位置／夹具指针／能证明／不能证明），再给选项与影响；末尾是集中裁决表。所有"现状"陈述都标明来源；来自文本扫描的结论**不等于**穷尽证明。

## 现状共同前提（逐项证据索引指向的事实）

- **准入结果枚举确无等待值**：`AdmissionResultKind` 定义在
  `MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs` 第 **87–112+** 行，成员为
  Accepted(90)／TerminalRejected(92)／RetryableRejected(94)／NotSelected(96)／F11Blocked(98)／NeedPreemptConfirm(100)／NeedReconcile(102)／Reconciling(104)／Error(106)／Cancelled(108–111)／ExecutionFailed。全仓库 `rg -o "AdmissionResultKind\.[A-Za-z]+"` 的枚举成员**点名声**恰好覆盖上述 11 个，**没有** `WaitLocally`／`Deferred`。
- **调用方按值分流已有具体位置**：`TaskCenterHost.Admission.cs` 第 **1589–1609** 行（把准入结果映射为边界提交结果：`RetryableRejected` 单独分流／`TerminalRejected`·`NotSelected`·`F11Blocked`·`NeedPreemptConfirm` 归确定拒绝／`NeedReconcile`·`Reconciling` 归未知）、第 **1042–1051** 行与第 **1232–1240** 行（UI 文案 switch，均带 `_ =>` 兜底）、第 **1999–2007** 行（外部启动结果映射）。
- **等待组件无生产调用方**：`rg -l "LocalWaitQueuePolicy|LocalWaitQueueStore|LocalWaitItem|LocalWaitQueueFile|RunningOccupancyArbiter|DecideEnqueue" MultiplayerHoeingAssistant`（排除 bin/obj）只返回 5 个定义文件：`Models/TaskCenter/LocalWaitModels.cs`、`Models/TaskCenter/ArbitrationModels.cs`、`Services/TaskCenter/LocalWaitQueueStore.cs`、`Services/TaskCenter/Arbitration/RunningOccupancyArbiter.cs`、`Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs`。**能证明**：本工作区当前源码文本中未发现入口／宿主调用者。**不能证明**：反射、动态调用、生成代码、未提交或未来文件；也不排除后续批次新接线。
- **等待项永不带发送许可（已有结构保证）**：`LocalWaitEnqueueDecision` 的构造函数为私有、`SendPermitted => false` 只读，仅 `DecideEnqueue`／静态工厂可建（`LocalWaitQueuePolicy.cs` 第 **10–34、44–58** 行）。
- **夹具与证据口径**：本单只引用**已重跑的定向夹具**（见文末证据），不引用未验证的运行效果；构建/测试通过不等于运行验证通过。

---

## D1：本地等待如何穿过统一准入面

**逐项证据索引**

| 项 | 指针 |
|---|---|
| 代码位置 | `ArbitrationAdmissionService.cs` 第 87–112 行（枚举）；`TaskCenterHost.Admission.cs` 第 1042–1051、1232–1240、1589–1609、1999–2007 行（调用方分流）；`LocalWaitQueuePolicy.cs` 第 22–34 行（`SendPermitted` 恒 false） |
| 夹具指针 | `LocalWaitQueueTests.DecideEnqueue_OnlyWaitLocallyEnqueues_AndNeverPermitsSend`（`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitQueueTests.cs` 第 45–65 行）；`EnqueueDecision_CannotBeForgedWithSendPermission`（同文件第 346–353 行） |
| 能证明 | 现有枚举没有等待值；现有调用方按枚举值分流（并已有 `_ =>` 兜底）；等待决策结构上不可能带发送许可 |
| 不能证明 | 新增枚举值后**所有**调用方是否被正确更新（只能靠编译器 + 全量回归）；也不证明任何生产接线已存在 |

**当前事实**

- 准入结果枚举（见上）没有"等待"值；等待组件与准入面之间当前没有任何已实现转换。
- 直接复用 `RetryableRejected` 会让调用方可能按"拒绝后可重试"处理（该分流在 `TaskCenterHost.Admission.cs` 第 1594–1596 行明确映射为重试窗口），与 owner B.1 的"本地等待、不抢发、不进 BGI 队列"语义不同。

**选项**

| 项 | 方案 | 优点 | 代价／风险 | 不推荐理由（若适用） |
|---|---|---|---|---|
| A（推荐） | 新增独立 `AdmissionResultKind.WaitLocally` ＋等待项引用／原因码；该结果不授予发送许可 | 语义唯一，调用方可穷尽处理；与 `PreemptConfirmPending` 可严格区分 | 修改冻结结果枚举；所有按值分流的调用方需同步（已知位置见索引）；需明确持久化/重放规则与 UI 文案 | — |
| B | 复用 `RetryableRejected`，仅以 `reasonCode="local_wait"` 区分 | 不改枚举 | 等待与可重试拒绝在既有调用方同路径（第 1594–1596 行会给重试窗口）；"可重试"可能触发重发；调用方容易漏判 | 语义混同，且现有分流点已把该值固定解释为"窗口内可重试" |
| C | 不增加结果，保持组件未接线 | 改动最小、门禁不变 | owner B.1 无法落地；低优先级请求仍只能拒绝或走现有路径 | 不是错误方案，但等于撤回 B.1 承诺 |

**未决默认（现状）**：C——`AdmissionResultKind` 不变、生产不接线、等待组件保持无调用方。该默认**不依赖 D3**：即使 D3 选 A（事件驱动重评），只要 D1 仍为 C，等待项就没有穿过准入面的出口。

---

## D2：前置就绪与计划时刻如何表达

**逐项证据索引**

| 项 | 指针 |
|---|---|
| 代码位置 | `LocalWaitModels.cs`：`LocalWaitItem` 字段（第 27–56 行）**无**前置就绪／可执行时刻字段；`LocalWaitItemState` 仅 Waiting/Cancelled（第 6–10 行）；`LocalWaitQueueFile.CurrentVersion = 1`（第 63 行）。`LocalWaitQueuePolicy.cs` 第 104–113 行：投影时 `ScheduledAt = item.ScheduledAt`、`PrerequisiteReady = true` |
| 排序规则 | `RunningOccupancyArbiter.cs` 第 124–138 行：`.Where(w => w.PrerequisiteReady)` 恒成立（因投影恒 true）；`.ThenBy(w => w.ScheduledAt.HasValue ? 0 : 1).ThenBy(w => w.ScheduledAt ?? default)` ——**有值先于 null、升序**，但**不与当前时间比较** |
| 夹具指针 | `LocalWaitQueueTests.Store_ReloadsFromDisk_AndSelectsNextByPolicyRule`（第 103–129 行，落盘后按规则重选）；`Store_MissingTrustedFlagIsTreatedAsUntrusted`（第 323–344 行，缺字段保守按不可信）；`Store_LoadRejectsCorruptOrFutureVersion_AndMissingFileIsEmpty`（第 163–209 行，含"更高版本响亮拒绝"） |
| 能证明 | 现有落盘格式与选择规则的实际行为；`ScheduledAt` 只影响排序，未来时刻**不会**把项排除 |
| 不能证明 | 未裁决前**任何**前置就绪来源的权威性与失败语义；也没有夹具证明"未来时刻不阻止选中"以外的通用前置行为（该结论来自排序键逐行阅读 + 上述夹具，不是穷尽反例） |

**当前事实**：如索引所列。`ScheduledAt` 只有排序语义，因此"计划时刻在未来"不构成任何门槛。

**选项**

| 项 | 方案 | 优点 | 代价／风险 | 不推荐理由（若适用） |
|---|---|---|---|---|
| A（推荐） | 持久化稳定前置引用（例如 workflow/node/ticket 引用）＋重评时由**只读 evaluator** 得出的就绪结果；发送前再次验算 | 重启可复核；不信任过期布尔；可解释 | 需扩展持久格式与版本（`CurrentVersion=1` 需迁移）；evaluator 的权威来源与失败语义要冻结 | — |
| B | 直接把 boolean＋计划时刻写入等待项，选中时读取 | 实现简单 | 布尔会在重启后过期或失真；无法证明前置仍成立；与"缺字段保守按不可信"的既有取向冲突 | 引入不可复核的持久事实 |
| C | **本期不支持通用前置**（只按"占用是否结束"判定是否可再比较；不引入前置引用字段与 evaluator） | 范围最小、可先闭合 B.1 | 计划时刻/复杂前置承诺撤回；后续加字段仍需一次迁移 | 可接受；**本项只定义"前置就绪"的表达方式，重评的触发事件与次数完全由 D3 决定，本项不预设"一次性"** |

**未决默认（现状）**：**当前未实现任何通用前置能力或重评**——既无前置就绪字段与 evaluator，也不接受未来时刻作为就绪证明（`ScheduledAt` 只参与排序，见上"排序规则"）；等待项侧只有 `WaitLocally` 的原因文本（`RunningOccupancyArbiter.cs` 第 114–117 行）表达"占用结束后重新比较"的**设计意图**，该意图**尚未实现**。**注意**：此处默认是"未实现"，**不等同于已选择 C**；若 owner 选择 C，本项只确定"前置就绪如何表达"，**触发事件与次数一律由 D3 裁决**（本项不预设"一次性"，C 的语义中也不再包含"一次性重评"这一描述）。

---

## D3：什么事件触发重评

**逐项证据索引**

| 项 | 指针 |
|---|---|
| 代码位置 | 扫描范围＝**生产源码目录** `MultiplayerHoeingAssistant/`（排除 `bin`/`obj`）：`rg -n "WaitLocally" MultiplayerHoeingAssistant --glob "!bin" --glob "!obj"` 共 **5 处**——3 处 `Services/TaskCenter/Arbitration/RunningOccupancyArbiter.cs`（第 54、73、115 行）＋1 处 `Models/TaskCenter/RunningOccupantModels.cs` 第 28 行（枚举值）＋1 处 `Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs` 第 51 行（入队判定），**均无调度/定时/事件订阅点**；`LocalWaitQueueStore` 没有宿主实例化点（见"共同前提"扫描）。**注意**：该结论**只覆盖上述生产源码目录**——测试与文档中另有大量 `WaitLocally` 出现（本决策单与夹具本身即包含），故**不能**表述为"全仓库只命中 5 处" |
| 夹具指针 | 本项**没有**对应夹具：现有 16 项 `LocalWaitQueueTests` 全部是纯函数/落盘单测（入队判定、派生标识、幂等、落盘重载、清理、裁剪、不可信默认、伪造防护），**没有**任何触发器、线程模型或时序夹具 |
| 能证明 | **在上述生产源码范围内**，当前源码文本没有重评触发实现；选择函数可被任意调用方在任意时刻调用（纯函数）。**该范围不含**测试、文档与未提交/生成文件 |
| 不能证明 | "不存在任何后台行为"——宿主其它路径可能主动调用准入（与本等待组件无关）；也没有夹具能证明未来触发实现的时序正确性；**更不能证明"已选择 C"**——无触发器只是未实现，不等于 owner 已裁决为仅手动 |

**选项**

| 项 | 方案 | 优点 | 代价／风险 | 不推荐理由（若适用） |
|---|---|---|---|---|
| A（推荐） | 占用结束／权威退出事件触发为主，启动恢复与新候选到达为辅，加低频安全网；每次触发重新走完整准入 | 及时且可对账；安全网处理漏事件 | 需定义幂等、去重、并发和取消规则；可能引入额外读盘 | — |
| B | 固定周期轮询 | 实现简单 | 延迟与唤醒风暴；事件丢失只能靠时间等待；测试时序更难 | 唤醒成本与"本地等待"场景不匹配 |
| C | 仅手动/用户动作触发 | 最少后台行为 | 不符合"当前结束后重新比较"的自动调度意图；`WaitLocally` 的原因文本（`RunningOccupancyArbiter.cs` 第 116 行）会与实现不符 | 与 B.1 的既有表述冲突 |

**未决默认（现状）**：**当前不存在任何触发器**——在上述生产源码扫描范围内既无事件触发也无轮询（命中均为枚举值/判定/原因文本，无调度/定时/订阅点）。**该默认是"未实现"，不等同于已选择 C**；C（仅手动/用户动作触发）**仅是待选方案之一**。**与 D2 的关系**：D2 的"占用结束后重新比较"只是入队原因文本表达的**意图**，其可执行性取决于本项裁决；在 D3 未裁决前不存在任何自动重评，因此不能把 D2 的现状描述读成"已实现的自动重评"。

---

## D4：与 `PreemptConfirmPending` 的关系

**逐项证据索引**

| 项 | 指针 |
|---|---|
| 代码位置 | 字段定义：`MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs` 第 **757** 行 `[JsonPropertyName("preemptConfirmPending")] public bool PreemptConfirmPending { get; set; }`。读写点：`ArbitrationAdmissionService.cs` 第 **697–698**（已在待确认 ⇒ 直接回 `NeedPreemptConfirm`）、**781–785**（去重合并共享胜者保留待确认资格）、**1259–1267**（抢占胜者回 `Queued` 并置 `PreemptConfirmPending = true`）、**1634**、**1730**、**1810–1828**（镜像/清除）、**2254**、**5372**、**5500**、**6270**（`preempt_confirm_pending` 原因码与"未确认交接有独立停止/继任责任"）；`ArbitrationLeaseStore.cs` 第 **1602** 行 |
| 夹具指针 | 本单范围内**没有**覆盖"等待项 ↔ 待抢占确认"转换的夹具；相关既有夹具属抢占/交接面向（本批未重跑，故不作为本项证据） |
| 能证明 | `PreemptConfirmPending` 是操作记录上的独立布尔交接状态，已有既定读写与原因码；等待组件与它之间**没有任何已实现转换**（等待侧无调用方） |
| 不能证明 | 未扫描的其它历史转换入口是否隐含影响；也没有夹具证明两者并存时的现状后果 |

**选项**

| 项 | 方案 | 优点 | 代价／风险 | 不推荐理由（若适用） |
|---|---|---|---|---|
| A（推荐） | 严格互斥：同一稳定身份不能同时是等待项与待抢占确认；冲突时停驻并显式对账 | 避免重复身份、重复发送和错误覆盖 | 需要明确冲突裁决入口和失败收敛 | — |
| B | 保留父子/替代引用，允许显式状态迁移 | 可表达交接生命周期 | 状态机更复杂；需要事务/恢复证据 | 可实现但成本明显高于本期目标 |
| C | 两者并存、每次重评择优 | 看似灵活 | 同一身份可能被两个驱动同时推进，双写/双跑风险高 | 与"未知不得当空闲/不得重复发送"的既有底线冲突 |

**未决默认（现状）**：两者当前**没有交集**——等待组件无生产调用方，`PreemptConfirmPending` 也没有等待侧消费者；因此不需要额外门禁。若将来接线，A 的"冲突停驻"方向只作为保守门禁，**不实现迁移或自动解除**；生产继续关闭。

---

## D5：`BatchReconcileDecider.Complete` 的未知结果语义

**逐项证据索引**

| 项 | 指针 |
|---|---|
| 代码位置 | `MultiplayerHoeingAssistant/Services/BatchReconcilePlan.cs`：`ConfirmTerminal(int Index, bool Cancelled, string? ErrorCode)` 第 **100** 行；`Complete()` 第 **113** 行（**无载荷**），注释第 **112** 行写明"批次完成，调用方推进完成后动作（RunSpecified 收尾）"；空批次早退 `Complete` 第 **166–170** 行；`lost_job` 第 **232** 行；`result_unknown` 第 **245** 行；取消/失败确认第 **261、277–278** 行；"全部确认 → 完成"分支第 **282–289** 行 |
| 夹具指针 | `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/CoordinatedBatchAdmissionRelationTests.cs` 第 **137–154** 行 `BatchReconcile_LostJobConfirmation_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed`（断言 `lost_job` **与** `Complete` 同拍出现，且**不是** `AbortUserCancelled`）；第 **156–166** 行 `BatchReconcile_ExhaustedAttemptsUnknownResult_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed`（同上，`result_unknown`） |
| 能证明 | 当"最后一项"在本拍被确认为 `lost_job`／`result_unknown` 时，同一拍同时返回 `ConfirmTerminal`（带 ErrorCode）与无载荷 `Complete`；`Complete` 自身不带结果未知／失败标记（此为夹具覆盖的那一条路径） |
| 不能证明 | 调用方是否真的据此执行 RunSpecified（本批未核查该调用方）；没有夹具证明"真成功"路径与"未证退出"路径在调用方侧可区分；也**没有夹具**覆盖"批次在本拍前已全部 `TerminalConfirmed` 时同一分支仍返回 `Complete`"（该结论来自第 282–289 行逐行阅读，并已核对第 188–280 行在无 `Submitted` 项时确实无事可做） |

**当前事实（收窄表述）**

- `Complete` 是**无载荷**动作，注释关联"完成后动作（RunSpecified 收尾）"。
- `ConfirmTerminal(..., ErrorCode="lost_job")`（第 232 行）与 `ConfirmTerminal(..., ErrorCode="result_unknown")`（第 245 行）只写入当拍确认。
- 第 **282–289** 行的分支是 `All(State == TerminalConfirmed || 本拍已确认)`，**并未以"本拍确有新确认"为前提**。因此准确表述是：**当最后一批未确认项在本拍被确认为 `lost_job`／`result_unknown` 时，`ConfirmTerminal` 与无载荷 `Complete` 同拍返回**；此外，若**全部期望项在本拍之前就已是 `TerminalConfirmed`**（此时第 188–280 行循环无事可做、`confirmedThisTick` 为空），同一分支**仍会返回 `Complete`**——即无载荷 `Complete` 不止在"首次达成全确认"的那一拍出现，对同一已确认批次**可重复返回**。若仍有未确认项则该分支不成立（空批次另走第 166–170 行早退分支）。这不改变风险性质——该拍仍可能包含 `lost_job`／`result_unknown` 的确认。
- **夹具覆盖边界**：两条 `ExposedNotEndorsed` 夹具覆盖的是**前一条路径**（最后一批未确认项当拍被确认为 `lost_job`／`result_unknown`，两动作同拍出现）；**未被夹具覆盖**的是后者——"全部项在本拍之前已是 `TerminalConfirmed` 时同一分支仍返回 `Complete`"（该结论来自第 282–289 行逐行阅读，并核对第 188–280 行在无 `Submitted` 项时确实无事可做），也没有夹具证明 `Complete` 对同一已确认批次**只返回一次**。

**选项**

| 项 | 方案 | 优点 | 代价／风险 | 不推荐理由（若适用） |
|---|---|---|---|---|
| A（推荐） | 类型拆分：`CompleteSucceeded` 只在全部成功时产生；未知/失败终态产生 `CompleteWithUnresolved`（或等价显式动作），调用方只有前者可执行 RunSpecified | 编译期强制区分，最不容易误触发完成动作 | 改动作类型与调用方；需要定义 UI/日志/旧状态迁移 | — |
| B | 保持 `Complete`，增加结果汇总载荷与 `MayRunCompletionAction` 字段 | 改动较局部 | 仍依赖调用方读取并尊重载荷，编译期防误用较弱 | 可作为过渡，但不得声称已消除误用面 |
| C | 将 `lost_job`／`result_unknown` 改为失败或 Abort 收尾 | 不会推进正常完成动作，语义直接 | "未知"不是已证实失败；可能丢弃原未决责任或造成错误归因 | 与"未知不得表述为成功**也不得改写为已证实失败**"冲突 |
| **D（补充合法选项，本单不推荐但必须列出）** | **保留无载荷 `Complete` 作为"批次生命周期结束"信号**，由调用方依据 `ConfirmTerminal` 已记录的逐项结果（`lost_job`／`result_unknown`／`ErrorCode`）**禁止**执行 `RunSpecified` | 不动动作类型与枚举；与现有落盘结果保持一致；`ConfirmTerminal` 已携带所需事实 | **依赖调用方逐项判断**：漏判即误触发完成动作；跨进程/重放路径必须同样遵守；无法在编译期阻止；需要额外护栏测试把"未知 ⇒ 不得 RunSpecified"固定下来 | 防误用强度最低，只适合作为"先补护栏、再另行拆分"的过渡选择 |

**未决默认（现状）**：保持 A–D 全部未选；`Complete` 现状不变、未知结果**不得**表述为成功。B 的兼容方向或 D 的过渡方向**都不得**作为不接受裁决就落地的实现——若 owner 选择 B/D，必须同时给出可验证的调用方护栏（否则等同在未定语义上堆实现）。生产/节点改道门保持关闭。

---

## 集中裁决表（2026-09-24 owner 已裁决：D1–D5 全部选 A）

| 决策 | A | B | C | D | **owner 选择（2026-09-24 已裁决）** |
|---|---|---|---|---|---|
| D1 等待结果 | 新增 WaitLocally（推荐） | 复用 RetryableRejected | 保持未接线 | — | **A — 新增 `AdmissionResultKind.WaitLocally`（不含发送许可）** |
| D2 前置就绪 | 稳定引用＋只读 evaluator（推荐） | 持久 boolean | 不支持通用前置（触发方式见 D3） | — | **A — 持久化稳定前置引用 ＋ 只读 evaluator；发送前再次验算** |
| D3 重评触发 | 事件＋启动＋新候选＋安全网（推荐） | 周期轮询 | 仅手动 | — | **A — 占用结束／权威退出事件为主，启动恢复与新候选为辅，加低频安全网** |
| D4 抢占确认关系 | 严格互斥／冲突停驻（推荐） | 显式状态迁移 | 两者并存择优 | — | **A — 同一稳定身份不得同时是等待项与待抢占确认；冲突停驻并显式对账** |
| D5 Complete 未知语义 | 类型拆分（推荐） | 载荷字段 | 失败/Abort 收尾 | 保留无载荷 Complete＋调用方禁止 RunSpecified | **A — 类型拆分：仅全成功产生 `CompleteSucceeded`；未知／失败终态产生 `CompleteWithUnresolved`，只有前者可执行 `RunSpecified`（具体命名由实现批确认）** |

**裁决生效范围与约束（2026-09-24 owner 裁决）**

- **裁决内容**：D1–D5 全部选择**推荐项 A**。本裁决变更的是**实现方向**，不改变本单「边界」段的其余约束。
- **仍保持关闭**：生产入口、真实 User、部署/发布、R5.8 签署、节点改道门。**选 A ≠ 开生产门**；如需开门须 owner 另行明示。
- **未授权**：本裁决不授权修改冻结合同之外的既有语义、不授权第三方 JS 改动、不授权产生发送。
- **实施纪律**（依据仓库 AGENTS.md「长任务工序纪律」与 §17.4-A）：
  1. 五项属**不同状态面**（结果枚举／持久格式／后台触发／状态互斥／完成动作类型），**拆成多个可独立验证的批次**，不合并为单批。
  2. 每批只解决一个具体状态转换，写明完成判据、针对性证据、受影响回归与生产门。
  3. 「生产构造开门」类实现**反例先行**：先红夹具、后实现，只开一轮验证会诊。
  4. 声明的语义需与代码逐条一致；行号引用可核对。
  5. 声明面（状态词／门禁词／证据等级）改动后设 `CLAIM_SURFACE_REGENERATE=1` 再生清单并纳入提交与评审。
  6. 常规会诊 `gpt-6-sol`/medium；仅最难或安全敏感面用 `gpt-6-astra`。
  7. 构建/测试用 `-p:DeployToBgiTools=false`；构建测试通过不等于运行验证。
- **D5 的优先性**：D5 是五项中唯一存在**已固定行为风险**的一项（夹具已固定「未知结果与无载荷 `Complete` 同拍出现」并声明不背书），实施批次应**优先于** D1–D4（后者当前未接线、无运行风险）。
- **D5 实现提示（不构成语义变更）**：类型拆分需同时处理第 **166–170** 行空批次早退（当前直接产出 `Complete`）与第 **282–289** 行「全部确认」分支；两处是否都归入「全成功」类，须在该批的可验证设计中明确，不得默认沿用旧语义。

## 证据

**定向夹具（本批重跑，2026-09-24；工作区 HEAD = `eaf38252d` 之上未提交改动）**

- 命令：`dotnet test Test\MultiplayerHoeingAssistant.UnitTest\MultiplayerHoeingAssistant.UnitTest.csproj --filter "FullyQualifiedName~LocalWaitQueueTests|FullyQualifiedName~CoordinatedBatchAdmissionRelationTests" -c Debug --nologo -p:DeployToBgiTools=false --logger "trx;LogFileName=<name>.trx"`
- 结果：**49/49 通过** = `LocalWaitQueueTests` **16** ＋ `CoordinatedBatchAdmissionRelationTests` **33**（含本单引用的两条 `ExposedNotEndorsed` 风险暴露夹具）。TRX：`Test/MultiplayerHoeingAssistant.UnitTest/TestResults/r5_batch11_decision_evidence.trx`（同批二次运行 `r5_batch11_decision_evidence_450.trx` 亦为 49/49；措辞修正后第三次运行 `r5_batch11_decision_evidence_final2.trx` 仍为 49/49）。
- **这些夹具能证明**：D1 的"等待决策结构上不带发送许可"、D2 的"落盘重载后按排序规则重选"与"缺字段保守按不可信"、D5 的两条当前行为固定。
- **这些夹具不能证明**：任何生产接线行为、真实发送是否发生、触发器时序、跨进程/断电耐久、调用方是否执行 RunSpecified；也不构成对 D1–D5 任一选项的背书。

**代码事实（逐项行号见各项"逐项证据索引"）**

- 准入结果面：`ArbitrationAdmissionService.cs` 第 87–112 行；调用方分流 `TaskCenterHost.Admission.cs` 第 1042–1051、1232–1240、1589–1609、1999–2007 行。
- 等待模型／策略／落盘：`Models/TaskCenter/LocalWaitModels.cs`、`Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs`、`Services/TaskCenter/LocalWaitQueueStore.cs`。
- 选择与排序：`Services/TaskCenter/Arbitration/RunningOccupancyArbiter.cs` 第 124–138 行（`PrerequisiteReady` 过滤＋`ScheduledAt` 排序键）。
- 批次完成语义：`Services/BatchReconcilePlan.cs` 第 100、112–113、166–170、232、245、261、277–278、282–289 行。
- 待抢占确认：`Models/TaskCenter/ArbitrationModels.cs` 第 757 行；`ArbitrationAdmissionService.cs` 第 697–698、781–785、1259–1267 行。

**批次边界（避免范围误读）**

- 本批交付＝本决策单 ＋ R5.3 §24.107 ＋ 交接稿批次 11 页首 ＋ 总计划状态行 ＋ 声明面守卫文档清单／清单再生。
- **不属本批**：`Docs/design/mistletoe-session-relay-2026-09-24.md`（串行接续规则与 `ownership.json` 所有权 CAS 协议）与 `Docs/design/unified-job-registry-master-plan.md` 的改动是**此前批次/用户既有未提交改动**，本批不修改、不纳入提交；其归属另行说明。
- 本批未改生产代码、未接入口、未产生发送。

## 边界

本单不代表任何选项已接受、冻结或实现；不授权生产接线、真实 User、部署/发布、R5.8 签署，也不把未知结果改写为成功或改写为已证实失败。