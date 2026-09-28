# wave3-bo8-bo9-2026-09-28 开工范围与目标

## 一句话结果
在茶包主线施工 R5 Wave3 剩余的两项原级未闭合 **IMPORTANT**——**BO-8 R29**（恢复点之后推进段无完成过滤）与
**BO-9 R34 F5**（多有效停驻跨轮次推进），并在同一批（同一份 `WorkflowRunner.cs` 哈希重绑定）内修正已登记的
**BO-6/7-D1** 建议级注释陈旧残项。

## 范围与依赖顺序
- 只施工 BO-8、BO-9 与 BO-6/7-D1。
- 不施工 R5.6、R6.1、R6 diff guard；不提前集成任何其他并行成果。
- 不重开：BO-6 R19／R21 F4、BO-7 R21 F1/F2（已原级闭合）、BO-13、SB21-3 BO-4、SB21-2 BO-10/12。
- **BO-8 与 BO-9 同属 `DriveAsync` 推进层**（BO-8＝推进段按稳定身份跳过已完成出现；BO-9＝真实链尾按计划全序重建并
  重入**未履行的恢复义务**，含仍存活停驻与其同轮前插未执行出现），必须同批收敛，不得拆成互相绕过的子批。
- 生产门：BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程继续关闭；本批不声称实机或生产验收。

## 开工状态（opening.json）
- 分支 `main-OldTeaBag-B168`，开工 HEAD `f47b57b1cba90e78624ae6b6d2236aafd402f0e1`。
- 待审源码（工作区 CRLF 形态）：`WorkflowRunner.cs` = `5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`；
  `WorkflowRunnerTests.cs` = `b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711`。
- 上一批主线接收已完成（`1945913a4`／`8a014cf78`／`f47b57b1c`），BO-6/BO-7 已并入主线并经集成回归与 8 项反向突变重跑；
  其证据绑定当时的 `WorkflowRunner.cs` 字节，**本批改动该文件后旧哈希与旧突变绑定失效**，故全部受影响突变在本批重做。
- 材料外既有未提交文档（本批完整保护、不提交、不修改）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、
  `Docs/design/unified-job-registry-master-plan.md`。
- 部署目标开工读数：`BetterGenshinImpact/bin/x64/.../Tools/MultiplayerHoeingAssistant`（1158 文件，
  目录 LastWriteTimeUtc 2026-09-26T21:41:14.1381449Z）与 `bin/Debug/...` 同名目录（2242 文件，同 21:41:14.1215991Z）。

## 子 Agent 评估（按要求记录判断）
不使用固定版本只读子 Agent。理由：本批改动集中在 `WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor` 单一共享状态链，
按用户指令不得由多子 Agent 并行改代码；只读子 Agent 能提供的信息与主执行者必须自行完成的代码通读、反例构造、
突变与回归重叠，且项目规则明示 helper／只读报告不能替代真实 Runner 端到端证据（BO-8/BO-9 的闭合判据正是真实驱动）。
故净收益为负，记录为 `not_used`。

## 执行入口
`python -B tools/mistletoe/workflow.py begin|audit|verify`（v2 manifest、开工矩阵、送审材料与工作区差异）
与 `python -B tools/mistletoe/deliveries.py --root .`（并行成果发现，只读）。证据、反向突变与会诊材料均在
`_workflow/wave3-bo8-bo9/`；工具只做机械核验，结论由源码、TRX 与人工复核支持。
