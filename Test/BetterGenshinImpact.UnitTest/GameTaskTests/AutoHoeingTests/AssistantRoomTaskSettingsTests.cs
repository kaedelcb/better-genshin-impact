using BetterGenshinImpact.GameTask.AutoOnline;
using MultiplayerHoeingAssistant.Models;
using System.Text.Json;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class AssistantRoomTaskSettingsTests
{
    [Fact]
    public void ReadsSavedNamesFromTheChosenAssistantRoot()
    {
        var fixture = Directory.CreateTempSubdirectory("bgi-room-choices-");
        try
        {
            var roamingRoot = Directory.CreateDirectory(Path.Combine(fixture.FullName, "NexusBGI"));
            var explicitRoot = Directory.CreateDirectory(Path.Combine(fixture.FullName, "explicit"));
            var config = new AssistConfig
            {
                RoomProfiles =
                [
                    new() { Id = "a", Name = "周末房间", RoomCode = "G12345", Password = "test-password" },
                    new() { Id = "b", Name = "平日房间", RoomCode = "G12345", ServerUrl = "https://other.example.test" },
                    new() { Id = "c", Name = "重复 C", RoomCode = "AAAABC" },
                    new() { Id = "d", Name = "重复 c", RoomCode = "AAAABD" },
                    new() { Id = "draft", Name = "待补房号" }
                ]
            };
            File.WriteAllText(Path.Combine(roamingRoot.FullName, "assistant-config.json"), JsonSerializer.Serialize(config));
            Assert.Equal(["周末房间", "平日房间"], SwitchAssistantRoomTask.ReadSavedRoomNames(null, fixture.FullName));
            Assert.Empty(SwitchAssistantRoomTask.ReadSavedRoomNames(explicitRoot.FullName, fixture.FullName));
            Assert.Empty(SwitchAssistantRoomTask.ReadSavedRoomNames("relative", fixture.FullName));
            File.WriteAllText(Path.Combine(explicitRoot.FullName, "assistant-config.json"), "{ invalid json");
            Assert.Empty(SwitchAssistantRoomTask.ReadSavedRoomNames(explicitRoot.FullName, fixture.FullName));
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"targetRoomName\":\"\"}", false)]
    [InlineData("{\"targetRoomName\":\"  周末房间  \"}", true)]
    public void OnlineTaskDistinguishesCurrentRoomFromExplicitTarget(string json, bool targeted)
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        Assert.Equal(targeted, NotifyOnlineTask.HasTargetRoom(settings));
        Assert.Contains(BetterGenshinImpact.GameTask.SoloTaskRegistry.GetSettingItems(NotifyOnlineTask.TaskName),
            item => item.Name == SwitchAssistantRoomTask.TargetRoomName);
    }

    [Fact]
    public void ReadsPreviouslySavedTaskTarget()
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"targetRoomName":"  周末房间  "}""");
        Assert.Equal("周末房间", SwitchAssistantRoomTask.ReadTargetRoomName(settings));
        Assert.Null(SwitchAssistantRoomTask.ReadTargetRoomName(null));
    }
}
