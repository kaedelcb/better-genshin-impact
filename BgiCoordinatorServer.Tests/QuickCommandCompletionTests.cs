using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class QuickCommandCompletionTests
{
    private static readonly string[] Keys = ["一键传奇", "一键次数盾", "一键精英", "一键小怪", "一键自定义"];

    private static async Task<(GatewayTestHarness H, GatewayHandlerContext[] C, string Room)> Setup()
    {
        var h = new GatewayTestHarness();
        var room = "Q" + Guid.NewGuid().ToString("N")[..8];
        var contexts = new[] { GatewayHandlerContext.Legacy("c0"), GatewayHandlerContext.Legacy("c1") };
        for (var i = 0; i < contexts.Length; i++)
        {
            await h.Ops.JoinControlRoomAsync(contexts[i], room, "pw", "u" + i, "成员" + i, clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(contexts[i], new ControlStatus
            {
                RoomCode = room, PlayerUid = "u" + i,
                // 不绑定常规联机任务，证明快捷指令只使用自己的绑定来源。
                QuickCommands = Keys.ToDictionary(key => key, key => (i == 0 ? "GROUP:" : "ONEDRAGON:") + key + i),
                HoeingCompletionAction = new("runSpecified", "group", "收尾任务", 7)
            });
            var reported = h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(player => player.PlayerUid == "u" + i);
            Assert.Equal(Keys.Length, reported.QuickCommands.Count);
        }
        return (h, contexts, room);
    }

    [Fact]
    public async Task StartSnapshotCopiesQuickBindingsWithoutSharingTheLiveDictionary()
    {
        var (h, _, room) = await Setup();
        var live = h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(player => player.PlayerUid == "u0");
        var snapshot = h.RoomManager.GetCoordinatedStartPlayers("CTRL_" + room).Single(player => player.PlayerUid == "u0");
        Assert.NotSame(live.QuickCommands, snapshot.QuickCommands);
        Assert.Equal(live.QuickCommands[Keys[0]], snapshot.QuickCommands[Keys[0]]);
        snapshot.QuickCommands[Keys[0]] = "GROUP:只改变快照";
        Assert.Equal("GROUP:" + Keys[0] + "0", live.QuickCommands[Keys[0]]);
    }

    [Theory]
    [InlineData("一键传奇")]
    [InlineData("一键次数盾")]
    [InlineData("一键精英")]
    [InlineData("一键小怪")]
    [InlineData("一键自定义")]
    public async Task EachQuickCommandRunsItsOwnMixedBindingsAndCompletesOnce(string key)
    {
        var (h, c, _) = await Setup();
        var state = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, key);
        Assert.Equal(key, state.QuickCommandKey);
        Assert.Equal("manual", state.Source);
        Assert.True(state.UseCompletionActions);
        Assert.Equal(new CoordinatedBatchBinding(key + "0", "group"), Assert.Single(state.Bindings["u0"]));
        Assert.Equal(new CoordinatedBatchBinding(key + "1", "onedragon"), Assert.Single(state.Bindings["u1"]));

        async Task<CoordinatedBatchSnapshot> Send(int member, string stage = "", string result = "")
            => await h.Ops.UpdateCoordinatedBatchAsync(c[member], state.Generation, "token" + member,
                state.BatchId, member == 0 ? "group" : "onedragon", 0, 0, result, stage);
        await Send(0, "registered");
        Assert.Equal("preparing", (await Send(1, "registered")).Phase);
        await Send(0, "prepared");
        Assert.Equal("takeover", (await Send(1, "prepared")).Phase);
        await Send(0, "takeoverConfirmed");
        Assert.Equal("running", (await Send(1, "takeoverConfirmed")).Phase);
        Assert.Equal("running", (await Send(0, result: "succeeded")).Phase);
        var completed = await Send(1, result: "succeeded");
        Assert.Equal("completed", completed.Phase);
        Assert.Equal(1, completed.Index);
        Assert.Equal(key, completed.QuickCommandKey);
        Assert.Equal("completed", (await Send(1, result: "succeeded")).Phase);
    }

    [Theory]
    [InlineData("c0")]
    [InlineData("c1")]
    public async Task MissingNewCapabilityRefusesBeforeCreatingAnyBatch(string unsupported)
    {
        var (h, c, _) = await Setup();
        h.Capabilities.UnsupportedCapabilities.Add((unsupported, GatewayProtocol.QuickCommandCompletionCapability));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, Keys[0]));
        Assert.Contains("hoeing_quick_command_completion_capability_required", error.Message);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(c[0]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("GROUP:")]
    [InlineData("ONEDRAGON: ")]
    [InlineData("GROUP: 首尾空格")]
    [InlineData("unsupported:任务")]
    public async Task InvalidQuickBindingCannotFallBackToRegularHoeingTasks(string binding)
    {
        var (h, c, room) = await Setup();
        await h.Ops.ReportControlStatusAsync(c[1], new ControlStatus
        {
            RoomCode = room, PlayerUid = "u1", OnlineHoeingGroupNames = ["其他联机任务"],
            OnlineHoeingGroupTypes = ["group"], QuickCommands = new() { [Keys[0]] = binding }
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, Keys[0]));
        Assert.Contains("batch_quick_binding_invalid:u1", error.Message);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(c[0]));
    }

    [Fact]
    public async Task RequestIdentityFreezesKeyAndBindingsAcrossReportsAndReplay()
    {
        var (h, c, room) = await Setup();
        var accepted = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, Keys[0]);
        await h.Ops.ReportControlStatusAsync(c[1], new ControlStatus
        {
            RoomCode = room, PlayerUid = "u1", QuickCommands = new() { [Keys[0]] = "GROUP:改后的任务" }
        });
        var replay = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u1", "u0"], null, true, Keys[0]);
        Assert.Equal(accepted.BatchId, replay.BatchId);
        Assert.Equal(new CoordinatedBatchBinding(Keys[0] + "1", "onedragon"), Assert.Single(replay.Bindings["u1"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, Keys[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true));
        var current = h.Ops.CurrentCoordinatedBatch(c[0])!;
        Assert.Equal(Keys[0], current.QuickCommandKey);
        Assert.Equal(accepted.BatchId, current.BatchId);
        Assert.Equal("registering", current.Phase);
    }

    [Fact]
    public async Task MemberCannotRegisterAnotherTypeThanItsOwnFrozenBinding()
    {
        var (h, c, _) = await Setup();
        var state = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true, Keys[0]);
        var rejected = await h.Ops.UpdateCoordinatedBatchAsync(c[1], state.Generation, "token1", state.BatchId,
            "group", 0, 0, "", "registered");
        Assert.Equal("aborted", rejected.Phase);
        Assert.Equal("batch_layout_mismatch:u1", rejected.Reason);
    }

    [Fact]
    public async Task GatewayCarriesTheQuickKeyAndRejectsUnsupportedKeys()
    {
        var (h, c, _) = await Setup();
        await h.Dispatcher.DispatchAsync(c[0], GatewayTestHarness.HelloEnvelope());
        var response = await h.Dispatcher.DispatchAsync(c[0], new GatewayEnvelope
        {
            Type = "command", Name = GatewayProtocol.Names.ControlBatchBegin, RequestId = Guid.NewGuid().ToString("N"),
            Payload = GatewayEnvelope.ToPayload(new { requestId = "wire", targetUids = new[] { "u0" },
                useCompletionActions = true, quickCommandKey = Keys[4] })
        });
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        Assert.Equal(Keys[4], response.Payload!["quickCommandKey"]!.GetValue<string>());
        Assert.True(response.Payload["useCompletionActions"]!.GetValue<bool>());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "unknown", ["u0"], null, true, "任意指令"));
    }
}
