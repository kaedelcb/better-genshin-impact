# SB21-2 R4 复核发现与关闭证据

## 当前事实快照

- 分支 `main-OldTeaBag-B168`，HEAD `8a4b8d988e2f0ff3ade35538bffa2de7a93c9da2`；暂存为空。完整 porcelain 在 `review-r4-status.txt`，本批未暂存 diff 在 `review-r4-related-unstaged-in-scope.diff`，材料外两份设计文档的状态/diff 分列在 `review-r4-material-out-*`。`.bak/.stale`、历史目录及其他 material-out 内容均未删除或回退。
- 本批源码/夹具 6 个文件；另外更新 R5.3 §24.122 与声明 manifest，并附上 `_batch21/b21_plan.md`、SB21-2 交接、合同、反向突变和本上下文。仓库外 relay 文档仍待最后定案后更新。
- R4 已发送并返回，当前累计 4/8；R1-R4 都是 GPT `gpt-6-astra`／medium，另有 2 次本地预检拒绝且未发送、不计数。独立台账为 `C:\Users\Administrator\.tools\zcode-relay\test\ledger-batch21-sb21-2.json`。历史等级 BO-10 R37、BO-12 R46 均重要-2，未降级。

## R3 重要发现及 R4 处置

R3 不建议核销 R1/R2 五项原重要发现，并提出当前夹具反向突变判别力、legacy 非空迁移及旧最大请求、v4 错形/long.Max 清理、损坏 Store Consume、临时写失败、Cleanup 原始字节比较和路径物理别名范围缺口。R4 没有指出新的实现缺陷，确认当前同锁键并发合同有明确限制；但保留两项**重要级证据缺口**（不得降级）：

1. `overflow-item-generation` 原输入同时把根 `generationHighWater` 设为溢出值；Load 会先在根校验处拒绝，预期错误标记 `generation` 又可能被 `generationHighWater` 子串满足，故没有独立证明项级 `ParseGeneration` 溢出拒绝。
2. 助手全量报告把同名测试显示名数误写为“共享执行用例”数，未给出严格守恒的 TRX 执行用例差集。

随后一轮 generation 测试在反向突变之后失败；不能据此认定原解析器 fail-open，该轮被测二进制身份和失败原因未定。为排除该状态歧义，恢复保存的原 Store 源（SHA `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`）并做助手测试项目非增量构建，`Load_V4` 根/项两测试通过 2/2，独立合法根 H=`long.MaxValue` 的 item overflow 测试通过。随后在原解析器上饱和接受 overflow 的反向突变使独立用例因“未抛异常”失败；源码按字节恢复同 SHA。最终生产 Store 不保留额外解析改动，仅保留独立测试夹具。精确恢复权威证据在 `mutations-r4/item-generation-overflow-original-parser/`；此前 v1/v2/v3 记录保留，但未重建确认的运行不作验收依据。原 `item-generation-overflow-original-source-probe/` 哈希不一致，仅作为有歧义的历史记录。

全量差集已按 TRX `testId` 重算：基线 1498、最终 1508、共享 1497、移除 1、新增 11、共享结果变化 17、不变 1480；计数分别满足 `1497+1=1498`、`1497+11=1508`，逐项清单见 `assistant-full-test-diff.md`／`.json`。完整 GPT R4 记录与后续验证补记见 `gpt-r4-review.md`。R4-1 的独立证据已在原 Store 源码上验证，仍待 R5 复核，不提前核销重要等级。

以下是 R3 后追加并在 R4 材料中提交的原重要发现闭环证据，须由 R5 继续核验：

1. **C5 当前反向突变**：移除代际比对使 `RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest` 唯一失败；旧 M51 测试已改名且不再称作 ItemId 比较独立突变。
2. **Store 输入代际**：移除 `incoming.Generation != 0` 使 `Upsert_NonzeroCallerGeneration_Rejected` 唯一失败。负数在 materialization 与统一输入守卫均拒绝，旧 M63 不称单分支独立证据。
3. **legacy**：v1/v2 非空存量缺 generation 时 gen0 旧请求在迁移前后均有效，Load 不改字节，新项分配 `int.MaxValue+1`；v3 已删 A(10)、当前仅余 B(9) 场景显式验证旧 `R_A(10)` 与 `R_A(int.MaxValue)` 均在重登后过期，Store 重建读回后再由 Store Consumer 验证。
4. **v4 与 Max**：非法矩阵覆盖 H/item generation 的缺失、null、错类型、负数、小数和溢出及 H/项关系；H=long.MaxValue 覆盖 Cleanup 取消、墓碑裁剪且 H 保留；损坏 Store Consume 响亮失败。
5. **持久化失败与 Cleanup**：目标替换失败及 DEBUG-only 部分临时写入失败均验证原文件逐字节不变；部分写失败还验证 temp 清理。跨线程 barrier 证明同路径另一 Store 写者在回调期间完成、外层抛冲突且字节与内层提交相同。仅追加空白但仍可解析为同队列的写入证明比较针对原始字节。
6. **路径别名边界**：`Path.GetFullPath` 字符串锁不解析 junction/symlink。生产构造链通过 `App.xaml.cs:98`、`MainViewModel.BgiExternal.cs:37`、`TaskCenterHost.cs:119`、`RunStore.cs:59` 及排除测试后的全仓构造点搜索确认只有 App 持有的单个 TaskCenterHost/Store；R5.3、Store 类注释和 `production-path-boundary.md` 明确规定并发 Store 必须使用同一规范路径表示或自行保证单写者，物理别名和跨进程写者不获保证。

## 验证结果

- 独立助手构建：成功，0 错误；1 个既有 NU1902 SharpCompress 中危警告。
- generation 定向 `LocalWaitGenerationContractTests`：45/45；`FullyQualifiedName~LocalWait`：190/190。
- 助手全量：1506 pass / 2 skip / 0 fail / 1508。实现前红基线 1479/2/17/1498。按 testId 的执行用例差集：共享 1497，移除 1、新增 11、结果变化 17、不变 1480；完整材料见 `assistant-full-test-diff.md` 和 `red-results-summary.md`。
- 10 个不同保护点反向突变各触发一个具名断言，legacy A(10)/B(9) 强反例加强后再次施突；item 溢出权威反向突变见 `mutations-r4/item-generation-overflow-original-parser/`，在原生产解析器上经非增量重建完成。源码逐字节恢复，哈希见 `reverse-mutations.md` 和 `mutations-r3/`。
- ClaimSurfaceGuardTests regen 与 no-env 均 1/1；manifest 对 HEAD 从 592 行增至 593 行，SHA-256 `A857167570E7E433E1795FCF2718F760E7877C1A79397EB4A3B899BEB3C566A0`。
- 所有 build/test 使用 `-p:DeployToBgiTools=false`。未做实机、真实 User、生产入口、R5.8、E3/E4/E5 或热键验证；这些门继续关闭。

## 请给出的复核结论

针对每个 R1/R2/R3 重要发现说明：已闭环或仍未闭环，依据对应代码/夹具/TRX，若未闭环列明反例、风险和验收条件。特别判断上述单生产 Store 的路径调用方约束是否足以界定本批并发保证，以及当前反向突变和差集是否足以进入收口。不得把尚未满足的事项降为建议。
