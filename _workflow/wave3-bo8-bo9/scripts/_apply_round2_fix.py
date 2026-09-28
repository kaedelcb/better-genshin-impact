import io
p = r"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs"
t = io.open(p, encoding="utf-8").read()
old = """            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkOcc)) continue;
            Consider(parkOcc);
"""
new = """            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkOcc)) continue;
            // [第 2 轮会诊 IMPORTANT-2] **已清偿（同身份已有完成结果）的停驻标记不再承载任何义务**：
            // 它既不是重入点，也**不得**为它扫描同轮前插未执行出现——否则会凭一条旧标记额外执行该轮更早的
            // 未执行节点（反例：`[A,P,T]`→`[A,X,P,T]`，P 已由停驻转为完成，旧标记仍会引出 X 的额外提交）。
            if (HasCompletedOutcome(run, parkOcc)) continue;
            Consider(parkOcc);
"""
assert t.count(old) == 1
t = t.replace(old, new)
old2 = """    /// ②每个存活停驻**同轮次**、序号更早的**从未执行**出现（R12 建议-1／R14 F1 的「不静默跳过未执行节点」口径：
    ///   停驻义务被重驱时，其所在轮次在该停驻之前的未执行节点必须先被驱动）。"""
new2 = """    /// ②每个存活停驻**同轮次**、序号更早的**从未执行**出现（R12 建议-1／R14 F1 的「不静默跳过未执行节点」口径：
    ///   停驻义务被重驱时，其所在轮次在该停驻之前的未执行节点必须先被驱动）。
    /// **前置过滤（第 2 轮会诊 IMPORTANT-2）**：已清偿（同身份已有完成结果）的停驻标记先被剔除，
    ///   既不作重入点也不产生前插义务——否则旧标记会额外执行其同轮更早的未执行节点。"""
assert t.count(old2) == 1
t = t.replace(old2, new2)
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("guard added")
