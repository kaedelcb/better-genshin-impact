# R4.6 设计稿：通用策略能力 + 前置动作适配合同（D8）+ 收尾抑制接线（D10）

- 日期：2026-09-19 · 状态：**设计稿（待 ASTRA 会诊）** · 基线：048c8730（Batch1/2 后）
- 上游：`onedragon-r4-implementation-breakdown-2026-09-19.md` §3 R4.6 行、D8/D10/D15 定案、§7 二轮挂账（I5、skipped 词表消费、有界重试）

## 1. 现状盘点（开工前核实，非记忆）

| 事实 | 落点 | 状态 |
| --- | --- | --- |
| 六类策略调度语义 | WorkflowPlanner/WorkflowRunner：trigger.time（NextFire+多触发器取最近）、condition.weekdays（闸门过滤）、loop（B9 轮次等待公式化） | R4.4/R4.5 已落地 |
| 前置/收尾接缝 | `IWorkflowPrerequisiteAdapter.ExecuteAsync(strategy, run, occurrence, ct)`；`IWorkflowTerminalExecutor.ExecuteAsync(action, run, ct)`；`PrerequisiteResult(Proceed, Reason)` | 仅测试假实现，无生产实现 |
| 提交请求侧 suppress 标记 | `WorkflowSubmitRequest.SuppressConfigCompletionAction` 已随调用传递（R4.5） | **BGI 侧零消费**（I5 挂账） |
| BGI 原子能力 | `OneDragonAccountCapability`：CheckAndRedeemCodeAsync(uid)（**吞异常**）、VerifyUidAsync(configName, uid, accountBinding, ct)→bool、SwitchAccountAsync(uid, bindingCode, ct, switchTime)→bool；月卡 BlessingOfTheWelkinMoonTask 已在切号登录段内嵌 | R3.1 迁出，公版原生链不调用 |
| 兑换历史 | `AutoRedeemCodeChecker.CheckAndRedeemIfNeeded(uid)`：RedeemCodeHistoryStore 按码+UID 去重 + per-UID 当日首查标记 | 可复用（D8 兑换历史复用） |
| 整龙尾部 | OneDragonFlowViewModel ~L940：CheckRewardsTask → DragonEnd 通知 → `executionConfig.CompletionAction` 四档 switch（关闭游戏/关闭软件/关闭游戏和软件/关机）；单项边界（TaskId != null）已不附带收尾 | D10 接线点 |
| ext 协议 | 信封 v2；capabilities 在 ExternalInterfaceProtocol ~L151（缺省即不支持）；派发 ExternalInterfaceCommandPlane → InstanceRequestHandler；合同校验 ExecutionRequestContract（v1：bgiEpoch/idempotencyKey/expiresAtUtc；ext.task.start 另要身份+expectedConfigRevision） | 加 capability 不改信封（锚点 6 同构） |
| skipped 词表 | BGI 侧 TaskRunResult.Skipped/JobState.Skipped/skipped_normal 已分别表达（Batch2）；引擎 `MapTerminal` 只认 succeeded/cancelled，其余归 failed | **助手消费缺口**（§7 挂账归 R4.6+） |

## 2. 设计决策提案（E1–E8，待会诊）

### E1 ext 协议扩展：三新操作 + 四新 capability（不改信封）

| capability | 操作 | 语义 |
| --- | --- | --- |
| `prerequisite.account` | `ext.prerequisite.account` | 切号 + UID 验证（原子两段：先 SwitchAccountAsync，成后 VerifyUidAsync 后置复验） |
| `prerequisite.redeemCode` | `ext.prerequisite.redeemCode` | 兑换码检查兑换 + 月卡准备（BlessingOfTheWelkinMoonTask 弹窗消解，归属本操作） |
| `terminal.completionAction` | `ext.terminal.completionAction` | 流程成功边界的收尾动作（结构化英文枚举 → BGI 既有四档映射） |
| `execution.suppressCompletionAction` | （ext.task.start data 字段） | 单次调用收尾权限抑制（D10；见 E4） |

