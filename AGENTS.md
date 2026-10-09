# BGI 项目协作入口

当前版本：2026-10-08。用户明确授权精简全局规则与槲寄生流程；旧的每批双审、全面认证和滚动验证要求已从当前入口删除。全局协作约定适用，本文件只保留项目差异。

## 新源码仓库与历史资料库

- 当前工作根是打开本文件的源码仓库（当前迁移根：`E:\Program Files\better-genshin-impact-LCB-source`）；旧源码根 `E:\Program Files\better-genshin-impact-LCB` 仅作为迁移期间的只读历史来源，不是开发、构建或运行根。
- 普通开发任务只查询当前仓库，不扫描旧目录，不复制 `_workflow`、日志、TRX、构建产物或 User。构建和运行不得隐式依赖旧路径。
- 只有任务中心、流程恢复、停止、迁移、数据保留或用户明确要求历史核对时，才执行一次定向历史门：运行 `pwsh -NoProfile -File tools/mistletoe/source-history-gate.ps1 -ChangedPath <changed-path> -Term <term>`，再读取 `Docs/design/mistletoe-source-history-index-20261009.json`、当前仓库历史和归档 refs；命中相关条目后才读取对应的归档文件。查询结果在本次 Goal/任务内复用，不重复扫描。
- 归档索引缺项时，可以按索引给出的具体相对路径对旧根做只读回退查询；不得对旧根整树扫描、写入、移动或把旧文件自动带入当前仓库。历史迁移尚未完成时，旧根只是后备来源。
- 删除旧根前必须先完成独立 Git 历史/归档 refs、必要证据和 worktree 的迁移核验；迁移清单见 `E:\Program Files\better-genshin-impact-LCB-source-migration-20261009.md`，架构说明见 `Docs/design/mistletoe-source-archive-architecture-20261009.md`。
## 完整产品交付

- 按[当前交付流程](_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md)执行，政策 ID `mistletoe-product-delivery-20261008-v1`。总计划当前约定的全部功能和正式可操作 UI 必须实现；正常旧数据接续、必要资源、启动与恢复步骤属于完整使用要求。
- 用户的取舍保持：功能不能用的问题现在修；特殊条件、特定小范围普通 BUG 可登记后修。有具体支持路径的误执行、数据覆盖、停止失效、未知假成功仍必须解决。游戏内效果由用户测试后主动反馈，不催用户，也不把它作为开发侧停工理由。
- 下一任务从缺失功能或真实使用故障选择。新增测试说明验证哪个约定行为、结果改变哪项交付决定、现有证据不足处和结束判据；不把“缺报告/还没认证”直接当作新产品任务。
- 集中修复、复用同一完整产物和有效旧证据，稳定后完成必要实际运行、停止、重启和数据保留检查，以及当前已授权的整版综合复核。普通文档修改不构建产品，不开新产品审查。
- 当前已批准的模型/强度/固定次数沿真实台账执行，失败或超时按原授权计次；不重置、不自动追加。原历史等级与未决项保留，按本版实际后果判断现在修、延期或范围外，不要求全历史归零。

## 开工与技术边界

- 先核对当前分支、HEAD、相关工作区及在途写者；“我的版本”包括未提交成果，茶包主分支动态确认。“公版”默认核实 `origin-lcb/main`。
- 项目开发、跨端排障和接力使用[项目开发技能](.agents/skills/bgi-project-development/SKILL.md)，按当前问题读取资料；不全量加载旧规格、历史审查或任务清单。
- BGI、MultiplayerHoeingAssistant 和 BgiCoordinatorServer 是独立项目。战斗与传送存在双实现，修改前核实际路由、配置消费和单机/联机调用方。
- 未明确要求不启动 Spec。公版合入茶包用 public-merge-assistant，茶包贡献公版用 teabag-pr-upstream-assistant，指定功能重构用 teabag-refactor-assistant。
- 自动交接、真实模型继承、原生 Goal 和本地检查点提交按全局规则执行。接班携带完整产品目标、当前未完成项、有效证据和有限授权，不再携带旧工序全集。

## 数据、构建与存储

- 保护所有运行目录的 User、配置、宏、截图及用户未提交数据，尤其 BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User。第三方 JS 源码只读，宿主适配不能向 main.js 注入代码。开发侧运行验证使用明确隔离数据根；真实 User 迁移或切换按当前专门授权执行。
- .kiro 原档、其他会话成果和历史失败默认只读；不得广泛 reset、整文件恢复、批量清理或移动真实用户目录。保持编码/BOM/换行，提交仅明确归属文件。
- 构建前检查部署目标；当前项目已有 `DeployToBgiTools=false` 时先核实再用于受控构建。不能为解除文件锁杀用户程序或改构建规则。
- 区分已发送、入队、执行和终态，未知不能当成功或空闲。构建/测试不能替代实际入口验证，旧失败按相同条件核身份与结果，不删测试求绿。
- 生成快照、取证或审查材料时采用[共享存储限制](Docs/design/mistletoe-storage-limits.md)及现有 Session 入口；额度以当前 `_workflow/storage-policy.json` 为准。按本次实际规模预约，复用已有产物，不按最大上限强制预约；不换目录/预算域绕限制。
- 普通读取、rg、文档校对不产生整树快照。工具是按需辅助；不默认 begin/audit/verify、认证收据、矩阵全格或每个断言反向突变。

## 并行成果与报告

- 选择相关集成工作时读取[并行成果索引](Docs/design/mistletoe-parallel-deliveries.md)及 JSON 的 deliveries/preparations，核相关真实终态、文件和当前输入；复用有效成果，不让用户提醒或搬运。已登记不等于已集成或验收。
- 独立复核按[简化审查流程](Docs/design/mistletoe-review-process.md)服务当前版本；安全或功能阻断须有当前依据，历史资料和工具字段不能自行扩大交付范围。
- 汇报用户已能使用什么、实际验证范围、剩余缺口和唯一下一项。旧报告不改绿，延期不写 closed，阶段提交不写整版交付。
- 本轮规则维护不修改产品代码、工具执行逻辑、旧冻结材料或其他聊天 Goal。当前在途请求按其原授权完成；下一安全选择边界和新聊天读取本版规则。
- AGENTS 与主要项目技能已纳入本次明确文件版本管理；其余 .agents 资料仍可能是本地文件，新工作区只核当前任务需要的入口，不能假定所有本地资料已同步。
