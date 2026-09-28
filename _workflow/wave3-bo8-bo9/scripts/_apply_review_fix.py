import io, sys
p = r"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs"
t = io.open(p, encoding="utf-8").read()
edits = []

# 1) DriveAsync tail branch: use the rebuilt-obligation helper and soften the termination wording
edits.append((
"""                if (occurrence is null)
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
""",
"""                if (occurrence is null)
                {
                    // [BO-9 / R34 F5 重要；第 1 轮会诊 IMPORTANT-1 修复] 真实链尾：线性推进
                    // （plan.Next）到不了本链的更晚义务——无循环定义时既不回绕到更高轮次停驻
                    // （如 A@loop2），也到不了 RecomputeSuccessor 已选中但尚未履行的 rescue
                    // （如 [A,P] + A@0 完成 + P@1/P@2 停驻 ⇒ rescue A@1）。按计划全序重建并重入
                    // 最早的未履行恢复义务，避免「零发送义务/未执行出现被静默吞」后假成功。
                    // 终止性：每次重入都驱动该出现一次（完成或被过滤 ⇒ 离开义务集合；再次停驻或
                    // unknown ⇒ 驱动立即返回），义务集合严格收缩；重入后若经暂停/取消/修订边界返回，
                    // 驱动本身结束，不构成自旋。入口即链尾（持久 TailReached）不由本路径重开。
                    if (!enteredAtTail && TryRelocateToOutstandingObligation(run, plan, out var obligation))
                    {
                        Log(run, $"链尾仍存未履行的恢复义务 {obligation!.NodeId}#{obligation.Occurrence}"
                                 + $"（轮次 {obligation.LoopIteration}）：按计划全序重入重驱，不按链尾放行。");
                        occurrence = obligation;
                        ApplyRelocation(run, occurrence);
                        _runs.Update(run);
                        continue;
                    }
                    break; // 链尾（TailReached 已落盘）
                }
"""))

# 2) Replace the helper with the obligation-rebuilding version
edits.append((
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
""",
"""    /// <summary>
    /// **[BO-9 / R34 F5 重要；第 1 轮会诊 IMPORTANT-1 修复]** 真实链尾处的**未履行恢复义务**重建点：
    /// 返回计划全序最早的一条未履行义务出现；没有时返回 false。义务集合 =
    /// ①仍存活（可在当前修订定位、且无完成结果）的停驻出现——与 <see cref="ParkedRescue"/> 及 tailBound 同口径；
    /// ②每个存活停驻**同轮次**、序号更早的**从未执行**出现（R12 建议-1／R14 F1 的「不静默跳过未执行节点」口径：
    ///   停驻义务被重驱时，其所在轮次在该停驻之前的未执行节点必须先被驱动）。
    ///
    /// **为何必须在链尾重建**：`RecomputeSuccessor` 在 candidate 早于 rescue 时返回 candidate 并依赖**线性推进**
    /// 自然到达 rescue（R24）；无循环定义时 `plan.Next` 走到链尾即 null，该依赖不成立 ⇒ rescue 义务被丢。
    /// 反例（第 1 轮会诊 IMPORTANT-1）：计划 `[A,P]`（无循环）+ `A@0` 完成 + `P@1/P@2` 停驻 ⇒
    /// `candidate=P@0`、`rescue=A@1`（同轮前插未执行出现）⇒ 只重入停驻点会让 `A@1` 永不执行而运行假成功。
    ///
    /// **入口即链尾**（持久 `TailReached`）的持久记录由调用方守卫，不在本方法内重开（BO-6 防御语义）。
    /// 判据只用稳定出现身份 `(NodeId, Occurrence, LoopIteration)`＋「无完成结果」：停驻标记本身不算完成；
    /// 过期标记（同身份已补上完成结果）不参与重入。
    /// </summary>
    private static bool TryRelocateToOutstandingObligation(WorkflowRunRecord run, WorkflowPlan plan,
        out WorkflowNodeOccurrence? obligation)
    {
        WorkflowNodeOccurrence? earliest = null;
        void Consider(WorkflowNodeOccurrence candidate)
        {
            if (HasCompletedOutcome(run, candidate)) return;
            if (earliest is null
                || candidate.LoopIteration < earliest.LoopIteration
                || (candidate.LoopIteration == earliest.LoopIteration
                    && candidate.SequenceIndex < earliest.SequenceIndex))
            {
                earliest = candidate;
            }
        }
        foreach (var parked in run.NodeOutcomes.Where(o => o.Result == LocalWaitResultWord))
        {
            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkOcc)) continue;
            Consider(parkOcc);
            // 同轮次、该停驻之前的未执行出现（与 ParkedRescue 的探针同口径；序号严格递增，不越轮）。
            for (var probe = plan.FirstOccurrence() is { } head
                     ? new WorkflowNodeOccurrence(head.NodeId, head.SequenceIndex, head.Occurrence, parkOcc.LoopIteration)
                     : null;
                 probe is not null && probe.SequenceIndex < parkOcc.SequenceIndex;
                 probe = plan.Next(probe))
            {
                Consider(probe);
            }
        }
        obligation = earliest;
        return earliest is not null;
    }
