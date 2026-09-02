#nullable enable

using BetterGenshinImpact.GameTask.AutoTrackPath;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// FastDragFreshPriorDecisions 的 PBT 属性测试。
/// **Validates: bugfix.md BC-1/BC-3/OQ-4 (design.md 组件 6)**
/// </summary>
public class FastDragFreshPriorDecisionsTest
{
    /// <summary>属性 1（主规格）：Resolve 全表穷举（2×2×2）。</summary>
    [Property]
    public Property Resolve_FullTruthTable(bool isFresh, bool activeSucceeded, bool cacheHasValid)
    {
        var expected = isFresh
            ? (activeSucceeded ? FreshPriorSource.Active : FreshPriorSource.None)
            : (cacheHasValid ? FreshPriorSource.Cache : FreshPriorSource.None);

        var actual = FastDragFreshPriorDecisions.Resolve(isFresh, activeSucceeded, cacheHasValid);
        return (actual == expected).ToProperty();
    }

    /// <summary>属性 2（BC-1/OQ-4）：fresh 且主动识别失败 → 恒 None（即使缓存有效，绝不回读陈旧缓存）。</summary>
    [Property(MaxTest = 1000)]
    public Property Resolve_FreshFailureNeverUsesCache(bool cacheHasValid)
    {
        var actual = FastDragFreshPriorDecisions.Resolve(true, activeSucceeded: false, cacheHasValid);
        return (actual == FreshPriorSource.None).ToProperty();
    }

    /// <summary>属性 3：fresh 且主动识别成功 → 恒 Active（不管缓存）。</summary>
    [Property(MaxTest = 1000)]
    public Property Resolve_FreshSuccessAlwaysActive(bool cacheHasValid)
    {
        var actual = FastDragFreshPriorDecisions.Resolve(true, activeSucceeded: true, cacheHasValid);
        return (actual == FreshPriorSource.Active).ToProperty();
    }

    /// <summary>属性 4（BC-3）：非 fresh 且缓存有效 → 恒 Cache。</summary>
    [Property(MaxTest = 1000)]
    public Property Resolve_NotFreshUsesCache(bool activeSucceeded)
    {
        var actual = FastDragFreshPriorDecisions.Resolve(false, activeSucceeded, cacheHasValid: true);
        return (actual == FreshPriorSource.Cache).ToProperty();
    }

    /// <summary>属性 5：HasPrior 与来源一致（None 无先验，Active/Cache 有）。</summary>
    [Property]
    public Property HasPrior_MatchesSource(FreshPriorSource source)
    {
        return (FastDragFreshPriorDecisions.HasPrior(source) == (source != FreshPriorSource.None)).ToProperty();
    }
}