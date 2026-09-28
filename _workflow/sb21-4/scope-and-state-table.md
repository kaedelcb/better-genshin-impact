# SB21-4：范围、状态转移与 BO-11 残项处置

## 授权范围

- **BO-13**：给 `LocalWaitParking` 增加会话内显式 Stop 出口；安全地终态化本地等待和持久 Hold/拒登；只清理由该运行完整绑定的队列项。
- **BO-11**：逐项核对 BO-6/7/8/9 的原始台账和现行实现。可实现被授权的具体范围，也可按原等级列明证据、风险、owner 和验收条件继续登记。本批不因同属 Wave3/C11 扩成四个额外修复批次。
- BO-4 已由 SB21-3 按 owner 接受的“历史修复＋本批验证”路径验收；BO-10/12 已由 SB21-2 验收。本批不重开。
- 生产入口、真实 User、R5.8、E3/E4/E5、热键和 BGI 生产进程门继续关闭。

## BO-13 停驻运行终局转移

| 当前记录与事实 | 动作/事件 | 期望持久状态 | 必须保留的身份 | 队列和同流程槽位 |
|---|---|---|---|---|
| `LocalWaitParking`，NoSend 已确认；Wait 裁定与当前 run/cursor/submission/context/binding 一致；无未决外部/前置动作责任 | 用户显式 Stop，宿主已无该流程 drive/reservation | `Cancelled`；追加可观察说明；不执行 flow terminal/收尾动作 | 保留 run/workflow/revision、cursor 的 node/occurrence/loop/attempt、submission key、旧 outcomes、Wait context 和原 binding | 原子检查全部队列可见不可变字段，仅精确匹配时写 `Cancelled` 墓碑；代际和 HWM 不回退，其他项不变；`Cancelled` 释放 `ActiveStates` 槽位 |
| `LocalWaitParking`，持久 Hold/拒登、NoSend 已确认、无 binding、无未决外部事实 | 显式 Stop | `Cancelled`；保留 Hold reason/context；不执行收尾 | 保留 run/cursor/submission/outcomes/decision context | 无 binding 就不写队列；终态释放同流程槽位 |
| Wait binding 对应队列项缺失或已是墓碑 | 显式 Stop | 无未决事实且运行身份有效时，终态 `Cancelled` | 不从队列重建或替换旧身份 | 缺失即已清；已有墓碑不重复写；HWM 不变 |
| Wait binding 的 `itemId` 命中项，但稳定身份、候选号、准入身份、流程/命名空间、排序、前置引用或登记时刻任一不匹配 | 显式 Stop | `LocalWaitParking`，返回 `Unavailable` | 完整保留原运行记录 | 队列原字节和不匹配项保持不变，避免旧 run 错取消新载荷 |
| `LocalWaitParking`/Hold 有未决发送/收尾事实；前置动作责任仍处于非终态（Intent/Submitted/Unknown/枚举外值）；或同流程仍有 drive/reservation | 显式 Stop | 返回 `Unavailable`，运行不转移 | 所有未决责任事实原样保留 | 不触碰等待队列，维持现有互斥保护。已终态前置责任可保留历史 send/job 字段，不单独阻止 Stop；按其 Succeeded/Failed/Cancelled 状态解释 |
| `StartupHandoff` 的 Context 与 binding 共用另一 run 的来源身份，或本 run 缺 scope/handoff 佐证 | 显式 Stop | 返回 `Unavailable`，不终态化 | run 与 queue 原字节保留 | 合法来源要求 `SourceIdentity == run.RunId`、规范 `AdmissionSourceScope` 与同 run 的 Start/ArmTrigger handoff。`PanelFlowRegistration` 来源必须在判定时由唯一父登记解析；Stop 只核对 Context/binding 配对快照，不把它当发送许可 |
| 请求 runId 与记录体 runId 不同、记录损坏/读失败、Wait 裁定游标快照不一致、deferred submission key 不一致、候选/准入身份不是共享 factory 的规范身份，或 Hold 错带 binding | 显式 Stop | 返回 `Unavailable`，不终态化 | run 文件和其所载其他身份记录原字节保留 | 不清理任何队列项；拒绝用路径名替记录身份背书 |
| 等待队列无法读取/损坏或取消写入失败 | 显式 Stop | 返回 `Unavailable`，运行仍可重试 | 原运行身份不改 | Store 错误不当作空队列；完成 Stop 前不终态化 |
| 已经 `Cancelled` | 再次 Stop | 返回终态提示，不产生第二次转移 | 记录修订与 decision/outcomes 不变 | 不改队列墓碑/HWM |
| 停驻后显式恢复，复核仍为 Wait/Hold | `ResumeRunAsync` | 仍 `LocalWaitParking`，不是 `Unknown`/`Cancelled` | 同一 run 和 cursor occurrence/loop/attempt；Wait binding 沿既有 Resume 规则复核 | 保持/重发同一 binding；之后 Stop 仍按当前完整 binding 清理 |
| 原 run 已显式停止 | 同流程新 Start | 可受理一个全新 run；旧 run 留在历史 | 新 run 有新 RunId，旧 run/outcomes 不改写 | 终态记录不再占 `ActiveStates` 槽位；Unknown/未决记录仍受原护栏拦截 |