"""))

# 3) finding-2 comment fixes (suggestion level)
edits.append((
"""    /// 若其之前（**同一轮次内**）存在从未执行的出现，返回更早者（不静默跳过未执行节点，
    /// R12 建议-1／R14 F1：探针从**该停驻所在轮次的链首**起步——由基准沿 Next 反向不可行，
    /// 改为从链首逐轮推进到目标轮次，避免 R14 F1 的「锁死第 0 轮」缺陷）。
""",
"""    /// 若其之前（**同一轮次内**）存在从未执行的出现，返回更早者（不静默跳过未执行节点，
    /// R12 建议-1／R14 F1：探针**直接构造该停驻所在轮次的链首出现**再向前探查——由基准沿 Next
    /// 反向不可行，逐轮推进又会锁死在第 0 轮，故取「同轮链首 + 序号上界」的构造口径）。
"""))

edits.append((
"""            // [Wave1 R24 重要-2] rescue 与 candidate 都非 null 时按**计划全序取较早者**：
            // 线性推进（从返回点沿 Next 走到链尾再回绕）保证较晚者自然到达（未完成 ⇒ 会被执行），
            // 两个义务都不丢。固定 rescue 覆盖 candidate 的旧形态（R24 反例：修订在锚后插入 C@0、
            // 停驻在更晚轮次 A@1 ⇒ 旧代码返回 A@1 ⇒ C@0 永不进 NodeOutcomes ⇒ 假成功丢步）。
""",
"""            // [Wave1 R24 重要-2；第 1 轮会诊建议-2 修订] rescue 与 candidate 都非 null 时按**计划全序取较早者**：
            // **有循环定义**时线性推进回绕到下一轮，较晚者自然到达（未完成 ⇒ 会被执行）；
            // **无循环定义**时 `plan.Next` 到链尾即 null，较晚者**不会**自然到达——该未履行义务由
            // DriveAsync 的链尾重入（TryRelocateToOutstandingObligation）按计划全序重建并重驱
            // （BO-9；第 1 轮会诊 IMPORTANT-1 反例 `[A,P]` + rescue `A@1` 即此形态）。
            // 固定 rescue 覆盖 candidate 的旧形态（R24 反例：修订在锚后插入 C@0、停驻在更晚轮次 A@1
            // ⇒ 旧代码返回 A@1 ⇒ C@0 永不进 NodeOutcomes ⇒ 假成功丢步）。
"""))

for old, new in edits:
    n = t.count(old)
    if n != 1:
        print("PATTERN COUNT", n, repr(old[:80])); sys.exit(1)
    t = t.replace(old, new)
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("applied", len(edits), "edits")
