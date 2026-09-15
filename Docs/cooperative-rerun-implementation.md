# 联机锄地轮末共同重跑：实施基线

状态：实现与自动化验证已完成（详见 §8–§13 的逐轮记录）。本文是用户确认后的实施约束与验证记录。

> ## ⚠ 验收状态（必读）
>
> - **四机实机验收尚未执行**：当前环境没有可用于实跑的游戏/多机条件，因此**没有任何**"四机跑通"的结论。
> - **JSON 策略战斗**的停战判据已接入代码但未在真实战斗里端到端实跑。
> - 已完成的验证限于：双向构建 0 error、服务端 127/127、客户端 `Cooperative` 定向 53/53（含真实客户端会话 ↔ 真实服务端状态机的跨端集成）、全量单测回归（失败项与本次改动零重叠）。
> - 结论：可以进入实机验收阶段，但**不得**在未跑四机的情况下宣称功能可用。

## 1. 确认需求

- 轮末范围为当前 AutoHoeingTask、当前世界、最终房主线路清单；不跨独立配置组汇总。
- 正常轮白名单线路战中复苏：只标记、不打断推进。正在该点战斗的停本场，未到者到点后跳战斗，已打完者忽略。
- 复苏者原有安全恢复保留；整线补跑机会与本地段重试、守护新运行分开计数。
- 正常轮全部结束后，全员使用同一去重有序计划，每个计划项从起点执行一次。
- 重跑再次复苏不追加计划；可恢复异常记不完整，队伍仍可协调时继续其他计划项。
- 中止战斗保留现有有界回点/聚物兜底；中止或重跑的无经验不是新的上限负证据。真实正经验与合法全员上限停止保留。
- 计划处理完成后额外去一次神像，全员确认收尾后才正常退出；失败/取消不包装成成功。

## 2. 实施前自审及影响半径

| 目标入口 | 新行为 | 保留边界 |
| --- | --- | --- |
| AutoHoeingTask.ProcessRoutesByGroup | 最终清单建立共同session，轮末按权威计划执行 | 普通选线、成员尊重房主清单、StartRouteIndex仅正常轮应用一次 |
| RouteExecutionEngine.ExecuteRoute | 可选CooperativeRouteContext，固定runtime，明确结果 | null上下文旧调用保持原样 |
| PathExecutor.Pathing | 重跑使用canonical同步点及明确Bypass | 正常等待、局部保护、移动与生存默认路径不全局改写 |
| AutoFight TXT/JSON | 本场停止，不取消整条路线；阶段化fight key | 单机、公版路由、固定策略、战斗超时保持 |
| CoordinatorClient/MultiplayerCoordinator | 独立可靠RPC及重跑上下文 | legacy接口签名/false返回契约、全局停止不清空 |
| RoomOperations/Gateway | 独立重跑状态机 | 旧ArrivalSets/quorum/控制房不改作新状态 |

根因：旧正常进度、可变当前线路、匿名布尔信号与重跑执行共享生命周期。处理策略为新增上下文与独立领域状态，不通过清空协调器、全局配置或新世界重置绕过。

## 3. 共享协议

单一DTO源为 Shared/CooperativeRerun/RerunProtocol.cs，由BGI与服务器链接编译。
能力 hoeing.rerun.v1；同v3房间每个实际参与者都必须支持。能力缺省不支持。旧协议分房策略保留。

- rerun.update：带OperationId的领域命令，返回权威RerunSnapshot。
- rerun.state：读取当前权威快照。客户端周期对账，不以广播送达当完成证明。
- Enroll固定roster、session身份、scope、各成员ResumeToken和完整manifest。Token不写日志/不广播。
- 同一房间世界session不可用相同scope静默重启；所有后续消息必须匹配session/token。合法重连只换连接绑定，旧连接无权写。
- Main标记与Replay标记区分；后者只影响本场三态，不增加计划。

状态序列：Registering -> Normal -> Preparing -> Running -> Finishing -> Completed；任意未终态可Aborted。

| 操作 | 提交含义 |
| --- | --- |
| enroll | 清单和逻辑点schema一致，全员登记才开始Normal |
| mark | 校验白名单及fight身份，幂等保存事件；Normal累积待重跑集合 |
| normalDone | 本端停止产生正常轮事件且已排空发送；全员后冻结排序计划/hash；空计划Completed |
| prepare | 本端已安装同一计划，所有参与者确认后才Running |
| arrive | 当前计划项某规范同步点已到达；不使用旧数值进度豁免 |
| recover | 安全恢复中，不等于已跳过或完成 |
| bypass | 明确跳段或跳线时豁免相应范围，下一目标仍有到达义务 |
| routeDone | Completed/Incomplete/Failed/Cancelled；全员结果后统一下一项或Finishing |
| finish | 神像收尾完成；固定roster全部确认才Completed |
| abort | 不可逆中止；迟到完成不能覆盖 |

