# 路径循环入口与定时截止集中修复

接续标记：OWN-ROOT-REVIEW1-PATH-CUTOFF-FIX-20261008-FROM-01a114ee。当前线程 01a1176e-6732-7bd2-9b74-38989fbe5fbe 原生完整 Goal 已建立并读回 active；实际 rollout 为 gpt-6.1-sol / ultra，cwd、main-OldTeaBag-B168、开工 HEAD 3f541bd611279640a4326cb3eb0654ecb5fa4d33 已核。原 Goal、opening、报告、等级与累计预算不重建。

采用 mistletoe-release-first-20261005-v2、mistletoe-complete-usable-delivery-20261006-v1、mistletoe-storage-limits-20261005-v1、mistletoe-work-package-review-20261001-v1。两个问题共用 Planner/Runner 到达和等待边界，一批修复。原独立综合报告中的 PATH-STRUCTURAL-LOOP-ENTRY-1 与 LOOP-CUTOFF-TIMED-NODE-1 保持 important；本记录是源码/执行证据，不代替剩余一次独立修复复核，也不表示整版交付。

本版准入：C08/C09 正常 UI 会跨车道按时间排序，支线 07:00 可能在数组 0、主线 08:00 在数组 1；结构下一轮误到支线。节点 11:00 的等待会越过循环 10:00 截止，误调用前置/提交。最小修复为统一首轮/结构重入入口，轮次等待绑定稳定入口身份；节点等待截到既存绝对截止或有效轮次结束，并在等待后复用原截止清偿/跨天跳过。之后转同一完整产物及实际入口、剩余一次整包修复复核和整版交付。

实现范围：WorkflowPlanner.cs、WorkflowPlan.Path.cs、WorkflowRunner.cs、WorkflowNodeSchedule.cs，以及新的 PathLoopCutoffTests.cs。首轮和 `$end` / 隐式车道尾均用同一主车道入口，显式回边保持选择去向；不加车道配额或每日去重。截止清偿复用 SettleDeadlineWait，未决外部事实、stop authority、原游标与持久时刻保护保持。已开始节点仍可完成，其后继不再启动。scheduled 的 skipAcrossDays 在同一异步等待边界再次检查。

有限矩阵与证据：

- `$end` / 隐式尾：主入口非零序号，下一轮仍是同一主节点、PathLane=0、轮次+1。
- 两轮 Runner：只提交主线，出现身份及提交键不同；未选支线零提交。
- scheduled 主入口非零序号：下一轮仍等待既定轮次起点。
- sequence / fixed 节点晚于截止：前置及提交为零，等待在截止收敛；迟到唤醒也不能启动。
- flexible 窗口跨截止：空闲仅截止后出现，零前置/提交；有限时钟跳跃，不做高速循环。
- 已开始节点可结束，后继零启动；既定 scheduled-round 过期后原轮剩余节点跳过。
- 既有显式回边、路径选择/恢复、定时/停止/终局/LocalWait 关联回归一并保留。

red：Rebuild exit0；11 用例中 10 Failed、1 Passed。原两轮实际出现 `[primary, secondary]`；截止反例调用了不应发生的前置/提交。green 与 restored-entry：Rebuild exit0，305 例为 303 Passed / 原两 LocalWait Failed / 0 Other。新 11 例全部 Passed。旧失败仍是 LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping 的 corrupt / unsupported，原 Expected Cancelled、Actual LocalWaitParking 未改。

因果突变：negative-entry 仅反转两个结构重入分支，5 个入口/主线/轮次等待反例 Failed；negative-cutoff 去掉等待上限及等待后的截止检查，5 个等待反例 Failed，前置 Expected 0 / Actual 1。每次都在 finally 用同目录 temp、flush/fsync、os.replace 精确恢复原字节并读回 hash；negative-*-source-restored.json 保存事实。最终恢复回归以 restored/result.json 为准，不用此文字代替终态。所有执行都有实际 build/test argv、SDK 输出、进程创建身份及 Job 树终态；普通来源不冒充旧认证 receipt。

原文件 BOM / 换行保持；四个源文件没有大幅缩水。材料外 MigrationReferenceActivation.cs、MigrationSwitchTransaction.cs、csproj 及其他修改不提交为本批成果。真实 User、第三方 JS、旧失败/报告/证据未改。运行必须显式使用既定 own-runtime/assistant-data，同一 product；只有受影响助手模块刷新，User 前后 hash 和 BGI 身份单列核验。

工具边界：verify-bundle 仍 exit1 `BLOCKED: review bundle drift`，deliveries 仍报告原 r61 缺报告；保留原状，没有工具修复或伪 permit。开工预算核算中一次自有 controller 在 baseline preflight 尚无构建/测试/源码写入时中断，实际 KeyboardInterrupt 原件在本线程 rollout；进程已退出、锁自然释放，未强抢。随后沿既有 runtime-entry 模板只对重复 old_roots 路径去重，集合完全相同，每个登记根仍核算，不绕额度。新记录走既有 Session/process_runner，非 OS 硬配额。

并行 preparations 的 storage-scan A、migration-acceptance B 原件保留；无针对这两个根因的既有补丁。只读子 Agent 只做现有证据/入口导航，未发实质会诊。第1综合复核已 used1/remaining1；唯一剩余一次固定 gpt-6.1-sol/high implementation 用于稳定修复、受影响实际来源及原级义务集中复核。allowance.json 原0/2初始记录和原 extra2/2 不覆盖，失败/未知及持久 intent 计次。

完整产品仍未完成。普通冷恢复提示、跨道拖动时间精度/收拢交互按原级后修；游戏、账号、兑换码、队友、运行资源 Skip 和关机等等待用户主动反馈，不伪验收或催用户。实际新产物运行证据、独立结论和启动/迁移/恢复步骤完成前不标整版完成。
