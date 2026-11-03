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
        => RunCoreAsync(ct, waypointForTrack, null);

    internal async Task<bool> RunWithPickupAsync(CancellationToken ct, WaypointForTrack waypoint, object? logSource)
    {
        var supportsPickup = waypoint.CombatScript is { } script && PickupCombatScriptExecutor.Supports(script);
        if (!supportsPickup && waypoint.CombatScript != null)
        {
            Logger.LogWarning("当前节点的简易策略包含不支持安全中断的指令，仍执行完整动作");
        }

        using var pickup = supportsPickup && waypoint.PickupTargetCount is { Count: > 0 } target
            ? PickupWaypointProgress.TryBegin(target.Count, logSource)
            : null;

        return await RunCoreAsync(ct, waypoint, pickup);
    }

    private static async Task<bool> RunCoreAsync(CancellationToken ct, WaypointForTrack? waypointForTrack, PickupWaypointProgress? pickup)
    {
        if (waypointForTrack is { CombatScript: not null })
        {
            Logger.LogInformation("执行 {Text}", "简易策略脚本");
            var combatScript = waypointForTrack.CombatScript;
            var combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
            if (combatScenes == null)
            {
                Logger.LogError("队伍识别未初始化成功！");
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

            ct.ThrowIfCancellationRequested();
            if (pickup.ReachedToken.IsCancellationRequested)
            {
                Logger.LogInformation("当前节点拾取计数已达 {Count}，结束剩余策略动作", waypointForTrack.PickupTargetCount?.Count);
                return true;
            }
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