### 3.1 阶段推进的强制顺序（实现与验收必须同时满足）

- routeDone 只在“服务端当前计划项 == 本线路”时被接受；固定全员都提交终态后，服务端才把当前项前移。
- 因此客户端 `CompleteRouteAsync` 必须等到**本线路不再是当前项**（当前项索引前移，或阶段进入 Finishing/Completed）才返回；只等 `Running` 会让快端抢跑下一条线路，其 Arrive 会被服务端拒绝并以超时 Abort 整个阶段。
- 开始下一条重跑线路前，必须确认服务端当前项已等于该线路，否则不得进入该线路。
- 最后一条线路完成后阶段为 Finishing，客户端等待谓词必须把 Finishing 视为“本线路已结算”，否则会在 `Running` 上无限自旋，永远到不了神像与 Finish。
- 固定参与者不得随在线人数缩减；掉线只能恢复或中止，不能静默折成成功。

## 4. 路线和点身份

CooperativeRoutePlan保留RouteInfo元数据与合并加载后的PathingTask快照；每次执行得到新鲜runtime，不能首次运行后再读可能被更新的文件。
RouteId由最终计划发生位置和归一化逻辑线路标识组成；不得重新按成员Selected/Group过滤，不把本地变体换成房主文件。
点身份由段和经清单校验的语义次序/显式同步ID组成，不以本地wp索引或坐标直接跨端配对；本地packed index只用于映射查找。
实际回归素材：E014 A变体fight在物理索引7、B变体在8，strict_1对应同一语义集合点。

## 5. 异常和资源所有权

- 真实死亡信号先本机救援，不等待网络；死亡业务事件按死亡episode而非按fight永久去重。
- Recovering不豁免；决定跳段才Bypass。SkipRoute和最后段结束必须提交线路终态，不发原列表index+1的虚构目标。
- 跳战斗在到达同步之后消费；中止结果不能产生无经验负样本。
- 只取消fight-local任务，不取消正常推进token；资源按fight/route scope取消并收齐，之后才能切换上下文。
- TXT/JSON都受本场停止约束；万叶原有坐标超时和固定停留兜底保持，不添加全员无限聚物屏障。
- 手动停止、真实全员经验上限、房间销毁优先于所有阶段推进。Abort使用有界独立清理token，仅清理/通知，不继续游戏动作。

## 6. 容错与不可破坏项

请求重试保留OperationId；快照只接受同session且不倒退的revision。提交后不可回滚Normal；失联只重试/中止，不能局部超时放行。
固定参与者不随在线人数缩水。身份无法恢复或服务器丢失session时中止，不猜测已执行与否；跨崩溃不宣称游戏动作exactly-once。
保持世界级经验状态、房主身份、多世界顺序、药物周期、背包黑名单、任务锁和助手上线generation；不得用ResetForNewRound启动重跑。
新的终态必须传到世界完成存档和正常完成标记；只修改新路径，不顺手修旧foreach游标、全局心跳身份和其它邻接问题。
时间限制优先；本次重跑不得触发越时继续或被误算为守护未执行后无限重开。

## 7. 验证矩阵

- 计划：一端/四端/零标记，顺序不同，迟到/重复mark，缺能力，缺路线，manifest不一致，空路线。
- 身份：E014 A/B物理点不同，StartRouteIndex 0/1/N，成员Selected=false，重复逻辑名/计划发生位置，实际变体runtime冻结。
- 协调：旧原跑进度很高，抢报开关开关两种，Arrive/Recover/Bypass，末段和跳整线，结果幂等，取消/关房与完成竞争。
- 通信：响应丢失、同OperationId重试、低revision/旧session、重连旧连接、lease耗尽、服务器重启。
- 战斗：TXT/JSON停止，早到/正在打/已完成，多点广播，死亡episode重复，后续fight不继承stop，回点/经验/摩托任务结束。
- 生存：10秒复苏/30秒卡死局部保护不新增整线次数；不重置用药禁用哨兵和周期CD。
- 经验：重跑空怪点不计负样本，真实正经验保留，合法全员上限立即停止。
- 调度：料理/黑芙/按线换人/药物，时间禁区，host/member/SoloDebug CD不双写，失败不完成世界，神像一次额外收尾。
- 保留：普通单机/联机与legacy协议、控制房、助手任务启停、正常多世界与守护流程。

