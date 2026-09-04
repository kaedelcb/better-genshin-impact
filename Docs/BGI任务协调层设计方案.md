# BGI 侧任务协调层（BgiTaskCoordinator）设计方案

> v1 · 2026-09-04 · 切片 7 设计 spec
> 上游文档：《联机助手重构总控计划.md》§4 切片 7、《联机助手通信架构标准化方案.md》模块一
> 状态：**待用户过目 → 过目后进入实施**

---

## 0. 背景：病例与根因

### 0.1 实机病例（2026-09-04 22:39 实机日志）

```
22:39:19.585 ERR TaskRunner  任务启动失败：当前存在正在运行中的独立任务
22:39:19.586 INF ScriptService 配置组 "66" 执行结束
22:39:19.601 WRN [IPC task.start] 拒绝启动：当前存在正在运行中的独立任务
22:39:20.614 WRN 同上（第 2 次）
22:39:21.623 WRN 同上（第 3 次）
22:39:22.638 WRN 同上（第 4 次）
```

上线锄地触发后，助手的 task.start 撞上上一个任务的**退场清理窗口**，被反复拒绝，靠 1s×6 无损重试硬等。

### 0.2 根因（已逐行钉死）

1. **锁的持有边界**：所有任务启动路径最终都收敛于 `TaskRunner.RunCurrentAsync`（`TaskRunner.cs:49`）持有全局单计数信号量 `TaskSemaphore`，释放在 `finally` 末尾（`TaskRunner.cs:116`）。
2. **"执行结束"日志早于槽位释放**：`配置组 "X" 执行结束` 打在任务体内部（`ScriptService.cs:464`），之后还要走 `finally → End()`（释放残留按键 `Simulation.ReleaseAllKey()`、恢复触发器、操作遮罩窗——`TaskRunner.cs:189-201`，部分在 UI 线程）→ 最后才 `Release()`。这个**清理窗口可达数秒**，期间 `TaskSemaphore.CurrentCount == 0`。
3. **无"槽位已释放"信号**：`task.status` 的 `running` 字段（`InstanceRequestHandler.cs:828`）虽用信号量做权威判断，但只能被**轮询**；助手的 settle 判定（P1-C 轮询，200ms×30）看到 status 翻转后立即发 task.start，必然撞窗。
4. **编排语义是"拒绝式"**：IPC task.start 撞锁即返回 `task_already_running`（`InstanceRequestHandler.cs:584-588`），把"等旧任务退场"的责任推给客户端——客户端只能重试（锤子）。

### 0.3 为什么必须动架构而不是继续打补丁

用户后续要做**任务调度器**：助手端频繁向 BGI 提交/跟踪/取消任务。"拒绝+客户端重试"语义下，调度器每个任务都要自带重试锤子，且无法区分"排队等待"与"失败"，无法可靠取消排队中的任务。需要的是**队列式编排 + 事件驱动生命周期**——这正是模块一原始需求（任务流、控制流、CT 控制）中未落地的部分。

---

## 1. 目标 / 非目标

### 目标

1. ext.task.start 从"撞锁拒绝"改为"**入队拿 taskHandle 立即返回**"，BGI 侧串行派发。
2. 任务生命周期**事件驱动**：`queued → started → completed/failed → slotReleased`，走既有 ext.event 通道（切片 1/4 的 EventHub + revision 闭环 + 断线续传直接复用）。
3. **CT 贯通**：取消按 taskHandle 路由；排队中可取消；task.stop 语义明确化。
4. `task.status` 扩展：`slotOccupied` / `queueDepth` / `currentTaskHandle`，settle 判定从"轮询猜"升级为"事件确认"。
5. 助手侧 SDK 提供 `SubmitTaskAsync → TaskHandle + 事件流`，CommandExecutor 的 1s×6 重试在新通道**退役**。

### 非目标（货冻结红线）

- `ScriptService.RunMulti`、`TaskRunner`、`OneDragonFlowViewModel` 的**业务执行逻辑一行不动**。
- 真实锄地执行/同步逻辑不动。
- v2 IPC 协议语义逐字节保留（老助手配新 BGI 行为不变）。
- 服务器零改动（本切片纯 BGI↔助手通道）。

---

## 2. 方法论基础

