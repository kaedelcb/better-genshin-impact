using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class BatchApplicantWhitelistTests
{
    private static readonly (string PlayerUid, string PlayerName)[] StandardRoomPlayers =
    [
        ("10001", "一时兴起"),
        ("10002", "小鲨鱼"),
        ("10003", "茶包s")
    ];

    private static readonly string[] StandardWhitelist = ["一时兴起", "小鲨鱼", "茶包s"];
    private static readonly string[] StandardRoster = ["10001", "10002", "10003"];

    [Theory]
    [InlineData("一时兴起")]
    [InlineData("时兴起")]
    [InlineData("二时兴起")]
    [InlineData("一时兴")]
    public void ConfirmBatchApplicant_AllowsExactAndSixtyPercentFuzzyMatchForBatchMember(string ocrName)
    {
        Assert.True(WhitelistMatchDecisions.ConfirmBatchApplicant(
            ocrName, StandardWhitelist, StandardRoster, StandardRoomPlayers));
    }

    [Theory]
    [InlineData("兴起")]
    [InlineData("二三兴起")]
    [InlineData("完全无关")]
    [InlineData("")]
    public void ConfirmBatchApplicant_RejectsBelowThresholdOrUnmatchedNames(string ocrName)
    {
        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant(
            ocrName, StandardWhitelist, StandardRoster, StandardRoomPlayers));
    }

    [Fact]
    public void ConfirmBatchApplicant_RejectsExactMatchOutsideBatchWithoutFallingBackToFuzzy()
    {
        var players = new[] { ("10001", "茶包1"), ("10002", "茶包2") };
        string[] whitelist = ["茶包1", "茶包2"];
        string[] roster = ["10001"];

        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("茶包2", whitelist, roster, players));
        Assert.True(WhitelistMatchDecisions.ConfirmBatchApplicant("茶包1", whitelist, roster, players));
    }

    [Fact]
    public void ConfirmBatchApplicant_RejectsAmbiguousFuzzyMatchWhenNonBatchRoomPlayerAlsoMatches()
    {
        var players = new[] { ("10001", "茶包1"), ("10002", "茶包2") };
        string[] whitelist = ["茶包1"];
        string[] roster = ["10001"];

        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("茶包2", whitelist, roster, players));
    }

    [Fact]
    public void ConfirmBatchApplicant_RejectsAmbiguousFuzzyMatchAcrossMultipleWhitelistEntries()
    {
        var players = new[] { ("10001", "茶包A") };
        string[] whitelist = ["茶包A", "茶包B"];
        string[] roster = ["10001"];

        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("茶包", whitelist, roster, players));
    }

    [Theory]
    [InlineData("小兴")]
    [InlineData("时兴起")]
    [InlineData("时兴起(小兴)")]
    [InlineData("一时兴起（小兴）")]
    public void ConfirmBatchApplicant_SupportsBracketRemarkLinkingToBatchMember(string ocrName)
    {
        var players = new[] { ("10001", "一时兴起") };
        string[] whitelist = ["一时兴起(小兴)"];
        string[] roster = ["10001"];

        Assert.True(WhitelistMatchDecisions.ConfirmBatchApplicant(ocrName, whitelist, roster, players));
    }

    [Fact]
    public void ConfirmBatchApplicant_RejectsMissingWhitelistOrRoster()
    {
        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("一时兴起", [], StandardRoster, StandardRoomPlayers));
        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("一时兴起", null, StandardRoster, StandardRoomPlayers));
        Assert.False(WhitelistMatchDecisions.ConfirmBatchApplicant("一时兴起", StandardWhitelist, [], StandardRoomPlayers));
    }

    [Fact]
    public void ConfirmBatchTemplateMatch_RequiresBatchOwnership()
    {
        var players = new[] { ("10001", "一时兴起"), ("10002", "茶包2") };
        string[] whitelist = ["一时兴起", "茶包2"];
        string[] roster = ["10001"];

        Assert.True(WhitelistMatchDecisions.ConfirmBatchTemplateMatch("10001.png", whitelist, roster, players));
        Assert.True(WhitelistMatchDecisions.ConfirmBatchTemplateMatch("一时兴起.png", whitelist, roster, players));
        Assert.False(WhitelistMatchDecisions.ConfirmBatchTemplateMatch("10002.png", whitelist, roster, players));
        Assert.False(WhitelistMatchDecisions.ConfirmBatchTemplateMatch("茶包2.png", whitelist, roster, players));
        Assert.False(WhitelistMatchDecisions.ConfirmBatchTemplateMatch("99999.png", whitelist, roster, players));
    }

    [Fact]
    public void ApplicantNameRoi1080P_ClearsAvatarAndLeavesLeftPaddingBeforeFirstChar()
    {
        // 1920x1080 完整截图实测：头像右缘 X=654，纯白间隙 X=655..676，首字抗锯齿外缘 X=677 / 主笔画 X=678，内框右边界 X=1357
        const int avatarRightX = 654;
        const int firstCharLeftX = 677;
        const int innerBoxRightX = 1357;

        Assert.InRange(AutoPartyTask.ApplicantNameRoiX1080P, avatarRightX + 6, firstCharLeftX - 10);
        Assert.True(AutoPartyTask.ApplicantNameRoiX1080P + AutoPartyTask.ApplicantNameRoiW1080P <= innerBoxRightX);
        Assert.True(AutoPartyTask.ApplicantNameRoiW1080P >= 420);
    }
}
