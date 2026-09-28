# 第 3 轮（验证轮）会诊报告（原文要点，gpt-6-astra / medium / attempts=1；只读）

**本范围内仍存在已确证、未闭合的 MUST / IMPORTANT，建议暂不验收。**

1. **MUST：新增激活目标的回滚仍不能在中断后恢复。**（`CompleteRollback`／`OwnershipConflictReason`／`UndoActivation`）
   单次正常回滚顺序已修正，但 `ownershipVerified` 仅在内存。路径：新增文件激活成功 → 回滚预检通过 → 撤销为 candidate-ready → 恢复基线时抛错/中断 → 再次恢复。持久化写集仍保存 **active 哈希**，预检会把本事务自己的撤销结果判为 `rollback_addition_not_owned`，之后持续阻断。可用 `fileRestoredHook` 在首次恢复后抛错构造。**R2-MUST-1 仍未闭环。**
2. **MUST：预核集合包含「原本不存在」的路径，并允许删除预检后出现的他方文件。**（`CompleteRollback`／`DeleteRecordedAdditions`）
   `OwnershipConflictReason` 对不存在的目标放行后，调用方仍把**全部 Added 路径**放入 `ownershipVerified`；删除只查路径成员资格、不查当前字节。反例：只快照+登记 Added → 预检时不存在 → 在恢复回调中创建他方文件 → 清理将其删除并报告 `RolledBack`。删除须绑定实际核验过的版本，并处理本事务撤销造成的版本变化。
3. **MUST：激活端口绑定的是调用方提供的哈希，未强制等于已确认写集哈希。**（`ActivateCandidate`／`WorkflowFileMigrationEffectService.Activate`）
   事务取得 `confirmedHash` 并检查当前文件，却只要求 `request.ExpectedContentHash` 非空。可构造：已确认内容 B，调用方传入 X 的哈希；状态读取回调把 B 换成 X（状态仍 candidate-ready）⇒ 前置检查通过、端口两次哈希均匹配 X ⇒ 激活并登记 X 为合法证据。应由事务用 `confirmedHash` 构造请求或强制校验相等。**R2-MUST-3 仍未闭环。**
4. **MUST：提交仍接受被修改的写集外基线文件。**（`Commit`／`UnexpectedFileReason`）
   缺失检查已补齐，但**字节修改分支**仍在：基线 A、B，仅更新并激活 A，随后修改 B 内容而保留路径 ⇒ 演练与提交仍通过（提交只查写集哈希与文件集合）。**R2-MUST-4 仍未闭环。**
5. **IMPORTANT：重入守卫未覆盖读回回调；`Dispose` 亦可绕过。**（`ApplyReferenceUpdate`／`ActivateCandidate`／`RecheckActivationRecord`／`UndoActivation`／`Dispose`）
   `_effectCallInProgress` 只包围两个正向写端口，随后在语义读回前清零 ⇒ 第 2 轮原反例（读回先成功、再同步 `Rollback()`、再返回缓存结果）仍成立；写端口回调可调用无守卫的 `Dispose()` 释放锁与窗口。**R2-IMPORTANT-7 仍未闭环。**
6. **IMPORTANT：回滚的两处后续状态读取仍可能向外抛异常。**（`UndoActivation` 撤销后读取、`CompleteRollback` 恢复后最终读取）
   外层仅捕获 `IOException`/`UnauthorizedAccessException`/`InvalidOperationException`；端口抛 `ArgumentException` 等仍外泄。**R2-IMPORTANT-6 仍未闭环。**
7. **IMPORTANT：副作用后取消仍未实际写入，台账也未统一。**（夹具／`findings.md`／`mutations/summary.md`／`risk-matrix.json`）
   取消夹具设 `ApplyTargetsLimit=1` 但计划只有一个目标 ⇒ 脚本仍走非成功直接返回分支，**零写入却报告一项完成**；`findings.md` F-8 仍写 17 项、summary 标题写 28 项、records 为 33 项；REF-F20 把 M30 关联到「不存在目标可安全中止」（M30 实际目标是另一夹具）；M31 的扰动回调每次读都重写文件，mutant 最终为 `activation_readback_failed` 而非「错误激活获准」。**R2-IMPORTANT-8 仍未闭环。**

逐条闭合判定：R2-MUST-1 未闭环；R2-MUST-2 **已闭环（代码层）**（不主张独立击杀突变）；R2-MUST-3 未闭环；R2-MUST-4 未闭环；R2-IMPORTANT-5 **已闭环**；R2-IMPORTANT-6 未闭环；R2-IMPORTANT-7 未闭环；R2-IMPORTANT-8 未闭环。

补充核对：`BeginTransaction → TryAcquireExclusive`、`TryRunProduction → AuthorizeProductionExecution` 的合法嵌套未被守卫误拒；旧夹具空写集路径不会进入 `EvidenceRelationProblem`；重复身份校验仍以写集非空为前提，不能宣称是无条件结构不变量。`UnexpectedFileReason` 实际用于**引用更新与提交**，未用于回滚演练。
