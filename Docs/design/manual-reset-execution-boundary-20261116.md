# 联机状态重置：物理复位与旧执行撤权

## 故障与约定

2026-11-16 房间 B54387 的上一轮联机批次已 aborted，但未全员 cleaned，服务器因此拒绝新的“精英”快捷启动并返回 `previous_batch_active`。Dream、渺茫（101115769）的多次重置回执均表示 BGI 已复位、任务中心发送闸门仍关闭。现有日志不能确定该成员具体卡住的内部运行号。

人工“重置状态”必须在确认旧 BGI 退出、新 BGI 执行环境和外部链路就绪后，撤销旧任务继续发送、写回、恢复和修改发送闸门的权限。旧任务不响应协作取消、旧发送回执丢失、旧取消回调不返回，不再拥有否决此次重置的权力。原 Unknown、失败及历史事实保留，不能改写为执行成功。

## 实现

- `CommandExecutor.ForceResetAllAsync` 继续负责受控重建 BGI、核对新纪元与就绪状态。重建或实际就绪失败仍返回失败。
- `TaskCenterDriveAuthority` 给批次、驱动和异步回调保留开始时的权限代。物理复位后撤权；运行记录、仲裁发布及执行端口的发送/取消边界拒绝旧代。权限锁不跨远端 await。
- `CompleteManualResetAfterPhysicalBgiResetAsync` 分离旧驱动，并替换发送信号量和任务流程副作用信号量。旧租约只释放其原信号量；旧 finally 只能移除自身 entry。
- `.physical-reset.json` 独立记录复位时精确的旧 RunId 集合。正常所有者写回失败时仍可排除旧运行，助手重启后也不能恢复它们；后续新 RunId 不受影响。损坏旧屏障先按原字节留存，再由本次物理复位建立新屏障。新屏障无法持久化或读回时不开放发送。
- 原批次的取消请求和退出继续被观察，但不等待其回调返回才报告已经确认的物理释放。普通取消不再在批次策略锁中运行用户取消回调。重复 resetId 复用原结果，不能再次重建后继 BGI。
- 清理确认丢失时，只为服务器仍持有的同一旧批次申请新的 resetId。旧批次已不存在或已经换代时不操作后继批次。服务器允许唯一在线的新助手实例通过一次新的明确重置确认旧批次清理；不能借此继续旧批次执行。批次核对、清理授权与房间就绪边沿清除共用同一次批次锁，避免并发新批次插入后被旧重置清理。

## 验证口径

验证要决定的是：正式重置入口在旧执行不配合退出时能否恢复发送，并且迟到旧执行不能影响新任务。结束判据是隔离入口发送新任务成功、服务器旧批次清理后受理下一批次，以及故障与身份边界回归通过。编译成功本身不满足结束判据。

新增和强化的场景：

- 正式 WPF 成员卡片“重置状态”按钮及确认对话框：保留未完成旧 RunTask、卡住的取消回调和占用中的旧发送租约；重置完成后，通过私有命名管道成功发送 `ext.task.start`，重复 resetId 不影响后继任务。
- 发送门：旧 waiter 被换代唤醒，旧 lease 的迟到 Dispose 不释放新门，旧权限不能 Open/Close 或发送。
- 任务中心：所有者写回不可用时保留 Unknown 原件，重载后仍排除旧运行并允许下一轮 Prepare；损坏屏障可再次重置；同 workflow 的旧 finally 不移除新驱动。
- 任务流程：外部线程送来的旧日志回调不能修改寄存器，辅助程序旧回执丢失不占住新代副作用门，迟到结果不能继续旧动作链。
- 服务器：清理授权缓存丢失后可重新授权同一旧批次；更换助手实例必须先做新的明确重置；旧实例及迟到旧成员消息不能修改下一批次。
- 真实本地 Kestrel/SignalR 网关和正式 WPF 房间刷新入口：核对成功/失败成员回执、断线重连、刷新失败和重复 ACK。

所有构建/测试使用 `tools/mistletoe/validate_command.py` 的隔离 `{artifacts}` 输出和显式 `NEXUSBGI_DATA_ROOT`，`DeployToBgiTools=false`。临时构建输出由存储 Session 回收，仅保留命令、结果及必要日志。

## 实际验证记录

- 服务器 `CoordinatedBatchGatewayTests` 与 `CoordinatedStopRoutingTests`：32 项通过，0 失败、0 跳过；包含清理授权丢失和更换执行实例的新回归。最终服务器同锁修改后的重验记录位于 `Build/validation-runs/48b9d399a3c442e9ba5b02c886b56862/records`：`server.trx` 32 项、`gateway.trx` 真实网关 2 项及 `room.trx` 正式房间入口 1 项全部通过，0 失败、0 跳过。此前记录 `Build/validation-runs/bbec7110adc54a34bf5fb03867a8be3a/records` 属于同锁修改之前，保留但不替代最终重验。
- 首轮助手验证：编译成功，但 `TaskCenterSuccessorPathGateTests.OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure` 多场景失败，整条命令在 600 秒超时，不能记作通过。原记录位于 `Build/validation-runs/b58428fe73504ab2bd6a08baa88e8037/records`。同一 Job 的清理证明为 `active_processes=0`；存储账本保留 `failed`，已按工具清单回收 281,564,998 字节生成文件。当前根既有 `Build/online-chain-original-host-stop-exited.log` 也记录相同断言失败，但缺少其构建输入身份，不能充作同 HEAD 的基线复跑。
- 对失败链做只读源码对照：`HEAD` 已有 `ObserveDriveAsync` 对 Unknown 调用 `RetireUnknownSubmission/RetireUnknownOperations`，将节点移至 `RetiredUnknownOperations`；`SettleOriginalNodeAcceptanceAsync` 对隔离操作只读 `CurrentSubmission` 并提前返回，不调用门面结清。它与测试查活动槽、故障注入门面，以及把提交转入历史后再结清的约定冲突。本轮未改该结清分支；该组测试也未调用物理复位。原轮普通恢复/停止结清的已知缺口保留未解决，不宣称这组测试通过；全局人工重置通过独立撤权和持久屏障解除旧执行占用。
- 最终助手源码的隔离验证记录：`Build/validation-runs/e2941e65033b450fa71a9331a0211dca/records`。`core.trx` 183 项通过，包含本次新增的全部任务中心撤权、发送门换代、损坏屏障、卡住回调及清理授权恢复用例；`button.trx` 正式 WPF 按钮 1 项通过，实际命名管道发送新任务成功；`gateway.trx` 真实本地网关 2 项通过；`room.trx` 正式房间刷新入口 1 项通过（内部含正常连接、断线重连、刷新失败三条件）。上述 187 项均 0 失败、0 跳过。
- 自动回收完成：助手验证释放 358,729,894 字节；最终服务器/网关重验释放 280,894,834 字节，回收记录 `3171ce7ff22b40fa95534b9ae3046ab0` 状态 `complete`，进程终态 `active_processes=0`。保留 TRX、命令、必要日志与原失败事实；未清理真实运行目录或 User。

## 源码与运行边界

本次开始时 HEAD：`0fca90ba709f12f04b4b9da2a4d9bb3649308a79`。

原源码接续基线：`0c83480ee53311dbceb3d4668c0acc3c2bd91428`。完成验证和提交后方可前移本机源码基线；它不代表整版产品验收。

隔离验证不修改真实 User，也不替换正在运行的助手或线上服务器。运行环境需要安装本次助手和服务器修复后才具有上述行为；游戏内实际效果未在本次隔离验证中证明。
