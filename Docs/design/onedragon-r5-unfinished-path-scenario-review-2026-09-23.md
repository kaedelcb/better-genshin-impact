# R5 未完成路径集中设计审查（2026-09-23；施工清单，未验收）

依据：最新交接稿页首及 B.1–B.5、R5.3 外部启动生命周期 §24.67–§24.71、总计划《R5 设计审查与拆解》§4／§7，并逐项核对当前代码。旧结项审计只作历史证据，不代表本表进度。下表的“授权者”是允许改变执行状态的唯一主体；助手的状态展示、旧发送者的证据追加、测试替身均不等于执行授权。所有“待证”保持生产 E3/E4/E5、节点改道和真实 User 门关闭。

| 场景／触发 | 唯一授权者 | 允许动作 | 回执与终态 | 重启后恢复 | 代码入口 | 当前证据与缺口 |
|---|---|---|---|---|---|---|
| E3 外部启动；可信网页／远程配置组或一条龙请求 | 当前租约 owner 的 `ArbitrationAdmissionService`；BGI 队列只执行持许可提交 | 按任务类别和配置优先级仲裁；只用带编号 ext 队列；可证实未发才拒绝重试 | `queued/adopted + taskHandle` 是受理，非执行成功；无编号、出站后超时为 Unknown，保留原发送责任 | 扫描 Submission 与外部台账，原身份对账，禁止自动重发 | `CommandExecutor.StartGroupViaAdmissionAsync`／`StartOneClickViaAdmissionAsync`、`TaskCenterHost.Admission`、`BgiExternalClient.SubmitTaskStartAsync` | `R5CompositionRootAcceptanceTests` 有测试接线态；生产组合根未注入，真实入口未验。**设计缺口 D1**：运行任务级优先级和可信类别尚无统一合同。 |
| E4 热键启动；任务热键按下 | 同上；控制热键仍是控制动作 | 任务热键必须有编号提交及取消／终态观察，准入后至多发送一次 | 编号受理后按句柄观察；无编号不得称已受理 | 以原热键请求身份及编号对账，未知停驻 | `CommandExecutor.StartHotkeyWithKeyPolicyAsync`、`ExecuteHotkeyCoreAsync` | 决策表与测试接线态覆盖部分路径；BGI 热键带编号协议、生产接线、真实按键证据未交付。**设计缺口 D2**。 |
| E5 助手承载命令、S4b 自动运行指定任务 | 当前租约 owner；S4b 是新任务而非原恢复 | 与普通任务同一优先级仲裁，确认被切任务退出后编号发送 | 受理／终态分层，冲突拒绝保留审计且自身释放槽位 | 重启后按新请求身份对账，未知不重发 | `CommandExecutor.StartSpecifiedTaskAsync`、`StartSpecifiedTaskViaAdmissionAsync` | S4b 适配边界已存在，未注入时拒绝且不回退无编号 v2；生产组合根、动作身份更新及端到端证据仍未验。**设计缺口 D1/D2**；不可沿用“仍直走 v2”的旧结论。 |
| S8b 自动恢复原任务；锄地结束 | 原恢复票据所属 owner，经 `AdmitRecoveryAsync`；不得冒充新启动 | 核对原 RunId/WorkflowId/RestoreBranch/Scope 和被切执行身份，再消费恢复许可 | BGI `restore_confirmed` 或明确拒绝；Unknown 保留票据，不新建替代作业 | 用原票据和持久化身份再取许可，仅对账不重放终止链 | `CommandExecutor.ExecuteResumeWithBusyRetryAsync`、`TaskCenterHost.Admission.AdmitRecoveryAsync` | 当前 `task.resume` 缺所需原身份／权威回执，S8b 直发；**设计缺口 D3**，协议与端到端夹具待补。 |
| 旧发送者换主后才收到带编号受理及终态 | 旧发送者仅能按 `submissionIdentity + sendSeq` 追加证据；结算只许当前 owner | 旧者不关闭 Submission、不释放槽位；新者逐字段核对后按原事务结清 | 旧轮编号／终态与新轮拒绝并存时留两轮审计，不以拒绝覆盖已受理 | 恢复扫描读取逐轮台账；完整证据才终局，重复扫描幂等 | `ExternalStartLedger.RecordLateAcceptedReceipt`、`TaskCenterHost.Admission`、`ArbitrationAdmissionService` | `HistoricalAcceptedReceiptCannotBeClearedByCurrentRoundNotAcceptedAdjudication`、`StaleOwner_AppendsLateAcceptedTerminalToOriginalRound_WithoutFinalizingOperation` 为组件／宿主证据；真实跨进程崩溃与磁盘故障未验。**设计缺口 D4**：正负结算原子排斥未闭合。 |
| task.status 过期、字段畸形、换 PID／启动 ticks | 被验证为实际管道服务端的 BGI 进程只提供事实；当前 owner 才能据新鲜事实授权 | 状态未知时零新发送、零自动恢复／收尾；不得把展示默认 false 当空闲 | 回执仅是观测，不能代替停止确认；Unknown 不终局 | 重连重验 OS 管道服务端身份及 epoch，丢弃旧快照／revision | `ExecutionScope.GetActiveSnapshot`、`IpcClient.VerifyRemoteSessionAsync`、`BgiExternalClient.TryEstablishOnceAsync`、`MainViewModel.IsTaskStatusPublishable`、`TaskCenterHost.CurrentArbitrationFacts` | BGI／助手身份快照和解析夹具已补，针对性 19/19（身份及迟到定时器负向）、助手全量 1169/2；真实重启交错与所有消费点未验。**实现错误 I2**：安全复审发现若干未知→空闲／收尾路径，本批已局部修补，仍需反例夹具与复审。 |
| 高级／同级新任务到来；包括两种最高级互遇 | 当前租约 owner 在持久事务内比较两任务身份及级别；BGI 对具体旧执行实例提供停止和退出事实 | 后来者级别 ≥ 当前才可发停止；确认同一旧实例确已退出后，原子签发新发送许可；较低者本地持久等待 | 停止请求≠已退出；退出不明维持 `PreemptConfirmPending`，新发送数 0 | 恢复时重读旧执行实例身份、stateRevision、epoch 和确认凭证，换实例／重放拒绝；低级等待记录继续重裁 | `ExecutionScope`、`ArbitrationAdmissionService.PreemptConfirmPending`、`TaskCenterHost.CurrentArbitrationFacts` | 现有布尔占用快照不足以提供来源／优先级；确认前阻断夹具已有，确认后继续及原子 CAS 未验。本地等待尚未实现。**设计缺口 D5**（高优先级，生产开门阻断）。 |
| 停止后等待退出；停止期间进程重启或旧回复迟到 | 被切 BGI 实例给出同一实例的退出证据；owner 比对后确认 | 仅在身份、revision、epoch 连续且槽位释放时进入下一发送；任何不一致待核查 | `task.stop/suspend` 成功只表示请求已收，不能作为退出终态 | 重启后旧 epoch 的退出回执不授权新 epoch 的提交 | `ExecutionScope.Stop`／`GetActiveSnapshot`、`CommandExecutor.WaitTaskSlotSettledAsync`、`ArbitrationAdmissionService` | 快照含执行实例 ID 和 stopRequested；跨端确认票据与原子占位尚无端到端证据。**设计缺口 D6**。 |
| 宿主关闭／崩溃于发送前、出站后、受理后、终态写入前 | 关闭令牌只取消本地在途动作；接管 owner 拥有恢复权 | 发送前有可靠未发送证据才可拒绝；出站后 Unknown 保留；受理后观察并补终态，不补造回执 | `BgiNotSentException` 白名单才是未发送；其余 Unknown；受理与完成分层 | 扫描未决 Submission、台账和租约，按原轮对账；无证据不自动重发 | `TaskCenterHost` shutdown CTS、`BgiWorkflowExecutionBoundary`、`ArbitrationAdmissionService` 恢复扫描 | `ExternalStart_HostShutdownDuringSend_CancelsSender_KeepsUnknownResponsibility` 等宿主夹具；真实子进程重启／磁盘故障分布和 E4/S8b 令牌链未验。**设计缺口 D7**。 |
| 两来源并发、旧新调度器并存及真实入口互撞 | 单一租约 owner 签发许可；BGI 物理执行锁是最后防线 | 每个实际提交点核验授权，任何时点最多一个在跑；旧调度器切换后退出 | 只用实际发送计数、BGI 运行实例及终态证明，无双跑不是“仅有一个候选” | 重启后先对账再放行，旧补丁不得复活形成第二调度器 | `TaskCenterHost` 组合根、`WorkflowRunner` 装饰器、BGI `JobRegistry`／协调器 | `R58DualRunContestTests` 仅合成候选组件层；E1/E2/E6a 协议贯通、E3/E4/E5 生产接线、真实入口与 R5.8 签署均未完成。**设计缺口 D8**。 |
| BGI ext 前置／收尾排队、取消、业务失败与破坏性动作 | BGI 统一槽门及该项的执行者；终态只能由实际执行结果和退出证据结算 | 排队不算运行；取得物理槽后才动资源；取消先于不可逆提交则零副作用，提交后结果不明保留 Unknown | 队列、注册表、事件按同一结果分层；不可逆 intent 不得预记业务成功 | 重启按耐久提交 key／intent 查询；查不到不等于未执行，也不自动补做 | `BgiTaskCoordinator.Submit/DispatchItemAsync`、`ExternalInterfacePrerequisitePlane.RunOpAsync/RunTerminalBodyAsync` | I9 类型登记已于 `f72f753ea` 修，定向 21/21、BGI 全量 944/14（14 失败与既有基线同身份）；I8 失败回 completed、I10 破坏性预记成功／取消提交仍待，**设计缺口 D26–D27**，见 §24.83。 |

