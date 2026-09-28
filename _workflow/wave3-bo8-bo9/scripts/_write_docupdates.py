import io, json, os

def read(p):
    return io.open(p, encoding="utf-8").read()

def write(p, t):
    io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
    print("wrote", p, len(t))

# ---------- 1) R5.3 ----------
r53 = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = read(r53)

old_tail = """- **BO-6/7-D1（建议级，注释陈旧）**：状态不变。两位来源审查者独立指出 `WorkflowRunner.cs` 中两处注释与实现不一致（`:1486`、`:1535–1537`），均评为建议级且明确未据此发现运行正确性缺陷。本批不为它单独改写已审源码；按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正，并按该批质量门验证。
- **BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT** 仍未并入、仍未闭合；R5.6、R6.1、R6 diff guard 未提前集成。"""
new_tail = """- **BO-6/7-D1（建议级，注释陈旧）**：状态不变。两位来源审查者独立指出 `WorkflowRunner.cs` 中两处注释与实现不一致（`:1486`、`:1535–1537`），均评为建议级且明确未据此发现运行正确性缺陷。本批不为它单独改写已审源码；按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正，并按该批质量门验证。
  〔2026-09-28 更新：BO-6/7-D1 已由 `wave3-bo8-bo9-2026-09-28` 子批在同一份 `WorkflowRunner.cs` 哈希重绑定内按登记口径修正（含本节扩大后的 `:1400–1403`），并按该批质量门重跑受影响反向突变与回归；见 §24.127。〕
- **BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT** 仍未并入、仍未闭合；R5.6、R6.1、R6 diff guard 未提前集成。
  〔2026-09-28 更新：BO-8 R29 与 BO-9 R34 F5 已由 `wave3-bo8-bo9-2026-09-28` 子批在主线实现并按原级登记为 closed；
  本节所述的"未并入未闭合"只描述本接收批当时状态，不再代表当前主线状态。见 §24.127。〕"""
assert t.count(old_tail) == 1
t = t.replace(old_tail, new_tail)

