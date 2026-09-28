import pathlib
p=pathlib.Path("槲寄生调度器总计划.md"); s=p.read_text(encoding="utf-8")
old="- **验证**：定向 **111/111**（同条件基线 70/70）；助手全量 **1611 通过 / 2 跳过 / 0 失败 / 1613**（同条件开工基线 **1570/2/0/1572**；testId added=41／removed=0／changed=0）；**28 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；33 行状态/并发/故障矩阵（32 行 covered 各绑定有效突变，`REF-C4` 记为 not_applicable 并附尝试记录）；部署目标未留下可观察变化（最新写入时间早于本批）。\n- **会诊**：第 1 轮 `gpt-6-astra`／medium 判**未闭合 MUST 5 + IMPORTANT 4 + 建议 1**，已全部按原级修复并逐条补夹具与有效突变（子批计数 **1/8**，须复会诊）。报告见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/`，逐项处置见 R5.3 §24.129.7。"
new="- **验证**：定向 **119/119**（同条件基线 70/70）；助手全量 **1619 通过 / 2 跳过 / 0 失败 / 1621**（同条件开工基线 **1570/2/0/1572**；testId added=49／removed=0／changed=0）；**33 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；39 行状态/并发/故障矩阵（37 行 covered 各绑定有效突变，`REF-C4` 记为 not_applicable 并附尝试记录）；部署目标未留下可观察变化（最新写入时间早于本批）。\n- **会诊**：第 1 轮判 MUST 5 + IMPORTANT 4 + 建议 1；第 2 轮（验证轮）判已闭环 4 项、未闭环 5 项并新报 MUST 4 + IMPORTANT 4。两轮发现均按原级修复并逐条补夹具与突变（子批计数 **2/8**，继续复会诊）。报告与逐条处置见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与 R5.3 §24.129.7／§24.129.8。\n- **残项（如实）**：`REF-C4`（同实例并发）与「降级 manifest 标记不得绕过归属保护」两条仅有夹具级反例，**未取得可杀死夹具的单点突变**（尝试记录在案），不主张突变判别力。"
assert s.count(old)==1
p.write_text(s.replace(old,new,1),encoding="utf-8")
q=pathlib.Path("Docs/design/mistletoe-parallel-deliveries.md"); t=q.read_text(encoding="utf-8")
o2="- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 111/111、助手全量 1611/2/0/1613、28 项反向突变、33 行矩阵覆盖）；第 1 轮会诊 5 MUST + 4 IMPORTANT 已按原级修复并逐条补夹具/突变（子批计数 1/8，复会诊待跑）。**仍未验收**：真实 `User` 目录、真实入口与生产消费。"
n2="- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 119/119、助手全量 1619/2/0/1621、33 项反向突变、39 行矩阵覆盖）；两轮会诊（1/8、2/8）发现均按原级修复并逐条补夹具/突变，第 3 轮验证轮待跑。**仍未验收**：真实 `User` 目录、真实入口与生产消费；`REF-C4` 与降级绕过两条仅有夹具级反例（未取得击杀突变，如实登记）。"
assert t.count(o2)==1
q.write_text(t.replace(o2,n2,1),encoding="utf-8")
print("plan & index updated to round-2 state")
