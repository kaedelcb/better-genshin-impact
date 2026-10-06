# 正式UI基础候选与真实流程路径接续

来源聊天：01a10eab-e2c9-7da1-b02b-da7476216c97。来源交接标记：UI-RESEARCH-FUNCTION-FIRST-20261006-FROM-01a10e1b。下一唯一标记：FORMAL-UI-PATH-NEXT-20261006-FROM-01a10eab。

完整产品未完成。本组是源码施工候选，不能称正式UI或完整版本已交付。交接理由是已得到可复核的界面/节点时间基础候选，接下来进入不同的核心状态转换——按真实流程路径推进、分支合流及重复到达身份；将此部分集中到接班施工，不按时间/工具数切换，不重开历史账或预算。

## 用户最新澄清，优先采用

用户原话：“他不需要判断几次，他是个节点，有流程走到他，他就应该执行……正常流程无论从哪条车到到了这个节点都应该执行，一般来说，流程都是往下走，是否执行第二次，由设定的流程确定”。车道是流程路径，跨列是同一节点的布局；到达即执行，重复由流程再次到达/循环决定。施工方此前提出的覆盖车道OR、节点门控优先、自动一天只执行一次均已撤回，不是用户批准合同。不要再让用户按列数裁决执行几次。整份调度列表与单个流程导入均明确要求同时支持。

外部正式预研仍为 E:/Program Files/mistletoe-ui-design，只读。来源 layout.ts 实际已有 v4 跨列聚类，较旧交接提到的版本更新；其他文件继续按当前源码核对，不覆盖外部原型。已定简易化方向、WPF/C#/MVVM、全部原约定功能、功能优先政策保持。旧v6/旧CODEX HTML不复活。

## 本组实际改动及证据

- 新 ScheduleListView 接入 MistletoePage：删除BGI大卡及旧流程列表大卡；顶部流程选择/入口、运行chip/停止暂停恢复/更多，完整时间轴、基准切换、密度、集合展开、资源搜索/拖入/三步添加、待安排、节点检查器。完整旧流程/策略设置保留在折叠区，最近20历史只读折叠；迁移/回退/资源编辑入口保留。
- 同一 TaskCenterPanelViewModel/WorkflowEditVm；弹窗是真正 Window，包含置顶/缩放/返回和本次生命周期位置记忆。测试实际渲染了两个WPF视图并观测共享草稿；未打开正式软件中的原生弹窗，不宣称全窗口运行/交互已验收。
- schedule.time 节点策略实际由 Runner 消费，开始/固定/灵活窗口时间、等待停止/暂停、持久时刻和恢复基准接入；UI编辑提交仍走原保留式副本/新修订。新元数据 scheduleLanes/scheduleLane 当前仅布局，**尚未决定运行路径**。
- build-r6：Rebuild 0错误，31/31；causal-negative：绕过时间消费者导致指定等待停止夹具 TimeoutException，1/1变红；源码逐字节恢复；causal-restored：Rebuild 0错误，含新夹具、VM、旧Planner/Runner定向回归118/118、0skip。原编码/BOM/换行复核保持。全部自有构建/测试进程树终态0。
- 证据：checkpoint-source.json、checkpoint-evidence.json、causal-result.json、causal-negative/causal.trx、causal-restored/causal.trx、causal-restored/schedule-render.png；脚本可读，勿复跑已存在输出目录。源候选助手DLL SHA=4f329601250afed7f2d24be2c27346be7fb5e08963f393f8c93203a61efd5eaf，位于原 carrier `_workflow/runtime-unified-01a10cef/single-tests/assistant`。完整 product **未更新**，其旧BGI/助手SHA仍见前一交接。

不存在独立综合复核pass/receipt，也没有游戏/真实BGI运行证明。review-bundle只读检查为 drift；并行发现无新漏登记，旧r61-distribution-candidate缺report错误保留。没有修工具、发送会诊或改变旧义务/预算。未用子Agent：本组紧密耦合UI/草稿/时间及用户澄清，保持唯一写者。

## 唯一下一项及完整剩余范围

直接继续**真实流程路径与全部正式UI/功能接入到完整可使用版本**，不只交方案。先核对下列具体缺口，再按功能合组实施/相称验证/交付：

