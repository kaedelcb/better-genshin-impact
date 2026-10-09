using BetterGenshinImpact.GameTask.AutoFight.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

public class CombatScriptBag(List<CombatScript> combatScripts)
{
    public List<CombatScript> CombatScripts { get; set; } = combatScripts;

    private static AutoFightConfig FightConfig { get; set; } = TaskContext.Instance().Config.AutoFightConfig;
    
    public CombatScriptBag(CombatScript combatScript) : this([combatScript])
    {
    }

    public List<CombatCommand>? FindCombatScript(ReadOnlyCollection<Avatar> avatars,bool isFirstRound = false)
    {
        if (!TrySelectCombatScript(avatars.Select(avatar => avatar.Name).ToArray(), out var combatScript, out var matchCount)
            || combatScript is null)
        {
            if (isFirstRound)
            {
                return null;
            }

            throw new Exception("未匹配到任何战斗脚本");
        }

        // 首轮只接受完整队伍匹配；部分匹配交给第二轮兜底策略。
        if (isFirstRound && matchCount != avatars.Count)
        {
            return null;
        }

        if (matchCount == avatars.Count)
        {
            Logger.LogInformation("匹配到战斗脚本：{Name}", combatScript.Name);
        }
        else
        {
            Logger.LogWarning("未完整匹配到当前队伍，使用匹配度最高的队伍：{Name}", combatScript.Name);
        }

        return combatScript.CombatCommands;
    }

    internal (CombatScript Script, int MatchCount) SelectCombatScript(IReadOnlyCollection<string> avatarNames)
    {
        if (!TrySelectCombatScript(avatarNames, out var bestScript, out var bestMatchCount))
        {
            throw new Exception("未匹配到任何战斗脚本");
        }

        return (bestScript!, bestMatchCount);
    }

    private bool TrySelectCombatScript(
        IReadOnlyCollection<string> avatarNames,
        out CombatScript? bestScript,
        out int bestMatchCount)
    {
        bestScript = null;
        bestMatchCount = 0;

        foreach (var combatScript in CombatScripts)
        {
            var matchCount = 0;
            foreach (var avatarName in avatarNames)
            {
                if (combatScript.AvatarNames.Contains(avatarName))
                {
                    matchCount++;
                }
            }

            if (matchCount == 0)
            {
                continue;
            }

            // 先比较匹配人数；人数相同时，策略角色越少，匹配比例越高。
            // 即使已覆盖全队也继续比较，避免通用策略抢先覆盖专用策略。
            if (bestScript == null
                || matchCount > bestMatchCount
                || (matchCount == bestMatchCount && combatScript.AvatarNames.Count < bestScript.AvatarNames.Count))
            {
                bestScript = combatScript;
                bestMatchCount = matchCount;
            }
            // 两项相同时保留先遇到的策略，不改变候选列表顺序。
        }
        return bestScript != null;
    }

    /// <summary>
    /// 第二轮匹配的"无异常"重载，仅供联机锄地兜底分支使用。
    /// 返回 ok=false 表示"未匹配到任何战斗脚本"，调用方应据此走兜底分支（不抛异常）。
    /// 完全匹配 / 匹配度最高 fallback 走 ok=true，commands 与 matchedScriptCount 与原 FindCombatScript 等价。
    ///
    /// **不修改原 FindCombatScript 行为**——单机路径继续抛 Exception 维持原行为（preservation §3.4-3.6）。
    /// 详见 design.md §3.1。
    /// </summary>
    /// <param name="avatars">当前队伍角色</param>
    /// <param name="commands">输出：匹配到的指令列表（ok=false 时为 null）</param>
    /// <param name="matchedScriptCount">输出：MatchCount &gt; 0 的脚本数量（用于 ShouldUseFallback C2/C3 判定）</param>
    /// <returns>true=找到（完全匹配 / 匹配度最高），false=无任何脚本匹配（matchedScriptCount=0）</returns>
    public bool TryFindCombatScript(
        ReadOnlyCollection<Avatar> avatars,
        out List<CombatCommand>? commands,
        out int matchedScriptCount)
    {
        commands = null;
        matchedScriptCount = 0;

        foreach (var combatScript in CombatScripts)
        {
            var matchCount = 0;
            foreach (var avatar in avatars)
            {
                if (combatScript.AvatarNames.Contains(avatar.Name))
                {
                    matchCount++;
                }

                if (matchCount == avatars.Count)
                {
                    Logger.LogInformation("匹配到战斗脚本：{Name}", combatScript.Name);
                    commands = combatScript.CombatCommands;
                    matchedScriptCount = 1; // 至少一个完全匹配
                    return true;
                }
            }

            if (matchCount > 0) matchedScriptCount++;
        }

        if (matchedScriptCount == 0)
        {
            return false; // 兜底信号
        }

        var avatarNames = avatars.Select(avatar => avatar.Name).ToArray();
        if (!TrySelectCombatScript(avatarNames, out var bestScript, out _) || bestScript is null)
        {
            return false;
        }

        Logger.LogWarning("未完整匹配到四人队伍，使用匹配度最高的队伍：{Name}", bestScript.Name);
        commands = bestScript.CombatCommands;
        return true;
    }
}
