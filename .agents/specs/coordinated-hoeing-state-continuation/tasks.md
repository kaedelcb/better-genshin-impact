# 四人锄地按实际状态接续：任务与接力

本次授权：按 Spec 实施、验证到完成；用户已明确从只读讨论转为修复。
整体阶段：本轮E1/E2/C1-C4/R-16修复及独立复审闭合，开发侧验收通过；此前原失败保留
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

## 3032a6263 原验证证据（复核发现未覆盖分支）

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

原3032a6263曾报告开发侧范围完成，现被下方复核结果限定：同一原BGI进程内按真实状态续办，不因临时占用/通信/辅助记录故障和累计等待放弃，不重复启动，不把普通报错转成停止。明确停止与身份/配置拒绝保持。永久断链只能保留待办等待恢复，无法凭空执行；BGI崩溃/断电后历史恢复不属于本次。

未操作真实User、原日志、第三方JS或用户运行实例，未部署/push。四连接入口只有一个生产MainVM，其余为受控成员；没有四个真实游戏客户端运行证据，不以此承诺零BUG。正常编译告警保留，未扩成本次无关依赖升级。


## 本轮复核修复（完成）

基线3032a62630f2ce7e1c4bdb63402217363fdae41b；main-OldTeaBag-B182；当前根模型gpt-6-astra/xhigh。原审查完整保留，历史通过不等于新需求通过。原生完整Goal已创建并读回active；源码预检complete/issues=[]；无其他在途聊天写者；前轮同范围历史门无命中结果复用。

- [x] T-6 修正准备/一键入口保留请求（R-10/R-11，V-9/V-10）— done
- [x] T-7 修正结果消费、间隙及solo恢复、拒绝后清理（R-12至R-15，V-11至V-14）— done
- [x] T-8 普通任务错误不跳过后续绑定和设定动作（R-16，V-15）— done
- [x] T-9 漏口定向验证、必要入口回归、无部署编译、独立复审及本地提交 — done；本Spec与明确源码/测试清单同提交

原件及hash/BOM/换行：Build/hoeing-state-continuation-20261020-01a156d7/repair-reaudit/source-before-manifest.json。当前修复和验证已完成；没有增加用户步骤、独立执行队列或通用框架。真实User、用户运行实例、原失败及旧报告保持。


### 本轮实际进展与原失败保留

- E1/E2/C1-C4/R-16 初版实现完成；服务器相关70/70通过，助手首轮193/193通过。
- 独立复审发现F1：指定后续配置确定拒绝后原计划发送门未打开；F2：切房间时迟到受理未在原房间撤销。均已修正并经同一轮定向补充核验确认代码闭合，原报告与补充结论将分别保存，不覆盖初始失败结论。
- F1新增生产MainVM缺失配置场景，检查门先关闭、警告保留、无后续发送、批次结算和实际发送许可。F2新增两场真实四成员/生产遥控入口，旧连接退役后只回原房间查询与精确Reset，检查真实成员不被替换、新房间未开始；无新增用户步骤。
- 四连接首轮失败在旧重连断言：旧测试要求断线后循环自行退出，新需求要求保留同一循环。已改为实际跨过旧30秒预算后断言原循环仍在、重连不换循环，并保留原有不重复/不误停断言。原TRX保留于repair-reaudit/four-member-entry-1。
- 助手后续一轮197例150通过、47失败，均为系统临时目录下创建隔离flows/runs被拒绝；未删测试或改产品文件权限。改为本次验证目录内的TEMP/TMP，最终候选助手197/197通过，原失败保留于repair-reaudit/assistant-tests-2。
- 初次最终验证：repair-reaudit/final-validation-1；助手197/197通过；当时BGI及入口尚未完成，后续闭合结果见下表。所有输出复用本聊天validation/artifacts，DeployToBgiTools=false，临时目录同一受管预算域。


## 本轮最终验收与独立复审闭合

证据根：`Build/hoeing-state-continuation-20261020-01a156d7/repair-reaudit/`。主执行者运行测试；独立审查者只读代码。所有实际构建/测试命令、退出码、标准输出和自身进程树终态保存在相应目录，最终正常命令均exit_code=0且active_processes=0。所有构建使用DeployToBgiTools=false及隔离artifacts。没有部署或push。

