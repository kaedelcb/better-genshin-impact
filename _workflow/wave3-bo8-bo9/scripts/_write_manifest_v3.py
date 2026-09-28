import io, json, hashlib, os, xml.etree.ElementTree as ET
root = r"E:\Program Files\better-genshin-impact-LCB"
base = r"_workflow/wave3-bo8-bo9"
batch = "wave3-bo8-bo9-2026-09-28"
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
sources = ["MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
           "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"]
cur = {s: sha(s) for s in sources}
old = {"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs":
       "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
       "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs":
       "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711"}
red_v1 = dict(old, **{sources[1]: "409844e01336298ff05cacb592179b42fe0452850aba210ddea2447a77649c7d"})
red_final = dict(old, **{sources[1]: "eef5b4eb3651bbe352cd5370317ff1e3b02bf015816f6cf3a4a13aaf204bbece"})
r1 = dict(old, **{sources[0]: "539dfe39e281305b4b8181def55e7d5f1d5ee9f27035137de0f587f3f3a82087",
                  sources[1]: "eef5b4eb3651bbe352cd5370317ff1e3b02bf015816f6cf3a4a13aaf204bbece"})
r2 = dict(old, **{sources[0]: "c71db8647501baf98f0de5cb4fab6d33cd63704b7e868ab17f8de7bf4bde0ae1",
                  sources[1]: "e7b55dbff4205d2832b3e421ada1417d7318af199320d6ef7835f9aab29b5bb2"})
print("current", json.dumps(cur, indent=1))

trx = base + "/final-v8/targeted-final.trx"
byid = {u.get("id"): u.get("name") for u in ET.parse(os.path.join(root, trx)).getroot().iter(NS + "UnitTest")}
def tid(name):
    hits = [k for k, v in byid.items() if v.endswith("." + name)]
    assert len(hits) == 1, (name, hits)
    return hits[0]
ids = {k: tid(n) for k, n in {
    "bo8": "RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted",
    "bo9_multi": "Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder",
    "bo9_wait": "Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess",
    "bo9_preinsert": "Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail",
    "bo9_settled": "Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode",
    "mixed": "Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences",
    "tail": "Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion"}.items()}
print(json.dumps(ids, indent=1))

