# 四人锄地按实际状态接续：任务与接力

本次授权：按 Spec 实施、验证到完成；用户已明确从只读讨论转为修复。
整体阶段：R-17至R-22实施中，跨助手/BGI接续及完整组合验证尚未完成；此前同进程修复证据和原失败保留
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

## 最高优先级与异常接续追加修复（2026-10-20，用户明确授权）

补齐用户确认的实际故障：旧计划记录仍挡开锄、BGI/助手异常后不能接续、后续本体失败被当作处理完。前次cef55430仅证明原限定范围，不能作为本轮通过。

- [ ] T-17（in_progress）：解除旧记录准入阻塞并落实联机优先；验证R-17/R-18。
- [ ] T-18（pending）：原批次/执行/停止恢复及重启接回；验证R-19/R-20/R-22。
- [ ] T-19（pending）：绑定后续异常接续及进度丢失退路；验证R-21。
- [ ] T-20（pending）：故障定向测试、生产入口受控验证及独立复审，修复本范围发现后提交。现有同进程证据不足以证明重启恢复；各R项有对应通过结果且无未解决本次阻断后结束验证。

本轮基线cef55430ce5e169adebc60b95da6dbfbd0c5aee6，源码备份及编码/换行/hash：Build/hoeing-priority-recovery-20261020-01a156d7/before-manifest.json。未操作真实User/游戏或运行实例。

### 追加修复当前真实状态与接力点

状态：实施中，绝非完整交付。T-17局部已实现、T-18/T-19未完成，全部复选框保持未勾选。已有代码不能被当作“异常都能恢复”的版本。

已写入并通过定向组件验证：旧计划准备失败可让BGI实际让位继续；retained suspend保存回调异常仍准确取消原root并保留内存snapshot；BGI普通队列在持票期间暂停，优先项消费原Channel通知后串行执行；新hoeing.priorityRecovery.v1协商后，全队未完成的当前绑定共同重试不受一次预算限制，旧执行退出前不重开，后续延至全队completed。旧协议行为保持。

- validation-1/server：46通过/1失败，失败为旧Gateway测试期望第二次失败终止，与新需求冲突；原件保留。更新该用例为新协议继续恢复，旧协议bounded测试仍保留。
- validation-2/server：47通过，退出0。
- validation-2/assistant及bgi：编译失败CS0103（新增catch缺System.IO引用），已修复，原失败保留。
- validation-3/assistant：67通过，退出0。包含新全队完成前不启动后续、aborted不无限等、旧记录读取失败仍放行等。
- validation-3/bgi：150通过，退出0。包含原root保存失败、满普通队列连续两个优先项及无并发执行。
- 所有上述有效运行的Job Object终态active_processes=0，未操作真实User/游戏或用户进程。复用受控artifacts：Build/hoeing-state-continuation-20261020-01a156d7/validation/artifacts。不要移动/删除历史证据。
- 当前来源/hash与TRX：Build/hoeing-priority-recovery-20261020-01a156d7/component-evidence.json。

本次独立只读Agent /root/priority_recovery_design_review 使用gpt-6.1-sol/high。原始第一部分复核报告在同证据目录review-part1-original.md。F1队列通知耗满、F2全队终止后本端等待不结束已修改并有validation-3定向通过；尚未由该Agent做改后确认。F3旧ticket复用不同目标导致takeover_identity_mismatch永久重试仍开放，必须修，不得标闭合。稳定完整实现后的整范围复审尚未执行。

唯一下一项：首先修F3旧票据的继承/结清，打通原批次执行身份恢复，接着完成T-18/T-19；不能把本地台账宽松返回Ready视为全部开锄入口已修好。

