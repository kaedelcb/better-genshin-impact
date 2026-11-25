using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RouteVerificationSyncTests
{
    private readonly RoomManager _roomManager;
    private readonly Mock<ILogger<RoomManager>> _loggerMock;

    public RouteVerificationSyncTests()
    {
        _loggerMock = new Mock<ILogger<RoomManager>>();
        _roomManager = new RoomManager(50, _loggerMock.Object);
    }

    [Fact]
    public void RecordRouteVerificationDone_WithOfflinePlayer_ShouldIgnoreOfflinePlayer()
    {
        // Arrange: 创建房间
        var roomCode = _roomManager.CreateRoom("conn-host", "Host", null, "host-uid", 1);
        var onlineConnectionId = "online-conn";
        var offlineConnectionId = "offline-conn";

        // Make host offline so only the test players are counted
        var room = _roomManager.GetRoom(roomCode);
        Assert.NotNull(room);
        room!.Players[0].LastHeartbeat = DateTime.UtcNow.AddMinutes(-5);

        // Add online player using helper (registers connection mapping)
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = onlineConnectionId,
            PlayerId = onlineConnectionId,
            PlayerName = "Online",
            PlayerUid = "100",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow
        });

        // Add offline player (old heartbeat) using helper
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = offlineConnectionId,
            PlayerId = offlineConnectionId,
            PlayerName = "Offline",
            PlayerUid = "101",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow.AddMinutes(-5) // 5 minutes ago
        });

        // Act - only online player reports
        var result = _roomManager.RecordRouteVerificationDone(roomCode, onlineConnectionId);

        // Assert - should return true because offline player is ignored
        Assert.True(result);
    }

    [Fact]
    public void RecordRouteVerificationDone_WithAllOnlinePlayersReported_ShouldReturnTrue()
    {
        // Arrange: 创建房间
        var roomCode = _roomManager.CreateRoom("conn-host", "Host", null, "host-uid", 1);
        var conn1 = "conn1";
        var conn2 = "conn2";

        // Make host offline so only the test players are counted
        var room = _roomManager.GetRoom(roomCode);
        Assert.NotNull(room);
        room!.Players[0].LastHeartbeat = DateTime.UtcNow.AddMinutes(-5);

        // Add two online players using helper (registers connection mapping)
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = conn1,
            PlayerId = conn1,
            PlayerName = "Player1",
            PlayerUid = "100",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow
        });
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = conn2,
            PlayerId = conn2,
            PlayerName = "Player2",
            PlayerUid = "101",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow
        });

        // Act - both players report
        var result1 = _roomManager.RecordRouteVerificationDone(roomCode, conn1);
        var result2 = _roomManager.RecordRouteVerificationDone(roomCode, conn2);

        // Assert
        Assert.False(result1); // First report should not complete
        Assert.True(result2);  // Second report should complete
    }

    [Fact]
    public void GetRouteVerificationStatus_ShouldReturnCorrectCounts()
    {
        // Arrange: 创建房间
        var roomCode = _roomManager.CreateRoom("conn-host", "Host", null, "host-uid", 1);
        var onlineConn = "online";
        var offlineConn = "offline";

        // Make host offline so only the test players are counted
        var room = _roomManager.GetRoom(roomCode);
        Assert.NotNull(room);
        room!.Players[0].LastHeartbeat = DateTime.UtcNow.AddMinutes(-5);

        // Add online player using helper
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = onlineConn,
            PlayerId = onlineConn,
            PlayerName = "Online",
            PlayerUid = "100",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow
        });
        // Add offline player using helper
        _roomManager.AddPlayerForTesting(roomCode, new PlayerInfo
        {
            ConnectionId = offlineConn,
            PlayerId = offlineConn,
            PlayerName = "Offline",
            PlayerUid = "101",
            Status = PlayerStatus.Pathing,
            LastHeartbeat = DateTime.UtcNow.AddMinutes(-5)
        });

        // Act
        _roomManager.RecordRouteVerificationDone(roomCode, onlineConn);
        var (onlineCount, reportedCount) = _roomManager.GetRouteVerificationStatus(roomCode);

        // Assert
        Assert.Equal(1, onlineCount);  // Only one online player
        Assert.Equal(1, reportedCount); // One reported
    }

    [Fact]
    public void FormatRouteDiffDetail_IncludesPerPlayerShortHashesMissingAndTruncation()
    {
        var reports = new List<(string Label, List<RouteHash> Routes)>
        {
            ("房主(10001)", [
                new RouteHash { FileName = "01.json", Md5 = "1111222233334444" },
                new RouteHash { FileName = "02.json", Md5 = "aaaa555566667777" },
                new RouteHash { FileName = "03.json", Md5 = "cccc888899990000" },
            ]),
            ("成员A(10002)", [
                new RouteHash { FileName = "01.json", Md5 = "9999888877776666" },
                new RouteHash { FileName = "03.json", Md5 = "dddd888899990000" },
            ]),
        };

        var detail = RoomOperations.FormatRouteDiffDetail(reports, ["01.json", "02.json", "03.json"], maxFiles: 2);

        Assert.Contains("01.json[房主(10001)=11112222, 成员A(10002)=99998888]", detail);
        Assert.Contains("02.json[房主(10001)=aaaa5555, 成员A(10002)=缺失]", detail);
        Assert.Contains("其余 1 个文件省略", detail);
        Assert.DoesNotContain("03.json", detail);
    }

    [Fact]
    public async Task ReportRouteListAsync_LogsPerMemberDiffDetailWhenHashesMismatch()
    {
        var harness = new GatewayTestHarness();
        var logs = new List<string>();
        var loggerMock = new Mock<ILogger<RoomOperations>>();
        loggerMock.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(inv =>
            {
                var formatter = (Delegate)inv.Arguments[4];
                var message = formatter.DynamicInvoke(inv.Arguments[2], inv.Arguments[3])?.ToString();
                if (message != null) logs.Add(message);
            }));

        var broadcaster = new GatewayBroadcaster(
            harness.LegacyHub.Object,
            harness.GatewayHub.Object,
            harness.Tracker,
            new Mock<ILogger<GatewayBroadcaster>>().Object);
        var ops = new RoomOperations(
            harness.RoomManager,
            loggerMock.Object,
            broadcaster,
            new RoomPhaseObserver(new Mock<ILogger<RoomPhaseObserver>>().Object),
            harness.Capabilities);

        var host = GatewayHandlerContext.Legacy("conn-host");
        var member = GatewayHandlerContext.Legacy("conn-member");
        const string version = "0.57.0+lcb.20261125";
        var (roomCode, _) = await ops.CreateRoomAsync(host, "房主", playerUid: "10001", expectedPlayerCount: 2, reportedVersion: version);
        var joined = await ops.JoinRoomAsync(member, roomCode, "成员A", "10002", reportedVersion: version);
        Assert.True(joined.Success, joined.Error);

        await ops.ReportRouteListAsync(host, [new RouteHash { FileName = "route.json", Md5 = "12345678abcdef00" }]);
        await ops.ReportRouteListAsync(member, [new RouteHash { FileName = "route.json", Md5 = "fedcba9876543210" }]);

        Assert.Contains(logs, msg =>
            msg.Contains("路线存在差异")
            && msg.Contains("route.json[房主(10001)=12345678, 成员A(10002)=fedcba98]"));
        harness.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "RouteDiffReceived",
            It.Is<object?[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}