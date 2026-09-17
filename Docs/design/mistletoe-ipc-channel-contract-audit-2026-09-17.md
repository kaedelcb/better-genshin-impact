# 槲寄生 IPC 通道合同审查（2026-09-17）

## 1. 结论与范围

**更新：用户授权“那就修”后，本报告 C01–C05 及 S01/S02/S05 的现有通道修复已经实施，最终通道回归 52/52。S03 完成当前茶包格式的版本绑定单项接口；S04 补齐 IPC 配置写入的接管守卫。不能把这些修复等同于原生公版任务模型迁移（R3）或完整 managed 模式（R5）已完成。**

本报告第 2–5 节保留修复前的失败证据；当前实现、验收和未覆盖范围以第 6 节为准。无需靠游戏实机才能判定这些通道缺陷，亦不承诺未经实现的计划能力。

保留 v2 信封、ext 能力扩展、作业查询/事件及现有协调器是可行的演进方向；本次没有发现必须更换命名管道传输技术的证据。但是“可以继续扩展”不等于“上层调度器现在即可依赖它获得可靠语义”。

依据：槲寄生总计划 §3.4–§3.5，以及 `onedragon-public-compatibility-audit-2026-09-17.md` §7 的身份、配置应用、接管与恢复合同。当前审查基于包含 `d3af610c` IPC 修复的工作树；开始核对的 HEAD 为 `e6b4a5a0`（后者是独立桌宠提交）。用户原有总计划及其他文档改动保留。

初次审查仅增加测试和报告；随后用户明确授权修复，本轮已经修改生产代码及三端兼容调用方，尚未提交、未部署。没有连接/操作前台 BGI、助手或游戏，不读写其运行配置。调度器业务（触发器、Planner、RunStore 等）的实现不纳入此次修复。

## 2. 实测与可信边界

- 原事故回归 `Test/IpcIncidentAudit`：本轮 35/35，退出码 0。
- 新增通道合同回归 `Test/IpcChannelContractAudit`：21 项，14 通过、7 失败；完整集合三个新进程复跑结果一致，每次退出码 1。
- 新集合直接链接 9 份生产源码：InstanceIpcProtocol、ExternalInterfaceProtocol、ExternalInterfaceSession、ExternalInterfaceCommandPlane、ExternalInterfaceQueryPlane、BgiTaskCoordinator、BgiJob、JobRegistry、PreemptionGate。
- 只有帧/会话/队列/注册表等通道代码是真的；游戏执行、配置写入 handler、DI、事件发送和执行槽使用内存替身。无产品工程引用、真实管道、网络、WPF 或输入调用。
- T19 是计划能力缺失的结构合同断言；其余 6 个失败断言为现有代码路径的行为复现。7 个失败断言不是 7 个互不相关的根因。
- 本次没有重跑全量产品单元测试，亦没有用历史测试计数冒充本次运行。既有 35 项没覆盖完整会话层、响应缓存、真实队列超时清退和新计划身份，因此不能推导整体通道通过。

复跑命令和逐项结果见 [测试说明](../../Test/IpcChannelContractAudit/README.md)。测试保留失败，不把“复现缺陷”伪装成绿色验收。

## 3. 已动态复现的阻断项

### C01：写请求幂等不是原子的，也未绑定请求内容（P1）

位置：`ExternalInterfaceSession.RouteAsync`，`JobRegistry.TryReplayIdempotent/CacheIdempotentResponse`。

当前流程为查缓存 → await 执行 → 缓存成功响应；多个连接同时到达相同 key 时，都能在缓存写入前进入执行。T09 用两个真实会话和受控内存写 handler 同时投递，写入边界被调用 2 次。

缓存键只有 `key:{idempotencyKey}` 或 `rid:{requestId}`，没有校验操作名和有效请求参数。T10 改变 taskIndex/enabled 仍得到旧成功响应；T11 在相同 key 下先写配置再发 stop，stop 得到配置写的旧成功响应，未执行 stop 边界。客户端正常生成唯一 key 能减少碰撞，却不是服务端已经履行冲突拒绝合同的证据。

