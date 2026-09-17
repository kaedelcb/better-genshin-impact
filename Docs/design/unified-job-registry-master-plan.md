# BGI 统一作业注册表与槲寄生调度接管 · 总计划

> 状态：v3 执行计划修订（2026-09-17）；A0–A6 保留历史实施记录，**用户先审查，明确下令前不启动 R0–R6 产品实施**。
> 差异审查入口：[公版与茶包逐项对照](onedragon-public-vs-teabag-comparison-2026-09-17.md)。共有功能只统一实现；不把“从此开始”、指定树脂次数、JSON 等误归茶包新增。
> 当前方向：恢复公版一条龙，茶包编排增强迁入槲寄生。旧 B1→B2→C1 已替代，见 §7/§13。
> 产品主入口：[槲寄生调度器总计划](../../槲寄生调度器总计划.md)。详细字段迁移、功能审计及验收：[2026-09-17 执行方案](onedragon-public-compatibility-audit-2026-09-17.md)。
> 阅读口径：§1–§3 是 2026-09-13 的事故与审计背景，F1–F13 不代表当前仍全数存在；本轮发现编号为新文档 F01–F13。§12 是历史实施证据；当前合同和纠偏以 §6/§7/§13 与新方案为准，不将旧“已完成”推断为实机全覆盖。
>
> 触发事故：2026-09-13 上线锄地冷启动回退场景，助手批次循环把"已下发"当"已完成"，
> RunSpecified 完成后动作（配置组"锄地继续"）在命令行批次进游戏前被提前下发，
> 与 `--startGroups` 路径并发抢 TaskRunner，导致"联机队长-传奇/次数盾"两组被
> "当前存在正在运行中的独立任务"撞死。

## 1. 历史问题定义（2026-09-13 事故背景）

本次事故每一环单独看都按设计工作：回退重启按设计返回成功、批次循环按设计推进、
策略收尾按设计触发。每个局部都对，合起来错——这是架构问题的定义。

病根只有一条：**BGI 没有"执行注册表"，"谁在跑/排队/已完结"这个事实散落在
各个入口的发起方手里**。审计确认的执行入口共六类（详见 §3）：

| 入口 | 路径 | 对外可见性 |
|---|---|---|
| 命令行 `--startGroups` / `--TaskProgress` / `startOneDragon` | ApplicationHostService.cs:81-105 | 仅引擎直挂事件 |
| UI 手动（调度器/JS 列表/地图追踪/任务设置页 19 处 solo 开关） | ScriptControlViewModel.cs:2469/2953、TaskSettingsPageViewModel.cs:865-1686 等 | 仅引擎直挂事件 |
| 热键（7 个 solo 热键 + 一条龙热键） | HotKeyPageViewModel.cs:587-640 | 仅引擎直挂事件 |
| v2 IPC `task.start` / `task.resume` | InstanceRequestHandler.cs:732/1602 | 同步回执，无队列登记 |
| ext `task.queue` | BgiTaskCoordinator（唯一有生命周期登记的入口） | 完整 |
| F10 触发器总开关 + 9 个实时触发器 | TaskTriggerDispatcher（GameTaskManager.cs:57） | **完全不可见** |

其中五类入口对任何外部观察者不可见。助手只能靠"我自己发过什么"推断 BGI 状态，
一旦有东西绕开它（命令行、用户手动、resume），世界模型即与现实脱节。
`_hasRestartedThisBatch`、`_teardownDoneGeneration` 这些补丁，本质都是在助手侧
用本地状态硬凑 BGI 的事实镜像——永远凑不齐。假终态、双执行、抢锁撞死，
全是这一个病的症状。

## 2. 方法论与参考架构

不闭门造车。这类"外部编排器 ↔ 本地执行代理"的系统有成熟先例，逐一对齐：

### 2.1 Kubernetes 控制器：水平触发对账（level-triggered reconciliation）

