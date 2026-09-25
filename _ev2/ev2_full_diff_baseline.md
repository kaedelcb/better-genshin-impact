# ev2 全量回归失败差集核验（判据 3 产物；第 1 轮必改2 处置，第 2 轮后以最终帧重算）

- 最终帧：`Test/BetterGenshinImpact.UnitTest/TestResults/_ev2_bgi_full_final5.trx`（第 5 轮修复后形态＝FIX-8 断言补强后重做；第 7 轮建议 4 更正父注）
- 基线：`Test/BetterGenshinImpact.UnitTest/TestResults/r58_bgi_full_20260924.trx`（既有 14 项）

| 项 | 值 |
|---|---|
| 最终帧计数 | 1045 过/14 败/1059 总 |
| 基线计数 | 1035 过/14 败/1049 总 |
| 新增失败（最终帧−基线） | 0 项（空集） |
| 消失失败（基线−最终帧） | 0 项（空集） |

## 最终帧 14 项失败逐名清单（全部为既有基线身份）
- BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests.OcrResultTests.Text_PreservesOfficialDetectionOrder
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.MultiWorldConfigLockingBugTests.Round1_HostUploadConfig_ShouldSaveToFirstHostConfig
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.MultiWorldConfigLockingBugTests.Round1_MemberConfigFetchFails_ShouldTerminateMultiWorld
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.MultiWorldConfigLockingBugTests.Round1_MemberFetchConfig_ShouldSaveToFirstHostConfig
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.MultiWorldConfigLockingBugTests.Round2Plus_HostFirstConfigNull_ShouldTerminateMultiWorld
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.PostTeleportRevivalProtectionBugConditionTest.BugCondition_A1_RevivalWithin2s_ShouldNotSkipToNextSegment
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.PostTeleportRevivalProtectionBugConditionTest.BugCondition_A2_RevivalAtExactly10s_ShouldNotSkipToNextSegment
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.PostTeleportStuckProtectionDecisionsUnitTest.IsEligible_Stuck_BeyondWindow_ReturnsFalse
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.PostTeleportStuckProtectionPbtPropertiesTest.IsEligible_BoundaryZeroAndTwenty_Stuck
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.WaitPointReportTests.WaitPointReport_Creation_IsValidWithRequiredFields
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.WaitPointReportTests.WaitPointReport_ExpiryCheck_WorksCorrectly
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.WaitPointReportTests.WaitPointReport_ExtractRouteIndex_HandlesVariousFormats
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.WaitPointReportTests.WaitPointReport_SyncPointIdValidation_WorksCorrectly
- BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests.AutoTrackPositionRecoveryDecisionsTest.Decide_TimesGte4_AlwaysThrowRetry

## 本批夹具入帧核验
- TerminalSplitCharacterization 入帧 10 例，outcome：Passed

解析逻辑：TRX XML UnitTestResult@outcome==Failed 逐名集合差。
