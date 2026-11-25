using BetterGenshinImpact.Core.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 线路来源扫描服务：联机仅扫描受管官方和独立本地导入；单机保留原内置目录。
/// </summary>
public class RouteDirectoryScanner
{
    private static readonly ILogger Logger = App.GetLogger<RouteDirectoryScanner>();
    private readonly string _assetsPath;
    private readonly bool _multiplayer;
    private readonly string _localRoot;

    public RouteDirectoryScanner(bool multiplayer = true)
        : this(AutoHoeingRoutePaths.BuiltinRoot, AutoHoeingRoutePaths.LocalRoot, AutoHoeingRoutePaths.LegacyLocalRoot, multiplayer) { }

    internal RouteDirectoryScanner(string builtinRoot, string localRoot, string legacyRoot, bool multiplayer = true)
    {
        _multiplayer = multiplayer;
        _localRoot = localRoot;
        _assetsPath = multiplayer ? builtinRoot : legacyRoot;
    }

    /// <summary>
    /// 扫描内置线路目录，返回所有有效的线路文件夹信息
    /// </summary>
    /// <returns>有效线路文件夹列表</returns>
    public List<BuiltinRouteFolder> ScanBuiltinRoutes()
    {
        var folders = new List<BuiltinRouteFolder>();

        // 官方与用户线路使用不同根和持久化标识，缺官方包时仍允许明确选择本地线路。
        var roots = new[]
        {
            (_assetsPath, "", _multiplayer ? "官方" : "本地旧目录"),
            (_localRoot, "local/", "本地导入")
        };
        foreach (var (root, prefix, source) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                // 递归识别纯变体目录；目录名 DebugRoutes 不再因无顶层 JSON 被漏扫。
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var count = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Length;
                    if (count == 0) continue;
                    folders.Add(new BuiltinRouteFolder
                    {
                        FolderName = prefix + Path.GetFileName(dir), FullPath = dir, RouteCount = count,
                        DisplayName = $"[{source}] {Path.GetFileName(dir)}"
                    });
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Logger.LogError(ex, "[线路扫描] 读取目录失败: {Path}", root);
            }
        }
        folders.Sort((left, right) => string.Compare(left.FolderName, right.FolderName, StringComparison.Ordinal));

        return folders;
    }

    /// <summary>
    /// 列出某内置线路文件夹下递归扫描到的所有 *.json 线路文件，返回稳定相对标识列表（R1.2）。
    /// 标识 = 相对该文件夹的相对路径（含变体子文件夹），用 '/' 归一分隔符，按序数升序排序保证稳定（R1.1）。
    /// 文件夹不存在 / 无 json → 返回空列表，不抛异常（R1.3）。
    /// 不修改 ScanBuiltinRoutes（§Unchanged Behavior 第 6 条），仅新增本方法。
    /// </summary>
    /// <param name="folderFullPath">BuiltinRouteFolder.FullPath</param>
    public List<RouteFileItem> ListRouteFiles(string folderFullPath)
    {
        var items = new List<RouteFileItem>();
        if (string.IsNullOrEmpty(folderFullPath) || !Directory.Exists(folderFullPath))
        {
            return items;   // 空列表，不抛（R1.3）
        }

        try
        {
            var files = Directory.GetFiles(folderFullPath, "*.json", SearchOption.AllDirectories);
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(folderFullPath, f).Replace('\\', '/');
                items.Add(new RouteFileItem { RelativeId = rel, FullPath = f });
            }
            items.Sort((a, b) => string.CompareOrdinal(a.RelativeId, b.RelativeId)); // 稳定排序
        }
        catch (Exception ex)
        {
            // 扫描失败不应阻断弹窗（可恢复异常）：记录并返回已收集项
            Logger.LogWarning(ex, "[线路清单] 扫描文件夹失败: {Path}", folderFullPath);
        }

        return items;
    }
}

/// <summary>
/// 文件夹内单条线路文件标识。
/// </summary>
public class RouteFileItem
{
    /// <summary>相对该文件夹的相对路径（'/' 分隔），唯一且稳定，作为映射键。</summary>
    public string RelativeId { get; set; } = "";

    /// <summary>线路文件绝对路径（仅 UI 展示/调试用，不入持久化）。</summary>
    public string FullPath { get; set; } = "";
}

/// <summary>
/// 内置线路文件夹信息
/// </summary>
public class BuiltinRouteFolder
{
    /// <summary>
    /// 文件夹名称（用作显示和标识）
    /// </summary>
    public string FolderName { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 文件夹完整路径
    /// </summary>
    public string FullPath { get; set; } = "";

    /// <summary>
    /// 文件夹中的路线文件数量
    /// </summary>
    public int RouteCount { get; set; }
}
