# 一条龙 R3 开工定案（2026-09-18）

> 阶段：R3（公版原生恢复）开工前定案。本文落实 R0 §8 硬门槛与 2026-09-18 ASTRA（gpt-6-astra）会诊修订，
> 是 R3.0–R3.4 施工的合同依据。上游：[总计划](../../../槲寄生调度器总计划.md) §3.1a/§3.3/§3.4、
> [审计方案](onedragon-public-compatibility-audit-2026-09-17.md) §8/§11、[R0 基线](onedragon-r0-baseline-2026-09-18.md) §6/§8、
> [对照表](onedragon-public-vs-teabag-comparison-2026-09-17.md)、[R2 矩阵](onedragon-r2-compat-matrix-2026-09-18.md)。
> 会诊方式：计划先行会诊（一轮），十项修订全部采纳并入本文；执行完成后再会诊审核效果。

## 1. 基线重核（开工日实时）

| 项 | 值 | 证据 |
|---|---|---|
| 茶包 HEAD | `6eeb7dc1` | `git rev-parse HEAD` |
| 茶包分支 | `main-OldTeaBag-B168` | git status |
| 公版基线 | `origin-lcb/main` = `97c90aa1` | 与 GitHub `kaedelcb/better-genshin-impact` main 的 `ls-remote` 实时一致 |
| 已跟踪工作区 | 零改动 | `git status --porcelain` 仅未跟踪探针/.bak/日志，不属 R3 |

**本轮公版快照固定为 `97c90aa1`，施工中不无记录追随漂移。**

## 2. 四项开放点裁决（owner 2026-09-18「既然要恢复，那肯定是按照公版」确认）

| 点 | 裁决 | 代码证据 |
|---|---|---|
| C19 连续计划命令行 | **整条删除**，含 `--batchGroups` 的 CLI 解析 | `ApplicationHostService.cs:74` 仅导航无独立逻辑；`OneDragonFlowViewModel.cs:1627` 是助手杀启回退通道；`CommandExecutor.cs:246` 标注该回退已废弃、批次名单走 IPC 协议字段（`:519`）。`RunnerContext` 批次名单预置（R0 §6 白名单 #4）保留，IPC 喂入 |
| DomainSelector | **整个控件退役删除**；原生页回公版普通下拉 | 全仓引用仅 `OneDragonFlowPage.xaml(.cs)` 与控件自身，无独立调用方；E13 事件标记耦合随控件删除消失。`MapLazyAssets` 等共享地图数据不动 |
| 全局 `ResinOrder/ResinCount`（茶包新增） | 退出活动配置/UI/执行读取，**并入 R3.3 预算合同逐块改写**，不孤立删字段 | 读取方为 `AutoDomainTask`/`AutoDomainParam`/`AutoLeyLineOutcropTask`/`AutoStygianOnslaughtTask`/`GoToCraftingBenchTask`，正是 F08 三源混用现场；孤立删字段不消除混用 |
| legacy→GUID | **一步切换**：不双读、不双写、不并存两套执行模型 | 旧 tuple 文件 R3.0 后即「待迁移、禁止执行」，双读窗口无服务对象；旧外部请求唯一通道是 R1 manifest 旧键→新 ID 映射，映射不上拒绝 |

## 3. R3.0 形状预检判型规则（硬门槛落地）

判型只看 JSON DOM，不反序列化为产品模型。规则移植自 R1 迁移器（`Test/OneDragonMigration/Core/OneDragonMigrationEngine.cs` `DetectFormat`），并补会诊修订：

| 判定 | 规则 |
|---|---|
| `TeabagTuple` | `TaskEnabledList` 值为 `{Item1,Item2}` 对象；或列表为空但存在 `NextTaskIndex` |
| `PublicCurrent` | 值为 bool 且存在 `TaskOrder` 或 `TaskDefinitions`；列表为空时凭 `TaskOrder`/`TaskDefinitions` 判定（**缺 TaskOrder 的公版文件不降级为名称键**，R1 加固结论） |
| `LegacyNameBool` | 值为 bool 但无 `TaskOrder`/`TaskDefinitions` |
| `StructuralBad` | `TaskEnabledList` 为数组/标量、值形状混合、字段类型错误、JSON 损坏 |
| `Unknown/Ambiguous` | 以上均不可靠区分（如完全空壳） |

旧格式（TeaBagTuple/LegacyNameBool）、StructuralBad、Ambiguous 统称**受保护文件**：保留原件与哈希、只读识别和报告、任何入口不得写回、不得执行。歧义文件不猜测重写。

### 3.1 全入口保护矩阵（会诊 P1-1 修订：保护必须覆盖全部写入入口，不止 VM 加载）

