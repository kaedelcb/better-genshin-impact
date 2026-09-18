# 一条龙恢复公版与槲寄生承接：代码审计及执行方案

> 审计日期：2026-09-17。状态：审计与计划已完成；**R0、R1 已于 2026-09-18 经用户下令完成**（[R0 基线与合同](onedragon-r0-baseline-2026-09-18.md)；R1 迁移器 `Test/OneDragonMigration/`，回归 16/16，未激活），R2 已于 2026-09-18 完成实施（[兼容矩阵](onedragon-r2-compat-matrix-2026-09-18.md)，经 ASTRA 会诊发现逐项修订后复跑全绿），R3–R6 待实施，R3 开工需用户明确下令。R2 计划已于 2026-09-18 经 ASTRA 会诊修订（§8 R2 行/R2.1–R2.5/交付性质矩阵，R4 行同步删除 C12–C14 旧表述）。
> 用户复核功能差异请先看：[公版与茶包一条龙逐项对照表](onedragon-public-vs-teabag-comparison-2026-09-17.md)。共有功能与实现差异不能混同为茶包新增。
> 主入口：[槲寄生调度器总计划](../../槲寄生调度器总计划.md)；执行基础设施：[统一作业注册表总计划](unified-job-registry-master-plan.md)。
> 本文替代旧 B1「把茶包一条龙整体对象化」→ B2 → C1 的实施路线。A0–A6 已有成果保留，历史完成记录不代替本次发现的缺口与实机验收。

## 1. 决策与范围

BGI 的一条龙恢复公版的配置格式、页面和默认执行语义；计划表、连续运行、定时循环、跨配置与跨账号编排等茶包增强由槲寄生持有。BGI 保留执行游戏动作、注册作业、取消、抢占、查询与恢复的能力。没有助手时，公版一条龙仍然独立可用。

交付分成两类资产：

- **标准一条龙配置**：按公版格式读写，可以直接交给同基线公版使用；公版已有的配置组混排、重复任务、每周秘境、地脉花、首领与幽境等功能继续保留。
- **槲寄生增强流程**：引用标准配置和配置组，保存茶包计划、账号、触发器、树脂策略及编排状态。增强数据不再写回标准一条龙 JSON。

兼容目标是标准一条龙配置及其公版可执行依赖。茶包专有 `SoloTask`、联机信号、专用脚本 API 等不能因外层 JSON 相同就宣称公版可运行；导出时必须列出依赖能力。不是整个 `User` 目录无条件跨版本通用。

**开发顺序可以先恢复 BGI，再补槲寄生界面；面向既有用户的默认切换必须等迁移后的增强流程可运行。** 不能先发布删除计划表功能的版本，再让用户等待承接。过渡期由一个明确的调度所有者控制某实例，不能同时启用旧连续循环和新流程。

本轮只修改计划文档。没有改产品源码、用户配置或运行数据，没有执行迁移，没有构建或进行游戏实机验证。

## 2. 基线、方法与覆盖边界

### 2.1 本次快照

| 项目 | 审计依据 |
|---|---|
| 茶包当前分支 | `main-OldTeaBag-B167`，动态核实而非按编号选择 |
| 审计开始 HEAD | `81fe892c026a7590e5031f21f0e0c35585f3033c` |
| 收尾 HEAD | `9482da382ea6d958905c7b8fb2501ca65bdd95b7`；期间新增 `9540b647` 远程配置编辑与 `9482da38` 成员卡布局提交，BGI 一条龙及其执行源码未变 |
| 公版 | `origin-lcb/main`，`97c90aa117d48ae1e01143f57515773fb72000a4` |
| 公版远端 | `https://github.com/kaedelcb/better-genshin-impact.git`；本地引用与本轮 `ls-remote` 一致 |
| 工作区口径 | BGI 已跟踪源码与审计开始 HEAD 一致；助手及服务端相关代码同时看工作区，包含审计期间随后提交的远程配置组编辑改动；收尾补查新增提交范围 |
| 本轮保护范围 | 现有暂存/未暂存修改、`.bak` 文件、运行目录 `User` 均保留；未读取真实用户账号配置 |

后续开工重新确认基线。若公版更新，先记录新基线及差异，再执行恢复，不把本文哈希当永久目标。

### 2.2 审计方式

对比公版与茶包差异；顺着配置读写、入口、执行、暂停、回执、UI 消费者追踪调用；检查同名底层功能的共享调用方；对照现有测试实际断言。源码位置以下述快照为准，执行时优先按符号定位。

| 审计面 | 已检查入口/链路 | 结论用途 |
|---|---|---|
| 一条龙模型与页面 | `OneDragonFlowConfig`、`OneDragonTaskItem`、`OneDragonFlowPage.xaml/.xaml.cs`、`OneDragonFlowViewModel`、DomainSelector；两个 `OneDragon/` 子目录 | 标准格式、原生功能、界面拆分边界 |
| 所有启动来源 | 页面、`TaskSettingsPageViewModel` 独立 VM、热键、`ApplicationHostService`/命令行、v2/ext 请求、resume | 防止只恢复页面而旁路仍写旧字段 |
| 持久化与引用 | `AllConfig`、旧升级/降级器、配置组重命名/删除、分发样例 | 顺序、ID、备份、重复项、迁移依赖 |
| 执行与生命周期 | `TaskRunner`、`ScriptService`、`RunnerContext`、`JobRegistry`、`BgiTaskCoordinator`、`PreemptionGate` | 状态真实性、幂等、仲裁、取消与父子作业 |
| 中断恢复 | `SuspendContextCapture`、`SuspendedTaskContext`、IPC resume、联机批次 | 稳定运行上下文、避免从头重跑/丢父链 |
| 游戏动作衍生 | 账号校验/切换、兑换码、月卡准备、秘境/树脂、首领/幽境、地脉花、合成/壶/收尾截图 | 策略上移，能力留 BGI，保留共享调用 |
| 脚本和联机 | `ScriptGroup`/`ScriptGroupProject`、`SoloTaskRegistry`、锄地/战斗路由、组内项目设置 | 独立能力不能随调度迁移删除 |
| 助手 | `BgiExternalClient`、`CommandExecutor`、`MainViewModel`/`OnlineBatch`、槲寄生启动流程模型/存储/Runner/VM | IPC 承接、现有编排复用、真实缺口 |
| 服务端与网页 | `ControlStatus`/`ControlRoomPlayer`/缓存、`RoomManager`、`GatewayDispatcher.ControlRoom`、`RoomOperations.ControlRoom`、`control-room.js` | 数字任务键、保存回执、远程命令兼容 |
| 现有验证 | Registry、进度事件、暂停信号过滤、协调器、助手 reconcile/重试相关测试 | 区分已有覆盖与必须补的集成验证 |

这是围绕恢复目标的跨模块静态审计，不是逐行审核整个仓库，也不表示所有游戏算法已经实机验证。与计划迁移无关的战斗/OCR/传送内部算法不进行重写。

代表性差异（`git diff --numstat --ignore-all-space origin-lcb/main HEAD`，新增/删除行，不是工作量估算）：

