# SB21-2 当前分支反向突变记录

所有测试使用 `-p:DeployToBgiTools=false`；每次只改一个保护点，运行表中唯一命名测试，之后按原始字节恢复源码。既有 9 个不同保护点均由各自测试失败检出，恢复 SHA 与突变前一致；R4 补证后，第 10 个 item overflow 保护在原 Store 源码强制非增量重编译后，用独立测试重新突变验证，未留下生产解析代码变化。

| 保护点 | 突变 | 检出测试 | 恢复 SHA-256 |
|---|---|---|---|
| Remove 保留 H | Remove 后按剩余项重算 H | `RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest` | Store `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289` |
| 裁剪保留 H | Cleanup 裁剪后按剩余项重算 H | `PrunedTombstoneThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest` | Store 同上 |
| legacy 全 int 域预留 | 迁移 H 改为仅取当前项最大代际 | `LegacyV3WithDeletedHigherGeneration_ReservesWholeIntRange`：已删 A(10)、只剩 B(9) 时旧 R_A(10) 实际通过 | Store 同上；强化夹具二次突变证据见 `mutations-r3/legacy-reservation-v2/legacy-reservation-v2.trx` |
| v4 根 H 必需 | 绕过 v4 根 `generationHighWater` 读取守卫 | `Load_V4MissingOrInvalidHighWaterAndItemGeneration_FailsClosed` | Store 同上 |
| v4 item generation 溢出 | 将原 `TryGetValue<long>` 失败分支改成饱和 `long.MaxValue` | `Load_V4ItemGenerationOverflowWithValidHighWater_FailsClosedWithoutMutation` | Store `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`；权威证据 `mutations-r4/item-generation-overflow-original-parser/evidence.json` |
| Cleanup 原始字节比较 | 绕过原始快照比较 | `Cleanup_RejectsRawByteChangeEvenWhenQueueStillParsesTheSame` | Store 同上 |
| C5 代际比较 | 绕过请求代际与当前代际比较 | `RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest` | Consumer `CCACFF30CB464409C323F1C7ABAD3AEA828BFB0887C6BFB74316DA1EEDA9D324` |
| Trigger long 修剪 | `long.TryParse` 改回 `int.TryParse` | `LongGeneration_RoundTripsThroughStoreTriggerConsumerAndPruning` | Trigger `122D5D5ECB2097A9AFE771ADBBC9876396A02C07A97094A22DB34AF175A070EC` |
| Max 分配拒绝 | 移除 `long.MaxValue` 分配边界守卫 | `LongMaxValue_LastAllocationSucceeds_ThenRejectsNewGenerationsWithoutChangingFile` | Store 同上 |
| 调用方不能自报代际 | 移除 `incoming.Generation != 0` 输入守卫 | `Upsert_NonzeroCallerGeneration_Rejected` | Store 同上；TRX `mutations-r3/caller-generation-guard/caller-generation-guard.trx` |

主矩阵结果、mutant/restored SHA 与日志：`mutations-r3/results.json` 和各项 `mutant.log`/TRX；另有调用方代际守卫突变与强化后的 legacy 突变 TRX。9 个旧保护点均由各自命名业务断言失败（不是编译失败）；legacy 案例在红断言加入具体旧 R_A(10) 消费验证后重新施突，最新证据为 `legacy-reservation-v2`。全部源码 `exact_restore=true`。追加的部分临时写入失败、持久化替换失败与 C5 损坏 Store 测试由当前 45/45 与 190/190 绿回归执行。

R4 复核后追加第 10 个独立证据点：v4 item generation Int64 溢出。保存的原 Store 源码经 `--no-incremental` 编译后，根 H 合法 `long.MaxValue` 的独立溢出夹具通过；再将原 `TryGetValue<long>` 失败分支突变为饱和接受 `long.MaxValue`，独立测试因目标溢出项未抛异常而失败。目标代码恢复 SHA 与突变前同为 `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`；日志/TRX/哈希见 `mutations-r4/item-generation-overflow-original-parser/`。因此当前主保护点共 **10 项**，legacy A(10)/B(9) 强化突变另计一次。更早突变后未强制重建的运行不作为证据。

历史 M51/M63 标注：Store-backed Consume 按请求 ItemId 查询，所以旧 “ItemId mismatch” 测试实际走“项不存在”；Generation 非 0 的统一输入守卫亦会拒绝负数，即使其中一个负数形状守卫被单独移除。因此两处旧标签不作为当前单分支独立突变证据；本表使用当前可观察的 C5 ABA 比较及统一来件边界证据。

## R5 C5 持久化读回与消费计数复核

Remove 与 prune 重登测试现用**同一个重开 Store**读取持久化的新代际，再消费旧请求与新请求，分别断言 `Expired=1`、`Valid=1`。Consumer 仍是纯复核组件，不依赖 sender；生产调用点为零，所以本批没有真实发送计数，发送入口结构性不存在，生产发送门保持关闭。

针对这两条计数断言再次反向突变 C5 代际比较：把 `reqGen != currentItem.Generation` 改为 `false`，非增量测试项目构建成功（0 错误、81 警告），`RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest` 在旧请求的 `Assert.False` 处失败；源文件恢复后 SHA 与突变前同为 `CCACFF30CB464409C323F1C7ABAD3AEA828BFB0887C6BFB74316DA1EEDA9D324`。权威证据为 `mutations-r5/c5-consume-generation-valid/evidence.json`、`mutant-test.log` 和 TRX；恢复后的非增量测试项目构建为 0 错误、79 警告，后续 45/45、190/190 与助手全量 1506/2/0/1508 全绿。

R5 修复 long-generation 辅助项的单代际批次不一致后，再对 `PruneHandledExceptGeneration` 将 `long.TryParse` 改为 `int.TryParse`。非增量构建 0 错误；`LongGeneration_RoundTripsThroughStoreTriggerConsumerAndPruning` 以 `Assert.Equal` 预期裁剪 1、实际 0 失败（断言细节以目标测试输出/TRX 为据）。Trigger 源码原始/恢复 SHA 均 `122D5D5ECB2097A9AFE771ADBBC9876396A02C07A97094A22DB34AF175A070EC`，原/恢复长度均 40660 字节。证据见 `mutations-r5-note-fixes/trigger-long-generation/evidence.json`、`mutant-build.log`、`mutant-test.log` 与 `test-results/long-generation-intparse-mutant.trx`。

同一变异曾有一次无效首跑：把 `--no-incremental` 传给 `dotnet test`，命令被 MSB1001 拒绝；该失败不是断言证据，保存在 `mutations-r5/c5-consume-generation/evidence.json` 并标 `mutationEvidenceValid=false`。只认上段有效的先 build、后 `dotnet test --no-build` 记录。
