# 等待到达时的路径模式降级修复

沿用完整产品 Goal、原批 opening/历史及固定一次复核授权；不建立新批或新预算。原生复核 request `8c795c4e92274393b1d148a722787014`、Sol/high、当前源码 verdict=blocked；101 finding/39 unknown逐项处置及原始 final 已捕获，未改报告。原插入漏步 PATH-ARRIVAL-REVISION-INSERT-1 经独立来源闭合，新 PATH-ARRIVAL-MODE-DOWNGRADE-1 保持 important/open。

本版准入：执行中路径流程的正常编辑/保存 → 删除等待中的全部路径节点并追加合法顺序资源 → 等待醒来采用新链首而误提交。最小修复只在共享 ValidatePathCursor 中保留原 pathLayout 模式绑定，禁止原路径运行接续无路径定义；现有重载异常分支沿用原定义/原到达，Resume 在写状态前拒绝。未绑定的新顺序运行继续正常启动。完成定向修复验证后转到具体有界复核检查点，不扩历史、工具、SDK或其他功能。

验证矩阵：显式重载与自动修订均用正式 WorkflowEditVm 删除/追加/提交命令构造无路径资源定义，零资源发送且保留 gate→end；冷恢复拒绝并保持暂停记录字节/游标；新顺序运行仍执行。先红，再修共享层，受影响原回归与原两项 LocalWait 失败身份对照；仅移除新增模式保护的反向突变须检出，再 finally 精确恢复并重建回归。源码/普通测试证据与新产物实际/完整版本交付分别判定。

写集限 WorkflowPlan.Path.cs 与 PathArrivalRevisionTests.cs；Runner 原六行不改。已有源码、第三方 JS、真实 User、产品 User、其他暂存与材料外依赖保持。当前产品仍是原 b56d5bb4 助手模块，源码修复阶段不声称产品已更新或实际通过；本轮已被用户实体 Escape 停止 Computer Use，不再输入 UI。

启动中心补验被提前并行，偏离交接的唯一下一项，已停止并保留用户中断。自有 Job 活动进程0，原28字节启动配置及助手配置逐字节恢复；任务中心runs3864、产品User9501、真实User9522哈希集合未变，未执行启动流程，不能记补验成功。记录位于 ../own-runtime/startup-supplement-01a11897/；不再次启动该补验。

当前唯一复核已用1/1、余额0，旧grant2/2保持。修复/回归不计新增复核请求；没有追加授权，不再派模型，不将新候选伪记独立通过、原级 closed 或整版 complete。原报告/原失败/原99/39与后来101/39身份保持。

## 本次安全终态与候选证据

正式编辑命令红反例10例7P/3F：显式与自动重载均错误提交新增资源，冷恢复未抛异常；新顺序正例及原六例通过。共享模式绑定修复后，受影响范围315例313P/原2LocalWait F/0Other。仅删新增模式拒绝行的负例再次7P/同3F；finally同目录tmp、flush/fsync、os.replace逐字节恢复，恢复后再Rebuild与相同315例，313P/原2F。原311结果未删且各原testId outcome均保持，新增4个testId全部Passed。红/负失败断言分别是Assert.Empty实际有新资源、Assert.Throws实际没有异常，具名断言与栈由verification.json核对。

WorkflowPlan.Path.cs修复SHA f7c8f7ab5bb068b466c0b867dbb66ec8fb6de419aa757266ed76259c4ca4de47；测试SHA 2a511e2197649d5afc8b47bc8f766e5d22f6181e5bbfd9e2924979e6c78c3f46，原LF/无BOM保持。Runner e9afdaa6未变。313编译输入在green/restored完全相等，突变只改变Path源，旧到当前输入只改变Path源和本测试。四阶段构建exit0，所有build/test Job active0，无在途突变。原失败ID仍7ff2dc0d-a3c3-d14e-a619-fee985a3deca和62d390bb-e91f-98d9-6e78-38ce05d3ea47，不改其Cancelled/LocalWaitParking合同冲突。

启动/助手自有配置再次核逐字节恢复，runs3864、产品User9501、真实User9522前后哈希相等；b56d5bb4五模块及原BGI DLL/EXE未改。当前是源码修复候选，important状态为implemented_with_evidence_pending_independent_verification；旧blocked原报告不重写，产品模块没有刷新、新候选实际入口没有补验，不标完整Goal完成。OWNER-CHECKPOINT.json给出已经可审阅的修复及仅此问题、固定1次Sol/high的待决范围；未收到授权、不发追加请求。仍在同一状态链，继续本聊天承接其有界复核，不另建施工聊天或Goal。
