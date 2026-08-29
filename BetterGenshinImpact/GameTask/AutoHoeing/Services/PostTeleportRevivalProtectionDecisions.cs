using System;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 传送后复苏保护纯决策层（PBT 友好，无副作用）。
/// 识别联机明确传送同步点严格等待完成后、10 秒窗口内的本机复苏，命中后仅重跑当前路线段。
/// 详见 .kiro/specs/multiplayer-hoeing-post-teleport-revival-protection/design.md §1。
/// </summary>
public static class PostTeleportRevivalProtectionDecisions
{
    /// <summary>
    /// 传送后复苏保护窗口时长（秒）。窗口起点是匹配的 <c>AllArrived</c> 被严格等待消费并完成的时刻。
    /// 保留为复苏窗口（向后兼容，不破坏既有引用）。
    /// </summary>
    public const double WindowSeconds = 10;

    /// <summary>
    /// 传送后卡死保护窗口时长（秒）。触发类型 <c>"stuck"</c> 使用该窗口，其余（含 <c>"revival"</c>）使用 <see cref="WindowSeconds"/>。
    /// </summary>
    public const double StuckWindowSeconds = 30;

    /// <summary>
    /// 按触发类型取传送后保护窗口时长（秒）。<c>"stuck"</c> 返回 <see cref="StuckWindowSeconds"/>（20），
    /// 其他触发类型（含 <c>"revival"</c>）返回 <see cref="WindowSeconds"/>（10）。
    /// </summary>
    /// <param name="triggerType">一次传送后保护候选事件的类型（<c>"stuck"</c> 或 <c>"revival"</c>）。</param>
    /// <returns>对应触发类型的保护窗口时长（秒）。</returns>
    public static double GetWindowSeconds(string triggerType)
        => triggerType == "stuck" ? StuckWindowSeconds : WindowSeconds;

    /// <summary>
    /// 判断一次保护候选事件（复苏或卡死）是否属于传送后保护命中（统一入口，按 <paramref name="triggerType"/> 参数化窗口）。
    /// </summary>
    /// <param name="triggerType">一次传送后保护候选事件的类型（<c>"stuck"</c> 或 <c>"revival"</c>）。</param>
    /// <param name="isMultiplayerHoeing">是否联机锄地模式。</param>
    /// <param name="syncPointType">同步点类型（明确传送同步点为 "teleport"）。</param>
    /// <param name="strictWaitCompleted">严格等待是否已完成（收到匹配的 <c>AllArrived</c>）。</param>
    /// <param name="completionTime">严格等待完成时刻。</param>
    /// <param name="triggerTime">本机触发事件（复苏或卡死）时刻。</param>
    /// <param name="consumed">当前段保护机会是否已消耗。</param>
    /// <returns>命中返回 <c>true</c>；否则 <c>false</c>。</returns>
    public static bool IsEligible(
        string triggerType,
        bool isMultiplayerHoeing,
        string? syncPointType,
        bool strictWaitCompleted,
        DateTime completionTime,
        DateTime triggerTime,
        bool consumed)
    {
        if (!isMultiplayerHoeing || syncPointType != "teleport") return false;
        if (!strictWaitCompleted || consumed) return false;
        var elapsed = (triggerTime - completionTime).TotalSeconds;
        var window = GetWindowSeconds(triggerType);
        return elapsed >= 0 && elapsed <= window;
    }

    /// <summary>
    /// 判断一次复苏事件是否属于传送后复苏保护命中（既有复苏专用入口，内部委托到统一入口，触发类型为 <c>"revival"</c>）。
    /// 保留该调用形态，保证既有复苏调用方逐字节不变（回归防护）。
    /// </summary>
    /// <param name="isMultiplayerHoeing">是否联机锄地模式。</param>
    /// <param name="syncPointType">同步点类型（明确传送同步点为 "teleport"）。</param>
    /// <param name="strictWaitCompleted">严格等待是否已完成（收到匹配的 <c>AllArrived</c>）。</param>
    /// <param name="completionTime">严格等待完成时刻。</param>
    /// <param name="revivalTime">本机复苏时刻。</param>
    /// <param name="consumed">当前段保护机会是否已消耗。</param>
    /// <returns>命中返回 <c>true</c>；否则 <c>false</c>。</returns>
    public static bool IsEligible(
        bool isMultiplayerHoeing,
        string? syncPointType,
        bool strictWaitCompleted,
        DateTime completionTime,
        DateTime revivalTime,
        bool consumed)
        => IsEligible("revival", isMultiplayerHoeing, syncPointType,
            strictWaitCompleted, completionTime, revivalTime, consumed);

    /// <summary>
    /// 原子消耗当前段保护机会（CAS，一次性消费）。同一段至多一个事件取得资格。
    /// </summary>
    /// <param name="consumed">机会消费状态（0 未消费，1 已消费）。</param>
    /// <returns>调用方赢得本次消费返回 <c>true</c>；否则 <c>false</c>。</returns>
    public static bool TryConsume(ref int consumed)
        => Interlocked.CompareExchange(ref consumed, 1, 0) == 0;

    /// <summary>
    /// 计算当前段起点进度（段内 offset 0 即段起点，不推进到下一段）。
    /// </summary>
    /// <param name="segmentStartIndex">当前段索引（<c>CurWaypoints.Item1</c>，类型为 <c>int</c>）。</param>
    /// <param name="offset">段内 waypoint 偏移。</param>
    /// <returns>当前段起点目标进度值。</returns>
    public static double ComputeProgress(int segmentStartIndex, int offset)
        => segmentStartIndex + offset;
}