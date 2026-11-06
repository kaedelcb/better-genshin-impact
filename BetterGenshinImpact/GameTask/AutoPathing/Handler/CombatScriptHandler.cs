using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class CombatScriptHandler : IActionHandler
{
    public Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
        => RunCoreAsync(ct, waypointForTrack, null, null);

    internal static PickupWaypointProgress? TryStartPickup(CancellationToken ct, WaypointForTrack waypoint, string? pointLabel,
        double distance, bool fromPreviousPoint, CombatScenes? scenes = null, bool counting = true)
    {
        if (waypoint.PickupTargetCount is not { Count: > 0 } target)
            return null;

        var pickup = PickupWaypointProgress.TryBegin(target.Count, ct, Logger, pointLabel, counting);
        if (pickup == null)
        {
            ct.ThrowIfCancellationRequested();
            Logger.LogWarning("【拾取达标】{Point} 已有节点在计数，按原路线执行", pointLabel);
            return null;
        }

        if (counting)
            Logger.LogInformation("【拾取达标】{Point} {Phase}距目标 {Distance:F2}，开始观察获得列表，目标 {Target} 行，达标后跳过 {Skip} 点",
                pointLabel, fromPreviousPoint ? "上一点移动中，" : "本点移动中，", distance, target.Count, target.SkipNextWaypoints);
        else
            Logger.LogInformation("【拾取达标】{Point} 预备获得列表基线，正常到点或距目标 5 个距离单位内开始计数，目标 {Target} 行",
                pointLabel, target.Count);
        return pickup;
    }

    internal async Task<bool> RunWithPickupAsync(CancellationToken ct, WaypointForTrack waypoint,
        PickupWaypointProgress pickup, string? pointLabel, bool keepObservingAfterAction = false)
    {
        // 观察已在移动时启动；基线尚未就绪时继续观察，不阻塞节点动作。
        // 靠近时已经拾够：移动照常到点，完全不发送节点策略指令。
        if (pickup.ReachedToken.IsCancellationRequested)
            return FinishPickup(ct, pickup, waypoint, pointLabel, false, Logger);

        return await RunCoreAsync(ct, waypoint, pickup, pointLabel, keepObservingAfterAction);
    }

    private static bool FinishPickup(CancellationToken ct, PickupWaypointProgress pickup,
        Waypoint waypoint, string? pointLabel, bool keepObservingAfterAction, ILogger logger)
    {
        ct.ThrowIfCancellationRequested();
        if (keepObservingAfterAction && pickup.BeginDeparture() && !pickup.ReachedToken.IsCancellationRequested)
        {
            logger.LogInformation("【拾取达标】{Point} 动作结束，已计 {Count}/{Target}，继续观察首个补捡路段",
                pointLabel, pickup.Count, waypoint.PickupTargetCount?.Count);
            return false;
        }

        pickup.Dispose();
        var reached = pickup.ReachedToken.IsCancellationRequested;
        if (reached)
            pickup.RecordReachedCompletion();
        logger.LogInformation("【拾取达标】{Point} 计数 {Count}/{Target}：{Result}",
            pointLabel, pickup.Count, waypoint.PickupTargetCount?.Count,
            reached ? "达标，已完成动作收尾和松键" : "未达标，后续点照常执行");
        return reached;
    }

    private static async Task<bool> RunCoreAsync(CancellationToken ct, WaypointForTrack? waypointForTrack,
        PickupWaypointProgress? pickup, string? pointLabel, bool keepObservingAfterAction = false)
    {
        if (waypointForTrack is { CombatScript: not null })
        {
            Logger.LogInformation("执行 {Text}", "简易策略脚本");
            var combatScript = waypointForTrack.CombatScript;
            var combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
            if (combatScenes == null)
            {
                Logger.LogError("队伍识别未初始化成功！");
                if (pickup != null)
                {
                    pickup.Dispose();
                    Logger.LogWarning("【拾取达标】{Point} 队伍识别未初始化，本点不跳过后续点", pointLabel);
                }
                return false;
            }

            combatScenes.BeforeTask(ct);
            if (pickup == null)
            {
                await CombatScriptExecutor.ExecuteAsync(combatScript, ct, Logger, combatScenes);
                return false;
            }

            return await RunPickupCommandsAsync(ct, pickup, waypointForTrack, pointLabel, keepObservingAfterAction, Logger,
                () => PickupCombatScriptExecutor.ExecuteAsync(combatScript, combatScenes, ct, pickup.ReachedToken, Logger));
        }
        else
        {
            Logger.LogError("策略脚本action_params内容为空");
        }

        return false;
    }

    internal static async Task<bool> RunPickupCommandsAsync(CancellationToken ct, PickupWaypointProgress pickup,
        Waypoint waypoint, string? pointLabel, bool keepObservingAfterAction, ILogger logger, Func<Task<bool>> executeCommands)
    {
        try
        {
            if (!await executeCommands())
            {
                // 普通命令错误只结束本节点动作，不以失败节点的计数跳过后续点。
                pickup.Dispose();
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == pickup.ReachedToken
            && pickup.ReachedToken.IsCancellationRequested)
        {
            // 执行器已经完成按键收尾，整条线路的停止仍优先。
        }

        return FinishPickup(ct, pickup, waypoint, pointLabel, keepObservingAfterAction, logger);
    }

    // private static bool IsOnlyCurrentAvatar(CombatScript combatScript)
    // {
    //     var isCurrentAvatar = false;
    //     if (combatScript.AvatarNames.Count == 1)
    //     {
    //         foreach (var avatarName in combatScript.AvatarNames)
    //         {
    //             if (avatarName == CombatScriptParser.CurrentAvatarName)
    //             {
    //                 isCurrentAvatar = true;
    //                 break;
    //             }
    //         }
    //     }
    //
    //     return isCurrentAvatar;
    // }
}