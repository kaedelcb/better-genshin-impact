# 一条龙 R2 兼容矩阵与旧写入入口清单（2026-09-18）

> 阶段：R2（兼容桥与执行事实）。本文件是 R2.5 交付物：客户端版本 × BGI 能力 × 配置形状 × 操作的兼容矩阵，
> 以及旧写入入口兼容权限清单。上游依据：[IPC 通道合同审计](mistletoe-ipc-channel-contract-audit-2026-09-17.md) §6、
> [审计及执行方案](onedragon-public-compatibility-audit-2026-09-17.md) §8、总计划 §3.4。
> 验证证据（2026-09-18 ASTRA 会诊修复后复跑）：IpcChannelContractAudit 68/68（含 T60–T71）、IpcIncidentAudit 36/36、
> 助手 150/150、服务端 276/276、网页回执 10/10。
> 测试范围声明：执行段由惰性桩承载（不碰磁盘/游戏/真实 IPC 管道）；T66 覆盖
> describe→apply→守卫启动准备→真实路由启动→协调器终态→job.status 的离线全链。
> resume 链路复用既有生产代码（票据重投去重/stale_ticket/stale_context/revision 校验/30s 受理超时），
> 本阶段核实无需补代码、未重复离线执行，属声明的已知局限。
> 整组写入的「写前字节复检」位于 ApplyRemoteGroup 内部（依赖 WPF Dispatcher），无单元夹具，由代码审查与集成路径保证。
> 并发边界声明（会诊第三轮收窄）：合同锁 + 写前复检约束的是「走合同的合作写方」；对不合作的外部写入者
> （直接改文件的编辑器），「复检通过→原子替换」之间仍存在覆盖窗口（无时间量级承诺），当前锁与复检方案
> 不能消除——如实声明，不声称原子 CAS。声明合同的写入其回执修订绑定本次提交字节（先算修订、同一份字节
> 原子落盘），不属于写后的外部改动。
> 数值保留（会诊第三轮加固）：引用服务以 decimal 解析浮点，写回前用 System.Text.Json 逐一校验数值字面量的
> decimal 往返无损；不能无损保留的（科学计数法 1e2、超 decimal 精度、1e-29 下溢等）整文件隔离跳过——
> 不存在静默改变数值的路径（T70 固化）。
> 内存刷新纪律（会诊第二轮 #1、第三轮补强）：ReloadScriptGroups 重载期间抑制整组落盘，抑制标志为静态
> （覆盖一条龙页内嵌「配置组管理」对话框共享集合的第二实例），远程写入后的刷新为纯内存操作，不重写任何组文件。
> 死锁风险收窄（会诊第四轮）：合同方法内部续体已全部 ConfigureAwait(false)；HandleConfigApplyGroup 持锁等
> Dispatcher 的路径锁 ScriptGroup 文件、引用服务锁 OneDragon 文件，锁键=完整路径不相交，不构成互等。
> 但传入 action 委托的内部行为不受 ConfigureAwait 约束，不作「全部持锁方永不依赖 UI」的总括声明。
> 另：ApplyEnabledAsync 的临时文件清理已嵌套 try/finally，清理失败必定释放锁（会诊第四轮 #2）。

## 1. 兼容矩阵（客户端版本 × BGI 能力 × 配置形状 × 操作）

约定：DAP 规则（缺省即不支持）；`configWriteContract:1` / `executionContractVersion:1` 由客户端显式声明；
BGI 能力以 `ext.hello` capabilities 为准（`config.revision`、`config.applied`、`task.single.legacy`、`execution.contract.v1`）。