核心思想：控制器不依赖事件流推进状态，而是**反复比对期望状态与观察状态，
幂等地纠偏**，事件只用来加速收敛，丢失不影响正确性。
参考：[Level Triggering and Reconciliation in Kubernetes](https://hackernoon.com/level-triggering-and-reconciliation-in-kubernetes-1f17fe30333d)。

**映射到本项目**：助手的批次循环必须是水平触发的——每个推进决策的依据是
"向 BGI 拉到的作业实际状态"（观察状态），而不是"我发过什么命令"（本地记忆）。
本次事故正是边沿触发（"发完了 = 完成了"）的典型死法。AGENTS.md 纪律 §2
"push 是快速路径，pull 才是事实源"与此同源，本计划把它升级为结构性保证。

### 2.2 Temporal：持久执行 + 可见性存储 + 幂等启动 + 心跳

Temporal 的构成：Task Queue（任务队列）、Worker（执行器）、Visibility store
（可见性存储，供按 ID 查询工作流状态）、心跳与超时、幂等启动
（WorkflowIdReusePolicy）。参考：[How Temporal works](https://docs.temporal.io/encyclopedia/architecture/how-temporal-works)、
[Designing a Workflow engine from first principles](https://temporal.io/blog/workflow-engine-principles)。

**映射到本项目**：
- BGI = Worker + Visibility store 合体；注册表 = 可见性存储（按 jobId 可查）。
- 幂等启动 = 注册表维护请求身份；当时的 generation+name 是雏形，2026-09-17 发现会误合并重复节点，必须按 §13 改为流程/节点/轮次/尝试身份。
- 心跳 = 在跑作业的周期心跳（见 §4.4）。
- **不照搬持久化**：Temporal 的 event history 重放对本项目过重。我们的崩溃语义
  显式降级为"纪元 fencing + 编排器重对账"（见 §4.2/§4.3），这是有意的取舍，写入 ADR。

### 2.3 GitHub Actions 自托管 Runner：会话 + 作业租约（正面与反面教材）

Runner 架构：Listener 建会话（session）→ 从 broker 拉 job message → 执行 →
**每 60s 续约租约**（job lease renewal）→ 上报结果。参考：
[GitHub Actions Runner architecture: The Listener](https://depot.dev/blog/github-actions-runner-architecture-part-1-the-listener)。

反面教材（与本事故同病）：[actions/runner#4422](https://github.com/actions/runner/issues/4422)
——runner 实际在跑 Job B 且租约正常续约，但 REST API 的 `busy` 字段返回 false。
**同一问题的两个事实源发散**，正是我们"命令行路径对助手不可见"的镜像。
教训：事实源必须唯一，派生视图（如 busy 标志）必须从唯一事实源计算，禁止独立维护。

**映射到本项目**：job lease/heartbeat（§4.4）；`busy` 类派生状态一律从注册表实时计算。

### 2.4 UiPath Orchestrator ↔ Robot：最接近的产品形态

UiPath 是"中央编排器 + 桌面机器人"的成熟商业产品，与本项目（槲寄生 ↔ BGI）
形态几乎同构。其作业状态机：Pending / Running / Successful / Faulted / Stopped /
（Stopping / Terminating / Suspended / Resuming 等过渡态），支持终态重启、
状态历史时间线。参考：[UiPath Orchestrator Job States](https://docs.uipath.com/orchestrator/automation-cloud/latest/user-guide/job-states)、
[Managing Jobs](https://docs.uipath.com/orchestrator/automation-cloud/latest/user-guide/managing-jobs)。

**映射到本项目**：
- 作业状态机直接对齐（§6.3），状态历史（转换时间线 + 停止原因）纳入终态记录。
- UiPath 的 **Attended Robot（有人值守）**概念：本地用户是一等公民，可手动启动
  任务，Orchestrator 能观察到。这正是"BGI 单机用户零回归 + 完全透明"的成熟先例：
  UI 手动启动也产生 Job，编排器只观察不干涉（除非配置了抢占策略）。

### 2.5 SQS：至少一次投递 + 幂等 + 死信

SQS 语义：at-least-once 投递、消费端幂等去重、可见性超时、死信队列（DLQ）。
**映射到本项目**：命令投递按 at-least-once 设计（重发是常态，不是异常），
去重全部收敛到注册表的幂等键；失败作业进终态表并携带结构化失败原因
（错误码分类，见 §4.6），等价 DLQ 的可排查性。单进程单泵无需可见性超时。

### 2.6 Fencing token（DDIA）

资源持有者必须能区分"旧租约持有者"和"新租约持有者"，防脑裂。
**映射到本项目**：进程纪元 fencing（§4.2）——BGI 每次启动产生新纪元，
旧纪元的 jobId/句柄一律不被信任。

### 2.7 设计决策总表

| 决策 | 借鉴 | 本项目形态 |
|---|---|---|
| 编排器水平触发对账 | K8s controller | 助手 reconcile 循环（§4.5） |
| 可见性存储 | Temporal Visibility | 注册表按 jobId 可查 + 快照接口 |
| 幂等启动 | Temporal ReusePolicy / SQS | 显式请求身份；流程节点/轮次/尝试分离，禁止仅名称去重（§13） |
| 作业租约/心跳 | GHA Runner 60s 续约 | 在跑作业周期心跳事件（§4.4） |
| 状态机 + 历史 | UiPath Job States | §6.3 状态机 + 终态记录含转换时间线 |
| Attended 模式 | UiPath Attended Robot | UI/热键启动也产生 Job，助手可观察 |
| Fencing | DDIA | bgiEpoch（pid+startTicks）+ generation |
| 死信可排查 | SQS DLQ | 终态表 + 结构化失败原因分类 |

## 3. 代码审计覆盖与关键发现

v2 前已审：执行核心（TaskRunner/CancellationContext/RunnerContext/RunMulti）、
ext 层全部、一条龙引擎。v2 补审：热键/触发器/solo 入口、启动退出接线、
助手编排层全部状态机、通信基础设施全链路。审计清单见附录 §11。
以下为影响架构设计的新发现（此前报告未覆盖的）：

### 3.1 BGI 侧

- **F1 热键即时动作完全裸奔**：强化圣遗物/快速购买/尘歌壶/一键战斗宏等
  （HotKeyPageViewModel.cs:538-781）在钩子线程同步执行，`Thread.Sleep` 阻塞、
  无取消令牌、无登记。重构时须分类：**宏类不登记为 Job（瞬态动作），但必须留
  日志痕**；DEBUG 目录的 `Task.Run` 裸跑（:821-870，`CancellationToken.None`，
  不可被停止热键取消）单独处理。
- **F2 双停止语义不一致**：热键停独立任务走 `Cancel()`（:874），全局停止热键走
  `ManualCancel()`（:375），差异在 `IsManualStop` 标志与外部 CTS 级联
  （CancellationContext.cs:65/96）。注册表收口时必须统一为单一取消语义。
- **F3 `OnStopSoloTask` 散落状态**：TaskSettingsPageViewModel.cs:823 手动复位
  14 个 `SwitchXxxEnabled` 布尔——UI 状态与任务实际状态双写，注册表收口后
  这些布尔应改从 Job 状态派生。
- **F4 实例仲裁在 DI 之前**：InstanceBootstrap 在 host Build 前完成管道占坑/
  激活转发/Exit（RuntimeHelper.cs:173）——注册表不能依赖 DI 启动顺序之外的
  前置状态，新建 `Service/Execution/` 模块须在 ApplicationHostService 之前可用。
- **F5 触发器与槽位无交互但读水位**：TaskTriggerDispatcher.cs:323 读
  TaskSemaphore.CurrentCount 决定画中画显示——注册表需提供等价只读视图。

### 3.2 助手侧（联机链路纪律的高价值候选修复）

- **F6 `AssistConfigManager.Load()` 无容错**（AssistConfigManager.cs:50-59）：
  配置文件损坏一次 = 助手永久起不来。重构前置修复（原子写有现成实现
  `LocalPeerSyncService.WriteFileAtomic`）。
- **F7 `BgiExternalClient` 热重连路径**（BgiExternalClient.cs:686-736）：
  "连上即被对端关掉"故障形态下零退避连接风暴。加最小存活时长退避。
- **F8 两处 async void 无外壳**：`OnRemoteCommand`（MainViewModel.cs:4288）与
  `_resumeTimeoutTimer`（:1263）异常逃逸 = 进程终止。各包一层 try/catch 留痕。
- **F9 MainViewModel 7100 行上帝类**：连接生命周期/重试/接线三份复刻
  （语义目前一致，是漂移源）；远程命令分发约 15 个分支。A4 阶段顺手拆分。
- **F10 助手侧零单测**：OnlineIntentLifecycle/CommandExecutor 的对账、防抖、
  会话守卫等高价值纯逻辑无回归保护，违反纪律第 5 条。A4 起必须补。

### 3.3 通信链路

- **F11 幂等窗口不跨连接**：`_idempotencyWindow` 挂在连接级会话上
  （ExternalInterfaceSession.cs），断线重连即清空。设计级修复：幂等状态上移到
  注册表（进程级），连接只是传输（见 §4.3）。
- **F12 终态表容量 32 / 近因缓冲 500 的 `not_found` 语义模糊**：调用方无法区分
  "BGI 重启"与"句柄过期淘汰"。fencing 纪元（§4.2）顺带解决此歧义。
- **F13 跨助手进程无仲裁**：同用户多会话各跑一个助手时，杀/启 BGI 只靠管道
  ACL/会话守卫兜底，"别会话助手 restart 根管道"无进程外互锁。列为已知边界，
  暂靠会话守卫（设计决策，写 ADR）。

## 4. 容错与纠错设计（核心新增）

### 4.1 投递语义：at-least-once + 注册表幂等

命令重发是常态（超时≠未执行）。所有写操作携带 `idempotencyKey`
（缺省 `rid:{requestId}`），幂等判定在**注册表**（进程级、跨连接存活），
不在连接级会话。幂等窗口 TTL 从 60s 上调到覆盖一个批次的典型时长（如 30 分钟），
命中重放该请求的已知响应；显式新 key 不得回退命中 generation+name。已受理的请求即使最终失败也保留原终态；未受理的瞬态拒绝可按策略重试，新执行尝试使用新 attempt 身份。手动停止冷却属于终态，不自动重发（A6）。

### 4.2 进程纪元 fencing

- BGI 启动即生成 `bgiEpoch = {processId, processStartTicksUtc}`（复用只读状态管道
  已有的三元组校验模式），hello/事件帧/状态响应全部携带。
- 助手持有作业句柄时绑定纪元；纪元变化（BGI 重启）→ 旧纪元全部句柄视为失效，
  触发重对账（§4.5），绝不对旧纪元句柄发取消/恢复。
- `not_found` + 纪元相同可能是句柄淘汰或未知 ID；纪元不同说明重启。均不可据此判成功，须结合已保存的受理/终态证据判断；不能无条件补发。

### 4.3 崩溃语义（显式取舍，写 ADR）

注册表为**内存权威** + 终态日志落盘（复用 task_progress 目录模式，仅诊断用，
不做重放恢复）。BGI 崩溃/重启 = 在跑与在队作业全部丢失，由纪元 fencing 暴露，
编排器侧先标记 interrupted/unknown，核实状态后按显式恢复策略处理，不直接重发整个一条龙。槲寄生持久化计划与运行提交/终态事实，不做游戏动作的全历史重放。理由：BGI 是桌面单机进程，作业多为分钟级游戏
自动化，持久化重放的复杂度（Temporal 式 event history）与收益不成比例；
真正需要跨重启续跑的场景由既有 `--TaskProgress` 机制（配置组级断点）覆盖。

### 4.4 在跑作业心跳/租约

注册表对在跑作业每 30s 发 `job.heartbeat`（含 jobId、当前进度摘要）。
助手侧：超过 2 个心跳间隔未收到且 ext 连接健康 → 判定作业卡死，进入人工
介入/自动取消策略。区分三种故障形态：**BGI 死**（纪元失效）/ **连接断**
（重连重对账）/ **作业卡**（心跳停）。这正对应 GHA Runner 的租约续约机制。

### 4.5 助手侧 reconcile 循环（水平触发）

助手维护"期望批次"（declared batch），按固定节拍（10s，复用现有状态轮询）
执行：拉 `ext.job.list` 快照 → 与期望比对 → 幂等纠偏（补提交缺失项、
确认终态项、推进完成后动作）。事件只用于加速。批次推进、RunSpecified 收尾、
取消传播全部由 reconcile 驱动——`_hasRestartedThisBatch`、`_teardownDoneGeneration`
这类边沿记忆全部删除。

### 4.6 失败原因分类（死信可排查）

终态记录的 `errorCode` 使用受控词表：
`task_busy`（槽位拒绝）/ `queue_full`（背压）/ `cancelled_user`（F11）/
`cancelled_superseded`（被新批次顶替）/ `cancelled_shutdown` /
`task_start_failed`（执行异常）/ `stale_epoch` / `not_found`。
每种都必须能从日志链完整还原（纪律 §3）。

### 4.7 助手自身健壮性（A 阶段前置项）

F6/F7/F8 三个缺陷在编排器可靠性上是地基问题，列为 A0 前置修复，随 A1 同批交付。

## 5. 约束与原则

1. **BGI 独立工作零回归**。不用助手的用户是大多数。UI 手动启动、热键、命令行、
   一条龙、触发器的行为对单机用户必须无感知；没有 ext 订阅者时注册表额外开销
   接近零（复用 PublishSafe 无订阅短路模式）。
2. **协议向后兼容**。信封保持 v2（protocolVersion 如实报 2），演进只加
   capability。老助手连新 BGI、新助手连老 BGI 都必须可用。
3. **切片交付**。每片独立可验证、可回退，严禁一步到位大改。
4. **联机链路纪律**按当前根目录 AGENTS.md 与项目 Skill 执行；旧版 AGENTS.md 的章节编号不再作为现行入口。本计划 §4 容错与 §13 纠偏同时生效。
5. **事实源唯一**。任务状态只允许注册表一个写入者；一切派生视图
   （busy 标志、UI 开关态、task.status）从注册表实时计算（GHA #4422 教训）。
6. **Attended 优先**（UiPath 先例）：本地用户操作永远产生可见 Job，
   编排器默认只观察，抢占必须显式配置。

## 6. 目标架构

### 6.1 角色划分

- **BGI = 原生一条龙 + 执行能力**：原生配置/页面/默认流程恢复公版，无助手可用；执行扩展承接注册表、单项分发、运行上下文与取消/恢复。所有入口接受同一受理仲裁，保留原生可用性。
- **助手/槲寄生 = 增强编排器**：保存流程定义和运行提交/终态事实，Planner + reconcile 对账 + 事件加速；查询 BGI 作为当前执行事实源，不靠“我已发送”推断成功。
- **当前差距**：JobRegistry 已有登记能力，BgiTaskCoordinator 仍是 ext 队列泵；不能声称所有原生入口已统一排队。R2/R5 补齐仲裁，逻辑租约不代替实际执行锁。

### 6.2 作业模型（Job）

```
Job {
  jobId: Guid                    // 稳定句柄
  bgiEpoch: {pid, startTicksUtc} // fencing 纪元
  kind: group | onedragon | solo | script | keymouse | pathing
  name: string
  source: ui | hotkey | cli | v2 | ext | resume | onedragonInternal | trigger
  generation: int?               // 联机批次代序号（幂等键成分）
  idempotencyKey: string?        // 同一请求复用；显式新 key 不回退为同名任务
  workflowRunId / nodeId?       // R2 起区分流程运行和节点出现位置
  iteration / attempt?         // 下一轮与新尝试不是重发同一请求
  configRevision / taskId?      // 标准字符串 ID，区别于组内项目序号
  ownerId / leaseEpoch?         // 接管所有者与逻辑租约；R5 完成仲裁
  parentJobId: Guid?             // A5 起：一条龙子作业的父作业
  state: 见 §6.3
  stateHistory: [{state, atUtc, reason?}]   // UiPath 式状态时间线
  result: { errorCode?, errorMessage?, wasCancelled }?
  enqueuedAt / startedAt / finishedAt
  lastHeartbeatAtUtc?            // running 期间周期更新
}
```

### 6.3 状态机与不变量

`queued → running → (succeeded | failed | cancelled)`，提交即拒走 `rejected`
（终态，携带原因——消灭 TaskRunner.cs:50-58 的静默 return 形态）。
过渡态 `cancelling`（取消已请求、执行体尚未退出）显式存在。
终态登记先于终态事件；终态表有界保留 + 状态历史随终态落盘诊断日志。
每个 BGI 实例仍串行执行。受理仲裁统一准入，TaskRunner 在叶子执行阶段获取实际 TaskSemaphore；父级只持逻辑租约，不持实际信号量等待子项。注册表登记不是排队成功或执行成功；父级必须按实际子结果及公开策略汇总（见 §13）。

### 6.4 对外观察面（双通道）

- 推：`job.queued/started/completed/failed/cancelled/heartbeat/progress`，
  携带 jobId/parentJobId/bgiEpoch。现有 `task.*` 事件双发一个版本周期作兼容别名。
- 拉：`ext.job.status {jobId}` + `ext.job.list`（在队+在跑+近端终态快照，
  reconcile 的输入）。v2 `task.status` 格式不变，内部改读注册表。
- UI/热键/命令行启动同样产生 Job（Attended 透明）。

### 6.5 兼容策略

- v2 `task.start` 保持既有信封与受支持语义；明确区分受理结果与作业终态，不因 await 返回而宣称 succeeded。新字段/操作 capability 门控，旧端不支持时如实降级。
- `--startGroups A B C` 保持用户命令行能力，内部执行纳入统一受理和观察。`startOneDragon` 保留；茶包 `startContinuousOneDragon` 通过兼容映射转到槲寄生流程，缺少承接能力时明确提示，不能静默跑第一份配置。
- ext `task.queue` 客户端平滑迁移：jobId 即 taskHandle，字段别名保留一个版本周期。

## 7. 阶段划分（2026-09-17 修订）

A0–A6 是现有基础设施与历史验收记录，保留并针对本轮缺口补回归。后续路线从“抽取旧茶包一条龙”改为“标准化、兼容桥、恢复公版、增强外移”。

```text
A0–A6 已有地基（保留；缺口按本轮审计补验收）
  → R0 基线与合同 → R1 迁移候选 → R2 兼容桥与执行事实
  → R3 公版原生恢复 → R4 槲寄生增强承接
  → R5 接管与迁移切换 → R6 发布验收与 diff 收敛
```

R1 候选不改用户数据；R2 必须先于新格式切换；R3 可以先开发，默认切换要等 R4/R5 完成，避免丢失旧用户增强功能。以下是待用户明确授权后执行的顺序。具体交付和勾选项以 [执行方案 §8](onedragon-public-compatibility-audit-2026-09-17.md#8-实施阶段与退出条件) 为准。

### 阶段 A0：助手健壮性前置 ✅ 已完成 2026-09-13（见 §12）

- F6 AssistConfigManager.Load 容错 + 原子写。
- F7 BgiExternalClient 重连最小存活退避。
- F8 OnRemoteCommand / _resumeTimeoutTimer 外壳 try/catch。
- 锚点：三个修复各带单测或故障注入推演记录（纪律 §5）。

### 阶段 A1：执行核心收口 ✅ 已完成 2026-09-13（见 §12）

- A1.1 `TaskRunner.RunCurrentAsync` 抢锁失败改显式返回 `Rejected`（带原因），
  调用方按语义记录/上报。锚点：运行中再发 task.start 的故障注入。
- A1.2 收编独立持锁点：AutoSkip/AutoTrackTask.cs:47/78、
  AutoTrackPath/AutoTrackPathTask.cs:65/96——走注册表或显式声明"内部子任务"
  并留痕。
- A1.3 统一取消语义（F2）：`Cancel()`/`ManualCancel()` 合并为单一入口 +
  显式原因参数；修 `CancellationContext.Clear()` 死代码（:147-156）与
  Cts 引用锁粒度。
- A1.4 定义 Job/JobRegistry（新目录 `Service/Execution/`），注册表在
  InstanceService 之后、ApplicationHostService 命令行分流之前可用（F4 时序）。
- 回归基线：build + BgiCoordinatorServer.Tests 全绿 + 手动冒烟
  （UI 启动配置组 → F11 → task.status）。

### 阶段 A2：全入口改注册表提交 ✅ 已完成 2026-09-13（A2.1~A2.6，见 §12）

| 切片 | 入口 | 改造点 | 关键风险 |
|---|---|---|---|
| A2.1 | UI 手动 + 热键 solo（7+19 处） | 提交壳包裹；F3 的 14 个开关布尔改从 Job 状态派生 | 开关态双写遗留 |
| A2.2 | 命令行 | ApplicationHostService:81-105 改为提交 Job 序列；保留自动更新先行（:66-72） | 冷启动时序 |
| A2.3 | v2 task.start/resume | InstanceRequestHandler:570-832/1507-1738 改提交+等终态；SuspendContext 挂到 Job | 同步回执时长；取消透传 |
| A2.4 | ext task.queue | BgiTaskCoordinator 整编进 JobRegistry（换壳不换芯） | taskHandle→jobId 别名 |
| A2.5 | F10 触发器总开关 + 9 触发器 | 触发器总开关状态纳入注册表只读视图；F5 画中画水位改读注册表；**瞬态宏类（F1）不登记为 Job，仅留日志痕**（写 ADR） | 触发器不占槽位的既有语义 |
| A2.6 | 一条龙内部子项 | 先仅登记（不改为逐项提交，那是 A5） | OnOneKeyExecute 不动执行流 |

### 阶段 A3：观察面统一 ✅ 已完成 2026-09-13（A3.1~A3.4 + 心跳，见 §12）

- `job.*` 事件族 + `task.*` 别名双发；hello 宣告新 capability。
- `ext.job.status` / `ext.job.list`；bgiEpoch 全帧携带（§4.2）。
- 幂等判定上移到注册表（F11），TTL 上调（§4.1）。
- v2 `task.status` 内部改读注册表，格式不变。

### 阶段 A4：助手 reconcile 化（本次事故真正闭环） ✅ 已完成 2026-09-13（A4.1~A4.5 + F9-2，见 §12）

- 批次循环（MainViewModel.cs:6715-6805）改为 reconcile 循环（§4.5）：
  事件加速 + 10s 节拍对账，批次推进与 RunSpecified 收尾由对账驱动。
- 删除 `_hasRestartedThisBatch`：回退重启后由 reconcile 在 BGI 就绪后重新声明
  期望批次；命令行 `--startGroups` 回退在助手侧废弃（BGI 侧保留给单机用户）。
- MainViewModel 拆分（F9）：连接生命周期三份复刻收拢为一份。
- 助手侧核心纯逻辑补单测（F10）。
- **锚点（验收即此项）**：故障注入重放本次事故——冷启动回退 + 慢开门 40s，
  断言"锄地继续"严格在三个联机队长组全部终态后触发。

### 阶段 A5：一条龙进度事件化（形态 A） ✅ 已完成 2026-09-13（A5-1/A5-2/A5-3，见 §12）

- 启用 `parentJobId`：龙为父作业，子项逐个提交子作业——天然修复"项间锁空闲
  被插队后该项静默跳过"（现在显式 rejected，龙可选择等待或终止）。
- 执行期间回写龙级水位线：修复既有缺陷——OnOneKeyExecute 开头把 NextTaskIndex
  置 0 后执行中不回写，导致 suspend/resume 时 OneDragonTaskIndex 恒 0、
  恢复必从头重跑（InstanceRequestHandler.cs:1375）。
- ext 新增 `job.progress`（父作业视角：currentIndex/total/currentItemName）。

### 原 B1：整体对象化茶包一条龙 —— 已替代

不再把旧 `OnOneKeyExecute` 整体抽成 `OneDragonPlanExecutor`。R1 负责旧数据转标准配置+增强流程；R3 恢复公版模型/页面/原生主流程，仅抽必要的单项执行适配和运行上下文。账号/定时/循环策略移到助手 R4，图像识别和输入留 BGI。

### 原 B2：子作业图与整龙租约 —— 拆入 R2/R4/R5

R2 先完成稳定 taskId、真实结果、幂等身份、恢复祖先链、配置应用回执与三端兼容。R4 才能按快照展开正确的节点序列。R5 用逻辑所有者租约防插队，不能父锁等子锁；所有原生入口也纳入仲裁。`BatchGroupNames` 的现有判断晚于执行，先修过渡路径，再随节点身份完备而退役。

### 原 C1：接管态 —— 纳入 R5，F11 始终保留

接管继续采用可逆运行态 `local | managed`，声明 owner/epoch/到期与释放；BGI 相关页面呈只读控制视图，并在热键/CLI/IPC/网页/恢复受理端校验所有者。不能只禁用按钮；**F11 手动停止永远可用**。助手失联要按有界退出与租约规则恢复本地控制，不能把只读状态写成永久配置。服务器不是本机接管前提。

## 8. 明确不做（防范围蔓延）

- 不改 v2 信封格式、不升 protocolVersion 3。
- 不做 Temporal 式持久化重放（§4.3 取舍）。
- 不改变 SignalR 的定向转发职责；本次必须适配经过服务器/网页的任务 ID、配置应用回执、修订与缓存，不能以“只管助手↔BGI”遗漏现有调用方。
- A 阶段不解构一条龙。
- 不引入单个 BGI 实例内的并行游戏动作；多实例按现有实例隔离，各自串行，不用一个全局锁串住所有实例。
- 瞬态宏类热键动作不进 Job 模型（A2.5 ADR）。

## 9. 风险与回退

| 风险 | 缓解 |
|---|---|
| A2 动 UI/热键路径，单机用户回归 | 每片只包提交壳，执行体原样；冒烟清单覆盖无助手全场景 |
| 注册表/仲裁异常造成错误受理 | 明确失败并拒绝新的受影响请求，保留手动停止和诊断；不能承诺基础设施故障仅丢观察性后继续盲跑 |
| 事件双发期助手收到重复终态 | jobId 幂等去重；A4 完成后删别名 |
| 内存注册表崩溃丢作业被误判"跑完了" | 纪元 fencing（§4.2）+ reconcile（§4.5）：重启后助手看到的是"全丢"而非"全完" |
| 心跳开销/误报卡死 | 无订阅者不发心跳；卡死判定需 2 个间隔 + 连接健康双条件 |
| 一条龙父子模型与既有 suspend/resume 冲突 | A5 先修水位线缺陷再引入父子模型；resume 单测覆盖 |
| reconcile 节拍延迟批次推进 | 事件加速下正常路径零延迟；节拍只是兜底收敛 |

## 10. 验收标准

- A4 完成 = 本次事故场景（冷启动回退 + 慢开门）在故障注入下行为正确：
  完成后动作严格在批次全部终态后触发，日志链完整可查。
- A5 完成 = 外部观察者仅凭 ext 事件 + job.list 即可完整还原任意执行轨迹，
  包括用户手动启动的（Attended 透明）。
- R3 完成 = 公版标准配置互读互写、原生 UI/流程和默认参数恢复，无助手可用；R4 完成 = 旧茶包增强按迁移图完整承接，不重复执行原生整链。
- R5/R6 完成 = 槲寄生纯本机完成“定时 → 打断 → 批次 → 完成动作 → 恢复”，可解除接管，F11 不受接管限制；迁移/重启/回滚无双跑无丢配置，标准文件在干净公版直接互用。
  详细矩阵以执行方案 §9 为准，构建、单测、实机证据分别登记。
- 全程：老助手连新 BGI、新助手连老 BGI 可用（capability 裁剪验证）。

## 11. 附录：代码审计清单（v2 全量）

**BGI 执行核心**：TaskRunner.cs、Common/TaskControl.cs、Core/Script/CancellationContext.cs、
RunnerContext.cs、Service/ScriptService.cs（RunMulti/StartGameTask/ExecuteProject）、
GameTask/SoloTaskRegistry.cs、TaskProgress/TaskProgressManager.cs。

**BGI 入口面**：ScriptControlViewModel.cs（StartGroups/GetNextProjects/SetTaskContextNextFlag）、
OneDragonFlowViewModel.cs（OnOneKeyExecute/连续一条龙/NextTaskIndex）、
JsListViewModel.cs、MapPathingViewModel.cs、TaskSettingsPageViewModel.cs
（19 处 RunSoloTaskAsync + OnStopSoloTask）、HotKeyPageViewModel.cs（全热键分类）、
FeedWindowViewModel.cs、RedeemCodeManager.cs、MapEditorWebBridge.cs、
MusicPageViewModel.cs、KeyMouseRecordPageViewModel.cs、TaskTriggerDispatcher +
GameTaskManager（9 触发器）、ApplicationHostService.cs、App.xaml.cs（启动/退出接线）、
Helpers/RuntimeHelper.cs（InstanceBootstrap 时序）。

**BGI 通信面**：Service/Instance/ 全部（InstanceService/InstanceConnection/
InstanceContext/InstanceIpcProtocol/InstanceBootstrap/ReadOnlyStatusPipe/
RemoteEditSession/MessageHandlers 全 handler）、Service/ExternalInterface/ 全部
（BgiTaskCoordinator/Protocol/Session/EventHub/EventObserver/CommandPlane/QueryPlane/EventPlane）。

**BGI 一条龙**：OneDragonFlowViewModel.cs、Core/Config/OneDragonFlowConfig.cs、
Model/OneDragonTaskItem.cs、SuspendedTaskContext.cs。

**助手侧**：MainViewModel.cs（7100 行全量：连接生命周期/批次循环/远程命令分发/
恢复定时器/策略收尾）、CommandExecutor.cs、BgiExternalClient.cs、IpcClient.cs、
BgiProcessMonitor.cs、OnlineIntentLifecycle.cs、OnlineHoeingBatch、SignalRClient.cs、
MhaGatewayClient、AssistConfigManager.cs、TaskPolicySyncService、
RemoteConfigEditService、LocalPeerSyncService、MemberConfigCacheManager。

**参考文档**：.agents/rules/bgi-implementation-patterns-v2.md §26/§27、
Docs/design/multi-instance-ipc.md。

## 12. 实施进度

| 日期 | 阶段 | 内容 | 验证 |
|---|---|---|---|
| 2026-09-13 | A0 | 助手侧三修复：AssistConfigManager 容错加载+原子写（复用 LocalPeerSyncService.WriteFileAtomic）、BgiExternalClient 热重连退避（MinHealthyLifetime=5s）、MainViewModel 恢复定时器回调与 OnRemoteCommand lambda 加 try/catch 外壳 | MHA 0 error CS |
| 2026-09-13 | A1 | BGI 执行核心收口：TaskRunResult 显式化（RejectedSlotBusy 不再静默）；RunMulti 组级 ERR+提前 return；AutoTrack/AutoTrackPath 旁路持锁点标记；CancellationContext 三取消入口收敛 CancelCore、全字段进 _sync 锁、Cancel/Dispose 锁外执行；新建 Service/Execution（BgiJob/JobDescriptor/JobRegistry，Epoch 纪元 fencing，终态表 64 FIFO） | BGI 0 error CS；协调器单测 88/88 |
| 2026-09-13 | A2.1~A2.3 | 全入口注册表提交：TaskRunner.RunCurrentAsync 执行漏斗接线（锁前 Submit、抢锁失败 Rejected(task_busy)、拿锁 Running、finally 终态先于槽位释放/事件；容错留痕绝不影响执行）；RunMulti/StartGroups 元数据链（UI=Ui、命令行=Cli、v2 task.start/resume=V2+generation、IPC solo=V2、一条龙内部=OneDragonInternal、JS 列表/地图追踪=Script/Pathing）；ExecuteTaskStartCoreAsync 透传 generation（v2 handler 与协调器 pump 两调用方同步）；全部加法+默认参数，单机零感知 | BGI 0 error CS；88/88；post-edit 静态对账：JobDescriptor 仅在预期调用点，无辜调用方形态未变 |
| 2026-09-13 | A2.4 | BgiTaskCoordinator 整编进 JobRegistry（换壳不换芯）：taskHandle==jobId 同一 Guid 别名（BgiJob/JobRegistry.Submit 支持外部 jobId，显式指定时跳过按键认领防跨通道误并）；协调器入队即建 Queued 作业（Source=Ext）、QueueFull 登记 Rejected(queue_full)、派发点 TryMarkRunning、RecordTerminal 收口处终态同源进注册表（漏斗先写者赢，协调器兜底排队取消/等槽超时/Executor 异常三条漏斗不可达路径）；Executor 签名加首参 taskHandle 透传执行段，ExecuteTaskStartCoreAsync 加第 7 可选参 jobId（v2 直连默认 null 零变化）；漏斗 JobId 认领分支（认领失败退化新建+留痕） | BGI 0 error CS；协调器单测 17/17（新增 4 例：别名 Queued/排队取消 Cancelled/成功 Succeeded/等槽超时 Failed(task_busy)）；静态对账 Executor 单一生产构造点 |
| 2026-09-13 | A2.5 | 触发器只读视图：F5 画中画水位并集升级（TaskTriggerDispatcher:323 = 信号量空闲 ∧ 注册表无活跃作业；并集而非替换——AutoTrack/AutoTrackPath 旁路持锁点尚未进注册表，纯读注册表会漏占用；IsCreated 守卫不为读数创建单例）；F10 触发器总开关状态纳入注册表只读视图（JobRegistry.TriggerDispatcherRunning + Set 单一写入者=TaskTriggerDispatcher.Start/Stop，容错留痕；进程级运行态非 Job，A3 出口暴露） | BGI 0 error CS |

### ADR-2026-09-13：瞬态宏类热键动作不进 Job 模型

**决策**：F1 类瞬态宏（按下即发、松开即停的输入合成动作）不登记为 Job，仅保留日志痕。

**理由**：
1. 瞬态宏不占任务槽位（TaskSemaphore），无排队/终态/取消语义，与 Job 状态机（Queued→Running→终态）不匹配；强行纳入会产生海量秒级生灭作业，污染终态表与幂等索引。
2. 其可观察性需求已由既有日志覆盖；编排面（助手/槲寄生）不需要把瞬态宏纳入批次终态等待。
3. 对齐成熟编排系统惯例（UiPath attended macro、Temporal local activity 均不进入全局 job 编排视图）。

**反转条件**：若未来宏类动作需要参与批次编排或远程下发，以轻量子作业模型另立 ADR，不直接复用 BgiJob。
| 2026-09-13 | A2.6 | 一条龙内部子项仅登记：龙内默认条目（自动秘境/首领讨伐等，OneDragonFlowViewModel:2525）带 Solo/OneDragonInternal 描述符进漏斗（配置组条目 A2.1 已覆盖）；龙父作业仅 IPC 入口登记/认领（ExecuteTaskStartCoreAsync configName 分支：v2 直连新建 Source=V2、协调器派发认领 Source=Ext，终态按 WasCancelled/异常在执行段括号内登记，协调器 RecordTerminal 重复登记幂等无操作）。**显式边界**：UID 验证/兑换码检查等前导步骤与计划表等待循环/收尾通知块不登记（非子项）；UI 手动入口的龙父作业与 ParentJobId 父子链接推迟 B1；执行流零改动 | BGI 0 error CS；协调器单测 17/17 |
| 2026-09-13 | A3.1~A3.2 | 观察面统一（推+拉）：job.* 事件族（JobRegistry.Transitioned 唯一事实源，锁外触发；EventHub.PublishJobTransition 状态映射——Rejected 归并 job.failed 由 errorCode 区分，Cancelling 不发仅留 stateHistory）；注册表懒工厂接线 EventHub（无注册表使用则无订阅关系，保懒语义）；hello 增 capability `job.registry`；bgiEpoch 静态化（CurrentEpoch）并全帧携带（hello + BuildEventData 每个事件帧 + job 查询响应）；ext.job.status{jobId}/ext.job.list 入 QueryPlane（IsCreated 守卫不为查询建单例；job.list 附带 triggerDispatcherRunning 出口） | BGI 0 error CS |
| 2026-09-13 | A3.3~A3.4 | v2 task.status 内读注册表（格式零增删：running/slotOccupied=信号量∨注册表在跑 并集，taskName/groupName 注册表兜底——并集/兜底而非替换，龙内前导与计划表等待等未登记持锁路径不丢真）；幂等窗口上移注册表（进程级跨连接存活，TTL 60s→30min，失败不缓存沿用，**重放重写 RequestId** 否则跨连接重放被客户端相关器丢弃；连接级 _idempotencyWindow/PruneExpiredWindowEntries/IdempotencyWindowSeconds 全删） | BGI 0 error CS；新增 JobRegistryTests 6 例（事件出口/采用/先写者赢/显式 jobId 跳过按键采用/重放重写 RequestId/纪元一致）全绿：UnitTest 23/23、Server 88/88 |

**A3 遗留切片**：~~job.heartbeat 发布器（§4.4，30s 在跑心跳——事件名已放行订阅，发布器随 A4 reconcile 需求接线）。~~ 已随 A4 落地（见下表 A3-心跳行）。

| 2026-09-13 | A3-心跳 | job.heartbeat 发布器落地：JobRegistry 内 30s 进程级 Timer（ctor 参数 startHeartbeatTimer 控制——测试实例传 false，活动 Timer 被计时器队列根住不可回收，用例隔离实例不泄漏）；每拍锁内收集 Running 作业并刷 LastHeartbeatAtUtc、锁外触发新增 Heartbeated 事件（纪律同 Transitioned：异常吞咽不反噬状态机）；EventHub.PublishJobHeartbeat 唯一出口（payload 带 jobId/kind/name/source/generation/lastHeartbeatAtUtc，纪元由 BuildEventData 全帧携带）；助手侧 job.heartbeat 仅作存活信号、不唤醒 reconcile 循环 | BGI 0 error CS；JobRegistryTests 6 例随 ServiceTests 56/56 全绿 |
| 2026-09-13 | A4.1+A4.2+A4.6 | 助手 reconcile 工程骨架：BgiExternalClient 增 job.* 六事件常量（入 All 订阅）、capability `job.registry`、ext.job.status/ext.job.list 拉取（QueryJobListAsync/QueryJobStatusAsync 返回 null=瞬态失败不误判）与 BgiEpoch/BgiJobInfo/BgiJobListSnapshot 投影；新建 BatchReconcilePlan.cs——BatchExpectedItem（PendingSubmit→Submitted→TerminalConfirmed）+ BatchJobObservation（纯 BCL record）+ BatchReconcileDecider 纯函数（MaxSubmitAttempts=3；cancelled/WasCancelled→ConfirmTerminal+Abort 立即返回；函数内不改 items 状态，confirmedThisTick 局部集合做拍内推进依据）；新建 Test/MultiplayerHoeingAssistant.UnitTest 工程（xunit，ProjectReference 取 MHA Services 纯逻辑类） | MHA 0 error CS；新测试工程 11/11 全绿（含事故回放锚点：Complete 严格在三组全终态后才允许输出） |
| 2026-09-13 | A4.3 | MainViewModel 批次循环 reconcile 化（capability 门控）：ext Ready ∧ HasCapability(job.registry) → RunBatchReconcileLoopAsync（每拍必拉 ext.job.list=事实源，job.* 事件仅经 _batchWakeSignal TCS 唤醒=快速路径，10s 节拍兜底；纪元 fencing——首拍捕获、失配时 Submitted 项退回 PendingSubmit 重对账；观察输入=本批 generation ∪ 已附着 jobId）；老 BGI 无能力位走原阻塞式 for 循环（逐字节保留，仅缩进进 else 块）。动作应用：Submit/Resubmit→SubmitBatchItemAsync（业务拒绝/already_executed 直接记终态，同旧循环"启动失败跳过/幂等按成功"语义；通道瞬态失败保持 Submitted 无 jobId 待附着自愈）；AbortUserCancelled→batch.Cancel()+teardown(userCancelled:true)；Complete→落既有策略收尾出口。收尾三语义（F11/外部取消/正常完成）与旧循环逐条对齐 | MHA 0 error CS；11/11 |
| 2026-09-13 | A4.4 | 废弃 --startGroups/--startOneDragon 命令行回退黑盒：CommandExecutor.StartGroupAsync/StartOneClickAsync 回退重构为有界尝试循环（attempt 0 Connect 失败→RestartBgiControlledAsync(null) 裸拉起→WaitForBgiIpcReadyAsync→本方法内重试一次 task.start；拒绝/传输失败语义不变）；`_hasRestartedThisBatch` 散落标记与 ResetBatch() 全删（MainViewModel 6 调用点同步清除）——命令行执行路径消失后"命令行执行期间 IPC 假空闲双入口"竞态随之消失；MainViewModel CommandExecutor==null 路径改裸拉起+指引日志。BGI 侧 --startGroups/--batchGroups CLI 与 task.start 的 batchGroupNames 协议字段保留（一条龙龙内跳过判定 OneDragonFlowViewModel:2592 在用） | MHA 0 error CS；11/11；静态对账 _hasRestartedThisBatch/ResetBatch 生产代码零命中 |

**A4 遗留切片**：~~A4.5（F9 MainViewModel 拆分）推迟到实机验证后。~~ 两个阶段均已完成（见下表 A4.5 / F9-2 行）。已知轻微项：末项在提交即被拒/幂等命中时，批次收尾最多晚一拍（≤10s 节拍兜底，事件无触发源），可接受。

| 2026-09-13 | A4.5 | MainViewModel 拆分第一阶段（F9）：7329 行上帝类 partial 化，纯代码搬移零行为变化——拆出 `MainViewModel.SignalR.cs`（160 行，ConnectSignalRAsync/单机切换簇）、`MainViewModel.BgiExternal.cs`（225 行，ext 通道生命周期/事件回调/快照应用）、`MainViewModel.OnlineBatch.cs`（468 行，AllReady 批次 + reconcile 循环 + 策略收尾）；共用字段（_signalRClient/_activeBatch/_teardownGate 等）留主文件；主文件降至 6506 行。验证：拆分前后逐字节断言一致 + 方法多重集 111/111；三份连接复刻收拢（F9 第二阶段）不在本次范围 | MHA 0 error CS；11/11；commit 77a26023 |
| 2026-09-13 | F9-2 | 连接生命周期三份复刻收拢：首连/刷新/失败重试统一为 SignalR.cs 四原语（CreateWiredSignalRClient/ConnectSignalRWithConfigAsync/AdoptConnectedSignalRClientAsync/StartSignalRRetryTimer）；RefreshCompleted 触发参数化保留原行为。有意对齐四处语义裂缝（取较新加固版）：首连重试改每轮新建客户端、首连补在飞切单机不接管、重试定时器有在位连接时自我销毁（原空转）、RefreshCompleted 统一 Dispatcher 内触发；所有权移交挪进 Dispatcher.Invoke 防关机竞态污染。同日另附实机修复 4 例：完成后动作监控端 stale（6e34cf17 实拉+镜像）、空批次误触发 RunSpecified（4a5271a0 Started 守卫）、绑定弹窗一条龙被翻配置组（e439b3ae 回填带前缀）、完成后动作类型切换错配（8cbd4ddc）；冷启动快速通道（d323ec7b 省 ~40s suspend/settle 空等） | MHA 0 error CS；11/11；commit c4852b30 |
| 2026-09-13 | A5-1 | 龙级水位线回写（A5 第一片，先修缺陷再引父子模型）：OneDragonFlowViewModel 新增运行时水位线 `_currentExecutingTaskIndex`（volatile，写=执行线程/读=IPC 线程），OnOneKeyExecute 每轮开头清零、执行循环每条目（含批次跳过项——已逻辑启动）回写 task.Index；HandleTaskSuspend 改读水位线（原读 NextTaskIndex——执行开头消费即清零，执行中恒 0，resume 必从头重跑的根因）；suspend 保存日志扩展携带 OneDragonIndex（探针留痕锚点）。NextTaskIndex 用户标记语义（从此执行/显示 IsNextTask/消费清零）与 resume 回灌路径零改动；水位线不持久化、不进 UI，单机零感知。遗留边界：默认条目（自动秘境等）执行中 suspend 仍识别不到 onedragon（taskProgress 仅配置组分支写入），另行处理 | BGI 0 error CS；BGI 单测（537 例基线，14 个 FsCheck PBT 区预存失败非回归） |
| 2026-09-13 | A5-2 | 父子作业模型落地：OnOneKeyExecute 拆壳/核（壳=[RelayCommand] 父作业生命周期，核=原 400 行执行体逐字节不动）；父作业全入口覆盖——IPC task.start 预置认领（RunnerContext.OneDragonParentJobId，终态仍由 ExecuteTaskStartCoreAsync 单一登记，壳不抢先写者）、resume/CLI 经 OneDragonJobSourceHint 提示新建（Resume/Cli）、UI/连续一条龙默认 Ui 每轮一父作业；龙内两子项分发点挂 ParentJobId（默认条目 Solo + 配置组 Group，均 OneDragonInternal）；项间槽位抢占从静默跳过变显式 Rejected(task_busy)+响亮日志（默认条目分支观察 TaskRunResult；等待/终止策略留 B2 整龙租约）；壳终态口径=faulted→Failed/_finishMark→Succeeded/其余→Cancelled（壳开头重置 _finishMark 消跨轮残留）。**同片修复 A5-1 集成缺口**：resume 回灌 NextTaskIndex 前必须 WriteConfig 持久化+同步 SelectedOneDragonFlowConfigName——OnOneKeyExecute 开头 InitConfigList 从磁盘重载并整体替换 SelectedConfig，仅写内存会被覆盖（A5-1 水位线此前到不了执行） | BGI 0 error CS；单测 524/538（14 预存失败非回归；新增父子拓扑用例：链接/子终态不扩散/Rejected 可查/父终态先写者赢） |
| 2026-09-13 | A5-3 | ext 新增 job.progress（A5 收尾，父作业视角）：ExternalInterfaceEventNames 增 JobProgress 入 All/IsKnown；EventHub.PublishJobProgress 唯一出口（jobId=父作业、currentIndex=启用序列 1-based 序号、total=启用条目总数、currentItemName；容错不外抛）；唯一挂载点=OnOneKeyExecuteCore 执行循环水位线推进处（批次跳过项同样推进=已逻辑启动；父作业登记失败为空时跳过发布）。助手侧 BgiExternalClient 增常量并入 All 订阅，事件分发静默落地不唤醒 reconcile（进度非状态迁移）。A5 验收口径达成：外部观察者凭 job.queued/started/progress/completed 事件族 + ext.job.list 可还原含手动启动（Attended）的任意龙执行轨迹 | BGI/MHA 0 error CS；新增锚点 2 例（事件名登记/发布不外抛） |

### ADR-2026-09-16：有界退出契约（bounded quiesce）

**背景**：owner 对槲寄生接管的验收语义是四句话——完全接管、想什么时候启动就什么时候启动、
随便停、随时知道 BGI 在干什么。其中"随时启动/随便停"的物理基础是"取消必然在有界时间内
生效"，而现状取消是协作式的，任务在检查点之间不看令牌就死不了；`HandleTaskSuspend` 等锁
5s 超时还谎报 `suspended`（实机审计实锤）。零延迟启动在协作式执行中不存在，能承诺且必须
承诺的是**有界延迟**。

**决策**：
1. 取消/抢占请求发出后，执行器必须在显式常量 **QuiesceBound = 30s** 内确认任务死亡并归还
   槽位；等待方以"槽位确认空闲"这一**状态**为通过判据，时间只是上限不是通过条件。
2. 超界即**响亮失败**（BGI 通知 + 助手横幅 + 批次报告），并逐级升级：助手侧按既有
   BgiProcessMonitor 进程仲裁器重启 BGI（有意杀死豁免崩溃误判语义不变）。
3. 任何等待路径不得再"超时仍回报成功"——`suspend`/`task.start` 响应新增加法布尔
   `liveTask`/`quiesceConfirmed`，`status` 字符串保持旧值（老助手零感知）。
4. 瞬态拒绝分类：`queue_full`/`task_busy`/`preempt_timeout` 可重试（每项 2min 预算退避）；
   `manual_stop_cooldown` 归终态+响亮通知**不重试**（F11 是用户最终权威，d4dc54a9 教训）。

**反转条件**：若未来执行器改为可强制中断的解释器宿主（JS/宏全部引擎级可杀），QuiesceBound
可收紧；契约本身（状态判据 + 超时必响亮 + 有兜底升级）不反转。

### 阶段 A6：抢占闭环真实化（租约语义的 local 态子集）

**定位**：本阶段是 B2 整龙租约/C1 接管态的 **local 态地基**。所有机制按租约语义命名与建模，
后续在 R5 演化为所有者租约而非拆除重来。原“managed 直接旁路 PreemptionGate”仅为早期设想：
须先在所有入口证明 owner/leaseEpoch 和取消仲裁等价，才可简化相应路径；不能因 managed 标志直接绕过门。local 态继续服役。

**根因（2026-09-16 实机事故）**：AllReady→suspend 的 roundtrip（~2s）确定性落在"上线信号
任务已自结、下一条龙条目未起步"的组间缝隙：suspend 依 RunnerContext 残留把已死信号任务当
受害者，`Cancel()` 打在已 disposed 的取消上下文上（CancelCore 早退=空枪）；下一条目拿锁后
`RunCurrentAsync` 无条件 `Set()` 重建上下文从此免疫；ext 队列等槽 15s 判死 task_busy，
reconcile 当终态跳过——锄地静默整批丢失。核心教训：**抢占意图不能寄生在会被 Set/Clear
重置的取消令牌上，必须是独立结构**。

**机制**：
- `PreemptionGate`（BGI 进程级静态，租约意图门）：`Arm()`（suspend/抢占声明租约意图，代际
  递增）、`IsArmed`（TTL 90s 惰性过期）、`SavedForEpoch`（每代际中断上下文只存一次）。
  锁内只做标志读写，等待一律锁外。F11/F12 手动停止 = 用户收租（Disarm）。
- 唯一强制让位点：`TaskRunner.RunCurrentAsync` 拿锁成功后、一切副作用（job 推进/started
  事件/RunnerContext.Clear）**之前**——armed 且来源非 IPC（JobSource ∈ {V2,Ext,Resume}
  豁免并在持锁处原子消费门）时：捕获保存中断上下文（每代际一次）→ 放锁返回新枚举
  `TaskRunResult.Preempted`，静默（无 started 事件无 toast），job 登记 Cancelled(preempted)。
- 安静停点：`ScriptService.RunMulti` 项目循环与一条龙条目循环各加门检查，armed 时停组/
  停龙并发**一条**"已让位联机锄地"通知，避免级联撞门与 toast 刷屏。
- `SuspendCurrentTaskCore`：HandleTaskSuspend 步骤 1-5 抽共用原语，三处复用（IPC suspend、
  ext pump 抢占路径、ExecuteTaskStartCoreAsync 接通 :686-701 僵尸抢占代码）。用信号量区分
  活体/残留：无活体→不保存+Arm+响应 `liveTask:false`；有活体→照旧保存（2.5 WasCancelled、
  2.6 信号任务规则原样）+Cancel+等释放（QuiesceBound 内以槽位空闲为准，超界
  `quiesceConfirmed:false`）。ext.task.start 项加 `preempt` 标志（助手锄地批次/按键抢占置位）。
- 助手侧：settle 改状态确认（槽位空闲为准、超时再查一次仍忙则中止+告警）；reconcile 按
  ADR-2026-09-16 第 4 条分类重试；远端下发补启动结果回执（服务端转发，加法，老端降级无回执）。

**已拍板决策（owner 2026-09-16）**：D1 F11 冷却 = 终态+通知不重试；D2 门 armed 期间用户
UI 手动启动 = 让位给锄地并通知原因（F11 永远是否决键）。

**回归守卫**：ABABAB 不复发（2.6 原样保留，信号任务永不进 SuspendedTaskContext）；F11 语义
与 30s 冷却不变；孤儿上下文对账不受影响；批次吞组不复发；老助手连新 BGI 可用（capability
裁剪）。验收：A4 故障注入锚点 + 组间缝隙场景重放（06:39 日志时间线逐秒对照）。

| 2026-09-16 | A6 | 抢占闭环真实化全量落地。BGI：新增 Service/Execution/PreemptionGate.cs（租约意图门：Arm/Disarm/ConsumeIfServedBy/ShouldYield/TryMarkContextSaved，TTL 90s、QuiesceBound 30s、PreemptTimeoutException）+ SuspendContextCapture.cs（捕获/保存单一事实源，2.5/2.6 收口；CaptureForYield 只信组级/龙级数据——项目级信息在让位点必为残留）；TaskRunner 让位点（持锁后、Running 推进前：IPC 来源原子消费门，本地来源让位→Cancelled(preempted)→放锁发 slotReleased→每代际存一次恢复点+Toast 一次，返回新增 TaskRunResult.Preempted）；CancellationContext.CancelCore manualStop 分支 Disarm（F11 收租）；HandleTaskSuspend 重写（Arm+信号量活体判定：无活体→不保存不取消只留门、status 兼容+suspended/no_task+liveTask:false；活体→捕获保存+Cancel+30s 状态确认 quiesceConfirmed，未识别持锁者不再豁免中断）；ExecuteTaskStartCoreAsync 增 preempt 可选参（默认 false 旧行为逐字节；true 时条件 Arm+保存+Cancel+有界等待，超界抛 PreemptTimeoutException）；ext pump preempt 项等槽 3s 转主动抢占、preempt_timeout 独立错误码；RunMulti 增 Preempted 分支（不走"执行结束"假收尾）；审查修复三连——门判定移到 Running 推进前（消 Running 残帧）、preempt 条件 Arm（保代际守卫）、Save 成功即标记代际（首受害者优先）。助手：settle 状态确认（超时复核仍忙→中止+响亮告警，四调用方全 honor）；suspend 客户端超时 8s→35s（覆盖 30s 契约）；quiesceConfirmed=false→告警+仲裁器重启 BGI；reconcile 拒绝分类（queue_full/task_busy/preempt_timeout 退避重试 2min/12 次帽；manual_stop_cooldown 终态+通知不重发——owner D1）；AllReady 批次 ext.task.start 置 preempt:true；NotifyLoud/NotifyBatchLoud 告警通道。服务端+助手：control.reportCommandResult/remoteCommandResult 远端回执（目标机上报→服务端按 SenderUid 双路由回投→发起方卡片显示；老端降级"已发送（无回执）"）。**设计偏差留痕**：ADR 中"RunMulti/一条龙循环安静停点"未实施——让位点级联已静默（每代际仅首者通知+保存），实测路径覆盖，不额外动循环 | BGI/MHA 0 error CS；Server 251/251、MHA 单测 128/128（新增 17 例）、BGI 单测 753/767（14 例预存 FsCheck PBT 失败非回归，均在 AutoHoeing/OCR/AutoPathing 区）；post-edit 审查修复 3 项见左 |

## 13. ADR-2026-09-17：公版标准一条龙与增强编排分离

**背景与决策**：用户要求减少茶包/公版一条龙 diff，解决配置不能直接分享的问题。经模型、执行及衍生跨端调用审计，决定恢复公版原生链，旧计划/循环/账号等由槲寄生承接。不是仅恢复页面，也不保留另一套完整茶包一条龙执行器。

**事实纠偏**（本轮是静态审查，未实机复现）：

| 旧计划假设 | 本轮核实 | 对应行动 |
|---|---|---|
| A5 有父子登记就已闭环 | TaskRunner 吞执行异常后仍可返回 Ran；拒绝/组异常可能继续，IPC 缺配置可走成功；关系登记没有自动汇总 | R2 类型化结果与父终态合同；不能按正常返回推断成功 |
| 只修整数水位即可恢复 | CaptureCurrent 先命中组内项目可丢龙祖先；捕获读 DI VM，热键可用另一 VM | R2 独立不可变运行上下文，稳定 taskId/配置修订/祖先链 |
| generation+name 幂等够用 | 同名重复节点、父与子同名以及不同显式 key 仍可能归并 | R2 分离实例/epoch/run/node/iteration/attempt |
| 已登记就都进同一队列 | ext 队列之外仍有原生直接 TaskRunner 入口 | R2/R5 全入口 admission 与逻辑租约；实际锁仅叶子持有 |
| BatchGroupNames 能跳过已跑组 | 茶包判断在执行之后，日志跳过不能证明没运行 | 过渡修复并加行为回归，再退休名称补丁 |
| 旧“转公版”可当迁移器 | 会以任务名覆盖重复项；旧数字键大小不是执行顺序，公版已有 ID/order/definitions | R1 独立无副作用转换+预览+稳定映射，R5 事务切换 |
| 已有远程启动回执覆盖配置编辑 | 网页等的是转发 ack，执行端配置写超时仍可继续；原回执主要覆盖启动 | R2 config-applied + revision，三端同步，旧能力门控 |
| 一条龙增强都在 VM | 共享树脂引擎、间接读配置的合成/壶任务、改名引用、脚本/联机均有依赖 | R3 保留共享增强，参数显式化；R4 承接策略 |
| 槲寄生已有启动流程就有任务引擎 | enterTaskCenter 占位，运行状态存储尚未具备 | R4 Store/Planner/Reconciler 与可操作任务中心 |

**边界**：现有作业注册表、抢占、F11、联机信号过滤和批次对账保留；宏类不登记 Job 的决定不变。公版配置组混排、每周秘境、首领/幽境和地脉是原生能力，不能误删。独立锄地、SoloTask、战斗/传送双实现不随旧计划表恢复。

**发布条件**：候选迁移先于切换，BGI 原生恢复和增强承接都通过后才对既有用户默认启用；转换后的标准文件与增强包分离。缺依赖、非法水位、特殊路由投影或时间回退差异必须出报告，不能静默丢失。助手失联/重启未知状态不得自动当成功或整链重发。

**执行入口与状态**：[审计执行方案 §8](onedragon-public-compatibility-audit-2026-09-17.md#8-实施阶段与退出条件) 列 R0–R6 依赖、清单与退出条件，§9 列验证矩阵；[槲寄生主计划 §3](../../槲寄生调度器总计划.md#3-任务中心与恢复公版实施路线2026-09-17-生效) 维护产品进度。本轮完成审计/文档，产品阶段均等待用户审查并明确下令，没有引用旧 A6 测试计数作为本轮结果。补查确认公版已有四类树脂次数与 JSON 分支；页面后台/选择器依赖、原粹 20/40、地脉 JSON/局部参数、奖励等待、壶截图、幽境重试差异已纳入执行方案 R3/R6，详见逐项对照表。
