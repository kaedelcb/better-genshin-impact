using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · R5.1 仲裁排序纯函数（设计冻结稿 v5 §2.1/§3）。
/// 纯函数契约：无副作用，不读取时钟、不生成随机、不探测环境；全部输入来自参数，
/// 同输入必得同输出（含 <see cref="ArbitrationDecision.ActionId"/> 的确定性派生）。
/// </summary>
public static class ArbitrationOrdering
{
    /// <summary>
    /// §2.1 身份编码规则表。stableIdentity 以 '|' 连接各段，故字符串段内出现的 '|' 与转义符 '~' 必须转义；
    /// 编码为单射，与 <see cref="DecodeString"/> 严格互逆。
    /// </summary>
    public static class ArbitrationIdentityEncoding
    {
        /// <summary>§2.1：身份整数上界（8 位十进制）。</summary>
        public const int Int32IdentityMax = 99_999_999;

        /// <summary>
        /// §2.1：null 或空串编码为单段占位符 <c>"~"</c>；否则先 <c>'~'→"~0"</c> 再把 <c>'|'→"~1"</c>
        /// （顺序不可颠倒，否则二次转义破坏单射）。
        /// </summary>
        public static string EncodeString(string? v)
        {
            if (string.IsNullOrEmpty(v))
            {
                return "~";
            }

            // 顺序不可颠倒：必须先替换 '~' 再替换 '|'。
            return v.Replace("~", "~0").Replace("|", "~1");
        }

        /// <summary>
        /// §2.1：<see cref="EncodeString"/> 的逆变换。按序扫描，<c>"~0"→'~'</c>、<c>"~1"→'|'</c>、
        /// 孤立 <c>"~"</c>→空串。与 <see cref="EncodeString"/> 构成单射往返。
        /// </summary>
        public static string DecodeString(string e)
        {
            if (string.IsNullOrEmpty(e))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(e.Length);
            for (var i = 0; i < e.Length; i++)
            {
                if (e[i] != '~')
                {
                    sb.Append(e[i]);
                    continue;
                }

                if (i + 1 < e.Length && e[i + 1] == '0')
                {
                    sb.Append('~');
                    i++;
                }
                else if (i + 1 < e.Length && e[i + 1] == '1')
                {
                    sb.Append('|');
                    i++;
                }
                // 孤立 '~'（占位或非法）：贡献空串，跳过该字符。
            }

            return sb.ToString();
        }

        /// <summary>
        /// §2.1：身份整数编码为 8 位定宽十进制（<c>"D8"</c>，不变文化）。
        /// v&lt;0 或 v&gt;99,999,999 → 响亮拒绝（<see cref="ArgumentOutOfRangeException"/>），不截断、不扩宽。
        /// </summary>
        public static string EncodeInt(int v)
        {
            if (v < 0 || v > Int32IdentityMax)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(v),
                    v,
                    "身份整数必须落在 [0, 99999999]；越界响亮拒绝（不截断、不扩宽）。");
            }

