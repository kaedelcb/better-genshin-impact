#nullable enable

using System;
using System.IO;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 联机锄地"从本地文件夹导入线路"的纯决策 + 递归拷贝 helper。
/// spec: multiplayer-hoeing-import-local-route-folder / design.md §C1。
///
/// 纯决策函数（IsValidRouteFolder / ResolveTargetName / IsInsideAssets / NeedsOverwriteConfirm）
/// 无外部依赖，PBT 友好；CopyDirectoryRecursive 仅做"创建目录 + 逐文件拷贝覆盖"，
/// 绝不删除任何目录或文件（受保护路径安全约束，Req 9.1/9.2）。
/// 仅供 ScriptControlViewModel.ShowHoeingSettingsDialog 调用，不被运行时（AutoHoeingTask）引用。
/// </summary>
public static class LocalRouteFolderImporter
{
    /// <summary>
    /// Valid_Route_Folder 判定：递归含至少一个 *.json（复用 RouteDirectoryScanner.ScanBuiltinRoutes 语义，Req 3.1）。
    /// 目录不存在 / 无任何 json → false。该方法触碰文件系统（GetFiles），不是纯函数，但行为确定、易测。
    /// </summary>
    public static bool IsValidRouteFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return false;
        if (!Directory.Exists(folderPath)) return false;
        try
        {
            return Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories).Length > 0;
        }
        catch (Exception)
        {
            // 无权限/IO 异常时按"无效"处理，由调用方走失败提示分支（Req 6）。
            return false;
        }
    }

    /// <summary>
    /// 目标文件夹名 = 源文件夹名（Path.GetFileName，去尾部分隔符，Req 3.3）。纯函数。
    /// 返回空表示无法解析（异常输入），调用方应终止导入。
    /// </summary>
    public static string ResolveTargetName(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return string.Empty;
        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }

    /// <summary>
    /// 源是否已位于 Assets 目录之内（含 Assets 自身的直接子目录或更深）。纯函数（路径规范化比较，Req 3.5）。
    /// 用于跳过自拷贝/同路径拷贝。比较时统一规范化为全路径 + 末尾分隔符，忽略大小写（Windows）。
    /// </summary>
    public static bool IsInsideAssets(string? sourcePath, string? assetsDir)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(assetsDir)) return false;
        string Normalize(string p)
        {
            var full = Path.GetFullPath(p);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        }
        var src = Normalize(sourcePath);
        var root = Normalize(assetsDir);
        return src.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否需要弹覆盖确认：目标已存在即需要确认（OQ-2，Req 4.1）。纯函数。
    /// </summary>
    public static bool NeedsOverwriteConfirm(bool targetExists) => targetExists;

    /// <summary>
    /// 递归拷贝目录内容（含变体子目录 A变体/B变体/...，Req 3.4）。
    /// 只创建目录 + 逐文件拷贝（File.Copy(overwrite)），绝不删除任何目录或文件（Req 9.1/9.2）。
    /// 抛出的 IOException / UnauthorizedAccessException 由调用方捕获并提示（Req 6.2/6.3）。
    /// </summary>
    public static void CopyDirectoryRecursive(string sourceDir, string targetDir, bool overwrite)
    {
        var source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("导入源与目标目录不能相同或互相包含");
        AutoHoeingRouteUpdater.RequireNoLinks(source);
        AutoHoeingRouteUpdater.RequireNoLinks(target);
        Directory.CreateDirectory(targetDir);   // 确保目标目录存在（不删除已有内容）
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite);   // 覆盖=逐文件复制覆盖同名文件，不删除目标目录
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir, overwrite);
        }
    }
}
