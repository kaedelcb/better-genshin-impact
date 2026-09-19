# R4.10 集成验收单（2026-09-19）

> 上游：[R4 施工分解](onedragon-r4-implementation-breakdown-2026-09-19.md) R4.10 行、
> [R4.9 设计稿](onedragon-r4-9-startup-handoff-2026-09-19.md) §8 留待清单（十轮终审 8 项）。
> 口径：构建 / 桩测试 / 集成 / 实机**分别报告**；惰性桩段与真实段分开；硬门槛=结果通道+收尾抑制+单项拒绝路径。
> 会诊记录：途中会诊（§4）+ 终审（§5）+ 终审复核第二/三轮（§6/§7），均逐条处置。

## 1. 回归基线（2026-09-19 本机实测）

**口径声明**：以下为「所列回归范围内未发现新增失败」，不是「全绿」——BGI 套件保留 14 项已登记既有失败（指纹见 §1.1）。
**BGI 双 trx 比较仅覆盖本轮 C2 变更前后**（助手侧改动由助手套件承担回归），不外推为「整个 R4 无回归」的全称证明。

| 套件 | 结果 | 证据口径 |
|---|---|---|
| 助手单测 | **368/368**（352 基线 + R4.10 新夹具 16；`ed04355d` 词汇修复+复核补例后 **380/380**=368+12 新夹具，见 §8） | 含生产接线夹具 R410ProductionWiringTests（A/B×4/C1–C5）+ 边界失败分类纯函数夹具 + Runner 混用分类夹具（理论 4 例+白名单 1 例）。诚实记录：曾观察到一次并行跑 `RunAction_Stop_ForwardsToHost_CancelsInFlight_Terminalizes` 自旋等待超时（359/360），单跑与全量复跑均通过——按**并行时序偶发**记录，持续观察，不做「非语义回归」全称断言 |
| 服务端 BgiCoordinatorServer.Tests | **276/276** | 本轮无服务端代码改动；基线为 R4.10 会话早前实测（含网页回执路由类 RemoteCommandResultRouting/RemoteConfigReplyRouting） |
| 迁移器合同回归 T01–T27 | **27/27**，退出码 0 | 本轮无迁移器改动；基线为 R4.10 会话早前实测（`Test/OneDragonMigration`） |
| IpcChannelContractAudit | **80/80**（78→80，新增 T72/T73） | T72=严格合同单项拒绝（校验器层）；T73=经 RouteAsync 完整路由的单项负向（执行/入队边界不触达）；T36/T37/T39 非严格单项选择对照不受影响 |
| IpcIncidentAudit | **37/37** | C2 闸门落地后复测，无影响 |
| BGI 全量单测 | **936/950 通过，14 失败=既有基线族（指纹 §1.1）** | C2 闸门落地前后各跑一次（`r410_bgi_full_20260919.trx` / `r410_bgi_full_c2_20260919.trx`），**14 失败指纹（名字+首行签名）逐一比对完全一致**；BGI 树自 0074ddf2 起仅本轮 C2 一处改动（合同校验器加闸，纯拒绝路径） |
| BGI 构建 | **0 错误**（仅既有警告） | `-p:DeployToBgiTools=false` |
| R3 守卫夹具 | 不退化（含于 BGI 936 通过集） | EvaluateNewConfigName 等 |

### 1.1 BGI 14 项既有失败指纹（trx 实测：名字 + 首行错误签名）

