# 本批提交范围（wave3-bo8-bo9-2026-09-28）

## 纳入提交
- 产品/夹具/生成清单：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、
  `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`、
  `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt`（按 §17.4-A 强制再生）。
- 状态文档：`Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`（§24.127 与 §24.126.5 日期化更新）、
  `_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`、
  `Docs/design/mistletoe-parallel-deliveries.md` 与同名 `.json`。
- 本批证据（`_workflow/wave3-bo8-bo9/` 下）：根记录（manifest/opening/risk-matrix/context/findings/budget、
  run-mutants.ps1、mutations-run-round3.log、mutation-records-round3.json、deploy-target-*、本文件）、`scripts/`、
  `consultation/`（三轮请求/预检/结论）、`claims/`（首轮）、`claims-v4/`（末版）、`baseline/`、`red/`、`red-final/`、
  `final-v9/`（最终定向与全量 TRX）、`fixed/`? 见下、`fixed-v5/`（含第 2 轮修复后 v6 证据）、`mutations-round3/`（14 项权威突变）、
  `review-v3/`（最终送审快照）、`preclose-verify/`（收口前机械核验快照）。

## 刻意留在工作区、未纳入提交（可恢复，非删除）
| 目录/文件 | 大小（约） | 原因 |
|---|---|---|
| `mutations/`、`mutations-final/`、`mutations-round2/` | 3.5 / 3.9 / 4.3 MB | 被中止的初版运行与两轮中间突变集合；已由 `mutations-round3/`（14 项，最终字节）取代，findings.md 已登记其非证据地位。 |
| `final-v3/`、`final-v7/`、`final-v8/` | 各约 2.8 MB | 中间版本的定向/全量 TRX（送审时的版本）；最终证据为 `final-v9/`。 |
| `fixed/`、`fixed-v2/` | 各约 2.8 MB | 中间版本（含被扩展前的 BO-9 夹具）的回归 TRX。 |
| `claims-v2/`、`claims-v3/` | 各约 0.23 MB | 中间声明面再生记录；末版为 `claims-v4/`。 |
| `review-v1/`、`review-v2/` | 各约 2.7 MB | 第 1／2 轮送审快照（其结论已入 `consultation/`）；最终送审快照为 `review-v3/`。 |
| `mutation-records-final.json` | 0.2 MB | 12 项中间突变记录，已被 `mutation-records-round3.json`（14 项）取代。 |

上述材料均保留在本地工作区，未被删除；如需在克隆仓库中复核中间版本，可按本文件与 findings.md 的说明从工作区恢复。
本批未改写的材料外既有内容（两份既有未提交设计文档、历史批次证据、`.bak`/`.stale`、日志、TestResults、DLL/工具输出与截图）
一律不纳入提交，也不因本批而删除。
