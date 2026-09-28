# Wave3 BO-6/BO-7 本批范围

基线 ref：e2613a851bd45c28fdd56b84dfc10784e1d9c9b8，来自 `main-OldTeaBag-B168` 的开工 HEAD；隔离 worktree，未复制原工作区的材料外变更。

只完成 BO-6 与直接依赖 BO-7。真实 Runner/loop 端到端验证停驻/已完成锚冲突、救援全序、多有效停驻与删除后同身份插回。必须选择完成过滤推进或显式失败态。BO-8/9 不并入；不重开 BO-13、SB21-3 BO-4、SB21-2 BO-10/12；不提前接收 R5.6/R6.1/R6 diff guard。生产入口、真实 User、R5.8、E3/E4/E5、热键、生产进程均关闭；不做实机验证。

权威材料：`_batch21/sb21-4-handoff-2026-09-28.md`、R5.3 §24.120.4/§24.124、`_workflow/sb21-4/raw-bo-obligations.json`、`_workflow/sb21-4/scope-and-state-table.md` 与 `C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch20.json` BO-6/7；并行索引/机器台账及 `deliveries.py` 发现结果。