- 能力协商：引擎启动预检经 ext.hello 读取 capabilities；流程含前置/收尾策略而对应 capability 缺省 → **预检响亮拒绝**（不允许静默降级）。
- 请求合同：三新操作沿用 ExecutionRequestContract 同族校验（合同版本 + bgiEpoch + idempotencyKey + expiresAtUtc + 任务身份 runId/nodeId/occurrence/attempt）。
- 取消语义：三操作全程可取消；OCE → 明确 `cancelled` 回执，不虚报成功（与 Batch2 判据统一同纪律）。

### E2 D8 前置动作适配合同（生产 `WorkflowPrerequisiteAdapter`）

策略 → 操作映射：`prerequisite.account` → ext.prerequisite.account；`prerequisite.redeemCode`（含月卡准备）→ ext.prerequisite.redeemCode。参数：account 策略持 uid/accountName/bindingCode 引用（**配置引用而非复制内容**，锚点 3 同构）；redeemCode 策略持 uid 引用。

合同九条（对照 D8）：

1. **能力协商**：E1 预检，缺 capability 拒绝受理。
2. **任务身份**：请求携带 runId+nodeId+occurrence+attempt+idempotencyKey；BGI 侧幂等窗口复用既有 idempotency.window。
3. **可取消执行**：ct 全链传递；BGI 侧 OCE → cancelled 回执。
4. **真实结果**：BGI 不再吞异常——新增 capability 结果返回路径（redeem 返回 已兑换数/无新码/禁用/失败原因；account 返回 switched/alreadyCurrent/failed+原因）。`CheckAndRedeemCodeAsync` 的吞异常包装保留给既有调用语义，新增**非吞变体**供 ext 操作调用（原生链不调用 capability 的 R3.1 边界不变）。
5. **UID 后置验证**：ext.prerequisite.account 内 SwitchAccountAsync 成功后必须 VerifyUidAsync 复验；验证失败 = 前置失败（真实原因回执），**不得放行资源执行**。
6. **兑换历史复用**：BGI 侧沿用 RedeemCodeHistoryStore（按码+UID 去重）+ per-UID 当日首查；助手不另建兑换账本。
7. **月卡准备归属**：BlessingOfTheWelkinMoonTask 弹窗消解归入 ext.prerequisite.redeemCode 操作前段（切号登录段内嵌的既有调用不动）。
8. **防双触发事实绑定**：助手侧 `RunStore` 持久化前置事实 `PrerequisiteFacts`（键 = runId+nodeId+occurrence+attempt+账号标识+执行会话 bgiSessionId）；**不信任流程文件中的"已执行"标记**（流程文件永不写运行时事实）。崩溃恢复后同一 attempt 已成功的前置不重发；attempt 递增（显式重试/失败续跑）则重新执行。重发安全性由 BGI 侧幂等与兑换历史双保险。
9. **账号上下文保护**（D8 括号内二选一）：采用 **执行边界复验 UID**——前置切号完成后，紧随的 ext.task.start 提交在 data 中携带 `expectedUid`（仅当节点声明 account 策略）；BGI 执行段入口（受保护执行阶段、作业准入后）复验当前 UID，不符则拒绝执行（rejected/account_mismatch，真实原因）。不采用"前置段并入 ext.task.start"方案（侵入 ExecuteTaskStartCoreAsync 编排，公版侵入面大）。

**待会诊点 E2-①**：E2-9 的复验发生在作业准入后、资源执行前——若准入到执行间存在排队窗口，账号可能被手动/其他入口改变。是否要求复验紧贴执行起点（TaskRunner/ScriptService 准入回调内），还是接受排队窗口并在拒绝后由引擎按失败语义处置？

### E3 生产收尾执行器（`WorkflowTerminalExecutor`）