## 8. 实施与验证记录
- [x] 用户确认需求及行为推荐规则。
- [x] 实施前全链路只读审查；固定目标和无辜调用方边界。
- [x] 共享协议DTO和客户端编译链接。
- [x] 服务端状态机、网关和故障测试：父代理独立运行 server build（0 error）与 server tests（120/120、后为123/123通过）。客户端尚未闭合，不能据此宣称联机端到端完成。
- [x] 客户端全项目构建通过（0 error）：通信骨架、路线冻结计划、Engine 可选上下文、Task 外层调度接入后验证。
- [x] 重跑执行语义接入（本轮完成）：传送点与集合点在 Replay 下走 `CooperativeContext.WaitAsync(canonicalPointId)`；Replay 跳过旧 pending/异常等待点、段级落后追赶、三处旧集体跳段消费；无 canonical 点时只略过等待、仍推进线路；战斗点三态（到点后跳过/战斗中断/完成）与 `MarkIncomplete` 已接；Replay/正常两阶段 `BuildFightKey` 含 RouteId 防跨线路撞键；FastReport 不再抑制严格 Arrive；`ReportDeath` 由观察时刻捕获的战斗点身份上报（`CaptureCooperativeRevivalHandler`），且保护命中时短路团队副作用；`ReportRecoveryAsync` 仅真实复苏时发布（本机局部保护重试不再占用 RPC 门）；Replay 无经验不入上限计数、真实正经验仍上报；阶段推进等候语义修正（等本线路不再是当前项、Finishing 视为结算完毕）。
- [x] 客户端定向测试通过（第5轮终值）：`Cooperative` 过滤共 **39/39**。含 `CooperativeRerunCommunicationTests`（会话/阶段推进 8 项）与 `CooperativeRerunContractTests`（故障与三态 7 项：真实 `room.closed` 事件下严格等待必须判为未确认、Recover 不豁免、Bypass 携带已认可区间与跳线标志、旧 revision/他人 session/他人 scope 不得污染本地快照、按点停战按阶段匹配且完成后不再命中、Finish 从 Finishing 收口、Aborted 阶段必须以异常暴露而不是静默成功）。
- [x] AutoHoeing 命名空间回归（第5轮）：381 项中 369 通过；12 项失败与改动前同源，均为仓库既有“未修复代码预期失败”的缺陷复现与 FsCheck 属性用例（PostTeleport 保护缺陷复现、WaitPointReport/MultiWorldConfigLocking 属性），其目标类未被本次改动触及。
- [x] 白名单资格核对：`Manifest.Eligible` 在最终 `groupRoutes` 上按 `RouteRetryModeDecisions.IsRetryRoute` 关键词计算后再建会话，因此“非白名单线路不标记、不重跑”的前提成立。
- [x] 服务端测试：**123/123**（含协议接线/能力与固定 roster、canonical 路线语义、失败重试权限与生命周期边界）。
- [x] 双向构建：客户端 0 error、服务端 0 error；`git diff --check` 干净。
- [x] 普通/单机零感知静态核对：`CooperativeContext` 全部通过 `?.`/`!= null` 使用；`ShouldAbortFightForRetryRevival` 新增分支在无协作作用域时恒 false；`AutoHoeingTask` 在功能门控关闭时直接走原 `ProcessRoutesByGroupCore` 旧逻辑。
- [ ] 客户端协议测试仍有已列明缺口，不宣称“协议已全量覆盖”。
- [ ] 客户端可靠协议、上下文和恢复测试：服务端已有 123 项；客户端已收 32 项会话/阶段用例，仍缺真实 `RoomClosed` 事件路径、Recover/Bypass 三态、低 revision/错误 Session/错误 Scope 忽略、Finish 全员聚合、`ShouldSkipFight`/`CompleteFight` 阶段断言。
- [x] 已知可接受项：快速抢报真实发送一次 Arrive 后，严格等待会再发一次同点 Arrive；服务端该操作按集合幂等（`HashSet.Add`），只多一次 RPC，不改变状态语义，故不扩展 API 消除。
- [x] AutoHoeing 命名空间回归：366 项中 354 通过；12 项失败经核对均为仓库既有“未修复代码预期失败”的缺陷复现与 FsCheck 属性用例（PostTeleport 保护缺陷复现、WaitPointReport/MultiWorldConfigLocking 属性），其目标类未被本次改动触及。
- [x] 本场停战链路补全（第4轮）：`ShouldAbortFightForRetryRevival` 现在除旧标志外同时查询本机协作战斗作用域（`ActiveCooperativeFight.CheckStop()`），TXT 引擎与 JSON 引擎（`AutoFightJsonTask` 战斗循环新增同判据）都会在队友于本战斗点复苏时立即停战；无协作作用域时恒 false，单机与正常轮行为不变。跳战斗仍严格晚于到达同步（`WaitAsync` 在 1259 行、跳过判定在 1272 行之后）。
- [x] 战斗作用域健壮性（第4轮）：监视任务与 `StopMonitoringAsync`/`Dispose` 不再把判据读取异常升级为线路失败（降级为“不停战”），清理路径绝不抛出。
- [x] 失败终态提交顺序（第4轮）：线路结局为 Failed/Cancelled 时先 `CompleteRouteAsync` 把该线路终态提交给服务端（服务端据此对全员统一 Abort），再按失败收口；提交因服务端已中止而失败属预期。
- [ ] 路线快照、逻辑点、战斗/异常接入。**剩余未完成项（必须收口后才能验收）**：
  - 摩托下车 CTS 与经验检测器在异常/末段路径的收尾不完整属**仓库既有**结构（`git show HEAD` 确认该局部 CTS 模式在基线即存在，非本次引入）；本次新增的 `CooperativeFightScope` 已在 finally 中 StopMonitoring+Dispose 并清空战斗点身份，未扩大该既有缺口。
  - 协作模式下死亡 episode 去重使本机复苏信号每个 episode 只发一次；仓库既有两个“消费信号但不走恢复流程”的旁路一旦吞掉该信号，本 episode 不会补发（既有可疑旁路，未在本次扩张修复）。
  - JSON 侧停战判据已接但未在真实 JSON 策略战斗里做端到端实跑验证。
