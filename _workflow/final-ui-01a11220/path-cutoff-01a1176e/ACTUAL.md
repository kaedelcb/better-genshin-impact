# 同一新产物的有限实际边界证据

源码检查点 15adb12da6396ad44afd8479a12dcf51e95f7e6d；助手 DLL 28ce1817fa990476083324d2b121dab8c2130d17e7809adf83876716f05bcec3。product-refresh/result.json 绑定恢复回归实际载体、五助手模块和原 BGI DLL/EXE。两程序均从同一 product 在 Windows Session 1 启动，助手显式 NEXUSBGI_DATA_ROOT 指定既定 own-runtime/assistant-data。D: 用户 Session 未操作。

Computer Use 只用当前 @oai/sky 返回的自有窗口，逐动作观察；没有 PS UIA、游戏动作或脚本注入。正常正式 WPF 调度列表选择/启动，实际进程为 BGI 24056、助手 15188。原生调用/响应和观察保存在 product-actual/private/native-ui-source.jsonl；本记录不把截图或用户待测项目当验收通过。

两个普通、有限的本地判断流程没有高速循环：主线在数组位置 1、支线在位置 0；第一主线判断后等待下一定时轮次，第二主线判断后结束。另一流程让节点下限晚于绝对截止，验证共享 Runner 的等待/到达边界。它们不提交资源或游戏任务；资源前置/发送为零的根因判别力由本批假边界 Runner 红反例与有效 P/F/P 提供，实际资源/游戏/Skip 效果仍未验收。

初始样例时刻在 UI 导航期间已过期，**此前两样例没有任何运行**，未把过期时刻重锚到明日冒充截止通过。开始前保全输入并重新安排时刻；fixtures-retimed/scope.json、两完整实际定义及对应 SHA 是本次实际运行权威输入。现有 apps Session/process_runner 已登记并监测 BASE、DATA 和 product 根，新增小输入仍在同一预算域，没有新快照/渠道。外部定义修订使自有旧草稿显示冲突，正式“放弃当前草稿”恢复入口阻止了旧草稿覆盖，新选择后才启动；未改用户草稿。

实际耐久结果（actual-behavior.json、actual-terminal-runs/ 原件）：

- run-3816f2d66d58 / wf-own-cutoff-01a1176e：定义节点为 02:23，绝对截止 02:22。正式运行卡显示等待到 02:22；耐久 Wait.NextTriggerAt 同值。02:22 实际终态 Succeeded，TailReached=true、Wait=null、StopRequested=false、NodeOutcomes=[]、SubmissionHistory=[]。没有到达 late 或 end。
- run-7f32238e28d0 / wf-own-entry-01a1176e：第一次 primary@loop0（SequenceIndex=1），持久游标 primary@loop1 / PathLane=0，等待 loop.scheduled 到 02:23；02:23 第二次 primary@loop1。实际终态 Succeeded，LastScheduledRoundWait=1，只有两条 branchYes、无 secondary 到达、无资源提交。第一/第二轮不同持久出现身份。

两运行均由产品写入，没有手写运行、准入、成功或封印记录。末尾读回原文件后保全 SHA；完成后恢复本次原始自有输入，再由当前 runtime 收回临时 loop/triggers/terminal。原流程、旧运行和失败身份保持。

正常关闭两程序，原进程 handle 返回 NORMAL_APP_EXITS [0,0]，apps-process-result exit0、apps-tree-terminal active_processes0；进程/窗口均消失。协议值已恢复；9501 个 product/User 文件前后未变、未删除。fixtures-withdrawn.json 保存临时样例 loop=null、triggers=[]、terminal=[]。这证明当前影响部分的有限本机运行与正常退出，不代替旧 C17 自退出效果或整版全部功能验收。

准备失误另存 preflight-runtime-cancel-recovery.json：第一次 actual 在 Session.baseline 尚未进入 body、无新 reservation/程序/数据写入时撤回，纠正 schema/扁平策略 JSON。对应工具 session1898 exit1、自有 PID34720/nonce、无 body 目录和无后代事实核对后，仅精确释放该残留预检锁，账本未改。原失败未删除，没叫成功。

不变正式 UI/互导/旧数据迁移激活回退/原自有软件退出证据，沿交接来源与限定条件复用；四个本次源码不修改这些界面和迁移实现。综合判断由剩余一次独立 Sol/high 修复复核作出，原重要级和原报告 blocked 不改。完整产品尚未宣布完成。
