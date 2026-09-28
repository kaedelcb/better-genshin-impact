import io, json, hashlib, os, xml.etree.ElementTree as ET
root = r"E:\Program Files\better-genshin-impact-LCB"
base = r"_workflow/wave3-bo8-bo9"
batch = "wave3-bo8-bo9-2026-09-28"
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

def sha(p):
    return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()

sources = [
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs",
]
cur = {s: sha(s) for s in sources}
print("current source hashes:", json.dumps(cur, indent=1))

# testIds from the final targeted TRX
trx = os.path.join(base, "final-v3/targeted-final.trx")
troot = ET.parse(os.path.join(root, trx)).getroot()
byid = {}
for u in troot.iter(NS + "UnitTest"):
    byid[u.get("id")] = u.get("name")
def tid(name):
    hits = [k for k, v in byid.items() if v.endswith("." + name)]
    assert len(hits) == 1, (name, hits)
    return hits[0]

ids = {
    "bo8": tid("RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted"),
    "bo9_multi": tid("Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder"),
    "bo9_wait": tid("Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess"),
    "mixed": tid("Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences"),
    "tail": tid("Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion"),
}
print(json.dumps(ids, indent=1))

old = {"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs":
       "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
       "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs":
       "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711"}
red_v1 = dict(old, **{"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs":
                      "409844e01336298ff05cacb592179b42fe0452850aba210ddea2447a77649c7d"})
red_final = dict(old, **{"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs":
                         "eef5b4eb3651bbe352cd5370317ff1e3b02bf015816f6cf3a4a13aaf204bbece"})

def ev(i, path, purpose, level, conditions, binding="current", hashes=None):
    item = {"id": i, "path": path, "purpose": purpose, "level": level, "conditions": conditions,
            "binding": binding, "source_sha256": hashes or cur}
    return item

