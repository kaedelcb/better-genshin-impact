# 槲寄生施工辅助设施

执行者使用，owner 无需逐批导入或手动运行。权威流程与适用范围见
[后续施工接入计划](../../Docs/design/mistletoe-workflow-facilities.md)。
Python 3.11+，仅标准库。原 evidence/deliveries 工具只做本地核验；新增 review_process 会调用独立 Codex 审查，execution_evidence 会按执行者的已授权 recipe 运行构建/测试。均不自动开生产门、不加 hooks/后台任务。

## v3 正式代码批次入口

先读 [完整审查工序](../../Docs/design/mistletoe-review-process.md)。本节覆盖下文旧的可选前审与大材料分流：正式代码批次均需完整方案前审及实现后综合审查，triage 不能授予许可。

- `review_process.py verify-bundle`：核对工具/模板版本。缺失或漂移必须从权威交付版本恢复/重新核验；不靠删 bundle 放行。
- 先填 plan（模板 `templates/review-plan.json`）、risk matrix 和 manifest，manifest 增加 `review_process` 配置路径；配置格式见工序文档。
- `workflow.py begin` 为新代码批次要求 v3 配置并写不可覆盖 opening；随后 `review_process.py --manifest <manifest> new` 注册。
- 暂停旧批使用 `adopt` 替代 `new`，不重写 opening，原文导入所有历史请求（失败也计）和原级未决项。相同导入幂等，不允许清零。
- `review_process.py --manifest <manifest> review --stage plan --codex <absolute-codex.exe> --auth-home <dedicated-ChatGPT-home> --assessment <本轮判断.json>` 自动冻结、占额并发起本轮选定模型的只读会诊。取得 pass 后 `implement` 才给实施/限定修复许可。
- 实施后用 `execution_evidence.py --recipe <recipe.json> --out _workflow/<batch>/executions` 采集真实执行来源。manifest 的 `execution_evidence` 列收据路径；测试/突变的 TRX 指向这些执行结果。
- 原 `workflow.py audit --stage review` 和 `verify` 仍执行回归、矩阵和证据门。再 `review_process.py ... review --stage implementation --evidence-snapshot <review-dir> --codex ... --auth-home ... --assessment <本轮判断.json>` 做完整影响链审查。
- 有重要问题就集中修复、重新捕获受影响执行证据并复审；最终 `workflow.py audit --stage closeout` 统一检查原门与新实现审查。独立 `review_process closeout` 也检查原 manifest 门，不能替代构建/实机门。
- GPT 工具的小范围辅助请求须先 `reserve-external --channel <channel> --question <exact-question> --assessment <本轮判断.json>`，调用后 `record-external --request <NNN> --report <raw-file> --findings <JSON-array>`。失败原文也登记，不能凭辅助报告授予前审/收口许可。

所有上述子命令前可加 `--root <repo>`，不加则当前目录。review_process 的 `--manifest` 放在子命令前；workflow 原参数格式保持。
锁和注册表在 Git common-dir/mistletoe-review，全 worktree 共享。同批固定权威目录，其他 worktree 不得重开账本。故障遗留锁不按 PID/时间自动抢占；先核实创建身份和完整进程树并保存恢复记录。不得自行释放可能已发出的会诊额度。

本地会诊文件清单、git status/diff、输入哈希、request、原始事件/报告/退出码全部保留。工具验证机械条件，独立审查负责范围与语义；不承诺零 BUG 或防恶意绕过。

## 统一入口（原证据工序）

从实际仓库根目录执行；下列占位符由执行者替换，不交给用户填。

```powershell
python -B tools/mistletoe/workflow.py begin --manifest _workflow/<batch>/manifest.json
python -B tools/mistletoe/workflow.py audit --manifest _workflow/<batch>/manifest.json --out _workflow/<batch>/review-<unique-id> --stage review
python -B tools/mistletoe/workflow.py verify --snapshot _workflow/<batch>/review-<unique-id>
```

