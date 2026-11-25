using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomOrchestrationStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "room-orchestration-test-" + Guid.NewGuid().ToString("N"));
    private string PathForStore => Path.Combine(_directory, "state.json");
    private static readonly RoomOrchestrationRoomKey A = new("a", "https://a.example.test/", "ABCDEF", "123");
    private static readonly RoomOrchestrationRoomKey B = new("b", "https://b.example.test/", "ABCDEF", "123");
    private static readonly CoordinatedHoeingBinding[] Bindings = [new("第一项", "group"), new("第二项", "onedragon")];

    [Fact]
    public void FrozenEvidenceRemainsScopedToRoomAndBatch()
    {
        var store = new RoomOrchestrationStateStore(PathForStore);
        store.Begin(A, "batch-a", 37, Bindings);
        store.SetItem(A, "batch-a", 37, 0, "completed", "已确认第一个作业成功");
        store.SetItem(A, "batch-a", 37, 1, "running", "正在执行");
        store.Begin(B, "batch-b", 1, Bindings);
        store.SetItem(A, "batch-b", 1, 0, "completed", "旧房不应消费这里的结果");
        Assert.Equal(new[] { "completed", "running" }, store.GetRun(A)!.Tasks.Select(t => t.State));
        Assert.All(store.GetRun(B)!.Tasks, t => Assert.Equal("pending", t.State));
        var reopened = new RoomOrchestrationStateStore(PathForStore);
        Assert.Equal("unknown", reopened.GetRun(A)!.Phase);
        Assert.Equal(new[] { "completed", "unknown" }, reopened.GetRun(A)!.Tasks.Select(t => t.State));
        reopened.Begin(A, "batch-a", 37, Bindings);
        Assert.Equal("completed", reopened.GetRun(A)!.Tasks[0].State);
        reopened.SetPhase(A, "batch-a", 37, "cancelled", "停止", settled: true);
        reopened.Begin(A, "batch-new", 38, [new("重排之后", "group")]);
        reopened.SetItem(A, "batch-a", 37, 0, "completed", "迟到结果");
        Assert.Equal("重排之后", Assert.Single(reopened.GetRun(A)!.Tasks).Name);
        Assert.Equal("pending", reopened.GetRun(A)!.Tasks[0].State);
    }

    [Fact]
    public void TransitionObservationsCannotReplaceNewIntentAndReconnectDoesNotInventSuccess()
    {
        var store = new RoomOrchestrationStateStore(PathForStore);
        var target = new RoomSwitchCompletion(A.ProfileId, A.ServerUrl, A.RoomCode,
            B.ProfileId, B.ServerUrl, B.RoomCode, "乙", RunAfterSwitch: true);
        var first = new RoomOrchestrationTransitionSnapshot(A, "first", target, "waiting", "pending", "等待", DateTimeOffset.Now);
        store.SetTransition(first, begin: true);
        var second = first with { IntentId = "second", UpdatedAt = DateTimeOffset.Now };
        store.SetTransition(second, begin: true);
        store.SetTransition(first with { SwitchState = "switched", UpdatedAt = DateTimeOffset.Now });
        Assert.Equal("second", store.GetTransition(A)!.IntentId);
        Assert.Equal("waiting", store.GetTransition(A)!.SwitchState);
        var reopened = new RoomOrchestrationStateStore(PathForStore);
        Assert.Equal("unknown", reopened.GetTransition(A)!.SwitchState);
        Assert.Equal("unknown", reopened.GetTransition(A)!.PostState);
        Assert.Null(reopened.GetTransition(B));
    }

    [Fact]
    public void LateDisplayPublicationCannotOverwriteSameIntentTerminal()
    {
        var store = new RoomOrchestrationStateStore(PathForStore);
        var target = new RoomSwitchCompletion(A.ProfileId, A.ServerUrl, A.RoomCode, B.ProfileId, B.ServerUrl, B.RoomCode, "乙", true);
        var running = new RoomOrchestrationTransitionSnapshot(A, "intent", target, "switched", "running", "执行中", DateTimeOffset.Now, Revision: 1);
        store.SetTransition(running, begin: true);
        store.SetTransition(running with { PostState = "completed", Revision = 2 });
        store.SetTransition(running with { UpdatedAt = DateTimeOffset.Now.AddSeconds(1) });
        Assert.Equal("completed", store.GetTransition(A)!.PostState);
        Assert.Equal(2, store.GetTransition(A)!.Revision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
