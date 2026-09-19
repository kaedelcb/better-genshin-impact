using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R4.6 Batch 3c（助手侧）验收夹具：E2-8' 前置意图/对账/确认链、E3' 收尾意图纪律（pending/submitted/unknown）、
/// I1 能力协商预检、I2 词表分离（skipped 原始词 vs skippedUser/skippedFilter 归一化词）、B1 WireRunId。
/// 全假边界/假前置/假收尾，无真实 IPC/磁盘外副作用。
/// </summary>
public class R46Batch3cRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public R46Batch3cRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf3c-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported { get; set; }
        public List<string> Submissions { get; } = new();
        public List<bool> SuppressFlags { get; } = new(); // 任务中心提交固定 suppress=true（B6/E4'）的见证
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }
        public List<string> CancelRequests { get; } = new();
        public Queue<string> TerminalScript { get; } = new();

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add(request.Occurrence.NodeId);
            SuppressFlags.Add(request.SuppressConfigCompletionAction);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count));
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            var word = OnAwait is not null ? await OnAwait(jobId, ct) : "succeeded";
            return BoundaryTerminalResult.Observed(word);
        }

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelRequests.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrerequisite : IWorkflowPrerequisiteAdapter
    {
        public List<string> Executed { get; } = new();
        public Func<WorkflowStrategy, CancellationToken, Task<PrerequisiteResult>>? OnExecute { get; set; }
        public Func<PrerequisiteActionRecord, CancellationToken, Task<PrerequisiteResult>>? OnReconcile { get; set; }
        public Func<PrerequisiteActionRecord, CancellationToken, Task<PrerequisiteResult>>? OnConfirmCancel { get; set; }

        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            Executed.Add(strategy.Kind);
            return OnExecute is not null ? OnExecute(strategy, ct) : Task.FromResult(PrerequisiteResult.ProceedInstance);
        }

        public Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => OnReconcile is not null ? OnReconcile(record, ct)
                : Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "假适配器默认对账未决", record.JobId));

        public Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => OnConfirmCancel is not null ? OnConfirmCancel(record, ct)
                : Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, null, record.JobId));
    }

    private sealed class FakeTerminal : IWorkflowTerminalExecutor
    {
        public List<string?> Actions { get; } = new();
        public Func<WorkflowTerminalAction, WorkflowRunRecord, Task>? OnExecute { get; set; }
        public TerminalExecutionResult Result { get; set; } = TerminalExecutionResult.Executed("job-terminal");

        public async Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
        {
            Actions.Add(action.GetString("action"));
            if (OnExecute is not null) await OnExecute(action, run);
            return Result;
        }
    }

    private static WorkflowNode DragonNode(string id, string config)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig",
            Ref = new WorkflowResourceRef { Config = config, ConfigKey = config + "#k", Revision = "rev-1" } };

    private static WorkflowStrategy AccountStrategy(string uid = "123456789")
        => new()
        {
            Kind = "prerequisite.account",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(uid) },
        };

    private string SeedFlow(WorkflowDocument doc)
    {
        doc.Activation = new WorkflowActivation { Status = "active" };
        return _workflows.Save(doc, null) + "|" + doc.WorkflowId!;
    }

    private (WorkflowRunner, FakeBoundary, FakePrerequisite, FakeTerminal) MakeRunner(
        FakePrerequisite? prerequisite = null, FakeTerminal? terminal = null, bool continueOnFailure = false)
    {
        var boundary = new FakeBoundary();
        var prereq = prerequisite ?? new FakePrerequisite();
        var term = terminal ?? new FakeTerminal();
        var runner = new WorkflowRunner(_workflows, _runs, boundary, prereq, term,
            new WorkflowRunnerOptions
            {
                ContinueOnNodeFailure = continueOnFailure,
                DelayAsync = (_, _) => Task.CompletedTask,
                SkipConfirmTimeout = TimeSpan.FromMilliseconds(300),
            });
        return (runner, boundary, prereq, term);
    }

    // ── B1：WireRunId 线协议身份 ─────────────────────────────────────────

    [Fact]
    public void CreateRun_AssignsWireRunId_ValidGuid()
    {
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        Assert.True(Guid.TryParse(rec.WireRunId, out var g) && g != Guid.Empty); // BGI ReadIdentity 严格 Guid
        var loaded = _runs.Load(rec.RunId)!;
        Assert.Equal(rec.WireRunId, loaded.WireRunId); // 持久化往返稳定
        Assert.NotEqual(rec.WireRunId, _runs.CreateRun("wf-aaaaaaaa", "rev-1").WireRunId); // 每 run 独立
    }

    // ── E2-8'：前置意图/对账 ─────────────────────────────────────────────

    [Fact]
    public async Task Prerequisite_SameKeySucceeded_ResumeSkipsExecute_NoResend()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "同键成功不重发",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord // 崩溃前已成功的前置事实
        {
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1, StrategyIndex = 0,
            Kind = "prerequisite.account", State = PrerequisiteActionState.Succeeded,
            AccountKey = RunStore.DeriveAccountKey("123456789"), // 四轮阻断 3：完整身份（含账号标识哈希）
            JobId = "job-p1", IdempotencyKey = "idem-x",
        });
        _runs.Update(rec);
        Assert.Single(_runs.RecoverOnStart()); // Interrupted

        var prereq = new FakePrerequisite();
        var (runner, boundary, _, _) = MakeRunner(prerequisite: prereq);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Empty(prereq.Executed); // 同键成功事实：不重发
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(x => x)); // 节点主体照常执行
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task Prerequisite_InFlightReconcileUnknown_RunUnknown_NoAdvance()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "在飞对账未决",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord // 崩溃窗口：已受理在飞
        {
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1, StrategyIndex = 0,
            Kind = "prerequisite.account", State = PrerequisiteActionState.Submitted,
            AccountKey = RunStore.DeriveAccountKey("123456789"),
            JobId = "job-p9", IdempotencyKey = "idem-y",
        });
        _runs.Update(rec);
        Assert.Single(_runs.RecoverOnStart());

        var prereq = new FakePrerequisite
        {
            OnReconcile = (_, _) => Task.FromResult(
                new PrerequisiteResult(PrerequisiteStatus.Unknown, "作业不存在于当前纪元（not_found）", "job-p9")),
        };
        var (runner, boundary, _, terminal) = MakeRunner(prerequisite: prereq);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(WorkflowRunState.Unknown, run.State); // B3：不推进、不触发收尾、禁止自动重跑
        Assert.Empty(boundary.Submissions);
        Assert.Empty(terminal.Actions);
        Assert.Empty(prereq.Executed); // 对账未决不盲目重发
        Assert.Equal(PrerequisiteActionState.Unknown, _runs.Load(rec.RunId)!.PrerequisiteActions[0].State);
    }

    [Fact]
    public async Task Prerequisite_SkipCurrent_ConfirmedCancel_RecordsSkippedUser_Advances()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "前置期跳过确认",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|')[1];

        var entered = new TaskCompletionSource();
        string? runId = null;
        var prereq = new FakePrerequisite
        {
            OnExecute = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // 在飞前置：叶子取消 → OCE → 确认链
                return PrerequisiteResult.ProceedInstance;
            },
            OnConfirmCancel = (_, _) => Task.FromResult(
                new PrerequisiteResult(PrerequisiteStatus.Cancelled, "远端取消已确认", "job-p1")),
        };
        var (runner, boundary, _, _) = MakeRunner(prerequisite: prereq);

        // SkipCurrent 需要 runId：从 RunStore 最新记录取（前置进入后 run 已存在）
        var task = runner.StartAsync(workflowId);
        await entered.Task;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" }); // 确认链 Cancelled
        Assert.Equal(["n-2"], boundary.Submissions.Select(x => x)); // n-1 主体未提交，流程推进
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(PrerequisiteActionState.Cancelled, _runs.Load(runId!)!.PrerequisiteActions[0].State);
    }

    [Fact]
    public async Task Prerequisite_SkipCurrent_ConfirmTimeout_RunUnknown_NoResend()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "前置期跳过未确认",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|')[1];

        var entered = new TaskCompletionSource();
        var prereq = new FakePrerequisite
        {
            OnExecute = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return PrerequisiteResult.ProceedInstance;
            },
            OnConfirmCancel = (_, _) => Task.FromResult(
                new PrerequisiteResult(PrerequisiteStatus.Unknown, "取消后远端终态未确认", "job-p1")),
        };
        var (runner, boundary, _, terminal) = MakeRunner(prerequisite: prereq);

        var task = runner.StartAsync(workflowId);
        await entered.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Unknown, run.State); // 不确认不猜成功
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "cancelUnconfirmed" });
        Assert.Empty(boundary.Submissions);
        Assert.Empty(terminal.Actions); // Unknown 不触发收尾
        Assert.Equal(PrerequisiteActionState.Unknown, _runs.Load(runId!)!.PrerequisiteActions[0].State);
    }

    // ── I2：词表分离 ─────────────────────────────────────────────────────

    [Fact]
    public async Task MapTerminal_RawSkipped_MapsSkippedFilter_TerminalStillFires()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "远端正常跳过",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var (runner, boundary, _, terminal) = MakeRunner();
        boundary.OnAwait = (_, _) => Task.FromResult("skipped"); // 原始线协议词

        var run = await runner.StartAsync(workflowId);

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedFilter" }); // 原始词如实映射
        Assert.Equal(WorkflowRunState.Succeeded, run.State); // D15：不阻断成功边界
        Assert.Single(terminal.Actions); // 收尾照常触发
    }

    [Fact]
    public async Task ConfirmSkip_RemoteSkippedRace_RecordsSkippedFilter_NotSkippedUser()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过竞态",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        var job1Calls = 0;
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId != "job-1") return "succeeded";
            if (Interlocked.Increment(ref job1Calls) == 1)
            {
                awaitEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // 首次等待：叶子取消进入确认
            }
            return "skipped"; // I2：确认阶段观察到远端正常跳过（竞态）
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedFilter" }); // 竞态如实优先
        Assert.DoesNotContain(run.NodeOutcomes, o => o.Result == "skippedUser");
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    // ── E3'：收尾意图纪律 ────────────────────────────────────────────────

    [Fact]
    public async Task Terminal_OceAfterSubmitted_RunCancelled_IntentRetainedAsUnknown()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾提交后取消",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var term = new FakeTerminal
        {
            OnExecute = (_, run) =>
            {
                run.PendingCompletion!.State = "submitted"; // 生产执行器受理即持久化 submitted
                run.PendingCompletion!.JobId = "job-t9";
                throw new OperationCanceledException(); // B8：取消打断收尾段
            },
        };
        var (runner, _, _, _) = MakeRunner(terminal: term);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        var loaded = _runs.Load(run.RunId)!;
        Assert.NotNull(loaded.PendingCompletion); // submitted 事实保留
        Assert.Equal("unknown", loaded.PendingCompletion!.State); // 标 unknown，禁止补发
        Assert.Equal("job-t9", loaded.PendingCompletion!.JobId);
    }

    [Fact]
    public async Task Terminal_OceBeforeSubmit_RunCancelled_PendingIntentCleared()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾未提交取消",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var term = new FakeTerminal
        {
            OnExecute = (_, _) => throw new OperationCanceledException(), // pending 即取消（未提交无副作用）
        };
        var (runner, _, _, _) = MakeRunner(terminal: term);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Null(_runs.Load(run.RunId)!.PendingCompletion); // pending 清除（D10：取消不清算为可执行收尾）
    }

    [Fact]
    public async Task Terminal_Unknown_RunFailed_IntentUnknown_NeverRefired()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾结果不可考",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var term = new FakeTerminal { Result = TerminalExecutionResult.UnknownWith("job-t1", "等待终态超预算") };
        var (runner, _, _, _) = MakeRunner(terminal: term);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Failed, run.State); // 不记成功
        var loaded = _runs.Load(run.RunId)!;
        Assert.Equal("unknown", loaded.PendingCompletion!.State);
        Assert.Equal("job-t1", loaded.PendingCompletion!.JobId);
        Assert.Contains("禁止自动补发", loaded.Note);
        Assert.Single(term.Actions); // 禁止补发：只发一次
    }

    [Fact]
    public async Task Terminal_Rejected_RunFailed_IntentRetained()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾被拒绝",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var term = new FakeTerminal { Result = TerminalExecutionResult.RejectedWith("queue_full") };
        var (runner, _, _, _) = MakeRunner(terminal: term);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Failed, run.State);
        var loaded = _runs.Load(run.RunId)!;
        Assert.Equal("pending", loaded.PendingCompletion!.State); // 未受理，意图保留待人工处置
        Assert.Contains("被拒绝", loaded.Note);
    }

    // ── E3'/I1：Planner 预检 ─────────────────────────────────────────────

    [Fact]
    public void Preflight_MultipleTerminalActions_Blocking()
    {
        var doc = new WorkflowDocument
        {
            Name = "多收尾",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal =
            [
                new WorkflowTerminalAction { Kind = "terminal.completionAction" },
                new WorkflowTerminalAction { Kind = "terminal.completionAction" },
            ],
        };
        var plan = new WorkflowPlan(doc);

        var preflight = plan.Preflight(true, WorkflowKindCatalog.StrategyKinds, WorkflowKindCatalog.TerminalKinds);

        Assert.False(preflight.Executable); // E3'：多收尾无逐动作水位，响亮拒绝
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("至多一个"));
    }

    [Fact]
    public void Preflight_MissingPrerequisiteCapability_Blocking()
    {
        var doc = new WorkflowDocument
        {
            Name = "能力缺失",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
            ],
        };
        var plan = new WorkflowPlan(doc);

        var blocked = plan.Preflight(true, new HashSet<string>(), WorkflowKindCatalog.TerminalKinds);
        Assert.False(blocked.Executable); // I1：缺 capability 即不支持
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("prerequisite.account"));

        var allowed = plan.Preflight(true, WorkflowKindCatalog.StrategyKinds, WorkflowKindCatalog.TerminalKinds);
        Assert.True(allowed.Executable); // 能力齐备放行

        var seam = plan.Preflight(true); // null = 测试接缝不检查
        Assert.True(seam.Executable);
    }

    [Fact]
    public void Preflight_MissingTerminalCapability_Blocking()
    {
        var doc = new WorkflowDocument
        {
            Name = "收尾能力缺失",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        };
        var plan = new WorkflowPlan(doc);

        var blocked = plan.Preflight(true, WorkflowKindCatalog.StrategyKinds, new HashSet<string>());
        Assert.False(blocked.Executable);
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("terminal.completionAction"));
    }

    [Fact]
    public void Preflight_RedeemWithoutUidSource_Blocking_SiblingAccountResolves()
    {
        var orphan = new WorkflowDocument
        {
            Name = "兑换无身份",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [new WorkflowStrategy { Kind = "prerequisite.redeemCode" }] },
            ],
        };
        var blocked = new WorkflowPlan(orphan).Preflight(true);
        Assert.False(blocked.Executable); // 严格合同：无 uid 来源响亮拒绝
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("redeemCode"));

        var withSibling = new WorkflowDocument
        {
            Name = "兑换有身份",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy(), new WorkflowStrategy { Kind = "prerequisite.redeemCode" }] },
            ],
        };
        Assert.True(new WorkflowPlan(withSibling).Preflight(true).Executable); // 同节点账号策略提供身份
    }

    // ── 四轮处置（ASTRA R4.6 阶段审核）故障窗口 ──────────────────────────

    [Fact]
    public async Task Prerequisite_FailedRecord_NoAutoRetry_NeedsHumanDecision()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "既有失败不重试",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord // 既有失败终态事实
        {
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1, StrategyIndex = 0,
            Kind = "prerequisite.account", State = PrerequisiteActionState.Failed,
            AccountKey = RunStore.DeriveAccountKey("123456789"),
            Reason = "account_mismatch", IdempotencyKey = "idem-f",
        });
        _runs.Update(rec);
        Assert.Single(_runs.RecoverOnStart());

        var prereq = new FakePrerequisite();
        var (runner, boundary, _, _) = MakeRunner(prerequisite: prereq);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Empty(prereq.Executed); // I4/四轮阻断 3：不自动创建第二次执行
        Assert.Empty(boundary.Submissions);
        Assert.Equal(WorkflowRunState.Failed, run.State);
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "failed" });
    }

    [Fact]
    public async Task Prerequisite_UnknownResult_CursorHeldAtCurrentNode()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "前置未知不推进",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|')[1];
        var prereq = new FakePrerequisite
        {
            OnExecute = (_, _) => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "对端不可考")),
        };
        var (runner, boundary, _, _) = MakeRunner(prerequisite: prereq);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Unknown, run.State);
        var loaded = _runs.Load(run.RunId)!;
        Assert.Equal("n-1", loaded.Cursor!.NodeId); // 四轮阻断 5：游标不推进（恢复回到本节点对账）
        Assert.False(loaded.TailReached);
        Assert.Empty(boundary.Submissions); // 节点主体未提交
    }

    [Fact]
    public async Task RecoverOnStart_PrerequisiteInFlight_InterruptedThenResumeReconciles()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "前置在飞恢复对账",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [AccountStrategy()] },
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord // 崩溃窗口：前置已受理未终态
        {
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1, StrategyIndex = 0,
            Kind = "prerequisite.account", State = PrerequisiteActionState.Submitted,
            AccountKey = RunStore.DeriveAccountKey("123456789"),
            JobId = "job-p7", IdempotencyKey = "idem-z", SendAttempted = true,
        });
        _runs.Update(rec);

        // 四轮阻断 5 处置口径：游标恒在本节点（CommitOutcome 对未决结果不推进），前置在飞 = Interrupted 可恢复，
        // 恢复即回到本节点对账——而非恢复扫描直接标 Unknown（那会把对账路径堵死）
        var recovered = Assert.Single(_runs.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);

        var reconcileCalls = 0;
        var prereq = new FakePrerequisite
        {
            OnReconcile = (_, _) =>
            {
                reconcileCalls++;
                return Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Proceed, null, "job-p7"));
            },
        };
        var (runner, boundary, _, _) = MakeRunner(prerequisite: prereq);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(1, reconcileCalls); // 恢复后先对账
        Assert.Empty(prereq.Executed); // 对账放行不重发
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(x => x));
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(PrerequisiteActionState.Succeeded, _runs.Load(rec.RunId)!.PrerequisiteActions[0].State);
    }

    [Fact]
    public async Task Terminal_OceDuringDispatch_IntentRetainedAsUnknown()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾发送窗口取消",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var term = new FakeTerminal
        {
            OnExecute = (_, run) =>
            {
                run.PendingCompletion!.State = "dispatching"; // 生产执行器发送前持久化（可能已发送）
                throw new OperationCanceledException();
            },
        };
        var (runner, _, _, _) = MakeRunner(terminal: term);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        var loaded = _runs.Load(run.RunId)!;
        Assert.Equal("unknown", loaded.PendingCompletion!.State); // 四轮阻断 2：dispatching 不当 pending 清除
    }

    [Fact]
    public async Task Submit_SuppressConfigCompletionAction_AlwaysTrue()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "suppress 固定",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            // 注意：不声明 Execution.SuppressConfigCompletionAction
        }).Split('|')[1];
        var (runner, boundary, _, _) = MakeRunner();

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal([true, true], boundary.SuppressFlags.Select(x => x)); // B6/E4'：与流程声明无关，固定 true
    }

    [Fact]
    public void Preflight_SuppressCapabilityMissing_BlockingForResourceNodes()
    {
        var dragon = new WorkflowDocument
        {
            Name = "龙引用",
            Nodes = [DragonNode("n-1", "配置A")],
        };
        var blocked = new WorkflowPlan(dragon).Preflight(true, WorkflowKindCatalog.StrategyKinds,
            WorkflowKindCatalog.TerminalKinds, suppressConfigCompletionSupported: false);
        Assert.False(blocked.Executable); // 四轮阻断 7：缺 suppress 能力的整龙流程响亮拒绝
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("suppressConfigCompletionAction"));

        var ok = new WorkflowPlan(dragon).Preflight(true, WorkflowKindCatalog.StrategyKinds,
            WorkflowKindCatalog.TerminalKinds, suppressConfigCompletionSupported: true);
        Assert.True(ok.Executable);

        var singleOnly = new WorkflowDocument // 单项节点不吃 suppress 合同（D10 抑制针对配置收尾）
        {
            Name = "纯单项",
            Nodes = [new WorkflowNode { NodeId = "t-1", Kind = "resource.singleTask",
                Ref = new WorkflowResourceRef { TaskId = "task-x" } }],
        };
        var seam = new WorkflowPlan(singleOnly).Preflight(true, WorkflowKindCatalog.StrategyKinds,
            WorkflowKindCatalog.TerminalKinds, suppressConfigCompletionSupported: false);
        Assert.True(seam.Executable);
    }

    [Fact]
    public async Task SkipCurrent_Unconfirmed_ObservedTerminalStaysNull_CursorHeld()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过未确认词表",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        boundary.OnAwait = async (_, ct) =>
        {
            awaitEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 首次等待与确认等待均不返回（确认超时）
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Unknown, run.State);
        var loaded = _runs.Load(run.RunId)!;
        Assert.Null(loaded.CurrentSubmission!.ObservedTerminal); // I2/四轮阻断 5：未观察到原始词绝不落 ObservedTerminal
        Assert.Equal("n-1", loaded.Cursor!.NodeId); // 游标不推进
        Assert.Single(boundary.Submissions);
    }
}