## 发现分级与先后

### 对抗交错补表（每行均需独立证据）

| 触发条件 | 唯一授权者 | 允许动作 | 回执及终态 | 重启后恢复 | 对应代码入口 | 当前证据／状态 |
|---|---|---|---|---|---|---|
| 旧发送者在写回前暂停，租约换主后恢复 | 仅新 owner 可结算；旧者限原轮追加 | 旧者写逐轮证据，不改 Operation／Submission | 迟到编号保留为 Pending，不借新租约结案 | 新 owner 按原轮读回接纳 | `TaskCenterHost.Admission`、`ExternalStartLedger.RecordLateAcceptedReceipt` | 旧轮宿主夹具覆盖部分；跨进程换主仍待证，D4／I3 |
| 低优先级新任务到来，或未发送继任者被更高者替换 | 当前租约 owner 管本地等待；BGI 只管已建立预留的撤销 | 入口先有稳定 `source+clientActionId`，持久冻结身份／级别／到达序号／载荷；等当前完成后重裁；被替换者先确认撤销旧许可与预留 | 纯等待回 `Deferred`＋动作身份，无 taskHandle／Submission／出站；持有预留者先回撤销待确认，不凭未发终局 | 等待记录继续存在，重复请求查原身份；远端撤销 Unknown 保留交接资源；目标换代重验 | 待新增本地等待记录；`ArbitrationOrdering` 仅原同轮排序 | owner 2026-09-24 裁决；等待／重启／过期／第三者交错均待夹具，D10／§24.81 |
| 等待 A 被选中，等待记录与 Operation 绑定前后崩溃 | 当前租约 owner 的同一文件事务 | 原子写 `ActivationBound + boundRequestIdentity + Operation`；不先删等待、不再分配第二请求 ID | 绑定后仍无远端受理；Submission 签发前 `sendSeq=0`、零出站 | 重启按绑定找回唯一 Operation，不把它当无发送孤儿终局；许可已签则只查原轮 | 待新增等待段、`RecoverAfterRestart`、`SubmitAsync(Create)` | Astra/medium 1 次发现原提案缺原子绑定；当前 v5 无等待段，待红夹具，D23 |
| 等待登记已落盘但 `Deferred` 应答丢失 | 入口可信身份分配者＋当前租约 owner | 用提交前确定的 `source+clientActionId` 查询，不换身份再登记；磁盘结果不明先读回 | 区分确定未提交与提交结果 Unknown；不可向用户假报“未安排” | 重启仍展示原等待动作，重复投递返回同一状态 | 待新增等待查询入口；`ArbitrationLeaseStore` | 当前无查询协议；应答丢失重复点击可能双建，待夹具，D24 |
| v5 无发送非终局记录，或 v1–v4 旧格式迁移 | 迁移专用 owner；普通调度者无权推断空闲 | 逐态保留或隔离；v1–v3 对 `Submission/Pending`、指定 Operation 状态及两种覆盖标记拒读；其余责任字段和 v4 迁移须全表审查 | 命中旧判定为 Unsupported；未命中也不等于“无等待、无责任” | 保留原字节及残件，专用流程确认后才升候选新版 | `ArbitrationLeaseStore.ReadCore`、`HasUnresolvedResponsibilityForLegacyUpgrade`、`RecoverAfterRestart` | 两种覆盖标记红夹具后修复，租约定向 30/30、助手全量 1176/2；v4 无同一拒读条件，v5 非终局迁移反例待写，D25 部分关闭 |
| 已绑定 Operation 的等待动作在发送许可前到期或取消 | 当前租约 owner 同事务结算两侧 | 先证明零许可、零出站、零预留及旧处理者撤权，再把等待记录和绑定 Operation 同时终局；不全则停驻核查 | 确证才回 `ExpiredNotSent/CancelledBeforeSend`，不得留下孤儿 Operation | 按原绑定身份复核；一侧缺失或结果不明不重发 | 待新增等待记录／`RecoverAfterRestart` | 第二次 Astra/medium 1 次复核指出原提案缺退出转移；规则已补，红夹具及代码待做，D23 |
| 远端已受理，本地落编号前崩溃 | 当前 owner | 先用发送前已持久键远端查询，不再提交；新许可核对迟到证据阻断水位 | 找到编号才记 Accepted；查不到也不证明未受理 | 原 Submission 保留，观察器继续查询或人工核查 | `DispatchExternalStartViaHostAsync`、BGI `ext.job.status`／队列查询 | 远端按发送键查询合同缺失，D9 |
| 同级请求重复到达／真正后到者到达 | 当前 owner | 重复沿用原到达序号、不抢占自己；新身份后到才比较 | 重复回原结论；后到须经退出确认 | 到达序号持久化，重启不变 | `ArbitrationOrdering`、外部启动适配器 | 现有排序用 scheduledAt，运行中比较未接，D10 |
| 第三挑战者在停止与发送之间到达 | 当前 owner 的交接事务，BGI 原子换预留 | 仅旧继任许可尚未被发送者认领、且新请求高于或同级时撤销旧许可并替换；已认领须先证实未受理，否则只对账 | 被替换动作保留原身份／到达序号并退回本地等待；原停止票据仍须结算，不双发 | 读回 BGI 与本地预留一致后恢复唯一继任者；旧发送若 Unknown 先对账 | `PreemptConfirmPending`、`ValidateAndOccupy`、待新增本地等待 | 旧“被替换请求终局”与 owner 等待裁决冲突，已修；交接替换状态尚未冻结／测试，D10／D16 |
| 被切任务在 stop 请求同时自然完成 | BGI 目标实例提供终态，owner 确认 | 保留真实完成结果，确认槽位释放后再签新许可 | 停止回执不覆盖真实完成终态 | 旧终态与交接票据一并恢复，禁止补造“已取消” | `ExecutionScope.StopActive`／`Dispose`、BGI 协调器 | 无原子退出凭证，D11 |
| BGI 在 stop／确认／下一发送任一点重启 | 当前 owner，且只认新 epoch 事实 | 旧 epoch 确认作废；对账新进程无旧任务后重新裁决 | 旧回执只入历史，不授权新发送 | 新 owner 先证明旧进程死亡或仍活着，再验新管道 PID／启动 ticks；旧发送责任独立保留 | `IpcClient.VerifyRemoteSessionAsync`、`ExecutionScope`、`ArbitrationAdmissionService` | 状态 epoch 有反例；交接 CAS 与实机重启缺，D11 |
| 裁决 claim 后新增相反证据／多历史受理轮 | 当前 owner，按完整证据集合 | 冻结证据 revision/hash 后裁决；新证据使旧 claim 失效 | 不清其他未决轮；责任仍 Pending | 按轮重扫，不复用旧 claim | `ClaimAdjudicationAsync`、`SettleCompletionAsync` | claim 只存方向且发布失败结果未拦，I5 |
| Operation 归档、主槽满后收到旧轮编号 | 当前 owner；旧者限追加证据 | 在非主槽责任区保留迟到受理并阻断新发送，不为记录证据挤占主槽 | 记录 Accepted 与冲突 Pending，拒绝假空闲 | 归档感知查询与游标仍可定位原轮 | `RehydrateArchivedOperationForLateAcceptanceReceipt`、`MigrateAndClean` | 现重载为 `TerminalPendingTransfer`，容量边界未证，D13 |
| `PreemptConfirmPending` 期间崩溃重启 | 接管 owner | 保留 successor Run 与停止责任，先查旧实例退出，不清孤儿 | 无确认不新发；确认凭证只能用一次 | 恢复先识别交接，再做普通 Queued 清理 | `RecoverAfterRestart`、`SubmitFlowStartViaAdmissionAsync` | 恢复误清已由红夹具证实并局部修复：Active／Queued／标记保留且零发送；预建 Run 和完整确认后恢复仍待证，I7 |
| F11 在槽位预留后、ext 排队期间到达 | 当前 owner 负责撤销；BGI 执行门执行 F11 | 撤销未发 successor；已入队则按编号取消并确认，不重发 | 未确认取消留 Unknown，不能报完成 | 继承取消责任，禁止队列迟到启动 | `ProcessWinnerAsync`、`BgiExternalClient.SubmitTaskStartAsync`／`CancelTaskAsync` | 组件 F11 零发送已证；排队与实机交错待证，D7/D8 |
| 恢复观察器断线／超时 | 当前 owner 的受监督观察器 | 用持久句柄重绑订阅或轮询；不可用时目标保持关闭 | 不把观察失败当终态 | 接管先确认 observer-ready，再开放相关新提交 | `TaskCenterHost` 恢复扫描、外部台账 | 重绑字段有证，运行时替换观察器未证，D13 |
| 新仲裁请求与旧调度器／BGI 原生执行撞车 | 目标 BGI 的物理执行门＋当前租约 owner，各守各自边界 | 旧新模式仅一方可活跃，原生执行也占同一物理槽；新发送仍需 owner 许可 | 双方都不能以对方布尔空闲推断自己有权发送 | 持久模式和进程 fencing 后再启对应入口 | `TaskCenterHost` 组合根、BGI `JobRegistry`／`TaskSemaphore` | 七合成候选只证组件；真实跨入口无双跑未验，D8/D13 |

