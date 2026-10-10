using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>共享战斗终态的权威回查/幂等恢复测试。</summary>
public sealed class FightDoneRecoveryTests
{
    private const string SyncKey = "0:route/s:1:fight:0";

    [Fact]
    public void Query_FinalizesReachedQuota_AndSecondQueryIsIdempotent()
    {
        var manager = CreateRoom(out var roomCode, out var room);
        room.FightParticipantSets[SyncKey] = new HashSet<string>(
            ["conn-1", "conn-2", "conn-3", "conn-4"]);
        room.FightDoneSets[SyncKey] = new HashSet<string>(["conn-1", "conn-2"]);

        var first = manager.GetFightDoneStatus(roomCode, SyncKey);
        var second = manager.GetFightDoneStatus(roomCode, SyncKey);

        Assert.True(first.Terminal);
        Assert.True(first.ShouldBroadcast);
        Assert.Equal(2, first.DoneCount);
        Assert.Equal(4, first.ParticipantCount);
        Assert.Equal(2, first.RequiredDoneCount);
        Assert.True(second.Terminal);
        Assert.False(second.ShouldBroadcast);
    }

    [Fact]
    public void Query_ReplaysPreviouslyBroadcastTerminal()
    {
        var manager = CreateRoom(out var roomCode, out _);
        foreach (var connectionId in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
            manager.RecordFightParticipant(roomCode, SyncKey, connectionId);

        Assert.False(manager.RecordFightDone(roomCode, SyncKey, "conn-1"));
        Assert.True(manager.RecordFightDone(roomCode, SyncKey, "conn-2"));

        var replay = manager.GetFightDoneStatus(roomCode, SyncKey);

        Assert.True(replay.Terminal);
        Assert.False(replay.ShouldBroadcast);
    }

    private static RoomManager CreateRoom(out string roomCode, out Room room)
    {
        var manager = new RoomManager();
        roomCode = manager.CreateRoom("conn-1", "host", playerUid: "uid-1", expectedPlayerCount: 4);
        room = manager.GetRoom(roomCode)!;
        room.HostConfig = new RoomConfig
        {
            SharedFightEndQuorumEnabled = true,
            SharedFightEndQuorumRatio = 0.5,
        };

        foreach (var connectionId in new[] { "conn-2", "conn-3", "conn-4" })
        {
            manager.AddPlayerForTesting(roomCode, new PlayerInfo
            {
                ConnectionId = connectionId,
                PlayerId = connectionId,
                PlayerUid = $"uid-{connectionId[connectionId.Length - 1]}",
                LastHeartbeat = DateTime.UtcNow,
            });
        }

        return manager;
    }
}