- `terminal.completionAction` 参数 `action` 取结构化枚举：`closeGame / closeSoftware / closeGameAndSoftware / shutdown`（流程文件英文词表；BGI 侧映射到既有中文四档，不改原生配置语义与 UI 文案）。
- 发送 ≠ 完成：ext.terminal.completionAction 返回执行回执（executed/rejected+原因）；引擎收到 executed 才清除 PendingCompletionAction；rejected/超时 → 收尾失败记 Failed（保留待执行意图，与 Batch1 B5 语义一致）。
- shutdown 破坏性最强：流程 terminal 显式声明才触发，不做默认值。

### E4 D10 收尾抑制 BGI 接线

- 通道：ext.task.start data 增加 `suppressConfigCompletionAction: true`（仅严格合同 v1 承认；旧入口无此字段 = false）。ExecutionRequestContract 读取后透传。
- 落点：`JobDescriptor` 增加 `SuppressConfigCompletionAction`（默认 false）→ `ExecutionScope` 同名只读属性 → OneDragonFlowViewModel 整龙尾部消费。
- 尾部语义拆分（D10：奖励检查/通知/关闭动作分别归属，不笼统一并关闭）：
  - CheckRewardsTask（领取额外奖励）：**保留**——属本次资源执行的组成部分，不是流程级收尾；
  - DragonEnd 通知：**保留**——BGI 本地执行事实通知，与流程收尾通知（助手侧职责）不冲突；
  - CompletionAction 四档（关闭游戏/软件/关机）：**被 suppress 跳过**——流程编排下由 terminal.completionAction 在成功边界统一触发一次，避免每个节点执行都触发关机类动作。
- 五不触发（引擎侧既有保证，此处对齐验收）：failed/rejected/unknown/手动停止/循环等待中均不触发收尾；skippedUser/skippedFilter 不阻断（D15）。

**待会诊点 E4-①**：手动入口（原生一条龙页面/旧控制入口）执行整龙时 CompletionAction 行为完全不变（suppress 缺省 false）。流程展开单项执行（TaskId != null）本就不跑尾部——suppress 只作用于整龙引用执行。此归属划分是否与 D10"整龙引用与展开单项执行只能选一条路径"无冲突？

### E5 skipped 词表助手消费（§7 二轮挂账收口）

- 生产边界 ext.job.status 终态映射新增 `skipped`：引擎 `MapTerminal` 增加 `"skipped" => ("skippedFilter", "BGI 侧跳过事实")`——子项正常跳过**不算坏结果**，聚合与 D15 一致（不阻断收尾）。
- 注意区分来源：BGI JobState.Skipped 现仅由"子项终态提交前通道消费"产生（skippedNormal 语义）；skippedUser（显式跳过）走 cancelled→确认链，已有 B4 语义，不经此词表。

### E6 独立策略实例 + 敏感字段脱敏

- 实例隔离：策略评估无跨节点共享可变状态——前置事实全部经 RunStore 按出现身份键控；适配器/执行器实现保持无状态（事实查询/写入只经 RunStore）。
- 脱敏：WorkflowRunnerOptions.Log 出口与 ext 请求日志中，uid 打码为 `前3***后2`（<5 位全码），bindingCode/账号绑定码**永不落日志**（适配器日志只记策略 kind+结果+脱敏 uid）；策略 Params 不整包进日志。

### E7 有界重试（§7 挂账 R4.6+ 部分落地）

- 前置动作（account/redeemCode）适配器内**有界重试**：瞬时失败（网络/识别抖动）最多 2 次（指数退避可取消），终态失败返回真实原因；**切号失败/UID 不符/兑换结果未知不重试**（确定性失败直接阻断）。
- 资源执行提交级重试维持挂账（引擎 attempt 键已备，R4.10 前不开放自动重试，防假成功）。

### E8 测试与验收口径（对照分解文档 R4.6 行）

