# -*- coding: utf-8 -*-
"""ev2 第 2 轮发现处置登记（直接补 disposition/evidence/status，保留会诊原文）。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r2 = next(r for r in d["rounds"] if r["round"] == 2)
fs = r2["findings"]
assert len(fs) == 5, len(fs)

DISPOSITIONS = [
    (  # 必改1：_record_r1.py 未跟踪
        "采纳并已处置——_ev2/_record_r1.py 显式暂存（git add）随批提交：它是第 1 轮处置的登记载体，与 _apply_mutation.py/_build_evidence.py 同属处置工具链，如实入库留痕。第 2 轮起 _record_r2.py 同口径处理。",
        "_ev2/_record_r1.py; _ev2/_record_r2.py",
    ),
    (  # 重要2：事实载体不在材料内
        "采纳并已闭合——①TaskTakeoverIncidentTests.cs（CollectionDefinition(\"TaskTakeoverIncident\", DisableParallelization=true) 定义所在文件，第 15 行）纳入第 3 轮 scope，定义全文随材料快照送审，闭合在定义层成立（R4）；②新增 _ev2/ev2_singleton_writers_scan.md：集合定义 grep 输出、JobRegistry.Instance 写者全集 grep 输出（三个测试文件，间接写者集合与直接引用一致，无第四者；JobRegistryTests 自建实例不触单例）、并行配置事实（无 xunit.runner.json、无程序集级 CollectionBehavior，xunit 2.5.3 跨集合并行为默认）。两个事实均可由材料内快照与扫描产物独立复核。",
        "_ev2/ev2_singleton_writers_scan.md; Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/TaskTakeoverIncidentTests.cs",
    ),
    (  # 建议3：脚本注释旧编号
        "采纳并已更正——_apply_mutation.py PAIRS 各组注释更新为权威对照：M1→FIX-1/2/3/4/5、M2→FIX-3/5/6/7/8（并补「FIX-1×2 与 FIX-2 预期保持绿」方向对照说明）、M3→FIX-5、M4→FIX-7、M5→FIX-8；与夹具头注、突变日志三方一致。",
        "_ev2/_apply_mutation.py",
    ),
    (  # 建议4：摘录标题硬编码
        "采纳并已修复（修复落在源）——_build_evidence.py 的摘录标题与 SHA 清单标题均改为 len(FRAMES) 派生，不再硬编码帧数；重跑不再复发。帧数权威口径＝_ev2_trx_sha256.md 帧清单（当前 11 帧）。",
        "_ev2/_build_evidence.py; _ev2/ev2_trx_excerpts.md; _ev2/ev2_trx_sha256.md",
    ),
    (  # 建议5：FIX-9 清理与超时窗口
        "采纳并已修复——①ClearQueue 移入 finally：断言中途失败也必须清理，不把非终态泄漏叠加在原始失败之上；②FIX-9 的 Harness slotWaitTimeout 拉长到 30s：占位项在用例期间绝不因等槽超时移出 _pending（否则第 9 个提交变 Queued 而非 QueueFull）。修复后全部证据帧按当前送审夹具形态重做：M1-M5 突变红帧重跑（红数 6/5/1/1/1 与第 1 轮一致）、定向绿帧 _ev2_targeted_green4.trx（10/10）、全量终帧 _ev2_bgi_full_final3.trx（1059/1045/14，与基线差集空，产物 ev2_full_diff_baseline.md 同步重算）。",
        "Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_targeted_green4.trx; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_bgi_full_final3.trx; _ev2/ev2_full_diff_baseline.md",
    ),
]

for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"

json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round2 处置登记完成：", [f["level"] for f in fs])
