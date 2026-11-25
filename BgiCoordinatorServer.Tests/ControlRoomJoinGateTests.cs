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

    [Theory]
    [InlineData("log_viewer_legacy")]
    [InlineData("web_logviewer_aligned")]
    public async Task LogViewerCannotCreateOrClaimMissingRoom_AndCannotReadAccess(string viewerUid)
    {
        var h = new GatewayTestHarness(); var room = Room();
        Assert.False(h.Ops.CreateControlRoom(Ctx("viewer"), "attacker-pw", viewerUid, [viewerUid]).Success);

        var join = await h.Ops.JoinControlRoomAsync(Ctx("viewer"), room, "attacker-pw", viewerUid, "日志协助", isRemote: true);
        Assert.False(join.Success);
        Assert.Equal("控制房间不存在", join.Reason);
        Assert.False(Joined(h, room, "viewer"));
        Assert.False(h.Ops.GetControlRoomAccess(room, "attacker-pw", viewerUid).Success);

        // 未存在的房间没有被旁观连接抢占密码，正式成员仍可用自己的密码建房。
        await h.Ops.JoinControlRoomAsync(Ctx("pc"), room, "pw", "uid1", "成员一");
        Assert.Single(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));
    }

    [Fact]
    public async Task LogViewerCanJoinWhitelistedRoomAsRemote_WithoutBypassingExecutorGate()
    {
        var h = new GatewayTestHarness();
        var created = h.Ops.CreateControlRoom(Ctx("creator"), "pw", "10001", ["10001", "10002"]);
        Assert.True(created.Success, created.Reason);
        var room = created.RoomCode;

        // 不在白名单的普通成员即使密码正确也被拒绝（包括 isRemote=true 监控模式）。
        Assert.False((await h.Ops.JoinControlRoomAsync(Ctx("outsider"), room, "pw", "99999", "路人")).Success);
        Assert.False((await h.Ops.JoinControlRoomAsync(Ctx("outsider-remote"), room, "pw", "99999", "路人", isRemote: true)).Success);

        // log_viewer_ 必须携带 isRemote=true，不能以执行端身份绕过白名单。
        var nonRemoteLegacy = await h.Ops.JoinControlRoomAsync(Ctx("legacy-nonremote"), room, "pw", "log_viewer_bad", "日志协助", isRemote: false);
        Assert.False(nonRemoteLegacy.Success);
        Assert.False(Joined(h, room, "legacy-nonremote"));
        Assert.Empty(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));

        // web_logviewer_ 若未带 isRemote=true，仍按网页控制端要求匹配成员昵称。
        Assert.False((await h.Ops.JoinControlRoomAsync(Ctx("web-nonremote"), room, "pw", "web_logviewer_bad", "日志协助", isRemote: false)).Success);

        // 旧版 PC（log_viewer_）与对齐后的 PC/网页（web_logviewer_）带 isRemote=true：错密拒绝，对密放行且仅登记为旁观连接。
        Assert.False((await h.Ops.JoinControlRoomAsync(Ctx("legacy-wrong"), room, "wrong", "log_viewer_1", "日志协助", isRemote: true)).Success);
        Assert.False((await h.Ops.JoinControlRoomAsync(Ctx("web-wrong"), room, "wrong", "web_logviewer_1", "日志协助", isRemote: true)).Success);

        var legacyOk = await h.Ops.JoinControlRoomAsync(Ctx("legacy-ok"), room, "pw", "log_viewer_2", "日志协助", isRemote: true);
        Assert.True(legacyOk.Success, legacyOk.Reason);
        Assert.True(Joined(h, room, "legacy-ok"));

        var webOk = await h.Ops.JoinControlRoomAsync(Ctx("web-ok"), room, "pw", "web_logviewer_2", "日志协助", isRemote: true);
        Assert.True(webOk.Success, webOk.Reason);
        Assert.True(Joined(h, room, "web-ok"));

        Assert.Empty(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));
        Assert.False(h.Ops.GetControlRoomAccess(room, "pw", "log_viewer_2").Success);
        Assert.False(h.Ops.GetControlRoomAccess(room, "pw", "web_logviewer_2").Success);
    }
}