1. 逐项夹具：六类策略各有生产路径夹具（trigger/condition/loop 已有引擎夹具，补生产适配器夹具：account 成功/切号失败/UID 不符/兑换成功/兑换未知/月卡消解/收尾四档/收尾拒绝）。
2. 跨资源类型挂载：同一策略实例挂载龙引用/配置组/单项三类节点，行为一致 + 实例隔离（并行 run 互不见事实）。
3. 防双触发证据：同 attempt 崩溃恢复不重发；同 UID 同日重复兑换被历史去重；自动（流程）与手动（原生页面）入口并存互不影响。
4. 收尾边界五不触发夹具 + skippedUser/skippedFilter 触发夹具（D15）。
5. 收尾抑制：suppress=true 时 BGI 尾部 CompletionAction 不触发、CheckRewards/通知保留；缺省 false 行为与基线逐字节一致（手动入口回归）。
6. 能力协商：capability 缺省时预检拒绝夹具。
7. skipped 词表：ext.job.status=skipped → 引擎记 skippedFilter，不阻断成功边界。
8. 回归：助手 210 + BGI 基线（922/936 抖动族）+ IpcIncident 37 + 合同 68 全绿。

## 3. 不做清单（R4.6 边界）

- 不改信封版本、不改既有 ext 操作语义（只加不改，锚点 6 同构）。
- 不动原生链：公版原生执行路径不调用 capability（R3.1 边界）；原生页面手动入口行为零变化。
- 资源执行提交级自动重试、重连/epoch 触发源接线、`task.single.native` 开放：归 R4.10。
- 生产 IWorkflowExecutionBoundary（ext.task.start 提交实现）：R4.6 只做 suppress 字段透传所需的最小链路（ExecutionRequestContract 读取 + JobDescriptor/ExecutionScope 传递 + 尾部消费）；完整生产边界随 R4.9/R4.10 集成验收。

## 4. 文件改动预估

| 侧 | 文件 | 改动 |
| --- | --- | --- |
| BGI | ExternalInterfaceProtocol.cs | capabilities +4 |
| BGI | ExecutionRequestContract.cs | suppress 字段读取 + 三新操作合同校验 |
| BGI | ExternalInterfaceCommandPlane.cs / InstanceRequestHandler.cs | 三新操作派发 |
| BGI | OneDragonAccountCapability.cs | 非吞结果变体（不改动既有方法签名语义） |
| BGI | BgiJob.cs / ExecutionScope.cs | SuppressConfigCompletionAction 传递 |
| BGI | OneDragonFlowViewModel.cs | 尾部消费 suppress |
| 助手 | Services/TaskCenter/WorkflowPrerequisiteAdapter.cs（新） | E2 生产适配器 |
| 助手 | Services/TaskCenter/WorkflowTerminalExecutor.cs（新） | E3 生产执行器 |
| 助手 | WorkflowRunner.cs / WorkflowRunModels.cs | PrerequisiteFacts 持久化 + MapTerminal skipped |
| 助手 | BgiExternalClient.cs | 三新操作客户端 + capability 读取 |
| 测试 | TaskCenter 夹具 + BGI 侧 ext 操作夹具 | E8 |


## 5. ASTRA 会诊处置记录（2026-09-19 三轮·设计稿会诊：8 阻断 + 6 重要 + 3 建议，全部处置）

核实方式：B1/B7 已由施工方对照源码亲自复核成立（ReadIdentity 强制 Guid.TryParse；OnOneKeyExecuteCore 尾部 scope.Result != Ran 即 return + ExecuteOneDragonAsync result=Skipped 落根作业 Failed，与 Batch2"根作业 Succeeded-with-skips"记录冲突属实，Batch2 记录偏乐观）。其余按 ASTRA 引证落点处置。

### 阻断处置

