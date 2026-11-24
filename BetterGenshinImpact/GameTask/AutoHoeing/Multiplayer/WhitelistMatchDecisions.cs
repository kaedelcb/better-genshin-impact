#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 白名单匹配纯决策逻辑（无外部依赖，PBT 友好）。
/// 从 AutoPartyTask.IsInWhitelist 抽出，OCR 侧与白名单侧共用同一括号拆分，
/// 消除"一侧拆一侧不拆"的不对称根因（feature: hoeing-whitelist-entry-bracket-remark-not-split-fix）。
/// </summary>
public static class WhitelistMatchDecisions
{
    /// <summary>
    /// 把显示名拆成候选名集合。
    /// "红姐(大哥)" / "红姐（大哥）" → ["红姐", "大哥"]；无括号 → [display.Trim()]。
    /// 只取第一对括号，与 OCR 侧现有逻辑语义等价（bracketIdx>0 才拆、endIdx>bracketIdx 才加备注名）。
    /// 过滤空串。
    /// </summary>
    public static IReadOnlyList<string> ExtractNameCandidates(string display)
    {
        var result = new List<string>();
        if (display == null)
        {
            return result;
        }

        // 清理 OCR 可能引入的换行符/回车符/控制字符/空格（原神名字不能带空格）
        display = display.Replace("\n", "").Replace("\r", "").Replace("\t", "").Replace(" ", "");

        var bracketIdx = display.IndexOfAny(new[] { '(', '（' });
        if (bracketIdx > 0)
        {
            var main = display[..bracketIdx].Trim();
            if (!string.IsNullOrEmpty(main)) result.Add(main);

            var endIdx = display.IndexOfAny(new[] { ')', '）' });
            if (endIdx > bracketIdx)
            {
                var remark = display[(bracketIdx + 1)..endIdx].Trim();
                if (!string.IsNullOrEmpty(remark)) result.Add(remark);
            }
        }
        else
        {
            var whole = display.Trim();
            if (!string.IsNullOrEmpty(whole)) result.Add(whole);
        }

        return result;
    }

    /// <summary>模糊匹配：相同字符数 / 较长串长度 &gt;= threshold。与 AutoPartyTask.FuzzyMatch 算法逐字节一致。</summary>
    public static bool FuzzyMatch(string a, string b, double threshold)
    {
        if (a == b) return true;
        var longer = a.Length >= b.Length ? a : b;
        var shorter = a.Length >= b.Length ? b : a;
        if (longer.Length == 0) return false;
        int matchCount = 0;
        var used = new bool[longer.Length];
        foreach (var c in shorter)
        {
            for (int i = 0; i < longer.Length; i++)
            {
                if (!used[i] && longer[i] == c)
                {
                    used[i] = true;
                    matchCount++;
                    break;
                }
            }
        }
        var ratio = (double)matchCount / longer.Length;
        return ratio >= threshold;
    }

