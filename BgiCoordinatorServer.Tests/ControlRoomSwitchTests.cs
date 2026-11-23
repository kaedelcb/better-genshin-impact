using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class ControlRoomSwitchTests
{
    private const string Password = "fixture-only-password";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<GatewayHandlerContext> Connect(GatewayTestHarness h)
    {
        var ctx = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(ctx, GatewayTestHarness.HelloEnvelope());
        return ctx;
    }

    private static Task<GatewayEnvelope> Command(GatewayTestHarness h, GatewayHandlerContext ctx, string name, object payload)
        => h.Dispatcher.DispatchAsync(ctx, new()
        {
            Name = name, Type = GatewayProtocol.MessageTypes.Command, RequestId = Guid.NewGuid().ToString("N"),
            Payload = GatewayEnvelope.ToPayload(payload),
        });

    private static T Payload<T>(GatewayEnvelope envelope) where T : class
    {
        Assert.Null(GatewayTestHarness.ErrorCode(envelope));
        return Assert.IsType<T>(envelope.Payload!.Deserialize<T>(GatewayJson.Options));
    }

    private static async Task<ControlRoomCreateResult> Create(GatewayTestHarness h, GatewayHandlerContext ctx,
        string owner, string[] roster) => Payload<ControlRoomCreateResult>(await Command(h, ctx, "control.createRoom",
        new { playerUid = owner, password = Password, allowedUids = roster }));

    private static async Task<ControlRoomPlayersResult> Join(GatewayTestHarness h, GatewayHandlerContext ctx,
        string roomCode, string playerUid, string[]? allowedUids = null, string password = Password)
        => Payload<ControlRoomPlayersResult>(await Command(h, ctx, "control.joinRoom", new
        {
            roomCode, playerUid, playerName = playerUid, password, allowedUids, requireReady = true,
            clientInstanceId = ctx.ConnectionId,
        }));

    private static ControlStatus Status(string room, string uid, int expected = 2) => new()
    {
        RoomCode = room, PlayerUid = uid, BgiStatus = "running", ExpectedHoeingPlayers = expected,
        OnlineHoeingGroupNames = ["该房间的绑定"], OnlineHoeingGroupTypes = ["group"],
    };

    private static async Task<ControlRoomPlayersResult> Ready(GatewayTestHarness h, GatewayHandlerContext ctx, ControlStatus status)
        => Payload<ControlRoomPlayersResult>(await Command(h, ctx, "control.readyRoom", new { status }));

    private static async Task<ControlRoomLeaveResult> Leave(GatewayTestHarness h, GatewayHandlerContext ctx, string roomCode)
        => Payload<ControlRoomLeaveResult>(await Command(h, ctx, "control.leaveRoom", new { roomCode }));

    private static async Task<ControlRoomAccessResult> Access(GatewayTestHarness h, GatewayHandlerContext ctx, string roomCode, string playerUid)
        => Payload<ControlRoomAccessResult>(await Command(h, ctx, "control.roomAccess", new { roomCode, password = Password, playerUid }));

    private static async Task<ControlRoomResult> Online(GatewayTestHarness h, GatewayHandlerContext ctx,
        string roomCode, int generation, bool isOnlineReady = true)
        => Payload<ControlRoomResult>(await Command(h, ctx, "control.reportOnlineEvent", new { roomCode, generation, isOnlineReady }));

    [Fact]
    public async Task RegistrationsKeepIdsCredentialsAndQuotaAcrossServiceRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "control-registry-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new GatewayTestHarness(controlRoomAuth: new ControlRoomAuth(root));
            var oldCode = RoomLogContext.ComputeControlCode(["10001", "10002"]);
            var firstConnection = await Connect(first);
            Assert.True((await Join(first, firstConnection, oldCode, "10001", ["10002", "10001"])).Success);
            var issued = new HashSet<string>(StringComparer.Ordinal);
            var creator = await Connect(first);
            for (var i = 0; i < ControlRoomAuth.DefaultMaxRoomsPerUid; i++)
            {
                var created = await Create(first, creator, "10001", ["10001", "10002"]);
                Assert.True(created.Success);
                Assert.Matches("^[A-Z0-9]{6}$", created.RoomCode);
                Assert.Contains(created.RoomCode, c => !char.IsAsciiHexDigit(c));
                Assert.True(issued.Add(created.RoomCode));
            }
            Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(root, "registry.json")), StringComparison.Ordinal);

            var restarted = new GatewayTestHarness(controlRoomAuth: new ControlRoomAuth(root));
            var fresh = await Connect(restarted);
            Assert.False((await Create(restarted, fresh, "10001", ["10001", "10002"])).Success);
            Assert.True((await Create(restarted, fresh, "10002", ["10002"])).Success);
            foreach (var code in issued.Append(oldCode))
                Assert.True((await Access(restarted, fresh, code, "10002")).Success);
            Assert.False((await Join(restarted, fresh, oldCode, "10002", password: "incorrect")).Success);
            Assert.False((await Join(restarted, fresh, issued.First(), "10003")).Success);
            Assert.False((await Join(restarted, fresh, "NOEXST", "10002")).Success);
            Assert.True((await Join(restarted, fresh, oldCode, "10002")).Success);
            Assert.True((await Ready(restarted, fresh, Status(oldCode, "10002"))).Success);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CandidateIsReadOnlyAndOnlyFlushesValidCommandsForItsOwnRoomAfterReady()
    {
        var clock = new Clock();
        var h = new GatewayTestHarness(controlTimeProvider: clock);
        var senderA = await Connect(h); var senderB = await Connect(h); var candidate = await Connect(h);
        var a = (await Create(h, senderA, "sender-a", ["sender-a", "same-uid"])).RoomCode;
        var b = (await Create(h, senderB, "sender-b", ["sender-b", "same-uid"])).RoomCode;
        Assert.True((await Join(h, senderA, a, "sender-a")).Success);
        Assert.True((await Ready(h, senderA, Status(a, "sender-a"))).Success);
        Assert.True((await Join(h, senderB, b, "sender-b")).Success);
        Assert.True((await Ready(h, senderB, Status(b, "sender-b"))).Success);

        async Task Send(GatewayHandlerContext sender, string roomCode, string targetUid, string id, int seconds) =>
            await h.Ops.SendRemoteCommandAsync(sender, new RemoteCommand
            {
                Cmd = "pause", RoomCode = roomCode, Target = [targetUid], CommandId = id,
                ExpiresAtUtc = clock.Now.AddSeconds(seconds),
            });
        await Send(senderA, a, "same-uid", "only-A", 60);
        await Send(senderB, b, "same-uid", "expired-B", 1);
        await Send(senderB, b, "same-uid", "valid-B", 60);
        clock.Now += TimeSpan.FromSeconds(2);
        var prepared = await Join(h, candidate, b, "same-uid");
        Assert.True(prepared.Success);
        Assert.True(prepared.PlayersUpdate!.Full);
        Assert.Equal("sender-b", Assert.Single(prepared.PlayersUpdate.Players!).PlayerUid);
        Assert.False((await Join(h, candidate, a, "same-uid")).Success);
        Assert.False((await Leave(h, candidate, a)).Success);
        Assert.False(Payload<ControlRoomPlayersResult>(await Command(h, candidate, "control.roomSnapshot", new { roomCode = a })).Success);
        Assert.True(Payload<ControlRoomPlayersResult>(await Command(h, candidate, "control.roomSnapshot", new { roomCode = b })).Success);
        Assert.False((await Online(h, candidate, b, 1)).Success);
        Assert.False((await Ready(h, candidate, Status(b, "someone-else"))).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.BeginManualCoordinatedBatchAsync(candidate, "attempt", ["sender-b"], null));
        await Send(candidate, b, "sender-b", "not-ready", 60);
        h.GatewayGroups.Verify(g => g.AddToGroupAsync(candidate.ConnectionId, "CTRL_" + b,
            It.IsAny<CancellationToken>()), Times.Never);

        var activated = await Ready(h, candidate, Status(b, "same-uid"));
        Assert.True(activated.Success);
        var member = Assert.Single(activated.PlayersUpdate!.Players!.Where(player => player.PlayerUid == "same-uid"));
        Assert.Equal(new[] { "该房间的绑定" }, member.OnlineHoeingGroupNames);
        Assert.True((await Ready(h, candidate, Status(b, "same-uid"))).Success);
        var delivered = Assert.Single(h.GatewayClientProxy.Invocations
            .Select(call => (call.Arguments[1] as object?[])?.FirstOrDefault()).OfType<GatewayEnvelope>()
            .Where(env => env.Name == "control.remoteCommand"));
        Assert.Equal(b, delivered.RoomCode);
        Assert.Equal("valid-B", delivered.Payload!["command"]!["commandId"]!.GetValue<string>());
        Assert.Equal("only-A", Assert.Single(h.RoomManager.GetAndClearPendingCommands("CTRL_" + a, "same-uid")).CommandId);
    }

    [Fact]
    public async Task HeartbeatsAndDuplicateGenerationCannotExtendIntentOrStartExpiredMemberBeforeScan()
    {
        var clock = new Clock();
        var h = new GatewayTestHarness(controlTimeProvider: clock);
        var member = await Connect(h); var peer = await Connect(h);
        var room = (await Create(h, member, "member", ["member", "peer"])).RoomCode;
        Assert.True((await Join(h, member, room, "member")).Success);
        Assert.True((await Ready(h, member, Status(room, "member"))).Success);
        Assert.True((await Join(h, peer, room, "peer")).Success);
        Assert.True((await Ready(h, peer, Status(room, "peer"))).Success);
        Assert.True((await Online(h, member, room, 7)).Success);
        Assert.False((await Online(h, member, "OTHER1", 7)).Success);
        Assert.False((await Online(h, member, room, 6)).Success);
        Assert.True((await Leave(h, member, room)).Busy);
        var expires = h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "member").OnlineReadyExpireTime;
        clock.Now += TimeSpan.FromMinutes(20);
        var status = Status(room, "member"); status.OnlineReady = true;
        await h.Ops.ReportControlStatusAsync(member, status);
        Assert.True((await Online(h, member, room, 7)).Success);
        Assert.Equal(expires, h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "member").OnlineReadyExpireTime);

        clock.Now = new DateTimeOffset(expires, TimeSpan.Zero);
        Assert.False((await Access(h, peer, room, "member")).Busy);
        Assert.True((await Online(h, peer, room, 8)).Success);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(peer));
        Assert.True((await Online(h, member, room, 7)).Success); // receipt only, never rearms
        Assert.True((await Leave(h, member, room)).Success);
        Assert.True((await Leave(h, member, room)).Success);
        Assert.False((await Access(h, peer, room, "member")).Joined);
        await h.Ops.ReportControlStatusAsync(member, status);
        Assert.False((await Online(h, member, room, 7)).Success);
        Assert.False((await Join(h, member, room, "member")).Success);
        Assert.True((await Online(h, peer, room, 8, isOnlineReady: false)).Success);
        Assert.True((await Leave(h, peer, room)).Success);
    }

    [Fact]
    public async Task LeaveAndManualAdmissionCannotBothSucceedForTheSameMember()
    {
        var h = new GatewayTestHarness();
        var target = await Connect(h); var starter = await Connect(h);
        var room = (await Create(h, starter, "starter", ["starter", "target"])).RoomCode;
        Assert.True((await Join(h, starter, room, "starter")).Success);
        Assert.True((await Ready(h, starter, Status(room, "starter"))).Success);
        Assert.True((await Join(h, target, room, "target")).Success);
        Assert.True((await Ready(h, target, Status(room, "target"))).Success);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaveTask = Task.Run(async () => { await go.Task; return await Leave(h, target, room); });
        var beginTask = Task.Run(async () =>
        {
            await go.Task;
            return await Command(h, starter, "control.batch.begin", new { requestId = "race", targetUids = new[] { "target" } });
        });
        go.SetResult();
        var left = await leaveTask;
        var begin = await beginTask;
        if (left.Success)
        {
            Assert.NotNull(GatewayTestHarness.ErrorCode(begin));
            Assert.Null(h.Ops.CurrentCoordinatedBatch(starter));
        }
        else
        {
            Assert.True(left.Busy);
            Assert.Null(GatewayTestHarness.ErrorCode(begin));
            Assert.Equal(new[] { "target" }, h.Ops.CurrentCoordinatedBatch(starter)!.ParticipantUids);
        }
    }

    [Fact]
    public async Task AnotherRoomCannotOpenBatchForUidUntilOldBatchCleans_AndStopStaysRoomScoped()
    {
        var h = new GatewayTestHarness();
        var old = await Connect(h); var next = await Connect(h);
        var a = (await Create(h, old, "uid", ["uid"])).RoomCode;
        var b = (await Create(h, next, "uid", ["uid"])).RoomCode;
        Assert.True((await Join(h, old, a, "uid")).Success);
        Assert.True((await Ready(h, old, Status(a, "uid"))).Success);
        Assert.True((await Join(h, next, b, "uid")).Success);
        Assert.True((await Ready(h, next, Status(b, "uid"))).Success);
        var original = await h.Ops.BeginManualCoordinatedBatchAsync(old, "old", ["uid"], null);
        await h.Ops.UpdateCoordinatedBatchAsync(old, original.Generation, "token", original.BatchId,
            "group", original.Index, original.Attempt, "", "registered");
        Assert.True((await Access(h, next, a, "uid")).Busy);
        Assert.True((await Online(h, next, b, 3)).Success);
        Assert.True((await Leave(h, next, b)).Busy);

        async Task Stop(GatewayHandlerContext ctx, string roomCode) => await h.Ops.SendRemoteCommandAsync(ctx, new()
        {
            Cmd = "stop", RoomCode = roomCode, Target = ["*"], Params = new() { ["stopScope"] = "all" },
        });
        await Stop(next, b);
        Assert.False((await Access(h, next, b, "uid")).Busy);
        Assert.True((await Online(h, next, b, 3)).Success);
        Assert.False((await Access(h, next, b, "uid")).Busy);
        Assert.Equal("preparing", h.Ops.CurrentCoordinatedBatch(old)!.Phase);

        await Stop(old, a);
        Assert.Equal("aborted", h.Ops.CurrentCoordinatedBatch(old)!.Phase);
        Assert.True((await Leave(h, old, a)).Busy);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(next, "new", ["uid"], null));
        Assert.Equal("participant_batch_active_in_another_room", blocked.Message);
        await h.Ops.UpdateCoordinatedBatchAsync(old, original.Generation, "token", original.BatchId,
            "group", original.Index, original.Attempt, "", "cleaned");
        Assert.True((await Leave(h, old, a)).Success);
        Assert.NotEqual(original.BatchId, (await h.Ops.BeginManualCoordinatedBatchAsync(next, "new", ["uid"], null)).BatchId);
        await h.Ops.HandleDisconnectAsync(old, null);
        Assert.Equal("uid", h.RoomManager.GetUidByConnectionId("CTRL_" + b, next.ConnectionId));
    }
}