| 操作 | 新客户端 + 新 BGI | 新客户端 + 旧 BGI | 旧客户端 + 新 BGI | tuple 形状 | native 形状（R3 后） |
|---|---|---|---|---|---|
| `ext.config.describe` | 返回 taskId（内容标识/`legacy:<index>`）+ configRevision + bgiEpoch | 操作不存在，能力门控拒绝（`HasCapability` false） | 旧客户端不调用 | 完整支持 | 可读可投影（bool 项以 ID 即名；taskDefinitions/taskOrder 的语义投影属 R3）；native 项 `singleExecutionSupported=false` |
| `ext.config.applyTaskState` | 强制 taskId+expectedConfigRevision；写后返回新 revision | 能力门控拒绝，不降级重发 | 旧客户端不调用 | 完整支持 | 开关可写；单项执行明确拒绝 `native_single_execution_not_supported` |
| `config.apply_group`（整组写入） | 声明合同 → 强制 revision；写后返回新 configRevision | 旧 BGI 不识别合同字段，按既有「携带才校验」语义处理（新助手此时不会携带 → 不校验）；baseMd5 乐观提示仍有效 | 未声明合同 → 兼容路径（W2），revision 可选、携带仍校验 | 完整支持 | 组文件与一条龙形状无关，行为一致 |
| `ext.task.start`（严格合同） | executionContractVersion=1 要求 epoch+幂等键+有效期+节点身份+revision | 能力门控拒绝；不允许降级 v2 冒充（T52） | 旧 v2 task.start 行为冻结，不提供严格合同 | 单项选择走 `legacy:<index>` | 原生 GUID 单项执行属 R3，当前明确拒绝 |
| `ext.task.resume` | 受理/入队/终态分级回执；票据重投去重；stale_context/stale_ticket 拒绝 | 能力门控拒绝 | 旧客户端无 resume 合同 | 按悬挂现场的水位恢复 | R3 切换后按稳定 ID 水位 |
| `ext.job.status/list` | jobId/kind/parent/identity/revision 全量投影 | 能力门控拒绝 | 旧客户端不调用 | 与形状无关 | 与形状无关 |

跨版本要点：**不会出现"新 BGI 拒绝旧助手"的断裂**——强制校验只由客户端显式声明触发；新助手仅在 pull 响应
携带 configRevision（新 BGI）时才声明合同并回传 revision，旧 BGI 无此字段时整条链自动保持旧语义。

### 1.1 远程整组编辑三角色混布（发起助手 × 执行端助手 × 执行端 BGI）

远程编辑链有三个独立版本角色。「编辑可用」指功能可完成；「revision 保护有效」指编辑期间文件被第三方改动时
写入会被拒绝（configuration_changed）而非静默覆盖。

| 发起助手 | 执行端助手 | 执行端 BGI | 编辑可用 | revision 保护 | 说明 |
|---|---|---|---|---|---|
| 新 | 新 | 新 | ✅ | ✅ 强制（并发边界见页首声明） | 声明合同 + 磁盘快照构造 + 写前复检 + 修订绑定提交字节 + 回执贯通 |
| 新 | 新 | 旧 | ✅ | ❌（不声明） | pull 无 configRevision → 发起端不声明合同，整链保持旧语义（baseMd5 乐观提示仍在） |
| 新 | 旧 | 新 | ✅ | ❌ 丢失（发起端可见提示） | 旧执行端助手不透传 expectedConfigRevision/configWriteContract → BGI 走兼容路径；push_result 缺写后修订 → 发起端明确提示「回执缺少写后修订号，请刷新核实」；若旧执行端连 pull 回执字段也裁剪，则整链自动按旧语义 |
| 新 | 旧 | 旧 | ✅ | ❌ | 全链旧语义 |
| 旧 | 任意 | 任意 | ✅ | ❌ | 旧发起端不携带 revision，BGI 兼容路径（W2），携带时仍强制校验 |

要点：revision 保护生效的前提是「发起端拿到 configRevision 且执行端助手透传合同字段」；任一环节为旧版即退回
兼容路径。**编辑可用性限定于已支持远程编辑协议的版本组合**（更旧的无协议版本不在此列）；revision 保护的并发边界见页首声明，不承诺对不合作外部写入者的原子性。

## 2. 旧写入入口兼容权限清单（R2.2 交付物）

