# 四人锄地按实际状态接续：任务与接力

本次授权：按 Spec 实施、验证到完成；用户已明确从只读讨论转为修复。
整体阶段：开发侧实现与验收完成，按明确文件清单做本地提交；未作四个真实游戏客户端运行验收
更新日期：2026-10-20
依据：[requirements.md](requirements.md)；[design.md](design.md)

本次补今天 B54387 接管误判和完成动作卡死的真实使用故障；验收决定是否可作为同套三端源码修复交付，已有日志不能证明拟修改代码有效，以 V-1 至 V-8 的隔离重放和无部署构建为开发侧结束判据。

## 任务

- [x] T-1 固定事实、需求和三端最小设计
  - 状态：done
  - 依据：R-1 至 R-9、P-1 至 P-4。
  - 结果：原生完整 Goal 已读回 active；源码预检 complete/issues=[]；HEAD 1b51cf8ade568fe3e59c1b98075e43702d04f44d；定向历史门无命中；无其他在途聊天写者。读取 Spec 技能并按全链验收固定本规格。
- [x] T-2 BGI 同号队列事实保留、补登记容错和结果查询/确认
  - 状态：实现完成，最终验证已由 T-5 汇总；本勾选不代表游戏验收。
  - 结果：retained 请求由原 PendingTask 保持唯一，按 key 查询原句柄/状态/退出事实；辅助登记与回执写入故障沿原编号补齐，未确认结果不受普通历史时间/容量淘汰。精确 ack 释放详细结果但保留已结算去重关联；retained suspend/resume 及等槽均消费原停止围栏。普通旧调用保持原边界。
  - 依据：D-2；V-3/V-5/V-7/V-8。
- [x] T-3 服务器保留同批待办，临时等待不按时间 Abort
  - 状态：done
  - 结果：服务器定向44/44通过、退出码0；TRX：Build/hoeing-state-continuation-20261020-01a156d7/validation/results/server-state-continuation.trx。第一次命令包装器参数错误未启动测试；第二次真实测试通过，包装器因dotnet构建服务驻留拒绝终态，已只清自身Job进程树，cleanup.active_processes=0；原失败均保留。后续命令禁构建服务。
  - 依据：D-4；V-4/V-7/V-8。
- [x] T-4 助手接管并发修复、原请求接续与各端完成动作
  - 状态：实现完成，最终验证已由 T-5 汇总；首轮失败保留。
  - 结果：接管回执按最新原身份原子合并，允许合法 parked 变化；保留同批原请求和挂起回执，暂时准备/观察/控制失败在原循环接续。冻结完成动作沿原批次执行权进入 retained 队列，忙时等待、已受理后只观察；计划恢复沿原 continuation 补关联并继续原 Runner 后续节点。真实终态先存，记录安排已就绪时先释放本端执行权，ack 失败只补确认。旧普通手动启动继续 R5；明确停止/重置与新进程身份不复活旧请求。
  - 依据：D-1/D-3/D-4；V-1/V-2/V-3/V-6/V-7。
- [x] T-5 关联回归、隔离四端重放、无部署构建与本地提交
  - 状态：done；本 Spec 与明确归属源码/测试一同本地提交，不 push。
  - 结果：助手146/146、BGI174/174、服务器44/44、四连接入口1/1（内部顺序四场景）通过；三端隔离输出编译通过，DeployToBgiTools=false。原失败记录保留。
  - 依据：V-1 至 V-8；所有真实失败保留，不能删测试或将源码验证称为游戏验收。

## 验证结果