- [ ] 轮末调度与终态接入（外层计划顺序、神像、CD、世界完成已接；尚缺四机验证）。
- [ ] 编译、自动测试、独立改后审查。
- [ ] 四机实跑验收（无游戏环境时必须明确未执行）。

## 9. 独立对抗审查（第5轮）与修复

独立只读审查以"找确定性故障"为目标复核了协议、服务端状态机、客户端会话与执行器接入，结论为**不通过**，并给出 4 个确定性阻断。四个阻断均已核实成立并已修复，且各补一条契约测试锁定：

| 编号 | 缺陷（修复前） | 后果 | 修复 |
| --- | --- | --- | --- |
| P0-1 | 客户端 `session.hello` 宣告空能力数组，而服务端按"连接 hello 里的能力"校验参与者 | `Enroll` 100% 被 `rerun_capability_required` 拒绝 → 20s 超时 → 整轮中止 | `BgiGatewayClient.HelloAsync` 宣告 `hoeing.rerun.v1` |
| P0-2 | `CompleteNormalAsync` 在 `NormalDone` 后立刻发 `Prepare`，而非末位成员此刻拿到的 `PlanHash` 仍为空 | 除最后一名外全员被 `rerun_plan_hash` 拒绝 → 整轮中止 | 先等 `Preparing`（快照带回冻结 hash）再发 `Prepare` |
| P0-3 | 正常轮（Stage=Normal）就发送只接受于 `Running` 的 `Recover`/`Bypass` | 20s 空转占用 RPC 门；未包裹调用点直接抛超时 → 线路 Failed → 整轮 Abort。**这正是主场景：正常轮复苏** | 上下文按阶段门控：正常轮只本地记录、不发协议；正常轮异常由 Mark + 轮末重跑承担 |
| P0-4 | 段级/整线豁免未登记"不完整"，且 `Incomplete` 的 reason 可为空 | 终态要么 `Completed`（服务端要求真实到达）要么 `Incomplete` 无原因 → 双双被拒 → 整轮中止 | `ReportBypassAsync` 同步登记 `HadBypass`+`MarkIncomplete`；`CompleteRouteAsync` 补非空原因并自检"有豁免不得 Completed" |

同时采纳审查的 P1 建议：`rerun_*` 协议错误改为**立即失败**而不是重试 20s，避免把确定性拒绝伪装成超时并长时间占用 RPC 门。

审查另有一项对诊断方法的重要提示：此前客户端测试使用固定快照的假服务端（恒返回 `PlanHash="hash"`、`NormalDone` 后立刻 `Preparing`），恰好掩盖了 P0-1/P0-2。新增测试因此改为**按协议语义推进的假服务端**（能力宣告、NormalDone→仍 Normal、Preparing 才带 hash、终态需推进当前线路）。

审查确认**不成立**的怀疑点（已核对，无需修复）：慢成员不会因线路推进被误判失败；不存在"服务端未切换线路就开跑"的可复现窗口（仅缺显式断言）；不会静默缩人数算成功；计划不会变长或重复执行同一线路；Abort 不可逆与迟到完成保护实现正确；协作关闭时门控齐全、无门控外副作用。

