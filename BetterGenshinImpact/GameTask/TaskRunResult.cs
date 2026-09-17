namespace BetterGenshinImpact.GameTask;

/// <summary>
/// [A1.1 统一作业注册表] TaskRunner 执行的显式结果。
/// 旧语义：抢锁失败仅一行 ERR 日志后静默返回，调用方无法区分"跑了"与"没跑"——
/// 这是 task.resume 静默丢失、一条龙条目静默跳过、批次组被并发入口撞死等事故的共同根因。
/// 拒绝、让位、失败与取消必须传回父流程；只有 Ran 可作为成功叶子参与汇总。
/// </summary>
public enum TaskRunResult
{
    /// <summary>执行体完成，且未观察到失败、取消或让位。</summary>
    Ran,

    /// <summary>任务槽位被占用，本次未执行（旧行为：仅 ERR 日志后静默返回）。</summary>
    RejectedSlotBusy,

    /// <summary>所属根运行已让位；不是用户停止，也不是成功。</summary>
    Preempted,
    /// <summary>执行或必要前置步骤失败。</summary>
    Failed,
    /// <summary>执行被取消，原因由根运行/注册表记录。</summary>
    Cancelled,
}
