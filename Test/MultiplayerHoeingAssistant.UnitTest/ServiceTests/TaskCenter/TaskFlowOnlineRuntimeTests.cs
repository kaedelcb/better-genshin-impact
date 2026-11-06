using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskFlowOnlineRuntimeTests
{
    [Fact]
    public async Task SamePidEpochChangeCannotInvokeLiveCallbackBeforeFreezeFindsTheGap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flow-online-epoch-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "better-genshin-impact.log"); await File.WriteAllTextAsync(file, "");
        using var tail = new BgiLogTailService(() => dir);
        var epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 1 };
        var source = new WorkflowLogObservationSource(tail, () => epoch); var callbacks = 0;
        try
        {
            await using var session = await source.ArmLiveAsync("old-task", "hit", _ => Interlocked.Increment(ref callbacks), default);
            epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 2 };
            await File.AppendAllTextAsync(file, $"[{DateTime.Now:HH:mm:ss.fff}] [INF] [Primary:S1:P42:T2] Test\nhit\n");
            var fact = await session.FreezeAsync(default);
            Assert.Equal("unknown", fact.State); Assert.Equal(0, callbacks);
        }
        finally { tail.Dispose(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CompletedOnlineGapCannotTurnItsOldWaitCancellationIntoUserStop()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.DelayAsync = async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await release.Task; throw; }
        };
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait);
        var running = f.Runtime.ExecuteAsync(f.Run, Flow(wait), 0, () => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await f.Suspend();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3)); await f.Open(); release.TrySetResult();
        await Assert.ThrowsAsync<TaskFlowOnlineParkException>(() => running);
        Assert.Null(f.Runtime.GetError(f.Run.RunId));
    }

    [Fact]
    public async Task ResumedWatchdogWaitsForPostResumeStatusInsteadOfUsingOnlineCache()
    {
        using var f = new Fixture();
        var before = DateTimeOffset.UtcNow;
        var sample = new ControlStatus { TaskRunning = false, TaskStatusAvailable = true, TaskStatusBgiEpoch = "epoch", TaskStatusObservedAtUtc = before };
        var probes = 0;
        f.Runtime.StatusProvider = () => { Interlocked.Increment(ref probes); return sample; };
        f.Runtime.DelayAsync = (_, ct) => Task.Delay(10, ct);
        var watchdog = StartupStepKinds.Create(StartupStepKinds.Watchdog);
        watchdog.TriggerScope = "round"; watchdog.WatchRepeat = false; watchdog.FireSteps = [Write(1, 1)];
        await f.Runtime.ExecuteAsync(f.Run, Flow(watchdog), 0, () => { }, default);
        await Until(() => Volatile.Read(ref probes) >= 2);
        await f.Suspend();
        sample = new ControlStatus { TaskRunning = true, TaskStatusAvailable = true, TaskStatusBgiEpoch = "epoch", TaskStatusObservedAtUtc = before };
        await f.Runtime.PrepareOnlineResumeAsync(f.Run.RunId); await Task.Delay(70);
        Assert.Equal(0, f.Value(1)); Assert.Null(f.Runtime.GetError(f.Run.RunId));
        sample = new ControlStatus { TaskRunning = true, TaskStatusAvailable = true, TaskStatusBgiEpoch = "epoch", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow };
        await Until(() => f.Value(1) == 1);
        await f.Open();
    }

    [Fact]
    public async Task RepeatedGateOpenKeepsCurrentManualPromptCancelableByNextTakeover()
    {
        using var f = new Fixture(); await f.Suspend(); await f.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Runtime.ConfirmAsync = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return true; };
        var question = StartupStepKinds.Create(StartupStepKinds.ManualConfirm);
        var running = f.Runtime.ExecuteAsync(f.Run, Flow(question), 0, () => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Runtime.OnlineSendOpened(); // 重复Open不能丢掉本次prompt所绑定的取消令牌。
        await f.Suspend();
        await Assert.ThrowsAsync<TaskFlowOnlineParkException>(() => running).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task FlowWaitKeepsRemainingProgressAndDoesNotRepeatCompletedRegisterWrite()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        f.Runtime.DelayAsync = async (_, ct) =>
        { if (Interlocked.Increment(ref first) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 1;
        var node = Flow(Write(1, 1), wait, Write(2, 1));
        var running = f.Runtime.ExecuteAsync(f.Run, node, 0, () => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Suspend();
        await Assert.ThrowsAsync<TaskFlowOnlineParkException>(() => running);
        Assert.Equal(1, f.Value(1)); Assert.Equal(0, f.Value(2));
        f.Registers.Set(new() { Values = Values(1, 0) });
        Assert.Contains(f.Run.ExtensionData!, x => x.Key.EndsWith(":remaining"));
        await f.Open();
        await f.Runtime.ExecuteAsync(f.Run, node, 0, () => { }, default);
        Assert.Equal(0, f.Value(1)); Assert.Equal(1, f.Value(2));
    }

    [Fact]
    public async Task LocalConditionResumesChosenSubchainWithoutRejudgingOrReplaying()
    {
        using var f = new Fixture(); f.Registers.Set(new() { Values = Values(1, 1) });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var delays = 0;
        f.Runtime.DelayAsync = async (_, ct) =>
        { if (Interlocked.Increment(ref delays) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 1;
        var condition = StartupStepKinds.Create(StartupStepKinds.JudgeRegister);
        condition.RegisterCondition = new() { Values = Values(1, 1) };
        condition.TrueSteps = [Write(2, 1), wait, Write(3, 1)]; condition.FalseSteps = [Write(4, 1)];
        var node = Flow(condition); var running = f.Runtime.ExecuteAsync(f.Run, node, 0, () => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await f.Suspend();
        await Assert.ThrowsAsync<TaskFlowOnlineParkException>(() => running);
        f.Registers.Set(new() { Values = Values((1, 0), (2, 0)) });
        await f.Open(); await f.Runtime.ExecuteAsync(f.Run, node, 0, () => { }, default);
        Assert.Equal(0, f.Value(2)); Assert.Equal(1, f.Value(3)); Assert.Equal(0, f.Value(4));
    }

    [Fact]
    public async Task OnlineLogsAreExcludedAndFirstResumeLogCanWriteBeforeSendGateOpens()
    {
        using var f = new Fixture();
        await f.Runtime.ExecuteAsync(f.Run, Flow(f.Log), 0, () => { }, default);
        await using var scope = await f.Runtime.BeginTaskAsync(f.Run, Resource(), Occurrence(), 1, () => { }, default);
        var original = Assert.Single(f.Source.Sessions);
        await f.Suspend(); original.Hit(); await Task.Delay(30);
        Assert.Equal(0, f.Value(1)); Assert.Single(f.Runtime.List());
        await f.Runtime.PrepareOnlineResumeAsync(f.Run.RunId);
        Assert.True(f.Closed);
        f.Source.Sessions.Last().Hit();
        await Until(() => f.Value(1) == 1);
        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(f.Runtime.List());
        await f.Open();
    }

    [Fact]
    public async Task SuspendDoesNotWaitForWholeCallbackButKeepsAcceptedWorkForReturn()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var delays = 0;
        f.Runtime.DelayAsync = async (_, ct) =>
        { if (Interlocked.Increment(ref delays) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait); wait.WaitSeconds = 1;
        f.Log.FireSteps = [wait, Write(1, 1)];
        await f.Runtime.ExecuteAsync(f.Run, Flow(f.Log), 0, () => { }, default);
        await using var scope = await f.Runtime.BeginTaskAsync(f.Run, Resource(), Occurrence(), 1, () => { }, default);
        f.Source.Sessions[0].Hit(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Suspend().WaitAsync(TimeSpan.FromSeconds(1)); release.TrySetResult();
        await Task.Delay(40); Assert.Equal(0, f.Value(1));
        await f.Runtime.PrepareOnlineResumeAsync(f.Run.RunId);
        await Until(() => f.Value(1) == 1);
        await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); await f.Open();
    }

    [Fact]
    public async Task CancellingWhileOnlinePreventsPendingCallbacksFromWritingLater()
    {
        using var f = new Fixture();
        await f.Runtime.ExecuteAsync(f.Run, Flow(f.Log), 0, () => { }, default);
        var scope = await f.Runtime.BeginTaskAsync(f.Run, Resource(), Occurrence(), 1, () => { }, default);
        await f.Suspend();
        var id = Assert.Single(f.Runtime.List()).RuntimeId;
        Assert.True(await f.Runtime.CancelAsync(id));
        await f.Runtime.PrepareOnlineResumeAsync(f.Run.RunId); await f.Open();
        f.Source.Sessions[0].Hit(); await Task.Delay(30);
        Assert.Equal(0, f.Value(1)); Assert.Empty(f.Runtime.List());
        await scope.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CoveredEmptyContinuationRequiresSameLogPathAndEpoch(bool changePath, bool changeEpoch)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flow-online-log-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var other = Path.Combine(dir, "other"); Directory.CreateDirectory(other);
        var first = Path.Combine(dir, "better-genshin-impact.log");
        await File.WriteAllTextAsync(first, $"[{DateTime.Now:HH:mm:ss.fff}] [INF] [Primary:S1:P42:T1] Test\nhistory\n");
        var selected = dir; using var tail = new BgiLogTailService(() => selected);
        var epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 1 };
        var source = new WorkflowLogObservationSource(tail, () => epoch);
        try
        {
            await using var session = await source.ArmLiveAsync("first", "hit", _ => { }, default);
            await File.AppendAllTextAsync(first, $"[{DateTime.Now:HH:mm:ss.fff}] [INF] [Primary:S1:P42:T1] Test\nhit\n");
            var fact = await session.FreezeAsync(default); Assert.Equal("frozen", fact.State);
            if (changePath)
            {
                var nextPath = Path.Combine(other, "better-genshin-impact.log");
                await File.WriteAllTextAsync(nextPath, "");
                // tail有5秒防抖；仅改provider不能证明实际目标已经切换。
                File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(-10)); selected = other;
                Assert.Equal(nextPath, await tail.DrainBoundaryAsync(default));
            }
            if (changeEpoch) epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 2 };
            if (changeEpoch)
            {
                await Assert.ThrowsAsync<WorkflowObservationUnknownException>(() => source.ArmLiveContinuationAsync("next", "hit", _ => { },
                    fact.CoveredSourceInstance, fact.CoveredSourcePath, fact.CoveredEpoch, default));
                return;
            }
            await using var continued = await source.ArmLiveContinuationAsync("next", "hit", _ => { },
                fact.CoveredSourceInstance, fact.CoveredSourcePath, fact.CoveredEpoch, default);
            var result = await continued.FreezeAsync(default);
            Assert.Equal(changePath ? "unknown" : "frozen", result.State); Assert.Empty(result.Hits);
        }
        finally { tail.Dispose(); Directory.Delete(dir, true); }
    }

    private static WorkflowNode Flow(params StartupStep[] steps) => new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Steps = steps.ToList() } };
    private static WorkflowNode Resource() => new() { NodeId = "A", Kind = "resource.configGroup", Ref = new() { Config = "A" } };
    private static WorkflowNodeOccurrence Occurrence() => new("A", 0, 0, 0);
    private static StartupStep Write(int number, int value) => new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Values(number, value) } };
    private static List<int?> Values(int number, int value) => Values((number, value));
    private static List<int?> Values(params (int number, int value)[] pairs)
    { var list = RegisterBank.EmptySelection(); foreach (var (number, value) in pairs) list[number - 1] = value; return list; }
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 150 && !done(); i++) await Task.Delay(10); Assert.True(done()); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "flow-online-runtime-" + Guid.NewGuid().ToString("N"));
        public readonly RegisterBankService Registers;
        public readonly Source Source = new();
        public readonly TaskFlowRuntime Runtime;
        public readonly WorkflowRunRecord Run = new() { RunId = Guid.NewGuid().ToString("N"), WorkflowId = "wf-flow-online" };
        public readonly StartupStep Log;
        public bool Closed;
        public Fixture()
        {
            Registers = new(Path.Combine(_dir, "registers.json"), startTimer: false);
            Runtime = new() { Registers = Registers, ObservationSource = Source, OnlineGateClosed = () => Closed };
            Log = StartupStepKinds.Create(StartupStepKinds.LogTrigger); Log.LogKeyword = "今天执行B线";
            Log.TriggerScope = "task"; Log.TargetNodeId = "A"; Log.WatchRepeat = false;
            Log.FireSteps = [Write(1, 1)]; Log.BeforeAttemptSteps = [Write(1, 0)];
        }
        public Task Suspend() { Closed = true; return Runtime.SuspendForOnlineAsync(); }
        public async Task Open() { await Runtime.ResumeAfterOnlineAsync(); Closed = false; Runtime.OnlineSendOpened(); }
        public int Value(int number) => Registers.GetSnapshot().Entries[number - 1].Value;
        public void Dispose() { Runtime.CloseRunAsync(Run.RunId).GetAwaiter().GetResult(); Registers.Dispose(); if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    }
    private sealed class Source : IWorkflowLiveObservationSource
    {
        public List<Session> Sessions = [];
        public Task<IWorkflowObservationSession> ArmAsync(string id, string keyword, CancellationToken ct) => ArmLiveAsync(id, keyword, _ => { }, ct);
        public Task<IWorkflowObservationSession> ArmLiveAsync(string id, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct)
        { var session = new Session(id, hit); Sessions.Add(session); return Task.FromResult<IWorkflowObservationSession>(session); }
        public sealed class Session(string id, Action<WorkflowObservationHit> hit) : IWorkflowObservationSession
        {
            private bool _closed;
            public void Hit() { if (!_closed) hit(new(DateTime.Now, "今天执行B线", "fixture.log", 1)); }
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
            { _closed = true; return Task.FromResult(new WorkflowObservationFact(id, "frozen", [], null)); }
            public ValueTask DisposeAsync() { _closed = true; return ValueTask.CompletedTask; }
        }
    }
}