| 设计 | 出处 |
|---|---|
| 队列式提交、忙时不拒收、串行派发 | Actor 模型 Mailbox（Hewitt 1973；Erlang/OTP、Akka、Orleans） |
| queued→started→completed/failed 生命周期事件 | DAP 生命周期事件（initialized→terminated→exited） |
| capability `task.queue` 门控新语义、老端静默降级 | LSP initialize 能力协商 |
| 事件 revision + 近因缓冲 + 断线补发 | LSP 文档同步模型 / Kafka offset（切片 4 已落地，复用） |
| generation+name 幂等去重 | EIP 幂等接收者 |
| taskHandle 关联请求与事件 | DAP request seq 关联 |
| 有界队列（上限 8） | Reactive Streams 背压 |

---

## 3. 现状调用点全图（代码映射，行号以 main-OldTeaBag-B151 为准）

### 3.1 锁与信号

| 设施 | 位置 | 说明 |
|---|---|---|
| `TaskSemaphore`（全局信号量，计数 1） | `GameTask/Common/TaskControl` | 独立任务槽位；`CurrentCount==0` = 被占用 |
| `TaskRunner.RunCurrentAsync` | `GameTask/TaskRunner.cs:49` | 持锁入口：`WaitAsync(0)` :52 → `action()` :73 → `finally` :101 → `End()` :103 → `Release()` :116 |
| `TaskRunner.End()` 清理 | `TaskRunner.cs:189-201` | 释放按键、恢复触发器、遮罩窗（部分 UI 线程）——**清理窗口来源** |
| `CancellationContext` | `Core/Script/CancellationContext` | `Set/Cancel/CancelTokenOnly/Clear/WasCancelled` |

### 3.2 任务启动路径（全部收敛于 RunCurrentAsync 持锁）

| 路径 | 入口 | 收敛点 |
|---|---|---|
| IPC task.start 配置组 | `InstanceRequestHandler.HandleTaskStart` :554-693 → `RunMulti` :673 | `ScriptService.RunMulti` :135 → 内嵌 `TaskRunner.RunThreadAsync` **:197** → `RunCurrentAsync` |
| IPC task.start 一条龙 | `HandleTaskStart` :695-736 → `OneDragonFlowViewModel.OnOneKeyExecute` :720 | `OneDragonFlowViewModel` 内 `RunCurrentAsync` :2358/:2410/:2448 等 |
| IPC task.resume | `HandleTaskResume` :1273 → `RunMulti` :1335 | 同上 :197 |
| 手动 UI 配置组 | `ScriptControlViewModel.OnStartScriptGroupAsync` :2446 → `RunMulti` :2469 | 同上 :197 |
| 手动 UI 一条龙 | `OneDragonFlowViewModel` 手动入口 | `RunCurrentAsync` :2358/:2410/:2448 |
| 手动 UI 独立任务 | `TaskSettingsPageViewModel` 多处 → `RunSoloTaskAsync` :131 | `RunCurrentAsync` :147 |
| JS/地图追踪手动 | `JsListViewModel` :131 / `MapPathingViewModel` :152 → `RunMulti` | 同上 :197 |

### 3.3 IPC 控制/查询 handler

| handler | 位置 | 现状语义 |
|---|---|---|
| `HandleTaskStart` | :554-749 | 同步等执行完返回；幂等检查 :569-575、无损拒绝 :584-588、幂等登记 :593-596、等锁轮询（200ms/15s 兜底）:611-616 |
| `HandleTaskStop` | :540-552 | `CancellationContext.Cancel()` 全局取消 |
| `HandleTaskSuspend` / `HandleTaskResume` | :1137 / :1273 | 挂起上下文保存/恢复 |
| `HandleTaskStatus` | :751-854 | `running=CurrentCount==0` :828、`wasCancelled` :836、`hasSuspendedTaskContext` :824 |

### 3.4 已有可复用基础设施（切片 1/4 成果）

| 设施 | 位置 | 复用方式 |
|---|---|---|
| `ExternalInterfaceEventHub` | `Service/ExternalInterface/ExternalInterfaceEventHub.cs` | `Publish` :110（revision 单调+近因缓冲 500+扇出）、`GetReplayEvents` :156（断线续传）——新事件直接走这里 |
| 控制面分发 | `ExternalInterfaceCommandPlane.cs` | ext.task.start 分支改接协调器（按 capability） |
| 会话幂等窗口 | `ExternalInterfaceSession.cs:98` | 只缓存成功响应——queued 响应可缓存重放 |
| 助手 SDK | MHA `BgiExternalClient` | 加 SubmitTaskAsync + 任务事件订阅 |
| hello capabilities | 切片 1 握手 | 新增能力位 `task.queue` |

