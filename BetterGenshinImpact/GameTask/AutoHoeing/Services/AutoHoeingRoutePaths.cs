using System;
using System.IO;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>执行、设置扫描、变体扫描和打开目录共用同一来源协议。本地线路从不放进受管 current。</summary>
public static class AutoHoeingRoutePaths
{
    public static string ManagedRoot => Path.Combine(Global.UserRoot, "AutoHoeingRoutes");
    public static string BuiltinRoot => Path.Combine(ManagedRoot, "current", "builtin");
    public static string PathingRoot => Path.Combine(ManagedRoot, "current", "pathing");
    public static string LocalRoot => Path.Combine(Global.UserRoot, "AutoHoeingLocalRoutes");
    public static string LegacyLocalRoot => Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoHoeing", "Assets");

    public static string ResolveSelection(string? selection, bool multiplayer)
    {
        selection = string.IsNullOrWhiteSpace(selection) ? "DebugRoutes" : selection;
        var root = multiplayer ? BuiltinRoot : LegacyLocalRoot;
        if (selection.StartsWith("local/", StringComparison.Ordinal))
        { root = LocalRoot; selection = selection[6..]; }
        else if (selection.StartsWith("legacy-local/", StringComparison.Ordinal))
        {
            if (multiplayer)
                throw new InvalidDataException("联机锄地旧线路目录已弃用，请重新选择官方线路或本地导入线路。");
            root = LegacyLocalRoot;
            selection = selection[13..];
        }
        if (selection is "." or ".." || selection.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || selection.Contains('/') || selection.Contains('\\'))
            throw new InvalidDataException("线路目录名无效");
        return Path.Combine(root, selection);
    }

    public static bool IsLocalSelection(string? selection) => selection?.StartsWith("local/", StringComparison.Ordinal) == true
        || selection?.StartsWith("legacy-local/", StringComparison.Ordinal) == true;
}