| 编号 | 发现 | 处置（修订决策） |
| --- | --- | --- |
| B1 | workflowRunId 强制 Guid，助手 run- 格式不过；occurrence/attempt 无线协议表达；严格合同非强制；IsWriteOperation 未含新操作；收尾无节点身份 | **E1 修订**：WorkflowRunRecord 增加持久化 WireRunId（Guid，建 run 时生成）；线协议身份=WireRunId+nodeId+iteration(=LoopIteration)，occurrence/attempt 作为 add-only 扩展字段入 data 并进 checkpoint；三新操作强制 executionContractVersion=1（缺版本即拒）；IsWriteOperation 纳入三新操作；收尾身份 nodeId=$flow、iteration=动作序号（0 起，与 PendingCompletionAction 同生） |
| B2 | 前置只有成功事实，无意图-受理-终态；键缺策略实例/轮次/epoch/指纹；attempt 恒 1 非现成能力 | **E2-8 修订**：每前置策略实例独立 PrerequisiteActionRecord（发送前落盘 intent）：键=runId+nodeId+occurrence+loopIteration+attempt+策略索引+操作类型+账号标识+epoch，含幂等键+请求指纹+状态机（intent/submitted/succeeded/failed/cancelled/unknown）；恢复时 unknown 先经 ext.job.status 对账，查不到按 D12 失败处置（不盲目重发）；传输重投同键同载荷（expiresAtUtc 不刷新），重投计数持久化 |
| B3 | ct 只到本地等待，跨进程取消缺失；前置期 LeafCts 未建立，SkipCurrent 够不到前置 | **E1/E2-3 修订**：三新操作**作业化**——BGI 侧登记统一注册表（JobKind 尾部追加 Prerequisite/Terminal），快速返回受理回执（jobId），助手经 ext.job.status 对账、ext.task.cancel 取消；前置段开始前建立 LeafCts（SkipCurrent 绑定出现身份取消在飞前置）；PrerequisiteResult 结构化：Proceed/Failed/Rejected/Cancelled/Unknown，**Unknown 不按普通失败续跑**（走 D12 保守路径）；本地 OCE≠远端停止，取消后走确认链 |
| B4 | UID 复验必须先取得执行权；VerifyUidAsync 是 Contains 非相等；accountBinding=false 绕过；OnAdmitted 同步 Action 无法等待 | **E2-9 定案**：expectedUid 复验在**作业准入后、首个资源副作用前**的受保护执行阶段内异步完成（连续持有执行权，不释放再排队）；严格路径 UID 校验修订：规范化完整相等、空 UID 格式拒绝、accountBinding=false 不豁免（新严格路径专用，旧方法签名/语义不动）；redeem 操作在自身副作用前同样验当前账号；准入后异步钩子替代同步 OnAdmitted（新路径）；**诚实边界**：保护覆盖受协调入口，直接人工操作游戏不在边界内（写入用户文档） |
| B5 | 兑换内层也吞异常+吞 OCE；失败写当日标记致后续假跳过；会话成功缓存不分 UID；HTTP 无 ct；无逐码真实结果 | **E2-4 修订**：AutoRedeemCodeChecker 新增严格路径方法（旧方法逐字不动）：结构化结果（redeemed/alreadyRedeemed/noNewCodes/disabled/failed/cancelled/unknown+逐码计数）、OCE 传播、失败不写当日标记、HTTP 全链 ct、会话缓存按 UID 键控；逐码成功数以 UseRedemptionCodeTask 实际返回为准，不可考则计数语义如实标 unknown |
| B6 | suppress 仅文档显式声明才传 true，任务中心多节点流程可在首个整龙节点触发原生关机 | **E4 修订（B6 定案）**：任务中心提交的整龙调用**固定 suppress=true**（生产边界常量，与流程是否声明 terminal 无关）；suppress 权限贯穿嵌套执行与恢复描述符（checkpoint 携带）；原生手动入口缺省 false 不变 |
| B7 | 整龙含正常跳过子项 → 根作业 Failed + 尾部不执行，与 D15/Batch2 记录冲突 | **E9（新决策，根/叶结果合同）**：整龙跑完且仅有正常跳过 → 根作业 Succeeded（子作业各自携带 Skipped）；尾部判定 scope.Result is not (Ran or Skipped) 才提前返回（含跳过时 CheckRewards/通知/CompletionAction 照常，D15 语义）；ExecuteOneDragonAsync 结果判定 Ran-or-Skipped+_finishMark；单项边界的正常跳过保持 Skipped 终态不变；失败/取消/拒绝路径逐字节不动 |
| B8 | closeSoftware/shutdown 会杀死回执进程；提交后取消承诺不可兑现；Runner 收尾 OCE 清空 PendingCompletionAction 丢事实；terminal 列表无逐动作水位；迟到收尾可能误伤新任务 | **E3 修订**：每流程**至多一个** terminal.completionAction（Planner 预检响亮拒绝多个）；回执三态+unknown：动作前返回 accepted（含 epoch+无其他活动作业校验结果），动作后尽力 executed，进程自杀类允许永远 unknown（禁止补发）；accepted 后不可逆，取消请求返回 rejected；Runner 收尾 OCE **保留** PendingCompletionAction（恢复按 unknown 对账）；BGI 执行前校验 epoch 匹配+JobRegistry 无其他活动作业；PendingCompletionAction 持久化动作身份+指纹+状态 |

