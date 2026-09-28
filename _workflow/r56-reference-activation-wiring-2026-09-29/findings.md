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
