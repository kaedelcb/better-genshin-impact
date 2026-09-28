# SB21-4 修复后 testId 差集（最终源码）

| 比较 | 基线 → 最终 | shared | added | removed | changed | unchanged |
|---|---:|---:|---:|---:|---:|---:|
| opening_to_final | 1511 → 1560 | 1511 | 49 | 0 | 0 | 1511 |
| r6_to_final | 1558 → 1560 | 1558 | 2 | 0 | 0 | 1558 |
| r7_to_final | 1560 → 1560 | 1560 | 0 | 0 | 0 | 1560 |
| r8_pre_to_final | 1560 → 1560 | 1560 | 0 | 0 | 0 | 1560 |
| localwait_exact240_pre_to_final | 240 → 240 | 240 | 0 | 0 | 0 | 240 |

比较由 `tools/mistletoe/workflow.py` 的 `parse_trx`/`compare_trx` 生成。shared 表示共同 testId；unchanged 还要求名称、类、方法和结果一致。此比较不证明测试正文未变。

R6→最终新增的两个测试：
- `ea2159f2-e93b-4a94-f8bd-3fd37b9298c5` `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes`
- `76837a78-5830-d89e-daab-11ea2c45307b` `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding`

开工→最终新增49/移除0/变化0；R6→最终新增2/移除0/变化0；R7 与复审前 R8→最终新增/移除/变化均0；精确 LocalWait 前后均240项且无差异。三个宽筛选的284项补充运行均通过，不替代精确240项单独验证。
