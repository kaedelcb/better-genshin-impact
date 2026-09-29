# 完整审查工序 v3 方案

本批只改 tools/mistletoe、相关设施文档及忽略的入口；不修改产品代码、在途 R5.6 证据和 Goal。目标是可审核的流程门禁，不声称防止恶意执行者绕过脚本或证明零缺陷。

## 工序与接口

新增 review_process.py，工作流 v2 manifest 保留；新 code begin 要求 review_process 配置，已存在 opening 的旧批保持旧验证。新配置指向 _workflow/<batch>/review-process.json，包含同一 batch、方案路径、导航路径、供独立查阅的源码目录、累计既有请求和原级发现、必需模型/强度、接入模式。所有后续 code 批必须启用。暂停旧批显式 adopt：保留 opening，核对历史会诊原文/次数/未决项；评审的是剩余修复方案，不能倒签已实现代码的前审。

plan 是结构化 JSON：目标/非目标、接口与调用链、状态/并发/故障路径、所有权与持久化、兼容、具体实施步骤、测试与反例、生产门、未知与决策。所有项必须非空；具体语义由独立审查核实。

prepare 复制允许目录内所有源码/测试/合同到唯一 snapshot，不把 navigation 当白名单；排除 .git、User、bin、obj、.kiro、密钥/凭据和运行数据，禁止符号链接/重解析路径。全量路径清单和哈希、分支/HEAD、git status、相关完整 staged/unstaged diff 一起冻结，捕获前后全量清单对比以发现新增/删除/改写。快照禁止运行产品构建，不复制输出日志以免秘密泄漏。所需证据显式纳入，未知或漏材料必须阻断。

review stage=plan|implementation：按 batch 独占锁，核对快照和版本后先持久化 reservation，再调用独立本地 codex exec --ignore-user-config --ignore-rules --ephemeral -s read-only -m gpt-6-astra -c model_reasoning_effort=medium。专用已登录 ChatGPT home 与白名单环境，绝不继承 relay。超时/失败计入原批累计8次；预检未发不计。文件、events、stderr、退出码原样保留；不自动重试。崩溃后 reservation 留下、锁不自动抢占，人工核实没有子进程后解锁，保留记录。

报告 JSON schema：结论 pass|changes_required|blocked；实际审阅路径、独立追踪路径、每项影响面覆盖与限制、完整原级发现、统一修复方案、逐条旧发现处置。每条发现包含 id/severity/root_cause/counterexample/paths/repair_steps/tests；旧重要项须独立 reviewer 给出 closed 与修复/回归证据，缺项或降级阻断。实现阶段强制覆盖调用方/被调用方、状态、并发、故障恢复、兼容、测试判别力和生产门；不允许仅报告上一轮差异。

implement 命令只在同方案、同快照、无重要未闭合项且有效 plan pass 后生成许可收据。review implementation 要求许可，允许实现源码变更但方案不变；方案改变须重新 plan review。closeout 要求当前实现全量快照与 implementation pass 一致、无未知/重要未闭合项、既有 workflow audit/verify 回归门通过。审查期间新文件/源码漂移/报告丢失/计划变更均阻断。

规则和模板以受版本控制文档为权威；bundle.json 包含设施文件哈希，新 worktree verify-bundle 核对，接力包含入口。忽略的 AGENTS/Skill 仅导航到跟踪文档，不把其缺失当作设施不存在。已运行 R5.6 由 owner 恢复后执行 adopt，不能热注入。

## 故障与回归

测试缺方案/缺报告/伪 pass、错误阶段/模型、计划或来源变更、新增依赖、重要项遗漏或降级、失败占额/第9次、双进程竞争、崩溃保留锁、路径逃逸/链接、受保护数据、跨批证据、旧批接入保留计数及未决项、新 worktree 完整/缺文件继承。重要阻断断言做反向突变。

真实演练以小型临时仓库：导航只给 service.py，其调用的 storage.py 包含丢更新错误；本地 reviewer 须自主发现关联文件并提供完整修复。正式实现后再以固定本批源码进行独立综合审查。旧 tools/mistletoe 测试全部运行，质量门不减少。

## 风险

此机制依赖执行者准确声明允许源码目录和历史记录，无法自动证明它们完整；reviewer 必须记录外部依赖和未知，未知阻断 pass。只读沙箱限制写入，不宣称操作系统级读取隔离；提示禁止目录外读取，副本排除敏感材料。标准命令机械验证与独立语义审查共同作用，不是新的后台钩子。

## 审计中补充的实现约束（提交下一轮核对）

