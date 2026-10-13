using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPick;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

public sealed class AutoPickConfigCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void OldOcrConfig_RemainsInOcrModeAndPreservesExistingChoices()
    {
        const string oldJson = """
            {
              "enabled": true,
              "ocrEngine": "Yap",
              "fastModeEnabled": true,
              "pickKey": "E",
              "itemIconLeftOffset": 66,
              "itemTextLeftOffset": 119,
              "itemTextRightOffset": 420,
              "mode": "Blacklist",
              "blacklistModePickEnabled": true,
              "whitelistModeDoNotPickEnabled": false
            }
            """;

        var config = JsonSerializer.Deserialize<AutoPickConfig>(oldJson, JsonOptions)!;
        Assert.False(config.MojangMatchEnabled);
        Assert.True(config.Enabled);
        Assert.Equal("Yap", config.OcrEngine);
        Assert.True(config.FastModeEnabled);
        Assert.Equal("E", config.PickKey);
        Assert.Equal(66, config.ItemIconLeftOffset);
        Assert.Equal(119, config.ItemTextLeftOffset);
        Assert.Equal(420, config.ItemTextRightOffset);
        Assert.Equal(AutoPickMode.Blacklist, config.Mode);
        Assert.True(config.BlacklistModePickEnabled);
        Assert.False(config.WhitelistModeDoNotPickEnabled);

        var reread = RoundTrip(config);
        Assert.False(reread.MojangMatchEnabled);
        Assert.Equal(config.OcrEngine, reread.OcrEngine);
        Assert.Equal(config.PickKey, reread.PickKey);
        Assert.Equal(config.Mode, reread.Mode);
        Assert.Equal(config.BlacklistModePickEnabled, reread.BlacklistModePickEnabled);
        Assert.Equal(config.WhitelistModeDoNotPickEnabled, reread.WhitelistModeDoNotPickEnabled);
    }

    [Fact]
    public void TemplateOptIn_RoundTripsWithoutLosingTheSavedOcrSettings()
    {
        var config = new AutoPickConfig
        {
            MojangMatchEnabled = true,
            OcrEngine = "Yap",
            PickKey = "E",
            Mode = AutoPickMode.Blacklist,
            BlacklistModePickEnabled = true,
            MojangMatchThreshold = 0.91,
            TestModeEnabled = true,
            AutoScreenshotStreak = 3,
            PickLogLevel = 2,
            InteractionDelay = 80,
            RepeatFInterval = 30,
            ConfirmInterval = 50,
            ScrollDistance = -360,
            ScrollInterval = 100,
            ScrollAfterInterval = 20,
        };

        var reread = RoundTrip(config);
        Assert.True(reread.MojangMatchEnabled);
        Assert.Equal("Yap", reread.OcrEngine);
        Assert.Equal("E", reread.PickKey);
        Assert.Equal(AutoPickMode.Blacklist, reread.Mode);
        Assert.True(reread.BlacklistModePickEnabled);
        Assert.Equal(0.91, reread.MojangMatchThreshold);
        Assert.True(reread.TestModeEnabled);
        Assert.Equal(3, reread.AutoScreenshotStreak);
        Assert.Equal(2, reread.PickLogLevel);
        Assert.Equal(80, reread.InteractionDelay);
        Assert.Equal(30, reread.RepeatFInterval);
        Assert.Equal(50, reread.ConfirmInterval);
        Assert.Equal(-360, reread.ScrollDistance);
        Assert.Equal(100, reread.ScrollInterval);
        Assert.Equal(20, reread.ScrollAfterInterval);

        reread.MojangMatchEnabled = false;
        Assert.Equal("Yap", reread.OcrEngine);
        Assert.Equal("E", reread.PickKey);
        Assert.Equal(AutoPickMode.Blacklist, reread.Mode);
    }

    [Fact]
    public void LegacyWhitelistMigration_DoesNotEnableTemplatePickup()
    {
        var config = JsonSerializer.Deserialize<AutoPickConfig>(
            "{\"whiteListEnabled\":true,\"ocrEngine\":\"Paddle\"}", JsonOptions)!;

        config.MigrateLegacyConfig();

        Assert.False(config.MojangMatchEnabled);
        Assert.Equal("Paddle", config.OcrEngine);
        Assert.Equal(AutoPickMode.Blacklist, config.Mode);
        Assert.True(config.BlacklistModePickEnabled);
        Assert.Null(config.LegacyWhiteListEnabled);
        Assert.DoesNotContain("whiteListEnabled", JsonSerializer.Serialize(config, JsonOptions));
    }

    [Fact]
    public void OldAndNewGroupConfig_RoundTripIndependentTemplateLists()
    {
        var oldGroup = JsonSerializer.Deserialize<AutoPickGroupConfig>("{}", JsonOptions)!;
        Assert.Empty(oldGroup.WhiteList);
        Assert.Empty(oldGroup.BlackList);

        var group = new AutoPickGroupConfig
        {
            WhiteList = ["薄荷", "甜甜花"],
            BlackList = ["调查", "甜甜花"],
        };
        var reread = JsonSerializer.Deserialize<AutoPickGroupConfig>(
            JsonSerializer.Serialize(group, JsonOptions), JsonOptions)!;

        Assert.True(group.WhiteList.SetEquals(reread.WhiteList));
        Assert.True(group.BlackList.SetEquals(reread.BlackList));
    }

    private static AutoPickConfig RoundTrip(AutoPickConfig config) =>
        JsonSerializer.Deserialize<AutoPickConfig>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions)!;
}
