from pathlib import Path
import hashlib, json

root = Path('.').resolve()
B = '_workflow/r56-mainline-integration'
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest().lower()
def shu(p): return sha(p).upper()

SOURCES = [
    'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs',
    'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs',
    'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs',
]
cur = {s: sha(s) for s in SOURCES}
print('current source hashes:', json.dumps(cur, ensure_ascii=False, indent=1))

opening = json.loads(Path(B, 'opening.json').read_text(encoding='utf-8'))
pre = opening['source_hashes']  # pre-import hashes
pre_lower = {k: v for k, v in pre.items()}

def ev(i, path, purpose, level, conditions, binding='current', hashes=None):
    e = {"id": i, "path": path, "purpose": purpose, "level": level, "conditions": conditions,
         "binding": binding, "source_sha256": hashes or (cur if binding == 'current' else pre_lower)}
    return e

SD = f'{B}/source-delivery'
MUT = f'{B}/mutations'
mut_records = json.loads(Path(MUT, 'mutation-records.json').read_text(encoding='utf-8'))

evidence = [
    ev('opening-snapshot', f'{B}/opening.json',
       '本批开工快照（分支 main-OldTeaBag-B168、HEAD 8a3ee6c4c、工作区状态、三个源的开工哈希、7 行原始风险矩阵）',
       'document', 'python -B tools/mistletoe/workflow.py begin --manifest …；无产品副作用；此快照早于本批导入',
       'historical', pre_lower),
    ev('intake-equivalence', f'{B}/intake-equivalence.json',
       '接收等价性：唯一测试增量 48/0、blob 相同、工作区 4C101FF3…；25 个材料文件的暂存内容与来源 blob 逐项相等',
       'document', 'git checkout <来源提交> -- <路径> 导入后逐文件哈希比对'),
    ev('intake-line-endings', f'{B}/source-delivery-line-endings.json',
       '摄入材料的行尾形态与来源 worktree 字节对照（25/25 等于来源现行字节；4 个 JSON 在来源侧为 CRLF 工作区形态）',
       'document', '逐文件 SHA-256 与 git blob 比对'),
    ev('intake-note', f'{SD}/INTAKE-NOTE.md',
       '摄入说明：来源 worktree/HEAD、路径映射（为何收纳到 _workflow 下）、逐字节结论、刻意未收录清单、边界',
       'document', '人工编写；映射与哈希由 intake-equivalence.json 支持'),
    ev('src-report-audit', f'{SD}/r56-migration-audit/_r56_parallel/report.md',
       '来源交付 A 报告（含 7 行覆盖矩阵、回归/突变证据、提交清单、主线集成要求）；来源 SHA-256 044A62D0…9D97',
       'document', '逐字节摄入来源提交 c11cb45f6 的该文件',
       'historical', {'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs': sha(f'{SD}/r56-migration-audit/_r56_parallel/report.md'),
                      'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs': cur['MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'],
                      'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs': cur['MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs']}),
    ev('src-acceptance-audit', f'{SD}/r56-migration-audit/_r56_parallel/acceptance.md',
       '来源交付 A 验收单（范围、允许改动、覆盖矩阵要求、回归/突变要求、关闭门禁）',
       'document', '逐字节摄入来源提交 c11cb45f6 的该文件', 'historical'),
    ev('src-mutation-results', f'{SD}/r56-migration-audit/_r56_parallel/reverse-mutation/followup/results.json',
       '来源 4 项断言突变的记录（原始/突变/恢复哈希、命中行、退出码），本批据此做 mutant 哈希交叉核对',
       'document', '逐字节摄入来源提交 c11cb45f6 的该文件', 'historical'),
    ev('src-mutation-source-hashes', f'{SD}/r56-migration-audit/_r56_parallel/reverse-mutation/source-hashes.txt',
       '来源 extra 突变的 before/mutant/restored 哈希（未记录精确 needle）',
       'document', '逐字节摄入来源提交 c11cb45f6 的该文件', 'historical'),
    ev('src-prep-report', f'{SD}/r56-activation-prep/_r56_activation_prep/report.md',
       '来源交付 B 报告：固定基线审计结论、去重核销、正式消费批最小实施顺序、未决事项；来源 SHA-256 BEE48867…4FED',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-acceptance-matrix', f'{SD}/r56-activation-prep/_r56_activation_prep/acceptance-matrix.md',
       '来源交付 B 逐状态验收矩阵（A–F 前置 × 状态/证据/余证/门禁）',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-acceptance', f'{SD}/r56-activation-prep/_r56_activation_prep/acceptance.md',
       '来源交付 B 验收单（准备包与未来正式集成的分层判据）',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-contact-inventory', f'{SD}/r56-activation-prep/_r56_activation_prep/contact-inventory.md',
       '来源交付 B 六项接点清单（条款、定义与调用者、状态、证据能与不能证明的范围）',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-search-audit', f'{SD}/r56-activation-prep/_r56_activation_prep/evidence/search-consumer-audit.md',
       '来源交付 B 有界搜索记录（生产消费者命中/未命中范围与命令）',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-registration', f'{SD}/r56-activation-prep/_r56_activation_prep/delivery-registration.json',
       '来源交付 B 登记侧车（唯一交付 ID、目标批次、A–F 门禁、主线接管说明）；来源 SHA-256 D8AAE0C5…90BF',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('src-prep-manifest', f'{SD}/r56-activation-prep/_workflow/manifest.json',
       '来源交付 B 自己的 v2 manifest（范围、来源、证据、修正历史）',
       'document', '逐字节摄入来源提交 90588159b 的该文件', 'historical'),
    ev('baseline-build-assistant', f'{B}/baseline/assistant-build.log',
       '导入前助手项目 Rebuild（-p:DeployToBgiTools=false，exit 0，59 警告/0 错误）',
       'build', 'dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false', 'historical'),
    ev('baseline-build-testproject', f'{B}/baseline/testproject-build.log',
       '导入前测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）',
       'build', 'dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false', 'historical'),
    ev('baseline-targeted', f'{B}/baseline/targeted-baseline.trx',
       '导入前同条件基线：R56/R58 迁移定向三类 78/78（未导入 theory）',
       'test', 'dotnet test … --no-build --filter R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests|R58MigrationRehearsalHostTests', 'historical'),
    ev('baseline-full', f'{B}/baseline/assistant-full-baseline.trx',
       '导入前同条件基线：助手全量 1567 passed/2 skipped/0 failed/1569',
       'test', 'dotnet test … --no-build（全部用例）', 'historical'),
    ev('final-build-testproject', f'{B}/final/testproject-build.log',
       '导入后测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）',
       'build', 'dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false'),
    ev('targeted-final', f'{B}/final/targeted-final.trx',
       '集成后定向三类 81/81：三行新增 theory（extra/missing/changed）全部 Passed；风险矩阵 S1/C2/F1/F2 的反例证据',
       'test', 'dotnet test … --no-build --filter 三类迁移夹具；exit 0；81 passed'),
    ev('full-final', f'{B}/final/assistant-full-final.trx',
       '集成后助手全量 1570 passed/2 skipped/0 failed/1572（含声明面守卫与全部既有夹具）',
       'test', 'dotnet test … --no-build；exit 0'),
    ev('testid-comparison', f'{B}/testid-comparison.json',
       'testId 差集：定向 78→81（added=3/removed=0/changed=0/unchanged=78）；全量 1569→1572（added=3/removed=0/changed=0/unchanged=1569）',
       'document', 'workflow.compare_trx 逐 testId 对照（同名不折叠）'),
    ev('deploy-target-before', f'{B}/deploy-target-before.json',
       '构建/测试前部署目标目录清单（1158 个文件 + 逐文件 SHA-256 + 清单摘要）',
       'document', '本批全部构建/测试开始前采集', 'historical'),
    ev('deploy-target-after', f'{B}/deploy-target-after.json',
       '构建/测试后部署目标目录清单（与前后对比使用）',
       'document', '本批全部构建/测试之后采集'),
    ev('deploy-target-comparison', f'{B}/deploy-target-comparison.json',
       '部署目标前后清单逐项相同（identical=true，1158→1158）',
       'document', '前后清单摘要对比'),
    ev('claim-manifest-before', f'{B}/claims/manifest-before.txt',
       '声明面再生前清单（618 行），用于评审清单差异',
       'document', 'R5.3 §24.128 追加后、再生前', 'historical'),
    ev('claim-preguard', f'{B}/claims/claim-preguard.trx',
       '声明面守卫在追加 §24.128 后按预期失败（新增/变更 1 行、移除 0 行）——证明本批触及声明面、不得援引措辞类豁免',
       'test', 'dotnet test … --filter FullyQualifiedName~ClaimSurfaceGuardTests（不带 CLAIM_SURFACE_REGENERATE）；exit 1', 'historical'),
    ev('claim-regen', f'{B}/claims/claim-regen.trx',
       '带 CLAIM_SURFACE_REGENERATE=1 再生清单并守卫通过（618→619 行，+1/-0）',
       'test', 'dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0', 'historical'),
    ev('claim-noenv', f'{B}/claims/claim-noenv.trx',
       '清除 CLAIM_SURFACE_REGENERATE 后复跑守卫通过（清单 SHA 28567E75…FF52，619 行）',
       'test', 'dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0；绑定 §24.128.1–.5 的文档状态（§24.128.6 追加后将再次再生复跑）'),
    ev('claims-diff', f'{B}/claims/claims-diff.json',
       '声明面清单差异摘要（新增/移除行数与清单 SHA 前后值）',
       'document', 'man anifest-before/after 逐行对照'),
    ev('mutation-records', f'{MUT}/mutation-records.json',
       '本批 5 项反向突变的机读记录（原/突变/恢复哈希、三段 TRX、具名 testId、命中断言行、退出码）',
       'document', '由 mutations/run-mutations.py 依来源突变定义重跑并落盘'),
    ev('mutation-crosscheck', f'{MUT}/mutation-hash-crosscheck.json',
       '与来源 records.json/source-hashes.txt 的 mutant 哈希交叉核对（4/5 CRLF 形态完全相同；extra 为等价独立突变）',
       'document', '逐突变 CRLF/LF 形态哈希比对'),
    ev('mutation-runner', f'{MUT}/run-mutations.py',
       '本批突变执行脚本（needle/anchor/断言行/恢复校验），供复查复现',
       'document', 'Python；不改产品源码之外的任何内容，逐次 finally 恢复'),
]
# per-mutation evidence
for r in mut_records:
    mid = r['id']
    evidence.append({"id": f"mut-{mid}", "path": r['mutant_trx'],
                     "purpose": f"反向突变 {mid} 的 mutant TRX：目标 theory 行 Failed（命中 {r['assertion_contains']}）",
                     "level": "test", "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
                     "binding": "historical", "source_sha256": {SOURCES[0]: cur[SOURCES[0]],
                                                                 SOURCES[1]: r['mutant_sha256'].lower(), SOURCES[2]: cur[SOURCES[2]]}})
    evidence.append({"id": f"restored-{mid}", "path": r['restored_trx'],
                     "purpose": f"反向突变 {mid} 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
                     "level": "test", "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…", "binding": "current",
                     "source_sha256": cur})
    evidence.append({"id": f"mut-{mid}-log", "path": r['build_log'],
                     "purpose": f"反向突变 {mid} 的执行日志（构建与测试输出、退出码）",
                     "level": "document", "conditions": "dotnet test 全量输出重定向", "binding": "historical",
                     "source_sha256": {SOURCES[0]: cur[SOURCES[0]], SOURCES[1]: r['mutant_sha256'].lower(), SOURCES[2]: cur[SOURCES[2]]}})

tests = ([{"path": f'{B}/final/targeted-final.trx', "expect_success": True},
          {"path": f'{B}/final/assistant-full-final.trx', "expect_success": True},
          {"path": f'{B}/claims/claim-noenv.trx', "expect_success": True}] +
         [{"path": r['restored_trx'], "expect_success": True} for r in mut_records] +
         [{"path": r['mutant_trx'], "expect_success": False} for r in mut_records])

mutations = []
for r in mut_records:
    mutations.append({
        "id": r['id'], "description": r['mutation_label'], "source": r['source'],
        "original_sha256": r['original_sha256'], "mutant_sha256": r['mutant_sha256'],
        "restored_sha256": r['restored_sha256'], "baseline_trx": r['baseline_trx'],
        "mutant_trx": r['mutant_trx'], "restored_trx": r['restored_trx'],
        "target_test_id": r['target_test_id'], "target_name": r['target_name'],
        "failure_contains": r['failure_contains'], "assertion_contains": r['assertion_contains'],
        "build_log": r['build_log'], "build_exit": 0, "baseline_exit": 0,
        "mutant_exit": r['mutant_exit'], "restored_exit": r['restored_exit'],
        "assertion_message": r['assertion_message'],
        "delivery_recorded_mutant_hash_crlf": r.get('delivery_recorded_mutant_hash'),
    })

IDS = [r['target_test_id'] for r in mut_records]
theory_ids = ['b1efc0c6-a858-0d3a-f1ea-1cf252267ba9', '7481b2d1-35ca-c66b-9954-00d698d9ff2d',
              'bb416257-70f3-d03a-4df9-51f86d37131c']
extra_id, changed_id, missing_id = theory_ids[0], theory_ids[1], theory_ids[2]

matrix = json.loads(Path(B, 'risk-matrix.json').read_text(encoding='utf-8'))
notes = {
    'INT-S1': '导入等价性由 intake-equivalence.json 证明（blob 相同、工作区 4C101FF3…）；三行 theory 的 testId 与来源 results.json 记录一致。',
    'INT-S2': '本行 expected 的绝对值按上一子批散文预登记（1566/1568→1569/1571），实测为 1567/1569→1570/1572（+1）。差额已定位为上一子批散文漏记第 2 轮会诊新增夹具（见 findings.md F-1/F-2 与 R5.3 §24.128.4），非本批导入所致；本行的实体主张（导入只新增 theory、既有成员不变）由 testid-comparison.json 证明：added=3/removed=0/changed=0。冻结行不改写。',
    'INT-C1': '5 项突变在集成字节上重跑；4/5 的 mutant SHA-256（CRLF 形态）与来源记录完全相同，extra 为语义等价独立文本突变。',
    'INT-C2': '同一源码版本上重复执行定向集合：baseline 78/78 与 final 81/81 均为 0 失败；导入后全量 1570/2/0/1572。',
    'INT-F1': '三情形断言 expectedReason + Commit 拒绝 + Stage=Activated + CommitMarker=null；commit-refusal 突变专门放宽 Commit 门，命中 :line 190。',
    'INT-F2': 'production-gate 突变在授权失败时仍执行动作，命中 :line 196 的执行计数断言。',
}
for row in matrix['rows']:
    rid = row['id']
    if rid == 'INT-F3':
        row['status'] = 'not_applicable'
        row['reason'] = ('本批无产品行为或构建配置改动，"部署目标被写入"不是本批引入的行为分支；该风险以构建参数 '
                         '-p:DeployToBgiTools=false 与部署目标前后清单（deploy-target-before/after/comparison.json，level=document）'
                         '机械登记，不适用 test/runtime 反例。前后清单摘要相同（1158 文件）。')
        continue
    row['status'] = 'covered'
    row['note'] = notes[rid]
    if rid in ('INT-S1', 'INT-S2', 'INT-C2', 'INT-F1', 'INT-F2'):
        row['counterexample_ids'] = ['targeted-final'] if rid != 'INT-S2' else ['full-final']
    if rid == 'INT-S1':
        row['test_ids'] = theory_ids
        row['mutation_ids'] = ['extra', 'missing', 'changed']
    elif rid == 'INT-S2':
        row['test_ids'] = theory_ids
    elif rid == 'INT-C1':
        row['counterexample_ids'] = ['restored-missing', 'restored-changed', 'restored-extra']
        row['test_ids'] = theory_ids
        row['mutation_ids'] = ['extra', 'missing', 'changed', 'commit-refusal', 'production-gate']
    elif rid == 'INT-C2':
        row['test_ids'] = theory_ids
    elif rid == 'INT-F1':
        row['test_ids'] = theory_ids
        row['mutation_ids'] = ['extra', 'missing', 'changed', 'commit-refusal']
    elif rid == 'INT-F2':
        row['test_ids'] = [changed_id]
        row['mutation_ids'] = ['production-gate']

Path(B, 'risk-matrix.json').write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')

r53 = Path('Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md')
lines = r53.read_text(encoding='utf-8').splitlines()
start = next(i for i, l in enumerate(lines) if l.startswith('## §24.128')) + 1
end = len(lines)
claim = Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt')
clines = claim.read_text(encoding='utf-8').splitlines()
_before_set = set(Path(B, 'claims/manifest-before.txt').read_text(encoding='utf-8').splitlines())
added_claim_lines = [i + 1 for i, l in enumerate(clines) if l not in _before_set]
added_claim_line = added_claim_lines[0] if added_claim_lines else 1

outside = ('本批只改：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`（+48 行，逐字节导入）、'
           '`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt`（按 §17.4-A 强制再生）、'
           '本批证据目录 `_workflow/r56-mainline-integration/**`（含 `source-delivery/**` 摄入副本）、'
           '状态文档 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`、`槲寄生调度器总计划.md`、`_batch21/b21_plan.md`、'
           '`_batch21/sb21-4-handoff-2026-09-28.md`、`Docs/design/mistletoe-parallel-deliveries.md` 与同名 `.json`。'
           '材料外变更（非本批写入者，本批不触碰、不提交）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、'
           '`Docs/design/unified-job-registry-master-plan.md`（两份既有未提交设计文档），以及工作区其余历史批次证据、`.bak`／`.stale`、'
           '日志、TestResults、DLL／工具输出与截图等未跟踪内容。')

manifest = {
    "schema_version": 2,
    "batch": "r56-mainline-integration-2026-09-28",
    "mode": "code",
    "sources": SOURCES,
    "risk_matrix": f'{B}/risk-matrix.json',
    "opening_snapshot": f'{B}/opening.json',
    "review_control": {
        "existing_results": {
            "decision": "none",
            "reason": ("逐项核对现有交付：来源组件交付 c11cb45f6 的 81/81 与助手 1482/2/0/1484 绑定隔离 worktree 的开工字节（测试源为交付版、产品源为旧主线字节），"
                       "不是主线集成 HEAD 的结果；§24.126/§24.127 的主线回归与突变绑定 WorkflowRunner 相关字节，与 R5.6 迁移组件无关；"
                       "来源的 extra 突变未记录精确 needle，其 mutant 无法复用。因此无可复用的同版本集成回归或突变结果，本批在主线上重新执行。")
        },
        "review_round": 1,
        "prior_findings": [],
        "criticality_reason": ("INT-S1/INT-C1/INT-F1/INT-F2 设 critical：它们决定「导入是否等价」「关键断言是否仍有判别力」「损坏快照能否被提交」与「未授权是否仍零执行」，"
                               "其中后者涉及不可撤销副作用（提交/执行）与失败闭锁，故要求具名突变绑定。"),
        "subagents": {"decision": "not_used", "reason": "待补：子 Agent 报告返回后更新。", "tasks": []},
    },
    "evidence": evidence,
    "tests": tests,
    "mutation_scope": ("2 项来源交付的关键断言在集成字节上逐项重跑（来源共 5 项：extra + results.json 的 4 项）；未新增突变，未覆盖："
                       "本地等待/停驻链（BO 系列，属其他批次）、生产接线路径（本批不施工）、跨进程/断电耐久（组件与设施均不证明）。"),
    "mutations": mutations,
    "comparison": {"baseline": f'{B}/baseline/assistant-full-baseline.trx', "final": f'{B}/final/assistant-full-final.trx'},
    "outside_changes": outside,
    "packet_limit_bytes": 524288,
    "packet": [
        {"path": f'{B}/context.md', "role": "objective"},
        {"path": f'{B}/findings.md', "role": "findings"},
        {"path": f'{B}/budget.md', "role": "budget"},
        {"path": f'{B}/intake-equivalence.json', "role": "findings"},
        {"path": r53.as_posix(), "role": "source", "start_line": start, "end_line": end,
         "coverage_notes": f"本批在 R5.3 中的全部改动＝新增 §24.128（{end - start + 1} 行，含范围/接收方式/集成回归/材料漂移登记/会诊与边界）；文档其余 5000 余行未改动。"},
        {"path": claim.as_posix(), "role": "source",
         "start_line": added_claim_line,
         "end_line": added_claim_line,
         "coverage_notes": f"本轮再生新增的唯一声明行（清单 {len(clines)} 行，618→619，+1/-0）；清单全文哈希随所有 current 证据的 source_sha256 与 claims-diff.json 绑定。"},
        {"path": SOURCES[0], "role": "source"},
        {"path": SOURCES[1], "role": "source"},
        {"path": SOURCES[2], "role": "source"},
    ],
}
# claims diff summary
before = Path(B, 'claims/manifest-before.txt').read_text(encoding='utf-8').splitlines()
after = clines
diff = {"before_lines": len(before), "after_lines": len(after), "added": len(after) - len(before),
        "before_sha256": shu(Path(B, 'claims/manifest-before.txt')), "after_sha256": shu(claim),
        "added_lines": [l.split('\u0001')[-1] for l in after if l not in set(before)][:5]}
Path(B, 'claims/claims-diff.json').write_text(json.dumps(diff, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
Path(B, 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
print('manifest written: evidence=', len(evidence), 'tests=', len(tests), 'mutations=', len(mutations))
print('claim diff:', json.dumps(diff, ensure_ascii=False)[:300])
