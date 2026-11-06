using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 客户端：把服务端权威轮换序列（UID 列表）映射回本地 PlayerInfo 顺序（纯函数，PBT 友好）。
/// multiplayer-server-authoritative-round-order §B2。
///
/// 决策：
/// - authoritativeUids 为 null/空 → 降级返回本地快照（旧服务端 / 未填 UID / 未生成）。
/// - 否则按 UID 映射回 snapshot 中的 PlayerInfo；映射不到的 UID（极端：该玩家刚掉线，
///   本地快照已无）用占位 PlayerInfo（UID 正确，PlayerName=UID），保证轮数与权威序列严格一致。
/// </summary>
public static class RoundHostOrderClientMapping
{
    public static List<PlayerInfo> BuildPlayerOrder(List<PlayerInfo> snapshot, List<string>? authoritativeUids)
    {
        snapshot ??= new List<PlayerInfo>();
        if (authoritativeUids == null || authoritativeUids.Count == 0)
            return snapshot;

        var byUid = snapshot.Where(p => p != null && !string.IsNullOrEmpty(p.PlayerUid))
                            .GroupBy(p => p.PlayerUid)
                            .ToDictionary(g => g.Key, g => g.First());
        return authoritativeUids.Select(uid =>
            byUid.TryGetValue(uid, out var p)
                ? p
                : new PlayerInfo { PlayerUid = uid, PlayerName = uid }).ToList();
    }

    // A v1 server can return the whole roster minus completed UIDs before applying its
    // round limit. Intersect that reply with the original candidate worlds; do not re-read
    // completion history or fill vacated rounds with later players.
    internal static List<PlayerInfo> BuildV1PlayerOrder(List<PlayerInfo> snapshot, List<string>? authoritativeUids,
        string initialHostUid, int roundLimit)
    {
        var order = BuildPlayerOrder(snapshot, authoritativeUids);
        if (authoritativeUids is not { Count: > 0 } || authoritativeUids.Count >= snapshot.Count)
            return order;
        var original = snapshot.Select(p => p.PlayerUid).ToHashSet(System.StringComparer.Ordinal);
        if (!original.Contains(initialHostUid) || string.IsNullOrEmpty(initialHostUid)
            || original.Any(string.IsNullOrEmpty) || original.Count != snapshot.Count
            || authoritativeUids.Any(uid => !original.Contains(uid)))
            throw new System.InvalidOperationException("旧协调器返回了裁剪轮次，但无法确认原候选世界");
        var candidates = new[] { initialHostUid }.Concat(original.Where(u => u != initialHostUid)
            .OrderBy(u => u, System.StringComparer.Ordinal)).Take(roundLimit).ToHashSet(System.StringComparer.Ordinal);
        return order.Where(p => candidates.Contains(p.PlayerUid)).ToList();
    }

    /// <summary>是否采用了服务端权威序列（true）还是降级本地快照（false）。仅用于日志区分。</summary>
    public static bool UsedAuthoritative(List<string>? authoritativeUids)
        => authoritativeUids != null && authoritativeUids.Count > 0;
}
