using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.OneDragon;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

/// <summary>
/// R4.6 Batch 3a 夹具：I3 脱敏规则 + E9 根/叶结果合同（ASTRA 三轮 B7 修复）。
/// </summary>
public class R46Batch3aRuleTests
{
    [Theory]
    [InlineData(null, "(空)")]
    [InlineData("", "(空)")]
    [InlineData("12345", "***")] // ≤5 位全遮盖（前三后二在 5 位下仍全露）
    [InlineData("123456", "123***56")]
    [InlineData("123456789", "123***89")]
    public void MaskUid_按长度规则脱敏(string? uid, string expected)
        => Assert.Equal(expected, SensitiveTextMask.MaskUid(uid));

    [Fact]
    public void E9_整龙仅正常跳过且尾部跑完_根结果成功()
        => Assert.Equal(TaskRunResult.Ran,
            OneDragonRootResultRule.DecideRootResult(TaskRunResult.Skipped, finishMark: true));

    [Fact]
    public void E9_整龙仅正常跳过但尾部未跑完_根结果失败()
        => Assert.Equal(TaskRunResult.Failed,
            OneDragonRootResultRule.DecideRootResult(TaskRunResult.Skipped, finishMark: false));

    [Fact]
    public void E9_正常完成_根结果成功()
        => Assert.Equal(TaskRunResult.Ran,
            OneDragonRootResultRule.DecideRootResult(TaskRunResult.Ran, finishMark: true));

    [Theory]
    [InlineData(TaskRunResult.Failed)]
    [InlineData(TaskRunResult.Cancelled)]
    [InlineData(TaskRunResult.Preempted)]
    [InlineData(TaskRunResult.RejectedSlotBusy)]
    public void E9_坏结果原样透传且阻断尾部(TaskRunResult bad)
    {
        Assert.Equal(bad, OneDragonRootResultRule.DecideRootResult(bad, finishMark: true));
        Assert.False(OneDragonRootResultRule.ShouldRunTail(bad));
    }

    [Theory]
    [InlineData(TaskRunResult.Ran)]
    [InlineData(TaskRunResult.Skipped)]
    public void E9_正常完成与仅跳过均放行尾部(TaskRunResult good)
        => Assert.True(OneDragonRootResultRule.ShouldRunTail(good));
}
