结论：**义务 1 closed（原级 MUST）；义务 2 closed（原级 MUST）**。裁决限于本轮指定范围，不代表 BO-8/BO-9 或生产实机验收通过。

【事实】已核对 HEAD 为 `0f46128fbba75ea346a9e05b94ec12f55f062b3e`、detached，已跟踪文件无未提交差异；相关产品源码及夹具相对 `10675950a` 无后续变化。工作区存在未跟踪审查材料。本轮未写文件、未运行构建或测试，以下运行结论来自核验过的既有日志/TRX。

**1. 义务 1：closed，保持 MUST**

【事实】[WorkflowRunner.cs](C:/Users/Administrator/.codex/worktrees/wave3-bo6-bo7/better-genshin-impact-LCB/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs:1460)：

- `1460–1467`：candidate/rescue 比较采用 `(LoopIteration, SequenceIndex)` 字典序，candidate 较早即返回 candidate；rescue 已非无条件覆盖。
- `1541–1546`：探针下界同样使用上述字典序，没有把轮次、序列号拆为独立排除条件。
- `568–579`：显式 Resume 重算恢复点并进入真实 `DriveAsync`。
- `655–662`：救援后推进过滤已完成身份；Planner 的 `Next` 在 `WorkflowPlanner.cs:65–72` 按序列推进、循环尾递增轮次。

实际核验的双向反例及结果：

- candidate 较早：`WorkflowRunnerTests.cs:1090–1135`，真实 Resume 首次提交 `candidate@0`，随后到达 `park@1`，不重提 `anchor@0`。
- rescue 较早：`WorkflowRunnerTests.cs:1138–1175`，首次提交 `P@0`，过滤完成的 `A@0`，随后到达 `P@1`。

【推断／裁决】代码比较与两方向 Runner/loop 证据共同支持原 F1 闭合。夹具使用真实 Runner、Planner 和存储，外部执行边界为 FakeBoundary；不扩大为 BGI 实机证据。

**2. 义务 2：closed，保持 MUST**

【事实】同一实现中：

- `WorkflowRunner.cs:1411–1426` 遍历所有停驻标记，没有“最新标记安全即 break”。
- `1503–1515` 遍历全部仍可定位、未完成标记，选取计划全序最早者。
- `1575–1579` 完成判定使用 `NodeId + Occurrence + LoopIteration`，不依赖可变的 `SequenceIndex`。
- `WorkflowPlanner.cs:82–96` 以稳定身份重新定位；`WorkflowRunner.cs:2060–2082` 追加历史结果，停驻不推进游标。

实际核验的连续修订反例：[WorkflowRunnerTests.cs:941](C:/Users/Administrator/.codex/worktrees/wave3-bo6-bo7/better-genshin-impact-LCB/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs:941) 从真实 Start 开始，经历 P1 停驻、删除 P1、Q 停驻、删除 Q、R 停驻，再将同身份 P1 插回完成锚前、Q 插回锚后。

`1008–1016` 精确断言恢复提交顺序为 `P1、Q、R、stop`，完成的 `lead/A/B` 各仅一次；`1023–1029` 又验证推进至下一轮 `P1@1`，不与 `P1@0` 混同。

【推断／裁决】该连续修订反例及完成过滤证据支持原 F2 闭合。

**3. 四份反向突变：均命中目标断言，恢复证据通过**

已直接解析各目录原始 TRX，并校验记录中的原始文件散列：

| 突变 | 命中的具名测试／断言 | 突变实际结果 |
|---|---|---|
| `bo7-candidate-first-v3` | `Resume_CandidateAndRescueAcrossLoop…`；实验行 1121，现行 1131 | 应为 `candidate,1,0`，实际 `anchor,0,1` |
| `bo7-rescue-first-v3` | `Resume_RescueBeforeNextLoopCandidate…`；实验行 1163，现行 1173 | 应为 `P,0,0`，实际 `P,0,1` |
| `bo7-earliest-park-v3` | `Resume_ReinsertedParksBeforeCompletedAnchors…:1008` | 实际遗漏 P1，仅 `Q、R、stop` |
| `bo7-stable-identity-v3` | 同一测试 `:1008` | 实际重复提交 `lead、A、B` |

【事实】四项均为基线 Passed、突变 Failed、恢复 Passed；三个阶段构建退出码均为 0，测试退出码均为 `0→1→0`。

四份补丁均可在内存中从保存原件重建出记录的 mutant SHA-256；反向替换均精确还原原字节。实验记录的 original/restored SHA-256 均为：

`5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`

当前源码为 LF，直接 SHA 为 `181aa93e…76f14c9`；仅转换为 CRLF 后与上述原件散列完全一致。v3 测试快照到现行版本也已核对，仅增加记录在案的持久化断言及行尾变化，三个 BO-7 测试正文未变。

既有修正后回归为 Runner/LocalWait **93 通过、0 失败**；全量 TRX 为 **1562 通过、0 失败**。已核实相关具名测试通过及全量 TRX 散列。

**4. 新发现**

- **建议级，注释陈旧**：`WorkflowRunner.cs:1486` 仍写“取最后一条可定位停驻标记”，与 `1503–1515` 的全序最早选择矛盾；`1536` 仍描述驱动“无完成跳过”，与 `655–662` 不符。【事实】代码直接对照可复核。【推断】会误导维护者，但本次核验未据此发现运行正确性缺陷，不阻断上述两项裁决。

本轮只读审查已完成，无需额外操作或施工交接。

**本范围仍有未闭合的 MUST/IMPORTANT：否。**