| 文件 | + / - | 含义 |
|---|---:|---|
| `Core/Config/OneDragonFlowConfig.cs` | 324 / 54 | 已经是另一套配置模型 |
| `Model/OneDragonTaskItem.cs` | 132 / 64 | 标识与秘境分发发生分歧 |
| `View/Pages/OneDragonFlowPage.xaml` | 1078 / 509 | 计划、账号等大量茶包界面混在原生页面 |
| `View/Pages/OneDragonFlowPage.xaml.cs` | 311 / 100 | 后台事件包含周期、账号绑定、自定义秘境和选择逻辑，须同页一起恢复 |
| `ViewModel/Pages/OneDragonFlowViewModel.cs` | 3046 / 470 | 执行、调度、迁移、账号、存储耦合 |
| `Service/Instance/MessageHandlers/InstanceRequestHandler.cs` | 1865 / 5 | 外部控制不能随 VM 恢复而丢失 |
| `GameTask/AutoDomain/AutoDomainTask.cs` | 696 / 244 | 共享执行引擎有独立增强，不能整文件回退 |

`ViewModel/Pages/OneDragon/`、`View/Pages/OneDragon/` 两个子目录与公版无差异；`CheckRewardsTask.cs` 也无差异，但茶包调用处已注释。这说明必须检查调用行为，不能只按文件差异量划范围。

## 3. 现状调用关系与目标架构

现状存在两条容易混淆的路径：

```text
UI / 热键 / CLI / v2 / ext / resume
  → 一个或另一个 OneDragonFlowViewModel
  → 读取/写入旧配置、准备账号、循环配置、选择任务
  → TaskRunner 或 ScriptService.RunMulti → 游戏动作
  ↘ JobRegistry、RunnerContext、暂停快照、助手和网页状态

助手联机批次 → reconcile → ext 队列 → 上述执行链
槲寄生启动中心 → enterTaskCenter → 目前只有占位日志
```

目标关系：

```text
公版一条龙 JSON → BGI 公版页面/原生执行流程 → 游戏动作
                         ↕ 最小通用生命周期与执行上下文接点
                  JobRegistry / TaskRunner / 取消与抢占
                         ↕ 能力协商、提交/查询/回执
槲寄生 WorkflowStore → Planner → Reconciler → BGI 执行能力
         ↑                    ↕ RunStore / 执行历史
迁移器：旧茶包数据 → 标准配置 + 增强流程 + ID 映射/迁移报告
```

### 3.1 明确归属

| 层 | 拥有的职责 | 约束 |
|---|---|---|
| BGI 公版一条龙 | 标准配置读写、原生页面、任务排序/开关/从此处开始、完整原生链 | 默认行为以公版为基线，脱离助手可用 |
| BGI 茶包执行扩展 | 作业登记、单项执行适配、实例运行上下文、账号输入/OCR、树脂参数执行、取消/恢复 | 放独立服务或茶包目录；只留下可列举的原生接点 |
| 槲寄生 | 计划、账号流程、时间/星期、跨配置顺序、循环、重试、暂停策略、全流程完成后动作 | 不复制 BGI 图像识别和游戏控制实现 |
| 助手现有联机模块 | 上线、人齐、锄地批次和已有恢复策略 | 通过同一仲裁接口接入，先保持现有行为 |
| 服务端/网页 | 定向转发、状态展示、命令回执与兼容适配 | 不是本机工作流事实源；本地执行不依赖服务器 |

### 3.2 最小侵入实施规则

1. `OneDragonFlowConfig`、`OneDragonTaskItem`、原生 XAML 及后台事件以对齐公版为目标。DomainSelector 直接引用配置类事件标记，移除旧字段/常量时同步解耦控件和类型化节点，不能遗留编译依赖。VM 恢复公版主流程，必要的作业/运行上下文接点单独列白名单和理由；不用旧茶包 VM 整体改名成 Executor。
2. 新增无 UI 依赖的单项分发适配器，复用现有原生动作及 `TaskRunner`。当前 ext 开始接口主要接受组/整龙，`SoloTaskRegistry` 也不等于原生一条龙任务工厂，**单项原生执行能力尚需实现**。
3. 同一个增强流程要么提交完整原生链作为一个节点，要么按不可变快照展开叶子节点；不得先跑完整原生链再跑展开项。首批迁移使用展开方式，才能在原位置安插旧茶包增强动作。此处“不可变”限定为**单次执行冻结的资源快照**（执行期间不受后续编辑影响）；流程定义本身可产生新修订并在节点边界生效（动态实时任务流，总计划 §3.1a 锚点 2），二者不冲突。
4. 下发携带本次有效参数与配置修订，不靠连续改全局选择项/临时覆盖 `User/OneDragon` 文件传参。合成、尘歌壶等间接读取当前选择配置的辅助任务也要获得同一作用域上下文。
5. 队列和注册表职责分开：注册作业不代表已排入统一泵。逻辑所有者租约负责防插队，`TaskRunner` 执行叶子时才持实际信号量；父作业不能持信号量等待再次抢锁的子作业。
6. 现有启动中心可复用编辑交互、日志、取消和进程工具；其存储与定时器不能直接当作可恢复任务引擎。任务中心建立独立 Store/Planner/Reconciler，避免继续膨胀 `MainViewModel`。

## 4. 功能归属与衍生影响清单

