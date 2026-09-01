using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 茶包快速拖动传送（TpTaskFastDrag）专属的网络挂起信号检测器。
/// 把 TpTaskFastDrag 依赖的、但公版缺失的共享成员 TaskControl.IsSuspendedByNetwork
/// （网络挂起信号，inventory E1）能力抽成茶包版独立辅助类，使 TpTaskFastDrag 不再直接
/// 引用共享 TaskControl 的茶包扩展成员，从而自包含（PR 公版时无需改动公版共享 TaskControl）。
///
/// 解耦纪律：只"搬移"不"改逻辑"。此处通过转发读到与共享 TaskControl 完全相同的网络挂起信号，
/// 茶包版行为逐字节不变。
/// </summary>
public static class TpTeleportSuspendDetector
{
    /// <summary>
    /// 是否因网络中断而挂起（暂停传送过渡页守卫）。
    /// 转发自 <see cref="TaskControl.IsSuspendedByNetwork"/>。
    /// </summary>
    public static bool IsSuspendedByNetwork => TaskControl.IsSuspendedByNetwork;
}