# 完整审查工序 v3

## 原生渠道接入（2026-09-30 本Goal授权）

用户在本线程明确授权“不再让我授权，我都同意”“不要再计什么数了”“不计代价解决问题”，并要求现成独立Agent/全项目读取/审查Skill与真实R5.6验收。本授权Goal的原生渠道不受旧累计次数和阶段额度停工条件；原记录保留，旧CLI渠道及其他Goal不因本条自动变更。语义前审/后审、模型逐次判断、原级闭环、认证执行、回归、突变、声明面与生产门全部保留。

原生渠道保留原manifest的review_process/opening/history，再以native_review配置显式接入。完整项目副本按路径集合/逐字节SHA/Git/合同固定，含未提交源码；送入导航仅为起点，审查者自主追查全影响链。受保护数据/输出排除记录与缺失关键依赖的unknown不能静默抹除。

原生来源以派发前request、session_meta子Agent身份、turn_context的可观测model/effort、真实final_answer和完成/读取记录核验；仅证明调用配置，不推断服务端底层模型。报告JSON必须来自独立原始final，不由父Agent改写。原生receipt不虚构CLI/Job/退出码。源码只读是任务约束加隔离/前后核对，不声称抵抗恶意进程的OS沙箱。

方案pass允许本批实现义务继续open，只授予原合同内限定修复；实现pass与收口要求本批重要/必改和正确性未知闭环。原finding/unknown逐项保留原文/等级/obligation/来源；范围外边界由独立审查基于原合同判断，保持未验收、门关闭，不能借分类降级或移出本批。

大型阅读单元checkpoint只保存已返回结果与覆盖/队列；存活Agent续接，确认终态后才以新上下文恢复同snapshot，未保存部分重做。最终独立综合报告不能由局部pass合成。实现与主线接收的相关输入变化须补审/回归，旧报告不重写身份。

机械入口、配置及实际权限局限见 tools/mistletoe/README.md 原生渠道节与 tools/mistletoe/skills/mistletoe-independent-review/SKILL.md。现成组件仍须真实验收，安装或工具套件通过不能代替R5.6及产品验收。

适用后续槲寄生正式代码批次，以及 owner 明确暂停后接入的既有批次。执行者读取本文件、设施说明和工具 README 后自行完成工序；用户无需逐步提醒。具体交付版本与测试结论以 _workflow/review-process-v3/ 的验收记录为准，不能用本文存在代替验收。

1. 核对原 Goal、计划、写者、并行成果和原级未决项；建立完整方案及状态/并发/故障矩阵。
2. 固定源码、合同和相关调用链快照。独立本地模型 自主查找依赖，审查如何实现和如何证明；导航不是阅读白名单。
3. 方案缺陷修订成批送审。代码未决项可以保持 open，凭已审修复方案进入限定修复；不能将方案通过登记为问题闭合。
4. 按方案集中实现，关键反例先行；通过执行证据捕获实际版本、产物和测试结果，保留原有回归、突变、实机及生产门。
5. 实现后独立综合审查整条影响链：调用方/被调用方、状态与并发、异常恢复、兼容、测试判别力、生产门、原发现。报告必须给具体修复步骤与测试，未知范围不能写通过。
6. 重要项成批修复并按原级复核；最后统一核对版本、回归与原发现闭环。次数全渠道累计，失败也计；上限不代表通过。

仅工具返回“机械核验通过”不等于语义证明。标准工序门禁不能拦截执行者绕过脚本的任意 shell，也不能保证零 BUG。

## 暂停批次接入

R5.6 等既有批次恢复时沿用原 Goal、opening、历史请求与原级未决项。adopt 是单向接入，不重新开批、不清零、不倒签代码前审。先审剩余修复方案，来源完整的历史材料保留；缺执行来源的当前回归重新验证。已用尽额度时保留阻断，只有 owner 的有限额外授权允许新增会诊。只要仍有重要项，相关生产门保持关闭。

## 后续提示词必须携带

权威入口 Docs/design/mistletoe-review-process.md、Docs/design/mistletoe-workflow-facilities.md、tools/mistletoe/README.md；核验 bundle；用完整 Goal 覆盖开工、方案审查、实施、真实执行证据、综合审查、闭环与收口；接力继承原账本。独立 worktree 缺设施时执行者核对权威工作区及版本，精确同步，不让用户搬运。

## 配置与实际命令

manifest 仍用 v2，增加 `review_process: "_workflow/<batch>/review-config.json"`，新 code begin 会验证它。
配置示例（由执行者填，不交给 owner 填）：

