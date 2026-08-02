using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.GameTask.FarmingPlan;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Model;
using System.Linq;


namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

[Serializable]
public class PathingTask
{
    /// <summary>
    /// 实际存储的文件名
    /// </summary>
    [JsonIgnore]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 实际存储的文件路径
    /// </summary>
    [JsonIgnore]
    public string FullPath { get; set; } = string.Empty;

    public PathingTaskInfo Info { get; set; } = new();

    /// <summary>
    /// 逻辑路线标识（route-variant-sync-by-logical-id spec / R1.1 / §15.5）。
    /// 非空时该路线进入"手动同步模式"：syncId 拼为 {LogicalRouteId}_{SyncPointId} /
    /// {LogicalRouteId}_tp_{listIdx}_{wpIdx}，与同 LogicalRouteId 的其他变体路线在同步点上配对。
    ///
    /// 来源（R15 文件夹式变体）：不再从 JSON 反序列化作为唯一来源。
    /// 当路线文件位于变体子文件夹（A变体/B变体/C变体/D变体）内时，
    /// BuildFromFilePath 会将本字段派生为"去后缀的文件基名"（如 B001南陵传奇），
    /// 使同一线路的不同变体（_a/_b...）共享相同命名空间从而对齐。
    /// 普通线路（不在变体子文件夹）保持 null，行为完全不变。
    /// </summary>
    public string? LogicalRouteId { get; set; }

    /// <summary>
    /// 路径追踪任务配置
    /// </summary>
    public PathingTaskConfig Config { get; set; } = new();
    
    /// <summary>
    /// 锄地信息
    /// </summary>
    public FarmingSession  FarmingInfo { get; set; } = new();
    public List<Waypoint> Positions { get; set; } = [];

    public bool HasAction(string actionName)
    {
        return Positions.Exists(p => p.Action == actionName);
    }

    /// <summary>
    /// 获取采集物名称
    /// </summary>
    /// <returns></returns>
    public string? GetMaterialName()
    {
        if (string.IsNullOrWhiteSpace(FullPath))
        {
            return null;
        }

        // 获取 MapPathingViewModel.PathJsonPath
        var basePath = MapPathingViewModel.PathJsonPath;

        // 获取 FullPath 相对于 basePath 的相对路径
        var relativePath = Path.GetRelativePath(basePath, FullPath);
        
        //计算一共有多少级目录
        var level = relativePath.Count(c => c == Path.DirectorySeparatorChar);
        
        //获取每一级目录的名称
        var fileNames = relativePath.Split(Path.DirectorySeparatorChar);
        
        // 获取ConditionDefinitions采集物列表
        var conditionDefinition = ConditionDefinitions.Definitions["采集物"];
        var materialList = conditionDefinition.ObjectOptions?.ToList() ?? new List<string>();
        
        //跳过第一个目录，i从1开始,（一级目录必定不是采集物），对比每一个采集物名称 
        for (var i = 1; i < level; i++)
        {
            var materialName = fileNames[i];
            if (materialList.Contains(materialName))
            {
                return materialName;
            }
        }
        return null;
    }