| 入口 | PublicCurrent | 受保护旧文件 |
|---|---|---|
| `InitConfigList` 普通加载 | 正常加载 | 不入 `ConfigList`；进「待迁移」清单，UI 明确提示 |
| W3 本机保存（`SaveConfig`/`WriteConfig`） | 正常 | 拒绝写回；同名新建/重命名/默认回退不得占用其路径 |
| W1 `config.set_task_enabled` / `ext.config.applyTaskState` | 正常（合同规则不变） | 拒绝并返回明确错误，不冒充成功 |
| W2 `config.apply_group` | 与一条龙形状无关，行为不变 | 同左（组文件不受本矩阵约束） |
| W4 引用服务（改名/删除引用更新） | 正常 DOM 更新 | **跳过并显式报告未更新引用**；文件保持禁止执行，未来迁移重新解析时处理 |
| 执行准备（ExecutionScope/起步校验） | 正常 | 拒绝执行，终态为 rejected/skipped 并带原因 |
| W5 旧升级器 | — | **退出**：删除 `AdaptVersions`/`RestoreOldVersions`/`ReverseAdaptTaskEnabledList` 及 VM 构造触发点（F02） |

哈希绑定实际读取字节；写前复检当前文件形状/身份，不信任启动时缓存（防启动后外部替换）。

### 3.2 落地顺序说明

判型助手与保护状态先行实现并配夹具（不改现有行为）；**接线必须与 R3.1 模型切换同批进行**——当前产品模型就是 tuple 模型，提前接线会把全部现有配置标记「待迁移」而打断正常使用。

## 4. GUID 身份合同（会诊 P1-4 修订）

切换面（已核实的代码点）：

| 位置 | 现状 | 切换后 |
|---|---|---|
| `TaskConfigurationContract.cs:145` | 投影 `"legacy:"+键名` | 投影真实 GUID |
| `OneDragonFlowViewModel.cs:2385` | 单项过滤 `"legacy:"+Index` | 按真实 ID 过滤 |
| `InstanceRequestHandler.cs:717` | `startFromIndex` int → `ResumeIndex` | 字符串 taskId |
| `BgiJob.cs:86` `ResumeIndex: int?` | int 水位 | `ResumeTaskId: string?` |
| VM `:2371/:2419` | `NextTaskIndex` | `NextTaskId` |
| describe 投影 | 「ID 即名」过渡 | 三件套正确语义：名称/顺序/类型/开关按 `TaskDefinitions`/`TaskOrder` |
| applyTaskState | 按 legacy 键写开关 | 按真实 ID 更新 `TaskEnabledList`，保持 `TaskOrder`/`TaskDefinitions`/`NextTaskId` 一致 |

语义规则：

- 同名重复项各自持稳定 ID；编辑/排序/重载**不重新生成** ID；仅新增任务项生成新 GUID。
- 旧整数请求无明确映射时拒绝，不用列表位置猜测。
- **组内项目序号游标不属于本次切换**（`SuspendedTaskContext` 组内水位与一条龙任务 ID 是不同游标，逐用途核对）；一条龙悬挂水位按稳定 ID（R2 矩阵 resume 行）。
- 失效起点分模式：原生模式按公版 B04（警告+从头）；严格外部合同（executionContractVersion=1）拒绝。不把迁移保护冒充公版现行行为。

### 4.1 生产端切换面（2026-09-18 开工日补充核实）

数字索引不止 BGI 内部：当前跨端合同本身就是 int 承载。以下生产端必须同步切换为字符串 taskId，**仅限一条龙（configName）路径**：

| 位置 | 现状 | 切换后 |
|---|---|---|
| `MultiplayerHoeingAssistant/Services/CommandExecutor.cs:29/:182/:200/:484/:776`（`BuildStartPayload`/`StartOneClickAsync`/`SetTaskEnabledAsync`） | `int startFromIndex`/`int taskIndex` | 一条龙路径改字符串 `taskId`/`startFromTaskId` |
| `BgiExternalClient.SubmitTaskStartAsync` 及 IPC 载荷 | int 字段 | 字符串字段（v2 旧命令冻结不改，ext 合同加法扩展） |
| `BgiCoordinatorServer/wwwroot/control-room.js:858-910` | `parseInt` 收集、`Number(taskIdx)` 发送、弹窗 `data-index` 为数字键 | 从 describe 投影携带字符串 taskId；`set_task_enabled`/`start_oneclick` 改字符串参数 |
| 服务端转发（GatewayDispatcher/RoomOperations） | 载荷透传 | 保持透传，不新增 int 假定；缓存保留 ID/类型/revision（E03 已在 R2 收口） |

**游标边界（不得混替）**：配置组路径 `StartGroupAsync(groupName, startFromIndex)` 的 int 是**组内项目序号游标**，不属于本次切换，保持 int。只有一条龙任务项身份换 GUID。

