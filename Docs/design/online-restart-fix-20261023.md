# BGI 重启后上线通知遗漏修复（2026-10-23）

基线：53baeb56af0d1365e3f50e72a08a9822b1f52174，分支 main-OldTeaBag-B182。

目标：修复海带屁骨汤 06:35 上线通知丢弃，保证 BGI 重启后的新上线可被处理，验证停止、定时、防重复与后续任务。

## 原因与修改

DisposePipe 清空 ServerEpoch，后续握手却用它比较旧身份；无接管票据的重启漏发 EpochChanged，MainViewModel 的旧状态序号门槛无法复位。SDK 已提高快照覆盖线，业务层却拒收快照，online.triggered 同时被覆盖线过滤。

仅修改 BgiExternalClient：独立保存最后验证成功的进程身份；断线时当前身份仍失效；握手完成本 Windows 会话及实际管道身份验证后，与历史身份比较。重启准确触发已有清理入口，同进程重连保留原接管责任。没有修改普通事件过滤、上线时效、去重、停止、定时与后续执行策略。

## 验证

- 源码预检通过，改前字节、SHA256、BOM、换行与原文件保存在 Build/online-restart-20261023-01a16739/before*。
- 使用 DeployToBgiTools=false 完成助手 Debug 与服务端 Debug 构建。助手构建保留现有 SharpCompress NU1902 警告。
- 最终定向回归 34/34：OnlineRestartEpochTests、OnlineIntentLifecycleResetTests、HighestPriorityStopTests、CoordinatedLocalFinishWiringTests、QuickHoeingCompletionExecutionTests、ConfigTransferScheduleRegressionTests。
- 新用例使用真实 SDK 握手及 MainViewModel 快照回调、独立命名管道。模拟旧进程身份和旧序号门槛，验证重启、失败握手后重试、新鲜/过期上线、手动停止后迟到通知、定时序号与重复 AllReady、同进程有/无接管票据重连。
- 修复前源码对照：两个新增重启用例均在“应通知一次、实际零次”断言失败；修复后通过。对照期间仅临时恢复本次源文件，结束已恢复修复版本并重新构建。
- FourMemberFinishEntryTests 独立 testhost 通过，单用例顺序执行 11 个场景：本端完成与队友失败、本端完成与重连、普通任务失败隔离、旧任务取消、运行中停止后新意图、原任务接续（含配置移除、优先恢复、恢复中停止）、换房间（含回执丢失）。使用真实本地 SignalR 服务器和四个客户端，BGI 为私有管道替身。各场景日志保存在 four-member-user。

## 保留失败与边界

首次关联回归 57 项中 49 通过、8 超时。修复前源码对照相同 8 项仍超时，未修改测试或宣称修复：

- CoordinatedBatchLocalFinishTests.RequiredFinishEvidenceCannotTreatMissingOrInconsistentTerminalAsZeroNativeAttempts：missing、unsealed、active、inconsistent、invalid_counts 共 5 项，5 秒超时。
- PersistentlyUnavailableReadsEndAfterBoundedReconciliationAndNeverResubmit(jobs:false)：5 秒超时。
- PriorityJointAbort_AfterLocalSuccess_DoesNotWaitForeverOrStartCompletion：2 秒超时。
- PriorityCompletion_WaitsForJointCompletionBeforeStartingBoundAction：2 秒超时。

这些既有失败未关闭，本次修复未新增这些失败。测试数据均使用显式隔离 NEXUSBGI_DATA_ROOT；没有部署、push、操作真实游戏或真实 User。隔离入口验证不是四个真实游戏客户端验收。