| 验证 | 证据 | 结果 | 局限 |
|---|---|---|---|
| 源码预检 | source_preflight.py --json | complete，issues=[] | 只证明源码输入和基线 |
| V-1 | `OnlineHoeingAtomicSuspendTests`：RunnerParkingBetweenReceiptEntryAndAtomicMerge、身份变化/明确停止拒绝、未受理准备刷新及同 continuation 重取；`OnlineHoeingContinuationTests` 关联回归 | 通过；最终TRX见下方“最终证据” | 测试端口与原 Host/RunStore；并发重放不等于四人游戏运行 |
| V-2 | 助手 `CoordinatedStateContinuationTests`：PreparationCanWaitPastStageDeadline、ControlAndObservationLossPastOldBudgets、BusyNotAcceptedRequests | 通过；最终TRX见下方“最终证据” | 假时钟/受控传输驱动实际循环；永久断链只保留待办 |
| V-3 | BGI `StateContinuationTests`：ConcurrentAndLateSameKey、FiveHourReceiptAndOrdinaryHistoryEviction、ReplyConstructionFailure；助手 `BgiStateContinuationTransportTests` 冻结请求及 `CompletionStateContinuationTests` 丢回复去重 | 通过；最终TRX见下方“最终证据” | 覆盖真实队列和 SDK 合同；未模拟断电后历史丢失恢复 |
| V-4 | 服务器 `CoordinatedStateContinuationTests` 与 `CoordinatedBatchGatewayTests`：跨注册/准备/接管/心跳阈值、unknown 同批待核实、真实失败及明确停止 | 通过；服务器44/44，源码此后未变化；最终TRX见下方 | 状态机/网关验证；服务器 elapsed 时间不能证明 BGI 执行退出 |
| V-5 | BGI `StateContinuationTests`：RegistrationFailure、RegistryMissing、FullReceiptWindow；助手 `StateContinuationPlanResumeTests`：ParkBindAndTerminalWriteFaults、OriginalRunner_ConsumesResumeOnce_AndExecutesNextNode | 通过；最终TRX见下方“最终证据” | 注入辅助登记/落盘/观察故障；一条龙真实游戏运行不由这些队列/Runner用例代替 |
| V-6 | `CompletionStateContinuationTests`：SharkBusyThenIdle、记录/ack故障、lost resume key；`FourMemberFinishEntryTests` 同STA中的 RunStateContinuationCompletionEntryAsync；原计划B恢复后继续C的Runner用例 | 通过；四连接入口及恢复原Runner后继续C均实际执行；最终TRX见下方 | 四个真实SignalR连接，一个生产MainVM/CommandExecutor，另三成员受控；完成动作入口使用group，未运行四个真实BGI游戏实例 |
| V-7 | 助手 `CoordinatedStateContinuationTests` 明确停止/普通撤销/新epoch，`CompletionStateContinuationTests` 迟到受理精确取消，`BgiStateContinuationTransportTests` 整批停止围栏；BGI/服务器同名停止回归 | 通过；最终TRX见下方“最终证据” | 私有协议端/状态机/实际SDK；不停止或重启用户程序 |
| V-8 | 既有普通队列、R5完成动作准入、精确停止及身份/配置冲突回归；三个项目隔离输出、`DeployToBgiTools=false` 构建 | 通过；三项目编译及回归均有实际日志，见下方 | 构建/组件回归/入口测试分别记录；无部署、push或游戏验收 |

## 接力点

- 完整目标：见本聊天 active Goal；不因子任务完成缩小或清除。
- 当前：三端实现及相称验证完成；26份原件及hash/编码/换行保存在Build/hoeing-state-continuation-20261020-01a156d7/source-before-manifest.json及source-before/。当前规范D-3按已核代码收窄为本批完成动作延续已有执行权，旧R5普通新启动保持。
- 环境：当前仓库 main-OldTeaBag-B182，原 HEAD 如上；实际模型 gpt-6.1-sol/ultra；旧目录只读且本次无历史命中。
- 数据边界：不碰真实 User/日志/第三方 JS，不运行或重启用户实例。构建必须 DeployToBgiTools=false 并采用隔离输出。
- 收尾：本文件与三端源码/定向测试作为同一个本地修复提交；确切提交号见本次最终答复及 git log。无需部署、运行游戏或新增审查来完成本次开发侧任务。

## 一次有界交叉复核及修复记录

- 范围：本次同请求不重复、明确停止与BGI/助手协议交接，未扩全项目。两名未写对应端的子代理只读，原结论保留在本聊天。
- 必修 F-1：BGI按key查无记录未回原key，助手误判冲突。已修响应并加MissingRetainedRequest回归，BGI最终回归已通过。
- 必修 F-2：客户端首次冻结可能以新BGI覆盖原epoch。已实现保留/核对原epoch并补跨进程no-send用例，助手最终回归已通过。
- 必修 F-3：本批后续第一次发送可能取得F11后的新停止围栏，且manual_stop_fence未正确取消。已固定整个批次的接管围栏、接入明确停止分类并补新用例，助手最终回归已通过。
- 必修 F-4：接管明确未受理后仍重发旧目标。已加仅未受理且无票据时刷新prepared标记及同BGI目标的处理，parked/accepted拒绝丢弃；新增测试已在最终回归通过。
- 必修 F-5：回执表满可能挡住结果确认。已使精确ack和保留队列启动不依赖辅助回执容量；同号真实队列仍唯一，新增容量满回归已通过。
- 首轮助手：编译通过，133例中123通过、10失败；9例新completion夹具漏Protocol=hoeing.start.v2，已修夹具；1例旧四端入口缺进程级NEXUSBGI_DATA_ROOT，将按原测试要求以独立隔离testhost重跑。未删测试/断言，原TRX保留。

## 最终证据

共享证据根：`Build/hoeing-state-continuation-20261020-01a156d7/validation/`。所有构建均输出本根下的 `artifacts`，`DeployToBgiTools=false`。每次命令原 argv、stdout/stderr、退出码与自身进程树终态保留在对应阶段目录中。