- 工序记录绑定 opening 路径及原始哈希，目录由 opening 同级 review-process 固定推导；adopt 写一次性锚点。workflow review/closeout 根据锚点或 opening 内标志强制检查，不因 manifest 删除配置就回退旧路。
- 只有新 code begin 引入强制配置；已有 opening 保留。原有测试的 legacy fixture 改为直接载入历史 opening，不通过新 begin 伪造“已开工”。另建新 begin 必须拒绝缺配置的测试。
- 请求序列是连续追加目录，不覆盖、不删除；锁覆盖准备/预留/发出/结果持久化，所有外部渠道也先 reserve 再记录结果。reservation 崩溃后按可能已发送计数且阻断；只有证明未发送的本地预检发生在 reserve 前。
- 阶段只能消费最新一次实质审查；后续 failed/blocked/changes_required 使旧 pass 不再是最新。收口同时检查历次重要发现的并集及原等级，不能只读最后一份空列表。
- 方案审查通过的源码身份用于首次 implement；实施后源码允许变，方案/配置必须不变。每份 implementation 审查绑定当时全部选定源目录的集合和字节、回归证据及原门禁快照；重要项关闭须提供原层修复和验证证据。源码在审查后新增/删除/变化全部失效。
- snapshot MCP 仅提供 list/read/search，不提供 shell/写操作；固定 UTF-8 stdio，限定快照 files.json 中的路径与字节。审查者可以发现导航之外的已冻结源码。外部依赖缺失记 unknown，扩范围后必须重新审查。

## 前审 R3 七项的统一修订（原级保留）

1. MUST R3-1：BatchStore 锁和注册表放 Git common-dir/mistletoe-review/<SHA256(batch)>，所有 worktree 共享；一个批只能绑定一个权威 opening/账本，第二工作区不得另开账本。事件顺序 prepared(未发不计) → durable dispatch_intent(开始占额) → success/failed/unknown；intent 后任何不确定均占额，不实现自动释放。文件独占创建+flush+fsync，发布用原子 replace；损坏拒绝；普通进程中断可恢复，断电持久性受文件系统保障限制。锁正常退出关闭；崩溃残锁不自动抢。恢复要求核实完整进程创建身份和后代，并追加人工证据，不提供盲删锁命令。跨工作区正在同批只能回权威目录执行，防双账；新克隆须携完整账本并核验，不能当新目标。
2. MUST R3-2：注册信息绑定 opening 哈希、原批名称、权威绝对目录、初始配置与导入历史文件哈希。adopt 一次性原子锚定，重复同内容只读返回，不重新导入；后续 manifest 丢配置按注册表仍阻断。新 begin 使用独占创建；legacy 由发布时已有 opening 明确清单判定，测试使用历史 fixture，不通过新 begin 规避。
3. MUST R3-3：发现分为 plan 与 implementation 义务，原 severity 不变。plan pass 可以携带尚未关闭的 implementation 重要问题，但每项须有已审 repair_steps/tests/allowed_paths，生成 repair-only 许可。未闭合 plan 重要项或无法定位修改范围禁止许可。所有 implementation 重要项继续阻断 closeout，禁止生产开门。
4. MUST R3-4：请求包含随机 request_id、batch、stage、snapshot_hash、plan_hash、policy/bundle_hash；输出必须回显绑定，由 runner 核验成功退出、原始最终输出、MCP 读取事件和原始输出哈希后写 receipt。每次 gate 重验原始数据。runner 不提供导入自写 pass 的成功接口。累积历次有效报告 findings，逐条保留 scope/severity，不允许遗漏降级；无效或 failed 最新审查不能倒退使用旧 pass。关闭重要项要求独立 reviewer 引用快照内具体修复/测试证据路径。历史审查导入也须完整原文和逐项对照，历史缺失无法认证则阻断并报告。
5. IMPORTANT R3-5：身份包括配置、范围、计划、工具bundle、固定模型强度、开工身份、显式合同和证据。配置/计划/bundle 改变失效计划及下游，源码只使当次审查失效；implement 首次核对前审源码，实施后许可不要求仍等于旧源码。triage reuse 只是建议，不得替代新 gate。每个命令 verify-bundle；开发工具自身采用 bootstrap 人工等效记录，定版后生成 bundle 并重新审查。
6. IMPORTANT R3-6：输入目录不准包含审查输出；证据只能逐文件引用。捕获文件与完整 Git diff 统一敏感名/常见凭据模式预检，命中阻断而不裁剪。不能证明任意秘密都可检测，执行者仍核对目录。所有路径组件拒绝 symlink/junction/reparse，普通文件UTF-8仅，过大文件显式阻断。两次清单/字节及Git diff/HEAD/branch稳定才发布完整快照标记。报告无完整标记不可用。
7. IMPORTANT R3-7：audit review 只做旧证据门+实施许可有效性，不要求本次 implementation pass；closeout 才统一要求有效实现 pass。本地 route 的旧 packet 只做导航索引，原 source/evidence 字节全部进入本地快照，不受拼包512KiB限制；旧 GPT packet 模式上限原样保留，禁止截断。文档批仍旧流程。

