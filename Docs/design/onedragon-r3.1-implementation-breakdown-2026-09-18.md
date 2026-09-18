# 一条龙 R3.1 施工分解（2026-09-18，侦察自 6eeb7dc1 + origin-lcb/main 97c90aa1）

> R3.1 主体切换的施工图纸。所有结论来自开工日逐文件 diff 与源码核实，不是旧文档转述。
> 上游合同：[R3 开工定案](onedragon-r3-prework-contracts-2026-09-18.md)。本文随施工推进更新实际行号。

## 1. 规模事实（git diff origin-lcb/main --stat）

| 文件 | 变化量 | 性质 |
|---|---|---|
| OneDragonFlowViewModel.cs | +3556/−?（3585 行 vs 公版 979 行） | 茶包调度 UI + R2 桥钩子共存，**重写为公版基底+桥钩子** |
| OneDragonFlowPage.xaml | 2739 行变化 | 整文件恢复公版（129 个 i18n 用法随页面回来） |
| OneDragonFlowPage.xaml.cs | 473 行变化 | 整文件恢复公版（后台事件一并恢复，审计 §8 修订 3） |
| InstanceRequestHandler.cs | +1466 | 绝大部分是 R2 桥（保留），仅一条龙身份适配（手术） |
| OneDragonFlowConfig.cs | 390 行变化 | 整文件恢复公版（397 行） |
| OneDragonTaskItem.cs | 204 行变化 | 整文件恢复公版（245 行），C12/C14/C21 增量随之消失 |
| DomainSelector.xaml(.cs) | +108/+337（纯新增） | **删除两个文件**（裁决：无独立调用方） |
| AllConfig.cs | +106 | 手术：删 9 个调度字段+构造器播种，其余茶包功能字段保留 |
| ApplicationHostService.cs | +86 | 手术：只删 startContinuousOneDragon 导航分支；`--startGroups`/`--TaskProgress` 等其他茶包 CLI 保留 |
| TaskConfigurationContract.cs | +169（R2 新文件） | 手术：投影换 GUID |
| BgiJob.cs | +164（R2 新文件） | 手术：ResumeIndex 仅限组游标；龙换 ResumeTaskId |

## 2. OneDragonFlowViewModel 区域图（当前茶版 3585 行）

**删除**（茶包调度/UI，字段归属见对照表 §8 决策）：
- 117/132 FilterLogic/RefreshFilteredConfigList（计划筛选）、152 _playTaskList、313 FindNextAvailableIndex
- 328 OnResinUsageSequenceAsync（C12 树脂顺序弹窗）
- 776 ScriptControlPageAsync、820 ShowAndSwitchPlanAsync、1027 AddScheduleItem、1143 CopyConfig、1206 EditConfig、1258 DeleteScheduleItem（C03/C15/计划管理 UI）
- 1698 SetNextConfiguration、1721 ClearNextConfiguration（C06 游标）
- 1742 SetAccountBindingCodeSwitchButton、1774–1785 SetCycleTime*（C10/C07/C09 UI）
- 2003–2270 OnOneKeyContinuousExecutionOneKey 连续执行循环（C05/C08；`_continuousExecutionMark`/`_executionSuccessCount`/`_finishMark` 语义随之简化——`_finishMark` 被 ExecuteOneDragonAsync 结果判定使用，移植时保留 finishMark 机制但去掉连续语义）
- 3342–3585 W5 全家桶：AdaptVersions/UpgradeConfig/AdaptTaskEnabledList/BackupUser/RestoreOldVersions/BackupDirectory/DowngradeConfig/ReverseAdaptTaskEnabledList + OneDragonFlowConfigV0 嵌套类（F02 退出）
- 681 InitializeDomainNameList、_customDomainList（DomainSelector 支持）
- 茶版 InitConfigList(1328)/LoadDisplayTaskListFromConfig(1377)/SaveConfig(1438)/SetSomeSelectedConfig(1474)/WriteConfig(1549)/OnLoaded(1573)/DeleteTaskGroup(2831)/OnAddConfig(2844)/NextTaskGroup(2873)/ClearNextTaskGroup(2917) → 由公版对应方法替代

**移植到公版基底的桥钩子**（R2 交付，必须保留）：
- ExecuteOneDragonAsync(JobDescriptor?)（2273–2331）：ExecutionScope.Start 准入、registry Submit/TryMarkRunning/TryMarkTerminal、RejectedSlotBusy、Preempted/Cancelled 分类
- RequireDragonStep（2333）：scope.Observe + ThrowIfStopped + 前置失败抛错
- OnOneKeyExecuteCore（2342–2805）的骨架，改造点：
  - 批次名单捕获即清（RunnerContext.BatchGroupNames，2349）
  - revision 守卫：ConfigRevision 快照读取 + configuration_changed 拒绝 + TrackConfigurationFile（2360-2371）——`snapshot.Document.ToObject<OneDragonFlowConfig>()` 随模型换原生
  - 单项过滤：2385 `legacy:` → GUID；单项 `CompletionAction=""` 清空守卫保留
  - resume：NextTaskIndex 改 NextTaskId；严格合同「恢复位置已不存在，不能从头重跑」拒绝保留；原生模式按公版 B04 警告+从头
  - scope.ThrowIfStopped 分布点、SetDragonNode（改 string taskId）、PublishJobProgress（A5-3）、龙内子项 Solo/OneDragonInternal 登记（A2.6/A5-2）、批次跳过（F03 修正版，执行前判断）
  - 尾部：恢复 CheckRewardsTask 调用（2768 注释行恢复，D06/F11）；CompletionAction 单次配置级保留（公版语义）
