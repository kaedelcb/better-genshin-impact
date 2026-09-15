using System.Text.Json;
using BetterGenshinImpact.Shared.CooperativeRerun;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RerunGatewayTests
{
    private static GatewayEnvelope Envelope(string name, RerunRequest request) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = name, RequestId = "request-1", Payload = GatewayEnvelope.ToPayload(request)
    };

    private static void Hello(GatewayTestHarness h, string connection, bool capable = true)
        => h.Tracker.CompleteHello(connection, new ClientHello
        {
            ProtocolVersion = 3, Capabilities = capable ? [RerunProtocol.Capability] : []
        });

    private static (GatewayTestHarness Harness, Room Room) Create(bool memberCapable = true)
    {
        var h = new GatewayTestHarness();
        Hello(h, "host"); Hello(h, "member", memberCapable);
        var code = h.RoomManager.CreateRoom("host", playerUid: "uid-host", expectedPlayerCount: 2);
        Assert.True(h.RoomManager.JoinRoom(code, "member", "member", playerUid: "uid-member").Success);
        return (h, h.RoomManager.GetRoom(code)!);
    }

    private static RerunRequest Enroll(string token = "host-token") => new()
    {
        Operation = RerunProtocol.Enroll, OperationId = Guid.NewGuid().ToString(),
        Scope = "0", ResumeToken = token, PlanHash = "plan",
        Routes = [new() { RouteId = "route-0", OriginalIndex = 0, Eligible = true }]
    };

    private static Task<GatewayEnvelope> Update(GatewayTestHarness h, RerunRequest request, string connection = "host")
        => h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(connection), Envelope(RerunProtocol.Update, request));

    [Fact]
    public async Task Hello_AdvertisesRerunCapability()
    {
        var h = new GatewayTestHarness();
        var response = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3("host"), GatewayTestHarness.HelloEnvelope());
        Assert.Contains(RerunProtocol.Capability, response.Payload!["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task Enrollment_RequiresEveryMemberCapability()
    {
        var (h, room) = Create(memberCapable: false);
        var response = await Update(h, Enroll());
        Assert.Equal("rerun_capability_required", GatewayTestHarness.ErrorCode(response));
        Assert.Null(room.RerunExecution);
    }

    [Fact]
    public async Task Enrollment_AndPoll_ReturnDirectSnapshot()
    {
        var (h, room) = Create();
        var response = await Update(h, Enroll());
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        var snapshot = response.Payload!.Deserialize<RerunSnapshot>(GatewayJson.Options)!;
        Assert.Equal("0", snapshot.Scope);
        Assert.Contains("uid-host", snapshot.Participants);
        Assert.Contains("uid-member", snapshot.Participants);
        Assert.False(string.IsNullOrEmpty(snapshot.SessionId));
        var poll = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3("host"), Envelope(RerunProtocol.State,
            new RerunRequest { Scope = "0", SessionId = snapshot.SessionId, ResumeToken = "host-token" }));
        Assert.Null(GatewayTestHarness.ErrorCode(poll));
        Assert.Equal(snapshot.SessionId, poll.Payload!["sessionId"]!.GetValue<string>());
        Assert.Equal("request-1", poll.RequestId);
    }

    [Fact]
    public async Task StateQuery_RejectsMutation_WithoutCreatingState()
    {
        var (h, room) = Create();
        var response = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3("host"), Envelope(RerunProtocol.State, Enroll()));
        Assert.Equal("bad_request", GatewayTestHarness.ErrorCode(response));
        Assert.Null(room.RerunExecution);
    }

    [Fact]
    public async Task Routes_EnforceQueryAndCommandChannels()
    {
        var (h, _) = Create();
        var query = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3("host"), Envelope(RerunProtocol.Update, Enroll()));
        var command = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3("host"), Envelope(RerunProtocol.State, new()));
        Assert.Equal("wrong_channel", GatewayTestHarness.ErrorCode(query));
        Assert.Equal("wrong_channel", GatewayTestHarness.ErrorCode(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("uid-host")]
    public async Task Enrollment_RejectsMissingOrDuplicateUid(string memberUid)
    {
        var (h, room) = Create();
        room.Players.Single(p => p.ConnectionId == "member").PlayerUid = memberUid;
        Assert.Equal("rerun_invalid_roster", GatewayTestHarness.ErrorCode(await Update(h, Enroll())));
        Assert.Null(room.RerunExecution);
    }

    [Fact]
    public async Task Enrollment_RejectsStaleConnectionIdentity()
    {
        var (h, room) = Create();
        room.Players.Single(p => p.ConnectionId == "host").ConnectionId = "replacement";
        Assert.Equal("rerun_not_participant", GatewayTestHarness.ErrorCode(await Update(h, Enroll())));
        Assert.Null(room.RerunExecution);
    }

    [Fact]
    public async Task ExpCap_AbortsExistingState_WithoutReplacingSession()
    {
        var (h, room) = Create();
        var enrolled = await Update(h, Enroll());
        var session = enrolled.Payload!["sessionId"]!.GetValue<string>();
        room.ExpCapBroadcasted = true;
        var request = new RerunRequest { Scope = "0", SessionId = session, ResumeToken = "host-token" };
        var response = await Update(h, request);
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        Assert.Equal(RerunStage.Aborted, response.Payload!.Deserialize<RerunSnapshot>(GatewayJson.Options)!.Stage);
        Assert.Equal(session, room.RerunExecution!.SessionId);
    }

    [Fact]
    public async Task WorldChange_InvalidatesOldSession_AndAllowsNewScopeEnrollment()
    {
        var (h, room) = Create();
        await Update(h, Enroll());
        var original = room.RerunExecution;
        await h.Ops.ResetForNewWorldRoundAsync(GatewayHandlerContext.Legacy("host"), 1);
        var request = Enroll(); request.Scope = "1";
        await Update(h, request);
        Assert.NotSame(original, room.RerunExecution);
        Assert.Equal(RerunStage.Aborted, original!.Stage);
        Assert.Equal("1", room.RerunExecution!.Scope);
        var late = new RerunRequest { Scope = "0", SessionId = original.SessionId, ResumeToken = "host-token" };
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Update(h, late)));
    }

    [Fact]
    public async Task ExistingSession_RequiresCallerCapabilityOnEveryRequest()
    {
        var (h, room) = Create();
        var enrolled = await Update(h, Enroll());
        var session = enrolled.Payload!["sessionId"]!.GetValue<string>();
        h.Tracker.CompleteHello("host", new ClientHello { ProtocolVersion = 3, Capabilities = [] });
        var response = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3("host"), Envelope(GatewayProtocol.Names.RerunState,
            new RerunRequest { Operation = RerunProtocol.Poll, Scope = "0", SessionId = session, ResumeToken = "host-token" }));
        Assert.Equal("rerun_capability_required", GatewayTestHarness.ErrorCode(response));
        Assert.NotNull(room.RerunExecution);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    public async Task TaskScopeIsIndependentOfDefaultServerWorldRound(string scope)
    {
        var (h, room) = Create();
        Assert.Equal(0, room.CurrentWorldRound);
        var host = Enroll(); host.Scope = scope;
        var response = await Update(h, host);
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        Assert.Equal(scope, response.Payload!["scope"]!.GetValue<string>());
        Assert.Equal(0, room.RerunExecution!.WorldRound);
        var member = Enroll("member-token"); member.Scope = "different";
        Assert.Equal("rerun_scope_mismatch", GatewayTestHarness.ErrorCode(await Update(h, member, "member")));
        member.Scope = scope;
        Assert.Null(GatewayTestHarness.ErrorCode(await Update(h, member, "member")));
        Assert.Equal(RerunStage.Normal, room.RerunExecution.Stage);
    }

    [Fact]
    public async Task Poll_CannotCreateState()
    {
        var (h, room) = Create();
        Assert.Equal("rerun_not_enrolled", GatewayTestHarness.ErrorCode(await Update(h, new() { Scope = "1" })));
        Assert.Null(room.RerunExecution);
    }

    /// <summary>
    /// 同一世界轮里第二次启动锄地：旧会话若已是终态（Completed/Aborted），必须允许新 Enroll 建立新会话。
    /// 否则新 token 会被 rerun_token 拒绝，把整轮锄地直接拖停（用户重开/守护重开/异常后重开的真实路径）。
    /// </summary>
    [Fact]
    public async Task TerminalSessionIsReplacedByNextEnrollment()
    {
        var (h, room) = Create();
        await Update(h, Enroll("first-token"));
        var original = room.RerunExecution!;
        original.Abort("participant_left");
        Assert.True(original.IsTerminal);

        var second = await Update(h, Enroll("second-token"));
        Assert.Null(GatewayTestHarness.ErrorCode(second));
        Assert.NotSame(original, room.RerunExecution);
        Assert.NotEqual(original.SessionId, room.RerunExecution!.SessionId);
        Assert.Equal(RerunStage.Registering, room.RerunExecution.Stage);
    }

    /// <summary>
    /// 进行中的会话绝不能被新 Enroll 替换：不同 token 必须被拒（避免误重置正在跑的阶段）。
    /// </summary>
    [Fact]
    public async Task ActiveSessionIsNotReplacedByForeignEnrollment()
    {
        var (h, room) = Create();
        await Update(h, Enroll("first-token"));
        var original = room.RerunExecution!;
        Assert.False(original.IsTerminal);

        var response = await Update(h, Enroll("second-token"));
        Assert.Equal("rerun_token", GatewayTestHarness.ErrorCode(response));
        Assert.Same(original, room.RerunExecution);
    }
}