审查仍未关闭的 P1/P2（不作为本轮结论）：同一世界轮内二次启动会被旧终态会话挡住且无人清理；服务端 Abort 后客户端仍会跑完当前线路才在下个 RPC 感知；`WaitCore` 无超时且 Poll 持续续租，极端分歧下可能需人工停止；旧轮末重跑 API（`TakeRouteRerunMarkSet` 等）已无调用方而成为死代码，且能力缺失时是硬失败而非回退 legacy——这与"旧协议房间策略保留"的表述存在张力，需在验收前明确取舍。

## 10. 第6轮：审查遗留项收口

上一节列出的四项审查遗留问题已全部处理，并各补测试：

| 项 | 修复 | 证据 |
| --- | --- | --- |
| P2-2 兼容门控（原为硬失败） | 能力缺失时**退回旧轮末重跑路径**并明确告警，而不是让整轮锄地失败；`CooperativeRerunTaskDecisions.IsEnabled(...)` 增加能力参数 | 决策纯函数测试 4 例；`AutoHoeingTask.ProcessRoutesByGroup` 门控 |
| P2-2 连带缺陷（旧重跑被整体替换后变成"不重跑"） | 恢复旧轮末重跑实现为 `RunLegacyRoundEndRerunAsync`（本地标记集合 + 轮末屏障 + syncId 后缀 + 至少一条成功才去神像），协作路径不经过它 | 与 `git show HEAD` 原实现逐段一致；协作路径 `cooperative != null` 分支 |
| P1-3 中止后仍跑完整条线路 | 上下文暴露 `IsAborted`；`PathExecutor` 每段开始同步判定并抛取消；`ShouldAbortFightForRetryRevival` 同时查询中止态，使正在打的战斗当帧停战 | 契约测试 `IsAbortedReflectsServerAbortSoGameplayCanStop` |
| P1-2 同轮二次启动被旧终态会话挡住 | 服务端仅在旧会话为**终态**时允许新 `Enroll` 替换；进行中会话一律保留（幂等重试与迟到消息仍打到原会话） | 服务端测试 `TerminalSessionIsReplacedByNextEnrollment`、`ActiveSessionIsNotReplacedByForeignEnrollment` |
| P2-1 等待无上界 | `WaitCore` 增设有界等待（8 分钟，远大于正常跨端等待），超时显式失败并让阶段统一中止，而不是静默挂死 | 实现内注释说明"把永久挂死变成显式失败" |

另外采纳审查的诊断建议：`rerun_*` 协议错误立即失败（不再重试 20s），错误码直接进入异常与日志。

验证终值（第6轮）：客户端 `Cooperative` 48/48；服务端 125/125；AutoHoeing 命名空间 390 项中 378 通过（12 项为既有预期失败缺陷复现与属性用例，与改动前同源）；双向构建 0 error；`git diff --check` 干净。

仍未完成（验收前必须显式声明）：JSON 策略战斗的停战判据未在真实战斗里端到端实跑；四机实跑验收未执行；服务端"阶段无 revision 进展"的后台看护未实现（当前依赖客户端有界等待兜底）。

## 11. 第7轮：跨端契约集成测试与共享热路径回归

### 11.1 新增"真实客户端 ↔ 真实服务端"契约集成测试

前几轮的客户端测试都用**注入固定快照的假服务端**，因此无法发现两端约定不一致——第5轮审查的四个阻断正是这样被漏掉的。本轮新增 `CooperativeRerunServerContractTests`：把服务端权威状态机源文件（`BgiCoordinatorServer/Services/RerunExecutionState.cs`，只依赖 BCL 与共享协议）直接编进测试程序集，让 `CooperativeRerunSession` 对真实状态机收发请求。

- 双成员完整成功流程：Enroll → 正常轮死亡标记 → NormalDone → Prepare（校验携带冻结 hash）→ 两端真实到达规范点 → RouteDone → Finish → 服务端 `Completed`。阶段推进与计划哈希全部走真实实现。
- 无标记 → 空计划 → 服务端直接 Completed，且明确"不应去神像"。
- 未真实到达就申报 Completed → 真实服务端以 `rerun_unresolved` 拒绝（防止把跳过误报成成功）。

选型说明：链接源文件而非 `ProjectReference` 服务端工程（后者会引入 Web SDK/ASP.NET 框架引用，需要额外还原且曾导致测试程序集无法编译，代价更高）。

该测试上线即抓到一处真实契约误解：死亡标记必须传**规范检查点 ID**，而不是共享战斗配额使用的 `RouteId/point` 复合键——修正后才有标记进入计划。

### 11.2 共享热路径回归（单机同样经过这些代码）