| 测试 | 首行签名 | 高可信候选归因（非闭环证明） |
|---|---|---|
| `MultiWorldConfigLockingBugTests.Round1_MemberFetchConfig_ShouldSaveToFirstHostConfig` | `Round 1, Member fetched config but _firstHostConfig is null` | 联机配置锁 bug 条件族（R4.6 记录基线已含此族） |
| `MultiWorldConfigLockingBugTests.Round1_MemberConfigFetchFails_ShouldTerminateMultiWorld` | `FsCheck.Xunit.PropertyFailedException` | 同上 |
| `MultiWorldConfigLockingBugTests.Round1_HostUploadConfig_ShouldSaveToFirstHostConfig` | `Round 1, Host uploaded config but _firstHostConfig is null` | 同上 |
| `MultiWorldConfigLockingBugTests.Round2Plus_HostFirstConfigNull_ShouldTerminateMultiWorld` | `FsCheck.Xunit.PropertyFailedException` | 同上 |
| `PostTeleportStuckProtectionDecisionsUnitTest.IsEligible_Stuck_BeyondWindow_ReturnsFalse` | `Assert.False() Failure` | b2a0646f StuckWindowSeconds 20→30 产品值与夹具失配（候选） |
| `PostTeleportStuckProtectionPbtPropertiesTest.IsEligible_BoundaryZeroAndTwenty_Stuck` | `FsCheck.Xunit.PropertyFailedException` | 同上族 |
| `PostTeleportRevivalProtectionBugConditionTest.BugCondition_A1_RevivalWithin2s_ShouldNotSkipToNextSegment` | `BUG CONFIRMED（EXPECTED TO FAIL in unfixed code）：2s 内复苏误判跳段` | **设计即失败**的 bug 条件记录测试（未修复执行器行为存档） |
| `PostTeleportRevivalProtectionBugConditionTest.BugCondition_A2_RevivalAtExactly10s_ShouldNotSkipToNextSegment` | `BUG CONFIRMED（EXPECTED TO FAIL…）：恰好 10s 复苏仍跳段` | 同上 |
| `WaitPointReportTests.WaitPointReport_SyncPointIdValidation_WorksCorrectly` | `FsCheck.Xunit.PropertyFailedException` | 等点上报族（R4.6 记录基线已含） |
| `WaitPointReportTests.WaitPointReport_Creation_IsValidWithRequiredFields` | `FsCheck.Xunit.PropertyFailedException` | 同上 |
| `WaitPointReportTests.WaitPointReport_ExpiryCheck_WorksCorrectly` | `FsCheck.Xunit.PropertyFailedException` | 同上 |
| `WaitPointReportTests.WaitPointReport_ExtractRouteIndex_HandlesVariousFormats` | `FsCheck.Xunit.PropertyFailedException` | 同上 |
| `OcrResultTests.Text_PreservesOfficialDetectionOrder` | `Assert.Equal() Failure: Strings differ` | OCR 族（R4.6 记录基线已含） |
| `AutoTrackPositionRecoveryDecisionsTest.Decide_TimesGte4_AlwaysThrowRetry` | `FsCheck.Xunit.PropertyFailedException` | 位置恢复族（R4.6 记录基线已含） |

失败族一致性说明：与 R4.6 阶段记录基线（922/936，14 失败族）族谱一致，**不据此断言早于 R4**；失败集合（名字+首行签名）在 C2 前后零变化。
**过程记录（诚实声明）**：R4.10 首次全量跑报 164/950 失败；clean 重建后恢复 936/950——「重建后失败数回落」是观察到的现象，
归因为陈旧构建产物污染是**高可信候选**（非唯一性证明）。经验：BGI 全量跑前须 clean 重建或对干净产物 `--no-build`。

## 2. 集成验收项状态（R4.10 行 + R4.9 §8 八项合并，两轮会诊处置后口径）

### 2.1 组件级/组装级已闭环（夹具证据，逐项标明证明边界）

- D13 候选/开发验证/正式激活三段纪律（**非三态词表**，重要7 更正）：R4.6 词表分离 + R4.8 边界三态化夹具；
- 断线/迟到/乱序/重复回执不推进错误节点：R4.8 BatchB 边界夹具（不确定发送幂等键对账绝不重发/终态纯观察/重复回执按事实补记不重跑）；
- 转发 ack/受理/入队不触发成功收尾：R4.6 E3' 收尾意图纪律（executed 才清偿）+ BGI 侧作业化（0074ddf2）；
- 全程不依赖 SignalR：任务中心全链本地（RunStore/WorkflowStore/ext IPC），助手可单机模式运行（既有总开关；两级开关已撤销——总计划 §1 规则 3）；
- 退出收敛（组件级）：宿主 ShutdownAsync 取消+限时收敛夹具、观察器 finally 出册直证（HasDrive）；
  **生产退出路径代码证据**：`MainViewModel.Shutdown()`（复核 重要3 重构：锁内「标志置位+捕获引用+字段脱离」原子段 →
    锁外 ShutdownAsync 12s 有界等待；首访持锁构造与退出同锁互斥——已消除首访构造漏捕获窗口，捕获实例按 §7 重要3 口径 best-effort 关闭）；