### 先修订的规则（施工提案，待集中会诊核对；不自动开放入口）

1. **运行中任务比较独立于候选队列排序。** 候选队列原有 `tier/priority/scheduledAt/id` 全序保持；新请求与“当前正在执行者”的比较另用任务级优先级。可信来源才能赋予上线锄地／一键锄地最高级；普通任务读持久化配置，缺省同级时后来者胜。较低级请求按 owner 最新裁决进入本地持久等待，当前任务结束后重裁，不可直接发或提前进入 BGI ext 队列；等待合同见 R5.3 §24.81。
2. **停止确认是独立事实。** `stopRequested`、`suspend` 回执、`running=false` 单次观察各自不能单独授权下一发送。确认凭证至少绑定被切 `executionInstanceId`、BGI `processId/startTicksUtc`、停止前后 `stateRevision`、旧实例终态及执行槽已释放；随后由当前 owner 在同一仲裁事务核对占用未换人并签发新许可。任一字段缺失或变化进入待核查，零新发送。确认凭证只能消费一次，旧轮、旧进程、换实例均不能复用。
3. **所有未知结果保留原责任。** 已出站但没有有效编号、编号迟到、发送异常后无法证明零字节写入、宿主关闭及恢复扫描中证据不全，均不能推断“未受理”。旧发送者只可追加原轮证据；当前 owner 只可依据同轮完整载荷结算。同轮正向 `Accepted` 与负向 `NotAccepted`／`RetryableRejected` 证据可以并存；互斥的是最终结算决定。新增证据须即时阻断基于旧证据集签发的许可，不能靠下次扫描才发现。
4. **恢复和自动收尾沿用原身份。** S4b 是新任务，走任务级比较、停止确认、编号提交；S8b 是原任务的恢复，必须引用原票据和执行身份，经恢复专用准入，绝不借新任务 ID 绕开。定时器触发前的完成边沿和触发时的复核必须同一进程代际、同一执行实例，且仍有原上下文；否则保留责任待核查。
5. **崩溃后先对账。** 新 owner 获取租约后先读取操作、提交轮、外部台账与 BGI 作业登记，并逐项校验引用和清理后历史凭证；未决轮不能自动重发。`MigrateAndClean` 删除主 Operation 后，审计、预观察及分页游标必须仍有独立可验证来源，不能把缺引用当空记录。

