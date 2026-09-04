using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>房间实时日志汇聚·观众驱动订阅表（RoomManager 级）测试。</summary>
public class MemberLogSubscriptionTests
{
    private readonly RoomManager _roomManager;

    public MemberLogSubscriptionTests()
    {
        var loggerMock = new Mock<ILogger<RoomManager>>();
        _roomManager = new RoomManager(50, loggerMock.Object);
    }

    [Fact]
    public void Subscribe_Idempotent_AndCountsCorrectly()
    {
        const string group = "CTRL_1001";
        // 两个观众订阅同一目标
        Assert.Equal(1, _roomManager.SubscribeMemberLog(group, "uid-a", "conn-1"));
        Assert.Equal(2, _roomManager.SubscribeMemberLog(group, "uid-a", "conn-2"));
        // 重复订阅幂等：不重复计数
        Assert.Equal(2, _roomManager.SubscribeMemberLog(group, "uid-a", "conn-1"));
        Assert.Equal(2, _roomManager.GetLogSubscriberCount(group, "uid-a"));
    }

    [Fact]
    public void Unsubscribe_NotSubscribed_ReturnsNull_NoNotifyNeeded()
    {
        Assert.Null(_roomManager.UnsubscribeMemberLog("CTRL_1001", "uid-a", "conn-x"));
    }

    [Fact]
    public void Unsubscribe_ToZero_RemovesKey()
    {
        const string group = "CTRL_1001";
        _roomManager.SubscribeMemberLog(group, "uid-a", "conn-1");
        Assert.Equal(0, _roomManager.UnsubscribeMemberLog(group, "uid-a", "conn-1"));
        Assert.Equal(0, _roomManager.GetLogSubscriberCount(group, "uid-a"));
        // key 已移除：再退订返回 null（不产生重复 0 通知）
        Assert.Null(_roomManager.UnsubscribeMemberLog(group, "uid-a", "conn-1"));
    }

    [Fact]
    public void DisconnectCleanup_RemovesFromAllTargets_AndReportsCounts()
    {
        _roomManager.SubscribeMemberLog("CTRL_1001", "uid-a", "conn-1");
        _roomManager.SubscribeMemberLog("CTRL_1001", "uid-a", "conn-2");
        _roomManager.SubscribeMemberLog("CTRL_1001", "uid-b", "conn-1");
        _roomManager.SubscribeMemberLog("CTRL_1002", "uid-c", "conn-1");

        var changed = _roomManager.RemoveLogSubscriberEverywhere("conn-1");

        // conn-1 从三个目标移除：uid-a 剩 1、uid-b 剩 0、uid-c 剩 0
        Assert.Equal(3, changed.Count);
        Assert.Contains(changed, c => c.Group == "CTRL_1001" && c.TargetUid == "uid-a" && c.Count == 1);
        Assert.Contains(changed, c => c.Group == "CTRL_1001" && c.TargetUid == "uid-b" && c.Count == 0);
        Assert.Contains(changed, c => c.Group == "CTRL_1002" && c.TargetUid == "uid-c" && c.Count == 0);
        // conn-2 的订阅不受影响
        Assert.Equal(1, _roomManager.GetLogSubscriberCount("CTRL_1001", "uid-a"));
        // 未订阅过的连接清理返回空
        Assert.Empty(_roomManager.RemoveLogSubscriberEverywhere("conn-never"));
    }

    [Fact]
    public void Subscribe_OverCap_Rejected()
    {
        const string group = "CTRL_1001";
        for (var i = 0; i < 20; i++)
            Assert.Equal(i + 1, _roomManager.SubscribeMemberLog(group, "uid-a", $"conn-{i}"));
        // 第 21 个订阅者被拒绝（-1），计数保持 20
        Assert.Equal(-1, _roomManager.SubscribeMemberLog(group, "uid-a", "conn-20"));
        Assert.Equal(20, _roomManager.GetLogSubscriberCount(group, "uid-a"));
    }
}
