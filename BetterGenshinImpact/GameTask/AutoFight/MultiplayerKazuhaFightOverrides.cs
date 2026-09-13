using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 联机锄地万叶玩家战斗参数覆盖（决策 + 副作用入口）。
/// <para>
/// - <see cref="ClampFightWaitNotEndTime"/> / <see cref="ClampFastCheckDelay"/> 是纯函数，PBT 友好。
/// - <see cref="Apply"/> 是副作用入口，承担 10 项写入并返回日志友好的钳制结果结构。
/// </para>
/// <para>
/// 详见 .kiro/specs/multiplayer-kazuha-fixed-fight-overrides/design.md §1。
/// </para>
/// </summary>
public static class MultiplayerKazuhaFightOverrides
{
    /// <summary>"战斗前不检测结束的时间（毫秒）"下限。低于该值则用此值。</summary>
    public const int FightWaitNotEndTimeFloor = 1000;

    /// <summary>"派蒙模式检查延时（秒）"下限。低于该值则用此值。</summary>
    public const double FastCheckDelayFloor = 0.08;

    /// <summary>"派蒙模式检查延时（秒）"上限。高于该值则用此值。</summary>
    public const double FastCheckDelayCeiling = 0.4;

    /// <summary>
    /// 钳制 FightWaitNotEndTime 到下限以上。
    /// <c>player &gt; 1000</c> → 返回 player；否则返回 1000。
    /// 等价于 <c>Math.Max(player, 1000)</c>。
    /// </summary>
    public static int ClampFightWaitNotEndTime(int playerValue)
        => Math.Max(playerValue, FightWaitNotEndTimeFloor);

    /// <summary>
    /// 钳制 FastCheckDelay 到闭区间 [0.08, 0.4]。
    /// <c>player &lt; 0.08</c>（含 NaN / 负数 / 0）→ 返回 0.08；
    /// <c>player &gt; 0.4</c>（含 +∞）→ 返回 0.4；
    /// 区间内 → 返回 player。
    /// 注意：.NET <c>Math.Clamp(double.NaN, lo, hi)</c> 返回 NaN（IEEE 754），所以单独显式 NaN 兜底到下限。
    /// </summary>
    public static double ClampFastCheckDelay(double playerValue)
    {
        if (double.IsNaN(playerValue)) return FastCheckDelayFloor;
        return Math.Clamp(playerValue, FastCheckDelayFloor, FastCheckDelayCeiling);
    }

    /// <summary>
    /// 在联机万叶路径下对 <paramref name="taskParams"/> 应用 10 项覆盖。
    /// 调用方负责保证仅在 (MultiplayerFightTimeoutOverride.HasValue &amp;&amp; IsCurrentPlayerKazuha) 双信号成立时调用。
    /// </summary>
    /// <param name="taskParams">已构造完成的 AutoFightParam 实例（字段已从 AutoFightConfig 拷贝）。</param>
    /// <returns>钳制结果结构，供调用方输出日志使用。</returns>
    public static KazuhaFightOverrideResult Apply(AutoFightParam taskParams)
    {
        if (taskParams is null) throw new ArgumentNullException(nameof(taskParams));

        // 8 项固定值
        taskParams.FinishDetectConfig.RotateFindEnemyEnabled = true;   // #1
        taskParams.RotaryFactor = 1;                                   // #2
        taskParams.CheckBeforeBurst = false;                           // #3
        taskParams.IsFirstCheck = false;                               // #4
        taskParams.FinishDetectConfig.GoDistance = 0;                  // #5
        taskParams.FinishDetectConfig.RotationMode = true;             // #6
        taskParams.FinishDetectConfig.EndModel = true;                 // #7
        taskParams.FinishDetectConfig.PaimonEndModel = true;           // #9

        // 2 项下限钳制
        var origFightWait = taskParams.FinishDetectConfig.FightWaitNotEndTime;
        var origFastCheck = taskParams.FinishDetectConfig.FastCheckDelay;
        var finalFightWait = ClampFightWaitNotEndTime(origFightWait);
        var finalFastCheck = ClampFastCheckDelay(origFastCheck);
        taskParams.FinishDetectConfig.FightWaitNotEndTime = finalFightWait;  // #8
        taskParams.FinishDetectConfig.FastCheckDelay = finalFastCheck;       // #10

        return new KazuhaFightOverrideResult
        {
            OriginalFightWaitNotEndTime = origFightWait,
            FinalFightWaitNotEndTime = finalFightWait,
            OriginalFastCheckDelay = origFastCheck,
            FinalFastCheckDelay = finalFastCheck,
        };
    }
}

/// <summary>
/// 联机万叶玩家战斗参数覆盖结果（仅含钳制字段的玩家原值 vs 最终值，供日志使用）。
/// </summary>
public readonly struct KazuhaFightOverrideResult
{
    public int OriginalFightWaitNotEndTime { get; init; }
    public int FinalFightWaitNotEndTime { get; init; }
    public double OriginalFastCheckDelay { get; init; }
    public double FinalFastCheckDelay { get; init; }
}
