#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 踢人判定白名单合并纯决策逻辑。
/// 从 AutoPartyTask.KickStrangersAsync 抽出，PBT 友好。
/// spec: sync-roomwhitelist-to-roomconfig
/// </summary>
public static class KickWhitelistDecisions
{
    /// <summary>
    /// 踢人判定白名单 = 上报名单 ∪ 配置白名单（去重、忽略大小写、过滤空串）。
    /// 与 AutoPartyTask.KickStrangersAsync 原有 Concat 逻辑逐字节一致。
    /// </summary>
    public static string[] MergeAllowedNames(
        IEnumerable<string> reportedNames,
        IEnumerable<string>? configuredWhitelist)
    {
        return (reportedNames ?? Array.Empty<string>())
            .Concat(configuredWhitelist ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}