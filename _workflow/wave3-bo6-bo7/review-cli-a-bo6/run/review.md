**义务 1：closed，保持原级 MUST。义务 2：closed，保持原级 IMPORTANT。** 裁决仅覆盖本轮指定的 BO-6 两项义务，不代表 BO-7/8/9 或生产门通过。

【事实】HEAD 为指定的 `0f46128fbba75ea346a9e05b94ec12f55f062b3e`，detached；已跟踪工作区无差异，存在未跟踪审查材料。`10675950a..HEAD` 的产品源码与测试无变化。本轮只读检查，未写文件、运行测试或追加会诊。

以下位置简称：

- `Runner`：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`
- `Tests`：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`

**1. BO-6 R19：closed（MUST）**

【事实】逐条核验如下：

| 验收点 | 代码及夹具证据 |
|---|---|
| 救援点选择 | `Runner:568–571`：有停驻历史且未标链尾时，显式 Resume 重算 successor，即使原 cursor 仍可定位。`Runner:1503–1516` 选择计划全序最早的有效、未完成停驻；`:1460–1467` 比较 candidate/rescue。 |
| 救援后完成过滤 | `Runner:655–662`：每次循环在节点闸门、前置和提交之前检查完成身份，跳过后继续循环。`:1575–1579` 使用 `NodeId + Occurrence + LoopIteration`，不使用可因重排变化的 SequenceIndex。 |
| 停驻义务保留 | `Runner:2060–2082`：追加 outcome；`waitLocally` 不推进游标。历史标记只有在同身份已有完成结果时才被视为清偿。 |
| 不虚假成功、不提前收尾 | `Runner:745–759`：仍可定位且未完成的停驻使聚合结果持久化为 Failed，并立即返回；收尾意图创建在其后 `:766–782`，Succeeded 在 `:815`。 |
| 真实 Runner/loop 证据 | `Tests:112` 实例化真实 Runner，使用真实 WorkflowStore/RunStore，外部边界为 fake。`:1005–1016` 验证恢复提交恰为 `P1,Q,R,stop`，lead/A/B 不重复，P1/Q/R 被清偿，无收尾且不是 Succeeded。`:1023–1029` 验证继续进入下一轮，旧轮完成身份没有被重提。 |
| 救援点两侧的完成出现 | `Tests:1131–1133` 验证返回点之前的 `anchor@0` 不提交，并推进至 `park@1`；`:1173–1175` 验证救援后跳过 `A@0`，继续至 `P@1`。 |

【推断／裁决】实现选择了原义务允许的“带完成过滤的推进”，过滤实际生效于同一 run 的驱动循环；并非仅修改 helper 返回值。上述证据足以关闭本项。

**2. BO-6 R21 F4：closed（IMPORTANT）**

【事实】旧的“真冲突返回普通链尾 null”路径已改为返回可驱动的停驻出现（`Runner:1563–1565`）。对于持久 `TailReached` 与未清偿停驻并存的防御路径，调用方获得的是：

- `State = Failed`；
- Note 明确说明未清偿停驻；
- `_runs.Update(run)` 后返回，保留历史义务；
- 不创建 PendingCompletion，不执行收尾。

具体位置为 `Runner:745–759`。`Tests:1071–1086` 同时断言返回对象与重新加载的 RunStore 状态，包含 Failed、TailReached、P1/Q 标记和空 PendingCompletion；`:1076` 验证收尾动作为空。

【推断／裁决】可正常救援的冲突通过实际重驱清偿；残留冲突通过持久 Failed 与正常成功链尾区分，满足等价可观察收敛要求。

**3. 四项反向突变核验**

【事实】直接解析了各目录的 baseline/mutant/restored TRX，并核对 testId、补丁、构建日志及 artifact 哈希。四项均为 **Passed → Failed → Passed**，三个阶段构建均成功；失败不是编译错误。

