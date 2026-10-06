# 真实路径、编辑保留与整表互导候选接续

来源聊天：01a10ed6-47ed-7f52-9421-30f7dda24905。入口标记：FORMAL-UI-PATH-NEXT-20261006-FROM-01a10eab。
下一唯一标记：PATH-RUNTIME-FINISH-20261006-FROM-01a10ed6。

完整产品未完成。本组是已做相称组件/WPF/存储验证的源码候选，不是独立综合 pass/receipt、全功能实机验收或版本交付。交接发生在真实流程去向与每次到达身份、编辑保留完成可验证批次后，下一步转入不同状态转换：节点时间仲裁与观察器生命周期、完整运行版本接线。不是时间/工具数量强制切换；保存检查点、结束写入、原生暂停读回后才创建唯一接班。

## 已有有效候选

- WorkflowPath.cs / WorkflowPlan.Path.cs / Planner / Runner：实际 next、是/否去向；节点、车道、时刻目标；跨列同一节点并保留到达车道；回边递增既有循环身份，合法再次到达产生不同提交键。条件为星期、时间窗口、固定选择，control.condition/control.end 不下发为 BGI 资源作业。新 flow.route 标记使旧执行器拒绝，不默认为顺序链。
- 恢复沿已落盘游标与所选去向，不重求前一判断；路径不扫描未到达分支作为旧顺序链救援。路径布局首次绑定入 RunStore，冷恢复布局/游标失效在改状态、取消等待及发送前拒绝；热重载此类不兼容布局保留旧定义，不猜路径。
- 停止/受理回执竞态：同一提交受理事实合并到最新记录，保留停止意图；原证据守卫保持。反例确实覆盖 Stop 在回执返回前落盘。
- 真实 WPF：判断与结束节点、相邻覆盖拖柄、去向菜单（节点/车道/时刻）、实际线路预览、分钟细刻度、检查器时间调序。去向下拉 OneWay + 用户选择才写，打开视图不清空 $end。原生窗口开/缩放/置顶/共享草稿/关/返回测试完成，当前执行宿主为 Windows Session 1；不是用户旧运行 Session 2 的全软件验收。
- 完整参数/高级策略/路径/车道编辑撤销通过统一草稿快照，仍保留同一 NodeEditVm、原值与未知字段。未知条件显示“保留自定义”，布局/去向编辑不改成默认星期；有效条件的未触碰字段也保留。
- 删除车道：被任务/跨列覆盖的车道先移走/缩覆盖再删，不自动把任务塞主车道；删除空车道同步调整数字去向，指向被删除车道者保留 removed: 无效标记阻止执行，需明确重新选择；撤销还原。
- WorkflowScheduleTransfer + VM：整表导入/导出新增入口。先完整判型再写，新身份只读候选，不覆盖现有列表；写入中断如实报告已保留候选，不伪称整批原子。公开导出递归移除已知账号值/本机迁移事务依赖。显式激活导入候选仅转为本机编辑；启动仍校验资源与账号。单流程互导原合同保留，冲突拒绝测试保持。

## 验证与失败原件

green-r4：Rebuild 0错误、129/129、0skip、source_drift=[]，真实 WPF 窗口及整表/撤销覆盖。
关键反向突变：临时绕过 PathSuccessor，指定 ExplicitPath_DoesNotRunUnreachedArrayNeighbour 实际 [a,b,c] 对预期 [a,c]，1/1变红；逐字节恢复，causal-restored Rebuild0、129/129、0skip，进程树0。
green-r5：补未知条件保留、删除空车道去向重定位/占用拒绝、布局变化冷恢复零写零发；Rebuild0、132/132、0skip、source_drift=[]、自有进程树0。
最新助手候选 DLL 在原 carrier `_workflow/runtime-unified-01a10cef/single-tests/assistant`，SHA=2eed4daec02b093ba7eda84e9f4e68954d4322c3553c3b900501da05a2a8d2cf。完整 product 尚未更新。
原 red/green-r1/green-r2/green-r3 失败保留：初始接缝冻结事实不足、Stop回执修订竞态、只读属性双向绑定、两处局部变量作用域编译错误，均不能改写为通过。Python直接覆盖源码一度报 EINVAL，已确认文件可读未缩水，后用原生 apply_patch 完成，不做整文件恢复/清库；不要盲跑旧修改脚本。control.py 同名输出在任何源码修改前拒绝；旧证据目录不重跑。

