import io, sys
p = r"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs"
t = io.open(p, encoding="utf-8").read()
before_len = len(t)
edits = []

def add(old, new):
    edits.append((old, new))

# E1: BO-8 scope widening + BO-9 entry guard capture
add(
"""        // BO-6: a resumed run with a persisted local-wait obligation filters already completed
        // stable occurrences while advancing from a rescue point. Keep this recovery behavior
        // scoped to parked runs; ordinary revision progression remains outside BO-8.
        var filterCompletedDuringParkRecovery = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord);
""",
"""        // [BO-8 / R29 重要；继承缺陷] 推进段按**稳定出现身份**过滤已完成项：恢复点（显式恢复重算、
        // 修订热重载重算、或链尾重入的停驻）之后的线性推进不得二次提交已完成出现——修订重排把已
        // 完成节点挪到恢复点之后时，旧行为会重复触发外部副作用（不可撤销）。过滤只按
        // (NodeId, Occurrence, LoopIteration) 判定；停驻标记（LocalWaitResultWord）不算完成，
        // 停驻义务照常重驱。
        // [BO-9 / R34 F5 重要] 真实链尾仍存活的停驻义务按计划全序重入驱动（TryRelocateToLivePark）。
        // enteredAtTail：入口即为持久链尾的记录不由重入路径重开（BO-6 防御语义）。
        var enteredAtTail = run.TailReached;
""")

# E2: unconditional completion filter
add(
"""                // BO-6: a rescue point may precede completed anchors. Re-drive the parked
                // obligation, but never resubmit a stable occurrence already completed in this
                // run. One step per loop preserves definition-boundary and round-start handling.
                if (filterCompletedDuringParkRecovery && HasCompletedOutcome(run, occurrence))
                {
                    Log(run, $"停驻恢复推进过滤已完成出现 {occurrence.NodeId}#{occurrence.Occurrence}"
                             + $"（轮次 {occurrence.LoopIteration}），继续按计划全序前进。");
""",
"""                // [BO-8] 恢复点或修订重排可能落在已完成出现之前。停驻义务照常重驱，但任何已完成
                // 稳定出现都不得二次提交。逐步推进（每步一个出现）保留定义边界与轮次起点处理。
                if (HasCompletedOutcome(run, occurrence))
                {
                    Log(run, $"推进过滤已完成出现 {occurrence.NodeId}#{occurrence.Occurrence}"
                             + $"（轮次 {occurrence.LoopIteration}）：按稳定身份跳过，不二次提交。");
""")

# E3: BO-9 tail re-entry
add(
"""                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);
                if (occurrence is null) break; // 链尾（TailReached 已落盘）
""",
"""                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);
                if (occurrence is null)
                {
                    // [BO-9 / R34 F5 重要] 真实链尾：线性推进（plan.Next）到不了跨轮次停驻
                    // （如修订移除循环定义后仍有效的 A@loop2）。按计划全序重入最早的存活停驻，
                    // 避免「零发送义务被静默吞」。每次重入必然驱动该出现一次（完成或再次停驻），
                    // 故重入严格消耗义务、可终止；入口即链尾（持久 TailReached）不由本路径重开。
                    if (!enteredAtTail && TryRelocateToLivePark(run, plan, out var livePark))
                    {
                        Log(run, $"链尾仍存可定位且未完成的停驻义务 {livePark!.NodeId}#{livePark.Occurrence}"
                                 + $"（轮次 {livePark.LoopIteration}）：按计划全序重入重驱，不按链尾放行。");
                        occurrence = livePark;
                        ApplyRelocation(run, occurrence);
                        _runs.Update(run);
                        continue;
                    }
                    break; // 链尾（TailReached 已落盘）
                }
""")

# E4: helper next to HasCompletedOutcome
add(
"""    /// <summary>游标 → 当前计划中的出现（恢复/推进共用；身份失效按最后完成身份重算，不回链首重跑）。</summary>
""",
"""    /// <summary>
    /// **[BO-9 / R34 F5]** 真实链尾处的停驻义务重入点：返回**计划全序最早**的、在当前修订中仍
    /// 可定位且未完成的停驻出现（与 <see cref="ParkedRescue"/> 及 tailBound 同口径）；没有这样的
    /// 存活停驻时返回 false。存在性判据只有两条：可在当前计划定位、且没有完成结果——停驻标记本身
    /// 不算完成，过期标记（同身份已补上完成结果）不参与重入。
    /// 入口即链尾的持久记录由调用方守卫，不在本方法内重开。
    /// </summary>
    private static bool TryRelocateToLivePark(WorkflowRunRecord run, WorkflowPlan plan,
        out WorkflowNodeOccurrence? livePark)
    {
        WorkflowNodeOccurrence? earliest = null;
        foreach (var parked in run.NodeOutcomes.Where(o => o.Result == LocalWaitResultWord))
        {
            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var occ)) continue;
            if (HasCompletedOutcome(run, occ)) continue;
            if (earliest is null
                || occ.LoopIteration < earliest.LoopIteration
                || (occ.LoopIteration == earliest.LoopIteration && occ.SequenceIndex < earliest.SequenceIndex))
            {
                earliest = occ;
            }
        }
        livePark = earliest;
        return earliest is not null;
    }

    /// <summary>游标 → 当前计划中的出现（恢复/推进共用；身份失效按最后完成身份重算，不回链首重跑）。</summary>
""")

