#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 单个变体文件的扫描结果（route-variant-sync-by-logical-id spec / §15.2 / R15）。
/// </summary>
public sealed record VariantFileEntry(
    string FileName,        // 仅文件名（如 "B001南陵传奇_a.json"）
    string AbsolutePath,    // 绝对路径（变体在不同子目录，下游不能再 Path.Combine 拼）
    string BaseName,        // 去后缀的线路基名（= 配对键 = syncId 命名空间，非空）
    string VariantFolder,   // 所属变体子文件夹名（"A变体".."D变体"，非空）
    DateTime FileMtime);

/// <summary>
/// 按"线路基名"分组扫描总线路文件夹下变体子文件夹（A变体/B变体/C变体/D变体）内的 *.json。
/// 静态类 + 进程内缓存（per topDir）+ mtime 失效；UI 折叠面板每次打开 forceRefresh=true。
/// 只纳入位于 X变体 子文件夹里的文件；不在变体子文件夹里的（普通老线路）忽略。
/// </summary>
public static class RouteVariantScanner
{
    // 缓存：topDir → (扫描时各文件 mtime 字典, 结果)
    private static readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>整包替换即使保留了相同 mtime，也必须丢弃旧绝对路径及变体扫描结果。</summary>
    public static void InvalidateUnderRoot(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = full + Path.DirectorySeparatorChar;
        lock (_lock)
        {
            foreach (var key in _cache.Keys.Where(key =>
                string.Equals(Path.GetFullPath(key), full, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(key).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                _cache.Remove(key);
        }
    }

    private sealed class CacheEntry
    {
        public Dictionary<string, DateTime> FileMtimes = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<VariantFileEntry>> Result = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 递归扫描 topDir 下所有 *.json，仅保留位于变体子文件夹（A变体..D变体）里的文件，
    /// 按"去后缀基名"分组。返回 Dict&lt;基名, List&lt;VariantFileEntry&gt;&gt;。
    /// </summary>
    /// <param name="topDir">总线路文件夹绝对路径（如 ...Assets/传奇）；递归扫子目录</param>
    /// <param name="forceRefresh">true=忽略缓存重扫；UI 折叠面板每次打开置 true</param>
    public static Dictionary<string, List<VariantFileEntry>> ScanVariants(
        string topDir, bool forceRefresh = false)
    {
        if (string.IsNullOrEmpty(topDir) || !Directory.Exists(topDir))
            return new Dictionary<string, List<VariantFileEntry>>(StringComparer.Ordinal);

        lock (_lock)
        {
            // 枚举当前 mtime 快照
            var current = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(topDir, "*.json", SearchOption.AllDirectories))
            {
                try { current[f] = File.GetLastWriteTimeUtc(f); }
                catch { /* 文件刚被删除，忽略（可恢复：下次扫描自然反映最新目录状态） */ }
            }

            if (!forceRefresh
                && _cache.TryGetValue(topDir, out var cached)
                && DictEquals(cached.FileMtimes, current))
            {
                return cached.Result;
            }

            // 重扫
            var grouped = new Dictionary<string, List<VariantFileEntry>>(StringComparer.Ordinal);
            foreach (var (path, mtime) in current)
            {
                var folder = RouteVariantNaming.TryGetVariantFolder(path);
                if (folder == null) continue;   // 不在 X变体 子文件夹 → 不算变体

                var fileName = Path.GetFileName(path);
                var baseName = RouteVariantNaming.StripBaseName(fileName, folder);
                if (string.IsNullOrEmpty(baseName)) continue;

                if (!grouped.TryGetValue(baseName, out var list))
                {
                    list = new List<VariantFileEntry>();
                    grouped[baseName] = list;
                }
                list.Add(new VariantFileEntry(
                    FileName: fileName,
                    AbsolutePath: path,
                    BaseName: baseName,
                    VariantFolder: folder,
                    FileMtime: mtime));
            }

            _cache[topDir] = new CacheEntry { FileMtimes = current, Result = grouped };
            return grouped;
        }
    }

    /// <summary>
    /// 扫描多个总目录并按基名合并分组（供运行时变体替换用）。同一基名跨目录合并；
    /// 同（变体文件夹+文件名）只保留先扫到的一份。
    /// </summary>
    public static Dictionary<string, List<VariantFileEntry>> ScanMultipleDirs(
        IEnumerable<string> topDirs, bool forceRefresh = false)
    {
        var merged = new Dictionary<string, List<VariantFileEntry>>(StringComparer.Ordinal);
        if (topDirs == null) return merged;
        foreach (var dir in topDirs)
        {
            var one = ScanVariants(dir, forceRefresh);
            foreach (var (baseName, entries) in one)
            {
                if (!merged.TryGetValue(baseName, out var list))
                {
                    list = new List<VariantFileEntry>();
                    merged[baseName] = list;
                }
                foreach (var e in entries)
                {
                    // 去重：同变体文件夹 + 同文件名只留一份（避免跨目录重复）
                    if (!list.Any(x =>
                            string.Equals(x.VariantFolder, e.VariantFolder, StringComparison.Ordinal)
                            && string.Equals(x.FileName, e.FileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(e);
                    }
                }
            }
        }
        return merged;
    }

    /// <summary>
    /// 供 UI 变体偏好面板用（R15.6）：扫描多个候选目录，按"总文件夹名"分组，
    /// 返回每个总文件夹下实际存在的变体子文件夹集合（按 A→B→C→D 排序）。
    /// 用户对每个总文件夹选一次变体（点 传奇 → 弹 A/B/C/D），整个文件夹跟随该变体。
    /// </summary>
    public static Dictionary<string, List<string>> ScanTopFolders(
        IEnumerable<string> candidateDirs, bool forceRefresh = false)
    {
        // 总文件夹名 → 该文件夹下出现过的变体子文件夹集合
        var topToVariants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (candidateDirs == null)
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var dir in candidateDirs)
        {
            var byBase = ScanVariants(dir, forceRefresh);
            foreach (var entries in byBase.Values)
            {
                foreach (var e in entries)
                {
                    var top = RouteVariantNaming.TryGetTopFolderName(e.AbsolutePath);
                    if (string.IsNullOrEmpty(top)) continue;
                    if (!topToVariants.TryGetValue(top, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        topToVariants[top] = set;
                    }
                    set.Add(e.VariantFolder);
                }
            }
        }

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (top, set) in topToVariants)
        {
            result[top] = RouteVariantNaming.VariantFolders.Where(set.Contains).ToList();
        }
        return result;
    }

    /// <summary>
    /// 供 UI 用：读取某总文件夹下某变体子文件夹里的"变体说明.txt"（R15.11）。
    /// 在所有候选目录里找到第一个匹配 (topFolderName, variantFolder) 的实际目录并读说明。
    /// 读不到 / 无文件 → 返回空字符串（不阻塞）。
    /// </summary>
    public static string ReadVariantDescription(
        IEnumerable<string> candidateDirs, string topFolderName, string variantFolder)
    {
        if (candidateDirs == null || string.IsNullOrEmpty(topFolderName) || string.IsNullOrEmpty(variantFolder))
            return string.Empty;

        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                // dir 是候选根（如 Assets）；递归找匹配的 {topFolderName}\{variantFolder}\变体说明.txt
                foreach (var descPath in Directory.EnumerateFiles(
                    dir, RouteVariantNaming.VariantDescriptionFileName, SearchOption.AllDirectories))
                {
                    var vf = Path.GetFileName(Path.GetDirectoryName(descPath) ?? string.Empty);
                    if (!string.Equals(vf, variantFolder, StringComparison.Ordinal)) continue;
                    var top = RouteVariantNaming.TryGetTopFolderName(
                        Path.Combine(Path.GetDirectoryName(descPath) ?? string.Empty, "x.json"));
                    if (!string.Equals(top, topFolderName, StringComparison.Ordinal)) continue;

                    var raw = File.ReadAllText(descPath);
                    return RouteVariantNaming.NormalizeDescription(raw);
                }
            }
            catch (Exception ex)
            {
                // 读说明失败属可恢复（说明只是辅助展示）：跳过该目录继续找。
                TaskControl.Logger.LogWarning(ex,
                    "[RouteVariantScanner] 读取变体说明失败 top={Top} variant={Variant} dir={Dir}",
                    topFolderName, variantFolder, dir);
            }
        }
        return string.Empty;
    }

    private static bool DictEquals(
        Dictionary<string, DateTime> a, Dictionary<string, DateTime> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var v2) || v != v2) return false;
        return true;
    }
}