- R4.9 §8 场景 1–13 组件级证明（十轮终审口径，352 夹具基线）；
- **R4.10 生产接线集成夹具（新增 10，R410ProductionWiringTests）**——证明边界逐条标明：
  - A：Waiting 不占槽位——同宿主 arm Waiting 与另一流程 Succeeded 共存（真实 Host/Runner + 注入边界；
    **边界**：生产边界/真实协调器槽位行为留实机）；
  - B×4：对账命中判定纯函数（**三重纪元一致性已并入纯函数可测**：查询前=查询后连接纪元=快照自报 bgiEpoch=冻结纪元，
    任一不符/缺失→null；合同目标=进程纪元级一致性——同进程断连重连纪元不变，不否决已观察事实）；
    **边界**：真实重连端到端离线不可全真模拟；
  - C1：宿主单例（顺序访问）+ 槲寄生面板同一实例；**边界**：四入口运行时事件传播留实机（静态调用关系证据：
    主流程/定时/电子狗/日志共用 `_runner`，移交委托通向 `_mainVm.TaskCenterHost`，MistletoeViewModel.cs:44/57/642）；
  - C2：能力/快照/客户端三提供方活委托实况接线（反射布置状态取证，**边界**：绕过正常状态转换）；
  - C3：监控端 Start/移交入口 NoCapability 拒绝 + 未新建目录 + 未登记驱动（断言范围如此，不宣称全部副作用面）；
  - C4：并发首访单例（Barrier 受控起跑 16 路同实例——终审 重要3+复核 建议7：主证据=锁实现，夹具为并发回归探针；
    `MainViewModel.BgiExternal.cs` 属性锁内惰性初始化+`_disposing` 拒绝重建）；
  - C5：启动链接线（复核 阻断2 收窄=「直接调用移交方法的负向路径 + 委托绑定证据」，不宣称完整实测链）——
    Runner._enterTaskCenter 委托 Target+MethodInfo 断言（绑定到本 VM 的 EnterTaskCenterAsync）；监控端 VM 层拒绝
    （专有文案区分层次）；执行端穿透 VM→共享宿主→StatusUncertain（宿主专有词，链路到达）。**本机实测：执行端分支已执行**
    （运行目录干净，trx `r410_c5.trx` 输出留痕；前提不满足的机器将明确输出「未验证」而非静默通过）；
  - **终审复核新发现处置（生产加固）**：SubmitAsync 纪元单次读取（空检查与冻结同一局部值，消除断连清空二次读取 NRE 窗口）；
    失败响应分类白名单纯函数 `IsPreSideEffectRejection`（仅 capability_required/invalid_request/stale_epoch/request_expired/
    unsupported_operation/queue_full/task_busy 可证实未受理→Rejected；result_unknown/task_start_failed/无码等→Unknown 停驻待对账——
    失败回执不自动触发对账，依赖后续人工/恢复处置，不写成「已自动对账」）；
  - **复核三轮新发现处置（生产修复）**：`StartupFlowRunner.ExecuteBgiTaskCommitAsync` 混用事实登记同类分类遗漏——
    `IsProvenNotCommitted` 白名单纯函数（request_expired/capability_required/legacy_start_index_not_supported/batch_busy/task_busy/
    task_already_running 可证实未提交→不计；result_unknown/quiesce_timeout/task_start_failed/未知码/空白码→保守计入不确定事实，
    混用守卫不得绕过）+ 理论夹具 4 例（非白名单码→移交被阻断）；
- **执行端单项节点拒绝（组件级）**：`WorkflowPlannerTests.SingleTaskNode_RejectedWithoutSideEffects_NoCollectivePunishment`
  （task.single.native=false 时 Planner Reject、不进入执行、不记成功）；
- **C2 BGI 协议负向路径（本轮实现，双层闭环）**：严格合同（executionContractVersion=1）流程提交携 taskId →
  `ExecutionRequestContract` 显式 `capability_required` 拒绝（T72 校验器层 + T73 RouteAsync 路由层，副作用前短路——
  `DispatchTaskStartAsync` 首行 Validate 代码顺序证据）；R3 非严格单项选择 T36/T37/T39 不受影响；v2 旧入口不携 taskId 不受影响。

### 2.2 待实机/真实段（需运行中的 BGI（部分需游戏）+ owner 监督，开发验证用独立配置根）

