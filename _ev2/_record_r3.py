# -*- coding: utf-8 -*-
"""ev2 第 3 轮发现处置登记（直接补 disposition/evidence/status，保留会诊原文）。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r3 = next(r for r in d["rounds"] if r["round"] == 3)
fs = r3["findings"]
assert len(fs) == 5, len(fs)

DISPOSITIONS = [
    (  # 重要1：摘录无机械核验载体
        "采纳并补齐（承认偏差）——第 2 轮处置原文写「定义全文随材料快照送审」，实际第 3 轮材料只送了自述摘录：偏差原因是全文快照使材料达 292K 字符、kimi 上游连续 504/EOF（如实登记于摘录文件与本节）。补齐：ev2_collection_definition_excerpt.md 增加字节数/SHA-256/git blob 三重哈希锚（git blob=0c8f6788… 与 HEAD blob 同值 ⇒ 文件零改动可机械证明），口径改为「摘录＋机械核验锚」——审计者对工作区文件复核任一哈希即等价于见到定义全文。",
        "_ev2/ev2_collection_definition_excerpt.md",
    ),
    (  # 重要2：写者全集扫描不足
        "采纳并按 v2 扫描闭合——承认 v1 两缺陷（裸类型名 pattern 漏 target-typed new；中介符号未扫）。v2 扫描（ev2_singleton_writers_scan.md）以裸类型名＋五个中介符号（InstanceRequestHandler/ExternalInterfaceQueryPlane/ExternalInterfaceCommandPlane/TaskRunner/TaskTriggerDispatcher）全集重扫 ⇒ 新发现 CoordinatedTaskQueueTests（经 BgiTaskCoordinator.Submit 写单例，target-typed new 形态）⇒ 补挂 TaskTakeoverIncident 串行集合（+4 行，第 3 轮修复后全帧重跑验证）；另逐文件核查 TaskSuspendOnlineSignalTests＝仅静态纯名称分类（IsOnlineSignalTask 5 处断言），无注册表触及，不挂。串行集合成员 3 类→4 类；扫描产物含逐文件定性表与 grep 原始输出。",
        "_ev2/ev2_singleton_writers_scan.md; Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/CoordinatedTaskQueueTests.cs",
    ),
    (  # 重要3：FIX-7 同构窗口
        "采纳并已修复——FIX-7 的 Harness slotWaitTimeout 同样拉长到 30s（用例语义不需要短超时）：消除「测试线程冻结 >5s ⇒ pump 先按 task_busy 超时记终态 ⇒ CancelByHandle 返回 NotFound ⇒ 环境性冻结伪装成取消路径断言失败」的窗口；修复理由与第 2 轮建议 5（FIX-9）同构、在夹具头注注明。修复后 green5 定向 10/10 绿、M1-M5 红帧按当前形态重做（红数与此前三轮一致）。",
        "Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_targeted_green5.trx",
    ),
    (  # 必改4：帧身份叙述矛盾
        "采纳全部四点并已修复——①_build_evidence.py 身份标签改为显式 FRAMES 映射（帧,标签)二元组，不再子串匹配推断，green4/final3 类误标不可能复发；②final3 身份更正为「第 2 轮修复后留档」；③目标书帧叙述更正：final2＝第 1 轮加固后留档、final3＝第 2 轮修复后留档、final4＝最终帧；加固前帧 final1 不在归档清单内（如实登记，未被任何判据引用）；④突变日志终帧叙述随第 3 轮重做更新（终帧＝green5/final4）。摘录/SHA 清单/归档已按 13 帧重建。",
        "_ev2/_build_evidence.py; _ev2/ev2_trx_excerpts.md; _ev2/ev2_trx_sha256.md; _ev2/ev2_objective.txt; _ev2/ev2_mutation_log.md",
    ),
    (  # 建议5：WaitForTerminalEvent 返回值
        "采纳并已修复——WaitForTerminalEvent 返回值改用与等待谓词相同的过滤（含 errorCode），消除未来同句柄同名多事件时读到非预期记录的可能；当前各用例每句柄每事件名仅一条记录的现状不变。",
        "Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs",
    ),
]

for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"

json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round3 处置登记完成：", [f["level"] for f in fs])
