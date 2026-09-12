#nullable enable

using System;
using BetterGenshinImpact.GameTask.Common;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonTests;

/// <summary>
/// CaptureRetryDecisions 决策纯函数的 PBT 属性测试（T1 批量收口 E3）。
/// **Validates: capture-failure-suspend-signal spec / bugfix.md §5 D1-D3**
/// 固化三条决策契约：
///   1) 上次成功 → ReturnImage（与 elapsed 无关）
///   2) 未成功：elapsed >= MaxRecoveryWait(30s) → Abandon（边界含等号）；&lt; 30s → RetryAfterDelay
///   3) ShouldLogProgress：当且仅当 elapsed - lastLoggedAt >= ProgressLogInterval(5s)（lastLoggedAt 超前/负差值恒 false）
/// </summary>
public class CaptureRetryDecisionsPbtTest
{
    private static Gen<int> ElapsedMsGen() => Gen.Choose(0, 120_000);

    /// <summary>P1（MaxTest=500）：上次尝试成功 → 恒 ReturnImage，elapsed 任意。</summary>
    [Property(MaxTest = 500)]
    public Property Decide_ReturnsReturnImage_WhenLastAttemptSucceeded()
    {
        return Prop.ForAll(Arb.From(ElapsedMsGen()), elapsedMs =>
            (CaptureRetryDecisions.Decide(true, TimeSpan.FromMilliseconds(elapsedMs))
                == CaptureRetryDecision.ReturnImage).ToProperty());
    }

    /// <summary>P2（边界含等号）：elapsed 恰为 30s 或超出 → Abandon。</summary>
    [Property(MaxTest = 500)]
    public Property Decide_ReturnsAbandon_AtOrBeyondMaxRecoveryWait()
    {
        return Prop.ForAll(Arb.From(Gen.Choose(30_000, 120_000)), elapsedMs =>
            (CaptureRetryDecisions.Decide(false, TimeSpan.FromMilliseconds(elapsedMs))
                == CaptureRetryDecision.Abandon).ToProperty());
    }

    /// <summary>P3（边界内侧）：elapsed &lt; 30s → RetryAfterDelay（覆盖 0 与 29_999 两端）。</summary>
    [Property(MaxTest = 500)]
    public Property Decide_ReturnsRetryAfterDelay_BeforeMaxRecoveryWait()
    {
        return Prop.ForAll(Arb.From(Gen.Choose(0, 29_999)), elapsedMs =>
            (CaptureRetryDecisions.Decide(false, TimeSpan.FromMilliseconds(elapsedMs))
                == CaptureRetryDecision.RetryAfterDelay).ToProperty());
    }

    /// <summary>P4（MaxTest=1000）：ShouldLogProgress 当且仅当 (elapsed - lastLoggedAt) >= 5s；负差值（lastLoggedAt 超前）恒 false。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldLogProgress_ExactlyAtProgressLogInterval()
    {
        return Prop.ForAll(Arb.From(ElapsedMsGen()), Arb.From(ElapsedMsGen()), (elapsedMs, lastLoggedMs) =>
        {
            var expected = (elapsedMs - lastLoggedMs)
                >= (int)CaptureRetryDecisions.ProgressLogInterval.TotalMilliseconds;
            return (CaptureRetryDecisions.ShouldLogProgress(
                        TimeSpan.FromMilliseconds(elapsedMs),
                        TimeSpan.FromMilliseconds(lastLoggedMs)) == expected).ToProperty();
        });
    }
}