    /// <summary>
    /// 白名单匹配（纯版本）。空白名单放行所有；否则对 ocrText 与每个白名单条目都拆候选，
    /// 候选 × 候选笛卡尔积 FuzzyMatch，任一对命中返回 true。
    /// </summary>
    public static bool IsInWhitelist(string ocrText, string[] whitelist, double threshold = 0.6)
    {
        if (whitelist == null || whitelist.Length == 0) return true;

        var ocrCandidates = ExtractNameCandidates(ocrText);

        foreach (var wlName in whitelist)
        {
            var wlCandidates = ExtractNameCandidates(wlName);
            foreach (var wl in wlCandidates)
            {
                foreach (var name in ocrCandidates)
                {
                    if (FuzzyMatch(name, wl, threshold))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 协同批次模式下的申请者核对：
    /// 1. 白名单为空或批次名单为空时一律返回 false（暂停自动同意，提示房主核对）；
    /// 2. 优先走候选名精确匹配，避免相似名（如 "茶包1"/"茶包2"）互相串扰；
    /// 3. 精确未命中时回退到既有的 60% 模糊容错（<see cref="FuzzyMatch"/>），吸收 OCR 偶发漏字/错字；
    /// 4. 无论精确还是容错，都必须唯一解析到 1 位在 <paramref name="batchRoster"/> 中的成员 UID，
    ///    同名或跨条目歧义一律返回 false。
    /// </summary>
    public static bool ConfirmBatchApplicant(
        string? applicant,
        string[]? configuredWhitelist,
        IReadOnlyList<string>? batchRoster,
        IEnumerable<(string PlayerUid, string PlayerName)>? roomPlayers,
        double threshold = 0.6)
    {
        if (string.IsNullOrWhiteSpace(applicant)
            || configuredWhitelist is not { Length: > 0 }
            || batchRoster is not { Count: > 0 }
            || roomPlayers is null)
        {
            return false;
        }

        var applicantCandidates = ExtractNameCandidates(applicant);
        if (applicantCandidates.Count == 0) return false;

        var validPlayers = roomPlayers
            .Where(p => !string.IsNullOrWhiteSpace(p.PlayerUid) && !string.IsNullOrWhiteSpace(p.PlayerName))
            .Select(p => (Uid: p.PlayerUid, Candidates: ExtractNameCandidates(p.PlayerName)))
            .Where(p => p.Candidates.Count > 0)
            .ToArray();
        if (validPlayers.Length == 0) return false;

        var wlEntries = configuredWhitelist
            .Select(entry => ExtractNameCandidates(entry))
            .Where(cands => cands.Count > 0)
            .ToArray();
        if (wlEntries.Length == 0) return false;

        // 1. 精确匹配优先（保留原精确路径，避免 "茶包4" 精确命中白名单后误模糊匹配到 "茶包1"）
        var exactWlEntries = wlEntries
            .Where(cands => cands.Any(c => applicantCandidates.Contains(c, StringComparer.Ordinal)))
            .ToArray();
        if (exactWlEntries.Length > 0)
        {
            var exactLinkNames = exactWlEntries
                .SelectMany(cands => cands)
                .ToHashSet(StringComparer.Ordinal);
            var exactUids = validPlayers
                .Where(player => player.Candidates.Any(exactLinkNames.Contains))
                .Select(player => player.Uid)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            return exactUids.Length == 1 && batchRoster.Contains(exactUids[0], StringComparer.Ordinal);
        }

        // 2. 精确未命中：沿用白名单模糊容错（默认 60%），但仍须唯一归属到本批成员
        var fuzzyWlEntries = wlEntries
            .Where(cands => HasFuzzyCandidateMatch(applicantCandidates, cands, threshold))
            .ToArray();
        if (fuzzyWlEntries.Length == 0) return false;

        var fuzzyLinkNames = fuzzyWlEntries
            .SelectMany(cands => cands)
            .ToHashSet(StringComparer.Ordinal);

        var matchedPlayers = validPlayers
            .Where(player =>
                player.Candidates.Any(fuzzyLinkNames.Contains)
                || HasFuzzyCandidateMatch(applicantCandidates, player.Candidates, threshold))
            .ToArray();

        var matchedUids = matchedPlayers
            .Select(player => player.Uid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matchedUids.Length != 1 || !batchRoster.Contains(matchedUids[0], StringComparer.Ordinal))
        {
            return false;
        }

        var matchedPlayerCandidates = matchedPlayers
            .SelectMany(player => player.Candidates)
            .ToHashSet(StringComparer.Ordinal);
        return fuzzyWlEntries.All(cands => cands.Any(matchedPlayerCandidates.Contains));
    }

    /// <summary>
    /// 协同批次模式下的模板匹配兜底核对：仅当命中模板文件名（去扩展名）对应本批成员 UID，
    /// 或经 <see cref="ConfirmBatchApplicant"/> 唯一确认属于本批成员时放行，防止跨房间模板误放行。
    /// </summary>
    public static bool ConfirmBatchTemplateMatch(
        string? matchedTemplateFile,
        string[]? configuredWhitelist,
        IReadOnlyList<string>? batchRoster,
        IEnumerable<(string PlayerUid, string PlayerName)>? roomPlayers,
        double threshold = 0.6)
    {
        if (string.IsNullOrWhiteSpace(matchedTemplateFile)
            || configuredWhitelist is not { Length: > 0 }
            || batchRoster is not { Count: > 0 })
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(matchedTemplateFile).Trim();
        if (string.IsNullOrEmpty(stem)) return false;

        if (batchRoster.Contains(stem, StringComparer.Ordinal))
        {
            return true;
        }

        return ConfirmBatchApplicant(stem, configuredWhitelist, batchRoster, roomPlayers, threshold);
    }

    private static bool HasFuzzyCandidateMatch(
        IReadOnlyList<string> leftCandidates,
        IReadOnlyList<string> rightCandidates,
        double threshold)
    {
        foreach (var left in leftCandidates)
        {
            foreach (var right in rightCandidates)
            {
                if (FuzzyMatch(left, right, threshold))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
