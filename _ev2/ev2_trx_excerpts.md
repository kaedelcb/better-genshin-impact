# ev2 TRX 摘录（18 帧；解析逻辑：TRX XML Counters＋UnitTestResult@outcome）

| 帧 | 通过/失败/总数 | 身份 |
|---|---|---|
| _ev2_targeted_green.trx | 10/0/10 | 定向绿（初版夹具形态） |
| _ev2_targeted_green2.trx | 10/0/10 | 定向绿（第1轮突变还原后） |
| _ev2_targeted_green3.trx | 10/0/10 | 定向绿（第1轮加固后） |
| _ev2_targeted_green4.trx | 10/0/10 | 定向绿（第2轮修复后） |
| _ev2_targeted_green5.trx | 10/0/10 | 定向绿（第3轮修复后） |
| _ev2_targeted_green6.trx | 10/0/10 | 定向绿（第5轮修复后终帧） |
| _ev2_mutM1_red.trx | 4/6/10 | 突变红 M1（第5轮修复后形态重做） |
| _ev2_mutM2_red.trx | 5/5/10 | 突变红 M2（第5轮修复后形态重做） |
| _ev2_mutM3_red.trx | 9/1/10 | 突变红 M3（第5轮修复后形态重做） |
| _ev2_mutM4_red.trx | 9/1/10 | 突变红 M4（第5轮修复后形态重做） |
| _ev2_mutM5_red.trx | 9/1/10 | 突变红 M5（第5轮修复后形态重做） |
| _ev2_mutM6_red.trx | 7/3/10 | 突变红 M6（方向反转；第5轮修复后形态重做） |
| _ev2_mutM7_red.trx | 9/1/10 | 突变红 M7（队列满注册表写入拆除，第5轮新增） |
| _ev2_mutM6_jobregistry_red.trx | 5/2/7 | 佐证帧：M6 下 JobRegistryTests 先终态者赢合同夹具实跑红（第5轮建议4） |
| _ev2_bgi_full_final2.trx | 1045/14/1059 | 全量回归（第1轮加固后；被最新帧取代留档） |
| _ev2_bgi_full_final3.trx | 1045/14/1059 | 全量回归（第2轮修复后；被最新帧取代留档） |
| _ev2_bgi_full_final4.trx | 1045/14/1059 | 全量回归（第3/4轮形态；被最新帧取代留档） |
| _ev2_bgi_full_final5.trx | 1045/14/1059 | 全量回归（最终帧：差集=基线空集） |

## 突变红帧失败名单（守护断言归因）

### _ev2_mutM1_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesRejected_ReturnsTrue_QueueCompleted_JobRejectedNotCancelled — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome(outcome: Rejected, code: "task_busy") — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome(outcome: Failed, code: "prerequisite_failed") — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_ExecutorCancelled_QueueCompletedCancelled_JobCancelledWithUserReason — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_ExecutorPreWritesSucceeded_QueueCompleted_JobSucceeded — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_NoPreWrittenTerminal_QueueCompleted_JobSucceeded — Assert.Equal() Failure: Strings differ            ↓ (pos 0) Expected: "completed" Actual:   "failed"            ↑ (pos 0

### _ev2_mutM2_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_NoPreWrittenTerminal_QueueCompleted_JobSucceeded — Assert.Equal() Failure: Values differ Expected: Succeeded Actual:   Running
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_SlotWaitTimeout_QueueFailedTaskBusy_JobFailedTaskBusy — Assert.Equal() Failure: Values differ Expected: Failed Actual:   Queued
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_QueueCancelWhilePending_QueueQueueCancelled_JobCancelledUser — Assert.Equal() Failure: Values differ Expected: Cancelled Actual:   Queued
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_ExecutorThrows_QueueFailedTaskStartFailed_JobFailedSameCode — Assert.Equal() Failure: Values differ Expected: Failed Actual:   Running
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_ExecutorCancelled_QueueCompletedCancelled_JobCancelledWithUserReason — Assert.Equal() Failure: Values differ Expected: Cancelled Actual:   Running

### _ev2_mutM3_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.SameSource_NormalPath_ExecutorCancelled_QueueCompletedCancelled_JobCancelledWithUserReason — Assert.Equal() Failure: Values differ Expected: Cancelled Actual:   Succeeded

### _ev2_mutM4_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_QueueCancelWhilePending_QueueQueueCancelled_JobCancelledUser — Assert.Equal() Failure: Values differ Expected: Cancelled Actual:   Succeeded

### _ev2_mutM5_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_SlotWaitTimeout_QueueFailedTaskBusy_JobFailedTaskBusy — Assert.Equal() Failure: Strings differ                 ↓ (pos 5) Expected: "task_busy" Actual:   "task_start_failed"    

### _ev2_mutM6_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome(outcome: Rejected, code: "task_busy") — Assert.Equal() Failure: Values differ Expected: Rejected Actual:   Succeeded
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome(outcome: Failed, code: "prerequisite_failed") — Assert.Equal() Failure: Values differ Expected: Failed Actual:   Succeeded
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Split_NormalPath_ExecutorPreWritesRejected_ReturnsTrue_QueueCompleted_JobRejectedNotCancelled — Assert.Equal() Failure: Values differ Expected: Rejected Actual:   Cancelled

### _ev2_mutM7_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Instance.BgiTaskCoordinatorTerminalSplitCharacterizationTests.Fallback_QueueFull_RejectionLivesOnlyInRegistry_NoQueueObservation — Assert.True() Failure Expected: True Actual:   False

### _ev2_mutM6_jobregistry_red.trx
- BetterGenshinImpact.UnitTest.ServiceTests.Execution.JobRegistryTests.TryMarkTerminal_FirstWriterWins_SecondMarkIsNoop — Assert.False() Failure Expected: False Actual:   True
- BetterGenshinImpact.UnitTest.ServiceTests.Execution.JobRegistryTests.Submit_WithParentJobId_ChildrenLinkToDragonParent — Assert.False() Failure Expected: False Actual:   True
