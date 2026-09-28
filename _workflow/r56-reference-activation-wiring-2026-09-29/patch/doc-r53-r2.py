import pathlib
# R5.3 §24.129：补第 1 轮会诊与修复、更新验证数字与矩阵/突变计数
p = pathlib.Path("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:90])
    s = s.replace(old, new, cnt)

rep('''- **定向**：`R56MigrationSwitchTransactionTests` + `R58MigrationRehearsalTests` + `R56ReferenceActivationWiringTests` **99/99**；同条件开工基线（前两类）**70/70**。
- **助手全量**：**1599 通过 / 2 跳过 / 0 失败 / 1601**；同条件开工基线 **1570 / 2 / 0 / 1572**；逐 testId 差集 **added=29 / removed=0 / changed=0 / unchanged=1572**。
- **反向突变 17 项**（`_workflow/r56-reference-activation-wiring-2026-09-29/mutations/`，逐项含 build/mutant/restored 独立日志与 TRX）：baseline Passed / mutant Failed（构建 exit 0、测试 exit>0，命中具名断言）/ restored Passed，**源码逐字节恢复**（raw SHA-256 `4122B45C7712…`）。其中 M3、M5 为「同一位点两层独立复核对同一漂移各自判别」的**整块削弱**突变（单独去掉任一层会被另一层拦下，故按整块定义以取得判别力）。
- **矩阵覆盖**：状态/并发/故障 23 行全部 `covered`，每行绑定具名反例与**有效突变**。''',
'''- **定向**：`R56MigrationSwitchTransactionTests` + `R58MigrationRehearsalTests` + `R56ReferenceActivationWiringTests` **111/111**（第 1 轮会诊修复后复跑）；同条件开工基线（前两类）**70/70**。
- **助手全量**：**1611 通过 / 2 跳过 / 0 失败 / 1613**；同条件开工基线 **1570 / 2 / 0 / 1572**；逐 testId 差集 **added=41 / removed=0 / changed=0 / unchanged=1572**。
- **反向突变 28 项**（`_workflow/…/mutations/`，逐项含 build/baseline/mutant/restored 独立日志与 TRX、补丁原文与三段判定，汇总见同目录 `summary.md`）：baseline Passed / mutant Failed（构建 exit 0、测试 exit>0，命中具名断言）/ restored Passed，**源码逐字节恢复**（事务文件 `46668E1CB646…`、引用服务文件 `6AF93577F049…`）。其中 M3、M5、M14、M28 为**整块/组合削弱**型突变（同一位点存在多层独立复核，单独削弱任一层会被另一层拦下，故按整块定义以取得判别力）——此点如实登记，不主张「每层都有独立判别力」。
- **矩阵覆盖**：状态/并发/故障 **33 行**（其中 32 行 `covered` 并各绑定有效突变；`REF-C4`「同实例并发提交/回滚」为 `not_applicable`：同实例串行边界由代码结构（共用 monitor）承载，真实交错的单点突变不可确定性构造——已附尝试记录，判别力由不变量夹具 + `REF-C2`/`REF-C3` 承担）。''')

rep('''- 会诊：本子批独立计数（上限 8 次）；逐轮记录见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.129.7。''',
'''- 会诊：本子批独立计数（上限 8 次）；逐轮记录见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.129.7。

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
- **边界**：会诊只审阅所附代码与 diff，未独立核验原始 TRX/突变日志与运行行为；本批据此不主张无条件完整验收。B–F 与真实 User/生产门未作为本批缺陷要求补齐，仍关闭。''')
p.write_text(s, encoding="utf-8")
print("R5.3 §24.129.7 appended")
