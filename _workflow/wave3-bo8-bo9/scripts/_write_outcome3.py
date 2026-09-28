import io, os
base = r"_workflow/wave3-bo8-bo9"
outcome3 = """# 会诊结果与逐项处置 v3（本批子批，第 3 轮 = 验证轮）

- 渠道：既有 GPT 会诊工具（`gpt_workspace.gpt_review`，mode=review，read-only，`diff_included=true`）
- 模型/强度：`gpt-6-astra` / `medium`；尝试次数 `attempts=1`（一次成功返回，无失败、无超时）
- 计次：**3 / 8**（本批子批累计；BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承）
- 送审快照：`_workflow/wave3-bo8-bo9/review-v3`（audit review + verify 通过，packet 449,331 字节）
- 材料预检：`consultation/preflight-v3.json`（27 件、502,436 字节、最大单件 162,147 字节，均在附件限制内）
- 范围：严格限定为"第 2 轮 IMPORTANT-2 是否按原级闭合 + 是否新增 MUST/IMPORTANT（并确认 IMPORTANT-1 未回退）"

## 第 3 轮结论（原文要点）

1. **IMPORTANT-2 已按原级闭合**：最终源码与 diff 显示 `TryLocate` 成功后先执行
   `if (HasCompletedOutcome(run, parkOcc)) continue;` 再进入 `Consider` 与前插探针，`continue` 跳过整个历史标记处理，
   同时阻断"旧标记作重入点"与"旧标记生成前插义务"；完成判据用 `(NodeId, Occurrence, LoopIteration)`（不含可变 `SequenceIndex`）。
   推演原反例成立（恢复点 T；T 完成后旧标记被整体排除，X 不被引出；重复旧标记同样排除；其他轮次存活停驻不被误清偿）。
   新夹具确为真实 `ResumeAsync` 调用并断言 `["T"]`、X 无结果、成功、收尾一次与持久结果；`bo9-settled-park-probe-v5`
   三阶段 P/F/P，突变实际提交由 `["T"]` 变为 `["T","X"]`，命中 `:1472`；判别力直接对应原问题，无需降低原级。
2. **新增 MUST/IMPORTANT：未发现；原 IMPORTANT-1 未回退**：同轮前插重建循环、计划全序选择、推进段稳定身份完成过滤与
   `!enteredAtTail` 守卫均保留；新增守卫只剔除已清偿标记（原反例的 P@1/P@2 尚未完成，仍产生 A@1/A@2 义务，
   对应夹具仍断言完整序列 `[(P,0),(A,1),(P,1),(A,2),(P,2)]`）。D1 订正仅涉及注释，未改动比较、下界或返回逻辑。
3. **建议级观察（不构成 MUST/IMPORTANT）**：仍有旧方法名 `TryRelocateToLivePark`（3 处注释引用）与 `return candidate`
   行尾"线性推进自然到达 rescue"的残留措辞未同步。
4. 回归材料相互一致：定向 98/98；全量 1567 passed／2 skipped／0 failed；对照 1564 unchanged／5 added／无 removed/changed；
   14 项突变均为 P/F/P。**建议将 IMPORTANT-2 登记为原级 closed，并据本轮限定范围将 BO-9 登记为原级闭合。**
5. **收尾原文**："**本批是否仍有未闭合的 MUST/IMPORTANT：否。**"
6. 审查者声明：结论仅基于所附源码、diff 与证据文本；未调用工具、未修改文件、未执行命令、未独立计算哈希；
   部署材料只证明文件计数与目录时间戳读数一致；不扩展至实机、真实 User 或生产验收。

## 逐项处置（本批）

| 发现 | 原级 | 处置 | 依据 |
|---|---|---|---|
| R2-IMPORTANT-2（已清偿标记仍生成前插义务 ⇒ 额外提交） | IMPORTANT | **已按原级闭合**（第 3 轮确认） | 守卫 `if (HasCompletedOutcome(run, parkOcc)) continue;` ＋新夹具 ＋突变 `bo9-settled-park-probe-v5`（P/F/P，命中 `:1472`）。 |
| R1-IMPORTANT-1（链尾丢同轮前插 rescue） | IMPORTANT | **已按原级闭合**（第 2 轮确认，第 3 轮复核未回退） | 第 3 轮逐项核对前插重建、全序选择、完成过滤与入口守卫均保留。 |
| R3-建议级-1（旧方法名与 R24 行尾措辞残留：`TryRelocateToLivePark` 3 处、`return candidate` 行尾"自然到达"1 处） | 建议级 | **登记 `BO-9-D1`，本批不改源码** | 见下。 |

### BO-9-D1（建议级，注释陈旧；本批不改源码）

- **事实**：`WorkflowRunner.cs` 中 3 处注释仍引用旧辅助方法名 `TryRelocateToLivePark`（`:597`、`:1393`、`:1430`），
  `return candidate;` 行尾（`:1495`）仍写"线性推进自然到达 rescue"；第 3 轮审查者明确评为**建议级**且明确
  "不构成本轮新的 MUST/IMPORTANT"。
- **本批不改的理由（三条可复核事实）**：①第 3 轮裁决与全部证据绑定**当时的源码字节**（`b0b3579b…`），
  改动源码会作废该裁决绑定、manifest 源哈希与 14 项突变记录，需重跑整套构建/回归/突变并重取同等级复审；
  ②两处均为**注释**，不改变任何行为路径（推进段完成过滤、链尾义务重建、`enteredAtTail` 守卫、全序选择与下界逻辑均未动），
  审查者也据此未升级等级；③与上一批 `BO-6/7-D1` 同类（同一族"注释陈旧"残项），既定处置口径是
  "在下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正，并按该批质量门验证"。
- **修复条件**：下一次因功能/缺陷改动而重新绑定 `WorkflowRunner.cs` 哈希的批次，或 owner 指定的小批，一并订正上述 4 处，
  并按该批要求重跑反向突变与适用回归；owner 亦可选择仅接受为文档说明。**本批不据此宣布任何行为或生产结论。**
- **边界**：该残项不影响 BO-8／BO-9／BO-6-D1 的原级闭合；不改变声明面（未改状态词、门禁词或证据等级）。

## 边界
裁决只覆盖本批送审材料（BO-8、BO-9、BO-6/7-D1）；外部执行边界为 fake；BGI 生产门、真实 User、R5.8、E3/E4/E5、热键面与实机门继续关闭。
"""
io.open(os.path.join(base, "consultation", "review-outcome-v3.md"), "w", encoding="utf-8", newline="\r\n").write(outcome3)

