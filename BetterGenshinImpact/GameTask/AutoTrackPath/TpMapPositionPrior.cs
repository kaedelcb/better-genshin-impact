using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 茶包快速拖动传送（TpTaskFastDrag）专属的小地图先验位置读取器。
/// 把 TpTaskFastDrag 依赖的、但公版缺失的共享成员 Navigation.GetTpPriorPosition
/// （小地图先验位置，inventory A13）能力抽成茶包版独立辅助类，使 TpTaskFastDrag 不再直接
/// 引用共享 Navigation 的茶包扩展成员，从而自包含（PR 公版时无需改动公版共享 Navigation）。
///
/// 解耦纪律：只"搬移"不"改逻辑"。此处通过转发读到与共享 Navigation 完全相同的传送先验缓存坐标，
/// 茶包版行为逐字节不变。
/// </summary>
public static class TpMapPositionPrior
{
    /// <summary>
    /// 读传送先验专用缓存坐标（不受小地图 WarmUp/Reset 影响）。
    /// 转发自 <see cref="Navigation.GetTpPriorPosition"/>。
    /// </summary>
    public static (float X, float Y) GetTpPriorPosition() => Navigation.GetTpPriorPosition();

    /// <summary>
    /// 消费传送先验 fresh 首启标志（转发共享 Navigation，茶包自包含，不直接引用共享扩展成员）。
    /// 任务启动后首次快速传送 TpOnce 调用，驱动主动小地图识别锚定。
    /// </summary>
    public static bool ConsumeTpPriorFresh() => Navigation.ConsumeTpPriorFresh();
}