### 重要处置

| 编号 | 发现 | 处置 |
| --- | --- | --- |
| I1 | capability 名不一致；重握手缺字段保留旧能力；HasCapability 不查连接态；预检未接三入口 | 统一 execution.suppressConfigCompletionAction（D10 名）；BgiExternalClient 每次握手**重建**能力快照（缺=不支持）；HasCapability 前置连接态检查；引擎预检接 启动/恢复/重载 三入口；expectedUid 归入 prerequisite.account capability 保证，服务端不得静默忽略（夹具覆盖） |
| I2 | skipped 映射混来源；ConfirmSkipAsync 不接受 skipped 竞态；CommitOutcome 归一化词回流 MapTerminal 误判 | MapTerminal 原始词扩展 skipped，NodeOutcome 增 RawTerminal（原始线协议词+来源）与业务结果分开；ConfirmSkip 收到 skipped 记 skippedFilter（远端正常跳过如实优先），不计 skippedUser（两者均不阻断收尾，D15 不变）；CommitOutcome 的 ObservedTerminal 只存**原始线协议词**，恢复 MapTerminal 只吃原始词 |
| I3 | 底层日志直接打印完整 UID/bindingCode；NodeOutcomes.Reason/RunRecord.Note 持久化 ex.Message；≤5 位 UID 前三后二仍全露 | OneDragonAccountCapability/AutoRedeemCodeChecker 日志脱敏（UID 打码 **长度≤5 全遮盖**，bindingCode 永不落日志）；结构化结果走受控错误码+清洗消息；RunRecord.Note 只存受控原因码+脱敏摘要，不存原始 ex.Message |
| I4 | 重试依据应是副作用确定性；expiresAtUtc 刷新会改指纹；"最多两次"歧义且预算不持久化 | 三层区分定案：传输重投（同键同载荷同指纹，最多 1 次，计数持久化）；新执行尝试（R4.6 不自动开放）；操作内部观察重试（BGI 侧只读重读）；未知/取消/UID 不符/切号结果不明均不自动重复副作用；兑换网络获取失败（副作用前）可安全重投 |
| I5 | 账号引用结构未定；月卡归属与实际调用不符；登录循环用全局令牌 | 账号引用定案 accountRef={configName, accountKey}：BGI 侧解析（配置权威在 BGI），解析后**冻结本次会话账号上下文**（uid+bindingCode 快照随作业描述符），执行途中配置变更不影响本次；月卡两段区分：登录弹窗处理（切号内嵌不动）+ 节点准备月卡检查（redeem 前段，幂等安全，验收证重复检查不重复领取）；SwitchAccountAsync 内嵌月卡改用传入 ct |
| I6 | capability 有可变字段/共享配置/跨 UID 缓存；RunStore 搬家不消除依赖对象污染 | ext 操作每次新建 capability 实例（临时字段天然隔离）；跨 UID 缓存按 UID 键控（B5 同项）；RedeemCodeHistoryStore 原子性施工时核实，非原子则加锁；E8 措辞改"同一实现、独立实例" |

