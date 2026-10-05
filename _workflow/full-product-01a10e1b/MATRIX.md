# 完整产品施工矩阵（2026-10-06）

交接 FULL-PRODUCT-DELIVERY-RESTORE-20261006-FROM-01a10cef；自身原生Goal active，实际cwd正确，rollout model=gpt-6.1-sol/effort=medium，settings一致。采用release-first-v2与storage-limits-v1；1.5GiB/16GiB/8GiB。沿原plan/manifest/预算，不重开包。以下为本轮审计状态，不是验收结论。

用户当前定义：完整版本包含总计划全部约定功能及UI；导致功能无法使用的BUG现在修，特殊条件/特定小范围BUG可后修。旧报告原级和open不改。游戏测试待用户主动反馈。

| 功能 | 当前入口/证据 | 本轮缺口与处置 |
|---|---|---|
| 启动中心、公版独立BGI | 启动中心树/条件/动作/定时/电子狗/日志触发已有UI，BGI原生配置页 | 核当前运行资源与源码身份；游戏效果用户待测 |
| C01 多计划/互导 | TaskCenter.New/Edit/Save/Import/Export；r3实际保存互导重启证据 | 旧证据六项源码逐SHA一致；依赖继续核 |
| C02/C05 重复混排/顺序/动态插入 | Edit.Nodes/AppendNode/MoveNode，保存新修订，ReloadRun | 上下按钮可用但拖拽未实现；补拖拽和明确插入位置，核边界重载 |
| C04 星期/旧过滤 | 节点工作日UI、Planner消费 | 保留显式空；复用相关合同证据，用户游戏待测 |
| C06 指定起点/once | Planner.EntrySeed、RunStore耐久消费 | 迁移起点已接；手动选择起点UI/宿主尚未发现，继续核查，不记完成 |
| C07 普通/固定/灵活 | TriggerMode/Time/Until/MissNextDay UI与Runner | 当前实际链路及nextDay源码证据待统一核；无需游戏等待可自测 |
| C08/C09 循环/截止 | LoopMode/Time/Deadline UI及Runner | 等待停止、绝对截止合同已有；无需游戏可验证部分自行完成 |
| C10/C11 账号/兑换/准备 | 节点账号/兑换UI、BGI前置与后验 | 合同实现与每日历史核；真实账号/兑换留用户 |
| C17 收尾 | 五项收尾UI、终态Observer/RunStore | 实际退出可自行验证安全软件部分；游戏/关机留用户 |
| 原生八项/组JS宏 | 实时目录/单项选择与所属配置修订；r3真实混排 | 当前候选资源齐全性核查；游戏效果不假报 |
| C20 优先级/统一准入/持久等待 | PriorityText、HostAdmission、LocalWaitParking | 停驻卡片无停止/恢复按钮且状态英文，宿主已有方法；补UI |
| 暂停/跳过/重载/恢复 | 五项运行按钮及Host | 受影响UI/Host回归；游戏中效果用户待测 |
| 引用编辑/更新 | OpenResourceEditor/RefreshResourceReference | r3实际打开、旧引用拒绝、草稿更新；同页导航BUG有规避，后修 |
| 正常旧数据迁移/激活/回退 | PrepareLegacyMigration/Activate/Rollback UI；r3真实IPC | 7真实旧文件仅试转换；当前真实安装/读取/保存/重启尚须完成，保护原数据 |
| 联机/手动/远程兼容 | 既有命令/网页/协调器 | 当前静态兼容核；缺队友真实运行用户待测 |

已读回deliveries：exit2，r61-distribution报告缺失；verify-bundle：drift。原样保留，已有等价文件/入口核查用于产品工作，不扩工具工程。当前未观察到BetterGI/助手/MSBuild/dotnet/Rider进程。

状态/验证矩阵：编辑旧草稿不修改冻结运行；拖拽只允许同草稿且保留nodeId/引用/策略，取消拖拽零变化；停驻无驱动时只开放Stop/Resume，不开放Skip/Pause；Host继续独立守卫责任、停止权威、准入。反例先验证停驻UI不可操作，再修复后验证；编辑变更执行身份与持久读回测试。原综合blocked与额外复核剩余0保持，统一稳定材料前不发送新请求。

唯一下一项：集中补实际UI缺口，统一Rebuild与相关回归，再形成同一完整产品产物与无需游戏实际验证。