后续**每个新批次**在改动前建立 v2 manifest，先运行一次 `begin`。代码批次还须先建立
状态、并发、故障三类风险矩阵；纯文档批次登记范围、已有结果复用与子 Agent 评估，
不伪造产品反例。`begin` 记录开工 HEAD、工作区状态、源码哈希及代码批次的原始矩阵，
不覆盖既有开工快照。集中修复和必要验证后填充同一 manifest，再运行 `audit review`、
`verify` 与会诊渠道预检。
只有这三步通过才送审；当前已在途 SB21-4 的精确 v1 批次保留兼容，不热改。

每次会诊由执行者结合当前源码与方案、状态、并发、故障、影响链、改动规模、不确定性及原级未决项智能判断：统一使用 `gpt-6.1-sol`，默认 `medium`；复杂、高风险或风险未排除时选 `high`。首轮通常范围较广，更可能选 high，但不按轮次固定强度；后续复杂问题同样用 high。逐次记录证据及强度选择理由，用户无需选模型；不使用 Astra，也不自动回退旧模型。模型强度判断与渠道分开，所有渠道使用同一政策，累计预算和质量门不减。

执行者逐次运行 `review_process.py --manifest <manifest> assessment-template --stage <plan|implementation|auxiliary> --out _workflow/<batch>/model-<unique>.json`，填入证据与理由，再用 --assessment 送审。模板未填、判断过期或模型与判断矛盾会拒绝请求。旧五因素脚本仅作风险提示，不选模型、不免除审查。

在 `begin` 后、改代码前调用（输出文件必须不存在，以免覆盖旧决定）：

```powershell
python -B tools/mistletoe/consultation_triage.py --manifest _workflow/<batch>/manifest.json --assessment _workflow/<batch>/preflight-assessment.json --out _workflow/<batch>/preflight-decision.json
```

`preflight-assessment.json` 格式如下。五项均须有具体证据；`applies=false` 也须写排除理由，
不能把未知填为 false。工具核对 opening 源码哈希，发现漂移就拒绝，必须重审现场。

```json
{
  "schema_version": 1,
  "batch": "本批真实编号",
  "opening_snapshot": "_workflow/batch/opening.json",
  "design_contract_path": "_workflow/batch/preflight-design.md",
  "factors": {
    "authority_transition": {"applies": false, "evidence": "入口与合同定位", "consequence_or_exclusion": "本批不改受理/许可/提交/终局"},
    "durable_migration_or_rollback": {"applies": true, "entry_or_contract": "实际迁移/回滚入口", "evidence": "源码和合同位置", "consequence_or_exclusion": "错误删除他方文件"},
    "cross_process_or_generation": {"applies": false, "evidence": "入口与合同定位", "consequence_or_exclusion": "本批不改跨进程或代际身份"},
    "cross_component_production_gate": {"applies": false, "evidence": "入口与合同定位", "consequence_or_exclusion": "本批不改跨端生产门"},
    "safety_contract_or_open_findings": {"applies": false, "evidence": "权威合同和发现台账", "consequence_or_exclusion": "本批不冻结安全合同、不承接未闭合高优先级项"}
  },
  "equivalent_review": {"available": false, "reason": "无同范围、同合同、同开工版本的独立前审"}
}
```

若 `equivalent_review.available=true`，还须给 `report_path`（仓库内已存在报告）、
`source_hashes`（与 opening 完全相同）和 `same_scope_and_contract`（具体核对依据）；
工具仅校验报告存在和哈希相等，**不能证明报告的语义范围确实等效**，须执行者人工核实。
输出仅为 `risk_attention_required`、`equivalence_needs_assessment` 或 `no_listed_risk_hit`；均不是模型选择或许可。缺项、漂移或路径错误退出码2。正式会诊仍逐次走完整 assessment。

阶段：`evidence` 核对证据和生成索引；`review` 增加送审材料与工作区差异；
`closeout` 同样核对材料，由执行者继续核验权威完成判据，工具不批准收口。
每轮新建输出目录，禁止覆盖旧快照。输出只允许位于 `_workflow/`，不会修改输入。
`verify` 在送审/收口前再次确认 HEAD、分支、输入、材料和本批 staged/unstaged diff。
材料外变化仍要重新核对归属；不把其他施工方的正常进展判成自己的成功。

退出码 0 只表示声明范围的机械核验成功；2 表示无法核验，执行者补齐/修复后重新运行。
所有成功报告均标注 `quality_verdict: NOT PROVIDED`。工具损坏或 Python 不可用时由执行者
完成同等人工核验并登记原因，不能删减原有质量门，也不让 owner 临时负责安装。

