using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>时段附加选项：等到点执行、等待期间关闭游戏、严格按结束时间结束。</summary>
public sealed class WorkflowTimeWindowWaitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "node-time-window-wait-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("17:00", "18:00", "15:00:00", "17:00")]
    [InlineData("17:00", "18:00", "17:30:00", null)] // 时段内不等
    [InlineData("17:00", "18:00", "18:30:00", null)] // 当天已错过，按原规则跳过
    [InlineData("22:00", "06:00", "15:00:00", "22:00")]
    [InlineData("22:00", "06:00", "07:00:00", "22:00")]
    [InlineData("07:30", "07:30", "05:00:00", null)] // 全天
    public void StartWaitTargetOnlyBeforeTodaysStart(string start, string end, string at, string? expected)
    {
        var window = new WorkflowTimeWindow { Start = start, End = end, WaitForStart = true };
        Assert.Equal(expected, WorkflowTimeWindowPolicy.StartWaitTarget(true, window, At(at))?.ToString("HH:mm"));
        Assert.Null(WorkflowTimeWindowPolicy.StartWaitTarget(false, window, At(at)));
        window.WaitForStart = false;
        Assert.Null(WorkflowTimeWindowPolicy.StartWaitTarget(true, window, At(at)));
    }

    [Fact]
    public void CurrentWindowEndHandlesMidnight()
    {
        var w = new WorkflowTimeWindow { Start = "22:00", End = "06:00" };
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T06:00:00+08:00"), WorkflowTimeWindowPolicy.CurrentWindowEnd(w, At("23:00:00")));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T06:00:00+08:00"), WorkflowTimeWindowPolicy.CurrentWindowEnd(w, At("01:00:00")));
        Assert.Null(WorkflowTimeWindowPolicy.CurrentWindowEnd(w, At("12:00:00")));
    }

    [Fact]
    public void OldDocumentsReadWithoutOptionsAndDefaultsAreNotWritten()
    {
        var json = JsonSerializer.Serialize(new WorkflowTimeWindow { Start = "10:00", End = "11:00" });
        Assert.DoesNotContain("waitForStart", json);
        Assert.DoesNotContain("stopAtEnd", json);
        Assert.DoesNotContain("closeGameWhileWaiting", json);
        var parsed = JsonSerializer.Deserialize<WorkflowTimeWindow>("{\"start\":\"10:00\",\"end\":\"11:00\",\"waitForStart\":true,\"stopAtEnd\":true}")!;
        Assert.True(parsed.WaitForStart);
        Assert.True(parsed.StopAtEnd);
        Assert.Equal("等到点执行 · 到点结束", WorkflowTimeWindowPolicy.OptionsDisplay(parsed));
    }

    [Fact]
    public async Task EarlyArrivalWaitsUntilStartInsteadOfSkipping()
    {
        var clock = new Clock(At("15:00:00"));
        var node = Node("a", "17:00", "18:00");
        node.TimeWindow!.WaitForStart = true;
        var waits = new List<(WorkflowRunState State, string? Kind, DateTimeOffset? At)>();
        RunStore? runs = null;
        var fixture = CreateRunner(Document(node), clock, onDelay: () =>
        {
            var r = runs!.List().Single();
            waits.Add((r.State, r.Wait?.Kind, r.Wait?.NextTriggerAt));
        });
        runs = fixture.Runs;
        var result = await fixture.Runner.StartAsync(fixture.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "a" }, fixture.Boundary.Sent);
        Assert.Equal("succeeded", Assert.Single(result.NodeOutcomes).Result);
        Assert.Contains(waits, w => w.State == WorkflowRunState.Waiting && w.Kind == WorkflowRunner.TimeWindowStartWaitKind
            && w.At == At("17:00:00"));
        Assert.Equal(At("17:00:00"), Assert.Single(fixture.Boundary.SentAt));
    }

    [Fact]
    public async Task WithoutWaitOptionEarlyArrivalStillSkips()
    {
        var clock = new Clock(At("15:00:00"));
        var f = CreateRunner(Document(Node("a", "17:00", "18:00")), clock);
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Boundary.Sent);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task MissedStartTodayStillSkipsEvenWithWaitOption()
    {
        var clock = new Clock(At("18:30:00"));
        var node = Node("a", "17:00", "18:00");
        node.TimeWindow!.WaitForStart = true;
        var f = CreateRunner(Document(node), clock);
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Boundary.Sent);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task DisabledRowDoesNotWait()
    {
        var clock = new Clock(At("15:00:00"));
        var node = Node("a", "17:00", "18:00");
        node.TimeWindow!.WaitForStart = true;
        node.ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) };
        var f = CreateRunner(Document(node), clock);
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Boundary.Sent);
        Assert.Equal(At("15:00:00"), clock.Now);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task CloseGameOnlyWhenOptionChecked(bool closeGame, int expectedCloses)
    {
        var clock = new Clock(At("15:00:00"));
        var node = Node("a", "17:00", "18:00");
        node.TimeWindow!.WaitForStart = true;
        node.TimeWindow.CloseGameWhileWaiting = closeGame;
        var closes = 0;
        var f = CreateRunner(Document(node), clock, closeGame: _ =>
        {
            closes++;
            return Task.FromResult((true, "已关闭游戏"));
        });
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedCloses, closes);
        Assert.Equal(new[] { "a" }, f.Boundary.Sent);
        Assert.Equal("succeeded", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task CloseGameFailureStillWaitsAndRuns()
    {
        var clock = new Clock(At("15:00:00"));
        var node = Node("a", "17:00", "18:00");
        node.TimeWindow!.WaitForStart = true;
        node.TimeWindow.CloseGameWhileWaiting = true;
        var f = CreateRunner(Document(node), clock, closeGame: _ => throw new InvalidOperationException("通道断开"));
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "a" }, f.Boundary.Sent);
        Assert.Equal(At("17:00:00"), Assert.Single(f.Boundary.SentAt));
        Assert.Equal("succeeded", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public void WaitDisplayDescribesClosedGame()
    {
        var wait = new WaitStateRecord
        {
            Kind = WorkflowRunner.TimeWindowStartWaitKind, NextTriggerAt = At("17:00:00"),
            ExtensionData = new() { ["gameClosed"] = JsonSerializer.SerializeToElement(true) },
        };
        Assert.Equal("等待时段开始（17:00 执行） · 已关闭游戏", WorkflowTimeWindowWaitDisplay.Describe(wait));
        Assert.Null(WorkflowTimeWindowWaitDisplay.Describe(new WaitStateRecord { Kind = "trigger.time" }));
    }

    [Fact]
    public async Task StopAtEndCancelsRunningTaskAndContinuesPlan()
    {
        var clock = new Clock(At("13:30:00"));
        var node = Node("a", "13:00", "14:00");
        node.TimeWindow!.StopAtEnd = true;
        var f = CreateRunner(Document(node, Node("b", "00:00", "00:00")), clock);
        f.Boundary.HoldUntilCancelled = "a";
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "a", "b" }, f.Boundary.Sent);
        Assert.Equal(new[] { "job-1" }, f.Boundary.Cancelled);
        var a = result.NodeOutcomes.First(o => o.NodeId == "a");
        Assert.Equal("skippedFilter", a.Result);
        Assert.Contains("时段结束", a.Reason);
        Assert.Equal("succeeded", result.NodeOutcomes.First(o => o.NodeId == "b").Result);
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
    }

    [Fact]
    public async Task WithoutStopAtEndRunningTaskIsNotCancelled()
    {
        var clock = new Clock(At("13:30:00"));
        var f = CreateRunner(Document(Node("a", "13:00", "14:00")), clock);
        f.Boundary.AfterSubmit = () => clock.Now = At("14:30:00");
        var result = await f.Runner.StartAsync(f.Store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Boundary.Cancelled);
        Assert.Equal("succeeded", Assert.Single(result.NodeOutcomes).Result);
    }

    private (WorkflowRunner Runner, Boundary Boundary, WorkflowStore Store, RunStore Runs) CreateRunner(WorkflowDocument document,
        Clock clock, Action? onDelay = null, Func<CancellationToken, Task<(bool, string)>>? closeGame = null)
    {
        var store = new WorkflowStore(Path.Combine(_root, "flows"));
        var runs = new RunStore(Path.Combine(_root, "runs"));
        store.Save(document, null);
        var boundary = new Boundary(clock);
        var options = new WorkflowRunnerOptions
        {
            Clock = () => clock.Now,
            // 手动时钟：每次延时直接把时钟拨过去，不真实等待。
            DelayAsync = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                onDelay?.Invoke();
                clock.Now += delay;
                return Task.CompletedTask;
            },
            CloseGameForTimeWindowWaitAsync = closeGame,
            SkipConfirmTimeout = TimeSpan.FromSeconds(5),
        };
        return (new(store, runs, boundary, new Preparation(), new Terminal(), options), boundary, store, runs);
    }

    private static WorkflowDocument Document(params WorkflowNode[] nodes)
        => new() { Name = "等待时段测试", NodeTimeWindowEnabled = true, Nodes = nodes.ToList() };

    private static WorkflowNode Node(string id, string start, string end)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig", Ref = new() { Config = id, Revision = "revision" },
            TimeWindow = new() { Start = start, End = end } };

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-25T" + time + "+08:00");

    private sealed class Clock(DateTimeOffset now)
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = now;
        public DateTimeOffset Now { get { lock (_gate) return _now; } set { lock (_gate) _now = value; } }
    }

    private sealed class Boundary(Clock clock) : IWorkflowExecutionBoundary
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _heldJob;
        public bool SingleNativeSupported => true;
        public List<string> Sent { get; } = [];
        public List<DateTimeOffset> SentAt { get; } = [];
        public List<string> Cancelled { get; } = [];
        public string? HoldUntilCancelled { get; set; }
        public Action? AfterSubmit { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Sent.Add(request.Occurrence.NodeId);
            SentAt.Add(clock.Now);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            var job = "job-" + Sent.Count;
            if (request.Occurrence.NodeId == HoldUntilCancelled) _heldJob = job;
            AfterSubmit?.Invoke();
            return Task.FromResult(BoundarySubmitResult.AcceptedWith(job));
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (jobId != _heldJob) return BoundaryTerminalResult.Observed("succeeded");
            await _cancelled.Task.WaitAsync(ct);
            return BoundaryTerminalResult.Observed("cancelled");
        }

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            lock (Cancelled) Cancelled.Add(jobId);
            if (jobId == _heldJob) _cancelled.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class Preparation : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => throw new InvalidOperationException("本夹具不包含收尾动作。");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
