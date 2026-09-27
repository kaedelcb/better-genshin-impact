# SB21-2 红夹具运行摘要

- 命令：`dotnet test Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -p:DeployToBgiTools=false --filter FullyQualifiedName~LocalWaitGenerationContractTests --no-restore --logger "trx;LogFileName=sb21-2-red-confirmed.trx" --results-directory _batch21/sb21-2-review/red`
- 结果：22 项中 18 通过、4 失败、0 跳过。TRX：`_batch21/sb21-2-review/red/sb21-2-red-confirmed.trx`。
- 4 个命名失败分别是：Remove 后重登代际预期 1、墓碑裁剪后重登代际预期 1、`int.MaxValue` 重激活不得回绕、C5 不得接受 Remove 前缓存的快照。
- 第一轮红跑的回绕用例曾因测试载荷与 Store 记录不同而命中载荷冲突；已将第二次登记改为使用 Store 读回的同载荷对象并重跑。以上 4 项为校正后红跑结果，不把第一次失败计为有效缺陷证据。
- 构建成功；既有 SharpCompress NU1902 与若干 nullable/xUnit warnings 存在。该红跑只证明现实现下的定向反例，不证明助手全量状态。

## R1 重要意见后的独立反例

- 命令：`dotnet test Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -p:DeployToBgiTools=false --filter FullyQualifiedName~LocalWaitGenerationContractTests --no-restore --logger "trx;LogFileName=sb21-2-red-r1-review.trx" --results-directory _batch21/sb21-2-review/red-r1`
- 结果：24 项中 18 通过、6 失败、0 跳过。TRX：`_batch21/sb21-2-review/red-r1/sb21-2-red-r1-review.trx`。
- 六个失败分别为：`RemoveThenReregister...` 与 `PrunedTombstoneThenReregister...` 实际仍为 gen0；`ReactivationAfterIntMax...` 写侧拒绝回绕后的负数；`Consume_RemovedCachedSnapshot_IsExpired` 旧纯函数仍接受 Remove 前缓存对象；`Cleanup_ReentrantSamePathUpsert_IsNotLostByOuterSnapshot` 显示外层旧快照覆盖回调中的新登记；`LegacyV3WithDeletedHigherGeneration_ReservesWholeIntRange` 显示当前迁移没有预留已删除历史代际。
- C5 现在是 Remove-only 独立反例，不再依赖重登代际断言；cleanup 重入及 legacy 删项历史分别有独立失败证据。其余 18 项通过。构建仍成功；既有 NU1902、nullable 与 xUnit warnings 未在本轮处理。

## 最终实现前红夹具

- 命令：`dotnet test Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -p:DeployToBgiTools=false --filter FullyQualifiedName~LocalWaitGenerationContractTests --no-restore --logger "trx;LogFileName=sb21-2-red-final-before-implementation-v2.trx" --results-directory _batch21/sb21-2-review/red-final-before-implementation-v2`
- 结果：35 项中 18 通过、17 失败、0 跳过。TRX：`_batch21/sb21-2-review/red-final-before-implementation-v2/sb21-2-red-final-before-implementation-v2.trx`。
- 红例现覆盖 Remove/prune 回到 gen0、int.MaxValue 溢出、Remove-only 缓存消费、跨线程 Cleanup 重入覆盖、Cleanup 回调改写项身份/代际、legacy 已删高代际重用、v1/v2/v3 空文件迁移、首写 Remove/裁剪保留 H、long Trigger 修剪、v4 格式与严格根/项校验、拒绝非零来件代际、以及可变调用方对象不能改写分配代际。
- 17 个失败均由业务断言触发，没有编译失败；`Upsert_MutableCallerObject...` 证明当前 Store 实际把 GenItem 来件写成探针改写后的 99，`Cleanup_CallbackMutation...` 证明回调可把持久 ItemId 改成 `wait-mutated-by-callback`。具体失败名和诊断可从 TRX 重算。
- 同版本助手全量红基线：**1479 通过 / 2 跳过 / 17 失败 / 1498**，TRX：`_batch21/sb21-2-review/assistant-full-red-baseline-final/sb21-2-assistant-full-red-baseline-final.trx`。17 个失败均属于本批 `LocalWaitGenerationContractTests` 红夹具；两个 P50 负载诊断按既有规则跳过。该结果用于与修复后助手全量逐名比较。

## R5 会诊前最终绿验证（含 C5 显式消费计数，2026-09-27）