| 功能 | 当前实现/差异 | 恢复与迁移决定 | 验证重点 |
|---|---|---|---|
| 原生八类任务 | 邮件、合成、秘境、首领、幽境、每日奖励、尘歌壶、地脉花 | 全部留在 BGI；不是茶包独有功能 | 顺序、开关、参数与公版一致 |
| 配置组混排 | 两版均支持，茶包增加执行观察/专有组类型 | 保留公版混排；增强图也可引用组 | 组内 JS/路线/键鼠及重复组 |
| 重复任务、拖拽排序 | 公版稳定字符串 ID+显式顺序；茶包数字键+字典枚举 | 恢复公版实现；迁移保持每个出现位置 | 同名不合并，不按数字键排序 |
| 从此处开始 | 公版 `NextTaskId`；茶包 `NextTaskIndex` | 本地恢复字符串 ID，三端协议同步适配 | 删除/禁用目标、缓存修订冲突 |
| 配置列表与计划表 | `ScheduleName`、`IndexId`、计划筛选联动 SelectedConfig | 计划列表/跨配置排序移槲寄生；原生只显示标准配置 | 筛选不能修改正在运行的配置 |
| 连续一条龙 | 多配置循环与 `_lastUid`、下一个配置标记混用 | 槲寄生顺序/循环节点，运行态独立 | 无绑定账号配置原来会被过滤，迁移不得偷偷纳入 |
| 定时启动与循环 | `ScheduleStartOnTime/Time`、`CycleMode/Time`、`ScheduleLoop` | 槲寄生触发器与循环策略 | 等待不占 BGI 槽位，取消及时 |
| 星期与跨天 | `Period/PeriodList`、运行跨天跳过 | 槲寄生条件与周期截止策略 | 本地午夜、游戏服 4 点不是同一时间定义 |
| 跨配置游标 | `NextConfiguration` | 迁移为流程入口水位，不放标准 JSON | 多标记/失效标记报冲突，不能默认从头 |
| UID 绑定与切账号 | `GenshinUid/AccountBinding/AccountBindingCode`、剪贴板校验、登录界面操作 | 绑定/选择策略移槲寄生；识别/输入封装 BGI 原子能力 | 两字符提示歧义、未绑定、第三方登录、失败/F11 |
| 登录准备 | 启动游戏、回主界面、月卡检查、部分 UI 清理 | 原生必要准备保留公版；茶包额外账号准备作为节点 | 不用独立遗留 CTS 绕过整流程取消 |
| 兑换码 | 龙前自动检查，另有独立入口与本地使用历史 | 自动触发策略归槲寄生；服务/历史/手动能力留 BGI | 实际账号 UID、每日检查边界、避免双触发 |
| 每周秘境 | 公版已有每日队伍/秘境/奖励及回退语义 | 原生保持公版；增强流程保存旧有效选择 | 星期切换、周日字段改名、空值回退 |
| 自定义秘境=配置组 | 茶包按 DomainName 匹配 ScriptGroups，可能名称冲突 | 槲寄生显式组节点，保持原任务位置 | 用类型+引用，不再靠中文名猜路由 |
| 秘境格中的首领/幽境标记 | 茶包特殊字符串改走另一任务；首领使用全局参数 | 编译成对应类型节点，快照旧有效参数来源 | 不能误用标准 AutoBoss 字段替代原全局值 |
| 指定树脂数量/顺序 | 公版已有四类全局次数；茶包增加配置单字典覆盖及消费顺序，标量/字典双轨；公版另有 20/40 分项 | 公版次数能力保留，仅额外节点覆盖归助手，消费留 BGI | 区分共有功能与参数归属差异；保留/适配公版 20/40 分项 |
| 地脉花配置 | 两版共有周几/地区/类型/次数/耗尽模式 | 保留公版，隔离临时全局覆盖的恢复边界 | 独立地脉、幽境共享树脂函数不被删除 |
| 合成/尘歌壶 | 公版任务+茶包 OCR/传送细节增强，内部可读选中配置 | 底层增强独立保留；显式参数或作用域配置访问 | 错选另一配置、识别和传送双实现 |
| 收尾截图/通知 | `CheckRewardsTask` 公版存在；茶包调用注释 | 原生尾部恢复；增强流程对应边界执行一次 | 不漏、不因子项+父项重复发送 |
| 完成后动作 | 单配置 `CompletionAction` 与连续 `ContinuousCompletionAction` | 原生单链保持公版；整计划收尾移槲寄生 | 关闭游戏/BGI/关机只在确定的流程边界执行；停止/未知不得当成功 |
| 联机人齐打断与恢复 | A6、批次 reconcile、30s 安静退出、手动停止冷却 | 保留基础设施，接入统一身份与所有者 | ABABAB 信号循环、组间缝隙、手动 F11 优先 |
| 原生调度器及锄地一条龙 | `ScriptService` 自有顺序/设置；`AutoHoeingTask` 是游戏任务 | 不随旧计划表一起删除 | 联机/单机锄地、组内 SoloTaskSettings、队伍与战斗路由 |
| 共享游戏任务其他差异 | 地脉 JSON 路径/局部检测参数、秘境奖励等待、壶截图、幽境重试、双引擎/万叶拾取复用 | 见逐项对照 D04–D09、B12–B16；R3 逐项适配或明确保留，禁止整引擎回退 | 相同任务名称不能证明真实执行等价 |
| 页面后台与选择器 | 计划周期、账号提示、自定义秘境后台事件；DomainSelector 国家/奖励展示与事件标记 | 原生页连同后台恢复，增强编辑移助手；本地化恢复 | 旧事件引用、空列表、自定义标记依赖与右键选择 |
| 配置组改名/删除/远程编辑 | 会修正一条龙引用，且存在用户在做的远程编辑功能 | 统一引用索引；组编辑能力保持独立 | 本地/远程变更后流程引用失效必须可见 |
| 分享与分发样例 | 仓库 `AutoHoeingUpdater/OneDragon` 仍有旧结构样例 | 迁移为标准配置+可选增强包并更新说明 | 新安装不重新分发旧格式，账号信息不进公开包 |

## 5. 配置转换合同

