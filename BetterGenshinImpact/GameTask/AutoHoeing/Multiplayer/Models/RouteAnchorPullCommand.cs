#nullable enable

using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

/// <summary>
/// 服务端下发的"拉回边界"命令（route-anchor · 定向发送给需要被拉回的成员）。
///
/// 语义（方案第 6 章）：只中断**当前路线子任务**并跳到 <see cref="TargetRouteIndex"/> 的边界，
/// 不取消整个会话；执行并确认旧执行器退出后回报 PullApplied，再等 Released 才能跑目标路线。
/// </summary>
public sealed class RouteAnchorPullCommand
{
    /// <summary>锚点 ID（服务端生成）。</summary>
    public string AnchorId { get; set; } = "";

    /// <summary>会话标识（服务端生成；与本地缓存不一致即为过期命令）。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>世界代际。</summary>
    public int WorldEpoch { get; set; }

    /// <summary>冻结的路线计划标识。</summary>
    public string PlanId { get; set; } = "";

    /// <summary>本次恢复动作编号：重发保持同一 ID，客户端据此去重。</summary>
    public string CommandId { get; set; } = "";

    /// <summary>目标路线索引（客户端只允许跳到服务端给出的这个边界）。</summary>
    public int TargetRouteIndex { get; set; } = -1;

    /// <summary>第几次尝试（诊断用）。</summary>
    public int Attempt { get; set; }

    /// <summary>收到时刻（UTC）。</summary>
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    public override string ToString()
        => $"RouteAnchorPull[CommandId={CommandId}, Target={TargetRouteIndex}, Attempt={Attempt}]";
}