以上均纳入实现及故障测试。权限门是标准工序授权，不控制任意手工 shell；对恶意删除整个账本/修改工具不作安全证明。

## R4 对 R3-4 剩余 MUST 的完整合同

ExecutionEvidence 由新增 execution_evidence.py capture 生成，不接受仅重填 manifest 哈希的认证。recipe 固定 input_roots、input_files（源码/测试/配置/锁文件）、build_argv、run_argv、products、results、conditions；shell=False。执行目录由工具在 _workflow/<batch>/executions/<UUID> 独占创建，产品与结果必须位于这个全新目录内；build/run 通过 {out} 使用它，不准复用旧产物或旧TRX。编译型执行必须先成功 build 到该目录，声明 products 全部存在，run_argv 必须引用本次产品；脚本型可免 build，但 run_argv 必须引用绑定在 inputs 中的脚本，记录解释器路径/哈希，审查其条件。实际命令与参数、工作目录、环境条件、进程退出码、运行前后源码/测试/配置全量哈希、build 后/run 前产品哈希、run 后产品哈希、输出TRX/日志哈希、时间与 execution ID 均写入 receipt。任何输入/产品漂移、非零退出、缺结果、结果预存在或未引用本次产品都失败，不发布合格标记。工具只在本任务授权的构建/测试范围执行 recipe，不启动生产。

validate_execution_evidence 对当前输入重新枚举/hash，对 receipt 的 recipe、命令、产品和结果原文/hash及成功标记重验；清单必须覆盖本 manifest sources 及关键测试/配置，最终覆盖是否充分仍由 reviewer 确认。workflow 的已接入 code review/closeout 必须引用当前有效 ExecutionEvidence，并要求 tests[].path 指向该执行 receipt 的结果文件（TRX解析仍执行原门）；旧绿TRX+新源码哈希没有新capture记录则失败。独立 reviewer 的 important closed 引用须指向已认证 execution receipt 以及对应修复源码，不把 ReviewReceipt 当执行证明。

暂停 R5.6 已有执行证据只有原始命令/版本/产物与运行条件材料齐全、可独立认证时才可历史复用；v3 首版不实现人工旧证据变绿接口，缺失时重新定向验证，由执行者完成。不把无来源的旧TRX默认为有效，也不因此重做全部历史工作。恢复用户现有批次的修复许可不依赖未产生的最终回归证据；implementation review 前才要求完整执行证据。

新增反例：A绿色结果不能通过B输入绑定；编译到新目录却run旧二进制在发出run前失败；执行中输入/产品变化失败；失败退出后残留绿TRX失败；缺recipe/原始日志/来源失败；真正同版本新目录执行可通过。相应阻断加入反向突变。

## R5 对 R3-4 最后负向证据合同的修订（替代上文“非零不发布收据”）

来源认证与成功判定分离：capture 在进程确实执行完、输入/产品稳定、结果完整时保存 authenticated execution receipt，无论退出为0还是预声明的失败码；启动/超时/漂移/缺输出不认证。receipt 记录 purpose 与 expected outcome，但不自行宣布绿色。validate_execution_evidence(receipt, expected_identity, purpose) 按用途消费：current_regression 必须当前身份、exit0和原TRX green；negative_test 绑定预声明版本、失败testId和关键断言，不能当green；historical_baseline 保留原版本只作同条件比较；mutation 使用B/M/B三收据的关系判定。当前已知失败的完整回归可以作为comparison证据，能否接受仍由原 compare_trx 的相同TestId/条件规则判定，绝不因为receipt认证而放行新失败。

mutation_check 接收 baseline_execution、mutant_execution、restored_execution 三个receipt及固定 mutation patch：三者capture各自完整输入集，B与恢复B完全相同，M仅改变record声明的source为mutant_sha256，其余输入完全相同；补丁和变更字节摘要固定。先B基线→M执行→恢复B后再执行，认证顺序用时间/执行ID绑定，三份结果必须对应原record.trx且退出码一致，M build 必须成功且run实际命中目标失败testId及断言；非零+绿TRX拒绝。当前目录比对恢复B，不要求M等于B；历史快照保存全部输入摘要供验证，不把旧M换个声明哈希当本次M。

capture 在全新输出目录运行，receipt 保存build及run分离退出；所有用途build失败均不能冒充有效run。未验证负向用途不可作重要项已修复证据；关闭重要项仍须当前回归证据，必要时附认证突变关系。关键测试覆盖真实红夹具、完整B/M/B、错误断言、旧mutant重放、非目标输入变化、非完整恢复、build失败与负向报告冒充当前green。这里只是按用途更准确判定，不删除原mutation/TRX/compare门。