### 集中会诊后的修订（GPT-6-Astra／medium，成功 1 次；逐项以源码复核为准）

会诊纠正了本稿初版三处旧状态：S4b 已有拒绝回退的准入适配边界；`ExecutionScope` 已有执行实例 ID／revision；`MigrateAndClean` 目前会归档完整操作而非简单删除。它们仍没有生产贯通证据。以下按**已核实**与**待证假设**分开，不能把会诊静态判断直接写成测试结果。

| 分类 | 发现与当前核实 | 规则修订／受影响场景 | 下一证据 |
|---|---|---|---|
| **设计缺口 D9＋实现错误候选** | `SubmissionDispatch` 有目标 epoch／发送键，但外部启动发送链是否逐字段使用该已授权身份尚未闭合；会诊指出 `CommandExecutor` 仍可能从 `_requestContext` 取 wire 身份。 | E3/E5/S4b、迟到回执、崩溃恢复：冻结不可变授权发送包，包含请求身份、发送轮、幂等键、目标 epoch、配置指纹、owner generation；发送者只能使用包内字段。远端必须可按**发送前持久化**的键查“已受理但本地尚未记编号”，查询不等于重发。S4b 每次自动动作产生新动作身份，不借触发它的旧 CommandId。 | 沿 `DispatchViaHostAsync → CommandExecutor → ext.task.start` 逐字段取证；丢回执后远端按键查询反例。 |
| **实现错误候选 I3** | 会诊指出 `SubmitAsync`、结算、裁决等入口多处读取磁盘当前 Lease 后直接拿它作写凭据。源码 `ClaimAdjudicationAsync` 确实如此；是否有更外层身份绑定仍需逐入口核对。 | 所有改变 Operation/Submission/责任的路径：服务实例持不可变 owner token；磁盘读取只提供事实，不授予调用者权力。旧发送者仅有独立追加证据权。 | 旧 owner 在换主后暂停／恢复写入的并发反例；逐入口权限表。 |
| **实现错误 I4（局部修复）** | `ValidateAndOccupy` 原先未检查 `op.PreemptConfirmPending`；先红夹具实际返回 `Accepted`，发送安全门缺失。现最终占位锁内检查并把本轮 `InRound` 退回 `Queued`，保留确认标记。 | 优先级抢占、停止确认、无双跑：同一原子占位事务拒绝未确认交接；确认事务消费一次性证据并保留指定继任者，普通重驱动／镜像不得清除标记。 | `PreemptConfirmPending_SetBeforeFinalOccupy_CannotCreateSubmission` 红转绿，仲裁类 215/215，助手全量 1170 通过／2 跳过／0 失败。确认后放行及重启仍待证。 |
| **设计缺口 D10** | 原规则把候选排序、运行中抢占、恢复资格混用；第三个更高优先级请求在交接中到达、重复请求与“后到”混淆、配置中途变化均无明确转移。 | 独立冻结运行中比较、等待集合选择、恢复资格三个纯函数；持久化可信类别、有效优先级、策略版本和到达序号。重复请求沿用原序号。低优先级及被替换但未发送的继任者持久等待，待当前结束后重裁；已有交接票据不能被新请求绕过。 | 高/同/低＋两最高级＋重复／第三挑战者／配置变化／等待中重启和过期全格夹具。 |
| **设计缺口 D11** | `GetActiveSnapshot` 是只读进程内事实；`StopActive`／`Suspend` 现未按预期执行实例 CAS，`Dispose` 与 `TaskSemaphore` 的释放也未形成耐久退出凭证。 | 停止确认、重启、无双跑：BGI 端停止命令须指定预期实例，旧实例退出记录与槽位释放在同一可核查代际内发布；助手不得仅凭 `running=false`、取消标志或 stop 回执开新发送。 | 目标实例换人、自然完成与停止同时发生、BGI 重启夹具，逐次断言零错停／零抢发。 |
| **实现错误候选 I5** | 会诊指出裁决 claim 写失败可能返回 `null`（继续），且 claim 只存方向；源码 `ClaimAdjudicationAsync` 确有 `mutate` 结果未用于返回。 | 迟到回执、冲突裁决：claim 持 owner token、轮次、证据集 revision/hash、决定 ID；发布失败或不确定须读回确认。新证据到达使旧 claim 失效；执行终态可先存在，但责任直到审计与清冲突事务提交前仍 Pending。 | 发布故障、新证据插入、两历史轮冲突及复跑幂等反例。 |
| **实现错误 I6（回执原词局部修复）** | `ClassifyQueueSubmitEarly` 原先把任意 `Success=false` 判为确定拒绝；红夹具确认未登记词和远端回显本地错误词也误关责任。另一个红夹具证明未知成功 status 即使带编号也误记 Accepted。现仅 `queue_full`／`queue_unavailable` 关闭未受理轮，`queued`／`adopted`＋编号记受理，其余 Unknown；存证失败另待查。 | 外部启动、恢复：列出“可证实未受理／已受理／权威终态／未知”完整原词表；未登记词一律 Unknown。观察时点和错误码取自接收事实，不在映射层补造。存证失败返回原事实与 Pending，不退回先前拒绝。 | 相关类 43/43、助手全量 1174 通过／2 跳过；完整原词及响应形状、存证失败、真实线路 R-8 待证。 |
| **实现错误 I7（孤儿清理局部修复）** | `RecoverAfterRestart` 原会把 `PreemptConfirmPending` 的未发送 `Queued/InRound` 当孤儿终局化；红夹具确认误清。现该转换跳过确认待定操作，但预建 Run 保留与确认后的恢复仍待证。 | 优先级交接、崩溃恢复：统一 handoff-pending 结果并保留其 Run／票据；恢复先检查交接状态再清孤儿。等待队列不复用普通 `Queued` 孤儿规则。 | `Recovery_PreemptConfirmationPending_PreservesSuccessorWithoutSending` 红转绿；停止前／后、确认前／后四点重启仍待证。 |
| **设计缺口 D12＋实现错误 I1** | S8b 仍直接 `task.resume`；控制热键的 `SuspendHotkey`／`BgiEnabledHotkey` 是否可能恢复执行不能仅按名称放行。 | 恢复入口分暂停续行、原票据恢复、确认取消重跑、Unknown 仅对账四支；只允许**不能启动或恢复执行**的控制动作旁路。恢复回执验证身份及内容后才能消耗票据。 | 热键动作分类全表、异常／畸形恢复回执夹具、原票据端到端。 |
| **设计缺口 D13** | 观察重绑台账有持久字段，但新进程是否实际重新订阅／轮询未见端到端证据。生产接线门关着只说明新路径未开放，旧路径仍可执行。迟到受理重载归档操作可能重新占用主槽，容量边界未核实。 | 恢复、无双跑：每目标有 recovery-ready 门；接管 owner 真正启动受监督观察者，失败时该目标继续关闭。归档迟到证据保存在不占主容量的受保护记录并原子阻断执行。旧新调度器采用持久模式／代际 fencing，且每个物理执行入口共享排他域。 | 观察者超时替换、归档后满槽迟到回执、旧新调度器／BGI 原生任务并发实测。 |