1. 当前 Next/CommitOutcome/HasCompletedOutcome/修订重定位及恢复义务仍依据顺序链。必须接条件是/否实际去向、车道/节点/时间目标、跨列同一节点，并保证合法再次到达可再次执行、恢复不会误重发。不要用车道开关或固定“一次去重”代替流程。连续布局范围可用预研的相邻拖柄；真正改变运行含义且文档/用户话不能判定时才精确提问。
2. 当前 LaneIndex 仅显示；没有跨度拖柄/真实线路/条件节点求值/解除或时间目标入口，不能作为已运行分支交付。任务、判断、动作三类目录、结束链尾与观察器实际能力继续补齐；不删原C01/C02/C04-C11/C17/C20、八单项、配置组/JS/宏混排和公版能力。
3. 节点固定型的实际仲裁tier、其他灵活候选的固定声明仍主要依据根触发器，需要按新节点时刻核对接线。手工编辑时间的调序/流程去向、跨午夜/循环到达的时刻、重复到达身份须与新路径一起处理。当前ScheduleNode拖放会按时间重排；检查器直接改时间尚不调用该重排，不能假称所有编辑已一致。
4. 撤销目前只覆盖调度/车道/新增删除/重排，未覆盖所有参数和旧高级策略编辑。小时/分钟按钮目前改变比例，分钟细刻度及展开分钟刻度尚缺；剩余布局/连线/选中态、检查器实际参数、添加引导、弹窗真实开闭/缩放/返回/草稿保留验证要完成。
5. 顶条导入/导出沿用单个WorkflowDocument旧入口；整份调度列表格式与入口、候选/激活和冲突保护、账号值保护未补齐，用户已明确两种粒度都要，不再询问范围。
6. 复用现有完整目录 `_workflow/runtime-unified-01a10e1b/product` 的依赖/资源，完成后安全更新同一BGI+助手版本，处理并保全自有测试迁移数据，给可启动实际版本/步骤及诚实开发侧运行/等待停止/保存重启事实。不能只交当前候选或本截图。游戏/账号/树脂/兑换码/关机/队友动作待用户主动反馈，不代执行，不据此停开发侧交付。

## 数据/写者/存储边界

9498个原BGI User文件与旧基线比较，9497相同，一个203字节JS运行进度JSON在2026-10-06 01:07 UTC发生变化；42份JS源码全同。变化当前字节已保存到private/user-current-1.json，索引private/user-current-drift.json；这不是本组源码编辑目标，不恢复旧副本，不提交/泄露UID。来源尚未归因。原助手116文件逐SHA一致。用户会话2目前有旧D:版助手、BGI和游戏运行，不退出、不杀、不代执行，不启动第二套游戏任务。按实时状态保护，不把前一聊天“已退出”当现状。

本组未写原User、第三方JS、注册协议或旧运行目录；仅源码/自有carrier与受控证据。材料外csproj、MigrationReferenceActivation.cs、MigrationSwitchTransaction.cs及旧设计/账本/总计划保持，不能整库提交。长路径假删除先核实，不恢复/提交删除。

采用 mistletoe-storage-limits-20261005-v1；实际policy：单次1610612736、累计18253611008、余量8589934592。所有本组构建/取证用storage_limits.Session及process_runner，未整树复制。历史材料、独有证据及User保留；不能绕输出路径/工作树/渠道规避。private*与前一测试数据档案不提交。旧bf733…blocked/98义务及额外会诊2/2余额0不变；新Goal不重置、不超额。按用户最新要求旧复核/历史工具/集成余项可用后做，不当全局功能施工前置。

## 交接核验

工作区 E:/Program Files/better-genshin-impact-LCB，main-OldTeaBag-B168，opening HEAD=8e27f61325aa5d0169311b446559a0a12d527481；本组明确文件本地检查点提交后，实际新HEAD在初始提示词给出。当前文件可用git log定位归属；不要改绑旧opening/receipt。

来源实际模型从本聊天rollout最近有效turn_context只读核对：2026-10-06T00:45:12.591Z，model=gpt-6.1-sol，effort=medium，collaboration_mode.settings一致。采用 handoff-inherit-model-20261004-v1。来源Goal在原生暂停读回前保持active；只有暂停确认、旧写者终态成立才创建唯一同项目local接班。接班先核对实际cwd/rollout模型及自身完整Goal，读回active后施工。完整产品未完成，交接不等于交付。
