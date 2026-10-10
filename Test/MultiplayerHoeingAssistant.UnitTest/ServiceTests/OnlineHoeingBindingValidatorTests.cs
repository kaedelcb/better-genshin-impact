using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class OnlineHoeingBindingValidatorTests
{
    [Fact]
    public void LegacyEmptyTypesAreAllGroups()
    {
        var result = OnlineHoeingBindingValidator.Validate(["A", "B"], []);

        Assert.True(result.IsValid);
        Assert.Equal(["group", "group"], result.Types);
        Assert.Equal("group,group", result.Layout);
    }

    [Fact]
    public void RejectsPartialTypesUnknownTypesAndDuplicates()
    {
        var result = OnlineHoeingBindingValidator.Validate(
            ["A", "A", " ", "D"], ["group", "group", "group", "invalid"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("重复"));
        Assert.Contains(result.Errors, e => e.Contains("为空"));
        Assert.Contains(result.Errors, e => e.Contains("类型无效"));
    }

    [Fact]
    public void AvailabilityCheckRejectsMissingLocalTask()
    {
        var result = OnlineHoeingBindingValidator.Validate(
            ["存在", "不存在"], ["group", "onedragon"], ["存在"], ["另一条龙"], true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不存在"));
    }

    [Fact]
    public void LayoutComparisonAllowsDifferentNamesButRejectsDifferentShape()
    {
        var left = OnlineHoeingBindingValidator.Validate(["甲", "乙"], ["group", "onedragon"]);
        var sameShape = OnlineHoeingBindingValidator.Validate(["丙", "丁"], ["group", "onedragon"]);
        var differentShape = OnlineHoeingBindingValidator.Validate(["丙"], ["group"]);

        Assert.Null(OnlineHoeingBindingValidator.CompareLayout(left, sameShape, "成员B"));
        Assert.Contains("同一轮联机必须保持",
            OnlineHoeingBindingValidator.CompareLayout(left, differentShape, "成员C"));
    }
}