| 项目/场景 | 最终结果 | 证据（相对上述根） |
|---|---|---|
| 服务器三组关联回归 | 44/44通过，dotnet退出码0 | `results/server-state-continuation.trx`；`server-tests-2/targeted-stdout.log` / `targeted-process-result.json`；构建服务驻留由自身Job清理，`targeted-cleanup.json` active_processes=0；源码此后未变 |
| 助手核心/协议/原计划恢复 | 146/146通过，退出码0 | `results/assistant-state-continuation-3.trx`；`assistant-tests-3/targeted-stdout.log`、`targeted-process-result.json`、`targeted-tree-terminal.json` active_processes=0 |
| BGI队列/登记/接管/停止 | 174/174通过，退出码0 | `results/bgi-state-continuation-2.trx`；`bgi-tests-2/targeted-stdout.log`、`targeted-process-result.json`；`targeted-tree-terminal.json` active_processes=0 |
| 独立四连接生产助手入口 | 1/1通过，退出码0；同STA顺序四场景全部走完 | `results/four-member-finish-state-continuation-1.trx`；`four-member-entry-1/targeted-stdout.log`、`targeted-tree-terminal.json` active_processes=0；`data/`下四个coordinator日志均保留 |
| 三项目无部署编译 | BGI、助手、服务器均在上述dotnet test构建阶段成功 | 对应stdout含三项目最终输出；四连接使用本次服务器DLL，未引用旧bin服务 |
| 提交前源码输入及文本检查 | source_preflight complete/issues=[]；git diff --check通过 | 提交前重核；原基线`09933380242e6218b46e2f2124bf4a3037a0c5fb`包含于原HEAD，基线推进只作为源码接续检查点 |

助手核心过滤范围：CoordinatedStateContinuationTests、CompletionStateContinuationTests、OnlineHoeingAtomicSuspendTests、BgiStateContinuationTransportTests、OnlineHoeingContinuationTests、FailedOnlineHoeingSettlementTests、CoordinatedBatchLocalFinishTests、RunningReconnect、CommandExecutorTakeoverResetTests、CommandExecutorTeardownReceiptTests、CommandExecutorExecutionExitProofTests、BgiTaskStartPayloadTests、StateContinuationPlanResumeTests。

BGI过滤范围：StateContinuationTests、BgiTaskCoordinatorTests、JobRegistryTests、ManualStopFenceContractTests、TaskTakeoverIncidentTests。服务器过滤范围：CoordinatedBatchStateTests、CoordinatedBatchGatewayTests、CoordinatedStateContinuationTests。

四连接入口独立testhost的进程级`NEXUSBGI_DATA_ROOT`为`four-member-entry-1/data`的绝对路径；`NEXUSBGI_TEST_COORDINATOR_DLL`指向本次`artifacts/bin/BgiCoordinatorServer/debug/BgiCoordinatorServer.dll`。仅私有BGI管道，禁止操作真实BGI。新增场景在BGI受理完成动作后真实断开私有连接丢回复，经生产SDK重连按key找回；忙到空闲后只启动一次；队友`local_failed`及迟到全队中止不停止本端已运行后继；真实完成后ack、释放本端。

## 保留的失败与修正

- 服务器第一次包装器目录参数错误，未运行测试；第二次44/44通过但构建服务驻留使包装器拒绝进程树终态，已只清自身Job进程。原记录未改绿；后续命令全部禁构建服务。
- 助手首轮133例123过10失败：9例新夹具缺Protocol，1例四连接入口未显式设数据根。修正夹具及隔离命令后真实重跑。
- 助手第二轮146例144过2失败：测试伪造同一进程startTicks，被SDK真实OS身份校验正确拒绝，握手超时。改为真实第二个隐藏自有管道进程，保留全部旧epoch禁止发送断言；最终146/146通过，未放宽产品身份校验。
- BGI首轮174例173过1失败：缺记录测试将Guid值类型误断言为null；修正为Guid.Empty，协议对外仍输出空句柄，最终174/174通过。
- 以上原TRX、stdout和清理证据全部保留，不删除断言或排除失败用例求通过。

## 交付边界

本次开发侧范围完成：同一原BGI进程内按真实状态续办，不因临时占用/通信/辅助记录故障和累计等待放弃，不重复启动，不把普通报错转成停止。明确停止与身份/配置拒绝保持。永久断链只能保留待办等待恢复，无法凭空执行；BGI崩溃/断电后历史恢复不属于本次。

未操作真实User、原日志、第三方JS或用户运行实例，未部署/push。四连接入口只有一个生产MainVM，其余为受控成员；没有四个真实游戏客户端运行证据，不以此承诺零BUG。正常编译告警保留，未扩成本次无关依赖升级。
