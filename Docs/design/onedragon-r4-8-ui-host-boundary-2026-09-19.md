# 槲寄生 R4.8 任务中心最小 UI / 生产执行边界 / 进程内宿主 设计稿（2026-09-19）

上游：槲寄生调度器总计划 §3.1a/§3.3 R4 行/§3.4；onedragon-r4-implementation-breakdown-2026-09-19.md R4.8 行（验收口径）；
onedragon-r4-6-prerequisite-terminal-contract-2026-09-19.md（前置/收尾作业化模式沿用）。
本文是 R4.8 唯一权威设计稿；ASTRA 会诊记录与逐条处置见 §5。

## 1. 范围

- 生产执行边界 `BgiWorkflowExecutionBoundary`：IWorkflowExecutionBoundary 的 ext 实现（ext.task.start 严格合同 + ext.job.status/list 对账 + ext.task.cancel）。
- 进程内宿主 `TaskCenterHost`：Store/Runner 组装、RecoverOnStart 启动屏障、同流程运行互斥、驱动任务生命周期、结构化动作结果。
- 任务中心最小 UI：槲寄生 Tab 1 = BGI 任务状态卡（保留）+ 流程列表卡 + 流程编辑卡 + 运行状态卡（用户已确认草图 task-center-mockup）。
- 引擎/边界合同补强（ASTRA 一轮阻断前置）：提交三态、结构化终态、取消与观察分离、提交身份冻结、WaitAsync 延时泄漏修复、WorkflowStore 并发/判型加固。

明确不做：拖拽编辑器、高级自由跳转（§3.5）、单项节点追加、启动中心交接接线（R4.9）、监控端远程编辑（D14 挂账）、
task.single.native 开 true（R4.10 集成验收后评估）、候选激活/另存 active（R5 事务迁移）。

## 2. 验收口径（分解文档 R4.8 行 + 本轮细化）

- 构建 0 error（-p:DeployToBgiTools=false）；助手测试基线 232/232 之上新增夹具全绿。
- 管理-预览-启动-停止-查看-运行动作链路可操作（夹具级 + 独立配置根手动链路）；监控端无该 Tab（按钮 + 内容区双重隐藏）。
- 不确定事实零降级：提交/终态不可考一律 Unknown 停驻（游标不推进、不触发收尾、禁止自动重跑），不得记 Rejected/Failed。
- 候选 candidate-ready 只读预览：禁启动、禁编辑、无另存 active 路径；隔离文件展示原因。

## 3. 批次划分

- Batch A 引擎与边界合同补强（§4.1–4.5 + WaitAsync 修复 + Store 加固）。
- Batch B 生产边界 BgiWorkflowExecutionBoundary + Planner 账号预检（I2）。
- Batch C 进程内宿主 TaskCenterHost（§4.6）。
- Batch D UI：TaskCenterPanelViewModel + XAML + MainViewModel/MistletoeViewModel 接线（§4.7–4.9）。
- Batch E 夹具全集（含 ASTRA I8 异步窗口场景表）+ 阶段 ASTRA 终审。

## 4. 关键合同决策

### 4.1 提交三态（一轮 B1）

BoundarySubmitResult 增加第三态 Unknown（受理与否不可考：发送超时/传输异常且对账未命中/回执畸形/无法解释的 already_executed）。
引擎 SubmitAndAwaitAsync 对 Unknown 走既有 "unknown" 结果路径：先置 run.State=Unknown 再 CommitOutcome（单次原子落盘、游标不推进、
rawTerminal=null、ObservedTerminal 保持空=在飞事实保留），不触发收尾、禁止自动重跑。
Rejected 仅限可证实的未受理：本地校验失败（缺修订/缺意图/资源映射失败/账号合同违例）或对端副作用前协议拒绝。
「不猜成功」同样「不猜失败/拒绝」（D6/D11 事实合同）。

### 4.2 结构化终态（一轮 B1/I3）