实现顺序是先按 binding 写队列墓碑，再将 run 更新为 `Cancelled`，最后在 gate 外把已关联的 admission registration 回写到 terminal。若队列发布失败，原 run/queue 字节保留并可重试；若 run 终态落盘失败，队列墓碑已写而 run 保持原状态，重试识别 tombstone 后完成 run 状态且不重复写队列；队列读失败/载荷不匹配拒绝终态化。缺失项允许终态化；Hold 无 binding 时不访问队列。读损坏记录、路径/记录身份不符、游标或提交身份不符、前置动作责任未结及并发 reservation 均不可清理队列。错绑最终复读测试的字节基线为替换注入后的 A/B 记录与队列，不声称恢复注入前 A 内容；独立 queue 文件读取 I/O 故障未单独注入验证。

### BO-13 当前行为与验证

- `TaskCenterHost.RequestRunAction(Stop)` 仍保留原有 `Paused` 出口，另为无 drive 的 `LocalWaitParking` 走专用终态化路径；入口和 gate 内复读均核对路径 runId 与记录体 runId。转移前核对同流程无 reservation/drive，并分别用 `RunStore.HasUnresolvedExternalFact` 和 Stop 专用 `HasUnresolvedPrerequisiteResponsibility` 拒绝未决提交/收尾事实及前置动作责任；后者不改变恢复扫描的谓词合同。
- `HasValidParkedDecision` 绑定当前 run/workflow/revision、cursor 和 decision cursor 快照；要求当前 `submission` 为同一派生 key 的 `LocalWaitDeferred`，无 send attempt/job/accepted identity/terminal receipt。Hold 必须无 binding；Wait 必须以 context/binding 全字段互相一致，并经共享身份翻译和 successor identity factory 得到规范候选/准入身份。合法关系为 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`：恢复复核可更新 Context 并保留旧 binding；不要求 equality，但拒绝 Context 早于 binding 或任一快照领先当前 run。
- 来源身份分类型：`StartupHandoff` 的 `SourceIdentity` 必须等于 `run.RunId`，同一 run 还须有规范 `AdmissionSourceScope`、匹配的 binding scope 和 `Start`/`ArmTrigger` handoff 记录。`PanelFlowRegistration` 的身份来自该 run 唯一父登记的 `RequestIdentity`，Context 与 binding 保存同一 RequestIdentity 快照；Stop 检查配对一致性，不把快照当作发送许可，也不从队列读取来源身份（队列镜像不携带它）。
- `LocalWaitQueueStore.Cancel(LocalWaitBinding, ...)` 在路径锁内核对队列可见载荷；仅精确匹配时墓碑化。身份复用/载荷漂移返回 `PayloadMismatch`，不会取消它。
- NoSend 等待和 Hold 均保留在历史；Wait 清理保留其 generation、队列全局 `generationHighWater` 和无关等待项。重复 Stop 不再写盘。
- R6 正式复审后处理：#4/#R3-1 的 logger 异常由异常安全 `TryLog` 阻断，命名 throwing-logger 夹具先在旧实现以 sibling 仍为 Accepted 失败；R4 overlap 夹具加入 reconciliation 完成信号，在两条 worker 均退出后才查单次 revision、UpdatedRevision、另一 run operation JSON 和重复 Stop 全租约原始字节。R6 定向为 47 个用例。
- R7 补足 R1 #2/R4 identity：StartupHandoff 双快照借用其他 run identity 的具名反例要求 `Unavailable` 且 run/queue 精确字节不变；合法本 run identity、scope 与 handoff 的正向用例正常完成 Stop。重启恢复先验证持久 `Interrupted` 和 revision 递增，再显式 Resume/repark 保留原 binding 与 queue generation/HWM 并刷新 Context；same-process repark 也比较旧/新 Context revision。R7 当前定向覆盖 49 个用例。六次具名突变实验覆盖上述身份、状态持久化与 revision/Context 断言，其中 M3/M5 共享同一源码 mutant，因此为五种不同源码变体。
- R1 早期反向突变为 23 项；R5 的 5 项摘要见 `_workflow/sb21-4/review/r5-repair/reverse-mutations-r5-final/summary.json`；R6 五项与 R7 六次新增突变实验均逐项 build 成功、仅目标命名断言变红、源字节/SHA 精确恢复且恢复构建/测试通过，分别见 `_workflow/sb21-4/review/r6-repair/mutations/` 与 `_workflow/sb21-4/review/r7-repair/mutations/`。R7 M3/M5 是两个断言实验、一个唯一源码 mutant；合计 R7 五种源码 mutant。R1 历史 execution source 哈希只沿用各自原台账记录；本轮不从当前源哈希反推或重绑。早期无效/重叠及本地预检失败尝试保留并明确排除。

## BO-13 R8 最终复审与原级验收

GPT R8 对原始 R1 #1–#5 及登记的 R3/R4 IMPORTANT 扩展均建议在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。本 Goal 要求完成本批原级验收，故接受该有限裁决；不代表生产运行验收或 Wave3 全面清零。逐项证据、审查边界及报告见 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md`。

