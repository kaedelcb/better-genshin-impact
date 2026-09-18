# 一条龙上游合并策略（DIFF 归因与裁决原则）

> 性质：**活文档**。每次公版（origin-lcb/main）合并后刷新 §2 快照。配套操作技能：$public-merge-assistant（.agents/skills/public-merge-assistant/SKILL.md）。
> 建立：2026-09-19（R3 收口归因分析实测数据）。

## 1. 目的与适用

茶包版与公版长期并行。R3 已把一条龙模型/页面/执行恢复公版原生，后续公版有一条龙相关提交时需要合并优选过来。本文回答一个问题：**每个文件的差异来自什么、合并冲突时哪侧优先**。

适用方向：公版 → 茶包。反向（茶包功能提 PR 上游）使用 $teabag-pr-upstream-assistant，不在本文范围。

## 2. 基线快照（2026-09-19 实测）

- 上游：`origin-lcb/main` = `97c90aa1`；茶包：`main-OldTeaBag-B168` = `9e8809ed`；merge-base = `6afbf069`
- 测量命令：`git diff --numstat origin-lcb/main HEAD -- <路径>`（+/- 为上游→茶包方向）

| 级别 | 文件 | 差异(+/-) | 归因 |
|---|---|---|---|
| T0 零差异 | Core/Config/OneDragonFlowConfig.cs | 0/0 | R3.1 已字节级恢复公版模型 |
| T0 零差异 | GameTask/AutoDomain/Model/ResinUseRecord.cs | 0/0 | R3.3 树脂归一化后与公版一致 |
| T1 近零 | GameTask/AutoDomain/AutoDomainConfig.cs | +10/-1 | D04 原粹 20/40 恢复后残余差 |
| T1 近零 | GameTask/AutoDomain/AutoDomainParam.cs | +11/-10 | 参数对齐后残余 |
| T1 近零 | GameTask/AutoStygianOnslaught/AutoStygianOnslaughtParam.cs | +2/-1 | R3.4 恢复后残余 |
| T1 近零 | GameTask/AutoLeyLineOutcrop/AutoLeyLineOutcropFightConfig.cs | +1/-16 | 地脉局部配置（R3.6 登记窗口） |
| T1 近零 | View/Pages/OneDragonFlowPage.xaml | +11/0 | 仅「待迁移」警告横幅（茶包新增块） |
| T2 中度 | GameTask/AutoLeyLineOutcrop/AutoLeyLineOutcropTask.cs | +57/-56 | R3.4 地脉 JSON 恢复 + 移位噪音 |
| T2 中度 | GameTask/AutoStygianOnslaught/AutoStygianOnslaughtTask.cs | +56/-65 | R3.4 幽境重试恢复 + 移位噪音 |
| T2 中度 | GameTask/Common/Job/GoToSereniteaPotTask.cs | +59/-13 | R3.4 壶新截图恢复 |
| T2 中度 | GameTask/AutoDomain/AutoDomainTask.*.resx | 各 ±12~24 | 茶包本地化资源删减；合并时注意资源键同步 |
| T3 高偏离 | GameTask/AutoDomain/AutoDomainTask.cs | +257/-133 | 见 §3 三大块归因 |
| T3 高偏离 | ViewModel/Pages/OneDragonFlowViewModel.cs | +375/-57 | 形状预检接线、守卫、待迁移 UI、IPC 刷新入口 |
| T3 高偏离 | Core/Script/Dependence/Dispatcher.cs | +155/-6 | 双引擎路由 + 茶包执行体系接点 |
| T3 高偏离 | GameTask/TaskTriggerDispatcher.cs | +116/-17 | 茶包独立执行体系（SoloTask/批次） |
| T3 高偏离 | ViewModel/Pages/TaskSettingsPageViewModel.cs | +868/-24 | **主体是 AutoHoeing 联机锄地设置面，非一条龙**；冲突时按锄地功能区隔处理 |
| 茶包独有 | Core/Config/OneDragonConfigShapePreflight.cs | +162/0 | R3.0 新增，上游无对应文件，不会冲突 |