后续必做，已获用户授权，不再逐项请求确认：
1. F3：CommandExecutor.ExecuteSuspendCoreAsync仍复用旧_takeoverTicket，BGI回执原target与新空闲target不同；需按实际原receipt/退出事实继承或结清原责任，绝不删除BGI目标身份校验，不对新根盲用旧票据。PreparePriorityOnlineHoeing对非IO的普通账本异常、CloseOnlineHoeingSendAsync等待在途发送也需一并核实（后者_mutex等待可能先于BGI实际预留，尚未修）。
2. 助手重启：OnlineBatch仍拒收非registering/pending，OnlineHoeingBatch.Token随机内存，loop内部_index/_attempt/_item/_epoch不恢复。ClientInstanceId已持久化且服务器按其匹配席位，可复用；恢复需同原成员/token/原job身份。现有SignalR AdoptConnected会RestoreCoordinatedAuthorityAsync。不能重启后冒认另一个Windows会话/活助手。
3. BGI变化：CoordinatedBatch/OnlineBatch/StateContinuation仍多处epoch变化撤权。要确认旧PID+启动时间死亡后重建准备/接管，分配新共同执行代次；旧进程活着/不明确不能重发。共同游戏房身份必须含Attempt，现有batchId每次重试未变化。房主退出删游戏房，现有路线rerun只有内存token，不能作为跨进程恢复。
4. 明确停止：ExecutionScope.StopVersion静态内存、BGI恢复现场AllConfig.SuspendedTaskContext为JsonIgnore；要保存可跨重启的停止意图，普通异常不能冒充人为停止。考虑保存失败但另一端仍有事实时补存，所有身份/事实全丢不能虚构。BGI guardian只监测PID消失，助手重启与卡死恢复尚未实现。App现有退出/自启动边界应复用，勿新增用户流程。
5. 后续：StateContinuation worker failed/queueCancelled仍只记录结束；RequiresAuthority仍绑定旧epoch；Reconcile仍可能永等；一次守卫缓存失败；RunSpecified首次执行才读配置内容，未冻结任务实体；Resume原现场仅内存。需要稳定key接回已执行项，确认退出后新尝试，已知任务丢进度重跑，缺任务身份只报该项无法恢复不锁其他项。保存/ack必须与执行释放分开但不能丢原Runner尚未消费的结果。
6. 真实生产组合的隔离故障验证仍需覆盖：助手重建原BGI仍执行不双跑、BGI真退出全队恢复、丢进度重跑、各保存点持续失败、后续失败和丢回复、停止跨重启不复活。既有四连接夹具FourMemberFinishEntryTests.StateContinuation可复用，必须按新全队完成后才后续调整合理断言；不操作四个真实游戏客户端。

施工约束：当前源码根E:/Program Files/better-genshin-impact-LCB-source，main-OldTeaBag-B182；旧根只读不扫描。当前所有差异均本聊天新增（开工tracked干净），168个既有untracked不动。禁止部署/push/改真实User/第三方JS。本地WIP可保存，但源码预检baseline维持cef55430，未完成不得前移。历史门本目标此前已运行matches=[]并读索引，不重复扫描。源码入口技能及AGENTS已读；Spec完整目标不因本组件通过缩小。

原生Goal为完整用户要求，接班必须继续同一完整结果而非只修F3。本聊天实际最新turn_context为gpt-6-astra/high，collaboration_mode.settings一致（全局默认sol/ultra不是来源模型）。来源rollout E:/CodexData/home/sessions/2026/10/20/rollout-2026-10-20T09-05-25-01a156d7-7bb3-7d12-83b9-fc334ac8cf86.jsonl。


### 唯一接班 01a15932 当前施工记录（未完成，2026-10-20）

本接班原生完整 Goal 已创建并读回 active，实际 cwd/main-OldTeaBag-B182/HEAD287bf4e2 与 gpt-6-astra/high及settings一致；取码预检complete/issues=[]。同一Goal历史门matches=[]结果复用。未部署、push、操作真实User/游戏/用户程序；原169个untracked保留。当前新增源码均未提交，属于WIP，不推进外部baseline。

