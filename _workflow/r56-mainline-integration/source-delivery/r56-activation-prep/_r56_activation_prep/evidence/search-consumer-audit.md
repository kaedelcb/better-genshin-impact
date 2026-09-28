# 搜索与消费者追踪记录

## 固定版本和方法边界

- 固定checkout：C:\Users\Administrator\.codex\worktrees\r56-activation-prep\better-genshin-impact-LCB
- 固定HEAD：5e7e7e22f11daad0c86795368e9a21bb14d79b19
- 查询只读固定checkout；工作树侧最新R5.3 §24.124、计划和SB21文件留在源worktree，另记SHA/status，未混入固定代码判断。
- 使用rg -n、git grep和直接查看定义/调用上下文。C#搜索限定MultiplayerHoeingAssistant、BetterGenshinImpact，排除测试、bin/obj并以*.cs为主；不是对运行时、反射、脚本、配置、生成源码的形式化全称证明。
- 没搜到只描述“在所列版本、目录、文件类型和查询内未发现”。

## 事务与演练实现

- MigrationSwitchTransaction.cs固定SHA-256：87905209E73AE3AE819D4382AA2AF643A942A2C4D2A687270C68D19670DB1ABA。
- 符号行：类86；构造102；TryAcquireExclusive 293；BeginTransaction 313；TakeSnapshot 364；阶段标记495–496；RehearseRollback 530；Commit 581；Rollback 608；RecoverOnStart 670；生产授权/执行713/730；quiescence验证960。
- MarkReferenceUpdateCompleted与MarkActivated是Advance阶段推进，不调用真实服务。固定生产C#查询命中事务定义、注释、测试与演练内部，未发现主机/运行入口调用。
- MigrationRehearsal.cs固定SHA-256：314FCE3E874A5A471A4FF00CC66C740BE2DC4DB48D38907002E7DD54B055E4D0。MigrationRehearsal.Run构造事务并传NoopQuiet，之后对代表文件顺序执行阶段标记（114、127、130）。
- TaskCenterPanelViewModel.cs:78调用_host.RunMigrationRehearsal()；TaskCenterHost.Admission.cs:772,807在助手数据根migration-rehearsal下运行演练。UserConfigRootProvider缺失时保守拒绝；真实User根只用于演练目录隔离/重叠防护。
- TaskCenterHost在117–119实例化flowsDir/runsDir stores；_runs.RecoverOnStart()在166为助手执行记录恢复。本次搜索未发现该路径调用事务RecoverOnStart。
- TryRunProduction在本次生产C#搜索内无Host/Runner/Queue消费命中。该结论仅限搜索范围。

## 助手存储与路径身份

- WorkflowStore.DefaultFlowsDir()在WorkflowStore.cs:78，数据文件*.flow.json及其本地备份。
- RunStore.DefaultRunsDir()在RunStore.cs:59，数据文件*.run.json及其本地备份；与BGI User根是不同路径来源。
- 演练在传入临时configRoot写测试文件，事务快照针对该根；没有把实际助手APPDATA stores作为迁移清单。
- PathIdentity.cs含final-path handle及不可比较命名空间拒绝；R58测试覆盖UNC/设备类拒绝与注入错误。R56测试注释说8.3短别名不能可移植创建；不同盘符/SUBST类测试用不可实际挂载目标的可构造分支。这不证明目标环境身份验收完成。

## 查询和命中摘要

执行的主要查询：

    rg -n --glob '*.cs' --glob '!Test/**' 'MigrationSwitchTransaction|TryRunProduction|MigrationRehearsal\.Run|RunMigrationRehearsal|ActivateCandidate|UpdateReferences' MultiplayerHoeingAssistant BetterGenshinImpact
    rg -n "RunMigrationRehearsal|MigrationRehearsal\.Run|TryRunProduction|MigrationSwitchTransaction|ActivateCandidate|UpdateReferences" MultiplayerHoeingAssistant BetterGenshinImpact --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' --glob '!Test/**'
    rg -n "DefaultFlowsDir|DefaultRunsDir|RecoverOnStart|flowsDir|runsDir" MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs
    rg -n "PathIdentity|UnprovenNamespaces|Short|8\.3|SUBST|UNC" MultiplayerHoeingAssistant/Services/TaskCenter/PathIdentity.cs Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R58MigrationRehearsalTests.cs Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs

显式命中：VM→Host演练；Host→MigrationRehearsal；演练→事务对象/阶段标记；MainViewModel reparse防护；事务/API定义；WorkflowStore/RunStore默认目录和Host启动RunStore恢复。C#目录查询未发现真实引用更新服务、候选激活消费者或TryRunProduction外部调用。动态/间接调用未穷尽。

## 既有组件交付核对

来源worktree：C:\Users\Administrator\.codex\worktrees\r56-migration-audit\better-genshin-impact-LCB；HEAD c11cb45f6de2436b46c58b91d47f2c9768132ef3。report.md SHA：044A62D06DEE49FCDB8E87FAD6ADB30521E03A276217785FACEB2EF817369D97；acceptance.md SHA：377FC50C072818764FA178BAFE14CF4F256464368165B41982EE378EB77055AE。

- 定向：报告记录81/81通过；组件TRX SHA：1A8CDDE9CDF2FD6E55E510426A01248BB151F7CFED95EFC769AE2A523CBB5547。
- 助手全量：基线1479通过/2跳过；最终1482通过/2跳过/0失败，新增3项；最终TRX SHA：80039433438D13B9110A7210332182551375FE498EE4C5F4887E70C4FB5B46。
- 崩溃恢复：子进程在隔离事务根的RollingBack中止，新实例启动恢复为RolledBack并还原旧文件；process-crash-data/result.json SHA：54D2182307AB7CF01C40AE83CCBD3AE9B49F78495ED07869AE39E6593C66F0BB。未启动真实BGI或碰真实User。
- 反向突变及生产gate突变按原报告证据复用；本准备批不再改组件或重跑。
- 组件提交相对固定基线分叉（merge-base 8a4b8d988e2f0ff3ade35538bffa2de7a93c9da2）；不把组件HEAD当主线基线，不cherry-pick其tip。

设施工具来自源worktree未跟踪的tools/mistletoe/workflow.py和deliveries.py，固定worktree缺副本；通过--root指向本隔离worktree并从已核实权威路径调用，不复制工具入产品目录。原始命令输出留在_workflow/r56-activation-prep/。