```json
{
  "version": 3,
  "batch": "与manifest完全相同的原批名称",
  "model_policy": "risk-assessed",
  "default_model": "gpt-6.1-sol",
  "effort": "medium",
  "source_roots": ["完整相关源码目录", "对应测试目录"],
  "navigation": ["入口源码路径"],
  "scope_rationale": "实际调用链、共享调用方和目录外依赖如何纳入；无法纳入即unknown",
  "extra_files": ["稳定相关合同文件"],
  "plan": "_workflow/<batch>/plan.json",
  "history": [],
  "prior_findings": []
}
```

`source_roots` 递归纳入普通UTF-8源文件；不把导航当白名单。User/bin/obj/.git/.kiro/node_modules 等目录排除；输入文件和链接/重解析点、过大/非UTF-8文件、疑似凭据、受保护路径的历史diff会阻断。需要这些文件证明语义时，单独安全分析并留下证据，不能静默删掉后说全面通过。不是任意秘密识别器；执行者仍要核对目录与敏感信息。

禁止把 review-process 输出树置于 source_roots 内。extra_files 是稳定合同，变化会撤销实施许可；动态执行证据不要列入它；实现审查还自动纳入原 audit 读取的文件、完整 diff、execution receipt/recipe/log/结果。`files.json` 分页接口展示全部冻结文件，模型可搜索和追踪导航外依赖。

`adopt` 配置另加 `history_reconciliation`，逐请求列 history：`request_id/channel/outcome/evidence[]`，原始失败记录也列；原级未决项列 prior_findings，字段同审查报告：`id/severity/obligation/status/root_cause/counterexample/paths/repair_steps/tests/closure_evidence`。旧代码问题用 obligation=implementation、status=open，不把修复方案批准写成问题关闭。历史记录与opening永久绑定，注册表先落盘再写本地锚点，中断可同内容重试。

执行 recipe 示例（解释型测试；编译型另加 build_argv 与 products，run_argv 必须直接引用本次全新输出目录产品）：

```json
{
  "purpose": "current_regression",
  "conditions": "填写平台/环境/基线条件，不启动生产",
  "input_roots": ["源码目录", "测试目录"],
  "input_files": ["关键配置和锁文件"],
  "build_argv": [],
  "run_argv": ["python", "-B", "{root}/测试入口.py", "{out}/results.trx"],
  "products": [],
  "results": ["results.trx"],
  "timeout_seconds": 1800
}
```

只采用 shell=False 的 argv；命令、产物、日志、实际退出与执行前后输入哈希写入全新UUID目录，不自动清理。进程认证不等于测试通过：current_regression 必须 exit0 且原TRX门绿色；negative_test 另填 expected_failure 的 test_id/assertion_contains；comparison 仍走原同身份基线差集检查。historical_baseline 只用于原版本比较，不能被重新标为当前结果。

突变记录除原字段外，增加 baseline_execution/mutant_execution/restored_execution、mutant_sha256、patch/patch_sha256。三次收据对应各自原始TRX/退出码；B与恢复B输入完全一致，M仅改变声明目标，运行顺序和目标失败断言均验证。原 mutation_check 继续执行，不用来源认证替代反向突变结果。

## 失败、预算与恢复

每个原批最多8次，跨本地和辅助GPT渠道累计。预检在持久 dispatch_intent 前失败不计；intent后不确定按已发占额，不自动重发或释放。失败不产生 pass，后续失败不能回用旧 pass。新增目录/字节/合同/计划/配置/bundle变化使相应证据失效；未完成或已损坏收据不授予许可。

残锁不按PID或超时直接删除。执行者核对进程创建身份、后代、写入终态并保存恢复证据；无法排除活跃进程则阻断。同批其它worktree使用Git common-dir的同一注册身份，必须回原权威账本继续；新克隆不得拿同批历史当零次新批。默认原批上限8次；预检在 `dispatch_intent` 前失败不计，intent 持久化后即使失败、取消或状态未知仍计数且不自动重发。

收到损坏或局部校验失败的报告时，不得编辑原报告、合成 `receipt.json`、改编号或重开批次。窄恢复命令 `reconcile-report --request <NNN>` 只可处理最新一个已完成、无 receipt 的本地请求；它验证原冻结快照的内部身份/逐文件哈希、进程树终态、runner 输出、报告与最终事件一致性，并要求原始报告仅因 `discovered_paths` 超出快照而未通过。它只在内存中去掉恰好这些越界发现，再对报告执行全部其余既有校验；只允许非 pass、仍有 unknowns 且所有发现均保持 open 的报告。恢复记录单独写在 `reconciliations/`，绑定请求与证据哈希；原请求目录逐字节不改。该状态仍是 `reconciled_unverified_non_gating`：不产生 receipt、不算 review pass、不授予 permit；最新 attempt 为该记录时 `latest/implement/closeout` 均拒绝。所有发现以原等级 open 进入后续审查，且每个旧 unknown 转为稳定 SHA-256 身份的开放义务；后续报告必须逐项原文保留，或给出明确 resolution 及当前冻结快照中的证据路径。遗漏旧 unknown、伪造 identity 或无证据关闭均阻断；任何未解 unknown 都禁止 pass。记录缺失、歧义、漂移或有第二个校验错误一律阻断。