| # | 项（终审 建议2：断言明确化） | 需要环境 | 阻塞 R4 收口？ |
|---|---|---|---|
| A | **硬门槛·结果通道**：整龙/配置组真实执行 + 链内单项子项结果传播。断言分别记录：**子作业原始终态**（failed/cancelled/skipped 各自到达注册表）、**根作业聚合终态**（子项 skipped ≠ 根失败；根可 Succeeded-with-skips）、**助手最终消费的 jobId 与词表** | BGI 进程+游戏 | **是** |
| B | **硬门槛·收尾抑制贯通链**：调用方 true → 载荷 true → BGI 消费 → 收尾未执行；**对照组**缺省 false 在**其余前提相同**下确实触发收尾（防止把「未走到收尾」误当抑制成功） | BGI 进程+游戏 | **是** |
| C1 | **硬门槛·单项拒绝（助手侧真实路径留痕）**：执行端单项节点提交被 Planner 拒绝——断言：拒绝不得记成功；停止策略不推进；成功收尾不授权；留痕可读 | BGI 进程（可不需游戏画面） | **是**（真实段观察） |
| C2 | ~~BGI 协议负向路径单项拒绝~~ | — | **已闭环（§2.1，T72+T73 双层）** |
| D | R4.9-1 端到端 UI 全链（新手动意图全链；A 受理 B 被拒不联动 StopBgi）。组件级证据=R4.9 回执三态夹具（不借组装夹具升级）；UI 贯通留实机 | BGI 进程+助手 UI | 否 |
| E | R4.9-2 方案 UI 入口（保存/恢复/导入导出） | 助手 UI | 否 |
| F | R4.9-3 四入口运行时事件传播（跨日排队/同日重复）。组装层=C1/C4/C5 + 静态调用关系证据；运行时表现留实机 | 助手 UI+BGI 进程 | 否 |
| G | R4.9-4 WPF STA 绑定行为（候选刷新/改名切换/闭合态显示/配置不回写清空）——明确延期实机窗口 | 助手 UI | 否 |
| H | R4.9-5 真实受理点崩溃恢复、关闭竞态、取消所有权边界。组件级已证 + 生产退出路径代码证据（§2.1）；退出入口端到端留实机 | BGI 进程+助手 | 否 |
| I | R4.9-6 能力/快照提供方接线 + 监控端零副作用完整面。组装层=C2/C3；真实连接事件到宿主行为贯通留实机 | 双端助手 | 否 |
| J | R4.9-7 保留与去重边界入档口径宣读（单宿主/台账永存/崩溃恢复，非跨进程仲裁） | 无需环境（口径确认） | 否 |
| K | R4.9-8 真实 Waiting 追加身份后自然到点冲突表现。组件级已证（R4.9 夹具，不借组装夹具升级）；自然到点 UI 表现留实机 | BGI 进程+助手 UI | 否 |
| L | 离线全链（独立配置根），证据分别陈述（复核 阻断2）：A=注入执行边界下宿主移交受理及 Waiting 行为已验证；C5=Runner 委托绑定+直接调用 VM 移交方法负向路径已验证（执行端分支本机已执行）；**生产启动入口→受理→等待的完整贯通链待实机** | 助手（独立配置根） | 否 |

> **A/B/C1 三段已于 2026-09-19 晚 owner 实机窗口通过**（跳过决策同日被补验取代），证据与断言映射见 §8。

### 2.3 `task.single.native` 开放评估

维持 **false**。开放前置（全部满足才可评估）：
1. 硬门槛 A/B/C1 真实段通过 + ASTRA 终审确认；
2. **独立单项正向执行验收**（开放前验收项，非 R4 收口项）；
3. 开放时移除 C2 合同闸门（`ExecutionRequestContract` 注释已标）并恢复单项修订号为前置校验。

## 3. 结论（本验收单当前状态，用例—证据—结论映射）

| 用例域 | 证据 | 结论 |
|---|---|---|
| 助手任务中心全机制（R4.1–R4.10） | 助手单测 368/368（含 R4.9 夹具 21 + R4.10 新夹具 16；`ed04355d` 后 380/380，§8） | 组件级+组装级闭环；本轮变更范围内未发现新增失败 |
| 网页回执路由（联机批次/远程配置组编辑兼容） | 服务端 276/276（本轮无改动，基线实测） | 未发现新增失败 |
| R3 守卫（UID 识别/兑换码/A6 抢占/配置名生成等） | BGI 936 通过集内含 R3 守卫夹具 | 未发现新增失败 |
| BGI 侧既有失败 | 14 项指纹（§1.1 名字+签名），C2 前后双 trx 一致 | **本轮 C2 变更前后失败集合零变化**；14 项与 R4.6 记录基线族一致 |
| IPC 合同（含 C2 单项闸门） | IpcChannelContractAudit 80/80（T72/T73）、IpcIncidentAudit 37/37 | 闭环 |
| 迁移产物合同（R1 schema 消费方兼容） | 迁移器 T01–T27（本轮无改动，基线实测） | 未发现新增失败 |