**新增必测交错**：旧 owner 换主后恢复写入；远端先受理本地后存编号；同级重复与真正后到；第三挑战者插入交接；旧任务自然完成与停止竞态；停止／确认／新发送之间 BGI 重启；裁决中追加证据或多历史轮；归档满槽后迟到受理；`PreemptConfirmPending` 中重启；F11 发生于预留槽或队列等待；观察者断线；新准入与旧／原生执行碰撞。每格先写唯一授权者与应留责任，再写夹具；不能以“零新增失败”代替这些反例。

### 第二轮集中会诊后的场景合同修订（GPT-6-Astra／medium，1 次成功）

下表续接前两张七列清单，修正物理槽、发送认领和重启的交错。D14–D22 的权威规则及源码核对细目见外部启动设计 §24.78；这些格均为**设计缺口**，所列代码只证明当前边界，不证明修订已实现。

| 触发条件／缺口 | 唯一授权者 | 允许执行的动作 | 回执及终态 | 重启后的恢复行为 | 对应代码入口 | 对应测试或实机证据 |
|---|---|---|---|---|---|---|
| A 退出前预留 B，或 A 已自然退出、B 尚未排队；D14 | BGI 物理槽门授予连续排他，当前 owner 指定 A/B | 先预留再定向停止；自然退出先发生则空槽 generation CAS；B 入队到起步全程保持槽权 | 预留回执不等于执行；B 起步后才绑定新实例，A 终态独立 | 读回预留持有人及 generation；不明就关闭新入口 | `BgiTaskCoordinator.WaitSlotFreeAsync`、`ExecutionScope.Start`、`TaskSemaphore` | 现无连续槽权协议或原生入口并发实测，待红夹具及 R5.8 实机 |
| 取消 A 时 A 自然完成、C 接管，或 F11 同时清队列；D15 | BGI 同一物理门内按预期实例执行定向停止；用户全局停止另有权限 | 先比 epoch／实例／revision 再作全部副作用；AlreadyExited 保留自然完成，不停 C | `StopRequested` 与 `AlreadyExited` 分开；终态 revision 可合法增加 | 按 `stopAppliedRevision/terminalRevision/slotGeneration` 复核，不用旧 stop 回执补确认 | `DispatchTaskStop`、`DispatchTaskCancel`、`CancelByHandle`、`ExecutionScope.StopActive` | 现先 ClearQueue／锁外全局 stop，错停交错待夹具与实机 |
| 第三挑战者 C 替换 B，BGI 改票据途中断线／崩溃；D16 | 当前 owner 准备替换，BGI 原子换预留并 fencing 旧 Sender | 固定 replacementId，待两端读回一致才放 C；不一致保持 handoff hold | `ReplacementPrepared/RemoteReplacementUnknown/ReplacementCommitted`，不能把本地写成功当远端成功 | 先查远端预留版本与本地认领代次，旧 B 不能恢复出站 | `ProcessWinnerAsync`、`AcceptanceClaim`、`PreemptionGate` | 现无发送前 claim／远端替换协议，待故障注入矩阵 |
| 新 B 已拿许可但未被 BGI 受理，旧 A 迟到 Accepted；D17 | 旧者仅写见证，当前 owner 管责任；BGI 受理点执行 fencing | 同水位挂起已签未受理许可，不仅阻断未来许可；未知先对账 | A 证据 Pending，B 许可撤销须有远端确认；不能宣称 B 零受理 | 读回见证序号、B 的认领／受理事实；任一不明保持门禁 | `PersistLateAcceptanceReceiptAsync`、`ValidateAndOccupy`、BGI `Submit` | 组件旧轮夹具未覆盖已签未发交错；待跨端反例 |
| BGI 接受键 K 后登记失败、回执丢失、助手重启；D18 | BGI 先耐久登记键 K 与载荷再执行；当前 owner 只查询 | 同键同载荷返原编号，异载荷拒绝；不得换键重发 | 查询分受理／权威未受理／未找到／过期／存储故障，后三者 Unknown | 原 K 按保留期查询；记录不可靠时保持 Pending | `BgiTaskCoordinator.Submit`、`SameSubmission`、`JobRegistry`、`SubmitTaskStartAsync` | 当前先入队后登记且失败仍执行、缓存仅 32；待磁盘故障与重启夹具 |
| S8b 消费原票据后、执行实例起步前崩溃；D19 | BGI 原票据 CAS 唯一消费，原 owner 仅持稳定动作身份查询 | 同动作查询原记录；异动作只返已消费引用，暂停续行独立处理 | `ConsumedAwaitingStart` 非成功；只有身份匹配的 `restore_confirmed` 才结清助手责任 | 重用原 restoreActionIdentity，不产生第二恢复动作 | `AdmitRecoveryAsync`、`ExecuteResumeAsync`、BGI `task.resume` | 现每次新 GUID 且成功先清票据；无此窗口端到端夹具 |
| 历史轮 1 已受理终态、轮 2 仅受理，当前轮 3 拒绝；D20 | 当前 owner 逐轮结算，台账为每轮事实来源 | 只结轮 1，轮 2 保持 Pending；操作级占用由全部轮聚合 | 一个终态不能代表三个轮；审计引用各自轮次 | 逐轮扫描及保留归档责任，轮 2 未结不放行 | `ResolveHistoricalAcceptanceTerminal`、`RecoverExternalStartObservationsAsync` | 当前方法清整个 ConflictPending，待多轮红夹具及 Store 校验 |
| 旧 owner 换主后调用结算，或裁决 claim 后新证据到来；D21 | 不可变 owner token 所属实例，旧 Sender 仅原轮证据追加 | 认领绑定证据 hash／决定 ID；审计与清冲突同次提交后才 Settled | 终态可先记，责任 Pending 至审计完成 | 新证据失效旧 claim，接管者重读重裁 | `ClaimAdjudicationAsync`、`SettleCompletionAsync`、`AdjudicateConflictAsync` | 现多个入口读最新 Lease、claim 只存方向；待换主及写失败反例 |
| BGI 旧进程死亡但无业务终态，Sender 已认领发送权；D22 | OS 进程死亡证明只解除旧物理占用；当前 owner 另管发送责任 | 分未认领／已认领且可靠零发送并封禁旧者／已认领不可考／已受理四格，后两格只查询 | 死亡证明不是任务成功／取消；旧 epoch 受理只归原轮 | 证明旧进程对象死亡后核新 epoch；旧包不改目标重发 | `IpcClient.VerifyRemoteSessionAsync`、`ExecutionScope`、`SubmissionDispatch` | 只有快照身份夹具；进程死亡＋发送阶段全格未验 |

