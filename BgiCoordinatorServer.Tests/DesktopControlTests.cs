using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// 新的写操作必须依赖该连接实际收到的新鲜截图；这些检查决定能否开放桌面输入，
// 原截图测试只验证转图，不能证明授权、跨观看者隔离、全局限频和执行回执。
public sealed class DesktopControlTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.Legacy(id);
    private static readonly DesktopCaptureGeometry Geometry = new() { Width=2560, Height=1440, DisplayId="primary", LayoutId="layout-a" };
    private sealed record Setup(GatewayTestHarness H, Clock Time, string Room, string Session);
    private static async Task<Setup> Create()
    {
        var clock=new Clock(); var h=new GatewayTestHarness(screenshotTimeProvider:clock);
        var room="T"+Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(Ctx("viewer"),room,"pw","viewerUid","观众");
        await h.Ops.JoinControlRoomAsync(Ctx("target"),room,"pw","targetUid","目标");
        await h.Ops.JoinControlRoomAsync(Ctx("other"),room,"pw","otherUid","另一个观众");
        return new(h,clock,room,Guid.NewGuid().ToString("N"));
    }
    private static async Task<string> Frame(Setup s, string viewer="viewer", bool allowed=true, DesktopCaptureGeometry? geometry=null, string? session=null)
    {
        var id=$"screen:{session??s.Session}:{Guid.NewGuid():N}";
        await s.H.Ops.RequestMemberScreenshotAsync(Ctx(viewer),s.Room,"targetUid",id);
        await s.H.Ops.ReportMemberScreenshotExAsync(Ctx("target"),s.Room,"targetUid","YQ==",1280,720,s.Time.GetUtcNow().UtcDateTime,id,geometry??Geometry,allowed);
        return id;
    }
    private static DesktopControlCommand Command(string frame) => new()
        { RequestId=Guid.NewGuid().ToString("N"), TargetUid="targetUid", FrameId=frame, Action="key", Key="Win+D" };
    private static string? Error(object response)
    {
        var node=GatewayEnvelope.ToPayload(response);
        return node?["error"]?["code"]?.GetValue<string>();
    }
    private static int Events(Setup s,string name) => s.H.GatewayClientProxy.Invocations.Count(i => i.Method.Name=="SendCoreAsync"
        && i.Arguments[1] is object?[] args && args.FirstOrDefault() is GatewayEnvelope env && env.Name==name);

    [Fact]
    public async Task ValidInput_IsOnlyForwardedToTarget_AndAckIsNotExecution()
    {
        var s=await Create(); var command=Command(await Frame(s));
        Assert.Null(Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,command)));
        Assert.Equal(1,Events(s,"desktop.control.requested"));
        Assert.Equal(0,Events(s,"desktop.control.result"));
        s.H.GatewayHub.Verify(h=>h.Clients.Client("target"),Times.AtLeastOnce);
        var result=new DesktopControlResult {RequestId=command.RequestId,TargetUid="targetUid",Status="executed",Message="系统已接受输入"};
        Assert.Equal("invalid_request",Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("other"),s.Room,result)));
        Assert.Null(Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("target"),s.Room,result)));
        Assert.Equal(1,Events(s,"desktop.control.result"));
        Assert.Equal("invalid_request",Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("target"),s.Room,result)));
    }
    [Fact]
    public async Task ReadOnlyPermissionAndCrossViewerFrame_AreRejected()
    {
        var s=await Create(); var readOnly=await Frame(s,allowed:false);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,Command(readOnly))));
        s.Time.Advance(1); var frame=await Frame(s);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("other"),s.Room,Command(frame))));
        Assert.Equal("not_in_room",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("outsider"),s.Room,Command(frame))));
        Assert.Equal(0,Events(s,"desktop.control.requested"));
    }
    [Fact]
    public async Task SameFrameAndRepeatedRequest_NeverExecuteTwice()
    {
        var s=await Create(); var cmd=Command(await Frame(s));
        Assert.Null(Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,cmd)));
        Assert.Equal("duplicate_request",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,cmd)));
        s.Time.Advance(2);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,Command(cmd.FrameId))));
        Assert.Equal(1,Events(s,"desktop.control.requested"));
    }
    [Fact]
    public async Task TwoSecondLimit_IsSharedByAllViewers()
    {
        var s=await Create(); var a=Command(await Frame(s));var b=Command(await Frame(s,"other"));
        Assert.Null(Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,a)));
        Assert.Equal("rate_limited",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("other"),s.Room,b)));
        s.Time.Advance(1.999);
        Assert.Equal("rate_limited",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("other"),s.Room,Command(b.FrameId))));
        s.Time.Advance(.001);
        Assert.Null(Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("other"),s.Room,Command(b.FrameId))));
        Assert.Equal(2,Events(s,"desktop.control.requested"));
    }
    [Fact]
    public async Task OldFrameAndClosedWatchingWindow_AreRejected()
    {
        var s=await Create(); var old=await Frame(s);s.Time.Advance(15);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,Command(old))));
        s.Time.Advance(164);var fresh=await Frame(s);s.Time.Advance(1);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,Command(fresh))));
        Assert.Equal(0,Events(s,"desktop.control.requested"));
    }
    [Fact]
    public async Task InputDeadline_CannotExtendPastThreeMinuteWindow()
    {
        var s=await Create();await Frame(s);s.Time.Advance(179);
        var cmd=Command(await Frame(s));
        Assert.Null(Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,cmd)));
        var sent=s.H.GatewayClientProxy.Invocations.Where(i=>i.Arguments[1] is object?[] a && a.FirstOrDefault() is GatewayEnvelope e && e.Name=="desktop.control.requested").Last();
        var env=(GatewayEnvelope)((object?[])sent.Arguments[1])[0]!;
        var request=env.Payload!.Deserialize<DesktopControlRequest>(GatewayJson.Options)!;
        Assert.Equal(s.Time.GetUtcNow().AddSeconds(1),request.ExpiresAtUtc);
        Assert.Equal(1000,request.LifetimeMilliseconds);
        Assert.Equal(0,request.FrameAgeMilliseconds);
        s.Time.Advance(1);
        Assert.Equal("invalid_request",Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("target"),s.Room,
            new(){RequestId=cmd.RequestId,TargetUid="targetUid",Status="executed"})));
    }
    [Fact]
    public async Task CancellationAndDisconnect_ClosePendingInputWithoutAffectingOtherViewer()
    {
        var s=await Create();var cmd=Command(await Frame(s));
        await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,cmd);
        await s.H.Ops.CancelDesktopControlAsync(Ctx("other"),s.Room,s.Session,"targetUid");
        Assert.Equal(0,Events(s,"desktop.control.cancelled"));
        await s.H.Ops.CancelDesktopControlAsync(Ctx("viewer"),s.Room,s.Session,"targetUid");
        Assert.Equal(1,Events(s,"desktop.control.cancelled"));
        Assert.Equal("invalid_request",Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("target"),s.Room,
            new(){RequestId=cmd.RequestId,TargetUid="targetUid",Status="executed"})));
        s.Time.Advance(2);cmd=Command(await Frame(s));await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,cmd);
        await s.H.Ops.HandleDisconnectAsync(Ctx("viewer"),null);
        Assert.Equal(2,Events(s,"desktop.control.cancelled"));
        Assert.Equal("invalid_request",Error(await s.H.Ops.RespondDesktopControlAsync(Ctx("target"),s.Room,
            new(){RequestId=cmd.RequestId,TargetUid="targetUid",Status="executed"})));
    }
    [Fact]
    public async Task PermissionRevocationInLatestFrame_RevokesEarlierControlProof()
    {
        var s=await Create();var first=await Frame(s);s.Time.Advance(1);await Frame(s,allowed:false);
        Assert.Equal("frame_unavailable",Error(await s.H.Ops.RequestDesktopControlAsync(Ctx("viewer"),s.Room,Command(first))));
    }
    [Fact]
    public async Task GatewayRoutesControlCommands_WithBoundedValidation()
    {
        var s=await Create();var frame=await Frame(s);var ctx=GatewayHandlerContext.V3("viewer");
        await s.H.Dispatcher.QueryAsync(ctx,GatewayTestHarness.HelloEnvelope());
        var invalid=await s.H.Dispatcher.DispatchAsync(ctx,new(){Type="command",Name="desktop.control.request",
            Payload=GatewayEnvelope.ToPayload(new{roomCode=s.Room,command=Command(frame) with{Action="shell"}})});
        Assert.Equal("invalid_args",GatewayTestHarness.ErrorCode(invalid));
        var valid=await s.H.Dispatcher.DispatchAsync(ctx,new(){Type="command",Name="desktop.control.request",
            Payload=GatewayEnvelope.ToPayload(new{roomCode=s.Room,command=Command(frame)})});
        Assert.Null(GatewayTestHarness.ErrorCode(valid));
        Assert.Equal(1,Events(s,"desktop.control.requested"));
    }
    [Theory]
    [InlineData("NaN")][InlineData("infinity")][InlineData("negative")][InlineData("unknownKey")]
    public void InputValidation_RejectsNonFiniteCoordinatesAndUnsupportedKeys(string kind)
    {
        var cmd=Command("frame") with{Action="left",Key="",X=.5,Y=.5};
        cmd=kind switch {"NaN"=>cmd with{X=double.NaN},"infinity"=>cmd with{Y=double.PositiveInfinity},
            "negative"=>cmd with{X=-.01},_=>cmd with{Action="key",Key="ArbitraryText"}};
        Assert.False(DesktopControlProtocol.IsValid(cmd));
        Assert.True(DesktopControlProtocol.IsValid(Command("frame")));
    }
}