### 3.5 助手侧现状（要退役的部分）

- `CommandExecutor.StartGroupAsync`（MHA）：收 `task_already_running` → 1s×6 重发循环（约 :284-289）。新通道启用后此循环**仅保留在 v2 老 BGI 兜底路径**。
- `MainViewModel` P1-C settle 轮询（约 :5471-5496）：200ms×30 轮询 `running==false || hasSuspendedTaskContext`。新通道下改为等 `slotReleased` 事件，轮询降级为兜底。

---

## 4. 设计

### 4.1 总体结构

```
助手 CommandExecutor / 未来任务调度器
   │  ext.task.start {groupName, generation}
   ▼
BgiExternalClient SDK ── NamedPipe ── ExternalInterfaceSession
                                          │ capability task.queue ?
                                          ▼
                                  BgiTaskCoordinator（进程级单例）
                                    │ 入队即返回 {queued, taskHandle}
                                    │ pump：槽位空 → 派发 → await 完成 → 下一项
                                          │ 派发 = ExecuteTaskStartCoreAsync
                                          ▼
                              现有执行段（HandleTaskStart 抽出，单一事实源）
                                          │ RunMulti / OnOneKeyExecute
                                          ▼
                              TaskRunner.RunCurrentAsync（持锁→释放）
                                          │
   生命周期事件 ◄── ExternalInterfaceEventHub.Publish ◄──┘
   （queued/started/completed/failed/slotReleased，fire-and-forget）
```

### 4.2 BgiTaskCoordinator 队列语义

- **载体**：`System.Threading.Channels` 有界通道（容量 8，背压：满则返回 `queue_full` 错误码，**不阻塞**调用方）。
- **PendingTask**：`{ taskHandle(Guid), generation, groupName?, configName?, startFromIndex, sessionId, enqueuedAt, cts }`。
- **入队（Submit）**：
  1. 幂等去重：同 `generation>0 且 generation+name` 命中"在队/在跑"项 → 返回 `{ status:"adopted", taskHandle: 已有 }`（不发 queued 事件）；已完成项沿用 `_lastExecutedTask` 语义（迁移进协调器，v2 handler 改为查询协调器以保持单一事实源——**实施时注意：这是 v2 路径唯一允许的改动，行为必须等价**）。
  2. 入队 → 发 `task.queued` 事件 → 立即返回 `{ status:"queued", taskHandle, queuePosition }`。
- **派发（pump，后台线程，串行）**：
  1. 等槽位空：`TaskSemaphore.CurrentCount==1`（200ms 轮询 + 15s 兜底，复用现有 :611-616 姿势；协调器是**唯二**允许读信号量等待的地方，绝不 `WaitAsync` 抢占）。
  2. 发 `task.started` → 走 `Dispatcher.InvokeAsync` 调 `ExecuteTaskStartCoreAsync`（从 HandleTaskStart :601-736 抽出的执行段，含 Cancel/CancelTokenOnly、配置组读取、RunMulti、一条龙分支——**抽出后 v2 handler 与协调器共用，逐字节等价**）。
  3. `await` 完成 → 按结果发 `task.completed {cancelled}` 或 `task.failed {errorCode,message}`。
  4. 返回响应语义：**不再同步等执行**——queued 响应即终态响应，执行结果只走事件。
- **排队项 CTS**：取消/出队/进程退出时 `Dispose`；等待期可被取消打断（CT 贯通）。

### 4.3 生命周期事件契约（新增 `ExternalInterfaceEventNames`）

| 事件 | payload | 发布点 |
|---|---|---|
| `task.queued` | `{ taskHandle, groupName?, configName?, generation, queuePosition }` | 协调器入队 |
| `task.started` | `{ taskHandle, groupName?, configName? }` | pump 派发前 |
| `task.completed` | `{ taskHandle, groupName?, cancelled, durationMs }` | 执行段正常返回 |
| `task.failed` | `{ taskHandle, errorCode, message }` | 执行段抛异常 |
| `task.queueCancelled` | `{ taskHandle }` | 排队中被取消 |
| `task.slotReleased` | `{ }` | `TaskRunner.cs:116` `Release()` 之后一行（**全局信号**，无 handle——手动任务结束也发，助手 settle 判定统一靠它） |

