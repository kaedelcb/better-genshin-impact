using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomOrchestrationPresentationTests
{
    private static AssistConfig Fixture()
    {
        var config = new AssistConfig
        {
            PlayerUid = "101", ServerUrl = "https://rooms.example.test", ActiveRoomProfileId = "a",
            OnlineHoeingGroupNames = ["新绑定"], OnlineHoeingGroupTypes = ["group"],
            RoomProfiles = [new() { Id = "a", Name = "甲", RoomCode = "AAAAAA", ServerUrl = "https://rooms.example.test" },
                new() { Id = "b", Name = "乙", RoomCode = "BBBBBB", ServerUrl = "https://rooms.example.test", OnlineHoeingGroupNames = ["乙任务"] },
                new() { Id = "c", Name = "丙", RoomCode = "CCCCCC", ServerUrl = "https://rooms.example.test" }]
        };
        return config;
    }

    private static RoomSwitchCompletion SetEdge(AssistConfig config, string source, string target)
    {
        var choice = RoomSwitchCompletion.Choices(config, source).Single(x => x.TargetProfileId == target);
        CompletionPolicyFields.ApplyForProfile(config, source, new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Stop, RoomSwitch = choice });
        return choice;
    }

    [Fact]
    public void ActualBatchIdentityKeepsEarlierCleanupEdgeAndFrozenTaskOrder()
    {
        var config = Fixture();
        var target = SetEdge(config, "a", "b");
        var key = new RoomOrchestrationRoomKey("a", target.SourceServerUrl, target.SourceRoomCode, config.PlayerUid);
        var now = DateTimeOffset.UtcNow;
        var run = new RoomOrchestrationRunSnapshot(key, "finished", 7,
            [new("旧冻结任务", "group", "completed")], "succeeded", "结清", now, true);
        var transition = new RoomOrchestrationTransitionSnapshot(key, "intent", target, "waiting", "none", "等待原组结清",
            now.AddSeconds(-1), SourceBatchId: run.BatchId, SourceGeneration: run.Generation);
        var result = RoomOrchestrationPresentation.Create(config, "a", null, "", true,
            p => p.Id == "a" ? run : null, p => p.Id == "a" ? transition : null);
        var a = result.Cards.First(c => c.Id == "a");
        Assert.Equal("旧冻结任务", Assert.Single(a.Tasks).Name);
        Assert.Equal("done", a.Tasks[0].Status.Kind);
        Assert.Equal("新绑定", Assert.Single(a.NextTasks).Name);
        Assert.Equal("waiting", a.Edge!.SwitchStatus.Kind);
        Assert.Null(a.PreviousEdge);
        Assert.Equal("waiting", result.CurrentStatus.Kind);
        Assert.Equal("none", result.Cards.First(c => c.Id == "b").Tasks[0].Status.Kind);
    }

    [Fact]
    public void CycleDrawsEachRoomExactlyOnceWithoutInventingSuccess()
    {
        var config = Fixture();
        SetEdge(config, "a", "b"); SetEdge(config, "b", "c"); SetEdge(config, "c", "a");
        var view = RoomOrchestrationPresentation.Create(config, null, null, "", true, _ => null, _ => null);
        Assert.Equal(new[] { "a", "b", "c" }, view.Cards.Select(c => c.Id));
        Assert.All(view.Cards, c => Assert.False(c.Current));
        Assert.True(view.Cards[0].Edge!.ContinuesToNext);
        Assert.True(view.Cards[1].Edge!.ContinuesToNext);
        Assert.False(view.Cards[2].Edge!.ContinuesToNext);
        Assert.Equal("unknown", view.CurrentStatus.Kind);
        Assert.DoesNotContain(view.Cards, c => c.Status.Kind == "done");
    }
}
