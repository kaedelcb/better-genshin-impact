namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 快速拖动传送（TpTaskFastDrag）非提瓦特地图"跳过切换地区"决策。
/// teleport-fastdrag-prior-skip-area-switch spec（bugfix.md BC-1/OQ-2）。
/// 纯函数、无副作用，PBT 友好。
/// </summary>
public static class FastDragAreaSwitchSkipDecisions
{
    /// <summary>
    /// 是否跳过 SwitchArea 直接识别定位：
    /// 仅当 首次尝试（retryTimes == 0）且第一层先验存在（hasMiniMapPrior）时跳过。
    /// 重试轮（retryTimes >= 1）恒不跳过（恢复无条件切区，保证最坏情况可切对，CC2）。
    /// </summary>
    public static bool ShouldSkipAreaSwitch(int retryTimes, bool hasMiniMapPrior)
        => retryTimes == 0 && hasMiniMapPrior;
}
