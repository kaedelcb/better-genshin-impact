using System;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 独立理由：传送（快速拖动）域的纯决策/纯计算聚合类，供 <see cref="TpTaskFastDrag"/> 在
/// MoveMapTo 主循环（缩放决策）、拖动跑道截断等路径复用；同域纯函数必须合并成一个 *Decisions 类
/// （refactor-feature-inventory §G4：避免"一个函数一个文件"碎片——曾散落于 TeleportZoomDecisions /
/// TeleportDragRunway 两个文件）。内部按职责用注释分隔（缩放决策 / 拖动跑道截断）；均为无状态纯函数，便于 PBT 属性测试。
/// </summary>
public static class TeleportDecisions
{
    // ===================== 缩放决策 =====================

    /// <summary>
    /// 快速拖动模式收工阈值（与 MoveMapTo 收工分支②的阈值表达式同源同值）。
    /// retryTimes == 0 时为 400，否则为 300。
    /// </summary>
    public static int FastModeBreakThreshold(int retryTimes) => retryTimes == 0 ? 400 : 300;

    /// <summary>
    /// 判定本轮循环是否应执行地图放大。
    /// 等价于原放大分支的三重进入条件 (mapZoomEnabled||mapMoveStepDivisor)
    /// && mouseDistance &lt; (mapMoveStepDivisor?600:mapZoomInDistance)
    /// && currentZoomLevel &gt; minZoomLevel + precisionThreshold，
    /// 外加快速拖动模式前置门控：已进入收工区间(mouseDistance &lt; 收工阈值)
    /// 且当前缩放已在传送点可见档(currentZoomLevel &lt;= displayTpPointZoomLevel + precisionThreshold)时不放大。
    /// 缩放语义：值越小越放大；普通传送点仅在缩放 &lt;= 显示档(DisplayTpPointZoomLevel=4.4)时才渲染，
    /// 缩放仍大于显示档时即使距离到位也必须继续放大到可见档，否则会点在不显示传送点的空位上
    /// （神像/秘境全缩放可见，此处保守不区分点类型，多放大一次无害）。
    /// 经典模式(mapMoveStepDivisor == false)门控恒为 false，行为与原逻辑逐字节等价。
    /// </summary>
    public static bool ShouldZoomInThisIteration(
        bool mapMoveStepDivisor,
        bool mapZoomEnabled,
        double mouseDistance,
        double currentZoomLevel,
        double minZoomLevel,
        double precisionThreshold,
        int retryTimes,
        double mapZoomInDistance,
        double displayTpPointZoomLevel)
    {
        // 快速拖动模式：已进入收工区间 且 当前缩放已在传送点可见档 → 本轮不放大，落到收工 break 去点击。
        // 若缩放仍大于显示档(普通传送点不渲染)，即使距离到位也不跳过，继续放大到可见档，避免点空。
        // && mapMoveStepDivisor 保证经典模式此门控恒 false（零回归）。
        bool fastModeReadyToClick =
            mapMoveStepDivisor
            && mouseDistance < FastModeBreakThreshold(retryTimes)
            && currentZoomLevel <= displayTpPointZoomLevel + precisionThreshold;
        if (fastModeReadyToClick)
        {
            return false;
        }

        if (!(mapZoomEnabled || mapMoveStepDivisor))
        {
            return false;
        }

        double zoomInThreshold = mapMoveStepDivisor ? 600 : mapZoomInDistance;
        if (!(mouseDistance < zoomInThreshold))
        {
            return false;
        }

        return currentZoomLevel > minZoomLevel + precisionThreshold;
    }

    // ===================== 拖动跑道截断 =====================

    /// <summary>
    /// 计算落点沿拖动方向到捕获区边缘、预留安全边距后的可移动跑道，
    /// 返回施加于位移向量的等比缩放因子 t ∈ [0, 1]。最终位移 = 原位移 * t，方向不变（整体等比）。
    /// </summary>
    /// <param name="landingX">落点 X（捕获区像素，0..width）</param>
    /// <param name="landingY">落点 Y（捕获区像素，0..height）</param>
    /// <param name="dispX">本次期望的 X 方向位移（捕获区像素，含符号）</param>
    /// <param name="dispY">本次期望的 Y 方向位移（捕获区像素，含符号）</param>
    /// <param name="width">捕获区宽度</param>
    /// <param name="height">捕获区高度</param>
    /// <param name="safetyMargin">安全边距，默认 50px</param>
    /// <returns>缩放因子 t：最终位移 = disp * t，t ∈ [0,1]</returns>
    public static double ComputeRunwayScale(
        double landingX, double landingY,
        double dispX, double dispY,
        double width, double height,
        double safetyMargin = 50)
    {
        // P6 零向量安全：无位移，返回 1.0（disp*1 == 0，无除零）
        if (dispX == 0 && dispY == 0)
        {
            return 1.0;
        }

        double t = 1.0;

        // X 轴：仅当有 X 分量时才受 X 跑道约束（某轴分量为 0 不施加限制）
        if (dispX != 0)
        {
            // 沿拖动方向到边缘的距离：正向到 width，负向到 0
            double edgeDist = dispX > 0 ? (width - landingX) : landingX;
            double runway = edgeDist - safetyMargin;
            if (runway < 0)
            {
                runway = 0; // P7 跑道非负
            }

            double axisT = runway / Math.Abs(dispX); // 该轴允许的最大比例
            if (axisT < t)
            {
                t = axisT;
            }
        }

        // Y 轴同理
        if (dispY != 0)
        {
            double edgeDist = dispY > 0 ? (height - landingY) : landingY;
            double runway = edgeDist - safetyMargin;
            if (runway < 0)
            {
                runway = 0;
            }

            double axisT = runway / Math.Abs(dispY);
            if (axisT < t)
            {
                t = axisT;
            }
        }

        // P4 界内不变：axisT >= 1 时保持 1，不放大
        if (t > 1.0)
        {
            t = 1.0;
        }

        if (t < 0.0)
        {
            t = 0.0;
        }

        return t;
    }
}