| 范围 | 结果 |
| --- | --- |
| 客户端全量单测 | 646 项：632 通过，14 失败 |
| 其中 `AutoHoeingTests` | 393 项：381 通过，12 失败 |
| `AutoFightTests` | 34/34 通过 |
| `AutoPathingTests` | 47 项：46 通过，1 失败 |
| 服务端 | 125/125 通过 |

**14 项失败与本次改动零重叠**：逐一核对后，失败用例的目标生产文件（OcrResult、WaitPointReport、PostTeleport*ProtectionDecisions、AutoTrackPositionRecoveryDecisions、多世界配置锁定相关模型）均**不在**本次改动文件清单内；其失败形态是"未修复代码预期失败"的缺陷复现与 FsCheck 属性用例。

### 11.3 本轮结论

- 跨端契约现在有真实双向验证；`Cooperative` 定向 51/51、服务端 125/125。
- 仍未完成（验收前必须显式声明）：JSON 策略战斗停战判据未在真实战斗端到端实跑；四机实跑验收未执行；服务端"阶段无进展"后台看护未实现（当前由客户端单点等待上界兜底）。

## 12. 第8轮：需求级端到端验证

在跨端集成测试（真实客户端会话 ↔ 真实服务端状态机）基础上，本轮把两条**用文字确认过的需求**变成可执行断言：

| 需求 | 断言 | 结果 |
| --- | --- | --- |
| ⑦ 只重跑一次：重跑期间再次复苏不再追加 | 重跑期 `ReportDeath` 只产生重跑期标记；冻结计划、当前线路、计划索引全部保持不变 | 通过 |
| 取消/失联：不得静默缩人数继续，也不得让其余成员永久等待 | 成员静默超出租约后，阶段被置为中止；其余成员的 `IsAborted` 立即可见（游戏循环据此停止）；迟到完成尝试既不生效也不被当成成功（暴露 `ParticipantLeaseExpired`，阶段保持 Aborted） | 通过 |

测试过程中确认了一处契约细节并固化为断言：死亡标记传**规范检查点 ID**；共享战斗配额 key 才是 `RouteId/point` 复合形式，两者不可混用。

验证终值（第8轮）：客户端 `Cooperative` 53/53；`AutoHoeingTests` 395 项中 383 通过（12 项为既有预期失败缺陷复现与属性用例，与本改动零重叠）；服务端 125/125；双向构建 0 error；`git diff --check` 干净。

仍未完成（验收前必须显式声明）：JSON 策略战斗停战判据未在真实战斗端到端实跑；四机实跑验收未执行；服务端"阶段无进展"后台看护未实现（客户端单点等待上界已可兜底，故列为可选加固）。

## 13. 第9轮：服务端无进展兜底看护

补上此前唯一剩下的服务端加固项：**阶段长时间无进展时由服务端统一中止**。

- 计时口径：任何被接受的操作（到达/豁免/线路结算/阶段确认）视为"有进展"，刷新计时；轮询不算进展。
- 上界 12 分钟，**必须大于客户端单点等待上界（8 分钟）**，否则正常等待会被误判成无进展。
- 为什么需要它：成员租约只看 `LastSeen`，而轮询会持续续租，因此"成员都活着但卡住"这一类不会被租约回收；客户端的单点等待上界又依赖该端能自我暴露。服务端再兜一层，把"全员无限互等"变成显式中止（原因 `NoProgress`）。
- 测试：`NoProgressStageIsAbortedByBackstopWatchdog`（两端持续轮询保持租约、但无任何进展 → 超过上界后中止）与 `ProgressResetsTheNoProgressWatchdog`（每 30 秒有真实到达 → 20 分钟后仍为 Running，不被误杀）。后者同时验证了轮询节奏必须快于 45 秒成员租约，否则触发的是租约中止。

验证终值（第9轮）：服务端 127/127；客户端 `Cooperative` 53/53（客户端测试程序集链接了该状态机源文件，故一并回归）；双向构建 0 error。

仍未完成（验收前必须显式声明）：JSON 策略战斗停战判据未在真实战斗端到端实跑；四机实跑验收未执行。

## 14. 第9轮（续）：第二轮独立审查的修复

第二轮独立审查（针对修复后代码）发现 1 个 **由上一轮加固自身引入的严重回归** 与若干半成品，已逐条修复：