    public static PathingTask? BuildFromFilePath(string filePath)
    {
        //var json = File.ReadAllText(filePath);
        var task = JsonSerializer.Deserialize<PathingTask>(JsonMerger.getMergePathingJson(filePath), PathRecorder.JsonOptions) ?? throw new Exception("Failed to deserialize PathingTask");
        task.FileName = Path.GetFileName(filePath);
        task.FullPath = filePath;

        // route-variant-sync-by-logical-id spec / §15.5 / R15.2：
        // 文件夹式变体——若文件位于变体子文件夹（A变体/B变体/C变体/D变体）内，
        // 则 LogicalRouteId（= syncId 命名空间 = 配对键）由"去后缀的文件基名"派生。
        // 这样 ExecuteRoute 每次从 FullPath 重新加载也能稳定得到同一基名，
        // 保证 A/B 变体在战斗点的 syncId 命名空间一致（跨变体对齐）。
        // 不在变体子文件夹里的普通线路：LogicalRouteId 保持反序列化结果（通常 null），零回归。
        var variantFolder = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantNaming.TryGetVariantFolder(filePath);
        if (variantFolder != null)
        {
            var baseName = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantNaming.StripBaseName(task.FileName, variantFolder);
            if (!string.IsNullOrEmpty(baseName))
                task.LogicalRouteId = baseName;
        }

        // route-variant-sync-by-logical-id spec / R1.6-1.8：
        // 加载阶段立即检测同一路线内 SyncPointId 重复，命中即拒绝加载。
        ValidateSyncPointIds(task.Positions, task.FileName);

        //添加区分怪物拾取标志
        foreach (var taskPosition in task.Positions)
        {
            taskPosition.PointExtParams.EnableMonsterLootSplit = task.Info.EnableMonsterLootSplit;
        }
        // 比较版本号大小 BgiVersion
        if (!string.IsNullOrWhiteSpace(task.Info.BgiVersion) && Global.IsNewVersion(task.Info.BgiVersion))
        {
            TaskControl.Logger.LogError("地图追踪任务 {Name} 版本号要求 {BgiVersion} 大于当前 BetterGI 版本号 {CurrentVersion} ， 禁止运行，请更新 BetterGI 版本！", task.FileName, task.Info.BgiVersion, Global.Version);
            TaskControl.Logger.LogError("地图追踪任务 {Name} 版本号要求 {BgiVersion} 大于当前 BetterGI 版本号 {CurrentVersion} ， 禁止运行，请更新 BetterGI 版本！", task.FileName, task.Info.BgiVersion, Global.Version);
            TaskControl.Logger.LogError("地图追踪任务 {Name} 版本号要求 {BgiVersion} 大于当前 BetterGI 版本号 {CurrentVersion} ， 禁止运行，请更新 BetterGI 版本！", task.FileName, task.Info.BgiVersion, Global.Version);
            return null;
        }
        return task;
    }

    public static PathingTask BuildFromJson(string json)
    {
        var task = JsonSerializer.Deserialize<PathingTask>(json, PathRecorder.JsonOptions) ?? throw new Exception("Failed to deserialize PathingTask");
        ValidateSyncPointIds(task.Positions, "<inline-json>");
        return task;
    }

    /// <summary>
    /// 校验路线内 SyncPointId 唯一性（route-variant-sync-by-logical-id spec / R1.6-1.8）。
    /// 仅扫描非空 SyncPointId；多个 null 不视为重复。
    /// 命中重复时抛 InvalidRouteException，异常消息含重复字符串 + waypoint 索引列表。
    /// O(n) 时间，单次字典扫描。
    /// </summary>
    private static void ValidateSyncPointIds(List<Waypoint> positions, string fileName)
    {
        var occurrences = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < positions.Count; i++)
        {
            var id = positions[i].SyncPointId;
            if (string.IsNullOrEmpty(id)) continue;
            if (!occurrences.TryGetValue(id, out var list))
            {
                list = new List<int>();
                occurrences[id] = list;
            }
            list.Add(i);
        }

        var duplicates = occurrences.Where(kv => kv.Value.Count >= 2).ToList();
        if (duplicates.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append($"路线 {fileName} 检测到 SyncPointId 重复（拒绝加载）：");
        foreach (var (id, idxs) in duplicates)
        {
            sb.Append($" \"{id}\" 出现于 waypoint 索引 [{string.Join(", ", idxs)}];");
        }
        throw new InvalidRouteException(sb.ToString());
    }

    public void SaveToFile(string filePath)
    {
        var json = JsonSerializer.Serialize(this, PathRecorder.JsonOptions);
        File.WriteAllText(filePath, json, new UTF8Encoding(false));
    }
}