| 范围 | 最终证据（相对证据根） | 结果与适用性 |
|---|---|---|
| 服务端协调状态及关联入口 | server-tests-1/results/server-reaudit-repair-1.trx | 70/70通过；服务端源码此后未变 |
| 助手本轮全部关联回归 | final-validation-1/results/assistant-repair-final-1.trx | 197/197通过；产品代码为最终候选，后续仅增强测试断言/入口夹具 |
| BGI队列、登记、接管、停止、solo恢复 | remaining-validation-1/results/bgi-repair-remaining-1.trx | 176/176通过；BGI产品源码此后未变 |
| 收尾结果保留/拒绝/明确停止 | verification-closure/results/completion-closure.trx | 13/13通过；ACK尝试计数在产品异常捕获外校验，防止断言被重试逻辑吞掉 |
| solo保留同编号及停止终态 | verification-closure/results/solo-closure.trx | 2/2通过；明确断言真实completed及cancelled区别，不只看句柄 |
| 七场景四成员入口 | verification-closure/results/entry-closure.trx | 1/1测试内顺序七场景全部通过；独立WPF/testhost，当前server DLL，私有BGI协议端 |
| 独立复审原文 | review-original.md | 首次本轮复审确认F1/F2，原结论未覆盖或改绿 |
| F1/F2闭合核验原文 | review-followup.md；review-local-cancel-followup.md | 原始发现均有对应修复；同轮仅核已发现项及直接修改，未新开全包审查 |

### 需求到证据

- V-9 / R-10：server已接受、BGI能力尚未知且跨旧准备预算，原真实循环继续等待，准备放行后只执行一次；原停止回归保持。
- V-10 / R-11：未送达重试、丢回执按原requestId认回、停止期间迟到受理、切房间后迟到/丢失回执均有定向用例。后两项还走生产MainVM和真实旧房间remote精确Reset，确认原成员未被替换、新房间无开始。
- V-11 / R-12：原计划未保存结果时ACK次数严格为0，保存后只确认一次；原Runner恢复B后继续C及临时记录故障关联回归通过。
- V-12 / R-13：gap释放已发布后首次读取失败，重试认回同ticketHash，只执行下一节点C，不重跑B、不重复归档；错误ticket拒绝。
- V-13 / R-14：BGI真实队列及ExecutionScope中solo沿同key/handle运行，重复提交认回；真实取消令牌中止执行体，退出/终态可查且确认后不重跑。游戏执行体为受控端口。
- V-14 / R-15：缺失指定配置不发送任务、保留原因、结束phase，后续停止清理可完成；生产MainVM验证原任务中心门先关闭，拒绝后开启且实际取得发送许可。
- V-15 / R-16：真实服务端和助手循环跨两绑定保留普通错误；四成员入口中本端与另一端普通任务失败，中途控制断线/重连，仍只执行第二绑定一次并自动启动设定收尾一次，错误终态保留，取消次数为0。

七场景为：本端完成后队友真实失败不误停新手动任务；本端完成后断线超过旧预算同循环重连；普通任务失败后继续下一绑定及配置收尾；收尾占用转空闲且受理后丢回复；指定收尾配置移除后正确释放；切房间后迟到受理；切房间后丢回复再认回。后两场使用四个执行端连接和生产遥控MainVM；其余为一个生产执行MainVM及三个受控成员。没有四个真实游戏客户端或真实游戏路线验收。

### 本轮其他失败和修正（保留）

- final-validation-1/bgi 构建期间，产物计量碰到MSBuild替换中的资源文件，FileNotFoundError使自身Job被清理；不是测试通过，cleanup记录active_processes=0。未改工具文件或预算。随后增量构建和176例真实通过；最终验证对自身构建路径的这种瞬时消失只有限重试完整计量，不跳过额度/余量检查。
- remaining-validation-1/entry 的旧夹具未模拟私有BGI端的进程存在，普通手动任务走到了测试宿主启动路径；补已有presence/launch测试接缝，意外启动/停止进程直接抛错，不放宽产品判断。
- entry-validation-2 已实际执行第二绑定和自动收尾，但断言在本端动作受理时立即要求服务端已收全终态，遇到合法的一轮心跳差。改为等待实际服务端completed再检查，原状态和不重复/不误停断言保留；verification-closure全部通过。
- 原报告、原TRX和失败日志均保留。原70/197/176结果复用有源码对应依据；新增/改变的验证只重跑受影响范围，不把重复运行计作额外产品成果。

当前所有本轮已确认缺陷均已修复并验证；无需新增用户确认或重置步骤来接续这些可恢复情况。明确停止仍取消原意图和任务；真正锄地未完成不伪装完成；永久通信不可达和跨进程/断电恢复保持原范围边界。本次本地提交的确切身份由最终答复和git log提供。