> 效力说明（2026-09-18）：本节字段映射规则仍然有效，但与 C12/C13/C14 相关的"迁移为增强节点/树脂预算"要求已被用户在 2026-09-18 的迁移决策**废止**（这三项删除不上移，见 [对照表 §8 用户迁移决策](onedragon-public-vs-teabag-comparison-2026-09-17.md#8-计划调整与用户审查状态)）；以对照表决策为准。

### 5.1 三种输入必须识别

1. 当前公版：`TaskEnabledList: {id: bool}`，`TaskOrder: [id]`，`TaskDefinitions: {id: name}`，`NextTaskId: string`。
2. 旧公版/旧茶包：任务名→bool，可能没有 definitions/order；按旧公版加载回退识别名称。
3. 茶包 Version 1：`TaskEnabledList: {数字键: {Item1: bool, Item2: name}}`，`NextTaskIndex: int`，另有计划/账号/自定义秘境等字段。

使用无副作用 JSON DOM/迁移 DTO，根据字段形状判别，版本号只作辅助。不能实例化带 `TaskContext`/Toast setter 的茶包配置或构造旧 VM 来做扫描。

仓库分发样例 `AutoHoeingUpdater/OneDragon/灭绝提瓦特.json` 的键顺序为 `1,30,18,4,5,3,6,7,8,9,10,11,62,2`；当前 `LoadDisplayTaskList` 按枚举顺序构建执行列表。旧升级器也会分配倒序数字键。**任务顺序以旧实际枚举为准，绝不按数字键升序重排。** 本次只检查仓库样例结构，没有读取用户 UID 值。

### 5.2 字段映射

| 旧字段/数据 | 目标 | 转换规则 |
|---|---|---|
| `Name` | 标准配置名+槲寄生内部配置引用 | 保留用户名称；文件同名/大小写冲突另行报出，不覆盖 |
| tuple 任务字典 | 公版三件套 | 每个旧出现项生成独立稳定 ID；bool→开关、name→定义、原枚举→TaskOrder |
| 已有公版 IDs | 原样保留 | 不重新生成；未知额外字段入原始备份与报告 |
| ID 映射 | 迁移清单 | 保存旧配置标识+旧键→新 taskId；重复导入复用映射，不能每次随机重建 |
| `NextTaskIndex` | 标准 `NextTaskId` 或增强流程入口 | 按映射定位，0 表示未指定；目标缺失/禁用时标为待处理，不能悄悄从头执行 |
| `IndexId` | 槲寄生配置引用顺序 | 只用于跨配置排序，不用于任务顺序 |
| `ScheduleName`、全局计划列表/选择项 | Workflow/Plan 与助手选择状态 | 空名、重复计划、悬空选择单独归档/报告 |
| `NextConfiguration` | 计划级入口标记 | 保留一次性含义；多入口冲突阻止自动激活 |
| `Period`、`PeriodList` | 显式星期条件 | 以当前有效结构为主；无法解析旧展示文本时不猜测；未选条件保持旧有效行为 |
| `ScheduleLoop`、`CycleMode/Time`、`ScheduleLoopSkip` | 循环/周期截止参数 | 分离固定时间、立即下一轮、跨天跳过；不复制 `_lastUid`/固定 startTime 的实现缺陷 |
| `ScheduleStartOnTime/Time` | 启动触发器 | 迁移时保留“已过则明天”规则；与错过触发后补跑策略分开 |
| `GenshinUid`、`AccountBinding`、`AccountBindingCode` | 本机账号绑定和验证/切换节点 | 保留原过滤逻辑及提示，不推测密码；公开导出默认去除账号绑定值 |
| `CustomDomainList`、特殊 DomainName | 增强节点的带类型引用 | 按旧运行路由解析；组名与标记冲突必须显示实际选中的分支 |
| `ResinCount`、`SpecifyResinUse` | 节点 ResinBudget | 公版已有全局次数，迁移只处理茶包配置单覆盖；保留种类/配额/顺序并深复制，同时记录标量来源，补足公版 20/40 原粹分项表达 |
| `SundayDaySelectedValue` | `SundaySelectedValue` | 字段改名；补齐公版 `SundayWeeklySelectedValue`；根据实际回退规则生成预览 |
| 普通/每周秘境、七天队伍与奖励字段 | 标准字段+必要的增强覆盖 | 公版默认 `"0"` 与茶包空串行为不同，不能只复制同名字段就宣称等价 |
| `AutoBoss*`、地脉花字段、合成/每日奖励/壶等 | 公版字段 | 按公版白名单和默认值复制；区分“秘境格特殊首领”使用的全局值 |
| `CompletionAction` | 标准单龙尾部/增强子链边界 | 展开执行时只由 Planner 安排一次；旧连续运行抑制单龙收尾的语义要保留 |
| `ContinuousCompletionAction` | 增强流程最终动作 | 放循环/计划结束边界，不放每个叶子末尾；执行条件明确 |
| 七个 `Is*Expanded`、下拉显示派生项 | 助手 UI 偏好或归档 | 不混入执行模型/标准 JSON；不要求公版复现茶包折叠状态 |
| `Version`、旧迁移标记 | MigrationManifest | 标准 JSON 不继续引入茶包版本字段 |
| `SuspendedTaskContext` | 运行上下文/RunStore | 当前 AllConfig 上有 JsonIgnore，属于进程内状态；不能承诺旧进程重启后天然可恢复 |
| `AutoDomainEnable` | 每次执行的来源与参数 | 它是共享执行分支信号，不作为长期调度配置搬家 |

特殊路由的公版投影：原“自动秘境”实际执行自定义组/特殊任务时，增强流程在同一位置生成真实目标节点；标准导出不能把标记字符串当合法秘境直接写入。若无法无损表达，保留该原生项的 ID/位置但禁用并清理非法参数，在迁移报告显示替代节点及原因；完整行为由增强包承接。任何这种差异均阻止“无损标准导出”的标记，不能静默丢任务。对完全可表达的输入，标准投影应无语义差异。

### 5.3 事务、引用与回退

- R1 只读扫描并生成候选目录、字段差异、旧/新执行序列预览、依赖清单、冲突清单与哈希；逐文件错误隔离，不能一份坏文件中断后续扫描。
- 备份按迁移 manifest 精确记录本次涉及的配置、全局相关字段和助手新增文件；保留原始字节。不要调用旧工具反复复制整个运行 `User`。
- 写入分两段：所有标准配置、增强流程、映射先在候选目录落盘并验证；最终切换使用日志/提交标记，恢复时可判定未提交、已提交、待回滚。跨 BGI/助手目录不能假装一个文件 rename 就是全局原子事务。
- 激活前关闭本次迁移范围内旧自动写入/循环入口，确认无在跑任务和无冲突写入；读取版本/哈希后若发生用户修改则停止切换并重算，不能覆盖新修改。
- 更新配置组、启动节点、CLI 旧计划引用、助手配置选择、远程缓存等关联。引用用稳定标识与类型，名称只负责显示；找不到组时保留未解析节点并阻止该流程启动，不能静默删除。
- 失败/回退按 manifest 恢复本次变更；回滚前比对现状哈希，迁移后的用户新编辑不得被覆盖，冲突副本需保留。恢复前先停止新调度所有者，避免回滚后双跑。
- 旧 `AdaptVersions`/`RestoreOldVersions`/`ReverseAdaptTaskEnabledList` 在新路径停用。旧降级器按任务名建 bool 字典会覆盖重复项，且没有完整公版 ID/顺序模型，不可作为新迁移器。

## 6. 必须先解决的静态审查发现

以下是首轮 13 项代码可达路径问题；后续按用户要求补查的共享执行差异列在逐项对照表 B/D，并纳入 §9 验收。本轮没有用游戏进程逐项复现。P1 表示可能错跑、丢状态或破坏迁移的实施阻断；P2 表示会造成策略偏差或维护分歧。历史测试通过不自动解除这些发现。

| 编号 | 级别 | 证据位置（茶包快照） | 触发与影响 | 要求/阶段 |
|---|---|---|---|---|
| F01 | P1 | `OneDragonFlowConfig`；`InstanceRequestHandler:1253–1288,1402` | IPC 任务列表只按 Item1/Item2 读取，修改只接受 int；换公版 bool 字典后列表可被 catch 吞空、操作无法定位 | R2 同时适配 schema 与稳定字符串 taskId，禁止 int 强转 |
| F02 | P1 | `OneDragonFlowViewModel:3308–3555` | 自动旧升级与“转公版”工具不能保留同名任务/公版 ID 顺序，VM 构造还可触发迁移 | R1 无副作用迁移器；R3 停用旧写入器 |
| F03 | P1 | `OneDragonFlowViewModel:2653–2730` | `BatchGroupNames` 的跳过判断在 action/RunMulti 之后，同名组已经执行才显示跳过 | R0 补回归场景；R2 过渡修正；R5 用节点身份退休名称跳过机制 |
| F04 | P1 | `TaskRunner.RunCurrentAsync` catch/finally；VM:2662,2709,2774；handler:900,948 | 抢锁失败只记日志、组异常被吞、TaskRunner 返回 Ran 不保证成功；IPC 找不到配置也可走非取消成功收尾 | R2 类型化受理/执行结果；父级汇总实际子终态，缺配置与未知状态不得成功 |
| F05 | P1 | `SuspendContextCapture:44–145`；handler:1759；`TaskSettingsPageViewModel:408` | 捕获先命中组内项目会丢龙父链；否则读取 DI VM 选择态，热键却可用另一 VM；resume 写 int 水位并 fire-and-forget | R2 每次执行不可变上下文，保留祖先链；resume 回执区分入队和终态 |
| F06 | P1 | `JobRegistry` 活跃去重；`MainViewModel.OnlineBatch:302–372` | 显式不同 key 未命中仍可回退 generation+name；批次投影丢 kind；同名父/组/重复节点可能被错误归并 | R2 以作业种类+流程/节点出现身份去重，新请求身份不能回退为同名任务 |
| F07 | P1 | `control-room.js:865`；`GatewayDispatcher.ControlRoom:25`；`CommandExecutor:50–67` | 网页 await 修改只等转发 ack；执行端等在飞写入 10s 超时仍继续，可能用旧开关启动 | R2 增 config-applied 回执/修订条件，失败或超时不启动；不可借用旧 start-only 回执冒充保存成功 |
| F08 | P1 | `AutoDomainParam.SetDefault:74`；`AutoDomainTask:166,278`；TaskSettings/Dispatcher 的 `AutoDomainEnable` | 字典/标量树脂状态并存、共享引用与全局标志覆盖有效参数；迁移后可能吃错树脂或用错数量 | R3 规范化每次 ResinBudget，R4 完整承接；覆盖独立、JS、龙和联机共享调用 |
| F09 | P1 | `BgiTaskCoordinator` ext 泵；`TaskRunner` 非阻塞抢锁；`PreemptionGate` 来源判断 | 登记父子作业没有自动把所有入口串行化；简单整龙持实际锁会与子锁死锁，IPC 父项内部来源又可能被视为外人 | R2 只验收已落地部分的回归固化（并发根拒绝、父子不嵌套持锁、owner/epoch 传递到叶子、F11 始终可达）；全入口统一 admission 仲裁与逻辑租约归 R5 |
| F10 | P2 | VM:2003–2262；`GetDomainConfig` 两版 | 长时间定时等待占 TaskRunner；循环复用旧 startTime；本地时间与服务区 4 点混用 | R4 明确时间语义与每轮状态，等待移助手 |
| F11 | P2 | VM:2738；公版 `CheckRewardsTask` 调用 | 公版收尾截图检查被取消，但任务文件本身无 diff | R3 恢复原生尾部，R4 按边界去重 |
| F12 | P1 | `ScriptControlViewModel:740–915`；VM 配置 setter/SaveConfig | 重命名/删除构造 VM，反射修改 DomainName 和 tuple 并写回；新标准格式会漏引用或走旧迁移 | R2 **新建**引用服务（无 VM 副作用、不得触发 F02 旧迁移；属新代码交付而非既有能力验收），R3 全部旧格式写入者退出 |
| F13 | P2 | `MistletoeViewModel:613–625`；`StartupFlowStore` | enterTaskCenter 仍占位；启动 JSON 存储不是事务运行日志 | R4 必须交付真实本机调度和 RunStore，不以已有页面视为引擎完成 |

F04 的处理不能简单把所有 `Task` 正常返回标成成功。准备条件未满足应有明确 skipped/rejected 原因；已捕获的执行异常需要传递结构化结果。原生链是否继续按公版语义运行，与对外父作业如实报告结果分开处理；增强链根据策略决定继续/停止。父子关系登记本身不自动聚合成功。

## 7. 跨端运行合同

### 7.1 身份、修订与结果

- 配置项统一暴露 `taskId: string`、显示名称、类型、顺序、enabled、`configRevision`。旧 `index` 只在具备明确旧键映射时兼容，不能把 GUID 解析为整数，不能用 UI 列表位置冒充 ID。
- 工作流身份至少区分：目标实例、`bgiEpoch`、`workflowRunId`、`nodeId`/出现位置、iteration、attempt。重复投递同一请求复用幂等键；下一轮/重试新尝试/同名另一节点生成新身份。配置组项目序号与一条龙 taskId 分开。
- 受理结果（accepted/queued/rejected）与作业终态分开。返回 jobId 后由查询确认终态；事件仅加速对账。未知、未找到旧纪元作业、通信超时均不是 succeeded。
- ext 新能力通过 hello capability 协商，加法扩展 v2 信封，不升 protocolVersion 3。新旧混接时能观察/运行的范围如实显示；不支持稳定 ID 或修订保存时，禁用该新操作，不能退回错误索引执行。
- 网页和助手必须等到执行端给出匹配 `CommandId + target instance + revision` 的配置应用回执，才开始依赖该配置的任务；可用原子“应用修改并开始”操作，但仍须报告应用结果与 jobId。服务端 ack 只代表转发/缓存。
- 离线缓存命令加有效期、目标实例和配置修订约束；过期/不匹配时明确拒绝，避免重连后执行旧“从此处开始”。

### 7.2 暂停、抢占、恢复与接管

运行上下文保存本次配置快照/修订、稳定任务 ID、父作业/工作流关系、组内项目游标、有效参数与抢占原因。暂停捕获从正在执行的上下文读取，不从当前界面选择项推断。原生热键/页面/命令行/IPC 使用同一服务入口。

保留 A6 的有界安静退出、门代际、信号任务不入恢复点、F11 最终停止权。`NotifyOnlineTask` 等信号不得重新纳入恢复链造成 ABABAB；停止/取消/抢占分别标识。原生链恢复与槲寄生展开链恢复各自有唯一所有者，不同时重启父链和当前叶子。

接管是可逆运行态：`local | managed`，带 owner、epoch、到期与释放过程。检查所有可启动/修改执行计划的入口，包括 UI、热键、CLI、v2、ext、网页和恢复；不能只禁用按钮。F11 始终可用。助手退出/失联后租约按规则失效、恢复本地控制；不得将接管只读永久写进标准配置。

失联时对正在执行的游戏动作先查询或有界停止；续租丢失不等于任务完成，更不能自动补发整链。BGI 重启换 epoch 后，将未证实终态标成 interrupted/unknown，核实账号/游戏状态后按显式恢复策略处理。RunStore 记录推进事实和恢复选择，不引入完整事件历史重放。

### 7.3 时间与状态存储

每个 trigger 明确时区、日界线、错过执行策略、重复频率和截止点。迁移保留已知旧规则，并在预览中显示与公版的差异：

| 来源 | 旧语义 | 新模型要求 |
|---|---|---|
| 公版每周秘境/地脉 | 游戏服务时间，4 点日界线 | 标准模式原样保留 |
| 茶包每周秘境 | 本地时间，4 点日界线 | 增强迁移显式保存兼容选择；不能静默变成服务器时间 |
| 连续计划星期过滤 | 本地日期/午夜 | 独立 Weekday 条件 |
| 旧定时启动/启动中心 timer | 当日时刻已过则明日 | 明确 missPolicy，不套用联机补跑规则 |
| 现有联机定时上线 | 有 15 分钟宽限 | 保留在对应触发器，非全局统一默认 |
| 兑换码日检查 | 既有服务器日期与 UID 历史 | 不自动改成 4 点日切 |
| 连续跨天跳过 | 旧代码受首次 startTime 影响 | 定义每轮开始/截止，迁移报告标注修正；覆盖运行跨午夜/4 点 |

新增本机 `WorkflowStore`（定义、版本、引用）、`RunStore`（运行、水位、提交 ID、终态、待恢复）、迁移清单。使用临时文件+原子替换/备份和修订校验；损坏隔离并保留原件，阻止受损计划自动执行。不要以“读失败返回空对象后自动保存”覆盖用户流程。持久化范围限定当前 Windows 用户，不经 SignalR 自动同步。

## 8. 实施阶段与退出条件

下列为建议实施路线，必须等用户审查并明确命令后才执行相应阶段。每阶段结束回写主计划的状态、实际变更文件、验证证据、待解决项；禁止把“接口已发出”“构建成功”登记为流程实机成功。

| 阶段 | 依赖 | 交付 | 退出条件/回退 |
|---|---|---|---|
| R0 基线和合同 | 本次审计 | 固定待实施基线；字段/入口/引用清单；F01–F13 回归场景；原生最小接点白名单 | 迁移与执行合同可逐项验收；只读/测试夹具，不动真实用户数据 |
| R1 无损迁移候选 | R0 | 纯解析器、三格式识别、稳定 ID 映射、标准配置+增强流程生成、dry-run 报告、事务/回滚设计 | 重复/乱序/坏文件/周日/账号/特殊路由夹具通过；旧输入字节不变；尚不激活 |
| R2 兼容桥和执行事实（**已于 2026-09-18 完成**，[兼容矩阵](onedragon-r2-compat-matrix-2026-09-18.md)） | R0/R1 数据合同 | IPC 桥主体已随合同修复落地，本阶段以**验收固化+补齐缺口**为主：三端 taskId/revision/config-applied 合同、运行上下文与类型化结果端到端贯通；**新代码**：引用服务、写入路径衔接、远程整组编辑强制 revision；幂等/批次跳过残余修正；产出兼容矩阵 | F01–F07、F09（R2 分界部分）/F12 对应入口集成+故障注入（含实例替换/epoch 冲突/revision 冲突/旧回执迟到）；离线（无服务器）本地 describe/apply/start/query 断言（执行段惰性桩，resume 经代码重核在位、未重复离线执行，局限已在兼容矩阵页首声明）；旧格式仍可读，迁移未激活 |
| R3 恢复公版原生链 | R1、R2 | 模型/任务项/XAML/VM 对齐；去旧循环/账号 UI/自动降级器；保留执行接点；原生单项能力与配置作用域；树脂输入归一化 | 无助手的公版配置互读互写/顺序/参数/收尾通过；独立任务/JS/联机底层回归；尚不向旧用户强制切换 |
| R4 槲寄生承接旧增强 | R2、R3 | WorkflowStore/RunStore/Planner/Reconciler+最低可用任务中心；全部旧计划/循环/账号/收尾节点（自定义秘境 C13、配置级树脂 C12、格子委派 C14 已按 2026-09-18 决策删除不上移）；启动中心真实移交 | 迁移样例从准备到最终动作纯本机跑通；等待不占槽；重复节点、停止/失联/恢复语义完整 |
| R5 接管与迁移切换 | R1–R4 | 所有者租约与入口仲裁；联机批次/抢占恢复接入；启动/CLI/缓存引用更新；事务激活和回滚；退休 BatchGroupNames | 迁移中断/重启/回滚不丢配置不双跑；F11/人齐打断/旧新混接通过；原生模式可退出接管后独立运行 |
| R6 发布门槛与 diff 收敛 | R5 | 默认切换、旧数据兼容读取退出策略、分发样例/文档、真实互用验证、接点 diff 报告 | §9 全部关键项通过，有可恢复备份；源码/构建/单测/实机分别记录；再发布给现有用户 |

### R0–R2 必须完成的具体工作

- [ ] R0.1 重新核实公版与工作区；给每个一条龙增量标注“公版还原/执行扩展/槲寄生/独立保留”。
- [ ] R0.2 录入公共配置字段、所有旧字段及有效默认值；将每个 F 编号连接到测试或实机用例。
- [ ] R1.1 按 JSON 形状实现只读解析，固定 ID 与排序规则，脱敏夹具覆盖三种格式和真实样例结构。
- [ ] R1.2 编译增强节点与标准投影，展示每条输入→输出位置；特殊路由、空值回退、时间差异不得遗漏。
- [ ] R1.3 完成候选目录、manifest、哈希、重复导入和故障恢复；生成回滚预览，不调用旧整目录降级器。
- [x] R2.1 身份分层与三端 DTO：分清**资源标识 / 配置修订 / 任务项标识 / 节点出现身份**四层；本阶段只交付过渡身份（`legacy:<index>`、内容哈希 ConfigKey）的三端一致适配（BGI handlers、助手展示/命令、服务端缓存、网页），二者不等于长期资源身份；原生 GUID 字符串 ID 切换属 R3，不在本阶段进行。
- [x] R2.2 配置应用回执与开始条件接通；在飞写入超时明确失败；覆盖远端缓存旧命令。**新代码**：远程整组编辑的 revision 验证由可选改为强制；旧写入入口的兼容权限单列清单；配置面写入路径衔接代码。
- [x] R2.3 运行上下文与 resume 回执贯通：受理/入队/终态分级贯通、重投去重、F11 后迟到恢复正确归类；修复缺配置/拒绝/异常假成功；父子关系不由 UI 推断。缺口处允许补生产代码，非纯验收。
- [x] R2.4 幂等与引用残余修正：修幂等键、批次跳过顺序；**必须覆盖批次投影丢身份、无 key 同名归并**两个已知缺口；同名重复和父子同名不被去重。**新代码**：引用服务（F12）——无 VM 副作用、不触发 F02 旧迁移，标准格式下组改名/删除以稳定标识更新引用。
- [x] R2.5 兼容矩阵与故障注入：产出「客户端版本 × BGI 能力 × 配置形状 × 操作」兼容矩阵；故障注入补实例替换/epoch 冲突/revision 冲突/旧回执迟到；离线（无服务器）本地 describe/apply/start/query 退出断言（T66 执行段为惰性桩，已断言准备选定的配置与起点到达执行边界，不证明真实任务运行）；resume 链路经代码重核在位（票据重投去重/stale 分级/revision 校验/受理超时），未重复离线执行——如实声明为已知局限。监控端只读边界不放开，既有远程配置组编辑功能不删除不覆盖。

R2 交付性质矩阵（2026-09-18 ASTRA 会诊后增补，开工按此核对，防止把验收误当新代码或反之）：

| 交付物 | 性质 |
|---|---|
| IPC 桥主体（describe/apply/start/query、revision 守卫、类型化结果、回执账本、网页逐目标回执） | **已有实现**（IPC 合同修复提交已落地）→ 本阶段做三端集成验收与回归固化 |
| 引用服务（F12 组改名/删除的引用更新） | **必须补代码**：无 VM 副作用、不触发 F02 旧迁移 |
| 远程整组编辑强制 revision、旧写入入口兼容权限清单、配置面写入路径衔接 | **必须补代码** |
| resume 回执分级贯通、重投去重、F11 后迟到恢复 | **集成验收**，缺口处补生产代码 |
| 兼容矩阵、故障注入扩展（实例替换/epoch/revision/旧回执迟到）、离线断言 | **集成验收**（矩阵文档 + 测试夹具） |
| 原生 GUID 资源身份切换、全入口统一仲裁/逻辑租约 | **后续阶段**（R3/R5），本阶段不切换 |

### R3–R6 必须完成的具体工作

- [ ] R3.1 在已保留桥接功能的基础上按文件/代码块恢复，删除计划表 UI/旧字段写入者；全仓扫描残留调用，不整文件覆盖共享引擎。
- [ ] R3.2 单项原生分发及结果合同可用；参数快照/作用域贯穿间接读配置的辅助任务；处理公版内部捕获异常造成的结果不可见。
- [ ] R3.3 验证默认值、周日、地脉及公版尾部；树脂字典/标量/来源统一且不改变独立任务能力。
- [ ] R3.4 按逐项对照 B/D 核对公版 20/40 树脂分项、地脉 JSON/局部检测参数、秘境奖励画面等待、壶循环截图和幽境按钮未找到重试；每项记录恢复或独立保留的理由，不整文件覆盖双引擎。
- [ ] R4.1 实现真实任务中心、Store 和 reconcile，最小界面要能预览迁移流程、启动/停止、查看当前节点和失败原因。
- [ ] R4.2 计划排序/星期/定时/循环/跨天、账号、兑换码和完成后动作全部具备承接能力（自定义秘境/配置级树脂/格子委派已删除不上移，见对照表 §8 决策）；不能以只支持“运行整龙”作为迁移完成。
- [ ] R4.3 `enterTaskCenter` 从占位改为真实提交；保留旧启动节点兼容，但防止旧节点与新触发器双发。
- [ ] R5.1 接管仲裁覆盖全部入口；保留 A6/F11/信号过滤、联机批次与恢复；逻辑租约不嵌套持执行锁。
- [ ] R5.2 激活前停止旧调度与写入，事务切换并验证引用；注入每个切换边界故障验证恢复。
- [ ] R6.1 更新分发样例及分享指南，区分标准文件与增强流程包；在干净公版与茶包环境验证直接互用。
- [ ] R6.2 列出剩余公版侵入接点及理由；删除已无调用的旧调度代码、迁移过渡写入器和名称去重补丁，保留有期限的只读导入能力。

## 9. 验证与发布验收

### 9.1 迁移与标准兼容

- 当前公版、旧 name→bool、茶包 tuple、混合目录分别导入；重复名称、乱序数字键、禁用项、下次入口正确，重复迁移不变 ID。
- 缺/坏 JSON、重复配置名、悬空组、空计划、多个入口标记、被删除/禁用的游标均有明确结果；不覆盖原始文件，不静默从头跑。
- 七天/周日/空值/全局回退、首领两种参数来源、各树脂顺序/配额、AccountBinding 过滤、自定义组名冲突的预览与旧有效语义对应。
- 标准配置“公版保存→茶包打开保存→公版打开”保持任务 ID/顺序/参数；比较规范化 JSON 语义，允许缩进差异，不允许夹带茶包字段。
- 公版可执行依赖与茶包专有依赖分级展示；存在替代/禁用投影时不宣称无损标准分享。
- 故障注入候选生成/提交/助手文件写入/激活/回滚各边界；再次启动只恢复一个确定状态，用户迁移后编辑不被回滚覆盖。

### 9.2 运行和跨端

- 无助手：页面、热键、CLI、v2/ext 的原生任务/整龙/配置组都可用；公开格式导入导出、重复任务与从此处开始正确。
- 同名任务多次、同名配置组与父龙、下一轮和重试分别执行；重复投递同一请求只执行一次。
- 缺配置/策略、任务异常、抢锁拒绝、组失败、预处理失败、F11、通信中断分别产生真实结果；父级不得因 `Task` 返回或 `Ran` 判成功。
- 暂停发生于原生项、组内 JS/路线、项间缝隙、账号准备，均保持正确祖先和水位；切换 UI 配置不改变正在跑的上下文。
- 网页开关任务后立刻启动、10s 慢写、执行端断线、旧缓存延迟投递、修订变化均不得错用旧状态；回执展示区分发送/应用/入队/执行终态。
- 人齐打断→有界退出→联机批次→完成动作→恢复；保留信号任务不恢复、ABABAB 防回归、F11 冷却终态不重发。
- 助手关闭/崩溃、BGI 重启换 epoch、服务器断开、租约到期、取消后重新接管；不永久锁页面、不自动补跑未知任务。
- 树脂共享调用覆盖独立秘境、JS 秘境、地脉、幽境，特别覆盖公版原粹 20/40 独立计数；组内 SoloTask/锄地单机与联机、队伍/战斗/传送实际路由分别验证。
- 地脉仅 JSON 文件与 TXT 文件、五个局部结束检测设置、奖励画面迟到、壶地图缩放后新画面、幽境“前往挑战”未识别必须分别验证；不把源码存在 JSON 方法当作该入口一定能执行。
- 纯本地无服务器：定时、循环、跨天/4 点、账号切换、失败策略、最终动作完整运行。监控端仍只读能力门控，不假定已有跨会话控制通道。

### 9.3 现有测试的边界与执行方式

`JobRegistryTests` 的父子关系断言不证明真实一条龙子失败会汇总；`JobProgressEventTests` 验证事件注册/发布，不验证 VM 执行顺序；`TaskSuspendOnlineSignalTests` 验证信号过滤，不等于完整嵌套恢复。应增加迁移夹具、跨端合同、真实入口集成和故障注入，而不是再写一组只检查类名/事件名的测试。

实施涉及 BGI、助手、服务端时分别构建并跑影响相称的测试。当前 BGI 与助手项目均有 `DeployToBgiTools=false` 条件，构建前复核后用于只验证源码；不得为文件锁杀用户程序或修改构建规则。构建按完整退出码/错误判断；旧失败必须同条件基线复现，不能沿用历史“FsCheck 预存失败”名单免验或排除测试。

本轮只有文档改动，验证文档内容、引用、变更范围与格式；不使用旧 A6 的构建/测试计数作为本轮测试结果。

## 10. 执行接力记录

| 日期 | 本轮完成 | 未完成/限制 | 下一步 |
|---|---|---|---|
| 2026-09-17 | 公版与茶包基线复核；一条龙及衍生跨端链路静态审计；F01–F13；字段迁移合同、功能归属、R0–R6、回退与验收更新到总计划；文档链接、阶段/发现清单、完整源码路径及格式校验通过 | 本轮无产品改动；未运行迁移、构建、单测或游戏实机验证；既有远程配置编辑工作随后由并行工作提交，本轮未覆盖 | 等待用户审查逐项对照并明确下令；获准后建议先 R0 基线与夹具，再 R1 dry-run；不得按旧 B1 整体抽取茶包 VM |

## 11. 文件级施工边界

以下完整路径相对仓库根目录，同格中的短名沿用对应项目/目录。表中的“保留”指保留其独立功能，不代表无需做参数/引用适配。新增服务名是职责建议，尚不存在的实现不能作为已有能力引用。

| 位置 | 施工方式 | 阶段 |
|---|---|---|
| `BetterGenshinImpact/Core/Config/OneDragonFlowConfig.cs` | 对齐公版字段/默认值；旧 DTO 放迁移模块，不能继续往标准类追加计划字段 | R1/R3 |
| `BetterGenshinImpact/Model/OneDragonTaskItem.cs` | 恢复公版 ID 与任务分发；单项外部执行适配放独立服务，必要接点单列 diff | R3 |
| `BetterGenshinImpact/View/Pages/OneDragonFlowPage.xaml`、`OneDragonFlowPage.xaml.cs` | 恢复原生布局、命令和后台事件；计划/账号/循环迁至任务中心，本地化恢复 | R3/R5 |
| `BetterGenshinImpact/View/Controls/DomainSelector.xaml`、`DomainSelector.xaml.cs` | 明确控件去向并解除标准配置类事件标记依赖；不因一条龙恢复删除共用地图数据 | R3 |
| `BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs` | 恢复公版加载/保存/执行主流程，退出旧升级器和连续执行；保留最小生命周期/上下文接点 | R2/R3 |
| `BetterGenshinImpact/ViewModel/Pages/OneDragon/`、`View/Pages/OneDragon/` | 当前与公版无差异，优先原样保留；不复制出助手版 UI 逻辑 | R3 |
| `BetterGenshinImpact/Core/Config/AllConfig.cs` | 只迁出一条龙调度字段；选中标准配置等公版字段保留；不整文件回退 | R1/R3/R5 |
| `BetterGenshinImpact/Service/Instance/MessageHandlers/InstanceRequestHandler.cs` | 配置列表/开关/开始/恢复/刷新引用逐项适配；保留现有远程能力 | R2/R5 |
| `BetterGenshinImpact/ViewModel/Pages/ScriptControlViewModel.cs` | 替换构造一条龙 VM 的引用修改流程；保留配置组原生调度与用户编辑 | R2/R3 |
| `BetterGenshinImpact/ViewModel/Pages/TaskSettingsPageViewModel.cs`、`HotKeyPageViewModel.cs` | 一条龙入口用同一执行上下文；清理旧全局秘境来源标志；保留其他独立任务与热键 | R2/R3/R5 |
| `BetterGenshinImpact/Service/ApplicationHostService.cs` | 保留公版命令行，旧连续计划参数只做明确兼容转交；更新启动引用 | R3/R5 |
| `BetterGenshinImpact/GameTask/TaskRunner.cs`、`RunnerContext.cs` | 真实执行结果、叶子锁、owner/上下文传递；退休半接管字段前查全调用方 | R2/R5 |
| `BetterGenshinImpact/Service/Execution/JobRegistry.cs`、`PreemptionGate.cs` | 请求身份、父子结果、逻辑租约与已有门语义衔接 | R2/R5 |
| `BetterGenshinImpact/Service/Execution/SuspendContextCapture.cs`、`Core/Config/SuspendedTaskContext.cs` | 从运行上下文捕获祖先/稳定水位，不读界面；保留信号过滤 | R2/R5 |
| `BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs` 及协议/会话层 | 能力声明、单项提交、应用回执、准入仲裁；不把队列登记当实际完成 | R2/R3/R5 |
| `BetterGenshinImpact/Service/ScriptService.cs`、`Core/Script/Group/ScriptGroup.cs`、`ScriptGroupProject.cs` | 保留组执行与 STJ 值归一化、项目设置克隆、SoloTask 能力；适配结果和引用 | R2/R3 |
| `BetterGenshinImpact/GameTask/AutoDomain/AutoDomainConfig.cs`、`AutoDomainParam.cs`、`AutoDomainTask.cs`、`Model/ResinUseRecord.cs` | 参数归一化和公版 20/40 分项合同；奖励等待差异复核；保留独立游戏执行增强 | R3/R4 |
| `BetterGenshinImpact/GameTask/AutoBoss/`、`AutoStygianOnslaught/`、`AutoLeyLineOutcrop/` | 直接调用差异见逐项表：双引擎、JSON 路径、局部检测参数、万叶拾取、未找到按钮重试；按接点评估 | R3/R6 |
| `BetterGenshinImpact/GameTask/Common/Job/GoToCraftingBenchTask.cs`、`GoToSereniteaPotTask.cs`、`CheckRewardsTask.cs` | 配置作用域正确；原生尾部调用恢复；不整文件替换 OCR/传送增强 | R3/R4 |
| `BetterGenshinImpact/GameTask/UseRedeemCode/AutoRedeemCodeChecker.cs` 及相关服务 | 保留独立/手动能力与历史，自动触发策略迁出 | R3/R4 |
| `BetterGenshinImpact/GameTask/SoloTaskRegistry.cs`、AutoHoeing、AutoFightOfficial 与传送相关模块 | 能力与实际路由回归；不是恢复计划表时的删除目标 | R3/R6 |
| `MultiplayerHoeingAssistant/Services/BgiExternalClient.cs`、`CommandExecutor.cs` | 新能力门控、字符串 ID、应用回执、超时/受理结果合同 | R2 |
| `MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs`、`MainViewModel.OnlineBatch.cs` | 列表/任务键与批次身份适配；联机策略走共用仲裁，不再堆任务引擎 | R2/R5 |
| `MultiplayerHoeingAssistant/Models/ControlStatus.cs`、`ControlRoomPlayer.cs`、`MemberConfigCache.cs` | 任务 ID/类型/修订端到端保留，不靠 object 列表假定 int | R2 |
| `MultiplayerHoeingAssistant/ViewModels/MistletoeViewModel.cs`、`Views/MistletoePage.xaml`、启动流程相关模型/服务 | 复用启动交互和工具，真实接入任务中心；新增独立 workflow 模型、Store、Planner、Reconciler、RunStore | R4 |
| `MultiplayerHoeingAssistant/Services/RemoteConfigEditService.cs`、`LocalConfigEditChannel.cs`、`ViewModels/MainViewModel.RemoteConfig.cs` | 用户已有远程配置组编辑工作；本次只做必要引用/合同衔接，不覆盖其内容 | R2/R5 |
| `BgiCoordinatorServer/Models/ControlStatus.cs`、`ControlRoomPlayer.cs`、`Services/RoomManager.cs` | 配置缓存/状态保留稳定身份和修订 | R2 |
| `BgiCoordinatorServer/Gateway/GatewayDispatcher.ControlRoom.cs`、`Services/RoomOperations.ControlRoom.cs`、`wwwroot/control-room.js` | 配置应用回执路由、开始前置修订、旧命令有效期和 UI 字符串 taskId | R2/R5 |
| `AutoHoeingUpdater/OneDragon/` | 转换分发资产与依赖说明，禁止新安装继续得到旧 tuple 格式 | R6 |
| `Test/BetterGenshinImpact.UnitTest/`、`Test/MHA.UnitTest/`、`BgiCoordinatorServer.Tests/` | 迁移/执行/跨端集成与故障注入，按 §9 断言用户可见语义 | R0–R6 |
