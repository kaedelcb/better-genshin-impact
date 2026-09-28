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

5. **IMPORTANT：登记 Added 后尚未创建文件，也无法安全中止事务。**
   位置：`OwnershipConflictReason`。
   该方法先要求写集哈希，之后才判断文件不存在。因此，真实事务只完成快照和 Added 登记，尚未调用更新便回滚，也会得到 `rollback_addition_without_ownership_evidence`。即使配置根完全等于基线，后续恢复仍重复阻断，且不能开启下一事务。
   应先安全确认目标是否不存在；不存在不需要删除归属证据。存在且无证据时继续保留并阻断。

6. **IMPORTANT：异常收敛仍遗漏复核和回滚读回路径。**
   位置：`RecheckActivationRecord`、`UndoActivation`、`CompleteRollback`、`TryHashConfigFile`。
   - `RecheckActivationRecord` 的端口读取没有捕获异常；重复激活和提交均可直接抛出，阶段不转 Blocked。
   - 撤销后的第二次状态读取、恢复后的最终状态读取没有完整异常边界；外层仅捕获三类异常，端口抛出 `ArgumentException` 等仍会外泄。
   - `TryHashConfigFile` 的 `IsSafeTarget` 调用位于其 `try` 之外，属性读取异常也不受该捕获保护。
   正向写端口和首次读回的修复有效，但不足以支持“全部读回均收敛”的结论。M25 只覆盖 apply 写端口抛异常。

7. **IMPORTANT：同实例 monitor 不能阻止端口同线程重入，外层操作可能覆盖已完成回滚。**
   位置：`ApplyReferenceUpdate` 的语义读回及后续阶段发布。
   可构造端口先完成真实语义读取，再同步调用同实例 `Rollback()`，最后返回之前的成功读取结果。C# `lock` 可重入，回滚可以完成并恢复基线。外层随后对恢复后的文件取哈希，用旧的局部 manifest 发布 `ReferenceUpdating` 并返回成功——引用已经恢复，回滚终态却被覆盖。
   新增校验没有在外部回调后检查事务阶段、操作代次或静止窗口是否变化。需要显式阻止外部回调重入事务变更入口，或验证操作代次。此问题与已接受的 REF-C4 `not_applicable` 登记不同，不要求重新构造线程调度突变。

8. **IMPORTANT：IMPORTANT-9 所称修复与所附夹具、突变材料仍不一致。**
   位置：`R56ReferenceActivationWiringTests.cs`、`mutations/summary.md`、`risk-matrix.json`。
   - `ReferenceUpdate_CancelledAfterEffects_Blocks` **仍然只设置 `ApplyCompletedWrites=1`**；未设置 `ApplyTargetsLimit`，脚本的非成功分支没有实际写入。原缺口原样存在。
   - M8 与 M6 的补丁、目标测试和 mutant 哈希完全相同，不能证明 M8 名称所指的“激活前引用门槛”。
   - M22 只删除归属预检；删除函数内部的归属检查仍保留。失败来自原因码改变，不能证明夹具检测到了“他方文件被删除”。
   - 多行矩阵仍同时列有 mutation ID 和“无有效反向突变”说明；`findings.md` 仍记录旧的 17 项结论。
   独立 manifest 副本、撤销调用断言和并发夹具确实已增加，但不能据此判 IMPORTANT-9 全部闭合。所附摘要中的运行结果也只能视为报告，不能代替独立执行验证。

逐条闭合判定：MUST-1 仍未闭环；MUST-2 仍未闭环；MUST-3 已闭环（原新增反例）；MUST-4 仍未闭环（`ChangedFiles` 身份唯一性未校验）；MUST-5 已闭环；IMPORTANT-6 已闭环；IMPORTANT-7 已闭环（原反例）；IMPORTANT-8 仍未闭环；IMPORTANT-9 仍未闭环。

MUST-4 的剩余结构缺口可以直接构造：在合法 manifest 的 `ChangedFiles` 中追加同一路径的重复记录，甚至追加同身份的 Deleted 记录，再重算摘要。逐条基线校验均可通过，`EvidenceRelationProblem` 用 `HashSet` 折叠非删除登记，无法发现重复或冲突身份。授权仍可接受这种结构矛盾；应在构造集合前拒绝重复登记身份。

建议先修复上述真实根回滚、版本绑定和提交复核问题，再补充对应独立反例，并同步整理突变台账。演练标签修正已经落实；B–F、生产接线及 owner 检查点未作为本轮缺陷。**本范围内发现了已确证、仍未闭合的 MUST/IMPORTANT。**