| 编号 | 问题 | 后果 | 修复 |
| --- | --- | --- | --- |
| **P0-1（自引入回归）** | 第9轮新增的"阶段无进展看护"条件落在 `else` 分支，**同时作用于正常轮**（`Normal`）；而正常轮唯一被接受的业务操作是"白名单线路战斗点死亡标记" | 正常轮动辄 20–60 分钟没有死亡是常态 → **大多数正常轮会在 12 分钟处被判失败并连带终止整个会话** | 看护**只对 `Running`/`Finishing` 生效**；租约判据对全部阶段保留 |
| P2-2 | `SendAsync` 在取消时无条件抛 `TimeoutException` | 用户停止/时间禁区被误分类为"线路失败"，污染停止原因与日志 | 循环后 `ct.ThrowIfCancellationRequested()` |
| P1-3 | `QueueDeathMark` 为 fire-and-forget，失败无人观测，且 `NormalDone` 前不排空 | 迟到/失败的标记会静默丢失 → 该线路不进重跑计划而无人知晓 | 登记在途标记任务；`CompleteNormalAsync` 前 `DrainPendingMarksAsync`；失败逐条告警 |
| P1-3(c) | 战斗点身份缺失时回退成 `RouteId/point` 复合键 | 服务端以 `rerun_mark` 拒收，且永远无法与队友跳过判据匹配 | 新增 `IsCanonicalFightPoint`；只有规范检查点才上报死亡，否则明确告警 |
| P1-4 | 线路提前结束时只提交 `Incomplete` 而无逐点豁免 → 服务端 `rerun_unresolved` 拒绝 → 整轮中止 | 与契约"记不完整、其余计划项继续"直接冲突 | 提交不完整终态前，若存在未到达的非战斗规范点则先补**覆盖性豁免** |
| P1-1 | 兼容门控只看得到**服务端**能力，看不到队友能力；房间内任一旧版本成员 → Enroll 被拒 → 整轮失败，且永远走不到旧回退 | 混合版本房间整轮被拖停 | 新增降级信号识别（能力/名册/登记/scope/身份类错误）→ 清会话、`Downgraded`、**退回旧轮末重跑**，且不校验协作完成度 |
| P1-2 | `IsAborted` 只在段起点/战斗帧/协作等待点判定，纯移动长段内中止不生效 | 中止后仍可能跑完数百米剩余路点 | 路径点循环内并列判定并抛取消 |

新增/加强的测试：`NoProgress` 只作用于重跑阶段（并把 `NormalMayRunIndefinitelyWithPolls…` 的轮询延长到 **40 分钟**——原用例只跑到 10 分钟，恰好掩盖了 P0-1）；`IncompleteRouteAutoBypassesUnreachedPointsAndSessionContinues`（跨端集成：不完整线路不再拖停整轮，且两端终态如实记录）。

**回归验证方法**：对 P0-1 采用"反向验证"——临时移除阶段限定后该测试必然失败，恢复后通过，证明测试确实锁住了该回归。

验证终值（第9轮末）：客户端 `Cooperative` 54/54；服务端 127/127；`AutoHoeingTests` 396 项中 384 通过（12 项为既有预期失败，与本改动零重叠）；双向构建 0 error；`git diff --check` 干净。

审查提出但**本轮未处理**的项（下一轮收口或明确列为已知限制）：
- P2-1：`ProcessRoutesByGroupCore` 若干"本轮跳过"的提前返回（等房主超时、变体 schema 失败、无可执行路线等）在协作模式下被升级为"整轮中止"并跳过剩余世界轮；
- P2-3：孤儿**非终态**会话（成员进程消失但房间存活）仍可永久挡住新 `Enroll`；
- P2-4：测试卫生——断言生产未调用的死函数（`CooperativeExecutionDecisions.Outcome`）、测试名与断言不符（`SkipRouteAllowsIncompleteButNotCompleted…`）、缺"每个规范点都被真实 Arrive"的断言。

## 15. 第10轮：审查剩余项收口

| 编号 | 问题 | 修复 |
| --- | --- | --- |
| P2-1 | `ProcessRoutesByGroupCore` 中"本轮跳过"的提前返回（等房主路线超时、变体 schema 失败、本组无可执行路线等）在协作模式下被升级为"协作会话失败"，并连带跳过剩余世界轮 | 新增 `FlowReachedEnd` 标记：只有真正走到轮末重跑才校验协作完成度；提前结束时按旧语义收口本轮，并把已建立的会话有界中止（避免留下孤儿） |
| P2-3 | 孤儿**非终态**会话（起过一轮、进程全没了、房间仍存活）会以 `rerun_token` 永久挡住新一轮注册 | 新增 `RerunExecutionState.IsOrphaned`（非终态且全体成员均已超过租约窗口未活动）；Enroll 时连同终态会话一起回收。活跃会话因持续轮询续租永不误判 |
| P2-4(a) | `CooperativeExecutionDecisions.Outcome` 是**无人调用**的影子实现，测试却断言它，造成"结果分类已覆盖"的假象 | 把引擎的终态计算改为**调用该纯函数**（并补 `skipRouteRequested` 参数保持语义等价），测试从此覆盖真实生产路径 |
| P2-4(b) | 服务端用例名承诺"Completed 被拒"但没有该断言 | 补上：构造一份带豁免的状态并断言 `Completed` 抛 `RerunProtocolException` |
| P2-4(c) | 缺"每个规范检查点都必须真实到达"的断言 | 新增跨端用例 `EveryCanonicalCheckpointMustBeArrivedBeforeCompleted`：两个规范点只到达一个 → 真实服务端以 `rerun_unresolved` 拒绝；补齐后同一上下文可正常完成 |