### 建议处置

| 编号 | 处置 |
| --- | --- |
| S1 | 采纳：E4-① 定案**无冲突**——整龙引用/展开单项是同一资源的两条既有执行路径，suppress 不新增路径；展开单项无奖励检查是 R3 单项合同结果，不得为补奖励再跑完整原生链；通知归属区分"资源执行结束（BGI）"与"流程成功（助手）" |
| S2 | 采纳：本批最小链路必含 严格身份+账号引用解析、expectedUid 与 suppress 实际消费、前置受理/查询/取消/恢复记录、兑换底层真实结果+取消传播、能力门控；§4 文件预估补 AutoRedeemCodeChecker/UseRedemptionCodeTask/恢复权限携带文件；**R4.6 完成口径=合同/组件完成+夹具证据**，生产端到端策略闭环归 R4.10 报告 |
| S3 | 采纳：E8 补故障窗口用例（副作用完成事实未落盘/幂等窗口过期/重连缺 capability/切号后排队被换号/仅 redeem 节点账号不符/前置期 SkipCurrent/取消后远端仍执行/含 skipped 整龙根结果/收尾提交后断线/恢复后抑制权限保留）；回归口径改"新增项通过、既有失败集合不扩大" |

### 实施批次（处置后）

- **Batch 3a（BGI 基础修复）**：E9 根/叶结果合同（B7）+ E2-4 兑换严格路径（B5）+ UID 严格校验变体（B4 前半）+ I3 底层脱敏 + I5 月卡 ct。
- **Batch 3b（BGI 协议+作业化）**：E1 身份合同扩展 + 三新操作作业化（JobKind.Prerequisite/Terminal、派发、取消、ext.job.status 复用）+ E4 suppress 接线（JobDescriptor/ExecutionScope/尾部消费/checkpoint 携带）+ E3 收尾 BGI 侧 + I1 capability 快照。
- **Batch 3c（助手侧）**：WireRunId + E2-8 前置意图记录 + 生产适配器/执行器 + 结构化 PrerequisiteResult + 前置期 LeafCts + I2 通道修复 + I4 传输重投 + E6 脱敏。✅ 已落地（2026-09-19 R4 八批：夹具 15/15、助手回归 225/225、合同审计 78/78；E6 脱敏经 Runner.Sanitize 持久化面 + 适配器 MaskAccount 双层落实）。

### 四轮会诊（R4.6 阶段完成审核，ASTRA 复核 Batch 3a–3c 执行效果）处置记录

审核结论：7 阻断 + 6 重要。逐条处置如下（Batch 3d 落地，commit 见总计划 §7 R4 九批）：

