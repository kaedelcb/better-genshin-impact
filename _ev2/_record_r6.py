# -*- coding: utf-8 -*-
"""ev2 第 6 轮发现处置登记。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r6 = next(r for r in d["rounds"] if r["round"] == 6)
fs = r6["findings"]
assert len(fs) == 5, len(fs)

DISPOSITIONS = [
    ("采纳并根治——ElementTree Element 真值＝子元素数，纯文本 <Message> 恒为 falsy ⇒ `or` 回退永不生效（第 5 轮处置声称与产物矛盾成立，产物即反证，如实承认）。改为 `is None` 显式判定并在脚本注释注明该陷阱；重建后 _ev2_trx_excerpts.md 的 8 个 mutM 帧 19 行失败记录全部带断言级消息（M1＝Expected \"completed\"/Actual \"failed\" 等），第 5 轮建议5② 在原层真正生效（R4）。",
     "_ev2/_build_evidence.py; _ev2/ev2_trx_excerpts.md"),
    ("采纳并改为规则式引用——复绿锚＝_build_evidence.py FRAMES 中标注「终帧」的定向绿帧（当前 green6），不再硬编码帧名；更正史（green2→green5→green6 两次漏改）如实登记于突变日志口径节。",
     "_ev2/ev2_mutation_log.md"),
    ("采纳并已同步——§三 更新为 18 个随批文件（_ev2 15 个，补 _record_r5.py），标题注明最终提交口径以 §十 提交清单为准。",
     "_ev2/ev2_objective.txt"),
    ("采纳并已查明补记——两红均为先终态者赢合同断言：①TryMarkTerminal_FirstWriterWins_SecondMarkIsNoop（夹具主体即该合同，M6 下二次写成功 ⇒ 红）；②Submit_WithParentJobId_ChildrenLinkToDragonParent 尾部两行显式断言同一合同（Assert.False(父二次终态写)，M6 下返回 true ⇒ 红）。逐例因果补入突变日志佐证帧节；非环境性/无关失败。",
     "_ev2/ev2_mutation_log.md"),
    ("采纳并已更正三处——脚本用法注释 M1..M6→M1..M7；§五.2 括号补齐；§五 标题更新为第 6 轮。",
     "_ev2/_apply_mutation.py; _ev2/ev2_objective.txt"),
]
for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"
json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round6 处置登记完成：", [f["level"] for f in fs])