section = """

## §24.127 Wave3 BO-8／BO-9 主线施工子批与 BO-6/7-D1 修正（2026-09-28）

### §24.127.1 范围、原级与实现决策

本子批在茶包主线只施工两项已登记未闭合 **IMPORTANT**：**BO-8 R29**（恢复点之后的推进段无完成过滤）与
**BO-9 R34 F5**（多有效停驻跨轮次推进），并在同一份 `WorkflowRunner.cs` 哈希重绑定内修正已登记的
**BO-6/7-D1**（建议级注释陈旧，含 §24.126.5 扩大后的 `:1400–1403`）。不重开 BO-6 R19／R21 F4、BO-7 R21 F1/F2、
BO-13、SB21-3 BO-4、SB21-2 BO-10/12；不施工 R5.6、R6.1、R6 diff guard；不提前集成任何其他并行成果。
BO-8 与 BO-9 同属 `DriveAsync` 推进层，按用户指令在同一子批内按依赖顺序收敛，未拆成互相绕过的子批。
开工 HEAD `f47b57b1cba90e78624ae6b6d2236aafd402f0e1`（分支 `main-OldTeaBag-B168`）；待审源码开工工作区哈希
`WorkflowRunner.cs` = `5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`、
`WorkflowRunnerTests.cs` = `b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711`。

实现决策（均为最小改动，**不新增参数或开关**）：

① **BO-8**——推进段完成过滤**无条件生效**（删除 BO-6 引入的 `filterCompletedDuringParkRecovery` 开关），判据仍为稳定出现身份
`(NodeId, Occurrence, LoopIteration)`（`HasCompletedOutcome`）；`waitLocally` 不算完成 ⇒ 停驻义务照常重驱，
BO-6 在该层的既有语义不变。
② **BO-9**——真实链尾（`occurrence is null`）若仍存**存活停驻**（可在当前修订定位且无完成结果），按计划全序
`(LoopIteration, SequenceIndex)` **最早**重入驱动（新私有方法 `TryRelocateToLivePark`），不再按链尾放行；两条守卫：
"入口即链尾（持久 `TailReached`，`enteredAtTail`）不由本路径重开"（保留 BO-6 防御语义）与"每次重入必然驱动该出现一次
（完成/被过滤 ⇒ 离开存活集；再次停驻 ⇒ 立即返回 `LocalWaitParking`），故严格消耗义务、可终止"。
③ **BO-6/7-D1**——按登记口径逐处修正注释：`:1400–1403` 下界口径改写为"**计划全序最早**"（R34 F5 已统一口径）并说明
"推进段穿越已完成出现"的历史理由已由稳定身份过滤消除；`:1486` 基准口径改写为"计划全序最早的可定位停驻标记"；
`:1535–1537` 删除已失效的"DriveAsync/Relocate 无完成跳过"理由；另同步 `RecomputeSuccessor` 两条不变量、
`ParkedRescue` 返回语义与 `DriveAsync` 的 BO-6 作用域注释。**不改行为、不改断言语义**，但改变了 `WorkflowRunner.cs`
字节 ⇒ 上一批反向突变绑定失效，本批在新字节上逐项重做（§24.127.3）。

### §24.127.2 真实 Runner 反例（反例先行：先红后实现）

- **BO-8 反例①（纯完成重排，无停驻，生产可达）**：旧计划 `[n3,n2,Y]` 已执行 `n3`、`n2`；`n2` 终态观察点外部改流为
  `[n2,X,n3,Y]`，`ProcessBoundaryActions` 在驱动中热重载 ⇒ 锚＝最后完成 `n2` ⇒ `candidate = Next(n2) = X` ⇒ X 完成后
  沿 `plan.Next` 推进撞**已完成** `n3` ⇒ **n3 被第二次提交**（外部副作用重复发生、不可撤销）。开工字节实测提交序列
  `["n3","n2","X","n3","Y"]`。
- **BO-9 反例②（R34 F5 形状，跨轮次多停驻）**：计划 `[A]`（无循环定义）+ `A@loop0` 已完成、`A@loop1/A@loop2/A@loop3`
  仍有效（可定位、未完成）停驻；线性推进只能到达全序最早的 `A@loop1`，较晚轮次义务不在 `Next` 链上。开工字节实测
  只驱动 `A@loop1`（`[(A,1)]`），`A@loop2`／`A@loop3` 从未被重驱（跳步；仅由 BO-6 聚合保护压成 `Failed`，不再是假成功）。
- **红证据**：`_workflow/wave3-bo8-bo9/red-final/`——开工字节由 `red-final/prefix-source.cs` 固定（SHA-256 `5470cfcb…`，
  等于 `opening.json` 记录），`bo8-bo9-red-final.trx` = 3 failed／0 passed／3 total，构建 exit 0、测试 exit 1
  （`red-final-exits.json`）；运行后源码逐字节恢复为本批最终字节 `539dfe39…`。
- **修复后同一组夹具**：BO-8 提交 `["n3","n2","X","Y"]`、`n3` 恰一次、收敛 `Succeeded` 且收尾恰一次；BO-9 提交
  `[(A,1),(A,2),(A,3)]` 各恰一次、`A@loop0` 不重跑、`Succeeded`、收尾一次；持续等待分支
  （`Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess`）回到 `LocalWaitParking`、零收尾、
  `TailReached=false`、停驻标记仍可定位且未完成。

### §24.127.3 反向突变与回归证据

- **12 项反向突变全部在最终字节上重做**（`_workflow/wave3-bo8-bo9/mutations-final/`；机读记录
  `mutation-records-final.json`）：8 项为上一批 BO-6/BO-7 突变在**新哈希**上的重新绑定（`-v5` 后缀；其中"关闭完成过滤"
  一项的源码模式随 BO-8 措辞更新，原模式已不存在），4 项为本批新增——`bo8-completion-filter-v5`、
  `bo9-tail-reentry-disabled-v5`、`bo9-reentry-order-last-v5`、`bo9-persisted-tail-reopen-v5`。全部
  baseline Passed／mutant Failed／restored Passed，三阶段构建 exit 0、`mutant_exit=1`，命中具名目标断言
  （`WorkflowRunnerTests.cs:1008／1070／1071／1080／1131／1173／1222／1283`），源码逐字节恢复
  （`original_sha256 == restored_sha256 == 539dfe39e281305b4b8181def55e7d5f1d5ee9f27035137de0f587f3f3a82087`）。
  **不复用旧哈希、不沿用旧 mutant SHA**；被中止的初版运行目录 `mutations/` 不作为证据。
- **构建与回归**：两个项目 `-p:DeployToBgiTools=false` Rebuild exit 0（0 error）；最终定向三类
  （`LocalWaitIdentityTranslationTests`＋`LocalWaitParkingStateContractTests`＋`WorkflowRunnerTests`）**96/96**；
  助手全量 **1565 passed / 2 skipped / 0 failed / 1567**。同条件主线基线（开工 HEAD、本批改动前独立重跑）
  **1562 passed / 2 skipped / 0 failed / 1564**、定向 93/93；testId 逐名对照 **1564 unchanged／3 added／0 removed／
  0 changed**（3 added 即本批三项新夹具，全部 Passed）。
- **部署目标未被写入**：`BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`
  （1158 文件，目录 LastWriteTimeUtc `2026-09-26T21:41:14.1381449Z`）与 `bin/Debug` 同名目录（2242 文件，`…1215991Z`）
  在开工与全部构建/测试后逐项一致。
- **判别力自检（R5）**：BO-8 fact ← `bo8-completion-filter-v5`；BO-9 全序 fact ← `bo9-tail-reentry-disabled-v5` 与
  `bo9-reentry-order-last-v5`；BO-6 链尾防御 fact ← `bo9-persisted-tail-reopen-v5` 与 rebased
  `bo6-tail-fail-closed-v5`／`bo6-tail-persisted-failure-v5`；mixed Runner fact ← 6 项 rebased 突变。

### §24.127.4 会诊状态与预算

__CONSULTATION__

### §24.127.5 边界与门禁

本子批只施工 BO-8、BO-9 与 BO-6/7-D1；R5.6、R6.1、R6 diff guard 未提前集成，未消费任何其他并行成果；
BO-6/BO-7 已闭合项不重开。证据限于助手侧源码、真实 Runner 驱动夹具（单进程、假边界）与主线集成回归；
**未做**实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证，不声称实机或生产验收；生产入口门、真实 User 门、
R5.8 签署与生产进程门继续关闭。停驻语义生产可达性结论未被本批改变（`ShouldRegisterLocalWait` 生产恒 null）。
"""
t = t.rstrip("\r\n") + "\r\n" + section.replace("\n", "\r\n")
write(r53, t)

