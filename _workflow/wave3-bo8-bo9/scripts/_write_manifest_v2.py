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
round1 = dict(old, **{sources[1]: "eef5b4eb3651bbe352cd5370317ff1e3b02bf015816f6cf3a4a13aaf204bbece"})
print("current:", json.dumps(cur, indent=1))

# testIds from the final targeted TRX
trx = base + "/final-v7/targeted-final.trx"
troot = ET.parse(os.path.join(root, trx)).getroot()
byid = {u.get("id"): u.get("name") for u in troot.iter(NS + "UnitTest")}
def tid(name):
    hits = [k for k, v in byid.items() if v.endswith("." + name)]
    assert len(hits) == 1, (name, hits)
    return hits[0]
ids = {k: tid(n) for k, n in {
    "bo8": "RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted",
    "bo9_multi": "Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder",
    "bo9_wait": "Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess",
    "bo9_preinsert": "Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail",
    "mixed": "Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences",
    "tail": "Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion"}.items()}
print(json.dumps(ids, indent=1))

# claim manifest added lines (current set)
def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
before = [l for l in io.open(os.path.join(root, base + "/claims-v2/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()]
after = io.open(os.path.join(root, "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"), encoding="utf-8-sig").read().splitlines()
b = {idn(l) for l in before}
added_lines = [i for i, l in enumerate(after, 1) if l.strip() and idn(l) not in b]
print("claim added line numbers:", added_lines)

# R5.3 section range
r53 = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
lines = io.open(os.path.join(root, r53), encoding="utf-8").read().splitlines()
r53_start = next(i for i, l in enumerate(lines, 1) if l.startswith("## §24.127"))
r53_end = len(lines)
print("r53 section:", r53_start, r53_end, "of", len(lines))

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
       "dotnet test … --filter 三个当时夹具；exit 1；BO-8 提交 [n3,n2,X,n3,Y]；BO-9 只提交 [(A,1)]", "historical", red_final),
    ev("red-final-exits", base + "/red-final/red-final-exits.json", "红运行退出码与源码恢复核对记录", "document",
       "run-red-final.ps1 落盘；prefix_build_exit=0、prefix_test_exit=1、恢复哈希 539dfe39…", "historical", red_final),
    ev("red-v1", base + "/red/bo8-bo9-red.trx", "早期红运行（BO-9 夹具为 2 停驻版本；仅留存历史）", "test",
       "dotnet test … --filter 三个夹具；3 failed", "historical", red_v1),
    ev("red-v1-build", base + "/red/testproject-build.log", "早期红运行的测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical", red_v1),
    ev("round1-review-packet", base + "/review-v1/packet.md", "第 1 轮送审材料（path/材料由 workflow.py audit review 生成，403,789 字节）", "consult",
       "python -B tools/mistletoe/workflow.py audit --stage review；快照 review-v1 经 verify 通过", "historical", round1),
    ev("consult-preflight-v1", base + "/consultation/preflight-v1.json", "第 1 轮渠道容量预检与材料清单（27 件、524,786 字节、最大 158,857 字节）", "document",
       "本地估算；未派发请求", "historical", round1),
    ev("consult-request-v1", base + "/consultation/review-request-v1.md", "第 1 轮会诊请求文本（范围、材料、请回答项与边界）", "consult",
       "发往既有 GPT 会诊工具", "historical", round1),
    ev("consult-outcome-v1", base + "/consultation/review-outcome-v1.md", "第 1 轮会诊结论与逐项处置（1 IMPORTANT + 1 建议级；IMPORTANT 已修复候选、建议级采纳）", "consult",
       "gpt-6-astra / medium，attempts=1，read-only；报告原文要点逐项抄录", "current"),
    ev("fixed-v5-build", base + "/fixed-v4-build.log", "第 1 轮 IMPORTANT-1 修复后的测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；0 error", "current"),
    ev("fixed-v5-targeted", base + "/fixed-v5/targeted-fixed-v5.trx", "修复后、文档未更新前的定向三类 97/97（中间版本）", "test",
       "dotnet test … --filter 三类；97 passed", "current"),
    ev("fixed-v5-full", base + "/fixed-v5/assistant-full-fixed-v5.trx", "修复后、文档未更新前的助手全量 1566 passed／2 skipped／0 failed／1568（中间版本）", "test",
       "dotnet test … --no-build；exit 0", "current"),
    ev("final-build", base + "/final-v7/testproject-build.log", "最终版本（含 R5.3 §24.127 与声明面再生）测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；0 error", "current"),
    ev("final-targeted", base + "/final-v7/targeted-final.trx", "最终定向三类 97/97（风险矩阵与关键断言的行级 test 证据）", "test",
       "dotnet test … --filter LocalWaitParkingStateContractTests|LocalWaitIdentityTranslationTests|WorkflowRunnerTests；exit 0", "current"),
    ev("final-full", base + "/final-v7/assistant-full-final.trx", "最终助手全量 1566 passed／2 skipped／0 failed／1568", "test",
       "dotnet test … --no-build；exit 0；含 ClaimSurfaceGuardTests", "current"),
    ev("final-testid-comparison", base + "/final-v7/testid-comparison.json", "基线→最终逐名 testId 对照：1564 unchanged／4 added／0 removed／0 changed", "component",
       "由 baseline/assistant-full-baseline.trx 与 final-v7/assistant-full-final.trx 解析生成", "current"),
    ev("mutation-records", base + "/mutation-records-round2.json", "13 项反向突变的机读记录（三阶段退出码、目标 testId/名称、断言行、TRX 路径、源码哈希）", "component",
       "由 run-mutants.ps1 生成；每项 baseline/mutant/restored 独立 TRX 与日志", "current"),
    ev("mutation-runner", base + "/run-mutants.ps1", "反向突变执行脚本（逐项独立构建/测试、源码逐字节恢复、失败断言行提取、逐项失败标记）", "document",
       "pwsh -NoProfile -File run-mutants.ps1；ALL MUTATIONS COMPLETE count=13", "current"),
    ev("mutation-run-log", base + "/mutations-run-round2.log", "突变运行日志（逐项 PASS 行与最终 original 哈希）", "document",
       "同一脚本 stdout；original=c71db864…", "current"),
    ev("claims-regen", base + "/claims-v2/claim-regen.trx", "声明面再生（CLAIM_SURFACE_REGENERATE=1）后守卫通过", "test",
       "dotnet test … --filter ClaimSurfaceGuardTests（带环境变量）；1 passed", "current"),
    ev("claims-noenv", base + "/claims-v2/claim-noenv.trx", "清除环境变量后复跑守卫通过（哈希稳定）", "test",
       "dotnet test … --filter ClaimSurfaceGuardTests（无环境变量）；1 passed；清单 SHA 前后一致", "current"),
    ev("claims-before-hash", base + "/claims-v2/manifest-before.sha256", "再生前清单哈希（609 行）", "document",
       "84e90f99bddd36fe3e0193cf33da758cac33ad103da8d36a1c3bf91dca703434", "current"),
    ev("claims-after-hash", base + "/claims-v2/manifest-after.sha256", "再生后并清除变量后清单哈希（614 行）", "document",
       "5ac1737b4405b96fbe3835e5f4b8b9a732ab4889dbe964b40bd2cd29b2f33fa8", "current"),
    ev("claims-diff-summary", base + "/claims-v2/manifest-diff-summary.json", "清单差异摘要：+6 行／-1 行（唯一改动行为 §24.127.5 边界句改写）", "document",
       "由上一版清单与本批再生清单逐行身份比对生成", "current"),
    ev("deploy-before", base + "/deploy-target-before.txt", "开工时部署目标读数（x64 1158 文件／bin/Debug 2242 文件及时间戳）", "document",
       "Get-ChildItem -Recurse -File 计数与目录 LastWriteTimeUtc", "current"),
    ev("deploy-final", base + "/deploy-target-final.txt", "全部构建与测试后部署目标读数（与开工逐项一致）", "document",
       "同上口径；两次读数一致", "current"),
    ev("findings", base + "/findings.md", "本批发现、实现、证据、会诊处置与边界", "document", "本批 findings", "current"),
    ev("context", base + "/context.md", "本批目标、范围、依赖顺序、开工状态与子 Agent 评估", "document", "本批 objective", "current"),
    ev("budget", base + "/budget.md", "本批会诊预算、计数与处置纪律", "document", "本批 budget", "current"),
]

tests = [
    {"id": "final-targeted", "path": base + "/final-v7/targeted-final.trx", "expect_success": True},
    {"id": "final-full", "path": base + "/final-v7/assistant-full-final.trx", "expect_success": True},
    {"id": "claims-regen", "path": base + "/claims-v2/claim-regen.trx", "expect_success": True},
    {"id": "claims-noenv", "path": base + "/claims-v2/claim-noenv.trx", "expect_success": True},
    {"id": "red-final", "path": base + "/red-final/bo8-bo9-red-final.trx", "expect_success": False},
]

mutations = json.load(io.open(os.path.join(root, base + "/mutation-records-round2.json"), encoding="utf-8"))
print("mutations:", len(mutations))

rows = json.load(io.open(os.path.join(root, base + "/risk-matrix.json"), encoding="utf-8"))["rows"]
rows = [r for r in rows if r["id"] != "R9-4"]
for r in rows:
    r["counterexample_ids"] = ["final-targeted"] if "final-targeted" in r.get("counterexample_ids", []) else r["counterexample_ids"]
rows.append({
    "id": "R9-4", "dimension": "state", "critical": True, "status": "covered",
    "scenario": "无循环计划中 RecomputeSuccessor 取 candidate 早于 rescue（rescue 为某停驻同轮前插的从未执行出现）时，线性推进到链尾即 null；若链尾重入只补停驻点，rescue 出现永不被驱动。",
    "expected": "链尾按计划全序重建未履行恢复义务（仍存活停驻 + 每个停驻同轮序号更早的从未执行出现）并逐条重驱：candidate→rescue→其停驻→下一轮同构形态全部按 (LoopIteration, SequenceIndex) 升序执行恰好一次，不假成功、不重复提交。",
    "counterexample_ids": ["final-targeted"], "test_ids": [ids["bo9_preinsert"]],
    "mutation_ids": ["bo9-tail-reentry-parks-only-v5"],
    "counterexample_notes": "第 1 轮会诊 IMPORTANT-1 反例；判别力由 bo9-tail-reentry-parks-only-v5（P/F/P，命中 WorkflowRunnerTests.cs:1411）钉死。",
})
matrix = {"schema_version": 1, "batch": batch, "rows": rows}
io.open(os.path.join(root, base + "/risk-matrix.json"), "w", encoding="utf-8").write(json.dumps(matrix, ensure_ascii=False, indent=2) + "\n")

manifest = json.load(io.open(os.path.join(root, base + "/manifest.json"), encoding="utf-8"))
manifest["sources"] = sources
manifest["review_control"] = {
    "existing_results": {"decision": "none",
        "reason": "逐项核对现有交付：BO-6/7 已于 e009068e2 接收（1945913a4／8a014cf78／f47b57b1c），其证据绑定当时的 WorkflowRunner.cs 字节；本批两次改动该文件（初始实现、第 1 轮会诊 IMPORTANT-1 修复）后旧哈希与旧突变绑定均失效，13 项突变已在最终字节上重跑。不存在可复用的同版本回归或突变结果。"},
    "review_round": 2,
    "repair_batch_id": "wave3-bo8-bo9-2026-09-28-review1-repair",
    "prior_findings": [
        {"id": "R1-IMPORTANT-1", "severity": "important", "source_review_evidence_id": "consult-outcome-v1",
         "disposition": "candidate_fixed",
         "repair_evidence_ids": ["final-targeted", "mutation-records", "fixed-v5-targeted", "fixed-v5-full"],
         "note": "链尾重建未履行恢复义务（仍存活停驻 + 同轮前插未执行出现）；新夹具 Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail 与新突变 bo9-tail-reentry-parks-only-v5（P/F/P，命中 :1411）钉死判别力。"},
        {"id": "R1-SUGGESTION-1", "severity": "suggestion", "source_review_evidence_id": "consult-outcome-v1",
         "disposition": "accepted",
         "reason": "D1 后两处注释解释不准确已订正（ParkedRescue 探测口径改为「直接构造该停驻所在轮次的链首出现」；R24 全序说明补「无循环时由链尾重建义务」，删除「自然到达」绝对表述）；另采纳 HasCompletedOutcome 口径表述与终止性收缩论证，并如实登记「夹具均无 scheduled loop」。见 findings.md 与 R5.3 §24.127.4。"},
    ],
    "criticality_reason": "BO-8 R29 与 BO-9 R34 F5 均为已登记 IMPORTANT；推进层同时决定「已完成节点是否被二次提交」（不可撤销外部副作用）与「恢复义务是否被吞/假成功」，故 R8-1／R9-1／R9-2／R9-4 设 critical=true 并要求具名突变。",
    "subagents": {"decision": "not_used",
        "reason": "本批改动集中在 WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor 单一共享状态链，按用户指令不得由多子 Agent 并行改代码；只读子 Agent 的信息与主执行者必须自行完成的代码通读、反例构造、突变与回归重叠，且只读报告不能替代真实 Runner 端到端证据（项目规则明示），故净收益为负。", "tasks": []},
}
manifest["evidence"] = evidence
manifest["tests"] = tests
manifest["comparison"] = {"baseline": base + "/baseline/assistant-full-baseline.trx",
                          "final": base + "/final-v7/assistant-full-final.trx"}
manifest["mutations"] = mutations
manifest["mutation_scope"] = ("13 项独立反向突变全部在本批最终源码字节上执行：8 项为上一批 BO-6／BO-7 突变在新哈希上的重新绑定"
    "（`-v5`；「推进段完成过滤」一项的源码模式随 BO-8 措辞更新，「链尾重入」两项随辅助方法改名与参数改名 rebased），"
    "5 项为本批新增（BO-8 完成过滤；BO-9 链尾重入关闭、重入取最后者、**链尾只重入停驻点**、持久链尾守卫移除）。"
    "覆盖 BO-8 fact、BO-9 反例②、BO-9 反例③（第 1 轮会诊 IMPORTANT-1）、BO-9 持续等待、BO-6 链尾防御与 mixed Runner fact。"
    "**未覆盖**：R9-3（持续等待）未单独设突变，其重入前置与反例②共享 `bo9-tail-reentry-disabled-v5`；BO-6/7-D1 为注释修正，"
    "不产生可突变的行为断言；有循环定义时 `LastScheduledRoundWait` 的全部交错组合、跨进程与实机语义均未覆盖。")
manifest["outside_changes"] = ("本批只改：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、"
    "`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`、"
    "`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt`（按 §17.4-A 强制再生）、"
    "本批 `_workflow/wave3-bo8-bo9/**`（新增证据/突变/会诊材料）、状态文档 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`、"
    "`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`、"
    "`Docs/design/mistletoe-parallel-deliveries.md` 与同名 `.json`。"
    "材料外变更（非本批写入者，本批不触碰、不提交）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、"
    "`Docs/design/unified-job-registry-master-plan.md`（两份既有未提交设计文档），以及工作区其余历史批次证据、"
    "`.bak`／`.stale`、日志、TestResults、DLL／工具输出与截图等未跟踪内容。")
manifest["packet_limit_bytes"] = 524288
manifest["packet"] = [
    {"path": base + "/context.md", "role": "objective"},
    {"path": base + "/findings.md", "role": "findings"},
    {"path": base + "/budget.md", "role": "budget"},
    {"path": base + "/consultation/review-outcome-v1.md", "role": "findings"},
    {"path": "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": added_lines[0], "end_line": added_lines[0],
     "coverage_notes": "再生清单中由 §24.127 新增的载荷声明（代表行之一）。清单是 614 行的生成产物，本批相对上一版 +6 行／-1 行（claims-v2/manifest-diff-summary.json）；再生与清除变量复跑结果由 claims-regen／claims-noenv 两份 TRX 与哈希记录证明，文件全文哈希绑定在所有 current 证据的 source_sha256 中。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": added_lines[2], "end_line": added_lines[2],
     "coverage_notes": "同批新增的第 2 条载荷声明（BO-9 反例③/义务重建句）。摘录只取本批新增行，不删除例外、失败或质疑。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": added_lines[4], "end_line": added_lines[4],
     "coverage_notes": "同批新增的第 3 条载荷声明（反例③正文句）。其余新增行同源同义，完整差异见 claims-v2/manifest-diff-summary.json。"},
    {"path": "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", "role": "source",
     "start_line": r53_start, "end_line": r53_end,
     "coverage_notes": "本批新增的 §24.127 全文（含 §24.127.4 第 1 轮会诊结论与逐项处置）。这是本批在 R5.3 中的全部改动（另有两句 §24.126.5 的日期化更新，原文保留）；文档其余 5000 余行未改动。"},
]
io.open(os.path.join(root, base + "/manifest.json"), "w", encoding="utf-8").write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
print("manifest v2 (round 2) written; evidence", len(evidence), "tests", len(tests), "packet", len(manifest["packet"]), "rows", len(rows))
