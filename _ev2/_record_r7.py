# -*- coding: utf-8 -*-
"""ev2 第 7 轮发现处置登记（最后一个处置登记脚本；后续轮次直接编辑台账并注明）。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r7 = next(r for r in d["rounds"] if r["round"] == 7)
fs = r7["findings"]
assert len(fs) == 6, len(fs)

DISPOSITIONS = [
    ("采纳并根治——①§三 与 §十 提交清单改为规则式口径：随批文件集＝提交时暂存集本身（git status A/M 条目为权威），构成描述给出类别与终态清单（测试 3＋_ev2 17＝20），不再手写会失效的总数；②自第 7 轮起处置登记不再新增 _record_rN.py 脚本文件（本脚本为最后一个），后续处置直接编辑台账并在对账节注明 ⇒ 计数漂移根源（每轮新增脚本使枚举失效）消除。三次复发（16≠17、17≠18、18≠19）的演变史在 §十二 如实登记。",
     "_ev2/ev2_objective.txt; _ev2/_record_r7.py"),
    ("采纳并已修正——突变日志口径行的还原核验改为「对被突文件（BgiTaskCoordinator.cs／JobRegistry.cs，按脚本 TARGETS 表）各自 git diff 0 差异＋git status 干净双核验」，M6（目标 JobRegistry.cs）的还原核验不再空真。",
     "_ev2/ev2_mutation_log.md"),
    ("采纳并已修正——第 6 轮对账节顺延为「十一」，本轮为「十二」；§十 提交清单标注「独立小节·最终口径」。",
     "_ev2/ev2_objective.txt"),
    ("采纳并已更正——①baseline 首行父注改为「final5＝第 5 轮修复后形态（FIX-8 断言补强后重做）」；②_record_r1.py 必改2 处置文本的计数笔误（「1059 通过/1045/14 失败」）正确读法＝1045 通过/14 失败/1059 总——该文本同时是已登记台账内容，按项目更正惯例不改写历史文件，以本更正为准。",
     "_ev2/ev2_full_diff_baseline.md; _ev2/ev2_objective.txt"),
    ("采纳并已对齐——§五.1 改为与突变日志同口径的规则式引用（终帧＝FRAMES 中标注「终帧」的定向绿帧，当前＝green6），批内不再有两种口径并存。",
     "_ev2/ev2_objective.txt"),
    ("确认——RecordTerminal 两侧分裂为现存生产可观测不一致，本批按目标书只表征不修（合规）；残项归宿＝§24.84 候选合同（I8 族）＋目标书 §四 观测边界冻结声明，已确认无新增阻断，登记于 §十二。",
     "_ev2/ev2_objective.txt"),
]
for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"
json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round7 处置登记完成：", [f["level"] for f in fs])
