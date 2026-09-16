#nullable enable

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// Pull 跳转（把本机带到服务端指定的路线边界）的纯判定（route-anchor · 阶段 4）。
///
/// 与仓库既有决策函数模式一致：无 logger / 无 client / 无游戏依赖，便于单测直接撒输入。
/// 全部判定都是"安全默认"：任何不确定都判为**不跳**（跳错路线比不跳更糟——
/// 会重复执行或漏执行路线，而漏执行的路线不会补跑）。
///
/// 目标路线索引一律以**服务端下发的 TargetRouteIndex**为准，客户端不得自行选择更远的路线。
/// </summary>
public static class RouteAnchorJumpDecisions
{
    /// <summary>
    /// 是否允许执行这次跳转。
    ///
    /// 条件（全部满足）：
    ///   1. 目标落在 [startIndex, routeCount) 内（本轮计划范围内，越界即非法）；
    ///   2. 目标是**严格向前**的（target &gt; currentRouteIndex）——
    ///      向后跳会重复执行已完成的路线，属于非法指令；
    ///   3. 目标不等于"下一条"时也必须允许（服务端可能要求直接跳到更后面的边界，例如本轮大部队已走远）。
    ///
    /// 注意：这里不管"是否正在战斗/是否可中断"——那是调用方在执行阶段的职责
    /// （不可中断时必须回报 PullFailed，而不是在这里静默放过）。
    /// </summary>
    public static bool CanJump(int currentRouteIndex, int startIndex, int targetRouteIndex, int routeCount)
    {
        if (routeCount <= 0) return false;
        if (startIndex < 0) return false;
        if (targetRouteIndex < startIndex) return false;
        if (targetRouteIndex >= routeCount) return false;
        if (targetRouteIndex <= currentRouteIndex) return false;
        return true;
    }

    /// <summary>
    /// 把跳转目标换算成"写入循环变量后应赋的值"。
    ///
    /// 路线外层循环形如 <c>for (int routeIndex = startIndex; routeIndex &lt; n; routeIndex++)</c>，
    /// 调用方写法固定为：
    /// <code>
    /// if (RouteAnchorJumpDecisions.CanJump(...))
    /// {
    ///     routeIndex = RouteAnchorJumpDecisions.ResolveLoopIndexForJump(target);
    ///     continue;   // continue 后 for 自增一次 → 下一次迭代恰好是 target
    /// }
    /// </code>
    /// 因此必须写入 <c>target - 1</c>；写 <c>target</c> 会跳过目标路线（漏执行），
    /// 这是本函数存在的唯一理由——把它固化成可测契约，避免接线时写错。
    /// </summary>
    public static int ResolveLoopIndexForJump(int targetRouteIndex) => targetRouteIndex - 1;

    /// <summary>
    /// 跳转失败时的回报语义：任何"不能跳"都必须如实回报失败，
    /// 由服务端按强兜底策略整队停止（不允许客户端静默继续跑旧路线）。
    /// </summary>
    public static bool ShouldReportPullFailure(bool canJump) => !canJump;

    /// <summary>
    /// 是否应把"当前正在执行的路线"视作需要中断：仅当目标不是当前路线本身时才需要中断。
    /// （CanJump 已保证严格向前，故一旦可跳，就必须先中断当前路线再跳。）
    /// </summary>
    public static bool ShouldAbortCurrentRoute(int currentRouteIndex, int targetRouteIndex)
        => targetRouteIndex > currentRouteIndex;
}
