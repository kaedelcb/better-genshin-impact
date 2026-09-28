import json, pathlib, hashlib, subprocess
root = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W = root/"_workflow/r56-reference-activation-wiring-2026-09-29"
mp = W/"manifest.json"
m = json.loads(mp.read_text(encoding="utf-8-sig"))

def sha(rel): return hashlib.sha256((root/rel).read_bytes()).hexdigest()
sources = m["sources"]
src_hashes = {s: sha(s) for s in sources}
head = subprocess.run(["git","-C",str(root),"rev-parse","HEAD"],capture_output=True,text=True).stdout.strip()

def ev(eid, rel, purpose, level, conditions, binding="current", src=None):
    e = {"id": eid, "path": rel, "purpose": purpose, "level": level, "conditions": conditions,
         "binding": binding, "source_sha256": src if src is not None else src_hashes}
    return e

m["evidence"] = [
  ev("baseline-targeted","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx",
     "开工同条件定向基线（R56/R58 迁移夹具 70/70）","test",
     "独立 baseline worktree @ 22ccd6ee2；dotnet test --filter R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests；-p:DeployToBgiTools=false",
     "historical", {}),
  ev("baseline-assistant-full","_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
     "开工同条件助手全量基线（1570/2/0/1572）","test",
     "独立 baseline worktree @ 22ccd6ee2；dotnet test 全量；-p:DeployToBgiTools=false","historical", {}),
  ev("targeted-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx",
     "本批定向回归（99/99，含 29 条新夹具）；矩阵各行反例证据","test",
     "主工作区最终源码；dotnet test --filter R56|R58|R56ReferenceActivationWiring；-p:DeployToBgiTools=false；build exit 0"),
  ev("assistant-full-final-frozen","_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx",
     "本批助手全量回归（1599/2/0/1601）","test",
     "主工作区最终源码；dotnet test 全量；-p:DeployToBgiTools=false；build exit 0"),
  ev("subagent-readonly-audit","_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md",
     "固定开工 ref 的只读子 Agent 独立核查（跨程序集可达性/写方消费方/写集与回滚归属）","consult",
     "只读、零写入；固定 ref 22ccd6ee2；主执行者按最终源码复核","historical", {}),
  ev("closing-baseline-mutation-notes","_workflow/r56-reference-activation-wiring-2026-09-29/findings.md",
     "现场审计发现（含跨程序集可达性与旧语义缺口）","document","开工只读审计，绑定开工 HEAD","historical", {}),
]
for mid in ["M1-no-readback-confirmation","M2-no-cross-check-with-change-registry","M3-no-outside-write-detection",
            "M4-no-activation-target-membership","M5-no-commit-recheck","M6-no-stage-mark-gate",
            "M7-no-activation-writeset-hash-sync","M8-no-reference-gate-for-activation",
            "M9-fail-open-on-non-success","M10-no-production-commit-gate","M11-no-lock-guard-on-real-effects",
            "M12-stage-persisted-before-effect","M13-no-activation-readback","M14-no-real-evidence-invariants",
            "M15-no-reference-idempotent-recheck","M16-no-activation-idempotent-recheck",
            "M17-writeset-evidence-not-persisted"]:
    m["evidence"].append(ev("mut-"+mid, f"_workflow/r56-reference-activation-wiring-2026-09-29/mutations/{mid}/result.json",
        "突变记录（含 baseline/mutant/restored 判定与源码哈希）","component","主工作区；构建+定向测试三段执行","historical", {}))
m["evidence"].append(ev("testid-comparison","_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json",
    "逐 testId 差集（added=29/removed=0/changed=0/unchanged=1572）","component","主工作区最终源码","historical", {}))
m["evidence"].append(ev("deploy-target-post","_workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json",
    "部署目标事后清单（1158 文件；最新写入时间早于本批）","component","Get-FileHash 清单；所有构建带 -p:DeployToBgiTools=false","historical", {}))

m["tests"] = [
  {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx","expect_success":True},
  {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx","expect_success":True},
]
m["comparison"] = {"baseline":"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
                   "final":"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx"}
m["mutations"] = json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
m["mutation_scope"] = ("已覆盖：真实写入前/后的读回顺序（M1、M13）、写集与变更登记交叉核对（M2）、写集外改动（M3）、"
  "激活目标写集归属（M4）、提交前读回复核（M5）、注入端口后旧阶段标记入口门禁（M6）、激活后写集哈希同步（M7）、"
  "激活的阶段前置（M8）、非成功结果的 fail-open（M9）、未提交生产闸门（M10）、真实入口持锁（M11）、副作用与阶段持久化顺序（M12）、"
  "真实证据结构不变量（M14）、双向幂等复核（M15、M16）、写集证据持久化（M17）。"
  "未覆盖：真实 User 目录与真实入口、跨进程崩溃（本批只做同进程故障注入）、W4 跨程序集绑定、"
  "目标机路径身份（UNC/映射盘/SUBST/8.3）、助手元数据恢复（B 项）。M3、M5、M14 为整块削弱型突变（同一位点多层独立复核各自判别）。")
m["outside_changes"] = ("本批文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`（改）、"
  "`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`（新增）、"
  "`Test/.../R56ReferenceActivationWiringTests.cs`（新增）、`Test/.../ClaimSurfaceManifest.txt`（再生 +4/-0）、"
  "R5.3 §24.129、`槲寄生调度器总计划.md` 进度条目、`Docs/design/mistletoe-parallel-deliveries.md` 进展段、本批 `_workflow/**`。"
  "材料外变更：工作区其余历史批次证据、`.bak`/`.stale`/`TestResults`/日志/DLL、两份既有未提交设计文档"
  "（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）——不进入提交，也不假定写者归属。")
m["review_control"]["subagents"] = {
  "decision":"used",
  "reason":"固定开工 ref 的 1 个只读子 Agent 独立枚举跨程序集可达性、写方/消费方与写集回滚归属，用于交叉核对主执行者审计；核心状态链仍由主执行者单写。",
  "tasks":[{"question":"固定 ref 上只读枚举：①配置根引用文件的真实写方与消费方（含跨程序集边界）；②candidate-ready→active 的承载字段与全部消费点及是否存在写入路径；③事务写集在回滚路径中的用法与是否存在写新内容的步骤。",
            "read_only":True,"fixed_ref":"22ccd6ee2721abb1642535253997c57dbf9020d6",
            "opening_source_hashes":{s: hashlib.sha256((root/s).read_bytes()).hexdigest() for s in [
              "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs"]},
            "report_evidence_id":"subagent-readonly-audit"}]}
mp.write_text(json.dumps(m, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
(W/"final-source-hashes.json").write_text(json.dumps({"head":head,"source_sha256":src_hashes},ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("manifest assembled; head=", head, "; sources:", len(sources), "; evidence:", len(m["evidence"]), "; mutations:", len(m["mutations"]))
