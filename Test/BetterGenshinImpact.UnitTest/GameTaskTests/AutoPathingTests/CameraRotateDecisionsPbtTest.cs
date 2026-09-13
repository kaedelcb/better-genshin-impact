#nullable enable

using BetterGenshinImpact.GameTask.AutoPathing;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// CameraRotateDecisions.IsRotationArrived 旋转到位判定纯函数 PBT（A11 收口）。
/// **Validates: spec fight-return-to-point-seek-rotation-conflict-fix Property 2/3**
/// 主守护：measuredDiff == null（本轮未真实测量：抢锁失败/异常）→ 恒 false（绝不误判到位）；
/// 非空时语义恒等价于 |diff| &lt; maxDiff + count / 2.0f（阈值边界含浮点除法）。
/// </summary>
public class CameraRotateDecisionsPbtTest
{
    private static Gen<int> G(int lo, int hi) => Gen.Choose(lo, hi);

    /// <summary>P1（主规格）：null（未测量）→ 恒 false，maxDiff/count 任意。</summary>
    [Property(MaxTest = 500)]
    public Property IsRotationArrived_Null_AlwaysFalse(int maxDiff, int count)
    {
        return (!CameraRotateDecisions.IsRotationArrived(null, maxDiff, count)).ToProperty();
    }

    /// <summary>P2（oracle 等价）：非空输入语义恒等价于 |diff| &lt; maxDiff + count / 2.0f（公式直译，含负 count 的公式外延——生产域内 count ≥ 0）。</summary>
    [Property(MaxTest = 1000)]
    public Property IsRotationArrived_MatchesOracle(int diffMilli, int maxDiff, int count)
    {
        // diff 以 0.001 度为粒度生成，避免浮点生成器的 NaN/Infinity 噪声
        var diff = diffMilli / 1000f;
        var expected = Math.Abs(diff) < maxDiff + count / 2.0f;
        return (CameraRotateDecisions.IsRotationArrived(diff, maxDiff, count) == expected).ToProperty();
    }

    /// <summary>P3（阈值边界，maxDiff=2/count=1 → 阈值 2.5）：2.4 → true；2.5（恰等）→ false；2.6 → false。</summary>
    [Property(MaxTest = 200)]
    public Property IsRotationArrived_ThresholdBoundary_Exclusive(float offset)
    {
        var diff = 2.4f + offset % 0.3f;   // 落在 [2.4, 2.7) 邻域
        var expected = Math.Abs(diff) < 2.5f;
        return (CameraRotateDecisions.IsRotationArrived(diff, 2, 1) == expected).ToProperty();
    }

    /// <summary>P4（对称性）：|diff| 由 Abs 决定 → 正负同判。</summary>
    [Property(MaxTest = 500)]
    public Property IsRotationArrived_SymmetricAroundZero(float diff, int maxDiff, int count)
    {
        var d = diff / 1000f;
        return (CameraRotateDecisions.IsRotationArrived(d, maxDiff, count)
                == CameraRotateDecisions.IsRotationArrived(-d, maxDiff, count)).ToProperty();
    }
}
