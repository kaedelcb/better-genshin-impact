using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BetterGenshinImpact.Core.Script;

/// <summary>
/// 在 BGI 脚本宿主侧根据项目生命周期与 pathingScript 调用生成第三方 JS 任务进度。
/// 本适配器自身不解析日志；脚本既有日志文本由独立的 ScriptTaskProgressLogParser /
/// ScriptTaskProgressLogSink 只读观察并作为补充（不读日志文件、不修改脚本源码）。
/// </summary>
internal static class ScriptTaskProgressAdapter
{
    private enum AdapterKind
    {
        None,
        CollectionCd,
        ArtifactBulk,
        ArtifactGroup,
        Fishing
    }

    private static readonly object Sync = new();
    private static AdapterKind _kind;
    private static DateTimeOffset _projectStartedAt;
    private static DateTimeOffset _phaseStartedAt;
    private static string? _routePath;
    private static string? _stage;
    private static int _primaryRouteIndex;
    private static bool _routeRunning;
    private static int? _maxRuntimeMinutes;

    public static void BeginProject(string? folderName, object? settings)
    {
        var kind = ResolveKind(folderName);
        lock (Sync)
        {
            ResetUnsafe();
            _kind = kind;
            if (kind == AdapterKind.None) return;

            _projectStartedAt = DateTimeOffset.Now;
            _phaseStartedAt = _projectStartedAt;
            _stage = kind switch
            {
                AdapterKind.CollectionCd => "准备采集",
                AdapterKind.ArtifactBulk => "读取记录与规划路线",
                AdapterKind.ArtifactGroup => "联机准备与角色识别",
                AdapterKind.Fishing => "筛选钓鱼点",
                _ => null
            };
            _maxRuntimeMinutes = kind == AdapterKind.CollectionCd
                ? ReadPositiveInt(settings, "maxRuntimeMinutes")
                : null;
        }
    }