- **设计缺口 D1–D8**：先修订权威规则、上表受影响场景及 R5.3／R5.8 门禁，再按单个状态转换施工。D1、D3、D5、D6、D8 为生产开门前高优先级矛盾；D4、D7 涉及恢复安全同级阻断。任何“已有限定组件证据”均不改写为生产验收。
- **实现错误 I1–I2**：I1 限于 S8b 仍直发；S4b 适配边界已交付但生产接线与新动作身份未验。I2 的状态未知误用按明确场景逐步修；当前安全切片仅局部完成，需负向反例与完整回归。相同代码若没有授权依据，只记录缺口，不通过猜测放开门禁。
- **既有测试失败 T1**：早前助手全量 1169 通过、2 跳过、0 失败；BGI 在运行程序尚未关闭时多次测试宿主崩溃，先前 539 通过／4 失败那次的四个失败身份与旧 TRX 相同，但当时未执行范围无结论。用户关闭 BGI 后，同参数 BGI 全量完整结束：**942 通过／14 失败／956 总计**；14 个失败身份与 `r410_bgi_full_c2_20260919.trx` **逐名完全相同**，差集为空。当前助手全量 **1174 通过／2 跳过／0 失败**。BGI 的 14 个既有失败仍需如实保留，不能称全绿，也不替代实机验收。
- **会诊工具失败 C1**：既往 §24.71 Astra/medium 一次执行器 exit code 1，未取得会诊结论，不记代码缺陷。本轮早期 Astra/medium 安全复审两次各 1 次成功（第一次 3 项、第二次 4 项重要发现）；I4 小切片复会诊 Astra/medium 尝试 1 次返回 exit code 1，**未取得该次会诊结论**，不记代码缺陷且不属于约定重试类别。随后独立的 §24.76 集中设计会诊 GPT-6-Astra／medium **1 次成功**，得到 D14–D22 九项规则修订；其结论已按源码核对，不代替代码验收。

## 执行门禁

本表是开工前设计清单，不是 R5 完成证明。先消除 D1/D3/D5/D6/D8 的合同矛盾，再实现对应状态转换与反例；各改动核对针对性测试和全量回归，并以失败用例身份比对基线。只有 §23.4 并集与总计划 §7 的 R5.1–R5.8 均有对应证据、R5.8 实际无双跑验收签署，才能报告 R5 完成。生产入口和真实 User 门持续关闭。