| 编号 | 发现 | 处置 |
| --- | --- | --- |
| 阻1 | 无 jobId 被误判取消已确认；恢复对账不被 SkipCurrent 中断；Stop 绕过前置确认 | **采纳**：PrerequisiteActionRecord 增 SendAttempted（发送前持久化）；ConfirmCancellationAsync 区分「确定未发送→Cancelled」与「可能已发送→Unknown」；恢复对账改用 leaf.Token（对账期叶子取消同走确认链）。Stop 场景诚实边界：运行取消时前置记录保留 Intent(SendAttempted)/Submitted 事实不清算，BGI 侧前置作业取消由显式动作经 ext.task.cancel 驱动，Stop 不冒充远端确认 |
| 阻2 | 收尾发送后受理前 OCE 会清除可能已执行的意图 | **采纳**：PendingCompletion 增 dispatching 状态（发送前持久化）；Runner OCE 纪律 = pending（确定未发送）清除 / dispatching·submitted 保留标 unknown |
| 阻3 | 事实复用缺 kind/账号身份；账号掩码可碰撞；既有失败自动产生第二次执行 | **采纳**：Matches 扩为完整身份（+Kind+AccountKey）；AccountKey 改 SHA256 截断哈希（非可逆掩码，碰撞隔离，RunStore.DeriveAccountKey）；既有 Failed/Cancelled 终态事实不自动重试（attempt 恒 1，需人工处置）；Succeeded=游戏态事实跨纪元有效（切号/兑换效果不随 BGI 重启消失——B2 epoch 约束限在飞作业，本条文档化） |
| 阻4 | 未实现 accountRef 合同 | **不成立（按修订）**：I5 已于 Batch 3a 施工期修订——uid/bindingCode 来自流程策略参数（R1 迁移产物携带原值+sensitive 标记），因 BGI 本机无账号存储可解析 accountRef；本表 I5 行未回写该修订致会诊按旧合同判。处置=本文档回写修订（本轮），代码无改动 |
| 阻5 | 前置 Unknown 先推进游标再标 Unknown，崩溃窗口可绕过未决前置 | **采纳**：CommitOutcome 对 unknown/cancelUnconfirmed 游标不推进+调用方先置状态单次原子落盘；ObservedTerminal 未确认不落（保持 null，恢复扫描按在飞标 Unknown）。恢复扫描不加前置在飞分支（会把恢复对账路径堵死）；不变式=在飞前置 ⟹ 游标恒在其节点（夹具证明） |
| 阻6 | 恢复对账把 outcome_unknown 降为 Failed、把 WasCancelled 判 Proceed | **采纳**：统一终态解释器 BgiJobTerminalPolling.InterpretJob（WasCancelled 优先、skipped 协议违例不可考），正常轮询与恢复对账共用；prerequisite_outcome_unknown 恒映射 Unknown |
| 阻7 | 任务中心 suppress 仍按流程声明，未固定 true | **采纳**：SubmitAndAwaitAsync 固定 suppress=true（生产边界常量）；IWorkflowExecutionBoundary 增 SuppressConfigCompletionSupported（默认 true=测试接缝）；Planner 预检接 suppress 能力（缺能力+含整龙/配置组节点 → 阻止执行）；启动/恢复/重载三入口接线 |
| 重8 | I4 只有字段无重投实现；指纹未覆盖注入字段 | **采纳（诚实化）**：传输重投不实施（发送失败一律 Unknown 保守处置），RedeliveryCount 字段预留；bgiEpoch 冻结进载荷后再计算指纹（指纹覆盖完整冻结载荷） |
| 重9 | 业务词回流 ObservedTerminal | **采纳**：WorkflowNodeOutcome 增 RawTerminal（原始词+业务结果分开）；ObservedTerminal 只存原始线协议词；MapTerminal 归一化词恒等映射降级为遗留防御 |
| 重10 | 脱敏未覆盖主要写入口 | **采纳**：持久化面统一 Sanitize（PrerequisiteActionRecord.Reason / NodeOutcome.Reason / Note 全出口）；边界说明：Sanitize 覆盖长数字串（UID 形态），远端自由文本受 BGI 侧受控错误码约束，bindingCode 永不由远端回传 |
| 重11 | 轮询传输异常未处理 | **采纳**：查询传输/协议异常按通道瞬态（预算内继续等），OCE 绝不降级为瞬态 |
| 重12 | Dispose 后 HasCapability 仍放行 | **采纳**：Dispose 同步撤销 Ready 态 + 清空能力快照 |
| 重13 | 收尾载荷缺 occurrence/attempt | **采纳**：收尾载荷与 PendingCompletion 记录补 occurrence=0/attempt=1 扩展字段 |

- **Batch 3d（四轮处置）**：上表 12 项采纳落地（阻4 为文档回写）。新夹具 7/7（失败不重试/Unknown 游标不推进/在飞恢复对账/dispatching 取消/suppress 固定/suppress 能力预检/未确认不落 ObservedTerminal）；助手回归 232/232。
