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
    /// 是否"本次未正常完成"。
    ///
    /// 判定顺序（hoeing-guard-false-restart-on-normal-close）：
    ///   防线 B（治本）completedNormally == true  → 恒 false（任务已声明跑完）
    ///   partyFailed == true                      → 只要 stopReason 非空即判未完成
    ///     （组队失败时 _guardPlannedRouteCount 恒为 0，未执行线路数为 0 会触发防线 A
    ///       误短路，故组队失败需显式豁免防线 A，hoeing-multiplayer-party-fail-restart）
    ///   防线 A（治标）unexecutedCount == 0       → 恒 false（一条线路都没落下，铁证）
    ///   否则回落原逻辑：stopReason 非空 ∨ 未执行数达阈值
    ///
    /// 为何不解析 stopReason 文字：正常收尾关房与房主掉线关房都产生
    /// "房间已关闭: ..."，文字无法区分二者（bugfix.md §2.3）。
    /// 两道防线在房主中途掉线场景均不成立（仍有未执行线路 + 走不到完成点），
    /// 故 EB-3"真异常照常重开"不受影响。
    ///
    /// completedNormally / partyFailed 默认 false：保证未显式传参的既有调用方行为与改动前一致。
    /// </summary>
    public static bool IsIncompleteRun(
        string? stopReason, int unexecutedCount, int threshold,
        bool completedNormally = false, bool partyFailed = false)
    {
        if (completedNormally) return false;    // 防线 B（治本）：任务已到达正常完成点
        if (partyFailed) return !string.IsNullOrEmpty(stopReason); // 组队失败：豁免防线 A
        if (unexecutedCount == 0) return false; // 防线 A（治标）：计划线路全部执行完毕
        return !string.IsNullOrEmpty(stopReason) || unexecutedCount >= threshold;
    }

    /// <summary>
    /// 是否应触发守护重开（R2.3 全部条件）。任一不满足即 false。
    /// - guardMode：守护开关（成员为房主下发后的本地值）
    /// - multiplayerEnabled：单机零感知（P1）
    /// - stopReason / unexecutedCount / threshold：异常退出或未执行达阈值（条件2）
    /// - userCancelled：原始外部 ct 是否被取消（手动停止 → 不重开，条件3）
    /// - expCapStopTriggered：经验上限正常停止 → 不重开（条件4）
    /// - isGuardRestartRun：本次是否已是守护重开产生的运行 → 不再重开（条件5，次数上限 1）
    /// - completedNormally：本次是否到达过正常完成点 → 不重开（防线 B，
    ///   hoeing-guard-false-restart-on-normal-close）。默认 false 保证既有调用方零变化。
    /// - partyFailed：本次组队阶段是否失败 → 豁免防线 A 的"未执行数=0"短路（hoeing-multiplayer-party-fail-restart）。
    ///   组队失败时尚未开锄，计划/已执行线路数均为 0，防线 A 会把组队失败误判为"全跑完了"，
    ///   故由调用方在组队失败路径显式传 true，使"只要 stopReason 非空即重开"成立。
    ///   默认 false 保证既有调用方零变化。
    /// </summary>
    public static bool ShouldRestart(
        bool guardMode,
        bool multiplayerEnabled,
        string? stopReason,
        int unexecutedCount,
        int threshold,
        bool userCancelled,
        bool expCapStopTriggered,
        bool isGuardRestartRun,
        bool completedNormally = false,
        bool partyFailed = false)
    {
        if (!guardMode) return false;              // 条件1
        if (!multiplayerEnabled) return false;     // P1 单机零感知
        if (userCancelled) return false;           // 条件3 手动停止
        if (expCapStopTriggered) return false;     // 条件4 经验上限正常停止
        if (isGuardRestartRun) return false;       // 条件5 重开只一次
        return IsIncompleteRun(stopReason, unexecutedCount, threshold, completedNormally, partyFailed); // 条件2
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