            return v.ToString("D8", CultureInfo.InvariantCulture);
        }

        /// <summary>§2.1：时刻编码为 UTC 定宽 ISO（<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>，不变文化）。</summary>
        public static string EncodeTime(DateTimeOffset t)
        {
            return t.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>§2.1：Guid 编码为 <c>"N"</c> 格式（32 位十六进制，小写）。</summary>
        public static string EncodeGuid(Guid g)
        {
            return g.ToString("N");
        }
    }

    /// <summary>
    /// §2.1：按固定顺序拼接身份元组——
    /// scope | namespace | workflowId | triggerOccurrenceId | runId | nodeOccurrenceIdentity(nodeId, occurrence) | loopIteration | attempt。
    /// NodeId 与 Occurrence 相邻两段即 nodeOccurrenceIdentity=(nodeId, occurrence)。
    /// </summary>
    public static string BuildStableIdentity(ArbitrationCandidate c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return string.Join(
            '|',
            ArbitrationIdentityEncoding.EncodeString(c.Scope),
            ArbitrationIdentityEncoding.EncodeString(c.Namespace),
            ArbitrationIdentityEncoding.EncodeString(c.WorkflowId),
            ArbitrationIdentityEncoding.EncodeString(c.TriggerOccurrenceId),
            ArbitrationIdentityEncoding.EncodeString(c.RunId),
            ArbitrationIdentityEncoding.EncodeString(c.NodeId),
            ArbitrationIdentityEncoding.EncodeInt(c.Occurrence),
            ArbitrationIdentityEncoding.EncodeInt(c.LoopIteration),
            ArbitrationIdentityEncoding.EncodeInt(c.Attempt));
    }

    /// <summary>§2.1/§3：候选号 = <c>"cand-"</c> + SHA-256(stableIdentity) 十六进制前 24 位（小写）。</summary>
    public static string DeriveCandidateId(string stableIdentity)
    {
        return "cand-" + ShortHash(stableIdentity);
    }

    /// <summary>§3：动作号 = <c>"act-"</c> + SHA-256(seed) 十六进制前 24 位（小写）。</summary>
    public static string DeriveActionId(string seed)
    {
        return "act-" + ShortHash(seed);
    }

    /// <summary>
    /// §3 排序比较器（负 = a 在前）。依次：
    /// Tier 大者胜（System&gt;Fixed&gt;Plan）→ Priority 大者胜（int32，允许负值）→
    /// ScheduledAt 升序（null 排最后）→ stableIdentity Ordinal 兜底。
    /// </summary>
    public static int Compare(ArbitrationCandidate a, ArbitrationCandidate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // Tier：枚举值大者胜。
        var tier = ((int)b.Tier).CompareTo((int)a.Tier);
        if (tier != 0)
        {
            return tier;
        }

        // Priority：int32 大者胜（允许负值）。
        var priority = b.Priority.CompareTo(a.Priority);
        if (priority != 0)
        {
            return priority;
        }

        // ScheduledAt：升序，null 排最后。
        var scheduled = CompareScheduledAt(a.ScheduledAt, b.ScheduledAt);
        if (scheduled != 0)
        {
            return scheduled;
        }

        // stableIdentity：Ordinal 兜底。
        return string.CompareOrdinal(BuildStableIdentity(a), BuildStableIdentity(b));
    }

    /// <summary>
    /// §3：主判定入口。判定顺序严格遵循冻结稿——F11 闸门 → 归一化 → 租约 → 票据 → 资格 → 事实未知 → 无合格 → 胜者。
    /// Rejections 一律按 CandidateId Ordinal 排序；ActionId 一律确定性派生（无胜者时用拒绝候选号集合，无候选时用空串）。
    /// </summary>
    public static ArbitrationDecision Decide(IReadOnlyList<CandidateEntry> entries, ArbitrationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(facts);

        // ① F11 独立停止闸门：先于一切排序与租约判断，也先于身份编码（§3 载荷：本分支候选引用可空）——
        //    候选编码失败（整数越界等）不得阻断闸门返回 F11Blocked。
        if (facts.F11Active)
        {
            var f11Rejections = new List<CandidateRejection>(entries.Count);
            foreach (var entry in entries)
            {
                ArgumentNullException.ThrowIfNull(entry);
                ArgumentNullException.ThrowIfNull(entry.Candidate);
                var cid = string.Empty;
                var sid = string.Empty;
                try
                {
                    sid = BuildStableIdentity(entry.Candidate);
                    cid = DeriveCandidateId(sid);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // 编码失败不阻断闸门；引用留空（响亮拒绝由正常路径的 identity_invalid 承担）。
                }

                f11Rejections.Add(new CandidateRejection { CandidateId = cid, StableIdentity = sid, Reason = "f11_active" });
            }

            return new ArbitrationDecision
            {
                Outcome = ArbitrationOutcome.F11Blocked,
                Winner = null,
                WinnerCandidateId = null,
                Rejections = SortRejections(f11Rejections),
                SuppressionSource = "f11",
                ActionId = DeriveActionId(BuildRejectSeed(f11Rejections)),
                Reason = "F11 独立停止闸门激活：全部候选被拒绝（不作候选、不参与排序）。",
            };
        }

        // 预解析：规范化身份与候选号（确定性，与输入排列无关）；编码失败 → 响亮拒绝 identity_invalid（不静默吞掉）。
        var resolved = new List<ResolvedEntry>(entries.Count);
        var rejections = new List<CandidateRejection>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Candidate);

            try
            {
                var stableIdentity = BuildStableIdentity(entry.Candidate);
                resolved.Add(new ResolvedEntry
                {
                    Entry = entry,
                    StableIdentity = stableIdentity,
                    CandidateId = DeriveCandidateId(stableIdentity),
                });
            }
            catch (ArgumentOutOfRangeException)
            {
                rejections.Add(new CandidateRejection { CandidateId = "", StableIdentity = "", Reason = "identity_invalid" });
            }
        }

        var surviving = new List<ResolvedEntry>();

        // ② 归一化：按候选号分组，组按 candidateId Ordinal 排序处理（输入排列无关）。
        foreach (var group in resolved
                     .GroupBy(r => r.CandidateId, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();

            // 同组内任意两项 PayloadFingerprint 或 Tier/Priority/ScheduledAt 不一致 → 整组拒绝。
            if (HasIdentityConflict(members))
            {
                foreach (var member in members)
                {
                    rejections.Add(MakeRejection(member, "identity_conflict"));
                }

                continue; // 其余组继续处理。
            }

            // 完全一致 → 去重留一项（确定性代表）。
            surviving.Add(CanonicalRepresentative(members));
        }

        // ③ 租约有效闸门：无效则剩余候选全体拒绝并返回。
        if (!facts.RequesterHoldsValidLease)
        {
            foreach (var r in surviving)
            {
                rejections.Add(MakeRejection(r, "lease_not_valid"));
            }

            return new ArbitrationDecision
            {
                Outcome = ArbitrationOutcome.NoEligibleCandidate,
                Winner = null,
                WinnerCandidateId = null,
                Rejections = SortRejections(rejections),
                SuppressionSource = "lease",
                ActionId = DeriveActionId(BuildRejectSeed(rejections)),
                Reason = "请求方未持有有效租约（§6.2 过期即禁启）：剩余候选全部被拒绝。",
            };
        }

        var suppressionSource = string.Empty;

        // ④ 票据压制：非授权抢占方一律拒绝，授权方保留资格。
        if (facts.ActiveTicket is not null)
        {
            var authorized = facts.ActiveTicket.AuthorizedPreemptorIdentity ?? string.Empty;
            var kept = new List<ResolvedEntry>(surviving.Count);
            foreach (var r in surviving)
            {
                if (string.Equals(r.StableIdentity, authorized, StringComparison.Ordinal))
                {
                    kept.Add(r);
                }
                else
                {
                    rejections.Add(MakeRejection(r, "ticket_suppressed"));
                }
            }

            surviving = kept;
            suppressionSource = "ticket";
        }

        // 票据压制事实（§3 载荷：TicketSuppressed 分支的被压制候选引用+票据来源必填）。
        var ticketSuppressedAny = rejections.Any(r => r.Reason == "ticket_suppressed");

        // ⑤ 资格筛选（顺序：到点 → 前置就绪 → 灵活窗口）；有任一此类拒绝且来源为空则置 "eligibility"。
        var eligible = new List<ResolvedEntry>(surviving.Count);
        var eligibilityRejected = false;
        foreach (var r in surviving)
        {
            var e = r.Entry.Eligibility;
            if (!e.IsDue)
            {
                rejections.Add(MakeRejection(r, "not_due"));
                eligibilityRejected = true;
                continue;
            }

            if (!e.PrerequisiteReady)
            {
                rejections.Add(MakeRejection(r, "prerequisite_not_ready"));
                eligibilityRejected = true;
                continue;
            }

            if (!e.FlexibleWindowOpen)
            {
                rejections.Add(MakeRejection(r, "window_closed"));
                eligibilityRejected = true;
                continue;
            }

            eligible.Add(r);
        }

        if (eligibilityRejected && suppressionSource.Length == 0)
        {
            suppressionSource = "eligibility";
        }

        // ⑥ 权威执行事实未知 → 待对账（禁止启动、禁止换键重跑）。
        if (facts.ExecutionFactsUnknown)
        {
            return new ArbitrationDecision
            {
                Outcome = ArbitrationOutcome.NeedReconcile,
                Winner = null,
                WinnerCandidateId = null,
                Rejections = SortRejections(rejections),
                SuppressionSource = "facts_unknown",
                SuppressionDetail = facts.FactsReference,
                ActionId = DeriveActionId(BuildRejectSeed(rejections) + "|" + facts.FactsReference),
                Reason = "权威执行事实未知：待对账（禁止换键重跑、禁止回 Idle 重提交；关联事实=" + (facts.FactsReference.Length > 0 ? facts.FactsReference : "无") + "）。",
            };
        }

        // ⑦ 无合格候选：若系票据压制所致 → TicketSuppressed（§3 载荷：被压制候选引用+票据来源必填）；
        //    授权抢占方资格仍保留——此分支仅在授权方自身也未通过资格筛选时到达。
        if (eligible.Count == 0)
        {
            if (ticketSuppressedAny)
            {
                return new ArbitrationDecision
                {
                    Outcome = ArbitrationOutcome.TicketSuppressed,
                    Winner = null,
                    WinnerCandidateId = null,
                    Rejections = SortRejections(rejections),
                    SuppressionSource = "ticket",
                    SuppressionDetail = facts.ActiveTicket?.SuspendedRunIdentity ?? "",
                    ActionId = DeriveActionId(BuildRejectSeed(rejections)),
                    Reason = "存续 A6 票据压制无关候选（授权抢占方保留资格；settle 未确认期间无关候选保持压制）。",
                };
            }

            return new ArbitrationDecision
            {
                Outcome = ArbitrationOutcome.NoEligibleCandidate,
                Winner = null,
                WinnerCandidateId = null,
                Rejections = SortRejections(rejections),
                SuppressionSource = suppressionSource.Length > 0 ? suppressionSource : "none",
                ActionId = DeriveActionId(BuildRejectSeed(rejections)),
                Reason = "无合格参选候选。",
            };
        }

        // ⑧ 胜者 = 合格候选按 Compare 排序首元素。
        var winner = eligible
            .OrderBy(r => r, Comparer<ResolvedEntry>.Create(CompareResolved))
            .First();

        var winnerActionId = winner.Candidate.ActionId;
        var actionId = string.IsNullOrWhiteSpace(winnerActionId)
            ? DeriveActionId(winner.StableIdentity)
            : winnerActionId!;

        return new ArbitrationDecision
        {
            Outcome = facts.ExecutionOccupied
                ? ArbitrationOutcome.NeedPreemptConfirm
                : ArbitrationOutcome.AllowRequestExecution,
            Winner = winner.Candidate,
            WinnerCandidateId = winner.CandidateId,
            Rejections = SortRejections(rejections),
            SuppressionSource = suppressionSource.Length > 0 ? suppressionSource : "none",
            SuppressionDetail = facts.ActiveTicket?.SuspendedRunIdentity ?? "",
            ActionId = actionId,
            Reason = facts.ExecutionOccupied
                ? "已选定胜者，但 BGI 权威执行被占用：需安全交接确认。"
                : "已选定胜者：允许请求执行（仍须经租约锁内意图发布，§6.2 原子准入边界）。",
        };
    }

    // ---- 内部辅助（均为确定性、无副作用）----

    /// <summary>§2.1/§3：SHA-256 十六进制前 24 位小写（纯函数内部派生身份，禁随机）。</summary>
    private static string ShortHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>§3：ScheduledAt 升序；null 排最后（恰好一个有值：非 null 在前）。</summary>
    private static int CompareScheduledAt(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a.HasValue && b.HasValue)
        {
            return a.Value.CompareTo(b.Value);
        }

        if (!a.HasValue && !b.HasValue)
        {
            return 0;
        }

        return a.HasValue ? -1 : 1;
    }

    /// <summary>§3：合格候选的完全确定序——Compare 主序 + (ActionId, ResourceRef, Intent) Ordinal 兜底，保证输入排列无关。</summary>
    private static int CompareResolved(ResolvedEntry a, ResolvedEntry b)
    {
        var c = Compare(a.Candidate, b.Candidate);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Candidate.ActionId ?? string.Empty, b.Candidate.ActionId ?? string.Empty);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Candidate.ResourceRef ?? string.Empty, b.Candidate.ResourceRef ?? string.Empty);
        if (c != 0)
        {
            return c;
        }

        return string.CompareOrdinal(a.Candidate.Intent ?? string.Empty, b.Candidate.Intent ?? string.Empty);
    }

    /// <summary>§3 ②：同组内任意两项 PayloadFingerprint 或 Tier/Priority/ScheduledAt 不一致即冲突。</summary>
    private static bool HasIdentityConflict(List<ResolvedEntry> members)
    {
        if (members.Count < 2)
        {
            return false;
        }

        var first = members[0].Candidate;
        for (var i = 1; i < members.Count; i++)
        {
            var c = members[i].Candidate;
            if (!string.Equals(first.PayloadFingerprint, c.PayloadFingerprint, StringComparison.Ordinal)
                || !string.Equals(first.ResourceRef, c.ResourceRef, StringComparison.Ordinal)
                || !string.Equals(first.Intent, c.Intent, StringComparison.Ordinal)
                || first.Tier != c.Tier
                || first.Priority != c.Priority
                || Nullable.Compare(first.ScheduledAt, c.ScheduledAt) != 0
                || !string.Equals(members[0].Entry.BindingDiscriminator, members[i].Entry.BindingDiscriminator, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>§3 ②：完全一致组去重留一项；以 (ActionId, ResourceRef, Intent) Ordinal 取确定性代表（排列无关）。</summary>
    private static ResolvedEntry CanonicalRepresentative(List<ResolvedEntry> members)
    {
        var rep = members
            .OrderBy(m => m.Candidate.ActionId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Candidate.ResourceRef ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Candidate.Intent ?? string.Empty, StringComparer.Ordinal)
            .First();
        if (members.Count < 2)
        {
            return rep;
        }

        // 重复项资格保守合并（AND，结束会诊 P1-②）：任一成员不合格则合并代表不合格——
        // 输入排列无关且偏保守（§1 无双跑第一原则：不确定不放行）。
        return new ResolvedEntry
        {
            Entry = new CandidateEntry
            {
                Candidate = rep.Entry.Candidate,
                Eligibility = new CandidateEligibility
                {
                    IsDue = members.All(m => m.Entry.Eligibility.IsDue),
                    PrerequisiteReady = members.All(m => m.Entry.Eligibility.PrerequisiteReady),
                    FlexibleWindowOpen = members.All(m => m.Entry.Eligibility.FlexibleWindowOpen),
                },
            },
            StableIdentity = rep.StableIdentity,
            CandidateId = rep.CandidateId,
        };
    }

    private static CandidateRejection MakeRejection(ResolvedEntry r, string reason)
    {
        return new CandidateRejection
        {
            CandidateId = r.CandidateId,
            StableIdentity = r.StableIdentity,
            Reason = reason,
        };
    }

    /// <summary>§3：Rejections 按 CandidateId Ordinal 排序（确定性）。</summary>
    private static List<CandidateRejection> SortRejections(IEnumerable<CandidateRejection> rejections)
    {
        return rejections
            .OrderBy(r => r.CandidateId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>§3：无胜者时的 ActionId 种子 = 拒绝候选号（Ordinal 排序、',' 连接）；无候选时为空串。</summary>
    private static string BuildRejectSeed(IEnumerable<CandidateRejection> rejections)
    {
        return string.Join(
            ",",
            rejections
                .Select(r => r.CandidateId)
                .OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>解析后的输入项：原始配对 + 预计算的稳定身份与候选号。</summary>
    private sealed class ResolvedEntry
    {
        public CandidateEntry Entry { get; init; } = null!;

        public string StableIdentity { get; init; } = string.Empty;

        public string CandidateId { get; init; } = string.Empty;

        public ArbitrationCandidate Candidate => Entry.Candidate;
    }
}
