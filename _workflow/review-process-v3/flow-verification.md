# 流程链路核验与外部阻塞台账（2026-09-29）

## 已核验通（证据）

- 版本一致性：`verify-bundle` 通过，`bundle=ca2ec525dd73d5a1be3124a8241485d86e38c575eb4d7f37ac3a0fea84ae7dea`。
- 最终版本回归：126 项 unittest 全绿，耗时约 76 秒；早前由 `execution_evidence` 捕获的实际执行来源见
  `executions/12ad7f2073a146fa80f4db51214934fc/`，其中真实 Windows Job 进程身份与 `run-tree-terminal.json` active=0 齐备。
- 独立综合审查的运行链路：R8 使用新增 `process_runner.run` 真实调用本地 Codex CLI（`-s read-only`），已保存
  `final-review8/review-process-request.json`、`review-process-identity.json`、`review-process-result.json`、
  `review-tree-terminal.json`（active=0）与 325KB `events.jsonl`。请求、快照、独立 MCP 读取和进程树清理链路完整跑通。
- 逐项机制已有定向测试：实施前审许可、合同身份、manifest/Audit 绑定、并发锁、取消清理、执行来源、B/M/B 条件、历史/当前绿灯、每请求模型判断及反向突变，均覆盖在当前套件。

## 未完成项（如实阻断）

- 第 8 次也是最后一次允许的综合独立复核：已按第 8 次发出（Astra/medium，基于进程/来源/门禁高风险，不按轮次）。
  终端返回 `usage limit`，`exit_code=1`、无 `report.json`，规则计为 8/8。请求本身已证明运行链路；**不能由测试、传输夹具或主执行者自审代替综合模型报告**。
- R7 六项原级 MUST/IMPORTANT 的候选修复仍待最终独立确认，尚未宣称闭合；流程尚未部署主线，暂停的 R5.6 未接入。

## 额度恢复后

直接使用冻结材料 `_workflow/review-process-v3/final_review8.py`（一次、有界重试）。额度仍不足则如实外部阻塞；
只有原级综合报告 pass、无重要未决项后，才继续主线安全集成与 R5.6 接入。期间用户无需逐项操作，也不要重开/改本批 Goal。
