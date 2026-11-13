using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// 验证结束动作传输合同和受理边界；执行端仍负责目录修订、任务存在性和启用状态。
public sealed class CompletionActionStartTests
{
    [Fact]
    public void LegacyFourArgumentActionKeepsItsOriginalEntry()
    {
        var action = new CoordinatedCompletionAction("runSpecified", "group", "旧配置", 2);
        Assert.True(action.IsValid());
        Assert.Equal("default", action.StartMode);
        Assert.Equal("", action.StartTaskId);
        Assert.Equal("", action.StartTaskName);
        Assert.Equal("", action.ConfigRevision);
        Assert.Equal("", action.WorkflowId);
        var legacy = JsonSerializer.Deserialize<CoordinatedCompletionAction>(
            "{\"policy\":\"runSpecified\",\"taskType\":\"onedragon\",\"taskName\":\"旧一条龙\",\"priority\":2}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(legacy.IsValid());
        Assert.Equal("default", legacy.StartMode);
    }

    [Theory]
    [InlineData("group", "beginning")]
    [InlineData("group", "node")]
    [InlineData("onedragon", "beginning")]
    [InlineData("onedragon", "node")]
    [InlineData("workflow", "beginning")]
    [InlineData("workflow", "node")]
    public async Task ExplicitStartFieldsSurviveStatusAndWireWithoutChangingHoeingBindings(string type, string mode)
    {
        var action = new CoordinatedCompletionAction("runSpecified", type, "收尾", 4, mode,
            mode == "node" ? "stable-task-2" : "", mode == "node" ? "第二项" : "", "revision-7",
            type == "workflow" ? "stable-workflow" : "");
        Assert.True(action.IsValid());
        var h = new GatewayTestHarness();
        var client = GatewayHandlerContext.Legacy("completion-start-client");
        var room = "S" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(client, room, "pw", "u0", "成员", clientInstanceId: "instance");
        await h.Ops.ReportControlStatusAsync(client, new ControlStatus
        {
            RoomCode = room, PlayerUid = "u0", OnlineHoeingGroupNames = ["联机配置组"],
            OnlineHoeingGroupTypes = ["group"], HoeingCompletionAction = action
        });
        var reported = Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room));
        Assert.Equal(action, reported.HoeingCompletionAction);
        var wire = GatewayEnvelope.ToPayload(reported)["hoeingCompletionAction"]!;
        Assert.Equal(mode, wire["startMode"]!.GetValue<string>());
        Assert.Equal(action.StartTaskId, wire["startTaskId"]!.GetValue<string>());
        Assert.Equal(action.StartTaskName, wire["startTaskName"]!.GetValue<string>());
        Assert.Equal(action.ConfigRevision, wire["configRevision"]!.GetValue<string>());
        Assert.Equal(action.WorkflowId, wire["workflowId"]!.GetValue<string>());
        var batch = await h.Ops.BeginManualCoordinatedBatchAsync(client, "request", ["u0"], null, true);
        Assert.True(batch.UseCompletionActions);
        Assert.Equal(new CoordinatedBatchBinding("联机配置组", "group"), Assert.Single(batch.Bindings["u0"]));
    }

    [Fact]
    public void InvalidExplicitStartsCannotBeTreatedAsLegacyBeginning()
    {
        var node = new CoordinatedCompletionAction("runSpecified", "group", "收尾", 0,
            "node", "stable-node", "任务", "revision");
        foreach (var invalid in new[]
        {
            node with { StartMode = "unknown" }, node with { StartTaskId = "" },
            node with { StartTaskName = " " }, node with { ConfigRevision = "" },
            node with { StartMode = "default" }, node with { StartMode = "beginning" },
            node with { TaskType = "workflow", WorkflowId = "" }, node with { WorkflowId = "wrong-kind" },
            node with { TaskType = "workflow", WorkflowId = "flow", StartMode = "default", StartTaskId = "", StartTaskName = "", ConfigRevision = "" }
        }) Assert.False(invalid.IsValid());
    }

    [Fact]
    public async Task InvalidCompletionChoiceRefusesBeforeCreatingBatchButDoesNotBlockStopAfterHoeing()
    {
        var h = new GatewayTestHarness();
        var client = GatewayHandlerContext.Legacy("invalid-start-client");
        var room = "S" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(client, room, "pw", "u0", "成员", clientInstanceId: "instance");
        await h.Ops.ReportControlStatusAsync(client, new ControlStatus
        {
            RoomCode = room, PlayerUid = "u0", OnlineHoeingGroupNames = ["联机"], OnlineHoeingGroupTypes = ["group"],
            HoeingCompletionAction = new("runSpecified", "group", "收尾", 0, "node", "", "旧节点", "revision")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(client, "invalid", ["u0"], null, true));
        Assert.Contains("hoeing_completion_action_invalid:u0", error.Message);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(client));
        Assert.False((await h.Ops.BeginManualCoordinatedBatchAsync(client, "stop-after", ["u0"], null, false)).UseCompletionActions);
    }
}
