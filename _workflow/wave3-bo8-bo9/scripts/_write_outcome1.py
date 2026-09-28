import io, os
base = r"_workflow/wave3-bo8-bo9"
outcome1 = """# 会诊结果与逐项处置 v1（本批子批，第 1 轮 = 首审）

- 渠道：既有 GPT 会诊工具（`gpt_workspace.gpt_review`，mode=review，read-only，`diff_included=true`）
- 模型/强度：`gpt-6-astra` / `medium`（一次成功返回；无失败、无超时）
- 尝试次数：`attempts=1`
- 计次：**1 / 8**（本批子批累计；BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承、不被本批消耗）
- 送审快照：`_workflow/wave3-bo8-bo9/review-v1`（audit review + verify 通过，packet 403,789 字节）
- 材料预检：`consultation/preflight-v1.json`（27 件、524,786 字节、最大单件 158,857 字节，均在附件限制内；
  预估输入约占假定有效窗口 54% ⇒ 若同范围原工具无报告，设施规则预授权本地只读 CLI 回退）

## 第 1 轮结论（原文要点）

1. **发现 1 — IMPORTANT**：`TryRelocateToLivePark` 只重入**停驻点**，可能丢掉 `ParkedRescue` 已选出的**同一轮前插出现**，
   最终错误收敛为成功。反例：计划 `[A,P]`（无循环）+ `A@0` 已完成 + `P@1/P@2` 停驻 ⇒ `RecomputeSuccessor` 取
   `candidate=P@0`、`rescue=A@1`（同轮前插未执行出现）；无循环时执行 `candidate` 后 `Next` 即到链尾，
   而重入只补停驻点 ⇒ `A@1`（及其后 `P@2` 同轮的 `A@2`）永不执行，停驻清偿后聚合检查发现不了该遗漏，
   允许 `Succeeded` 并执行收尾。修复方向：**保留尚未履行的 rescue 推进义务，或让链尾恢复机制重建该义务**
   （并指出"仅重新调用 `RecomputeSuccessor` 未必足够"，因为完成锚已变化、下界可能再次挡掉原 rescue）；
   要求补多节点、无循环、candidate/rescue 跨轮的**真实 Runner 测试及对应反向突变**。
2. **发现 2 — 建议级**：D1 目标注释确已修改且行为隔离成立，但仍有两处解释不准确——`ParkedRescue` 文档注释仍写
   "从链首逐轮推进到目标轮次"（实际是直接构造停驻所在轮次的链首），candidate/rescue 比较处仍写
   "线性推进自然到达 rescue"（被发现 1 的反例直接否定）。建议同步改正。
3. **逐项（原文要点）**：**(1) BO-8 可按原级 IMPORTANT 登记为 closed**（无条件过滤位于闸门/前置/提交之前，按稳定身份匹配；
   循环回绕、candidate、skip/reject/失败/unknown、`waitLocally` 逐项核对未发现"该执行而未执行"）；并指出需如实表述
   `HasCompletedOutcome` 是"非 `waitLocally`"谓词而非完成词白名单。**(2) BO-9 暂不可按原级 closed**（发现 1）；
   终止性、持久 `TailReached` 防御、重复提交与轮次等待四项专项中，除发现 1 外未发现新问题，但指出
   "每次重入必然驱动一次"表述过强（重入后仍会经过暂停/取消/修订边界），且本批夹具均无 scheduled loop，
   不能声称覆盖有循环时的全部高轮次等待组合。**(3) D1 行为隔离成立**，但 12 项突变不足以证明三项整体闭合
   （两项完成过滤突变共享同一 mutant；新夹具只有单节点；既有 candidate/rescue 夹具都有循环；行为突变不能证明注释正确）。
   **(4) 新的 MUST/IMPORTANT：有 1 项（发现 1）**，未发现其他新增 MUST 或安全漏洞。**(5) 结论：本批仍有未闭合的
   MUST/IMPORTANT＝是**，建议保留 BO-8 closed、BO-9 维持原级未闭合补齐后复审。
   另注：部署目标计数/时间戳一致只支持"读数未变"，不等于逐文件内容未写入的证明。
4. 审查者声明：结论仅来自所附源码、diff 与证据文本；未调用工具、未修改文件、未执行命令；不扩展到实机或生产验收。

## 逐项处置（本批）

| 发现 | 原级 | 处置 | 生效层次与证据 |
|---|---|---|---|
| 发现 1：链尾重入丢同轮前插 rescue ⇒ 假成功 | **IMPORTANT** | **已修复（原级闭环候选）** | 生效在**原问题发生的同一层**（同一次 `DriveAsync` 的链尾恢复点重建）：`TryRelocateToOutstandingObligation` 重建"仍存活停驻 + 每个停驻同轮序号更早的从未执行出现"，按计划全序重入。证据：新夹具 `Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail`（提交序列 `[(P,0),(A,1),(P,1),(A,2),(P,2)]`、`Succeeded`、收尾一次）+ 新突变 `bo9-tail-reentry-parks-only-v5`（baseline Passed／mutant Failed／restored Passed，命中 `WorkflowRunnerTests.cs:1411`）。 |
| 发现 2：D1 后两处注释仍不准确 | 建议级 | **采纳** | `ParkedRescue` 文档注释改述为"直接构造该停驻所在轮次的链首出现再向前探查"；R24 全序注释补"无循环时由链尾重建义务（BO-9）"，删除"自然到达"的绝对表述。 |
| 会诊附注：`HasCompletedOutcome` 口径 | 建议级（部分） | **采纳** | 源码注释与 R5.3 §24.127.1 如实写明其为"非 `waitLocally`"谓词，安全性依赖既有 Unknown 禁恢复守卫。 |
| 会诊附注：终止性表述过强 | 建议级（部分） | **采纳** | 代码注释与 R5.3 §24.127.1 改为"义务集合严格收缩"的收缩论证，不再写"每次重入必然驱动一次"。 |
| 会诊附注：夹具均无 scheduled loop | 观察项 | **如实登记** | R5.3 §24.127.5 与 findings 边界写明"有循环定义时 `LastScheduledRoundWait` 的全部交错组合未被本批夹具覆盖"。 |

## 边界
裁决只覆盖本批送审材料（BO-8、BO-9、BO-6/7-D1）；不审 R5.6／R6.1／R6 diff guard 与未消费并行成果；
BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；本批不声称实机或生产验收。
"""
io.open(os.path.join(base, "consultation", "review-outcome-v1.md"), "w", encoding="utf-8", newline="\r\n").write(outcome1)