# R5.3: fill round-3 record + register BO-9-D1
p = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = io.open(p, encoding="utf-8").read()
old = "- **第 3 轮（验证轮，本子批计 3/8）**：__ROUND3__"
new = """- **第 3 轮（验证轮，本子批计 3/8）**：裁定**第 2 轮 IMPORTANT-2 已按原级闭合**（守卫位置与生效层次逐行核对；
  `bo9-settled-park-probe-v5` 的 P/F/P 与 `:1472` 命中直接对应原问题），**原 IMPORTANT-1 的闭合结论仍成立**
  （前插重建、计划全序、推进段完成过滤与 `!enteredAtTail` 守卫均保留），**未发现新的 MUST/IMPORTANT**；
  收尾原文为「本批是否仍有未闭合的 MUST/IMPORTANT：否」。逐项原文见 `consultation/review-outcome-v3.md`。
- **新增建议级残项 `BO-9-D1`（注释陈旧，本批不改源码）**：第 3 轮观察指出 `WorkflowRunner.cs` 仍有 3 处旧辅助方法名
  `TryRelocateToLivePark`（`:597`、`:1393`、`:1430`）与 `return candidate;` 行尾（`:1495`）"线性推进自然到达 rescue"残留；
  审查者明确评为建议级且"不构成本轮新的 MUST/IMPORTANT"。处置：与 `BO-6/7-D1` 同族口径，
  **留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并订正并按该批质量门验证**（owner 亦可选择指定小批或仅接受为文档说明）。
  三条不改理由：①第 3 轮裁决与全部证据绑定当时源码字节，改动需重跑整套回归/突变并重取同等级复审；
  ②均为注释，不改变任何行为路径（完成过滤、链尾义务重建、入口守卫、全序选择与下界逻辑均未动）；
  ③与既有 `BO-6/7-D1` 同类残项的口径一致。该残项不影响 BO-8／BO-9／BO-6-D1 的原级闭合，也不改变声明面。"""
