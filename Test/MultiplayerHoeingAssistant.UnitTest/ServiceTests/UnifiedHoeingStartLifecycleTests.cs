using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[CollectionDefinition("UnifiedHoeingStartLifecycle", DisableParallelization = true)]
public sealed class UnifiedHoeingStartLifecycleCollection { }

[Collection("UnifiedHoeingStartLifecycle")]
public sealed class UnifiedHoeingStartLifecycleTests
{
    private static CoordinatedBatchSnapshot Start(string id = "batch-a", int generation = 41) =>
        new(id, generation, 0, 0, 0, "preparing", "")
        {
            Protocol = "hoeing.start.v2",
            Source = "manual",
            RequestId = "request-" + id,
            ParticipantUids = new[] { "self", "peer" },
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            {
                ["self"] = new[] { new CoordinatedHoeingBinding("本地锄地", "group") },
                ["peer"] = new[] { new CoordinatedHoeingBinding("队友锄地", "group") }
            }
        };

    private static OnlineIntentLifecycle Lifecycle()
    {
        // These are real state-machine calls: fresh edges/local intents persist their generation.
        // The test host must choose an isolated root before any production singleton is initialized.
        Assert.True(AssistantDataDirectory.IsExplicit,
            "运行此测试前必须为测试进程设置隔离 NEXUSBGI_DATA_ROOT，不能写真实用户的上线代序号。");
        return new OnlineIntentLifecycle(_ => { });
    }

    [Fact]
    public void PreparingWithoutRunTaskAlreadyOwnsTheBusyLifetime()
    {
        var batch = new OnlineHoeingBatch(Start(), "self", new TaskConflictPolicySettings());
        Assert.Null(batch.RunTask);
        Assert.Equal("preparing", batch.Phase);
        Assert.True(batch.IsAlive);

        var completion = new TaskCompletionSource<bool>();
        batch.RunTask = completion.Task;
        batch.Prepared = true;
        Assert.True(batch.IsAlive);
        completion.SetResult(true);
        Assert.True(batch.RunTask.IsCompleted);
        Assert.True(batch.IsAlive); // Execution ending does not prove ownership cleanup.

        batch.CleanupConfirmed = true;
        batch.MarkSettled();
        Assert.False(batch.IsAlive);
        batch.Cts.Dispose();
    }

    [Fact]
    public void CancelledOrFailedExecutionStaysBusyUntilCleanupHasSettled()
    {
        var batch = new OnlineHoeingBatch(Start(), "self", new TaskConflictPolicySettings());
        batch.RunTask = Task.CompletedTask;
        batch.Phase = "aborted";
        batch.Cancel();
        Assert.True(batch.IsCancellationRequested);
        Assert.False(batch.CleanupConfirmed);
        Assert.True(batch.IsAlive);

        batch.Phase = "cleanup";
        Assert.True(batch.IsAlive);
        batch.CleanupConfirmed = true;
        Assert.True(batch.IsAlive); // Explicit settlement closes the accepted-start lifetime.
        batch.MarkSettled();
        batch.MarkSettled();
        Assert.False(batch.IsAlive);
        batch.Cts.Dispose();
    }

    [Fact]
    public void AcceptedStartFreezesParticipantsAndEveryMembersBindings()
    {
        var participants = new List<string> { "self", "peer" };
        var localBindings = new List<CoordinatedHoeingBinding>
        {
            new("第一组", "group"), new("后续一条龙", "onedragon")
        };
        var peerBindings = new List<CoordinatedHoeingBinding> { new("队友第一组", "group") };
        var sourceBindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
        {
            ["self"] = localBindings, ["peer"] = peerBindings
        };
        var source = Start() with { ParticipantUids = participants, Bindings = sourceBindings };
        var batch = new OnlineHoeingBatch(source, "self", new TaskConflictPolicySettings());

        participants[0] = "replacement";
        participants.RemoveAt(1);
        localBindings[0] = new("不应执行的替换组", "onedragon");
        localBindings.Clear();
        peerBindings[0] = new("队友替换组", "onedragon");
        sourceBindings.Clear();

        Assert.Equal(new[] { "self", "peer" }, batch.Start!.ParticipantUids);
        Assert.Equal(new[] { new CoordinatedHoeingBinding("第一组", "group"),
            new CoordinatedHoeingBinding("后续一条龙", "onedragon") }, batch.Bindings);
        Assert.Equal(batch.Bindings, batch.Start.Bindings["self"]);
        Assert.Equal(new CoordinatedHoeingBinding("队友第一组", "group"),
            Assert.Single(batch.Start.Bindings["peer"]));
        batch.Cts.Dispose();
    }

    [Fact]
    public void AcceptedStartFreezesIntentGenerationsAndMemberSnapshots()
    {
        var generations = new Dictionary<string, int> { ["self"] = 101, ["peer"] = 202 };
        var members = new List<CoordinatedHoeingMember>
        {
            new("self", "preparing", ""), new("peer", "preparing", "")
        };
        var source = Start() with { IntentGenerations = generations, Members = members };
        var batch = new OnlineHoeingBatch(source, "self", new TaskConflictPolicySettings());

        generations["self"] = 999;
        generations.Remove("peer");
        members[0] = new("replacement", "failed", "来源已改变");
        members.Clear();

        Assert.Equal(2, batch.Start!.IntentGenerations.Count);
        Assert.Equal(101, batch.Start.IntentGenerations["self"]);
        Assert.Equal(202, batch.Start.IntentGenerations["peer"]);
        Assert.Equal(new[] { new CoordinatedHoeingMember("self", "preparing", ""),
            new CoordinatedHoeingMember("peer", "preparing", "") }, batch.Start.Members);
        batch.Cts.Dispose();
    }