R1 #3 最终复读错绑具名测试以注入后的 A/B run 文件和 queue 内容为基线，只证明 Stop 不进一步改写当时可见记录，不声称恢复注入前 A 文件；独立 queue 文件读取 I/O 故障没有单独注入，不宣称由其他读写故障用例代替覆盖。R1 #5 的 run publish failure 具名测试证明 queue tombstone 先落盘，重试终态化 run 且 tombstone 全文件字节不变。R3/R4 扩展限定于展示的 Host/service 组件用例及确定性交错。

修复两项 SHOULD 后，最终 assistant build/ClaimSurface/BO-13/LocalWait/full-suite 与 testId 差集证据见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`。所有证据仍是助手组件/Host 测试；真实 BGI/User/生产门保持关闭。

## BO-11：Wave3 冻结残项逐项处置

| 原台账项 | 原级别 | 当前实现/组件证据 | SB21-4 处置 | 风险与 owner 后续验收 |
|---|---|---|---|---|
| **BO-6** 锚前停驻与已完成出现的真实冲突；来源 R19 必改-1，并有 R21 重要级“普通链尾可被聚合为成功”同族发现 | **必改**（R19）；关联**重要**（R21 F4） | `RecomputeSuccessor` 检出有效停驻与已完成锚冲突后记日志并返回 `null`；源注释仍明确称由 BO-6 承担。当前 `DriveAsync` 的 `hadBadOutcome` 不把 `waitLocally` 计为坏结果，不能证明冲突是显式失败态。现有重定位/锚帮助器用例只断言返回点或 null。 | **未闭合，原级交 owner；生产门继续关闭。**本批没有把 helper/null 解释成失败闭环，也没有改驱动推进语义。 | 选择并实现完成过滤推进或显式失败状态；用真实 Runner/loop 驱动在救援返回后推进 N 步，证明已完成节点零重复提交、停驻义务不丢、冲突不会聚合为 `Succeeded`/触发收尾；关键断言反向突变并做助手回归。 |
| **BO-7** rescue/candidate 计划全序及多个有效停驻点逐项裁决；来源 R21 F1/F2 | **必改 F1、必改 F2**（原台账） | 现行 `RecomputeSuccessor` 组件代码已比较 `(LoopIteration, SequenceIndex)`、取 rescue/candidate 较早者并遍历有效停驻点；`LocalWaitIdentityTranslationTests` 有跨 loop 排序及早期 unsafe parking 用例。它们没有驱动救援后续推进，且 BO-7 原文把完整闭合依赖于 BO-6。 | **组件部分已存在；因 BO-6 及端到端闭环未完成，两个原必改均仍开并交 owner。**不把历史/组件证据重记为本批完成。 | 在 BO-6 验收驱动路径中同时构造 loop candidate/rescue 竞争、多个有效停驻、删除后同身份插回等连续修订；证明全序决定不漏 candidate、不吞任一有效停驻，也不越过已完成出现；每个反例命名并反向突变。 |
| **BO-8** 返回恢复点后的线性推进无完成过滤；来源 R29 | **重要** | `WorkflowRunner` 当前仍显式记录该缺口：`RecomputeSuccessor` 只过滤锚之后的返回点；返回点之后的 `DriveAsync/Relocate` 可能再次提交已完成节点。`RecomputeSuccessor_ReturnsFirstIncompleteAfterAnchor_Bo8BehaviorPin` 只钉死 helper 返回 `X`，不证明 Runner 后续不会重做 `n3`。 | **未闭合，原重要级交 owner。**本批只保留证据和验收，不改 `DriveAsync/Relocate`。 | 用真实 runner 的已完成 history `[n3,n2]` 与新链 `[n2,X,n3,Y]` 执行恢复，证明 `n3` 的提交/副作用不重复，游标和 outcome 正确推进；执行反向突变并跑适用助手回归。 |
| **BO-9** 多有效停驻跨轮次/锚前后时，救援返回后的推进语义；来源 R34 重要-F5 | **重要** | `ParkedRescue` 当前按 loop/sequence 全序选择最早有效停驻；现有 loop 组件测试覆盖 tailBound 与 candidate/rescue 顺序。没有实际 runner 在返回后经历多轮推进的闭环。 | **未闭合，原重要级交 owner。**不把全序 helper 证据扩大为跨轮次运行结论。 | 用 loop 计划和至少两个有效停驻点构造跨轮次锚前/后组合，真实恢复并推进所有义务；证明无跳步、重复已完成出现或错误链尾成功，并做定向反向突变。 |
| **BO-11** owner 冻结打包：R44 未修部分、C12 名义缺口、BO-8 | owner 冻结的打包义务 | 其 C12→BO-4 项已由 SB21-3 依据 owner “历史修复＋本批验证”裁决闭合；SB21-3 只为映射/组件识别证据，没有开生产 successor 门。BO-8 如上仍重要未闭合；R44 宿主停驻出口由本批 BO-13 具名 Host 测试补证。 | **本批完成逐项登记与依赖说明；不代表 Wave3 所有残项清零。** | BO-6/7/8/9 各自继续保留原级和验收条件；owner 决定是否授权将来施工。SB21-3 BO-4 不重开；不启动后续批次。 |

### BO-11 证据边界

- 只读原始来源：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch20.json` 的 BO-6/7/8/9/11/13 与 rounds 19/21/29/34/47；同步核对开工时真实 R5.3 §24.120.4、§24.121、§24.122、§24.123 和 `_batch21/b21_plan.md`。
- BO-6 的源注释在 `WorkflowRunner.RecomputeSuccessor` / `ParkedRescue` 明示“链尾保守裁决归 BO-6”；BO-8 源注释明示“返回点之后推进无完成过滤”；BO-7/9 的现有证据是组件/排序级。不得把注释、helper 或纯计划表写成 end-to-end 闭合。
- C12/BO-4 只引用 SB21-3 已完成记录：历史修复提交 `47571736f27bfbc1a20e4d7bb67ce6db967cc0b5`；owner 接受提交证据 `d4b406a176a7dad151a9d3ea4382115c07324b98` 与最终收口 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`。本批不改 BO-4 等级或归属。
- 所有证据均为源代码、单元/组件测试和 Host 测试接缝；没有真实 BGI 实机、生产 User 或生产入口运行。
