import pathlib
p=pathlib.Path("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"); s=p.read_text(encoding="utf-8")
assert "§24.129.9" not in s
sec = """

### §24.129.9 会诊（第 3 轮，验证轮）结论与 owner 检查点（子批计数 3/8）——**本子批未收口**

- **渠道/模型/强度**：既有 GPT 只读会诊工具；`gpt-6-astra`／medium；attempts=1（预检见 `consultation/preflight-v3.json`，白名单 10 文件 + 自动附加 diff；包体 492,451 字节）。
- **结论**：第 2 轮 8 项中 **已闭环 2 项**（R2-MUST-2 代码层、R2-IMPORTANT-5）、**仍未闭环 6 项**，并新报 **4 项 MUST + 3 项 IMPORTANT**。报告原文见 `consultation/review-round3-report.md`。
- **未闭环要点（保持原级，不降级、不伪记修复）**：
  1. **MUST** 新增激活目标回滚**中断后不可恢复**（归属证据仅在内存；撤销后的合法字节版本无持久化证据 ⇒ 二次恢复持续阻断）。
  2. **MUST** 归属删除只查**路径成员资格**、不查当前字节（预检放行的「原本不存在」路径被授予删除权 ⇒ 预检后出现的他方文件会被删除并报告完整回滚）。
  3. **MUST** 激活端口绑定的是**调用方提供的哈希**，未强制等于已确认写集哈希（可构造漂移内容被激活并登记为合法证据）。
  4. **MUST** 提交仍接受**写集外基线文件的字节漂移**（缺失分支已拦截、修改分支未拦截）。
  5. **IMPORTANT** 重入守卫未覆盖语义读回回调，`Dispose` 亦无守卫（回调内重入可改变阶段后被外层覆盖发布）。
  6. **IMPORTANT** 回滚两处后续状态读取仍可能外泄异常（非三类异常未收敛）。
  7. **IMPORTANT** 取消夹具仍**零写入**即报告完成；台账计数不一致（17／28／33）；两处突变映射失真。
- **本批已达成且可复核**：隔离配置根内的真实引用写入与 `candidate-ready → active` 激活已接线（写集声明 → 真实副作用 → 语义+字节读回 → **才**推进阶段）；定向 **119/119**、助手全量 **1619/2/0/1621**（同条件开工基线 70/70 与 1570/2/0/1572；testId added=49／removed=0／changed=0）；**33 项反向突变**全绿且源码逐字节恢复；39 行矩阵（37 行 covered）。部署目标未留下可观察变化。
- **owner 检查点**：`_workflow/r56-reference-activation-wiring-2026-09-29/owner-checkpoint.md`（逐项未决、原级、证据、风险、已尝试处理、建议的有限范围与验收条件）。
- **门禁**：本子批**未收口**；B–F 启用前置、真实 `User` 切换、生产构造、E3/E4/E5、热键、R5.8 签署与实机门**继续关闭**；生产引用写方归属仍为 owner 检查点。会诊预算已用 **3/8**，剩余 5 次。""".rstrip()+"\n"
p.write_text(s.rstrip("\n")+sec, encoding="utf-8")
print("§24.129.9 appended")

q=pathlib.Path("槲寄生调度器总计划.md"); t=q.read_text(encoding="utf-8")
old="- **会诊**：第 1 轮判 MUST 5 + IMPORTANT 4 + 建议 1；第 2 轮（验证轮）判已闭环 4 项、未闭环 5 项并新报 MUST 4 + IMPORTANT 4。两轮发现均按原级修复并逐条补夹具与突变（子批计数 **2/8**，继续复会诊）。报告与逐条处置见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与 R5.3 §24.129.7／§24.129.8。"
new2="- **会诊**：第 1 轮 MUST 5 + IMPORTANT 4 + 建议 1；第 2 轮（验证轮）判已闭环 4／未闭环 5 并新报 MUST 4 + IMPORTANT 4；第 3 轮（验证轮）判已闭环 2／未闭环 6 并新报 MUST 4 + IMPORTANT 3（子批计数 **3/8**）。\n- **状态（如实）**：本子批**未收口**——第 3 轮仍有 **4 MUST + 3 IMPORTANT 未闭环**（回滚中断后的归属证据、删除授权绑定版本、激活哈希由事务主导、提交面基线字节复核、读回/Dispose 重入、回滚异常边界、取消夹具与台账），已按原级登记 owner 检查点 `_workflow/r56-reference-activation-wiring-2026-09-29/owner-checkpoint.md`，**不登记完成、不开生产门**。"
assert t.count(old)==1
q.write_text(t.replace(old,new2,1),encoding="utf-8")

r=pathlib.Path("Docs/design/mistletoe-parallel-deliveries.md"); u=r.read_text(encoding="utf-8")
o3="- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 119/119、助手全量 1619/2/0/1621、33 项反向突变、39 行矩阵覆盖）；两轮会诊（1/8、2/8）发现均按原级修复并逐条补夹具/突变，第 3 轮验证轮待跑。**仍未验收**：真实 `User` 目录、真实入口与生产消费；`REF-C4` 与降级绕过两条仅有夹具级反例（未取得击杀突变，如实登记）。"
n3="- **A（真实引用更新 + candidate→active 激活）**：主线子批 `r56-reference-activation-wiring-2026-09-29` 已在**隔离配置根**内接线（定向 119/119、助手全量 1619/2/0/1621、33 项反向突变、39 行矩阵）。**本子批未收口**：第 3 轮会诊（3/8）仍有 **4 MUST + 3 IMPORTANT 未闭环**，已交付 owner 检查点（同目录 `owner-checkpoint.md`）。**仍未验收**：真实 `User` 目录、真实入口与生产消费；`REF-C4` 与降级绕过两条仅有夹具级反例（未取得击杀突变）。"
assert u.count(o3)==1
r.write_text(u.replace(o3,n3,1),encoding="utf-8")
print("plan & index updated to checkpoint state")
