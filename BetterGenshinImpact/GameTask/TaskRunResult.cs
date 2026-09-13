namespace BetterGenshinImpact.GameTask;

/// <summary>
/// [A1.1 统一作业注册表] TaskRunner 执行的显式结果。
/// 旧语义：抢锁失败仅一行 ERR 日志后静默返回，调用方无法区分"跑了"与"没跑"——
/// 这是 task.resume 静默丢失、一条龙条目静默跳过、批次组被并发入口撞死等事故的共同根因。
/// 注意：执行内部的成功/取消/异常仍沿用 RunCurrentAsync 既有 catch 分层，不在本枚举展开。
/// </summary>
public enum TaskRunResult
{
    /// <summary>拿到任务槽位并执行完毕（含执行体内部被取消/异常，与旧语义一致）。</summary>
    Ran,

    /// <summary>任务槽位被占用，本次未执行（旧行为：仅 ERR 日志后静默返回）。</summary>
    RejectedSlotBusy,
}