# ---------- 2) _batch21/b21_plan.md ----------
p = "_batch21/b21_plan.md"
t = read(p)
t = t.rstrip("\r\n") + "\r\n" + (
"- **BO-8/BO-9 主线施工与 BO-6/7-D1 修正（2026-09-28，`wave3-bo8-bo9-2026-09-28`）**："
"BO-8 R29 IMPORTANT（恢复点之后推进段无完成过滤）与 BO-9 R34 F5 IMPORTANT（多有效停驻跨轮次推进）在主线同一推进层内收敛——"
"推进段完成过滤改为无条件稳定身份跳过；真实链尾按计划全序重入仍存活的停驻义务（入口即持久链尾的记录不由该路径重开）。"
"反例先行：开工字节上 BO-8 实测提交 `[n3,n2,X,n3,Y]`（n3 二次提交）、BO-9 实测只驱动 `A@loop1`（跳步）；"
"修复后分别为 `[n3,n2,X,Y]` 与 `[(A,1),(A,2),(A,3)]`。BO-6/7-D1（建议级注释陈旧，含 `:1400–1403`、`:1486`、`:1535–1537`）"
"在同一哈希重绑定内按登记口径修正。最终定向 96/96、助手全量 1565/2/0/1567（基线 1562/2/0/1564）、"
"testId 1564 unchanged/3 added/0 removed/0 changed；12 项反向突变在新字节上重做（8 项 rebased + 4 项新增）全部 P/F/P 且命中具名断言；"
"部署目标未被写入。R5.6、R6.1、R6 diff guard 未集成；BO-8/BO-9 原级登记与边界见 R5.3 §24.127。\r\n")
write(p, t)

# ---------- 3) 总计划 ----------
p = "槲寄生调度器总计划.md"
t = read(p)
t = t.rstrip("\r\n") + "\r\n" + (
"- **BO-8/BO-9 主线施工（2026-09-28，`wave3-bo8-bo9-2026-09-28`）**：在茶包主线施工 Wave3 剩余两项原级未闭合 IMPORTANT——"
"BO-8 R29（恢复点之后推进段无完成过滤：修订重排把已完成节点挪到恢复点之后时该节点被二次提交）与 BO-9 R34 F5"
"（多有效停驻跨轮次推进：无循环修订下较晚轮次停驻义务不被重驱）。实现：推进段完成过滤无条件按稳定出现身份生效；"
"真实链尾按计划全序重入仍存活的停驻义务，入口即持久链尾的记录仍按 BO-6 防御语义 Failed 且零提交。"
"反例先行（开工字节红：BO-8 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）；修复后定向 96/96、助手全量 1565/2/0/1567"
"（同条件基线 1562/2/0/1564）、testId 1564 unchanged/3 added/0 removed/0 changed；12 项反向突变在新字节上重做并命中具名断言；"
"BO-6/7-D1 建议级注释陈旧在同一哈希重绑定内修正。部署目标未被写入，材料外两份既有未提交文档未触碰。生产门与实机门继续关闭；"
"详见 R5.3 §24.127。\r\n")
write(p, t)

