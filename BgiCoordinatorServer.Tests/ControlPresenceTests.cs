using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// 覆盖网关命令到房间状态及单播事件：转发 ACK 不能替代被控端显示确认。
public sealed class ControlPresenceTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed record Fixture(GatewayTestHarness H, Clock Time, string Room, string Session, string OtherSession);
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.V3(id);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static readonly DesktopCaptureGeometry Geometry = new() { Width=2560, Height=1440, DisplayId="primary", LayoutId="layout-a" };

    private static async Task<Fixture> Create()
    {
        var clock = new Clock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var room = "T" + Id()[..8];
        foreach (var (connection, uid) in new[] { ("viewer", "viewerUid"), ("other", "otherUid"), ("target", "targetUid") })
        {
            await h.Dispatcher.DispatchAsync(Ctx(connection), GatewayTestHarness.HelloEnvelope());
            await h.Ops.JoinControlRoomAsync(Ctx(connection), room, "pw", uid, uid);
        }
        await h.Dispatcher.DispatchAsync(Ctx("outsider"), GatewayTestHarness.HelloEnvelope());
        return new(h, clock, room, Id(), Id());
    }

    private static async Task<string> Frame(Fixture s, string viewer = "viewer", string? session = null)
    {
        var frame = $"screen:{session ?? (viewer == "other" ? s.OtherSession : s.Session)}:{Id()}";
        await s.H.Ops.RequestMemberScreenshotAsync(Ctx(viewer), s.Room, "targetUid", frame);
        await s.H.Ops.ReportMemberScreenshotExAsync(Ctx("target"), s.Room, "targetUid", "YQ==", 1280, 720,
            s.Time.GetUtcNow().UtcDateTime, frame, Geometry, true);
        return frame;
    }

    private static DesktopControlPresenceRequest Presence(string frame, string controlId, string action = "start") => new()
    {
        RequestId = Id(), ControlId = controlId, TargetUid = "targetUid", FrameId = frame, Action = action,
        RequesterConnectionId = "forged", RoomCode = "forged", LifetimeMilliseconds = 999999
    };
    private static DesktopControlCommand Input(string frame, string controlId) => new()
    {
        RequestId = Id(), ControlId = controlId, TargetUid = "targetUid", FrameId = frame, Action = "key", Key = "W"
    };
    private static Task<GatewayEnvelope> Send(Fixture s, string connection, string name, object payload) =>
        s.H.Dispatcher.DispatchAsync(Ctx(connection), new GatewayEnvelope { Type="command", Name=name, Payload=GatewayEnvelope.ToPayload(payload) });
    private static Task<GatewayEnvelope> SendPresence(Fixture s, string connection, DesktopControlPresenceRequest presence, string? room = null) =>
        Send(s, connection, "desktop.control.presence", new { roomCode=room ?? s.Room, presence });
    private static Task<GatewayEnvelope> SendInput(Fixture s, string connection, DesktopControlCommand command, string? room = null) =>
        Send(s, connection, "desktop.control.request", new { roomCode=room ?? s.Room, command });
    private static Task<GatewayEnvelope> Result(Fixture s, string connection, DesktopControlPresenceRequest presence, string status = "active", string? room = null) =>
        Send(s, connection, "desktop.control.presenceResult", new { roomCode=room ?? s.Room, result = new DesktopControlPresenceResult
        { RequestId=presence.RequestId, ControlId=presence.ControlId, TargetUid=presence.TargetUid, Status=status } });
    private static string? Error(GatewayEnvelope response) => GatewayTestHarness.ErrorCode(response);
    private static GatewayEnvelope[] Events(Fixture s, string name) => s.H.GatewayClientProxy.Invocations
        .Where(i => i.Method.Name == "SendCoreAsync" && i.Arguments[1] is object?[] a && a.FirstOrDefault() is GatewayEnvelope e && e.Name == name)
        .Select(i => (GatewayEnvelope)((object?[])i.Arguments[1])[0]!).ToArray();
    private static DesktopControlPresenceRequest SentPresence(GatewayEnvelope envelope) =>
        envelope.Payload!.Deserialize<DesktopControlPresenceRequest>(GatewayJson.Options)!;
    private static async Task Activate(Fixture s, string viewer, DesktopControlPresenceRequest presence)
    {
        Assert.Null(Error(await SendPresence(s, viewer, presence)));
        Assert.Null(Error(await Result(s, "target", presence)));
    }

    [Fact]
    public async Task ForwardingAck_DoesNotAuthorizeInput_BeforeTargetActiveResult()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        s.H.GatewayHub.Invocations.Clear();
        Assert.Null(Error(await SendPresence(s, "viewer", start)));
        var sent = Assert.Single(Events(s, "desktop.control.presence.requested"));
        var payload = SentPresence(sent);
        Assert.Equal("viewer", payload.RequesterConnectionId); Assert.Equal(s.Room, payload.RoomCode);
        Assert.Equal(12000, payload.LifetimeMilliseconds);
        s.H.GatewayHub.Verify(h => h.Clients.Client("target"), Times.AtLeastOnce);
        Assert.Empty(Events(s, "desktop.control.presence.result"));
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Empty(Events(s, "desktop.control.requested"));
        Assert.Null(Error(await Result(s, "target", start)));
        Assert.Single(Events(s, "desktop.control.presence.result"));
        Assert.Null(Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Single(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task PresenceResult_RequiresOriginalRoomAndTargetConnection()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        Assert.Null(Error(await SendPresence(s, "viewer", start)));
        Assert.Equal("invalid_request", Error(await Result(s, "other", start)));
        Assert.Equal("invalid_request", Error(await Result(s, "target", start, room: s.Room + "x")));
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Null(Error(await Result(s, "target", start)));
        Assert.Equal("invalid_request", Error(await Result(s, "target", start)));
        Assert.Null(Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
    }

    [Fact]
    public async Task Presence_IsBoundToViewerRoomFrameAndControlId()
    {
        var s = await Create(); var frame = await Frame(s); var controlId = Id();
        var start = Presence(frame, controlId); await Activate(s, "viewer", start);
        var otherFrame = await Frame(s, "other");
        Assert.Equal("not_in_room", Error(await SendPresence(s, "outsider", Presence(frame, Id()))));
        Assert.Equal("not_in_room", Error(await SendPresence(s, "viewer", Presence(frame, Id()), s.Room + "x")));
        Assert.Equal("frame_unavailable", Error(await SendPresence(s, "other", Presence(frame, Id()))));
        Assert.Equal("frame_unavailable", Error(await SendPresence(s, "viewer", Presence(frame, Id()) with { TargetUid="otherUid" })));
        Assert.Equal("presence_required", Error(await SendInput(s, "other", Input(otherFrame, controlId))));
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, Id()))));
        Assert.Null(Error(await SendInput(s, "viewer", Input(frame, controlId))));
    }

    [Fact]
    public async Task ConfirmedControlId_CannotBeReusedForAnotherWatchingSession()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start);
        s.Time.Advance(1);
        var nextSessionFrame = await Frame(s, session: Id());
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(nextSessionFrame, start.ControlId))));
        var renew = Presence(nextSessionFrame, start.ControlId, "renew");
        Assert.Equal("presence_expired", Error(await SendPresence(s, "viewer", renew)));
        Assert.Empty(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task InputRequiresControlIdInGuidNFormat()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start);
        Assert.Equal("invalid_args", Error(await SendInput(s, "viewer", Input(frame, ""))));
        Assert.Equal("invalid_args", Error(await SendInput(s, "viewer", Input(frame, Guid.ParseExact(start.ControlId, "N").ToString("D")))));
        Assert.Null(Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
    }

    [Fact]
    public async Task DeniedPresence_DoesNotAuthorizeInputOrAllowLateActiveResult()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        Assert.Null(Error(await SendPresence(s, "viewer", start)));
        Assert.Null(Error(await Result(s, "target", start, "denied")));
        Assert.Equal("invalid_request", Error(await Result(s, "target", start)));
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Empty(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task TwoViewers_EndingOneLease_DoesNotStopTheOther()
    {
        var s = await Create(); var firstFrame = await Frame(s); var secondFrame = await Frame(s, "other");
        var first = Presence(firstFrame, Id()); var second = Presence(secondFrame, Id());
        await Activate(s, "viewer", first); await Activate(s, "other", second);
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(firstFrame, first.ControlId, "end"))));
        var end = SentPresence(Events(s, "desktop.control.presence.requested").Last());
        Assert.Equal("end", end.Action); Assert.Equal("viewer", end.RequesterConnectionId); Assert.Equal(first.ControlId, end.ControlId);
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(firstFrame, first.ControlId))));
        Assert.Null(Error(await SendInput(s, "other", Input(secondFrame, second.ControlId))));
        Assert.Single(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task EndBeforeStartAndLateResult_CannotReviveControl()
    {
        var s = await Create(); var frame = await Frame(s); var early = Presence(frame, Id());
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(frame, early.ControlId, "end"))));
        Assert.Equal("presence_closed", Error(await SendPresence(s, "viewer", early)));
        var pending = Presence(frame, Id());
        Assert.Null(Error(await SendPresence(s, "viewer", pending)));
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(frame, pending.ControlId, "end"))));
        Assert.Equal("invalid_request", Error(await Result(s, "target", pending)));
        Assert.Equal("presence_closed", Error(await SendPresence(s, "viewer", pending with { RequestId=Id() })));
        Assert.Empty(Events(s, "desktop.control.presence.result"));
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, pending.ControlId))));
    }

    [Fact]
    public async Task RenewAfterTwelveSeconds_FailsAndCannotAuthorizeInput()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start); s.Time.Advance(12);
        var error = Error(await SendPresence(s, "viewer", Presence(frame, start.ControlId, "renew")));
        Assert.True(error is "presence_expired" or "presence_closed", $"Unexpected error: {error}");
        Assert.Equal("presence_required", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Empty(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task Renewals_DoNotExtendOriginalThreeMinuteWatchingWindow()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start);
        for (var elapsed = 10; elapsed <= 170; elapsed += 10)
        {
            s.Time.Advance(10);
            var renew = Presence(frame, start.ControlId, "renew");
            Assert.Null(Error(await SendPresence(s, "viewer", renew)));
            Assert.Null(Error(await Result(s, "target", renew)));
        }
        s.Time.Advance(9); frame = await Frame(s);
        var lastRenew = Presence(frame, start.ControlId, "renew");
        Assert.Null(Error(await SendPresence(s, "viewer", lastRenew)));
        Assert.Equal(1000, SentPresence(Events(s, "desktop.control.presence.requested").Last()).LifetimeMilliseconds);
        Assert.Null(Error(await Result(s, "target", lastRenew)));
        s.Time.Advance(1);
        Assert.Equal("frame_unavailable", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        var error = Error(await SendPresence(s, "viewer", Presence(frame, start.ControlId, "renew")));
        Assert.True(error is "presence_expired" or "presence_closed", $"Unexpected error: {error}");
        Assert.Empty(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task ViewerDisconnect_SendsTargetEnd_AndLeavesOtherViewerActive()
    {
        var s = await Create(); var frame = await Frame(s); var otherFrame = await Frame(s, "other");
        var start = Presence(frame, Id()); var other = Presence(otherFrame, Id());
        await Activate(s, "viewer", start); await Activate(s, "other", other);
        s.H.GatewayHub.Invocations.Clear();
        await s.H.Ops.HandleDisconnectAsync(Ctx("viewer"), null);
        var end = SentPresence(Events(s, "desktop.control.presence.requested").Last());
        Assert.Equal("end", end.Action); Assert.Equal("viewer", end.RequesterConnectionId); Assert.Equal(start.ControlId, end.ControlId);
        s.H.GatewayHub.Verify(h => h.Clients.Client("target"), Times.AtLeastOnce);
        Assert.Equal("not_in_room", Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Null(Error(await SendInput(s, "other", Input(otherFrame, other.ControlId))));
    }

    [Fact]
    public async Task TargetReconnect_CannotConfirmOldRequestOrReuseOldLease()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        Assert.Null(Error(await SendPresence(s, "viewer", start)));
        await s.H.Ops.HandleDisconnectAsync(Ctx("target"), null);
        await s.H.Dispatcher.DispatchAsync(Ctx("replacement"), GatewayTestHarness.HelloEnvelope());
        await s.H.Ops.JoinControlRoomAsync(Ctx("replacement"), s.Room, "pw", "targetUid", "目标");
        Assert.Equal("invalid_request", Error(await Result(s, "replacement", start)));
        Assert.Equal("invalid_request", Error(await Result(s, "target", start)));
        Assert.NotNull(Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Empty(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task OldEndAndCancel_DoNotRemoveNewFrameOrCancelNewControl()
    {
        var s = await Create(); var oldFrame = await Frame(s); var old = Presence(oldFrame, Id());
        await Activate(s, "viewer", old);
        s.Time.Advance(1);
        var newFrame = await Frame(s);
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(oldFrame, old.ControlId, "end"))));
        Assert.Null(Error(await Send(s, "viewer", "desktop.control.cancel", new
        { roomCode=s.Room, sessionId=s.Session, targetUid="targetUid", controlId=old.ControlId })));
        var next = Presence(newFrame, Id());
        await Activate(s, "viewer", next);
        var input = Input(newFrame, next.ControlId);
        Assert.Null(Error(await SendInput(s, "viewer", input)));
        Assert.Null(Error(await Send(s, "viewer", "desktop.control.cancel", new
        { roomCode=s.Room, sessionId=s.Session, targetUid="targetUid", controlId=old.ControlId })));
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(oldFrame, old.ControlId, "end"))));
        Assert.Empty(Events(s, "desktop.control.cancelled"));
        Assert.Null(Error(await Send(s, "target", "desktop.control.respond", new
        { roomCode=s.Room, result=new DesktopControlResult { RequestId=input.RequestId, TargetUid=input.TargetUid, Status="executed" } })));
    }

    [Fact]
    public async Task Cancel_RequiresControlIdAndTarget()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start); var input = Input(frame, start.ControlId);
        Assert.Null(Error(await SendInput(s, "viewer", input)));
        foreach (var (target, id) in new[] { ("targetUid", ""), ("targetUid", "bad"), ("", start.ControlId) })
            Assert.Equal("invalid_request", Error(await Send(s, "viewer", "desktop.control.cancel", new
            { roomCode=s.Room, sessionId=s.Session, targetUid=target, controlId=id })));
        Assert.Empty(Events(s, "desktop.control.cancelled"));
        Assert.Null(Error(await Send(s, "viewer", "desktop.control.cancel", new
        { roomCode=s.Room, sessionId=s.Session, targetUid="targetUid", controlId=start.ControlId })));
        Assert.Single(Events(s, "desktop.control.cancelled"));
    }

    [Fact]
    public async Task CancelBeforeInput_RejectsThatRequestWithoutRemovingFreshFrame()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start); var cancelled = Input(frame, start.ControlId);
        Assert.Null(Error(await Send(s, "viewer", "desktop.control.cancel", new
        { roomCode=s.Room, sessionId=s.Session, targetUid="targetUid", controlId=start.ControlId, requestId=cancelled.RequestId })));
        Assert.Equal("duplicate_request", Error(await SendInput(s, "viewer", cancelled)));
        Assert.Empty(Events(s, "desktop.control.requested"));
        Assert.Null(Error(await SendInput(s, "viewer", Input(frame, start.ControlId))));
        Assert.Single(Events(s, "desktop.control.requested"));
    }

    [Fact]
    public async Task End_CancelsPendingInputAndRejectsLateExecutionResult()
    {
        var s = await Create(); var frame = await Frame(s); var start = Presence(frame, Id());
        await Activate(s, "viewer", start); var input = Input(frame, start.ControlId);
        Assert.Null(Error(await SendInput(s, "viewer", input)));
        Assert.Null(Error(await SendPresence(s, "viewer", Presence(frame, start.ControlId, "end"))));
        Assert.Single(Events(s, "desktop.control.cancelled"));
        var response = await Send(s, "target", "desktop.control.respond", new { roomCode=s.Room, result = new DesktopControlResult
        { RequestId=input.RequestId, TargetUid="targetUid", Status="executed" } });
        Assert.Equal("invalid_request", Error(response));
        Assert.Empty(Events(s, "desktop.control.result"));
    }
}
