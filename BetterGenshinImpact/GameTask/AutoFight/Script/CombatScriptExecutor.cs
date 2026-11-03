using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public static class CombatScriptExecutor
{
    /// <summary>
    /// 执行简易战斗策略脚本。
    /// </summary>
    /// <param name="combatScript">已解析的策略</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="logger">日志</param>
    /// <param name="combatScenes">
    /// 可选。传了则使用现成的 CombatScenes（由调用方管理生命周期）；
    /// 不传则在内部通过截图自动创建并管理生命周期。
    /// </param>
    public static async Task ExecuteAsync(
        CombatScript combatScript,
        CancellationToken ct,
        ILogger logger,
        CombatScenes? combatScenes = null)
    {
        var ownsScenes = false;
        try
        {
            if (combatScenes == null)
            {
                using var capture = CaptureToRectArea();
                combatScenes = new CombatScenes();
                ownsScenes = true;
                combatScenes.InitializeTeam(capture);
                if (!combatScenes.CheckTeamInitialized())
                {
                    logger.LogError("队伍识别未初始化成功！");
                    return;
                }
            }

            // 仅在内部创建 CombatScenes 时设置 Avatar.Ct，外部传入时由调用方管理
            if (ownsScenes)
            {
                combatScenes.BeforeTask(ct);
            }

            if (!HasRequiredAvatar(combatScript, combatScenes, logger))
            {
                return;
            }

            try
            {
                // 通用化战斗策略
                for (var i = 0; i < combatScript.CombatCommands.Count; i++)
                {
                    var command = combatScript.CombatCommands[i];
                    var lastCommand = i == 0 ? command : combatScript.CombatCommands[i - 1];
                    ct.ThrowIfCancellationRequested();
                    command.Execute(combatScenes, lastCommand);
                }
            }
            catch (Exception e)
            {
                if (LogCommandFailureAndCheckRetry(e, logger))
                {
                    throw;
                }
            }
        }
        finally
        {
            if (ownsScenes)
            {
                SafeDispose(combatScenes!, logger);
            }
        }
    }

    internal static bool HasRequiredAvatar(CombatScript script, CombatScenes scenes, ILogger logger)
    {
        // 无角色前缀的指令使用当前角色，不要求队伍有指定角色。
        if (script.AvatarNames.Contains(CombatScriptParser.CurrentAvatarName)
            || scenes.GetAvatars().Any(avatar => script.AvatarNames.Contains(avatar.Name)))
        {
            return true;
        }

        logger.LogError("简易策略脚本要求的角色不存在！队伍中需要存在下面角色中的一个或多个：{AvatarNames}", string.Join(", ", script.AvatarNames));
        return false;
    }

    internal static bool LogCommandFailureAndCheckRetry(Exception error, ILogger logger)
    {
        if (error is RetryException)
        {
            logger.LogWarning("简易策略脚本执行时出现重试异常，原因：{Msg}，重试中...", error.Message);
            return true;
        }

        logger.LogError(error, "执行简易策略脚本时发生错误！");
        return false;
    }

    /// <summary>
    /// 安全释放 CombatScenes，异常仅记录不抛出，
    /// 避免释放异常覆盖 try 块中的原始异常。
    /// </summary>
    private static void SafeDispose(CombatScenes combatScenes, ILogger logger)
    {
        try
        {
            combatScenes.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "释放战斗场景资源时发生异常");
        }
    }
}