证据目录统一为 `_workflow/wave3-bo6-bo7/mutations/<突变名>/`，下表 TRX 为其中 `mutant/<突变名>-mutant.trx`。

| 突变名 | TRX 失败位置 | 命中的断言与失败文本 |
|---|---|---|
| `bo6-resume-live-park-v3` | TRX `:11–16` → `Tests:1008` | 提交序列断言：Expected `["P1","Q","R","stop"]`；Actual `["R","stop"]` |
| `bo6-completed-filter-v3` | TRX `:11–16` → `Tests:1008` | 同一提交序列断言：Actual `["P1","lead","A","B","Q",…]` |
| `bo6-tail-fail-closed-v3` | TRX `:11–14` → `Tests:1071` | `Assert.Equal(WorkflowRunState.Failed, failed.State)`；Expected Failed，Actual Succeeded |
| `bo6-tail-persisted-failure-v4` | TRX `:11–14` → `Tests:1080` | `Assert.Equal(WorkflowRunState.Failed, persisted.State)`；Expected Failed，Actual Running |

前两项命中 testId `398aed81-8691-87dd-a9cd-c62780f5f924`；后两项命中 `113f020c-1c29-e336-4c1a-3a50b69cbcc8`，均与具名目标一致。

【事实】四份实验记录的 original/restored SHA-256 均为：

`5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`

四份 `source-original.cs` 实测均匹配；在内存中应用各自补丁，重建出的 mutant SHA 均匹配记录。恢复后的 TRX 均通过。因此，**历史记录支持四项实验精确恢复原字节**；本轮没有重新执行恢复操作。

当前 Runner 为 LF，SHA 为 `181aa93e000be99d8eed9ce192587a56194d270e47f67bd561297a57476f14c9`。已逐字节验证它等于实验原件仅做 CRLF→LF 转换的结果，不能把当前 SHA 直接称为实验期恢复 SHA。

**4. 实际反例检查与新发现**

【事实】本轮核对了以下反例及结果：

- 保留可定位 R cursor、把 P1 插回完成锚前：禁用恢复重算后漏 P1/Q，目标断言变红。
- 救援后穿越 lead/A/B：禁用完成过滤后出现重复提交，目标断言变红。
- TailReached 与未清偿 P1/Q 并存：去掉聚合保护后错误 Succeeded，目标断言变红。
- 只改内存 Failed、不持久化：重新加载得到 Running，目标断言变红。
- 跨轮次恢复：当前夹具验证旧轮完成出现不重提，同时新轮同名出现仍可执行。

发现两项**建议级**材料问题：

| 位置 | 证据、影响与置信度 |
|---|---|
| `review-r9/raw-mutation-bundles/bo6-tail-persisted-failure-v4.txt:231` | 【事实】内嵌 experiment.json 与当前原件差一个 `mutation.patch` 哈希字段：旧值 `d7df…`，当前为 `fd6d…`。TRX、日志、补丁本体及重建 mutant 均核验一致。【推断】属于汇编包元数据滞后，不推翻目标失败证据；建议同步或注明版本差异。置信度高。 |
| `Runner:1486、1535–1537` | 【事实】注释仍写“最后一条”及“DriveAsync/Relocate 无完成跳过”，与当前最早停驻选择及停驻恢复过滤不一致。【推断】可能误导后续维护，不改变本轮已核验行为；建议修正文案。置信度高。 |

【事实】最新原始 TRX 为定向 **93/93**、助手全量 **1562 passed / 2 skipped / 0 failed**，四条新增 Runner facts 均通过。testId 差集为 8 added、4 removed；其中四对为 helper 更名，不能将 `0 changed` 解释为断言语义未变。

本轮未发现新增 MUST/IMPORTANT。结论限于源码与已保存的真实 Runner 驱动夹具证据；外部执行边界是 fake，未验证真实 BGI/生产运行。本次只读审查已完成，无需另开施工会话。

**本范围是否仍有未闭合的 MUST/IMPORTANT：否。**