def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
before = [l for l in io.open(os.path.join(root, base + "/claims-v3/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()]
after = io.open(os.path.join(root, "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"), encoding="utf-8-sig").read().splitlines()
b = {idn(l) for l in before}
added_lines = [i for i, l in enumerate(after, 1) if l.strip() and idn(l) not in b]
r53 = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
lines = io.open(os.path.join(root, r53), encoding="utf-8").read().splitlines()
r53_start = next(i for i, l in enumerate(lines, 1) if l.startswith("## §24.127"))
print("claim added", added_lines, "r53", r53_start, len(lines))

def ev(i, path, purpose, level, conditions, binding="current", hashes=None):
    return {"id": i, "path": path, "purpose": purpose, "level": level, "conditions": conditions,
            "binding": binding, "source_sha256": hashes or cur}

evidence = [
    ev("opening-snapshot", base + "/opening.json", "本批开工快照（HEAD、分支、工作区状态、两个源的开工哈希与原始风险矩阵行）", "document",
       "python -B tools/mistletoe/workflow.py begin（无产品副作用）", "historical", old),
    ev("baseline-build", base + "/baseline-build.log", "开工字节测试项目 Rebuild（同条件基线前提）", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；80 warnings／0 errors", "historical", old),
    ev("baseline-targeted", base + "/baseline/targeted-baseline.trx", "同条件开工基线定向三类 93/93（对照用）", "test",
       "dotnet test … --no-build --filter 三类；93 passed", "historical", old),
    ev("baseline-full", base + "/baseline/assistant-full-baseline.trx", "同条件开工基线助手全量 1562 passed／2 skipped／0 failed／1564（testId 对照基线）", "test",
       "dotnet test … --no-build；exit 0", "historical", old),
    ev("red-prefix-source", base + "/red-final/prefix-source.cs", "红运行使用的开工字节固定件（SHA-256 等于 opening.json 的 WorkflowRunner.cs 开工哈希）", "document",
       "git show HEAD:<path> 的 LF blob 转工作区 CRLF；运行后逐字节恢复", "historical", old),
    ev("red-final-build", base + "/red-final/testproject-build.log", "红运行前测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical", red_final),
    ev("red-final", base + "/red-final/bo8-bo9-red-final.trx", "反例先行红证据（开工字节 + 本批夹具）：3 failed／0 passed／3 total", "test",
       "dotnet test … --filter 当时三个夹具；exit 1；BO-8 提交 [n3,n2,X,n3,Y]；BO-9 只提交 [(A,1)]", "historical", red_final),
    ev("red-final-exits", base + "/red-final/red-final-exits.json", "红运行退出码与源码恢复核对记录", "document",
       "run-red-final.ps1 落盘；prefix_build_exit=0、prefix_test_exit=1", "historical", red_final),
    ev("red-v1", base + "/red/bo8-bo9-red.trx", "早期红运行（BO-9 夹具为 2 停驻版本；仅留存历史）", "test",
       "dotnet test …；3 failed", "historical", red_v1),
    ev("red-v1-build", base + "/red/testproject-build.log", "早期红运行的测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical", red_v1),
    ev("round1-review-packet", base + "/review-v1/packet.md", "第 1 轮送审材料（audit review 生成，403,789 字节）", "consult",
       "workflow.py audit --stage review；快照 review-v1 经 verify 通过", "historical", r1),
    ev("round2-review-packet", base + "/review-v2/packet.md", "第 2 轮送审材料（433,123 字节）", "consult",
       "workflow.py audit --stage review；快照 review-v2 经 verify 通过", "historical", r2),
    ev("consult-preflight-v1", base + "/consultation/preflight-v1.json", "第 1 轮渠道容量预检与材料清单", "document", "本地估算；未派发请求", "historical", r1),
    ev("consult-preflight-v2", base + "/consultation/preflight-v2.json", "第 2 轮渠道容量预检与材料清单（26 件、487,548 字节）", "document", "本地估算", "historical", r2),
    ev("consult-request-v1", base + "/consultation/review-request-v1.md", "第 1 轮会诊请求文本", "consult", "发往既有 GPT 会诊工具", "historical", r1),
    ev("consult-outcome-v1", base + "/consultation/review-outcome-v1.md", "第 1 轮结论与逐项处置（1 IMPORTANT + 1 建议级）", "consult",
       "gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录", "historical", r1),
    ev("consult-request-v2", base + "/consultation/review-request-v2.md", "第 2 轮验证请求文本（严格限定两问）", "consult", "同渠道", "historical", r2),
    ev("consult-outcome-v2", base + "/consultation/review-outcome-v2.md", "第 2 轮结论与逐项处置（IMPORTANT-1 确认闭合；新增 IMPORTANT-2）", "consult",
       "gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录", "current"),
    ev("fixed-v5-build", base + "/fixed-v4-build.log", "第 1 轮修复后的测试项目 Rebuild", "build", "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical", r2),
    ev("fixed-v5-targeted", base + "/fixed-v5/targeted-fixed-v5.trx", "第 1 轮修复后的定向 97/97（历史中间版本）", "test", "dotnet test …；97 passed", "historical", r2),
    ev("fixed-v5-full", base + "/fixed-v5/assistant-full-fixed-v5.trx", "第 1 轮修复后的助手全量 1566/2/0/1568（历史中间版本）", "test", "dotnet test …；exit 0", "historical", r2),
    ev("fixed-v6-build", base + "/fixed-v6-build.log", "第 2 轮修复后的测试项目 Rebuild", "build", "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical",
       dict(old, **{sources[0]: "c71db8647501baf98f0de5cb4fab6d33cd63704b7e868ab17f8de7bf4bde0ae1",
                    sources[1]: "7bdf48a66c5245ecff9cdc9aeb0b7b442fcb58845daa6a8d2bfd60e26401f3f2"})),
    ev("fixed-v6-targeted", base + "/fixed-v5/targeted-v6.trx", "第 2 轮修复后的定向 98/98", "test", "dotnet test …；98 passed", "current"),
    ev("fixed-v6-full", base + "/fixed-v5/assistant-full-v6.trx", "第 2 轮修复后的助手全量 1567/2/0/1569", "test", "dotnet test …；exit 0", "current"),
    ev("final-build", base + "/final-v8/testproject-build.log", "最终版本（含 R5.3 §24.127 与声明面再生）测试项目 Rebuild", "build", "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；0 error", "current"),
    ev("final-targeted", base + "/final-v8/targeted-final.trx", "最终定向三类 98/98（风险矩阵与关键断言的行级 test 证据）", "test",
       "dotnet test … --filter 三类；exit 0", "current"),
    ev("final-full", base + "/final-v8/assistant-full-final.trx", "最终助手全量 1567 passed／2 skipped／0 failed／1569", "test",
       "dotnet test … --no-build；exit 0；含 ClaimSurfaceGuardTests", "current"),
    ev("final-testid-comparison", base + "/final-v8/testid-comparison.json", "基线→最终逐名 testId 对照：1564 unchanged／5 added／0 removed／0 changed", "component",
       "由基线 TRX 与 final-v8 TRX 解析生成", "current"),
    ev("mutation-records", base + "/mutation-records-round3.json", "14 项反向突变的机读记录（三阶段退出码、目标 testId/名称、断言行、TRX 路径、源码哈希）", "component",
       "由 run-mutants.ps1 生成", "current"),
    ev("mutation-runner", base + "/run-mutants.ps1", "反向突变执行脚本（逐项独立构建/测试、源码逐字节恢复、断言行与失败标记提取）", "document",
       "pwsh -NoProfile -File run-mutants.ps1；ALL MUTATIONS COMPLETE count=14", "current"),
    ev("mutation-run-log", base + "/mutations-run-round3.log", "突变运行日志（逐项 PASS 行与最终 original 哈希）", "document",
       "同一脚本 stdout；original=b0b3579b…", "current"),
    ev("claims-regen", base + "/claims-v3/claim-regen.trx", "声明面再生（CLAIM_SURFACE_REGENERATE=1）后守卫通过", "test", "dotnet test … --filter ClaimSurfaceGuardTests（带环境变量）；1 passed", "current"),
    ev("claims-noenv", base + "/claims-v3/claim-noenv.trx", "清除环境变量后复跑守卫通过（哈希稳定）", "test", "dotnet test …（无环境变量）；1 passed；清单 SHA 前后一致", "current"),
    ev("claims-before-hash", base + "/claims-v3/manifest-before.sha256", "再生前清单哈希（614 行）", "document", "5ac1737b4405b96fbe3835e5f4b8b9a732ab4889dbe964b40bd2cd29b2f33fa8", "current"),
    ev("claims-after-hash", base + "/claims-v3/manifest-after.sha256", "再生后并清除变量后清单哈希（617 行）", "document", "daa2639f36ba35ae9370a759fe84e11afe49cb8d210d3892fa03e246520fb684", "current"),
    ev("claims-diff-summary", base + "/claims-v3/manifest-diff-summary.json", "清单差异摘要：+4 行／-1 行（唯一改动行为 §24.127.4/§24.127.1 陈述更新）", "document", "逐行身份比对生成", "current"),
    ev("deploy-before", base + "/deploy-target-before.txt", "开工时部署目标读数", "document", "Get-ChildItem -Recurse -File 计数与目录 LastWriteTimeUtc", "current"),
    ev("deploy-final", base + "/deploy-target-final.txt", "全部构建与测试后部署目标读数（与开工逐项一致）", "document", "同上口径；两次读数一致", "current"),
    ev("findings", base + "/findings.md", "本批发现、实现、证据、会诊处置与边界", "document", "本批 findings", "current"),
    ev("context", base + "/context.md", "本批目标、范围、依赖顺序、开工状态与子 Agent 评估", "document", "本批 objective", "current"),
    ev("budget", base + "/budget.md", "本批会诊预算、计数与处置纪律", "document", "本批 budget", "current"),
]

tests = [
    {"id": "final-targeted", "path": base + "/final-v8/targeted-final.trx", "expect_success": True},
    {"id": "final-full", "path": base + "/final-v8/assistant-full-final.trx", "expect_success": True},
    {"id": "claims-regen", "path": base + "/claims-v3/claim-regen.trx", "expect_success": True},
    {"id": "claims-noenv", "path": base + "/claims-v3/claim-noenv.trx", "expect_success": True},
    {"id": "red-final", "path": base + "/red-final/bo8-bo9-red-final.trx", "expect_success": False},
]
mutations = json.load(io.open(os.path.join(root, base + "/mutation-records-round3.json"), encoding="utf-8"))
print("mutations", len(mutations))

rows = [r for r in json.load(io.open(os.path.join(root, base + "/risk-matrix.json"), encoding="utf-8"))["rows"] if r["id"] not in {"R9-4", "R9-5"}]
for r in rows:
    r["counterexample_ids"] = ["final-targeted"]
rows.append({
    "id": "R9-4", "dimension": "state", "critical": True, "status": "covered",
    "scenario": "无循环计划中 RecomputeSuccessor 取 candidate 早于 rescue（rescue 为某停驻同轮前插的从未执行出现）时，线性推进到链尾即 null；若链尾重入只补停驻点，rescue 出现永不被驱动。",
    "expected": "链尾按计划全序重建未履行恢复义务（仍存活停驻 + 每个停驻同轮序号更早的从未执行出现）并逐条重驱：candidate→rescue→其停驻→下一轮同构形态全部按 (LoopIteration, SequenceIndex) 升序执行恰好一次，不假成功、不重复提交。",
    "counterexample_ids": ["final-targeted"], "test_ids": [ids["bo9_preinsert"]],
    "mutation_ids": ["bo9-tail-reentry-parks-only-v5"],
    "counterexample_notes": "第 1 轮会诊 IMPORTANT-1；判别力由 bo9-tail-reentry-parks-only-v5（P/F/P，命中 :1411）钉死。",
})
rows.append({
    "id": "R9-5", "dimension": "fault", "critical": True, "status": "covered",
    "scenario": "历史停驻标记已清偿（同身份另有完成结果）后仍留在 NodeOutcomes；若链尾义务重建不先剔除该已清偿标记，会为它扫描同轮更早节点并额外执行修订新插入的未执行节点。",
    "expected": "已清偿停驻标记既不作重入点也不产生同轮前插义务：只执行真正的待执行节点（如 T），不执行 X；无余额外外部副作用，运行按真实链尾成功且不重复。",
    "counterexample_ids": ["final-targeted"], "test_ids": [ids["bo9_settled"]],
    "mutation_ids": ["bo9-settled-park-probe-v5"],
    "counterexample_notes": "第 2 轮会诊 IMPORTANT-2；判别力由 bo9-settled-park-probe-v5（P/F/P，命中 :1472）钉死。",
})
io.open(os.path.join(root, base + "/risk-matrix.json"), "w", encoding="utf-8").write(
    json.dumps({"schema_version": 1, "batch": batch, "rows": rows}, ensure_ascii=False, indent=2) + "\n")

manifest = json.load(io.open(os.path.join(root, base + "/manifest.json"), encoding="utf-8"))
manifest["sources"] = sources
manifest["review_control"] = {
    "existing_results": {"decision": "none",
        "reason": "逐项核对现有交付：BO-6/7 已于 e009068e2 接收，其证据绑定当时的 WorkflowRunner.cs 字节；本批三次改动该文件（初始实现、第 1 轮会诊修复、第 2 轮会诊修复）后所有旧哈希与旧突变绑定均失效，14 项突变已在最终字节上重跑。不存在可复用的同版本回归或突变结果。"},
    "review_round": 3,
    "repair_batch_id": "wave3-bo8-bo9-2026-09-28-review2-repair",
    "prior_findings": [
        {"id": "R1-IMPORTANT-1", "severity": "important", "source_review_evidence_id": "consult-outcome-v1",
         "disposition": "candidate_fixed",
         "repair_evidence_ids": ["final-targeted", "mutation-records", "fixed-v5-targeted", "fixed-v5-full"],
         "note": "链尾重建未履行恢复义务（仍存活停驻 + 同轮前插未执行出现）。第 2 轮验证已确认按原级闭合（见 consult-outcome-v2）；因该轮同时新增 IMPORTANT-2，本项与 IMPORTANT-2 一并进入第 3 轮材料。"},
        {"id": "R2-IMPORTANT-2", "severity": "important", "source_review_evidence_id": "consult-outcome-v2",
         "disposition": "candidate_fixed",
         "repair_evidence_ids": ["final-targeted", "mutation-records", "fixed-v6-targeted", "fixed-v6-full"],
         "note": "已清偿停驻标记先剔除（`if (HasCompletedOutcome(run, parkOcc)) continue;`）；新夹具 Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode 与新突变 bo9-settled-park-probe-v5（P/F/P，命中 :1472）钉死判别力。"},
        {"id": "R1-SUGGESTION-1", "severity": "suggestion", "source_review_evidence_id": "consult-outcome-v1",
         "disposition": "accepted",
         "reason": "D1 后两处注释解释已订正（ParkedRescue 探测口径、R24 全序说明）；另采纳 HasCompletedOutcome 口径表述与终止性收缩论证，并把「夹具均无 scheduled loop」登记为未覆盖边界。"},
    ],
    "criticality_reason": "BO-8 R29 与 BO-9 R34 F5 均为已登记 IMPORTANT；推进层同时决定「已完成节点是否被二次提交」（不可撤销外部副作用）与「恢复义务是否被吞/被凭空生成」，故 R8-1／R9-1／R9-2／R9-4／R9-5 设 critical=true 并要求具名突变。",
    "subagents": {"decision": "not_used",
        "reason": "本批改动集中在 WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor 单一共享状态链，按用户指令不得由多子 Agent 并行改代码；只读子 Agent 的信息与主执行者必须自行完成的代码通读、反例构造、突变与回归重叠，且只读报告不能替代真实 Runner 端到端证据（项目规则明示），故净收益为负。", "tasks": []},
}
manifest["evidence"] = evidence
manifest["tests"] = tests
manifest["comparison"] = {"baseline": base + "/baseline/assistant-full-baseline.trx", "final": base + "/final-v8/assistant-full-final.trx"}
manifest["mutations"] = mutations
manifest["mutation_scope"] = ("14 项独立反向突变全部在本批最终源码字节上执行：8 项为上一批 BO-6／BO-7 突变在新哈希上的重新绑定"
    "（`-v5`，其中完成过滤与链尾重入相关的模式随本批源码改写 rebased），6 项为本批新增（BO-8 完成过滤；BO-9 链尾重入关闭、"
    "重入取最后者、链尾只重入停驻点、已清偿标记前插探针、持久链尾守卫移除）。覆盖 BO-8 fact、BO-9 反例②/③/④、BO-9 持续等待、"
    "BO-6 链尾防御与 mixed Runner fact。**未覆盖**：R9-3（持续等待）未单独设突变（其重入前置与反例②共享 "
    "`bo9-tail-reentry-disabled-v5`）；BO-6/7-D1 为注释修正，不产生可突变的行为断言；有循环定义时 `LastScheduledRoundWait` "
    "的全部交错、跨进程与实机语义均未覆盖。")
manifest["outside_changes"] = ("本批只改：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、"
    "`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`、"
    "`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt`（按 §17.4-A 强制再生）、"
    "本批 `_workflow/wave3-bo8-bo9/**`、状态文档 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`、"
    "`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`、"
    "`Docs/design/mistletoe-parallel-deliveries.md` 与同名 `.json`。"
    "材料外变更（非本批写入者，本批不触碰、不提交）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、"
    "`Docs/design/unified-job-registry-master-plan.md`（两份既有未提交设计文档），以及工作区其余历史批次证据、`.bak`／`.stale`、"
    "日志、TestResults、DLL／工具输出与截图等未跟踪内容。")
manifest["packet_limit_bytes"] = 524288
manifest["packet"] = [
    {"path": base + "/context.md", "role": "objective"},
    {"path": base + "/findings.md", "role": "findings"},
    {"path": base + "/budget.md", "role": "budget"},
    {"path": base + "/consultation/review-outcome-v2.md", "role": "findings"},
    {"path": "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": added_lines[0], "end_line": added_lines[0],
     "coverage_notes": "再生清单中由 §24.127 新增的载荷声明（代表行之一）。清单是 617 行的生成产物，本批相对上一版 +4 行／-1 行（claims-v3/manifest-diff-summary.json）；再生与清除变量复跑由 claims-regen／claims-noenv TRX 与哈希记录证明，全文哈希绑定在所有 current 证据的 source_sha256 中。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": added_lines[2], "end_line": added_lines[2],
     "coverage_notes": "同批新增的第 2 条载荷声明（IMPORTANT-2 修复/守卫句）。摘录只取本批新增行，不删除例外、失败或质疑。"},
    {"path": "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", "role": "source",
     "start_line": r53_start, "end_line": len(lines),
     "coverage_notes": "本批新增的 §24.127 全文（含 §24.127.4 第 1／2 轮会诊结论与逐项处置）。这是本批在 R5.3 中的全部改动（另有两句 §24.126.5 日期化更新，原文保留）；文档其余 5000 余行未改动。"},
]
io.open(os.path.join(root, base + "/manifest.json"), "w", encoding="utf-8").write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
print("manifest v3 written; evidence", len(evidence), "rows", len(rows), "packet", len(manifest["packet"]))
