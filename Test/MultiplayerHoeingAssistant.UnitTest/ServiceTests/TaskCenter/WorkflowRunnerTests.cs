using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// WorkflowRunner / Reconciler（R4.5）验收夹具：
/// 顺序链跑通 + 提交意图先行（D11）、过滤节点不提交、失败默认停止且不触发收尾（D10/D12）、
/// 继续策略下拒绝节点聚合 Failed（D6 失败不被成功覆盖）、修订对账节点边界生效（D7）、
/// 显式跳过当前/停止（不触发收尾）、触发器等待不占槽位（无提交无锁、时刻持久化）。
/// 全部走假边界/假前置/假终止 + 手动时钟，无真实 IPC/磁盘外副作用。
/// </summary>
public class WorkflowRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public WorkflowRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wfrun-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        private readonly RunStore _runs;
        public bool SingleNativeSupported { get; set; }
        public List<(string NodeId, string? Config)> Submissions { get; } = new();
        public Queue<string> TerminalScript { get; } = new();
        public Func<WorkflowSubmitRequest, Task>? OnSubmit { get; set; }
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }
        public List<string> IntentStateAtSubmit { get; } = new();

        public FakeBoundary(RunStore runs) => _runs = runs;

        public async Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add((request.Occurrence.NodeId, request.Node.Ref?.Config));
            IntentStateAtSubmit.Add(_runs.Load(request.Run.RunId)!.SubmitIntent.ToString());
            if (OnSubmit is not null) await OnSubmit(request);
            return BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count);
        }

        public Task<string> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => OnAwait is not null ? OnAwait(jobId, ct)
                : Task.FromResult(TerminalScript.Count > 0 ? TerminalScript.Dequeue() : "succeeded");
    }

    private sealed class FakePrerequisite : IWorkflowPrerequisiteAdapter
    {
        public List<string> Executed { get; } = new();
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            Executed.Add(strategy.Kind);
            return Task.FromResult(PrerequisiteResult.ProceedInstance);
        }
    }

    private sealed class FakeTerminal : IWorkflowTerminalExecutor
    {
        public List<string?> Actions { get; } = new();
        public Task ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
        {
            Actions.Add(action.GetString("action"));
            return Task.CompletedTask;
        }
    }

    private static WorkflowNode DragonNode(string id, string config)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig",
            Ref = new WorkflowResourceRef { Config = config, ConfigKey = config + "#k", Revision = "rev-1" } };

    private string SeedFlow(WorkflowDocument doc)
    {
        doc.Activation = new WorkflowActivation { Status = "active" };
        return _workflows.Save(doc, null) + "|" + doc.WorkflowId!;
    }

    private (WorkflowRunner, FakeBoundary, FakeTerminal) MakeRunner(bool continueOnFailure = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null, DateTimeOffset? now = null)
    {
        var boundary = new FakeBoundary(_runs);
        var terminal = new FakeTerminal();
        var runner = new WorkflowRunner(_workflows, _runs, boundary, new FakePrerequisite(), terminal,
            new WorkflowRunnerOptions
            {
                ContinueOnNodeFailure = continueOnFailure,
                Clock = () => now ?? DateTimeOffset.Now,
                DelayAsync = delay ?? ((_, _) => Task.CompletedTask),
            });
        return (runner, boundary, terminal);
    }

    [Fact]
    public async Task SequentialChain_SubmitsInOrder_IntentRecordedBeforeSubmit_TerminalFiresOnce()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "顺序链",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            Terminal = [new WorkflowTerminalAction
            {
                Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("关闭游戏") },
            }],
        });
        var workflowId = seed.Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId));
        Assert.All(boundary.IntentStateAtSubmit, s => Assert.Equal("IntentRecorded", s)); // 意图先行（D11）
        Assert.Equal(["关闭游戏"], terminal.Actions); // 成功边界收尾一次
        Assert.Equal(2, run.NodeOutcomes.Count(o => o.Result == "succeeded"));
        Assert.Equal(SubmitIntentState.Accepted, run.SubmitIntent);
    }

    [Fact]
    public async Task FilteredNode_NeverSubmitted()
    {
        var doc = new WorkflowDocument
        {
            Name = "过滤",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-off", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A" },
                    Strategies = [new WorkflowStrategy
                    {
                        Kind = "condition.weekdays",
                        Params = new Dictionary<string, System.Text.Json.JsonElement>
                        {
                            ["days"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "周一" }),
                            ["dayBoundary"] = System.Text.Json.JsonSerializer.SerializeToElement("localMidnight"),
                        },
                    }] },
                DragonNode("n-on", "配置B"),
            ],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var wednesday = new DateTimeOffset(2026, 9, 16, 6, 0, 0, TimeSpan.FromHours(8));
        var (runner, boundary, _) = MakeRunner(now: wednesday);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-on"], boundary.Submissions.Select(s => s.NodeId)); // 过滤节点不提交
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-off", Result: "skippedFilter" });
    }

    [Fact]
    public async Task NodeFailure_DefaultPolicy_StopsFlow_NoTerminal()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "失败停止",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();
        boundary.TerminalScript.Enqueue("failed");

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Failed, run.State);
        Assert.Equal(["n-1"], boundary.Submissions.Select(s => s.NodeId)); // 后续节点未提交
        Assert.Empty(terminal.Actions); // D10：失败不触发收尾
    }

    [Fact]
    public async Task RejectedNode_ContinuePolicy_AggregatesFailed_NotCoveredByLaterSuccess()
    {
        var doc = new WorkflowDocument
        {
            Name = "聚合",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-single", Kind = "resource.singleTask",
                    Ref = new WorkflowResourceRef { Config = "配置A", TaskId = "t-1" } },
                DragonNode("n-dragon", "配置B"),
            ],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner(continueOnFailure: true);
        boundary.SingleNativeSupported = false;

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-dragon"], boundary.Submissions.Select(s => s.NodeId)); // 拒绝节点未提交、不连坐
        Assert.Equal(WorkflowRunState.Failed, run.State); // B1：拒绝不被后续成功覆盖
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-single", Result: "rejected" });
        Assert.Empty(terminal.Actions);
    }

    [Fact]
    public async Task RevisionChange_TakesEffectAtNodeBoundary()
    {
        var doc = new WorkflowDocument
        {
            Name = "改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        // 第一个节点终态后、第二节点边界前：外部改流（换第二个节点）
        boundary.OnAwait = async (_, _) =>
        {
            var current = _workflows.Load(workflowId);
            var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
            current.Nodes[1] = DragonNode("n-2", "配置C-改后");
            _workflows.Save(current, revision);
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["配置A", "配置C-改后"], boundary.Submissions.Select(s => s.Config)); // 新修订节点边界生效
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_CancelsLeafWait_NodeMarkedSkippedUser_FlowAdvances()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "显式跳过",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();
        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        boundary.OnSubmit = (req) => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-1") // 只有第一个节点的终态等待阻塞（待显式跳过）；后续节点直接成功
            {
                awaitEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Succeeded, run.State); // 显式跳过不算坏结果
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" });
        Assert.Equal(2, boundary.Submissions.Count); // 流程继续推进到第二节点
    }

    [Fact]
    public async Task Stop_DuringTriggerWait_Cancelled_NoTerminal_NoSubmission()
    {
        var future = DateTimeOffset.Now.AddHours(2);
        var doc = new WorkflowDocument
        {
            Name = "定时",
            Nodes = [DragonNode("n-1", "配置A")],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement(future.ToString("HH:mm")),
                    ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
                },
            }],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var gate = new TaskCompletionSource();
        var (runner, boundary, terminal) = MakeRunner(delay: async (_, ct) =>
        {
            gate.TrySetResult(); // 已进入等待（不占槽位）
            await Task.Delay(Timeout.Infinite, ct);
        });

        var task = runner.StartAsync(workflowId);
        await gate.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.Stop);
        var run = await task;

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Empty(boundary.Submissions); // 等待期无提交
        Assert.Empty(terminal.Actions); // D10：手动停止不触发收尾
    }

    [Fact]
    public async Task TriggerWait_PersistsWakeTime_NoSlotHeld_FiresOnTime()
    {
        var now = new DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.FromHours(8));
        var doc = new WorkflowDocument
        {
            Name = "等待证据",
            Nodes = [DragonNode("n-1", "配置A")],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:30"),
                    ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
                },
            }],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        TimeSpan? capturedDelay = null;
        var (runner, boundary, _) = MakeRunner(now: now, delay: (d, _) =>
        {
            capturedDelay = d;
            return Task.CompletedTask; // 手动时钟快进
        });

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(TimeSpan.FromMinutes(90), capturedDelay); // 05:00 → 06:30
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Single(boundary.Submissions);
        Assert.Null(run.Wait); // 等待结束后清空（运行中快照曾持久化 NextTriggerAt）
    }
}
