using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class AssistantRoomProfileTests
{
    [Fact]
    public void LegacyRoomKeepsCodeBindingsAndAlreadyFiredSchedule()
    {
        var date = new DateTime(2026, 11, 10);
        var config = new AssistConfig
        {
            PlayerUid = "111111111", TeamUids = ["222222222", "111111111"], ControlRoomPassword = "test-password",
            OnlineHoeingGroupNames = ["A路线"], OnlineHoeingGroupTypes = ["group"],
            ScheduledOnlineTime = "12:00", ScheduledOnlineRequiresConfirmation = true, ScheduledOnlineLastFireDate = date
        };
        var oldCode = AssistConfigManager.GenerateControlRoomCode(config.TeamUids);
        Assert.True(config.EnsureRoomProfiles());
        Assert.False(config.EnsureRoomProfiles());
        var profile = Assert.Single(config.RoomProfiles);
        Assert.Equal(oldCode, config.EffectiveRoomCode);
        Assert.Equal(date, profile.ScheduledOnlineLastFireDate);
        Assert.True(profile.ScheduledOnlineRequiresConfirmation);
        Assert.Equal("A路线", Assert.Single(profile.OnlineHoeingGroupNames));
    }

    [Fact]
    public void OldProfilesInheritSharedServerWithoutOverwritingExplicitAddresses()
    {
        var config = new AssistConfig { ServerUrl = "https://original.example.test/gateway-bgi" };
        var inherited = new AssistantRoomProfile { Id = "old", RoomCode = "ABCDEF" };
        var explicitServer = new AssistantRoomProfile { Id = "other", RoomCode = "ABCDEF", ServerUrl = "https://other.example.test" };
        config.RoomProfiles.AddRange([inherited, explicitServer]);
        Assert.True(config.FillLegacyRoomServerUrls());
        Assert.False(config.FillLegacyRoomServerUrls());
        explicitServer.ApplyTo(config);
        Assert.Equal("https://other.example.test", config.ServerUrl);
        Assert.Equal("https://original.example.test/gateway-bgi", config.GetRoomServerUrl(inherited));
        inherited.ApplyTo(config);
        Assert.Equal("https://original.example.test/gateway-bgi", config.ServerUrl);
    }

    [Theory]
    [InlineData("https://ROOMS.example.test/app/hub/", "https://rooms.example.test:443/app", true)]
    [InlineData("https://rooms.example.test/app/sub/../hub", "https://rooms.example.test/app", true)]
    [InlineData("https://rooms.example.test/app", "https://rooms.example.test/App", false)]
    [InlineData("http://localhost:5010", "http://localhost:5011", false)]
    public void ServerIdentityNormalizesAliasesButKeepsDifferentEndpoints(string first, string second, bool same)
        => Assert.Equal(same, RoomServerAddress.Same(first, second));

    [Fact]
    public void ApplyingAnotherRoomDoesNotChangeOldRoomOrGlobalIdentity()
    {
        var config = new AssistConfig { PlayerUid = "111111111", BgiPath = "bgi.exe" };
        var a = new AssistantRoomProfile { Id = "fixture-a", Name = "房间 A", RoomCode = "AAAAAA", TeamUids = ["111111111", "222222222"],
            OnlineHoeingGroupNames = ["A"], ScheduledOnlineTime = "10:00", ScheduledOnlineLastFireDate = new DateTime(2026, 11, 10) };
        var b = new AssistantRoomProfile { Id = "fixture-b", Name = "房间 B", RoomCode = "BBBBBZ", TeamUids = ["111111111", "333333333"],
            OnlineHoeingGroupNames = ["B"], ScheduledOnlineTime = "18:30", ScheduledOnlineRequiresConfirmation = true };
        config.RoomProfiles.AddRange([a, b]);
        a.ApplyTo(config);
        config.ActiveRoomProfileId = a.Id;
        Assert.Equal(a.RoomCode, config.EffectiveRoomCode);
        b.ApplyTo(config);
        config.ActiveRoomProfileId = b.Id;
        config.OnlineHoeingGroupNames.Add("B收尾");
        b.Capture(config);
        var roundTrip = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal("BBBBBZ", roundTrip.EffectiveRoomCode);
        Assert.Equal("A", Assert.Single(roundTrip.RoomProfiles[0].OnlineHoeingGroupNames));
        Assert.Equal(new[] { "B", "B收尾" }, roundTrip.RoomProfiles[1].OnlineHoeingGroupNames.ToArray());
        Assert.True(roundTrip.ScheduledOnlineRequiresConfirmation);
        Assert.Equal("111111111", roundTrip.PlayerUid);
        Assert.Equal("bgi.exe", roundTrip.BgiPath);
    }
}
