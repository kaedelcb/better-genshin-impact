using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RoomLogStoreTests
{
    [Fact]
    public void CapturesOnlyLogsWithMatchingStructuredRoomCode()
    {
        var store = new RoomLogStore();
        using var provider = new RoomLogProvider(store);
        var logger = provider.CreateLogger("BgiCoordinatorServer.Services.RoomOperations");

        logger.LogInformation("房间 {Code} 已创建", "ABC123");
        logger.LogWarning("控制房间 {Group} 成员断线", "CTRL_DEF456");
        logger.LogError("没有房间标识的全局错误");
        logger.LogInformation("房间 {Code} 不应匹配短码", "ABC1234");

        var gameLog = Assert.Single(store.Read("ABC123"));
        Assert.Contains("已创建", gameLog.Message);
        Assert.Equal("Information", gameLog.Level);
        var controlLog = Assert.Single(store.Read("DEF456"));
        Assert.Contains("成员断线", controlLog.Message);
        Assert.Empty(store.Read("GH7890"));
        Assert.Empty(store.Read("../ABC123"));
    }

    [Fact]
    public void ReadIsBoundedAndNewestFirst()
    {
        var store = new RoomLogStore();
        for (var index = 0; index < 205; index++)
            store.Append(["ABC123"], LogLevel.Information, "Test", $"第 {index} 条");

        var entries = store.Read("ABC123", 1000);
        Assert.Equal(200, entries.Count);
        Assert.Equal("第 204 条", entries[0].Message);
        Assert.Equal("第 5 条", entries[^1].Message);
    }
}