| 编号 | 写入入口 | 兼容权限 | 退出计划 |
|---|---|---|---|
| W1 | v2 `config.set_task_enabled` | 已汇入配置面统一写入；revision 可选、携带强制校验；不得冒充新合同客户端 | 长期保留（旧客户端兼容） |
| W2 | v2/ext `config.apply_group` 未声明合同 | revision 可选（携带仍强制校验）；baseMd5 乐观并发提示保留 | 长期保留（旧助手远程编辑） |
| W3 | 本机 UI 保存（页面编辑 / WriteConfig） | 本机用户操作，不经 IPC 合同；**不持有合同文件锁**（属锁外写入者）；IPC 整组写入与引用服务以写前字节复检发现锁外改动并放弃（整组写入的复检对所有携带 expectedConfigRevision 的请求执行，含未声明合同的旧请求；完全不携带 revision 的旧请求无此保护）；复检→替换间对不合作写方仍有覆盖窗口（页首声明），不做原子 CAS 承诺 | 长期保留 |
| W4 | 配置组改名/删除的引用更新 | **R2 起**走 `OneDragonConfigReferenceService`（纯 DOM、不构造 VM、不触发 F02）；不再允许 `new OneDragonFlowViewModel()` 反射写回 | 已切换（R2） |
| W5 | 旧升级器 `AdaptVersions`/`RestoreOldVersions`/`ReverseAdaptTaskEnabledList` | 仍存在但冻结：不得新增调用方 | **R3 退出**（含 VM 构造触发点） |

## 3. 故障注入与离线断言覆盖（R2.5 验收映射）

| 场景 | 承载用例 |
|---|---|
| 实例替换（进程纪元变化） | T18（stale_epoch 拒绝入队）；T28（epoch ticks 字符串无损/畸形拒绝）；IpcIncident「expired lease cancels owner」 |
| revision 冲突 | T34（过期修订不得覆盖更新写入）；T35（并发 CAS 唯一胜者）；T51（等锁后过期复检）；T66（离线链 stale 拒绝） |
| 旧回执迟到 | T50（过期重投放回真实回执而非谎报未执行）；T25（账本满不挤掉在保留期回执） |
| 幂等/归并残余 | T65（显式不同 key 同名不归并、kind 区分、无 key 同名归并保留语义）；IpcIncident「parent and repeated same-name children stay distinct」「explicit keys do not collapse by name」 |
| 整组写入合同 | T60（声明强制/旧入口兼容）；组名路径穿越守卫与写前复检为生产代码顺序调用（见页首范围声明） |
| 引用服务 | T61（rename tuple/custom/domain + 未变更文件不写）、T62（delete）、T63（native 形状不合成 tuple + BOM 保留）、T64（坏文件隔离）、T67（旧版名键 v0 rename/delete + 键冲突跳过且报告 + 原位替换保序）、T68（混合形状双向隔离、字节不动）、T69（日期字符串/可无损浮点字面/未知字段保留）、T70（科学计数法/下溢/超精度数值字面量文件隔离跳过、字节不动、报告含文件名） |
| 改名半成品收敛 | T71（移动助手决策矩阵：正常移动 / 身份校验半成品跳过 / 双存冲突拒绝（绝不删文件）/ 冒名目标拒绝 / 歧义双存拒绝 / 源缺失异常可见 / 大小写别名同文件；字节级断言）。VM 侧：改名流程开头先 ReloadScriptGroups 并按名重解析 item（绑定最新磁盘副本，防止旧内存对象覆盖人工保留的文件）；ReadScriptGroup 本身不去重——双存时重复内存对象仍在，磁盘冲突解除并再次重载后集合才收敛。VM 调用链（重载+重解析+内容补保存）依赖 WPF，未离线覆盖，由代码审查保证） |
| 离线（无服务器） | T66（describe→apply→守卫启动准备→真实路由启动→协调器终态→job.status 全链本机，含「准备选定的配置与起点到达执行边界」断言；执行段为惰性桩，不证明真实任务运行）；全部合同用例均为本机内存/临时目录，无网络依赖 |
| 假成功防护（F04） | IpcIncident「thrown failure reaches result and registry」「cancel exception without global flag is not success」「caught project error survives later successful child」「missing screenshot initialization is failure not success」 |
| F09 R2 分界 | IpcIncident「concurrent roots admit exactly one owner」「expired lease cancels owner without admitting overlap」；全入口统一仲裁归 R5 |

## 4. 监控端边界

- 监控端（ObserverMode）仍不作为配置落盘目标（`CanHandleLocalConfigRequest` 不变）；远程编辑能力本身不删除不覆盖。
- 网页/服务端仅透传 taskId/revision/epoch，不解释内容；命令有效期默认且最长 2 分钟（既有纪律）。