IWorkflowExecutionBoundary.AwaitTerminalAsync 返回 Task<BoundaryTerminalResult>：
- Terminal：远端终态原词（succeeded/failed/cancelled/skipped/rejected；null=未观察到）；
- Uncertain：true=本地查询不可考（预算耗尽/not_found/通道瞬态耗尽），引擎走 Unknown 停驻，不得经 MapTerminal 落 failed；
- Reason/ErrorCode：受控原因上 UI。
节点作业解释器（边界侧，与前置/收尾分开）：WasCancelled→cancelled；skipped=合法终态原词（D15，消费 PollUntilTerminalAsync 的 Job 原值重解释，
不吃其 Outcome=unknown 的协议违例判定）；rejected→rejected（MapTerminal 恒等映射已有）。
FakeBoundary 夹具同步适配；恢复回流（B3-③）ObservedTerminal 恒等映射不变。

### 4.3 取消与观察分离（一轮 B3）

IWorkflowExecutionBoundary 新增 RequestCancelAsync(string jobId, CancellationToken ct)，默认 no-op（测试假实现免接线）。
- AwaitTerminalAsync 纯观察：取消只终止等待，绝不再发远端取消（修复"确认超时再发一次取消+15s 预算被 5s 取消拖延"）。
- SkipCurrent：ConfirmSkipAsync 先 RequestCancelAsync 一次（独立有界令牌 ≤5s，忽略结果）→ 再 SkipConfirmTimeout 纯观察确认；
  cancelled→skippedUser；超时→cancelUnconfirmed（Unknown 停驻）。
- Stop：RunCts 取消后，对在飞提交（Accepted 未观察终态）用独立短令牌 best-effort RequestCancelAsync（≤5s）；
  run.State=Cancelled（用户意图）+ Note 标注"远端取消未确认，在飞事实保留，需对账"；ObservedTerminal 保持空（事实不猜）。
- 发送窗口取消（SubmitAsync 内 OCE）：生产边界先以 cleanup 令牌（≤5s）按幂等键对账（§4.4），命中绑定 jobId 落盘并即发取消，
  未命中保留 SendAttempted 事实，随后重抛 OCE（取消纪律不吞）。

### 4.4 提交身份冻结与对账（一轮 B2）

WorkflowSubmission 增加冻结字段（加法，JsonExtensionData 兼容旧记录）：
- Epoch（"pid:ticks"，发送时冻结；跨纪元事实不沿用）；
- ExpiresAtUtc（+10min，冻结不刷新，I4 同构）；
- Fingerprint（SHA256(完整载荷) 截断 24 hex）；
- SendAttempted（发送前持久化=true：此后缺 jobId ≠ 未发送）。
生产边界 SubmitAsync 顺序：校验意图身份（Key/NodeId/Occurrence/LoopIteration/Attempt 与请求一致，不符→Rejected 本地违例）→
冻结上述字段+组载荷 → Intent=Submitted + _runs.Update 落盘（即将发送事实）→ 发送一次 →
受理回执：accepted+taskHandle→Accepted；传输异常/畸形/查询窗口 → 按幂等键 QueryJobListAsync 对账：
唯一命中且身份四元（IdempotencyKey+WorkflowRunId+NodeId+Iteration）一致且 Epoch 未变 → 绑定 jobId 转 Accepted（不重发）；
否则 Unknown（查不到 ≠ 未执行证明）。alreadyExecuted → Unknown（引擎正常不可达，不猜）。

### 4.5 账号合同（一轮 I2，Planner 预检 + 边界共用）

- 每节点至多一个 prerequisite.account（重复→预检响亮拒绝）。
- account 存在但 uid 空白/类型错误→预检/边界拒绝（不得当"无账号策略"省略 expectedUid）。
- 资源提交 expectedUid = 该 account 完整 uid（与前置执行同一原值；显示掩码不进协议）。
- account 与 redeemCode 显式 uid 冲突→预检响亮拒绝。
- BGI 侧 ExpectedUidGate 在取得执行权后复验（R4.6 E2-9 已落地）；本项是 R4.10 真实集成验收点，文档不越权声称关闭切号竞态。

### 4.6 宿主 TaskCenterHost（一轮 B4/B5/I5/S2）