最终当前字节、数据检查与提交范围见 checkpoint-source.json / checkpoint-evidence.json；本地新 HEAD 以接班初始消息和实际 git log 为准。opening=e989dd6ed3a7291b54965e9b4e0a60c6ea6a9235，原上游 opening/report/receipt 不改绑。

## 唯一下一项：运行接线与完整版本

1. 节点 schedule.time 的仲裁 tier/scheduledAt 仍多按根 TriggerTiming：Runner CreateWaitDecisionRequest、TaskCenterHost.Admission.cs 的 successor factory 和等待来源核验须一起接持久节点时刻；Host 灵活空闲 FixedScheduleDeclared 尚主要读根级等待。保护同身份/修订匹配，不自报提高权限。核跨午夜及合法回边/循环的下一次时刻，恢复不挪日、不误重发。
2. 伴随观察器真实生命周期与结果路由未实现。先按已有合同：宿主叶子开始前 arm 就绪；终态先冻结事实再收场再路由；跳过/失败/停止强制收场；等待未挂；Unknown 不当未命中；按节点出现/轮次/attempt/实例隔离。复用现有日志/监控来源，不改第三方JS，不增加施工后台/心跳设施。
3. 完整正式 UI 仍有具体缺口：资源/判断/动作目录分类与添加入口；默认关闭资源目录与选中检查器双模式；当前/下一节点实际标识；跨列覆盖碰撞的 v4 集合布局；进一步实际字段/资源编辑入口及联机/单项既有功能可达性。当前线仅示意实际可能去向，跨列共享节点绘线还主要取主列，不能宣称所有到达车道均已完整呈现。条件/结束节点在待安排显示与结束链尾约束需核对。参数/编辑源保留是强制边界。
4. 复用原 `_workflow/runtime-unified-01a10e1b/product` 依赖、资产、六类资源，安全更新同一完整 BGI+助手，保全测试数据/正常旧数据身份，做开发侧启动、等待停止、保存重启与数据保留，给实际产物身份和启动步骤。不能只交源码候选/截图。游戏/账号/兑换码/树脂/关机/队友动作待用户主动反馈，不代执行、不启动第二套游戏任务，不据此停开发侧交付。

## 保持的边界

采用 mistletoe-release-first-20261005-v2 / mistletoe-storage-limits-20261005-v1。实际单次1.5GiB、累计17GiB、余量8GiB；构建/快照/取证都经 Session + process_runner，不换目录/渠道/工作树绕过。历史报告、唯一成果、原User/配置/第三方JS保留，仅精确登记自有未发布 scratch 可按既有规则处理。
旧 bf733… blocked/98 原级义务、额外会诊2/2余额0保持；本组新请求0，无独立综合 pass。bundle drift、r61-distribution-candidate 缺report保持，发现无新漏登记；不修无关工具、清零账或按小函数重开前审。C01/C02/C04–C11/C17/C20、八单项、组/JS/宏混排和公版能力全保留。
用户语义是流程到达即执行，重复由路径/循环设定决定；不用车道 OR 开关或一天一次去重。整表与单流程都要，勿重问。
旧D:助手/BGI/游戏在用户 Session 2 运行，实时核对，不杀/退出/代执行；本组 tests 在 Session 1。原User漂移源未归因，不用旧备份覆盖。private*不提交/展示。材料外 csproj、MigrationReferenceActivation/MigrationSwitchTransaction、旧设计/总计划/台账/_workflow 保留；git仅明确本批文件 commit --only，不 push/部署。

接班先核实际 cwd、本线程 Goal active、rollout model/effort，再写；继承来源 gpt-6.1-sol/medium 及 handoff-inherit-model-20261004-v1。原Goal paused读回前不创建，已创建失败保留身份不重复开。新Goal接续不表示产品交付。