恢复后的下一份冻结快照会同时纳入未修改的原请求文件和其精确历史快照（含历史 `snapshot.json`、`files.json`、`git.json` 及冻结源文件），使独立审查者可以检查 runner/来源上下文；每份历史来源仍以原 request/snapshot 哈希校验。`__review__/git.json` 保留全工作区 porcelain status；staged/unstaged diff 精确覆盖配置声明的源码根与显式非流程记录引用，不声称覆盖全部材料外 diff。审查者须分别报告本批路径与材料外状态；若材料外状态中的文件可能进入调用/构建链，须先扩展冻结源范围，不能仅凭路径列表判为无关。

有限额外额度必须先由 `authorize-extra --source <owner-authorization.json>` 登记原授权源及本地/Git common-dir 双镜像。登记按原批 identity 和当前失败/请求账本校验，不覆盖或清零；授权记录纳入之后冻结的审查快照。每个超额 `dispatch_intent` 绑定授权摘要、阶段和该阶段序号；失败/未知请求照样消耗一次。额外分配须是有限、按阶段固定且总数等于 allocation 之和；缺一镜像、来源变化、阶段超额或第 8 次以后的无授权请求均阻断。当前 R5.6 owner 检查点仅授权2次：方案1次、实现1次；任一授权审查仍有未闭合 MUST/IMPORTANT 即停止并另行请求固定额度，不滚动续期。该登记机制不更改原批历史或产品/生产许可。

本地审查显式传入本轮智能判断的 GPT-6.1 Sol 与 medium/high、read-only、忽略用户provider设置及规则，专用ChatGPT认证环境；读取通过MCP仅暴露冻结文件。系统模型/登录/读取能力不可用时如实阻断，不降模型、不改成施工者自审。官方接口配置参考 [Codex MCP](https://developers.openai.com/codex/mcp)。

## 新工作区与持续执行

版本权威是经审查提交中的 tools/mistletoe/review-bundle.json 及其精确文件集合。设施清单的文本内容哈希仅规范化 CRLF 为 LF，以兼容 Git 换行转换；实际源码快照、合同、执行输入与报告仍逐字节绑定，不能据此复用旧运行证据。新工作区先按交接的权威提交核对，再运行 verify-bundle。缺失文件由执行者按权威版本获取，不能仅复制忽略的AGENTS/Skill或自己重算哈希冒充已审核版本。文档与工具一起受版本核验。

每个新 Goal 的执行者自行完成这条工序。AGENTS/Skill 导航、可复制 Goal 的必读入口和标准工具门共同保证流程被检查；它不是会自动改写旧 Goal 的后台进程。暂停会话只有读取并执行 adopt 后才算接入。最终报告区分工具已测试、实际运行、未验证与生产门，不承诺零风险。

## 每次会诊的模型判断

每次会诊由执行者结合当前源码与方案、状态、并发、故障、影响链、改动规模、不确定性及原级未决项智能判断：统一使用 `gpt-6.1-sol`，默认 `medium`；复杂、高风险或风险未排除时选 `high`。首轮通常范围较广，更可能选 high，但不按轮次固定强度；后续复杂问题同样用 high。逐次记录证据及强度选择理由，用户无需选模型；不使用 Astra，也不自动回退旧模型。模型强度判断与渠道分开，所有渠道使用同一政策，累计预算和质量门不减。

执行者先运行 `assessment-template --stage plan|implementation|auxiliary --out _workflow/<batch>/model-<unique>.json`，填写八维分析、当前证据路径及两档推理强度的比较理由。模板本身不能送审。随后每个 `review` 或 `reserve-external` 都提供 `--assessment <file>`；源码/合同漂移、缺判断、模型/强度与风险矛盾时预检拒绝，不占会诊额度。判断的真实性仍须执行者负责，工具不声称理解语义。

本版真实执行与本地会诊进程采用 Windows Job Object，当前仅支持 Windows；不支持的平台明确阻断，不退化成无后代约束的执行。每次保留真实进程创建身份、Job 与整棵进程树终态。取消/异常保留锁和占额；只清理本次创建的进程树。
