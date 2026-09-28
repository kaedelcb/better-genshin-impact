# 会诊预算

本独立 Wave3 BO-6/7 子批预算上限为 8 次已发送 GPT 会诊请求；失败、超时或本地回收失败的已发送请求均计次，施工与回归不计。固定模型/强度：gpt-6-astra / medium。

| 请求 | 时间 / 模型 | 状态 | 计数 |
|---|---|---|---:|
| 1 | 2026-09-28T05:09:44.8705047Z；gpt-6-astra / medium | 已发送并返回，但本地回收脚本因 TextEncoder is not defined 在写入前异常；报告正文未能取回，不能据此声称任何等级发现已处置。 | 1/8 |
| 2 | 2026-09-28T05:13:22Z；gpt-6-astra / medium | 已发送并返回，但第二次保存脚本因 JavaScript 运行时缺少 btoa 在写入前异常；报告正文未能取回，不能据此声称任何等级发现已处置。 | 2/8 |
| 3 | 2026-09-28T05:14Z；gpt-6-astra / medium | 已发送并成功保存 `_workflow/wave3-bo6-bo7/consultation/gpt-r3-review.md`；发现 1 MUST 与发现 2 IMPORTANT 未闭合并阻断；BO-7 F1 仅获报告支持，须复核原始突变日志后再定。 | 3/8 |
| 4 | 2026-09-28；gpt-6-astra / medium | 已发送并成功保存 `_workflow/wave3-bo6-bo7/consultation/gpt-r4-review.md`。全部原级阻断保持开放；要求补齐七项 v3 原始 TRX、构建/测试日志、真实 mutation diff 与 experiment 正文，并在 TailReached Runner 测试返回后重新加载验证落盘 Failed 状态。另要求解释两个保留 testId 的断言语义变化、定向筛选 92→88、C1 措辞反转及完整计划文档 diff。 | 4/8 |
| 5 | 2026-09-28；gpt-6-astra / medium | 第 5 次工具请求（BO-6 R19/F4 范围）；工具未返回报告，原始错误见 gpt-r5-failure.json；工具报告还启动 1 次内部重试。 | 5/8 |
| 6 | 2026-09-28；gpt-6-astra / medium | 同一工具调用的第 2 次服务尝试；错误为 GPT consultation failed for gpt-6-astra (Codex exit code 1). Start a new task and retry. Attempts: 2.，无审查报告。 | 6/8 |
| 7 | 2026-09-28T08:15:18Z；gpt-6-astra / medium | BO-6 R19/F4 实质审查已发送；工具报 Codex exit code 1、Attempts: 2，无报告。按已发送失败计次。 | 7/8 |
| 8 | 2026-09-28T08:15:18Z；gpt-6-astra / medium | 同一调用报告的第 2 次服务尝试；没有独立报告。按已发送失败计次。 | 8/8 |

Requests 7–8 were sent as one BO-6 scope call and its reported retry. The exact 24-file packet fit existing GPT tool limits; the existing GPT tool returned an error, not an oversize rejection, and no report was recovered. Both failures count. The channel selection and raw error are in `consultation/channel-selection-request7.md` and `consultation/gpt-request7-8-failure.json`. Budget is exhausted at 8/8; all original-grade findings remain open and require an owner checkpoint. No CLI request was sent.

## 当前计数与结论

当前子批按保守口径为 **8/8 已计，余额 0**。计数表的 1–4 是四次各自发送的 GPT 工具调用；工具调用 5 与 6 各在错误返回中报告 Attempts: 2，分别保守计作请求 5–6、7–8。故应并列记录：**GPT 工具调用 6 次；工具报告的服务尝试 8 次**。原始返回没有逐个底层重试的传输/接收遥测，不能独立证明每个 retry 是否跨过服务 dispatch；这个不确定性不把预算改回未耗尽。

两次无报告调用均为 BO-6 R19 MUST／R21 F4 IMPORTANT 范围，返回 Codex exit code 1、Attempts: 2，没有审查报告、没有等级处置；原始错误见 consultation/gpt-r5-failure.json 与 consultation/gpt-request7-8-failure.json。第 1、2 次也没有可审计报告：工具返回正文后，分别因本地保存环境缺少 TextEncoder、btoa 而未回收。未回收不等于无发现或通过。

所有必改/重要义务仍按原级开放：BO-6 R19 MUST、BO-6 R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST。累计额度耗尽，须 owner 明确批准固定追加次数和范围后才能复会诊；本台账不授权追加会诊。生产门保持关闭。

历史 review-r7 / review-r8 本地 allowlist 预检超单文件上限且未发送，不计次；它们不代表 request 7/8 未发送。request 7/8 已于 2026-09-28T08:15:18Z 发出，当前 task-relay 的过期快照曾错误显示 6/8、request 7 未发，现已登记对账；详见 consultation/current-ledger-reconciliation.json。request 7 的最终包 24 文件、1,728,816 bytes，最大文件 228,706 bytes，低于已知附件上限；工具错误不是超限证明。无本地 CLI 会诊已发送。

## Owner 批准追加的 2 次会诊（2026-09-28）

owner 于 2026-09-28 依据本批 `owner-checkpoint.md` 明确批准**额外最多 2 次**独立会诊；原 8/8 保留、不重置、不滚动追加。两次均**优先并实际使用独立本地 GPT/Codex CLI 只读通道**（`codex exec`，默认运行时 / aiotto provider），固定 `gpt-6-astra` / `medium`，未换模型、未降强度、未改回原工具。

| 请求 | 范围 | 通道 / 模型 | 状态 | 计数 |
|---|---|---|---|---|
| 9 | 仅 BO-6 R19 MUST、R21 F4 IMPORTANT | 本地 CLI `codex exec` / gpt-6-astra / medium / read-only | exit 0，182.8 秒，报告 `review-cli-a-bo6/run/review.md`（thread `01a0e788-ec95-7803-b183-97b0fbd6898b`）；BO-6 R19 **closed**、R21 F4 **closed**，无新增 MUST/IMPORTANT，含 2 项建议 | 追加 1/2 |
| 10 | 仅 BO-7 R21 F1/F2 MUST | 本地 CLI `codex exec` / gpt-6-astra / medium / read-only | exit 0，183.0 秒，报告 `review-cli-b-bo7/run/review.md`（thread `01a0e78c-3293-7b93-90f9-bd32eb461ed5`）；F1 **closed**、F2 **closed**，无新增 MUST/IMPORTANT，含 1 项建议 | 追加 2/2 |

- 追加额度 **2/2 用尽，余额 0**；原 8/8 计数不变。没有发生失败或超时（若发生亦从这 2 次扣除）。
- 两次运行的原始事件流、退出码、耗时、用量与报告哈希见各自 `review-cli-*/run-metadata.json`；逐项处置见 `consultation/owner-approved-requests-9-10.md`。
- 模型与强度由本地 rollout 记录的 model slug `gpt-6-astra` 与 `reasoning_effort=medium` 证实；只读沙箱 `-s read-only`。
- 通道预检（未派发模型请求）：`consultation/owner-approved-cli-preflight.json`。