# ---------- 4) sb21-4 handoff ----------
p = "_batch21/sb21-4-handoff-2026-09-28.md"
t = read(p)
t = t.rstrip("\r\n") + "\r\n" + (
"\r\n### BO-8/BO-9 主线施工与收口（2026-09-28）\r\n\r\n"
"- 独立子批 `wave3-bo8-bo9-2026-09-28` 在茶包主线施工：BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT 按原级登记；"
"BO-6/7-D1（建议级注释陈旧）在同一份 `WorkflowRunner.cs` 哈希重绑定内按登记口径修正。"
"未重开 BO-6/BO-7 已闭合项、BO-13、SB21-3 BO-4、SB21-2 BO-10/12；R5.6、R6.1、R6 diff guard 未提前集成。\r\n"
"- 实现：`DriveAsync` 推进段完成过滤无条件按稳定出现身份生效（BO-8）；真实链尾按计划全序重入仍存活的停驻义务"
"（BO-9，`TryRelocateToLivePark`），入口即持久链尾的记录保持 BO-6 的 Failed／零提交防御语义。\r\n"
"- 证据：反例先行红（开工字节 `red-final/`：BO-8 提交 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）、最终定向 96/96、"
"助手全量 1565/2/0/1567（基线 1562/2/0/1564）、testId 1564 unchanged/3 added/0 removed/0 changed、"
"12 项反向突变在新字节上 P/F/P、部署目标未被写入。逐项见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。\r\n"
"- 生产门、真实 User、R5.8、E3/E4/E5、热键面与实机门继续关闭；本批不声称实机或生产验收。\r\n")
write(p, t)

# ---------- 5) parallel deliveries index (md) ----------
p = "Docs/design/mistletoe-parallel-deliveries.md"
t = read(p)
old = "BO-8/BO-9 仍未并入未闭合；BO-6/7-D1 仍为建议级；生产门与实机门继续关闭 |"
new = ("BO-8/BO-9 仍未并入未闭合；BO-6/7-D1 仍为建议级；生产门与实机门继续关闭。"
       "〔2026-09-28 更新：BO-8 R29 与 BO-9 R34 F5 已由主线子批 `wave3-bo8-bo9-2026-09-28` 施工并按原级登记为 closed；"
       "BO-6/7-D1 已在该批内随 `WorkflowRunner.cs` 哈希重绑定修正；见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。〕 |")
assert t.count(old) == 1
t = t.replace(old, new)
write(p, t)

# ---------- 6) parallel deliveries registry (json) ----------
p = "Docs/design/mistletoe-parallel-deliveries.json"
d = json.load(io.open(p, encoding="utf-8"))
entry = [e for e in d["deliveries"] if e.get("id") == "wave3-bo6-bo7-2026-09-28"][0]
entry["blockers"] = [
    "BO-8 R29 与 BO-9 R34 F5 已由主线子批 wave3-bo8-bo9-2026-09-28 施工并按原级登记（R5.3 §24.127）；BO-6/7-D1 建议级注释陈旧已在该批内随 WorkflowRunner.cs 哈希重绑定修正。",
    "R5.6、R6.1、R6 diff guard 仍未提前集成（各自目标批次处理）。",
    "BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；未做实机或生产验收，外部执行边界为 fake。",
]
entry["followup"] = ("BO-6/7 修复已进入主线并经集成验证；随后 BO-8 R29 与 BO-9 R34 F5 已由主线子批 "
                     "wave3-bo8-bo9-2026-09-28 施工并按原级登记，BO-6/7-D1 同批修正（R5.3 §24.127）。"
                     "本条目不再有待消费内容；后续 R5/R6 主线工作按各自目标批次与并行索引处理。")
entry["later_consumption"] = {
    "batch": "wave3-bo8-bo9-2026-09-28",
    "note": ("BO-8/BO-9 为台账 BO-11 冻结残项，与并行成果无关；BO-6/7-D1 由本批修正。"
             "本条目所列交付物（BO-6/BO-7 源码/夹具/文档）在接收批已逐字节接收，未再变更。"),
    "date": "2026-09-28",
}
d["updated_at"] = "2026-09-28T20:05:00+08:00"
io.open(p, "w", encoding="utf-8", newline="\n").write(json.dumps(d, ensure_ascii=False, indent=2) + "\n")
print("wrote", p)
