# ev3 全量回归失败差集核验（完成判据产物；2026-09-26）

- 最终帧：`Test/BetterGenshinImpact.UnitTest/TestResults/_ev3_bgi_full_final3b.trx`
  （第 2 轮会诊建议处置后的夹具形态＝提交形态；此前帧 `_ev3_bgi_full_final.trx`／
  `_ev3_bgi_full_final2.trx`（第 1 轮处置前后形态）同样差集空，留档）
- 基线：`Test/BetterGenshinImpact.UnitTest/TestResults/r58_bgi_full_20260924.trx`（既有 14 项）

| 项 | 值 |
|---|---|
| 最终帧计数 | 1048 过/14 败/1062 总（基线 1059＋本批 3 例） |
| 基线计数 | 1045 过/14 败/1059 总 |
| 新增失败（最终帧−基线） | 0 项（空集） |
| 消失失败（基线−最终帧） | 0 项（空集） |

**不稳定基线项表征（如实，R3）**：`_ev3_bgi_full_final3.trx`（ContainsKey 采纳帧，已撤回）
同源运行 13 败/1049 过——基线 14 项中的
`WaitPointReportTests.WaitPointReport_SyncPointIdValidation_WorksCorrectly` 在该次全量上下文
**偶发通过**；隔离重跑该例 5/5 失败（单独运行必败）。即该基线失败项在全量上下文存在
偶发通过的不稳定形态，与本批无代码路径交集（AutoHoeing 域 vs 注册表夹具）；复核帧
final3b 恢复 14/14 且逐名与基线一致。两帧与隔离表征均留档入 ev3_trx_archive.zip。
§24.103 已登记过同族现象（同源运行失败名单 ±1 的偶发形态）。

最终帧 14 项失败与基线逐名相同（身份清单见 _ev2/ev2_full_diff_baseline.md 所列 14 项，
本次逐名集合差为空集，不重复抄录以避免双份清单漂移）。

## 本批夹具入帧核验
- JobTreeEvictionAndSamplingTests 入帧 3 例，outcome 全部 Passed：
  - ExitReceipt_RealCapacityEvictionOfTerminalAncestor_IsUnknownNotZero
  - JobRegistry_TerminalCapacityEviction_IsFifo_KeepsNewest64
  - JobTreeSnapshot_UnderConcurrentSubmitAndTerminalChurn_HoldsSamplingInvariants

解析逻辑：TRX XML（默认命名空间）UnitTestResult@outcome==Failed 逐名集合差。
