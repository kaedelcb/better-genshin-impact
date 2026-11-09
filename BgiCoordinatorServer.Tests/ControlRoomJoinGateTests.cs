using BgiCoordinatorServer.Gateway;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>网页控制端只能加入助手已建好的房间，且昵称必须是房间内某个成员的昵称。</summary>
public class ControlRoomJoinGateTests
{
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.V3(id);
    private static string Room() => "J" + Guid.NewGuid().ToString("N")[..8];

    private static bool Joined(GatewayTestHarness h, string room, string connection)
        => h.RoomManager.IsRemoteConnection($"CTRL_{room}", connection);

    [Fact]
    public async Task WebCannotCreateRoom_AndDoesNotClaimItsPassword()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("web"), room, "attacker-pw", "web_a", "任意");
        Assert.False(Joined(h, room, "web"));

        // 网页端没有抢占密码：助手随后仍能用自己的密码建房。
        await h.Ops.JoinControlRoomAsync(Ctx("pc"), room, "pw", "uid1", "成员一");
        Assert.Single(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));
    }

    [Fact]
    public async Task WebNicknameMustMatchRoomMember()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("pc"), room, "pw", "uid1", "成员一");

        await h.Ops.JoinControlRoomAsync(Ctx("web1"), room, "pw", "web_1", "随便填");
        Assert.False(Joined(h, room, "web1"));

        await h.Ops.JoinControlRoomAsync(Ctx("web2"), room, "wrong", "web_2", "成员一");
        Assert.False(Joined(h, room, "web2"));

        await h.Ops.JoinControlRoomAsync(Ctx("web3"), room, "pw", "web_3", " 成员一 ");
        Assert.True(Joined(h, room, "web3"));
    }
}