evidence = [
    ev("opening-snapshot", base + "/opening.json", "本批开工快照（HEAD、分支、工作区状态、三个源的开工哈希与原始风险矩阵行）", "document",
       "python -B tools/mistletoe/workflow.py begin（无产品副作用）", "historical", old),
    ev("baseline-build", base + "/baseline-build.log", "开工字节测试项目 Rebuild（同条件基线前提）", "build",
       "dotnet build Test/MultiplayerHoeingAssistant.UnitTest -t:Rebuild -p:DeployToBgiTools=false；exit 0；80 warnings／0 errors", "historical", old),
    ev("baseline-targeted", base + "/baseline/targeted-baseline.trx", "同条件开工基线定向三类 93/93（仅对照，不作当前回归证明）", "test",
       "dotnet test … --no-build --filter LocalWaitParkingStateContractTests|LocalWaitIdentityTranslationTests|WorkflowRunnerTests；93 passed", "historical", old),
    ev("baseline-full", base + "/baseline/assistant-full-baseline.trx", "同条件开工基线助手全量 1562 passed／2 skipped／0 failed／1564（testId 对照基线）", "test",
       "dotnet test … --no-build（Rebuild -p:DeployToBgiTools=false 后）；exit 0", "historical", old),
    ev("red-prefix-source", base + "/red-final/prefix-source.cs", "红运行使用的**开工字节**固定件（SHA-256 等于 opening.json 的 WorkflowRunner.cs 开工哈希）", "document",
       "git show HEAD:<path> 的 LF blob 转工作区 CRLF 形态；哈希核对通过；运行后已逐字节恢复最终字节", "historical", old),
    ev("red-final-build", base + "/red-final/testproject-build.log", "红运行前的测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0（prefix_build_exit=0）", "historical", red_final),
    ev("red-final", base + "/red-final/bo8-bo9-red-final.trx", "反例先行红证据：开工字节 + 本批最终夹具，3 failed／0 passed／3 total", "test",
       "dotnet test … --filter 三个新夹具；exit 1；BO-8 实测提交 [n3,n2,X,n3,Y]；BO-9 两个夹具均只提交 [(A,1)]", "historical", red_final),
    ev("red-final-exits", base + "/red-final/red-final-exits.json", "红运行退出码记录（构建 0、测试 1）与源码恢复核对", "document",
       "脚本 run-red-final.ps1 落盘；恢复后源码哈希 539dfe39…", "historical", red_final),
    ev("red-v1", base + "/red/bo8-bo9-red.trx", "早期红运行（BO-9 夹具当时为 2 停驻版本，后被扩展为 3 停驻；仅留存历史，最终红证据见 red-final）", "test",
       "dotnet test … --filter 三个夹具；3 failed；夹具版本已被 red-final 取代", "historical", red_v1),
    ev("red-v1-build", base + "/red/testproject-build.log", "早期红运行的测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "historical", red_v1),
    ev("fixed-build", base + "/fixed-v2/testproject-build.log", "中间版本（最终夹具，文档/声明面未更新）测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0", "current"),
    ev("fixed-targeted", base + "/fixed-v2/targeted-fixed.trx", "中间版本定向三类 96/96", "test",
       "dotnet test … --filter 三类；96 passed", "current"),
    ev("fixed-full", base + "/fixed-v2/assistant-full-fixed.trx", "中间版本助手全量 1565 passed／2 skipped／0 failed／1567", "test",
       "dotnet test … --no-build；exit 0", "current"),
    ev("final-build", base + "/final-v3/testproject-build.log", "最终版本（含 R5.3 §24.127 与声明面再生）测试项目 Rebuild", "build",
       "dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；0 error", "current"),
    ev("final-targeted", base + "/final-v3/targeted-final.trx", "最终定向三类 96/96（风险矩阵与关键断言的行级 test 证据）", "test",
       "dotnet test … --filter LocalWaitParkingStateContractTests|LocalWaitIdentityTranslationTests|WorkflowRunnerTests；exit 0", "current"),
    ev("final-full", base + "/final-v3/assistant-full-final.trx", "最终助手全量 1565 passed／2 skipped／0 failed／1567", "test",
       "dotnet test … --no-build；exit 0；含 ClaimSurfaceGuardTests", "current"),
    ev("final-testid-comparison", base + "/final-v3/testid-comparison.json", "基线→最终逐名 testId 对照：1564 unchanged／3 added／0 removed／0 changed", "component",
       "由 baseline/assistant-full-baseline.trx 与 final-v3/assistant-full-final.trx 解析生成", "current"),
    ev("mutation-records", base + "/mutation-records-final.json", "12 项反向突变的机读记录（三阶段退出码、目标 testId/名称、断言行、TRX 路径、源码哈希）", "component",
       "由 run-mutants.ps1 生成；每项 baseline/mutant/restored 独立 TRX 与日志", "current"),
    ev("mutation-runner", base + "/run-mutants.ps1", "反向突变执行脚本（逐项独立构建/测试、源码逐字节恢复、失败断言行提取）", "document",
       "pwsh -NoProfile -File run-mutants.ps1；ALL MUTATIONS COMPLETE count=12", "current"),
    ev("mutation-run-log", base + "/mutations-run-final.log", "突变运行日志（逐项 PASS 行与最终 original 哈希）", "document",
       "同一脚本 stdout；original=539dfe39…", "current"),
    ev("claim-regen", base + "/claims/claim-regen.trx", "声明面再生（CLAIM_SURFACE_REGENERATE=1）后守卫通过", "test",
       "dotnet test … --filter ClaimSurfaceGuardTests（带环境变量）；1 passed", "current"),
    ev("claim-noenv", base + "/claims/claim-noenv.trx", "清除环境变量后复跑守卫通过（SHA 稳定）", "test",
       "dotnet test … --filter ClaimSurfaceGuardTests（无环境变量）；1 passed；清单 SHA 前后一致", "current"),
    ev("claim-before-hash", base + "/claims/manifest-before.sha256", "再生前清单哈希与行数", "document",
       "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267／602 行", "current"),
    ev("claim-after-regen-hash", base + "/claims/manifest-after-regen.sha256", "再生后清单哈希", "document",
       "84e90f99bddd36fe3e0193cf33da758cac33ad103da8d36a1c3bf91dca703434／609 行", "current"),
    ev("claim-after-noenv-hash", base + "/claims/manifest-after-noenv.sha256", "清除变量后清单哈希（与再生一致）", "document",
       "84e90f99bddd36fe3e0193cf33da758cac33ad103da8d36a1c3bf91dca703434", "current"),
    ev("claim-diff-summary", base + "/claims/manifest-diff-summary.json", "清单差异摘要：+7 行／-0 行，仅新增 §24.127 承载声明关键词的行", "document",
       "由 HEAD 版本清单与本批再生清单逐行身份比对生成", "current"),
    ev("deploy-before", base + "/deploy-target-before.txt", "开工时部署目标读数（x64 1158 文件／bin/Debug 2242 文件及其时间戳）", "document",
       "Get-ChildItem -Recurse -File 计数与目录 LastWriteTimeUtc", "current"),
    ev("deploy-final", base + "/deploy-target-final.txt", "全部构建与测试后部署目标读数（与开工逐项一致，未被写入）", "document",
       "同上口径；两次读数一致", "current"),
    ev("findings", base + "/findings.md", "本批发现、实现、证据、边界与逐项处置", "document", "本批 findings", "current"),
    ev("context", base + "/context.md", "本批目标、范围、依赖顺序、开工状态与子 Agent 评估", "document", "本批 objective", "current"),
    ev("budget", base + "/budget.md", "本批会诊预算与处置纪律", "document", "本批 budget", "current"),
    ev("consult-plan", base + "/consultation/review-request-v1.md", "第 1 轮会诊请求（范围、材料、请回答项与边界）", "consult", "发往既有 GPT 会诊工具的请求文本", "current"),
]

tests = [
    {"id": "final-targeted", "path": base + "/final-v3/targeted-final.trx", "expect_success": True},
    {"id": "final-full", "path": base + "/final-v3/assistant-full-final.trx", "expect_success": True},
    {"id": "claim-regen", "path": base + "/claims/claim-regen.trx", "expect_success": True},
    {"id": "claim-noenv", "path": base + "/claims/claim-noenv.trx", "expect_success": True},
    {"id": "red-final", "path": base + "/red-final/bo8-bo9-red-final.trx", "expect_success": False},
]

mutations = json.load(io.open(os.path.join(root, base + "/mutation-records-final.json"), encoding="utf-8"))

matrix = {"schema_version": 1, "batch": batch, "rows": [
    {"id": "R8-1", "dimension": "state", "critical": True, "status": "covered",
     "scenario": "驱动中到达节点边界时新修订把已完成出现重排到恢复点之后（无停驻历史）：老计划 [n3,n2,Y] 已跑完 n3/n2，修订为 [n2,X,n3,Y]，RecomputeSuccessor 返回 X。",
     "expected": "推进段按稳定出现身份过滤已完成项：n3 只被提交一次，X 与 Y 各提交一次，运行按真实链尾收敛，不重复外部副作用。",
     "counterexample_ids": ["red-final", "final-targeted"], "test_ids": [ids["bo8"]],
     "mutation_ids": ["bo8-completion-filter-v5"]},
    {"id": "R8-2", "dimension": "state", "critical": False, "status": "covered",
     "scenario": "同一推进层中恢复点自身是未完成停驻（waitLocally），且其后方存在已完成锚（BO-6/BO-7 已闭合形态）。",
     "expected": "完成过滤不得把停驻标记当完成：停驻义务仍被重驱，已完成锚不被重提（BO-6 语义在该层保持不变）。",
     "counterexample_ids": ["final-targeted"], "test_ids": [ids["mixed"]]},
    {"id": "R9-1", "dimension": "concurrency", "critical": True, "status": "covered",
     "scenario": "同一 run 存在多个仍有效停驻且分处不同轮次（如 A@loop1/A@loop2/A@loop3），当前修订已无循环定义 ⇒ 线性推进只能到达其中最早者，较晚者不在 Next 链上。",
     "expected": "推进层在真实链尾处按计划全序重入到仍存活的停驻义务：每个存活停驻都被驱动恰好一次、按 (LoopIteration, SequenceIndex) 升序、无假成功、无跳步；重入严格消耗义务（可终止）。",
     "counterexample_ids": ["red-final", "final-targeted"], "test_ids": [ids["bo9_multi"]],
     "mutation_ids": ["bo9-tail-reentry-disabled-v5", "bo9-reentry-order-last-v5"]},
    {"id": "R9-2", "dimension": "fault", "critical": True, "status": "covered",
     "scenario": "持久记录已落盘 TailReached 且仍存可定位未完成的零发送停驻义务（BO-6 防御形态）。",
     "expected": "入口即链尾的持久不一致不得被重入重新打开：不提交任何节点、不执行收尾、聚合 Failed，停驻义务保留。",
     "counterexample_ids": ["final-targeted"], "test_ids": [ids["tail"]],
     "mutation_ids": ["bo9-persisted-tail-reopen-v5", "bo6-tail-fail-closed-v5"]},
    {"id": "R9-3", "dimension": "fault", "critical": False, "status": "covered",
     "scenario": "重入驱动的停驻在重驱后仍要求本地等待（持续有效停驻）。",
     "expected": "运行回到 LocalWaitParking、零发送、不终态化、不触发收尾；义务标记保持可定位且未完成。",
     "counterexample_ids": ["final-targeted"], "test_ids": [ids["bo9_wait"]]},
]}

io.open(os.path.join(root, base + "/risk-matrix.json"), "w", encoding="utf-8").write(
    json.dumps(matrix, ensure_ascii=False, indent=2) + "\n")

manifest = json.load(io.open(os.path.join(root, base + "/manifest.json"), encoding="utf-8"))
manifest["sources"] = sources
manifest["review_control"] = {
    "existing_results": {"decision": "none",
        "reason": "逐项核对现有交付：BO-6/7 已于 e009068e2 接收（1945913a4／8a014cf78／f47b57b1c），其证据绑定当时的 WorkflowRunner.cs 字节；本批改动该文件后旧哈希与 8 项突变绑定失效（8 项已在新字节上重做）。不存在可复用的同版本回归或突变结果。"},
    "review_round": 1,
    "prior_findings": [],
    "criticality_reason": "BO-8 R29 与 BO-9 R34 F5 均为已登记 IMPORTANT；推进层同时决定「已完成节点是否被二次提交」（不可撤销外部副作用）与「停驻义务是否被吞」，故 R8-1／R9-1／R9-2 设 critical=true，并要求具名突变。",
    "subagents": {"decision": "not_used",
        "reason": "本批改动集中在 WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor 单一共享状态链，按用户指令不得由多子 Agent 并行改代码；只读子 Agent 的信息与主执行者必须自行完成的代码通读、反例构造、突变与回归重叠，且只读报告不能替代真实 Runner 端到端证据（项目规则明示），故净收益为负。", "tasks": []},
}
manifest["evidence"] = evidence
manifest["tests"] = tests
manifest["comparison"] = {"baseline": base + "/baseline/assistant-full-baseline.trx",
                          "final": base + "/final-v3/assistant-full-final.trx"}
manifest["mutations"] = mutations
manifest["mutation_scope"] = ("12 项独立反向突变全部在本批最终字节上重做：8 项为上一批 BO-6／BO-7 突变在新哈希上的重新绑定"
    "（`-v5`；其中「推进段完成过滤」一项的源码模式随 BO-8 措辞更新，旧模式已不存在），4 项为本批新增（BO-8 完成过滤、"
    "BO-9 链尾重入关闭、BO-9 重入取最后者、BO-9 持久链尾守卫移除）。覆盖 BO-8 fact、BO-9 全序 fact、BO-9 持续等待 fact、"
    "BO-6 链尾防御 fact 与 mixed Runner fact。**未覆盖**：BO-9 持续等待 fact（R9-3）未单独设突变（其关键断言是状态与零收尾，"
    "不构成独立失败面；其重入前置与 R9-1 共享 `bo9-tail-reentry-disabled-v5`）；BO-6/7-D1 为注释修正，无新增断言，"
    "其影响通过上述 12 项在新字节上重跑体现。突变不覆盖实机、跨进程或并发压力语义。")
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
    {"path": "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs", "role": "source"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": 292, "end_line": 292,
     "coverage_notes": "再生清单中由 §24.127 新增的第 1 条载荷声明（生成文件的代表性摘录之一）。清单是 609 行的生成产物，本批共新增 7 行、删除 0 行（claim-diff-summary.json）；全文再生结果由 claim-regen／claim-noenv 两份 TRX 与哈希记录证明，文件全文哈希绑定在所有 current 证据的 source_sha256 中。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": 314, "end_line": 314,
     "coverage_notes": "再生清单新增的第 2 条载荷声明（BO-8 反例句）。摘录只取本批新增行，不删除例外或失败；完整差异见 claims/manifest-diff-summary.json。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": 374, "end_line": 374,
     "coverage_notes": "再生清单新增的第 3 条载荷声明（§24.127.1 范围句）。其余 4 条新增行同源同义，均来自本批 R5.3 §24.127 正文。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
     "start_line": 434, "end_line": 434,
     "coverage_notes": "再生清单新增的第 4 条载荷声明（BO-9 反例句）。摘录范围以生成文件为准，不代表其余新增行被删除。"},
    {"path": "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", "role": "source",
     "start_line": 5041, "end_line": 5208,
     "coverage_notes": "本批新增的 §24.127 全文与对 §24.126.5 两条陈旧状态句的日期化更新（原文保留）。这是本批在 R5.3 中的全部改动；文档其余 5000 余行未改动。"},
]
io.open(os.path.join(root, base + "/manifest.json"), "w", encoding="utf-8").write(
    json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
print("manifest written")