已写入但只按下列证据限定：
- `task.takeoverReceipt.v1`：task.suspend的receiptOnly只读原回执，继续校验原stop/ticket；助手在当前实际空闲时继承原已退出现场，普通改绑仍takeover_identity_mismatch。F3的空闲旧票据场景已有定向通过；旧票据下另一个活跃根的接管仍须完整入口审查，不能把F3整体提前标闭合。
- priority任务中心发送门可立即阻止新普通发送，不等待旧在途请求丢回执；BGI既有接管负责物理排他。旧台账一般异常不挡优先接管。
- `CoordinatedRecoveryStore`将原token、批次/绑定策略、epoch、stop、票据、loop项、指定后续尝试/失败写入助手数据根的coordinated-recovery/session目录，写失败不取消在跑安排。读取只接受同控制身份/成员/冻结绑定、原助手PID+start已死；未知/活着不接管。恢复原BGI时先按原key查作业；不是新的独立调度器。
- `Mistletoe.Shared/CoordinatedStopSignal.cs`：BGI/助手共同持有Windows命名manual-reset事件，StopActive(true)或batch.Cancel置位。任一进程死后存活端仍保留停止事实，不依赖进度文件写入成功；两端所有句柄都丢失不能猜测恢复授权。另一个closed事件区分正常退出与崩溃。尚未宣称整机断电恢复通过。
- BGI确切旧PID/start退出后，循环上报当前共同项failed；服务器新共同Attempt后才准备新BGI/接管/重跑，游戏房身份加入Index/Attempt。重复崩溃准备使用ProcessRecoveryPreviousEpoch；item自身epoch单独保留，旧死进程ACK不发给新进程。
- 指定后续RunSpecified失败且ExecutionExitConfirmed后新attempt，保留旧失败；普通记录失败不否决重试。Resume完整退路仍未完成。
- `CoordinatedAssistantRecovery`复用BGI JobRegistry现有30秒心跳，核实际peerPID/start/session/path死亡且当前会话无助手后同路径--minimized --manual-start重启；正常App退出设置suppression。App废弃Mutex按已取得处理；可恢复批次致命异常不再弹阻挡进程退出的对话框。这些只构建/组件层，未有真实双进程自动拉起验收。
- BGI卡死处理接原CoordinatedBatchLoop：全部执行观察连续失联3分钟后，再核窗口无响应持续30秒，沿原monitor进程门只结束精确PID/start/session/executable，不杀进程树；没有确切退出仍不重发。未做真实挂死产品进程验收。

本接班证据根：`Build/hoeing-priority-recovery-20261020-01a156d7/successor-01a15932/`；11份起始原件及hash/编码换行在before-manifest.json/before中。后续原件来自干净HEAD287bf4e2，变更前hash/大小/编码换行已在本轮工具输出记录。复用前述validation/artifacts；所有测试DeployToBgiTools=false、独立TEMP/TMP、--disable-build-servers，后续均设置NEXUSBGI_DATA_ROOT到当前受控phase/data。
- validation-1：包装器recovery_directory误用storage-control，入口拒绝，未启动构建；原失败不清理。
- validation-2：助手16通过；BGI新测试把IsCancellationRequested属性写成方法，编译失败，已修。
- validation-3：BGI83通过1失败；新测试Request helper会冻结目标，所谓idle仍是旧目标。已显式null/null，保留原身份拒绝断言，不改产品校验。
- validation-4：助手49/BGI84通过，退出0，Job active_processes=0。此后新增指定后续失败重试、跨进程停止信号与新尝试恢复。
- validation-5：助手53/BGI104通过，退出0，Job active_processes=0；其后新增自动助手拉起、卡死判断、原itemEpoch及死进程ACK修正和复审修复，不把旧结果当成这些新代码验收。
- validation-6：执行中。助手54通过1失败，唯一新R-F1测试自身3秒预算不足（4个失联阶段的真实只读重试至少约4秒），已仅改该测试等待10秒，尚未重跑；原断言/失败TRX保留。BGI正在收尾，结果须从真实进程终态读取。

