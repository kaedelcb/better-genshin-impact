#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 路线边界锚点的激活与放行判定（route-anchor）· 纯函数。
///
/// 与仓库既有模式一致（无 logger / 无 client / 无 TaskContext 依赖），便于单测直接撒输入。
/// 三条判定都是"安全默认"：任何不确定都判为**不激活/不放行**，
/// 避免"忘记接线就静默半启用"或"拿不到放行就继续跑"。
/// </summary>
public static class RouteAnchorActivationDecisions
{
    /// <summary>
    /// 本会话是否激活锚点。
    /// 必须同时满足：配置开启、多人模式、已在房间内、服务端宣告了对应能力。
    /// 任一不满足 → 不激活（完全走原有行为）。
    /// </summary>
    public static bool ShouldActivate(
        bool configEnabled, bool multiplayerEnabled, bool inRoom, bool serverSupportsCapability)
        => configEnabled && multiplayerEnabled && inRoom && serverSupportsCapability;

    /// <summary>
    /// 是否允许进入下一条路线。
    ///
    /// **只有两种结果允许继续**：
    ///   · <see cref="RouteAnchorWaitResult.Released"/>：服务端已放行（正常路径）；
    ///   · <see cref="RouteAnchorWaitResult.YieldedToRerun"/>：服务端显式表示当前处于协作重跑阶段
    ///     （`rerun_in_progress`），推进权归重跑——此时锚点让位是方案 §9.4 的要求，
    ///     若判失败会让落后成员在重跑窗口内被误停。
    ///
    /// 其余一律不允许继续：Stopped（无法统一）、Failed（通信/超时/锚点消失）、
    /// Cancelled（用户取消/任务取消）、Disabled（未启用，调用方走原行为）。
    /// 这是"绝不再允许表面继续、实际走散"的最后一道闸；新增放行值必须逐个论证并补窄化测试。
    /// </summary>
    public static bool AllowsNextRoute(RouteAnchorWaitResult result)
        => result is RouteAnchorWaitResult.Released or RouteAnchorWaitResult.YieldedToRerun;

    /// <summary>
    /// 由冻结的路线执行顺序推导计划标识（各成员必须一致）。
    /// 只取文件名与顺序：路线内容差异由既有一致性校验负责，这里只解决"同一份计划"的识别。
    /// 空列表返回空串（调用方据此跳过锚点：没有计划就没有边界）。
    /// </summary>
    public static string BuildPlanId(IReadOnlyList<string> orderedRouteFileNames)
    {
        if (orderedRouteFileNames == null || orderedRouteFileNames.Count == 0) return "";

        var joined = string.Join("\n", orderedRouteFileNames.Select(n => n ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"plan-{orderedRouteFileNames.Count}-{hex[..16]}";
    }

    /// <summary>
    /// 由"跳过计数是否发生变化"推导上一条路线的提交结果（避免在每个跳过出口都插桩）。
    /// </summary>
    public static string ResolveOutcome(int skippedCountBefore, int skippedCountNow)
        => skippedCountNow != skippedCountBefore
            ? Shared.RouteAnchor.RouteAnchorProtocol.OutcomeSkipped
            : Shared.RouteAnchor.RouteAnchorProtocol.OutcomeCompleted;
}
