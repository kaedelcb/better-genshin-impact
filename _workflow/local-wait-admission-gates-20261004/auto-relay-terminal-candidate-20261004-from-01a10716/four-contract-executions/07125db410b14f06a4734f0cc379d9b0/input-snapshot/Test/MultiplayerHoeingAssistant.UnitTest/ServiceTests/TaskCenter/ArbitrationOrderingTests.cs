using System;
using System.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;
using static MultiplayerHoeingAssistant.Services.ArbitrationOrdering;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 冻结稿 v5 §2.1/§2.2/§3/§5/§6.2 仲裁排序与身份编码契约测试。
/// 纯内存、无盘、无时钟、无随机：全部输入显式构造，断言确定性纯函数行为。
/// </summary>
public class ArbitrationOrderingTests
{
    // ── 自制辅助 ────────────────────────────────────────────────────────────

    /// <summary>构造候选，带常用默认（对应身份元组各段的固定取值为参数默认值）。</summary>
    private static ArbitrationCandidate Cand(
        string scope = "bgi-1",
        string ns = "trigger",
        string workflowId = "wf-a",
        string triggerOccurrenceId = "",
        string runId = "run-a",
        string nodeId = "n-1",
        int occurrence = 0,
        int loopIteration = 0,
        int attempt = 1,
        ArbitrationTier tier = ArbitrationTier.Plan,
        int priority = 0,
        DateTimeOffset? scheduledAt = null,
        string payloadFingerprint = "p",
        string resourceRef = "",
        string intent = "start",
        string? actionId = null)
    {
        return new ArbitrationCandidate
        {
            Scope = scope,
            Namespace = ns,
            WorkflowId = workflowId,
            TriggerOccurrenceId = triggerOccurrenceId,
            RunId = runId,
            NodeId = nodeId,
            Occurrence = occurrence,
            LoopIteration = loopIteration,
            Attempt = attempt,
            Tier = tier,
            Priority = priority,
            ScheduledAt = scheduledAt,
            PayloadFingerprint = payloadFingerprint,
            ResourceRef = resourceRef,
            Intent = intent,
            ActionId = actionId,
        };
    }

    /// <summary>构造候选+资格快照配对（Decide 输入项）。</summary>
    private static CandidateEntry Entry(
        ArbitrationCandidate c,
        bool isDue = true,
        bool prereq = true,
        bool windowOpen = true)
    {
        return new CandidateEntry
        {
            Candidate = c,
            Eligibility = new CandidateEligibility
            {
                IsDue = isDue,
                PrerequisiteReady = prereq,
                FlexibleWindowOpen = windowOpen,
            },
        };
    }

    private static string Id(ArbitrationCandidate c) => DeriveCandidateId(BuildStableIdentity(c));

    // ── §2.2/§3 全序 ───────────────────────────────────────────────────────

    /// <summary>§2.2/§3：Tier 优先级（System&gt;Fixed&gt;Plan）→ Priority 大者胜 → ScheduledAt 早者胜（null 最后）→ stableIdentity Ordinal。</summary>
    [Fact]
    public void TotalOrder_TierPriorityScheduledIdentity()
    {
        // Tier：枚举值大者胜（Compare 负 = a 在前）。
        var system = Cand(tier: ArbitrationTier.System, priority: 0);
        var fixedTier = Cand(tier: ArbitrationTier.Fixed, priority: 1000);
        var plan = Cand(tier: ArbitrationTier.Plan, priority: 2000);
        Assert.True(Compare(system, fixedTier) < 0);
        Assert.True(Compare(fixedTier, plan) < 0);
        Assert.True(Compare(system, plan) < 0);

        // 同 tier：priority 大者胜（int32 允许负值）。
        var hiPriority = Cand(priority: 5);
        var loPriority = Cand(priority: 3);
        Assert.True(Compare(hiPriority, loPriority) < 0);

        // 同 priority：scheduledAt 早者胜。
        var t0 = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
        var earlier = Cand(scheduledAt: t0);
        var later = Cand(scheduledAt: t0.AddMinutes(1));
        Assert.True(Compare(earlier, later) < 0);

        // scheduledAt 恰好一方为 null → 非 null 在前（null 排最后）。
        var noScheduled = Cand(scheduledAt: null);
        Assert.True(Compare(earlier, noScheduled) < 0);
        Assert.True(Compare(noScheduled, earlier) > 0);

        // 全相同：stableIdentity Ordinal 兜底。
        var idA = Cand(workflowId: "wf-a");
        var idB = Cand(workflowId: "wf-b");
        var expectedOrdinal = string.CompareOrdinal(BuildStableIdentity(idA), BuildStableIdentity(idB));
        Assert.Equal(expectedOrdinal, Compare(idA, idB));

        // Decide 端到端：Tier 主导，即便低层 priority 更大仍由高层胜。
        var cSystem = Cand(nodeId: "n-sys", tier: ArbitrationTier.System, priority: 0);
        var cFixed = Cand(nodeId: "n-fix", tier: ArbitrationTier.Fixed, priority: 100);
        var cPlan = Cand(nodeId: "n-plan", tier: ArbitrationTier.Plan, priority: 100);
        var byTier = Decide(
            new[] { Entry(cPlan), Entry(cSystem), Entry(cFixed) },
            new ArbitrationFacts());
        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, byTier.Outcome);
        Assert.NotNull(byTier.Winner);
        Assert.Equal("n-sys", byTier.Winner!.NodeId);

