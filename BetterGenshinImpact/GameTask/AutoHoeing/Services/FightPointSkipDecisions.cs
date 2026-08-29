using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 联机"同伴战斗点级跳过"决策（纯函数，PBT 友好，无外部依赖）。
/// hoeing-multiplayer-route-retry-mode spec §9（v3）。
/// 战斗点编号 = segIdx * 10000 + wpIdx（复用现有 tpMapKey = segIdx*10000+wpIdx 约定）。
/// </summary>
public static class FightPointSkipDecisions
{
    /// <summary>
    /// 把 (段索引, 段内 waypoint 索引) 编码为单个 int 战斗点编号。
    /// 约定与 PathExecutor._syncPointMap 的键一致：segIdx*10000 + wpIdx。
    /// </summary>
    public static int Encode(int segIdx, int wpIdx) => segIdx * 10000 + wpIdx;

    /// <summary>
    /// 是否应记录"待跳过战斗点"（情况3：自己尚未到达复苏战斗点）。
    /// - fightPointId == -1 → false（无待跳过点）
    /// - fightPointId 的段索引 &lt; 当前段 → false（该点已越过，情况2）
    /// - 否则 true（未越过，走到时才消费）
    /// </summary>
    public static bool ShouldRecordPendingSkip(int fightPointId, int currentSegIdx)
        => fightPointId != -1 && (fightPointId / 10000) >= currentSegIdx;

    /// <summary>
    /// 是否命中待跳过战斗点（走到该点时才跳过）。
    /// fightPointId 有效且等于当前战斗点编号即命中。
    /// </summary>
    public static bool IsMatch(int fightPointId, int curFp)
        => fightPointId != -1 && fightPointId == curFp;
}

