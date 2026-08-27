namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>
/// 任务运行状态判定纯函数。
/// 由 spec `task-status-model-redesign` 定义，用于验证 P1 修复（running 由 TaskSemaphore 权威判断，不再依赖 taskName 残留）。
/// 本类在测试项目中定义，仅在测试中验证逻辑；BGI 生产代码直接使用 <c>TaskControl.TaskSemaphore.CurrentCount == 0</c>。
/// </summary>
public static class TaskRunningDecisions
{
    /// <summary>
    /// 判断当前是否有任务在运行。
    /// </summary>
    /// <param name="semaphoreBusy">单任务锁是否被持有（TaskSemaphore.CurrentCount == 0）。</param>
    /// <returns>true = 有任务在跑；false = 空闲。</returns>
    /// <remarks>
    /// 这是本 spec 修复的核心原则：running 只由锁信号决定，不再依赖 taskName 是否残留 + isCancelled。
    /// 旧逻辑 `running = !string.IsNullOrEmpty(taskName) && !isCancelled` 会在任务正常结束且 taskName 残留时误判为 true。
    /// </remarks>
    public static bool IsTaskRunning(bool semaphoreBusy) => semaphoreBusy;
}