修复要求：进程级原子取得执行权；同一规范化请求在途时共享同一个结果；同 key 不同操作/参数明确 `idempotency_conflict`（或明确隔离操作命名空间，绝不能重放错操作回执）。摘要至少绑定操作、目标、有效配置/任务参数；区分不可变业务请求与续租等传输附加字段。停止仍必须可独立及时到达，不能用单个跨 await 全局锁串行所有命令。

### C02：缓存压力使已完成请求在标称时间窗口内重复执行（P1）

位置：`JobRegistry.IdempotencyWindowCapacity = 128`、缓存容量淘汰；`BgiTaskCoordinator.Submit`；`JobRegistry.Submit/TryMarkTerminal`。

响应缓存标称 TTL 30 分钟，但第 129 个键起会淘汰旧条目。作业强 key 索引只保留活跃项，终态后移除；协调器对带显式 key 的终态请求没有完整的终态去重回执。

T21：以明确 key 提交并完成一次无副作用任务 → 130 个内存配置写请求制造缓存压力 → 立即以原 key、原参数重试。第二次再次进入模拟执行，计数为 2，远未到 30 分钟。通过真实会话、控制面、协调器和注册表组合复现，不是直接绕过入口调用执行体。

修复要求：已接受请求的执行事实/终态去重记录不能只依赖可被任意写操作挤出的响应缓存。保证约定保留期内可采用原 job/终态；如果容量不足以履约，应在新请求产生副作用前背压，或提供明确的安全拒绝/未知状态机制。已失去权威记录的重试不能当新执行默默放行。BGI 重启仍以 epoch 隔离，不要求在 IPC 内做游戏全历史重放。

### C03：队列等待超时后未释放在队容量（P1）

位置：`BgiTaskCoordinator.DispatchItemAsync` 的 `!slotFree` 超时路径。

记录 failed 终态并 Dispose 项级 CTS 后直接返回，没有从 `_pending` 移除。T16 使用 25ms 测试等槽上限，权威状态已 failed，QueueDepth 仍为 1。后续请求会受到不存在的排队项挤占；重复发生可耗尽容量。清退路径再对已 Dispose 的 CTS 操作亦有风险（后者为源码推断，未单独作为已动态复现结果）。

修复要求：所有未进入运行的终态出口统一原子清退；终态记录、容量释放、取消和 CTS Dispose 各自只发生一次。补超时→清队→再次入队及并发取消测试，不能只校验错误消息。

### C04：启动入口未执行请求侧纪元隔离（P1）

位置：`ExternalInterfaceCommandPlane.DispatchTaskStartAsync`；真实 `InstanceRequestHandler.HandleTaskStart/ExecuteTaskStartCoreAsync`。

T18 发出合法组名、明确 key，但携带旧 `bgiEpoch.startTicksUtc=-1` 的请求，真实控制面仍入队并调用模拟执行一次。源码复核：suspend 校验 epoch，但普通 task.start 仅校验冷却/服务/票据，没有对应请求纪元守卫；真正执行段也没有接收/验证该字段。响应携带新 epoch 不能替代动作发生前的拒绝。

修复要求：新调度能力的写请求绑定目标实例与 epoch，在产生副作用前拒绝不匹配；排队到派发时重新验证必要前置条件。旧客户端按 capability 保留明确的旧能力范围，不把“省略字段”误认为满足新合同。查询旧句柄可以返回新 epoch + not_found，但不得据此重发执行。

### C05：作业合同缺少计划节点身份（R2 能力缺口）

位置：`BgiJob`、`JobDescriptor`、`ExternalInterfaceQueryPlane.SerializeJob`、控制面 TaskSubmission 构建。

T19 表明 BgiJob 没有 WorkflowRunId/NodeId/Iteration。JobDescriptor 虽已有 WorkflowRunId 用于根恢复，但普通 ext 提交未透传完整流程身份，查询也没有输出。当前 jobId/key/父 job 足以覆盖部分既有组/龙场景，不足以直接提供计划中跨轮次和重复节点的统一观察合同。

