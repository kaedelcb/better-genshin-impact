#nullable enable
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 独立理由（R4.2）：唯一消费方 PathExecutor.cs 是与公版同名的高冲突文件——纯函数防呆
/// 独立为零冲突新文件正当；是 A13 定位域与 M12 的相邻积木（PR 随 A13 定位域同批）。
/// 零坐标防呆纯函数（Requirement 4——历史 spec 编号，来源 spec 已归档）。无副作用，PBT 目标。
/// </summary>
public static class ZeroCoordGuard
{
    /// <summary>
    /// 决定朝向计算应使用的坐标。
    /// 关闭 → 直接用当前帧（含 (0,0)，保持现状，Requirement 4.3）。
    /// 开启 + 当前帧非 (0,0) → 用当前帧。
    /// 开启 + 当前帧 (0,0) + 有有效上一帧 → 用上一帧（Requirement 4.2）。
    /// 开启 + 当前帧 (0,0) + 无有效上一帧 → 返回 skip（调用方跳过本帧朝向更新 + 记日志，Requirement 4.4）。
    /// </summary>
    /// <param name="current">当前帧识别坐标。</param>
    /// <param name="prev">最近一帧坐标（(0,0) 视为无效）。</param>
    /// <param name="guardEnabled">防呆开关。</param>
    /// <returns>(effectivePosition, skipOrientation)。skip=true 时 effectivePosition 无意义。</returns>
    public static (Point2f position, bool skip) ResolveOrientationPosition(
        Point2f current, Point2f prev, bool guardEnabled)
    {
        if (!guardEnabled) return (current, false);
        if (current is not { X: 0, Y: 0 }) return (current, false);
        // 当前帧失败 (0,0)
        if (prev is not { X: 0, Y: 0 }) return (prev, false);
        return (current, true); // 无有效上一帧，跳过
    }
}