**总口径**：组件验证及部分生产组装验证完成（证明边界见 §2.1）；所列回归范围内未发现新增失败
（BGI 比较仅覆盖本轮 C2 变更前后）。本验收单为阶段记录，不签署「历次问题全部解除」。
**R4 收口阻塞项 = 硬门槛 A/B/C1 的真实段验证**（需 owner 实机窗口，独立配置根）；
其余实机项（D–L）不阻塞收口，可与硬门槛同一实机窗口一并观察；
`task.single.native` 维持 false（§2.3）。

**owner 决策登记（2026-09-19）**：owner 决策**跳过硬门槛 A/B/C1 实机真实段验收，直接进入 R5**——如实标注：**owner 决策跳过，非验证通过**；A/B/C1 维持未验证状态，本验收单不构成其解除依据。跳过项作为风险登记带入 R5 仲裁评审兜底（[R5 设计评审与分解](onedragon-r5-design-review-and-breakdown-2026-09-19.md) §6 R-0、§4 R5.8）；`task.single.native` 维持 false（§2.3 开放前置 1 未满足，贯穿 R5 不评估开放）；owner 实机窗口一旦出现，A/B/C1 补验优先于 R5 新功能实机项。

**owner 补验登记（2026-09-19 晚）**：跳过登记同日稍后即开实机窗口补验，**A/B/C1 三段全部通过**（证据 §8），R4 收口阻塞项解除；期间实锤并修复 ext.task.start 受理回执词汇 bug（`ed04355d`，TEST-2c/TEST-3 复跑通过=端到端证据）。`task.single.native` 维持 false（§2.3 开放前置 2 独立单项正向验收、3 移除 C2 闸门未做）。

## 4. ASTRA 途中会诊处置记录（3 阻断/4 重要/2 建议）

| # | 级别 | 发现 | 处置 |
|---|---|---|---|
| 1 | 阻断 | 验收单「全绿/无回归」越界 | §1 限定口径 + §1.1 指纹；终审复发部分见 §5-1 |
| 2 | 阻断 | 硬门槛 A/C 粒度过粗 | A/C 拆分（§2.2）；独立单项正向执行移 §2.3 |
| 3 | 阻断 | D–L 一刀切不阻塞遗漏关键接线 | 生产组装夹具 R410ProductionWiringTests + C2 合同闸门（终审复核：部分落实，续见 §5-2） |
| 4 | 重要 | 污染归因措辞过强 | §1.1 归因收窄为「高可信候选」（终审复发部分见 §5-1） |
| 5 | 重要 | B 缺贯通验证链 | §2.2 B 行贯通链口径（终审 建议2 再明确，§5） |
| 6 | 重要 | A 三态未区分子/根作业 | §2.2 A 行子/根作业区分（终审 建议2 再明确，§5） |
| 7 | 重要 | D13 被误写为三态 | §2.1 首项更正（终审复核：已落实） |
| 8 | 建议 | D/G/K 关键行为纳入一次接线验收 | D/K 组件级证据精确映射（不借组装夹具升级）；G 明确延期（终审复核后口径） |
| 9 | 建议 | 用例—证据—结论映射 | §3 映射表（终审复核：形式已落实，证据层次按 §5-1 收窄） |

## 5. ASTRA 终审处置记录（2 阻断/4 重要/2 建议）