## Manifest：由执行者生成

```json
{
  "schema_version": 2,
  "batch": "本批真实编号",
  "mode": "code",
  "sources": ["relative/source.cs", "relative/test.cs"],
  "risk_matrix": "_workflow/batch/risk-matrix.json",
  "opening_snapshot": "_workflow/batch/opening.json",
  "review_control": {
    "existing_results": {"decision": "none", "reason": "逐项核对现有交付后，无可复用的同版本结果"},
    "review_round": 1,
    "prior_findings": [],
    "criticality_reason": "仅当代码矩阵没有关键行时，说明判定依据",
    "subagents": {
      "decision": "not_used",
      "reason": "本批没有可独立推进的只读核查；若使用则列明固定开工 ref 和已索引报告",
      "tasks": []
    }
  },
  "evidence": [{
    "id": "targeted-final",
    "path": "_workflow/batch/final.trx",
    "purpose": "本批定向回归；不证明实机效果",
    "level": "test",
    "conditions": "执行命令、运行条件及基线失败身份",
    "source_sha256": {
      "relative/source.cs": "执行当时记录的64位SHA256",
      "relative/test.cs": "执行当时记录的64位SHA256"
    }
  }],
  "tests": [{"path": "_workflow/batch/final.trx", "expect_success": true}],
  "mutation_scope": "列出需突变的关键断言、已覆盖项、未覆盖项和理由",
  "mutations": [],
  "outside_changes": "明确区分本批文件与材料外变更；不得凭路径归属猜测写者",
  "packet_limit_bytes": 524288,
  "packet": [
    {"path": "_workflow/batch/context.md", "role": "objective"},
    {"path": "_workflow/batch/findings.md", "role": "findings"},
    {"path": "_workflow/batch/budget.md", "role": "budget"},
    {"path": "relative/source.cs", "role": "source"},
    {"path": "relative/test.cs", "role": "source"}
  ]
}
```

这只是格式示例，不是已经可用的产品证据。路径相对 `--root`（默认当前目录），不得跨出
仓库根目录；拒绝 User、bin、obj、.git、.kiro 路径，也不递归采集其他文件。
源码与证据只能在明确白名单内读取。不要将账号凭据、用户配置或无关日志收入材料。

开工矩阵文件格式：`{"schema_version":1,"batch":"本批真实编号","rows":[...]}`。
至少各有一行 `dimension=state/concurrency/fault`，每行必须有唯一 `id`、`scenario`、
`expected`、布尔 `critical`，开工时 `status=planned`。后续只可增加行，不可静默改动原行
场景/预期/关键性；送审前逐行改为 `covered`（列出 `counterexample_ids`，测试证据另列
实存且结果为 Passed 的 `test_ids`，关键行列有效 `mutation_ids`）或有具体 `reason` 的
`not_applicable`；不能把全部三类都记为不适用。行级测试证据须为当前源码绑定、列入成功回归
清单的 TRX；历史、红、跳过记录不能声称覆盖。所有引用必须指向 manifest 的证据/突变。
工具验证引用和 TRX 身份，不判断反例是否真的
覆盖业务语义；真正不适用的理由须由主执行者和审查者复核。

第 2 轮及以后 `review_control` 必须有非空 `repair_batch_id` 和逐项 `prior_findings`；
每项保留 `id`、`severity`、已索引的 `source_review_evidence_id`。必改/重要项送验证前
只能记 `disposition=candidate_fixed` 并列出 `repair_evidence_ids`，否则保留 owner 检查点，
不能伪记完成。建议项可有理由地采纳或拒绝。若使用子 Agent，`subagents.tasks` 限 1–2 项，
各列 `question`、`read_only=true`、与开工快照一致的 `fixed_ref`、`opening_source_hashes`、已索引的
`report_evidence_id`；主执行者仍须核查该报告在最终源码上是否适用。

- `mode=code`：要求测试记录和所有证据的精确源码绑定。绑定必须从实际执行当时记录取得，
  **不能给旧 TRX 补上当前哈希就宣称当时运行了当前源码**。依赖/合同/运行条件也必须由执行者
  放入范围或条件记录，不能只枚举修改过的文件。