修复要求：定义并端到端传递 workflowRunId、稳定节点出现位置、iteration、attempt 与配置身份；沿用 jobId 作为尝试身份时明确契约，无需机械新增同义字段。对应事件、查询、SDK DTO 和旧端 capability 门控必须一起验证。

## 4. 源码审查确认、尚未做动态故障注入的缺口

| 编号 | 发现与证据 | 对槲寄生的影响 | 必要处理 |
|---|---|---|---|
| S01（P1） | `CommandExecutor.WaitConfigWritesDrainedAsync` 等 10 秒后记录“继续启动”；网页 `control-room.js` 等 `sendRemoteCommand` 返回，网关只返回转发 ack；网页还吞单项保存异常后继续 | 未保证新配置已应用就启动，可执行旧启用状态 | 配置写入响应携带已应用 revision/目标/命令身份；依赖启动校验 expected revision；失败、超时、未知一律不得继续依赖启动 |
| S02（P1） | `TryStartViaQueueAsync` 发送后超时/断线或缺句柄返回 null；`StartGroupAsync/StartOneClickAsync` 随后走 v2；该旧调用不带贯穿两条通道的强 key，generation 默认可为 0 | 已受理但应答丢失时，切通道可能重放；不能将已改好的 OnlineBatch 路径泛化到所有入口 | 分开“发送前不可用”与“发送后结果未知”；后者必须按同一请求身份对账，禁止盲目换通道重发或启动回退 |
| S03（R2/R3） | `HandleSetTaskEnabled` 读取 int taskIndex；config.list/一条龙仍输出旧整数索引结构；TaskSubmission 只接组/整龙+StartFromIndex | 不支持标准配置字符串 taskId 的精确单项执行合同 | 单项执行适配、稳定 ID/配置修订/参数快照、旧 index 映射与能力声明；不能拿组项目序号代替原生任务 ID |
| S04（R5） | 票据守卫覆盖启动、热键、接管恢复；`HandleSetTaskEnabled` 仍直接按文件读改写，没有接管所有者/配置修订检查 | 不能承诺 managed 模式下所有会改变计划的入口都受同一权限约束 | 盘点所有修改执行计划入口并统一校验，保留 F11 最终停止权；不把只读状态永久写入用户配置 |
| S05（R2 远程调用兼容） | `RemoteCommand` 仅 Timestamp、Target UID 等；RoomManager 缓存列表原样入队/取出；RoomOperations 重连原样投递，未见过期/实例/revision 守卫；助手所查消费路径未见此约束 | 延迟命令可能在目标状态变化后仍执行 | 增加有效期、目标实例/epoch、必要 revision 前置条件，投递与执行两端检查；本机优先模式本身不依赖服务器上线 |

上述旧远程配置开关路径与已有独立“远程配置组编辑”功能不是同一合同，不覆盖或撤销后者的用户改动。本报告没有运行关闭游戏/重启回退路径，只审查调用关系。

另：助手 SDK `ReadEnvelopeAsync` 对 payload type/version 的严格检查弱于 BGI 侧；本次 T01–T06 仅测试 BGI 编解码器，不据此宣称两端所有异常帧互操作已通过。应在 SDK 接收合同测试中补齐。

## 5. 修复顺序和可判定的退出条件

1. **先修现有可靠性缺陷（C01–C04、S02）**：在途幂等合并和请求指纹、终态去重保留/背压、超时队列清退、epoch 校验、未知结果禁止跨通道重放。复用当前注册表/协调器，不另建第二套竞争的状态源。
2. **补槲寄生所需新增接口合同（C05、S01/S03）**：冻结字符串 taskId、revision、节点/轮次/尝试、单项执行、配置应用回执；BGI/助手及既有远程调用方同步适配和能力门控。
3. **补管理态入口与延迟命令边界（S04/S05）**：所有者守卫、到期与失联退出、执行前条件验证；与上层 RunStore 的职责区分清楚。
4. **通道本身的隔离验收**：本报告 21 项按原正确合同全部通过；补充真实 SDK 的独立测试端点互操作、断流/应答丢失/并发/重连/容量/配置应用故障注入。测试端点必须唯一命名且自建，不能发现/连接用户运行的产品实例；文件写入仅用测试临时数据。该阶段不需要实际游戏动作。