# E5..E8: BO-6/7-D1 stale comment fixes
add(
"""            // [Wave1 R18 必改-1] 下界：锚候选非 null ⇒ 候选本身；candidate 为 null（锚在链尾）⇒
            // **最后一条可定位停驻点**——探针不得返回早于下界的未执行出现：线性推进会从该早节点
            // 依次穿越中途**已完成**出现（含锚及其后节点）并重复提交（不变量①）。停驻点之前的
            // 新插节点不在此路径承载（其重驱义务由 C11 重驱合同覆盖，属接线批语义面）。
""",
"""            // [Wave1 R18 必改-1；BO-6/7-D1 修订] 下界：锚候选非 null ⇒ 候选本身；candidate 为 null
            // （锚在链尾）⇒ **计划全序最早**的可定位有效停驻点（[R34 重要-F5] 口径统一；不再取追加序
            // 最后者，该口径会漏掉低轮次的有效停驻）。探针不得返回早于下界的未执行出现：早于锚候选的
            // 出现按修订语义由 candidate 自身或其后续承载（不变量②的前插保全只在停驻同轮次内成立）。
            // 停驻点之前的新插节点不在此路径承载（其重驱义务由 C11 重驱合同覆盖，属接线批语义面）。
            // 原「驱动推进段穿越已完成出现并重复提交」的历史理由已由驱动推进层的稳定身份过滤消除
            // （BO-8/BO-9：见 DriveAsync 的 HasCompletedOutcome 调用点与 TryRelocateToLivePark）。
""")

add(
"""    /// 无可定位停驻 ⇒ null（调用方决定链首/链尾）。取最后一条可定位停驻标记为基准；
""",
"""    /// 无可定位停驻 ⇒ null（调用方决定链首/链尾）。取**计划全序最早**的可定位停驻标记为基准
    /// （[Wave1 R25 重要-1／R34 重要-F5] 口径统一）；
""")

add(
"""                // [Wave1 R18 必改-1] **下界约束**：锚可定位路径下，探针结果不得早于锚候选——
                // 否则救援返回的早节点完成后，驱动循环按线性推进（DriveAsync/Relocate 无完成跳过）
                // 会穿越中途的**已完成**出现并重复提交（外部副作用二次发生），违反不变量①。
                // 早于下界的未执行出现由「锚可定位路径的 candidate」自身或其后续承载（修订语义内）。
""",
"""                // [Wave1 R18 必改-1；BO-6/7-D1 修订] **下界约束**：锚可定位路径下，探针结果不得
                // 早于锚候选——否则会越过修订语义下由 candidate 承载的出现（不变量②的前插保全
                // 只在停驻同轮次内成立）。早于下界的未执行出现由「锚可定位路径的 candidate」自身
                // 或其后续承载（修订语义内）。驱动推进段对已完成出现已有稳定身份过滤（BO-8/BO-9），
                // 本下界不再承担「防止穿越已完成出现」的职责。
""")

add(
"""    /// ①**不重跑已完成出现**——返回点本身先过滤完成结果（R12 重要-1）；BO-6 为有停驻历史的恢复推进
    /// 增加同身份完成过滤。普通修订推进的完整过滤仍归 BO-8，本批不扩展该范围；
""",
"""    /// ①**不重跑已完成出现**——返回点本身先过滤完成结果（R12 重要-1）；驱动推进层对**整段推进**
    /// 按稳定出现身份跳过已完成项（[BO-8] DriveAsync 完成过滤），真实链尾仍存活的停驻义务按计划
    /// 全序重入（[BO-9] TryRelocateToLivePark），故本函数只需保证**返回点**未完成且不吞停驻义务；
""")

add(
"""    /// 若恢复点越过已完成出现，由 BO-6 驱动层在推进时过滤这些完成身份。
""",
"""    /// 若恢复点越过已完成出现，由驱动推进层在推进时按稳定身份过滤这些完成出现（BO-6／BO-8）。
""")

add(
"""            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；BO-6 驱动层会过滤其后已完成出现，
""",
"""            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；驱动推进层会过滤其后已完成出现
            // （BO-6／BO-8，覆盖整段推进），
""")

for old, new in edits:
    n = t.count(old)
    if n != 1:
        print("PATTERN COUNT", n, repr(old[:90]))
        sys.exit(1)
    t = t.replace(old, new)

io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("edits applied:", len(edits), "bytes:", before_len, "->", len(t))
