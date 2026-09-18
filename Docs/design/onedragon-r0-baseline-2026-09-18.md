# 一条龙 R0 基线与合同（2026-09-18）

> 阶段：R0（基线和合同）。本轮按 [审计方案 §8](onedragon-public-compatibility-audit-2026-09-17.md#8-实施阶段与退出条件) R0 行与 R0.1/R0.2 工作项执行，用户已于 2026-09-18 明确下令开始 R0。
> 主入口：[槲寄生调度器总计划](../../槲寄生调度器总计划.md)；上游依据：[审计及执行方案](onedragon-public-compatibility-audit-2026-09-17.md)、[逐项对照表](onedragon-public-vs-teabag-comparison-2026-09-17.md)、[IPC 通道合同审计](mistletoe-ipc-channel-contract-audit-2026-09-17.md)。
> 本轮只读核实 + 文档登记：未修改产品代码、用户配置或运行数据，未运行迁移/构建/游戏实机。

## 1. 固定基线（本轮重核）

| 项目 | 9-17 审计值 | 本轮（2026-09-18）核实值 | 结论 |
|---|---|---|---|
| 茶包分支 | `main-OldTeaBag-B167` | `main-OldTeaBag-B168` | 分支随既有提交推进，非人工切换 |
| 茶包 HEAD | 开始 `81fe892c`，收尾 `9482da38` | `069aec14` | 见 §2 增量核实 |
| 公版本地引用 | `origin-lcb/main` = `97c90aa1` | `97c90aa1`（不变） | 一致 |
| 公版远端 | `ls-remote` 一致 | `ls-remote https://github.com/kaedelcb/better-genshin-impact.git main` = `97c90aa117d48ae1e01143f57515773fb72000a4` | **本轮实时核实一致** |
| 工作区已跟踪文件 | 与审计 HEAD 一致 | `git status` 已跟踪文件零改动；仅探针/`.bak`/日志等未跟踪文件 | 一致，未跟踪杂项不属 R0 范围 |
| 运行数据保护 | User/配置/脚本只读 | 本轮未读取真实用户账号配置 | 维持 |

基线结论：**公版基线未漂移；茶包侧自审计以来的增量全部可枚举（§2），审计文档的字段合同（§5）、功能归属（§4）与对照表（92 项）继续作为 R1 输入，无需重审。**

## 2. 审计后增量核实（R0.1）

`git diff 81fe892c..HEAD` 全口径共 89 个产品/测试文件变化，其中**与一条龙执行链直接相关**的是三个已提交 IPC 修复：

- `d3af610c`（36 文件：联机接管执行权、恢复现场与任务终态）
- `8a8cc027`（43 文件：execution identity / idempotency / configuration receipts）
- `b69463d7`（6 文件：联机取消语义 + 槲寄生兼容回归）

涉及 `OneDragonFlowViewModel.cs`（+170/−140）、`TaskRunner`、`ExecutionScope`、`JobRegistry`、`SuspendContextCapture`、IPC 会话/控制/配置面、助手 `CommandExecutor`、网页回执及对应测试。变化性质：旧 A5-2「RunnerContext 预置父作业」壳改为 `ExecutionScope.Start(JobDescriptor)` 显式作用域；`ExecuteOneDragonAsync(descriptor?)` 供 IPC 携带身份/revision 进入；`RequireDragonStep` 前置结果检查、配置 revision 起步校验、执行配置快照；TaskRunner 类型化结果与注册表拒绝终态。**属于审计方案预期的「执行扩展/槲寄生桥」方向，不改变茶包配置 schema（数字键 tuple 格式未变），不触碰计划表/定时/循环等待迁移增强。**

其余提交（桌宠、设置导入导出、远程配置编辑、联机批次收尾、JS 进度观察等）为并行工作，与一条龙执行链无关，已抽查确认范围。

> 更正（2026-09-18 GPT 会诊发现 #12）：本段初版称"变化仅 `OneDragonFlowViewModel.cs` 一个文件"，那是按一条龙目录窄口径 diff 的结论；全口径实际如上，§3 各 F 项状态与之一致。

R0.1 增量标注结论：一条龙增量的「公版还原 / 执行扩展 / 槲寄生 / 独立保留」四类归属，以审计方案 §4 表（24 行功能面）与对照表 92 项为准；本轮新增核实：上述 VM 改写归入「执行扩展（桥）」，不改写任何既有归属行。

## 3. F01–F13 当前状态与回归场景映射（R0.2）

按当前 HEAD `069aec14` 逐项重核证据位置。状态分三档：**已过渡落地**（IPC/A 系列修复已覆盖，待回归夹具固化）、**部分落地**（主体机制在，缺口明确归属后续阶段）、**未变**（证据原样存在，等对应阶段）。

| 编号 | 当前证据（HEAD `069aec14`） | 状态 | 回归场景 → 承载用例 |
|---|---|---|---|
| F01 IPC 只认 tuple/int | v2 `config.set_task_enabled` 已改走 `ExternalInterfaceConfigurationPlane.DispatchAsync`；`ext.config.describe/applyTaskState` 输出 `taskId`（内容标识 / `legacy:<index>`）+ `configRevision` + 接管票据守卫。执行 schema 仍是数字键 tuple（`InstanceRequestHandler.cs:759-761` 读 `Item1`/startFromIndex） | 部分落地（桥已有，原生 schema 属 R3）；**R2 已闭环三端过渡身份一致性**（2026-09-18，兼容矩阵 §1） | 已有：IpcChannelContractAudit 配置 CAS/单项选择用例（T22–T52 组）。R2 补：三端统一过渡身份（`legacy:<index>`/内容哈希 ConfigKey；资源标识/修订/项标识/节点出现身份四层分清）后 describe/apply/start 全链夹具；公版 schema 读入不吞空。原生 GUID 字符串 ID 切换归 R3 |
| F02 旧升级器丢重复项 | `OneDragonFlowViewModel.cs:770`（构造触发 `AdaptVersions`）、`927`、`3350/3448/3565`（`AdaptVersions`/`RestoreOldVersions`/`ReverseAdaptTaskEnabledList`）原样存在 | 未变（R1 迁移器、R3 停用） | R1 夹具：同名重复任务/乱序数字键/三格式识别，输入字节不变；R3：VM 构造不再触发任何迁移的断言 |
| F03 批次跳过晚于执行 | 重写后跳过判断在执行前：`OneDragonFlowViewModel.cs:2660`（`continue` 先于 Action 调用，水位线照推） | 已过渡落地（名称匹配机制退休属 R5） | R0 登记场景：批次名单含龙内同名组 → 断言该组 Action 未调用且水位推进；承载：BGI 单元/集成测试（R2 固化）；R5：节点身份替代名称匹配后退役本场景 |
| F04 假成功/抢锁静默 | `TaskRunner.RunCurrentAsync` 已返回类型化 `TaskRunResult`（`RejectedSlotBusy`），登记 Rejected 终态；VM `RequireDragonStep` 检查前置结果；ext 合同区分受理/终态 | 已过渡落地（跨端集成验证属 R2） | 已有：IpcChannelContractAudit T12（受理前 `queue_full`）、T23（失败/未知不可重放）。R2 补：缺配置/抢锁拒绝/组异常 → 父作业非成功终态的端到端断言 |
| F05 暂停丢父链/读 UI 态 | `SuspendContextCapture` 不再引用 `OneDragonFlowViewModel`/`SelectedConfig`（ExecutionScope 快照捕获）；resume 经 `Descriptor.ResumeIndex` 回灌 | 已过渡落地；**R2 核实回执分级/重投去重/F11 后迟到拒绝均已在位**（2026-09-18，代码重核 + IpcIncidentAudit 36/36，无需补生产代码） | R2 场景：原生项/组内 JS/项间缝隙/账号准备四个暂停点分别保存祖先链与水位；resume 回执受理/入队/终态分级贯通、重投去重、F11 后迟到恢复正确归类 |
| F06 作业归并丢身份 | `JobRegistry` 归并键已含 `kind|generation|parentJobId|name`（`JobRegistry.cs:119`）；显式 key 走回执账本不再回退；无 key 同名仍按 name 归并（保留语义） | 部分落地（无 key 同名与批次投影属 R2） | 已有：注册表幂等采用用例（ServiceTests 71/71 内）。R2 补：同名父/组/重复节点不互相归并；显式不同 key 绝不复用同名 jobId |
| F07 网页只等转发 ack | 网页逐目标等待执行端 `config_applied` 回执（目标/命令关联校验），服务器 ack 不再授权启动；`CommandExecutor` 未知结果不切 v2 重发 | 已过渡落地 | 已有：web-config-receipts 10/10、IpcChannelContractAudit 回执用例。R2 补：10s 慢写/断线/旧缓存延迟投递的故障注入 |
| F08 树脂双轨互相覆盖 | `AutoDomainParam.cs:27-99`（`ResinCount` 字典 + `SpecifyResinUse` 标量 + `SetDefault` 全局读）原样存在 | 未变（R3 归一化） | R0 登记预期矩阵：字典/标量/全局三来源 × 独立任务/JS/龙/联机共享调用的有效预算；R3 按矩阵断言每次执行 ResinBudget |
| F09 仲裁不统一 | `ExecutionScope.Start` 拒绝并发根、逻辑 owner 到叶子已落地；UI/热键/CLI/网页等全入口仲裁与 managed 模式未建 | 部分落地（全入口仲裁属 R5）；**R2 分界部分已固化回归**（并发根拒绝/父子不嵌套/owner 传递/F11，IpcIncidentAudit，2026-09-18） | R2 固化回归：并发根拒绝、父子不嵌套持锁、owner/epoch 到叶子、F11 永远可达；R5 故障注入：全入口统一仲裁与租约接管；R0 登记入口全集（§4）供仲裁覆盖核对 |
| F10 定时等待占槽 | 定时/循环 UI 与逻辑仍在 VM（`OneDragonFlowViewModel.cs:1788-1841` 等 `ScheduleStartOnTime/CycleMode/ScheduleLoop` 绑定） | 未变（R4 移助手） | R4 设计断言：等待不占 `TaskSemaphore` 槽位；每轮独立开始/截止，不复用旧 startTime |
| F11 收尾截图被取消 | `OneDragonFlowViewModel.cs:2768` `CheckRewardsTask` 调用仍为注释 | 未变（R3 恢复） | R3：原生龙尾执行一次奖励检查；R4：增强流程边界去重（子项+父项不重复发送） |
| F12 改名/删除反射写旧格式 | `ScriptControlViewModel.cs:801-882` 反射改 `*DomainName` 属性并写回 tuple | **R2 已落地引用服务**（`OneDragonConfigReferenceService`，纯 DOM/无 VM 副作用/不触发 F02，T61–T64，2026-09-18）；旧格式写入者退出仍属 R3 | R2 **新建**引用服务（无 VM 副作用、不触发 F02 旧迁移）；R2/R3 夹具：标准格式配置下组改名/删除 → 引用以稳定标识更新，不产生旧格式写入；悬空引用可见且阻止启动 |
| F13 任务中心占位 | `StartupFlowModels.cs:268` `enterTaskCenter` 仍为占位委托（`StartupFlowRunner.cs:362` 调用点） | 未变（R4 真实调度） | R4：占位改真实提交；RunStore 事务写/损坏隔离/阻止受损计划自动运行 |

## 4. 入口清单（启动与修改执行计划的全集，供 F09/R5 仲裁核对）

**启动执行**：① 页面按钮 `OneDragonFlowViewModel.OnOneKeyExecute`；② 连续/热键路径 `ExecuteOneDragonAsync`（VM:2137）与 `TaskSettingsPageViewModel` 独立 VM；③ 命令行 `ApplicationHostService --startGroups`（Cli 来源提示，VM:1644）；④ IPC v2 `InstanceRequestHandler.HandleTaskStart` → `ExecuteOneDragonAsync(descriptor)`（handler:767）；⑤ ext 控制面 → `BgiTaskCoordinator` → 同一路径；⑥ resume（`SuspendedTaskContext`）；⑦ 网页 control-room → SignalR → 助手 `CommandExecutor` → IPC；⑧ 助手批次 `MainViewModel.OnlineBatch`/`CoordinatedBatch`；⑨ 启动中心旧版 BGI 节点（Runner 兼容分支，目录已移除）。

**修改执行计划**：① 页面 VM 保存；② `ScriptControlViewModel` 反射改名/删除写回（F12）；③ v2 `config.set_task_enabled`（已汇入配置面）；④ ext `config.applyTaskState`；⑤ 远程配置组编辑（独立功能，与配置面共用每文件互斥）；⑥ 旧升级器 `AdaptVersions`/`RestoreOldVersions`（F02，R3 退出）。

## 5. 字段与引用清单

字段迁移合同（三种输入格式、逐字段映射规则、时间语义表）以审计方案 §5 为权威版本，本轮抽查关键字段（`TaskEnabledList` tuple 形状、`NextTaskIndex`、`ResinCount`、`ScheduleLoop` 族、批次名单流转）与当前代码一致，无需修订。引用清单：配置组重命名/删除影响面 = F12 反射写入 + 槲寄生未来流程引用（稳定标识纪律见审计 §5 末段）；分发样例 `AutoHoeingUpdater/OneDragon/灭绝提瓦特.json` 键顺序事实（`1,30,18,4,5,3,6,7,8,9,10,11,62,2`，枚举顺序≠数字升序）已复核文件仍在。

## 6. 原生最小接点白名单（R3 恢复时 BGI 侧允许保留/新增的通用接点）

1. `ExecutionScope`（`Service/Execution/`）：执行身份、revision 校验、暂停快照——已存在，保留。
2. `TaskConfigurationContract` + `ExternalInterfaceConfigurationPlane`：配置 describe/apply（revision 守卫）——已存在，保留；R3 扩展原生 schema。
3. `JobDescriptor.ResumeIndex` / `TaskId` 单项选择：当前 `legacy:<index>` 过渡——R3 换稳定字符串 ID。
4. `RunnerContext` 单次执行预置（批次名单/来源提示，捕获即清）——保留，不得新增跨执行残留字段。
5. ext 事件/查询（job 快照、进度、终态）——保留，加法扩展。
6. 联机批次/抢占（A6 `PreemptionGate`、有界退出、F11）——保留，R5 接入统一所有者。

除此之外，R3 恢复公版时不得向 BGI 新增调度/计划类字段或长期状态；增强编排状态只进槲寄生 WorkflowStore/RunStore。

## 7. 退出条件自检

- [x] 固定待实施基线（§1，公版远端实时核实）
- [x] 字段/入口/引用清单（§4、§5，字段合同沿用审计 §5 并抽查复核）
- [x] F01–F13 回归场景，每项已连接已有用例或后续阶段夹具（§3）
- [x] 原生最小接点白名单（§6）
- [x] 只读核实，未动真实用户数据；无产品代码变更

**R0 完成。R1 输入齐备**：三格式样例（含分发样例键顺序事实）、字段合同 §5、迁移不得复用旧降级器的约束（F02）、以及「旧输入字节不变」的验收口径。R1 开工仍需用户明确下令。

## 8. 过渡窗口加载保护（R3 硬门槛，2026-09-18 会诊确认）

用户核心风险已实测确认：恢复后的 BGI（公版模型 `TaskEnabledList: Dictionary<string,bool>`）直接打开茶包 tuple 文件（值为 `{Item1,Item2}` 对象）时，`JsonConvert.DeserializeObject` 在值类型转换处**必然抛 `JsonSerializationException`**；公版 `InitConfigList` 循环内无局部 try/catch，该配置加载失败并中断列表初始化。虽然不会按任务名合并（那是旧 `ReverseAdaptTaskEnabledList` 的行为），但"页面初始化失败 + 用户随后操作保存"的组合可能间接危及盘上文件。

因此 R3 在首次接触旧配置目录**之前**必须落地加载保护：

1. 反序列化前做**形状预检**（轻量 DOM 探测 `TaskEnabledList` 值形状），识别出 tuple/旧 name→bool 文件；
2. 旧格式文件**不进入普通加载/自动保存路径**：标记为"待迁移"，UI 明确提示，禁止以任何同名新对象写回覆盖原件；
3. R1 迁移器（`Test/OneDragonMigration/`）是唯一的转换通道，转换产物经 R5 事务切换后才替换；旧文件全程保留原件哈希；
4. 夹具：恢复版 BGI 启动扫描含 tuple 文件的目录 → 不崩溃、不吞空、不写回，提示待迁移。
