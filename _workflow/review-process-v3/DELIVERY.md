# 完整审查工序已交付（2026-09-30）

候选版本已通过独立完整复核并接入主线；主线最终回归与提交记录见 delivery.json。已审核来源提交：f474793be78269cd78a4b387045dfdc4f1352316。本文件不表示 R5.6 产品施工已完成或已恢复。

执行入口：Docs/design/mistletoe-review-process.md、Docs/design/mistletoe-workflow-facilities.md、tools/mistletoe/README.md。新代码批次自行完成完整方案、独立本地前审、实施许可、真实执行证据、完整影响链复核、原级重要闭环及收口。每次统一 gpt-6.1-sol，智能选择 medium（默认）或 high（复杂、高风险、未排除风险）。不按轮次固定强度，不使用 Astra，不回退旧模型。

新工作区先按权威提交获取 tools/mistletoe/review-bundle.json 的全部精确文件集合，再核验。忽略的项目入口和Skill有可移植原文及SHA256：inherited-entries/files.json。缺入口由执行者读取对应archive并核对sha256后精确补入target；已有不同内容先保留并审计合并，不能覆盖用户规则。Skill引用的已有领域资料仍按实际权威工作区获取，缺失不能假称已读取。无需owner搬运。

本地模型运行前由执行者检查 Get-Command codex -All、对应CLI --version及登录目录的当前模型元数据，选择确实支持 gpt-6.1-sol medium/high 的现有CLI。本次旧独立安装CLI0.155被服务端拒绝，桌面版自带CLI0.159成功；路径和版本是本次证据，未来必须动态核实。登录只读核验，不打印令牌或自动换账号。原模型请求失败照样计次；不要用一次实质请求测试可避免的本地版本问题。

独立实现复核：final-review10/report.json verdict=pass，unknowns为空，R7-1至R7-6按原must/important等级闭合。final-review10/原始事件、退出码和Windows Job终态齐全。模型读取了完整44文件及其依赖，导航并非白名单；此前真实导航外依赖演练见final-review7/report.json demonstration。

累计会诊：原8次（包含usage-limit失败）+ owner明确最多2次追加。第9次旧CLI模型不支持失败，第10次新CLI GPT-6.1 Sol/high成功；累计10次，追加额度已用完，不重置或借新标题绕过。后续独立目标仅依owner明确范围建立自己的账本。

暂停R5.6恢复时沿用原Goal/opening/历史请求/原级未决项，按adopt接入；执行者自行核实原线程和原始报告，不能以磁盘报告数量代替次数，更不能填0。先审剩余修复方案，不倒签已写代码的前审，当前生产门继续按原证据控制。设施接入不等于启动、验收或批准R5.6产品行为。

运行范围：本次验证是Python工具、真实临时Git/进程/Job以及独立只读模型；没有启动BGI产品。门禁和模型审查不能证明语义穷尽、零BUG或防有写权限者伪造所有工具和账本。