- `LocalWaitGenerationContractTests` 定向：**45/45** 通过，0 失败、0 跳过；`FullyQualifiedName~LocalWait`：**190/190**。最终原 Store 源码及 C5 计数断言测试项目非增量构建后的 TRX：`targeted-r5-counter/sb21-2-generation-r5-counter.trx`、`localwait-r5-counter/sb21-2-localwait-r5-counter.trx`。Remove 与 prune 重登从同一个 reopened Store 消费旧/新请求，并断言有效 1、过期 1。
- 助手项目独立非增量构建：`dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -p:DeployToBgiTools=false --no-restore --no-incremental --verbosity quiet`；0 错误、58 个警告。最终助手测试项目非增量构建 0 错误、79 个警告；R5 C5 反向突变构建 0 错误、81 个警告（包含预期 unreachable-code 警告）。日志分别为 `assistant-build-r5-original-parser.log`、`test-project-build-r5-counter-nonincr.log`、`mutations-r5/c5-consume-generation-valid/mutant-build.log`；警告含既有 SharpCompress NU1902 及 nullable/analyzer 警告。
- 助手全量：**1506 通过 / 2 跳过 / 0 失败 / 1508**；最终 TRX：`assistant-full-r5-counter-final/sb21-2-assistant-full-r5-counter.trx`。按 TRX 执行用例 `testId` 对比：基线 1498 项、最终 1508 项，共享 1497 项，移除 1 项旧 C5 测试名，新增 11 项（含该用例语义更名版本、另 9 项绿夹具和 1 项 item 溢出独立夹具）；17 项共享用例由 Failed 转 Passed，其余 1480 项共享结果不变，2 个 P50 opt-in 跳过在共享不变集合内。计数守恒：1497+1=1498、1497+11=1508。最新逐项 ID/名称/结果见 `assistant-full-test-diff-r5-counter.md` 和 `.json`；旧差集仍保留在 `assistant-full-test-diff.md/.json`。移除的是 `Consume_ItemIdMismatch_And_NoGeneration_AreExpired`，新增更名用例为 `Consume_UnknownItemId_And_NoGeneration_AreExpired`；没有材料外基线用例被删除或改名。
- 声明面：R5 前最终文档 regen/no-env 均 **1/1**；TRX：`claim-r5-counters-final-regen/sb21-2-claim-r5-counters-final-regen.trx`、`claim-r5-counters-final-noenv/sb21-2-claim-r5-counters-final-noenv.trx`。相对 HEAD 的 592 行，manifest 新增 §24.122 一行，当前 593 行、SHA-256 `A857167570E7E433E1795FCF2718F760E7877C1A79397EB4A3B899BEB3C566A0`。
- 既有 9 个保护点反向突变、本轮 item 溢出保护突变与 R5 C5 代际比较突变均由具名业务断言检出；item 溢出的权威证据是在原 Store 源码非增量编译后完成。更早失败跑的二进制身份与原因未定，不作为原实现缺陷证据或验收依据。源码 SHA 精确恢复；细节见 `reverse-mutations.md`、`mutations-r3/`、`mutations-r4/item-generation-overflow-original-parser/` 与 `mutations-r5/c5-consume-generation-valid/`。
- 所有构建与测试命令均指定 `-p:DeployToBgiTools=false`。没有运行 BGI 实机、真实 User 或生产入口验证；门禁继续关闭。

## R5 意见修订后的恢复态复验（2026-09-27）

- 修正 long-generation Trigger 夹具：不同 generation 的条目分开传入显式代际批次；修订后测试项目非增量构建 0 错误/79 警告，generation 定向 **45/45**、全部 LocalWait **190/190**、助手全量 **1506 通过/2 跳过/0 失败/1508**。恢复态 TRX 在 `targeted-r5-restored-final/`、`localwait-r5-restored-final/`、`assistant-full-r5-restored-final/`；构建日志 `test-project-build-r5-final-nonincr.log`。
- 针对修订后的 long-generation 断言反向突变 `PruneHandledExceptGeneration` 的 `long.TryParse` 为 `int.TryParse`：非增量构建通过，命名测试在 `Assert.Equal` 处因预期裁剪 1、实际 0 失败；Trigger 源码长度恢复为 40660 字节，SHA 精确恢复 `122D5D5ECB2097A9AFE771ADBBC9876396A02C07A97094A22DB34AF175A070EC`。证据见 `mutations-r5-note-fixes/trigger-long-generation/`。
- 对恢复态全量 TRX 按 testId 再算差集：基线 1498、最终 1508、共享 1497、移除 1、新增 11、变化 17、不变 1480；与已记录差集的 testId 集合和结果完全相同。最终路径已写入 `assistant-full-test-diff-r5-counter.md/.json`。
- 最终 R5.3 文本之后 `ClaimSurfaceGuardTests` regen/no-env 各 1/1；manifest 保持 593 行、SHA-256 `A857167570E7E433E1795FCF2718F760E7877C1A79397EB4A3B899BEB3C566A0`，regen TRX 在 `claim-r5-closeout-final-regen/`，最终 no-env TRX 在 `claim-r5-final-verified-noenv/`。一次拼错项目路径的无变量调用未启动测试，日志留存于 `claim-r5-exact-final-noenv/`。
- `item-generation-overflow-original-source-probe/evidence.json` 的早期失败归因已改为“二进制身份不明确、不据此推断”；精确恢复仍只引用 `mutations-r4/item-generation-overflow-original-parser/evidence.json`。GPT R5 返回未发现新的 MUST/IMPORTANT，并认可 R4-1/2 可闭环；详见 `gpt-r5-review.md`。

- R4 指出的 item-generation 溢出遮蔽通过独立夹具闭环：合法根 H=`long.MaxValue`、item generation=`9223372036854775808`。保存的原 Store 源码非增量构建后 `Load_V4` 根/项两测试通过 2/2；在原 `TryGetValue<long>` item 解析失败分支上做饱和接受突变，该独立测试以“未抛异常”失败，源码 SHA `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289` 精确恢复。权威记录、日志和 TRX 见 `mutations-r4/item-generation-overflow-original-parser/`。旧 `item-generation-overflow-original-source-probe/` 哈希不一致、二进制身份不明，不能证明精确恢复也不能据此归因早期失败。
- 全量差集按 TRX `testId` 执行用例：共享 1497、移除 1、新增 11、共享结果变化 17、其余共享结果不变 1480；算术分别为 1497+1=1498、1497+11=1508。逐 ID 清单见 `assistant-full-test-diff-r5-counter.md` 和 `.json`；R4 会诊及后续处置见 `gpt-r4-review.md`，R5 闭环状态待本轮会诊返回后更新。
