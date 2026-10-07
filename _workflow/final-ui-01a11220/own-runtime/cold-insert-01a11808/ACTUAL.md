# 同 BGI 冷恢复通过；运行中插入未到达

本线程01a11808-ac6c-7132-8e5d-391a499678cf的原生完整Goal读回active，实际rollout和settings均gpt-6.1-sol/ultra，分支main-OldTeaBag-B168、开工HEAD b3aab71f428b12fd990e380c1b6d060d0e32b705。采用完整交付、交付优先与存储限制政策；本轮原产物/源码身份在admission.json，未重建旧Goal/opening/账本，审查grant已2/2、余额0。

两段助手在同一Job内正常退出并冷启，BGI始终为8660、同一创建纪元。助手25436正常exit0后，原04:35时刻过期，于04:36:10冷启5944；04:37:25正式UI恢复原run-f4b7a4d95adc，ScheduledAt与wait真实变为2026-10-09T04:35+08:00，OriginalScheduledAt仍2026-10-08T04:35+08:00、MissPolicy=nextDay、停止权威epoch未变，零节点/提交。04:37:46正式UI停止后原run为Cancelled、StopRequested=true、零节点/提交。未等一天执行，未改系统时间，未替换原跨BGI纪元失败run。

插入的原失败必须保留：04:38:44正式UI启动run-8386f7c03944，原修订82b5a7c6，等待gate至04:49。通过“＋添加任务”真实新增n-2cb64e03，正式检查器连接gate.yes→新增节点、新增节点.yes→end，no未选分支保留。04:45:18保存新修订1aa5c4c9，04:45:41正式菜单登记“重载修订（节点边界）”；等待期间仍原修订、原时刻、零节点。04:49实际记录却仅gate/branchYes、end/succeeded，未到达新增节点，虽记录已采用新修订并Succeeded，不能称插入验收通过。

正常安全终态：三个app exits=[0,0,0]，apps-tree-terminal.json active_processes=0；协议恢复；product/User 9501文件和真实Debug/User 9522文件前后SHA集合完全相同，未改或删数据。原run/flow、第一助手退出快照、最终快照、UI原生来源和控制器来源均保存在本目录。真实User原位、第三方JS只读，未启动游戏，未改产品源码或审查报告。两个自有新流程在失败归档前不改写。

本版新增准入PATH-ARRIVAL-REVISION-INSERT-1（important）：正式“等待中插入→保存→边界重载”可达，用户期望新增节点到达，实际仍按旧gate定义先选择end；随后ProcessBoundaryActions的新路径对账保留已落盘end游标，新增节点被跳过。最小修复在等待完成且尚无节点动作的到达边界对账新修订，再重新走稳定身份/时间门；不重新求值已完成条件、不改已选择的持久路径、不降低停止/截止/布局保护。六个有限反例覆盖显式/自动重载、已完成路径选择、删除等待节点、停止与截止；修复后相关回归/有效突变/精确恢复和新同套产物实际重演。原失败不重跑改写；完成后转整版剩余来源/版本步骤。原综合pass保留原范围，后续源变化不能冒充已经独立复核。

旧报告、101/39原发现与typed unknown、旧失败/原级预算均保持。新增发现单独继承原批责任，不更名重置额度，不发模型或Agent会诊。材料外csproj/MigrationReferenceActivation/MigrationSwitchTransaction及其他成果保护。游戏、资源Skip、账号兑换码队友和关机仍只等主动反馈，完整产品Goal未完成。