当前一次集中独立复审Agent：`/root/priority_recovery_final_review`，gpt-6.1-sol/high，独立只读不构建；同一身份后续核直接修复与最终稳定版本，不另开新轮。原第一部分F1/F2/F3报告继续保留。当前新增发现：
- R-F1 [P1] 间歇失联未清时钟会误判持续卡死。已改ReadObservation成功后清连续失联计时并通知monitor清无响应候选，终止前再核；新增RecoveredObservationRestartsBothContinuousHangBudgets，首跑因上述测试时间预算失败，待重跑与独立确认。
- R-F2 [P1] 恢复到prepared时StartStage根据_stage把Prepare放进_takeover，可能无真正让位。已改显式preparation槽并已Prepared不重复Prepare；RestoredPreparedStageMustPerformActualTakeoverExactlyOnce在validation-6实际通过，待独立改后核验。

必须继续，不能告知完成：
1. 完成当前组件验证并修复本轮独立复审新增实际问题；尤其普通CloseAsync取消后的门状态应保留原合同（priority单独立即关门），新代码还需相称回归。
2. 助手journal全部丢失/持续写失败后重启的退路未接通。目前OnCoordinatedBatchStart仍在无本地记录时拒绝旧阶段并有旧“状态重置”提示，必须移除该用户步骤。拟沿BGI现有查询/挂起请求保留一份原恢复快照，仅内存记录，不把其成功作为开锄门槛；旧BGI存活时可取回原token/策略/epoch/stop/key，恢复先查原key，不能生成第二份。这个计划尚未实施，不能当完成。
3. 后续Resume丢上下文/位置的已知任务重跑、旧RunStore持续失败不锁其他安排、后续执行中BGI进程死亡接续尚未完成。不得把no_context当已执行；仍要保留原Runner消费责任。所有任务身份全丢只报告该项不能恢复，不虚构或锁其他安排。
4. 自动助手拉起、确切BGI卡死回收、prepared恢复期间再次换进程等必须做自有进程/私有IPC/生产MainVM的受控入口验证；当前组件测试不是实际四端重启证据。
5. 四连接入口需要加入新priority能力场景、共同重试新游戏房、丢记录/丢进度/丢回复、后续失败和明确停止不复活。仅给原私有BGI握手补了task.coordinatedRecoveryStop.v1能力；新整场景未写/未运行。
6. 更新requirements/design实际实现与验收边界，集中复审闭合，本地中文commit --only明确归属文件；全部完成前T17-T20仍不勾。不要推进baseline、push或部署。


#### 本接班后续实现与验证更新（仍未完成）

- validation-6最终：助手54/55（唯一R-F1测试自身3秒上限失败，已改10秒）；BGI104/104；退出码助手1/BGI0，两个Job终态active_processes=0。原TRX保留。
- 新增存活BGI内存快照退路，实际peerPID/start/session与原控制身份验证，query/suspend附带快照，启动/ACK指纹不附带；MainVM在本地与BGI有效快照中选较新Revision。旧“请状态重置”提示已去掉，BGI通道恢复会自动重新查原服务器批次。双方进程及所有身份同时丢失仍不能猜测授权，也未被伪装成恢复成功。
- BGI原生Resume在context丢失时按原TargetedSuspendReceipt.RecoverySnapshot恢复，保留stop/ticket/原退出/当前根空闲检查；原票据撤销后不能重建。
- R-F3 [P1] 已释放后续票据被恢复成SDK发送资格：已修RestoreCoordinatedTakeover(batch)只恢复未释放责任；真实SDK/私有管道/worker覆盖旧job活着不再起、失败exit后新attempt不带旧票据。
- R-F4 [P1] prepared恢复跳过Prepare同时漏补共享停止事件：已将同原epoch+原stopVersion确认抽为BgiExternalClient.EnsureCoordinatedRecoveryStopAsync，在Prepare和真正Takeover前调用。旧信号全部丢失时Ensure不能自行新建；只在同原进程围栏仍匹配时补建。prepared恢复新用例验证补建、存活peer持有和stop变化拒绝。
- 普通CloseAsync维持取发送mutex后才关闭的旧取消合同，只有priority专用入口立即关门，新增普通关闭取消不残留closed门回归。
- validation-7：助手70/70，包含R-F1/R-F2/R-F3/R-F4、存活端快照恢复、SDK指纹稳定和停止；BGI105/105，包括原snapshot重建与撤销拒绝、辅助快照身份/Revision、共享停止、原队列/票据回归。当前工具包装器在做最终计量/收尾，最终退出与进程树结果以其真实结果为准。源码测试不等于实际四端或真实进程异常验收。
- 当前集中复审原文已保存`successor-01a15932/review-original.md`；同一只读Agent已代码确认R-F1/R-F2/R-F3根因修复，R-F4直接补核已发出，原报告不覆盖。最新补充结论需单独保存。

