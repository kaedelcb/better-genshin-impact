# -*- coding: utf-8 -*-
"""ev2 第 5 轮发现处置登记。"""
import json

LEDGER = r"C:/Users/Administrator/.tools/zcode-relay/test/ledger-ev2.json"
d = json.load(open(LEDGER, encoding="utf-8"))
r5 = next(r for r in d["rounds"] if r["round"] == 5)
fs = r5["findings"]
assert len(fs) == 5, len(fs)

DISPOSITIONS = [
    ("采纳并已补全——§三 重排后权威清单：17→18 个随批文件口径在 §十「提交清单」以逐文件枚举落定（3 测试＋15 个 _ev2）；_record_r4.py 已在枚举中；本轮新增的 _record_r5.py 一并枚举，一次性文档重排脚本 _fix_objective_r5.py 如实标注「无证据价值、不随批提交」并已删除。最终口径以 §十 提交清单为准（18 个）。", 
     "_ev2/ev2_objective.txt"),
    ("采纳并已闭合——FIX-9：新增 M7＝Submit 队列满路径注册表 Rejected 写入拆除（锚点为 _pending.Count 满路径，夹具确定可达）⇒ 终形态实测 1 红（FIX-9）/9 绿；Channel 物理满兜底路径（:407-408，极端竞态、夹具不可确定构造）如实登记为未钉边界（脚本与日志均已注明）。FIX-4：其注册表侧值与协调器映射值同值（Succeeded），可覆盖突变（M6）下被覆盖为同值 ⇒ 断言仍绿，不可判别属结构性边界，如实登记于突变日志「FIX-4 注册表侧断言的覆盖边界」节（方向安全由 M6 间接守护）。",
     "_ev2/ev2_mutation_log.md; _ev2/_apply_mutation.py; Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_mutM7_red.trx"),
    ("采纳并已修复——①对账节按轮次物理重排：六=第1轮、七=第2轮、八=第3轮、九=第4轮（脚本重排后逐标记校验顺序）；②「hash-object 双证见 §六」悬空指针改为内联哈希值（BgiTaskCoordinator.cs git hash-object＝HEAD blob＝b8679dd6d54d51d497a4ef7ea929d153ff8c4762）并指向第 1 轮对账「建议5」处置与 _record_r1.py。",
     "_ev2/ev2_objective.txt"),
    ("采纳并已实跑佐证——M6 下实跑 --filter FullyQualifiedName~JobRegistryTests：7 总/2 红/5 过（佐证帧 _ev2_mutM6_jobregistry_red.trx 归档），「JobRegistryTests 先终态者赢合同夹具会红」从静态推断升级为实跑留档；突变日志 M6 行同步改写为「已实跑佐证」。",
     "Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_mutM6_jobregistry_red.trx; _ev2/ev2_mutation_log.md"),
    ("采纳并已修复——①FIX-8 补 Assert.Equal(JobErrorCodes.TaskBusy, failed.ErrorCode)，等待锚点返回值产生判别力（与 FIX-6 同模式对齐）；②_build_evidence.py 消息提取改为优先 ErrorInfo/Message（xUnit 失败消息实际位置）、回退 Output/Message，重建后摘录的突变红帧失败名单含失败断言消息。修复后全帧按终形态重做：green6（10/10）、M1-M7 红（6/5/1/1/1/3/1）、佐证帧（7 总/2 红）、final5（1059/1045/14 差集空）。",
     "Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs; _ev2/_build_evidence.py; _ev2/ev2_trx_excerpts.md"),
]
for f, (disp, ev) in zip(fs, DISPOSITIONS):
    f["disposition"] = disp
    f["evidence"] = ev
    f["status"] = "closed"
json.dump(d, open(LEDGER, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("round5 处置登记完成：", [f["level"] for f in fs])