- 修复前基线等证据可标 `binding=historical`，登记当时真实源码哈希；允许作差集对照，
  但不能作为 `expect_success=true` 的当前回归证明。其他代码证据默认 `binding=current`。
- `mode=documents`：仅用于文档/清单改动，必须给出 `validation_reason`；拒绝把 .cs/.py 等
  执行源码藏在此模式。产品行为改动仍按原有回归要求，不能改用此模式免测试。
- `mode=historical`：允许整理历史材料，仅可用于 `evidence`；不能作为新版本送审或收口。
- `level` 允许 build/test/component/runtime/consult/document；只是执行者声明的证据层级，
  工具不从文件名猜测、更不把 component 升级为 runtime。
- `tests[].expect_success=false` 可记录红夹具/已声明失败，不自动豁免失败。
- 可增加 `comparison={"baseline":"...trx","final":"...trx"}`；按 testId 保留全部执行并报告
  增/删/变化。同名不折叠；跨适配器身份变化必须人工对账，移除项不自动判为合法。

## 突变记录

`mutations` 每项包含：id、source、original_sha256、restored_sha256、baseline_trx、mutant_trx、
restored_trx、target_test_id、target_name、failure_contains（预期错误信息）、assertion_contains
（预期断言栈位置）、build_log、build_exit、baseline_exit、mutant_exit、restored_exit。

必须有成功基线、成功编译记录、明确失败退出、指定测试/断言失败、恢复后成功和逐字节恢复。
编译失败、跳过、失败在更早断言、复用同一 TRX 均不能算有效突变。工具只核对已有记录，
不改产品源码、不自动制造突变；退出码与源码运行归属的真实性仍需送审。

## 送审材料和证据索引

`packet` 必须包含 objective/findings/budget 三种角色并覆盖 sources 中所有文件。
摘录使用 `start_line`/`end_line` 和必填的 `coverage_notes`；工具从原文件生成摘录并附
来源完整 SHA256、行范围。执行者负责语义完整性，不能用摘录删掉例外、失败或质疑。
材料自动包含全局 `git status --porcelain`、本批未暂存/已暂存 diff 和材料外变更说明。

本地材料默认最大 524288 字节，可以设更低上限，不能调高来规避预检。超限直接失败，
**不自动裁剪**。这是本地文本上限，不能证明会诊渠道的最终 JSON/附件/转义后请求体合规；
执行者仍须跑该渠道自身预检，不修改现有会诊工具或模型/强度/点位。
预算角色需要保留同子批累计次数、固定追加授权、原等级和未闭合项；工具不替 owner 裁决。

输出：report.json（逐执行结果与差集）、index.md（用途、条件、层级、哈希）、packet.md、
git-status.txt、scoped-unstaged.diff、scoped-staged.diff。索引只用于导航，不批准复用旧证据。

## 工具回归

```powershell
python -B -m unittest discover -s tools/mistletoe -p "test_*.py" -v
```

包含重复名称、计数不一致、截断 XML、无效突变、材料超限、输入漂移、覆盖输出、
缺矩阵维度、伪反例、重要项未处置和新批 v1 绕过等反例。另在临时目录中故意改坏
九处关键守卫，要求具体测试变红；语法错误不计作检出。


## 并行成果自动发现与接收

后续执行者在开工、自然边界和收口自行运行：

```powershell
python -B tools/mistletoe/deliveries.py --root <实际原工作区> --registry <权威机器台账绝对路径>
```

默认台账为实际root的 `Docs/design/mistletoe-parallel-deliveries.json`。
工具只读托管worktree和指定报告/验收哈希，发现 `_r*/report.md` 漏登记、HEAD或材料漂移；输出JSON队列到stdout。
退出2必须由执行者处理；新报告先核验并补账，不自动判完成。工具不查询线程状态、不写台账、不运行产品、不合并或批准验收。
执行者按 [回收闭环](../../Docs/design/mistletoe-parallel-deliveries.md) 在目标批次自行接收并验证，owner不用提醒或搬运。
测试包含漏登记、报告/HEAD漂移、缺失文件、未提交材料和伪集成标签等反例。
