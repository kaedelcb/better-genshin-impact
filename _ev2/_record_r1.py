# -*- coding: utf-8 -*-
"""ev2 第 1 轮发现处置登记（直接补 disposition/evidence/status，保留会诊原文）。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r1 = next(r for r in d["rounds"] if r["round"] == 1)
fs = r1["findings"]
assert len(fs) == 5, len(fs)

DISPOSITIONS = [
    (  # 必改1：16 帧笔误
        "采纳并已更正——判据 2 权威口径改为「10 帧＝3 定向绿（初版/突变还原后/夹具加固后）＋5 突变红＋2 全量（加固前/加固后终帧）」；处置时先更正 16→8，夹具加固后补 green3/final2 两帧归档重建（SHA 清单与摘录同步）。"
        "复绿口径明示：逐突变组以 git diff 0 差异保证实现回到原形，统一以加固后定向绿帧 _ev2_targeted_green3.trx（10/10）复绿，不逐组单跑复绿帧——"
        "反例所指「每组还原复绿帧缺位」如实登记为口径选择：还原正确性由 git diff 0 差异＋加固后定向绿帧 10/10 证明，非 16 帧口径。",
        "_ev2/ev2_objective.txt; _ev2/ev2_trx_sha256.md; _ev2/_build_evidence.py",
    ),
    (  # 必改2：差集产物缺位
        "采纳并已补齐——夹具加固后重跑全量终帧 _ev2_bgi_full_final2.trx（1059 通过/1045/14 失败），与 r58_bgi_full_20260924.trx 基线 14 项做逐名集合差＝新增 0/消失 0；差集核验产物 _ev2/ev2_full_diff_baseline.md 落盘（含 14 项逐名清单、两帧计数、本批 10 例入帧核验）；判据 3 改为引用该产物。",
        "_ev2/ev2_full_diff_baseline.md; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_bgi_full_final2.trx",
    ),
    (  # 重要3：单例淘汰/污染
        "采纳并按闭合路径①处置——三个 JobRegistry.Instance 写者（BgiTaskCoordinatorTests、TaskTakeoverIncidentTests、本批新类）同挂既有非并行集合 TaskTakeoverIncident（DisableParallelization=true，定义于 TaskTakeoverIncidentTests.cs）⇒ 单例写者两两互不并行，终态淘汰/反向污染的并发交错在本批夹具执行窗口内不可达。"
        "并行配置事实核实：工程无 xunit.runner.json、无程序集级 CollectionBehavior（xunit 2.5.3 跨集合并行为默认）；单例写者全集经 grep 核实仅上述三个测试文件（间接构造者无）。"
        "另 FIX-9 末尾 ClearQueue 把 8 个占位项终态化，消除 Queued 非终态作业跨用例滞留（非终态作业本就不进淘汰 FIFO，如实注明属额外收敛）。"
        "加固后定向 10/10 绿、全量 1059/1045/14 与基线差集空（终帧 _ev2_bgi_full_final2.trx）。",
        "Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs; Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTests.cs; _ev2/ev2_full_diff_baseline.md",
    ),
    (  # 建议4：口径小疵
        "采纳并已更正——M1 行「预期守护：四夹具」更正为「五夹具（实际红帧 6 例）」；突变日志补 FIX-n→测试方法编号对照表；夹具文件头注补同一对照表（与日志一致）。",
        "_ev2/ev2_mutation_log.md; Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs",
    ),
    (  # 建议5：racy-stat 假 M
        "采纳并已处置——git restore --source=HEAD 归位为干净检出版本＋git update-index --really-refresh；归位过程中如实登记真发现：突变脚本以 universal-newlines 读写曾把工作副本行尾改写（git diff 仍 0＝规范化等价），已随 restore 消除。现为 git diff 0 差异＋git hash-object 与 HEAD blob 相同（b8679dd6…）双证，git status 干净。",
        "_ev2/ev2_objective.txt",
    ),
]

for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"

json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round1 处置登记完成：", [f["level"] for f in fs])
