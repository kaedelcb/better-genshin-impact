# 项目知识导航

用于接手具体功能、查历史决策或继续既有重构。路径相对仓库根目录。原始资料可能过时；本文件负责导航，不复制整本历史经验。

## 阅读顺序

1. 从下表选择相关领域摘要，用它找源码与专项复盘。
2. 关键词检索 .agents/rules/bgi-implementation-patterns-v2.md、.agents/memory/project-experience.md，重点读同主题的新结论、用户纠正、已取代说明。
3. 回到当前路由、实现和调用方核实；跨层变更再扩展阅读。无需全量加载所有领域。

| 任务 | 领域入口 | 专项来源 |
|---|---|---|
| 联机锄地、同步、掉线、世界轮换 | .agents/knowledge/domains/multiplayer-hoeing.md | .agents/memory/fix-collective-skip-applied-ack.md；实现模式 v2 的房间一致决策章节 |
| 传送、地图、缩放、识别、误点击 | .agents/knowledge/domains/teleport.md | 对应 .agents/specs/ 或 .kiro/specs/；当前传送路由与资源 |
| IPC、SignalR、远程命令、跨会话 | .agents/knowledge/domains/ipc-signalr.md | .agents/memory/fix-startup-cross-session-status.md；Docs/交接-联机助手跨会话注入与任务误杀修复.md |
| 配置组、一条龙、启动、抢占、恢复 | .agents/knowledge/domains/configuration.md | Docs/design/unified-job-registry-master-plan.md；.agents/memory/fix-online-hoeing-state-lifecycle-overhaul.md；.agents/memory/fix-onedragon-fake-terminal-early-resume.md；.agents/memory/fix-onedragon-batch-namelist-swallow.md |
| WPF、绑定、显示、布局 | .agents/knowledge/domains/wpf-ui.md | .agents/rules/ui-layout-debugging-discipline.md；当前 View/VM/样式与可见证据 |
| 公版优选、合并、茶包重构 | .agents/knowledge/domains/upstream-merge.md | .agents/rules/origin-lcb-main.md；.agents/specs/teabag-refactor/refactor-feature-inventory.md 的 §H |
| 编译、测试、修改不生效 | .agents/knowledge/domains/validation-debugging.md | 当前 csproj、相关测试及 project-experience.md 的最新相关复盘 |

总索引：.agents/knowledge/index.md。旧 bgi-implementation-patterns.md 为历史档案；KIRO 任务索引只供追溯，旧的未勾选任务不是当前待办。总计划需结合后续实施记录、ADR 和当前代码判断，不能凭页首「草案」下结论。

## 容易选错的代码边界

- BGI 主程序、MultiplayerHoeingAssistant 助手与 BgiCoordinatorServer 服务端是独立项目。跨端改动逐端检查，不以只编译一端代替整体协议验证。
- 战斗有 BetterGenshinImpact/GameTask/AutoFight/ 与 BetterGenshinImpact/GameTask/AutoFightOfficial/ 两套。2026-09-16 源码抽查确认 OfficialAutoFightRouter.UseOfficial 在联机参数为 true 时返回 false；使用前仍核对调用方上下文。
- 传送的 TpTask 根据配置分发至 TpTaskOfficial/TpTaskFastDrag。检查直接调用与实际配置，不能由联机战斗规则推导全部传送也同样分流。
- 联机专属目录不是全部影响面；共享 PathExecutor、战斗和 TaskControl 中也有茶包增量。修改共享层前明确目标调用方及应保持原行为的调用方。

## 公版同步和重构取舍

核实「公版」目标 origin-lcb/main 的实际 ref；当前工作副本包括未提交改动。独立配对文件存在不证明某份文件可以覆盖，先比较本地增量、依赖与路由。

茶包增量优先按功能组织到独立文件，能包住原行为时采用包装层；需要替换原行为时再评估转发入口或必要改写。搬移要检查实际行为差异及源码编码，不能只用编译成功证明等价。

继续 teabag-refactor 时看 refactor-feature-inventory.md 的 §H 和相关功能记录。按用户本次授权选择工作项，检查功能归属、公版侧改动、依赖分歧与验证；不自动接着做全部旧计划。
