# -*- coding: utf-8 -*-
"""ev2 第 4 轮发现处置登记。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r4 = next(r for r in d["rounds"] if r["round"] == 4)
fs = r4["findings"]
assert len(fs) == 6, len(fs)

DISPOSITIONS = [
    ("采纳并已更正——§五.1 终帧更正为 _ev2_targeted_green5.trx（第 3 轮修复后形态），如实注明属第 3 轮必改4 同类漏改（当时处置了 _build_evidence.py 与突变日志，漏了判据 1 一句）；全文件交叉自查：§五.2/§八/突变日志/FRAMES 映射四处均指 green5，无第二处残留。",
     "_ev2/ev2_objective.txt"),
    ("采纳并已补全——§三 权威清单重写为 16 个随批文件全枚举（1 新增测试文件＋2 修改的既有测试文件＋13 个 _ev2 文件），与 git status 暂存集一一对应；清单头注标明第 4 轮更新。",
     "_ev2/ev2_objective.txt"),
    ("采纳并已同步——ev2_collection_definition_excerpt.md 末行改为「挂入该集合的四个类＝JobRegistry.Instance 单例写者全集」，并指向 ev2_singleton_writers_scan.md 的逐文件定性表（CoordinatedTaskQueueTests 第 3 轮补挂）。",
     "_ev2/ev2_collection_definition_excerpt.md"),
    ("采纳并已更正——突变日志口径节的复绿帧指称由 green2 更正为 green5（含更正登记行）；与末段终帧演进叙述一致。",
     "_ev2/ev2_mutation_log.md"),
    ("采纳并已落地——新增 M6＝JobRegistry.TryMarkTerminal 摘除已终态拒绝守卫（方向反转＝可覆盖）：实测 3 红（FIX-1×2 的注册表断言＝执行体终态被 Succeeded 覆盖、FIX-2＝被 Cancelled 覆盖）、7 绿（无先写终态的方向不敏感夹具），先终态者赢方向的判别力链补上直接突变；M6 对 JobRegistryTests 既有合同夹具的波及如实登记于脚本与日志。apply→revert 后 git diff 0 差异、status 干净。",
     "_ev2/ev2_mutation_log.md; _ev2/_apply_mutation.py; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_mutM6_red.trx"),
    ("采纳并已根治——_apply_mutation.py 重构：按目标文件原始字节探测换行风格（CRLF/LF）写回，apply→revert 字节级往返；M6 已在新实现下执行并 diff=0 核验，两生产文件 status 干净，不再产生行尾副作用。",
     "_ev2/_apply_mutation.py"),
]
for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"
json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round4 处置登记完成：", [f["level"] for f in fs])
