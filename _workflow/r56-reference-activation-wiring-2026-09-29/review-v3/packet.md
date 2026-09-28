
## objective: _workflow/r56-reference-activation-wiring-2026-09-29/context.md L1-L16 SHA256=4c8ded8239b7b8179f5378b6fb1355b373d0f7f5216ea9ff5bf38baf1977cde8

# 本批目标（R5.6 引用写入 + candidate→active 真实接线）

## 结果
在**隔离配置根**内把「真实引用写入」与「candidate→active 激活」接入 R5.6 迁移事务，
并证明**事务阶段标记只在实际副作用成功、持久化并读回确认之后推进**。

## 约束
- 不触碰真实 `User` 目录、不开放生产入口、不启用 E3/E4/E5/热键/R5.8 签署。
- 代码由主执行者单写；只读子 Agent 只做独立枚举核对。
- 权威材料：R5.2 §21.2–§21.10、§23.4；R5.3 §24.128；activation-prep 验收矩阵 A；
  AGENTS.md、bgi-project-development Skill、会诊处置纪律、设施接入计划与工具说明。

## 完成判据
A 项在隔离配置根的成功/拒绝/未知/取消/恢复/重复 + 各持久化/引用写入/激活故障窗口反例通过；
关键断言反向突变有效；定向与助手全量回归与同条件基线逐 testId 比较；
会诊无未闭合 MUST/IMPORTANT；证据绑定最终代码版本；B–F 生产前置如实标为未验收。


## findings: _workflow/r56-reference-activation-wiring-2026-09-29/findings.md L1-L70 SHA256=48ddc0947bc9142687f9b4604a6de291958ce8972b81d7d088d0e47f4200e31b

# 本批发现与结论（R5.6 A 项：真实引用写入 + candidate→active 激活）

开工 HEAD `22ccd6ee2721abb1642535253997c57dbf9020d6`（分支 `main-OldTeaBag-B168`）。以下 F-1–F-6 为**开工只读审计**；
F-7–F-11 为**实施后**的结论与边界；`F-12` 为 owner 检查点。

## F-1 跨程序集可达性（决定 A 项「真实引用服务」能否最小接入）

- `MultiplayerHoeingAssistant` 生产源码内 `using BetterGenshinImpact` 命中 **0**；csproj 无对 `BetterGenshinImpact` 的 `ProjectReference`（只有部署复制 Target）。
- W4 引用服务 `BetterGenshinImpact/Service/OneDragon/OneDragonConfigReferenceService.cs` 为**该程序集内 `internal static`**，BGI 的 `InternalsVisibleTo` 只授予 `BetterGenshinImpact.UnitTest`。
- ⇒ 助手进程内**无法**调用 W4。**生产绑定（哪一端提供引用写方／是否下沉共享库／是否经既有 IPC 委派）无权威材料裁决** ⇒ 登记为 F-12 owner 检查点；本批不把它伪装成已接线。
- 本批在隔离根内接入的是**真实文件写方**（经事务注入端口；与既有 `quiesce`/`stageHook` 同模式），验证的纪律为：真实副作用 → 持久化 → 逐项读回确认 → **才**推进阶段标记。

## F-2 阶段标记只推进阶段（本批先要推翻的旧语义）

- `MarkReferenceUpdateCompleted()` = `Advance(ReferenceUpdating)`；`MarkActivated()` = `Advance(Activated)`（`MigrationSwitchTransaction.cs:495–496`）：不执行任何引用写入、不执行激活。
- 后果（已由夹具钉死）：零文件写入即可从 `SnapshotReady` 推进到 `Activated`，并满足 `RehearseRollback`（要求 `Activated`）与 `Commit`，形成**假成功**。
- 本批据此在注入真实端口后**拒绝**这两个入口（`real_side_effects_required`）；`effectService: null` 只保留给未接线的旧夹具，不构成生产路径。

## F-3 candidate→active 的承载与消费点

- 承载字段：`WorkflowDocument.Activation.Status`（`WorkflowModels.cs:64/228`）；正常可启动/可编辑流程的取值是 `"active"`。
- R1 迁移器产出 `"activation": { "status": "candidate-ready" }`（`Test/OneDragonMigration/Core/OneDragonMigrationEngine.cs:552,596`）。
- **激活写入入口当前不存在**：`TaskCenterHost:188–198/248–249/907–909/1068–1070`、`WorkflowPlanner:115–116`、`TaskCenterPanelViewModel:175–177/407`、`EditParts:491–492` 全为「激活归 R5 专用入口」的**拒绝/只读**路径。
- ⇒ 本批新增的激活动作是该承载上**第一条真实写入路径**，只在事务内、隔离根内生效。

## F-4 / F-5 真实文件写方

- 流程定义（`*.flow.json`）：既有真实服务 `WorkflowStore`（同程序集、可达）——原子写（临时文件 + Move）、写前备份、乐观并发（`expectedRevision` = 盘上字节 SHA-256，写时实时重算）、失败响亮抛错。
- 一条龙配置（`User/OneDragon/*.json`）：既有真实实现是 BGI 侧 W4（`TaskConfigurationContract` 逐文件互斥 + 临时文件 + Move + 保留 BOM + 写前字节复检）；助手侧无对应实现（见 F-1）。

## F-6 精确写集声明面与原有缺口

- `ChangedFiles`（`Added`/`Modified`/`Deleted`）即本事务的精确写集声明面；回滚按归属恢复/删除。
- 缺口：事务内**没有任何一步真正把新内容写入配置根**——「引用更新/激活」两步只是阶段推进；`ApplyRepresentativeChanges` 只写演练副本。
- 本批补上该缺口，并把「声明写集 ↔ 实际写入 ↔ 盘上读回」三者绑定为同一组证据。

## F-7 实施（最小接线）

- 新增 `IMigrationEffectService` + `WorkflowFileMigrationEffectService`（`MigrationReferenceActivation.cs`）：
  - `ApplyReferenceUpdate`：`Added`+`NewContent`（新增文件）与 `Modified`+`RenameFrom/To`（引用重命名，助手侧资源引用面 = `nodes[].ref.config`，与 BGI W4 组名重命名同构）；逐文件临时文件+同目录替换、保留 BOM、写前字节复检、解析失败隔离跳过（绝不回空覆盖）、写失败回报**已落盘文件数**。
  - `Activate`：`candidate-ready→active` 读改写 + 原子替换 + 前置状态校验（已是目标态 ⇒ 零写入幂等）。
  - 结果四分类 `Succeeded/Rejected/Unknown/Cancelled`，`Unknown` 语义 = 写后不明/fail-closed。
- `MigrationSwitchTransaction`：`ApplyReferenceUpdate(plan)`、`ActivateCandidate(request)`；写集声明校验（非空/路径安全/无重复/逐项等于已登记变更归属且无漏项；`Deleted` 不支持）→ 真实副作用 → 语义+字节读回 → 才 `WriteManifest` 推进阶段；**写集外零改动**核对；提交前复核写集与激活记录；回滚先撤销激活（读回→反向→再读回）再恢复旧字节并要求旧态一致；manifest 增 `realEffectsRequired`/`referenceWriteSet`/`activationRecord` 并纳入完整性与结构不变量（篡改后重算摘要仍判无效）。

## F-8 证据（绑定最终代码版本）

- 定向 **99/99**（同条件开工基线 70/70）；助手全量 **1599 通过 / 2 跳过 / 0 失败 / 1601**（同条件开工基线 **1570/2/0/1572**；testId added=29 / removed=0 / changed=0 / unchanged=1572）。
- **17 项反向突变**：baseline Passed / mutant Failed / restored Passed，构建 exit 0、mutant 测试 exit>0 且命中具名断言，源码**逐字节恢复**（raw SHA-256 `4122B45C7712…`）。
- 状态/并发/故障 **23 行矩阵全部 covered**，每行绑定具名反例与有效突变。
- 部署目标：全部构建/测试带 `-p:DeployToBgiTools=false`；部署目录 1158 文件、最新写入时间 2026-09-27 09:29:33（早于本批）⇒ 本批未留下可观察变化（不主张「过程中从未写入」）。
- BGI 侧未修改任何文件（`git status`/`git diff` 可核）⇒ 未跑 BGI 回归。

## F-9 能力边界（如实）

- 只覆盖**隔离配置根**内的真实副作用；**不**覆盖真实 `User` 目录、真实入口/生产消费、跨进程崩溃（本批为同进程故障注入）、W4 跨程序集绑定、目标机路径身份（UNC/映射盘/SUBST/8.3）、助手元数据恢复（B 项）。
- 突变 M1 一行曾以「副作用之前先落阶段」定义，但被 `MarkBlocked` 覆盖阶段而**未被夹具检出**，已改定义为「不读回确认就推进阶段」并重新取得判别力；M3/M5/M14 为**整块削弱**型突变（同一位点多层独立复核，单独削弱任一层会被另一层拦下）——此点如实登记，不主张「每层都有独立判别力」。
- `DeleteRecordedAdditions` 对「期望文件却出现目录」计为未完成（既有行为），本批回滚后另加**旧态字节一致性**与**激活状态已回退**两项读回核对。

## F-10 未闭合与门禁

- **B–F 六项启用前置未闭合**（助手元数据恢复范围、生产消费检查点、真实静止窗口、真实入口回执、目标机路径身份）；真实 `User` 切换、生产构造、E3/E4/E5、热键、R5.8 签署继续关闭。
- 既有建议级残项 `R56-D1` 状态未变；本批未改其触发条件。

## F-12 owner 检查点（正确性相关，本批不自行裁决）

- **问题**：生产环境下「真实引用写方」应由哪一端提供——(a) BGI 侧直调 W4 `OneDragonConfigReferenceService`（需把它从 `internal` 提升或经 BGI 侧入口暴露）；(b) 下沉为共享库供助手与 BGI 共用；(c) 经既有 IPC 委派给 BGI 侧执行。
- **证据**：F-1（零程序集引用 + `internal` + IVT 不含助手）；本批已在隔离根内提供等价的助手侧真实写实现并通过全部反例。
- **风险**：若生产直接采用助手侧实现而对 W4 的既有利害（受保护文件跳过、`ChangeReport` 语义、逐文件互斥表）覆盖不足，可能出现两套引用改写语义分叉。
- **已尝试处理**：本批不发明协议，只保留扩展点（端口注入）与最小实现；未改 BGI 任何文件。
- **建议的有限范围与验收条件**：由 owner 选定 (a)/(b)/(c) 之一；若选 (a)/(b)，需补「W4 语义等价/映射」定向回归；若选 (c)，需补 IPC 委派契约与失败责任。完成前**生产接线保持关闭**。


## budget: _workflow/r56-reference-activation-wiring-2026-09-29/budget.md L1-L5 SHA256=5121fb363d0ab3c1160dd18443c026bb5f547c402144c3ad94c7a687ac4ffe5a

# 会诊与预算台账

- 上限：本独立子批累计 **8 次**会诊请求（含失败/超时；本地预检拦截未发出不计）。
- 固定模型/强度：`gpt-6-astra` / `medium`（只读会诊渠道）。
- 已用：见收口登记（R5.3 §24.129）。


## contract: _workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md L1-L47 SHA256=fc65079cf5684447a1fde127ef9570c54111faa9544bba228f2c1ddcdbf70545
第 1 轮会诊报告原文（10 项发现）。
# 第 1 轮会诊报告（原文，gpt-6-astra / medium / attempts=1；只读）

**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收 A 项。** 以下结论来自所附源码与 diff 的静态分析；未调用工具、未执行命令或测试。

1. **MUST：激活会把真实引用漂移重新登记为合法状态，随后允许提交。**
   位置：`MigrationSwitchTransaction.cs`，`ActivateCandidate`。
   引用更新 A→B 成功后，将同一文件锁外改为引用 X、状态仍为 `candidate-ready`；激活入口没有先核对已确认哈希，真实服务会把这份漂移文件改为 `active`，随后事务将其新哈希同时写入 `ReferenceWriteSet` 和 `ActivationRecord`。演练、提交复核因此均可通过，引用 X 被接受。应在激活前核对既有证据，并绑定实际修改所依据的字节版本，避免同步哈希时吸收漂移。

2. **MUST：声明为 Added、但实际从未由事务创建的文件会被回滚删除。**
   位置：`WorkflowFileMigrationEffectService.ApplyReferenceUpdate`；`DeleteRecordedAdditions`。
   可确定的路径：快照时文件不存在→登记 Added→其他写方创建同路径文件→真实写方返回 `added_target_already_exists`→事务 Blocked→回滚根据登记直接删除该文件并报告成功。这里把“计划新增”当成“已经拥有”，违反事务外文件保留要求。需要区分写入意图和实际创建归属；归属无法确认时应阻断删除。此外，Added 使用 `overwrite: true`，存在检查之后的同路径创建也可能被覆盖。

3. **MUST：写集外新增文件完全漏检，可带着额外副作用成功提交。**
   位置：`MigrationSwitchTransaction.ApplyReferenceUpdate` 的“写集外零改动”循环。
   该循环只遍历 `m.FileHashes`。让现有 `ScriptedEffectService.ExtraWrite` 写入一个基线中不存在、也未登记的新路径，引用更新仍可成功；后续激活和提交也不检查该文件。现有 `WriteOutsideDeclaredWriteset_Blocks` 只覆盖基线文件修改、删除，不能证明“写集外零改动”。应比较完整文件集合及写集外字节，并区分操作前已存在的事务外文件。

4. **MUST：manifest 的真实证据要求可通过改字段并重算摘要绕过。**
   位置：`IsManifestIntegrityValidCore`、`Commit`。
   在合法真实事务上将 `RealEffectsRequired=false`、清空写集和激活记录并重算摘要，结构校验仍接受；已提交记录仍能通过授权检查。另有独立缺口：真实模式下，没有验证写集与 `ChangedFiles` 精确对应、身份键唯一，以及 `ActivationRecord.AfterHash` 与对应写集哈希一致。应补齐这些关系不变量，并防止真实事务降级为夹具模式。普通摘要不提供来源认证，但上述结构矛盾本身即可被拒绝。

5. **MUST：部分写入失败后的回滚读回检查会空过，可能假报完整回滚。**
   位置：`CompleteRollback`。
   `ReferenceWriteSet` 只在整批成功后保存，因此部分写后 Unknown、或阶段发布前失败时，它仍为空。回滚恢复文件后，仅遍历这个空集合确认旧字节。利用已有 `fileRestoredHook`，在恢复某文件后将其改回错误字节而不抛异常，回滚就会返回 `RolledBack`。应核验实际恢复范围的完整基线字节，不能依赖“成功写集”覆盖失败路径。

6. **IMPORTANT：确认写集之后仍能修改变更登记，破坏精确写集约束。**
   位置：`RecordChanges`、`RecheckReferenceWriteSet`、`Commit`。
   真实事务到 `Activated` 后仍可登记新的 Added，或将原 Modified 改为 Deleted。重新演练后可以提交，因为提交没有重新检查登记与确认写集的一致性；幂等引用更新也直接跳过计划校验。应冻结真实写入后的登记，或使既有证据失效并要求重新建立完整事务证据。

7. **IMPORTANT：`ActivateCandidate` 没有限定 candidate-ready→active，且首次调用可零写入成功。**
   位置：`ValidateActivationRequest`、`TrySetActivationStatus`。
   校验只要求两个状态非空且不同，因而允许任意状态转换。真实服务还会在当前状态已经等于目标时直接返回 `Ok(0)`：例如基线已经是 `active`，引用更新后首次调用正常激活请求，也会记录未经证实的 `BeforeStatus=candidate-ready` 并推进阶段。事务入口应限定正向状态转换；已完成操作的幂等性应依据已持久化事务证据，反向操作保留给回滚内部使用。

8. **IMPORTANT：副作用或语义读回抛异常时，没有转为 Blocked，允许重复执行。**
   位置：`ApplyReferenceUpdate`、`ActivateCandidate` 的端口调用。
   两个入口均没有包围副作用和语义读回的异常处理。端口写入后抛异常，阶段仍保留在调用前，下一次调用会再次触发副作用。真实实现也存在可抛出的文档形状：例如 `nodes` 中含标量，`node?["ref"]` 并不能安全处理非对象节点。应将无法确认是否产生副作用的异常收敛为 Unknown/Blocked，并覆盖“首文件已写、后续处理抛异常”的反例。

9. **IMPORTANT：关键夹具存在被其他失败条件遮蔽的断言，现有材料不足以支持全部闭合。**
   位置：`R56ReferenceActivationWiringTests.cs`、`risk-matrix.json`。
   - manifest 篡改测试的第二分支沿用第一分支已清空的写集；即使移除“必须有激活记录”的校验，该分支仍可因写集为空而通过。
   - “副作用后取消”只设置 `CompletedWrites=1`，脚本实际没有写入，未覆盖真实部分副作用。
   - 回滚成功测试最终恢复快照，本身不能证明先执行了激活撤销；移除撤销步骤仍可能满足最终字节断言。
   - REF-C3 对应测试是顺序回滚，不是并发交错测试。
   应让各反例从独立合法状态开始，并分别杀掉对应单点突变。所附材料没有提供 17 项突变的具体补丁和逐项失败输出；矩阵多行同时列有突变 ID 和“无有效反向突变”说明，无法据此独立确认突变结论。

**建议级：**修正文档中超出证据的表述。`MigrationRehearsal.Run` 仍使用无端口的旧阶段标记，报告却显示“激活（candidate→active）”；应明确其为旧事务框架演练。正常路径的阶段赋值确实位于读回之后，但这不足以证明上述失败、漂移和恢复路径已经闭合。

建议先修复上述 MUST，再补齐 IMPORTANT 的独立反例与突变证据后复审。B–F、生产引用写方 owner 裁决及真实生产接线均未作为本批缺陷要求补齐。**本范围内发现了已确证、仍未闭合的 MUST/IMPORTANT。**


## contract: _workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round2-report.md L1-L26 SHA256=59a4e893d7273d50843cb6881dc3352a1ff7c8abbf708f305b66df9e98818021
**节选**：第 2 轮报告前 5 项（MUST 1–5）；全文见同目录文件（本批已逐条按原级修复）。
# 第 2 轮（验证轮）会诊报告（原文，gpt-6-astra / medium / attempts=1；只读）

**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收。** 以下仅依据所附源码、夹具与台账进行静态核对；未调用工具、未执行命令或测试。

1. **MUST：新增文件作为激活目标时，正常回滚必然发生归属冲突；他方文件还可能先被改写。**
   位置：`MigrationSwitchTransaction.cs`，`CompleteRollback`、`UndoActivation`、`OwnershipConflictReason`。
   可确定的正常路径：新增 candidate-ready 文件 → 引用更新确认 → 激活该新增文件 → 演练、提交 → 回滚。写集保存的是 **active 字节哈希**，但回滚先执行 `UndoActivation`，将文件改为 candidate-ready，再检查归属。因此它把自己的撤销结果判成 `rollback_addition_not_owned`，无法删除新增文件，重复恢复仍不能收敛。
   更严重的是，若该新增激活目标已被他方替换，且状态仍为 active，`UndoActivation` 会在归属检查之前改写他方文件；随后阻断也无法兑现“他方文件保持不变”。
   演练使用 `DeleteRecordedAdditions(..., requireOwnershipEvidence:false)`，既不撤销真实激活，也不运行真实根的归属检查，所以能通过并掩盖上述失败。需要在任何撤销写入前确认归属，并为合法撤销后的字节建立可恢复的证据。

2. **MUST：回滚归属保护仍可通过降级 manifest 绕过。**
   位置：`CompleteRollback`。
   提交和授权已使用 `m.RealEffectsRequired || _effects is not null`，但归属预检及删除参数仍只使用 `m.RealEffectsRequired`。
   在 `AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback` 的拒绝状态上，将该字段改为 false 并重算摘要，manifest 可以通过校验；同一个真实端口实例随后回滚，会跳过归属检查并删除他方同名文件。**MUST-2 尚未全面闭合**。真实根的保护条件也必须绑定实例。

3. **MUST：激活前哈希检查没有绑定实际写入所依据的版本，漂移吸收窗口仍存在。**
   位置：`ActivateCandidate` 与 `WorkflowFileMigrationEffectService.Activate`。
   新检查能拦截进入激活前已经发生的漂移，但检查后还会调用状态读取，再进入端口重新读取文件。若这期间引用 B 被改成 X、状态仍为 candidate-ready，真实服务会基于 X 激活；其内部写前复检只比较自己的两次读取，随后事务仍将 X 的哈希登记为合法证据。
   无需概率性测试：可在状态读取端口返回前注入该改动，再委托真实 `Activate`。当前 `MigrationActivationRequest` 没有期望字节哈希，不能传递并验证已确认版本。**MUST-1 的原反例已拦截，但原要求中的版本绑定尚未闭合。**

4. **MUST：提交面的文件集合检查并非相等检查，允许缺失或被修改的写集外基线进入提交。**
   位置：`UnexpectedFileReason`、`Commit`。
   `UnexpectedFileReason` 只检查 `current ⊆ expected`，没有检查 expected 中的文件是否全部存在；提交也只复核写集内哈希。
   确定路径：基线含 A、B，只更新和激活 A；引用更新完成后删除 B，再演练、提交。写集复核通过，集合检查没有发现额外文件，快照仍完整，因此提交成功。修改 B 的字节也同样漏检。更新阶段原有的全基线检查不能覆盖后续变化。
   原 MUST-3 的“写集外新增”反例已经修复，但声称的提交面完整集合约束尚未实现。



## source: MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs L1-L413 SHA256=c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// **真实副作用结果类别**（R5.6 A 项）：成功必须与「确定拒绝／写后不明／取消」**可区分**；
/// 未知一律 fail-closed；`CompletedWrites` 只统计**已实际落盘**的文件数，失败方不得虚报为 0 以外的机会。
/// </summary>
public enum MigrationEffectOutcome
{
    Succeeded = 0,
    Rejected = 1,
    Unknown = 2,
    Cancelled = 3,
}

/// <summary>真实副作用结果。`Reason` 非空即结构化原因码（供事务落盘与取证）。</summary>
public sealed record MigrationEffectResult(MigrationEffectOutcome Outcome, string Reason, int CompletedWrites)
{
    public static MigrationEffectResult Ok(int writes) => new(MigrationEffectOutcome.Succeeded, "", writes);
    public static MigrationEffectResult Rejected(string reason, int writes = 0) => new(MigrationEffectOutcome.Rejected, reason, writes);
    public static MigrationEffectResult Unknown(string reason, int writes = 0) => new(MigrationEffectOutcome.Unknown, reason, writes);
    public static MigrationEffectResult Cancelled(string reason, int writes = 0) => new(MigrationEffectOutcome.Cancelled, reason, writes);
}

/// <summary>
/// 一条真实引用写入目标。两种**真实**语义：
/// - `Added` + `NewContent`：在配置根内**新建**文件（迁移产生的新候选文件）；
/// - `Modified` + `RenameFrom`/`RenameTo`：对既有文件的**引用重命名**（与 BGI 侧 W4
///   `OneDragonConfigReferenceService.RenameGroupReferences` 同语义；助手侧流程文件的对应面是 `nodes[].ref.config`）。
/// </summary>
public sealed record MigrationReferenceWriteTarget(
    string Path,
    ChangeKind Kind,
    string? NewContent = null,
    string? RenameFrom = null,
    string? RenameTo = null);

/// <summary>真实引用更新计划（一次事务内的完整写集声明）。</summary>
public sealed record MigrationReferenceUpdatePlan(IReadOnlyList<MigrationReferenceWriteTarget> Targets);

/// <summary>
/// 真实 candidate→active 激活请求（D13 三态：只接受 `candidate-ready` 候选）。
/// `ExpectedContentHash` ＝ **本次写入所依据的字节版本**（事务的已确认写集哈希）：端口在写入前必须核对盘上字节哈希，
/// 不符即拒绝——防止「检查之后、写入之前」的锁外改动被连同激活一起合法化（会诊第 2 轮 MUST-3）。
/// </summary>
public sealed record MigrationActivationRequest(string Path, string ExpectedBeforeStatus, string TargetStatus,
    string? ExpectedContentHash = null);

/// <summary>
/// **真实副作用端口**（R5.6 A 项接线面）。事务只依赖本接口；生产用下方
/// <see cref="WorkflowFileMigrationEffectService"/>（真实文件写入），夹具用可注入故障的实现。
/// </summary>
public interface IMigrationEffectService
{
    /// <summary>执行真实引用更新（逐文件）；返回类别、原因与**已落盘文件数**。</summary>
    MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan);

    /// <summary>执行真实激活（读改写 + 原子替换）；返回类别、原因与已落盘文件数。</summary>
    MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request);

    /// <summary>语义读回：引用是否已全部由 `RenameFrom` 改为 `RenameTo`（`Added` 目标恒 false，交由字节读回）。</summary>
    bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail);

    /// <summary>语义读回：文件的 `activation.status` 现值（读不到时 status 为空且 detail 说明原因）。</summary>
    bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail);
}

/// <summary>
/// **R5.6 真实引用写入／激活实现（助手侧，隔离根内）**。
/// 纪律：只写配置根内**已声明**目标（相对路径安全 + 根内）；逐文件**临时文件 + 同目录替换**；
/// 保留原文件 BOM 形态；解析失败即隔离跳过（绝不回空覆盖）；每次写入都重新读盘校验前置条件（拒绝过期写入）。
/// 本实现**不**接触真实 `User` 目录：根由调用方传入，事务已保证根隔离与静止窗口。
/// </summary>
public sealed class WorkflowFileMigrationEffectService : IMigrationEffectService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>真实写入用序列化选项：保留人类可读的非 ASCII（不被转义为 `\uXXXX`），便于写入后人工比对与评审。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
    {
        var root = Path.GetFullPath(configRoot);
        var writes = 0;
        foreach (var target in plan.Targets)
        {
            var target_ = target ?? throw new ArgumentNullException(nameof(plan));
            if (!TryResolve(root, target_.Path, out var full, out var reason))
                return MigrationEffectResult.Rejected(reason, writes);
            switch (target_.Kind)
            {
                case ChangeKind.Added:
                {
                    if (target_.NewContent is null)
                        return MigrationEffectResult.Rejected("added_target_without_content:" + target_.Path, writes);
                    if (File.Exists(full))
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(full))
                    {
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 写失败：`CompletedWrites` 如实反映此前**已落盘**的文件数（不得假报 0）
                        return MigrationEffectResult.Unknown("reference_write_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    writes++;
                    break;
                }
                case ChangeKind.Modified:
                {
                    if (string.IsNullOrEmpty(target_.RenameFrom) || string.IsNullOrEmpty(target_.RenameTo))
                        return MigrationEffectResult.Rejected("modified_target_without_rename:" + target_.Path, writes);
                    if (!File.Exists(full))
                        return MigrationEffectResult.Rejected("modified_target_missing:" + target_.Path, writes);
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(full); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return MigrationEffectResult.Unknown("reference_read_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    var hasBom = HasUtf8Bom(bytes);
                    bool renamedOk;
                    int renamed;
                    string? text;
                    try
                    {
                        renamedOk = TryRenameReferences(DecodeText(bytes), target_.RenameFrom!, target_.RenameTo!, out renamed, out text);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
                    {
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    if (!renamedOk)
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path, writes);   // 坏文件隔离：不覆盖
                    if (renamed == 0)
                        return MigrationEffectResult.Rejected("no_reference_match:" + target_.Path, writes);            // 无可更新引用 ⇒ 确定拒绝
                    // **写前复检**：读后被锁外写方改动 ⇒ 放弃本次写入（不静默覆盖）
                    try
                    {
                        if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
                            return MigrationEffectResult.Unknown("reference_target_changed_after_read:" + target_.Path, writes);
                        AtomicWrite(full, EncodeText(text!, hasBom), hasBom);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return MigrationEffectResult.Unknown("reference_write_io_failed:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    writes++;
                    break;
                }
                default:
                    return MigrationEffectResult.Rejected("unsupported_change_kind:" + target_.Path, writes);
            }
        }
        return MigrationEffectResult.Ok(writes);
    }

    public MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request)
    {
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, request.Path, out var full, out var reason))
            return MigrationEffectResult.Rejected(reason, 0);
        if (!File.Exists(full))
            return MigrationEffectResult.Rejected("activation_target_missing:" + request.Path, 0);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MigrationEffectResult.Unknown("activation_read_io_failed:" + request.Path + ":" + ex.GetType().Name, 0);
        }
        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))
            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）
        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);
        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入
        try
        {
            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            if (!string.IsNullOrEmpty(request.ExpectedContentHash)
                && !string.Equals(Sha256Hex(preWrite), request.ExpectedContentHash, StringComparison.Ordinal))
                return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MigrationEffectResult.Unknown("activation_write_io_failed:" + request.Path + ":" + ex.GetType().Name, 0);
        }
        return MigrationEffectResult.Ok(1);
    }

    public bool TryReadReferenceState(string configRoot, MigrationReferenceWriteTarget target, out string detail)
    {
        detail = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, target.Path, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        if (target.Kind == ChangeKind.Added)
        {
            // 新增目标的**写回确认**＝内容逐字节等于声明内容（不适用「引用改写」语义）
            if (target.NewContent is null) { detail = "added_target_without_content"; return false; }
            try
            {
                var expected = Utf8NoBom.GetBytes(target.NewContent);
                if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(expected)) { detail = "added_content_mismatch"; return false; }
                detail = "added_content_confirmed";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail = "read_io_failed:" + ex.GetType().Name;
                return false;
            }
        }
        if (target.Kind != ChangeKind.Modified) { detail = "not_a_rename_target"; return false; }
        try
        {
            var text = DecodeText(File.ReadAllBytes(full));
            if (!TryParseDocument(text, out var doc, out detail)) return false;
            var stale = CountMatchingReferences(doc!, target.RenameFrom!);
            if (stale is null) { detail = "document_shape_unusable"; return false; }
            if (stale > 0) { detail = "stale_reference_remaining:" + stale; return false; }
            var updated = CountMatchingReferences(doc!, target.RenameTo!);
            if (updated is null) { detail = "document_shape_unusable"; return false; }
            if (updated == 0) { detail = "renamed_reference_absent"; return false; }
            detail = "renamed_reference=" + updated;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "read_io_failed:" + ex.GetType().Name;
            return false;
        }
    }

    public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
    {
        status = "";
        detail = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, relPath, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        try
        {
            var text = DecodeText(File.ReadAllBytes(full));
            if (!TryParseDocument(text, out var doc, out detail)) return false;
            var value = doc!["activation"]?["status"];
            if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s))
            {
                detail = "activation_status_absent";
                return false;
            }
            status = s;
            detail = "activation_status=" + s;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = "read_io_failed:" + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>相对路径安全解析（拒绝绝对/`..`/控制字符/越根；父链无链接由事务层另行保证）。</summary>
    private static bool TryResolve(string root, string? rel, out string full, out string reason)
    {
        full = "";
        reason = "";
        if (!MigrationSwitchTransaction.IsSafeRelativePath(rel)) { reason = "unsafe_path:" + rel; return false; }
        var rootFull = MigrationSwitchTransaction.EnsureTrailingSeparator(root);
        full = Path.GetFullPath(Path.Combine(rootFull,
            MigrationSwitchTransaction.NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { reason = "path_outside_root:" + rel; return false; }
        return true;
    }

    private static void AtomicWrite(string full, byte[] bytes, bool hasBom, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(full)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, full, overwrite: overwrite); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DecodeText(byte[] bytes)
        => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    private static byte[] EncodeText(string text, bool hasBom)
    {
        var payload = Utf8NoBom.GetBytes(text);
        if (!hasBom) return payload;
        var withBom = new byte[payload.Length + 3];
        withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
        payload.CopyTo(withBom, 3);
        return withBom;
    }

    private static bool TryParseDocument(string text, out JsonObject? doc, out string reason)
    {
        doc = null;
        reason = "";
        try
        {
            doc = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            reason = "json_invalid";
            return false;
        }
        if (doc is null) { reason = "json_shape_invalid"; return false; }
        return true;
    }

    private static bool TryRenameReferences(string text, string from, string to, out int renamed, out string? output)
    {
        renamed = 0;
        output = null;
        if (!TryParseDocument(text, out var doc, out _)) return false;
        var count = RenameReferences(doc!, from, to);
        if (count is null) return false;                 // 形状不可用 ⇒ 隔离跳过（不写回、不改变工件形状）
        renamed = count.Value;
        output = doc!.ToJsonString(WriteOptions);
        return true;
    }

    /// <summary>
    /// 真实引用重命名：遍历 `nodes[].ref.config`（助手侧流程文件的资源引用面）。
    /// **形状异常即拒绝**（标量节点、非对象 `ref` 等）：助手侧 `WorkflowStore` 对 `nodes` 非数组/元素非对象按隔离处理，
    /// 本服务不得把无法安全解析的文档改写后写回（会改变工件形状）；返回 null 表示文档形状不可用。
    /// </summary>
    private static int? RenameReferences(JsonObject doc, string from, string to)
    {
        var renamed = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;         // 标量/数组元素 ⇒ 形状不可用
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;       // ref 非对象 ⇒ 形状不可用
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (!value.TryGetValue<string>(out var current) || !string.Equals(current, from, StringComparison.Ordinal)) continue;
            reference["config"] = to;
            renamed++;
        }
        return renamed;
    }

    private static int? CountMatchingReferences(JsonObject doc, string name)
    {
        var count = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (value.TryGetValue<string>(out var current) && string.Equals(current, name, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    private static bool TrySetActivationStatus(string text, string expectedBefore, string targetStatus,
        out string reason, out string? output, out bool alreadyTarget)
    {
        reason = "";
        output = null;
        alreadyTarget = false;
        if (!TryParseDocument(text, out var doc, out var parseReason)) { reason = "activation_document_unusable:" + parseReason; return false; }
        if (doc!["activation"] is not JsonObject activation) { reason = "activation_block_missing"; return false; }
        if (activation["status"] is not JsonValue value || !value.TryGetValue<string>(out var current) || string.IsNullOrEmpty(current))
        {
            reason = "activation_status_absent";
            return false;
        }
        if (string.Equals(current, targetStatus, StringComparison.Ordinal)) { alreadyTarget = true; return true; }
        if (!string.Equals(current, expectedBefore, StringComparison.Ordinal)) { reason = "activation_precondition_mismatch:" + current; return false; }
        activation["status"] = targetStatus;
        output = doc.ToJsonString(WriteOptions);
        return true;
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}


## source: MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs L1-L1646 SHA256=56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>事务阶段（**持久化**；除 Committed 外一律不可生产执行）。</summary>
public enum MigrationStage
{
    None = 0,
    Snapshotting = 1,
    SnapshotReady = 2,
    ReferenceUpdating = 3,
    Activated = 4,
    Committed = 5,
    RollingBack = 6,
    RolledBack = 7,
    Blocked = 8,
}

/// <summary>变更归属（回滚据此判定「本事务新增」；**不再删除未登记文件**）。</summary>
public enum ChangeKind { Added = 0, Modified = 1, Deleted = 2 }

/// <summary>一条变更记录（相对路径 + 归属）。</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public ChangeKind Kind { get; set; }
}

/// <summary>迁移 manifest（**全字段完整性 + 结构与状态不变量**）。</summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    /// <summary>不可变快照身份（＝开事务时的会话标识）；快照路径**精确**绑定本事务，不用前缀判定。</summary>
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>本事务变更归属（回滚据此判定新增；无记录 ⇒ 不删除任何文件）。</summary>
    [JsonPropertyName("changedFiles")] public List<ChangeRecord> ChangedFiles { get; set; } = [];
    [JsonPropertyName("snapshotManifestHash")] public string SnapshotManifestHash { get; set; } = "";
    /// <summary>**基线是否完成**（快照清单已发布）。未完成 ⇒ 允许安全中止（**不得**用部分快照恢复）。</summary>
    [JsonPropertyName("baselineCompleted")] public bool BaselineCompleted { get; set; }
    [JsonPropertyName("stage")] public MigrationStage Stage { get; set; }
    /// <summary>唯一提交标记；提交前为空、非提交态必为空。</summary>
    [JsonPropertyName("commitMarker")] public string? CommitMarker { get; set; }
    [JsonPropertyName("rollbackRehearsed")] public bool RollbackRehearsed { get; set; }
    /// <summary>演练范围（绑定快照清单 + 变更归属；变更集改变即失效）。</summary>
    [JsonPropertyName("rehearsalScope")] public string? RehearsalScope { get; set; }
    /// <summary>结构化 blocked 原因（非空 ⇒ 禁止提交与生产执行）。</summary>
    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }
    /// <summary>静止窗口取得时刻（须与 QuiesceSessionId 同会话才算有效）。</summary>
    [JsonPropertyName("quiescedAtUtc")] public DateTimeOffset? QuiescedAtUtc { get; set; }
    /// <summary>取得静止窗口的会话标识（重启后不得凭历史记录提交）。</summary>
    [JsonPropertyName("quiesceSessionId")] public string? QuiesceSessionId { get; set; }
    /// <summary>静止窗口**代次**（释放即失效；同实例重新取锁不得复用历史资格）。</summary>
    [JsonPropertyName("quiesceGeneration")] public int QuiesceGeneration { get; set; }
    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
    /// <summary>
    /// **真实引用写入的读回证据**（本事务写集声明；`path → 写入后盘上字节 SHA256`；大小写不敏感身份）。
    /// 只有**逐项读回确认**后才写入；空表示尚未完成真实引用更新。
    /// </summary>
    [JsonPropertyName("referenceWriteSet")] public Dictionary<string, string> ReferenceWriteSet { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
    /// </summary>
    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }
    /// <summary>
    /// **本事务是否由真实副作用端口建立**（`BeginTransaction` 时按实例是否注入端口写入；受完整性摘要覆盖）。
    /// `true` ⇒ 阶段 `ReferenceUpdating/Activated/Committed` 必须携带真实写集/激活读回证据，且阶段标记只能由
    /// 真实副作用 + 读回确认推进；入口回绝 `MarkReferenceUpdateCompleted`/`MarkActivated`。
    /// `false` ⇒ 仅限未注入端口的只读夹具（旧行为保留）。
    /// </summary>
    [JsonPropertyName("realEffectsRequired")] public bool RealEffectsRequired { get; set; }
}

/// <summary>真实激活读回证据（D13：`candidate → active`）。</summary>
public sealed class MigrationActivationRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("beforeStatus")] public string BeforeStatus { get; set; } = "";
    [JsonPropertyName("afterStatus")] public string AfterStatus { get; set; } = "";
    [JsonPropertyName("afterHash")] public string AfterHash { get; set; } = "";
}

/// <summary>事务操作结果。</summary>
public sealed record MigrationResult(bool Success, string Reason, MigrationStage Stage)
{
    public static MigrationResult Ok(MigrationStage s) => new(true, "", s);
    public static MigrationResult Fail(string reason, MigrationStage s) => new(false, reason, s);
}
/// <summary>
/// **R5.6 事务迁移切换（v3：按第 2 轮会诊 7 项必改重构）**。
/// 不变量：①串行边界覆盖全部入口（恢复/变更/授权/执行/回滚均须本实例持锁，且实例内串行；
/// TryRunProduction 的「授权+执行」在同一临界区）；②回滚可恢复（先落 RollingBack 再做 IO，
/// 恢复路径幂等续做）；③静止窗口覆盖全程且绑定会话（重启后不得凭历史时间戳提交）；
/// ④身份/路径前置校验（事务号安全、快照路径属本事务、根绑定、链接拒绝）；
/// ⑤未决事务存在时拒绝开新事务，事务号/快照目录不复用；⑥变更归属按基线校验；
/// ⑦结构与状态不变量 + 全字段完整性双校验，null 字段结构化拒绝。
/// 开发验证只用独立配置根；真实 User 目录切换由 owner 另行下令。
/// </summary>
public sealed class MigrationSwitchTransaction : IDisposable
{
    private readonly string _configRoot;
    private readonly string _transactionRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IDisposable>? _quiesce;
    private readonly bool _requireQuiescence;
    private readonly Action<MigrationStage>? _stageHook;
    private readonly Action<string>? _fileRestoredHook;
    private readonly IMigrationEffectService? _effects;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileStream? _lock;
    /// <summary>**重入守卫**：外部副作用/读回回调执行期间置位；此期间任何变更入口一律拒绝（monitor 可重入，
    /// 单靠 `lock` 不能阻止回调同步重入事务，见会诊第 2 轮 IMPORTANT-7）。</summary>
    private bool _effectCallInProgress;
    private IDisposable? _quiet;
    private bool _quietValid;
    private int _quietGeneration;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null, IMigrationEffectService? effectService = null)
    {
        _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
        _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _quiesce = quiesce;
        _requireQuiescence = requireQuiescence;
        _stageHook = stageHook;
        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
        _effects = effectService;               // **真实副作用端口**：null ⇒ 本实例只能演练阶段推进（旧语义仅保留给只读夹具）
        ValidateRoots();
    }

    /// <summary>权威 D13 激活词（R5.2 §21.2：`candidate → active`）；仅本事务的激活入口使用，不作通用状态改写器。</summary>
    internal const string CandidateReadyStatus = "candidate-ready";
    internal const string ActiveStatus = "active";

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");

    /// <summary>事务号**占用历史**（追加式；重复事务号一律拒绝——不依赖当前 manifest 是否仍在）。</summary>
    public string HistoryPath => Path.Combine(_transactionRoot, "migration-history.txt");
    internal bool HoldsExclusiveLock => _lock is not null;
    internal string SessionId => _sessionId;

    /// <summary>快照路径＝事务根下按「事务号 + 会话」确定性推导（不复用既有目录）。</summary>
    internal string SnapshotPathOf(string transactionId, string sessionId)
        => Path.Combine(_transactionRoot, "snapshot-" + transactionId + "-" + sessionId);

    private void ValidateRoots()
    {
        // **路径命名空间门禁（第 15 轮会诊）**：`\\?\C:\data` 与 `C:\data` 指向同一目录却字符串前缀不匹配，
        // 可绕过「根互不包含」。此处**明确拒绝尚不支持的扩展/设备前缀**，并要求两根同属一种命名空间。
        foreach (var chain in new[] { _configRoot, _transactionRoot })
        {
            if (chain.StartsWith(@"\\?\", StringComparison.Ordinal) || chain.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidOperationException("不支持扩展/设备路径前缀（\\\\?\\、\\\\.\\）：无法保证与普通路径的目录身份一致，拒绝迁移事务：" + chain);
        }
        // **同卷约束（第 16 轮会诊）**：`SUBST X: C:\data` / 映射盘会让两个不同盘符指向同一目录，字符串互不包含且
        // 逐段名称检查也通过 ⇒ 无法证明隔离。处置＝要求两根**同卷**（不同卷直接拒绝；同卷别名由 reparse point 检查拦截）。
        var cfgVolume = (Path.GetPathRoot(_configRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        var txVolume = (Path.GetPathRoot(_transactionRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(cfgVolume, txVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根与事务根必须位于**同一卷**（不同卷可能由 SUBST/映射盘指向同一目录，目录身份不可比较），拒绝迁移事务。");

        var cfgIsUnc = _configRoot.StartsWith(@"\\", StringComparison.Ordinal);
        var txIsUnc = _transactionRoot.StartsWith(@"\\", StringComparison.Ordinal);
        if (cfgIsUnc != txIsUnc)
            throw new InvalidOperationException("配置根与事务根必须同属一种路径命名空间（均为盘符路径或均为 UNC），拒绝迁移事务。");

        var cfg = _configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tx = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (tx.StartsWith(cfg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务根不得位于配置根内（自包含快照会污染备份）。");
        if (cfg.StartsWith(tx, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根不得位于事务根内。");
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (HasReparsePoint(chain))
                throw new InvalidOperationException("根路径链上存在重解析点（junction/符号链接），拒绝迁移事务：" + chain);
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (!IsCanonicalAbsolutePath(chain, out var badSegment))
                throw new InvalidOperationException("根路径存在非规范名（如 NTFS 8.3 短名别名），拒绝迁移事务：" + badSegment
                    + "（根：" + chain + "）");
    }

    /// <summary>链接逃逸防护：路径链上任一层为 reparse point 即拒绝。</summary>
    internal static bool HasReparsePoint(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            dir = dir.Parent;
        }
        return false;
    }

    /// <summary>
    /// 相对路径安全校验（**先拒绝不支持的原始路径**，不做静默裁剪）：绝对路径/盘符/`..`/`.`/空段、
    /// 控制字符、以及段内**前导/尾随空格或尾点**（Windows 会裁剪 ⇒ 别名冲突）一律拒绝。
    /// </summary>
    internal static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        if (rel.Any(char.IsControl)) return false;                 // NUL 等控制字符拒绝（避免路径解析异常/绕过）
        var norm = rel.Replace('\\', '/');
        if (norm.StartsWith('/') || norm.Contains(':')) return false;
        foreach (var seg in norm.Split('/'))
        {
            if (seg is ".." or "." || seg.Length == 0) return false;
            if (seg.EndsWith('.') || seg.StartsWith(' ') || seg.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>路径身份规范化（**只统一分隔符，不裁剪**——裁剪会把合法文件名映射到另一个文件）。</summary>
    internal static string NormalizePath(string? rel) => (rel ?? "").Replace('\\', '/');

    /// <summary>路径**身份键**（Windows 语义：大小写不敏感）——基线、变更记录与实际文件操作三处统一使用。</summary>
    internal static string PathKey(string? rel) => NormalizePath(rel).ToLowerInvariant();

    /// <summary>按身份键查快照哈希（大小写不敏感；避免 `A.json` 与 `a.json` 在 Windows 上互相删改）。</summary>
    internal static bool TryGetHashCaseInsensitive(IReadOnlyDictionary<string, string> hashes, string? path, out string? hash)
    {
        var key = PathKey(path);
        foreach (var p in hashes)
        {
            if (PathKey(p.Key) == key)
            {
                hash = p.Value;
                return true;
            }
        }
        hash = null;
        return false;
    }

    /// <summary>
    /// **规范化名称校验**（Windows 短名/别名防护）：对已存在的每一段，要求该段名称与其父目录**枚举名**大小写不敏感匹配；
    /// 若路径在磁盘上存在、却**未被父目录枚举名匹配**（例：NTFS 8.3 短名别名）⇒ 视为**非规范名**拒绝。
    /// 尚未存在的新路径（新增文件/新目录）不受此限（新名不可能是既有别名）。
    /// </summary>
    internal static bool IsCanonicalExistingPath(string root, string? rel, out string notCanonicalAt)
    {
        notCanonicalAt = "";
        var segments = NormalizePath(rel).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = EnsureTrailingSeparator(root);   // 保留分隔符：`C:\` + seg 必须仍是**完全限定**路径
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            var exists = File.Exists(candidate) || Directory.Exists(candidate);
            if (!exists) return true;                        // 从这里起都是新路径 ⇒ 无别名风险
            var parentEntries = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName).Where(n => n is not null).ToList();
            if (!parentEntries.Any(n => string.Equals(n, segments[i], StringComparison.OrdinalIgnoreCase)))
            {
                notCanonicalAt = string.Join('/', segments.Take(i + 1));   // 存在但非枚举名 ⇒ 别名（如 8.3 短名）
                return false;
            }
            current = candidate;
        }
        return true;
    }

    /// <summary>规范化并**保留尾分隔符**（避免 `C:\` 被裁剪成 `C:`；盘符相对路径会随当前目录漂移）。</summary>
    internal static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
            ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>绝对路径版规范名校验（从盘符/UNC 根逐段枚举比对；用于根路径别名防护）。</summary>
    internal static bool IsCanonicalAbsolutePath(string fullPath, out string notCanonicalAt)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(full) ?? "";
        var rel = full.Length > root.Length ? full[root.Length..] : "";
        if (rel.Length == 0) { notCanonicalAt = ""; return true; }
        return IsCanonicalExistingPath(root, rel, out notCanonicalAt);
    }

    /// <summary>路径前缀判定（a 为 b 的祖先目录）：用于**文件/目录拓扑互换**的登记拒绝。</summary>
    internal static bool IsAncestorPath(string? a, string? b)
    {
        var ka = PathKey(a);
        var kb = PathKey(b);
        if (ka.Length == 0 || kb.Length == 0 || ka == kb) return false;
        return kb.StartsWith(ka + "/", StringComparison.Ordinal);
    }

    /// <summary>目标路径安全性：规范化后必须仍在 root 内，且**父目录链上无 reparse point**（逐段链接拒绝）。</summary>
    internal static bool IsSafeTarget(string root, string rel)
    {
        if (!IsSafeRelativePath(rel)) return false;
        var rootFull = EnsureTrailingSeparator(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            return false;                                                                         // **目标文件本身**是链接 ⇒ 拒绝
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;   // 父链任一段链接 ⇒ 拒绝
            if (string.Equals(EnsureTrailingSeparator(dir.FullName), rootFull, StringComparison.OrdinalIgnoreCase)) break;
            dir = dir.Parent;
        }
        return true;
    }

    /// <summary>事务号安全校验（单段安全路径）。</summary>
    internal static bool IsSafeTransactionId(string? txId)
        => IsSafeRelativePath(txId) && !(txId ?? "").Contains('/') && !(txId ?? "").Contains('\\');

    /// <summary>取得事务独占锁（不写 manifest；恢复路径用）。</summary>
    public MigrationResult TryAcquireExclusive()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (HoldsExclusiveLock) return MigrationResult.Ok(MigrationStage.None);
            Directory.CreateDirectory(_transactionRoot);
            try
            {
                _lock = new FileStream(Path.Combine(_transactionRoot, "migration.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return MigrationResult.Fail("transaction_busy", MigrationStage.None);
            }
            return MigrationResult.Ok(MigrationStage.None);
        }
    }

    /// <summary>开启新事务：须持锁；有未决事务或事务号占用 ⇒ 拒绝；先持久化 Snapshotting 并取得全程静止窗口。</summary>
    public MigrationResult BeginTransaction(string transactionId)
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!IsSafeTransactionId(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
            if (!HoldsExclusiveLock)
            {
                var acq = TryAcquireExclusive();
                if (!acq.Success) return acq;
            }

            if (File.Exists(ManifestPath))
            {
                var validated = LoadValidated();
                if (validated is null) return MigrationResult.Fail("pending_manifest_corrupt", MigrationStage.None);  // 存在但不可验证 ⇒ 保守阻断
                if (validated.Stage is not (MigrationStage.Committed or MigrationStage.RolledBack))
                    return MigrationResult.Fail("pending_transaction_exists:" + validated.TransactionId, validated.Stage);
                if (string.Equals(validated.TransactionId, transactionId, StringComparison.Ordinal))
                    return MigrationResult.Fail("transaction_id_in_use", validated.Stage);
            }
            if (File.Exists(HistoryPath) && File.ReadAllLines(HistoryPath).Any(l => string.Equals(l.Trim(), transactionId, StringComparison.Ordinal)))
                return MigrationResult.Fail("transaction_id_in_use", MigrationStage.None);        // 历史占用：事务号不复用

            var snapshotPath = SnapshotPathOf(transactionId, _sessionId);
            if (Directory.Exists(snapshotPath)) return MigrationResult.Fail("snapshot_path_in_use", MigrationStage.None);

            _quiet = _quiesce?.Invoke();                       // 静止窗口：覆盖全程，提交/回滚/释放时结束
            _quietValid = _quiet is not null;
            _quietGeneration++;
            var manifest = new MigrationManifest
            {
                TransactionId = transactionId,
                CreatedAtUtc = _utcNow(),
                ConfigRoot = _configRoot,
                SnapshotPath = snapshotPath,
                SnapshotId = _sessionId,
                RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
                Stage = MigrationStage.Snapshotting,
                CommitMarker = null,
                RollbackRehearsed = false,
                QuiescedAtUtc = _quiet is null ? null : _utcNow(),
                QuiesceSessionId = _quiet is null ? null : _sessionId,
                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
                RealEffectsRequired = _effects is not null,
            };
            // **先持久化占号、再发布 manifest**（占号失败/崩溃也保守占号 ⇒ 事务号不复用；不依赖窗口是否存在）。
            File.AppendAllText(HistoryPath, transactionId + Environment.NewLine);
            WriteManifest(manifest);
            return MigrationResult.Ok(MigrationStage.Snapshotting);
        }
    }
    /// <summary>步骤①②：全量快照（字节+SHA256）→ 清单哈希与快照路径落盘 → SnapshotReady。</summary>
    public MigrationResult TakeSnapshot()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Snapshotting) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 采集期须有**存续**窗口（重开实例不得续用旧资格）
            if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(m.SnapshotPath);   // **空配置根也须建立可验证快照目录**（否则基线完成后 VerifySnapshot 报 snapshot_missing）
                foreach (var file in EnumerateFiles(_configRoot))
                {
                    var rel = Rel(file, _configRoot);
                    if (!IsSafeTarget(_configRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                        return MarkBlocked("unsafe_path:" + rel);      // 读端与写端都须安全（含目标文件本身与父链链接）
                    var bytes = File.ReadAllBytes(file);
                    var target = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    hashes[rel] = Sha256Hex(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("snapshot_io_failed:" + ex.GetType().Name);
            }

            m.FileHashes = hashes;
            m.SnapshotManifestHash = ComputeSnapshotManifestHash(hashes);
            m.BaselineCompleted = true;                       // 基线（完整清单）已发布
            m.Stage = MigrationStage.SnapshotReady;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>快照清单哈希（排序后的 相对路径:内容哈希 取 SHA256）。</summary>
    public static string ComputeSnapshotManifestHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        var ordered = (fileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var p in ordered) sb.Append(p.Key).Append(':').Append(p.Value ?? "").Append('\n');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>记录变更归属（基线校验：Added 不得命中快照；Modified/Deleted 必须在快照中）。</summary>
    public MigrationResult RecordChanges(IEnumerable<ChangeRecord> changes)
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            // **确认引用更新后冻结登记（会诊 IMPORTANT-6）**：真实写集一旦确认，变更登记即为已核验证据的一部分；
            // 此后改登记（新增/改写归属）会使「声明写集 ↔ 变更登记」一致性失效，一律拒绝，须重新开事务。
            if (m.ReferenceWriteSet.Count > 0)
                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);

            var batch = new List<ChangeRecord>();
            foreach (var c in changes ?? [])
            {
                if (c is null) return MigrationResult.Fail("null_change_record", m.Stage);
                var raw = c.Path ?? "";
                if (!IsSafeRelativePath(raw)) return MigrationResult.Fail("unsafe_path:" + raw, m.Stage);   // **先验原始输入**
                var path = NormalizePath(raw);                                                             // 再统一解析（**不裁剪**）
                if (batch.Any(x => PathKey(x.Path) == PathKey(path)))
                    return MigrationResult.Fail("duplicate_change_path:" + path, m.Stage);
                var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, path, out _);
                var ok = c.Kind switch
                {
                    ChangeKind.Added => !inSnapshot,
                    ChangeKind.Modified or ChangeKind.Deleted => inSnapshot,
                    _ => false,
                };
                if (!ok) return MigrationResult.Fail("change_baseline_mismatch:" + path, m.Stage);
                batch.Add(new ChangeRecord { Path = path, Kind = c.Kind });
            }

            // **拓扑约束**：`Added` 路径不得与 `Modified/Deleted` 路径互为祖先——文件↔目录互换会让「先恢复子路径、
            // 后删除父路径」无法收敛（父被新文件阻挡）。登记期结构化拒绝，避免提交前崩溃后进入不可恢复回滚。
            foreach (var c in batch)
            {
                if (!IsCanonicalExistingPath(_configRoot, c.Path, out var badSegment))
                    return MigrationResult.Fail("non_canonical_path:" + badSegment, m.Stage);   // 短名/别名等新引用不一致 ⇒ 拒绝
            }

            var merged = m.ChangedFiles.Concat(batch).ToList();
            for (var i = 0; i < merged.Count; i++)
            {
                for (var j = 0; j < merged.Count; j++)
                {
                    if (i == j) continue;
                    var a = merged[i];
                    var b = merged[j];
                    if (a.Kind != ChangeKind.Added && b.Kind != ChangeKind.Added) continue;   // Modified/Deleted 必对应基线文件，不互为祖先
                    if (IsAncestorPath(a.Path, b.Path))                                        // 含 **Added↔Added**（同批与跨次）
                        return MigrationResult.Fail("unsupported_topology_change:" + a.Path + "<->" + b.Path, m.Stage);
                }
            }

            // **拓扑（基线侧）**：每条 Added 亦须与**完整基线文件集合**双向祖先检查——只登记 Added（未同时登记
            // 对应 Deleted）同样可能造成「先恢复基线子路径、后删除新增父路径」的不可收敛回滚，不能依赖调用方补齐。
            foreach (var added in merged.Where(c => c.Kind == ChangeKind.Added))
            {
                foreach (var basePath in m.FileHashes.Keys)
                {
                    if (IsAncestorPath(added.Path, basePath) || IsAncestorPath(basePath, added.Path))
                        return MigrationResult.Fail("unsupported_topology_change:" + added.Path + "<->baseline:" + basePath, m.Stage);
                }
            }

            foreach (var c in batch)
            {
                m.ChangedFiles.RemoveAll(x => PathKey(x.Path) == PathKey(c.Path));
                m.ChangedFiles.Add(c);
            }
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>
    /// **阶段推进旧语义（仅限未接真实副作用的只读夹具）**：已注入真实副作用端口后**必须**用
    /// <see cref="ApplyReferenceUpdate"/>；否则本方法会在零写入的情况下把阶段推进到 `ReferenceUpdating`，
    /// 使 `RehearseRollback`/`Commit` 全部通过而配置根**从未发生真实引用更新**（假成功）。此门禁只在
    /// 生产/真实事务上生效，不改变旧夹具（`effectService: null`）的行为。
    /// </summary>
    public MigrationResult MarkReferenceUpdateCompleted()
        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
    /// <summary>阶段推进旧语义（同 <see cref="MarkReferenceUpdateCompleted"/>：注入真实副作用端口后一律拒绝）。</summary>
    public MigrationResult MarkActivated()
        => _effects is null ? Advance(MigrationStage.Activated)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);

    /// <summary>
    /// **真实引用更新（R5.6 A 项）**：声明写集 → 真实副作用 → **逐项读回确认** → **才**持久化阶段与写集证据。
    /// 拒绝/未知/取消/读回不符一律不推进阶段：未知与读回不符置 `Blocked`（fail-closed、不盲目重试）；
    /// 副作用前取消保持当前阶段（可重试/可回滚）；已到本阶段时幂等重读盘复核，**不二次触发副作用**。
    /// </summary>
    public MigrationResult ApplyReferenceUpdate(MigrationReferenceUpdatePlan plan)
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入（不抛异常）

            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
            if (!IsLegalAdvance(m.Stage, MigrationStage.ReferenceUpdating))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->ReferenceUpdating", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 真实写入必须在**存续**窗口内
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            var stageBeforeEffect = m.Stage;
            MigrationEffectResult result;
            try
            {
                _effectCallInProgress = true;
                result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }
            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeEffect)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            // **读回确认先于阶段推进**：语义读回（引用已改写）+ 字节读回（写入后哈希）
            var writeSet = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var target in plan!.Targets)
            {
                try
                {
                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
                }
                catch (Exception ex)
                {
                    return MarkBlocked("reference_readback_exception:" + target.Path + ":" + ex.GetType().Name);
                }
                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
                writeSet[target.Path] = hash;
            }
            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);
            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）
            foreach (var baseline in m.FileHashes)
            {
                if (writeSet.Keys.Any(k => PathKey(k) == PathKey(baseline.Key))) continue;
                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
            }
            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>
    /// **真实激活（R5.6 A 项：D13 `candidate → active`）**：须已完成真实引用更新；目标必须是**已确认写集内**的文件；
    /// 副作用成功且状态读回一致后才推进到 `Activated`。已到 `Activated` 时幂等重读复核（不二次写入）。
    /// </summary>
    public MigrationResult ActivateCandidate(MigrationActivationRequest request)
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**

            if (m.Stage != MigrationStage.ReferenceUpdating)
                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);
            if (ValidateActivationRequest(m, request) is { } requestProblem) return MarkBlocked(requestProblem);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            // **激活前的字节版本核对（会诊 MUST-1）**：激活必须基于**已确认写集**所记录的那一份字节；
            // 若该文件自引用更新确认后已被锁外改动，则激活会把它「连同漂移一起合法化」——此处一律 fail-closed。
            if (!TryGetWriteSetHash(m, request!.Path, out var confirmedHash))
                return MarkBlocked("activation_writeset_hash_missing:" + request.Path);
            if (!TryHashConfigFile(request.Path, out var preHash, out var preProblem))
                return MarkBlocked("activation_precondition_" + preProblem + ":" + request.Path);
            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_drifted:" + request.Path);

            // **前置状态观测（会诊 IMPORTANT-7）**：盘上现值必须**恰为**声明的 before 状态；已等于目标态 ⇒ 拒绝，
            // 不得凭「已生效」零写入成功（幂等复核只经由已持久化的 `Activated` 阶段证据）。
            string observed;
            string observeDetail;
            try
            {
                if (string.IsNullOrEmpty(request.ExpectedContentHash))
                return MarkBlocked("activation_request_invalid:missing_content_hash:" + request.Path);
            if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out observed, out observeDetail))
                    return MarkBlocked("activation_precondition_status_unreadable:" + request.Path + ":" + observeDetail);
            }
            catch (Exception ex)
            {
                return MarkBlocked("activation_precondition_exception:" + request.Path + ":" + ex.GetType().Name);
            }
            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_already_applied:" + request.Path);
            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);

            var stageBeforeActivation = m.Stage;
            MigrationEffectResult result;
            try
            {
                _effectCallInProgress = true;
                result = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
            }
            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeActivation)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeActivation + "->" + CurrentStageOrNone());
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("activation_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("activation_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            try
            {
                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)
                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
            }
            catch (Exception ex)
            {
                return MarkBlocked("activation_readback_exception:" + request.Path + ":" + ex.GetType().Name);
            }
            if (!TryHashConfigFile(request.Path, out var hash, out var hashProblem))
                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);

            m.ActivationRecord = new MigrationActivationRecord
            {
                Path = request.Path,
                BeforeStatus = request.ExpectedBeforeStatus,
                AfterStatus = request.TargetStatus,
                AfterHash = hash,
            };
            // 写集记录的是「本事务写过的文件的**当前期望盘上状态**」：激活同样改写了该文件，故须同步更新其哈希，
            // 否则提交前的写集复核会把本事务自己的激活写入误判为漂移。
            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
                m.ReferenceWriteSet[key] = hash;
            m.Stage = MigrationStage.Activated;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>幂等复核：写集内每个文件仍在盘上且哈希与已确认写集一致（不触发副作用）。</summary>
    private MigrationResult RecheckReferenceWriteSet(MigrationManifest m)
    {
        if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_write_set_empty", m.Stage);
        foreach (var entry in m.ReferenceWriteSet)
        {
            if (!TryHashConfigFile(entry.Key, out var hash, out var problem))
                return MigrationResult.Fail("reference_recheck_" + problem + ":" + entry.Key, m.Stage);
            if (!string.Equals(hash, entry.Value, StringComparison.Ordinal))
                return MigrationResult.Fail("reference_recheck_hash_mismatch:" + entry.Key, m.Stage);
        }
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>幂等复核：激活记录仍在盘上（哈希一致）且状态仍为目标状态（不触发副作用）。</summary>
    private MigrationResult RecheckActivationRecord(MigrationManifest m)
    {
        var record = m.ActivationRecord;
        if (record is null) return MigrationResult.Fail("activation_record_missing", m.Stage);
        if (!TryHashConfigFile(record.Path, out var hash, out var problem))
            return MigrationResult.Fail("activation_recheck_" + problem + ":" + record.Path, m.Stage);
        if (!string.Equals(hash, record.AfterHash, StringComparison.Ordinal))
            return MigrationResult.Fail("activation_recheck_hash_mismatch:" + record.Path, m.Stage);
        if (_effects is null) return MigrationResult.Ok(m.Stage);
        try
        {
            if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var status, out var detail)
                || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
                return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
        }
        catch (Exception ex)      // 复核期异常同样收敛（会诊第 2 轮 IMPORTANT-6）
        {
            return MigrationResult.Fail("activation_recheck_exception:" + record.Path + ":" + ex.GetType().Name, m.Stage);
        }
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>写集声明校验：非空、路径安全、不重复、逐项等于**已登记变更归属**且无漏项/多项（精确写集）。</summary>
    private static string? ValidateReferencePlan(MigrationManifest m, MigrationReferenceUpdatePlan? plan)
    {
        if (plan?.Targets is null || plan.Targets.Count == 0) return "reference_writeset_mismatch:plan_empty";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in plan.Targets)
        {
            if (target is null) return "reference_writeset_mismatch:null_target";
            if (!IsSafeRelativePath(target.Path)) return "reference_writeset_mismatch:unsafe_path:" + target.Path;
            if (!seen.Add(PathKey(target.Path))) return "reference_writeset_mismatch:duplicate:" + target.Path;
            var record = FindChange(m, target.Path);
            if (record is null) return "reference_writeset_mismatch:not_registered:" + target.Path;
            if (record.Kind != target.Kind) return "reference_writeset_mismatch:kind:" + target.Path;
            switch (target.Kind)
            {
                case ChangeKind.Added when string.IsNullOrEmpty(target.NewContent):
                    return "reference_writeset_mismatch:added_without_content:" + target.Path;
                case ChangeKind.Modified when string.IsNullOrEmpty(target.RenameFrom) || string.IsNullOrEmpty(target.RenameTo)
                    || string.Equals(target.RenameFrom, target.RenameTo, StringComparison.Ordinal):
                    return "reference_writeset_mismatch:modified_without_rename:" + target.Path;
                case ChangeKind.Deleted:
                    return "reference_writeset_mismatch:deleted_target_unsupported:" + target.Path;
            }
        }
        foreach (var record in m.ChangedFiles)
        {
            if (record.Kind == ChangeKind.Deleted)
                return "reference_writeset_mismatch:deleted_record_unsupported:" + record.Path;
            if (!seen.Contains(PathKey(record.Path)))
                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项
        }
        return null;
    }

    /// <summary>激活请求校验：目标必须**已在确认写集内**且为本次真实写入的文件（不得激活写集外目标）。</summary>
    private static string? ValidateActivationRequest(MigrationManifest m, MigrationActivationRequest? request)
    {
        if (request is null) return "activation_request_invalid:null";
        if (!IsSafeRelativePath(request.Path)) return "activation_request_invalid:unsafe_path:" + request.Path;
        if (string.IsNullOrEmpty(request.ExpectedBeforeStatus) || string.IsNullOrEmpty(request.TargetStatus))
            return "activation_request_invalid:status_missing";
        if (string.Equals(request.ExpectedBeforeStatus, request.TargetStatus, StringComparison.Ordinal))
            return "activation_request_invalid:no_state_change";
        // **只接受权威的 D13 转换**（R5.2 §21.2：激活＝`candidate → active`）：本事务不发明通用状态改写器；
        // 其余状态对一律拒绝（回滚的撤销路径不经此入口，直接用端口）。
        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;
        if (m.ReferenceWriteSet.Count == 0) return "activation_request_invalid:reference_write_set_empty";
        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4
        var record = FindChange(m, request.Path);
        if (record is null || record.Kind == ChangeKind.Deleted)
            return "activation_target_not_a_written_file:" + request.Path;                    // REF-F4
        return null;
    }

    /// <summary>
    /// **证据关系不变量（会诊 MUST-4）**：真实证据之间必须自洽——写集键集合**恰等于**变更登记中非删除项、
    /// 身份键唯一、激活记录的盘上哈希必须等于写集中该文件的哈希。任一不符 ⇒ 拒绝（返回原因码）。
    /// </summary>
    private static string? EvidenceRelationProblem(MigrationManifest m)
    {
        var writeSetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in m.ReferenceWriteSet.Keys)
            if (!writeSetKeys.Add(PathKey(key))) return "evidence_relation:duplicate_writeset_key:" + key;
        var changeIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in m.ChangedFiles)
            if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;
        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);
        if (!writeSetKeys.SetEquals(registered)) return "evidence_relation:writeset_registry_mismatch";
        if (m.ActivationRecord is { } activation)
        {
            if (!TryGetWriteSetHash(m, activation.Path, out var activationHash))
                return "evidence_relation:activation_not_in_writeset:" + activation.Path;
            if (!string.Equals(activationHash, activation.AfterHash, StringComparison.Ordinal))
                return "evidence_relation:activation_hash_mismatch:" + activation.Path;
        }
        return null;
    }

    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>
    private static bool TryGetWriteSetHash(MigrationManifest m, string rel, out string hash)
    {
        hash = "";
        var key = PathKey(rel);
        foreach (var entry in m.ReferenceWriteSet)
        {
            if (PathKey(entry.Key) != key) continue;
            hash = entry.Value;
            return true;
        }
        return false;
    }

    /// <summary>按大小写不敏感身份查变更归属。</summary>
    private static ChangeRecord? FindChange(MigrationManifest m, string path)
    {
        var key = PathKey(path);
        foreach (var record in m.ChangedFiles) if (PathKey(record.Path) == key) return record;
        return null;
    }

    /// <summary>
    /// **写集外新增文件检测（会诊 MUST-3）**：配置根当前文件集合必须等于「基线 ∪ 声明写集中的新增目标」。
    /// 只比较基线清单会漏掉「副作用在写集外新建了文件」；本检查补齐该面（静止窗口下无其他写方，新增即本事务产物）。
    /// 返回 null 表示一致。
    /// </summary>
    private string? UnexpectedFileReason(MigrationManifest m, IEnumerable<MigrationReferenceWriteTarget> declared)
    {
        List<string> current;
        try { current = EnumerateFilesSafe(_configRoot).Select(p => PathKey(p)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "config_root_enumeration_failed:" + ex.GetType().Name;
        }
        var expected = new HashSet<string>(m.FileHashes.Keys.Select(PathKey), StringComparer.Ordinal);
        foreach (var target in declared)
            if (target.Kind == ChangeKind.Added) expected.Add(PathKey(target.Path));
        foreach (var path in current)
            if (!expected.Contains(path)) return "unexpected_new_file_outside_writeset:" + path;
        var present = new HashSet<string>(current, StringComparer.Ordinal);
        foreach (var baseline in m.FileHashes.Keys)
            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半
        return null;
    }

    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>
    private bool TryHashConfigFile(string rel, out string hash, out string problem)
    {
        hash = "";
        problem = "unhashable";
        try
        {
            if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
        }
        catch (Exception)     // 安全检查自身异常同样收敛为「不可哈希」（会诊第 2 轮 IMPORTANT-6）
        {
            problem = "unsafe_target_check_failed";
            return false;
        }
        var full = Path.Combine(_configRoot, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) { problem = "file_missing"; return false; }
        try
        {
            hash = Sha256Hex(File.ReadAllBytes(full));
            problem = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = "read_failed";
            return false;
        }
    }

    private MigrationResult Advance(MigrationStage to)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (!IsLegalAdvance(m.Stage, to)) return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + to, m.Stage);
            m.Stage = to;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>合法阶段转换（跳步提交被拒；回滚可自任意中间态/已提交/阻塞态发起）。</summary>
    public static bool IsLegalAdvance(MigrationStage from, MigrationStage to) => (from, to) switch
    {
        (MigrationStage.Snapshotting, MigrationStage.SnapshotReady) => true,
        (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) => true,
        (MigrationStage.ReferenceUpdating, MigrationStage.Activated) => true,
        (MigrationStage.Activated, MigrationStage.Committed) => true,
        (_, MigrationStage.Blocked) => from is not (MigrationStage.Committed or MigrationStage.RolledBack),
        (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated
            or MigrationStage.Committed or MigrationStage.Blocked or MigrationStage.RollingBack,
            MigrationStage.RollingBack) => true,
        (MigrationStage.Snapshotting, MigrationStage.RolledBack) => true,   // 基线未完成的中止出口（不迁移任何变更）
        (MigrationStage.RollingBack, MigrationStage.RolledBack) => true,
        (MigrationStage.RolledBack, MigrationStage.RolledBack) => true,
        _ => false,
    };
    /// <summary>步骤④：回滚演练——副本＝快照 → 施加代表变更 → 复用实际回滚核心 → 逐字节校验。</summary>
    public MigrationResult RehearseRollback()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            var rehearsalRoot = Path.Combine(_transactionRoot, "rehearsal-" + _sessionId);
            try
            {
                if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true);
                Directory.CreateDirectory(rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);
                ApplyRepresentativeChanges(m, rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);      // 复用实际回滚核心
                if (DeleteRecordedAdditions(m, rehearsalRoot) > 0)
                    return MigrationResult.Fail("rehearsal_cleanup_incomplete", m.Stage);
                foreach (var p in m.FileHashes)
                {
                    var target = Path.Combine(rehearsalRoot, p.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target)) return MigrationResult.Fail("rehearsal_missing:" + p.Key, m.Stage);
                    if (!string.Equals(Sha256Hex(File.ReadAllBytes(target)), p.Value, StringComparison.Ordinal))
                        return MigrationResult.Fail("rehearsal_hash_mismatch:" + p.Key, m.Stage);
                }
                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                {
                    var target = Path.Combine(rehearsalRoot, added.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target)) return MigrationResult.Fail("rehearsal_addition_not_cleaned:" + added.Path, m.Stage);
                }
            }
            catch (IOException ex)
            {
                return MarkBlocked("rehearsal_io_failed:" + ex.GetType().Name);
            }
            finally
            {
                try { if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true); } catch { }
            }

            m.RollbackRehearsed = true;
            m.RehearsalScope = RehearsalScopeOf(m);
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>提交：阶段 Activated、演练范围一致、无 blocked、静止窗口为当前会话且快照有效 ⇒ 写唯一提交标记。</summary>
    public MigrationResult Commit()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (m.RealEffectsRequired || _effects is not null)
            {
                // **真实事务的提交前置（会诊 MUST-4）**：门槛绑定「本实例是否接入真实副作用」与持久化标记的**并集**，
                // 故把 `realEffectsRequired` 改成 false 不能降级绕过；引用写入与激活都必须由真实副作用 + 读回确认产生
                if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);
                if (m.ActivationRecord is null) return MigrationResult.Fail("activation_not_confirmed", m.Stage);
                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
                if (EvidenceRelationProblem(m) is { } relationProblem) return MigrationResult.Fail(relationProblem, m.Stage);
                if (UnexpectedFileReason(m, m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted)
                        .Select(c => new MigrationReferenceWriteTarget(c.Path, c.Kind)).ToList()) is { } unexpected)
                    return MigrationResult.Fail(unexpected, m.Stage);
            }
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
                return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null || m.QuiescedAtUtc is null
                || !string.Equals(m.QuiesceSessionId, _sessionId, StringComparison.Ordinal)
                || m.QuiesceGeneration != _quietGeneration))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 须**实际存续**且同代次的窗口
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            m.CommitMarker = m.TransactionId;
            m.Stage = MigrationStage.Committed;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>回滚：先落 RollingBack（清提交标记与演练资格）再做 IO，最后落 RolledBack；只删归属为 Added 的文件。</summary>
    public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (!m.BaselineCompleted)
            {
                // 基线未完成（快照发布前失败/中止）⇒ **安全中止**：清理未完成快照、置 RolledBack，使新事务可开启。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (!IsLegalAdvance(m.Stage, MigrationStage.RollingBack))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->RollingBack", m.Stage);

            // **先持久化封锁**（落 RollingBack 并清标记/演练资格）——此后即不可生产执行；
            // 再做窗口/快照校验与恢复，失败一律 Blocked（失败处理产出仍可加载）。
            m.Stage = MigrationStage.RollingBack;
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            m.CommitMarker = null;
            WriteManifest(m);
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("rollback_snapshot_invalid:" + bad);
            return CompleteRollback(m);
        }
    }

    /// <summary>
    /// **撤销真实激活**：读回当前状态 → 仍为 `AfterStatus` 时施加反向副作用 → **再读回**确认等于 `BeforeStatus`。
    /// 任一读回失败或状态异常 ⇒ `Blocked`（**绝不**在未确认时报告完整回滚；本事务新增文件已不存在视为旧态）。
    /// </summary>
    private MigrationResult UndoActivation(MigrationManifest m, MigrationActivationRecord record)
    {
        var kind = FindChange(m, record.Path)?.Kind;
        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
        bool readOk;
        string current;
        string detail;
        try
        {
            readOk = _effects.TryReadActivationStatus(_configRoot, record.Path, out current, out detail);
        }
        catch (Exception ex)
        {
            return MarkBlocked("rollback_activation_readback_exception:" + ex.GetType().Name);
        }
        if (!readOk)
        {
            if (kind == ChangeKind.Added && detail == "target_missing")
                return MigrationResult.Ok(m.Stage);            // 本事务新增文件已不存在＝旧态（无激活可撤销）
            return MarkBlocked("rollback_activation_readback_failed:" + record.Path + ":" + detail);
        }
        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
        {
            MigrationEffectResult undo;
            try
            {
                undo = _effects.Activate(_configRoot,
                    new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus, record.AfterHash));
            }
            catch (Exception ex)
            {
                return MarkBlocked("rollback_activation_undo_exception:" + ex.GetType().Name);
            }
            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
        }
        else if (!string.Equals(current, record.BeforeStatus, StringComparison.Ordinal))
        {
            return MarkBlocked("rollback_activation_state_unexpected:" + record.Path + ":" + current);
        }
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var afterUndo, out var undoDetail)
            || !string.Equals(afterUndo, record.BeforeStatus, StringComparison.Ordinal))
            return MarkBlocked("rollback_activation_not_reverted:" + record.Path + ":" + undoDetail);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>
    private MigrationResult CompleteRollback(MigrationManifest m)
    {
        lock (_sync)
        {
            try
            {
                // **先撤销真实激活**（语义前沿先回退），再整份恢复旧字节；两步都须读回确认。
                // **归属预检必须先于任何写入（会诊第 2 轮 MUST-1）**：撤销激活本身也是写入，
                // 若目标是他方文件，先写再查会破坏他方内容；故先核对归属，再决定是否允许写入。
                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownershipVerified.Add(PathKey(added.Path));
                }
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
                // **回滚后旧态一致性（会诊 MUST-5）**：核对**完整基线字节集**（而不是「成功写集」——部分写后
                // Unknown / 阶段发布前失败时写集为空，只查写集会空过并假报完整回滚）。
                foreach (var baseline in m.FileHashes)
                {
                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))
                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);
                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))
                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);
                }
                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                {
                    var target = Path.Combine(_configRoot, NormalizePath(added.Path).Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target))
                        return MarkBlocked("rollback_addition_still_present:" + added.Path);
                }
                if (m.RealEffectsRequired)
                {
                    if (m.ActivationRecord is { } restored)
                    {
                        var kind = FindChange(m, restored.Path)?.Kind;
                        if (kind == ChangeKind.Added)
                        {
                            if (File.Exists(Path.Combine(_configRoot, NormalizePath(restored.Path).Replace('/', Path.DirectorySeparatorChar))))
                                return MarkBlocked("rollback_activation_target_still_present:" + restored.Path);
                        }
                        else if (!_effects!.TryReadActivationStatus(_configRoot, restored.Path, out var finalStatus, out var finalDetail)
                            || !string.Equals(finalStatus, restored.BeforeStatus, StringComparison.Ordinal))
                        {
                            return MarkBlocked("rollback_activation_state_after_restore:" + restored.Path + ":" + finalDetail);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
            }
            m.Stage = MigrationStage.RolledBack;
            m.CommitMarker = null;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>重启恢复（须先持锁）：已提交+标记+无 blocked ⇒ 保持新态；RollingBack ⇒ 幂等续做；RolledBack ⇒ 幂等；其余 ⇒ 回滚旧态。</summary>
    public MigrationResult RecoverOnStart()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.Committed
                && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(m.BlockedReason))
                return MigrationResult.Ok(MigrationStage.Committed);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (m.Stage == MigrationStage.None) return MigrationResult.Fail("illegal_stage:None", m.Stage);
            if (m.Stage == MigrationStage.Snapshotting || (m.Stage == MigrationStage.Blocked && !m.BaselineCompleted))
            {
                // **基线尚未完成**（且尚未发生任何迁移变更）⇒ 安全中止：清理未完成快照、置 RolledBack，使新事务可开启。
                // **绝不**把部分快照用于恢复（不调用 VerifySnapshot/CompleteRollback）。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("recover_snapshot_invalid:" + bad);

            if (m.Stage != MigrationStage.RollingBack)
            {
                m.Stage = MigrationStage.RollingBack;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.CommitMarker = null;
                WriteManifest(m);
            }
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 恢复亦须有效窗口
            return CompleteRollback(m);
        }
    }
    /// <summary>授权（须持锁）：完整性校验 → 已提交 → 标记匹配 → 无 blocked。</summary>
    public MigrationResult AuthorizeProductionExecution()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
                return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if ((m.RealEffectsRequired || _effects is not null)
                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))
                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>
    public MigrationResult TryRunProduction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            var auth = AuthorizeProductionExecution();
            if (!auth.Success) return auth;
            action();
            return MigrationResult.Ok(MigrationStage.Committed);
        }
    }

    /// <summary>快照完整性：manifest 结构与不变量 → 快照文件齐全/哈希一致 → 无未登记多余文件。</summary>
    public string VerifySnapshot()
    {
        var m = LoadManifest();
        if (m is null) return "manifest_missing_or_corrupt";
        if (!IsManifestIntegrityValid(m)) return "manifest_integrity_mismatch";
        if (!string.Equals(m.SnapshotManifestHash, ComputeSnapshotManifestHash(m.FileHashes), StringComparison.Ordinal))
            return "snapshot_manifest_hash_mismatch";
        if (!Directory.Exists(m.SnapshotPath)) return "snapshot_missing";

        List<string> onDisk;
        try { onDisk = EnumerateFilesSafe(m.SnapshotPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "snapshot_enumeration_failed:" + ex.GetType().Name;      // 读端链接/IO 失败 ⇒ 验证失败（不继续读）
        }
        foreach (var extra in onDisk.Where(f => !m.FileHashes.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;
        foreach (var p in m.FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!IsSafeTarget(m.SnapshotPath, p.Key)) return "snapshot_unsafe_target:" + p.Key;   // **读端**同样拒绝链接逃逸
            var target = Path.Combine(m.SnapshotPath, NormalizePath(p.Key).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;
            string hash;
            try { hash = Sha256Hex(File.ReadAllBytes(target)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return "snapshot_read_failed:" + p.Key;
            }
            if (!string.Equals(hash, p.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + p.Key;
        }
        return "";
    }

    /// <summary>全字段完整性摘要（仅完整性，不提供来源认证）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.SnapshotId).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append(m.BaselineCompleted ? '1' : '0').Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
        sb.Append(m.RealEffectsRequired ? '1' : '0').Append('|');
        foreach (var p in (m.ReferenceWriteSet ?? new Dictionary<string, string>(StringComparer.Ordinal))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
        sb.Append('|').Append(m.ActivationRecord is { } ar
            ? ar.Path + ':' + ar.BeforeStatus + '>' + ar.AfterStatus + ':' + ar.AfterHash : "<null>").Append('|');
        foreach (var c in (m.ChangedFiles ?? []).OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>结构与状态不变量校验（先于摘要校验；null/非法枚举/非法组合一律拒绝）。</summary>
    public bool IsManifestIntegrityValid(MigrationManifest m)
    {
        try { return IsManifestIntegrityValidCore(m); }
        catch (Exception) { return false; }        // 路径解析等异常 ⇒ **稳定转为验证失败**（不向上抛）
    }

    private bool IsManifestIntegrityValidCore(MigrationManifest m)
    {
        if (m is null) return false;
        if (m.SchemaVersion != 1) return false;
        if (!IsSafeTransactionId(m.TransactionId)) return false;
        if (!string.Equals(m.ConfigRoot, _configRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(m.SnapshotPath) || string.IsNullOrWhiteSpace(m.RollbackEntry)) return false;  // 先验空值，避免 GetFullPath 抛异常
        if (m.SnapshotId is not { Length: 32 }) return false;                                                       // 快照身份必填且格式固定
        foreach (var ch in m.SnapshotId) if (!Uri.IsHexDigit(ch)) return false;                                     // 仅 32 位十六进制（无分隔符/..）
        var expectedSnapshot = SnapshotPathOf(m.TransactionId, m.SnapshotId);
        if (!string.Equals(Path.GetFullPath(m.SnapshotPath ?? ""), Path.GetFullPath(expectedSnapshot), StringComparison.OrdinalIgnoreCase))
            return false;                                                                                            // **精确**绑定本事务快照（非前缀判定）
        if (!IsWithin(m.SnapshotPath, _transactionRoot)) return false;
        if (!Enum.IsDefined(m.Stage)) return false;
        if (m.FileHashes is null || m.ChangedFiles is null) return false;
        foreach (var k in m.FileHashes.Keys) if (!IsSafeRelativePath(k)) return false;
        foreach (var c in m.ChangedFiles)
        {
            if (c is null || !IsSafeRelativePath(c.Path) || !Enum.IsDefined(c.Kind)) return false;
            var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, c.Path, out _);
            if (c.Kind == ChangeKind.Added && inSnapshot) return false;
            if (c.Kind is ChangeKind.Modified or ChangeKind.Deleted && !inSnapshot) return false;
        }
        if (m.Stage == MigrationStage.Committed)
        {
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(m.BlockedReason)) return false;          // 已提交不得带 blocked
            if (!m.BaselineCompleted) return false;                            // 已提交 ⇒ 基线必已建立
        }
        else if (!string.IsNullOrEmpty(m.CommitMarker)) return false;          // 非提交态不得带标记（含 Blocked）
        if (m.Stage == MigrationStage.RolledBack && (m.RollbackRehearsed || m.RehearsalScope is not null)) return false;
        if (m.RollbackRehearsed && string.IsNullOrEmpty(m.RehearsalScope)) return false;
        if (m.BlockedReason is { Length: 0 }) return false;
        if (m.ReferenceWriteSet is null) return false;
        foreach (var entry in m.ReferenceWriteSet)
            if (!IsSafeRelativePath(entry.Key) || string.IsNullOrEmpty(entry.Value)) return false;
        if (m.ActivationRecord is { } activation)
        {
            if (!IsSafeRelativePath(activation.Path)) return false;
            if (string.IsNullOrEmpty(activation.BeforeStatus) || string.IsNullOrEmpty(activation.AfterStatus)
                || string.IsNullOrEmpty(activation.AfterHash)) return false;
            if (string.Equals(activation.BeforeStatus, activation.AfterStatus, StringComparison.Ordinal)) return false;
            if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(activation.Path))) return false;   // 激活目标须在写集内
        }
        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }
        // **证据关系（会诊 MUST-4）**：只要出现真实证据，写集必须与变更登记精确对应、激活哈希必须等于写集哈希。
        if (m.ReferenceWriteSet.Count > 0 && EvidenceRelationProblem(m) is not null) return false;
        return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);
    }

    private static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取并按结构+不变量+完整性校验的 manifest（不合格＝null）。</summary>
    public MigrationManifest? LoadValidated()
    {
        var m = LoadManifest();
        return m is not null && IsManifestIntegrityValid(m) ? m : null;
    }

    /// <summary>读取 manifest（原样，不校验；业务判定用 LoadValidated）。</summary>
    public MigrationManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private MigrationResult MarkBlocked(string reason)
    {
        var m = LoadManifest();
        if (m is null) return MigrationResult.Fail(reason, MigrationStage.None);
        m.BlockedReason = reason;
        m.Stage = MigrationStage.Blocked;
        m.RollbackRehearsed = false;
        m.RehearsalScope = null;
        m.CommitMarker = null;                 // 与校验规则一致：Blocked 态不得带提交标记（失败处理产出仍可加载）
        WriteManifest(m);
        return MigrationResult.Fail(reason, MigrationStage.Blocked);
    }

    private static string RehearsalScopeOf(MigrationManifest m)
    {
        var sb = new StringBuilder(m.SnapshotManifestHash).Append('|');
        foreach (var c in m.ChangedFiles.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void ApplyRepresentativeChanges(MigrationManifest m, string root)
    {
        foreach (var c in m.ChangedFiles)
        {
            var target = Path.Combine(root, c.Path.Replace('/', Path.DirectorySeparatorChar));
            switch (c.Kind)
            {
                case ChangeKind.Added:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "rehearsal-added", new UTF8Encoding(false));
                    break;
                case ChangeKind.Modified:
                    if (File.Exists(target)) File.WriteAllText(target, "rehearsal-modified", new UTF8Encoding(false));
                    break;
                case ChangeKind.Deleted:
                    if (File.Exists(target)) File.Delete(target);
                    break;
            }
        }
    }

    private void WriteManifest(MigrationManifest manifest)
    {
        Directory.CreateDirectory(_transactionRoot);
        _stageHook?.Invoke(manifest.Stage);
        manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true);
        _stageHook?.Invoke(manifest.Stage);
    }

    private void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            if (!IsSafeTarget(targetRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                throw new InvalidOperationException("unsafe_target:" + rel);   // 恢复目标与快照源都须安全
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
            if (string.Equals(Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                    _configRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                _fileRestoredHook?.Invoke(rel);   // 恢复中断注入点（**仅真实配置根**；演练副本不触发）
        }
    }

    /// <summary>
    /// 只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。返回**失败条数**——
    /// 不安全目标或删除失败**不得静默跳过**：调用方据此保持阻断（不得报告完整回滚）。
    /// </summary>
    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, HashSet<string>? ownershipVerified = null)
    {
        var failed = 0;
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeTarget(targetRoot, c.Path)) { failed++; continue; }
            var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
            if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
            if (ownershipVerified is not null)
            {
                // 归属已在**写入之前**核对并登记（见 CompleteRollback）；此后本事务自己的撤销会改变字节，
                // 故此处不再按（已失效的）写集哈希复核，只按预先核验的归属集合决定是否删除。
                if (!ownershipVerified.Contains(PathKey(c.Path))) { failed++; continue; }
            }
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }

    /// <summary>归属冲突预检：真实写入路径下，任何「新增」文件若不存在或字节不等于本事务所写 ⇒ 冲突（不删除）。</summary>
    private string? OwnershipConflictReason(MigrationManifest m)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!TryHashConfigFile(c.Path, out var currentHash, out var problem))
            {
                // **先判不存在**：目标不存在 ⇒ 无需归属证据（否则「只快照+登记、尚未写入」的合法中止会被永久阻断）
                if (problem == "file_missing") continue;
                return "rollback_addition_unverifiable:" + c.Path + ":" + problem;
            }
            if (!TryGetWriteSetHash(m, c.Path, out var owned))
                return "rollback_addition_without_ownership_evidence:" + c.Path;   // 存在但无证据 ⇒ 保留并阻断
            if (!string.Equals(currentHash, owned, StringComparison.Ordinal))
                return "rollback_addition_not_owned:" + c.Path;       // 他方文件/被改动 ⇒ 保留并阻断
        }
        return null;
    }

    /// <summary>指定根下文件的 SHA-256（用于演练副本的归属核对；不做真实根绑定）。</summary>
    private static bool TryHashConfigFileOnRoot(string root, string rel, out string hash)
    {
        hash = "";
        var full = Path.Combine(root, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return false;
        try { hash = Sha256Hex(File.ReadAllBytes(full)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>释放实际窗口并**使资格失效**（代次前进 ⇒ 历史时间戳/会话不可复用）。</summary>
    private void ReleaseQuiescence()
    {
        try { _quiet?.Dispose(); } catch { }
        _quiet = null;
        _quietValid = false;
        _quietGeneration++;
    }

    /// <summary>确保存在**有效（存续）**静止窗口：已有效则通过；否则重新取得并持久化会话/时刻/代次。</summary>
    private bool EnsureQuiescence(MigrationManifest m)
    {
        if (!_requireQuiescence) return true;
        if (!_quietValid || _quiet is null)
        {
            _quiet = _quiesce?.Invoke();
            if (_quiet is null) return false;
            _quietValid = true;
            _quietGeneration++;
        }
        m.QuiescedAtUtc = _utcNow();
        m.QuiesceSessionId = _sessionId;
        m.QuiesceGeneration = _quietGeneration;
        WriteManifest(m);
        return true;
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    /// <summary>安全枚举（**先拒绝重解析点再进入子目录**）；用于快照验证读端。</summary>
    private static List<string> EnumerateFilesSafe(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("snapshot_dir_reparse_point:" + dir);
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_dir_reparse_point:" + sub);
                pending.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_file_reparse_point:" + f);
                result.Add(Rel(f, root));
            }
        }
        return result;
    }
    private static string Rel(string file, string root) => Path.GetRelativePath(root, file).Replace('\\', '/');
    /// <summary>当前持久化阶段（读取失败返回 None；用于副作用返回后核对未被重入改变）。</summary>
    private MigrationStage CurrentStageOrNone()
    {
        try { return LoadManifest()?.Stage ?? MigrationStage.None; }
        catch (Exception) { return MigrationStage.None; }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        lock (_sync)
        {
            ReleaseQuiescence();
            _lock?.Dispose();
            _lock = null;
        }
    }
}


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs L1-L40 SHA256=5e30475a6ccb025d326b850cd3a378467b4e398d93f1c229b439bbdd47edb6fc
**节选**：新夹具头部（全文 1300+ 行、49 条用例，全部为本批新增）；断言细节见 targeted TRX 与 mutations/summary.md。
using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 A 项：真实引用写入 + candidate→active 激活的事务接线**（全程只用隔离临时配置根，绝不触碰真实 User）。
/// 覆盖验收矩阵 A 的成功／确定拒绝／未知／取消（副作用前后）／恢复／重复／写后窗口，与状态、并发、故障三类矩阵行
/// （REF-S1..S9 / REF-C1..C3 / REF-F1..F9）。核心不变量：**事务阶段标记只在实际副作用成功、持久化并读回确认之后推进**。
/// </summary>
public sealed class R56ReferenceActivationWiringTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string FlowPath = "flows/plan.flow.json";
    private const string SecondPath = "flows/other.flow.json";
    private const string ConfPath = "OneDragon/plan.json";

    public R56ReferenceActivationWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56w-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();


## source: _workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md L1-L60 SHA256=958ce01ef06f5515fc9085d18b396ecf0e3ff3f29638bc4850e84c877556fe35
**节选**：33 项突变台账的头部（说明 + 前若干项，含补丁原文与三段判定）；逐项完整记录见同批 `mutations/<id>/record.json` 与各目录日志/TRX。
# 反向突变台账（28 项，供审查者核对判别力）

每项＝对最终源码的**单点削弱**；判定要求 baseline Passed / mutant Failed（构建 exit 0、测试 exit>0 且命中具名断言）/ restored Passed，
且恢复后源码**逐字节**等于原始哈希。逐项目录含 `build.log`、`baseline.log|trx`、`mutant-build.log`、`mutant.log|trx`、`restored-build.log`、`restored.log|trx`、`record.json`。

## 逐项：补丁（before → after）与判定

### M1-no-readback-confirmation
- 描述：不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `6d767ff40954f40b464146623bea7d0a721313bbfdbbc29997ac0b373a4570bc`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback`（testId `9120aa3d-1436-a8f6-fa8a-da29cec5c17f`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `SelfReportedSuccessWithoutRealWrite_BlocksOnReadback`

```text
--- MUTANT 补丁（before → after）---
替换前：
                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
替换后：
                    // MUTANT: 读回确认被跳过
```

### M2-no-cross-check-with-change-registry
- 描述：去掉写集与变更登记的交叉核对（漏项不再拒绝）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `db08f0eff7df4d6a1e521692893885ba0409f04861e39d0993438428f2856251`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.WritesetMismatch_RegisteredNotCovered_Blocks`（testId `bb355b0d-c65c-d267-0304-1c78222244af`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `WritesetMismatch_RegisteredNotCovered_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (!seen.Contains(PathKey(record.Path)))
                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项
替换后：
            // MUTANT
```

### M3-no-outside-write-detection
- 描述：去掉「写集外不得改动」检测
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `80b7ae4591a9755991b5724711ec94b8bfc51e7c9ad30b50c91855e23404a472`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks(deleteInstead: False)`（testId `6608a8e3-0b21-3f57-a0f7-a2d88397e742`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `WriteOutsideDeclaredWriteset_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
替换后：
                // MUTANT: 写集外改动检测（改写支 + 删除支）整体被跳过
```



## source: MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs L1-L30 SHA256=3cf6ad4d7e377188709a47ee9d5ca34488226fb71a65fe26e6717f968a7810c2
**节选**：MigrationRehearsal.cs 全文 225 行，本批仅按其能力边界修正**文案与注释**（建议级发现）；头部注释与签名段在此，其余未改动。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>演练步骤结果（报告行）。</summary>
public sealed record MigrationRehearsalStep(string Name, bool Success, string Detail);

/// <summary>演练报告（结构化；供 UI 展示与验收单取证）。</summary>
public sealed class MigrationRehearsalReport
{
    public bool Success { get; init; }
    public string RehearsalRoot { get; init; } = "";
    public string ConfigRoot { get; init; } = "";
    public string TransactionRoot { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    /// <summary>快照清单哈希（事务产物）。</summary>
    public string SnapshotManifestHash { get; init; } = "";
    /// <summary>事务标识（取证：与 manifest/台账对照）。</summary>
    public string TransactionId { get; init; } = "";
    /// <summary>最终事务阶段（取证：回滚后应为 `RolledBack`）。</summary>
    public string FinalStage { get; init; } = "";
    /// <summary>参与比对的文件集合（相对路径，排序）。</summary>
    public IReadOnlyList<string> ComparedFiles { get; init; } = [];
    /// <summary>证据路径（manifest／快照目录／演练根）。</summary>
    public IReadOnlyList<string> EvidencePaths { get; init; } = [];
    /// <summary>逐步骤结果（顺序即执行顺序）。</summary>


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs L1-L24 SHA256=4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed
**节选**：R56MigrationSwitchTransactionTests.cs 全文 1074 行，本批**未改动**该文件；此处只给夹具头部注释。
using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 事务迁移切换 v3** 夹具（owner 0 点击；**全程只用独立临时配置根**，绝不触碰真实 User 目录）。
/// 覆盖第 2 轮会诊 7 项必改：全入口持锁与「授权+执行」同临界区、RollingBack 幂等恢复、静止窗口全程且绑定会话、
/// 身份/路径/链接边界、未决事务拒绝开新、变更归属基线校验、结构+状态不变量与全字段完整性。
/// **能力边界（如实）**：仍未接线真实引用服务/激活实现与生产消费侧（本类只提供强制检查点 API）；
/// 静止窗口由调用方提供委托，夹具只用桩验证「未取得/非同会话 ⇒ 拒绝提交」。
/// </summary>
public sealed class R56MigrationSwitchTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R56MigrationSwitchTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L1-L4 SHA256=a512c6a1d3bdf7fb9d71ff9c57a2a07acbedfdac27bfe7625267046c266b6d3a
**节选**：ClaimSurfaceManifest.txt 全文 629 行（含本批新增 6 行）；本批逐行改动见 claims/claims-diff.txt 与 claims/claims-diff-r2.txt。
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md0005270E844D1911F683543481FC16716601671F52809F6E09C64C5F98B9C6841| 任务中心根级触发器（`trigger.time*`） | RunStore 持久化 | `R54MechanismSchemaTests`；**引擎消费未接线**（§19.4） | **未闭合**，保留门禁 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md002BA9B048996078CEA46179D65012F64869DA755D64F9CC28347327CBFABEB41**结论（如实）**：R5.6 **仍未收口**；v3 在「普通入口持锁、实例内串行、RollingBack 续做」上有实质改善，但上述反例仍直接破坏原处置目标。组件**未接线**（无生产消费侧 ⇒ 不构成生产风险，也不得据此主张任何一致性/闸门成立）。真实引用/激活编排、助手侧元数据恢复、生产消费必经检查点与实机证据…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md01DB1F7BF526B9AD72ECED9B2A984DE189DFF10C090636AA4BDF99B016CBE8181| B-1 | 新增执行状态的旧消费方兼容未闭合 | §4.0 租约文件 version 升 2（旧消费方按 Unsupported 响亮拒绝，不依赖加法被忽略）；v1 向后读兼容；静止判定扩展=未决 Submission+已受理未终结台账+Pending 任一存在即非静止 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md08C42317C2D5FBEEE87B4199D8A3D15ABC14D0D5E8177A0F0B783FA30AF9629C1| **并存风险与未验收范围**的登记 | **并存防护验收**、旧直通路径（`start_bgi(args)` 等）的**覆盖或排除裁决** |


## contract: _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt L1-L2 SHA256=83348a4ea8a58395d314aa715293e6fce647e675252e27e148bfbd0a7af89fb1
第 2 轮声明面再生的逐行差异（+2/-0）。
+ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0ECA16DFE00D7C304AAD237765B0A219F16C96FF6A36FF261EA0E6654150D9491- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。**存在未闭合 MUST/IMPORTANT ⇒ 按纪律 R2 
+ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md733C458BF96A9BA8D1F07B23A7471B9748C42E4A535CDE80DFFC7D16AC2B004F1- **结论原文要点**：「**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收 A 项**」——**MUST 


## contract: _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r3.txt L1-L3 SHA256=03ec3a5f5b246779d6b92ab09f17ee71c7273247a4b5ed7bed1a2ace0519f81a
第 3 轮声明面再生差异（+3/-0）。
+ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md48DEA8839A949F2D1E7FE807946649762617CCE59D73B2393F03BBE4BC058DE11- **计数**：本轮后子批累计 **2/8**（无失
+ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4B9CC67587AC3D244783DF4BCE8CCDA1094C99A6EBAE3E1509F5936459DB0A951- **结论**：第 1 轮 9 项中 **已闭环 4
+ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE9BA25E311E2328BCD5BDA16CBC28F43AC14113E65BBBF2ED5A8DFCB6517C9C11| 8 | IMPORTANT | 同实例 monit


## contract: Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md L5252-L5342 SHA256=c6d5877d2d8cd5de9264a8f6a43c38cd43e485facf7d3d876b40ae302420c4d9
**节选**：本批新增的 §24.129 全节（含 §24.129.7／§24.129.8 两轮会诊逐项处置）（L5252-L5342）。
## §24.129 R5.6 A 项主线施工子批——隔离配置根的真实引用写入与 candidate→active 激活接线（2026-09-29）

### §24.129.1 范围、来源绑定与离线边界

- **本批只施工 R5.6 验收矩阵 A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程只在**隔离配置根**内；**不**执行真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。B–F 与真实入口/实机门**本批未验收**。
- 开工实况：分支 `main-OldTeaBag-B168`、HEAD `22ccd6ee2721abb1642535253997c57dbf9020d6`；开工快照与 23 行风险矩阵见 `_workflow/r56-reference-activation-wiring-2026-09-29/`（`workflow.py begin` 先于任何代码改动）。
- 已 integrated 的 `r56-migration-audit`／`r56-activation-prep` **不重复接收**；`tools/mistletoe/deliveries.py` 只读发现检查在开工与收口各运行一次。
- **只读子 Agent**（固定开工 ref `22ccd6ee2`，只读、零写入）独立复核三问：跨程序集可达性、写方/消费方枚举、写集与回滚归属；报告 `_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md`。其结论与主执行者审计一致，但**仍由主执行者按最终源码复核**，且不替代回归与突变。
- 材料外变更：工作区其余历史证据、`.bak`／`.stale`、日志、`TestResults`、DLL 与两份既有未提交设计文档（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）**完整保护、未触碰、未提交**。

### §24.129.2 反例先行：阶段标记不等于真实副作用（本批要推翻的旧语义）

- `MarkReferenceUpdateCompleted`／`MarkActivated` 实现为 `Advance(stage)`：**只校验合法转换并落 manifest**，不执行任何引用写入或激活动作。
- 可定位反例（`R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired`）：未注入真实副作用端口时，**零文件写入**即可 `SnapshotReady→ReferenceUpdating→Activated→RehearseRollback→Commit` 全部成功，且配置根字节始终未变（假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
- 另证明 R1 迁移器产出的 `activation.status = "candidate-ready"` 在固定版本中**只有读取判定与拒绝路径**（`TaskCenterHost` 保存/启动/移交启动/移交恢复四处禁写禁启、`WorkflowPlanner` 预检阻断、UI 只读预览），**没有任何生产写入路径**把该状态改写为 `active`。

### §24.129.3 最小实施（真实副作用 + 读回确认先于阶段推进）

- 新增 `IMigrationEffectService` 与真实实现 `WorkflowFileMigrationEffectService`（`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`）：引用重命名（`nodes[].ref.config`）、新增文件、`candidate-ready→active` 激活（读改写）；逐文件**临时文件 + 同目录替换**、**保留原 BOM 形态**、**写前字节复检**（读后被锁外改动即放弃）、解析失败**隔离跳过不覆盖**；结果分**成功／确定拒绝／写后不明／取消**四类并如实回报**已落盘文件数**。
- `MigrationSwitchTransaction` 新增两个真实入口（端口经构造注入，与既有 `quiesce`／`stageHook` 同模式）：
  - `ApplyReferenceUpdate(plan)`：**写集声明校验**（非空、路径安全、无重复、逐项等于已登记变更归属且无漏项/多项；`Deleted` 不支持）→ 真实副作用 → **语义读回 + 字节读回** → 才推进 `ReferenceUpdating` 并持久化 `referenceWriteSet`；另做**写集外零改动**核对（基线文件逐字节不得变化）。
  - `ActivateCandidate(request)`：须已完成已确认的引用更新；目标必须**在已确认写集内**；真实激活 + **状态读回** → 才推进 `Activated` 并持久化 `activationRecord`（同步更新写集哈希，因激活同样改写了该文件）。
- **失败语义**：拒绝／写后不明／副作用后取消 ⇒ `Blocked`（fail-closed、**不盲目重试**、零生产许可）；**副作用前取消**（`CompletedWrites==0`）⇒ 保持当前阶段、可回滚；已到目标阶段时**幂等重读复核**（不二次触发副作用，盘上漂移即拒绝）。
- **提交与回滚**：提交前复核写集与激活记录（`commit_recheck_failed`，锁外漂移一律拒绝）；回滚**先撤销真实激活**（读回当前状态 → 反向副作用 → 再读回确认），再恢复旧字节、按归属删除新增，并要求**旧态字节与引用一致**；未确认即 `Blocked`，**绝不报告完整回滚**。
- **持久化与校验**：manifest 新增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord`，全部纳入 `ManifestIntegrity` 摘要与**结构与状态不变量**（写集路径安全、激活目标须在写集内、真实事务的 `ReferenceUpdating/Activated/Committed` 必须携带对应读回证据）；`realEffectsRequired` 由开事务时是否注入端口决定并受摘要覆盖，**篡改后重算摘要仍判无效**。

### §24.129.4 验证证据（绑定最终代码版本）

- **定向**：`R56MigrationSwitchTransactionTests` + `R58MigrationRehearsalTests` + `R56ReferenceActivationWiringTests` **111/111**（第 1 轮会诊修复后复跑）；同条件开工基线（前两类）**70/70**。
- **助手全量**：**1611 通过 / 2 跳过 / 0 失败 / 1613**；同条件开工基线 **1570 / 2 / 0 / 1572**；逐 testId 差集 **added=41 / removed=0 / changed=0 / unchanged=1572**。
- **反向突变 28 项**（`_workflow/…/mutations/`，逐项含 build/baseline/mutant/restored 独立日志与 TRX、补丁原文与三段判定，汇总见同目录 `summary.md`）：baseline Passed / mutant Failed（构建 exit 0、测试 exit>0，命中具名断言）/ restored Passed，**源码逐字节恢复**（事务文件 `46668E1CB646…`、引用服务文件 `6AF93577F049…`）。其中 M3、M5、M14、M28 为**整块/组合削弱**型突变（同一位点存在多层独立复核，单独削弱任一层会被另一层拦下，故按整块定义以取得判别力）——此点如实登记，不主张「每层都有独立判别力」。
- **矩阵覆盖**：状态/并发/故障 **33 行**（其中 32 行 `covered` 并各绑定有效突变；`REF-C4`「同实例并发提交/回滚」为 `not_applicable`：同实例串行边界由代码结构（共用 monitor）承载，真实交错的单点突变不可确定性构造——已附尝试记录，判别力由不变量夹具 + `REF-C2`/`REF-C3` 承担）。
- **部署目标**：全部构建与测试带 `-p:DeployToBgiTools=false`；部署目录 1158 个文件、**最新最后写入时间 2026-09-27 09:29:33（早于本批 2026-09-29）**，即本批**未写入部署目标**（该事实只支持「本批未留下可观察变化」，不主张过程中从未写入）。
- **BGI 侧**：本批未修改 `BetterGenshinImpact` 任何文件（`git status`／`git diff` 可核），故未跑 BGI 相关回归。

### §24.129.5 未闭合项、owner 检查点与门禁

- **B–F 六项启用前置继续未闭合**：助手流程/引用/激活元数据恢复范围（B）、生产消费必经检查点（C）、真实静止窗口写方全覆盖（D）、真实入口回执与责任（E）、目标机路径身份（F）本批**未验收**。
- **owner 检查点（1 项，正确性相关，本批不自行裁决）**：BGI 侧 W4 引用服务 `OneDragonConfigReferenceService` 为 `BetterGenshinImpact` 程序集内 `internal static`，而助手工程**无**对 BGI 的程序集/项目引用（`using BetterGenshinImpact` 命中 0、`InternalsVisibleTo` 只授予 BGI 单测）⇒ 助手进程**无法直调** W4。故**生产环境应由哪一端提供引用写方**（BGI 侧直调 W4／下沉共享库／经既有 IPC 委派）**无权威材料裁决**；本批的真实引用写方是助手侧实现（隔离根内），**生产接线保持关闭**，不据本批开启。待 owner 裁决后再定接线归属。
- **门禁**：真实 `User` 目录切换、生产构造开门、E3/E4/E5、热键、R5.8 签署、实机与生产进程门**继续关闭**；本批**不**声称 R5.6 或 R5 完成。
- 既有建议级残项 `R56-D1`（提交后快照损坏 + 直接授权交错）**状态未变**；本批未改其触发条件。

### §24.129.6 设施与声明面

- 设施：`workflow.py begin`（开工快照 + 原始矩阵）／`audit --stage review` + `verify`／`audit --stage closeout` + `verify`；索引、`git status --porcelain`、本批未暂存/已暂存 diff 与材料外变更区分随快照保存。工具只做机械核验，`quality_verdict` 恒为 NOT PROVIDED。
- 本批改动声明面（新增状态/门禁措辞）⇒ 按 §17.4-A 第 1 条 `CLAIM_SURFACE_REGENERATE=1` 再生清单并纳入提交评审，清除变量后复跑守卫。
- 会诊：本子批独立计数（上限 8 次）；逐轮记录见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.129.7。

### §24.129.7 会诊（第 1 轮）与逐项处置（子批计数 1/8）

- **渠道/模型/强度**：既有 GPT 只读会诊工具（自动附本批 `git status` 与 staged/unstaged diff）；`gpt-6-astra`／`medium`；attempts=1。请求与预检见 `consultation/review-request-v1.md`、`consultation/preflight-v1.json`（10 个白名单文件；估计输入约 28 万字节 ≈ 7 万 token，未触发本地回退条件）。
- **结论原文要点**：「**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收 A 项**」——**MUST 5 项 + IMPORTANT 4 项 + 建议级 1 项**。报告原文见 `consultation/review-round1-report.md`。
- **逐项处置（全部按原级采纳，同一修复批；每条均新增夹具并绑定有效突变）**：

| # | 原级 | 会诊要点 | 处置（修复 + 反例 + 突变） |
|---|---|---|---|
| 1 | MUST | 激活会把真实引用漂移**重新登记为合法状态**（同步写集哈希时吸收漂移），随后允许提交 | `ActivateCandidate` 在副作用前核对**盘上字节 == 已确认写集哈希**；漂移即 `activation_precondition_drifted` 置 Blocked、零副作用、写集不被污染（`Activation_AfterDrift_IsNotAbsorbed` / M19） |
| 2 | MUST | 声明为 Added 但**从未由事务创建**的文件会被回滚删除；且 Added 允许覆盖他方文件 | 新增目标写入改为 `overwrite:false`（竞争窗口内被创建即拒绝，不覆盖）；回滚在真实写入路径要求**归属证据**（字节必须等于本事务所写），否则保留并 `rollback_addition_not_owned`／`rollback_addition_without_ownership_evidence` 阻断（`ForeignAddedFile_IsPreservedAndRollbackBlocks`、`AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback` / M22、M28） |
| 3 | MUST | 写集外**新增文件**完全漏检（只比较基线清单） | 新增「配置根当前文件集合 == 基线 ∪ 声明新增」核对，更新面与提交面都执行；不符即 `unexpected_new_file_outside_writeset` 阻断（`NewFileOutsideWriteset_Blocks`、`CommitRejectsNewFilesOutsideWriteset` / M20） |
| 4 | MUST | manifest 真实证据要求可通过改字段 + 重算摘要绕过；缺写集↔登记、激活哈希↔写集等关系不变量 | 提交/授权门槛改为**绑定本实例**（`realEffectsRequired \|\| _effects is not null`），并把**证据关系不变量**（写集键集合恰等于非删除登记、身份唯一、激活哈希等于写集哈希）纳入结构校验（`ManifestTamper_WithoutRealEvidence_IsRejected` 六个分支，含「降级并重算摘要」支 / M23、M14） |
| 5 | MUST | 回滚读回检查会**空过**（写集在部分写后为空），可能假报完整回滚 | 回滚后改为核对**完整基线字节集** + 全部「新增」必须不存在（与写集无关），不符即 Blocked，绝不报告 `RolledBack`（`Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet` / M21） |
| 6 | IMPORTANT | 确认写集后仍能改变更登记，破坏精确写集约束 | `RecordChanges` 在写集非空时拒绝（`change_registry_frozen_after_reference_update`）（`ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate` / M24） |
| 7 | IMPORTANT | 激活未限定 `candidate-ready→active`；且已等于目标态时零写入「成功」并记录未经证实的前置状态 | 只接受权威 D13 转换（其余 `unsupported_activation_transition`）；副作用前**观测盘上现值**，已生效即 `activation_already_applied`、与声明 before 不符即 `activation_precondition_status_mismatch`（`Activation_RejectsAlreadyAppliedAndForeignTransitions` / M26、M27） |
| 8 | IMPORTANT | 副作用/读回抛异常未收敛为 Blocked，允许重复执行；敌意文档形状可抛异常 | 端口调用与全部读回均 try/catch 收敛为 `*_exception_unknown` 置 Blocked（不重复执行）；引用改写对非对象节点/非对象 `ref` 稳定转结构化拒绝，不写回（`EffectPortExceptions_BecomeBlockedWithoutRepeat`、`HostileDocumentShape_IsRejectedNotThrown` / M25） |
| 9 | IMPORTANT | 关键夹具存在被遮蔽断言与「不证明撤销」的回滚断言 | 篡改测试每分支改为从**合法 manifest 独立副本**出发（并新增降级/多余写集/哈希不符三支）；「副作用后取消」改为真实部分写入后取消；新增「回滚确实调用真实激活撤销」夹具（端口请求序列）与同实例并发提交/回滚不变量夹具；突变补至 28 项并附**补丁原文 + 三段判定**（`mutations/summary.md`）（M18、M15、M16、M20） |
| 10 | 建议 | 演练报告文案超出证据 | `MigrationRehearsal` 步骤名与类注释改为如实表述（该入口**未注入真实副作用端口**，两步为阶段标记演练）（`FixedRehearsal` 文案；R58 夹具 14/14 复跑通过） |

- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。**存在未闭合 MUST/IMPORTANT ⇒ 按纪律 R2 必须复会诊**（第 2 轮为验证轮）。
- **边界**：会诊只审阅所附代码与 diff，未独立核验原始 TRX/突变日志与运行行为；本批据此不主张无条件完整验收。B–F 与真实 User/生产门未作为本批缺陷要求补齐，仍关闭。

### §24.129.8 会诊（第 2 轮，验证轮）与逐项处置（子批计数 2/8）

- **渠道/模型/强度**：既有 GPT 只读会诊工具；`gpt-6-astra`／medium；attempts=1（预检见 `consultation/preflight-v2.json`，白名单 10 文件 + 自动附加 diff）。
- **结论**：第 1 轮 9 项中 **已闭环 4 项**（MUST-3 原反例、MUST-5、IMPORTANT-6、IMPORTANT-7 原反例），**仍未闭环 5 项**（MUST-1 版本绑定、MUST-2 归属保护、MUST-4 关系不变量残余、IMPORTANT-8 异常边界、IMPORTANT-9 夹具/突变台账），并新报 **4 项 MUST + 4 项 IMPORTANT**。报告原文见 `consultation/review-round2-report.md`。
- **逐项处置（全部原级采纳，同一修复批；均新增夹具并绑定有效突变）**：

| # | 原级 | 会诊要点 | 处置（修复 + 反例 + 突变） |
|---|---|---|---|
| 1 | MUST | 新增文件作激活目标时，撤销激活**先写后查**，正常回滚必然归属冲突、且可能改写他方文件 | 归属预检**先于任何写入**（先判不存在、再判证据），并在撤销/恢复之后按**预核归属集合**清理新增（`Rollback_AddedActivationTarget_ConvergesAndRemovesIt`、`Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten`／M29、M30） |
| 2 | MUST | 回滚归属保护仍可通过**降级 manifest 标记**绕过 | 归属判据同样绑定本实例（`realEffectsRequired \|\| _effects is not null`）（`DowngradedManifestFlag_DoesNotBypassRollbackOwnership`；**判别力未由突变证明**，见下方残项） |
| 3 | MUST | 激活未绑定**写入所依据的版本**（前置检查与端口写入之间仍可漂移） | 激活请求新增 `ExpectedContentHash`，端口在写入前核对盘上字节哈希（两处），不符即 `activation_content_hash_mismatch` 置 Blocked（`Activation_VersionBinding_RejectsDriftAfterPrecheck`／M31） |
| 4 | MUST | 提交面文件集合核对**不是相等检查**（缺文件漏检） | 补齐「基线文件必须全部存在」一半 ⇒ `missing_baseline_file` 拒绝提交（`Commit_RejectsMissingBaselineFile`／M32） |
| 5 | MUST | 变更登记**重复身份**可由 `HashSet` 折叠绕过 | 结构关系不变量新增身份唯一性检查（`evidence_relation:duplicate_change_identity`）（`ManifestTamper_…` 新增分支／M34） |
| 6 | IMPORTANT | 只快照+登记 Added、尚未写入即中止的事务被**永久阻断** | 归属预检**先判不存在**（不存在无需证据），中止事务可安全回滚（`AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely`／M30） |
| 7 | IMPORTANT | 异常边界仍遗漏复核与回滚读回路径 | `RecheckActivationRecord`、撤销后读回、`TryHashConfigFile` 的安全检查均纳入异常边界（收敛为拒绝/Blocked）（`EffectPortExceptions_…` 覆盖正向路径；M25） |
| 8 | IMPORTANT | 同实例 monitor 可重入 ⇒ 端口回调内重入可覆盖已完成回滚 | 新增 `_effectCallInProgress` **重入守卫**（覆盖全部变更入口）并在副作用返回后核对阶段未被改变（`ReentrantMutationFromEffectCallback_IsRejected`／M33） |
| 9 | IMPORTANT | 夹具与突变台账仍不一致（取消后未真实写入、M8 与 M6 重复、M22 判别力失真、矩阵残留说明、findings 记旧计数） | 「副作用后取消」改为**真实部分写入**；M8 重定义为「激活阶段前置门」并与 M6 去重；新增 M30（整块去除归属保护）以证明「他方文件被删除」可被检出；矩阵清除陈旧说明；`findings.md` 与本节统一为 33 项突变口径（`ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks` 等） |

- **计数**：本轮后子批累计 **2/8**（无失败/超时请求）。仍有未闭合 MUST/IMPORTANT ⇒ 继续复会诊（第 3 轮）。
- **本批残项（如实）**：`REF-C4`（同实例并发提交/回滚）与「降级 manifest 标记不得绕过归属保护」两条，**未取得可杀死对应夹具的单点突变**（尝试记录见 §24.129.8 表格第 2 行与矩阵 `REF-C4.reason`）：前者在两种获胜顺序下不变量均成立，后者以「判据只依赖标记」构造的探测突变仍使夹具通过。二者保留**夹具级反例**但**不主张突变判别力**，不计入「关键断言均已突变验证」的陈述。
- **边界**：会诊只审阅所附材料与 diff；B–F、生产接线与 owner 检查点不作为本批缺陷，仍关闭。


## contract: 槲寄生调度器总计划.md L411-L421 SHA256=28dfadc931b766cf6448c4080e2d30950e66c3225562ca0f5c4698e3dd69d7bb
**节选**：本批 2026-09-29 进度条目（L411-L421）。
## 2026-09-29：R5.6 A 项主线施工（隔离配置根的真实引用写入与 candidate→active 激活接线）

- **性质与范围**：施工批。只做 R5.6 验收矩阵 **A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程**隔离配置根**；**不**做真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。
- **反例先行**：先证明 `MarkReferenceUpdateCompleted`／`MarkActivated` 仅推进阶段——未注入真实副作用端口时**零写入**即可推进到 `Activated` 并提交成功（配置根字节始终未变，假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
- **实现**：新增真实副作用端口与实现（`MigrationReferenceActivation.cs`：引用重命名／新增文件／激活读改写，临时文件+同目录替换、保留 BOM、写前复检、解析失败隔离跳过、四类结果与已落盘数如实回报）；事务新增 `ApplyReferenceUpdate`／`ActivateCandidate`（写集声明校验 → 真实副作用 → 语义+字节读回 → **才**推进阶段并持久化读回证据；拒绝/未知/副作用后取消 fail-closed 置 `Blocked`；副作用前取消保持阶段；已到阶段幂等复核；提交前复核写集与激活记录；回滚先撤销激活再恢复旧字节并要求旧态一致）；manifest 增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord` 并纳入完整性与结构不变量。
- **验证**：定向 **119/119**（同条件基线 70/70）；助手全量 **1619 通过 / 2 跳过 / 0 失败 / 1621**（同条件开工基线 **1570/2/0/1572**；testId added=49／removed=0／changed=0）；**33 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；39 行状态/并发/故障矩阵（37 行 covered 各绑定有效突变，`REF-C4` 记为 not_applicable 并附尝试记录）；部署目标未留下可观察变化（最新写入时间早于本批）。
- **会诊**：第 1 轮判 MUST 5 + IMPORTANT 4 + 建议 1；第 2 轮（验证轮）判已闭环 4 项、未闭环 5 项并新报 MUST 4 + IMPORTANT 4。两轮发现均按原级修复并逐条补夹具与突变（子批计数 **2/8**，继续复会诊）。报告与逐条处置见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与 R5.3 §24.129.7／§24.129.8。
- **残项（如实）**：`REF-C4`（同实例并发）与「降级 manifest 标记不得绕过归属保护」两条仅有夹具级反例，**未取得可杀死夹具的单点突变**（尝试记录在案），不主张突变判别力。
- **owner 检查点（1 项）**：BGI 侧 W4 `OneDragonConfigReferenceService` 为 BGI 程序集内 `internal`，助手工程对其**零引用** ⇒ 生产引用写方的归属（BGI 直调／共享库／IPC 委派）**无权威裁决**，生产接线保持关闭，待 owner 定夺。
- **门禁**：**B–F 六项启用前置与真实入口/实机/真实 User 门继续未验收**；本批**不**等于 R5.6 完成，也不等于 R5 完成。详见 R5.3 §24.129 与 `_workflow/r56-reference-activation-wiring-2026-09-29/`。



## contract: Docs/design/mistletoe-parallel-deliveries.md L107-L115 SHA256=3e6538e55f143d320f575c4912e5ddd2353a9f0cbf9bef524ac5a2dba94031ce
**节选**：本批「R5.6 A 项进展」段（L107-L115，全文 150 行）。
### R5.6 A 项进展（主线施工，2026-09-29）

`r56-migration-audit` 与 `r56-activation-prep` 的集成状态**不变**（均为 `integrated`），其 A–F 六项前置的**状态更新**如下：

- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 119/119、助手全量 1619/2/0/1621、33 项反向突变、39 行矩阵覆盖）；两轮会诊（1/8、2/8）发现均按原级修复并逐条补夹具/突变，第 3 轮验证轮待跑。**仍未验收**：真实 `User` 目录、真实入口与生产消费；`REF-C4` 与降级绕过两条仅有夹具级反例（未取得击杀突变，如实登记）。
- **B–F**：**未验收**，状态不变（助手元数据恢复范围、生产检查点、真实静止窗口、真实入口回执、目标机路径身份）。
- **owner 检查点（新增 1 项）**：生产引用写方归属未裁决（BGI 侧 W4 为 `internal`、助手程序集零引用）。详见 R5.3 §24.129.5。
- **下一依赖**：owner 裁决写方归属 → 再定 B/C/D 的接线顺序；真实 User 切换与 R5.8 签署仍在 owner 侧。



## Review readiness (mechanical claims, not quality verdict)
{
  "opening": {
    "opening_snapshot": "_workflow/r56-reference-activation-wiring-2026-09-29/opening.json",
    "opening_head": "22ccd6ee2721abb1642535253997c57dbf9020d6",
    "risk_matrix": "_workflow/r56-reference-activation-wiring-2026-09-29/risk-matrix.json",
    "risk_rows": 39,
    "subagents": "used",
    "existing_results": "none",
    "review_round": 3,
    "prior_findings": 10,
    "quality_verdict": "NOT PROVIDED"
  },
  "risk_matrix": {
    "schema_version": 1,
    "batch": "r56-reference-activation-wiring-2026-09-29",
    "rows": [
      {
        "id": "REF-S1",
        "dimension": "state",
        "scenario": "ApplyReferenceUpdate：真实引用写入成功且逐文件读回哈希一致",
        "expected": "阶段推进到 ReferenceUpdating 且 manifest 记录精确写集（路径+写入后哈希）",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "0758b932-6dc7-e2bb-ba01-64525d386dc5"
        ],
        "mutation_ids": [
          "M17-writeset-evidence-not-persisted"
        ]
      },
      {
        "id": "REF-S2",
        "dimension": "state",
        "scenario": "ActivateCandidate：真实 candidate→active 写入成功且状态读回一致",
        "expected": "阶段推进到 Activated 且 manifest 记录激活记录（目标路径/before/after/哈希）",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "354f034a-261c-1a1c-f4a1-d0e65537878b",
          "dc0b9da1-85c1-362f-ba3d-726bbc7ca289"
        ],
        "mutation_ids": [
          "M7-no-activation-writeset-hash-sync",
          "M8-no-activation-stage-gate"
        ]
      },
      {
        "id": "REF-S3",
        "dimension": "state",
        "scenario": "引用更新被确定拒绝（Rejected）",
        "expected": "阶段不推进，置 Blocked，零激活可能，写集未持久化",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "5370c416-3263-2816-0923-f3ca38400567"
        ],
        "mutation_ids": [
          "M9-fail-open-on-non-success"
        ]
      },
      {
        "id": "REF-S4",
        "dimension": "state",
        "scenario": "引用更新写后不明（Unknown）",
        "expected": "fail-closed 置 Blocked，不盲目重试、不推进阶段、不产生生产许可",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "50dabd98-6202-71ef-c368-8c989718d15d"
        ],
        "mutation_ids": [
          "M9-fail-open-on-non-success"
        ]
      },
      {
        "id": "REF-S5",
        "dimension": "state",
        "scenario": "副作用前取消（Cancelled，CompletedWrites=0）",
        "expected": "阶段保持 SnapshotReady，配置根零字节变化，可安全回滚",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "20598e9e-d1cf-e411-1bc0-5dfc3ff9d58e"
        ],
        "mutation_ids": [
          "M9-fail-open-on-non-success"
        ]
      },
      {
        "id": "REF-S6",
        "dimension": "state",
        "scenario": "副作用后取消（Cancelled，CompletedWrites>0）",
        "expected": "置 Blocked，禁止提交与生产执行，必须回滚",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "d7b14dbb-bf53-79cc-8528-4112d77cf11d"
        ],
        "mutation_ids": [
          "M9-fail-open-on-non-success"
        ]
      },
      {
        "id": "REF-S7",
        "dimension": "state",
        "scenario": "重复调用 ApplyReferenceUpdate（已 ReferenceUpdating）",
        "expected": "幂等：仅重读盘复核既有写集，不再次触发副作用，不产生第二次写入",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "00ca64e4-2c51-3f2e-22d1-089054e8092e",
          "7b3765e7-b0cc-3f09-bf48-2f788dfcfa27"
        ],
        "mutation_ids": [
          "M15-no-reference-idempotent-recheck"
        ]
      },
      {
        "id": "REF-S8",
        "dimension": "state",
        "scenario": "重复调用 ActivateCandidate（已 Activated）",
        "expected": "幂等：仅重读盘复核激活记录，不再次触发副作用",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "00ca64e4-2c51-3f2e-22d1-089054e8092e"
        ],
        "mutation_ids": [
          "M16-no-activation-idempotent-recheck"
        ]
      },
      {
        "id": "REF-S9",
        "dimension": "state",
        "scenario": "未提交（ReferenceUpdating/Activated）时的生产执行请求",
        "expected": "零执行（not_committed），activation 不代表生产许可",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "34602a8b-5ceb-3ec8-3e54-b72f31f37323"
        ],
        "mutation_ids": [
          "M10-no-production-commit-gate"
        ]
      },
      {
        "id": "REF-C1",
        "dimension": "concurrency",
        "scenario": "未持独占锁调用两个真实副作用入口",
        "expected": "均返回 lock_not_held，零写入",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "d17031e9-76c3-0d5d-54f0-10ec27c99423"
        ],
        "mutation_ids": [
          "M11-no-lock-guard-on-real-effects"
        ]
      },
      {
        "id": "REF-C2",
        "dimension": "concurrency",
        "scenario": "第二实例在事务进行中调用真实副作用入口",
        "expected": "transaction_busy，零写入",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "290e60ec-fc88-cb39-4a78-24a83936b1e3"
        ],
        "mutation_ids": [
          "M11-no-lock-guard-on-real-effects"
        ]
      },
      {
        "id": "REF-C3",
        "dimension": "concurrency",
        "scenario": "激活后、提交前并发回滚（另一入口）",
        "expected": "提交被拒（非法阶段），生产执行零动作",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "2de69b2b-21a6-a671-181f-4542f65c91a5",
          "8ad7acab-355e-b7cd-e932-b3944679a56b"
        ],
        "mutation_ids": [
          "M10-no-production-commit-gate"
        ]
      },
      {
        "id": "REF-F1",
        "dimension": "fault",
        "scenario": "写集与变更登记不一致（缺项/多项/未登记路径）",
        "expected": "reference_writeset_mismatch 置 Blocked，阶段不推进",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "857945a3-f8e2-8547-97a5-893b500b5dec",
          "bb355b0d-c65c-d267-0304-1c78222244af"
        ],
        "mutation_ids": [
          "M2-no-cross-check-with-change-registry"
        ]
      },
      {
        "id": "REF-F2",
        "dimension": "fault",
        "scenario": "动作自报成功但盘上字节/哈希不符（假成功）",
        "expected": "读回不匹配 ⇒ Blocked，阶段不推进，不得假报成功",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "9120aa3d-1436-a8f6-fa8a-da29cec5c17f",
          "e5abd208-af17-1f7c-5422-654e0a52518d"
        ],
        "mutation_ids": [
          "M1-no-readback-confirmation"
        ]
      },
      {
        "id": "REF-F3",
        "dimension": "fault",
        "scenario": "真实副作用成功、阶段落盘前崩溃（.tmp 半写/写前抛错）",
        "expected": "阶段仍为 SnapshotReady；恢复后配置根回到完整旧态字节",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "5d121dc3-6ebf-526f-284c-f33ad5c5d66d"
        ],
        "mutation_ids": [
          "M12-stage-persisted-before-effect"
        ]
      },
      {
        "id": "REF-F4",
        "dimension": "fault",
        "scenario": "激活目标不在登记写集内",
        "expected": "activation_target_not_in_writeset 置 Blocked，零激活",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "3536a152-f6d5-71c7-091e-e34429b764b9"
        ],
        "mutation_ids": [
          "M4-no-activation-target-membership"
        ]
      },
      {
        "id": "REF-F5",
        "dimension": "fault",
        "scenario": "激活后读回状态非 active（状态未真正持久化）",
        "expected": "Blocked，阶段不推进，禁止提交",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "d79a5bcb-ff16-67dd-0cd7-7ec7703ff0ff"
        ],
        "mutation_ids": [
          "M13-no-activation-readback"
        ]
      },
      {
        "id": "REF-F6",
        "dimension": "fault",
        "scenario": "回滚后撤销激活并恢复旧字节；事务外新增文件保留",
        "expected": "激活目标回到 candidate-ready/旧字节，事务外新增文件逐字节不变",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "e245e79d-6c7e-5717-a660-5ebef4b84520"
        ],
        "mutation_ids": [
          "M7-no-activation-writeset-hash-sync"
        ]
      },
      {
        "id": "REF-F7",
        "dimension": "fault",
        "scenario": "manifest 被篡改为 Activated/Committed 但无写集/激活记录",
        "expected": "结构+状态不变量校验判无效，授权与提交均被拒",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "7bc93f93-430e-82c2-804a-4c05389c58f9"
        ],
        "mutation_ids": [
          "M14-no-real-evidence-invariants"
        ]
      },
      {
        "id": "REF-F8",
        "dimension": "fault",
        "scenario": "引用更新故障窗口：写入中途 IO 失败（Rejected/Unknown）",
        "expected": "已写文件不产生假成功；回滚后旧态字节与引用一致",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "f82f6945-fa88-1f8b-3e28-22e086ffa40a"
        ],
        "mutation_ids": [
          "M9-fail-open-on-non-success"
        ]
      },
      {
        "id": "REF-F9",
        "dimension": "fault",
        "scenario": "副作用自报成功但改动了**声明写集之外**的基线文件（含删除）",
        "expected": "unexpected_write_outside_writeset 置 Blocked，阶段不推进",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "6608a8e3-0b21-3f57-a0f7-a2d88397e742",
          "bffce8cb-8f93-1aab-026b-e376a32f2a31"
        ],
        "mutation_ids": [
          "M3-no-outside-write-detection"
        ]
      },
      {
        "id": "REF-S10",
        "dimension": "state",
        "scenario": "注入真实副作用端口后，旧阶段标记入口（MarkReferenceUpdateCompleted/MarkActivated）必须被拒绝",
        "expected": "real_side_effects_required，阶段不推进、零写入；旧语义仅保留给未注入端口的只读夹具",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "4e66362b-8fbd-926e-95d1-fc6403406da0"
        ],
        "mutation_ids": [
          "M6-no-stage-mark-gate"
        ]
      },
      {
        "id": "REF-F10",
        "dimension": "fault",
        "scenario": "已真实写入/激活的文件在提交前被锁外写方改动",
        "expected": "commit_recheck_failed 拒绝提交，未提交不产生生产许可",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "0c64790c-23ff-4198-f8b1-505ea6864375"
        ],
        "mutation_ids": [
          "M5-no-commit-recheck"
        ]
      },
      {
        "id": "REF-S11",
        "dimension": "state",
        "scenario": "激活只接受权威 D13 转换（candidate-ready→active），且盘上现值必须等于声明的 before 状态（已生效 ⇒ 拒绝，不得零写入伪成功）",
        "expected": "unsupported_activation_transition / activation_already_applied / activation_precondition_status_mismatch 置 Blocked，零副作用",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "30f5595d-3f84-02c1-37ee-76cbfaa50bdc"
        ],
        "mutation_ids": [
          "M26-no-activation-status-precheck",
          "M27-no-d13-transition-restriction"
        ]
      },
      {
        "id": "REF-S12",
        "dimension": "state",
        "scenario": "真实证据门槛绑定「本实例是否接入真实副作用」，改 manifest 标记并重算摘要不得降级",
        "expected": "real_evidence_required_for_production 拒绝授权；结构关系不变量另行拒绝不一致证据",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "7bc93f93-430e-82c2-804a-4c05389c58f9"
        ],
        "mutation_ids": [
          "M23-commit-gate-not-bound-to-instance",
          "M14-no-real-evidence-invariants"
        ]
      },
      {
        "id": "REF-F11",
        "dimension": "fault",
        "scenario": "引用更新确认后、激活前该文件被锁外改动（引用漂移）",
        "expected": "activation_precondition_drifted 置 Blocked；激活不触发；写集不被漂移污染",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "407278ae-9e31-c8fc-e655-1dc876365d35"
        ],
        "mutation_ids": [
          "M19-no-activation-pre-drift-check"
        ]
      },
      {
        "id": "REF-F12",
        "dimension": "fault",
        "scenario": "副作用在声明写集之外**新增**文件（更新面与提交面）",
        "expected": "unexpected_new_file_outside_writeset 置 Blocked（提交前同样拒绝）",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "e72e8b5a-bb74-e3b2-db18-b73e22e6549e",
          "fec4b8f3-65e3-0937-f877-c9b424871c3b"
        ],
        "mutation_ids": [
          "M20-no-new-file-detection"
        ]
      },
      {
        "id": "REF-F13",
        "dimension": "fault",
        "scenario": "登记为「本事务新增」的文件实为他方所有或被改动（含从未由本事务创建）",
        "expected": "回滚保留该文件并阻断（rollback_addition_not_owned / rollback_addition_without_ownership_evidence），不假报完整回滚；新增目标不得覆盖他方文件",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "08d25379-d7a3-7252-948c-d81f69c10dd0",
          "4a7e697f-3c47-bd13-5759-79b2405e3218"
        ],
        "mutation_ids": [
          "M29-no-ownership-precheck-before-write",
          "M30-no-addition-ownership-enforcement",
          "M28-added-target-overwrite-allowed"
        ]
      },
      {
        "id": "REF-F14",
        "dimension": "fault",
        "scenario": "回滚后旧态核对必须覆盖完整基线字节集（成功写集为空的部分写/阶段前失败路径）",
        "expected": "rollback_restore_bytes_differ 置 Blocked，不报告 RolledBack",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "0ac6ce90-1a90-0a40-ff2f-374f287a5d6c"
        ],
        "mutation_ids": [
          "M21-no-full-baseline-verification-on-rollback"
        ]
      },
      {
        "id": "REF-F15",
        "dimension": "fault",
        "scenario": "回滚必须真实撤销激活（读回→反向副作用→再读回），不得仅靠快照还原字节",
        "expected": "端口收到 active→candidate-ready 的撤销请求；状态与引用一并回退",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "9595709b-d3d9-d6d3-b138-374e7f2e88ef"
        ],
        "mutation_ids": [
          "M18-no-activation-undo"
        ]
      },
      {
        "id": "REF-F16",
        "dimension": "fault",
        "scenario": "副作用端口抛异常 / 敌意文档形状（标量节点）",
        "expected": "异常收敛为 Blocked（unknown）且不重复执行；形状异常结构化拒绝且原文未被改写",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "4a42ae03-38a7-f449-0c96-d3f702762999",
          "cf0121e8-e2e1-2032-9bf2-785b7bf8026a"
        ],
        "mutation_ids": [
          "M25-no-port-exception-containment"
        ]
      },
      {
        "id": "REF-F17",
        "dimension": "fault",
        "scenario": "确认引用更新后仍尝试改变更登记",
        "expected": "change_registry_frozen_after_reference_update 拒绝（精确写集对应关系不得事后变更）",
        "critical": true,
        "status": "covered",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "2137126c-7151-96ff-3865-726893babc70"
        ],
        "mutation_ids": [
          "M24-no-change-registry-freeze"
        ]
      },
      {
        "id": "REF-C4",
        "dimension": "concurrency",
        "scenario": "同一实例上并发发起「提交」与「回滚」",
        "expected": "不变量：绝不出现「回滚报告完成却仍可生产执行」；终态只能是 Committed（新态）或 RolledBack（旧态），不出现半途混合",
        "critical": false,
        "status": "not_applicable",
        "reason": "同实例串行边界由代码结构承载（所有变更入口共用同一 monitor + 重入守卫）；真实交错的单点突变不可确定性构造。尝试记录：mutation M29/M33 分别去掉回滚前的归属预检与重入守卫，该并发夹具均未被杀（不变量在两种获胜顺序下仍成立），故本行判别力由不变量夹具 + REF-C2/REF-C3 的已验证突变承担，并如实登记为未由突变验证。",
        "counterexample_ids": [
          "targeted-final-frozen"
        ],
        "test_ids": [
          "8ad7acab-355e-b7cd-e932-b3944679a56b"
        ]
      },
      {
        "id": "REF-S13",
        "dimension": "state",
        "scenario": "激活写入必须绑定「已确认写集」的字节版本（前置检查之后、写入之前的锁外改动）",
        "expected": "activation_content_hash_mismatch 置 Blocked；漂移内容不被写入为 active",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "dfe239eb-e844-b8dc-4ee5-10416277eb87"
        ],
        "mutation_ids": [
          "M31-no-activation-content-binding"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      },
      {
        "id": "REF-F18",
        "dimension": "fault",
        "scenario": "新增文件作为激活目标（正常回滚路径）与他方替换新增文件",
        "expected": "合法回滚按**预核归属**清理新增并恢复旧态；他方文件在任何写入之前被识别 ⇒ 保留并阻断",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "fff51d5d-99a0-5d79-3b0e-a13f6faf9762",
          "fb7e6ff8-c357-8912-ec82-30545d47e58b",
          "f2436c03-f0c6-7bc9-9210-c5df8a3682a0"
        ],
        "mutation_ids": [
          "M29-no-ownership-precheck-before-write",
          "M30-no-addition-ownership-enforcement"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      },
      {
        "id": "REF-F19",
        "dimension": "fault",
        "scenario": "提交面文件集合核对须为**相等**（写集外基线文件缺失/被删）",
        "expected": "missing_baseline_file 拒绝提交",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "020506d4-fba1-6352-53d6-7285c481f1b0"
        ],
        "mutation_ids": [
          "M32-no-missing-baseline-half"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      },
      {
        "id": "REF-F20",
        "dimension": "fault",
        "scenario": "只快照+登记 Added、尚未写入即中止的事务",
        "expected": "可安全回滚（目标不存在无需归属证据），终态 RolledBack",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "3008c234-c0dd-60ee-6865-1bfb3e4b21e8"
        ],
        "mutation_ids": [
          "M30-no-addition-ownership-enforcement"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      },
      {
        "id": "REF-F21",
        "dimension": "fault",
        "scenario": "端口回调内同实例重入变更入口（monitor 可重入）",
        "expected": "reentrant_mutation_rejected；外层不得因重入发布错误阶段或污染登记/写集",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "93347cf9-00ca-e823-7ad7-628c19462f04"
        ],
        "mutation_ids": [
          "M33-no-reentrancy-guard-on-rollback"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      },
      {
        "id": "REF-F22",
        "dimension": "fault",
        "scenario": "manifest 变更登记出现重复身份",
        "expected": "结构关系不变量拒绝（evidence_relation:duplicate_change_identity）",
        "critical": true,
        "status": "covered",
        "test_ids": [
          "7bc93f93-430e-82c2-804a-4c05389c58f9"
        ],
        "mutation_ids": [
          "M34-no-duplicate-change-identity-check"
        ],
        "counterexample_ids": [
          "targeted-final-frozen"
        ]
      }
    ]
  },
  "review_control": {
    "existing_results": {
      "decision": "none",
      "reason": "逐项核对现有交付后，本批没有可复用的同版本结果：R56/R58 组件夹具（53+14 条）与 activation-prep 验收矩阵只覆盖「阶段标记语义」与准备型材料，而本批要推翻的正是「阶段标记可代表真实副作用」这一旧语义；已 integrated 的 r56-migration-audit / r56-activation-prep 不重复接收。基线 TRX 以 binding=historical 仅作差集对照，不作当前回归证明。"
    },
    "review_round": 3,
    "prior_findings": [
      {
        "id": "R2-MUST-1",
        "severity": "must",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M29-no-ownership-precheck-before-write",
          "mut-M30-no-addition-ownership-enforcement"
        ]
      },
      {
        "id": "R2-MUST-2",
        "severity": "must",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M30-no-addition-ownership-enforcement"
        ]
      },
      {
        "id": "R2-MUST-3",
        "severity": "must",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M31-no-activation-content-binding"
        ]
      },
      {
        "id": "R2-MUST-4",
        "severity": "must",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M32-no-missing-baseline-half",
          "mut-M34-no-duplicate-change-identity-check"
        ]
      },
      {
        "id": "R2-IMPORTANT-5",
        "severity": "important",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M30-no-addition-ownership-enforcement"
        ]
      },
      {
        "id": "R2-IMPORTANT-6",
        "severity": "important",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M25-no-port-exception-containment"
        ]
      },
      {
        "id": "R2-IMPORTANT-7",
        "severity": "important",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M33-no-reentrancy-guard-on-rollback"
        ]
      },
      {
        "id": "R2-IMPORTANT-8",
        "severity": "important",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M30-no-addition-ownership-enforcement",
          "mut-M31-no-activation-content-binding"
        ]
      },
      {
        "id": "R2-IMPORTANT-9-CLOSE",
        "severity": "important",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "mut-M8-no-activation-stage-gate"
        ]
      },
      {
        "id": "R2-RESIDUAL-1",
        "severity": "suggestion",
        "source_review_evidence_id": "review-round2-report",
        "disposition": "accepted",
        "reason": "残项（如实）：`REF-C4` 同实例并发与「降级 manifest 标记不得绕过归属保护」两条仅有夹具级反例；尝试以 M29/M33 与「判据只依赖标记」的探测突变击杀均未成功（记录在案），故不主张突变判别力。按 §17.4-A 第 ① 条，该情形属「证据等级如实登记」而非措辞类豁免；已写入 R5.3 §24.129.8 与矩阵行 reason。"
      }
    ],
    "criticality_reason": "本批矩阵全部 20 行为 critical（引用写入/激活副作用与阶段推进顺序、并发、故障窗口均为正确性面）。",
    "subagents": {
      "decision": "used",
      "reason": "固定开工 ref 的 1 个只读子 Agent 独立枚举跨程序集可达性、写方/消费方与写集回滚归属，用于交叉核对主执行者审计；核心状态链仍由主执行者单写。",
      "tasks": [
        {
          "question": "固定 ref 上只读枚举：①配置根引用文件的真实写方与消费方（含跨程序集边界）；②candidate-ready→active 的承载字段与全部消费点及是否存在写入路径；③事务写集在回滚路径中的用法与是否存在写新内容的步骤。",
          "read_only": true,
          "fixed_ref": "22ccd6ee2721abb1642535253997c57dbf9020d6",
          "opening_source_hashes": {
            "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
            "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "3952d85a4c57f0649b2107757fc4cba2235c0e8e67883d5b53daf1ad21612c90",
            "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9",
            "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
            "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs": "13686a3203719d2f7f30180cce93ce6c4d0ed2903f2e5bb8f0033c4d2c8a8bae",
            "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "05eb52dee8c357a25b99857d4c8cdfdf14804395a99c5887ab92d64b6171e725",
            "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "20cc45e620782409fea8fe715c0620838c546fb8133c9e57551f888477926bef",
            "槲寄生调度器总计划.md": "8196adda2178c57d3a1739c4fa4f5ee5d542f9f34959a6e06153ff6eb84e14f6"
          },
          "report_evidence_id": "subagent-readonly-audit",
          "note": "子 Agent 的固定 ref = 22ccd6ee2721abb1642535253997c57dbf9020d6；上表为开工快照全体 source 的哈希（工具要求与 opening 一致）。子 Agent 实际只读核查范围为该 ref 上的 MigrationSwitchTransaction.cs / 引用服务可见性 / activation 消费面。"
        }
      ]
    },
    "repair_batch_id": "r56-refactivation-r2-repair-2026-09-29"
  }
}

## Generated evidence overview (mechanical only)
{
  "tests": {
    "_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx": {
      "total": 119,
      "passed": 119,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx": {
      "total": 1621,
      "passed": 1619,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx": {
      "total": 1572,
      "passed": 1570,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/baseline.trx": {
      "total": 2,
      "passed": 2,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/mutant.trx": {
      "total": 2,
      "passed": 1,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/restored.trx": {
      "total": 2,
      "passed": 2,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-activation-stage-gate/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-activation-stage-gate/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-activation-stage-gate/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M29-no-ownership-precheck-before-write/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M29-no-ownership-precheck-before-write/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M29-no-ownership-precheck-before-write/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M30-no-addition-ownership-enforcement/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M30-no-addition-ownership-enforcement/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M30-no-addition-ownership-enforcement/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M31-no-activation-content-binding/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M31-no-activation-content-binding/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M31-no-activation-content-binding/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M32-no-missing-baseline-half/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M32-no-missing-baseline-half/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M32-no-missing-baseline-half/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M33-no-reentrancy-guard-on-rollback/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M33-no-reentrancy-guard-on-rollback/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M33-no-reentrancy-guard-on-rollback/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M34-no-duplicate-change-identity-check/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M34-no-duplicate-change-identity-check/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M34-no-duplicate-change-identity-check/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    }
  },
  "comparison": {
    "identity_basis": "testId; provider changes require manual reconciliation",
    "added_ids": [
      "00ca64e4-2c51-3f2e-22d1-089054e8092e",
      "020506d4-fba1-6352-53d6-7285c481f1b0",
      "04c34480-d5e1-38d0-dc14-c5b9c73fa2fc",
      "0758b932-6dc7-e2bb-ba01-64525d386dc5",
      "08d25379-d7a3-7252-948c-d81f69c10dd0",
      "0ac6ce90-1a90-0a40-ff2f-374f287a5d6c",
      "0c64790c-23ff-4198-f8b1-505ea6864375",
      "1c8e7435-46ff-4424-a7be-3ba95262b016",
      "20598e9e-d1cf-e411-1bc0-5dfc3ff9d58e",
      "2137126c-7151-96ff-3865-726893babc70",
      "290e60ec-fc88-cb39-4a78-24a83936b1e3",
      "2de69b2b-21a6-a671-181f-4542f65c91a5",
      "3008c234-c0dd-60ee-6865-1bfb3e4b21e8",
      "30f5595d-3f84-02c1-37ee-76cbfaa50bdc",
      "34602a8b-5ceb-3ec8-3e54-b72f31f37323",
      "3536a152-f6d5-71c7-091e-e34429b764b9",
      "354f034a-261c-1a1c-f4a1-d0e65537878b",
      "407278ae-9e31-c8fc-e655-1dc876365d35",
      "4a42ae03-38a7-f449-0c96-d3f702762999",
      "4a7e697f-3c47-bd13-5759-79b2405e3218",
      "4e66362b-8fbd-926e-95d1-fc6403406da0",
      "50dabd98-6202-71ef-c368-8c989718d15d",
      "5370c416-3263-2816-0923-f3ca38400567",
      "5b587371-78a8-8392-35c8-249047753815",
      "5d121dc3-6ebf-526f-284c-f33ad5c5d66d",
      "6608a8e3-0b21-3f57-a0f7-a2d88397e742",
      "7b3765e7-b0cc-3f09-bf48-2f788dfcfa27",
      "7bc93f93-430e-82c2-804a-4c05389c58f9",
      "857945a3-f8e2-8547-97a5-893b500b5dec",
      "8ad7acab-355e-b7cd-e932-b3944679a56b",
      "9120aa3d-1436-a8f6-fa8a-da29cec5c17f",
      "93347cf9-00ca-e823-7ad7-628c19462f04",
      "9595709b-d3d9-d6d3-b138-374e7f2e88ef",
      "bb355b0d-c65c-d267-0304-1c78222244af",
      "bffce8cb-8f93-1aab-026b-e376a32f2a31",
      "cf0121e8-e2e1-2032-9bf2-785b7bf8026a",
      "d17031e9-76c3-0d5d-54f0-10ec27c99423",
      "d79a5bcb-ff16-67dd-0cd7-7ec7703ff0ff",
      "d7b14dbb-bf53-79cc-8528-4112d77cf11d",
      "dc0b9da1-85c1-362f-ba3d-726bbc7ca289",
      "dfe239eb-e844-b8dc-4ee5-10416277eb87",
      "e245e79d-6c7e-5717-a660-5ebef4b84520",
      "e5abd208-af17-1f7c-5422-654e0a52518d",
      "e72e8b5a-bb74-e3b2-db18-b73e22e6549e",
      "f2436c03-f0c6-7bc9-9210-c5df8a3682a0",
      "f82f6945-fa88-1f8b-3e28-22e086ffa40a",
      "fb7e6ff8-c357-8912-ec82-30545d47e58b",
      "fec4b8f3-65e3-0937-f877-c9b424871c3b",
      "fff51d5d-99a0-5d79-3b0e-a13f6faf9762"
    ],
    "removed_ids": [],
    "changed_ids": [],
    "unchanged_ids": [
      "00142b57-1df8-928f-2651-3cbbb3206447",
      "0029f7b4-4cbb-e63f-7b5a-c30fa71e3281",
      "003a600a-a77b-ea57-dac9-336c7597f3a1",
      "003d5177-4eb2-cf90-dfc0-0922617f3967",
      "004be13c-df7b-6dce-dc23-8f25831c275a",
      "00561123-4e38-0327-8a59-60c754b57e14",
      "009197d2-4962-c363-1781-ec974087b085",
      "00bae332-096a-6738-aa59-fa898eb9d249",
      "00f53e62-c820-0cfc-f1b8-92d3e07e3280",
      "00fd1f0c-d2db-d03a-ec3d-4c1fa2db9c79",
      "01079a79-95f7-8058-b3d3-c07611ef62d6",
      "0147a014-80d6-daff-6fa2-066622dd1389",
      "0189c645-a3c5-e893-d377-56937c56d6ba",
      "019a9877-8f34-5643-0454-4b04bbee58a2",
      "01bd8e51-65e1-bf78-ec97-394754066ce7",
      "02121445-9cfa-e6ae-5a08-f0096bf979e5",
      "021b7f9f-0aee-4bb3-82c2-cd0a485f48e5",
      "0223a883-d0f3-d8be-d213-a74a83e4f9f3",
      "02305864-f10b-4e97-86d7-e604303554de",
      "023359c9-267e-2daf-a7cb-411d05b8545e",
      "024e5643-ef97-1f6e-5b7f-8200ca550fe7",
      "025141ac-b6a6-7938-e786-37c41145f43a",
      "02d9d50c-8920-a196-ab6a-c853befc2e4b",
      "0315f37c-45f6-8125-cd57-8e9658b09a66",
      "03221eac-6101-edd7-8229-82e79bab7090",
      "034f5f27-dd97-f4ca-61d5-0a63fc519270",
      "0366307a-5837-3de2-867a-0aede6702d98",
      "03ee9dd3-83e0-5921-6230-80e96ef329a5",
      "04442ed7-ad82-465e-5627-36fe971841df",
      "045de764-8532-677d-4ced-5a18a58883a5",
      "046edc8b-21d9-ee32-c595-04fbca9472b5",
      "0495c5c2-f2bf-78cd-28e4-54742d868691",
      "04af6a23-eaa4-291f-b8d4-35f990558b0d",
      "0510bf67-65ea-147d-7277-c229d2ec2b59",
      "052784c0-6d50-5e34-ab72-0ed54588bfb6",
      "054386e2-c6fd-49a3-d5c2-de430858192c",
      "054abd8e-5417-4420-236d-48c480a779be",
      "054b652d-c40f-684d-f64e-0a22289fe986",
      "0571ad29-6729-373b-9dfc-e398f772fd17",
      "057ecaf9-acdd-0fb3-b353-5fea565edbdc",
      "05c5442b-1658-6289-be67-54e1b3d7fec8",
      "05fb4393-9d22-27a2-41d0-ee4f8727f8d0",
      "05fface1-85ac-b4ab-1c49-1097352578a6",
      "06492130-398c-399f-6e68-527b7b5bc499",
      "067149f9-0370-4184-d971-d15f695706a2",
      "06a8c9f2-fa4d-c63c-2244-7747daa4bc2f",
      "06cfa5f6-a7a8-1274-202b-4ea3d454f210",
      "06d16786-48d0-02c8-6d54-afd6eb30370d",
      "06dba09a-e49b-c2fe-9402-b1e827f6da92",
      "06f9f636-aaad-06a7-4e32-a78785d62290",
      "078165f1-6c31-a760-fed6-de6958300469",
      "0791e4d4-2d59-bd27-5e2d-0abfc4c478d8",
      "07a9ab30-beff-e25a-f695-ea667ef86527",
      "07fc34dd-5e13-1688-f973-796a9039381a",
      "08014a98-94c7-5c1b-85a5-711be1f776c4",
      "0804af87-f343-333f-61d8-ecc9d77ea5f3",
      "0808d2a7-164c-c1ad-9ddd-d2a377128055",
      "0871427e-dcbe-6848-48d8-aa138c852ce1",
      "0878fd71-2dda-a1af-e169-e42d2a96a5df",
      "08871413-002b-e4e4-bc40-b7a852c49f37",
      "08a0ae26-bbb0-4758-a916-604b6207168f",
      "08f1c7c1-f15d-3e4c-7238-12c527ae5213",
      "08f4e732-b1d6-2bb4-bd69-9137ccd7e904",
      "0927c39f-e0cf-acb6-30ee-ed6f7d2b048c",
      "095a3ac4-3dac-a8b7-0057-a40a703e7058",
      "095abd7d-263f-ade9-f942-1bc1430e7470",
      "099829df-2e76-bdfd-f274-26b2c9a6a5d3",
      "09a05ca4-4ace-0785-981a-184674460496",
      "09a61b22-8045-cbcb-8acf-b25c18cb2848",
      "09fe8795-85ae-161f-13de-b462eae553e2",
      "0a05ef84-0523-4a1f-e4e7-2188426ffb7b",
      "0a33feed-7e0a-c4fe-19b5-fb778bc0a5ec",
      "0a3d044c-ed18-dd73-584b-75754bf5815e",
      "0a43a784-9763-43ca-71d5-c7fa3ef8e364",
      "0a5455aa-4a2d-3d48-3283-452a5dba23b8",
      "0a889cb7-3bce-7150-66a8-55fc4a4ddf5f",
      "0aaeca06-c904-2318-3c17-4850fac4875c",
      "0ad18e39-1bac-3b01-70da-1861a75fdd8d",
      "0ad96afc-4b16-be75-51fc-47c360770156",
      "0af21ded-4f28-aac4-d2e5-fe525dfa2a11",
      "0b970562-a75b-ca3f-5720-58fa0cdb7a06",
      "0b9712bb-ebf9-4b53-7dc1-dc96a0be7129",
      "0bc18e5b-5778-0c88-a844-a1e45ca5923e",
      "0bf48d71-bf36-69d4-3ce9-b95b0a390e8e",
      "0bf9c977-664e-5ffe-c39c-c2dced02d6b3",
      "0bfa7728-821b-ed8c-3cf9-4a6cac8c6553",
      "0c1d538d-bf33-7474-c7b7-e4443ff168f7",
      "0c4adbe0-49c7-c8cf-a943-6701e7b4ac4c",
      "0ca9b55e-bbbd-4256-2736-b43bc4c9e04c",
      "0ce085f0-0e7e-2726-1397-71c4505cae11",
      "0cec4223-d71b-e7dc-d677-c175650edc03",
      "0d052212-97ab-692f-e1a7-11c43c1abfb7",
      "0d3d066f-c4a5-b9b7-efff-855452aa5b5c",
      "0d5cc7f5-04b8-111a-8a9b-8727025fdb5f",
      "0d5d4664-d63a-36dc-c4b7-e80a1ea41c29",
      "0d5d5b4b-c48d-6c1c-26e2-85f4147fe8f8",
      "0e1187d5-6686-1d05-0e83-e82e837b7271",
      "0e4105d6-0c09-6fbc-3cb2-dc172274d528",
      "0e419e65-ea97-9104-dc61-306ad26adf4b",
      "0e45c80c-838c-ea53-95b3-8566619958bd",
      "0e6f7184-5df0-48a4-50fd-f4114cc0615d",
      "0e893e81-3b49-a44c-7d41-c56d0fd6eff3",
      "0e8c34cf-fcb8-7d9a-5214-ada45efcfb98",
      "0e9cfdd0-9994-a02f-ab43-d87f585627b7",
      "0e9d82b0-bf7f-3cad-4ad7-b619035603d3",
      "0f1b4b43-2174-bb8e-2959-449657fcc9d8",
      "0f28ef63-1962-b2c7-ac46-e277ebe8f527",
      "0f2cb31f-05b0-568e-9d26-4a686b620ddf",
      "0fe6e276-2320-ac40-58b3-3a84023b9b58",
      "0fed73b3-5124-3481-43ee-ef3bddd56b86",
      "0ff4fb0e-be67-5083-a7fe-24a472139116",
      "0ffcb24a-3e07-098e-ebf0-bfa83871e165",
      "101929e7-d0dc-7453-c004-ed9714a6599e",
      "1024a001-8280-110a-3f94-41e4229b1de3",
      "102caee8-97ad-b1e9-2cc2-2de07015ea43",
      "1085391a-daa8-0a43-1e8e-19fe0dade5f7",
      "10b11358-0d38-ac41-3599-6e9c6e13826f",
      "10c6a8cc-f33f-af9c-7181-18960c333def",
      "10c706cd-f9e7-e2b8-df67-bca171864646",
      "10dfd457-b43a-77c1-5c41-6bea79edd369",
      "10e0235f-7546-84da-a25e-bb8a80fe025c",
      "10fc23c2-fa03-9d03-a34c-502d5972cb65",
      "113f020c-1c29-e336-4c1a-3a50b69cbcc8",
      "11a8b597-c214-e249-f365-608e6fa633cd",
      "11c28d5e-d922-1dfd-ea48-4a52ddf39535",
      "11ce9403-f420-8054-c42e-70055a934c9f",
      "11d6262f-352d-e185-ac7e-ce17d38c0354",
      "11f522a8-241f-d91c-9304-1c32e6cd57c6",
      "121142e7-9bdb-3594-c6a0-9add18855844",
      "129fd047-3028-277b-c599-fbab9cafcc5b",
      "12a70f37-cf12-7de6-de6a-fd841495405a",
      "12d0e8ad-db9c-edf9-ee94-b64afd4db362",
      "131d17e6-b057-99df-39e8-01db618f7737",
      "13530bff-f176-5745-f265-8aaa2adce3e3",
      "1357f438-f8fd-ed43-413c-0a939516552b",
      "13a5bbe8-89f2-3d01-1a68-1c54ced7eccd",
      "14164b6a-cc69-3859-eee8-0630c138ce28",
      "141c3871-f4d2-0409-8fd8-74790a37f76a",
      "142ef5f1-b79f-c83a-cdce-c15299459e48",
      "1440192f-f2a7-024e-6ff0-c85606d3f668",
      "144e77c8-dde4-b90e-6ef4-67c275627d02",
      "148d427e-7143-588b-09e9-0074651b3590",
      "14addbd1-4d7d-814d-ec3d-c070098441ad",
      "14b12c92-31c4-d84d-5129-de01fac6b561",
      "14c7717c-4fab-0f2f-9e2b-5efcba4b124a",
      "14dd3e0f-2c52-548c-96f8-a12c8084c3c2",
      "151ac447-4101-a692-a5c2-d6b1f50b513b",
      "159174ae-6a51-5cfc-c09f-6f3bc06eaeb2",
      "15a13e67-cbe8-66f2-3b44-3878d536d5df",
      "15c2ace4-f76d-791f-78a2-7cec261109bd",
      "15dbe3b0-f449-f21b-aeb9-3d77ff171fee",
      "15f6117e-b614-0b91-832e-114d26b1209e",
      "1619b2b0-813d-3b20-086b-eb87ee62d0ff",
      "164246a2-ce6a-6430-5a57-7d6f1add27da",
      "16672732-edc7-4bb8-ca13-466826858396",
      "16860feb-8f9b-5ac1-d91a-8eb67eb8059f",
      "16a1b797-7b4b-37cd-339b-efbee393adc2",
      "16c35e31-f480-a72b-fc90-21246e4e2c42",
      "1716ee02-bfe0-2c6c-9e01-e6224bb95048",
      "1795f64c-da77-e8d7-2553-f809a15ead01",
      "17b632b8-4769-8870-9bcd-c783e73d90cc",
      "17fe3ac0-768a-8f20-c800-1122ffca4db4",
      "1845a269-edde-acc6-2863-aa8fda4ab402",
      "18b4a3db-dc7a-ea36-0f33-864f0940aea3",
      "194b3142-c253-a959-de7a-8a1b489c8e47",
      "194fcedb-5e86-679a-1cd6-c4485e36ae7c",
      "195e2102-a019-b234-3594-76fd527dafba",
      "197592fe-b175-34ac-9021-b4baa8d3575c",
      "1998488c-f983-de71-f1a9-a210758ec5f0",
      "19cf57a8-3e5d-df79-63d9-0f0c3007126c",
      "19e5baf9-b263-6b1f-5c8f-6ef294a14306",
      "19ff07a3-d7ef-2f7a-8f37-f827991c5aa3",
      "1a124453-a7b4-e27f-f8ff-45b6b1679d91",
      "1a26c543-b582-710f-dade-a54ce67ce9da",
      "1a270617-9920-6d68-3bae-9e32762b17c7",
      "1a3ca4a7-d969-e1aa-1ff0-0da847e2786f",
      "1a6fd421-c147-72bd-bf97-4f0f29722972",
      "1a7da4d6-d4b8-d91b-ed46-f34c5420aea7",
      "1a90fbc7-b7c3-9717-624c-6c4e4a6c3945",
      "1a981ac3-e494-c3e3-a8c9-1fa101f6398a",
      "1a9d4afa-5d09-4ef7-d1f7-d1751ee818e5",
      "1aceeccd-0427-4d41-c9de-cd3e54e63c02",
      "1ae03c6c-32f4-4acd-4022-f1433b83c5ff",
      "1b6b91d3-3dbe-ce42-ee59-4e73492fd276",
      "1b770f31-fabf-108e-9cff-f320a659957a",
      "1b7da3d7-39c4-3576-1f76-c13bedc18a2b",
      "1b966097-289d-5c9a-054c-6248d4c5624d",
      "1c3341c6-410d-0a23-173f-e21152311368",
      "1c3ec39b-92c2-7700-ac3e-7dc1bae2c896",
      "1c491d2b-a18c-d800-908f-6f2b75dc0445",
      "1c703abe-5121-67a6-efca-61d127f0f93f",
      "1c9118b6-37e5-b14b-e5e3-402aa46d43fc",
      "1ca019c8-8c12-999c-aaf8-fb8e279ca622",
      "1ca3c6bf-e3ef-4045-269c-159fe8ae537e",
      "1ce1a004-8458-54cc-3ebd-b38929f9326d",
      "1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc",
      "1d24eedb-5451-1e47-f74c-681c3a2d4d35",
      "1d68c536-f3ce-838c-2a56-f780b96d365d",
      "1d780516-9761-fa27-2f42-4d437a0b878c",
      "1dc7fe41-0d08-df13-8a38-e8102ce1fda7",
      "1dd4356a-07e8-ca20-9443-79d10b4919b6",
      "1de70f01-c8c7-4564-091c-8a3e9a15b0ca",
      "1e300de3-48b6-274a-aec5-41b657a7cd8e",
      "1e60fe17-d75e-c8f0-b16d-2dfd60edaaa6",
      "1e70d6a8-25e8-7050-1a1c-ecd6821ad4d8",
      "1f39b3bd-736e-11d1-8d3e-b2a7b4e64494",
      "1f648110-8e74-3dd2-a83a-4a5c96391189",
      "1f7911fd-2c6a-cb0d-71c3-fc9dc0693a3d",
      "1f939dd6-118c-65bb-43a4-7b9fbba84d36",
      "1fb4f53a-d7fd-a5fa-a807-ae35455e815e",
      "20011b8a-b299-4d6c-8208-e0d42beeb3d5",
      "200b98fa-0343-fcc4-5eba-8c81d2e7cb1a",
      "2010f702-2803-6ddb-49b4-2c517b3ab370",
      "2025f31f-39d0-43b6-a7b3-152d2285164f",
      "203c29c3-8043-1331-ce78-9891a7030967",
      "205da83c-d919-af08-cfec-07769e61f8a5",
      "20655ff5-3ae8-a011-2e28-f9405d8a1220",
      "20be6204-98ae-8571-9fe0-935a37ac0d9c",
      "20e9bf2f-8fce-e2c8-b678-3bf0893d9003",
      "2117176e-8aad-a2bb-a3c7-2459ccd33c86",
      "21ad2012-15d1-21f5-b558-c3807229324c",
      "21d5dab5-9c3b-8d2a-84ad-256d9b0a03a3",
      "21e83bdb-3a7d-3ac8-9e6a-1931e708118c",
      "21ec47d1-b180-e0f3-673b-647d6807a24b",
      "2211ab88-f62f-e169-f302-bea2c6355975",
      "2231c264-20e5-0421-62a8-f8e9f62ea308",
      "2245f9a6-c1e7-632a-e805-842f1d043f2b",
      "2267dc2e-4528-6c84-e77f-b1fdde4dad29",
      "2277a9d8-fbb9-8230-5298-2d3ec8960c86",
      "22ac99f3-9601-5fb7-d4a6-f2521be8c3a8",
      "2318056c-6e28-881e-7126-51cb9c6d2495",
      "233e2c80-440d-8d50-75a9-49fa2da015f2",
      "234f5a00-94a4-8ccc-7814-b9f85865a769",
      "23b1d4d8-798b-aced-dfee-f034086a46c6",
      "23e435a7-e1ad-669d-f741-b5faf0e3c47a",
      "2400f663-306d-9c34-3603-082688a612dd",
      "24028c13-098e-9d7d-2897-41888cdf195d",
      "24051436-cd3d-6e8b-f80a-5cc6eb6bc99d",
      "2406d19e-fdeb-2531-480b-04db277cdee4",
      "2408b12a-3be2-ed41-141e-f00bf650f0c8",
      "24420979-ecf1-5ecc-c550-e3430be1bdb7",
      "24596fd0-cb59-f8fb-764c-7a86429fbefa",
      "2486ea99-6168-bf04-8bbc-afeb6e5e1171",
      "248d5545-dff4-4a61-4840-f14b3c748475",
      "24a61d65-808c-9cc5-0347-dd0f2c6c6cd7",
      "24babe44-0d56-2504-dcc6-4d65f67e0cc1",
      "24c4b9f5-7a3b-5d1b-78c7-642291dda286",
      "24c8b883-ce39-5d1a-aeaa-95a50e4cc42f",
      "24d56785-d648-36a9-f60d-0ded110425be",
      "24ee8844-8c3c-5434-74d7-b57d3aeadc67",
      "24fddba1-235d-f407-f8fe-396dbd825383",
      "25421dda-36a6-5dc7-c426-bf907d060cec",
      "254a3475-b34c-5a30-4d58-68cc51efad60",
      "256e8bf4-1a23-fe77-66de-4309fa416c59",
      "259feb58-4a10-1216-3c17-14e62e308993",
      "25b25ff2-2389-8c5b-5aab-5b575640bf32",
      "25cfed8c-ec8d-6473-5b81-df7b709e89da",
      "260d9a6e-4e9e-c464-e723-c4ae8e20eaf4",
      "2616dd8c-c42c-6a14-5c29-ba1f9b6ba217",
      "2643e525-faf9-a7b2-961e-d9ae2cf8c670",
      "26ce627b-9b8d-0e56-5659-be317ae7c38a",
      "26d23683-7ab4-1fde-cd65-aedbd21fd783",
      "26df61dd-f5e1-2968-df0e-d1c3964c42e8",
      "26ea9147-8a3e-37b1-7ebf-7a7807578ca0",
      "26eaac62-563c-8447-1dde-e446dae7d965",
      "270ef12e-0f7f-57dc-e19d-fbcb33334708",
      "278249ab-bdf4-4a47-a0fa-c9e5435babea",
      "279a3bb5-25b8-3648-6c4a-1f0b5f0e6d1a",
      "27b7d19d-3d2c-5b8a-1093-79de2489f8ad",
      "27d09088-d434-9ba0-dfe6-99a16f57fa42",
      "27d2ae6c-bcfb-d9ef-698c-e4fd188b776d",
      "27d87b49-979e-9dc4-59bb-e846ab279bc4",
      "27e518f0-41f3-4a16-4164-e9bf5a28c36e",
      "280ee53c-d14f-2e67-2b26-734411ced7b8",
      "287c9ab8-7d2b-6385-1f1a-accbccedb6fa",
      "2889e327-2d87-fa7b-6beb-395a398342b3",
      "28add68c-ab0a-13e4-6b2e-6148412cb5c3",
      "28bfc9f6-f898-5914-8fd6-7458eec24ae2",
      "28e43da3-f3b7-324d-bff5-37796cfff13d",
      "291f7b72-705b-75d2-0a0a-8b4180529f40",
      "293a4953-c134-3300-f941-c26d943864f1",
      "2962cd4d-18b4-7352-f31f-57a3e538da55",
      "296c7867-baa5-9d54-ce57-7c4b0c97416b",
      "29a0500a-c5f6-5c72-434f-b76556fb7a1a",
      "29a8d075-62aa-15c7-957d-d3587a81a54c",
      "2a110364-66d8-3e33-fbe4-06d907c54f09",
      "2aaa57d9-3907-c14e-e49d-438fdfb977cc",
      "2abb8b27-1b51-4540-7944-3805c44969fa",
      "2adea4d4-1a8a-361b-f277-1b8270bcd680",
      "2af0c04a-24c4-5566-bce4-1ef851ad7c02",
      "2b0e2ad8-e5dc-0c76-f30c-91868822f5c7",
      "2b164511-52d5-1204-f0d3-00e398253f9e",
      "2b19c8ed-7268-d67f-bbc5-fc083217fdd4",
      "2b1e40c8-5ca9-9ad6-e47b-4a6b36a3e4cd",
      "2b53b7f9-96c6-e80b-7d81-0483decae077",
      "2b744d25-40e4-3ba5-e0b6-33b95bfdbb19",
      "2b9c2322-12ce-2ce7-5734-a10454f56a01",
      "2b9d16b0-f572-2c80-79e3-509fdf06ce21",
      "2badf7c5-debb-5c22-6f95-bf27e86cb28e",
      "2bd81863-2fcb-9085-046e-50132d053a40",
      "2bebf90b-1388-32fb-19a1-38ac2719bd94",
      "2c1cb34e-2d56-ae1f-905b-543dd5081224",
      "2c93fe06-84fb-f4a9-0d29-79d6ba10263e",
      "2c9676cb-7d12-527f-d69f-cdba9d909a7f",
      "2cda2753-aa21-2c97-c01e-392c9ba4597d",
      "2cfc22cb-30e2-c1e9-5c90-d4c16945eccf",
      "2d64e9fa-4870-1c0a-bf4c-8eff6585e981",
      "2d878291-278e-a262-ee5d-47deb57c0f48",
      "2d8c7290-12dd-431a-a803-50a28e216488",
      "2dd3c698-6b2b-14da-6221-2485b879d3d0",
      "2e0b725d-ea9f-4661-73d6-7cb40701321c",
      "2e64ef86-996c-8445-9928-110c7d31b7c9",
      "2ed0462b-8991-ea6b-da1f-5abf28a1f75b",
      "2eee92f8-1202-30b3-cd99-dcc01dff5d09",
      "2f8b74b2-0086-22c9-70dd-fb2e197b0eb7",
      "2faddd19-e14c-c0c9-fb1c-5b72ad3929ad",
      "2feaadcd-f066-244e-68a1-5189a7be58e9",
      "3014d945-4f35-ea3b-d307-a518df8bfb0a",
      "3052d686-b4dc-3483-eb86-ecaf168cbc7f",
      "307dc5d4-c66a-7067-7ef9-ec0f64cd8a73",
      "30a7044d-add6-ae8a-8532-5e952849f25e",
      "30df229e-c089-b540-d305-d8518b6836e3",
      "313690bb-8394-8f97-cf0f-05dd52a06cf8",
      "31461362-00a7-6c61-e7a5-bed9d1e52c79",
      "31533669-b089-5e91-5472-6f0fb4eb5164",
      "315d0ab0-2f3a-04cb-00e3-cb54f8f3ac36",
      "31b70e08-6e37-abdb-0062-340e6eb798e7",
      "31dc9127-cc23-e16c-b6c6-6c5570ecac7e",
      "3218ca8f-29ff-db16-8baf-c16b0a22ae5f",
      "3229fbc2-2071-bb37-f691-f819dca802af",
      "32318ef8-80e5-4e44-0de9-3cb145e4f9d5",
      "326f3e48-3afd-34ab-ba9b-67c13f6e4304",
      "32850e94-f98e-3a2f-a77d-21e4fca81ce7",
      "3326a896-144b-e150-0eaf-d7b4649c890d",
      "3333b721-a748-723d-5c57-db9a7ea9d91d",
      "3373fa8c-cd25-2766-0d88-ad72137275e6",
      "33bac64d-ddfa-e0bb-decf-bf592a67ed16",
      "33dad0c9-1307-413c-cb94-179dc4670246",
      "34079930-8fcc-359e-24f2-3a026229e749",
      "340f04df-8c38-5c09-b4c3-d9f7c6092404",
      "343f529c-dfbe-dbd4-b1b8-95f1d9a86eb7",
      "344b0c65-8da5-6c06-0284-ac938f7df7cd",
      "34fa88a2-f9e6-960e-1658-9eadbb57d168",
      "35081fa5-66ac-ea0e-24cf-cc6c6a788764",
      "352ed97a-288c-a49d-6fa3-d35cdd1e56ef",
      "353bb81d-87b3-af43-d1cc-8f3663401a5a",
      "35514ca7-980a-1683-6fdb-27d001c0c2d2",
      "3574b1b7-cab8-7636-88c6-18665782f837",
      "359cd6e3-210c-2a9e-e85f-f9c10991b076",
      "35e6a5a0-8c0f-900f-2a21-7e5d75294924",
      "35e758db-191b-115e-92ef-1b006f623f9a",
      "363c2e2f-5db1-212d-1d7d-5a687760f538",
      "364a69a4-a63c-2d18-fc7c-e468bc467ff5",
      "366dac3f-517e-9e95-f338-7aa239a4b828",
      "36c56f22-4298-490a-612e-6fd0ce5d6d36",
      "36d529c3-caba-2e02-719f-341d2f82c13b",
      "36f4e74f-a707-999c-b28a-89e4a567b848",
      "372631da-ed59-9b2b-0feb-20da3b93575e",
      "37e5053b-3332-ac3a-8261-b58ed834e88a",
      "37e6e5f7-6015-c1bf-1f1b-1e8440fab063",
      "37fc37ad-ffe7-6898-7d6a-051fc0ac0fc4",
      "382c07c9-8f34-f6de-8d07-ed7156479eb9",
      "382c98f0-bca0-c42d-8aee-dd4f2e47e361",
      "38be1be3-a879-fd2e-16de-405fc8ff2688",
      "3902c818-f2c4-51b7-0c11-253e93444f0a",
      "39061be3-21b3-5cda-e592-a82a073ad740",
      "390bd7e4-14d2-e6a9-7d72-e2b1405242ec",
      "3914599a-8bcb-17d5-0b53-3717e2394a25",
      "39215c65-fa73-b560-b0e3-ccc7a040f2ef",
      "39871320-b4f2-4cef-1997-f820d63a87c1",
      "398aed81-8691-87dd-a9cd-c62780f5f924",
      "39cd6343-7e04-0ac4-f9c7-f6b9807a4d83",
      "39d00e9e-8619-aa98-d691-2323ac234ec7",
      "39fc4642-47cf-6af3-5f76-90eb7f5a8996",
      "3a113757-0d17-a9c0-f008-0bf5577bc058",
      "3a2bd604-11bc-e782-d5b6-428776dd6532",
      "3a2f0b4b-e12b-8516-059b-207b8665adad",
      "3a35269e-a775-65c5-9e06-2f565f927afd",
      "3a51e788-5101-87a9-e879-d7d6edc76e18",
      "3a9bce68-3cf6-2529-c81b-75583ba0778d",
      "3b0e2682-0e17-0367-d2c8-ad471964d61b",
      "3b4e76c5-9c47-1435-e492-742fd7eca5f0",
      "3b61fbb9-0cee-8231-31d8-2f23403be93c",
      "3b6fede2-96fb-b17e-ac27-3f7b3bb91bfa",
      "3bbf9c77-542f-9a9f-771e-af2642175cda",
      "3bc58d94-5b43-8041-4ca2-04d3f796be33",
      "3bee2f37-a2d4-eb06-fb55-c6d458325ebd",
      "3bfba981-8fee-cc77-35da-e21159d7d982",
      "3c379d6c-ed89-0186-ac0e-40e6b7c86979",
      "3c38a48c-65ba-79ff-ab05-a4844fe70076",
      "3c547640-8ec9-4a8b-6233-3755c96b8adb",
      "3cba2927-7bd6-23d9-328e-d3a4c87ade1e",
      "3ccd6cee-4476-b4c8-e28a-13707ff33aeb",
      "3d3ec3e4-9ce2-739d-c9d4-a391984e7d45",
      "3d41e3e0-e7ab-e449-2fa4-9a5c52144ec2",
      "3dc6219b-1b8e-d643-1187-322310dd09f3",
      "3e152489-d2f5-5894-5907-7dd4aa2f7030",
      "3f0862e5-31d8-8bd9-ea3f-2436d733ceb9",
      "3f12d21d-5ff4-4fa1-4395-675022cdddc0",
      "3f66e0ac-bb20-f6f4-36d2-a485bd16d781",
      "3f9723da-820a-6354-4e81-5d930f0e979e",
      "3fa75949-b6c2-f686-0562-da2fd4e42cff",
      "3fc7ba0b-37ed-d9f3-fe23-8ae9c37acaf8",
      "3ff1ddaa-8ae5-4200-5007-7b861c907faf",
      "3ff20d13-2e10-89d6-da68-b64da8ea0d17",
      "400a4789-a5d8-0d12-5e25-2a00a43f256e",
      "402d0e6c-6af6-fd4c-a468-bae2b5cc0038",
      "4036baec-f77c-fffc-4fa9-161f095f51ba",
      "40407c66-3649-a016-1509-4a9cf349d04d",
      "404d1fd3-16c6-f091-96c2-73c8305ae7c5",
      "4070599e-5019-262f-dd8f-8f9c067801de",
      "407bcc93-4676-2806-19d4-7dd6df63df49",
      "4096cc77-05fa-5651-2dc5-f48a97090607",
      "40c0293b-9937-da09-9fa2-671eef57e267",
      "40f863c4-f1f2-e309-40b1-94d8f48d9a75",
      "41190329-7282-9735-ec28-c2bef9d9281f",
      "4149e980-b7b5-9c8b-d4f6-9fe030941f12",
      "4173a1ec-8816-9943-95a3-500f0697a380",
      "417d4984-7726-ea33-9c86-90c3605e3418",
      "4180eb13-1f46-c5ed-be10-6532e8ef0ad3",
      "41c4114f-bbe7-3923-e2da-bead1305bb56",
      "41ddbfc0-ad04-56fd-18dd-0e017c60cc79",
      "421725d6-46d4-3766-8459-3ab8a2bc17a6",
      "427ef644-6de3-d3bf-27cd-c2e17b237ee4",
      "42c55c44-0a5c-43a9-9cc3-12f57caa0be2",
      "430dcdfc-b551-71c9-3476-0d64bdb2d14e",
      "43136988-f212-dbe5-482c-5e06f65fe276",
      "4340bcaf-bb5d-c5fc-62d9-e7cd5565069d",
      "4373a707-431e-4ce4-5cd7-465c9a2f6055",
      "43ccc934-12e7-147f-dba8-33b34d5da1e8",
      "43defdd5-e426-359d-7a6d-5cb31d593b2a",
      "442143a3-9622-f76d-3190-78d5699f03ae",
      "444e6611-0ec8-b868-8daf-8d825ade1f00",
      "4459903b-e5cc-f2fc-2dfe-e17daafd8ec5",
      "44630786-69ca-5b33-b21e-18cab81152aa",
      "449ced58-937d-a8cc-afd5-5b8804d016af",
      "44b0d1e4-9421-9bcd-d759-8e4443e50602",
      "44ce09b4-1218-9db5-fabf-5bac9ddd29e7",
      "44e8eda7-63b5-e964-2143-36145d77d170",
      "44f41471-8e16-e628-1ede-7fd6532e4f00",
      "454b1b43-257d-4db1-6324-724985ff361b",
      "456d701d-ca5c-3f7c-98f7-663e19da003e",
      "45823701-b743-092e-85cd-c4bdbf601721",
      "45a33f9c-2031-0389-53e7-359e6fab768e",
      "45b6cbda-c088-e41d-bff2-2bf19f9a5275",
      "45c44c91-6e42-12d1-9c69-3375953176f3",
      "45d18079-26e3-4c48-d292-4075a8feedb5",
      "45fc69b6-e676-e952-293c-b603bacc3797",
      "46357ab0-c4f5-c189-b193-06a068bc4a97",
      "463fcd56-1cdf-e349-5466-7fda8a3c7875",
      "46466978-6b64-2411-fb5f-79ee88a8efd3",
      "467b04de-c12f-8910-5d78-840c704f2f52",
      "469abd48-059d-1a19-e028-a2c2ada93248",
      "46e9a027-0485-fb66-95b3-b8d26beb016d",
      "4713550b-1811-0188-a37a-b2c75c3e0f6e",
      "4781215b-17d3-9b1f-53e3-aa921efd8b12",
      "47ef8c07-9c6e-db44-c799-dcda2ae91c2e",
      "488a1de0-ad46-da22-8bdc-5db998a92549",
      "488a4974-ed1f-a01c-8c2d-1e3edb4efbdf",
      "489cdcb9-efb5-f3ca-0b2d-ee503e8b8c3b",
      "48c80e2d-f80f-7938-0b67-bb1f367c0333",
      "48d1bfe7-e7f2-4b14-7de2-36df4111c534",
      "48fbc11b-84ea-de07-741a-538865c92f7e",
      "496bfcaa-95da-268b-f489-66a3d9d73f5f",
      "498a7976-527c-fae1-ab3e-f136522b4597",
      "49ed957c-1109-9f53-6f87-ae6a491c78c8",
      "4a1851d8-fc7b-ece2-367f-b0cc60157f47",
      "4a21ef3d-bbc8-6a62-e77a-247f785c1fd1",
      "4a5512d4-628e-d16d-d31e-53d2f7cf3bfc",
      "4a75e252-2d65-8d7a-1b94-1d141cbc5087",
      "4a788db1-907e-8f77-836a-6d86b1f105f8",
      "4a857373-4a6c-848d-b1d0-85c11bd99db4",
      "4aed04fe-e158-2565-22ce-fef239336cbc",
      "4af6f741-9a64-6a93-e8ef-f17d7a969254",
      "4b326417-083c-118b-a51e-51537c8ecefa",
      "4b4a1177-756a-dadf-210f-48e695d0e526",
      "4b911f5b-ae11-c8af-88da-75d4559547f3",
      "4bb621f1-ca1e-513f-48b8-df1b331346c1",
      "4c2e5228-1cf8-5ddb-f9bf-eda6840ecfd7",
      "4c525d70-abc9-0fac-9ad9-2ad2be8f99f3",
      "4cb47db4-4f90-ed10-ec7c-8945d3b855de",
      "4cb96399-456d-c567-088d-863b7508577c",
      "4cea0377-5b28-781e-07c2-bff167001c27",
      "4cf69cf2-790f-1aa1-ca76-c9b53c7c78dc",
      "4d00bd52-058d-068e-2207-9f4b81916eed",
      "4d0eb756-7630-b7aa-98ea-d638c2c702c7",
      "4d197cd2-fed4-bd4f-bdbb-dfec108d5a10",
      "4de35c7e-1035-ecba-7a29-08c3a1d11bf5",
      "4e406f2d-d06c-27d6-a5a7-da653232fd32",
      "4e4aa0fc-b976-01bb-8b0d-ad14bf579e1f",
      "4e61a2fb-96f6-8455-5f6e-588c070e327a",
      "4eaab00a-7ebf-b8b7-3cc6-0802f98d2865",
      "4ee31cea-540d-a10f-621c-810afe820415",
      "4ef0b0ac-6e3d-cb46-5224-3bf06ca10292",
      "4f0a923a-448f-c1e9-d91f-53356f403644",
      "4f5f359e-b4c1-049c-8fda-21ed47742e94",
      "4f624e94-f115-1867-4afb-20396b6350fe",
      "4f63504a-bc7d-6b85-8dbb-3f11f1612f69",
      "4f68dc61-d0cb-9b7d-bdec-7724045ae65e",
      "4f81a805-398d-cb0d-2b4a-e5da4a2eca12",
      "4f9310c1-6997-f66e-681f-dbcd97180e91",
      "4fc58f5d-fcf1-55b8-d061-bfc4d9bb0d8a",
      "50164772-7759-8a68-7282-ddd43ccd05d0",
      "50a452fc-ba6f-51b8-1b9f-e567a2155908",
      "50a5aa43-7b95-8ccb-cdb6-f5b429b91b39",
      "50aaae5e-ef43-1f6a-bbf9-df3072f645b7",
      "50d40c8d-6da5-6f2b-f730-7d51a073cc34",
      "50eab2c0-a859-abfd-2dcb-cb8939a4a3c2",
      "50fab0f2-e62b-c198-667f-4fdf7c5d13a7",
      "51043591-a24f-67d3-5ecd-1decbd19bae2",
      "51063747-e39f-65f8-5810-9eb32848ffdd",
      "511ae5b9-1ed9-609c-034c-570bdfeb732b",
      "5125182b-5295-9070-aded-a378a1aae87b",
      "5136a3ed-0614-0e20-471a-92a892be626a",
      "515b5663-2e0d-ca58-ac21-7f09e168fe24",
      "51789f87-b7ae-7bc1-0b99-13a015114f0b",
      "517f96ad-7e82-6368-ec96-b9b30cbcaec7",
      "51948c78-38e9-4cd3-2236-ec0c2d24311f",
      "519ec9c0-d9c2-d39f-c9f9-c09f513954b5",
      "51b16b2f-884a-6404-4fbb-1bff6c445643",
      "51bb0e7b-5e4e-1de7-aaf0-5f81e3c8ee16",
      "51c61bab-dc31-060e-6844-488bc1e730dd",
      "51eaf727-a6c5-2b1c-f022-8f8ca2ddb128",
      "5216fa2b-a4c1-5c55-9861-d8fb71648a60",
      "52eaa852-1955-65a8-b69a-57f8cb149725",
      "53022e31-a0dd-646c-41c9-44cffa59a92b",
      "5391b1cd-62f0-ceeb-e3af-34b32c17a58f",
      "539369d4-a8c2-deac-54c3-5ec588b6f2ec",
      "53ab48b1-5f5c-a1a9-8155-3c4e89f10323",
      "53dcdb72-1ad9-dc62-ed9d-721f301f1935",
      "53eb4eb0-db1d-9422-6736-c60c089711a9",
      "5433dce8-8750-c648-87ab-80e77a39b186",
      "54e0919e-6773-27dd-3ec6-541ca9ce8cef",
      "54fde810-a27c-3593-8e75-b6c55569d11d",
      "552717dc-2b65-4078-0ff8-c94ded2266d9",
      "557d6155-af65-c4f8-6819-1257fb082f60",
      "55ce4aeb-02f6-5979-b443-9fdf6573f062",
      "55cff473-0845-32fd-88d3-76fe547cf2e3",
      "55edc41a-88cf-7ae9-cbc5-70c83f3d87fc",
      "561f1dfd-7160-9890-7eb7-75af8119de65",
      "5659aaca-894f-1601-c0ee-1f7d5b7790b0",
      "568894e9-bbf1-098b-77cf-969edcfdc624",
      "569bd082-3bd2-91f2-f681-9a644b77bc02",
      "56ae76bd-5a2f-8bb8-8e5e-036e8ac15054",
      "56e30932-a728-01ce-166d-4d55b22c287d",
      "5718a51e-5354-d285-3ec2-cc7ef5ce1e9c",
      "5718efab-8d36-f0d5-fd1e-b9eafe7f9eb9",
      "572ee832-b3b1-844f-322d-c228652edd8d",
      "574ed46b-d41f-8681-a47b-cb7a3bdccb29",
      "575a4bae-9f61-b2f9-141b-4f5e65385506",
      "5798f83f-c30d-a2f4-9f46-2e651a88da2b",
      "57d1d377-d041-c378-76ae-e1e418c64445",
      "57fe38c2-9660-2d37-f43b-e965519a82d4",
      "5807596e-f8c7-ff6f-3a21-72c0cc71a834",
      "583a7801-e84d-a372-3613-6e3932d531e3",
      "5841b333-cbcb-2031-376d-0a4743e5d56f",
      "58874a3b-da75-4e78-5ad2-c7187f42b431",
      "5897ac5e-9d2b-528e-9467-6a1dd1f50af4",
      "58b435b8-edd9-9b85-5131-0db8d5d9f362",
      "58bd3a17-00fb-c0f6-9bcb-04799083d997",
      "58efa830-c3a1-a543-206f-850608936b1d",
      "5908a2d4-569b-afba-7a65-1ad71d5aedb8",
      "5915e9e2-2d56-f99c-7b15-70d030523b07",
      "59262fee-b5d9-698c-0dc4-3cf0ea030c25",
      "597bdfe1-11e3-bf38-36b0-b3db5a805f8f",
      "5a249635-bb80-cfdf-c966-c14eb72273ff",
      "5a6d051d-e95f-0f1d-26c8-6010680d60e3",
      "5ad7d878-c6c5-abc5-264d-b1906b53837b",
      "5af18b30-2e08-5dd9-a0b0-9ff6645386ef",
      "5afc1445-eaac-d698-8d67-f569acf455ab",
      "5afee4a0-06a5-69cd-3e29-743e5c88ff35",
      "5b3bed37-b3b7-4093-5345-26cdd287fc58",
      "5b6c7709-2f58-0f39-4c81-baa9e2f9b58e",
      "5b86cb75-7ee6-52b6-f3c5-977e549b35e6",
      "5b8fca40-657e-0cf6-eca7-24018ae36df5",
      "5b97dad8-34e3-b58a-7da1-cd5d748bcf2b",
      "5bb00c9b-4ff0-433c-a4a8-41a36e0dfd25",
      "5be5c01c-9731-04f1-8bcf-46268ec86057",
      "5c33fd5a-a3e4-f3fb-f35c-97d9d1e5b600",
      "5c404c4f-d9d7-d6cc-ef13-f0618cc4d9c2",
      "5c829a04-e5ac-38c9-c8ba-4c8bb35f4eeb",
      "5c9341a4-949f-1933-495a-c43d4ba5c9a2",
      "5d538487-dd34-5015-e083-9e3feec90b60",
      "5d827860-4fac-6a1e-9c73-70bcc9b2d5d8",
      "5dad80f3-9410-56f9-9eb5-360400aa1a81",
      "5e371183-386e-ff1f-44b2-e14856c0e963",
      "5e3df100-9d59-1da7-0b6d-856f8edc0c30",
      "5e92274f-5ac9-4d59-85b7-faf5fed4d9d2",
      "5eaf26c7-ba7a-54c6-67af-de45face77ef",
      "5eb7f46e-c70a-d5e8-2673-ed417f569d74",
      "5ec818a4-3ae3-8feb-f680-332f53d2a5b1",
      "5f483b3e-7899-b5ff-c225-45498611e751",
      "5f5ea1f0-048a-1358-fcd4-06a1a256e9d0",
      "5f8cfd1f-43c4-354a-b893-2e6846db70bf",
      "5faefae5-5aae-ae0e-7e23-85282a01a1f0",
      "5fb9f07c-d5b2-0bfc-1027-3bbf1a51681b",
      "6057ce2e-a9d7-a5e1-2413-c4388cf15893",
      "6081f44e-def9-f660-5645-459740ac0501",
      "60da87c2-b4a4-9879-3896-79540701c4c6",
      "60dcb186-2a55-593c-ebf6-89f1fba738b6",
      "613e6e7f-598a-4a26-0e47-87939c008d18",
      "61a08ae5-a2fa-8fef-dc10-66d6f0ac262c",
      "61cc391c-137f-19cb-9c17-6e26d82313ce",
      "6239c6ce-32c9-613a-bac4-201d7bee9145",
      "6247a77c-edaf-9c51-7e5c-3bb4be6ac727",
      "624e258b-d3d4-50b6-a36b-ceaf404a56af",
      "62ad6694-f7e5-3bd3-0bf0-666cbaf0fe8c",
      "62d390bb-e91f-98d9-6e78-38ce05d3ea47",
      "62f12bcf-6aa1-1e2f-6e17-5d38431d20e1",
      "6349a170-bcd1-58c5-5b1c-dbc913d73c72",
      "6369e269-4e3b-f6b7-11b3-1f5e4617d98a",
      "63812b75-43f9-e6c5-f7dd-3a43f34c2173",
      "638f70a0-6b8b-e358-07c2-10661c8cebe1",
      "63d91430-9d6b-a92d-d6f3-86c97f9ef9a5",
      "63d96ba2-036e-8678-a572-0e466e6d1f6c",
      "63ddc4f9-9817-6440-2b93-da90cc2bd933",
      "63e4cd04-7e76-1ed7-c89b-3df73a6d6131",
      "63f367d0-1178-dd85-c327-26e5e7e0df0f",
      "640197df-6b9e-a54e-eb7c-0f05e64fd8b1",
      "645d1d12-869b-c7a7-b1c1-3402dbf194dd",
      "646546e2-16f7-ffea-297a-6bdec747adfb",
      "649615b1-05cd-37fd-dd38-a13785289b5f",
      "64aa666f-f101-947f-d529-4c3bfbf3cef7",
      "64c7557a-3207-724d-7865-2fa538f17648",
      "64c94c7e-fc5b-5d0a-8e03-9e04ea9fab37",
      "64df35de-ddf1-f851-01e3-488858776d3f",
      "64df4f0b-c7d8-5c8f-be27-d8984b569408",
      "654e4e1b-b033-ecf7-f97c-5e6c15ae9467",
      "65771a60-3bda-359a-9841-4771faf3a9af",
      "65ef8146-3f59-c61e-42e1-b08ebffc2a3d",
      "65f24447-f478-0cbd-31ab-1c8aae7d7d14",
      "66351d6f-e743-cab8-b6d8-65b400be1e7f",
      "66537de6-cb5b-50f8-b0b1-2efc47af0a32",
      "6658fc83-8cc1-cdcd-066e-b2cc6070608b",
      "666215f0-90f5-fdeb-9a14-fc8af73c53dc",
      "66666b6f-2a25-0878-fcef-ce9ff52ec9de",
      "666a3ad0-e1a5-1112-6c64-d861ec9a34c2",
      "6677c6d8-6c74-dcfa-9d88-2ec013642a1f",
      "66a4053d-8825-bf1d-4185-fd6d2de11b2a",
      "66b8e67f-d7f4-6798-ec24-925da1181981",
      "66d7155f-8f12-bd80-8c98-34dec2d5cc87",
      "66ee9c3d-7c32-96ea-680a-f1968c100541",
      "67377150-1c02-e229-74c1-dbc5a248a58d",
      "67e8a37f-9f0c-56ca-1c0c-689816fb5224",
      "67eddf97-714a-1341-b3de-3d85eb4166c3",
      "67ee60dc-3513-d62a-2fda-386eb835aad9",
      "67fd4085-dd87-668b-c3dc-fbde5b1b1204",
      "68369c1c-79b3-b49c-d144-905c68498cad",
      "6866a48c-b63b-b2ff-d8f5-3d038210898e",
      "68890aa5-589f-15a0-f0d6-ac8f02ea65c3",
      "68a57e5e-e7de-adb5-ef6c-33811eb16aec",
      "68de0cbb-5e09-32fd-6759-6a83cd78fb8a",
      "69215b2b-abd1-ce7a-4e07-c8c33d9fd608",
      "69255b5c-bf55-530b-fd12-141cc4a3103d",
      "692b2392-b635-8e21-db8c-4feb737e19d7",
      "692eee5c-7b6f-a5a5-2557-9f7ca4a08b06",
      "698440bd-c610-636d-33c7-7270f3adbf32",
      "6990b298-d26e-6e8e-802f-957f49ac37d9",
      "699a13c2-4844-6592-e1cd-1855889eb366",
      "699b31f1-4377-7420-1a47-8d9a16a72c5f",
      "69cfcd49-147c-5d6e-4c91-25f49e85a0ad",
      "69d05a3c-330b-371c-382f-c216c8d6bd6c",
      "6a3802fd-313a-1b78-9d0f-0144774be870",
      "6a56f166-6666-1a67-bd2e-b18509dd84b9",
      "6a5f540e-f310-14a4-0c4f-cbaf3b90cdda",
      "6a760905-0829-f683-b8e5-501e7034e0e3",
      "6a8b4411-c778-0621-be9c-42466eca6c8d",
      "6a9ea839-e7ab-6cd2-bd13-65a240f4159b",
      "6acbe7a2-e361-fffb-0cbf-b06cce2b1cfa",
      "6b4cc60d-6802-722f-a450-e6bd868ead29",
      "6b69710a-6a3d-d19b-f3e0-1076ab15c90d",
      "6bc4fcbe-72cc-3e2f-bb49-34cd51d4b073",
      "6be68028-1814-0386-6a21-15d4f1c7ae18",
      "6c143bd6-ea4d-de96-87bd-177b1ed3c357",
      "6c182843-0302-6578-141e-dde2a6fa7467",
      "6c52b4f3-58e4-f7b1-8342-1cf528cca56a",
      "6c73296d-dfb8-2705-f313-d2115e4518c4",
      "6ca463c6-ccc5-283e-efd4-c7cd4b979420",
      "6ce5dbd7-60cc-888f-7a5d-5586e3b70a58",
      "6d2c9f6d-d7e7-1ccd-3b94-54070d0ac83d",
      "6d837540-b421-3d0c-8b7c-b94be0cc3c16",
      "6de3cf50-62a2-a172-1f98-c51d0cdbe7a2",
      "6deb0ad3-6707-4b9b-043d-d899fa1d45e4",
      "6e2c4b0e-e2db-2cc9-403a-80d2ee4d650f",
      "6e45c2d4-1f9b-fad4-4a3e-d824ea323ab8",
      "6e6d908c-db04-d7b4-1d2d-6cef8dc02359",
      "6e8857fb-de2e-1cb8-784d-66d7ce501366",
      "6e914f28-6cfe-7662-8160-f5dbeda9cabd",
      "6e98a8c7-6961-e83e-faf8-e84975e7e7f5",
      "6ead67bb-60b4-4a5c-7065-40726383d558",
      "6ec042e3-e072-c721-6f7b-69846478d652",
      "6ec0761b-3c2d-3c0d-1bce-b00bd091b22a",
      "6ef17117-adc3-fb21-b6e8-ef8c2b1ba398",
      "6efe9d9a-6c24-955b-b312-23f624e79df1",
      "6f04d11b-8025-a915-acae-cbce8e10e031",
      "6f1129b3-8d03-92a8-3d0c-39d4cc57738c",
      "6f423c16-ea04-52a8-ea4d-024f1cef2f8e",
      "6f424851-5bfc-1b81-7ec1-147ab92f3371",
      "6f7ba4b6-08d4-67f4-337c-19c9e35b5d18",
      "6fae42b5-fea0-b290-12cc-c605bcb8aa6a",
      "7030ed9e-0765-b95b-8d6f-e1f121c160b0",
      "705dd29d-b9da-5d3e-3ef6-2e59d9b9174d",
      "70723dcb-8e2e-eb59-0b19-39fdca74bb55",
      "7073e39e-2832-8576-93fd-5ac5e161be3b",
      "70b0cb81-64ef-3e9d-91dd-c2f22298ef33",
      "70fcaa92-c531-fd76-183e-e6df81d1386a",
      "7102b748-1939-d3f9-80d2-cc1d3aeef256",
      "711d8e9a-8b6e-6451-d709-56882ca5dfb4",
      "713fc6f9-1608-10d6-0c66-bf4b438ae960",
      "714d181f-e303-c2df-dccb-a7e838953b44",
      "71a85982-f327-3997-3fc9-57654755269a",
      "720cb80f-e6b2-bde8-6d89-8ef98827bc15",
      "723e4738-0714-c395-1d61-496431f1e65c",
      "725fb04f-d29d-3ce0-0fbb-0b42f262afd3",
      "72857e13-f22b-d0d1-9663-0e23f5f58437",
      "7288d0b9-6ea7-52de-61fc-60aa382ac6c9",
      "72a16648-0dad-3de8-fd60-4e8f2f31c86a",
      "7350283a-ea3c-f4b8-ea7b-5bb866b39944",
      "736d5a30-f8f7-cd9f-1c0c-0e01f2bd2196",
      "736f5c91-76a6-2490-16b8-5866a1df83c2",
      "738076de-7b68-5ca8-976a-acfe5e607507",
      "7395ccf0-334a-b58c-49e3-cce9c8e13508",
      "73bd6ad3-0df1-0105-9e64-3c2aa017243d",
      "73f41565-cadf-2571-c690-71e8beed9b95",
      "74189cc5-c658-c1ae-7306-0b304704e987",
      "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
      "7487b1f9-a2ad-0cd1-c098-3f74b9ff3e11",
      "74fb28a2-6200-c1c9-5385-2f4e789e3ceb",
      "75426fb6-32b8-59e0-a821-309579d73e81",
      "75476ece-1e42-d1c7-3236-25de43f450e0",
      "7583e68e-16ea-7676-0ab6-1ae528cd4b8e",
      "759a2c11-2a07-f072-3563-d950d7aef548",
      "75a93fcd-3b0e-33fc-7051-e22eb3720a71",
      "75c664f1-9a86-c806-2881-5bf995f3e445",
      "75cd4cc9-3819-bd90-386f-e81b8d34616f",
      "75ec973b-ff93-59e1-97ed-137e607a4f47",
      "76085c53-eb05-f09e-da9c-4278b9bb8bce",
      "760b86fe-f352-d140-84bf-c9fb878efcc4",
      "760cac2c-1d29-5307-9186-9dd306ce80b6",
      "7656b2cf-2ec7-b9a7-1c97-e28bb20d19b6",
      "767fde72-a8ad-12d5-ad7f-a4e382fca72c",
      "76837a78-5830-d89e-daab-11ea2c45307b",
      "76c221e8-ec81-4306-2a22-c557668a3eb0",
      "76c711f2-cad2-d1ec-3746-76374839c56a",
      "76e2d0d2-bc5a-bf7b-e293-9a9365241e4b",
      "76ead242-85b8-7dc7-3c8a-579e6740c3c7",
      "772d1bf2-8ac4-fa44-d75e-55d5d331f96b",
      "773721cb-ee7e-262c-6eb9-412675530aa8",
      "77c92358-db0c-9605-214c-f942d3d30cf5",
      "77d90737-dc5d-ae7e-abc8-f07932de328b",
      "77dbeb77-0faa-f6d3-840c-e728427ee0ab",
      "780a7b57-8dfa-1099-391c-e17b5a9b30fa",
      "78301e8c-11e0-6e72-8f58-3fdd73e18355",
      "7873794a-d403-1587-82b4-d8f671f79783",
      "78839c44-c92b-218d-dc21-f2aafa65f2df",
      "7893375f-b92b-a885-a8cb-2ed9d1b16009",
      "78954246-0049-98de-b58b-f39d7bc0ff66",
      "78c184fd-890f-24e0-5a27-44cbff4bdcf0",
      "7928e0a7-9722-9bbd-23f7-8dcb765b1c48",
      "7971bef3-c983-43d7-0b0e-c6c01d60fa85",
      "7986e2e2-09bc-905f-c643-b6187ca48872",
      "79ca3488-a80f-3105-c9fb-e2258def4f29",
      "7a243f52-7691-9fd2-5a5b-186094a915e8",
      "7a332db4-22ad-e359-d97a-66f1e32203c8",
      "7a665a8f-f8e9-11b4-598f-a1f8d24b805a",
      "7a6b2314-06a9-9229-378a-79df1d4ab886",
      "7a6fd1a8-c66d-a521-9082-550684005894",
      "7a825413-7687-f042-b2a6-167b9bac9f18",
      "7a8b9b97-d99e-c295-a7e0-139e3dabfcd9",
      "7a8e575f-8139-625b-d636-21ead5587def",
      "7acf19cd-2077-c1b6-21cc-e0e1da16bebb",
      "7aef64f4-5285-3013-5618-1fecbded6387",
      "7af5a285-e7f5-e904-67d3-ca5d546268c9",
      "7afa314a-ea98-97c6-dea8-fe882c0d10a1",
      "7b47f1e4-e29c-d328-df89-e28678371ba1",
      "7b68635c-8992-7217-ea5f-e92e24ef88f9",
      "7b719226-57a7-c3e6-1ecf-57ad304a8800",
      "7bd6e569-ef42-a7b1-1a8b-cc90de9fa56c",
      "7be74e22-37a2-2820-d7bb-29869cdd7cbd",
      "7c85c38f-9810-bb07-78fb-37c0f99955d5",
      "7cabd69f-aec3-4d53-74fc-3aca03a1d77f",
      "7cb9bc91-258b-787a-33dc-7666c76b4124",
      "7cbe185f-7900-350b-9a6c-9d85b0549d48",
      "7cc84f18-9830-07da-10e8-a65fd5e476f3",
      "7d2f4811-0f08-9b2f-f6d7-7ebc206fc705",
      "7d3eac56-cfe0-5d42-d7e4-5c2bcc1eea69",
      "7d5313ad-761e-8261-ea7c-4a223afb5be0",
      "7d8003b1-1329-5202-1f0c-ebe3803c605b",
      "7d8ee91a-7ade-ce53-f3d7-cf37a2109765",
      "7e223adb-59f3-58c6-fd03-4a9072c4b9ad",
      "7e248a7b-ab29-e9c0-625d-1e86ffb85fdf",
      "7e43e54b-0dc9-42b4-a5f7-9ce8041db7b2",
      "7ec4cc6c-9c78-2926-a9c6-2efa9e96804d",
      "7eda29bc-6ee4-9095-71cb-6b97cc21fa15",
      "7ef3f121-6ea8-d573-0a2d-0e4c1564cecb",
      "7f1d3825-cf4e-b371-4c02-c5d681052311",
      "7f3b575b-4a69-9f43-f7dd-691e78885e74",
      "7f955968-d40e-6c52-9e2c-925c8855dcee",
      "7fa3eb3d-9d2b-aec0-baea-f8e5675a6fd9",
      "7fd0ee46-9e8d-2a38-fb6b-7e57f0b56c3d",
      "7fe25de9-adb3-e402-bd29-d37f53de154e",
      "7ff2dc0d-a3c3-d14e-a619-fee985a3deca",
      "8014a689-9081-69ff-c2d5-a3eaa1cb8057",
      "8015ed2e-681b-55d0-63b1-bbc61ca3d73f",
      "80307ca6-ca76-08ac-e691-babe0083900b",
      "80896733-2a73-e8b2-5c17-0c1c7f40a673",
      "80b10c44-eb2b-c962-4b3e-29dd5778e7ea",
      "80da5e5c-1b62-042c-a1d3-df291db27123",
      "80dd7244-dcbc-97bd-6b3a-95376c0502f1",
      "80ef8514-9285-12c3-a284-0dfad2b4202d",
      "80efe9ac-2ebd-b621-de8e-3e2375379b5f",
      "80f886bc-cd92-c478-21b0-ec4a1ecff277",
      "814e3fbf-5748-2cfd-b123-8a44b33a7289",
      "81b3c148-baba-3b14-172a-1e089dafe787",
      "81e688b1-aeb5-5470-a9cd-ad764744c1cb",
      "82064300-6b56-8fe9-2cb5-1e249c63dcf2",
      "82772c31-8d3f-81be-a3af-af35aa9d0348",
      "82a4c9c0-c24d-a33f-0e0e-f6584dbd4234",
      "82fbdb40-2ad8-7b1e-4831-8a5d4d4b7dab",
      "82fd7130-8575-e0c2-381a-1bb3b78606fc",
      "8361a653-d600-4a4c-25e3-dd2dffe5129c",
      "837082bf-e7b4-a92c-f37b-8b46a4417beb",
      "8372e4b8-3e9e-335d-75c5-e23d64d5b46b",
      "83793e69-7c84-41d2-e9bc-91c3837101b7",
      "8393d549-abb1-af1f-e6f6-03a52fb289d8",
      "83c959b3-9330-ed7d-5464-ee731a7fc186",
      "842ab264-4f2f-9fc5-aaa9-61ce02135dba",
      "84648bb2-2c07-2853-f713-11f8b7feab5b",
      "847ee407-cb12-2885-3f18-0eda55880466",
      "84871a19-371f-90f6-4d32-669eb5005c62",
      "84963219-4d88-6ff5-2ec3-2dc2e94f8300",
      "84ae4b1e-5819-8bae-939c-870047780592",
      "84afa6d3-653d-92d0-6276-f75ae2bd0d4d",
      "8510a02a-4be4-bc6b-d445-23721e6e2ae6",
      "851f5003-ddf2-fadd-4aa5-b6f23b062990",
      "8586bbf2-64d6-917a-9b81-a9c1f1fd9249",
      "85bd80e3-8390-b6c8-2ac7-85d6ff5a0427",
      "861b8ba9-d558-bb68-fc91-cd909cdda25a",
      "865fd498-62dd-7d93-b891-b397251d7eed",
      "869b768e-98ee-d585-7ae5-4cf04b77fb6e",
      "86ed2b0f-442b-4243-87f8-267c90e95c72",
      "86f8e3f0-039d-5f3a-0829-6469b982ce78",
      "87068ade-91a2-f4e3-bd8d-2613bdd50497",
      "8735a2f8-1122-48e0-bf53-5621226b80c9",
      "874497c3-8157-1225-d0c7-f2a64157d19c",
      "877db3cc-cba6-21cf-8c17-1c7db15d5076",
      "87ca02bd-cff5-6a0c-ab14-604fd0bedeb0",
      "87d025c9-aeb8-1e1a-ebf8-529d13319f4d",
      "88092a45-8ec5-c1eb-0122-a5f3f0e4d55e",
      "880a5ff7-5027-a0d4-a574-1eea0f2ad653",
      "88204333-f80b-d8a7-6e0f-429c38d1dbea",
      "88248b1d-6959-4f15-4337-df52fd7e8e3d",
      "88c2b012-c964-7673-98fc-90d11b2b00ca",
      "89034b87-8aa1-82b7-a630-3cc9e73fb524",
      "893bf6ce-a24a-b471-ae1c-bc2306c7a4f9",
      "894d5ca3-83ba-1940-218c-64336c055690",
      "895b4929-9a8e-afc8-fceb-b7fa9a14a966",
      "8983db62-6f34-8d98-20b8-01879036554a",
      "899248e7-9818-da95-6fa3-228fc29fb751",
      "89c67c42-78c0-de01-0b34-87270e2fe6be",
      "89e435f6-f5b5-6806-f2d4-f72878f59ba5",
      "89e894ed-a5da-b5fc-6d81-e4bc88c6a1e7",
      "89eaea41-9afd-ee71-fd57-83a8c6621029",
      "8a05c8b7-fbb9-b2da-3c84-13d86d9b1b93",
      "8a09e069-d2b2-2d5d-a800-1ab772a77244",
      "8a3e7534-d3fb-7b7f-09ee-e458c8b1b080",
      "8a457035-20ae-9cf3-d500-dd73ba2e4b11",
      "8a694ddb-f36a-22be-df6d-496e39f5c470",
      "8a6b1406-8a13-0232-3e8d-fb41db791c1d",
      "8a8e9755-54f1-6f92-3533-12b8a4f04f0c",
      "8abf6d28-3ac9-13db-eb92-a16a0e10788a",
      "8b1d7451-4ab4-d98e-2062-c261c37b3fe0",
      "8b3d2128-05df-a441-3071-87b6c38d1fd2",
      "8b7fe6e8-c6ad-d9c6-d3d3-a4816acf43fc",
      "8bd70622-52b2-e8c6-f62d-85eeda216bdf",
      "8be2facc-5e7f-285d-d027-26c34d8ae6ee",
      "8bed1e40-df5e-24b3-8bad-e2d6045bbeb7",
      "8befcd88-b898-97e6-123c-7ea9299eebe7",
      "8c208ad4-8b20-b9c2-bbea-acbcb28fd355",
      "8c5b06d8-fcbe-6df6-c777-5be3b3d50de4",
      "8c755af4-dd29-b6f2-05be-a97dc99faf6f",
      "8c8c302a-91dd-95c8-94c6-0590627cc210",
      "8c94cc54-6b7c-98fe-c6c1-d81766e43f39",
      "8cc49789-0421-ce7c-4823-1fa9b60bff9c",
      "8cd0b119-129c-b7d3-03f3-dd49123fb28c",
      "8cf866f2-8783-3704-e441-778ee590c009",
      "8d231493-e185-9bfc-a046-d57190da855b",
      "8d3bfdae-8840-147d-4125-daaf43f6d300",
      "8d7517d4-2c0a-a24b-da79-bb6fcd9fb20d",
      "8e21a9fc-2e94-c62b-a32f-e20449ed2b77",
      "8e45193f-4f42-f5ef-45ff-2011135eb106",
      "8ea0495d-c3c2-f6f9-2cab-ad5f4d6194c4",
      "8eabe7f4-9b9b-8593-c90f-e8bbf87a4bbe",
      "8eb055eb-1c6d-c932-2ffc-9ca3c8663273",
      "8eb66fb4-76e0-e878-de56-36217daf5348",
      "8ee8db36-db39-c067-f2eb-6844abc9ac33",
      "8f04d1e1-943d-5461-6694-814b2764acfe",
      "8f42df5e-22d5-3215-5af8-dde79b9d50ef",
      "8f6b7019-09d4-812a-ac52-53f2a155a0e1",
      "8f847d58-5a37-6fb2-5ca8-1dedbd81e720",
      "8f8e25aa-d737-db32-4999-a26b749149d5",
      "8fcfdb85-9109-4d02-190c-c72e325cfec1",
      "8ff9468c-cf91-4fe7-e72b-3282f5a7a983",
      "90256b14-6b7c-15e9-a504-4c5525201b67",
      "90289ea3-1298-e377-5adf-bed75acd6fed",
      "9065e0f9-51c9-740c-d304-a3bf84144c5f",
      "90e063c0-731e-ea4e-d806-070f1b50335e",
      "90f09267-3d3d-9bec-59dc-c31b84103ee5",
      "911e9b1b-e4b2-1408-f2f5-d9ea5048db2b",
      "912ba26e-1916-23d6-7a66-d96a40e3cf3b",
      "918f4556-79fa-40d2-c09a-1422572084a6",
      "91b00edf-e1b8-ad0d-32b7-a6c7f9fc52d9",
      "91e58181-c31b-6251-1ed7-627827071837",
      "92104663-e2dc-5d4e-e456-72d1bc9edde0",
      "923cfc33-7d54-bbb5-e106-048fd6b1b50e",
      "9271d5e0-f606-211c-a421-cada3354bb35",
      "92977682-ee74-fac9-02ff-cee360704c48",
      "92b23966-7faa-d517-5edd-4b82945a7131",
      "933f78e3-56d6-6519-3ce3-39aeda492676",
      "936366aa-9fd8-9d17-1db4-c50aa35574e1",
      "93849a9b-0f6c-9c37-f740-6806df995078",
      "93cb944d-0591-077a-eab6-beb41d545cca",
      "9405d208-f6a2-688e-96f9-e97b6056611f",
      "9439c691-e98a-52ae-4258-802c88fa2847",
      "94549705-4351-9051-87ca-29388f0104d4",
      "9489320d-cf9d-486f-e2f6-009a6608ee6d",
      "94ae9d4b-383d-9a6b-7344-88957b28f185",
      "94f36e5b-1ad4-cdb8-f518-fdf98f83c857",
      "951684ab-1e6d-8cc7-9b4a-340d1da473cd",
      "963d9811-68d5-33db-6bde-f81642da06be",
      "9646c079-6241-0af7-d227-0375300bfb92",
      "968007db-4db7-5367-596e-d131f53ef64e",
      "96c6466c-69a4-54ce-7bed-1c3ac3bc6cf4",
      "96d8e824-044d-39a1-9191-a6ed05b7341c",
      "96f9bcba-8e0e-e62c-430d-9fad33fac89e",
      "971ec3c6-6384-c5f6-11b2-3ed1c25084e8",
      "9737a467-886e-6dda-a7b2-86503b091f06",
      "97435ee7-dcbd-dba9-7f85-44c119a338e5",
      "97dd0ed7-8d33-0187-90ea-b0a9946cb52b",
      "97fe93ad-0865-00b2-89e9-bf1deb3f3921",
      "98a0c7cc-0bcd-75a0-f847-0f97b023e8bb",
      "98b9677b-593b-6949-8fce-15c4ba374e45",
      "98db95f1-beb7-5558-89d1-db18b5f9c013",
      "9915c01a-7d94-b680-cac8-be917e4a29cb",
      "996446b5-1ae4-6327-3218-52c29e49fca4",
      "9972e0cf-787e-e172-0fb2-8503b0b608bf",
      "99811af7-f815-3da1-553c-1d87404b9f56",
      "99b8bc1d-8b88-fee5-1bec-076dad675378",
      "99cad053-97a8-b695-5cf6-fa716809b1cc",
      "99e7eae6-7f97-6eeb-fab0-95e2f3323d88",
      "9a25482d-c348-8529-7962-0126bb8304ed",
      "9a2d7aac-e198-4366-dfec-e637eb96f85d",
      "9a56d593-ab1b-a4c3-3d01-42d18b223bbd",
      "9a66d29b-2eff-620d-28e7-54b74e87be41",
      "9a6f8718-b80d-489e-1c0b-c4a278fc15b1",
      "9a7fb2a1-0a7c-e0ae-b203-5d14343203da",
      "9a81a1d8-32c1-65a8-5bdd-ae6216a1df89",
      "9a98ab4d-cf58-f768-9678-256995e82bf3",
      "9ac254e1-bd83-b33a-33a0-94329ef1dfaf",
      "9af2a623-08d2-d735-58a9-e94765329424",
      "9af9b078-b523-24bc-a21d-487f85b8814e",
      "9b17441d-20fb-77a6-db45-f11ff47d08e7",
      "9b66cc4f-2a21-4172-efbe-18114302536d",
      "9b73800a-866a-708c-a522-97b9c3ebb178",
      "9b946079-d04c-3b9a-bab3-fc1456b3f644",
      "9b998d3a-bda0-29c6-f24d-c71217a46cdf",
      "9b9f4d81-38ae-1c17-8060-84ba6d225f62",
      "9bc9e4a9-2c47-a14b-02e8-79fa5e429598",
      "9c027c3e-6748-8055-fb9b-b013ce516431",
      "9c1b5316-7611-a44e-4044-776359ac3820",
      "9c2e5f01-38d8-fa07-565b-0ea3b2d2b87b",
      "9c7cb5b6-bb66-bb39-f8ac-efaeb0fb33fb",
      "9c9169bc-7ed6-ac60-c5ef-b6996f3f114c",
      "9cb5dc6d-2af6-1fc2-ec11-bb617e6ecc56",
      "9ced6a91-f120-759b-ab26-8b4229013c40",
      "9d0c2649-c9c1-2e65-9f7e-340c51556929",
      "9df539b5-f73a-016c-8f36-8e7f06364231",
      "9e1fd64f-901b-b542-c8a9-1d82950e8ffe",
      "9e3de44b-c69c-72ac-1da2-d7d4490c84eb",
      "9e4c2d1d-7b8a-556d-4e2b-5fa8d5d0b938",
      "9e5eee28-827d-6c1d-ea7e-baeff8118ea4",
      "9e61878b-dc75-146a-cf09-ef195041bf59",
      "9e6ec7cb-45da-b4f2-5dae-edb3f0785320",
      "9e84d879-bf54-eab8-2006-64abd280339a",
      "9ef83e00-aa14-6090-371d-cb3bb9aa548f",
      "9f4bb619-08e7-3bef-ab8e-76b87ffba09f",
      "9f657598-63c4-1500-45fe-4bb368fb136c",
      "9f8686f4-cd75-07ef-0025-85d41d5e409b",
      "9fd90cfe-7ec5-4125-6827-0fbbb4aac316",
      "9fdddd40-bedf-80e5-985c-00ed094e0406",
      "a02885b6-4038-8229-b4cd-d25ee1158162",
      "a02eec5b-6d27-b43e-1e16-8b12252cc834",
      "a033485c-5419-aaea-f2bf-1290dfe5d08b",
      "a046eb36-4494-d9aa-4ba4-5c1e3b75a1a4",
      "a04c8b33-7293-6bc0-3315-876d7a9f9e0c",
      "a055dc0d-9d80-59f5-94ed-154f301eb7ea",
      "a080e2dd-ec7e-5300-3d23-7d2c28c8d59a",
      "a0a6b886-5f09-8505-40ea-6c077da5ba41",
      "a0d1b2fa-c2df-669a-fbd8-d2c452733fdf",
      "a0e75229-c2a1-7652-895b-41dffc7ea9f3",
      "a12816e2-cbf4-9179-a38e-e0b42b266b7f",
      "a12c96ac-faba-0015-a0bc-56261fda44fc",
      "a142ef19-9b26-d819-3b22-c3f2cabc9243",
      "a1ae7be7-c439-f0fe-a51a-b7caec3cf1a7",
      "a1fdd2bb-77b5-780a-d89a-1feb17d9021b",
      "a202cfbe-6ef3-5274-f3fa-cb64be0c0781",
      "a208630c-8da8-6c72-0121-12e64786b8c7",
      "a20d4812-eed6-be0b-4c6f-8861175716de",
      "a21e4779-835c-bde8-dee2-f9c7e93a6cfb",
      "a25ca7c2-bc47-55af-668d-f337aba4a60f",
      "a26037ba-7ae5-81e8-787d-d7d599ab9a96",
      "a276dd1a-01b7-0a12-6531-801606eeec0a",
      "a281fc5a-d569-cb87-4288-a34f393c7302",
      "a311047d-ae2a-ebad-7b13-5608c0e8e4d7",
      "a37b2143-1fbc-5237-4038-5159afb5e5f2",
      "a3e11870-70cb-c58b-de69-e2035fbcf95a",
      "a3e51f8c-a874-0a0e-1781-3a6968f78236",
      "a40be035-9544-87a3-b992-eb67bab64b9c",
      "a44393f2-976b-19c5-22ed-0d3551c03ea9",
      "a44e4465-6f31-fdb9-f527-f182fad3e0dd",
      "a458bd48-920f-67aa-c71a-b67ae76f90c3",
      "a47d39f8-a01e-a21e-ed81-d39501a20e1c",
      "a49f2c8d-93c5-f2ae-1b73-4080260aa1eb",
      "a4c3164b-5606-e270-67fb-7d2be71aec5a",
      "a4ec2af6-aea4-8b4e-adc4-bb2c1e1f750a",
      "a511a70d-c5f1-4d5c-42f5-3a16b1073142",
      "a538b491-e632-d770-7f1a-eae4ecab9073",
      "a5396007-6b3e-7f56-909e-cef76c9e4251",
      "a55af77b-bc6d-cf8e-6670-2eff261d8210",
      "a56e4704-c39a-0775-23df-3e54de4598c0",
      "a58392fb-c186-e75e-d939-982918b23b57",
      "a5a74505-7628-820a-3315-112d2e594df5",
      "a5bdfa23-e791-78b2-8680-5de0db1bfed2",
      "a617330c-7d66-7909-c706-b45dca4399dd",
      "a623e94f-fd46-75e0-bd25-8cadbcda6d18",
      "a6452412-ff11-98f8-4067-63f6d4c20b20",
      "a700d3c0-45a1-a5ad-0717-bae9902dfc33",
      "a788771a-88cf-f2d7-2728-d314b0670dd5",
      "a7aefc60-dae1-3dbe-f6f5-704f4e7920f1",
      "a81579c6-9db0-aed8-7de8-3932a6b6b39f",
      "a81c1321-fa9c-f219-f4c5-5ae9c1f7b92a",
      "a85e1ebe-f6d9-1aaa-226f-2478694efed5",
      "a870bb61-226c-9cbf-1666-28f844d75f68",
      "a898c3b6-8258-f587-b697-964e858dd638",
      "a8b46732-0d2f-b368-939c-e0bb48284507",
      "a8ddda99-020e-2e1c-fb8d-6ffdf24e8029",
      "a9561eba-0296-a30e-4f77-994c87e95b0d",
      "a9a0c2b3-b5b4-d003-e4e1-74a7d7800934",
      "a9aa052c-2b0d-063c-2f39-5fdb9103ffd2",
      "a9d1abb8-f7b8-9ddc-4a34-75be568d9ff8",
      "a9eaf6ab-4e64-a03f-eb49-6b2a3e911063",
      "aa5e754a-463f-2d39-98d4-f498bf6f680c",
      "aa82eff6-65e2-a3d7-b4b0-9a65321c1e41",
      "aab86597-1a7b-abe9-0ad7-92fee8b56ed2",
      "aac86acf-44f1-277b-29ff-7747e89eaa47",
      "ab088440-aca8-7f8e-0abc-c6177d62ea91",
      "ab2b85c0-acfb-f209-2d93-96028ad8374d",
      "ab4aab07-f762-1503-3d04-4380ad5ac08f",
      "ab7bbfbe-6e89-ff16-4731-a4f8c32f0229",
      "ab7f5b36-df74-e590-3b05-f3c59fa4b2a0",
      "ab9b59f8-7288-19b5-8e01-e09e44579e13",
      "abc66a89-927d-2d67-5ee9-72b7a78a24bd",
      "abce83b0-690a-c2b0-2e8f-202cbf3fc8c9",
      "abe21c56-4aac-b6dc-e5fc-19f6e075e451",
      "abe780c8-c2eb-ee37-2e5a-d082a0725591",
      "ac0109f5-198e-eac1-9efe-10dc1f1c69e5",
      "ac21525f-6fee-9f89-ef87-fc44b017eac8",
      "aca9c611-534a-8139-633e-075a04831a1f",
      "acb27c9f-3056-4397-8ce3-dd940ed7702a",
      "accc02b1-7769-f87b-8c7b-fef81d993d6a",
      "acf00fea-f072-621b-92d3-5f2a6de14c26",
      "acff16b7-9ded-2798-a32a-cb8d66de13c0",
      "ad1a97df-4b52-00ab-6825-991405d63541",
      "ad3a2071-4862-006a-d9ff-5d513c3f6fc5",
      "ad4fd168-a878-663d-f282-024a21b47ce5",
      "ad82e640-718a-2be1-26a8-1aa26f70d20d",
      "adee83f1-f18e-dd69-c4fb-dc874f42afca",
      "ae14766a-0229-fe8b-dd1b-645fb6c55a8a",
      "ae359a66-856e-6c93-1fc4-8bd50341fefd",
      "ae44b708-e4b0-00a8-f7a4-91d07258b5cf",
      "ae4b1a05-4654-7ad0-d1c2-7c2670a0a887",
      "ae6ce38f-85c3-c668-8d30-8b43e2a88325",
      "ae8ffde4-cb14-0f92-1afa-bdc4ba52289d",
      "aeb3f42b-9f39-9e7c-e078-fa2eb9288a05",
      "aeb96a8c-6f0e-3924-7f7a-0ecb784c8bb7",
      "aed05ebe-4b3e-8fe9-21c1-ed57706cf161",
      "aee5ce05-9b3f-9571-4685-f1937c9ef6cf",
      "aeee2fde-9061-8179-335d-bcc49f3dbc33",
      "af14d3fd-e462-493c-c64a-e8f78600485f",
      "af33bf0f-decd-c1bc-8a0d-8de588fe380b",
      "af42380c-305c-a308-7083-ff9ab377d273",
      "afd02175-836a-8b89-d63b-af57c1a95dce",
      "afe5b63d-7f37-1b47-9697-88aa86c63c00",
      "afe94cab-75ee-f1b8-c3ab-d83b6200998a",
      "b025c52d-76fd-9407-e2fd-76b141bc65d4",
      "b02becde-ba64-3f5a-2629-793df746a331",
      "b0426639-2d51-1406-e6c7-7e7bcda05c59",
      "b099ba85-da4b-88fa-aa8b-2242e273217a",
      "b0cdd7a9-8863-56ce-3dc3-11a57ca2ae2d",
      "b1058be0-9250-db9e-32d1-ff6f51d45f90",
      "b117040f-9755-a6ad-c142-e4533d284dad",
      "b1846c87-aae6-3933-3ce5-e929f767adc0",
      "b18d69ff-1c54-5de1-7c7b-3151ed727efa",
      "b1ad2fb8-105d-0cdb-a762-d436fd80acf7",
      "b1ed7faa-88fd-2955-7609-7c0257d61bd0",
      "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
      "b2069634-4630-9e92-35ed-91606c36ce03",
      "b2662a8a-5c35-9d45-7dbb-5cbe87a7d9ce",
      "b2752d82-e5e3-610d-1b1e-587f1de14f90",
      "b2969b94-ae96-3cad-3dd8-778c75dd320c",
      "b2984909-581f-aba7-c49a-efe6be7274aa",
      "b2da051f-5df4-f52c-533e-ad86cc4e4cf5",
      "b2e95089-e2fe-c5c1-419a-618fc554622d",
      "b33bf27b-d201-a264-a8b3-4e2c606cd566",
      "b35660df-8c01-1f50-e3c9-d4c91b875282",
      "b39720c5-5394-12cc-b913-a602e52c7a0a",
      "b3a6b900-bbee-a20a-c8d5-72c5c3bb235d",
      "b476a91a-c3db-8a87-b210-88dc1751064f",
      "b4889ba5-4619-38c3-fd08-2378776b593a",
      "b4e46616-068c-bf5c-8169-c99dce4a5a09",
      "b4f217ea-8acf-3abc-6052-6aee105297f8",
      "b5379f70-83ad-ed38-cb4d-d3256db30b6b",
      "b58c6876-72eb-bd76-d75a-6a5325aed482",
      "b59258e2-c419-b7b0-8793-864203703dff",
      "b5ca91cd-918c-5f8e-c84c-13285ac990ae",
      "b5f640b7-27ee-0862-a4d0-1f5805adeabf",
      "b608f2e8-f152-f742-a3c4-dbf2c1778281",
      "b60a0169-20b9-9db5-1884-51be54d4ded0",
      "b6396849-c1e2-6232-61c7-fb12ff5f1b0b",
      "b64017f1-4528-ce02-89cc-3193ae008620",
      "b651b121-b34c-f077-bdf3-ff2c28170d99",
      "b69d58d4-b46f-fcf4-91d8-bf2e89e2e910",
      "b6a07e11-f65f-b9ae-37df-b72060eb9407",
      "b72e7b30-e0c5-8c4d-1b1b-7f267a91f3d2",
      "b748cde9-c496-1ebb-3c33-edc8f41057de",
      "b78a12f3-397a-f9fc-b119-41d3e33e09dc",
      "b7a75d35-a2c7-6c50-77e5-34a139cc0a7c",
      "b7b01544-e902-cecd-cb0a-bf8664f4ddb8",
      "b7f26c10-c43c-4288-c46d-b1c13688f643",
      "b8307bf2-0e31-dc11-2b09-40e882aa73c9",
      "b839296a-2279-7b22-fa88-8f53d3d21624",
      "b840330c-1a05-d734-7a1d-2d4adc2464c1",
      "b8981943-1c52-1ca9-d2f2-c64cd4d1a14d",
      "b8d1b571-663e-5e95-0a9c-a7ff1a1d5e31",
      "b91c0ae3-80b1-afbe-1e30-7702661e47c2",
      "b9486373-2a29-240e-8610-5f6572fb320b",
      "b9719ed6-5ac7-fa44-f083-de96b9531db0",
      "b98b3b36-2c6e-6d9c-8eeb-a567e56f25ed",
      "b9a2eb4d-cebb-f7c3-7e19-ae0963de42d5",
      "ba147715-6745-f852-3605-6bbde2164cb4",
      "ba26ee93-4329-808a-a998-4cc78122a16c",
      "ba5ca97b-7ef6-8323-2427-7b80713d2c32",
      "ba940fb9-1e4f-4efb-0b9a-d5d378524b13",
      "ba9fb665-f192-5e3c-2654-d91472d538ee",
      "bad4e68b-9422-901d-fa68-24049aa9f961",
      "bad52820-ac89-e884-1362-d29c700a27cd",
      "baeaaf58-d448-7acf-7fc0-f7132fbcfa77",
      "bb0fbfa9-5820-1fae-a9df-61341b7e6e19",
      "bb27989e-3df7-2494-1fff-2d2c31222c19",
      "bb416257-70f3-d03a-4df9-51f86d37131c",
      "bb6733c8-a825-beb9-0964-7d080b1e700c",
      "bbdbb4de-2f86-82e7-d6da-97d02d23a4e4",
      "bc205159-8e3e-c60c-93d1-79c042702ef1",
      "bc2e211b-1553-54d2-cb10-889907353619",
      "bc4ab773-29ac-dd52-579c-16ffd15db72b",
      "bc86898c-227b-5e35-d4a9-a688b7a392f3",
      "bcaf90a1-48be-bbc7-4a41-38e5e4efdc69",
      "bcc2bf58-98b5-d955-e087-19cf55c3f4df",
      "bce68864-aa24-a6bc-1be9-b7c2b11fe24e",
      "bd1cfe8d-482f-b1cf-8323-30750d88691d",
      "bd238e74-eb5f-8311-1e3b-10aca683f093",
      "bd427e5e-3945-8701-e5eb-e1d789f05e93",
      "bd46be7b-4899-daf1-758b-84c67648a913",
      "bd4c854d-a728-5ac2-9670-2c2fac74dd45",
      "bd82e0ef-7d9a-476f-b7ce-6c7fb2c6c0e5",
      "bd86a304-6c0c-1538-8fa8-12081d33eb83",
      "bdd6ebd2-0c1e-924e-2764-2f68c5213ea2",
      "be053156-a112-af95-44f1-9e61cfc1827e",
      "beca1b27-fa5c-8cea-8319-f80d1f49b9c1",
      "bedbe20e-7aea-bb3a-5f3b-5a6a6f22a562",
      "beeb158b-20d1-ae97-8282-9f545bc3c169",
      "bf5b87f3-2f15-b511-454b-33645d3dd95c",
      "bf847586-1809-9f99-2e7c-dde3a35fb94c",
      "bf8a4668-7c0d-853e-4061-bc94b2a7d2fa",
      "bfc00787-64db-e692-f439-1d1578022362",
      "bfca1a6a-8189-f0ae-a4eb-0609fdfcd596",
      "bfe70ebc-a6bb-5a60-31b9-1fa2a3fb8760",
      "bfeb8ca7-7fa2-45b9-c5f0-b343c9f4a962",
      "bff90f30-0903-a85a-fa0b-cafb80b80566",
      "c00ac986-f1bb-20d1-f535-6f0df9e586de",
      "c0388e6b-a779-390d-4eca-cfe95b03e6c5",
      "c0ca8036-9243-87c1-6153-47852f96d910",
      "c1061266-abd4-1ecf-0012-4a4f0d6fce42",
      "c1134bc2-edec-27af-b6e8-e987ec3c3804",
      "c1268eb3-fbd0-b57c-5fb9-857a044c564a",
      "c12d1c6d-d670-0b00-d04a-80ea08b7850f",
      "c136b7ef-7428-ff9a-71d3-c3c84b15754d",
      "c13c5a30-f4a4-5958-65f5-f7e0f05f1de5",
      "c1410478-9c9d-7b0c-9149-da879cf8c7b3",
      "c1417c88-f282-b3f9-1102-d85485db4ba2",
      "c15f67e1-1752-0479-b725-996c976e1aee",
      "c1b1040c-beb8-84b2-eb77-9ddfe727f772",
      "c1c3dbb1-c244-61d3-0de7-74d731377f06",
      "c1e56288-5dfe-1233-8b7b-6b54f0128a3f",
      "c1e8fe69-38df-2900-499c-e7db12866b10",
      "c2205b34-3cd9-7c38-b2df-3c0e88feefd0",
      "c23d457b-80f4-ddbc-da0e-2baf0fe48c7b",
      "c24594e0-cfcb-9f36-1dfa-036be7bfa2d2",
      "c2546203-7f19-41c1-5142-784bf3b0b003",
      "c28ae0d1-0869-c051-102e-81b6f59bc853",
      "c28c94b3-b36c-ba25-88f1-79e1dc5c7107",
      "c29cbc77-a3d4-87d1-9480-3349dc035bf0",
      "c2a068ba-9744-7b47-ab9c-69f33df229d9",
      "c2b672d8-0460-028e-3a0b-aff093ca57b7",
      "c2e26faf-be0a-7209-39fe-53f40394cdb6",
      "c30b49f6-ae8c-9fff-184c-c3f70e5cf99f",
      "c32336a5-f3c3-442e-6baa-eb1b7dd6bce1",
      "c35068d3-257e-584b-f218-84cd364ab584",
      "c3d82dd4-bd83-85b5-485b-3a9b45989bfa",
      "c4785784-fb5b-8de4-3a9e-e53345b26d4e",
      "c4a1ea89-bfb1-1347-7808-5cdfc8b5b3c5",
      "c4f4e397-5cf5-b163-ca36-5dddcd9b4049",
      "c53c8d9b-6cf1-843e-0fa5-f82276c015bc",
      "c5402bcc-6cd0-4d51-121e-43538c795310",
      "c56e6f92-a76d-a206-0550-3e746d573bf7",
      "c578c4d1-8c94-6d45-4c82-97dde6ed6292",
      "c5a6d31c-7f24-efef-9d9f-3a9b18fe42ef",
      "c5ad594b-8a5b-6d83-5d63-6c79ee11ac11",
      "c5dcb5fe-69a8-bb82-ddd1-d4c0a7336ae6",
      "c5e50355-2692-d36a-3f2e-c6ee4fbb77b1",
      "c5ebd4f9-162f-d619-f486-2ecbaede4b90",
      "c60c6a03-d1e1-1641-c24c-f156eac5249c",
      "c62d1954-a59a-c2a0-8e51-94bede232e14",
      "c64fa76b-d17f-34c8-587e-66ad0ff69b6a",
      "c66f754f-853b-1642-197b-7188dad6a98d",
      "c683f015-2de3-f0ba-2388-3aa9ce813dea",
      "c698df09-f0d2-cd18-a81c-32e3175df2ca",
      "c6a0ac05-cc40-2911-399e-464300553e2e",
      "c6bfe26a-d9dc-545e-e8b2-f26eb0797bae",
      "c6e7f77c-01b1-aa60-3ae0-54fee81ca16c",
      "c703f675-50dc-6a8d-b774-e4b778d39cec",
      "c7316f15-6ccc-8b79-d74d-12c2dfb0b9f4",
      "c7a73b9d-3a8e-6ac4-d397-b8c9d9c160cb",
      "c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72",
      "c7bd9941-0447-8865-9314-43affd9c739f",
      "c7fe583b-9b28-df59-fd36-0dff1f3ea80d",
      "c80bb198-745c-dc68-69a6-c6daf5de15c4",
      "c8628d3f-06f5-d7ec-e8fc-c30f90a22105",
      "c86d684e-cd09-41f8-0d65-ff19b0af8833",
      "c8a93f9b-d0a9-7eae-272f-f3a0a962832a",
      "c8af7f83-9dee-eac6-a5c3-ce5b46334f3c",
      "c8f5cee5-7002-62ca-e0e4-cf3c9b97f595",
      "c8fec91f-2228-757a-765a-d7836170e59e",
      "c963ad1e-7e19-ee71-014c-e924fedcdc0f",
      "c9816994-8ee7-8137-33a7-1a189c1cf855",
      "c991722b-498b-d997-2e9e-42131edeb9c5",
      "c9ae45e0-35af-0de5-e444-5442860f7752",
      "c9b0e7ac-71f0-c5d4-4f67-7d07b8e4d548",
      "c9b4acef-4348-1bd4-9f96-d3adfec68fca",
      "c9e3ca06-e337-098f-0a82-ce98e4645566",
      "c9e8ba67-b836-e455-ee0e-73f2fdf1612e",
      "ca130fc0-427d-56db-262a-42752d6e24d8",
      "ca1c8505-6954-15fe-a352-9fcd9c8d016a",
      "ca666596-b5dd-b365-1378-269010325c4b",
      "ca6ebebd-8d0b-be86-1cec-082f2ba6fccc",
      "ca824df4-888f-fc27-bee1-02574015e285",
      "caa7c472-a8e6-1e33-2e78-3ff3728347f1",
      "caeaccee-53dd-ae82-3148-ba0d4bb6ca16",
      "caeeff58-b580-0b28-20bb-6e1ff2b85fdc",
      "cb0a2701-1c97-253f-c246-93ccc53b3da6",
      "cb67b78b-3210-dd63-8f94-883c95957d0a",
      "cb8d8648-aa1f-9206-a66f-73f83be03612",
      "cba2b69f-7088-3c06-f799-a71ac4c76acd",
      "cbbf3575-a94b-6374-eaa8-8728fd983f2e",
      "cbebdf54-d98f-1634-f259-2cf16ab1f79b",
      "cbee2d96-8126-e745-27cf-bbb2528132c4",
      "cc29b660-0a24-79a5-53e6-7ce49d55af0e",
      "cc2c12b6-ee50-e9f8-665c-25862a617b71",
      "cc314f58-8fd3-2a19-a631-b26ad95ce873",
      "cc76eb84-7fe5-ec22-c0b7-5014805d96dd",
      "cc9619ce-46cb-e6c1-1545-35c32d437d3d",
      "ccf88082-4067-348c-0a4a-c01cf6745d9a",
      "cde88516-3ae4-65e0-006c-5b525a397641",
      "ce2be85d-6036-ee64-fca7-46ee4c910ec6",
      "ce60ed81-80c0-c3e1-c655-b65c7b77fc58",
      "ce6cb83f-190b-4474-48e5-d29037003498",
      "ce995f4c-484d-a224-3003-9641fa1549d2",
      "cea59186-244a-adde-4a92-797fd46e8cea",
      "cf17108e-f657-1dcf-4459-357a89b39b47",
      "d00e56b0-3f75-651e-513d-089d4f3a38c6",
      "d0100932-678c-be95-8325-918a3c3e3bf5",
      "d04f3510-324e-8256-fff1-60400f21bf61",
      "d08c0e65-3527-64ac-68cc-dbe6fc00a486",
      "d09a3aed-b967-5d1b-293d-099ecf774005",
      "d0a5e2ac-cf5a-2cfe-50bd-7eda4a99531e",
      "d0aead39-6e7b-5789-3ca3-6f75ee198940",
      "d0ee6a57-cf7c-19cb-ed95-5d95451081a7",
      "d14037a9-ca34-15ea-624f-929dfb876160",
      "d159f1a4-8121-957b-c6e2-16b0ca107434",
      "d163fd10-5447-9738-3f4b-904cc48c0d6a",
      "d1662770-0ecf-a126-5f04-6dbacff93514",
      "d17d5f3a-46c8-2c79-6d9b-9d547705c15a",
      "d19d1fcf-a289-a87d-30b7-7152b34af4da",
      "d1b0cfa2-c180-94b2-7eea-5a7679a01227",
      "d1b2a0b5-ba0f-6af9-6831-b3e5c4b2bfc4",
      "d1baa080-bc7f-f0f4-8b16-96d1f9e021ab",
      "d1c3f543-c51e-0a70-bd82-1faae5e24295",
      "d2570c26-41b8-dc60-a14d-6941b37e3b7e",
      "d28c252d-f771-0056-9779-443a9a3a9c90",
      "d2b042d4-c14f-c8f5-133a-5be9cbd39ab8",
      "d2c74a77-0997-c5f6-22c3-2f19818493ea",
      "d2cade33-70ea-f88a-d2e3-5971d3b7019b",
      "d2d06ee1-0ec2-0c3d-f737-a137a3126091",
      "d30d5903-0ff5-96e0-46b9-fccb93b9d3e9",
      "d30f1d16-2efc-78cb-c858-14bb121548d4",
      "d35a0c27-1454-5df6-9769-bf9c85c00488",
      "d3beb7d3-29fe-1689-ae12-6caf16c8c949",
      "d3db43c6-950c-a6d5-8771-bf447ddabd7b",
      "d3ef1b78-a11b-b822-9221-4836c358ac2e",
      "d46b4eab-5a5e-c3b8-4795-aa0828c3dce9",
      "d4817dcb-2cc9-a186-3c51-a45da00c5980",
      "d4989791-e3d8-33f3-03f5-01bdc2fb73a7",
      "d4c65ac7-c265-5bab-e3c7-f603c48f92a0",
      "d500c339-4dd8-6e90-24ee-1e3ee401d2fb",
      "d5346fb8-6aa5-493a-ed88-1415e0c2005b",
      "d53c3129-3271-dc9f-0987-9d069b42304f",
      "d546f04d-2345-b0b0-5c1e-e2bf5bcf270d",
      "d549dcee-15b7-7ec4-5358-e81e914ad2d8",
      "d5953d95-d9e2-a837-ed07-60643992cb16",
      "d5970f32-ce1b-50be-c762-a77134c5456e",
      "d5a75c20-8265-9dc8-9213-10cdc5af8688",
      "d5b73704-705a-d089-b50c-b716eba59c0c",
      "d5bf0427-c3ea-f9c8-933d-deb15ff23d3d",
      "d5debe7b-da69-a4a0-c60a-d7f9fcf71b06",
      "d5fab709-9ddf-7581-227a-d50b9587a6a5",
      "d6082ab9-80dd-1157-6553-2d03aff7fbe9",
      "d6219e1d-87a5-5a7c-e114-9321849add8c",
      "d66d5d60-9f1b-7425-03c8-bc1b1a9a0910",
      "d672aef8-6565-e8f3-6df3-7cb4ecb019d2",
      "d67fa128-22c1-5b86-56b8-046a47578664",
      "d6afddb4-c774-fd8c-cee9-8edd6e831c53",
      "d6c524f3-c801-302d-be1e-cc16fda99d0b",
      "d6f88c23-2f87-964f-b33b-f31b8e0aceb2",
      "d6f8df19-66d2-4b57-2d5f-b6726de7b667",
      "d6fa1359-5b6a-b3eb-a67b-b1cf714291d5",
      "d702fa9d-b05e-f70c-b26c-1eae48912fd5",
      "d71bd310-4404-257d-f344-e161f5d80db6",
      "d7427258-34d9-44e9-73c5-630308a8b138",
      "d79c6998-3271-e88a-0451-56601bb12bff",
      "d7b3cf40-4f50-fd7a-17c5-df5eae6a61b5",
      "d7bc8d89-361c-d61d-29f9-19c3d9414155",
      "d7e65a54-5dec-994e-09b5-602004f3347d",
      "d7f08099-cd22-9ed0-a95a-6b469abcc680",
      "d82c26a1-f316-f72f-d1d7-441caf19728a",
      "d83d5ac0-1923-c45b-563c-dd961754ad5e",
      "d86f8c85-1f42-3bd7-9e85-78036e4b7177",
      "d8716125-dfe6-69c4-eb64-c8a8da2c2128",
      "d884e1c7-cfde-e2da-b504-c88cf3621cec",
      "d8e3917b-930e-f719-e1c6-450ea3cb6e80",
      "d9071f6d-87a8-c2ff-2a8c-609fbe331cb8",
      "d90da23d-8cd7-ae08-1d22-269cae0da83f",
      "d98fcbec-80bd-cc85-4784-5326aaf21ffa",
      "d9defa5b-19db-bd1d-5700-3d70a731c4d7",
      "d9e07297-dabb-1965-130a-5950b96d6d0a",
      "da1cb414-98b2-5b08-8475-30b5db256ae1",
      "da4ddb5a-1190-b86e-b81a-5f91399dee9b",
      "da710d26-932b-e95f-627a-19565d2d531e",
      "dab01bcb-763f-c6be-3c34-a6bf7396d663",
      "dacd6390-2842-651f-ee61-d11512f91e86",
      "db0aaf8f-4bd9-cca7-40bc-c07708ac2fc7",
      "db269c06-33f7-a19a-f25d-2664f1276d2e",
      "db816d77-a2c5-6f2d-799b-593a81f6cc92",
      "dc0fc0d9-634a-ccfd-ad46-7d3c83d2108a",
      "dc3c9962-c515-2a7d-281b-052a8952da9b",
      "dc60c383-14a4-5a2e-32fb-cddb52453bb0",
      "dc789115-cf84-fe7e-73a3-2e16153cc4e2",
      "dca10ce6-e8cb-4a06-7d49-d197529de614",
      "dcaab5e5-ebb8-fb51-6398-d3734aa9d300",
      "dd42ef66-6029-2d67-dd1f-c28bd4ad4c6d",
      "dd97e287-1c3c-1171-803b-5498eb00b620",
      "dd9c8652-ec81-3c52-d7db-2788a2935b2d",
      "ddb5f52b-5610-a708-5271-23915cdab993",
      "ddd138af-472f-296b-b747-74eebe9a2859",
      "dde61f40-31cf-8fe9-7585-9bf05a408fd6",
      "de8a4ac3-c8d9-5b58-f98f-ad8d12406a6f",
      "de8c793b-100e-3eed-e813-a1cbdccd67ee",
      "dee93a05-3313-61aa-8165-dd596c9cc623",
      "df16766f-51ae-c4ce-0bd6-7a45b36b4529",
      "df4c7c95-80e4-6726-2249-c42e36d98d79",
      "dfc05e75-bedd-cabe-2e13-60e0279cc92e",
      "e036c956-8869-3c98-bbab-86c5caf8ed12",
      "e080bf87-a185-708c-b158-83fac382b89c",
      "e097eb80-4e39-3ef8-0a82-1b12fbb54115",
      "e0b6391f-1bef-9887-a303-a50236c327df",
      "e0dac2f7-3d8a-c806-336f-0651010826c2",
      "e0ea3f2e-6d32-7593-6b2b-794998ca63e1",
      "e1014146-479a-7fc5-0a59-fa8b417a8b91",
      "e10ad457-d9a0-15db-921a-2e8eaa99cdbe",
      "e14eb3a1-9ff2-2320-9065-b14e2f7adfc3",
      "e17d1b88-833b-4b22-3124-1ef48d03a184",
      "e17eaf15-ebd7-8ba3-8ff9-b23ea5afc8c4",
      "e1b4ec19-ae93-cf18-9c95-eb309759d0bb",
      "e21e83a1-ffe7-4b09-6c74-4ca9a298543f",
      "e2276d16-c1c3-6f2f-1a88-c092e12b672e",
      "e239661c-1d58-afc3-271b-bf149b275724",
      "e2c86e00-bf80-5a0d-005e-6154b26f59d0",
      "e2d79df7-7b4c-88ab-daeb-402665514a88",
      "e3789a69-8c3a-32d1-e515-1e1bbb601de1",
      "e380f952-4c83-3e73-0dea-4539f0bdf019",
      "e3c72cf3-6a64-a2ed-8c8f-69dfa089e589",
      "e3d1bfb7-4b26-251e-66e8-677a4cb0362d",
      "e4472897-ca7d-9e21-0d82-2b8e83a4064f",
      "e46b3bfb-2f7c-6f76-d174-eda79ded9686",
      "e4fa22f0-a063-eee0-d7db-c64412dcd3f5",
      "e54cb2bd-0991-a935-1c64-8733e139d96e",
      "e5913a3b-0ddf-3006-1b42-bb1b2640a748",
      "e5d0627b-a7a5-c151-ae1e-4dcd48bb9983",
      "e5eebb47-7818-27d1-a83a-dd54f1d1033b",
      "e60ed4db-d840-e44b-416e-9c9f27b2932f",
      "e63c0dfb-bff7-a1a1-8f3a-84dc48cab3b2",
      "e65ddf51-7b60-3578-57a0-36997a5bb425",
      "e6998624-8225-68af-17c0-1b44b7df3d1c",
      "e6a7fe1b-eeae-5e58-9a67-b255931653c7",
      "e6f9a325-02a2-fb2a-85fd-28ca16afbccf",
      "e7081c24-ddad-8ee4-6eac-4d386b0dc36b",
      "e711bd89-7df2-904b-2472-2972022f4839",
      "e714e94c-b208-3b4b-4318-8b2dbb7c0200",
      "e739383f-adf0-8b84-1326-fdde32a9e8cd",
      "e761c576-16a1-c058-4aa6-58df7f447436",
      "e7a5fae4-049e-f75c-ca82-97838d9c37a3",
      "e7b09558-b4e4-d87a-766f-91a2f8518cbb",
      "e7c4ca7e-d0cc-1ae3-fb36-dbc9d73ffbc9",
      "e7cac818-d71b-6823-b66d-49a123afda02",
      "e7ce18a8-61f6-b562-c338-7709830030fa",
      "e7e7294b-f915-4846-1e8c-eda0f7022430",
      "e860e4e4-fce3-08c1-c97e-97e9ce859435",
      "e89e85bb-40aa-03c1-3197-1db87aa61207",
      "e92178ee-c616-1629-b20d-f12bec381b04",
      "e9335fec-4d38-38cb-fbad-25dff403d5b7",
      "e93c7055-0c67-e6b1-2dd3-6069d53c1c2e",
      "e99473e9-999c-66da-7997-c22f9c007a08",
      "e9dae09a-8ff7-9f1f-2104-68da27c88f21",
      "e9ef12de-d81c-f872-2f2c-b861751ad8d2",
      "e9fa2305-e7fe-a04d-41fd-9c9382bba390",
      "e9fb8b6b-5809-d292-c348-2b0962e8eca6",
      "ea0be47e-99ed-ca63-8386-c5d2c5caaef6",
      "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5",
      "ea258620-a86d-62f7-7caf-fc2e4cca8d30",
      "ea61ca6f-f2dc-6ce2-c488-53992bb56fec",
      "eac3f5de-47ee-8ebc-2c06-9aa619e42e6f",
      "eaca5708-e469-40f3-2c12-8da66b2dfa4c",
      "eaf3e1be-ec85-a0d1-3dea-c11e6e4aa63a",
      "eb0d969b-65f2-1ab7-8471-d514c443f569",
      "eb107684-f4ed-a90f-5ebe-580369c9b08a",
      "eb300dab-65c4-49f0-5a82-4cbb1e08d2d1",
      "eb57bb9e-e574-a7a9-b285-48c6f9f3786a",
      "eb6f6d9d-0718-0bf0-cd06-80f8e5930e22",
      "eb7dc658-a74c-2170-54f1-577b5379970d",
      "eb8abca0-a5c8-5b39-9ea3-f044196efde4",
      "eb93065d-b188-1aad-7f9a-7c330be3dc25",
      "ebdad1d2-5eae-e9ce-e99d-ff3d5a84a82a",
      "ebe838e1-6ce2-680f-0b1e-8c4a533e5464",
      "ec3b4575-60e7-a499-c37f-c9ae12b11e0e",
      "ec59f453-1855-f508-32d2-f63f10699526",
      "ecb5781d-7c00-ac84-5831-c2d971c76b9b",
      "ecb9fcda-9a8e-86e9-b548-12327b05fe26",
      "eccc9735-913f-1ea9-2d80-146da0716d28",
      "ecdb4257-4905-f6a4-e28f-656ea33ab87c",
      "ed31d169-323a-f162-6020-3882412d04d8",
      "ed70c501-bf76-ffed-7f7e-59421d4f35f3",
      "eddbefb5-44ff-415b-f9c7-8f0b2c63fe5e",
      "ee010e8e-ab94-0cd2-d826-4a0e144c4ffa",
      "ee4a5125-3af0-858b-7745-16e46080c33f",
      "eea2ec2e-9e02-f19c-81ab-238412df461f",
      "eea6f80c-b871-fad7-f989-7021f8db58e0",
      "ef0af52f-cdb2-f336-d575-22848a385532",
      "ef2335ef-865d-4921-c2e5-977ec4dc9ac2",
      "ef3d8eef-ad86-0910-7c0b-550eeb8f5451",
      "ef51d721-4265-e390-f4c0-2dcef95c03d9",
      "ef881666-f6d3-c565-a332-8b1c32d6767b",
      "efcb95a0-6f5b-fba1-f700-465ba398fcef",
      "efd15ca8-c9fd-e5b5-fd7c-153c5417b7ec",
      "efeb6a1e-c75b-3d03-0db8-ebf4d67528bd",
      "f054397a-1259-52a6-55bf-0d870a5e19a5",
      "f07ca678-966f-7c86-2ff7-1e2e5c789f00",
      "f0816440-682a-f732-697d-16863f42e962",
      "f0821ab7-a335-b778-a899-2e4096e77ab8",
      "f086dd03-5f1d-d609-0481-6437df537f6f",
      "f098ca40-9b7b-ac95-36e5-cdecc772bc8e",
      "f0ea43e7-de92-1bd8-83b8-07525c223864",
      "f0efb4e5-2f72-55a7-cee4-10d0a5f0efe1",
      "f1151544-8f4b-8366-9a6f-34d169b63e93",
      "f13d3eb6-1328-46b1-e6d8-bcbcf32f9f88",
      "f18c11cc-0c7e-f94d-9d96-d2b970763b61",
      "f197ff05-afd2-ce9b-577c-de4381d9d0dc",
      "f1d89459-9368-d428-634d-64dbf50df233",
      "f228c3c1-e50a-135f-86fc-f39f6be3e994",
      "f248d8c3-9cde-2774-fa1b-268e5d9915be",
      "f255031c-4d59-a77e-c3c4-07360beb6a70",
      "f2a42dc6-736e-c19b-3734-eb3e01e6e936",
      "f2bcbb9d-d6bd-1323-ab8c-58ef05672709",
      "f2e0653b-18c3-91e4-84a4-0f361625b012",
      "f34ffdd6-6f1e-d798-d8e8-3a9667727a50",
      "f35c1682-0ad8-c906-053c-8ec3c11842af",
      "f3fc55cb-a112-5768-f291-7aa7e6e6910d",
      "f40fa041-b021-e7be-30cd-72a3878e1a5c",
      "f41fd1aa-0799-bc5f-a72d-f53c68cccea5",
      "f4301ac0-7b76-7251-fa56-7151d12627c0",
      "f46afc5b-48d2-01e2-26df-96de77496f2e",
      "f485f448-6463-bf0c-5c73-5612226e638f",
      "f49674cd-6d23-cee5-afc3-2ab0c44378c2",
      "f4b3d190-9819-f908-08d3-428aaaad3863",
      "f4c12d61-e836-d654-58a5-fe93bf176a87",
      "f4e29300-d124-0127-52fb-14d5be0f1ef6",
      "f5232418-3705-8661-dda0-651e98d991b6",
      "f57c5e07-d978-28da-be57-ba8699acc3f8",
      "f5940a1e-17a9-6ad1-da0e-ef7bf8e5d738",
      "f5a5c886-4f07-a6a9-729d-44ccd2ca053a",
      "f5b1db8f-9d56-d29e-2ed3-67d0159e2807",
      "f5d0ddfb-dc85-27c0-9491-279a16aa736b",
      "f5e25625-e4c0-0e8e-4c5a-7aa0601bc418",
      "f5e64514-8fc9-7102-5d02-cc8964685457",
      "f62833bc-4e33-5c5a-16a7-30f44f0dfb16",
      "f6357b69-8777-4f27-73a1-6639e4421881",
      "f69b4ac2-c7c0-9aef-a771-2e558daa4ce1",
      "f6aeab43-7d7b-4476-fe6d-bbe69de759fe",
      "f75a4003-4438-e59e-7d69-4d70f99a81a1",
      "f7d03d9a-23f1-83f9-afb7-770f58aec66c",
      "f7f0235f-f1f3-15d2-ccf5-b870a48b2963",
      "f8ad1f8b-aa8f-75cf-c387-ee2fb475e9b7",
      "f8e9fc76-8f79-3ba2-865c-86297dc7736b",
      "f904c9f7-f66c-3fb3-0783-0460f363145c",
      "f94a18d9-2212-e986-d735-e8d6517f51c7",
      "f999102b-70e4-326e-b4b7-212381fa92b6",
      "f9d83b86-6120-7b43-ec87-953ea6d355eb",
      "f9f3ee49-dd77-1283-b2ef-03abe5e9b4ee",
      "f9f607e8-cae7-06d8-4518-e4bfb811cf48",
      "fa9831c6-6775-a870-e951-3dc897a8c398",
      "fab66767-82dc-d7ff-0214-48dc81671e1d",
      "faee7d4e-e7c7-6b61-6162-8bfbc9873b44",
      "fb199ce7-c83e-19ee-ddd3-cb678550198e",
      "fb903198-40d0-d653-e337-c5a78e92602a",
      "fc0129e3-c37e-b179-e94b-e65159182b50",
      "fc0c17bf-cf1e-736f-2ade-d0520a71e6eb",
      "fc2a9a46-ecb8-7b64-a029-35a65d96b24b",
      "fc2c10cf-d31e-3c6a-5748-8e8e1db47718",
      "fc46a616-3618-60bc-a085-aa97017af805",
      "fcb28975-0bc5-e238-cdec-aceb492b7ddb",
      "fd0b20bc-4cce-db68-f3d1-5689f740af1b",
      "fd60def2-8c4e-a7b0-97b3-7b4a14996ff1",
      "fd6c8b1b-fb78-ab68-ae62-7c279a5f23f4",
      "fdc03c36-ee06-7477-74f4-673a4497fcdc",
      "fe1be7b9-f02e-daf5-2bb4-2fdcdfefe12b",
      "fe3f1ace-2794-6bdf-1e0d-7340c089a2f1",
      "fe3ff1fd-a9c4-18b8-585a-de1977f72e68",
      "fe4a601e-5ea1-ebe2-237f-57e1797814d8",
      "fe679bba-77d8-3999-4971-fadc7385be80",
      "fe9e3b28-ae3f-606d-39b5-eba2e817c2cd",
      "feac5974-899d-282b-f5cf-c9fabb9d9958",
      "feaffd78-30aa-6c1c-d1df-d14f31e98ded",
      "fec44bff-0abd-e91a-a277-550beba03fbf",
      "fec7937b-b7fb-3cb6-3b80-30cbcbefc07c",
      "fef43715-094d-af8e-8056-a4352e57eb4e",
      "fefdf415-49e2-0b92-2a44-68af13b388d8",
      "ff176f6d-d64b-12fa-9d50-98a7f02bcee0",
      "ff22b4ef-785e-c033-fd9d-4429492b3ac6",
      "ff270387-accb-0a70-bf15-3a7ce1232ca2",
      "ff4d67c7-ee7e-3876-1e73-afa2b7e500fa",
      "ff811e0e-4e3f-f433-c5e8-481e818ec140",
      "ffb8fdc4-b3e9-a755-ddb2-ee062c30e2da",
      "ffca3d0e-6852-2c7f-a626-ce5d701ae2bb",
      "ffcd2c36-e746-3f85-e2d7-e73f4f0a9e28"
    ]
  },
  "mutations": [
    {
      "id": "M1-no-readback-confirmation",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M2-no-cross-check-with-change-registry",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M3-no-outside-write-detection",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M4-no-activation-target-membership",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M5-no-commit-recheck",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M6-no-stage-mark-gate",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M7-no-activation-writeset-hash-sync",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M9-fail-open-on-non-success",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M10-no-production-commit-gate",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M11-no-lock-guard-on-real-effects",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M12-stage-persisted-before-effect",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M13-no-activation-readback",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M14-no-real-evidence-invariants",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M15-no-reference-idempotent-recheck",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M16-no-activation-idempotent-recheck",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M17-writeset-evidence-not-persisted",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M18-no-activation-undo",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M19-no-activation-pre-drift-check",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M20-no-new-file-detection",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M21-no-full-baseline-verification-on-rollback",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M23-commit-gate-not-bound-to-instance",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M24-no-change-registry-freeze",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M25-no-port-exception-containment",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M26-no-activation-status-precheck",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M27-no-d13-transition-restriction",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M28-added-target-overwrite-allowed",
      "mechanical_status": "ok",
      "source_sha256": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M8-no-activation-stage-gate",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M29-no-ownership-precheck-before-write",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M30-no-addition-ownership-enforcement",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M31-no-activation-content-binding",
      "mechanical_status": "ok",
      "source_sha256": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M32-no-missing-baseline-half",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M33-no-reentrancy-guard-on-rollback",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "M34-no-duplicate-change-identity-check",
      "mechanical_status": "ok",
      "source_sha256": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    }
  ],
  "evidence": [
    {
      "id": "baseline-targeted",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx",
      "purpose": "开工同条件定向基线（R56/R58 迁移夹具 70/70）",
      "level": "test",
      "conditions": "独立 baseline worktree @ 22ccd6ee2；-p:DeployToBgiTools=false",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "b08d358d451e291b5450029c5adb164657956de9dab0d371a1f5303de2707c2a"
    },
    {
      "id": "baseline-assistant-full",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx",
      "purpose": "开工同条件助手全量基线（1570/2/0/1572）",
      "level": "test",
      "conditions": "独立 baseline worktree @ 22ccd6ee2",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "e854aa4b960867f4ce514697633c42acff57c9deb9b4eaa6b72fdc1eb3f584ac"
    },
    {
      "id": "targeted-final-frozen",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx",
      "purpose": "本批定向回归 119/119（含 49 条新夹具，两轮会诊修复后）",
      "level": "test",
      "conditions": "主工作区最终源码；-p:DeployToBgiTools=false",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "3cf6ad4d7e377188709a47ee9d5ca34488226fb71a65fe26e6717f968a7810c2",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs": "5e30475a6ccb025d326b850cd3a378467b4e398d93f1c229b439bbdd47edb6fc",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "a512c6a1d3bdf7fb9d71ff9c57a2a07acbedfdac27bfe7625267046c266b6d3a",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "c6d5877d2d8cd5de9264a8f6a43c38cd43e485facf7d3d876b40ae302420c4d9",
        "槲寄生调度器总计划.md": "28dfadc931b766cf6448c4080e2d30950e66c3225562ca0f5c4698e3dd69d7bb"
      },
      "file_sha256": "d46576576ea55e3209d9dfa606a2346e2735dcceae2ba3b3222299e3a57cf3a3"
    },
    {
      "id": "assistant-full-final-frozen",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx",
      "purpose": "本批助手全量回归 1619/2/0/1621",
      "level": "test",
      "conditions": "主工作区最终源码；全量",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "3cf6ad4d7e377188709a47ee9d5ca34488226fb71a65fe26e6717f968a7810c2",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs": "5e30475a6ccb025d326b850cd3a378467b4e398d93f1c229b439bbdd47edb6fc",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "a512c6a1d3bdf7fb9d71ff9c57a2a07acbedfdac27bfe7625267046c266b6d3a",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "c6d5877d2d8cd5de9264a8f6a43c38cd43e485facf7d3d876b40ae302420c4d9",
        "槲寄生调度器总计划.md": "28dfadc931b766cf6448c4080e2d30950e66c3225562ca0f5c4698e3dd69d7bb"
      },
      "file_sha256": "c7b53c34d27b4f2ee3cfb2bb211776c0c8011efd57e5228a487c1c1a34726305"
    },
    {
      "id": "subagent-readonly-audit",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md",
      "purpose": "固定开工 ref 的只读子 Agent 独立核查",
      "level": "consult",
      "conditions": "只读、零写入；固定 ref 22ccd6ee2",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "e2c5851064c985945b77e8f4243a3ad16381589d10a808fe007b1e9239aeaa99"
    },
    {
      "id": "field-audit",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/findings.md",
      "purpose": "现场审计 + 会诊发现处置 + 边界（F-1..F-12）",
      "level": "document",
      "conditions": "开工只读审计与会诊后修订",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "48ddc0947bc9142687f9b4604a6de291958ce8972b81d7d088d0e47f4200e31b"
    },
    {
      "id": "claims-regeneration",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt",
      "purpose": "第 1 轮声明面再生差异（+4/-0）",
      "level": "component",
      "conditions": "CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "be653bae6e2fced7f6e4cae5314e542a538a7274c3fd054a19cf5366818423bb"
    },
    {
      "id": "claims-regeneration-r2",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt",
      "purpose": "第 2 轮声明面再生差异（+2/-0，627→629）",
      "level": "component",
      "conditions": "同上",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "83348a4ea8a58395d314aa715293e6fce647e675252e27e148bfbd0a7af89fb1"
    },
    {
      "id": "mutations-summary",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md",
      "purpose": "28 项反向突变逐项补丁与三段判定（供审查者核对判别力）",
      "level": "component",
      "conditions": "主工作区；逐项含独立日志与 TRX",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "46668e1cb646c2f4e310381f57926282248ad4f4baa8be8c7c7926971f2c0243"
      },
      "file_sha256": "958ce01ef06f5515fc9085d18b396ecf0e3ff3f29638bc4850e84c877556fe35"
    },
    {
      "id": "testid-comparison",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json",
      "purpose": "逐 testId 差集 added=49/removed=0/changed=0/unchanged=1572",
      "level": "component",
      "conditions": "主工作区最终源码",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "3cf6ad4d7e377188709a47ee9d5ca34488226fb71a65fe26e6717f968a7810c2",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs": "5e30475a6ccb025d326b850cd3a378467b4e398d93f1c229b439bbdd47edb6fc",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "a512c6a1d3bdf7fb9d71ff9c57a2a07acbedfdac27bfe7625267046c266b6d3a",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "c6d5877d2d8cd5de9264a8f6a43c38cd43e485facf7d3d876b40ae302420c4d9",
        "槲寄生调度器总计划.md": "28dfadc931b766cf6448c4080e2d30950e66c3225562ca0f5c4698e3dd69d7bb"
      },
      "file_sha256": "e033dcd640b801ea615db0d4b7b010cdf84bb3bc1dbfc911677f0c06907fa257"
    },
    {
      "id": "deploy-target-post",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json",
      "purpose": "部署目标事后清单（1158 文件；最新写入早于本批）",
      "level": "component",
      "conditions": "Get-FileHash 清单；全部构建带 -p:DeployToBgiTools=false",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "490edb152357b7e99f6a33a38f7313830c03cbd148c8cc951e6e54776158d26b"
    },
    {
      "id": "review-round1-report",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md",
      "purpose": "第 1 轮会诊报告原文（5 MUST + 4 IMPORTANT + 1 建议级）",
      "level": "consult",
      "conditions": "gpt-6-astra / medium；只读；attempts=1（子批计数 1/8）",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "fc65079cf5684447a1fde127ef9570c54111faa9544bba228f2c1ddcdbf70545"
    },
    {
      "id": "review-round2-report",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round2-report.md",
      "purpose": "第 2 轮（验证轮）会诊报告原文（已闭环 4 / 未闭环 5 + 新报 MUST 4 + IMPORTANT 4）",
      "level": "consult",
      "conditions": "gpt-6-astra / medium；只读；attempts=1（子批计数 2/8）",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "59a4e893d7273d50843cb6881dc3352a1ff7c8abbf708f305b66df9e98818021"
    },
    {
      "id": "claims-regeneration-r3",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r3.txt",
      "purpose": "第 3 轮声明面再生差异（+3/-0，629→632）",
      "level": "component",
      "conditions": "同上",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "bf62d914c7438df3542a24794e9bba022ea4599b37ced50aecf622991e8c7127"
      },
      "file_sha256": "03ec3a5f5b246779d6b92ab09f17ee71c7273247a4b5ed7bed1a2ace0519f81a"
    },
    {
      "id": "mut-M1-no-readback-confirmation",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "82f7d17e14cd84d5c2a2621ee2007cd38a5cf2f654c3888fd897947471123f8b"
    },
    {
      "id": "mut-M2-no-cross-check-with-change-registry",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "a771468a8da2c83a543c1626e858e6e28e1db0747290910229776ad615c9c9ec"
    },
    {
      "id": "mut-M3-no-outside-write-detection",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "675571a50cae775454ad04366269cef703e0b890bfec46a257a59eb1e2ff32a8"
    },
    {
      "id": "mut-M4-no-activation-target-membership",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "31f402ae686f5997460af5867a887f84d797917affa34e87fe29992b3f5afa8e"
    },
    {
      "id": "mut-M5-no-commit-recheck",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "48a1462edf3015c713144d75e45cf373ddaef375ec6d5a904b4da89a6684e529"
    },
    {
      "id": "mut-M6-no-stage-mark-gate",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "1d0a52e6c2ac5c3ad0b59c75e7e89379e18303ffe91f354f0793f3744ebc01a3"
    },
    {
      "id": "mut-M7-no-activation-writeset-hash-sync",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "fe0a87f220523a0c29b090ddfd975e11436fa33fa528871cc92b92d6c6399f53"
    },
    {
      "id": "mut-M9-fail-open-on-non-success",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "269a845cde2b1e34bcb8e547cec444df5a4958d4540d9bfa9a66f9924e25eb80"
    },
    {
      "id": "mut-M10-no-production-commit-gate",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "f408ee6ef613a944081c2bd77fc29cf23bbe3aeee2c7b9612ad7ca85eaf4f02a"
    },
    {
      "id": "mut-M11-no-lock-guard-on-real-effects",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "502776e7405515199b84c9b522f4d93f9f0882962a95f590d19c54d738117996"
    },
    {
      "id": "mut-M12-stage-persisted-before-effect",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "35299192439c802f4836242548296087276f9dd7107d966f50aa811090fe2f19"
    },
    {
      "id": "mut-M13-no-activation-readback",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "c244a172c504f22e55c786b6daf164177aee2fc7d78ee2c2adbb60beb3e2a3df"
    },
    {
      "id": "mut-M14-no-real-evidence-invariants",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "e81ddd5dbfa71904c846801bc8616316b72c766abd3f0e274398d219d615f19a"
    },
    {
      "id": "mut-M15-no-reference-idempotent-recheck",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "269b85dadef14c0e9d697c9ca667f17bbaa7e431c5a58d2b86bef6fdac709ab6"
    },
    {
      "id": "mut-M16-no-activation-idempotent-recheck",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "32a45793c9f1d036500a3195b51060adbd560cbd0c18d4b16e5cce746f1146fe"
    },
    {
      "id": "mut-M17-writeset-evidence-not-persisted",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "967205b2af3208bd0aaf2e013e69b25ad278ad590687c725ba58cb02cfca5b9b"
    },
    {
      "id": "mut-M18-no-activation-undo",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "7b851f3f755b45a6ccf2034086dfa271366502258c7b8350aeb5b5a3bc7e7236"
    },
    {
      "id": "mut-M19-no-activation-pre-drift-check",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "403119ac3bdb9372a79b9becb272ee3b2db159beee48efc0bb8e2443dd14a040"
    },
    {
      "id": "mut-M20-no-new-file-detection",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "c0f3a6001ea3f017ae563e813edeef6f1c5aa480f44e0f8cb2b7e89eb53d0184"
    },
    {
      "id": "mut-M21-no-full-baseline-verification-on-rollback",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "dac07f0c8fa437d232403edcf0f3ba414211754b4f741049815aa4f9b80dad63"
    },
    {
      "id": "mut-M23-commit-gate-not-bound-to-instance",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "7337ff5a22f82b64621efcb1014db344871d70ec79691ec502324fa1255a21e3"
    },
    {
      "id": "mut-M24-no-change-registry-freeze",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "b34da4028b844fd0300d72abbdd482230d11caa509dc4c26f361282cd2969277"
    },
    {
      "id": "mut-M25-no-port-exception-containment",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "025365a4f68b7c0b68d666d6b01b7ad347628ba93b6202afe85fa7e812ff0e24"
    },
    {
      "id": "mut-M26-no-activation-status-precheck",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "4c9610dd55c2726ef7603b6ec36c8c5876b80394d98a35db4db3fb6752684db1"
    },
    {
      "id": "mut-M27-no-d13-transition-restriction",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "f012a3d9ac851f45c5f2a2a47ff4d9795f4781da825361e653801ec39f860173"
    },
    {
      "id": "mut-M28-added-target-overwrite-allowed",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4"
      },
      "file_sha256": "3eff314c5afc305e8c5086c1f1a9366ce7675e8da34fdf6e9f19818ed6020c94"
    },
    {
      "id": "mut-M8-no-activation-stage-gate",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-activation-stage-gate/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "c3736039922135df45e63c924ff0d3a2a6b415472eb5e83197352323f7e35e63"
    },
    {
      "id": "mut-M29-no-ownership-precheck-before-write",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M29-no-ownership-precheck-before-write/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "aafbbe8591bb1862e1349af014857a64619250aa3bbf79ee96d3317b2b2f8356"
    },
    {
      "id": "mut-M30-no-addition-ownership-enforcement",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M30-no-addition-ownership-enforcement/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "3e7ccd3f795cb08b4342376e982656da27af0c176e6cc0856f2b110b944d82ed"
    },
    {
      "id": "mut-M31-no-activation-content-binding",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M31-no-activation-content-binding/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs": "c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4"
      },
      "file_sha256": "ec82e8398409d2bc915edcc731e63a3be4bbe2c2f576e2680d7741ad2c040472"
    },
    {
      "id": "mut-M32-no-missing-baseline-half",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M32-no-missing-baseline-half/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "a186088937d99e5c335db5d79d14feaade50b868e5ea6ba3c38332f4ccec9a6e"
    },
    {
      "id": "mut-M33-no-reentrancy-guard-on-rollback",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M33-no-reentrancy-guard-on-rollback/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "5f6e1c766371d3666f12a8339228bbaaef3f6be051ecb129b905fc953e0f04dd"
    },
    {
      "id": "mut-M34-no-duplicate-change-identity-check",
      "path": "_workflow/r56-reference-activation-wiring-2026-09-29/mutations/M34-no-duplicate-change-identity-check/record.json",
      "purpose": "突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复",
      "level": "component",
      "conditions": "三段独立日志与 TRX，绑定源文件哈希",
      "binding": "historical",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8"
      },
      "file_sha256": "abd8e785ad96a8caa22027de9bfa150c276afd40d0bf38c2417bfae8124ca0d3"
    }
  ],
  "quality_verdict": "NOT PROVIDED"
}

## git status --porcelain (all changes; ownership requires manual classification)
 M Docs/design/mistletoe-parallel-deliveries.md
 M Docs/design/mistletoe-session-relay-2026-09-24.md
 M Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
 M Docs/design/unified-job-registry-master-plan.md
 M MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs
 M MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs
 M Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
 M 槲寄生调度器总计划.md
?? .zcodeignore
?? AGENTS.md.bak-20260926-142346
?? AGENTS.md.bak-20260926-1438
?? AGENTS.md.bak-20260927-brake
?? AGENTS.md.bak-auto-handoff-20260927
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoAutoEqDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoBurstGateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/AutoFightTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/CombatHealthDetector.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/Model/Avatar.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialAutoFightRouter.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialParamAdapter.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/MiniMapPositionDiagnostics.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/ZeroCoordGuard.cs.bak
?? BetterGenshinImpact/GameTask/Common/CaptureRetryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/FocusRecoveryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak2
?? Docs/design/mistletoe-parallel-recovery-2026-09-27.md
?? Docs/design/mistletoe-r62-registration-2026-09-27.md
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r4_doc
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r5_1
?? MultiplayerHoeingAssistant.dll
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs.bak_b16r4_2
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitPrerequisiteModels.cs.bak_b16r4_1
?? MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartBugConditionTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartPreservationPbtTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/TestResults/
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_10
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_11
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_12
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_13
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_3
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_4
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_5
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_6
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_7
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_8
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_9
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationModels.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTrigger.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.wip-batch16
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs
?? Test/MultiplayerHoeingAssistant.UnitTest/TestResults/
?? TestResults/
?? _aout.txt
?? _assist_xamlcheck.log
?? _audit1.txt
?? _audit2.txt
?? _audit3.txt
?? _audit4.txt
?? _audit5.txt
?? _audit6.txt
?? _backup_assistant-config.json
?? _backup_dodoco_settings.json
?? _batch13/
?? _batch14/
?? _batch14_admissionkind_hits.txt
?? _batch15/
?? _batch16/
?? _batch17/
?? _batch19/b19_commit_verify.txt
?? _batch20/
?? _batch21/b21_red.trx
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/evidence.json
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/mutant.log
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/restored.log
?? _batch21/sb21-1-r8-review/accepted-no-job-mutant/
?? _batch21/sb21-1-r8-review/accepted-no-job/
?? _batch21/sb21-1-r8-review/assistant-full-post-r8-2.log
?? _batch21/sb21-1-r8-review/assistant-full-post-r8.log
?? _batch21/sb21-1-r8-review/claim-surface-final-docs/
?? _batch21/sb21-1-r8-review/claim-surface-final-docs2/
?? _batch21/sb21-1-r8-review/claim-surface-final-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-manifest-before.txt
?? _batch21/sb21-1-r8-review/claim-surface-post-r16-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-regen/
?? _batch21/sb21-1-r8-review/claim-surface-verify/
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-restored.log
?? _batch21/sb21-1-r8-review/doc-relay-before-inventory.txt
?? _batch21/sb21-1-r8-review/final-full/
?? _batch21/sb21-1-r8-review/git-diff.txt
?? _batch21/sb21-1-r8-review/git-status.txt
?? _batch21/sb21-1-r8-review/ledger-batch21-before-sb21-1-closeout-refresh.json
?? _batch21/sb21-1-r8-review/ledger-batch21-pre-disposition.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/batch51-r16-design-excerpt.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/committed-sb21-1.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-scoped-working-tree.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-staged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-status-porcelain.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-unstaged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/ledger-batch21-before-r9-update.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/r8-findings-and-dispositions.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/review-context.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/sb21-1-design-closeout-excerpt.txt
?? _batch21/sb21-1-r8-review/pre-accepted-fact-guard-inventory.txt
?? _batch21/sb21-1-r8-review/pre-repair-source-inventory.txt
?? _batch21/sb21-1-r8-review/previous-reverse-mutants.md
?? _batch21/sb21-1-r8-review/relay-before-final-refresh/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/catch-summary.json
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/summary.json
?? _batch21/sb21-1-r8-review/reverse-mutation-evidence.md
?? _batch21/sb21-1-r8-review/review-context.md
?? _batch21/sb21-1-r8-review/send-attempted-current-mutant/
?? _batch21/sb21-1-r8-review/targeted-after-r8.log
?? _batch21/sb21-1-reverse-mutants/
?? _batch21/sb21-2-review/assistant-full-baseline/
?? _batch21/sb21-2-review/assistant-full-final/
?? _batch21/sb21-2-review/assistant-full-r5-counter-final/
?? _batch21/sb21-2-review/assistant-full-r5-final/
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes.log
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes/
?? _batch21/sb21-2-review/assistant-full-r5-original-parser-final/
?? _batch21/sb21-2-review/assistant-full-red-baseline/
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.json
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.md
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.json
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.md
?? _batch21/sb21-2-review/assistant-full-test-diff.json
?? _batch21/sb21-2-review/assistant-full-test-diff.md
?? _batch21/sb21-2-review/claim-final-noenv/
?? _batch21/sb21-2-review/claim-final-regen/
?? _batch21/sb21-2-review/claim-manifest-before-r5-closeout.txt
?? _batch21/sb21-2-review/claim-manifest-before-r5-finalize.txt
?? _batch21/sb21-2-review/claim-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-regen/
?? _batch21/sb21-2-review/claim-r5-counters-final-noenv/
?? _batch21/sb21-2-review/claim-r5-counters-final-regen/
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv.log
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv/
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry.log
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry/
?? _batch21/sb21-2-review/claim-r5-exact-final-regen.log
?? _batch21/sb21-2-review/claim-r5-exact-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-noenv/
?? _batch21/sb21-2-review/claim-r5-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-state-noenv.log
?? _batch21/sb21-2-review/claim-r5-final-state-noenv/
?? _batch21/sb21-2-review/claim-r5-noenv.log
?? _batch21/sb21-2-review/claim-r5-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen/
?? _batch21/sb21-2-review/claim-r5-regen.log
?? _batch21/sb21-2-review/claim-r5-regen/
?? _batch21/sb21-2-review/claim-regen/
?? _batch21/sb21-2-review/claim-review-noenv/
?? _batch21/sb21-2-review/claim-review-regen/
?? _batch21/sb21-2-review/consultation-ledger-before-r4-record.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-dispatch.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-update.json
?? _batch21/sb21-2-review/implementation-pre-edit-inventory.json
?? _batch21/sb21-2-review/item-generation-overflow-diagnostic/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v2/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v3/
?? _batch21/sb21-2-review/item-generation-overflow-fixed/
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/LocalWaitQueueStore.strict-current.cs
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-nonincr-build.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-overflow-test.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/test-results/
?? _batch21/sb21-2-review/ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/ledger-pre-r5-update.json
?? _batch21/sb21-2-review/legacy-gap-v2/
?? _batch21/sb21-2-review/localwait-r5-counter/
?? _batch21/sb21-2-review/localwait-r5-final/
?? _batch21/sb21-2-review/localwait-r5-note-fixes.log
?? _batch21/sb21-2-review/localwait-r5-note-fixes/
?? _batch21/sb21-2-review/localwait-r5-original-parser-final/
?? _batch21/sb21-2-review/localwait-suite-final-pre-docs/
?? _batch21/sb21-2-review/localwait-suite-final/
?? _batch21/sb21-2-review/localwait-suite-v1/
?? _batch21/sb21-2-review/mutations-r3/legacy-reservation/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v2/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v3/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow/
?? _batch21/sb21-2-review/mutations-r5/c5-consume-generation/
?? _batch21/sb21-2-review/original-parser-confirmation-build.log
?? _batch21/sb21-2-review/original-parser-confirmation/
?? _batch21/sb21-2-review/pre-edit-inventory.json
?? _batch21/sb21-2-review/pre-implementation-inventory.json
?? _batch21/sb21-2-review/pre-r5-closeout/b21_plan-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/handoff-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/r5-3-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/red-results-summary-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/reverse-mutations-before-r5.md
?? _batch21/sb21-2-review/r4-item-overflow-fix-pre-edit.json
?? _batch21/sb21-2-review/r5_3_anchor_excerpt.md
?? _batch21/sb21-2-review/r5_3_sb21_2_current_excerpt.md
?? _batch21/sb21-2-review/red-final-before-implementation-v2/
?? _batch21/sb21-2-review/red-final-before-implementation/
?? _batch21/sb21-2-review/red-r1/
?? _batch21/sb21-2-review/red-r2/
?? _batch21/sb21-2-review/red/
?? _batch21/sb21-2-review/review-r5-material-out-staged.diff
?? _batch21/sb21-2-review/targeted-final/
?? _batch21/sb21-2-review/targeted-r5-counter/
?? _batch21/sb21-2-review/targeted-r5-generation-final/
?? _batch21/sb21-2-review/targeted-r5-generation/
?? _batch21/sb21-2-review/targeted-r5-note-fixes.log
?? _batch21/sb21-2-review/targeted-r5-note-fixes/
?? _batch21/sb21-2-review/targeted-r5-original-parser-final/
?? _batch21/sb21-2-review/targeted-v1/
?? _batch21/sb21-2-review/targeted-v2/
?? _batch21/sb21-2-review/targeted-v3/
?? _batch21/sb21-2-review/test-project-build-r5-counter-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-note-fixes-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-original-parser.log
?? _batch21/sb21-3-review/
?? _c16out.txt
?? _dpiprobe/
?? _extprobe/
?? _fixhash.py
?? _incidentprobe/
?? _log.py
?? _mergebuild.log
?? _mergebuild2.log
?? _mergebuild3.log
?? _mergebuild4.log
?? _mergebuild5.log
?? _mergebuild6.log
?? _mergebuild7.log
?? _mergebuild8.log
?? _mergebuild9.log
?? _probe/
?? _probe_taskline.png
?? _probe_taskline2.png
?? _r17.txt
?? _r5_batch9_assistant_full.log
?? _r5_test_temp/
?? _statusprobe/
?? _styleprobe/
?? _tools/
?? _uidmask_preview.png
?? _wf.py
?? _workflow/import-integration/
?? _workflow/import-pilot-r56/
?? _workflow/parallel-recovery/
?? _workflow/parallel-registry-integration/
?? _workflow/r56-reference-activation-wiring-2026-09-29/
?? _workflow/r62-registration/
?? _workflow/sb21-3-bo4/
?? _workflow/sb21-4/baseline/
?? _workflow/sb21-4/claims/
?? _workflow/sb21-4/closeout-final-20260928-v2/
?? _workflow/sb21-4/closeout-final-20260928-v3/
?? _workflow/sb21-4/closeout-final-20260928-v4/scoped-staged.diff
?? _workflow/sb21-4/closeout/
?? _workflow/sb21-4/consultation-budget.md
?? _workflow/sb21-4/deliveries/capture-parallel-index-diff-r7.py
?? _workflow/sb21-4/deliveries/closeout-discovery-r3.json
?? _workflow/sb21-4/deliveries/final-closeout-r1.exit-code
?? _workflow/sb21-4/deliveries/final-closeout-r1.json
?? _workflow/sb21-4/deliveries/final-closeout-r2.json
?? _workflow/sb21-4/deliveries/final-closeout-r3.json
?? _workflow/sb21-4/deliveries/final-r7-discovery.json
?? _workflow/sb21-4/deliveries/natural-boundary-final-r2.json
?? _workflow/sb21-4/deliveries/natural-boundary-v2.json
?? _workflow/sb21-4/deliveries/natural-r7-continuation.json
?? _workflow/sb21-4/deliveries/natural-r7.json
?? _workflow/sb21-4/deliveries/parallel-index-json-malformed-pre-fix.bin
?? _workflow/sb21-4/deliveries/parallel-index-r7-diff.patch
?? _workflow/sb21-4/deliveries/parallel-index-r7-hashes.json
?? _workflow/sb21-4/deliveries/post-completion-registered-r1.json
?? _workflow/sb21-4/deliveries/post-registration-r2.json
?? _workflow/sb21-4/deliveries/post-registry-update-r1.json
?? _workflow/sb21-4/deliveries/pre-r7-registration/
?? _workflow/sb21-4/deliveries/pre-review-r2.json
?? _workflow/sb21-4/deliveries/r8-review-discovery.json
?? _workflow/sb21-4/deliveries/register-continuation-candidates.py
?? _workflow/sb21-4/deliveries/repair-parallel-index-r7.py
?? _workflow/sb21-4/evidence-closeout-r4/
?? _workflow/sb21-4/evidence-final-20260928-v1/
?? _workflow/sb21-4/evidence-final-20260928-v2/
?? _workflow/sb21-4/evidence-final-20260928-v3/
?? _workflow/sb21-4/evidence-final-20260928-v4/
?? _workflow/sb21-4/evidence-final-r2-retry1/
?? _workflow/sb21-4/evidence-final-r2-retry2/
?? _workflow/sb21-4/evidence-final-r2-retry3/
?? _workflow/sb21-4/evidence-final-r2-retry4/
?? _workflow/sb21-4/evidence-final-r2-retry5/
?? _workflow/sb21-4/evidence-postcommit-r1/
?? _workflow/sb21-4/evidence-postcommit-r2/
?? _workflow/sb21-4/evidence-pre-review-v2/
?? _workflow/sb21-4/evidence-pre-review-v3/
?? _workflow/sb21-4/evidence-pre-review/
?? _workflow/sb21-4/evidence-r5-05b/
?? _workflow/sb21-4/evidence-r5-05c/
?? _workflow/sb21-4/evidence-r5-05d/
?? _workflow/sb21-4/evidence-r8-20260928-v1/
?? _workflow/sb21-4/evidence-r8-budget-fit-20260928-v1/
?? _workflow/sb21-4/evidence-r8-corrected-20260928-v1/
?? _workflow/sb21-4/final/
?? _workflow/sb21-4/mutations-review-r1-retry1/
?? _workflow/sb21-4/mutations-review-r1-retry2/
?? _workflow/sb21-4/mutations-review-r1/
?? _workflow/sb21-4/mutations/
?? _workflow/sb21-4/pre-closeout-refresh-20260928-0307/
?? _workflow/sb21-4/red/
?? _workflow/sb21-4/refresh_closeout_docs.py
?? _workflow/sb21-4/review-packet-r8-final-20260928-v1/
?? _workflow/sb21-4/review-prep-final/
?? _workflow/sb21-4/review-prep-r2/
?? _workflow/sb21-4/review-r5-05d/
?? _workflow/sb21-4/review-snapshot-r2-retry3/
?? _workflow/sb21-4/review-snapshot-r2-retry4/
?? _workflow/sb21-4/review/committed-diff-afe84.patch
?? _workflow/sb21-4/review/gpt-r1-request.md
?? _workflow/sb21-4/review/gpt-r1-review.md
?? _workflow/sb21-4/review/gpt-r1-snapshot-v2/
?? _workflow/sb21-4/review/gpt-r1-snapshot-v3/
?? _workflow/sb21-4/review/gpt-r2-attempt1-failure.md
?? _workflow/sb21-4/review/gpt-r2-request.md
?? _workflow/sb21-4/review/gpt-r3-budget.md
?? _workflow/sb21-4/review/gpt-r3-objective.md
?? _workflow/sb21-4/review/gpt-r3-request.md
?? _workflow/sb21-4/review/gpt-r3-review.md
?? _workflow/sb21-4/review/gpt-r4-review.md
?? _workflow/sb21-4/review/gpt-r5-review.md
?? _workflow/sb21-4/review/gpt-r6-review.md
?? _workflow/sb21-4/review/gpt-r7-review.md
?? _workflow/sb21-4/review/ledger-closeout-before-owner-checkpoint.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-final.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-snapshot.json
?? _workflow/sb21-4/review/ledger-pre-r3-update.json
?? _workflow/sb21-4/review/ledger-r2-snapshot.json
?? _workflow/sb21-4/review/manifest-r3.json
?? _workflow/sb21-4/review/manifest-r3b.json
?? _workflow/sb21-4/review/manifest-r3c.json
?? _workflow/sb21-4/review/material-out-tracked.diff
?? _workflow/sb21-4/review/parallel-recovery-registration-2026-09-28.json
?? _workflow/sb21-4/review/parallel-recovery-registration-latest.json
?? _workflow/sb21-4/review/parallel-recovery-report-2026-09-28.md
?? _workflow/sb21-4/review/parallel-recovery-report-latest.md
?? _workflow/sb21-4/review/pre-edit-r3.json
?? _workflow/sb21-4/review/r3-baseline/
?? _workflow/sb21-4/review/r3-repair/
?? _workflow/sb21-4/review/r4-prep/
?? _workflow/sb21-4/review/r5-3-contract-extract.md
?? _workflow/sb21-4/review/r5-3-sb21-4-current.md
?? _workflow/sb21-4/review/r5-repair/
?? _workflow/sb21-4/review/r6-audit-evidence-v6/
?? _workflow/sb21-4/review/r6-repair/
?? _workflow/sb21-4/review/r6-review-20260928-v1/
?? _workflow/sb21-4/review/r6-review-snapshot-final/
?? _workflow/sb21-4/review/r6-review-snapshot-v2/
?? _workflow/sb21-4/review/r6-review-validation-final/
?? _workflow/sb21-4/review/r6-review-validation/
?? _workflow/sb21-4/review/r7-repair/
?? _workflow/sb21-4/review/r7-review-20260928-v1/
?? _workflow/sb21-4/review/r8-review-20260928-v1/.keep
?? _workflow/sb21-4/review/r8-review-20260928-v1/budget-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/findings-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-before-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-before-r8-sync.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-sync-evidence.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/objective-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/parallel-deliveries-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/active-ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/builds-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-final-closeout.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-after-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-pre-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v3/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/next-batch-prompt-final.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/test-project-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/validation-summary.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-comparisons-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-opening-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-r6-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/validation-summary-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/workflow-preflight-correction.md
?? _workflow/sb21-4/review/redundant-mutant-exclusion.md
?? _workflow/sb21-4/review/snapshot-r3/
?? _workflow/sb21-4/review/snapshot-r3c/
?? _workflow/sb21-4/review/snapshot-r4f/
?? _workflow/sb21-4/review/snapshot-r4g/
?? _workflow/sb21-4/review/staged-supplement.patch
?? _workflow/sb21-4/review/status-at-r2-prep.txt
?? _workflow/sb21-4/review/supplemental-tracked-diffs.patch
?? _workflow/sb21-4/review/unstaged-supplement.patch
?? _workflow/sb21-4/settled-final-r2/
?? _workflow/sb21-4/settled-final-r3/
?? _workflow/sb21-4/settled-final-r4/
?? _workflow/sb21-4/write_manifest.py
?? _workflow/wave3-bo6bo7-receive/closeout-receive-20260928-v7/
?? _workflow/wave3-bo6bo7-receive/scripts/_fin.py
?? _workflow/wave3-bo8-bo9/claims-v2/
?? _workflow/wave3-bo8-bo9/claims-v3/
?? _workflow/wave3-bo8-bo9/closeout-20260928-v1/
?? _workflow/wave3-bo8-bo9/closeout-20260928-v2/
?? _workflow/wave3-bo8-bo9/deploy-target-after.txt
?? _workflow/wave3-bo8-bo9/final-v3/
?? _workflow/wave3-bo8-bo9/final-v7/
?? _workflow/wave3-bo8-bo9/final-v8/
?? _workflow/wave3-bo8-bo9/fixed-v2/
?? _workflow/wave3-bo8-bo9/fixed/
?? _workflow/wave3-bo8-bo9/mutation-records-final.json
?? _workflow/wave3-bo8-bo9/mutation-records-round2.json
?? _workflow/wave3-bo8-bo9/mutations-final/
?? _workflow/wave3-bo8-bo9/mutations-round2/
?? _workflow/wave3-bo8-bo9/mutations-run-final.log
?? _workflow/wave3-bo8-bo9/mutations-run-round2.log
?? _workflow/wave3-bo8-bo9/mutations-run.log
?? _workflow/wave3-bo8-bo9/mutations/
?? _workflow/wave3-bo8-bo9/review-v1/
?? _workflow/wave3-bo8-bo9/review-v2/
?? _workflow/wave3-bo8-bo9/scripts/_write_commit_record.py
?? build_final.log
?? build_head_output.txt
?? test_preservation.txt
?? testrun_preservation.log

## scoped unstaged diff
diff --git a/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md b/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
index 719ca4110..4bf77b1ad 100644
--- a/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
+++ b/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
@@ -5248,3 +5248,95 @@ BO-6/BO-7 已闭合项不重开。证据限于助手侧源码、真实 Runner 
 - **未消费成果保持**：`r61-*`、`r62-*`、`r57-*`、`r58-acceptance-prep`、`r5-prepared-process-*`、`r5-slot-process-*`、`parallel-delivery-review-requirements-*` 等条目**不在本批范围**，状态与目标批次不变；`wave3-bo6-bo7-*` 保持 `verified`。
 - **工作区如实登记**：`Docs/design/mistletoe-session-relay-2026-09-24.md` 与 `Docs/design/unified-job-registry-master-plan.md` 两份既有未提交设计文档**完整保护、未触碰、未提交**；工作区其余历史批次证据、`.bak`／`.stale`、日志、TestResults、DLL／工具输出与截图均为材料外变更，不进入提交。
 - **门禁**：A–F 六项 R5.6 启用前置仍未闭合；BGI 产品入口、真实 User、R5.8 签署、E3/E4/E5、热键面与生产进程门继续关闭；**未做**实机或生产验收，本批**不**声称 R5.6 或 R5 完成。建议级残项 `R56-D1`（提交后快照损坏 + 直接授权交错无覆盖）与 `F-5`（交付 B 行号漂移）按各自条件留待下一次重绑定 `MigrationSwitchTransaction.cs` 哈希的批次处置。
+
+## §24.129 R5.6 A 项主线施工子批——隔离配置根的真实引用写入与 candidate→active 激活接线（2026-09-29）
+
+### §24.129.1 范围、来源绑定与离线边界
+
+- **本批只施工 R5.6 验收矩阵 A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程只在**隔离配置根**内；**不**执行真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。B–F 与真实入口/实机门**本批未验收**。
+- 开工实况：分支 `main-OldTeaBag-B168`、HEAD `22ccd6ee2721abb1642535253997c57dbf9020d6`；开工快照与 23 行风险矩阵见 `_workflow/r56-reference-activation-wiring-2026-09-29/`（`workflow.py begin` 先于任何代码改动）。
+- 已 integrated 的 `r56-migration-audit`／`r56-activation-prep` **不重复接收**；`tools/mistletoe/deliveries.py` 只读发现检查在开工与收口各运行一次。
+- **只读子 Agent**（固定开工 ref `22ccd6ee2`，只读、零写入）独立复核三问：跨程序集可达性、写方/消费方枚举、写集与回滚归属；报告 `_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md`。其结论与主执行者审计一致，但**仍由主执行者按最终源码复核**，且不替代回归与突变。
+- 材料外变更：工作区其余历史证据、`.bak`／`.stale`、日志、`TestResults`、DLL 与两份既有未提交设计文档（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）**完整保护、未触碰、未提交**。
+
+### §24.129.2 反例先行：阶段标记不等于真实副作用（本批要推翻的旧语义）
+
+- `MarkReferenceUpdateCompleted`／`MarkActivated` 实现为 `Advance(stage)`：**只校验合法转换并落 manifest**，不执行任何引用写入或激活动作。
+- 可定位反例（`R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired`）：未注入真实副作用端口时，**零文件写入**即可 `SnapshotReady→ReferenceUpdating→Activated→RehearseRollback→Commit` 全部成功，且配置根字节始终未变（假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
+- 另证明 R1 迁移器产出的 `activation.status = "candidate-ready"` 在固定版本中**只有读取判定与拒绝路径**（`TaskCenterHost` 保存/启动/移交启动/移交恢复四处禁写禁启、`WorkflowPlanner` 预检阻断、UI 只读预览），**没有任何生产写入路径**把该状态改写为 `active`。
+
+### §24.129.3 最小实施（真实副作用 + 读回确认先于阶段推进）
+
+- 新增 `IMigrationEffectService` 与真实实现 `WorkflowFileMigrationEffectService`（`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`）：引用重命名（`nodes[].ref.config`）、新增文件、`candidate-ready→active` 激活（读改写）；逐文件**临时文件 + 同目录替换**、**保留原 BOM 形态**、**写前字节复检**（读后被锁外改动即放弃）、解析失败**隔离跳过不覆盖**；结果分**成功／确定拒绝／写后不明／取消**四类并如实回报**已落盘文件数**。
+- `MigrationSwitchTransaction` 新增两个真实入口（端口经构造注入，与既有 `quiesce`／`stageHook` 同模式）：
+  - `ApplyReferenceUpdate(plan)`：**写集声明校验**（非空、路径安全、无重复、逐项等于已登记变更归属且无漏项/多项；`Deleted` 不支持）→ 真实副作用 → **语义读回 + 字节读回** → 才推进 `ReferenceUpdating` 并持久化 `referenceWriteSet`；另做**写集外零改动**核对（基线文件逐字节不得变化）。
+  - `ActivateCandidate(request)`：须已完成已确认的引用更新；目标必须**在已确认写集内**；真实激活 + **状态读回** → 才推进 `Activated` 并持久化 `activationRecord`（同步更新写集哈希，因激活同样改写了该文件）。
+- **失败语义**：拒绝／写后不明／副作用后取消 ⇒ `Blocked`（fail-closed、**不盲目重试**、零生产许可）；**副作用前取消**（`CompletedWrites==0`）⇒ 保持当前阶段、可回滚；已到目标阶段时**幂等重读复核**（不二次触发副作用，盘上漂移即拒绝）。
+- **提交与回滚**：提交前复核写集与激活记录（`commit_recheck_failed`，锁外漂移一律拒绝）；回滚**先撤销真实激活**（读回当前状态 → 反向副作用 → 再读回确认），再恢复旧字节、按归属删除新增，并要求**旧态字节与引用一致**；未确认即 `Blocked`，**绝不报告完整回滚**。
+- **持久化与校验**：manifest 新增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord`，全部纳入 `ManifestIntegrity` 摘要与**结构与状态不变量**（写集路径安全、激活目标须在写集内、真实事务的 `ReferenceUpdating/Activated/Committed` 必须携带对应读回证据）；`realEffectsRequired` 由开事务时是否注入端口决定并受摘要覆盖，**篡改后重算摘要仍判无效**。
+
+### §24.129.4 验证证据（绑定最终代码版本）
+
+- **定向**：`R56MigrationSwitchTransactionTests` + `R58MigrationRehearsalTests` + `R56ReferenceActivationWiringTests` **111/111**（第 1 轮会诊修复后复跑）；同条件开工基线（前两类）**70/70**。
+- **助手全量**：**1611 通过 / 2 跳过 / 0 失败 / 1613**；同条件开工基线 **1570 / 2 / 0 / 1572**；逐 testId 差集 **added=41 / removed=0 / changed=0 / unchanged=1572**。
+- **反向突变 28 项**（`_workflow/…/mutations/`，逐项含 build/baseline/mutant/restored 独立日志与 TRX、补丁原文与三段判定，汇总见同目录 `summary.md`）：baseline Passed / mutant Failed（构建 exit 0、测试 exit>0，命中具名断言）/ restored Passed，**源码逐字节恢复**（事务文件 `46668E1CB646…`、引用服务文件 `6AF93577F049…`）。其中 M3、M5、M14、M28 为**整块/组合削弱**型突变（同一位点存在多层独立复核，单独削弱任一层会被另一层拦下，故按整块定义以取得判别力）——此点如实登记，不主张「每层都有独立判别力」。
+- **矩阵覆盖**：状态/并发/故障 **33 行**（其中 32 行 `covered` 并各绑定有效突变；`REF-C4`「同实例并发提交/回滚」为 `not_applicable`：同实例串行边界由代码结构（共用 monitor）承载，真实交错的单点突变不可确定性构造——已附尝试记录，判别力由不变量夹具 + `REF-C2`/`REF-C3` 承担）。
+- **部署目标**：全部构建与测试带 `-p:DeployToBgiTools=false`；部署目录 1158 个文件、**最新最后写入时间 2026-09-27 09:29:33（早于本批 2026-09-29）**，即本批**未写入部署目标**（该事实只支持「本批未留下可观察变化」，不主张过程中从未写入）。
+- **BGI 侧**：本批未修改 `BetterGenshinImpact` 任何文件（`git status`／`git diff` 可核），故未跑 BGI 相关回归。
+
+### §24.129.5 未闭合项、owner 检查点与门禁
+
+- **B–F 六项启用前置继续未闭合**：助手流程/引用/激活元数据恢复范围（B）、生产消费必经检查点（C）、真实静止窗口写方全覆盖（D）、真实入口回执与责任（E）、目标机路径身份（F）本批**未验收**。
+- **owner 检查点（1 项，正确性相关，本批不自行裁决）**：BGI 侧 W4 引用服务 `OneDragonConfigReferenceService` 为 `BetterGenshinImpact` 程序集内 `internal static`，而助手工程**无**对 BGI 的程序集/项目引用（`using BetterGenshinImpact` 命中 0、`InternalsVisibleTo` 只授予 BGI 单测）⇒ 助手进程**无法直调** W4。故**生产环境应由哪一端提供引用写方**（BGI 侧直调 W4／下沉共享库／经既有 IPC 委派）**无权威材料裁决**；本批的真实引用写方是助手侧实现（隔离根内），**生产接线保持关闭**，不据本批开启。待 owner 裁决后再定接线归属。
+- **门禁**：真实 `User` 目录切换、生产构造开门、E3/E4/E5、热键、R5.8 签署、实机与生产进程门**继续关闭**；本批**不**声称 R5.6 或 R5 完成。
+- 既有建议级残项 `R56-D1`（提交后快照损坏 + 直接授权交错）**状态未变**；本批未改其触发条件。
+
+### §24.129.6 设施与声明面
+
+- 设施：`workflow.py begin`（开工快照 + 原始矩阵）／`audit --stage review` + `verify`／`audit --stage closeout` + `verify`；索引、`git status --porcelain`、本批未暂存/已暂存 diff 与材料外变更区分随快照保存。工具只做机械核验，`quality_verdict` 恒为 NOT PROVIDED。
+- 本批改动声明面（新增状态/门禁措辞）⇒ 按 §17.4-A 第 1 条 `CLAIM_SURFACE_REGENERATE=1` 再生清单并纳入提交评审，清除变量后复跑守卫。
+- 会诊：本子批独立计数（上限 8 次）；逐轮记录见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.129.7。
+
+### §24.129.7 会诊（第 1 轮）与逐项处置（子批计数 1/8）
+
+- **渠道/模型/强度**：既有 GPT 只读会诊工具（自动附本批 `git status` 与 staged/unstaged diff）；`gpt-6-astra`／`medium`；attempts=1。请求与预检见 `consultation/review-request-v1.md`、`consultation/preflight-v1.json`（10 个白名单文件；估计输入约 28 万字节 ≈ 7 万 token，未触发本地回退条件）。
+- **结论原文要点**：「**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收 A 项**」——**MUST 5 项 + IMPORTANT 4 项 + 建议级 1 项**。报告原文见 `consultation/review-round1-report.md`。
+- **逐项处置（全部按原级采纳，同一修复批；每条均新增夹具并绑定有效突变）**：
+
+| # | 原级 | 会诊要点 | 处置（修复 + 反例 + 突变） |
+|---|---|---|---|
+| 1 | MUST | 激活会把真实引用漂移**重新登记为合法状态**（同步写集哈希时吸收漂移），随后允许提交 | `ActivateCandidate` 在副作用前核对**盘上字节 == 已确认写集哈希**；漂移即 `activation_precondition_drifted` 置 Blocked、零副作用、写集不被污染（`Activation_AfterDrift_IsNotAbsorbed` / M19） |
+| 2 | MUST | 声明为 Added 但**从未由事务创建**的文件会被回滚删除；且 Added 允许覆盖他方文件 | 新增目标写入改为 `overwrite:false`（竞争窗口内被创建即拒绝，不覆盖）；回滚在真实写入路径要求**归属证据**（字节必须等于本事务所写），否则保留并 `rollback_addition_not_owned`／`rollback_addition_without_ownership_evidence` 阻断（`ForeignAddedFile_IsPreservedAndRollbackBlocks`、`AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback` / M22、M28） |
+| 3 | MUST | 写集外**新增文件**完全漏检（只比较基线清单） | 新增「配置根当前文件集合 == 基线 ∪ 声明新增」核对，更新面与提交面都执行；不符即 `unexpected_new_file_outside_writeset` 阻断（`NewFileOutsideWriteset_Blocks`、`CommitRejectsNewFilesOutsideWriteset` / M20） |
+| 4 | MUST | manifest 真实证据要求可通过改字段 + 重算摘要绕过；缺写集↔登记、激活哈希↔写集等关系不变量 | 提交/授权门槛改为**绑定本实例**（`realEffectsRequired \|\| _effects is not null`），并把**证据关系不变量**（写集键集合恰等于非删除登记、身份唯一、激活哈希等于写集哈希）纳入结构校验（`ManifestTamper_WithoutRealEvidence_IsRejected` 六个分支，含「降级并重算摘要」支 / M23、M14） |
+| 5 | MUST | 回滚读回检查会**空过**（写集在部分写后为空），可能假报完整回滚 | 回滚后改为核对**完整基线字节集** + 全部「新增」必须不存在（与写集无关），不符即 Blocked，绝不报告 `RolledBack`（`Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet` / M21） |
+| 6 | IMPORTANT | 确认写集后仍能改变更登记，破坏精确写集约束 | `RecordChanges` 在写集非空时拒绝（`change_registry_frozen_after_reference_update`）（`ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate` / M24） |
+| 7 | IMPORTANT | 激活未限定 `candidate-ready→active`；且已等于目标态时零写入「成功」并记录未经证实的前置状态 | 只接受权威 D13 转换（其余 `unsupported_activation_transition`）；副作用前**观测盘上现值**，已生效即 `activation_already_applied`、与声明 before 不符即 `activation_precondition_status_mismatch`（`Activation_RejectsAlreadyAppliedAndForeignTransitions` / M26、M27） |
+| 8 | IMPORTANT | 副作用/读回抛异常未收敛为 Blocked，允许重复执行；敌意文档形状可抛异常 | 端口调用与全部读回均 try/catch 收敛为 `*_exception_unknown` 置 Blocked（不重复执行）；引用改写对非对象节点/非对象 `ref` 稳定转结构化拒绝，不写回（`EffectPortExceptions_BecomeBlockedWithoutRepeat`、`HostileDocumentShape_IsRejectedNotThrown` / M25） |
+| 9 | IMPORTANT | 关键夹具存在被遮蔽断言与「不证明撤销」的回滚断言 | 篡改测试每分支改为从**合法 manifest 独立副本**出发（并新增降级/多余写集/哈希不符三支）；「副作用后取消」改为真实部分写入后取消；新增「回滚确实调用真实激活撤销」夹具（端口请求序列）与同实例并发提交/回滚不变量夹具；突变补至 28 项并附**补丁原文 + 三段判定**（`mutations/summary.md`）（M18、M15、M16、M20） |
+| 10 | 建议 | 演练报告文案超出证据 | `MigrationRehearsal` 步骤名与类注释改为如实表述（该入口**未注入真实副作用端口**，两步为阶段标记演练）（`FixedRehearsal` 文案；R58 夹具 14/14 复跑通过） |
+
+- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。**存在未闭合 MUST/IMPORTANT ⇒ 按纪律 R2 必须复会诊**（第 2 轮为验证轮）。
+- **边界**：会诊只审阅所附代码与 diff，未独立核验原始 TRX/突变日志与运行行为；本批据此不主张无条件完整验收。B–F 与真实 User/生产门未作为本批缺陷要求补齐，仍关闭。
+
+### §24.129.8 会诊（第 2 轮，验证轮）与逐项处置（子批计数 2/8）
+
+- **渠道/模型/强度**：既有 GPT 只读会诊工具；`gpt-6-astra`／medium；attempts=1（预检见 `consultation/preflight-v2.json`，白名单 10 文件 + 自动附加 diff）。
+- **结论**：第 1 轮 9 项中 **已闭环 4 项**（MUST-3 原反例、MUST-5、IMPORTANT-6、IMPORTANT-7 原反例），**仍未闭环 5 项**（MUST-1 版本绑定、MUST-2 归属保护、MUST-4 关系不变量残余、IMPORTANT-8 异常边界、IMPORTANT-9 夹具/突变台账），并新报 **4 项 MUST + 4 项 IMPORTANT**。报告原文见 `consultation/review-round2-report.md`。
+- **逐项处置（全部原级采纳，同一修复批；均新增夹具并绑定有效突变）**：
+
+| # | 原级 | 会诊要点 | 处置（修复 + 反例 + 突变） |
+|---|---|---|---|
+| 1 | MUST | 新增文件作激活目标时，撤销激活**先写后查**，正常回滚必然归属冲突、且可能改写他方文件 | 归属预检**先于任何写入**（先判不存在、再判证据），并在撤销/恢复之后按**预核归属集合**清理新增（`Rollback_AddedActivationTarget_ConvergesAndRemovesIt`、`Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten`／M29、M30） |
+| 2 | MUST | 回滚归属保护仍可通过**降级 manifest 标记**绕过 | 归属判据同样绑定本实例（`realEffectsRequired \|\| _effects is not null`）（`DowngradedManifestFlag_DoesNotBypassRollbackOwnership`；**判别力未由突变证明**，见下方残项） |
+| 3 | MUST | 激活未绑定**写入所依据的版本**（前置检查与端口写入之间仍可漂移） | 激活请求新增 `ExpectedContentHash`，端口在写入前核对盘上字节哈希（两处），不符即 `activation_content_hash_mismatch` 置 Blocked（`Activation_VersionBinding_RejectsDriftAfterPrecheck`／M31） |
+| 4 | MUST | 提交面文件集合核对**不是相等检查**（缺文件漏检） | 补齐「基线文件必须全部存在」一半 ⇒ `missing_baseline_file` 拒绝提交（`Commit_RejectsMissingBaselineFile`／M32） |
+| 5 | MUST | 变更登记**重复身份**可由 `HashSet` 折叠绕过 | 结构关系不变量新增身份唯一性检查（`evidence_relation:duplicate_change_identity`）（`ManifestTamper_…` 新增分支／M34） |
+| 6 | IMPORTANT | 只快照+登记 Added、尚未写入即中止的事务被**永久阻断** | 归属预检**先判不存在**（不存在无需证据），中止事务可安全回滚（`AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely`／M30） |
+| 7 | IMPORTANT | 异常边界仍遗漏复核与回滚读回路径 | `RecheckActivationRecord`、撤销后读回、`TryHashConfigFile` 的安全检查均纳入异常边界（收敛为拒绝/Blocked）（`EffectPortExceptions_…` 覆盖正向路径；M25） |
+| 8 | IMPORTANT | 同实例 monitor 可重入 ⇒ 端口回调内重入可覆盖已完成回滚 | 新增 `_effectCallInProgress` **重入守卫**（覆盖全部变更入口）并在副作用返回后核对阶段未被改变（`ReentrantMutationFromEffectCallback_IsRejected`／M33） |
+| 9 | IMPORTANT | 夹具与突变台账仍不一致（取消后未真实写入、M8 与 M6 重复、M22 判别力失真、矩阵残留说明、findings 记旧计数） | 「副作用后取消」改为**真实部分写入**；M8 重定义为「激活阶段前置门」并与 M6 去重；新增 M30（整块去除归属保护）以证明「他方文件被删除」可被检出；矩阵清除陈旧说明；`findings.md` 与本节统一为 33 项突变口径（`ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks` 等） |
+
+- **计数**：本轮后子批累计 **2/8**（无失败/超时请求）。仍有未闭合 MUST/IMPORTANT ⇒ 继续复会诊（第 3 轮）。
+- **本批残项（如实）**：`REF-C4`（同实例并发提交/回滚）与「降级 manifest 标记不得绕过归属保护」两条，**未取得可杀死对应夹具的单点突变**（尝试记录见 §24.129.8 表格第 2 行与矩阵 `REF-C4.reason`）：前者在两种获胜顺序下不变量均成立，后者以「判据只依赖标记」构造的探测突变仍使夹具通过。二者保留**夹具级反例**但**不主张突变判别力**，不计入「关键断言均已突变验证」的陈述。
+- **边界**：会诊只审阅所附材料与 diff；B–F、生产接线与 owner 检查点不作为本批缺陷，仍关闭。
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs
index b84940d23..f556ae513 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs
@@ -35,6 +35,9 @@ public sealed class MigrationRehearsalReport
 
 /// <summary>
 /// **R5.6/R5.8「迁移演练」入口（§21.4）**：在**独立配置根**上跑完整切换事务（不接触真实 User 目录）并产出报告。
+/// **能力边界（如实）**：本入口使用**未注入真实副作用端口**的事务框架，其「引用更新／激活」两步为**阶段标记演练**
+/// （`MarkReferenceUpdateCompleted`／`MarkActivated`）；真实引用写入与 `candidate-ready → active` 写入由
+/// `MigrationSwitchTransaction.ApplyReferenceUpdate`／`ActivateCandidate` 承担（见 R5.6 A 项）。
 /// **语义**：①**只接受独立根**——若配置根等于真实 User 目录（调用方传入的 `userConfigRoot`）⇒ 直接拒绝；
 /// ②演练＝快照→（无迁移变更的）引用更新→激活→**回滚演练**→提交→**真实回滚**→逐字节比对；
 /// ③**不授权生产开门**：本入口不执行真实 User 目录切换（须 owner 另行下令）。
@@ -124,10 +127,12 @@ public static class MigrationRehearsal
                 tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]));
             steps.Add(changes);
             if (!changes.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
-            var refUpd = Step("引用更新完成", tx.MarkReferenceUpdateCompleted());
+            var refUpd = Step("引用更新阶段标记（无真实副作用端口）", tx.MarkReferenceUpdateCompleted());
             steps.Add(refUpd);
             if (!refUpd.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
-            var activated = Step("激活（candidate→active）", tx.MarkActivated());
+            // **如实命名（R5.6 A 项第 1 轮会诊建议级）**：本入口**未注入真实副作用端口**，
+            // 该步是**阶段标记演练**，不代表真实的 `candidate-ready → active` 写入（真实激活接线见迁移事务的真实副作用入口）。
+            var activated = Step("激活阶段标记（无真实副作用端口；真实激活见 R5.6 A 项）", tx.MarkActivated());
             steps.Add(activated);
             if (!activated.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
             var rehearsal = Step("回滚演练（复用真实回滚核心）", tx.RehearseRollback());
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs
index 7beffd638..5ea12b50d 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs
@@ -65,6 +65,31 @@ public sealed class MigrationManifest
     /// <summary>静止窗口**代次**（释放即失效；同实例重新取锁不得复用历史资格）。</summary>
     [JsonPropertyName("quiesceGeneration")] public int QuiesceGeneration { get; set; }
     [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
+    /// <summary>
+    /// **真实引用写入的读回证据**（本事务写集声明；`path → 写入后盘上字节 SHA256`；大小写不敏感身份）。
+    /// 只有**逐项读回确认**后才写入；空表示尚未完成真实引用更新。
+    /// </summary>
+    [JsonPropertyName("referenceWriteSet")] public Dictionary<string, string> ReferenceWriteSet { get; set; } = new(StringComparer.Ordinal);
+    /// <summary>
+    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
+    /// </summary>
+    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }
+    /// <summary>
+    /// **本事务是否由真实副作用端口建立**（`BeginTransaction` 时按实例是否注入端口写入；受完整性摘要覆盖）。
+    /// `true` ⇒ 阶段 `ReferenceUpdating/Activated/Committed` 必须携带真实写集/激活读回证据，且阶段标记只能由
+    /// 真实副作用 + 读回确认推进；入口回绝 `MarkReferenceUpdateCompleted`/`MarkActivated`。
+    /// `false` ⇒ 仅限未注入端口的只读夹具（旧行为保留）。
+    /// </summary>
+    [JsonPropertyName("realEffectsRequired")] public bool RealEffectsRequired { get; set; }
+}
+
+/// <summary>真实激活读回证据（D13：`candidate → active`）。</summary>
+public sealed class MigrationActivationRecord
+{
+    [JsonPropertyName("path")] public string Path { get; set; } = "";
+    [JsonPropertyName("beforeStatus")] public string BeforeStatus { get; set; } = "";
+    [JsonPropertyName("afterStatus")] public string AfterStatus { get; set; } = "";
+    [JsonPropertyName("afterHash")] public string AfterHash { get; set; } = "";
 }
 
 /// <summary>事务操作结果。</summary>
@@ -92,16 +117,20 @@ public sealed class MigrationSwitchTransaction : IDisposable
     private readonly bool _requireQuiescence;
     private readonly Action<MigrationStage>? _stageHook;
     private readonly Action<string>? _fileRestoredHook;
+    private readonly IMigrationEffectService? _effects;
     private readonly object _sync = new();
     private readonly string _sessionId = Guid.NewGuid().ToString("N");
     private FileStream? _lock;
+    /// <summary>**重入守卫**：外部副作用/读回回调执行期间置位；此期间任何变更入口一律拒绝（monitor 可重入，
+    /// 单靠 `lock` 不能阻止回调同步重入事务，见会诊第 2 轮 IMPORTANT-7）。</summary>
+    private bool _effectCallInProgress;
     private IDisposable? _quiet;
     private bool _quietValid;
     private int _quietGeneration;
 
     public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
         Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
-        Action<string>? fileRestoredHook = null)
+        Action<string>? fileRestoredHook = null, IMigrationEffectService? effectService = null)
     {
         _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
         _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
@@ -110,9 +139,14 @@ public sealed class MigrationSwitchTransaction : IDisposable
         _requireQuiescence = requireQuiescence;
         _stageHook = stageHook;
         _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
+        _effects = effectService;               // **真实副作用端口**：null ⇒ 本实例只能演练阶段推进（旧语义仅保留给只读夹具）
         ValidateRoots();
     }
 
+    /// <summary>权威 D13 激活词（R5.2 §21.2：`candidate → active`）；仅本事务的激活入口使用，不作通用状态改写器。</summary>
+    internal const string CandidateReadyStatus = "candidate-ready";
+    internal const string ActiveStatus = "active";
+
     public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");
 
     /// <summary>事务号**占用历史**（追加式；重复事务号一律拒绝——不依赖当前 manifest 是否仍在）。</summary>
@@ -294,6 +328,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (HoldsExclusiveLock) return MigrationResult.Ok(MigrationStage.None);
             Directory.CreateDirectory(_transactionRoot);
             try
@@ -314,6 +349,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!IsSafeTransactionId(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
             if (!HoldsExclusiveLock)
             {
@@ -353,6 +389,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
                 QuiescedAtUtc = _quiet is null ? null : _utcNow(),
                 QuiesceSessionId = _quiet is null ? null : _sessionId,
                 QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
+                RealEffectsRequired = _effects is not null,
             };
             // **先持久化占号、再发布 manifest**（占号失败/崩溃也保守占号 ⇒ 事务号不复用；不依赖窗口是否存在）。
             File.AppendAllText(HistoryPath, transactionId + Environment.NewLine);
@@ -365,6 +402,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
@@ -420,12 +458,17 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
             if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                 return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
+            // **确认引用更新后冻结登记（会诊 IMPORTANT-6）**：真实写集一旦确认，变更登记即为已核验证据的一部分；
+            // 此后改登记（新增/改写归属）会使「声明写集 ↔ 变更登记」一致性失效，一律拒绝，须重新开事务。
+            if (m.ReferenceWriteSet.Count > 0)
+                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);
 
             var batch = new List<ChangeRecord>();
             foreach (var c in changes ?? [])
@@ -492,8 +535,404 @@ public sealed class MigrationSwitchTransaction : IDisposable
         }
     }
 
-    public MigrationResult MarkReferenceUpdateCompleted() => Advance(MigrationStage.ReferenceUpdating);
-    public MigrationResult MarkActivated() => Advance(MigrationStage.Activated);
+    /// <summary>
+    /// **阶段推进旧语义（仅限未接真实副作用的只读夹具）**：已注入真实副作用端口后**必须**用
+    /// <see cref="ApplyReferenceUpdate"/>；否则本方法会在零写入的情况下把阶段推进到 `ReferenceUpdating`，
+    /// 使 `RehearseRollback`/`Commit` 全部通过而配置根**从未发生真实引用更新**（假成功）。此门禁只在
+    /// 生产/真实事务上生效，不改变旧夹具（`effectService: null`）的行为。
+    /// </summary>
+    public MigrationResult MarkReferenceUpdateCompleted()
+        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)
+                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
+    /// <summary>阶段推进旧语义（同 <see cref="MarkReferenceUpdateCompleted"/>：注入真实副作用端口后一律拒绝）。</summary>
+    public MigrationResult MarkActivated()
+        => _effects is null ? Advance(MigrationStage.Activated)
+                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
+
+    /// <summary>
+    /// **真实引用更新（R5.6 A 项）**：声明写集 → 真实副作用 → **逐项读回确认** → **才**持久化阶段与写集证据。
+    /// 拒绝/未知/取消/读回不符一律不推进阶段：未知与读回不符置 `Blocked`（fail-closed、不盲目重试）；
+    /// 副作用前取消保持当前阶段（可重试/可回滚）；已到本阶段时幂等重读盘复核，**不二次触发副作用**。
+    /// </summary>
+    public MigrationResult ApplyReferenceUpdate(MigrationReferenceUpdatePlan plan)
+    {
+        lock (_sync)
+        {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
+            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
+            var m = LoadValidated();
+            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
+
+            if (m.Stage == MigrationStage.ReferenceUpdating)
+                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入（不抛异常）
+
+            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
+            if (!IsLegalAdvance(m.Stage, MigrationStage.ReferenceUpdating))
+                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->ReferenceUpdating", m.Stage);
+            if (_requireQuiescence && (!_quietValid || _quiet is null))
+                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 真实写入必须在**存续**窗口内
+            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);
+
+            var stageBeforeEffect = m.Stage;
+            MigrationEffectResult result;
+            try
+            {
+                _effectCallInProgress = true;
+                result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
+            }
+            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
+            {
+                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
+            }
+            finally
+            {
+                _effectCallInProgress = false;
+            }
+            if (CurrentStageOrNone() != stageBeforeEffect)
+                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());
+            if (result.Outcome != MigrationEffectOutcome.Succeeded)
+            {
+                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
+                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
+                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
+                    + result.Reason + ";writes=" + result.CompletedWrites);
+            }
+
+            // **读回确认先于阶段推进**：语义读回（引用已改写）+ 字节读回（写入后哈希）
+            var writeSet = new Dictionary<string, string>(StringComparer.Ordinal);
+            foreach (var target in plan!.Targets)
+            {
+                try
+                {
+                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
+                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
+                }
+                catch (Exception ex)
+                {
+                    return MarkBlocked("reference_readback_exception:" + target.Path + ":" + ex.GetType().Name);
+                }
+                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
+                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
+                writeSet[target.Path] = hash;
+            }
+            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);
+            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）
+            foreach (var baseline in m.FileHashes)
+            {
+                if (writeSet.Keys.Any(k => PathKey(k) == PathKey(baseline.Key))) continue;
+                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
+                {
+                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
+                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
+                }
+                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
+                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
+            }
+            m.ReferenceWriteSet = writeSet;
+            m.Stage = MigrationStage.ReferenceUpdating;
+            WriteManifest(m);
+            return MigrationResult.Ok(m.Stage);
+        }
+    }
+
+    /// <summary>
+    /// **真实激活（R5.6 A 项：D13 `candidate → active`）**：须已完成真实引用更新；目标必须是**已确认写集内**的文件；
+    /// 副作用成功且状态读回一致后才推进到 `Activated`。已到 `Activated` 时幂等重读复核（不二次写入）。
+    /// </summary>
+    public MigrationResult ActivateCandidate(MigrationActivationRequest request)
+    {
+        lock (_sync)
+        {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
+            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
+            var m = LoadValidated();
+            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
+
+            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**
+
+            if (m.Stage != MigrationStage.ReferenceUpdating)
+                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);
+            if (ValidateActivationRequest(m, request) is { } requestProblem) return MarkBlocked(requestProblem);
+            if (_requireQuiescence && (!_quietValid || _quiet is null))
+                return MigrationResult.Fail("no_quiescence_window", m.Stage);
+            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);
+
+            // **激活前的字节版本核对（会诊 MUST-1）**：激活必须基于**已确认写集**所记录的那一份字节；
+            // 若该文件自引用更新确认后已被锁外改动，则激活会把它「连同漂移一起合法化」——此处一律 fail-closed。
+            if (!TryGetWriteSetHash(m, request!.Path, out var confirmedHash))
+                return MarkBlocked("activation_writeset_hash_missing:" + request.Path);
+            if (!TryHashConfigFile(request.Path, out var preHash, out var preProblem))
+                return MarkBlocked("activation_precondition_" + preProblem + ":" + request.Path);
+            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))
+                return MarkBlocked("activation_precondition_drifted:" + request.Path);
+
+            // **前置状态观测（会诊 IMPORTANT-7）**：盘上现值必须**恰为**声明的 before 状态；已等于目标态 ⇒ 拒绝，
+            // 不得凭「已生效」零写入成功（幂等复核只经由已持久化的 `Activated` 阶段证据）。
+            string observed;
+            string observeDetail;
+            try
+            {
+                if (string.IsNullOrEmpty(request.ExpectedContentHash))
+                return MarkBlocked("activation_request_invalid:missing_content_hash:" + request.Path);
+            if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out observed, out observeDetail))
+                    return MarkBlocked("activation_precondition_status_unreadable:" + request.Path + ":" + observeDetail);
+            }
+            catch (Exception ex)
+            {
+                return MarkBlocked("activation_precondition_exception:" + request.Path + ":" + ex.GetType().Name);
+            }
+            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
+                return MarkBlocked("activation_already_applied:" + request.Path);
+            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
+                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);
+
+            var stageBeforeActivation = m.Stage;
+            MigrationEffectResult result;
+            try
+            {
+                _effectCallInProgress = true;
+                result = _effects.Activate(_configRoot, request);
+            }
+            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
+            {
+                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
+            }
+            finally
+            {
+                _effectCallInProgress = false;
+            }
+            if (CurrentStageOrNone() != stageBeforeActivation)
+                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeActivation + "->" + CurrentStageOrNone());
+            if (result.Outcome != MigrationEffectOutcome.Succeeded)
+            {
+                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
+                    return MigrationResult.Fail("activation_cancelled_before_effects:" + result.Reason, m.Stage);
+                return MarkBlocked("activation_" + result.Outcome.ToString().ToLowerInvariant() + ":"
+                    + result.Reason + ";writes=" + result.CompletedWrites);
+            }
+
+            try
+            {
+                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)
+                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
+                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
+            }
+            catch (Exception ex)
+            {
+                return MarkBlocked("activation_readback_exception:" + request.Path + ":" + ex.GetType().Name);
+            }
+            if (!TryHashConfigFile(request.Path, out var hash, out var hashProblem))
+                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);
+
+            m.ActivationRecord = new MigrationActivationRecord
+            {
+                Path = request.Path,
+                BeforeStatus = request.ExpectedBeforeStatus,
+                AfterStatus = request.TargetStatus,
+                AfterHash = hash,
+            };
+            // 写集记录的是「本事务写过的文件的**当前期望盘上状态**」：激活同样改写了该文件，故须同步更新其哈希，
+            // 否则提交前的写集复核会把本事务自己的激活写入误判为漂移。
+            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
+                m.ReferenceWriteSet[key] = hash;
+            m.Stage = MigrationStage.Activated;
+            WriteManifest(m);
+            return MigrationResult.Ok(m.Stage);
+        }
+    }
+
+    /// <summary>幂等复核：写集内每个文件仍在盘上且哈希与已确认写集一致（不触发副作用）。</summary>
+    private MigrationResult RecheckReferenceWriteSet(MigrationManifest m)
+    {
+        if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_write_set_empty", m.Stage);
+        foreach (var entry in m.ReferenceWriteSet)
+        {
+            if (!TryHashConfigFile(entry.Key, out var hash, out var problem))
+                return MigrationResult.Fail("reference_recheck_" + problem + ":" + entry.Key, m.Stage);
+            if (!string.Equals(hash, entry.Value, StringComparison.Ordinal))
+                return MigrationResult.Fail("reference_recheck_hash_mismatch:" + entry.Key, m.Stage);
+        }
+        return MigrationResult.Ok(m.Stage);
+    }
+
+    /// <summary>幂等复核：激活记录仍在盘上（哈希一致）且状态仍为目标状态（不触发副作用）。</summary>
+    private MigrationResult RecheckActivationRecord(MigrationManifest m)
+    {
+        var record = m.ActivationRecord;
+        if (record is null) return MigrationResult.Fail("activation_record_missing", m.Stage);
+        if (!TryHashConfigFile(record.Path, out var hash, out var problem))
+            return MigrationResult.Fail("activation_recheck_" + problem + ":" + record.Path, m.Stage);
+        if (!string.Equals(hash, record.AfterHash, StringComparison.Ordinal))
+            return MigrationResult.Fail("activation_recheck_hash_mismatch:" + record.Path, m.Stage);
+        if (_effects is null) return MigrationResult.Ok(m.Stage);
+        try
+        {
+            if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var status, out var detail)
+                || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
+                return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
+        }
+        catch (Exception ex)      // 复核期异常同样收敛（会诊第 2 轮 IMPORTANT-6）
+        {
+            return MigrationResult.Fail("activation_recheck_exception:" + record.Path + ":" + ex.GetType().Name, m.Stage);
+        }
+        return MigrationResult.Ok(m.Stage);
+    }
+
+    /// <summary>写集声明校验：非空、路径安全、不重复、逐项等于**已登记变更归属**且无漏项/多项（精确写集）。</summary>
+    private static string? ValidateReferencePlan(MigrationManifest m, MigrationReferenceUpdatePlan? plan)
+    {
+        if (plan?.Targets is null || plan.Targets.Count == 0) return "reference_writeset_mismatch:plan_empty";
+        var seen = new HashSet<string>(StringComparer.Ordinal);
+        foreach (var target in plan.Targets)
+        {
+            if (target is null) return "reference_writeset_mismatch:null_target";
+            if (!IsSafeRelativePath(target.Path)) return "reference_writeset_mismatch:unsafe_path:" + target.Path;
+            if (!seen.Add(PathKey(target.Path))) return "reference_writeset_mismatch:duplicate:" + target.Path;
+            var record = FindChange(m, target.Path);
+            if (record is null) return "reference_writeset_mismatch:not_registered:" + target.Path;
+            if (record.Kind != target.Kind) return "reference_writeset_mismatch:kind:" + target.Path;
+            switch (target.Kind)
+            {
+                case ChangeKind.Added when string.IsNullOrEmpty(target.NewContent):
+                    return "reference_writeset_mismatch:added_without_content:" + target.Path;
+                case ChangeKind.Modified when string.IsNullOrEmpty(target.RenameFrom) || string.IsNullOrEmpty(target.RenameTo)
+                    || string.Equals(target.RenameFrom, target.RenameTo, StringComparison.Ordinal):
+                    return "reference_writeset_mismatch:modified_without_rename:" + target.Path;
+                case ChangeKind.Deleted:
+                    return "reference_writeset_mismatch:deleted_target_unsupported:" + target.Path;
+            }
+        }
+        foreach (var record in m.ChangedFiles)
+        {
+            if (record.Kind == ChangeKind.Deleted)
+                return "reference_writeset_mismatch:deleted_record_unsupported:" + record.Path;
+            if (!seen.Contains(PathKey(record.Path)))
+                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项
+        }
+        return null;
+    }
+
+    /// <summary>激活请求校验：目标必须**已在确认写集内**且为本次真实写入的文件（不得激活写集外目标）。</summary>
+    private static string? ValidateActivationRequest(MigrationManifest m, MigrationActivationRequest? request)
+    {
+        if (request is null) return "activation_request_invalid:null";
+        if (!IsSafeRelativePath(request.Path)) return "activation_request_invalid:unsafe_path:" + request.Path;
+        if (string.IsNullOrEmpty(request.ExpectedBeforeStatus) || string.IsNullOrEmpty(request.TargetStatus))
+            return "activation_request_invalid:status_missing";
+        if (string.Equals(request.ExpectedBeforeStatus, request.TargetStatus, StringComparison.Ordinal))
+            return "activation_request_invalid:no_state_change";
+        // **只接受权威的 D13 转换**（R5.2 §21.2：激活＝`candidate → active`）：本事务不发明通用状态改写器；
+        // 其余状态对一律拒绝（回滚的撤销路径不经此入口，直接用端口）。
+        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
+            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
+            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;
+        if (m.ReferenceWriteSet.Count == 0) return "activation_request_invalid:reference_write_set_empty";
+        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
+            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4
+        var record = FindChange(m, request.Path);
+        if (record is null || record.Kind == ChangeKind.Deleted)
+            return "activation_target_not_a_written_file:" + request.Path;                    // REF-F4
+        return null;
+    }
+
+    /// <summary>
+    /// **证据关系不变量（会诊 MUST-4）**：真实证据之间必须自洽——写集键集合**恰等于**变更登记中非删除项、
+    /// 身份键唯一、激活记录的盘上哈希必须等于写集中该文件的哈希。任一不符 ⇒ 拒绝（返回原因码）。
+    /// </summary>
+    private static string? EvidenceRelationProblem(MigrationManifest m)
+    {
+        var writeSetKeys = new HashSet<string>(StringComparer.Ordinal);
+        foreach (var key in m.ReferenceWriteSet.Keys)
+            if (!writeSetKeys.Add(PathKey(key))) return "evidence_relation:duplicate_writeset_key:" + key;
+        var changeIdentities = new HashSet<string>(StringComparer.Ordinal);
+        foreach (var change in m.ChangedFiles)
+            if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;
+        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);
+        if (!writeSetKeys.SetEquals(registered)) return "evidence_relation:writeset_registry_mismatch";
+        if (m.ActivationRecord is { } activation)
+        {
+            if (!TryGetWriteSetHash(m, activation.Path, out var activationHash))
+                return "evidence_relation:activation_not_in_writeset:" + activation.Path;
+            if (!string.Equals(activationHash, activation.AfterHash, StringComparison.Ordinal))
+                return "evidence_relation:activation_hash_mismatch:" + activation.Path;
+        }
+        return null;
+    }
+
+    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>
+    private static bool TryGetWriteSetHash(MigrationManifest m, string rel, out string hash)
+    {
+        hash = "";
+        var key = PathKey(rel);
+        foreach (var entry in m.ReferenceWriteSet)
+        {
+            if (PathKey(entry.Key) != key) continue;
+            hash = entry.Value;
+            return true;
+        }
+        return false;
+    }
+
+    /// <summary>按大小写不敏感身份查变更归属。</summary>
+    private static ChangeRecord? FindChange(MigrationManifest m, string path)
+    {
+        var key = PathKey(path);
+        foreach (var record in m.ChangedFiles) if (PathKey(record.Path) == key) return record;
+        return null;
+    }
+
+    /// <summary>
+    /// **写集外新增文件检测（会诊 MUST-3）**：配置根当前文件集合必须等于「基线 ∪ 声明写集中的新增目标」。
+    /// 只比较基线清单会漏掉「副作用在写集外新建了文件」；本检查补齐该面（静止窗口下无其他写方，新增即本事务产物）。
+    /// 返回 null 表示一致。
+    /// </summary>
+    private string? UnexpectedFileReason(MigrationManifest m, IEnumerable<MigrationReferenceWriteTarget> declared)
+    {
+        List<string> current;
+        try { current = EnumerateFilesSafe(_configRoot).Select(p => PathKey(p)).ToList(); }
+        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
+        {
+            return "config_root_enumeration_failed:" + ex.GetType().Name;
+        }
+        var expected = new HashSet<string>(m.FileHashes.Keys.Select(PathKey), StringComparer.Ordinal);
+        foreach (var target in declared)
+            if (target.Kind == ChangeKind.Added) expected.Add(PathKey(target.Path));
+        foreach (var path in current)
+            if (!expected.Contains(path)) return "unexpected_new_file_outside_writeset:" + path;
+        var present = new HashSet<string>(current, StringComparer.Ordinal);
+        foreach (var baseline in m.FileHashes.Keys)
+            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半
+        return null;
+    }
+
+    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>
+    private bool TryHashConfigFile(string rel, out string hash, out string problem)
+    {
+        hash = "";
+        problem = "unhashable";
+        try
+        {
+            if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
+        }
+        catch (Exception)     // 安全检查自身异常同样收敛为「不可哈希」（会诊第 2 轮 IMPORTANT-6）
+        {
+            problem = "unsafe_target_check_failed";
+            return false;
+        }
+        var full = Path.Combine(_configRoot, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
+        if (!File.Exists(full)) { problem = "file_missing"; return false; }
+        try
+        {
+            hash = Sha256Hex(File.ReadAllBytes(full));
+            problem = "";
+            return true;
+        }
+        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
+        {
+            problem = "read_failed";
+            return false;
+        }
+    }
 
     private MigrationResult Advance(MigrationStage to)
     {
@@ -531,6 +970,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
@@ -582,12 +1022,28 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
             if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
             if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
+            if (m.RealEffectsRequired || _effects is not null)
+            {
+                // **真实事务的提交前置（会诊 MUST-4）**：门槛绑定「本实例是否接入真实副作用」与持久化标记的**并集**，
+                // 故把 `realEffectsRequired` 改成 false 不能降级绕过；引用写入与激活都必须由真实副作用 + 读回确认产生
+                if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);
+                if (m.ActivationRecord is null) return MigrationResult.Fail("activation_not_confirmed", m.Stage);
+                var rechecked = RecheckReferenceWriteSet(m);
+                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
+                var activationRecheck = RecheckActivationRecord(m);
+                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
+                if (EvidenceRelationProblem(m) is { } relationProblem) return MigrationResult.Fail(relationProblem, m.Stage);
+                if (UnexpectedFileReason(m, m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted)
+                        .Select(c => new MigrationReferenceWriteTarget(c.Path, c.Kind)).ToList()) is { } unexpected)
+                    return MigrationResult.Fail(unexpected, m.Stage);
+            }
             if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
                 return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
             if (_requireQuiescence && (!_quietValid || _quiet is null || m.QuiescedAtUtc is null
@@ -609,6 +1065,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
@@ -643,6 +1100,56 @@ public sealed class MigrationSwitchTransaction : IDisposable
         }
     }
 
+    /// <summary>
+    /// **撤销真实激活**：读回当前状态 → 仍为 `AfterStatus` 时施加反向副作用 → **再读回**确认等于 `BeforeStatus`。
+    /// 任一读回失败或状态异常 ⇒ `Blocked`（**绝不**在未确认时报告完整回滚；本事务新增文件已不存在视为旧态）。
+    /// </summary>
+    private MigrationResult UndoActivation(MigrationManifest m, MigrationActivationRecord record)
+    {
+        var kind = FindChange(m, record.Path)?.Kind;
+        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
+        bool readOk;
+        string current;
+        string detail;
+        try
+        {
+            readOk = _effects.TryReadActivationStatus(_configRoot, record.Path, out current, out detail);
+        }
+        catch (Exception ex)
+        {
+            return MarkBlocked("rollback_activation_readback_exception:" + ex.GetType().Name);
+        }
+        if (!readOk)
+        {
+            if (kind == ChangeKind.Added && detail == "target_missing")
+                return MigrationResult.Ok(m.Stage);            // 本事务新增文件已不存在＝旧态（无激活可撤销）
+            return MarkBlocked("rollback_activation_readback_failed:" + record.Path + ":" + detail);
+        }
+        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
+        {
+            MigrationEffectResult undo;
+            try
+            {
+                undo = _effects.Activate(_configRoot,
+                    new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus, record.AfterHash));
+            }
+            catch (Exception ex)
+            {
+                return MarkBlocked("rollback_activation_undo_exception:" + ex.GetType().Name);
+            }
+            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
+                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
+        }
+        else if (!string.Equals(current, record.BeforeStatus, StringComparison.Ordinal))
+        {
+            return MarkBlocked("rollback_activation_state_unexpected:" + record.Path + ":" + current);
+        }
+        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var afterUndo, out var undoDetail)
+            || !string.Equals(afterUndo, record.BeforeStatus, StringComparison.Ordinal))
+            return MarkBlocked("rollback_activation_not_reverted:" + record.Path + ":" + undoDetail);
+        return MigrationResult.Ok(m.Stage);
+    }
+
     /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>
     private MigrationResult CompleteRollback(MigrationManifest m)
     {
@@ -650,9 +1157,58 @@ public sealed class MigrationSwitchTransaction : IDisposable
         {
             try
             {
+                // **先撤销真实激活**（语义前沿先回退），再整份恢复旧字节；两步都须读回确认。
+                // **归属预检必须先于任何写入（会诊第 2 轮 MUST-1）**：撤销激活本身也是写入，
+                // 若目标是他方文件，先写再查会破坏他方内容；故先核对归属，再决定是否允许写入。
+                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
+                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）
+                if (ownerBound)
+                {
+                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
+                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
+                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
+                        ownershipVerified.Add(PathKey(added.Path));
+                }
+                if (m.ActivationRecord is { } activation)
+                {
+                    var undone = UndoActivation(m, activation);
+                    if (!undone.Success) return undone;
+                }
                 RestoreFromSnapshot(m, _configRoot);
-                if (DeleteRecordedAdditions(m, _configRoot) > 0)
+                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)
                     return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
+                // **回滚后旧态一致性（会诊 MUST-5）**：核对**完整基线字节集**（而不是「成功写集」——部分写后
+                // Unknown / 阶段发布前失败时写集为空，只查写集会空过并假报完整回滚）。
+                foreach (var baseline in m.FileHashes)
+                {
+                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))
+                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);
+                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))
+                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);
+                }
+                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
+                {
+                    var target = Path.Combine(_configRoot, NormalizePath(added.Path).Replace('/', Path.DirectorySeparatorChar));
+                    if (File.Exists(target))
+                        return MarkBlocked("rollback_addition_still_present:" + added.Path);
+                }
+                if (m.RealEffectsRequired)
+                {
+                    if (m.ActivationRecord is { } restored)
+                    {
+                        var kind = FindChange(m, restored.Path)?.Kind;
+                        if (kind == ChangeKind.Added)
+                        {
+                            if (File.Exists(Path.Combine(_configRoot, NormalizePath(restored.Path).Replace('/', Path.DirectorySeparatorChar))))
+                                return MarkBlocked("rollback_activation_target_still_present:" + restored.Path);
+                        }
+                        else if (!_effects!.TryReadActivationStatus(_configRoot, restored.Path, out var finalStatus, out var finalDetail)
+                            || !string.Equals(finalStatus, restored.BeforeStatus, StringComparison.Ordinal))
+                        {
+                            return MarkBlocked("rollback_activation_state_after_restore:" + restored.Path + ":" + finalDetail);
+                        }
+                    }
+                }
             }
             catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
             {
@@ -671,6 +1227,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
@@ -714,6 +1271,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     {
         lock (_sync)
         {
+            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
             if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
             var m = LoadValidated();
             if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
@@ -722,6 +1280,9 @@ public sealed class MigrationSwitchTransaction : IDisposable
             if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
                 return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
             if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
+            if ((m.RealEffectsRequired || _effects is not null)
+                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))
+                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);
             return MigrationResult.Ok(m.Stage);
         }
     }
@@ -783,6 +1344,12 @@ public sealed class MigrationSwitchTransaction : IDisposable
         sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
         sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
         sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
+        sb.Append(m.RealEffectsRequired ? '1' : '0').Append('|');
+        foreach (var p in (m.ReferenceWriteSet ?? new Dictionary<string, string>(StringComparer.Ordinal))
+                     .OrderBy(p => p.Key, StringComparer.Ordinal))
+            sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
+        sb.Append('|').Append(m.ActivationRecord is { } ar
+            ? ar.Path + ':' + ar.BeforeStatus + '>' + ar.AfterStatus + ':' + ar.AfterHash : "<null>").Append('|');
         foreach (var c in (m.ChangedFiles ?? []).OrderBy(c => c.Path, StringComparer.Ordinal))
             sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
         sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal)));
@@ -829,6 +1396,25 @@ public sealed class MigrationSwitchTransaction : IDisposable
         if (m.Stage == MigrationStage.RolledBack && (m.RollbackRehearsed || m.RehearsalScope is not null)) return false;
         if (m.RollbackRehearsed && string.IsNullOrEmpty(m.RehearsalScope)) return false;
         if (m.BlockedReason is { Length: 0 }) return false;
+        if (m.ReferenceWriteSet is null) return false;
+        foreach (var entry in m.ReferenceWriteSet)
+            if (!IsSafeRelativePath(entry.Key) || string.IsNullOrEmpty(entry.Value)) return false;
+        if (m.ActivationRecord is { } activation)
+        {
+            if (!IsSafeRelativePath(activation.Path)) return false;
+            if (string.IsNullOrEmpty(activation.BeforeStatus) || string.IsNullOrEmpty(activation.AfterStatus)
+                || string.IsNullOrEmpty(activation.AfterHash)) return false;
+            if (string.Equals(activation.BeforeStatus, activation.AfterStatus, StringComparison.Ordinal)) return false;
+            if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(activation.Path))) return false;   // 激活目标须在写集内
+        }
+        if (m.RealEffectsRequired)
+        {
+            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
+                && m.ReferenceWriteSet.Count == 0) return false;
+            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
+        }
+        // **证据关系（会诊 MUST-4）**：只要出现真实证据，写集必须与变更登记精确对应、激活哈希必须等于写集哈希。
+        if (m.ReferenceWriteSet.Count > 0 && EvidenceRelationProblem(m) is not null) return false;
         return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);
     }
 
@@ -932,7 +1518,7 @@ public sealed class MigrationSwitchTransaction : IDisposable
     /// 只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。返回**失败条数**——
     /// 不安全目标或删除失败**不得静默跳过**：调用方据此保持阻断（不得报告完整回滚）。
     /// </summary>
-    private static int DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
+    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, HashSet<string>? ownershipVerified = null)
     {
         var failed = 0;
         foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
@@ -941,12 +1527,47 @@ public sealed class MigrationSwitchTransaction : IDisposable
             var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
             if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
             if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
+            if (ownershipVerified is not null)
+            {
+                // 归属已在**写入之前**核对并登记（见 CompleteRollback）；此后本事务自己的撤销会改变字节，
+                // 故此处不再按（已失效的）写集哈希复核，只按预先核验的归属集合决定是否删除。
+                if (!ownershipVerified.Contains(PathKey(c.Path))) { failed++; continue; }
+            }
             try { File.Delete(target); }
             catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
         }
         return failed;
     }
 
+    /// <summary>归属冲突预检：真实写入路径下，任何「新增」文件若不存在或字节不等于本事务所写 ⇒ 冲突（不删除）。</summary>
+    private string? OwnershipConflictReason(MigrationManifest m)
+    {
+        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
+        {
+            if (!TryHashConfigFile(c.Path, out var currentHash, out var problem))
+            {
+                // **先判不存在**：目标不存在 ⇒ 无需归属证据（否则「只快照+登记、尚未写入」的合法中止会被永久阻断）
+                if (problem == "file_missing") continue;
+                return "rollback_addition_unverifiable:" + c.Path + ":" + problem;
+            }
+            if (!TryGetWriteSetHash(m, c.Path, out var owned))
+                return "rollback_addition_without_ownership_evidence:" + c.Path;   // 存在但无证据 ⇒ 保留并阻断
+            if (!string.Equals(currentHash, owned, StringComparison.Ordinal))
+                return "rollback_addition_not_owned:" + c.Path;       // 他方文件/被改动 ⇒ 保留并阻断
+        }
+        return null;
+    }
+
+    /// <summary>指定根下文件的 SHA-256（用于演练副本的归属核对；不做真实根绑定）。</summary>
+    private static bool TryHashConfigFileOnRoot(string root, string rel, out string hash)
+    {
+        hash = "";
+        var full = Path.Combine(root, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
+        if (!File.Exists(full)) return false;
+        try { hash = Sha256Hex(File.ReadAllBytes(full)); return true; }
+        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
+    }
+
     /// <summary>释放实际窗口并**使资格失效**（代次前进 ⇒ 历史时间戳/会话不可复用）。</summary>
     private void ReleaseQuiescence()
     {
@@ -1004,6 +1625,13 @@ public sealed class MigrationSwitchTransaction : IDisposable
         return result;
     }
     private static string Rel(string file, string root) => Path.GetRelativePath(root, file).Replace('\\', '/');
+    /// <summary>当前持久化阶段（读取失败返回 None；用于副作用返回后核对未被重入改变）。</summary>
+    private MigrationStage CurrentStageOrNone()
+    {
+        try { return LoadManifest()?.Stage ?? MigrationStage.None; }
+        catch (Exception) { return MigrationStage.None; }
+    }
+
     private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
 
     public void Dispose()
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
index 770211487..6eb0a48c0 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
@@ -185,6 +185,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0967C06309A2CA
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0C46AC4BF63BF9DFE6AFB78F273B624045DF71CCE3B5A45556AED4AF4F2000791新增助手夹具把真实 MapAdmissionResultToBoundary 接入 ArbitrationWorkflowExecutionBoundary 和 WorkflowRunner，并读回 RunStore/LocalWaitQueueStore 持久化状态；另从 facade 外部状态映射进入 Comman…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0CABAECF7384AAA0DCF8B0ABC4416F6AD3F2C361E2ECF592292D4B616AE8CA0E1但因其暴露了本节的语义问题，**夹具待裁定后按实际语义重写**再交付（当前**不计为已完成**）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0EACDC94623BA4C0691A75087C8B0DBE54695FBFBE6ED27B2CC983188BCA20E61| `EarlyAccepted(jobId, CompletionTask)` | **`Accepted(jobId)`**（唯一映射；不得再产出 `Rejected`/`Unknown`） | 由 `CompletionTask` 稍后交付或**已完成**：`Succeeded`／`Cancelled`／`Exe…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0ECA16DFE00D7C304AAD237765B0A219F16C96FF6A36FF261EA0E6654150D9491- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。**存在未闭合 MUST/IMPORTANT ⇒ 按纪律 R2 必须复会诊**（第 2 轮为验证轮）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md0F09B0AEF599DE1715181231D077954AFCB254D3CFDF6A76165CC896348D0E9C1本节以 owner 已确定的业务行为为输入，对 §24.71 以前未给出可执行答案的交接场景明确施工规则；状态仍为**设计提案、未验收**。不改变旧候选队列 `ArbitrationOrdering` 的已冻结全序，也不把测试接线态写成生产态。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md10363AB17A85ADABBFDB6CB44A3E9FA5BC585F0C43A57469DC0292AF24CE55E91| 七轮 | N24-09 | 阻断 | 冲突证据的占用／恢复／裁决未闭合 | §24.2-2″、§24.12-3、§24.15 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md11C383BAE74A594C53B37755BE8F2F8EBD8F975A1F83AA0B99182198F99AE3251**施工与证据**：`71f1d90ca` 已按上述单转移落地；反例先以缺 `ExpectedStopVersion` 编译红，再在 `Sync` 内比较后转绿。`TaskTakeoverIncidentTests` **12/12**；BGI 全量 **945 通过／14 失败／959**，失败身份与既有 14 项基…
@@ -261,9 +262,11 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md46109D24616054
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4767545B507F9D486597484CDC88627D7FF84ACD74ADCD3187C4BE82305F991D1`P@1/P@2` 仍有效停驻 ⇒ `RecomputeSuccessor` 取 `candidate=P@0`、`rescue=A@1`（`P@1` 同轮前插的从未执行出现），
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md47DE23B389A84E70C7FF1C4D17EB7B8E94BFA9B371314EB4F3A29774FC09CD241| **Interrupted 恢复（E2）** | ✅ **有来源继承**（P28 正/负向：逐字一致／纪元变化 ⇒ `stale_epoch` 终局拒绝） | ❌ **未实现** | ✅ S8a（生产已接线） |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md48761B88B0E2FADFC8A3D7E4383FC195B58E0ACE3D80C076D756CA59FE97E91C1| §23.8 | 1 | 受理后台账无生产终态回写链 ⇒ 永久占位 | 已交付（组件/宿主层）：完成结算唯一顺序＋宿主运行终态回写 | 完成证据 | §24.15／§24.21 |
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md48DEA8839A949F2D1E7FE807946649762617CCE59D73B2393F03BBE4BC058DE11- **计数**：本轮后子批累计 **2/8**（无失败/超时请求）。仍有未闭合 MUST/IMPORTANT ⇒ 继续复会诊（第 3 轮）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md492B9409743EFEE8E86A18D9750367DB2955AD510100C7697940AB0E93B031751| 等待后重载跳过待执行节点 | ❌ 接线前残项：`RecomputeSuccessor` 把最后一条 `NodeOutcome` 当已完成；等待路径也经 `CommitOutcome` 写一条 `waitLocally`，定义修订后重定位可能取其 `Next`（当前 `ShouldRegisterLocalWait`…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md49745A10E4791CD5960DB6E6ACC8988E0261213DDBB25DA081674BD8F8F9909A1| 「结构版本」 | `LocalWaitPrerequisiteReference.StructuralVersion` 是**模型层纯数据字段**，**未**落盘（`LocalWaitItem` 只持久化 `string? PrerequisiteReference`），**未**参与求值；「引用指向什么权威事实源…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4981D2981F9F271B70B9770DFCA70FCFC994D63DF5E895920A435269E8CC86941以驱动 S2/S3 的「首次＋冲突重试」两个静态发送点，本批**未建该替身能力**，故该支仍为「已移交·未验收」。
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4B9CC67587AC3D244783DF4BCE8CCDA1094C99A6EBAE3E1509F5936459DB0A951- **结论**：第 1 轮 9 项中 **已闭环 4 项**（MUST-3 原反例、MUST-5、IMPORTANT-6、IMPORTANT-7 原反例），**仍未闭环 5 项**（MUST-1 版本绑定、MUST-2 归属保护、MUST-4 关系不变量残余、IMPORTANT-8 异常边界、IMPORTANT-9 …
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4C41A11F62808B1258C426B58B07A6EAE8CC5F29FF72D11569F02EB2D1E71ED11D2 仍为**未接线组件＋反例夹具**：**生产零消费点**、**零发送**、未接 E3/E4/E5／节点改道／S4b/S8b／热键；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4C41A11F62808B1258C426B58B07A6EAE8CC5F29FF72D11569F02EB2D1E71ED12D2 仍为**未接线组件＋反例夹具**：**生产零消费点**、**零发送**、未接 E3/E4/E5／节点改道／S4b/S8b／热键；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md4C41A11F62808B1258C426B58B07A6EAE8CC5F29FF72D11569F02EB2D1E71ED13D2 仍为**未接线组件＋反例夹具**：**生产零消费点**、**零发送**、未接 E3/E4/E5／节点改道／S4b/S8b／热键；
@@ -274,6 +277,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md50B3A406DDF704
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md534751CBEFFC608FB43C5BC2BD1349345691F412F22724CE718F5A75DFB5BA7F1| 10 | **§24.36 范围残余**：重启/恢复后不换键、不新增许可；真实磁盘故障分布 | R5.8 实现闭合序列（施工方：恢复项负责人） | 恢复入口再次取许可路径的夹具＋故障分布说明 | 节点改道门 | **部分已交付·未验收（整行未整体验收）**（[批次四十一] §24.52 重启后阻塞半；[批次四十八]…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md5398016AEEA64D6BD9740988CAFAD8F05514DB79416556F85D5761E4E813FB5E1该路径上的分类端点归 **R5.8 实现闭合序列（外部启用门）**；§24.41-C#5 因此记「**部分已交付·未验收**」。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md552EA39E34C96377CDEE0C4D7FFEFFE367EDCEB8563F7F85620D727723B560E51**[Owner ruling 2026-09-23；取代本节“待 owner 裁决”状态及旧甲/乙选项，尚未构成生产验收]**：所有参与方升级；生产外部启动只走能返回真实任务编号的 ext 队列通道，不回退无编号的 v2 `task.start`。提交前可证实通道不可用时明确拒绝且零发送；提交已出站后若回执丢失、超时…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md5735238080F3E2B5BCB2EBCC430359EE53C71D1D326161A6272E44D31449010A1- `ActivateCandidate(request)`：须已完成已确认的引用更新；目标必须**在已确认写集内**；真实激活 + **状态读回** → 才推进 `Activated` 并持久化 `activationRecord`（同步更新写集哈希，因激活同样改写了该文件）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md57E9D92FE9787770D79562440D6E13ED85D7760A4AEE803723D6C71A3E06C90D1| 2 | 助手租约存储与历史游标；施工方实现独立历史凭证/归档和清理夹具 | 到期清理后租约可读；审计/预观察引用有独立可验证来源；旧游标可分页或明确过期；同一历史身份不能借清理窗口重放 | 持久化读写门、R5.8 门 | 未闭合；不得只删反向引用校验或把历史游标当作无记录 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md581F96B77EE30275243BA1CD8E7C89BBEE9D2BD3EBA0A5AB32116A71C99DB0071| 4 | **G4a 启动移交/暂停续行的来源登记** | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 移交受理处落定固定 Scope/绑定来源记录＋后继提交可查得 | 节点改道门 | **已交付（组件/宿主层）**（[批次四十五] §24.56：来源权威下沉运行台账 `admissionSource…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md58B6D0E4547BEC64B1CA00A0F9AC301B17E0FB1505FA3D0BB182863E459350531- **门禁不变**：本批未改任何生产源码、未接任何生产入口、不产生发送；生产入口门、真实 User 门与 R5.8 签署**继续关闭**；批次 14 的接线前残项与 §24.106 未闭合项**全部仍然有效**。
@@ -293,6 +297,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md634359296AA54F
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md641E52663BDB9361CF1C156A5D39ECBBD819AAF44D5755B4725DE0F9DDB7E4DA1- **批次 17 新增残项（B17-R4-02/R5-01/R6-01：书面拒绝＋R3 三要素＋尝试记录；owner 2026-09-25 已裁决＝(a) 归入接线前批，见下）**：两个并发夹具对「被测调用在宿主内持续自旋」**无终止手段**——已做：共享运行器（后台线程＋统一清理＋「清理唤醒≠授权执行」）＋成功路径…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md643605EE435CA241324BEC08CBB8BF794D9C33B044D19A56535F27CBEA5DDF921"推进段穿越已完成出现"的历史理由已由稳定身份过滤消除；`:1486` 基准口径改写为"计划全序最早的可定位停驻标记"；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md64F57690BD814D7EBEFC7B4F7D1B00FB2C5C19F215613DE69B07E8EC75901EFC1| # | 残项 | 承接批次／角色 | 完成证据要求 | 保留门禁 | 状态 |
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md658681FEF146537C91E8131AAC46F8113B1E1D364EA42B1F3291BEEEC240C3501### §24.129.5 未闭合项、owner 检查点与门禁
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md65EB82E1891BBB6319FBFA8E8B3C99AD305CD8F4ECEE9B315F7F8E5ADF75FFF51**D. 状态**：§24.41-C#1（P50 负载敏感诊断套件）由「已移交·未验收」改为
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md667C67FB5CBBB7B477E7E6DE099B37A93E2CED4A76BADE73F040CBD2DC3923911| §24.41-C | 9 | 外部启动路径接管失败映射端点／子进程重启／E4 取消入口映射 | 已移交·未验收（真实入口层） | 残项登记 | §23.1／§24.37 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md66FB14DBB120A4ACCEF4349832546286467A20C3EA555F675F991268561D716A1| §24.55 | 3 | 去重镜像在占用路径的状态语义 | 已移交·未验收（设计批） | 残项登记 | §24.22 |
@@ -312,6 +317,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md70F14D6FCFE0E7
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md70F27170A5E76C4E797E54CD90527A10B3A7FF73410D6416A4E4D81EE51CD0EE1**〔批次 17 闭合登记，2026-09-25：owner 裁决①选 (b)／②选 (b)，上文真·残余第 2、3 两项均已闭合；本批只动测试文件与测试配置，生产源码零改动（ZCode 施工，HEAD `3adb13abd` 之上）〕**
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md729F7DEAC32E75979DA826A4777E966DC61C4811D2AC567A36395073B954D28E1| 已完成会诊轮次 | 冻结轮（12）／复（6）／终（6）／四（4）／五（1）／六（2）／七（4）／八（4）／九（5）／十（4）／十一（2）／十二（1）／十三（1）／十四（4）／十五（4）／十六（1）／十七（2）／十八（3）／十九（1）／二十（1）／二十一（1）／二十二（2）／**二十三（无必改项）** |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7314FA735BC6F536281AC605ADF9EF80FCCCA393A26BA9D27840664939D51A991| **取舍 1：`SelectNext(items)` 旧签名** | ✅ **已闭合：按批约束「冻结合同只允许纯加法」保留公开签名（纯加法），语义废除** | 该单参重载**无求值器** ⇒ 缺引用时只能**静默**把项判为「已就绪」，**正是 C4 缺陷的重演**；故其**语义**必须废除。保留**签名**只为不…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md733C458BF96A9BA8D1F07B23A7471B9748C42E4A535CDE80DFFC7D16AC2B004F1- **结论原文要点**：「**存在已确证、仍未闭合的 MUST / IMPORTANT，当前不宜验收 A 项**」——**MUST 5 项 + IMPORTANT 4 项 + 建议级 1 项**。报告原文见 `consultation/review-round1-report.md`。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md739D5BB499EC9780771DE099B8CC1464BFAA567EC2270AA0EB9B7CBCB45ECB511用户关闭运行中的 BGI 后，以 `DeployToBgiTools=false`、`--no-build` 重跑 BGI 全量：**942 通过／14 失败／956 总计**，测试宿主完整结束。失败身份集合与 `r410_bgi_full_c2_20260919.trx` 的 **14 个失败逐名相同**，差集为空；…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7476939160760415A8D3CE3DEE2F9D3E1F79759223DF9EE678C1829F5DDFA04E1**实现（基础组件，未接线）**
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7538962CD4358F65F6FA6ACAE09BAF5EDB4531B0B36B2FFE8DDF6386B7DA45461「**已交付（组件/宿主层）**」——可单独重复运行的**负载复现入口** ＋ **根因定位与修复** ＋ **该类夹具稳定
@@ -333,6 +339,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md80DD4EC8C1253A
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md80DDF01D0BFC70983CFED44C6BE7AABB647CF583049394FD85354333A54606A11| §24.41-C | 10 | 恢复入口再次取许可、真实磁盘故障分布 | **部分已交付（整行未整体验收）**：重启后阻塞半＋**子项「恢复入口再次取许可」**已交付；**仍欠**＝子进程级重启与真实磁盘故障分布（门禁保留） | 部分证据 | §24.52／§24.60 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md81D7B581B4601E8E67480F720408281D7FBBF84E30DB9F5AB53F67420992203C1"仍存活停驻 + 每个停驻同轮序号更早的从未执行出现"的未履行恢复义务并按计划全序重入
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md825BB94E7C0BAC32A93C2A3E676554F4EE14C61922EE0A139228A6A54F49D3741| 50 | 重要 | 总计划 §3.3 R5 行「**R5.0–R5.7 已完成**」**效力拔高**：未限定证据层级，与 §7「生产接线仍关闭、P50 等门禁未闭、未签署」冲突 | **已修**：该行改为「**R5.0–R5.7 组件/设计层按各自范围收口（≠ 生产启用、≠ 验收签署；R5 整体仍未完成）**」，并…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md834ED8BC4ED242B6E386BE295914174318127751287D0BB23770097D60CB13111- **本批只施工 R5.6 验收矩阵 A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程只在**隔离配置根**内；**不**执行真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。B–F 与真实入口/实机门**本批未验…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md85762AB383BD60FC1F9EEE655542A85233D09F596235E6D9236B0F668412CD861收尾原文为「本批是否仍有未闭合的 MUST/IMPORTANT：否」。逐项原文见 `consultation/review-outcome-v3.md`。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md859A37E8D27E1800A45C13862F0E5BF9A602D48CEEEC118913BEFEBC4B73C6DF1| ext 前置／收尾的执行与终态 | 统一结果须区分 `Succeeded/Failed/Cancelled/Unknown` 与物理退出；不可逆动作先落不可重放 intent，实际结果另记 | `RunOpAsync` 失败返回 `false`，协调器将 `false` 记为 `completed`；破坏性收尾动作…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8674C4A4BAD8661F4324DCAD6D7CC9A8DDCA5BA433B0A4BCA5EAA57A3FBA42F31冻结合同未改、真实事件会送达、安全网会被调度、消费者确实完整准入、运行时绝无重复发送——这些均由本节的命令证据与本批的"未接线"立场分别承担，
@@ -449,6 +456,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE7E85D8830B20A
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE881CF6ECDBA15EF4A7138CDD64E10B5D5BAE4F68913B87161E911281914F7901- 会诊第五轮（最终状态窄范围确认，覆盖第四轮之后的全部改动）：**GPT-6-Astra／medium／1 次成功**（attempts=1）。结论：①GUID 形状／溢出证据**已闭合**；②包装层残余登记**属诚实口径**（静态支持惰性求值，但无条件测试证据仍缺）；③最终状态**无必改项**；④**无未登记的重要…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE888C76FEBDC1619CCE68BA364942E5E0FD15D3DD5E690044D024DE179B98AD41- 首轮表的 #1／#2／#3 三行**已就地加注「2026-09-24 更正：判为未闭合」并指向本修复表**（原文保留以便追溯，不删除）；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE92D265D95832371702BB11E3CDD08B4D88FCE04B139AA22C1CF609BC8D321781该形态在"仅重入停驻点"的中间实现上可复现，闭合方式＝链尾重建同轮前插未执行出现（§24.127.1 ②）。
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdE9BA25E311E2328BCD5BDA16CBC28F43AC14113E65BBBF2ED5A8DFCB6517C9C11| 8 | IMPORTANT | 同实例 monitor 可重入 ⇒ 端口回调内重入可覆盖已完成回滚 | 新增 `_effectCallInProgress` **重入守卫**（覆盖全部变更入口）并在副作用返回后核对阶段未被改变（`ReentrantMutationFromEffectCallback_IsRejec…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdEAC1B5314BAA3AE6B8455C17E31E931B4EBBE11A1102B8AC6872081DB251CA6D1| §23.8 | 4 | 生产 F11 事实源为空、占用未合并运行台账 | **门禁保留**：生产 F11 源未接线；占用归属事实源缺失 | 残项登记 | §24.55 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdEAD488EB01CBA08B06534D2DD2E3F4F6FB77E6AD63F70D7F888ACB7EA72725811| §24.41-C | 7 | S2/S3 组合根端到端反例（F11／编号通道冲突零重发／抢占） | **部分**：F11 零发送已交付；旧 v2 回退冲突证据已被新合同取代，当前须验证编号通道下拒绝零重发；优先级行为已裁决但抢占接线未闭合 | 部分证据 | §24.42／§24.50／§24.65／§24.68 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdEB032A8CCF97E1F4AF6DC4EAC0564AC0BE732F8B293CCED4EA82217D0E321ED91**B. 仍未覆盖（登记，禁悬空）**（[更正·2026-09-21 批次二十一] 本节 B-1「准备阶段 `RunStore` 更新失败：当前无注入接缝 ⇒ 归 B4 未验收」**已由 §24.36 取代**——该支已交付组件/宿主层夹具并通过；本节其余条目不变）
@@ -473,6 +481,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFB8504B1F803BF
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFB944D03EB853B3A359D85ABCF4A88B46FAB522D0F048B1CCD465FEDDE9FD11C1| 生产消费端 | ❌ 未接线：抢占执行、退出确认、恢复专用准入仍未消费本批事实（生产门保持关闭） |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFBE89153DB0A570A1832D57B78495F3EC946CA01CC65CD8D3F4F37229ABA16121| **④ 生产时序与新鲜度** | ⚠ 生产解析**已接线**，但新增同步读取（运行台账 + 租约）的耗时、锁交错、以及读取后快照新鲜度/纪元一致性**尚未验证** |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFC153C1C4E1F5B6CFFEBBC5CCB331E2304C86AC1D4838E206C499EA4E2163EA91本批是**接收批**：把已独立交付、并按原等级闭环的 BO-6/BO-7 修复接入茶包主线。不施工 BO-8/BO-9，不提前集成 R5.6、R6.1 或 R6 diff guard，也不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFCD2572456EAA547195057C704E4E4DF401D047AF863D8C88697640AAF6DA26B1- **B–F 六项启用前置继续未闭合**：助手流程/引用/激活元数据恢复范围（B）、生产消费必经检查点（C）、真实静止窗口写方全覆盖（D）、真实入口回执与责任（E）、目标机路径身份（F）本批**未验收**。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFCE6B4691F864A07829278D1C3670C068E38976805218538157254EC16F957801| 2 | **P19① 原因码逐次取证**（批次三十二原诊断已按 owner 裁决更新） | R5.8 真实入口与拒绝审计验收（施工方：门面/宿主夹具负责人） | 组件层已验证 `submission_conflict` 释放被拒请求槽位、保留拒绝原因与原未决责任；尚需真实入口端到端核对拒绝映射、审计可查与解除冲突后…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFE3C9DA096826D54AA3505702CCBA877AB65B6B6F2C1622E5E941C2835F8A88B1但 Runner 的 `attempt` 仍固定为 1、未驱动 `RetryAsync` ⇒ **重试链未闭环**（窗口只能由显式恢复/后续批次消费）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFE6A5508ED80DF42F43A81D61D9DE8D844DFF7A49E80AB474566B07C4AE650131- **Owner 收口裁决（2026-09-27）**：owner 本轮指令要求收尾 SB21-1；据 R9 两项原级必改的补证已完成本地机械核验，关闭本子批且不追加会诊。反向突变审计中的 11 组 mutant/restored TRX、命名 Fact 结果、TRX SHA 与当前恢复态源码 SHA 均逐项复核；R…
diff --git a/槲寄生调度器总计划.md b/槲寄生调度器总计划.md
index c181a267d..b550b6a80 100644
--- a/槲寄生调度器总计划.md
+++ b/槲寄生调度器总计划.md
@@ -407,6 +407,17 @@ BGI 的一条龙恢复 `origin-lcb/main` 的配置格式、原生页面和默认
 
 
 
+## 2026-09-29：R5.6 A 项主线施工（隔离配置根的真实引用写入与 candidate→active 激活接线）
+
+- **性质与范围**：施工批。只做 R5.6 验收矩阵 **A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程**隔离配置根**；**不**做真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。
+- **反例先行**：先证明 `MarkReferenceUpdateCompleted`／`MarkActivated` 仅推进阶段——未注入真实副作用端口时**零写入**即可推进到 `Activated` 并提交成功（配置根字节始终未变，假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
+- **实现**：新增真实副作用端口与实现（`MigrationReferenceActivation.cs`：引用重命名／新增文件／激活读改写，临时文件+同目录替换、保留 BOM、写前复检、解析失败隔离跳过、四类结果与已落盘数如实回报）；事务新增 `ApplyReferenceUpdate`／`ActivateCandidate`（写集声明校验 → 真实副作用 → 语义+字节读回 → **才**推进阶段并持久化读回证据；拒绝/未知/副作用后取消 fail-closed 置 `Blocked`；副作用前取消保持阶段；已到阶段幂等复核；提交前复核写集与激活记录；回滚先撤销激活再恢复旧字节并要求旧态一致）；manifest 增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord` 并纳入完整性与结构不变量。
+- **验证**：定向 **119/119**（同条件基线 70/70）；助手全量 **1619 通过 / 2 跳过 / 0 失败 / 1621**（同条件开工基线 **1570/2/0/1572**；testId added=49／removed=0／changed=0）；**33 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；39 行状态/并发/故障矩阵（37 行 covered 各绑定有效突变，`REF-C4` 记为 not_applicable 并附尝试记录）；部署目标未留下可观察变化（最新写入时间早于本批）。
+- **会诊**：第 1 轮判 MUST 5 + IMPORTANT 4 + 建议 1；第 2 轮（验证轮）判已闭环 4 项、未闭环 5 项并新报 MUST 4 + IMPORTANT 4。两轮发现均按原级修复并逐条补夹具与突变（子批计数 **2/8**，继续复会诊）。报告与逐条处置见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与 R5.3 §24.129.7／§24.129.8。
+- **残项（如实）**：`REF-C4`（同实例并发）与「降级 manifest 标记不得绕过归属保护」两条仅有夹具级反例，**未取得可杀死夹具的单点突变**（尝试记录在案），不主张突变判别力。
+- **owner 检查点（1 项）**：BGI 侧 W4 `OneDragonConfigReferenceService` 为 BGI 程序集内 `internal`，助手工程对其**零引用** ⇒ 生产引用写方的归属（BGI 直调／共享库／IPC 委派）**无权威裁决**，生产接线保持关闭，待 owner 定夺。
+- **门禁**：**B–F 六项启用前置与真实入口/实机/真实 User 门继续未验收**；本批**不**等于 R5.6 完成，也不等于 R5 完成。详见 R5.3 §24.129 与 `_workflow/r56-reference-activation-wiring-2026-09-29/`。
+
 ## 2026-09-28：R5.6 主线迁移集成批（并行成果接收与集成验证）
 
 - **性质与范围**：接收批。把目标批次为「R5.6 主线迁移集成批」的两项并行成果（`r56-migration-audit`、`r56-activation-prep`）接入茶包主线并重跑集成版本回归；不施工 R5.6 生产接线（真实引用更新/candidate→active 激活、生产检查点消费、真实静止窗口、真实入口回执、目标机路径身份），不施工 R6.1 与 R6 diff guard，不重开 BO-8/BO-9、BO-6/7-D1、BO-9-D1、BO-13、SB21-3 BO-4、SB21-2 BO-10/12。

## scoped staged diff

## materials outside this batch
本批文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`（改）、`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`（新增）、`Test/.../R56ReferenceActivationWiringTests.cs`（新增）、`Test/.../ClaimSurfaceManifest.txt`（再生 +4/-0）、R5.3 §24.129、`槲寄生调度器总计划.md` 进度条目、`Docs/design/mistletoe-parallel-deliveries.md` 进展段、本批 `_workflow/**`。材料外变更：工作区其余历史批次证据、`.bak`/`.stale`/`TestResults`/日志/DLL、两份既有未提交设计文档（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）——不进入提交，也不假定写者归属。