## 3. AutoDomainTask 三大删除块归因（R3 收口已逐块核实）

diff 中三个最大删除块全部可归因，**无「说不清来源的漂移」**：

1. `StartJsonFight` 整块消失 → 茶包双引擎内联实现（AutoFightOfficial 路由内联），功能未丢失；
2. B15 准备流程重排 → 双引擎边界保留的结构性调整，按代码块恢复时已定保留；
3. 若干方法「被删」实为**移位假象**（diff 配对失败），方法仍在文件内，无行为变化。

## 4. 差异四分法与裁决原则

合并冲突时逐块归因，按类别裁决：

| 类别 | 定义 | 裁决原则 |
|---|---|---|
| ① 茶包特有功能 | 双引擎路由、独立执行体系、奖励识别增强、联机锄地 | **保留茶包侧**；上游改同区时把上游变更翻译进茶包结构，不得整文件回退（AutoDomainTask 等共享引擎的红线） |
| ② 超公版加固 | ASTRA 合同守卫：EvaluateNewConfigName、WriteConfig 提交确认、OneDragonFlowExecutionGuards、形状预检 | **保留茶包侧**；上游若修同一 bug，吸收上游修复并保留守卫语义，冲突块逐行比对 |
| ③ 过渡设施 | R6 退出清单项：AllConfig JsonExtensionData 袋、待迁移保护通道、legacy 引用边界 | 合并时**不得被上游改动静默吞掉**；退役只在 R6 统一执行 |
| ④ 无行为噪音 | using 重排、方法移位、变量改名（如 raInLoop） | 下次因正当理由触碰该文件时**顺手对齐上游**，持续缩小未来 diff；不为对齐单独提交 |

## 5. 合并操作规程

1. 走 $public-merge-assistant 技能；先跑 §6 刷新命令拿到最新分级；
2. **冲突注意力集中 T3**；T0/T1 文件出现冲突 = 异常信号（理论上不该冲突），必须逐行查清原因再解；
3. 合并后回归基线（与 R3 收口同口径）：通道合同 68/68、IpcIncident 37/37、迁移引擎 27/27、助手 150/150、服务端 276/276、网页回执 10/10、执行守卫 11/11、树脂归一化 8/8；BGI 单元测试对照既有失败基线族（PostTeleport/MultiWorld×4/WaitPoint×4/OCR/AutoTrack）逐名核对，**新增失败即阻断**；
4. 合并完成后刷新 §2 快照与本文日期。

## 6. 快照刷新命令

```powershell
git fetch origin-lcb
git merge-base HEAD origin-lcb/main
git diff --numstat origin-lcb/main HEAD -- `
  "BetterGenshinImpact/Core/Config/OneDragonFlowConfig.cs" `
  "BetterGenshinImpact/Core/Config/OneDragonConfigShapePreflight.cs" `
  "BetterGenshinImpact/Core/Script/Dependence/Dispatcher.cs" `
  "BetterGenshinImpact/GameTask/AutoDomain" `
  "BetterGenshinImpact/GameTask/AutoStygianOnslaught" `
  "BetterGenshinImpact/GameTask/AutoLeyLineOutcrop" `
  "BetterGenshinImpact/GameTask/Common/Job/GoToSereniteaPotTask.cs" `
  "BetterGenshinImpact/GameTask/TaskTriggerDispatcher.cs" `
  "BetterGenshinImpact/View/Pages/OneDragonFlowPage.xaml" `
  "BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs" `
  "BetterGenshinImpact/ViewModel/Pages/TaskSettingsPageViewModel.cs"
```

## 7. 维护纪律

- 新出现的偏离必须能归入 §4 四分法；**归不进 = 红旗**，先归因再合并；
- 本文件只覆盖一条龙/自动秘境表面；锄地等其他茶包功能的合并策略另立文档；
- 与 R6「发布与 diff 收敛」衔接：R6 验收以本文件最新快照为输入，T0/T1 清单是「干净公版互用」的量化证据。
