using System.IO;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

public sealed class RemoteLogRoomHistoryStoreTests
{
    [Fact]
    public void ThreeRooms_ReloadEncryptedPasswords_UpdateAndDeleteIndividually()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var path = Path.Combine(AssistantDataDirectory.Root, "history-store-" + Guid.NewGuid().ToString("N") + ".json");
        const string server = "http://localhost:49123";
        var store = new RemoteLogRoomHistoryStore(path);
        for (var i = 1; i <= 3; i++) store.Remember(server, "room-" + i, "local-secret-" + i);

        // 新实例从磁盘重新载入，三个房间及各自密码均保留。
        var reloaded = new RemoteLogRoomHistoryStore(path);
        Assert.Equal(3, reloaded.GetRooms(server).Count);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal("local-secret-" + i, reloaded.GetPassword(server + "/hub", "room-" + i));
            Assert.DoesNotContain("local-secret-" + i, File.ReadAllText(path));
        }
        reloaded.Remember(server, "room-1", "updated-secret");
        reloaded.Remember("http://localhost:49124", "room-1", "other-server-secret");
        Assert.Equal(3, reloaded.GetRooms(server).Count);
        Assert.Equal("room-1", reloaded.GetRooms(server)[0].RoomCode);
        Assert.Equal("updated-secret", reloaded.GetPassword(server, "room-1"));
        Assert.Equal("other-server-secret", reloaded.GetPassword("http://localhost:49124", "room-1"));

        reloaded.Forget(server, "room-2");
        var afterDelete = new RemoteLogRoomHistoryStore(path);
        Assert.Equal(2, afterDelete.GetRooms(server).Count);
        Assert.Null(afterDelete.GetPassword(server, "room-2"));
        Assert.Equal("local-secret-3", afterDelete.GetPassword(server, "room-3"));
        Assert.Single(afterDelete.GetRooms("http://localhost:49124"));
        Assert.DoesNotContain("updated-secret", File.ReadAllText(path));
    }
}