assert t.count(old) == 1
t = t.replace(old, new)
old2 = "生产入口门、真实 User 门、R5.8 签署与生产进程门继续关闭。停驻语义生产可达性结论未被本批改变"
new2 = ("生产入口门、真实 User 门、R5.8 签署与生产进程门继续关闭。**遗留建议级残项 `BO-9-D1`**（4 处注释陈旧，见 §24.127.4）"
        "随本批登记，不阻断本批原级闭合，其修正条件为下一次重新绑定 `WorkflowRunner.cs` 哈希的批次。"
        "停驻语义生产可达性结论未被本批改变")
assert t.count(old2) == 1
t = t.replace(old2, new2)
io.open(p, "w", encoding="utf-8", newline="\n").write(t)
print("round-3 record + BO-9-D1 registered in R5.3")

# findings + budget
f = base + "/findings.md"
t = io.open(f, encoding="utf-8").read()
old3 = "- 第 3 轮（验证轮，计 3/8）：逐项结论见 `consultation/review-outcome-v3.md`，计数以该文件记录为准。"
new3 = ("- 第 3 轮（验证轮，计 3/8）：裁定 IMPORTANT-2 **已按原级闭合**、原 IMPORTANT-1 未回退、**未发现新的 MUST/IMPORTANT**"
        "（收尾原文「本批是否仍有未闭合的 MUST/IMPORTANT：否」）。新提出的一条**建议级**观察（3 处旧方法名 "
        "`TryRelocateToLivePark` 与 `:1495` 行尾残留措辞）按与 `BO-6/7-D1` 同族口径登记为 **`BO-9-D1`**，"
        "本批不改源码（裁决与全部证据绑定当时字节），留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次订正。"
        "逐项见 `consultation/review-outcome-v3.md`。")
assert t.count(old3) == 1
t = t.replace(old3, new3)
old4 = "- 边界：会诊结论只覆盖本批材料；不代表实机、生产或真实 User 验收。"
new4 = ("- 边界：会诊结论只覆盖本批材料；不代表实机、生产或真实 User 验收。\n"
        "- 遗留建议级残项 **`BO-9-D1`**：`WorkflowRunner.cs` 4 处注释陈旧（旧方法名 ×3、`return candidate` 行尾措辞 ×1），"
        "第 3 轮明确评为建议级；修正条件为下一次重新绑定该文件哈希的批次（与 `BO-6/7-D1` 同族口径）。")
assert t.count(old4) == 1
t = t.replace(old4, new4)
io.open(f, "w", encoding="utf-8", newline="\r\n").write(t)

b = base + "/budget.md"
t = io.open(b, encoding="utf-8").read()
old5 = ("- 计数：第 1 轮首审 1 次（1 IMPORTANT ＋ 1 建议级，一次成功返回）；第 2 轮验证 1 次 —— 累计 2/8。\n"
        "  逐轮原始结论见 `consultation/review-outcome-v1.md`、`review-outcome-v2.md`。")
new5 = ("- 计数：第 1 轮首审 1 次（1 IMPORTANT ＋ 1 建议级）；第 2 轮验证 1 次（IMPORTANT-1 closed，新增 IMPORTANT-2）；\n"
        "  第 3 轮验证 1 次（IMPORTANT-2 closed，无新增 MUST/IMPORTANT，新增 1 项建议级 BO-9-D1）—— 累计 **3/8**。\n"
        "  逐轮原始结论见 `consultation/review-outcome-v1.md`、`review-outcome-v2.md`、`review-outcome-v3.md`。")
assert t.count(old5) == 1
io.open(b, "w", encoding="utf-8", newline="\r\n").write(t.replace(old5, new5))
print("findings/budget updated")
