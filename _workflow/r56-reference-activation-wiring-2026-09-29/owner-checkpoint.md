# R5.6 A 项子批 · owner 检查点（2026-09-29，会话交接用）

本批（`r56-reference-activation-wiring-2026-09-29`）**未收口**。原始目标要求「会诊无未闭合重要/必改项」才可登记完成；
第 3 轮会诊仍有 **4 MUST + 3 IMPORTANT 未闭环**，故按纪律**保持原级、不开生产门、不登记完成**，并交付本检查点。

## 未决项（原级，未降级）

| # | 原级 | 未闭环要点 | 关键位置 | 建议的有限修复范围 | 验收条件 |
|---|---|---|---|---|---|
| 1 | MUST | 新增激活目标回滚**中断后不可恢复**：归属证据仅在内存（`ownershipVerified`），撤销后的字节版本无持久化证据 ⇒ 二次恢复被判 `rollback_addition_not_owned` 持续阻断 | `CompleteRollback`／`OwnershipConflictReason`／`UndoActivation` | 为「撤销后的合法字节版本」建立**持久化**证据（如写集增加 `postUndoHash`/阶段化归属表），使幂等恢复可判定「本事务自己写的当前版本」 | 用 `fileRestoredHook` 在首次恢复后抛错 ⇒ 二次恢复收敛到 `RolledBack`，且他方文件仍被保留 |
| 2 | MUST | 归属删除只查**路径成员资格**、不查当前字节：预检放行的「原本不存在」路径被直接授予删除权 ⇒ 预检后出现的他方文件会被删除并报告 `RolledBack` | `CompleteRollback`（`ownershipVerified`）／`DeleteRecordedAdditions` | 删除授权绑定**实际核验过的版本**（记录核验时的哈希，删除前再核对），并区分「预检时不存在」与「已核验归属」 | 新增反例：只登记未写入 + 恢复回调创建他方文件 ⇒ 保留文件并阻断，不得报告完整回滚 |
| 3 | MUST | 激活端口绑定的是**调用方提供的哈希**，未强制等于已确认写集哈希 ⇒ 可构造「请求带 X 的哈希 + 状态读取回调把内容换成 X」而登记 X 为合法证据 | `ActivateCandidate`／`WorkflowFileMigrationEffectService.Activate` | 由事务用 `confirmedHash` **构造**端口请求（或强制校验 `request.ExpectedContentHash == confirmedHash`） | 新增反例：请求哈希与写集不一致 ⇒ 直接拒绝；漂移内容不得被激活 |
| 4 | MUST | 提交仍接受**写集外基线文件的字节漂移**（缺失已拦截、修改未拦截） | `Commit`／`UnexpectedFileReason` | 提交面复核**全部基线文件字节**（不只写集内 + 集合相等） | 新增反例：修改写集外基线文件内容 ⇒ 提交被拒 |
| 5 | IMPORTANT | 重入守卫未覆盖语义读回回调，且 `Dispose` 无守卫 ⇒ 读回回调内 `Rollback()`/`Dispose()` 可改变阶段后被外层覆盖发布 | `ApplyReferenceUpdate`／`ActivateCandidate`／`RecheckActivationRecord`／`UndoActivation`／`Dispose` | 守卫覆盖**全部端口回调**（含读回、撤销、恢复回调）；`Dispose` 同样受守卫；回调返回后统一核对阶段/代次 | 读回回调内重入 ⇒ 被拒；外层不得发布成功阶段 |
| 6 | IMPORTANT | 回滚两处后续状态读取仍可能外泄异常（`ArgumentException` 等） | `UndoActivation`／`CompleteRollback` | 端口读回统一 `try/catch(Exception)` 收敛为结构化 Blocked/拒绝 | 定向反例：撤销后读回抛非三类异常 ⇒ 结构化 Blocked |
| 7 | IMPORTANT | 取消夹具仍**零写入**即报告完成；台账计数不一致（17/28/33）；两处突变映射失真（REF-F20↔M30、M31 一次性扰动） | 夹具／`findings.md`／`mutations/summary.md`／`risk-matrix.json` | 取消夹具改为**两个目标**以走真实部分写入分支；统一计数口径；重映射两处突变并说明判别依据 | 夹具断言「已落盘文件数 == 完成写入数」；三处计数一致；映射与断言一一对应 |

## 已达成（本批已交付、可复核）

- 真实引用写入与 `candidate-ready → active` 激活已在**隔离配置根**内接线：写集声明校验 → 真实副作用 → 语义+字节读回 → **才**推进阶段并持久化证据；拒绝/未知/副作用后取消 fail-closed；副作用前取消保持阶段；幂等重读复核；提交前复核；回滚先撤销激活再恢复并核对**完整基线字节集**。
- 证据（绑定最终代码版本）：定向 **119/119**（同条件开工基线 70/70）；助手全量 **1619 通过 / 2 跳过 / 0 失败 / 1621**（同条件开工基线 **1570/2/0/1572**；testId added=49／removed=0／changed=0／unchanged=1572）；**33 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；39 行状态/并发/故障矩阵（37 行 covered 各绑定有效突变）。部署目标未留下可观察变化（最新写入早于本批）。
- 会诊：第 1 轮 1/8、第 2 轮 2/8、第 3 轮 3/8（上限 8；无失败/超时请求）。三轮报告与逐条处置见 `consultation/` 与 R5.3 §24.129.7–§24.129.9。

## 风险与门禁

- **生产引用写方归属**（owner 检查点，独立于本批）：BGI 侧 W4 `OneDragonConfigReferenceService` 为 BGI 程序集内 `internal`，助手工程对其**零引用** ⇒ 生产环境写方归属（BGI 直调／共享库／IPC 委派）**无权威裁决**。
- **真实 `User` 目录切换、生产构造、E3/E4/E5、热键、R5.8 签署、实机门继续关闭**；B–F 六项启用前置未验收。
- 残留残项（如实）：`REF-C4`（同实例并发提交/回滚）与「降级 manifest 标记不得绕过归属保护」两条**仅有夹具级反例、未取得击杀突变**，不主张突变判别力。

## 下一会话可直接执行的范围（一个批次）

按上表 1–7 逐条修复并补定向反例与有效突变（每条须能被单点突变杀死），统一台账计数，然后跑第 4 轮验证会诊；只有在**无未闭合 MUST/IMPORTANT** 时才登记本子批完成。会诊预算已用 3/8，剩余 5 次。


## 工具核验的口径限制（如实登记）

`tools/mistletoe/workflow.py` 的 `review/closeout` 阶段要求：`review_round > 1` 时每个 MUST/IMPORTANT 前置发现必须记为 `disposition=candidate_fixed`
并提供已索引的修复证据。本批的第 1–3 轮发现中**存在未修复项**（见上表），因此**无法在不降级、不伪记修复的前提下**通过该字段生成 closeout 快照。
按设施接入计划的「工具或材料有问题，执行者自行修复或登记并完成同等人工核验」，本批**不**运行 closeout 快照，改以人工核验替代并在本文件与 R5.3 §24.129.9 登记：
证据清单（40+ 条，含两轮声明面再生差异、三轮会诊报告、33 项突变记录与 `summary.md`）、TRX 逐 testId 差集、矩阵 39 行绑定、以及 `review-v1/v2/v3` 三轮送审快照均已落盘于 `_workflow/r56-reference-activation-wiring-2026-09-29/`。
任务模式、固定模型/强度、会诊计数（3/8）、重要/必改不降级与生产门关闭均未受影响。
