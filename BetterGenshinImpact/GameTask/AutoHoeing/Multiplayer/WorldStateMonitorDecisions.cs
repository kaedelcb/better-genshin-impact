#nullable enable
using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// CheckOnceAsync 一帧的决策结果分类。决定调用方应走哪条分支。
/// </summary>
public enum WorldStateDecisionKind
{
    /// <summary>暂停态最高优先级：清零计数 + 恢复窗口，跳过本轮（对应 IsSuspend 分支）。</summary>
    Suspend,
    /// <summary>截图/识别抛异常：走 _consecutiveScreenshotFailures 分支，不计被踢出。</summary>
    ScreenshotFailed,
    /// <summary>IsInMultiGame=true：一切正常，清零 _connectedButNotInGame（step 2）。</summary>
    NormalInGame,
    /// <summary>组队 / 传送抑制期内 / 轮次切换：忽略 IsInMultiGame=false，不累计（step 3a/3b/3c）。</summary>
    Ignore,
    /// <summary>墙钟超时兜底强制解除抑制：解除 + 清零计数（step 0，OQ-2）。本帧之后回到累计/恢复链。</summary>
    ForceClearSuppression,
    /// <summary>IsInMultiGame=false + SignalR 正常，累计未达阈值：判瞬态干扰（step 4 非终止分支）。</summary>
    AccumulateTransient,
    /// <summary>IsInMultiGame=false + SignalR 正常，累计达阈值：判定被踢出，调 ConfirmExitAsync（step 4 终止分支）。</summary>
    JudgeKickedOut,
    /// <summary>IsInMultiGame=false + SignalR 断开：进入/继续恢复窗口（step 5，细节仍由 CheckOnceAsync 处理）。</summary>
    RecoveryWindow,
}

/// <summary>
/// CheckOnceAsync 一帧的判定输入快照。所有字段为值类型，便于 FsCheck 撒真值表。
/// </summary>
public readonly record struct MonitorState(
    bool TeleportSuppressed,        // _isTeleportSuppressed（已含 Begin 调用、End 未调）
    int MsSinceSuppression,         // UtcNow - _teleportSuppressionStart（毫秒）
    int TimeoutSeconds,             // TeleportSuppressionTimeoutSeconds（修复后=40）
    bool IsInMultiGame,             // 截图识别结果
    bool SignalrConnected,          // _client.IsConnected
    int ConnectedNotInGameCount,    // 进入本帧前的 _connectedButNotInGame
    int Threshold,                  // ConnectedNotInGameThreshold（=7）
    bool IsRoundSwitching,          // _isRoundSwitching
    bool IsPartyPhase,              // IsPartyPhase
    bool IsSuspend,                 // RunnerContext.Instance.IsSuspend
    bool ScreenshotThrew,           // 截图/识别抛异常
    bool IsRoleSwitching,           // _isRoleSwitching（按线路换角色抑制中）
    bool IsEatingMedicine);         // _isEatingMedicine（按周期吃食物抑制中）

/// <summary>
/// 决策结果：分类 + 是否应清零 _connectedButNotInGame。
/// </summary>
public readonly record struct WorldStateDecision(
    WorldStateDecisionKind Kind,
    bool ResetConnectedCount);

/// <summary>
/// WorldStateMonitor.CheckOnceAsync 的纯决策函数。不持有 client / logger / 截图，
/// 便于 FsCheck PBT 直接撒 MonitorState 真值表验证 Fix / Preservation 双向性质。
/// 决策优先级严格对齐 CheckOnceAsync 早返回链：
///   Suspend > ScreenshotThrew > ForceClearSuppression(墙钟超时) > NormalInGame
///   > PartyPhase > 抑制期 > 轮次切换/换角色 > 信号融合(累计/被踢出) > 恢复窗口
/// 详见 .kiro/specs/world-state-monitor-teleport-suppression-premature-expiry-fix/design.md §Correctness Properties。
/// </summary>
public static class WorldStateMonitorDecisions
{
    public static WorldStateDecision Decide(MonitorState x)
    {
        // 1) 暂停态最高优先级（step CheckOnceAsync 顶部）
        if (x.IsSuspend)
            return new WorldStateDecision(WorldStateDecisionKind.Suspend, ResetConnectedCount: true);

        // 2) 墙钟超时兜底解除（step 0）——发生在 isInMultiGame 判定之前，
        //    解除后将抑制视为关闭。OQ-2：force-clear 同时清零计数。
        bool suppressed = x.TeleportSuppressed;
        if (suppressed && x.MsSinceSuppression > x.TimeoutSeconds * 1000L)
        {
            // 抑制被墙钟强制解除：本帧标记 ForceClear + 清零，后续帧才进入累计/恢复
            return new WorldStateDecision(WorldStateDecisionKind.ForceClearSuppression, ResetConnectedCount: true);
        }

        // 3) 截图异常（step 1 异常分支）
        if (x.ScreenshotThrew)
            return new WorldStateDecision(WorldStateDecisionKind.ScreenshotFailed, ResetConnectedCount: false);

        // 4) IsInMultiGame=true（step 2）
        if (x.IsInMultiGame)
            return new WorldStateDecision(WorldStateDecisionKind.NormalInGame, ResetConnectedCount: true);

        // 5) 组队阶段（step 3a）
        if (x.IsPartyPhase)
            return new WorldStateDecision(WorldStateDecisionKind.Ignore, ResetConnectedCount: true);

        // 6) 传送抑制期内（step 3b）——窗口内 / 被 retry 刷新后 msSinceSuppression 不超阈值
        if (suppressed)
            return new WorldStateDecision(WorldStateDecisionKind.Ignore, ResetConnectedCount: false);

        // 7) 轮次切换 / 换角色 / 吃药（step 3c/3d/3e）——语义一致：忽略本帧、不累计不重置
        if (x.IsRoundSwitching || x.IsRoleSwitching || x.IsEatingMedicine)
            return new WorldStateDecision(WorldStateDecisionKind.Ignore, ResetConnectedCount: false);

        // 8) 信号融合（step 4）：IsInMultiGame=false + SignalR
        if (x.SignalrConnected)
        {
            int next = x.ConnectedNotInGameCount + 1;
            return next >= x.Threshold
                ? new WorldStateDecision(WorldStateDecisionKind.JudgeKickedOut, ResetConnectedCount: false)
                : new WorldStateDecision(WorldStateDecisionKind.AccumulateTransient, ResetConnectedCount: false);
        }

        // 9) SignalR 断开 → 恢复窗口（step 5）
        return new WorldStateDecision(WorldStateDecisionKind.RecoveryWindow, ResetConnectedCount: true);
    }
}
