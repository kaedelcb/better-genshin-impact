# R5.6 接点清单

固定代码版本：5e7e7e22f11daad0c86795368e9a21bb14d79b19。本表区分组件API存在与生产调用链接入。搜索边界见 evidence/search-consumer-audit.md；逐状态验收见 acceptance-matrix.md。

| ID / 状态 | 权威条款 | 固定定义和实际消费者 | 已有证据及不能证明的事 | 消费批最小补项与依赖 | 门禁 |
|---|---|---|---|---|---|
| A 真实引用更新与candidate→active；组件阶段API已实现，真实消费者未发现/未接线 | R5.2 §21.4、§21.10、§23.4；R5.3 §24.94及消费时有效登记 | MigrationSwitchTransaction.MarkReferenceUpdateCompleted()/MarkActivated()只推进阶段；MigrationRehearsal.Run在独立根顺序调用。固定搜索未发现生产引用/激活服务调用事务。Host→演练只写migration-rehearsal代表根 | 组件定向81/81及演练证明事务阶段/API和代表文件；不证明真实引用文件更新、候选切换、写后读回、真实User | 枚举真实引用/候选/活动目标及服务；接入真实写入者，副作用与阶段标记关联；覆盖成功、拒绝、未知、取消、恢复、重复及写后窗口。依赖B/D/E的目标清单、静止和责任语义 | 关闭 |
| B 助手流程、引用、激活元数据恢复；边界可定位，纳入迁移事务未实现/未验证 | R5.2 §21.10、§23.4；R5.3 §24.94/§24.96 | WorkflowStore默认%APPDATA%\\NexusBGI\\flows；RunStore默认%APPDATA%\\NexusBGI\\runs；Host组装WorkflowStore、RunStore、LocalWaitQueueStore并启动_run恢复。此恢复不调用迁移事务恢复 | 固定代码证明助手存储位置/构造/启动恢复、迁移快照基于传入configRoot；组件崩溃演练证明独立根恢复。不能证明助手哪些文件属于迁移域、完整恢复顺序或User引用一致性 | 由真实引用/激活调用图确定最小状态清单；证明每项与configRoot关系和冲突处理，再逐项恢复/重复恢复验证。不得推定整个RunStore迁移。依赖A目标集和D静止 | 关闭 |
| C TryRunProduction实际检查点及生命周期；组件API实现，生产调用者在有界搜索内未发现 | R5.2 §21.10、§23.4；R5.3 §24.94 | MigrationSwitchTransaction.TryRunProduction(Action)仅见组件定义；固定C#消费搜索无Host/VM/Runner/Queue生产调用命中。TaskCenter Host入口调用MigrationRehearsal.Run | 组件测试和生产门突变报告支持API自身闭锁断言；不证明实际生产路径受保护或所有生命周期覆盖 | 从真实入口到执行delegate逐调用图接线；列出BGI/助手相关入口，证明门覆盖；验证重启/恢复、并发/取消、异常闭锁。依赖A/B/D/E | 关闭；正确性重要项未闭合 |
| D 静止窗口；组件抽象/失效检查已实现，真实写方覆盖未接线/未验证 | R5.2 §21.10、§23.4；R5.3 §24.94、§24.96 | 组件接受Func<IDisposable>静止窗口，事务绑定session/generation并在回滚/恢复前重新校验；MigrationRehearsal用NoopQuiet。有限搜索未发现真实writer接入 | 组件定向/崩溃测试支持组件合同；不证明真实引用、激活、恢复writer均静止，或提交后回滚能重获有效窗口 | 按A/B实际目标枚举写方；接入所有相关写方；覆盖acquire/release/reacquire/restart/generation、恢复和commit后回滚。依赖A/B目标清单和C/E生命周期责任 | 关闭；恢复路径是启用前置 |
| E 真实入口回执、失败/取消/写后未知责任；组件结果类型存在，真实回执未接线，R5.8部分验证保留 | R5.2 §21.4、§21.10、§23.4；R5.3 §24.94/§24.96 | MigrationResult/MigrationRehearsalReport记录组件/演练状态；固定UI显示演练摘要。R5.2 §21.4将真实入口回执留待R5.8 | 组件报告证明离线事务结果及隔离崩溃恢复；不证明真实入口受理/取消/写后未知回执和责任归属 | 正式集成批明确调用键、受理/完成/拒绝/未知、重复请求及恢复owner；覆盖入口故障窗口。依赖A/C/D接线；真实入口环境验收仍归R5.8 | 关闭；本包不签署R5.8 |
| F UNC、映射盘/SUBST、8.3与身份；部分词法规则/可构造分支测试，目标机身份未验证 | R5.2 §21.4、§21.10；R5.3 §24.94、§24.96 | PathIdentity拒绝不可证命名空间并使用最终路径句柄规范本地路径；ValidateRoots校验根、交叠、reparse。真实路径只用于Host演练隔离检查 | 单测覆盖UNC/设备命名空间拒绝、注入错误和可构造路径；组件报告说明8.3别名夹具不可移植。没有目标机映射盘/SUBST/启用短名/真实User证据 | 在目标Windows环境记录本地/UNC/映射盘/SUBST/8.3身份及接受/拒绝结果；不可比较则fail-closed。依赖A/B路径集和部署环境 | 关闭；R5.8/环境门另行消费 |

## 状态词

- 已实现：固定源码存在可调用逻辑，不自动意味着生产消费者已调用。
- 组件测试：独立组件夹具、测试或报告证据，不代表实际Host/入口。
- 已接线：固定版本中从实际生产入口至真实副作用的调用链可证。本次A/C/D/E未达到。
- 仅设计/待决：条款描述目标或前置，当前代码未证明落实。
- 未验证：运行目标环境或实机证据缺失。
- 重要阻断：影响正确性、一致性或生产安全的门未闭合，不能降为建议。