| # | 级别 | 发现 | 处置 |
|---|---|---|---|
| 1 | 阻断 | §3 再次扩大回归结论（「R4 未引入 BGI 回归」「既有族」全称化）；指纹仅名字；164 归因过强；偶发断言过强 | §1/§3 统一限定「本轮 C2 变更前后失败集合零变化」；§1.1 指纹补首行签名+双 trx 文件名可追溯；164 归因收窄为现象+候选；偶发改「持续观察」 |
| 2 | 阻断 | 生产接线补证不完整（C1↔C3 错配；H/D/K 借组装夹具升级；缺真实发送/链路/退出证据） | C1 执行端单项拒绝改映射 `SingleTaskNode_RejectedWithoutSideEffects_NoCollectivePunishment`（组件级）；H 改代码证据（MainViewModel.cs:3865–3907）；D/K 不借组装升级（组件级精确映射）；补 C5 启动链真实接线夹具；真实 SubmitAsync 不确定发送/重连端到端离线不可达（BgiExternalClient 无连接时 ServerEpoch 缺失，提交在本地校验即拒）——诚实留界，列实机窗口 |
| 3 | 重要 | C2 仅校验器层已证；T36–T39 绕过 Validate 不能证明路由兼容 | 新增 T73（RouteAsync 完整路由负向，capability_required + 副作用前短路代码顺序证据）；「不得以纪元错误替代」限定语义已在闸门注释写明（前置字段无效的请求先返回对应错误不构成缺陷） |
| 4 | 重要 | 夹具环境依赖/清理缺口（C3 断言时机、C1 计时器泄漏、A 无 finally、出册窗口） | C3 目录断言移到构造前+断言范围收窄；C1/C5 finally 停 `_bgiStatusRefresh`；A 改 try/finally + 出册等待收敛；措辞改「反射布置测试状态并读取接线」 |
| 5 | 重要 | 宿主单例非线程安全（`??=` 竞态+退出后重建） | **生产修复**：`MainViewModel.BgiExternal.cs` 属性改锁内惰性初始化 + `_disposing` 拒绝重建；退出置空同锁（MainViewModel.cs:3905）；C4 并发夹具回归 |
| 6 | 重要 | 纪元纯函数未覆盖快照来源/查询窗口时序 | **生产加固**：对账改三重纪元一致性（查询前后连接纪元 + 快照自报 bgiEpoch 同帧证据——HandleJobList 恒发）；离线不可全真模拟已入 §2.1 边界声明 |
| 7 | 建议 | T40 编号碰撞（同文件已有 foreign takeover T40） | 新测试改 T72/T73（唯一编号） |
| 8 | 建议 | 真实段断言明确化 | §2.2 A（子作业原始终态/根聚合/助手消费分别记录）/B（对照组同前提确认收尾具备触发条件）/C1（拒绝不记成功+停止策略不推进+成功收尾不授权） |
## 6. ASTRA 终审复核（第二轮）处置记录（2 阻断残余/4 重要（含 2 新发现）/2 建议）

| # | 级别 | 发现 | 处置 |
|---|---|---|---|
| 1 | 阻断（残余） | §1「本轮 R4.10 变更前后」实为仅 C2；§1.1「早于 R4」无支撑 | §1 改「BGI 比较仅覆盖本轮 C2 变更前后」；§1.1 改「与 R4.6 记录失败族一致，不据此断言早于 R4」 |
| 2 | 阻断（残余） | C5 执行端断言条件内静默跳过；监控端 NoCapability 不分层；委托绑定无证据；与 A 拼接成链 | C5 收窄为「负向路径+委托绑定」：Runner._enterTaskCenter Target/Method 断言；监控端 VM 专有文案分层；执行端前提不满足时 ITestOutputHelper 明确报告未验证；不与夹具 A 拼接宣称完整链（§2.2 L 分别陈述） |
| 3 | 重要 | 退出协议竞态（首访持锁构造 vs 退出跳过 Shutdown 窗口） | **生产重构**：Shutdown() 锁内「_disposing 置位+捕获引用+字段脱离」原子段，锁外有界 ShutdownAsync；getter 与退出同锁互斥（持锁构造必被退出捕获收敛） |
| 4 | 重要 | 纪元校验注释过强（同进程重连纪元不变）+ 快照纪元分支无夹具 | 三重纪元判定并入 TryMatchReconcileHit 纯函数（B 夹具覆盖：查询窗口变化/快照缺帧/快照不符/冻结不符/全一致）；注释改合同目标=进程纪元级一致性 |
| 5 | 重要（新发现） | SubmitAsync 纪元检查与使用分离（二次读取 NRE 窗口在发送 try 之外） | **生产修复**：纪元单次读取到局部变量，空检查与冻结同值 |
| 6 | 重要（新发现） | 所有失败响应归类 Rejected 依据不足（体系存在 result_unknown/执行后失败） | **生产修复**：`IsPreSideEffectRejection` 白名单纯函数（7 码→Rejected；其余→Unknown 待对账）+ 夹具；非本轮引入问题的定性保留 |
| 7 | 建议 | C4 Parallel.For 不保证重叠 | Barrier 受控起跑；注明主证据=锁实现，夹具为探针 |
| 8 | 建议 | 合同校验重复代码+闸门注释绝对化 | 闸门注释加「前置合同字段有效时」限定；重复校验合并挂账 R5+（避免规则漂移） |

