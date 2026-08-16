namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// AutoFightSeek.SeekAndFightAsync 内部决策的纯函数集合，用于 PBT 守住决策语义。
/// 详见 .kiro/specs/multiplayer-kazuha-pre-cast-positioning/design.md §6 PBT-1。
/// </summary>
public static class AutoFightSeekDecisions
{
    /// <summary>
    /// 判定联机万叶玩家"持续回点"是否应触发。
    /// 仅在距离实时超阈值 ∧ 距上次回点完成已超过最小间隔时返回 true。
    /// </summary>
    public static bool ShouldTriggerContinuousReturn(
        double realtimeDistance,
        double returnDistanceThreshold,
        double elapsedMsSinceLastReturn,
        double returnIntervalMs)
    {
        return realtimeDistance > returnDistanceThreshold
               && elapsedMsSinceLastReturn >= returnIntervalMs;
    }

    /// <summary>
    /// 通用版回点配置合法性校验。仅当所有判据满足时返回 true。
    /// PBT 友好：无外部依赖，纯函数。
    /// 距离触发部分始终校验；时间触发部分仅当 timeTriggerEnabled && rotateFindEnemyEnabled 才校验。
    /// 详见 .kiro/specs/fight-return-to-point-revamp/design.md §2.2.1
    /// </summary>
    public static bool IsReturnToFightPointConfigValid(
        int intervalMs,
        double triggerDistance,
        double stopDistance,
        bool timeTriggerEnabled,
        bool rotateFindEnemyEnabled,
        int timeTriggerSeconds)
    {
        // 距离触发部分（始终校验）
        if (intervalMs <= 0) return false;
        if (!(triggerDistance > 0) || !(triggerDistance <= 150)) return false;
        if (!(stopDistance >= 0) || !(stopDistance <= 150)) return false;
        // 注意：不强制 stopDistance < triggerDistance；
        // 用户可设 trigger=8 / stop=15 的"宽松回点"语义：玩家 ≥ 8 触发但只需走回到 < 15 即停。

        // 时间触发部分（仅当两开关同时开启才校验）
        if (timeTriggerEnabled && rotateFindEnemyEnabled)
        {
            if (timeTriggerSeconds <= 0 || timeTriggerSeconds > 600) return false;
        }
        return true;
    }

    /// <summary>
    /// 通用版回点距离触发判据：当前距离 > 触发距离 ∧ 上次回点已过节流间隔。
    /// 详见 .kiro/specs/fight-return-to-point-revamp/design.md §2.2.2
    /// </summary>
    public static bool ShouldTriggerGeneralDistanceReturn(
        double realtimeDistance,
        double triggerDistance,
        double elapsedSinceLastReturnMs,
        int intervalMs)
    {
        if (!(triggerDistance > 0) || intervalMs <= 0) return false;
        return realtimeDistance > triggerDistance
               && elapsedSinceLastReturnMs >= intervalMs;
    }

    /// <summary>
    /// 通用版回点时间触发判据：连续 timeTriggerSeconds 秒未发现敌人 ∧ 上次回点已过节流间隔。
    /// 严格大于（&gt;）避免边界抖动；与 §Q7 T4 决议对齐。
    /// 必须 timeTriggerEnabled && rotateFindEnemyEnabled 同时为 true 才生效。
    /// 详见 .kiro/specs/fight-return-to-point-revamp/design.md §2.2.3
    /// </summary>
    public static bool ShouldTriggerTimeReturn(
        double elapsedSinceEnemySec,
        int timeTriggerSeconds,
        double elapsedSinceLastReturnMs,
        int intervalMs,
        bool timeTriggerEnabled,
        bool rotateFindEnemyEnabled)
    {
        if (!timeTriggerEnabled || !rotateFindEnemyEnabled) return false;
        if (timeTriggerSeconds <= 0 || intervalMs <= 0) return false;
        return elapsedSinceEnemySec > timeTriggerSeconds
               && elapsedSinceLastReturnMs >= intervalMs;
    }

    /// <summary>
    /// 判定回点后台循环是否应因"正在前往神像传送"而 return 终止本场回点循环。
    /// 命中后调用方应 return 退出循环（不是 continue 跳过一轮）——传送后角色必定不回战斗点，
    /// 本循环已无意义，回点能力由下一场战斗重新启动的新循环恢复。
    /// 纯函数：仅以 AutoFightTask.IsTeleportingToStatue 为输入，不读取任何用户主动暂停信号
    /// （IsSuspend / IsSuspendedByCapture）——死亡复苏是"游戏内事件需让路"，与用户主动暂停严格区分。
    /// 详见 .kiro/specs/return-to-point-suspend-during-revival-teleport/design.md Property 1 / 3。
    /// </summary>
    public static bool ShouldStopReturnForTeleport(bool teleportingToStatue)
    {
        return teleportingToStatue;
    }

    /// <summary>
    /// 判定 SeekAndFightAsync 寻敌旋转是否应跳过 MoveMouseBy（让位给回点的 RotateToApproach）。
    /// 语义：回点进行中（returnToPointInProgress=true）⇒ true（跳过）；否则 false（正常甩鼠标）。
    /// 纯函数：仅以"回点是否进行中"为输入，不依据是否万叶玩家、不依据 RotaryFactor。
    /// PBT 友好：无外部依赖。
    /// 详见 .kiro/specs/fight-return-to-point-seek-rotation-conflict-fix/design.md 改动 2 / Property 1。
    /// </summary>
    public static bool ShouldSkipSeekRotation(bool returnToPointInProgress)
    {
        return returnToPointInProgress;
    }

    /// <summary>
    /// 血条"足够近"高度阈值。MoveForwardAsync 以 height &gt; 该阈值判定"已足够近、停止靠近"。
    /// 联机锄地下放宽到 8（血条 7~8 像素仍继续靠近），单机保持 6。
    /// 纯函数：无外部依赖，便于 PBT。
    /// </summary>
    public static int GetNearHeightThreshold(bool isMultiplayerHoeing)
        => isMultiplayerHoeing ? 8 : 6;
}