事件全部走 `EventHub.Publish`（fire-and-forget、revision 同源、近因缓冲、断线续传——切片 1/4 机制自动生效）。`slotReleased` 挂载点是本切片**唯一**触碰 `TaskRunner` 的地方：一行 Publish，只读无状态，零订阅者时开销为一次入队（可忽略），单机行为不变。

### 4.4 task.status 扩展字段

`HandleTaskStatus` :826 响应体追加（纯增量，老客户端忽略未知字段）：

```json
{
  "slotOccupied": false,        // = TaskSemaphore.CurrentCount==0，与 running 同义但语义显式
  "queueDepth": 0,              // 协调器在队任务数（未协商 task.queue 的老客户端不受影响）
  "currentTaskHandle": null     // 在跑任务的 handle（协调器派发时登记，手动任务为 null）
}
```

ext.task.status 快照同样携带（QueryPlane 委托同一 handler，天然一致）。

### 4.5 协议与兼容

- **hello 能力位**：BGI 端 `task.queue`；SDK hello 后检查，**有则走协调器新语义，无则走现状路径**（v2 + 1s×6 重试兜底保留）。
- **v2 task.start**：逐字节不变（拒绝式、同步等待）。老助手配新 BGI 零感知。
- **ext.task.start 无 capability 时**：不可能发生（capability 是 BGI 端声明的，BGI 支持协调器才声明）——防御性保留 `unsupported_operation` 分支。
- **响应形状**：`{ status:"queued"|"adopted", taskHandle, queuePosition }`；错误码新增 `queue_full`。
- **ext.task.stop**：增加可选参数 `clearQueue`（ext 通道默认 true——"停止"含"别再继续"语义；v2 无此参数行为不变）。清空时在队项逐项发 `task.queueCancelled`。
- **ext.task.cancel**（新操作）：`{ taskHandle }` → 在队则移除+事件；在跑且 handle 匹配则等价 task.stop；否则 `task_not_found`。

### 4.6 助手侧改动（MHA）

1. `BgiExternalClient`：新增 `SubmitTaskStartAsync(...) → Task<BgiTaskHandle>`；新增任务事件订阅（`TaskLifecycleReceived` 事件，按 taskHandle 路由）。
2. `CommandExecutor.StartGroupAsync`：通道活跃且能力命中 → Submit + **等 `task.completed/failed` 事件**（带超时兜底 24h 级别 + 断线时按 SDK 恢复机制续订，事件不丢）；否则走现状 v2 路径（重试锤子保留）。
3. settle 判定（P1-C）：新通道下等 `slotReleased` 事件；`task.status` 轮询降级为兜底（现状逻辑不动）。
4. `HandleTaskStop` 助手侧封装：ext 通道默认带 `clearQueue:true`。

### 4.7 CT 贯通与取消语义

| 场景 | 行为 |
|---|---|
| 取消排队项 | `ext.task.cancel {taskHandle}` → CTS.Cancel → pump 跳过 → `task.queueCancelled` |
| 停止在跑任务 | `ext.task.stop` → `CancellationContext.Cancel()`（现状逻辑），在跑项按 `cancelled:true` 收尾 |
| 助手断线 | 在队项**保留**（BGI 侧任务不随连接死亡——与手动启动语义一致；调度器重连后经快照/事件续订恢复跟踪） |
| BGI 关机 | 协调器随进程销毁；在队项 CTS 在进程退出前 Dispose（防句柄泄漏） |

### 4.8 手动操作优先策略

- 手动 UI/触发器路径**不入队、零改动**。
- 手动任务在跑时，协调器排队项正常等槽位（pump 的等锁逻辑天然兼容）。
- 用户 F11 停止协调器在跑的任务：任务按 `cancelled:true` 收尾，pump **继续派发下一项**（在队语义=助手明确要执行；F11 只表达"停当前"）。——此为默认行为，见 §7 开放问题①。

---

## 5. 红线与纪律