**复核后状态**：阻断 1/2 处置完毕待三轮复核确认；退出竞态与失败分类两项签署前关键项已生产修复+夹具回归。
## 7. ASTRA 终审复核（第三轮）处置记录（阻断1 解除/阻断2 残余 1 处文字/重要 3（含 1 新发现）/建议 2）

| # | 级别 | 发现 | 处置 |
|---|---|---|---|
| 1 | 阻断1 | — | **解除**（§1/§1.1/§3 口径已限定） |
| 2 | 阻断2（残余） | §2.2 L 行仍把 A/C5 拼接成连续证据链 | L 行改三项证据分别陈述：A（注入边界受理/Waiting）、C5（委托绑定+负向路径）、完整贯通链待实机——**按收窄口径解除** |
| 3 | 重要 | 退出：「立即拒绝新工作/必完成收敛」不能由代码推出；前置 Dispose 抛异常会跳过宿主捕获 | **生产重构**：捕获段前移到 Shutdown() 开头（锁内原子段），宿主收敛移入 finally 保证到达；口径改为「已消除首访构造漏捕获竞态；已捕获实例 best-effort 关闭（锁外最多 12s，超时仅结束等待）；既有引用并发准入由宿主自身 _shutdown 守卫承担」；getter 残余行为按此合同可接受 |
| 4 | 重要 | C4 Parallel.For+Barrier 调度性挂死风险 | C4 改显式 16 线程 + Barrier 受控起跑 + 30s 有界 Join（逐线程断言完成） |
| 5 | 重要（新发现） | `StartupFlowRunner.ExecuteBgiTaskCommitAsync` 混用事实登记同类分类遗漏（非空错误码一律不计入，result_unknown 会绕过混用守卫） | **生产修复**：`IsProvenNotCommitted` 白名单纯函数（6 码不计，其余保守计入）+ 理论夹具 4 例 + 白名单夹具；词表与边界层七码各自独立（本层为 v2/命令执行器码，已核实 CommandExecutor 可达码：result_unknown/quiesce_timeout/epoch_unknown 等） |
| 6 | 建议 | §3 数字滞后（362 vs 363）；C5 分支结果需记录 | §1/§3 同步 368/368；C5 本机执行端分支「已执行」记录（trx 留痕），前提不满足机器明确输出「未验证」 |
| 7 | 建议 | 委托断言 Target+Method.Name 可更精确 | 改 MethodInfo 直接比较 |

**第三轮复核确认（ASTRA 原文要点）**：TryMatchReconcileHit 纪元逻辑正确；SubmitAsync 单次读取修复成立；IsPreSideEffectRejection 七码白名单方向正确（各码全部产生位置未穷举——准入类「副作用前」语义以 BGI 侧合同注释+审计为准）；验收单可作为阶段记录收口；`task.single.native=false` 维持及开放前置合理。
## 8. 真实段验收记录（2026-09-19 晚，owner 实机窗口）

环境：dev 构建 BGI（Release 输出目录，独立 User 配置根，PID 30928）+ dev 助手（Administrator 会话，Tools 部署 19:42 起含 ed04355d 修复）+ 真实游戏；测试配置=「234」（收尾+领取每日奖励，完成后动作=关闭游戏）；测试流程文件由施工方预置（TEST 系列，owner 仅操作界面按钮）。

### A 段·结果通道 ✅

- TEST-2c（wf-test00a2c / run-20a8b296c2fd）：节点 1（n-a2skip，周一过滤，当天周六）→ `skippedFilter`；节点 2（n-a2run）真实执行 → `succeeded`（rawTerminal="succeeded"，jobId=2e7362ba…，observedTerminal="succeeded"）。
- 断言：子作业原始终态分别到达 ✅——n-a2skip 的 `skippedFilter` 为**助手侧节点闸门过滤词**（D15：skippedUser/skippedFilter 不阻断收尾；rawTerminal=null=未发送 BGI，不经 InterpretNodeJob，与 BGI 作业终态词 `skipped` 两个层面不混淆），n-a2run 为 BGI 注册表真实终态 succeeded；根作业聚合 ✅——根=RunStore run 本体，落盘字段 state=4（=WorkflowRunState.Succeeded，WorkflowRunModels.cs 枚举），skipped 子项不拖垮根（E9/D15）；助手最终消费的 jobId（2e7362ba…）与词表（observedTerminal=succeeded）正确 ✅。

