# 统一候选后审与迁移恢复候选检查点

2026-10-06，仍原包 local-wait-admission-gates-20261004；采用 mistletoe-release-first-20261005-v2、mistletoe-storage-limits-20261005-v1，原 opening/报告/原级/预算不重开。

用户最新直接答复：“我只要给我验证内容，我自己后续去验证就可以了。”实机全部改为用户后续自己执行；不要求UID或消耗/关机授权、不代游戏/账号/树脂/兑换码/关机。当前 VALIDATION.md 已完整写操作/判据/恢复/证据，但候选身份需随新助手更新；原总目标未验收，不complete。

## 本轮实有结果

- 原产品298afbc45/c91b17383在HEAD180d7b35b上两端完整默认依赖Rebuild0，SDK8.0.406/MSBuild17.11.22，候选 `_workflow/runtime-unified-01a10c87/candidate-r1`，BGI与Tools助手。源2253项前后同SHA。BGI在Session1实际启动，八类任务UI创建保存、正常退出重启，配置SHA511a2cfa...一致。未游戏/助手全UI/真实迁移IPC/OS/联机验收。
- 补齐旧D只读570路线资源/1,195,076 bytes，逐SHA；不是用旧程序证明新源码。候选未整树拷贝旧User；用户自备合法引用资源，JS不改。115个现有AppData文件只读备份到private-user-backup，再查全部原文件未变；该私有目录不得送审/提交/分享。
- 启动检查触发原程序现有BetterGI://注册行为；启动前未取原注册键，不能宣称逐字回退。只在当前值仍是自己candidate的CAS条件下恢复到助手配置中真实存在的旧D exe，读回；没有启动旧程序、没有改助手配置。前后见protocol-route-restored.json。
- 首轮组合测试载体错误把MHA8依赖覆盖BGI9、缺probe/TestData，原失败保持。r2独立完整构建各载体/补probe/注入实际candidate模块，28文件测试后同SHA：助手2509/2502P/5F/2skip，BGI611/607P/4F。完整身份/失败栈与argv/Job终态/TRX保留，非认证receipt。

## 最后综合独立复核（已完成，原报告blocked）

唯一bf73360e2886405bad322e9dbe41983d，快照E:/CodexReviewSnapshots/terminal-candidate-20261004/bf73360e2886405bad322e9dbe41983d，SHA28185de3...；实际子Agent01a10caa-0961-7342-bd54-6d58b0de2eb0的rollout model/effort/settings均gpt-6.1-sol/high，actual final与task_complete已捕获。原final带记忆citation尾注，原件完整保存；report.json是原JSON前缀，非改写报告或receipt。98原key/等级/obligation逐项比对保全。

额外implementation2已用2/余0（026c0ee0为1、bf733为2），plan0/CLI9/native22及所有失败历史不清零。不得用同Agent续轮冒充不计次复审。bundle drift/policyfalse/9f85/原收据不改；audit-review-r2原drift及verify缺report保持，没有工具放行。

报告本版具体直接阻断仅MIGRATION-RECOVERY-ENTRY-1 important open：含nativeMigrationTransactionId的未提交流程执行读取应隔离，但回退也先LoadSnapshot而被挡住。九个失败独立逐项裁决在report：静态注释命中、旧ranking种子无冻结流程、corrupt/unsupported坏owner零发布保全后合法重试oracle、旧生产false门期待、OCR几何排序/Regions原检测顺序、A1/A2固定Unfixed模拟、stuck旧20秒而现合同30秒。保留原失败/原级，不伪全绿、不扩大无现实关联历史工具工程。

## 后审定向修复：源码候选，不是独立closed

三产品文件WorkflowStore.cs、TaskCenterHost.cs、StandardMigrationConsumer.cs及WorkflowMigrationConsumerTests.cs。原LoadSnapshot提交门完全保留；新增只读WorkflowMigrationRecoveryDescriptor仅身份/原资源引用，没有WorkflowDocument，不可传Runner。读取核形状/事务ID/ConfigRoot/声明路径与归属/ActivationRecord/原快照SHA及原candidate身份；Host回退用此描述从原source-index找原安装绑定，原双根验证/事务/后改拒绝继续原链。页面隔离守卫未取消。实际回退仍既有事务执行。新reader有before-reference的空声明分支沿原阶段合同，未新认证全部极端故障组合。