    [Fact]
    public void AcceptedStartFreezesAllFourPostRunPolicyFields()
    {
        var sourcePolicy = new TaskConflictPolicySettings
        {
            Policy = TaskConflictPolicy.RunSpecified,
            SpecifiedTaskType = "onedragon",
            SpecifiedTaskName = "接受开始时约定的后续任务",
            SpecifiedTaskPriority = 17
        };
        var batch = new OnlineHoeingBatch(Start(), "self", sourcePolicy);
        sourcePolicy.Policy = TaskConflictPolicy.Resume;
        sourcePolicy.SpecifiedTaskType = "group";
        sourcePolicy.SpecifiedTaskName = "执行期间新选任务";
        sourcePolicy.SpecifiedTaskPriority = 99;

        Assert.NotNull(batch.Policy);
        Assert.NotSame(sourcePolicy, batch.Policy);
        Assert.Equal(TaskConflictPolicy.RunSpecified, batch.Policy.Policy);
        Assert.Equal("onedragon", batch.Policy.SpecifiedTaskType);
        Assert.Equal("接受开始时约定的后续任务", batch.Policy.SpecifiedTaskName);
        Assert.Equal(17, batch.Policy.SpecifiedTaskPriority);
        batch.Cts.Dispose();
    }

    [Fact]
    public void AnotherBatchCannotReplaceExecutingEvenWhenItIsManual()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-a", 41, manual: true, intentGeneration: null));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);

        Assert.False(lifecycle.TryBeginCoordinatedBatch("batch-b", 42, manual: true, intentGeneration: null));
        Assert.False(lifecycle.TryBeginCoordinatedBatch("batch-b", 42, manual: false, intentGeneration: 42));
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-a", 41, manual: true, intentGeneration: null));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);

        lifecycle.FinishCoordinatedBatch("batch-b", "未受理批次的迟到收尾");
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
        lifecycle.FinishCoordinatedBatch("batch-a", "本批已清理");
        Assert.Equal(OnlineIntentState.Idle, lifecycle.State);
    }

    [Fact]
    public void FreshOnlineEdgeCannotRearmAnActiveBatchAfterDebounceWindow()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryBeginCoordinatedBatch("manual", 41, manual: true, intentGeneration: null));
        // No successful report was recorded: the usual 60-second debounce cannot explain rejection.
        Assert.Equal(TimeSpan.Zero, lifecycle.GetDebounceRemaining());
        var result = lifecycle.ApplyEdge(100, DateTime.UtcNow, out var reportGeneration);
        Assert.Equal(OnlineEdgeResult.Debounced, result);
        Assert.Equal(0, reportGeneration);
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
        Assert.False(lifecycle.TryBeginCoordinatedBatch("edge-batch", 42, manual: false, intentGeneration: 100));
    }

    [Fact]
    public void LocalIntentDuringExecutionCannotArmOrStartAnotherBatch()
    {
        var lifecycle = Lifecycle();
        var intent = lifecycle.RaiseLocalIntent("scheduled");
        Assert.True(lifecycle.TryBeginCoordinatedBatch("online", 41, manual: false, intentGeneration: intent));
        var repeated = lifecycle.RaiseLocalIntent("command");
        Assert.Equal(intent, repeated);
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
        Assert.False(lifecycle.TryBeginCoordinatedBatch("replacement", 42, manual: false, intentGeneration: repeated));
    }

    [Fact]
    public void SettledBatchAllowsAFreshIntentButDoesNotReplayAnEdgeObservedWhileBusy()
    {
        var lifecycle = Lifecycle();
        Assert.Equal(OnlineEdgeResult.Accepted, lifecycle.ApplyEdge(101, DateTime.UtcNow, out var firstIntent));
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-a", 41, manual: false, intentGeneration: firstIntent));
        Assert.Equal(OnlineEdgeResult.Debounced, lifecycle.ApplyEdge(102, DateTime.UtcNow, out _));
        lifecycle.FinishCoordinatedBatch("batch-a", "执行与清理已完成");
        Assert.Equal(OnlineIntentState.Idle, lifecycle.State);

        Assert.Equal(OnlineEdgeResult.NoEdge, lifecycle.ApplyEdge(102, DateTime.UtcNow, out _));
        Assert.False(lifecycle.TryBeginCoordinatedBatch("late", 42, manual: false, intentGeneration: 102));
        Assert.Equal(OnlineEdgeResult.Accepted, lifecycle.ApplyEdge(103, DateTime.UtcNow, out var nextIntent));
        Assert.Equal(OnlineIntentState.Armed, lifecycle.State);
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-b", 43, manual: false, intentGeneration: nextIntent));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
    }

    [Fact]
    public void LateOldFinishCannotClearANewExecutingBatch()
    {
        var lifecycle = Lifecycle();
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-a", 41, manual: true, intentGeneration: null));
        lifecycle.FinishCoordinatedBatch("batch-a", "旧批已清理");
        var intent = lifecycle.RaiseLocalIntent("command");
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-b", 42, manual: false, intentGeneration: intent));

        lifecycle.FinishCoordinatedBatch("batch-a", "迟到的旧批 finally");
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-b", 42, manual: true, intentGeneration: null));
        Assert.False(lifecycle.TryBeginCoordinatedBatch("batch-c", 43, manual: true, intentGeneration: null));
        lifecycle.FinishCoordinatedBatch("batch-b", "新批已清理");
        Assert.Equal(OnlineIntentState.Idle, lifecycle.State);
    }
}