第一步是既有代码缺陷修复，第二/三步包含计划尚未实现的能力，不能混称“再补几个测试即可”。接口命名与兼容策略须在实施中统一；本次审查没有擅自实现整份 R0–R6 或标记任何产品阶段完成。

**判定标准：通道合同未通过就不能承诺“符合槲寄生全部通道需求”；通道将来通过，也不自动代表上层调度器业务已完成。** 实机游戏验收是另一个维度，不用于推迟本次已经有证据的“不达标”结论。

## 6. 授权实施后的修复及验收

### 6.1 已落地的变化

1. **原子幂等及背压**：在同一个 JobRegistry 中加入请求回执账本。不同会话的同键同参数请求合并等待；同键不同操作/参数返回 `idempotency_conflict`。在途回执不淘汰，完成回执保留 30 分钟；4096 条容量满时在执行前拒绝新写入，不挤掉尚在保留期的事实；安全停止仍可达。只允许明确的受理前拒绝重试，执行失败/未知结果不自动重跑。旧缓存 API 为兼容已有调用保留，但 ext 会话不再使用它判断执行幂等。
2. **队列、纪元和期限**：等槽超时先移除容量索引再释放 CTS；补充超时/取消交替 40 次的压力断言。供应方传入的旧 epoch 不再被忽略；执行派发前再次检查期限。日期 token 保留毫秒及 offset；期限只禁止新执行，不抹掉已接受请求的可重放回执。等待配置写锁后再次过期也不得落盘。
3. **作业身份贯通**：`workflowRunId/nodeId/iteration`、`taskId/configRevision` 从提交进入根作业、子作业、查询/事件、SDK DTO，以及活体断点。`attemptId == jobId`；恢复是同一 workflow/node/iteration 下的新 attempt，不复活历史完成任务。
4. **配置应用链**：新增 `ext.config.describe`、`ext.config.applyTaskState`，读取真实文件哈希作为 revision，按 revision 比较更新，返回 `config_applied` 和目标 epoch。当前组项目使用内容标识加重复出现次数的 ID，旧龙使用 `legacy:<index>`；ID 必须连同 revision 使用，不承诺跨任意编辑永远不变。精确单项选择必须有 revision，并且只允许启用的目标。组读取冻结文档；龙从核对过的文档生成执行副本，单项执行禁用整龙完成关机等动作。
5. **旧入口兼容与未知结果**：旧 `set_task_enabled` 使用同一配置写入入口；整组远程编辑与开关写入共用每文件互斥，追加接管票据/可选 revision 验证，不撤销旧远程编辑功能。助手收到 ext 超时、缺句柄等未知结果，不再切 v2 重新启动；旧客户端仍可使用旧入口，但不能冒充新合同客户端。
6. **远程回执与有效期**：网页对每个目标逐条等待执行端配置应用回执，校验目标/命令关联；服务器转发 ack、失败、无版本、超时均不能授权依赖启动。后续写入和启动携带刚应用的 revision/epoch。跨 JavaScript 的 epoch ticks 用字符串避免 Int64 精度损失。远程命令默认且最长有效期 2 分钟，服务端转发/离线取出及助手消费分别检查。
7. **真实编码互操作**：SDK 严格拒绝未知帧类型、版本、非对象信封。管道工厂从 Bootstrap 等价抽出独立文件，权限策略未降低：当前用户所有、拒绝 Network SID、禁止继承。测试直接调用该工厂与真实双端帧方法，只连接随机名称的自建测试管道。

### 6.2 新合同使用边界

`ext.hello` 维持 protocolVersion=2，加法声明 `execution.contract.v1`、`config.revision`、`config.applied`、`task.single.legacy`。`task.single.native=false`，没有虚报未来公版原生 GUID 执行器。