证据recovery-fix：red-r1真实事务停ReferenceUpdating/Activated，2指定红/21P；green-r1为我误写manifest.Changes的编译失败，已纠正ChangedFiles，原日志保留；green-r2 23/23；加入原页面VM命令及重复回退后green-r3 23/23。entry反向突变插入原执行LoadSnapshot，2指定红/21P；identity放宽事务ID检查，foreign指定1红/22P；finally各源原子恢复同SHA。restored-full-r1 Rebuild0，助手2513/2506P/5F/2skip，四新增通过，原五身份保留。源前后、products、argv/TRX/Job终态均在对应目录。不是新独立复核/认证receipt，不把important改closed。

当前源码SHA：WorkflowStore 0658bbb38282c225dfc9b937e4006c228000cc6ca9a3c475325af280a56fd9db；Host30776b75f5d1e3f39bce1af2bc1727b2b94dd3ef59fa888cc8486ca980d2231a；StandardConsumer d44d035fd842c31aeb6916cb2f084e32869f69fac8b540b8beeed99d1a9d02c3；测试 f4fc670dcd925117f8b4a58b1bf4552808fb6dda80f87b990253fb3f0841d1c9。BGI源码未变，Module E03aadbc...；candidate-r1里的MHA还是修复前5f95bf0f...，不能当本补丁运行版本。最新助手程序与测试模块在 `_workflow/runtime-unified-01a10c87/recovery-restored-full-r1`（完整测试载体，不能整目录当发行）。

## 唯一下一交付项

先核源码/提交/终态，装配含该恢复修复的同一完整运行候选与BGI+Tools助手+必要资源，绑定实际模块/依赖/SDK/源码SHA；复用未变的合法资源与BGI，不重复整库拷贝，不Rebuild硬链接旧不可变证据，不链接User。相称联合迁移/标准写方回归需要绑定新助手（BGI原模块未变），新实际候选模块与已测模块若不同须复验对应部分；不只凭同版本号推断等价。不要交含testhost/ControlledWriterProbe/TestData的测试载体当分发；完整依赖可按原normal MHA包文件清单及deps核，不盲混8/9依赖。

更新VALIDATION版本/事实/关联迁移前置，说明重要候选修复已有测试但独立修复复核尚未取得（额外额度0，不能再发）。不拿用户实机替代独立审查、不伪产品通过；若需要最终独立签署，只有具体就绪补丁后的固定追加范围/次数可交owner决策，不让owner搬Goal/模型/文件。用户当前只要验证内容，必须完整交付清单、启动/恢复步骤和候选状态；不要重新索取UID或执行游戏/关机。不得顺手清九个旧失败或扩认证平台。

## 保护与现场

原4个产品材料外换行status（MHA.csproj、MigrationReferenceActivation.cs、MigrationSwitchTransaction.cs、TaskCenterPanelViewModel.cs）内容numstat空，未改/恢复；全部其他材料外、旧报告/失败/JSON/私有备份/runtime保留。git提交仅本次明确文件。无在途产品/游戏写入；所有构建测试终态后才交接。具体最新HEAD/暂存/status及本地提交范围以本目录commit-result/safe-terminal及接续握手实时读回为准。

交接理由：恢复修复/红绿/两因果突变/恢复完整回归达到聚焦安全边界；版本装配/验收说明是剩余交付链。本上下文发生依赖拼装、原生输出phase/footer/编码以及manifest字段假设纠正，继续同上下文承担最终来源与包装判断有可定位风险，不是90分钟/工具数强制。先本地保存、源恢复读回、原生暂停同Goal读回paused，再同项目local唯一接班，继承当前实证gpt-6.1-sol/medium，完整初始Goal一次发送核验；不双写、不复活祖先Goal。
