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
            IntentStateAtSubmit.Add(_runs.Load(request.Run.RunId)!.CurrentSubmission?.Intent.ToString() ?? "None");
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
        public Func<WorkflowRunRecord, Task>? OnExecute { get; set; }
        public async Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            Executed.Add(strategy.Kind);
            if (OnExecute is not null) await OnExecute(run);
            return PrerequisiteResult.ProceedInstance;
        }
    }

    private sealed class FakeTerminal : IWorkflowTerminalExecutor
    {
        public List<string?> Actions { get; } = new();
        public Func<WorkflowTerminalAction, WorkflowRunRecord, Task>? OnExecute { get; set; }
        public async Task ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
        {
            Actions.Add(action.GetString("action"));
            if (OnExecute is not null) await OnExecute(action, run);
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
        Func<TimeSpan, CancellationToken, Task>? delay = null, DateTimeOffset? now = null,
        FakePrerequisite? prerequisite = null, TimeSpan? skipConfirmTimeout = null)
    {
        var boundary = new FakeBoundary(_runs);
        var terminal = new FakeTerminal();
        var runner = new WorkflowRunner(_workflows, _runs, boundary, prerequisite ?? new FakePrerequisite(), terminal,
            new WorkflowRunnerOptions
            {
                ContinueOnNodeFailure = continueOnFailure,
                Clock = () => now ?? DateTimeOffset.Now,
                DelayAsync = delay ?? ((_, _) => Task.CompletedTask),
                SkipConfirmTimeout = skipConfirmTimeout ?? TimeSpan.FromSeconds(15),
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
        Assert.Equal(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent);
        Assert.Equal("succeeded", run.CurrentSubmission!.ObservedTerminal); // B3：终态与结果同写
        Assert.StartsWith("idem-", run.CurrentSubmission!.Key); // B2：确定性派生键
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
        var job1Calls = 0;
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-1") // 第一节点：首次等待阻塞（待显式跳过）；B4 确认调用返回远端已取消
            {
                if (Interlocked.Increment(ref job1Calls) == 1)
                {
                    awaitEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return "cancelled"; // 确认阶段：远端取消已确认
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
    // ======== ASTRA 二轮处置夹具（B1 修订寻址 / B3 恢复 / B4 跳过确认 / B5 收尾 / B9 轮次等待） ========

    [Fact]
    public async Task RevisionReload_NodeInserted_RecomputesSuccessorByIdentity_NoMisaddress()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "插入改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            // A 执行期间：外部在 A 与 B 之间插入新节点 X（B1：旧序列坐标寻址会错位/越界）；仅此次改流
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.Insert(1, DragonNode("n-x", "配置X-插入"));
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        // 新修订按稳定身份重算后继：A → X（插入节点被执行）→ B，不错位、不重跑
        Assert.Equal(["n-1", "n-x", "n-2"], boundary.Submissions.Select(s => s.NodeId));
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task RevisionReload_NodeDeleted_SkipsDeleted_NoReplay()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "删除改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B"), DragonNode("n-3", "配置C")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.RemoveAt(1); // 删除 B：新定义 [A, C]
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-1", "n-3"], boundary.Submissions.Select(s => s.NodeId)); // B 被删除不再执行；C 不以旧坐标错位执行
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task RevisionReload_TailAppended_ChainTailReconciles_ExecutesAppendedNode()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "链尾追加",
            Nodes = [DragonNode("n-1", "配置A")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.Add(DragonNode("n-2", "配置B-追加")); // 链尾追加（B1：链尾也是节点边界）
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId)); // 追加节点被执行
        Assert.True(run.TailReached); // 最终链尾落盘（游标 null 消歧）
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task Resume_InterruptedRun_ContinuesFromCursor_NoReplay_HistoricalFailureAggregates()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "恢复",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B"), DragonNode("n-3", "配置C")],
        }).Split('|');
        var revision = seed[0];
        var workflowId = seed[1];

        // 模拟崩溃现场：A 已失败（历史结果）、游标指向 B、无在飞提交
        var rec = _runs.CreateRun(workflowId, revision);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "n-1", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "failed", Reason = "模拟崩溃前失败" });
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-2", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        _runs.Update(rec);

        var recovered = Assert.Single(_runs.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);

        var (runner, boundary, _) = MakeRunner(continueOnFailure: true);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(["n-2", "n-3"], boundary.Submissions.Select(s => s.NodeId)); // 不重放已完成节点
        Assert.Equal(WorkflowRunState.Failed, run.State); // B3：历史失败经 NodeOutcomes 重建，不被后续成功覆盖
    }

    [Fact]
    public async Task Resume_UnknownRun_Rejected_NeverAutoResumed()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "未知拒恢复",
            Nodes = [DragonNode("n-1", "配置A")],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        _runs.RecordIntent(rec, new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1),
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
        });
        Assert.Single(_runs.RecoverOnStart()); // 提交在飞 → Unknown

        var (runner, _, _) = MakeRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ResumeAsync(rec.RunId)); // 禁止自动恢复
    }

    [Fact]
    public async Task CrashWindow3_SubmissionTerminalUncommitted_RecoveryBackfillsOutcome_NoRerun()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "补记",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|');
        // 遗留现场：n-1 提交终态已观察但结果未提交（正常路径单次写已消除此窗口，仅遗留/手工记录可达）
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        _runs.RecordIntent(rec, new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1),
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
        });
        rec.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
        rec.CurrentSubmission!.JobId = "job-legacy";
        rec.CurrentSubmission!.ObservedTerminal = "succeeded";
        _runs.Update(rec);
        Assert.Single(_runs.RecoverOnStart()); // Interrupted（有终态事实）

        var (runner, boundary, _) = MakeRunner();
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(["n-2"], boundary.Submissions.Select(s => s.NodeId)); // n-1 按事实补记，不重跑
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "succeeded" });
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_DuringPrerequisite_AppliesWhenLeafCreated_ConfirmedSkipped()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "空窗跳过",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [new WorkflowStrategy { Kind = "prerequisite.account" }] }, // 前置挂起期 = 叶子未建立空窗
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|')[1];

        var prereqGate = new TaskCompletionSource();
        string? runId = null;
        var prereq = new FakePrerequisite { OnExecute = run => { runId = run.RunId; return prereqGate.Task; } };
        var (runner, boundary, _) = MakeRunner(prerequisite: prereq);

        var job1Calls = 0;
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId != "job-1") return "succeeded"; // 后续节点正常成功
            if (Interlocked.Increment(ref job1Calls) == 1)
                await Task.Delay(Timeout.Infinite, ct); // 首次等待：叶子已取消 → 抛 OCE 进入 B4 确认路径
            return "cancelled"; // 确认阶段：远端取消已确认
        };

        var task = runner.StartAsync(workflowId);
        // 前置挂起期请求跳过（LeafCts 尚不存在——B4 空窗）；动作绑定当前出现身份
        for (var i = 0; i < 100 && runId is null; i++) { if (runId is null) await Task.Delay(10); }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        prereqGate.TrySetResult();
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" }); // 空窗动作未丢失
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId)); // 流程推进
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_Unconfirmed_RunUnknown_NoAdvanceNoTerminal()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过未确认",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner(skipConfirmTimeout: TimeSpan.FromMilliseconds(200));

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (_, ct) =>
        {
            awaitEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 首次等待与确认等待都不主动返回（确认超时路径）
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Unknown, run.State); // B4：未确认不猜成功
        Assert.Single(boundary.Submissions); // 不推进到下一节点
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "cancelUnconfirmed" });
        Assert.Empty(terminal.Actions); // D10：未知不触发收尾
    }

    [Fact]
    public async Task SkipCurrent_LateLeafSucceeded_RecordedAsSucceeded_NotSkipped()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过太迟",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        var job1Calls = 0;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-1" && Interlocked.Increment(ref job1Calls) == 1)
            {
                awaitEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return "succeeded"; // 确认阶段：节点实际已完成（跳过请求到达太迟）
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "succeeded" }); // 留痕为成功而非跳过
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task TerminalAction_Throws_RunFailed_PendingCompletionRetained()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾失败",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("关闭游戏") } }],
        }).Split('|')[1];
        var (runner, _, terminal) = MakeRunner();
        WorkflowRunState? stateAtExecution = null;
        string? pendingAtExecution = null;
        terminal.OnExecute = (_, run) =>
        {
            stateAtExecution = run.State; // B5：收尾执行期 = Completing + 意图已落盘
            pendingAtExecution = run.PendingCompletionAction;
            throw new InvalidOperationException("模拟关机失败");
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Completing, stateAtExecution); // 先落盘收尾意图再执行
        Assert.NotNull(pendingAtExecution);
        Assert.Equal(WorkflowRunState.Failed, run.State); // 收尾失败不记成功
        Assert.NotNull(run.PendingCompletionAction); // 待执行收尾保留（恢复扫描标 Unknown，禁止自动补发）
        Assert.Contains("收尾动作", run.Note);
    }

    [Fact]
    public async Task Pause_DuringTriggerWait_PersistsPaused_ResumeContinuesToSuccess()
    {
        var future = DateTimeOffset.Now.AddHours(2);
        var doc = new WorkflowDocument
        {
            Name = "暂停恢复",
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
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var delayCalls = 0;
        var gate = new TaskCompletionSource();
        var (runner, boundary, _) = MakeRunner(delay: async (_, ct) =>
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // 首次等待挂起（待暂停打断）
            }
        });

        var task = runner.StartAsync(workflowId);
        await gate.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.Pause);
        var paused = await task;

        Assert.Equal(WorkflowRunState.Paused, paused.State); // 暂停 ≠ 停止
        Assert.NotNull(paused.Wait); // 等待记录保留（恢复后重排）
        Assert.False(paused.TriggerConsumed); // 触发未消费，恢复重等
        Assert.Empty(boundary.Submissions);

        var resumed = await runner.ResumeAsync(runId!);
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Single(boundary.Submissions);
    }

    [Fact]
    public async Task ScheduledLoopWait_AppliesAfterFilterSkippedRound_B9()
    {
        var wednesday0500 = new DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.FromHours(8));
        var doc = new WorkflowDocument
        {
            Name = "循环等待",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-off", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
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
            Loop = new WorkflowLoop
            {
                Mode = "scheduled",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:00"),
                    ["skipAcrossDays"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                },
            },
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var capturedWaits = new List<TimeSpan>();
        using var stopAfterTwo = new CancellationTokenSource();
        var (runner, boundary, _) = MakeRunner(now: wednesday0500, delay: (d, _) =>
        {
            capturedWaits.Add(d);
            return Task.CompletedTask; // 手动时钟快进
        });
        boundary.OnSubmit = _ =>
        {
            if (boundary.Submissions.Count >= 2) stopAfterTwo.Cancel(); // 跑完两轮即停
            return Task.CompletedTask;
        };

        var run = await runner.StartAsync(workflowId, stopAfterTwo.Token);

        Assert.Equal(WorkflowRunState.Cancelled, run.State); // 外部停止收尾
        Assert.Equal(2, boundary.Submissions.Count(s => s.NodeId == "n-on")); // 两轮 B 各执行一次
        Assert.Contains(capturedWaits, d => d == TimeSpan.FromMinutes(60)); // B9：上一轮以过滤跳过结束，新一轮仍经轮次起点等待（05:00→06:00）
        Assert.Equal(2, run.NodeOutcomes.Count(o => o is { NodeId: "n-off", Result: "skippedFilter" }));
    }
}