**旧客户端行为**：旧助手/旧网页仍发 int 时，native 形状下无映射——按 §4 规则拒绝并报明确错误，不用列表位置猜测；tuple 形状文件本属受保护、禁止执行，同样拒绝。跨版本断裂面因此是「响亮拒绝」而非静默错跑。
## 5. ResinBudget 预算合同（会诊 P1-5 修订；R3.3 前置定案）

- **每次执行构造一次 `ResinBudget` 快照**，执行期内是唯一有效输入。来源优先级：JS 显式参数 > 独立任务参数 > 公版全局 `AutoDomainConfig`。
- 未指定（`SpecifyResinUse=false`）= 公版默认「先浓缩、后原粹、其他不用」；某类零次 = 该类型不用；耗尽模式按公版语义。
- **D04**：恢复公版 `OriginalResin20UseCount/OriginalResin40UseCount` 分项及 `ResinUseRecord` 消费路径两名称分支，与共有四类计数的关系按公版规则，单独夹具回归。
- 消费状态只写本次快照，执行结束即弃；**禁止 `AutoDomainEnable` 全局标志与全局字典在执行中覆盖预算**——三处现状改造点：`TaskSettingsPageViewModel.cs:945-950`、`Dispatcher.cs:338-340`、`AutoDomainTask.cs:278`。
- C12 删除后**不提供**「节点持久化树脂覆盖」复活退役功能。
- 共享调用方（地脉/幽境/合成）合法参数组合随代码块恢复逐块核对。
- 现状证据：茶包 `AutoDomainTask` 树脂段为 `Dictionary<string,int>`（中文键）实现，公版为标量计数+`ResinPriorityList`——树脂段属「按代码块恢复」的最大块，施工时以公版块为底、保留双引擎路由。

## 6. AllConfig 过渡保护（会诊 P1-3，已源码证实）

**证实**：`ConfigService.Write` 为全对象覆盖写（`File.WriteAllText(file, JsonSerializer.Serialize(config))`），且 `Config.OnAnyChangedAction = Save`——任意属性变更即整文件重写。R3 删除 AllConfig 调度字段后，下一次任意保存将永久抹掉盘上旧计划/循环/账号数据，破坏 R1 迁移输入。

**方案**：`AllConfig` 增 `[JsonExtensionData]` 未知字段袋——删除的茶包调度字段读入时落袋、保存时原样往返。活动模型删除但盘上数据不丢。该袋是**过渡设施**，列入 R6 退出清单；不得为保留数据把调度字段塞回标准模型。开发验证用独立配置根，不碰真实 User 目录。

## 7. 保留代码块清单（删旧 VM/字段时不得误删，会诊 P1-6）

- UID 识别/剪贴板校验/登录界面切账号原子动作（R4 账号策略的执行端能力）
- 兑换码服务、使用历史、手动入口（`AutoRedeemCodeChecker`、`RedeemCodeHistoryStore`）
- 公版必要启动准备与取消链
- R2 桥：`ExecutionScope`、`TaskConfigurationContract`、`ExternalInterfaceConfigurationPlane`、`GroupConfigWriteContract`、`OneDragonConfigReferenceService`
- A6：`PreemptionGate`、有界退出、信号过滤、F11、执行前批次跳过（F03 修正版）
- 双引擎边界（B15）、`KazuhaCollectExecutor` 万叶拾取（B16）、OCR/传送独立增强
- `RunnerContext` 单次执行预置（白名单 #4，捕获即清）

## 8. 施工顺序与验证

**顺序**：R3.0 判型助手+夹具（不改行为）→ R3.1 原生 schema/配置面/GUID 切换 + R3.0 接线 + VM/XAML/旧路径退出（C19 整条、DomainSelector 退役、W5 退出）→ R3.2 单项服务与作用域 → R3.3 树脂归一化+D04+D01–D03 → R3.4 D 组逐块（B/D 核对贯穿全程，改块前定保留/恢复）→ 集成验收后最后开启能力声明（`task.single.native=true`）。

**验证**：

- 新夹具：形状预检全形状、混合目录全写入入口保护、标准三件套投影、单项边界（不隐式跑整龙准备/收尾）、GUID 恢复、ResinBudget 归一化、D 组逐项。
- 回归：R2 既有 68/68+36/36+150+276+10 不退化；R2 声明未覆盖的真实执行/resume/WPF 调用链补证据。
- 构建用 `DeployToBgiTools=false`，看退出码与完整错误；构建/桩测试/入口集成/实机分别报告。
- 退出条件：无助手环境公版标准配置互读互写/顺序/参数/收尾通过；独立任务/JS/联机底层回归；不向旧用户强制切换。

**验收口径更正（会诊 P2-7）**：不是「tuple/NextTaskIndex 文本零残留」——R1 解析器、只读保护、夹具仍需识别旧格式。口径为：原生活动路径无旧 schema 依赖、无旧格式写入；旧格式读取仅存于列明的兼容/迁移边界；旧升级器无可达调用。