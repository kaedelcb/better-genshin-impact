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
