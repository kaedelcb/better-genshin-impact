# SB21-2 助手全量 TRX 执行用例差集（含 C5 计数断言后复跑）

本报告对照最终复跑 TRX `_batch21/sb21-2-review/assistant-full-r5-restored-final/sb21-2-assistant-full-restored-final.trx`；其 testId 差集与前一版 JSON/Markdown 完全相同（added=True, removed=True, changed=True, outcomes=True）。

按 TRX 属性 testId 比较执行用例（不是测试显示名）；基线 1498，最终 1508，共享 1497，移除 1，新增 11，结果变化 17，共享且结果不变 1480。

计数校验：基线 1497+1=1498；最终 1497+11=1508。

## 移除（仅旧 C5 用例重命名）

| testId | 用例名 | 基线结果 |
|---|---|---|
| 89e52975-566b-2c30-6266-326c2bb664b2 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Consume_ItemIdMismatch_And_NoGeneration_AreExpired | Passed |

## 新增

| testId | 用例名 | 最终结果 |
|---|---|---|
| c578c4d1-8c94-6d45-4c82-97dde6ed6292 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Cleanup_RejectsRawByteChangeEvenWhenQueueStillParsesTheSame | Passed |
| 1a6fd421-c147-72bd-bf97-4f0f29722972 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Consume_CorruptStoreFailsLoudly | Passed |
| 63f367d0-1178-dd85-c327-26e5e7e0df0f | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Consume_UnknownItemId_And_NoGeneration_AreExpired | Passed |
| 3914599a-8bcb-17d5-0b53-3717e2394a25 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Load_V4ItemGenerationOverflowWithValidHighWater_FailsClosedWithoutMutation | Passed |
| ef3d8eef-ad86-0910-7c0b-550eeb8f5451 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LongGeneration_RoundTripsThroughStoreTriggerConsumerAndPruning | Passed |
| cf17108e-f657-1dcf-4459-357a89b39b47 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LongMaxValue_CleanupCancelAndPruneRemainAvailableWithoutLoweringHighWater | Passed |
| abc66a89-927d-2d67-5ee9-72b7a78a24bd | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LongMaxValue_LastAllocationSucceeds_ThenRejectsNewGenerationsWithoutChangingFile | Passed |
| c35068d3-257e-584b-f218-84cd364ab584 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.NonEmptyLegacyV1V2_PreservesGen0RequestAndReservesRangeForNewLifecycle(version: 1) | Passed |
| 7395ccf0-334a-b58c-49e3-cce9c8e13508 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.NonEmptyLegacyV1V2_PreservesGen0RequestAndReservesRangeForNewLifecycle(version: 2) | Passed |
| cc29b660-0a24-79a5-53e6-7ce49d55af0e | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Persist_PartialTemporaryWriteFailurePreservesPreviousQueueBytesAndCleansTemp | Passed |
| 1b770f31-fabf-108e-9cff-f320a659957a | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Persist_ReplaceFailurePreservesPreviousQueueBytes | Passed |

## 共享用例结果变化

| testId | 用例名 | 基线结果 | 最终结果 |
|---|---|---|---|
| 38be1be3-a879-fd2e-16de-405fc8ff2688 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Cleanup_CallbackMutationCannotAlterPersistedIdentityOrGeneration | Failed | Passed |
| 517f96ad-7e82-6368-ec96-b9b30cbcaec7 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Cleanup_ReentrantSamePathUpsert_IsNotLostByOuterSnapshot | Failed | Passed |
| 0f1b4b43-2174-bb8e-2959-449657fcc9d8 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Consume_RemovedCachedSnapshot_IsExpired | Failed | Passed |
| e2d79df7-7b4c-88ab-daeb-402665514a88 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.EmptyLegacyFile_ReservesLegacyIntRangeWithoutLoadMutation(version: 1) | Failed | Passed |
| d8716125-dfe6-69c4-eb64-c8a8da2c2128 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.EmptyLegacyFile_ReservesLegacyIntRangeWithoutLoadMutation(version: 2) | Failed | Passed |
| 82fbdb40-2ad8-7b1e-4831-8a5d4d4b7dab | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.EmptyLegacyFile_ReservesLegacyIntRangeWithoutLoadMutation(version: 3) | Failed | Passed |
| fb199ce7-c83e-19ee-ddd3-cb678550198e | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LegacyMigration_PruneLastTombstoneOnFirstWrite_PreservesMigratedHighWater | Failed | Passed |
| a02885b6-4038-8229-b4cd-d25ee1158162 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LegacyMigration_RemoveHighestItemOnFirstWrite_PreservesMigratedHighWater | Failed | Passed |
| 8393d549-abb1-af1f-e6f6-03a52fb289d8 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.LegacyV3WithDeletedHigherGeneration_ReservesWholeIntRange | Failed | Passed |
| c6bfe26a-d9dc-545e-e8b2-f26eb0797bae | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Load_V4MissingOrInvalidHighWaterAndItemGeneration_FailsClosed | Failed | Passed |
| 4cb96399-456d-c567-088d-863b7508577c | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.PrunedTombstoneThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest | Failed | Passed |
| 16c35e31-f480-a72b-fc90-21246e4e2c42 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.PruneHandledExceptGeneration_SupportsLongGeneration | Failed | Passed |
| ea0be47e-99ed-ca63-8386-c5d2c5caaef6 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.QueueFileVersion_AdvancesForPersistedHighWater | Failed | Passed |
| fe1be7b9-f02e-daf5-2bb4-2fdcdfefe12b | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.ReactivationAfterIntMaxGeneration_DoesNotWrap | Failed | Passed |
| b4e46616-068c-bf5c-8169-c99dce4a5a09 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.RemoveThenReregister_SameIdentity_AdvancesGenerationAndExpiresOldRequest | Failed | Passed |
| bad4e68b-9422-901d-fa68-24049aa9f961 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Upsert_MutableCallerObjectCannotRewriteAllocatedGenerationBeforePersist | Failed | Passed |
| 53ab48b1-5f5c-a1a9-8155-3c4e89f10323 | MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitGenerationContractTests.Upsert_NonzeroCallerGeneration_Rejected | Failed | Passed |

其余 1480 个共享 testId 结果不变；2 个 P50 opt-in 跳过项包含在其中。