1. **单机零感知**：不连助手时 BGI 行为逐字节不变；协调器 pump 只在有在队项时活动；事件发布零订阅者零扇出。
2. **货冻结**：RunMulti/TaskRunner 业务逻辑不动；TaskRunner 唯一改动 = :116 后一行 Publish。
3. **WPF STA**：执行段调度走 `Dispatcher.InvokeAsync`（现状姿势）；pump 循环在后台线程，绝不同步 `Dispatcher.Invoke` 死等。
4. **CT**：pump 等待与执行 await 全链路传 CTS；禁止 `Task.Run` 裸奔无观测（异常必须 try/catch 落日志 + `task.failed`）。
5. **IDisposable**：在队项 CTS 出队/取消/退出必 Dispose；协调器本身无外部资源。
6. **多实例**：协调器进程级单例，每 BGI 实例独立管道独立队列，天然隔离；不引入跨实例共享状态。
7. **单一事实源**：执行段抽出后 v2 handler 与协调器共用；`_lastExecutedTask` 幂等迁移必须保持 v2 行为等价（切片 1 审查修复的"登记在拒绝检查之后"语义不得回退）。

---

## 6. 验收标准

| # | 场景 | 预期 |
|---|---|---|
| C1 | 同机实机：任务 A 执行中，ext.task.start 提交 B | 立即返回 `queued`；A 清理完成后 B 自动执行；事件序 `queued(B)→…→slotReleased(A)→started(B)→completed(B)`，**全程零 task_already_running** |
| C2 | 上线锄地全链路（定时上线→确认→开锄，双助手） | 无 1s 重试噪音日志；开锄成功率不劣于现状 |
| C3 | 排队项取消 | `ext.task.cancel` 后该项不执行，`task.queueCancelled` 到达 |
| C4 | v2 老助手配新 BGI | 行为逐字节同现状（拒绝式+重试） |
| C5 | 新助手配老 BGI | `unsupported_operation` → Legacy 静默降级 |
| C6 | 单机不连助手 | 手动启动/F11/触发器行为不变；无协调器日志噪音 |
| C7 | 断线恢复 | 助手重启重连后 lastKnownRevision 补发在队/完成事件，不丢不重 |
| C8 | 工程 | 三项目编译 0 error；协调器单测（入队/去重/取消/顺序/背压）通过 |

---

## 7. 风险与开放问题（实施前需用户拍板）

1. **F11 与队列交互**：默认"F11 只停当前、队列继续"。备选"F11 清空队列"（更保守，但助手定时上线链路会被用户误停打断）。**建议默认方案**。
2. **队列上限 8**：锄地场景同时排队 ≤3-4，8 有余量；满则 `queue_full` 让助手显式处理（不静默丢弃）。
3. **`_lastExecutedTask` 迁移**：v2 路径查询协调器是 v2 唯一改动点，需逐字对照保行为等价；若实施时评估风险偏高，可降级为"协调器与 v2 各自维护、以 generation 单调性兜底"，但会留下双事实源隐患（记入遗留）。
4. **slotReleased 的 Publish 在手动任务路径也会触发**——属有意设计（settle 判定统一），但手动独立任务高频启停时事件量略增（近因缓冲 500 容量无压力）。

---

## 8. 实施步骤（commit 粒度建议）

| # | commit | 内容 | 风险 |
|---|---|---|---|
| 1 | `refactor(bgi): 抽出 ExecuteTaskStartCoreAsync` | HandleTaskStart :601-736 执行段抽方法，v2 handler 调用它；行为逐字节等价 | 低（纯重构） |
| 2 | `feat(bgi): BgiTaskCoordinator 队列+pump` | Channel 有界队列、Submit/pump、幂等去重、CTS 生命周期 | 中 |
| 3 | `feat(bgi): 任务生命周期事件 + slotReleased 挂载` | 6 个事件名、协调器各转换点发布、TaskRunner:117 一行挂载 | 低 |
| 4 | `feat(bgi): task.status 扩展 + hello capability task.queue` | 3 个新字段、能力位声明、ext.task.start 接协调器（capability 门控）+ ext.task.cancel | 低 |
| 5 | `feat(mha): SDK SubmitTaskStartAsync + CommandExecutor 事件驱动切换` | TaskHandle、事件等待、v2 兜底保留、settle 判定接 slotReleased | 中 |
| 6 | `test: 协调器队列语义单测` | 入队/去重/取消/顺序/背压/queue_full | 低 |

服务器零改动。切片 7 完成后，CommandExecutor 的 1s×6 重试在新通道退役（v2 兜底保留），《总控计划》§5 记录。