### B 段·收尾抑制贯通链 ✅

- 实验组 TEST-3（wf-test00b3 / run-2bd7268f0cb2）：任务中心提交（固定 suppress=true）真实执行成功；BGI 日志「一条龙和配置组任务结束」后无完成后动作，**游戏保持运行**（owner 观察+日志无退出记录）✅。载荷链路定点：助手侧提交载荷构造 `BgiWorkflowExecutionBoundary.cs:96` `suppressConfigCompletionAction = request.SuppressConfigCompletionAction`（任务中心固定 true，行 21 注释 B6/E4'）→ BGI 消费结果=收尾未执行+对照组执行，中间环以代码定点+双向结果证据支撑。
- 对照组：同配置「234」经 BGI 原生界面启动（其余前提相同），跑完**游戏被关闭**（日志 19:53:07「游戏已退出，BetterGI 自动停止截图器」；原生路径无 RunStore 记录，留痕=BGI 日志+owner 观察）——证明收尾具备触发条件，实验组的未关闭=真抑制，非「未走到收尾」假象 ✅。

### C1 段·单项拒绝留痕 ✅

- TEST-1（wf-test00c1 / run-a119ec48b97a）：单项节点被 Planner 响亮拒绝（task.single.native=false）——`result:"rejected"` 不记成功 ✅；**停止策略不推进** ✅——rejected 后 tailReached=true、cursor=null、无后续提交、无重试 attempt 递增、recordRevision 停于 5、终态 Failed 不前进；currentSubmission=null，未发送 BGI ✅；pendingCompletion=null，成功收尾未授权 ✅；reason 留痕可读（UI 历史与 RunStore 同文）✅。

### 真实段带出的生产修复（阻断级实锤）

- **ext.task.start 受理回执词汇不匹配**：BGI 真实线路=queued/adopted（带 taskHandle），助手 ParseAcceptance 原仅认假设词 accepted → 真实提交一律误判「协议违例」Unknown（TEST-2/TEST-2b 两次复现：run-d60e7b4ec178、run-6d1b386196f1；BGI 侧 taskHandle=d61ee5f1/3f52bcb4 真实入队并执行）。整龙/配置组/前置/收尾四路共用同一解析全部受影响。
- 修复 `ed04355d`：三词兼容 + 缺 taskHandle 仍判协议违例 + 夹具 12 例（BgiJobTerminalPollingTests 钉死真实线路词汇，含 DS 复核补 accepted 缺 handle 1 例）；助手回归 380/380。TEST-2c/TEST-3 复跑通过即修复的端到端证据。
- **覆盖面核实**：前置适配器（BgiWorkflowPrerequisiteAdapter.cs:124）与收尾执行器（BgiWorkflowTerminalExecutor.cs:97）均调用同一 `ParseAcceptance`，无重复/旧解析——四路修复同源闭环。
- **adopted 语义核实**：BGI 认领判定 `BgiTaskCoordinator.SameSubmission`（:694-696）——提交带 IdempotencyKey 时**严格按幂等键相等**认领既有作业；任务中心提交必带确定性派生幂等键，adopted 绑定的即本 submission 的既有排队作业，设计上无错绑面，无需额外对账。

### 过程观察（不阻塞）

1. 测试中途配置被再保存（19:24:12）→ BGI 以 configuration_changed 正确拒绝执行——修订守卫按设计工作（换最新修订号后复跑通过）。
2. 双会话（YS+Administrator）下只读任务状态查询间歇报「用户名未匹配到唯一登录会话」——跨会话只读管道已知边界（总计划 §3.5），BGI 状态卡显示可能滞后。

### 残留证据件

- Unknown 运行两条（run-d60e7b4ec178、run-6d1b386196f1，词汇 bug 期间产生）保留为证据，不删除。
- TEST 系列流程文件（wf-test00c1/a2/a2b/a2c/b3）保留于助手配置根，复验可用；归 R5 清理或留作回归素材由 owner 定。

**结论**：R4 收口硬门槛 A/B/C1 真实段**全部通过**，§3 收口阻塞项解除；`task.single.native` 维持 false（§2.3 开放前置 2/3 未做）。