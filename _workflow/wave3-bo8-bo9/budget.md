# 会诊预算（本批子批）

- 固定渠道：既有 GPT 会诊工具（`gpt_workspace`，read-only，自动附本批工作区差异）；固定模型/强度：`gpt-6-astra` / `medium`。
- 子批上限：**8 次累计请求**（覆盖所有渠道的首审、复审、验证与收口请求；已发出的失败/超时同样计次；
  本地预检拦截且未发出的不计；代码修复与回归不计次）。
- **本子批计数独立**：BO-6/BO-7 独立子批的 8/8 + owner 批准的 2/2 不重置、不继承、不被本批消耗。
- 计数：第 1 轮首审 1 次（1 IMPORTANT ＋ 1 建议级）；第 2 轮验证 1 次（IMPORTANT-1 closed，新增 IMPORTANT-2）；
  第 3 轮验证 1 次（IMPORTANT-2 closed，无新增 MUST/IMPORTANT，新增 1 项建议级 BO-9-D1）—— 累计 **3/8**。
  逐轮原始结论见 `consultation/review-outcome-v1.md`、`review-outcome-v2.md`、`review-outcome-v3.md`。
- 处置纪律：会诊给出的重要/必改不得由施工方降级；有未闭合项必须闭环或形成 owner 检查点；
  全称否定必须附反例尝试记录；"部分采纳"必须写明生效层次；新增关键断言须做反向突变。
- 送审材料：`workflow.py audit --stage review` + `verify` 通过后的本地 packet（含 objective/findings/budget、
  两个源码全文、清单与 R5.3 摘录、突变记录与工作区差异），并做渠道容量预估（`consultation/preflight-*.json`）；
  超限或同范围经允许重试仍无报告且输入达有效窗口三分之一时，按设施规则改用独立本地只读通道并保持模型/强度。
