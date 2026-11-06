using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 路线文件扫描与解析，从 pathing 目录加载路线并提取怪物信息
/// </summary>
public class RouteInfoLoader
{
    private static readonly ILogger Logger = App.GetLogger<RouteInfoLoader>();

    // 正则：预计用时X秒
    private static readonly Regex TimeRegex = new(@"预计用时([\d\.]+)秒", RegexOptions.Compiled);
    // 正则：包含以下怪物：N只怪物名、M只怪物名。
    private static readonly Regex MonsterRegex = new(@"包含以下怪物：(.*?)。", RegexOptions.Compiled);

    /// <summary>
    /// 扫描 pathing 目录下所有 JSON 路线文件并解析
    /// </summary>
    public static List<RouteInfo> LoadRoutes(
        string pathingDir,
        MonsterInfoRepository monsterRepo,
        int ignoreRate,
        List<string> allGroupTags, bool strict = false)
    {
        var routes = new List<RouteInfo>();
        if (!Directory.Exists(pathingDir))
        {
            if (strict) throw new DirectoryNotFoundException(pathingDir);
            Logger.LogError("路线目录不存在: {Dir}", pathingDir);
            return routes;
        }

        var files = Directory.GetFiles(pathingDir, "*.json", SearchOption.AllDirectories);
        int index = 0;

        foreach (var filePath in files)
        {
            try
            {
                var route = ParseRouteFile(filePath, monsterRepo, ignoreRate, allGroupTags, strict);
                if (route != null)
                {
                    route.Index = ++index;
                    routes.Add(route);
                }
            }
            catch (Exception ex)
            {
                if (strict) throw new InvalidDataException("预判无法读取线路: " + filePath, ex);
                Logger.LogWarning("解析路线文件失败 {File}: {Msg}", filePath, ex.Message);
            }
        }

        Logger.LogInformation("共加载 {Count} 条路线", routes.Count);
        return routes;
    }

    private static RouteInfo? ParseRouteFile(
        string filePath,
        MonsterInfoRepository monsterRepo,
        int ignoreRate,
        List<string> allGroupTags, bool strict = false)
    {
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (strict && (!root.TryGetProperty("positions", out var positions)
            || positions.ValueKind != JsonValueKind.Array || positions.GetArrayLength() == 0))
            throw new InvalidDataException("预判线路缺少有效路径点: " + filePath);

        var info = root.TryGetProperty("info", out var infoEl) ? infoEl : default;
        var description = info.ValueKind != JsonValueKind.Undefined
            && info.TryGetProperty("description", out var descEl)
            ? descEl.GetString() ?? ""
            : "";

        var route = new RouteInfo
        {
            FileName = Path.GetFileName(filePath),
            FullPath = filePath,
            MapName = info.ValueKind != JsonValueKind.Undefined
                && info.TryGetProperty("map_name", out var mapEl)
                ? mapEl.GetString() ?? "Teyvat"
                : "Teyvat"
        };

        // 从 info.tags 收集标签
        if (info.ValueKind != JsonValueKind.Undefined
            && info.TryGetProperty("tags", out var tagsEl)
            && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                var t = tag.GetString();
                if (!string.IsNullOrEmpty(t)) route.Tags.Add(t);
            }
        }

        // 解析 description
        var (time, monsterInfo) = ParseDescription(description);
        route.EstimatedTime = time;
        route.MonsterInfo = monsterInfo;

        // 计算怪物统计
        CalculateMonsterStats(route, monsterRepo);

        // ignoreRate 过滤
        ApplyIgnoreRate(route, ignoreRate);

        // 反查补 tag：路径名+描述中匹配用户标签
        var textToMatch = filePath + " " + description;
        foreach (var tag in allGroupTags)
        {
            if (textToMatch.Contains(tag) && !route.Tags.Contains(tag))
                route.Tags.Add(tag);
        }

        // 去重
        route.Tags = route.Tags.Distinct().ToList();
        route.AdjustedTime = route.EstimatedTime;

        return route;
    }

    /// <summary>
    /// 从 description 中提取预计用时和怪物信息
    /// </summary>
    public static (double time, Dictionary<string, int> monsters) ParseDescription(string desc)
    {
        double time = 60; // 默认60秒
        var monsters = new Dictionary<string, int>();

        if (string.IsNullOrEmpty(desc)) return (time, monsters);

        var timeMatch = TimeRegex.Match(desc);
        if (timeMatch.Success && double.TryParse(timeMatch.Groups[1].Value, out var t))
            time = t;

        var monsterMatch = MonsterRegex.Match(desc);
        if (monsterMatch.Success)
        {
            var monsterList = monsterMatch.Groups[1].Value.Split('、');
            foreach (var monsterStr in monsterList)
            {
                var parts = monsterStr.Split('只');
                if (parts.Length == 2
                    && double.TryParse(parts[0].Trim(), out var count)
                    && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    monsters[parts[1].Trim()] = (int)Math.Ceiling(count);
                }
            }
        }

        return (time, monsters);
    }

    private static void CalculateMonsterStats(RouteInfo route, MonsterInfoRepository monsterRepo)
    {
        foreach (var (monsterName, count) in route.MonsterInfo)
        {
            var monster = monsterRepo.FindByName(monsterName);
            if (monster == null) continue;

            if (monster.IsNormal)
            {
                route.NormalMonsterCount += count;
                route.NormalMoraGain += count * 40.5 * monster.MoraRate;
            }
            else if (monster.IsElite)
            {
                route.EliteMonsterCount += count;
                route.EliteMoraGain += count * 200 * monster.MoraRate;
                route.OriginalEliteCount += count;
            }

            if (monster.MoraRate > 1)
            {
                if (!route.Tags.Contains("高收益")) route.Tags.Add("高收益");
                if (monster.IsElite && !route.Tags.Contains("精英高收益"))
                    route.Tags.Add("精英高收益");
            }

            if (monster.Tags.Count > 0)
                route.Tags.AddRange(monster.Tags);
        }
    }

    private static void ApplyIgnoreRate(RouteInfo route, int ignoreRate)
    {
        if (ignoreRate <= 0 || route.EliteMonsterCount <= 0) return;

        var protectTags = new[] { "精英高收益", "高危", "传奇" };
        if (protectTags.Any(tag => route.Tags.Contains(tag))) return;

        var ratio = (double)route.NormalMonsterCount / route.EliteMonsterCount;
        if (ratio >= ignoreRate)
        {
            route.EliteMonsterCount = 0;
            route.EliteMoraGain = 0;
        }
    }
}