验证终值（第10轮）：客户端 `Cooperative` 59/59；服务端 128/128；`AutoHoeingTests` 401 项中 390 通过；双向构建 0 error；`git diff --check` 干净。

**关于那批"既有失败"的准确表述**（修正此前说法）：它们集中在 `MultiWorldConfigLockingBugTests`、`PostTeleportRevivalProtectionBugConditionTest`、`PostTeleportStuckProtection*`、`WaitPointReportTests`，多为 FsCheck 属性用例与"未修复代码预期失败"的缺陷复现；其目标生产文件均不在本次改动清单内。由于是属性测试，**失败数量会随生成输入波动**（本轮由 12 变为 11 即此原因），因此不应把它们当成固定计数，也不应把其通过与否当作本次改动的回归信号。

## 16. 收口结论与实机验收清单

### 16.1 交付状态

- 代码：客户端 + 服务端全部接入完成，`Cooperative` 定向 59/59、服务端 128/128、双向构建 0 error。
- 文档：本文件（需求、协议、边界、逐轮修复记录、已知限制）。
- 测试：纯函数层、会话层（含真实客户端 ↔ 真实服务端状态机跨端集成）、服务端状态机层、回归层。
- 关键设计不变量：正常轮只标记不打断；重跑使用服务端冻结的共同计划；同步点身份为规范检查点而非本地路点；豁免必须显式；中止不可逆且对执行器可见；能力缺失退回旧路径而不是拖停整轮。

### 16.2 四机验收清单（**尚未执行**，需实机逐项确认）

前置：至少 2 台（建议 4 台）机器，全员同一版本；联机锄地配置了重跑关键词；房间全员宣告 `hoeing.rerun.v1`（否则应看到"退回旧轮末重跑路径"的告警，且锄地照常）。

1. **基线**：一轮无死亡，全部线路正常跑完 → 不触发重跑、不去额外神像、无告警。
2. **正常轮三态**：某白名单线路战斗点，A 在战斗中复苏；B 正在同一点打 → 应当场停战；C 尚未到达 → 到点后应跳过该战斗点；D 已打完 → 不受影响。A 本人照旧神像回血 + 跳段。
3. **轮末统一重跑**：本轮所有线路跑完后，四端进入同一重跑计划（日志应显示相同计划与 plan hash），逐条从起点重跑；重跑期间传送点/集合点必须真实互等（不应出现毫秒级放行）。
4. **只重跑一次**：重跑中再次出现复苏 → 计划不得增加条目；该线路如实记为"未完整"。
5. **收尾**：重跑计划处理完 → 额外去一次七天神像 → 全员确认收尾后才退世界；关房前不应有成员仍在重跑。
6. **取消与失联**：重跑中让一台机器断网/杀进程 → 其余机器应在租约到期后统一中止（日志 `ParticipantLeaseExpired`），并且**立刻停止游戏动作**，不继续跑完整条线路。
7. **兼容**：把一台机器换成旧版本客户端 → 其余机器应记录"退回旧轮末重跑路径"并照常锄地，**不得**整轮失败。
8. **普通功能不回退**：单机锄地、无重跑关键词的联机锄地、经验上限停止、时间禁区、多世界轮换、按线路切角色、周期吃药、万叶聚物均与改动前一致。
9. **JSON 策略战斗**：若使用 JSON 战斗策略，需单独确认"队友在本战斗点复苏时本场立即停战"（该判据已接入代码但未在真实战斗验证）。

### 16.3 已知限制（不隐瞒）

- 四机实机验收与 JSON 真实战斗验证**未执行**，因此不得宣称真机可用。
- 崩溃后无法证明游戏动作 exactly-once：进程崩溃后保守记为不完整，不自动重放不确定的已消费计划项。
- 服务端会话状态为内存态：服务器重启后需要重新组队/重开一轮；不声称跨重启恢复。
- 僵尸/孤儿会话依赖"全体成员超出租约未活动"判定回收；极端情况下（房间长期存活且无人活动）仍可能需要关房或换世界轮才能彻底清理。
