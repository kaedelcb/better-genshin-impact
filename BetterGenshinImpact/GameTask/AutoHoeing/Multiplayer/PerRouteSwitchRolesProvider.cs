#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoSwitchRoles;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机锄地「按线路切换角色」Provider（hoeing-multiplayer-per-route-switch-roles）。
/// 封装：运行时 RouteId 推导（{FolderName}/{相对路径}）+ 查映射 + RouteNeedsSwitch 判定 +
/// 构造 PerRouteSwitchHook（其 SwitchAsync 内部 new AutoSwitchRolesTask + MultiplayerSwitchOverride）。
/// 由 AutoHoeingTask 构造并注入 RouteExecutionEngine；仅联机 + 配了角色时生效，单机/未配线路返回 null Hook。
/// </summary>
public sealed class PerRouteSwitchRolesProvider
{
    private readonly Dictionary<string, RouteRoleEntry> _perRouteSwitchRoles;
    private readonly PathingPartyConfig? _partyConfig;
    private readonly string? _groupName;
    private readonly ILogger _logger;

    public PerRouteSwitchRolesProvider(
        Dictionary<string, RouteRoleEntry> perRouteSwitchRoles,
        PathingPartyConfig? partyConfig,
        string? groupName,
        ILogger logger)
    {
        _perRouteSwitchRoles = perRouteSwitchRoles ?? new Dictionary<string, RouteRoleEntry>();
        _partyConfig = partyConfig;
        _groupName = groupName;
        _logger = logger;
    }

    /// <summary>
    /// 运行时 RouteId 推导：找到 route.FullPath 所属的内置线路文件夹，
    /// 返回 "{FolderName}/{相对该文件夹的相对路径}"（'/' 归一）；不属任何内置文件夹 → null。
    /// 与 UI 写入键 {FolderName}/{RelativeId} 对齐（§运行时 RouteId 推导）。
    /// </summary>
    public string? DeriveRouteId(RouteInfo route)
    {
        if (route == null || string.IsNullOrEmpty(route.FullPath)) return null;
        var routeFull = Path.GetFullPath(route.FullPath);
        var folders = new RouteDirectoryScanner().ScanBuiltinRoutes();
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder.FullPath)) continue;
            var folderFull = Path.GetFullPath(folder.FullPath);
            var prefix = folderFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? folderFull
                : folderFull + Path.DirectorySeparatorChar;
            if (routeFull.StartsWith(prefix))
            {
                var rel = Path.GetRelativePath(folderFull, routeFull).Replace('\\', '/');
                return $"{folder.FolderName}/{rel}";
            }
        }
        return null;
    }

    /// <summary>
    /// 为某条线路构造切角色 Hook：未配映射/不属内置文件夹/无有效切换 → null（不注入，安全降级 R4.2）。
    /// </summary>
    public PerRouteSwitchHook? BuildHookForRoute(RouteInfo route)
    {
        if (_perRouteSwitchRoles.Count == 0) return null;
        var routeId = DeriveRouteId(route);
        if (routeId == null) return null;
        if (!_perRouteSwitchRoles.TryGetValue(routeId, out var entry)) return null;
        if (!PerRouteSwitchRolesDecisions.RouteNeedsSwitch(entry)) return null;

        return new PerRouteSwitchHook
        {
            RouteHasSwitch = true,
            SwitchAsync = ct => ExecuteRouteSwitchAsync(entry, ct),
        };
    }

    private async Task ExecuteRouteSwitchAsync(RouteRoleEntry entry, CancellationToken ct)
    {
        var res = new AutoSwitchRolesResources(_logger);
        if (!res.LoadAliasMap())
        {
            _logger.LogWarning("[联机切角色] 别名表加载失败，跳过");
            return;
        }

        var targets = PerRouteSwitchRolesDecisions.ResolveEntryTargets(entry, res.AliasMap); // 长度 2
        if (targets.All(t => t == null))
        {
            _logger.LogInformation("[联机切角色] 无号位需切换，跳过");
            return;
        }

        var mpOverride = new MultiplayerSwitchOverride
        {
            SkipSwitchParty = true,
            IsPairingPageOpen = () =>
            {
                using var capture = CaptureToRectArea();
                using var ra = capture.Find(RecognitionAssets.Get("QuickTeleport", "MapCloseButton", capture));
                return ra.IsExist();
            },
        };

        var switchSettings = new Dictionary<string, object?>
        {
            ["option"] = "推荐-非快速配对模式 @Tool_tingsu",
            ["position1"] = targets[0] ?? "",
            ["position2"] = targets[1] ?? "",
            ["position3"] = "",
            ["position4"] = "",
            ["switchPartyName"] = "",
        };

        await new AutoSwitchRolesTask(_partyConfig, switchSettings, _groupName, mpOverride).Start(ct);
    }
}