        // Decide 端到端：同 tier 内 priority 大者胜。
        var cFixedHi = Cand(nodeId: "n-f1", tier: ArbitrationTier.Fixed, priority: 100);
        var cFixedLo = Cand(nodeId: "n-f2", tier: ArbitrationTier.Fixed, priority: 1);
        var byPriority = Decide(
            new[] { Entry(cFixedLo), Entry(cFixedHi) },
            new ArbitrationFacts());
        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, byPriority.Outcome);
        Assert.Equal("n-f1", byPriority.Winner!.NodeId);
    }

    /// <summary>B1 反例：同属 Tier=Plan，priority=-100 的 flexible 低优先候选败给 priority=100 的 chain 候选。</summary>
    [Fact]
    public void CounterExample_FlexibleLowPriority_LosesToChain()
    {
        var flexibleLow = Cand(nodeId: "n-flex", tier: ArbitrationTier.Plan, priority: -100);
        var chainHigh = Cand(nodeId: "n-chain", tier: ArbitrationTier.Plan, priority: 100);

        var decision = Decide(
            new[] { Entry(flexibleLow), Entry(chainHigh) },
            new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.NotNull(decision.Winner);
        Assert.Equal("n-chain", decision.Winner!.NodeId);
        Assert.Equal(100, decision.Winner.Priority);
    }

    /// <summary>B1 反向例：Plan 层映射后 priority 生效（100 胜 -100），并非 flexible 恒败。</summary>
    [Fact]
    public void SamePlanTier_HigherPriorityWins_RegardlessOfSourceClass()
    {
        // 反向例（结束会诊 P1-⑦修正）：灵活型高优先级 vs 普通链低优先级 → 灵活型胜（同层时 priority 生效，非 flexible 恒败）。
        var sourceClassFlexible = Cand(workflowId: "wf-flex", nodeId: "n-flex", tier: ArbitrationTier.Plan, priority: 100);
        var sourceClassChain = Cand(workflowId: "wf-chain", nodeId: "n-chain", tier: ArbitrationTier.Plan, priority: -100);

        var decision = Decide(
            new[] { Entry(sourceClassChain), Entry(sourceClassFlexible) },
            new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.Equal("n-flex", decision.Winner!.NodeId);
    }

    /// <summary>输入排列无关：5 个候选两种相反顺序输入，胜者 candidateId 与 ActionId 一致。</summary>
    [Fact]
    public void InputPermutation_DoesNotChangeWinner()
    {
        var candidates = new[]
        {
            Cand(nodeId: "n-1", tier: ArbitrationTier.Plan, priority: 1),
            Cand(nodeId: "n-2", tier: ArbitrationTier.Fixed, priority: 0),
            Cand(nodeId: "n-3", tier: ArbitrationTier.System, priority: 0),
            Cand(nodeId: "n-4", tier: ArbitrationTier.Plan, priority: 9),
            Cand(nodeId: "n-5", tier: ArbitrationTier.Fixed, priority: 5),
        };

        var forward = candidates.Select(c => Entry(c)).ToArray();
        var reverse = candidates.Reverse().Select(c => Entry(c)).ToArray();

        var d1 = Decide(forward, new ArbitrationFacts());
        var d2 = Decide(reverse, new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, d1.Outcome);
        Assert.NotNull(d1.WinnerCandidateId);
        Assert.Equal(d1.WinnerCandidateId, d2.WinnerCandidateId);
        Assert.Equal(d1.ActionId, d2.ActionId);
        Assert.Equal("n-3", d1.Winner!.NodeId);
    }

    // ── 身份唯一性 / 冲突 ──────────────────────────────────────────────────

    /// <summary>B2 复核：Occurrence 相同但 NodeId 不同（n-a / n-b）→ candidateId 不同、无 identity_conflict、正常产出胜者。</summary>
    [Fact]
    public void SameOccurrence_DifferentNodeId_NoConflict()
    {
        var a = Cand(nodeId: "n-a", occurrence: 0);
        var b = Cand(nodeId: "n-b", occurrence: 0);

        Assert.NotEqual(Id(a), Id(b));

        var decision = Decide(new[] { Entry(a), Entry(b) }, new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.DoesNotContain(decision.Rejections, r => r.Reason == "identity_conflict");
        Assert.NotNull(decision.Winner);
        // nodeOccurrenceIdentity 兜底按 stableIdentity Ordinal：'n-a' 在前。
        Assert.Equal("n-a", decision.Winner!.NodeId);
    }

    /// <summary>平行身份可区分：分别只改 RunId/Occurrence/LoopIteration/Attempt，candidateId 全不同。</summary>
    [Fact]
    public void ParallelIdentities_Distinguished()
    {
        var baseId = Id(Cand());

        Assert.NotEqual(baseId, Id(Cand(runId: "run-b")));
        Assert.NotEqual(baseId, Id(Cand(occurrence: 2)));
        Assert.NotEqual(baseId, Id(Cand(loopIteration: 1)));
        Assert.NotEqual(baseId, Id(Cand(attempt: 2)));

        var variantIds = new[]
        {
            Id(Cand(runId: "run-b")),
            Id(Cand(occurrence: 2)),
            Id(Cand(loopIteration: 1)),
            Id(Cand(attempt: 2)),
        };
        Assert.Equal(variantIds.Length, variantIds.Distinct().Count());
    }

    /// <summary>同 candidateId 仅载荷不同 → 整组拒绝；无关候选正常胜出（不影响其余组）。</summary>
    [Fact]
    public void SameCandidateId_DifferentPayload_WholeGroupRejected()
    {
        var dup1 = Cand(nodeId: "n-1", payloadFingerprint: "p1");
        var dup2 = Cand(nodeId: "n-1", payloadFingerprint: "p2");
        var unrelated = Cand(nodeId: "n-other");

        var decision = Decide(
            new[] { Entry(dup1), Entry(dup2), Entry(unrelated) },
            new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.Equal("n-other", decision.Winner!.NodeId);

        var conflicts = decision.Rejections.Where(r => r.Reason == "identity_conflict").ToList();
        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, r => Assert.Equal(Id(dup1), r.CandidateId));
    }

    /// <summary>冲突拒绝在输入交换下确定：两次 Decide 的 Rejections（candidateId+reason 序列）完全一致。</summary>
    [Fact]
    public void ConflictRejection_DeterministicUnderInputSwap()
    {
        var x1 = Cand(nodeId: "n-x", payloadFingerprint: "p1");
        var x2 = Cand(nodeId: "n-x", payloadFingerprint: "p2");
        var y1 = Cand(nodeId: "n-y", payloadFingerprint: "q1");
        var y2 = Cand(nodeId: "n-y", payloadFingerprint: "q2");

        var forward = Decide(
            new[] { Entry(x1), Entry(x2), Entry(y1), Entry(y2) },
            new ArbitrationFacts());
        var reverse = Decide(
            new[] { Entry(y2), Entry(y1), Entry(x2), Entry(x1) },
            new ArbitrationFacts());

        var seqForward = forward.Rejections.Select(r => r.CandidateId + "|" + r.Reason).ToArray();
        var seqReverse = reverse.Rejections.Select(r => r.CandidateId + "|" + r.Reason).ToArray();

        Assert.Equal(seqForward, seqReverse);
        Assert.Equal(4, forward.Rejections.Count);
    }

    /// <summary>同 ID+同载荷+同排序键 → 幂等去重留一项，无 identity_conflict，重复项胜出。</summary>
    [Fact]
    public void SameIdSamePayloadSameKey_Dedup()
    {
        var dup = Cand(nodeId: "n-dup", priority: 100);
        var dupCopy = Cand(nodeId: "n-dup", priority: 100);
        var lower = Cand(nodeId: "n-low", priority: 1);

        var decision = Decide(
            new[] { Entry(dup), Entry(dupCopy), Entry(lower) },
            new ArbitrationFacts());

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.Equal("n-dup", decision.Winner!.NodeId);
        Assert.DoesNotContain(decision.Rejections, r => r.Reason == "identity_conflict");
    }

    // ── F11 / 票据 / 事实 / 占用 / 租约 闸门 ───────────────────────────────

    /// <summary>§1/§3：F11 独立停止闸门先行 → 全拒、F11Blocked、来源 f11、无胜者、全体 f11_active。</summary>
    [Fact]
    public void F11Gate_RejectsEverything()
    {
        var facts = new ArbitrationFacts { F11Active = true };
        var decision = Decide(
            new[] { Entry(Cand(nodeId: "n-1")), Entry(Cand(nodeId: "n-2")) },
            facts);

        Assert.Equal(ArbitrationOutcome.F11Blocked, decision.Outcome);
        Assert.Equal("f11", decision.SuppressionSource);
        Assert.Null(decision.Winner);
        Assert.Null(decision.WinnerCandidateId);
        Assert.Equal(2, decision.Rejections.Count);
        Assert.All(decision.Rejections, r => Assert.Equal("f11_active", r.Reason));
    }

    /// <summary>§5/I2：票据压制无关候选、保留授权抢占方；授权方自身未到点 → TicketSuppressed/ticket。</summary>
    [Fact]
    public void TicketSuppression_UnrelatedSuppressed_AuthorizedRetained()
    {
        var a = Cand(nodeId: "n-a", priority: 100);
        var b = Cand(nodeId: "n-b", priority: 1);

        var facts = new ArbitrationFacts
        {
            ActiveTicket = new TicketBinding { AuthorizedPreemptorIdentity = BuildStableIdentity(b) },
        };

        var decision = Decide(new[] { Entry(a), Entry(b) }, facts);

        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, decision.Outcome);
        Assert.Equal("n-b", decision.Winner!.NodeId);
        Assert.Contains(
            decision.Rejections,
            r => r.Reason == "ticket_suppressed" && r.StableIdentity == BuildStableIdentity(a));

        // 再令授权方 B 未到点：授权资格保留但自身不参选 → 无合格候选且系票据压制所致。
        var factsAuthorizedNotDue = new ArbitrationFacts
        {
            ActiveTicket = new TicketBinding { AuthorizedPreemptorIdentity = BuildStableIdentity(b) },
        };
        var suppressed = Decide(
            new[] { Entry(a), Entry(b, isDue: false) },
            factsAuthorizedNotDue);

        Assert.Equal(ArbitrationOutcome.TicketSuppressed, suppressed.Outcome);
        Assert.Equal("ticket", suppressed.SuppressionSource);
        Assert.Null(suppressed.Winner);
    }

    /// <summary>权威执行事实未知 → NeedReconcile / facts_unknown / 无胜者。</summary>
    [Fact]
    public void NeedReconcile_WhenFactsUnknown()
    {
        var decision = Decide(
            new[] { Entry(Cand(nodeId: "n-1")) },
            new ArbitrationFacts { ExecutionFactsUnknown = true });

        Assert.Equal(ArbitrationOutcome.NeedReconcile, decision.Outcome);
        Assert.Equal("facts_unknown", decision.SuppressionSource);
        Assert.Null(decision.Winner);
    }

    /// <summary>占用/空闲两分支：有胜者时占用→NeedPreemptConfirm、空闲→AllowRequestExecution；同输入两次确定。</summary>
    [Fact]
    public void NeedPreemptConfirm_WhenOccupied_And_AllowRequestExecution_WhenFree()
    {
        var c = Cand(nodeId: "n-c", priority: 42);

        var occupied = Decide(new[] { Entry(c) }, new ArbitrationFacts { ExecutionOccupied = true });
        var free = Decide(new[] { Entry(c) }, new ArbitrationFacts { ExecutionOccupied = false });

        Assert.Equal(ArbitrationOutcome.NeedPreemptConfirm, occupied.Outcome);
        Assert.Equal(ArbitrationOutcome.AllowRequestExecution, free.Outcome);
        Assert.Same(c, occupied.Winner);
        Assert.Same(c, free.Winner);
        Assert.False(string.IsNullOrEmpty(occupied.ActionId));
        Assert.False(string.IsNullOrEmpty(free.ActionId));

        // 确定性：同输入两次 Decide 结果一致。
        var occupiedAgain = Decide(new[] { Entry(c) }, new ArbitrationFacts { ExecutionOccupied = true });
        Assert.Equal(occupied.Outcome, occupiedAgain.Outcome);
        Assert.Equal(occupied.ActionId, occupiedAgain.ActionId);
        Assert.Equal(occupied.WinnerCandidateId, occupiedAgain.WinnerCandidateId);
    }

    /// <summary>§6.2 过期即禁启：无有效租约 → NoEligibleCandidate / 全体 lease_not_valid / 来源 lease。</summary>
    [Fact]
    public void LeaseInvalid_AllRejected()
    {
        var decision = Decide(
            new[] { Entry(Cand(nodeId: "n-1")), Entry(Cand(nodeId: "n-2")) },
            new ArbitrationFacts { RequesterHoldsValidLease = false });

        Assert.Equal(ArbitrationOutcome.NoEligibleCandidate, decision.Outcome);
        Assert.Equal("lease", decision.SuppressionSource);
        Assert.Null(decision.Winner);
        Assert.Equal(2, decision.Rejections.Count);
        Assert.All(decision.Rejections, r => Assert.Equal("lease_not_valid", r.Reason));
    }

    // ── §2.1 身份编码 ──────────────────────────────────────────────────────

    /// <summary>四轮阻断二：字符串转义往返（单射，无歧义）。</summary>
    [Fact]
    public void Encoding_StringEscape_RoundTrip()
    {
        var enc = ArbitrationIdentityEncoding.EncodeString;
        var dec = ArbitrationIdentityEncoding.DecodeString;

        foreach (var s in new[] { "~", "|", "~0", "a|b~c" })
        {
            Assert.Equal(s, dec(enc(s)));
        }

        // 空串与 null 均编码为占位符 "~"，解码回空串（编码对 null/"" 归一，不可区分）。
        Assert.Equal("~", enc(string.Empty));
        Assert.Equal("~", enc(null));
        Assert.Equal(string.Empty, dec(enc(string.Empty)));
        Assert.Equal(string.Empty, dec(enc(null)));
        Assert.Equal(string.Empty, dec("~"));

        // 含保留字符的两个不同原始串编码后不相等（无歧义）。
        Assert.NotEqual(enc("a|b"), enc("a~b"));
        Assert.NotEqual(enc("~"), enc("|"));
    }

    /// <summary>§2.1：身份整数 8 位定宽十进制；越界（&gt;99,999,999 或 &lt;0）响亮拒绝。</summary>
    [Fact]
    public void Encoding_Int_Boundary()
    {
        Assert.Equal("00000000", ArbitrationIdentityEncoding.EncodeInt(0));
        Assert.Equal("99999999", ArbitrationIdentityEncoding.EncodeInt(99_999_999));

        Assert.Throws<ArgumentOutOfRangeException>(() => ArbitrationIdentityEncoding.EncodeInt(100_000_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArbitrationIdentityEncoding.EncodeInt(-1));
    }

    /// <summary>§2.1：时刻编码为 UTC 定宽 ISO（yyyy-MM-ddTHH:mm:ss.fffffffZ），同一瞬时不同偏移表示归一。</summary>
    [Fact]
    public void Encoding_Time_UniqueForm()
    {
        var utc = new DateTimeOffset(2026, 9, 20, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234_567);
        var plus8 = utc.ToOffset(TimeSpan.FromHours(8));

        var encUtc = ArbitrationIdentityEncoding.EncodeTime(utc);
        var encPlus8 = ArbitrationIdentityEncoding.EncodeTime(plus8);

        Assert.Equal(encUtc, encPlus8);
        Assert.EndsWith("Z", encUtc);
        Assert.Contains(".1234567", encUtc);
        Assert.Equal("2026-09-20T12:34:56.1234567Z", encUtc);
    }

    /// <summary>§2.1/§3：candidateId 确定性派生——同元组两次相同、改任一段不同。</summary>
    [Fact]
    public void CandidateId_Deterministic()
    {
        var sameA = Id(Cand());
        var sameB = Id(Cand());
        Assert.Equal(sameA, sameB);
        Assert.StartsWith("cand-", sameA);

        Assert.NotEqual(sameA, Id(Cand(scope: "bgi-2")));
        Assert.NotEqual(sameA, Id(Cand(ns: "manual")));
        Assert.NotEqual(sameA, Id(Cand(workflowId: "wf-b")));
        Assert.NotEqual(sameA, Id(Cand(runId: "run-b")));
        Assert.NotEqual(sameA, Id(Cand(nodeId: "n-2")));
        Assert.NotEqual(sameA, Id(Cand(occurrence: 1)));
        Assert.NotEqual(sameA, Id(Cand(loopIteration: 1)));
        Assert.NotEqual(sameA, Id(Cand(attempt: 2)));
    }
}
