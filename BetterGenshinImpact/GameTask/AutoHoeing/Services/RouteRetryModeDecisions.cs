using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 线路重试模式白名单决策（纯函数，PBT 友好，无外部依赖）。
/// hoeing-multiplayer-route-retry-mode spec。
/// 形态照抄 <see cref="RouteKeywordFilterDecisions"/>：全/半角分隔、Trim、OrdinalIgnoreCase 去重、Contains 匹配。
/// </summary>
public static class RouteRetryModeDecisions
{
    // 同时支持全角「，」与半角「,」分隔
    private static readonly char[] Separators = { ',', '，' };

    /// <summary>
    /// 解析逗号分隔（全/半角）的关键词字符串为去重无序集合。
    /// - 按 ',' 与 '，' 切分
    /// - 每个关键词 Trim 首尾空白
    /// - 丢弃空白关键词
    /// - 大小写不敏感去重（OrdinalIgnoreCase）
    /// 纯函数：相同输入恒产出相同结果。
    /// </summary>
    public static IReadOnlyCollection<string> ParseKeywords(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;

        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var kw = part.Trim();
            if (kw.Length > 0) set.Add(kw);
        }
        return set;
    }

    /// <summary>
    /// 判断给定线路文件名（应已去除 .json 扩展名）是否命中任一关键词而启用重试模式。
    /// - 空集合恒返回 false（不启用）
    /// - OrdinalIgnoreCase Contains 任一关键词即启用
    /// 纯函数。
    /// </summary>
    public static bool IsRetryRoute(string? fileNameWithoutExt, IReadOnlyCollection<string> keywords)
    {
        if (keywords == null || keywords.Count == 0) return false;
        if (string.IsNullOrEmpty(fileNameWithoutExt)) return false;

        foreach (var kw in keywords)
        {
            if (fileNameWithoutExt.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
