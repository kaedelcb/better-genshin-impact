using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class CombatScriptHandler : IActionHandler
{
    public Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
        => RunCoreAsync(ct, waypointForTrack, null, null);

    internal static PickupWaypointProgress? TryStartPickup(CancellationToken ct, WaypointForTrack waypoint, string? pointLabel,
        double distance, bool fromPreviousPoint)
    {
        if (waypoint.CombatScript == null || !PickupCombatScriptExecutor.Supports(waypoint.CombatScript))
        {
            Logger.LogWarning("【拾取达标】{Point} 策略为空或包含不支持安全中断的指令，按原路线执行", pointLabel);
            return null;
        }

        if (waypoint.PickupTargetCount is not { Count: > 0 } target)
            return null;

        var pickup = PickupWaypointProgress.TryBegin(target.Count, ct, Logger, pointLabel);
        if (pickup == null)
        {
            ct.ThrowIfCancellationRequested();
            Logger.LogWarning("【拾取达标】{Point} 已有节点在计数，按原路线执行", pointLabel);
            return null;
        }

        Logger.LogInformation("【拾取达标】{Point} {Phase}距目标 {Distance:F2}，开始观察获得列表，目标 {Target} 行，达标后跳过 {Skip} 点",
            pointLabel, fromPreviousPoint ? "上一点移动中，" : "本点移动中，", distance, target.Count, target.SkipNextWaypoints);
        return pickup;
    }

    internal async Task<bool> RunWithPickupAsync(CancellationToken ct, WaypointForTrack waypoint,
        PickupWaypointProgress pickup, string? pointLabel)
    {
        if (!await pickup.WaitForBaselineAsync(ct))
        {
            pickup.Dispose();
            Logger.LogWarning("【拾取达标】{Point} 未取得稳定的列表基线，按原路线执行", pointLabel);
            return await RunCoreAsync(ct, waypoint, null, null);
        }

        // 靠近时已经拾够：移动照常到点，完全不发送节点策略指令。
        if (pickup.ReachedToken.IsCancellationRequested)
            return FinishPickup(ct, pickup, waypoint, pointLabel);

        return await RunCoreAsync(ct, waypoint, pickup, pointLabel);
    }

    private static bool FinishPickup(CancellationToken ct, PickupWaypointProgress pickup,
        WaypointForTrack waypoint, string? pointLabel)
    {
        ct.ThrowIfCancellationRequested();
        pickup.Dispose(); // 动作收尾后停止计数，后到的日志不能改变本点结果。
        var reached = pickup.ReachedToken.IsCancellationRequested;
        Logger.LogInformation("【拾取达标】{Point} 计数 {Count}/{Target}：{Result}",
            pointLabel, pickup.Count, waypoint.PickupTargetCount?.Count,
            reached ? "达标，已完成动作收尾和松键" : "未达标，后续点照常执行");
        return reached;
    }

    private static async Task<bool> RunCoreAsync(CancellationToken ct, WaypointForTrack? waypointForTrack,
        PickupWaypointProgress? pickup, string? pointLabel)
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
                    Logger.LogWarning("【拾取达标】{Point} 队伍识别未初始化，本点不跳过后续点", pointLabel);
                return false;
            }

            combatScenes.BeforeTask(ct);
            if (pickup == null)
            {
                await CombatScriptExecutor.ExecuteAsync(combatScript, ct, Logger, combatScenes);
                return false;
            }

            try
            {
                await PickupCombatScriptExecutor.ExecuteAsync(combatScript, combatScenes, ct, pickup.ReachedToken);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == pickup.ReachedToken
                && pickup.ReachedToken.IsCancellationRequested)
            {
                // 执行器已经完成按键收尾，整条线路的停止仍优先。
            }

            return FinishPickup(ct, pickup, waypointForTrack, pointLabel);
        }
        else
        {
            Logger.LogError("策略脚本action_params内容为空");
        }

        return false;
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