严格计划启动必须使用 **ext** 入口，提交 `executionContractVersion:1`、`idempotencyKey`、`bgiEpoch`、`expiresAtUtc`、`workflowRunId`、`nodeId`、`iteration`、`expectedConfigRevision` 及唯一的 groupName/configName。选择单项时附 taskId。不允许把同键换参数重发；新一次执行使用新键。超过 30 分钟回执保留窗口或进程 epoch 改变，不得把查询不到当成未执行后盲目重放，须由上层 RunStore 对账。旧 v2 入口不提供此严格合同，显式要求新合同却走旧入口会被拒绝。

配置更新示例顺序：describe 取得 taskId/revision → applyTaskState(expectedConfigRevision) → 收到 config_applied 的新 revision → task.start(expectedConfigRevision, taskId, epoch, identity...) → 按 jobId 查询/订阅真实终态。`queued` 不是完成。

### 6.3 当前验证

| 验证 | 本轮结果 | 能证明什么 |
|---|---|---|
| IpcChannelContractAudit | **52/52，最终完整集合连续 4 个新进程通过** | 真实协议/会话/账本/协调器/配置文件 helper/SDK 帧；自建管道 ACL 和双向传输 |
| IpcIncidentAudit | **36/36** | 原 35 项事故用例保持通过；追加真实 TaskRunner 注册与活体断点身份传递 |
| BGI ServiceTests | **71/71** | 既有服务层相关回归；不代表全仓所有测试通过 |
| 助手单元测试 | **147/147** | 原 141 项及 6 项过期/能力降级拒绝用例 |
| 服务端单元测试 | **263/263** | 含新增 4 项离线命令期限和 ticks 序列化用例 |
| 网页回执 | **10/10**，`node --check` 通过 | 执行真实生产回执/目标解析函数（含全员 UID 展开），内存传输/虚拟计时器，无浏览器/网络 |
| 产品构建 | BGI、助手、服务端测试构建均成功 | 输出放在测试子目录；BGI/助手显式 `DeployToBgiTools=false`，没有部署 |

修复前 21 项中的 7 个失败断言保留并已通过。T12 原来使用没有定义语义的 `temporary` 错误，现明确为“受理前 queue_full 可重试”；新增 T23 单独保证执行失败及未知结果不可重放，未排除失败用例。重复测试曾暴露日期 token 丢失毫秒，已修正生产代码后复跑；不是扩大等待时间掩盖问题。

隔离工程无产品 ProjectReference；所有配置写入仅操作其新建临时目录并安全回收。真实管道仅为 `Codex.IpcChannelAudit.<随机 GUID>`。未调用 SDK StartAsync、未发现或连接产品实例，未触碰运行 User、第三方 JS、前台程序或游戏。构建有既有警告，不把成功说成零警告。

### 6.4 仍不能宣称完成的部分

- **R3 原生任务模型迁移**：当前茶包执行器还是整数元组任务列表。已提供版本绑定的过渡接口，能描述/修改布尔字典配置，但原生 GUID/TaskDefinitions 单项执行明确拒绝，不能称公版一条龙还原已完成。
- **R5 完整 managed 模式**：本轮沿用现有接管租约并约束 IPC 配置写入，未创建新的全局管理模式，未声称所有原生 UI 配置编辑、热键、条件/循环编排均纳入永久管理。完整模式需要按计划迁移对应入口再验收。
- CAS 覆盖采用此 helper 的 IPC 写入；会拒绝已观察到的外部文件改动，但没有声称能与任意外部编辑器、未迁移 UI 写入器建立跨进程事务。执行副本与 revision 检查提供当前启动约束，不等同于所有脚本资源和游戏状态的全局快照。
- 本机测试验证同用户权限和网络拒绝规则；未模拟其他 Windows 用户/会话、进程崩溃后的所有部署组合，也未运行 WPF 完整交互或游戏。上述局限不影响本次已有通道缺陷已被隔离复现并修复的结论。

因此：**现有通道可靠性修复及当前配置格式桥接已验收；槲寄生完整 R0–R6、原生任务迁移和 managed 模式不冒领完成。** 未经授权不会部署或替用户提交。
