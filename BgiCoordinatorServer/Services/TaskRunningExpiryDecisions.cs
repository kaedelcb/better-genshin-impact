namespace BgiCoordinatorServer.Services;

/// <summary>
/// 服务端 TaskRunning 过期判定的纯函数（PBT 友好）。
/// 对应 spec `task-status-model-redesign` PBT-B 与 BC-5/BC-6 超时自愈逻辑。
/// </summary>
public static class TaskRunningExpiryDecisions
{
    /// <summary>
    /// 是否为"任务运行态已超时、应复位为未运行"。
    /// </summary>
    /// <param name="isTaskRunning">该成员当前 TaskRunning 是否为 true。</param>
    /// <param name="nowTicks">当前 UTC 时间的 Ticks（DateTime.UtcNow.Ticks）。</param>
    /// <param name="expireTicks">TaskRunningExpireTime 的 Ticks（DateTime.MinValue 时传 0）。</param>
    /// <returns>true = 已超时应复位；false = 未超时或本就不在运行。</returns>
    /// <remarks>
    /// 规则：仅当 TaskRunning=true 且 expireTicks>0（表示已设过期时间，非 MinValue）且 now > expire 时，为已超时。
    /// DateTime.MinValue == 0 Ticks，故 expireTicks==0 表示未设置过期 → 不判超时。
    /// </remarks>
    public static bool ShouldResetTaskRunning(bool isTaskRunning, long nowTicks, long expireTicks)
        => isTaskRunning && expireTicks > 0 && nowTicks > expireTicks;
}