    public static void RouteStarted(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Sync)
        {
            if (_kind == AdapterKind.None) return;
            _routePath = path.Replace('\\', '/');
            var (stage, primary) = ClassifyRoute(_kind, _routePath);
            _stage = stage;
            if (primary) _primaryRouteIndex++;
            _routeRunning = true;
            _phaseStartedAt = DateTimeOffset.Now;
        }
    }

    public static void RouteCompleted(string? path)
    {
        lock (Sync)
        {
            if (_kind == AdapterKind.None || !_routeRunning) return;
            var normalized = path?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(normalized, _routePath, StringComparison.OrdinalIgnoreCase)) return;

            _routeRunning = false;
            _phaseStartedAt = DateTimeOffset.Now;
            _stage = _kind switch
            {
                AdapterKind.Fishing when IsFishingPoint(_routePath) => "垂钓与点位处理",
                AdapterKind.CollectionCd => "路线完成，整理采集记录",
                AdapterKind.ArtifactBulk => "路线完成，更新狗粮记录",
                AdapterKind.ArtifactGroup => "路线完成，等待联机步骤",
                _ => "路线完成，处理中"
            };
        }
    }

    public static string? GetProgressText()
    {
        lock (Sync)
        {
            if (_kind == AdapterKind.None || _projectStartedAt == default) return null;
            var now = DateTimeOffset.Now;
            var title = _kind switch
            {
                AdapterKind.CollectionCd => "采集CD",
                AdapterKind.ArtifactBulk => "狗粮批发",
                AdapterKind.ArtifactGroup => "圣遗物团购",
                AdapterKind.Fishing => "自动钓鱼",
                _ => "JS任务"
            };

            var pieces = new List<string> { title, _stage ?? "运行中" };
            if (_primaryRouteIndex > 0)
            {
                pieces.Add(_kind == AdapterKind.Fishing
                    ? $"第 {_primaryRouteIndex} 个钓鱼点"
                    : $"第 {_primaryRouteIndex} 条主要路线");
            }

            if (_routePath != null)
            {
                var fileName = Path.GetFileNameWithoutExtension(_routePath);
                if (!string.IsNullOrWhiteSpace(fileName)) pieces.Add(Compact(fileName, 32));
            }

            pieces.Add($"阶段 {FormatDuration(now - _phaseStartedAt)}");
            pieces.Add($"总用时 {FormatDuration(now - _projectStartedAt)}");
            if (_maxRuntimeMinutes is > 0)
            {
                var remaining = TimeSpan.FromMinutes(_maxRuntimeMinutes.Value) - (now - _projectStartedAt);
                pieces.Add(remaining > TimeSpan.Zero ? $"限时剩余 {FormatDuration(remaining)}" : "已到运行限时");
            }
            return string.Join(" · ", pieces);
        }
    }

    public static void Clear()
    {
        lock (Sync) ResetUnsafe();
    }

    private static (string Stage, bool Primary) ClassifyRoute(AdapterKind kind, string path)
    {
        return kind switch
        {
            AdapterKind.CollectionCd => IsCollectionAuxiliary(path)
                ? ("采集辅助步骤", false)
                : ("采集路线执行中", true),
            AdapterKind.ArtifactBulk => ClassifyArtifactBulk(path),
            AdapterKind.ArtifactGroup => path.Contains("/占位/", StringComparison.OrdinalIgnoreCase)
                ? ("成员占位路线", true)
                : path.Contains("/额外/", StringComparison.OrdinalIgnoreCase)
                    ? ("额外路线", true)
                    : ("联机收尾路线", true),
            AdapterKind.Fishing => IsFishingPoint(path)
                ? ("前往钓鱼点", true)
                : path.Contains("pathing_statues", StringComparison.OrdinalIgnoreCase)
                    ? ("前往七天神像", false)
                    : ("钓鱼辅助路线", false),
            _ => ("路线执行中", false)
        };
    }

    private static (string Stage, bool Primary) ClassifyArtifactBulk(string path)
    {
        if (!path.Contains("ArtifactsPath", StringComparison.OrdinalIgnoreCase)) return ("队伍或时间调整", false);
        if (path.Contains("高铁", StringComparison.OrdinalIgnoreCase)) return ("高铁路线", true);
        if (path.Contains("联机收尾", StringComparison.OrdinalIgnoreCase)) return ("联机收尾路线", true);
        if (path.Contains("收尾", StringComparison.OrdinalIgnoreCase)) return ("收尾路线", true);
        if (path.Contains("额外", StringComparison.OrdinalIgnoreCase)) return ("额外路线", true);
        if (path.Contains("激活", StringComparison.OrdinalIgnoreCase)) return ("激活路线", true);
        return ("普通狗粮路线", true);
    }

    private static bool IsCollectionAuxiliary(string path) =>
        path.Contains("学习螃蟹技能", StringComparison.OrdinalIgnoreCase)
        || path.Contains("调为白天", StringComparison.OrdinalIgnoreCase)
        || path.Contains("调为夜晚", StringComparison.OrdinalIgnoreCase);

    private static bool IsFishingPoint(string? path) =>
        path?.StartsWith("assets/pathing/", StringComparison.OrdinalIgnoreCase) == true;

    private static AdapterKind ResolveKind(string? folderName)
    {
        if (string.Equals(folderName, "采集cd管理", StringComparison.OrdinalIgnoreCase)) return AdapterKind.CollectionCd;
        if (string.Equals(folderName, "AAA-Artifacts-Bulk-Supply", StringComparison.OrdinalIgnoreCase)) return AdapterKind.ArtifactBulk;
        if (string.Equals(folderName, "ArtifactsGroupPurchasing", StringComparison.OrdinalIgnoreCase)) return AdapterKind.ArtifactGroup;
        if (string.Equals(folderName, "AutoFishingTeyvat", StringComparison.OrdinalIgnoreCase)) return AdapterKind.Fishing;
        return AdapterKind.None;
    }

    private static int? ReadPositiveInt(object? settings, string key)
    {
        if (settings is not IDictionary<string, object?> values
            || !values.TryGetValue(key, out var raw)
            || raw == null) return null;
        try
        {
            var value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
        totalSeconds -= totalSeconds % 10;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}时{minutes}分{seconds}秒" : $"{minutes}分{seconds}秒";
    }

    private static string Compact(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static void ResetUnsafe()
    {
        _kind = AdapterKind.None;
        _projectStartedAt = default;
        _phaseStartedAt = default;
        _routePath = null;
        _stage = null;
        _primaryRouteIndex = 0;
        _routeRunning = false;
        _maxRuntimeMinutes = null;
    }
}
