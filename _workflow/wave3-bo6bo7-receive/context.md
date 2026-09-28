# 接收批目标（objective）

把已独立交付、并按原等级闭环的 Wave3 BO-6/BO-7 修复**接收进茶包主线**，并在主线 HEAD 上重新取得定向回归、助手全量回归、反向突变与声明面证据；只做接收及由集成引起的最小适配，不施工 BO-8/BO-9、R5.6、R6.1、R6 diff guard，不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。

来源交付：`wave3-bo6-bo7-2026-09-28`，隔离 worktree `C:\\Users\\Administrator\\.codex\\worktrees\\wave3-bo6-bo7\\better-genshin-impact-LCB`，交付 HEAD `e009068e22b18d89f4eb9f8947fa937dfa8c925c`，开工基线 `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`。
本批开工 HEAD：`6fd6207e58ec93dcc38645c12131a9a512f5fc69`（`main-OldTeaBag-B168`）。主线相对来源基线只多 3 个文档提交，与来源增量无文件重叠。

接收方式：不合并隔离分支；按 `git checkout e009068e2 -- <路径>` 逐字节导入来源相对基线的 8 个产品/测试/状态文档文件，另收录少量权威证据（检查点、两份独立会诊报告与元数据、会诊台账对账、突变证据索引、testId 对照、风险矩阵）。6 份 closeout 快照的 report.json/packet.md 与逐突变 TRX 日志包留在来源 worktree，不搬入。

完成判据（本批）：BO-6/7 修复确实进入主线；集成版本定向 93/93 与助手全量回归通过且与来源逐 testId 对照；8 项反向突变在集成字节上重新执行为 baseline Passed / mutant Failed / restored Passed 且精确恢复；四项原级义务闭环状态未被削弱；声明面按规则再生并评审；台账回填；生产门保持关闭。
