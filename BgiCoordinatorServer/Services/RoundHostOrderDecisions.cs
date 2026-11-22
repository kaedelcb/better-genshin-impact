using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>从冻结的房间名单生成服务端权威轮换顺序。</summary>
public static class RoundHostOrderDecisions
{
    internal static string[] SplitWhitelist(string? whitelist) =>
        whitelist?.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static IEnumerable<string> NameCandidates(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        // 与 BGI 的 WhitelistMatchDecisions 共用括号规则，不使用 OCR 模糊匹配。
        name = name.Replace("\n", "").Replace("\r", "").Replace("\t", "").Replace(" ", "").Trim();
        int open = name.IndexOfAny(['(', '（']);
        if (open <= 0)
        {
            if (name.Length != 0) yield return name;
            yield break;
        }
        var main = name[..open].Trim();
        if (main.Length != 0) yield return main;
        int close = name.IndexOfAny([')', '）']);
        if (close <= open) yield break;
        var remark = name[(open + 1)..close].Trim();
        if (remark.Length != 0) yield return remark;
    }

    /// <summary>有匹配则按白名单位置排序；否则房主优先。排序后才裁剪已完成世界。</summary>
    public static List<string> Build(IEnumerable<PlayerInfo> players, string hostUid,
        IReadOnlySet<string>? excludeUids = null, string? whitelist = null) =>
        Build(players, hostUid, excludeUids, SplitWhitelist(whitelist), out _);

    internal static List<string> Build(IEnumerable<PlayerInfo> players, string hostUid,
        IReadOnlySet<string>? excludeUids, IReadOnlyList<string> entries, out int matched)
    {
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
            foreach (var name in NameCandidates(entries[i]))
                positions.TryAdd(name, i);

        var ranked = (players ?? []).Where(p => p != null && !string.IsNullOrEmpty(p.PlayerUid))
            .GroupBy(p => p.PlayerUid, StringComparer.Ordinal)
            .Select(g => (Uid: g.Key, Rank: g.SelectMany(p => NameCandidates(p.PlayerName))
                .Select(name => positions.GetValueOrDefault(name, int.MaxValue)).DefaultIfEmpty(int.MaxValue).Min()))
            .ToList();
        matched = ranked.Count(p => p.Rank != int.MaxValue);
        var ordered = matched > 0 ? ranked.OrderBy(p => p.Rank) : ranked.OrderBy(p => p.Uid != hostUid);
        return ordered.ThenBy(p => p.Uid, StringComparer.Ordinal).Select(p => p.Uid)
            .Where(uid => excludeUids?.Contains(uid) != true).ToList();
    }
}
