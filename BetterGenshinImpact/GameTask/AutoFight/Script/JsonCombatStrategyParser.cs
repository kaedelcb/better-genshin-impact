using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

/// <summary>
/// JSON 战斗策略解析器
/// </summary>
public static class JsonCombatStrategyParser
{
    /// <summary>
    /// 从文件解析 JSON 战斗策略
    /// </summary>
    /// <param name="path">策略文件路径</param>
    /// <returns>解析后的战斗策略</returns>
    /// <exception cref="FileNotFoundException">文件不存在</exception>
    /// <exception cref="InvalidOperationException">解析失败或格式错误</exception>
    public static JsonCombatStrategy ParseFile(string path)
    {
        if (!File.Exists(path))
        {
            Logger.LogError("JSON 战斗策略文件不存在：{Path}", path);
            throw new FileNotFoundException("JSON 战斗策略文件不存在", path);
        }

        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// 从 JSON 字符串解析战斗策略
    /// </summary>
    /// <param name="json">JSON 字符串</param>
    /// <returns>解析后的战斗策略</returns>
    /// <exception cref="InvalidOperationException">解析失败或格式错误</exception>
    public static JsonCombatStrategy Parse(string json)
    {
        JsonCombatStrategy? strategy;
        try
        {
            strategy = JsonConvert.DeserializeObject<JsonCombatStrategy>(json);
        }
        catch (JsonException ex)
        {
            Logger.LogError("JSON 战斗策略解析失败：{Msg}", ex.Message);
            throw new InvalidOperationException($"JSON 战斗策略格式错误：{ex.Message}", ex);
        }

        if (strategy == null)
        {
            Logger.LogError("JSON 战斗策略反序列化结果为空");
            throw new InvalidOperationException("JSON 战斗策略反序列化失败");
        }

        if (strategy.Info == null)
        {
            Logger.LogError("JSON 战斗策略缺少 Info 节点");
            throw new InvalidOperationException("JSON 战斗策略缺少 Info 节点");
        }

        if (strategy.Actions == null || strategy.Actions.Count == 0)
        {
            Logger.LogError("JSON 战斗策略缺少 Actions 节点或动作为空");
            throw new InvalidOperationException("JSON 战斗策略中未定义任何动作");
        }

        // 校验动作合法性（名称需能作为条件标识符解析；index 允许重复）
        ValidateActions(strategy.Actions);

        Logger.LogInformation("JSON 战斗策略加载完成：{Name}，共 {Count} 个动作",
            strategy.Info.Name, strategy.Actions.Count);

        return strategy;
    }

    /// <summary>
    /// 校验动作名称合法性。
    /// 仅拒绝会与条件语法产生歧义的名称（<see cref="ConditionEvaluator.IsAmbiguousActionName"/>：
    /// 布尔字面量 true/false、纯数字、与内置条件函数同名）——这三类会在条件表达式中被解析为字面量或函数调用而静默误判。
    /// 含 +、空格、/ 等的描述性名称（如"蓝砚-开盾+回点"）允许使用：它们只是无法作为单个标识符被 since/count 等按名引用，
    /// 但可正常用于日志与按 index 引用，不会影响策略正确执行。
    /// 允许不同动作使用相同 index（since/count 等按 index 查询时取最近一次执行的事件记录）。
    /// </summary>
    private static void ValidateActions(List<JsonAction> actions)
    {
        foreach (var action in actions)
        {
            if (!string.IsNullOrEmpty(action.Name) && ConditionEvaluator.IsAmbiguousActionName(action.Name))
            {
                Logger.LogError("JSON 战斗策略中动作名称与条件语法冲突（不能是布尔字面量 true/false、纯数字，也不能与内置条件函数同名）：{Name}", action.Name);
                throw new InvalidOperationException($"JSON 战斗策略中动作名称与条件语法冲突：{action.Name}");
            }
        }
    }
}
