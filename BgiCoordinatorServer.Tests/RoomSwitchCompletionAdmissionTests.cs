using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// Verify the same-version admission boundary: a confirmed ordinary stop must not authorize
// a later switch, and an accepted switch must keep its original action across status/replay.
public sealed class RoomSwitchCompletionAdmissionTests
{
    private static readonly CoordinatedCompletionAction Stop = new("stop", "group", "", 0);
    private static readonly CoordinatedCompletionAction Switch = new("switchRoom", "group", "准备", 7,
        "node", "stable-task", "上线前准备", "task-revision", "", "乙房", "ROOM-B", true, "v1:" + new string('A', 64));

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("stop-to-switch")]
    [InlineData("switch-to-stop")]
    [InlineData("target")]
    [InlineData("revision-only")]
    [InlineData("successor")]
    [InlineData("invalid")]
    public async Task ManualBeginRejectsUnconfirmedOrChangedSwitchBeforeAnyStart(string change)
    {
        var (h, client, room) = await SetupAsync();
        var displayed = change switch
        {
            "unconfirmed" => null,
            "stop-to-switch" => Stop,
            _ => Switch
        };
        var actual = change switch
        {
            "switch-to-stop" => Stop,
            "target" => Switch with { TargetRoomCode = "ROOM-C", TargetName = "丙房", ActionRevision = "v1:" + new string('B', 64) },
            "revision-only" => Switch with { ActionRevision = "v1:" + new string('B', 64) },
            "successor" => Switch with { ConfigRevision = "new-task-revision" },
            "invalid" => Switch with { TargetRoomCode = "" },
            _ => Switch
        };
        await ReportAsync(h, client, room, actual);
        var reply = await BeginAsync(h, client, "confirmed", true, displayed);
        Assert.Equal(GatewayProtocol.ErrorCodes.BadRequest, GatewayTestHarness.ErrorCode(reply));
        Assert.Contains("重新", reply.Payload!["error"]!["message"]!.GetValue<string>());
        Assert.Null(h.Ops.CurrentCoordinatedBatch(client));
        Assert.Empty(Starts(h));
    }

    [Fact]
    public async Task AcceptedSwitchFreezesTheActualActionAndDoesNotRebindOnRetry()
    {
        var (h, client, room) = await SetupAsync();
        // A display-only rename is not a changed destination; it keeps the same action identity.
        var actual = Switch with { TargetName = "乙房新名称" };
        await ReportAsync(h, client, room, actual);
        var accepted = Snapshot(await BeginAsync(h, client, "same-request", true, Switch));
        Assert.Equal(actual, accepted.ConfirmedCompletionActions["self"]);
        var start = Assert.Single(Starts(h));
        Assert.Equal(actual, start.ConfirmedCompletionActions["self"]);
        Assert.Equal(accepted.BatchId, start.BatchId);

        var changed = actual with { TargetRoomCode = "ROOM-C", ActionRevision = "v1:" + new string('B', 64) };
        await ReportAsync(h, client, room, changed);
        Assert.Equal(actual, h.Ops.CurrentCoordinatedBatch(client)!.ConfirmedCompletionActions["self"]);
        var replay = Snapshot(await BeginAsync(h, client, "same-request", true, Switch));
        Assert.Equal(accepted.BatchId, replay.BatchId);
        Assert.Equal(actual, replay.ConfirmedCompletionActions["self"]);
        Assert.Equal(GatewayProtocol.ErrorCodes.BadRequest,
            GatewayTestHarness.ErrorCode(await BeginAsync(h, client, "same-request", true, changed)));
        Assert.Equal(actual, h.Ops.CurrentCoordinatedBatch(client)!.ConfirmedCompletionActions["self"]);
    }

    [Fact]
    public async Task AutomaticOnlineStartFreezesSwitchEvenWithoutManualChoiceFlag()
    {
        var (h, client, room) = await SetupAsync();
        await ReportAsync(h, client, room, Switch);
        await h.Ops.ReportOnlineEventAsync(client, 17, true);
        var accepted = h.Ops.CurrentCoordinatedBatch(client)!;
        Assert.Equal("online", accepted.Source);
        Assert.False(accepted.UseCompletionActions);
        Assert.Equal(Switch, accepted.ConfirmedCompletionActions["self"]);
        Assert.Equal(Switch, Assert.Single(Starts(h)).ConfirmedCompletionActions["self"]);
    }

    [Fact]
    public async Task UncheckedManualBeginDoesNotFreezeOrRequireAValidSwitch()
    {
        var (h, client, room) = await SetupAsync();
        await ReportAsync(h, client, room, Switch with { TargetRoomCode = "" });
        var accepted = Snapshot(await BeginAsync(h, client, "no-action", false, null));
        Assert.False(accepted.UseCompletionActions);
        Assert.Empty(accepted.ConfirmedCompletionActions);
        Assert.Empty(Assert.Single(Starts(h)).ConfirmedCompletionActions);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("runSpecified")]
    public async Task OrdinaryWebConfirmationRetainsOriginalAdmissionWithoutNewConfirmationMap(string policy)
    {
        var (h, client, room) = await SetupAsync();
        var action = new CoordinatedCompletionAction(policy, "onedragon", "本机收尾", 0);
        await ReportAsync(h, client, room, action);
        var accepted = Snapshot(await BeginAsync(h, client, "ordinary", true, null));
        Assert.True(accepted.UseCompletionActions);
        Assert.Equal(action, accepted.ConfirmedCompletionActions["self"]);
    }

    [Fact]
    public async Task QuickCommandWithSelectedActionsUsesTheSameSwitchConfirmationBoundary()
    {
        var (h, client, room) = await SetupAsync();
        await ReportAsync(h, client, room, Switch);
        Assert.Equal(GatewayProtocol.ErrorCodes.BadRequest, GatewayTestHarness.ErrorCode(
            await BeginAsync(h, client, "quick-unconfirmed", true, Stop, "一键自定义")));
        var accepted = Snapshot(await BeginAsync(h, client, "quick-confirmed", true, Switch, "一键自定义"));
        // This is only admission, not evidence the bound ordinary job actually performed joint hoeing.
        Assert.Equal("一键自定义", accepted.QuickCommandKey);
        Assert.Equal("普通快捷任务", Assert.Single(accepted.Bindings["self"]).Name);
        Assert.Equal(Switch, accepted.ConfirmedCompletionActions["self"]);
        Assert.Equal("registering", accepted.Phase);
    }

    private static async Task<(GatewayTestHarness H, GatewayHandlerContext Client, string Room)> SetupAsync()
    {
        var h = new GatewayTestHarness();
        var client = GatewayHandlerContext.V3("switch-confirmation-" + Guid.NewGuid().ToString("N"));
        var room = "A" + Guid.NewGuid().ToString("N")[..8];
        await h.Dispatcher.DispatchAsync(client, GatewayTestHarness.HelloEnvelope());
        await h.Ops.JoinControlRoomAsync(client, room, "pw", "self", "成员", clientInstanceId: "instance");
        return (h, client, room);
    }

    private static Task ReportAsync(GatewayTestHarness h, GatewayHandlerContext client, string room, CoordinatedCompletionAction action)
        => h.Ops.ReportControlStatusAsync(client, new ControlStatus
        {
            RoomCode = room, PlayerUid = "self", ExpectedHoeingPlayers = 1,
            OnlineHoeingGroupNames = ["联机锄地"], OnlineHoeingGroupTypes = ["group"],
            QuickCommands = new() { ["一键自定义"] = "GROUP:普通快捷任务" }, HoeingCompletionAction = action
        });

    private static Task<GatewayEnvelope> BeginAsync(GatewayTestHarness h, GatewayHandlerContext client,
        string requestId, bool useCompletionActions, CoordinatedCompletionAction? displayed, string quickCommandKey = "")
        => h.Dispatcher.DispatchAsync(client, new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command, Name = GatewayProtocol.Names.ControlBatchBegin,
            RequestId = Guid.NewGuid().ToString("N"),
            Payload = GatewayEnvelope.ToPayload(new
            {
                requestId, targetUids = new[] { "self" }, useCompletionActions, quickCommandKey,
                confirmedCompletionActions = displayed == null ? null : new Dictionary<string, CoordinatedCompletionAction> { ["self"] = displayed }
            })
        });

    private static CoordinatedBatchSnapshot Snapshot(GatewayEnvelope response)
    {
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        return response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
    }

    private static IEnumerable<CoordinatedBatchSnapshot> Starts(GatewayTestHarness h)
        => h.GatewayClientProxy.Invocations.SelectMany(call => (object?[])call.Arguments[1])
            .OfType<GatewayEnvelope>().Where(e => e.Name == GatewayProtocol.Events.ControlBatchStart)
            .Select(e => e.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!);
}
