# `_workflow/wave3-bo6-bo7/` 主线收录范围说明（接收批 `wave3-bo6bo7-receive`，2026-09-28）

本目录在主线上只**部分收录**，目的是让 R5.3 §24.125／§24.126 的权威引用可在主线解析。
**它不是该子批证据的全集**，不得据此声称证据完整。

## 已收录（逐字节取自来源交付 HEAD `e009068e2`）

- `owner-checkpoint.md`（权威检查点，登记 SHA-256 A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1）
- `context.md`、`findings.md`、`budget.md`、`risk-matrix.json`
- `consultation/owner-approved-requests-9-10.md`、`consultation/current-ledger-reconciliation.json`
- `review-cli-a-bo6/run/review.md`、`review-cli-a-bo6/run-metadata.json`
- `review-cli-b-bo7/run/review.md`、`review-cli-b-bo7/run-metadata.json`
- `mutations/raw-mutation-evidence-index.md`、`mutations/mutation-summary.md`
- `regression/final/testid-comparison-final.json`

## 未收录（仅保留在来源 worktree，保留至接收验证完成）

- `manifest.json`（132 项证据索引）、`opening.json`、`pre-*`／`registration-*` 等开工与中间快照
- 6 份 closeout 快照（`closeout-owner-20260928-v6|v8|v9|v10|v11|v12|v13`）中的 `report.json`／`packet.md`／`index.md`
- 逐突变证据包：每个突变目录的 `baseline|mutant|restored` 构建日志、TRX、`experiment.json`、`mutation.patch`、`source-original.cs`
- `review-r1`…`review-r11`、`review-r9-audit*`、`raw-mutation-bundles/`、`regression/**`（除上方已收录的 testId 对照）、`deliveries/`、`consultation/` 内其余尝试与失败原始记录

因此本目录内引用到的上述未收录路径在主线上**不存在**；请以来源 worktree 或并行成果台账为准。

## 主线接收批自己的证据

接收批的构建、基线、集成回归、testId 对照、8 项反向突变重跑与声明面证据在
`_workflow/wave3-bo6bo7-receive/`；接收口径与剩余项见 R5.3 §24.126。