唯一下一施工重点：R21原计划Resume的记录依赖及恢复任务失败接续，沿原Runner而不是另一套执行器。
- 独立复核定位：TaskCenterHost.OnlineHoeing.cs中_onlineResumeSignals只用于legacy；StateContinuation直接走AwaitStateContinuationResumeAsync（原1218附近），尚无权威内存接受槽。该方法持有原run+original；如补槽，须带完整run/node/occurrence/iteration/原key及新job/key/attempt/epoch，复用当前归档/CurrentSubmission绑定，不能只发jobId。
- WorkflowRunner.RebaseOnlineContinuationAsync（原2894附近）/CommitOnlineContinuationOutcomeAsync（原2917附近）也以_runs为必需，会因持续IO失败永等；一般后继RecordIntent/CommitOutcome还有原持久准入合同，不能直接吞异常或改全局RunStore安全校验来假称完成。所有后续发送仍须同一原Runner消费责任和有效停止/owner边界。
- managed恢复job的failed+exit目前被ResourceRetryAttempt（原3012附近）排除重试，并按节点/计划onFailure决定后继；默认ContinueOnNodeFailure=false。R21需明确priority策略继续/重试，保留真实failed，不能伪装succeeded。
- 非managed原生group/onedragon失败退出后可用已知原snapshot配置和固定新attempt key走原task.start入口；旧resume ticket已释放且缓存旧receipt，不可拿旧票据再次执行。solo必须保留具体任务及设置，含原NodeId的managed snapshot不得绕开Runner走原生退路。
- 仍须补后续执行期间BGI崩溃、旧账持续失败和丢位置退路，再跑生产四成员/实际自有进程异常组合入口。原四成员仅补了私有BGI新能力，新增完整场景仍未运行。最后同一集中复审身份核完整稳定版本并修闭本次缺口，再提交正式修复。

完整Goal继续active；用户无需重复授权/粘贴提示词。当前仍WIP，不推进预检baseline、不push、不部署，不宣称全项完成。


#### 原生恢复重试与原Runner进度退路（本地WIP，仍未完成）