- OnOneKeyExecute（2270）入口壳

**删除的执行链内茶包策略**（动作能力保留，调用移除）：
- UID 校验/切账号循环（2440-2560 调用块）：`_lastUid`/`executionConfig.GenshinUid` 驱动的整段不进原生链
- CheckAndRedeemCodeIfEnabledAsync 调用（2597-2600）
- 账号/兑换原子能力本体保留：GetClipboardText(2809)、CheckAndRedeemCodeIfEnabledAsync(2938)、VerifyUid(2953)、GetConfirmRa(3005)、SwitchAccount(3020)、Login3rdParty(3018)——R4 槲寄生账号策略的执行端原料，标注保留用途注释

## 3. 身份切换点（GUID 合同 §4 落地清单）

| 位置 | 改动 |
|---|---|
| ExecutionScope.cs:139 `SetDragonNode(int)` → `SetDragonNode(string taskId)`；Snapshot 第 6 参 |
| SuspendContextCapture.cs:25 `int OneDragonTaskIndex` → `string? OneDragonTaskId`（含 :83 复制与 :87 日志） |
| SuspendedTaskContext.cs 对应字段 |
| InstanceRequestHandler.cs:717 resume 消费 + `startFromIndex` int → 龙路径 taskId 字符串 |
| BgiJob.cs:86 `ResumeIndex: int?` → 拆：组内游标保留 int（改语义注释），龙新增 `ResumeTaskId: string?` |
| TaskConfigurationContract.cs:145 投影 `legacy:` → GUID；native 投影三件套语义（名称/顺序/类型/开关） |
| VM:2385 单项过滤、:2371/:2419 NextTaskId 回灌 |
| 生产端：定案 §4.1 表（助手 CommandExecutor 一条龙路径、control-room.js:858-910） |

## 4. R3.0 保护接线点（与模型切换同批）

- VM InitConfigList：逐文件 `OneDragonConfigShapePreflight.InspectBytes`；受保护文件入 `PendingMigrationFiles`（ObservableCollection<string>，文件名+形状），UI 顶部提示条；不加入 ConfigList
- VM WriteConfig/SaveConfig：目标路径现存文件受保护 → 拒绝并提示（同名占用防护）
- OnAddConfig/RenameConfig：新名与受保护文件同名 → 拒绝
- W1 `config.set_task_enabled`/`ext.config.applyTaskState`（InstanceRequestHandler）：目标文件受保护 → 明确错误
- W4 OneDragonConfigReferenceService：受保护文件跳过 + ChangeReport 显式报告
- 执行准备（ExecuteOneDragonAsync 起步）：配置名对应文件受保护 → rejected
- GoToCraftingBenchTask/GoToSereniteaPotTask/AutoDomainTask 的直接文件读取（E12，公版也有此模式）：读受保护文件会抛反序列化异常——接线时统一走预检，受保护按「无可用选中配置」处理并留痕（R3.2 作用域改造的完整方案另列）

## 5. AllConfig 手术清单

删：`_continuousCompletionAction`、`_selectedOneDragonFlowPlanName`、`_scheduleList`、`_scheduleLoop`、`_cycleTime`、`_cycleMode`、`_scheduleLoopSkip`、`_scheduleStartOnTime`、`_scheduleStartTime` + 构造器播种 `默认计划表`。
留：`SelectedOneDragonFlowConfigName`（公版）、`SuspendedTaskContext`（JsonIgnore 运行态）、`AutoDomainEnable`（R3.3 预算合同时改造，不在本批）、其余茶包功能字段（AutoFightOfficial/AutoHoeing/好感/切武器/切角色/小地图调参/TpTaskFastDrag/MedicineEatCd）。
已落地：`[JsonExtensionData] LegacyExtensionData` 袋（b29457bc 之后本轮提交），夹具 2/2。

## 6. CLI 手术

ApplicationHostService.cs:74 条件拆半：删 `|| startContinuousOneDragon`，保留 `startOneDragon`。VM OnLoaded 恢复公版 CommandLineOptions 版（删 --batchGroups 解析与 startContinuous 分支）；`RunnerContext.BatchGroupNames` 预置保留（IPC 协议字段喂入，CommandExecutor:519）。

## 7. 变绿策略

以上全部同一批落地后才开始编译修复循环；中途不提交。编译修复顺序：模型 → VM → XAML → IPC/合同 → 生产端。每修一处先判「恢复公版语义还是适配桥」，不凭同名猜测。完成后：BGI 构建 0 错 → 既有测试基线对比（873 通过/13 既有失败）→ 新增夹具 → 提交。R3.2（单项能力/作用域）与 R3.3（树脂）不混入本批。