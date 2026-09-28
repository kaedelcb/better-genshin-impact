import pathlib
# 总计划：更新本批条目（数字与第 1 轮会诊）
p = pathlib.Path("槲寄生调度器总计划.md")
s = p.read_text(encoding="utf-8")
old = "- **验证**：定向 **99/99**（同条件基线 70/70）；助手全量 **1599 通过 / 2 跳过 / 0 失败 / 1601**（同条件开工基线 **1570/2/0/1572**；testId added=29／removed=0／changed=0）；**17 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；23 行状态/并发/故障矩阵全部 covered 并各绑定有效突变；部署目标未留下可观察变化（最新写入时间早于本批）。"
new = "- **验证**：定向 **111/111**（同条件基线 70/70）；助手全量 **1611 通过 / 2 跳过 / 0 失败 / 1613**（同条件开工基线 **1570/2/0/1572**；testId added=41／removed=0／changed=0）；**28 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；33 行状态/并发/故障矩阵（32 行 covered 各绑定有效突变，`REF-C4` 记为 not_applicable 并附尝试记录）；部署目标未留下可观察变化（最新写入时间早于本批）。\n- **会诊**：第 1 轮 `gpt-6-astra`／medium 判**未闭合 MUST 5 + IMPORTANT 4 + 建议 1**，已全部按原级修复并逐条补夹具与有效突变（子批计数 **1/8**，须复会诊）。报告见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/`，逐项处置见 R5.3 §24.129.7。"
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")

q = pathlib.Path("Docs/design/mistletoe-parallel-deliveries.md")
t = q.read_text(encoding="utf-8")
old2 = "- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 99/99、助手全量 1599/2/0/1601、17 项反向突变、23 行矩阵覆盖）。**仍未验收**：真实 `User` 目录、真实入口与生产消费。"
new2 = "- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 111/111、助手全量 1611/2/0/1613、28 项反向突变、33 行矩阵覆盖）；第 1 轮会诊 5 MUST + 4 IMPORTANT 已按原级修复并逐条补夹具/突变（子批计数 1/8，复会诊待跑）。**仍未验收**：真实 `User` 目录、真实入口与生产消费。"
assert t.count(old2) == 1
t = t.replace(old2, new2, 1)
q.write_text(t, encoding="utf-8")
print("plan & index updated")
