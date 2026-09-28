import json, io, os
root = r"E:\Program Files\better-genshin-impact-LCB"
base = os.path.join(root, "_workflow", "wave3-bo8-bo9")
batch = "wave3-bo8-bo9-2026-09-28"

rows = [
  {
    "id": "R8-1", "dimension": "state", "critical": True, "status": "planned",
    "scenario": "驱动中到达节点边界时新修订把已完成出现重排到恢复点之后（无停驻历史）：老计划 [n3,n2,Y] 已跑完 n3/n2，修订为 [n2,X,n3,Y]，RecomputeSuccessor 返回 X。",
    "expected": "推进段按稳定出现身份过滤已完成项：n3 只被提交一次，X 与 Y 各提交一次，运行按真实链尾收敛，不重复外部副作用。"
  },
  {
    "id": "R8-2", "dimension": "state", "critical": False, "status": "planned",
    "scenario": "同一推进层中恢复点自身是未完成停驻（waitLocally），且其后方存在已完成锚（BO-6/BO-7 已闭合形态）。",
    "expected": "完成过滤不得把停驻标记当完成：停驻义务仍被重驱，已完成锚不被重提（BO-6 语义在该层保持不变）。"
  },
  {
    "id": "R9-1", "dimension": "concurrency", "critical": True, "status": "planned",
    "scenario": "同一 run 存在多个仍有效停驻且分处不同轮次（如 A@loop1、A@loop2），当前修订已无循环定义 ⇒ 线性推进只能到达其中最早者，较晚者不在 Next 链上。",
    "expected": "推进层在真实链尾处按计划全序重入到仍存活的停驻义务：每个存活停驻都被驱动恰好一次、按 (LoopIteration, SequenceIndex) 升序、无假成功、无跳步；重入严格消耗义务（可终止）。"
  },
  {
    "id": "R9-2", "dimension": "fault", "critical": True, "status": "planned",
    "scenario": "持久记录已落盘 TailReached 且仍存可定位未完成的零发送停驻义务（BO-6 防御形态）。",
    "expected": "入口即链尾的持久不一致不得被重入重新打开：不提交任何节点、不执行收尾、聚合 Failed，停驻义务保留。"
  },
  {
    "id": "R9-3", "dimension": "fault", "critical": False, "status": "planned",
    "scenario": "重入驱动的停驻在重驱后仍要求本地等待（持续有效停驻）。",
    "expected": "运行回到 LocalWaitParking、零发送、不终态化、不触发收尾；义务标记保持可定位且未完成。"
  }
]
matrix = {"schema_version": 1, "batch": batch, "rows": rows}
with io.open(os.path.join(base, "risk-matrix.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump(matrix, f, ensure_ascii=False, indent=2)
    f.write("\n")

manifest = {
  "schema_version": 2,
  "batch": batch,
  "mode": "code",
  "sources": [
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
  ],
  "risk_matrix": "_workflow/wave3-bo8-bo9/risk-matrix.json",
  "opening_snapshot": "_workflow/wave3-bo8-bo9/opening.json",
  "review_control": {
    "existing_results": {
      "decision": "none",
      "reason": "逐项核对现有交付：BO-6/7 已于 e009068e2 接收（1945913a4/8a014cf78/f47b57b1c），其证据绑定当时的 WorkflowRunner.cs 字节；本批改动 WorkflowRunner.cs 后旧哈希与 8 项突变绑定失效，故不存在可复用的同版本结果。"
    },
    "review_round": 1,
    "prior_findings": [],
    "criticality_reason": "BO-8 R29 与 BO-9 R34 F5 均为已登记 IMPORTANT；推进层同时决定「已完成节点是否被二次提交」（不可撤销外部副作用）与「停驻义务是否被吞」，故对 R8-1/R9-1/R9-2 设 critical=true。",
    "subagents": {
      "decision": "not_used",
      "reason": "本批改动集中在 WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor 单一共享状态链，按用户指令不得由多子 Agent 并行改代码；只读子 Agent 能提供的信息与主执行者必须自行完成的代码通读、反例构造、突变与回归重叠，且只读报告不能替代真实 Runner 证据（项目规则明示 helper/只读报告不算端到端闭合）。故净收益为负，记录不使用。"
    }
  },
  "evidence": [],
  "tests": [],
  "mutation_scope": "",
  "mutations": [],
  "outside_changes": "",
  "packet_limit_bytes": 524288,
  "packet": []
}
with io.open(os.path.join(base, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump(manifest, f, ensure_ascii=False, indent=2)
    f.write("\n")
print("written")