consult = """- **第 1 轮（首审，本子批计 1/8）**：`gpt-6-astra` / `medium`，attempts=1，read-only，自动附本批 diff；
  送审快照 `review-v1`（packet 403,789 字节），材料预检 `consultation/preflight-v1.json`。结论：**1 项 IMPORTANT + 1 项建议级**。
  - **IMPORTANT-1（BO-9 链尾重入只补停驻点 ⇒ 丢掉同轮前插 rescue，运行假成功）**：**已修复**——链尾改为重建
    "仍存活停驻 + 每个停驻同轮序号更早的从未执行出现"的未履行恢复义务并按计划全序重入
    （`TryRelocateToOutstandingObligation`）。生效层次＝原问题发生的同一层（同一次 `DriveAsync` 的链尾恢复点）。
    证据：新夹具 `Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail`
    （提交 `[(P,0),(A,1),(P,1),(A,2),(P,2)]`、`Succeeded`、收尾一次）＋新突变 `bo9-tail-reentry-parks-only-v5`
    （P/F/P，命中 `WorkflowRunnerTests.cs:1411`）。
  - **建议级-1（D1 后两处注释解释仍不准确）**：**采纳并订正**（`ParkedRescue` 探测口径、R24 全序说明）。
  - 同轮另两条建议（`HasCompletedOutcome` 口径表述、终止性表述过强）**采纳**；一条观察（夹具均无 scheduled loop）
    **如实登记**为未覆盖边界（§24.127.5）。
  - 该轮对 BO-8 的结论为**可按原级 closed**；对 BO-9 为**暂不可 closed**（因 IMPORTANT-1），据此本批保留原级阻断并修复。
- **第 2 轮（验证轮，本子批计 2/8）**：__ROUND2__
- 计数：**2 / 8**（本子批独立计数；BO-6/BO-7 子批的 8/8 + owner 批准 2/2 不重置、不继承、不被消耗）。
  逐轮原始结论见 `consultation/review-outcome-v1.md` 与 `review-outcome-v2.md`；请求文本见 `review-request-v1.md`／`review-request-v2.md`。"""
p = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = io.open(p, encoding="utf-8").read()
assert t.count("__CONSULTATION__") == 1
io.open(p, "w", encoding="utf-8", newline="\n").write(t.replace("__CONSULTATION__", consult))
print("round-1 record written into R5.3 24.127.4 (round 2 placeholder kept in-file pending dispatch)")
