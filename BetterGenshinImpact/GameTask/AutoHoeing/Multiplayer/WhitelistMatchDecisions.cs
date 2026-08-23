#nullable enable

using System;
using System.Collections.Generic;

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
}
