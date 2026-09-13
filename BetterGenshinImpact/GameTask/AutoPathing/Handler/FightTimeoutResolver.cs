using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>
/// 独立理由（G4.3）：B5 waypoint 级战斗超时的解析决策——唯一消费方 AutoFightHandler 是公版同名高冲突宿主，
/// 并入宿主会扩大 PR 冲突面且不利 PBT；同目录 OfficialAutoFightRouter 已有"薄决策独立文件"先例。
/// 茶包引擎（AutoFightParam）与公版引擎（AutoFightOfficial.AutoFightParam）两条路径共用同一套判定，
/// 纯函数零副作用；公版 PR 时为零冲突纯新增（失败残留档位②纯插入，见 v2 计划 M07/B5）。
/// </summary>
public static class FightTimeoutResolver
{
    /// <summary>
    /// 解析 waypoint 级战斗超时（茶包重构 B5）：路线点的 Action 为 "fight" 且 ActionParams 为可解析整数时
    /// 返回超时秒数，否则返回 null（不注入）。
    /// 解析语义与原 AutoFightHandler 内联实现逐字等价（int.TryParse 现状语义，含负数/前导空白容忍），
    /// 由 FightTimeoutResolverPbtTest 以直译 oracle 全空间守护；调用方拿到非 null 后自行赋值 Timeout 并打日志。
    /// </summary>
    /// <param name="action">waypoint 的 Action 代码（waypointForTrack?.Action，可为 null）。</param>
    /// <param name="actionParams">waypoint 的 ActionParams 原文（waypointForTrack?.ActionParams，可为 null）。</param>
    public static int? ResolveWaypointTimeout(string? action, string? actionParams)
    {
        if (action != ActionEnum.Fight.Code || string.IsNullOrEmpty(actionParams))
        {
            return null;
        }

        return int.TryParse(actionParams, out var number) ? number : null;
    }
}
