#nullable enable
using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机锄地守护自动重开的纯决策函数（hoeing-multiplayer-guard-auto-restart）。
/// 无外部依赖（不持有 client/logger/config 实例），供 PBT 直接撒输入验证。
/// </summary>
public static class HoeingGuardDecisions
{
    /// <summary>未执行线路数 = 计划应跑总数 − 已执行数，下限 0（防负）。</summary>
    public static int ComputeUnexecutedCount(int plannedTotal, int executedTotal)
        => Math.Max(0, plannedTotal - executedTotal);

    /// <summary>阈值 clamp：下限 1（R1.2）。</summary>
    public static int ClampThreshold(int v) => Math.Max(1, v);

    /// <summary>
    /// 是否"本次未正常完成"（R2.1 ∨ R2.2）：异常退出（stopReason 非空）或未执行数达阈值。
    /// </summary>
    public static bool IsIncompleteRun(string? stopReason, int unexecutedCount, int threshold)
        => !string.IsNullOrEmpty(stopReason) || unexecutedCount >= threshold;

    /// <summary>
    /// 是否应触发守护重开（R2.3 全部条件）。任一不满足即 false。
    /// - guardMode：守护开关（成员为房主下发后的本地值）
    /// - multiplayerEnabled：单机零感知（P1）
    /// - stopReason / unexecutedCount / threshold：异常退出或未执行达阈值（条件2）
    /// - userCancelled：原始外部 ct 是否被取消（手动停止 → 不重开，条件3）
    /// - expCapStopTriggered：经验上限正常停止 → 不重开（条件4）
    /// - isGuardRestartRun：本次是否已是守护重开产生的运行 → 不再重开（条件5，次数上限 1）
    /// </summary>
    public static bool ShouldRestart(
        bool guardMode,
        bool multiplayerEnabled,
        string? stopReason,
        int unexecutedCount,
        int threshold,
        bool userCancelled,
        bool expCapStopTriggered,
        bool isGuardRestartRun)
    {
        if (!guardMode) return false;              // 条件1
        if (!multiplayerEnabled) return false;     // P1 单机零感知
        if (userCancelled) return false;           // 条件3 手动停止
        if (expCapStopTriggered) return false;     // 条件4 经验上限正常停止
        if (isGuardRestartRun) return false;       // 条件5 重开只一次
        return IsIncompleteRun(stopReason, unexecutedCount, threshold); // 条件2
    }

    /// <summary>
    /// 执行期队友掉线检测的基准更新 + 触发判定（R5，hoeing-multiplayer-guard-auto-restart §10）。
    /// 纯函数无副作用。输入本帧观测人数 currentCount 与已保存基准 baseline（-1 表示未捕获），
    /// 返回新基准与是否"低于基准"（below）。below 由调用方连续 2 次去抖后才触发协同中止。
    /// 语义：
    /// - baseline &lt; 0：首帧捕获，newBaseline = currentCount，below = false。
    /// - currentCount &gt; baseline：迟到者加入 / 列表滞后补齐，抬高基准，below = false。
    /// - currentCount &lt; baseline：疑似掉线，基准不变，below = true。
    /// - 相等：基准不变，below = false。
    /// 仅在调用方判定为"干净执行帧"（非组队/轮换/换角色/吃药/传送/暂停）时调用。
    /// </summary>
    public static (int newBaseline, bool below) UpdatePeerBaseline(int baseline, int currentCount)
    {
        if (baseline < 0) return (currentCount, false);
        if (currentCount > baseline) return (currentCount, false);
        if (currentCount < baseline) return (baseline, true);
        return (baseline, false);
    }
}