- 构造：flowsDir/runsDir/catalogCache（生产默认 %APPDATA%/NexusBGI/*，测试临时目录）+ Func<BgiExternalClient?> + 日志委托；
  宿主无自有持久化状态（I6）；内存任务表/锁不算持久化。
- 启动屏障：EnsureRecovered() 在任何 Start/Resume 前完成 RecoverOnStart 一次（Interrupted/Unknown 标记+日志；绝不自动恢复）；
  重复调用幂等；本进程驱动的运行不参与扫描。
- 互斥（同一 lock 临界区完成"检查→预留→注册"）：同 workflowId 存在 Planned/Running/Waiting/Completing/Paused 运行→响亮拒绝；
  存在 Unknown 运行→拒绝（需先对账）；Interrupted 允许另开新运行（旧记录不动，可显式 Resume）；
  Start 与 Resume 共用临界区（防并发 Start/Start、Start/Resume、Resume/Resume）。
  本期产品限制=同 workflowId 唯一活动运行（响亮拒绝，不按资源名去重、不静默吞）；与 R4.9 入口原子提交登记互补不替代。
- 驱动：后台 Task + per-run CTS 登记在册；非 OCE 异常收敛——当前提交在飞→Unknown，否则→Interrupted（等价崩溃语义）+ Note+日志；
  绝不只记日志留 Running 僵尸。
- 退出：ShutdownAsync 取消全部 CTS + 限时收敛（≤10s）；未收敛=进程退出语义，下次启动 RecoverOnStart 标 Interrupted/Unknown（在飞）。
- client/epoch 绑定：每次 Start/Resume 以当时 client 组装 Runner（边界/前置/收尾同一实例）；在飞作业的观察/取消用同一边界实例，
  不无声切新 client；client 缺失（BGI 离线）→ Start 响亮拒绝（离线优先=本地可管理流程，执行需本机 IPC 能力，与 SignalR/在线模式无关，S2）。
- 动作结构化结果：HostActionResult(Registered/Effective/Unavailable+原因)；Paused 运行的 Stop=宿主直接终态化
  （校验无在飞提交→Cancelled；有在飞→拒绝并提示对账）；UI 文案标注"暂停请求，节点边界生效"。

### 4.7 最小编辑器（一轮 I4/I6）

- 保留式修改：编辑 LoadSnapshot 文档的深拷贝（System.Text.Json 序列化往返深拷），只改触碰字段；
  未知字段/未支持类型/多触发器/多策略/loop 参数/activation 元数据原样保留（ExtensionData 自然往返）。
- missPolicy=nextDay 仅作新建触发器默认值，保存不强行覆盖既有值；scheduled 循环编辑仅暴露 mode（既有参数保留）。
- 调序保 NodeId；新增节点 NodeId="n-"+Guid8（与 R1 产物同形）+ 冲突检查。
- 追加节点来源=ResourceCatalogService.Current 快照（OneDragonConfig/ConfigGroup；SingleTask 禁追加）；
  Ref.Config=DisplayName（BGI 启动合同寻址名，与 R1 产物一致）、Ref.Revision=ConfigRevision、ConfigKey 不充当资源 ID（D14）。
- uid/bindingCode 三分：原值（内存模型）/显示值（掩码：长度>5 前3+***+后2，≤5 全遮盖）/已修改标记；
  未触碰保存原值，掩码绝不落盘；日志/预览/错误结构化脱敏（不走通用数字正则）。
- 备份裁决：_backup 含原值（与流程文件同保护级，本机权威存储边界内；遮值会破坏可恢复性）——展示层全程脱敏。本裁决入档。

### 4.8 流程列表与候选（一轮 I1）

- Ready+active：启动/预览/编辑；candidate-ready：预览（只读展开），禁启动禁编辑（D13；激活归 R5，不提供另存 active）；
- Quarantined：展示隔离原因，禁一切写路径；UnsupportedKinds 展示但按 Planner 预检阻执行。
- 刷新：2s DispatcherTimer 只刷展示快照（列表/运行卡/历史），不重建编辑草稿、不承担调度/恢复；防 Tick 重叠；页面释放停表退订（S1）。
- 历史运行：最近 20 条只读（活动运行除外，按 UpdatedAt 倒序）；NodeOutcomes 可视呈现限长。

### 4.9 XAML（Tab 1）

保留 BGI 任务状态卡；占位卡替换为流程列表卡/流程编辑卡/运行状态卡（QuestCard/GoldText/Dim 既有资源，暗色金调与草图一致）。
内容区 Grid 加 IsExecutorMode→Collapsed（Tab 按钮既有隐藏之外的第二道保险，防 SelectedTabIndex 残留）。

## 5. ASTRA 会诊记录

### 一轮（方案会诊，2026-09-19，8 文件 allowlist）——5 阻断/8 重要/2 建议，处置：

| # | 发现 | 处置 |
|---|---|---|
| B1 | 不可考降级 Rejected/Failed 绕过 Unknown 停驻 | 采纳：提交三态 + 结构化终态（§4.1/4.2），Rejected 仅限可证实未受理 |
| B2 | 提交身份未冻结持久化，job.list 对账缺证据校验 | 采纳：§4.4 冻结四字段 + 唯一命中身份四元+Epoch 校验，否则 Unknown；查不到≠未执行 |
| B3 | 任意 Await 取消都发远端取消不自洽；发送窗口/Stop 未覆盖 | 采纳：§4.3 取消与观察分离 + RequestCancelAsync；Stop/发送窗口有界处置 |
| B4 | 宿主互斥漏状态不防并发 | 采纳：§4.6 同临界区预留 + 全活动态集合 + Unknown 禁跑；与 R4.9 互补不替代 |
| B5 | 宿主生命周期只落日志 | 采纳：§4.6 启动屏障/驱动在册/异常收敛 Unknown|Interrupted/ShutdownAsync/epoch 绑定 |
| I1 | 候选另存 active 绕过 R5 | 采纳：候选只读预览，无派生活性路径（§4.8） |
| I2 | expectedUid 取首个 account 语义不完整 | 采纳：§4.5 最小合同（唯一 account/空白拒绝/冲突拒绝/同原值）；ExpectedUidGate 复验归 R4.10 验收 |
| I3 | skipped 会被 PollUntilTerminal Outcome 吃掉 | 采纳：消费 Job 原值重解释（§4.2） |
| I4 | 表单重建丢扩展字段 | 采纳：保留式深拷贝编辑（§4.7） |
| I5 | 动作转发无闭环；Paused 停止失效；WaitAsync 延时泄漏 | 采纳：结构化动作结果 + Paused 终态化 + WaitAsync 链接 CTS 修复（Batch A） |
| I6 | 掩码/备份/Sanitize 冲突 | 采纳：§4.7 三分模型 + 备份含原值裁决入档 |
| I7 | Store 并发/判型缺口被 2s 刷新放大 | 采纳：Save 同进程互斥 gate + InspectBytes 容错 + PathFor 身份校验（Batch A） |
| I8 | 纯函数夹具不够 | 采纳：Batch E 组件级场景表（受理丢回执/发送期取消/竞态/epoch 替换/并发互斥/Paused 停止/编辑保留） |
| S1 | 定时器纪律 | 采纳（§4.8） |
| S2 | 能力 vs 离线分开展示 | 采纳（§4.6 拒绝文案 + §4.8 展示） |

（后续轮次逐次追加）

### 二轮（阶段终审：Batch D/E 执行效果，2026-09-19，11 文件 allowlist）——暂不通过 → 5 阻断/6 重要/3 建议全处置后回归 272/272：

| # | 发现 | 处置 |
|---|---|---|
| 阻断1 | 保留式编辑实为全表单回写：自定义 loop mode 被清、未知收尾 action 被清、既有触发器被补 missPolicy、缺 time 触发器被删、days 无条件重建、名称无条件 Trim；失败重试污染「原值」语义 | 采纳：字段级基线 + 不可映射值新增「保留自定义（不修改）」索引项（Loop=3/Terminal=5）；保存改为 BuildSubmissionCopy（基线+编辑集生成提交副本，失败零污染）；uid/bindingCode/days 锚定构造基线；名称仅变化才 Trim 回写。夹具：复杂文档零修改保存逐字段保留 + uid 失败重试回基线 |
| 阻断2 | IsCandidate 耦合 UnsupportedKinds==0 → 候选+未知类型显示为可编辑 active；列表过期窗口（条目 active/盘上已候选）；宿主无独立禁写边界 | 采纳：候选身份只依据 activation；BeginEdit 复核快照 activation（候选/缺 workflowId 拒绝编辑转预览）；宿主 SaveFlow 候选禁写 + StartWorkflowAsync 候选禁启动（双保险不依赖 UI 新鲜度）；StartFlowCommand 补 CanStart 守卫。夹具：候选+未知类型三处拒绝、过期列表复核 |
| 阻断3 | 三处掩码 Run.Text 默认 TwoWay 绑只读属性（运行时绑定错误） | 采纳：显式 Mode=OneWay ×3 |
| 阻断4 | Start/Resume 命令 Task.Run+GetResult 与 AddLog Dispatcher.Invoke 互等死锁；退出 Task.Run.Wait 有界但留清理竞态 | 采纳：命令真异步（async/await + 防重入 + 异常兜底）；Shutdown() 置 _disposing，AddLog 退出期跳 UI 派发仅落文件；宿主屏障 await ConfigureAwait(false) |
| 阻断5 | _recovered 扫描前置位（并发越窗/失败不重试）；Resume 缺同流程活动/Unknown 护栏 | 采纳：屏障任务化（共同 await 同一扫描，失败重置重试）；Resume 复用 Start 互斥判定（排除自身）。夹具：并发 Start 仅一运行、Unknown+Interrupted 恢复拒绝 |
| 重要1 | DetailNote/NoteText 默认 Collapsed 且无显示路径（隔离原因/运行附注永久不可见） | 采纳：默认可见，仅 null/空隐藏 |
| 重要2 | 宿主经面板提前创建 flows 目录；定时器无恢复路径 | 采纳：WorkflowStore/RunStore 构造零副作用（目录首次写入才建）；面板 StartAutoRefresh + 页面 Loaded 恢复 |
| 重要3 | 生产目录无刷新入口；RefreshCatalog 每 2s 重建下拉；已选资源消失静默改选第一项 | 采纳：面板构造/刷新命令 KickCatalogRefresh（异步防重入）；快照引用未变不重建；选择消失置未选+提示 |
| 重要4 | 错误/状态出口未脱敏；失败重试「原值」漂移 | 采纳：SetStatus 展示前按草稿敏感值集掩码替换；基线锚定（见阻断1） |
| 重要5 | Start/Resume 异常无兜底；CreateRunner 失败留预留槽；切换编辑静默丢冲突草稿 | 采纳：命令全 try/catch 结构化反馈；组装失败释放预留；GuardNoOpenDraft 拒切换/新建 |
| 重要6 | 进度链混用修订/轮次（旧轮 outcome 冒充本轮）；历史新记录追加尾部破坏倒序 | 采纳：outcome 按当前轮次匹配+游标节点「运行中」优先+修订不一致显式标注；SyncCollection 按源序 Move 重排。夹具：历史刷新重排 |
| 建议1 | 初始序号全 0；下拉索引未拒越界 | 采纳：构造 Renumber；setter 守卫 [-1 拒绝/上限] |
| 建议2 | BeginEdit knownRevision 参数无效；缺 workflowId 文件保存致身份错位 | 采纳：参数移除；缺身份拒绝编辑（仅预览） |
| 建议3 | ActivationStatus 三处构造点正确；XAML 命令路径/索引语义对齐 | 确认无需改动 |

二轮未覆盖（转 R4.10 集成验收）：STA/Dispatcher 下真实 XAML 模板加载冒烟、独立配置根手动链路、带真实 AddLog 回调的启动恢复链路、恢复扫描失败重试夹具（RecoverOnStart 逐文件容错难以无接缝制造抛错，屏障失败重置逻辑已落码）。