import pathlib
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/owner-checkpoint.md")
s=p.read_text(encoding="utf-8")
s += """

## 工具核验的口径限制（如实登记）

`tools/mistletoe/workflow.py` 的 `review/closeout` 阶段要求：`review_round > 1` 时每个 MUST/IMPORTANT 前置发现必须记为 `disposition=candidate_fixed`
并提供已索引的修复证据。本批的第 1–3 轮发现中**存在未修复项**（见上表），因此**无法在不降级、不伪记修复的前提下**通过该字段生成 closeout 快照。
按设施接入计划的「工具或材料有问题，执行者自行修复或登记并完成同等人工核验」，本批**不**运行 closeout 快照，改以人工核验替代并在本文件与 R5.3 §24.129.9 登记：
证据清单（40+ 条，含两轮声明面再生差异、三轮会诊报告、33 项突变记录与 `summary.md`）、TRX 逐 testId 差集、矩阵 39 行绑定、以及 `review-v1/v2/v3` 三轮送审快照均已落盘于 `_workflow/r56-reference-activation-wiring-2026-09-29/`。
任务模式、固定模型/强度、会诊计数（3/8）、重要/必改不降级与生产门关闭均未受影响。
"""
p.write_text(s,encoding="utf-8")
print("checkpoint note appended")