- 既有WIP提交d2837c7a1已保存前述组件，validation-7最终助手70/BGI105、exit0/tree0；本节是该提交之后尚未提交的增量，不代表整项完成。
- 原生Resume失败且真实退出后，保留完整原任务快照（含具体solo及设置），核明确ManagedByWorkflow=false，按同stop信号取得新reservation再恢复；managed快照不能走原生退路。BGI对新票据/空闲根/stop/快照不变校验，未admitted的排队恢复失败仅释放自己的reservation。
- R-F5：受理丢回复后执行器残留旧ticket，status暂时失效时不能清旧job/票据。worker已先确认实际执行器票据结清才重试；validation-9真实SDK/private pipe连续4次status失败后只取1张新票据、总2次resume，助手39/39 exit0/tree0，同一复核Agent确认闭合。原发现保留。
- validation-8助手37/38：旧测试返回同一个旧job给所有新key，与新增恢复前原key查询冲突；仅改替身对未提交key返回not_found，原断言保留。BGI110/110 exit0/tree0，含具体原生快照、managed拒绝及未admitted释放。validation-9已验证助手修正。
- 原Runner的优先接续只在同run的已消费priority handoff/释放历史上启用：resource真实failed+exit沿原Runner新attempt重试，不受普通3次上限；保留失败，1秒等待可被原取消令牌中断。普通策略未变。validation-10因新测试事后给init-only State赋值编译失败，改为初始化时传state；validation-11助手17/17 exit0/tree0，覆盖多次失败后B成功继续C及明确停止阻止下一次发送。同一Agent源码未发现新增双跑/扩大范围。
- 现新增RunStorePriorityContinuation：仅原host仍持活跃/未cancel的原Runner时捕获原run进度；每次推进仍验证原ownerFence、revision CAS、RunStoreEvidenceGuard和停止基线。持续进度IO故障期间该原Runner从内存消费原job结果并继续同安排；待补存仅保存增量变化，恢复后按原顺序经过PersistDisk验证。
- R-F6原发现：整backlog持有owner/运行锁会耗尽租约且阻塞停止。已改每次只补1条，既有heartbeat在锁外让出并续期后补下一条；不新增timer/后台服务。
- R-F7原发现：每次完整历史快照导致积压内存平方增长。已改字段差异、数组追加、备注后缀，仅保存新增内容；Current/Published各一份。以上两项源码补核已确认根因解除，实际验证仍待validation-13。
- validation-12：助手38/40，新持续读写故障原Runner完整B恢复→C→顺序补存通过。两项旧真实进程围栏测试因缺ControlledWriterProbe依赖未启动，不是通过；原失败保留。validation-13正在先构建该依赖，复制明确的probe dll/deps/runtimeconfig到隔离测试产物，再跑RunStore/原Runner/原子接管回归，结果以TRX及进程树终态为准。

此处仍不能勾T17—T20。剩余主要缺口：进度退路的生产MainVM/host真实入口绑定、整目录失效后发送门/非关联等待的独立处理、助手重启后managed进度接回、后续执行期间BGI真正退出后的接续，以及完整四成员/自有进程故障组合。当前进度缓存不是跨进程持久恢复事实，不能把助手死亡后的旧缓存当存活。所有身份均丢失仍不猜测。当前完整Goal保持active，用户无需管理下一批，禁止push/部署/推进baseline。


#### 生产宿主进度/等待接线补充（完整Goal仍未完成）

- validation-13助手54/54、probe构建exit0，两者tree0；原两项真实进程依赖失败已按新鲜ControlledWriterProbe补齐后通过。R-F6/R-F7由同一Agent读取实际结果后闭合，原报告及闭合原文分存review-native-and-progress-original.md和review-native-and-progress-closure.md。
- 生产host只在同workflow原_drives还活着/未cancel时保留实时进度；MainVM只在同generation+原run已受理恢复、且存在该原缓存时，可依据原消费责任恢复发送门。旧目录IO异常不会否定已完成的物理工作。旧GateParked计划的补查沿既有租约heartbeat触发，串行且纳入Shutdown；补查从不重开新批次关闭的门。validation-14助手88/88 exit0/tree0，包含真实Host.StartWorkflow/PreparePriority/ConfirmSuspend/原Runner消费，在enumerate与publish持续失败下B恢复→C的入口测试。
- R-F8[P1]独立复核发现：提前清等待标记、或原drive两秒仍未退就清pending，导致临时未受理永久不再自动唤醒。已将标记消费移到原Runner实际写Running的同次发布，保留未受理责任；原drive尚未退出保留pending。
- validation-15助手89/90 exit1，唯一环境首次未就绪用例实际复现同根的Interrupted窗口：恢复扫描可先把LocalWaitParking改Interrupted，原筛选遗漏。进一步复核也指出首次Running发布失败后Converge改Interrupted的同类路径。原失败不删。
- 当前统一IsPendingOnlineWaiter只接受原GateParked=true、未Stop、无handoff、无未决外部发送、未重置的LocalWaitParking/Interrupted。普通Interrupted不自动恢复。新增一次Running发布失败、Converge成功、下一heartbeat C只执行一次的用例；原drive超两秒窗口已在15通过。validation-16已启动，结果待真实TRX与进程树收尾；R-F8源码补核通过，不能在结果出来前标验证闭合。

后续唯一施工重点仍是原安排跨助手/跨BGI的实际接续：完成动作RequireAuthority目前捕获旧epoch并在BGI变化时撤权；managed原Runner的StopAuthority也固定旧epoch，不能简单刷新为新权限。需在旧PID/start确切退出、原共享停止信号仍有效及新BGI实际停止状态匹配时沿原执行链恢复，保留原停止来源/历史，活着或身份不明禁止双跑。当前内存进度不是跨进程恢复存档，不能用本节组件证据替代该缺口或四成员故障组合验收。

- validation-16实际助手91/91通过，包括R-F8首次环境拒绝、原drive慢退、首次Running发布失败后Interrupted继续；脚本正在最终计量，exit/tree以process-result/tree-terminal读回为准。旧validation-15的89/90失败保留。当前源码未在16构建后继续修改。
- 本阶段待保存中文WIP本地提交；完整Goal仍active，T17—T20不勾，外部分支预检baseline仍cef55430。下一项是上述跨BGI/助手安排接续，不能因91个组件用例通过说整项可交付。
- validation-16最终exit0/tree0已读回；同一Agent据源码+实际结果确认R-F8闭合。保持原R-F8报告与validation-15失败，后续完整目标仍未完成。


#### 后续动作跨BGI施工（0637ca5bd之后，未验证/未完成）

本次继续同一完整Goal，补“绑定后续执行期间BGI退出仍自动接原安排”。已开始将原生/指定后续的旧epoch直接撤权改为确切旧PID/start退出、共享停止信号有效、新BGI无明确停止后重新准备；新进程先绑定停止信号，旧请求结果未知保留原因并分配新attempt，不向新进程ACK旧结果。已经观察成功的旧结果不重跑。原生快照保留具体solo及设置，managed原Runner仍须独立原身份接续，不能借原生通道绕过。
当前新增MainViewModel.CompletionRecovery.cs抽出生产实际执行路径供受控私有IPC+自有旧进程测试直接调用；覆盖活旧进程不动作、确切退出后原生/指定后续、释放回执丢失、原停止信号。即将validation-17，源码候选不代表验证通过；跨助手/managed跨进程和四成员组合仍待完成。

- validation-17助手40/42、exit1/tree0：两项指定后续测试误要求takeoverTicket字段完全不存在，SDK冻结payload实际保留null；改为缺失或null，并额外要求executor与SDK票据均null，原失败保留。原生跨自有进程路径首跑通过。
- validation-18助手48/48、exit0/tree0：自有PowerShell等待进程真实存活/退出、生产EnsureCompletionExecutorAsync、真实SDK/v2私管、指定后续/原生恢复、释放丢回复及停止保护均过。同一Agent读实际证据后确认该段无已确认未闭问题；不包含managed/跨助手恢复，不声称真正BGI/游戏实跑。
- 新进程恢复准备使用ProcessRecoveryPreviousEpoch及CompletionRecoveryReservationAcquired持久化已取得/待释放阶段；同一重入不再分配完成动作attempt。已经成功的旧结果不因进程退出重跑；旧进程消失只解除其内存ACK义务。原停止信号缺失/已停、新停止水位不匹配、旧PID/start存活或未知均不能借恢复获得权限。

下一具体入口：RunAcceptedHoeingBatchAsync的RestoredFromRecovery目前只关门，未恢复TaskCenterHost._onlineHoeingRunId/_stateContinuationRuns，也未重建原Runner观察。应先用经过原owner死亡验证的批次快照及原ticket/run/node/提交事实恢复同一关联，沿原Runner消费已存在resume job后继续C；不能让managed快照走原生退路。当前快照没有ManagedRunId/原计划进度，持久记录丢失时仍需补退路。managed跨BGI还涉及原StopAuthority固定旧epoch，应保留原停止来源而非直接刷新权限。完整Goal保持active。
