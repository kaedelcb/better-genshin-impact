using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R4.9 启动移交受理（TaskCenterHost.RegisterHandoffAsync）夹具——设计稿 §8 不变量矩阵宿主侧 + ASTRA 二轮处置回归：
/// 1 重放/并发双提交去重、2 同键内容冲突、4 手动新意图、5 崩溃窗真实扫描（Planned→Interrupted→AlreadyAccepted）、
/// 6 arm 四态+身份追加登记（B2）、7 resume 绑定最新+多绑定追加（B1）+重放不重选、
/// 12 能力守卫（监控端直接调宿主也被拦，含 Start/Resume 公共入口）、13 未知 mode 不回落、
/// B3 受理前权威预检（组装/预检失败不消耗 IntentKey）、B4/I1 受理后驱动故障按准确 RunId 收敛、
/// I4 恢复扫描幂等（不改写 Interrupted；未决前置事实→Interrupted，恢复时 ReconcileAsync 对账——R4.6 合同）、I5 台账不完整拒绝、I3 取消贴近提交点。
/// 假 Runner 工厂 + 假就绪判定 + 假能力提供方接缝，无真实 IPC。
/// </summary>
public class StartupHandoffHostTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public StartupHandoffHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "handoff-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public List<string> Submissions { get; } = new();
        /// <summary>故障注入：提交阶段抛出（驱动故障收敛夹具）。</summary>
        public Exception? SubmitThrows { get; set; }
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            if (SubmitThrows is not null) throw SubmitThrows;
            Submissions.Add(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count));
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => BoundaryTerminalResult.Observed(OnAwait is not null ? await OnAwait(jobId, ct) : "succeeded");
    }

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("job-t"));
    }

    /// <summary>播种流程（withTrigger=true 带合法 trigger.time 参数，可真实进入 Waiting）。</summary>
    private string Seed(string name, bool withTrigger = false)
    {
        var doc = new WorkflowDocument
        {
            Name = name,
            Activation = new WorkflowActivation { Status = "active" },
            Nodes =
            [
                new WorkflowNode
                {
                    NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", Revision = "rev-1" },
                },
            ],
        };
        if (withTrigger)
            doc.Triggers.Add(new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, JsonElement> { ["time"] = JsonDocument.Parse($"\"{DateTime.Now.AddHours(2):HH:mm}\"").RootElement.Clone() }, // 四轮 建议4：动态时刻（远离触发点，无临近时钟依赖）
            });
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    private (TaskCenterHost Host, FakeBoundary Boundary) MakeHost(bool ready = true, bool capability = true,
        bool runnerThrows = false, Func<ControlStatus?>? snapshot = null)
    {
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            runnerThrows
                ? (_, _, _) => throw new InvalidOperationException("组装爆炸")
                : (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => ready ? (true, null) : (false, "BGI 离线（测试就绪判定）"),
            () => capability,
            snapshot);
        return (host, boundary);
    }

    private static StartupHandoffRequest Req(string workflowId, string intentKey,
        string mode = StartupHandoffModes.Start, string? executionId = null)
        => new()
        {
            ExecutionId = executionId ?? Guid.NewGuid().ToString("N"),
            StepId = "step-1",
            IntentKey = intentKey,
            WorkflowId = workflowId,
            Mode = mode,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 500)
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "等待条件超时未满足");
    }

    /// <summary>直接造一条 Interrupted 运行（可带移交身份）。</summary>
    private WorkflowRunRecord SeedInterrupted(string workflowId, HandoffIdentity? identity = null)
    {
        var rec = _runs.CreateRun(workflowId, "rev-1", handoff: identity);
        rec.State = WorkflowRunState.Interrupted;
        _runs.Update(rec);
        return rec;
    }

    [Fact]
    public async Task Start_Accepted_RunCarriesIdentity_LedgerRoundtrip()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();
        var req = Req(workflowId, "manual:exec-1");

        var r = await host.RegisterHandoffAsync(req);

        Assert.Equal(HandoffOutcome.Accepted, r.Outcome);
        Assert.NotNull(r.RunId);
        var ledger = _runs.QueryHandoffLedger("manual:exec-1");
        Assert.Equal(HandoffLedgerState.Hit, ledger.State);
        Assert.Equal(r.RunId, ledger.Run!.RunId);
        var binding = Assert.Single(ledger.Run.Handoffs);
        Assert.Equal("manual:exec-1", binding.IntentKey);
        Assert.Equal(StartupHandoffModes.Start, binding.Mode);
        Assert.Equal(req.ExecutionId, binding.ExecutionId);
        Assert.Equal("step-1", binding.StepId);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Replay_SameKey_AlreadyAccepted_SameRunId()
    {
        var workflowId = Seed("日常流程");
        var (host, boundary) = MakeHost();
        var gate = new TaskCompletionSource();
        boundary.OnAwait = async (_, _) => { await gate.Task; return "succeeded"; };
        var req = Req(workflowId, "timerTrigger:ts-1:2026-09-19");

        var first = await host.RegisterHandoffAsync(req);
        Assert.Equal(HandoffOutcome.Accepted, first.Outcome);
        await WaitUntilAsync(() => _runs.List().FirstOrDefault()?.State == WorkflowRunState.Running);

        var replay = await host.RegisterHandoffAsync(Req(workflowId, "timerTrigger:ts-1:2026-09-19"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.Single(_runs.List());

        gate.SetResult();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Concurrent_SameKey_SingleRun()
    {
        var workflowId = Seed("日常流程");
        var (host, boundary) = MakeHost();
        var gate = new TaskCompletionSource();
        boundary.OnAwait = async (_, _) => { await gate.Task; return "succeeded"; };
        const string key = "watchdog:wd-1:2026-09-19";

        var results = await Task.WhenAll(
            host.RegisterHandoffAsync(Req(workflowId, key)),
            host.RegisterHandoffAsync(Req(workflowId, key)));

        Assert.Single(_runs.List());
        Assert.Equal(1, results.Count(r => r.Outcome == HandoffOutcome.Accepted));
        Assert.Equal(1, results.Count(r => r.Outcome == HandoffOutcome.AlreadyAccepted));
        Assert.Equal(results[0].RunId, results[1].RunId);

        gate.SetResult();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task SameKey_DifferentContent_IdentityConflict()
    {
        var wfA = Seed("流程A");
        var wfB = Seed("流程B");
        var (host, _) = MakeHost();
        var first = await host.RegisterHandoffAsync(Req(wfA, "manual:exec-x"));
        Assert.Equal(HandoffOutcome.Accepted, first.Outcome);
        await WaitUntilAsync(() => _runs.List().FirstOrDefault()?.IsTerminal == true);

        // 同键不同流程 → 身份冲突（不返回旧 runId）
        var conflict = await host.RegisterHandoffAsync(Req(wfB, "manual:exec-x"));
        Assert.Equal(HandoffOutcome.Rejected, conflict.Outcome);
        Assert.Equal(HandoffReasonCodes.IdentityConflict, conflict.ReasonCode);
        Assert.Null(conflict.RunId);

        // 同键同流程不同语义 → 身份冲突
        var modeConflict = await host.RegisterHandoffAsync(Req(wfA, "manual:exec-x", StartupHandoffModes.Resume));
        Assert.Equal(HandoffOutcome.Rejected, modeConflict.Outcome);
        Assert.Equal(HandoffReasonCodes.IdentityConflict, modeConflict.ReasonCode);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Manual_NewKey_AfterTerminal_NewRunAllowed()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();
        var first = await host.RegisterHandoffAsync(Req(workflowId, "manual:exec-1"));
        await WaitUntilAsync(() => _runs.List().FirstOrDefault()?.State == WorkflowRunState.Succeeded);

        var second = await host.RegisterHandoffAsync(Req(workflowId, "manual:exec-2"));
        Assert.Equal(HandoffOutcome.Accepted, second.Outcome);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(2, _runs.List().Count);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task CrashWindow_PlannedWithIdentity_RealScan_Interrupted_ReplayAlreadyAccepted()
    {
        // 真实崩溃窗（I7）：受理落盘（Planned 带身份）后未驱动即"崩溃"→ 恢复扫描标 Interrupted → 重放 AlreadyAccepted 不新建不换键
        var workflowId = Seed("日常流程");
        var seeded = _runs.CreateRun(workflowId, "rev-1", handoff: new HandoffIdentity
        {
            IntentKey = "timerTrigger:ts-9:2026-09-19",
            ExecutionId = "exec-old",
            StepId = "step-1",
            TriggerKind = "timerTrigger",
            Mode = StartupHandoffModes.Start,
        });
        Assert.Equal(WorkflowRunState.Planned, seeded.State);
        var (host, _) = MakeHost();

        var replay = await host.RegisterHandoffAsync(Req(workflowId, "timerTrigger:ts-9:2026-09-19"));

        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(seeded.RunId, replay.RunId);
        Assert.Single(_runs.List()); // 不新建不换键
        Assert.False(host.IsDriving(workflowId)); // 不自动重跑
        Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(seeded.RunId)!.State); // 经真实恢复扫描转换
        Assert.Contains("Interrupted", _runs.Load(seeded.RunId)!.Note);
    }

    [Fact]
    public async Task CrashWindow_UnknownReplay_AlreadyAccepted_NeedsReconcile()
    {
        // S1 定案：台账命中=受理事实存在——AlreadyAccepted 附当前状态（Unknown 不重新驱动、不新建，提示需对账）
        var workflowId = Seed("日常流程");
        var rec = _runs.CreateRun(workflowId, "rev-1", handoff: new HandoffIdentity
        {
            IntentKey = "logTrigger:lt-1:2026-09-19", Mode = StartupHandoffModes.Start,
        });
        rec.State = WorkflowRunState.Unknown;
        _runs.Update(rec);
        var (host, _) = MakeHost();

        var replay = await host.RegisterHandoffAsync(Req(workflowId, "logTrigger:lt-1:2026-09-19"));

        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(rec.RunId, replay.RunId);
        Assert.Equal(WorkflowRunState.Unknown, replay.CurrentRunState);
        Assert.Contains("对账", replay.Reason);
        Assert.Single(_runs.List());
        Assert.False(host.IsDriving(workflowId));
    }

    [Fact]
    public async Task ArmTrigger_WaitingWithPendingTrigger_AlreadyAccepted_IdentityAppended()
    {
        var workflowId = Seed("定时流程", withTrigger: true);
        var (host, _) = MakeHost();
        host.EnsureRecovered(); // 恢复屏障只扫一次：先跑完扫描再设在等运行（否则 Waiting 会被启动扫描标 Interrupted）
        var waiting = _runs.CreateRun(workflowId, "rev-1");
        waiting.State = WorkflowRunState.Waiting;
        waiting.Wait = new WaitStateRecord { Kind = "trigger.time", NextTriggerAt = DateTimeOffset.Now.AddHours(3) };
        _runs.Update(waiting);

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-1", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.AlreadyAccepted, r.Outcome);
        Assert.Equal(waiting.RunId, r.RunId);
        Assert.Single(_runs.List()); // 幂等挂载：不新建运行
        Assert.False(host.IsDriving(workflowId));
        // B2：挂载成功前本次身份已原子追加登记（落盘失败不得报成功）
        var binding = Assert.Single(_runs.Load(waiting.RunId)!.Handoffs);
        Assert.Equal("manual:arm-1", binding.IntentKey);
        Assert.Equal(StartupHandoffModes.ArmTrigger, binding.Mode);
    }

    [Fact]
    public async Task ArmTrigger_MountThenTerminal_ReplaySameKey_AlreadyAccepted()
    {
        // B2 补测：挂载成功 → 运行完结 → 同键重放仍 AlreadyAccepted（身份已随挂载登记，不新建）
        var workflowId = Seed("定时流程", withTrigger: true);
        var (host, _) = MakeHost();
        host.EnsureRecovered();
        var waiting = _runs.CreateRun(workflowId, "rev-1");
        waiting.State = WorkflowRunState.Waiting;
        waiting.Wait = new WaitStateRecord { Kind = "trigger.time", NextTriggerAt = DateTimeOffset.Now.AddHours(3) };
        _runs.Update(waiting);
        var mount = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-t", StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, mount.Outcome);

        // 运行完结（外部推进到终态）
        var rec = _runs.Load(waiting.RunId)!;
        rec.State = WorkflowRunState.Succeeded;
        _runs.Update(rec);

        var replay = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-t", StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(waiting.RunId, replay.RunId);
        Assert.Contains("已完结", replay.Reason);
        Assert.Single(_runs.List());
    }

    [Fact]
    public async Task ArmTrigger_MountThenDifferentContent_IdentityConflict()
    {
        // B2 补测：挂载成功后同键改投不同流程/语义 → 身份冲突（不静默）
        var wfOther = Seed("别的流程", withTrigger: true);
        var workflowId = Seed("定时流程", withTrigger: true);
        var (host, _) = MakeHost();
        host.EnsureRecovered();
        var waiting = _runs.CreateRun(workflowId, "rev-1");
        waiting.State = WorkflowRunState.Waiting;
        waiting.Wait = new WaitStateRecord { Kind = "trigger.time", NextTriggerAt = DateTimeOffset.Now.AddHours(3) };
        _runs.Update(waiting);
        var mount = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-c", StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, mount.Outcome);

        var conflictFlow = await host.RegisterHandoffAsync(Req(wfOther, "manual:arm-c", StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.Rejected, conflictFlow.Outcome);
        Assert.Equal(HandoffReasonCodes.IdentityConflict, conflictFlow.ReasonCode);

        var conflictMode = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-c", StartupHandoffModes.Start));
        Assert.Equal(HandoffOutcome.Rejected, conflictMode.Outcome);
        Assert.Equal(HandoffReasonCodes.IdentityConflict, conflictMode.ReasonCode);
        Assert.Single(_runs.List().Where(r => r.WorkflowId == workflowId));
    }

    [Fact]
    public async Task ArmTrigger_PausedRun_Rejected_NoFakeMount()
    {
        var workflowId = Seed("定时流程", withTrigger: true);
        var (host, _) = MakeHost();
        host.EnsureRecovered(); // 同上：先扫描再造 Paused 运行
        var paused = _runs.CreateRun(workflowId, "rev-1");
        paused.State = WorkflowRunState.Paused;
        _runs.Update(paused);

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-2", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.AlreadyRunning, r.ReasonCode);
        Assert.Empty(_runs.Load(paused.RunId)!.Handoffs); // 拒绝不戳身份
    }

    [Fact]
    public async Task ArmTrigger_UnknownRun_Rejected_NeedsReconcile()
    {
        var workflowId = Seed("定时流程", withTrigger: true);
        var unknown = _runs.CreateRun(workflowId, "rev-1");
        unknown.State = WorkflowRunState.Unknown;
        _runs.Update(unknown);
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-3", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.Unknown, r.ReasonCode);
        Assert.Single(_runs.List());
    }

    [Fact]
    public async Task ArmTrigger_FlowWithoutTrigger_Rejected_NoTrigger()
    {
        var workflowId = Seed("无触发器流程"); // 无 trigger.time、无 loop
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-4", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.NoTrigger, r.ReasonCode);
        Assert.Empty(_runs.List()); // 预检失败不留运行记录
    }

    [Fact]
    public async Task ArmTrigger_NoWaitingRun_CreatesArmedRun_WaitingNoSubmission()
    {
        var workflowId = Seed("定时流程", withTrigger: true);
        var (host, boundary) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:arm-5", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Accepted, r.Outcome);
        // I7：真实进入 Waiting 且有待触发时刻；等待不占槽位（零提交）
        await WaitUntilAsync(() => _runs.Load(r.RunId!)?.State == WorkflowRunState.Waiting);
        var run = _runs.Load(r.RunId!)!;
        Assert.NotNull(run.Wait?.NextTriggerAt);
        Assert.Empty(boundary.Submissions);
        Assert.Equal("manual:arm-5", Assert.Single(run.Handoffs).IntentKey);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Resume_BindsLatestInterrupted_ReplaySameRun_NotReselect()
    {
        var workflowId = Seed("日常流程");
        var older = SeedInterrupted(workflowId);
        await Task.Delay(30); // 拉开 UpdatedAt
        var latest = SeedInterrupted(workflowId);
        var olderUpdated = _runs.Load(older.RunId)!.UpdatedAt;
        var latestUpdated = _runs.Load(latest.RunId)!.UpdatedAt;
        var (host, _) = MakeHost();
        host.EnsureRecovered();
        // I4：恢复扫描幂等——不改写既有 Interrupted（UpdatedAt 不动，候选排序依据不变）
        Assert.Equal(olderUpdated, _runs.Load(older.RunId)!.UpdatedAt);
        Assert.Equal(latestUpdated, _runs.Load(latest.RunId)!.UpdatedAt);

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:resume-1", StartupHandoffModes.Resume));

        Assert.Equal(HandoffOutcome.Accepted, r.Outcome);
        Assert.Equal(latest.RunId, r.RunId); // 绑定最新一条
        Assert.Equal("manual:resume-1", Assert.Single(_runs.Load(latest.RunId)!.Handoffs).IntentKey);
        Assert.Empty(_runs.Load(older.RunId)!.Handoffs); // 旧记录不动

        // 重放同键：直接 AlreadyAccepted 同一 runId，不重选、不新建
        await WaitUntilAsync(() => _runs.Load(latest.RunId)?.IsTerminal == true);
        var replay = await host.RegisterHandoffAsync(Req(workflowId, "manual:resume-1", StartupHandoffModes.Resume));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(latest.RunId, replay.RunId);
        Assert.Equal(2, _runs.List().Count);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Resume_AppendsBinding_OldIntentStillDeduped()
    {
        // B1：意图 A 受理创建运行 R → 崩溃窗（受理后未执行即中断，无在飞事实）→ 意图 B resume 绑定 R
        // （追加不替换）→ R 完结 → A 重放仍 AlreadyAccepted（去重依据永存，不新建运行）
        var workflowId = Seed("日常流程");
        var run = SeedInterrupted(workflowId, new HandoffIdentity
        {
            IntentKey = "manual:intent-a", ExecutionId = "exec-a", StepId = "step-1", Mode = StartupHandoffModes.Start,
        });
        var (host, _) = MakeHost();

        var resume = await host.RegisterHandoffAsync(Req(workflowId, "manual:intent-b", StartupHandoffModes.Resume));
        Assert.Equal(HandoffOutcome.Accepted, resume.Outcome);
        Assert.Equal(run.RunId, resume.RunId);
        var bindings = _runs.Load(run.RunId)!.Handoffs;
        Assert.Equal(2, bindings.Count); // 两条不可变绑定并存（旧绑定不替换）
        Assert.Equal("manual:intent-a", bindings[0].IntentKey);
        Assert.Equal("manual:intent-b", bindings[1].IntentKey);

        await WaitUntilAsync(() => _runs.Load(run.RunId)?.IsTerminal == true);
        // A 重放：旧意图去重依据仍在 → AlreadyAccepted（终态已完结），不新建运行
        var replayA = await host.RegisterHandoffAsync(Req(workflowId, "manual:intent-a"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replayA.Outcome);
        Assert.Equal(run.RunId, replayA.RunId);
        Assert.Contains("已完结", replayA.Reason);
        Assert.Single(_runs.List());
        await host.ShutdownAsync();
    }
    [Fact]
    public async Task Resume_NoResumableRun_Rejected()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:resume-2", StartupHandoffModes.Resume));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.NoResumableRun, r.ReasonCode);
        Assert.Empty(_runs.List());
    }

    [Fact]
    public async Task Resume_UnknownRunPresent_Rejected_NeedsReconcile()
    {
        var workflowId = Seed("日常流程");
        var interrupted = SeedInterrupted(workflowId);
        var unknown = _runs.CreateRun(workflowId, "rev-1");
        unknown.State = WorkflowRunState.Unknown;
        _runs.Update(unknown);
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:resume-3", StartupHandoffModes.Resume));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.Unknown, r.ReasonCode);
        Assert.Empty(_runs.Load(interrupted.RunId)!.Handoffs); // 未戳身份
    }

    [Fact]
    public async Task Resume_CandidateReadyFlow_Rejected_BeforeStamping()
    {
        // B3：resume 的候选只读守卫在身份戳记之前（原记录保持不变）
        var doc = new WorkflowDocument
        {
            Name = "候选流程",
            Activation = new WorkflowActivation { Status = "candidate-ready" },
            Nodes = [new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" }],
        };
        _workflows.Save(doc, null);
        var interrupted = SeedInterrupted(doc.WorkflowId!);
        var noteBefore = _runs.Load(interrupted.RunId)!.Note;
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(doc.WorkflowId!, "manual:resume-cand", StartupHandoffModes.Resume));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.FlowUnavailable, r.ReasonCode);
        var after = _runs.Load(interrupted.RunId)!;
        Assert.Empty(after.Handoffs); // 未戳身份
        Assert.Equal(noteBefore, after.Note); // 原记录保持不变
    }    [Fact]
    public async Task Monitor_CapabilityGuard_RejectsHandoff_Start_Resume()
    {
        var workflowId = Seed("日常流程");
        var interrupted = SeedInterrupted(workflowId);
        var (host, _) = MakeHost(capability: false);

        var handoff = await host.RegisterHandoffAsync(Req(workflowId, "manual:mon-1"));
        Assert.Equal(HandoffOutcome.Rejected, handoff.Outcome);
        Assert.Equal(HandoffReasonCodes.NoCapability, handoff.ReasonCode);
        Assert.Empty(_runs.Load(interrupted.RunId)!.Handoffs); // 先于台账写入

        // I2/I7：公共入口统一检查——Start/Resume 同样被拦（绕过 VM 的调用也不例外）
        var start = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Unavailable, start.Status);
        Assert.Contains("监控端", start.Message);
        var resume = await host.ResumeRunAsync(interrupted.RunId);
        Assert.Equal(HostActionStatus.Unavailable, resume.Status);
        Assert.Contains("监控端", resume.Message);
        Assert.Single(_runs.List()); // 只有播种记录，无任何新建
        Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(interrupted.RunId)!.State); // 状态未被改动
    }

    [Fact]
    public async Task Start_ActiveRunExists_Rejected_AlreadyRunning()
    {
        var workflowId = Seed("日常流程");
        var (host, boundary) = MakeHost();
        var gate = new TaskCompletionSource();
        boundary.OnAwait = async (_, _) => { await gate.Task; return "succeeded"; };
        var first = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, first.Status);
        await WaitUntilAsync(() => _runs.List().FirstOrDefault()?.State == WorkflowRunState.Running);

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:busy-1"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.AlreadyRunning, r.ReasonCode);
        Assert.Single(_runs.List());
        gate.SetResult();
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task UnknownMode_Rejected_NoSilentFallback()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:bad-mode", mode: "banana"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.UnsupportedMode, r.ReasonCode);
        Assert.Contains("不静默回落", r.Reason);
        Assert.Empty(_runs.List());
    }

    [Fact]
    public async Task EmptyIntentKey_Rejected_TerminatesChain_NotConfigMissing()
    {
        // I6：空 IntentKey 是身份合同错误——HandoffError（终止链），不得归 ConfigMissing（链继续）
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, ""));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.HandoffError, r.ReasonCode);
        Assert.NotEqual(HandoffReasonCodes.ConfigMissing, r.ReasonCode);
        Assert.Empty(_runs.List());
    }

    [Fact]
    public async Task AssemblyFailure_Rejected_BeforeAcceptance_NoIntentBurned()
    {
        // B3：组装/权威预检失败在受理之前——Rejected，不消耗 IntentKey、不留运行记录、不标 Failed
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(runnerThrows: true);

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:boom-1"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.FlowUnavailable, r.ReasonCode);
        Assert.Contains("组装爆炸", r.Reason);
        Assert.Empty(_runs.List());
        // 重试同键：意图未被消耗——修复后仍可受理（不是 AlreadyAccepted）
        var (host2, _) = MakeHost();
        var retry = await host2.RegisterHandoffAsync(Req(workflowId, "manual:boom-1"));
        Assert.Equal(HandoffOutcome.Accepted, retry.Outcome);
        await host2.ShutdownAsync();
    }

    [Fact]
    public async Task DriveFailure_AfterAcceptance_ConvergedUnknown_ExactRunId()
    {
        // B4/I1：受理后驱动故障（提交阶段异常）——回执 Accepted；观察器按准确 RunId 收敛，
        // 在飞提交意图 → Unknown（不标 Failed、不留 Planned/Running 僵尸）
        var workflowId = Seed("日常流程");
        var (host, boundary) = MakeHost();
        boundary.SubmitThrows = new InvalidOperationException("提交通道爆炸");

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:drive-fail"));

        Assert.Equal(HandoffOutcome.Accepted, r.Outcome);
        Assert.NotNull(r.RunId);
        await WaitUntilAsync(() => _runs.Load(r.RunId!)?.State == WorkflowRunState.Unknown);
        var run = _runs.Load(r.RunId!)!;
        Assert.Contains("驱动异常", run.Note);
        Assert.Equal("manual:drive-fail", Assert.Single(run.Handoffs).IntentKey);
        Assert.False(host.IsDriving(workflowId)); // 驱动已收敛出册
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task LedgerIncomplete_Rejected_NewIntentBlocked()
    {
        // I5：坏文件可能藏着受理事实——读不到 ≠ 未受理；台账不完整拒绝新受理
        var workflowId = Seed("日常流程");
        var seeded = SeedInterrupted(workflowId, new HandoffIdentity
        {
            IntentKey = "manual:known", Mode = StartupHandoffModes.Start,
        });
        Directory.CreateDirectory(Path.Combine(_dir, "runs"));
        File.WriteAllText(Path.Combine(_dir, "runs", "run-corrupt01.run.json"), "{ 这不是合法 JSON");
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(workflowId, "manual:new-intent"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.LedgerIncomplete, r.ReasonCode);
        Assert.Single(_runs.List()); // 只有播种记录，未新建
        // 已有身份的键命中仍可读（命中优先于不完整——该键受理事实已证实）
        var hit = await host.RegisterHandoffAsync(Req(workflowId, "manual:known"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, hit.Outcome);
        Assert.Equal(seeded.RunId, hit.RunId);
    }

    [Fact]
    public async Task Cancelled_BeforeAcceptance_NoRun()
    {
        // I3：启动链取消仅覆盖受理点之前——已取消令牌在提交前抛出，不落盘
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.RegisterHandoffAsync(Req(workflowId, "manual:cancel-1"), cts.Token));
        Assert.Empty(_runs.List());
    }

    [Fact]
    public void RecoveryScan_PrerequisiteInFlight_Interrupted_ResumeReconciles_R46Contract()
    {
        // I4 处置定案（与 R4.6 已验收合同一致）：前置动作在飞【不】标 Unknown——标 Interrupted，
        // 恢复时经 ReconcileAsync 先查远端权威终态对账（详见 R46Batch3cRunnerTests 同名合同夹具）。
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = "n-1", Kind = "prerequisite.account", State = PrerequisiteActionState.Submitted,
            IdempotencyKey = "idem-pre-1", Fingerprint = "fp", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        });
        _runs.Update(rec);

        var recovered = Assert.Single(_runs.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
    }

    [Fact]
    public void RecoveryScan_IntentNotSent_StillInterrupted()
    {
        // I4 对照组：意图已落盘但确定未发送（SendAttempted=false）——无外部事实，按 Interrupted（可恢复）
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        rec.State = WorkflowRunState.Running;
        rec.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = "n-1", Kind = "prerequisite.account", State = PrerequisiteActionState.Intent, SendAttempted = false,
            IdempotencyKey = "idem-pre-2", Fingerprint = "fp", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        });
        _runs.Update(rec);

        var recovered = Assert.Single(_runs.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
    }

    [Fact]
    public void RecoveryScan_Idempotent_DoesNotRewriteInterrupted()
    {
        // I4：既有 Interrupted 幂等保持——不重写（UpdatedAt/RecordRevision 不变，不重复追加留痕）
        var rec = SeedInterrupted("wf-aaaaaaaa");
        var updatedAt = rec.UpdatedAt;
        var revision = rec.RecordRevision;
        var note = rec.Note;

        var again = _runs.RecoverOnStart();

        var after = Assert.Single(again);
        Assert.Equal(WorkflowRunState.Interrupted, after.State);
        Assert.Equal(updatedAt, after.UpdatedAt);
        Assert.Equal(revision, after.RecordRevision);
        Assert.Equal(note, after.Note);
    }

    [Fact]
    public async Task ArmTrigger_LoopOnlyFlow_Rejected_NoTrigger()
    {
        // 四轮 重要5：结构性循环首轮立即执行（DriveAsync 轮次等待仅 LoopIteration>0），不算可挂载——loop-only 流程 arm 拒绝
        var doc = new WorkflowDocument
        {
            Name = "纯循环流程",
            Activation = new WorkflowActivation { Status = "active" },
            Loop = new WorkflowLoop { Mode = "immediate" },
            Nodes =
            [
                new WorkflowNode
                {
                    NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", Revision = "rev-1" },
                },
            ],
        };
        _workflows.Save(doc, null);
        var (host, _) = MakeHost();

        var r = await host.RegisterHandoffAsync(Req(doc.WorkflowId!, "manual:exec-loop", StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.NoTrigger, r.ReasonCode);
        Assert.Empty(_runs.List());
    }

    // ===== ASTRA 三轮处置回归（快照判定入宿主/台账优先；守卫窗口收紧；构造零副作用） =====

    [Fact]
    public async Task SnapshotBusy_LedgerHitReplay_StillAlreadyAccepted()
    {
        // 三轮 阻断1 核心回归：受理事实优先——同 IntentKey 重放时即使快照显示 BGI 忙，也回 AlreadyAccepted（不得 BgiBusy 遮蔽）
        var wf = Seed("流程A");
        var (host, _) = MakeHost(snapshot: () => new ControlStatus { TaskRunning = false });
        var req = Req(wf, "timer:t1:2026-09-19");
        var first = await host.RegisterHandoffAsync(req);
        Assert.Equal(HandoffOutcome.Accepted, first.Outcome);

        // 重放：快照变为忙（如正是该受理运行在跑）——台账命中优先
        var (host2, _) = MakeHost(snapshot: () => new ControlStatus { TaskRunning = true, CurrentTaskName = "锄地" });
        var replay = await host2.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(first.RunId, replay.RunId);
    }

    [Fact]
    public async Task SnapshotBusy_NewIntent_RejectedBgiBusy_NoRunCreated()
    {
        // 三轮 阻断1：快照在跑 + 新意图（台账未命中）→ BgiBusy 拒绝且不留运行记录
        var wf = Seed("流程A");
        var (host, _) = MakeHost(snapshot: () => new ControlStatus { TaskRunning = true, CurrentTaskName = "锄地" });

        var r = await host.RegisterHandoffAsync(Req(wf, "manual:exec-x"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.BgiBusy, r.ReasonCode);
        Assert.Contains("锄地", r.Reason);
        Assert.Empty(_runs.List());
    }

    [Fact]
    public async Task SnapshotUnavailable_NewIntentStart_RejectedStatusUncertain()
    {
        // 矩阵 12（宿主侧）：快照不可考 + start/resume → StatusUncertain；armTrigger 不受限
        var wf = Seed("流程A");
        var (host, _) = MakeHost(snapshot: () => null);

        var r = await host.RegisterHandoffAsync(Req(wf, "manual:exec-y"));
        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.StatusUncertain, r.ReasonCode);
        Assert.Empty(_runs.List());

        var wfTrig = Seed("流程B", withTrigger: true);
        var arm = await host.RegisterHandoffAsync(Req(wfTrig, "manual:exec-z", StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.Accepted, arm.Outcome);
        await host.ShutdownAsync(); // 四轮 建议4：Waiting 驱动的延时任务不跨越夹具清理期
    }

    [Fact]
    public async Task CapabilityRevoked_DuringSnapshotRejection_RecheckWins()
    {
        // 三轮 重要9：锁外预检（快照拒绝）返回前的快返回复核与最终临界区同顺序——等待期间能力撤销 → NoCapability 优先
        var wf = Seed("流程A");
        var capabilityCalls = 0;
        // 能力提供方：入口预检（第 1 次调用）放行，快返回复核（第 2 次）已撤销
        var host2 = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, new FakeBoundary(), new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null),
            () => ++capabilityCalls <= 1,
            () => new ControlStatus { TaskRunning = true });

        var r = await host2.RegisterHandoffAsync(Req(wf, "manual:exec-cap"));

        Assert.Equal(HandoffOutcome.Rejected, r.Outcome);
        Assert.Equal(HandoffReasonCodes.NoCapability, r.ReasonCode);
        Assert.Empty(_runs.List());
    }

    /// <summary>
    /// **G4a（[批次四十五]）**：**启动移交受理处**落定**准入来源固定 Scope**——接缝提供 BGI epoch 时，
    /// `RegisterHandoffAsync(start)` 必须把 `bgi:local:{受理时 epoch}` 随受理**同一次**写入运行记录
    /// （`WorkflowRunRecord.AdmissionSourceScope`；**权威＝运行台账受理登记事实**，不在租约内再造来源记录），
    /// 且后继准入反查（`AdmissionParentForTest`，生产路径同一实现）返回该固定 Scope 与来源身份；
    /// **epoch 不可用 ⇒ 不留来源**（后继准入不签发、不发送；不得读当前 epoch 补造），受理本身仍按 R4.9 成立。
    /// </summary>
    [Fact]
    public async Task StartHandoff_PersistsFixedAdmissionSourceScope_AndSuccessorResolvesIt()
    {
        var wf = Seed("移交来源登记流程");
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, new FakeBoundary(), new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null), () => true,
            () => new ControlStatus { TaskRunning = false },
            admissionWired: true, admissionSeams: new TaskCenterAdmissionSeams { Epoch = "9:900" });
        try
        {
            var first = await host.RegisterHandoffAsync(Req(wf, "manual:exec-source"));
            Assert.Equal(HandoffOutcome.Accepted, first.Outcome);
            var run = _runs.Load(first.RunId!);
            Assert.Equal("bgi:local:9:900", run!.AdmissionSourceScope);   // 受理时捕获的固定 Scope

            // 后继准入反查（生产路径同一实现）：来源身份为合成运行来源，Scope＝**登记值**（不重读当前纪元）
            var parent = host.AdmissionParentForTest(first.RunId!, wf);
            Assert.NotNull(parent);
            Assert.Equal("run-source:" + first.RunId, parent!.Value.RequestIdentity);
            Assert.Equal("bgi:local:9:900", parent.Value.Scope);

            // 已受理的运行不因「已有来源」而重复登记/改写（来源随受理固定，重放同键不重选）
            var replay = await host.RegisterHandoffAsync(Req(wf, "manual:exec-source"));
            Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
            Assert.Equal("bgi:local:9:900", _runs.Load(first.RunId!)!.AdmissionSourceScope);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>
    /// **G4a 反例（[批次四十五]）**：**当前 BGI epoch 不可用**时，启动移交仍按 R4.9 既有合同受理，但运行记录
    /// **不得**留下来源 Scope ⇒ 后继准入反查返回 null（不签发、不发送，**不得**读当前 epoch 补造）。
    /// </summary>
    [Fact]
    public async Task StartHandoff_WithoutEpoch_LeavesNoSourceScope_SuccessorFailsClosed()
    {
        var wf = Seed("移交无纪元流程");
        // 生产构造（无接缝 epoch、clientAccessor=null）⇒ `CurrentBgiEpoch()` 为空
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, new FakeBoundary(), new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null), () => true,
            () => new ControlStatus { TaskRunning = false });
        try
        {
            var reg = await host.RegisterHandoffAsync(Req(wf, "manual:exec-no-epoch"));
            Assert.Equal(HandoffOutcome.Accepted, reg.Outcome);          // R4.9 受理合同不变
            Assert.Null(_runs.Load(reg.RunId!)!.AdmissionSourceScope);   // 不留来源（不得补造）
            Assert.Null(host.AdmissionParentForTest(reg.RunId!, wf));    // 后继准入 fail-closed
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>
    /// **G4a 来源解析矩阵（[批次四十五 验证会诊处置]）**：`TaskCenterHost.ResolveAdmissionParent` 逐支判定——
    /// ①**恰一条**面板来源 ⇒ 只认它；②租约侧**多条** ⇒ **来源歧义直接拒绝**（不得借运行台账字段继续签发）；
    /// ③零条 ＋ 运行台账**确有** start/armTrigger 移交受理事实 ＋ 规范 Scope ⇒ 回落（合成来源身份）；
    /// ④零条 ＋ 运行**无移交受理事实**（普通运行被写入来源字段）⇒ 不认；⑤畸形/空 Scope ⇒ 不认；
    /// ⑥workflow 不一致 ⇒ 不认。
    /// </summary>
    [Theory]
    [InlineData("panel_single", true)]
    [InlineData("panel_ambiguous_with_run_source", false)]
    [InlineData("run_source_with_handoff_binding", true)]
    [InlineData("run_source_without_handoff_binding", false)]
    [InlineData("run_source_malformed_scope", false)]
    [InlineData("run_source_wrong_workflow", false)]
    [InlineData("panel_scope_malformed", false)]
    public void ResolveAdmissionParent_Matrix(string mode, bool expectSource)
    {
        const string runId = "run-g4a-matrix";
        const string wf = "wf-g4a-matrix";
        OperationRecord PanelSource() => new()
        {
            RequestIdentity = "panel-" + Guid.NewGuid().ToString("N")[..8],
            RunBinding = runId,
            Intent = "start",
            ResourceRef = "flow:" + wf,
            OperationType = OperationType.FlowRegistration,
            RequestState = OperationRequestState.TerminalCompleted,
            SubmissionIdentity = "sub:panel:1",
            LastSendSeq = 1,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:local:ep-panel", WorkflowId = wf, RunId = runId,
                ResourceRef = "flow:" + wf, Intent = "start",
            },
        };
        WorkflowRunRecord Run(string? scope) => new()
        {
            RunId = runId,
            WorkflowId = wf,
            AdmissionSourceScope = scope,
            Handoffs =
            [
                new HandoffIdentity
                {
                    IntentKey = "fixture:matrix", ExecutionId = "e", StepId = "s",
                    Mode = StartupHandoffModes.Start,
                },
            ],
        };

        IReadOnlyList<OperationRecord>? sources = mode switch
        {
            "panel_single" => [PanelSource()],
            "panel_ambiguous_with_run_source" => [PanelSource(), PanelSource()],
            // 面板唯一命中但 Scope 畸形（`bgi:local:` 空 epoch 段）⇒ 不得作为权威来源
            "panel_scope_malformed" => [PanelSourceWithScope("bgi:local:")],
            _ => [],
        };
        var run = mode switch
        {
            "panel_single" => Run(null),
            "panel_ambiguous_with_run_source" => Run("bgi:local:ep-run"),
            "run_source_with_handoff_binding" => Run("bgi:local:ep-run:1234"),
            "run_source_without_handoff_binding" => RunWithNoHandoff("bgi:local:ep-run"),
            "run_source_malformed_scope" => Run("bgi:other:ep-run"),
            // 该支**只保留 workflow 为唯一差异**（移交受理事实与规范 Scope 均具备）⇒ 才能证明 workflow 逐字守卫有效
            "run_source_wrong_workflow" => RunWrongWorkflow("bgi:local:ep-run"),
            "panel_scope_malformed" => Run(null),
            _ => null,
        };

        var resolved = TaskCenterHost.ResolveAdmissionParent(sources, run, runId, wf);
        if (expectSource) Assert.NotNull(resolved);
        else Assert.Null(resolved);
        if (mode == "panel_single")
            Assert.Equal(sources![0].RequestIdentity, resolved!.Value.RequestIdentity);   // 只认面板来源
        if (mode == "run_source_with_handoff_binding")
        {
            Assert.Equal("run-source:" + runId, resolved!.Value.RequestIdentity);
            Assert.Equal("bgi:local:ep-run:1234", resolved.Value.Scope);                  // 完整 epoch 不截断
        }

        WorkflowRunRecord RunWithNoHandoff(string scope) => new()
        {
            RunId = runId, WorkflowId = wf, AdmissionSourceScope = scope, Handoffs = [],
        };
        // **只** workflow 不同（移交受理事实 + 规范 Scope 均具备）——唯一差异可判别
        WorkflowRunRecord RunWrongWorkflow(string scope) => new()
        {
            RunId = runId, WorkflowId = "wf-other", AdmissionSourceScope = scope,
            Handoffs =
            [
                new HandoffIdentity
                {
                    IntentKey = "fixture:matrix-wf", ExecutionId = "e", StepId = "s",
                    Mode = StartupHandoffModes.Start,
                },
            ],
        };
        OperationRecord PanelSourceWithScope(string scope)
        {
            var op = PanelSource();
            op.Candidate!.Scope = scope;
            return op;
        }
    }

    [Fact]
    public void CtorAndListFlows_DoNotCreateMissingDirs()
    {
        // 三轮 重要10（四轮 建议3 收窄口径）：宿主构造与只读列表不创建缺失的 flows/runs 目录（监控端零副作用的部分证据；
        // 不声称排除所有文件副作用——既有目录读取不改文件由 Store 层合同保证）
        var flows = Path.Combine(_dir, "nf-flows");
        var runs = Path.Combine(_dir, "nf-runs");
        var host = new TaskCenterHost(flows, runs, Path.Combine(_dir, "nf-cache.json"),
            () => null, () => false, () => null);

        var list = host.ListFlows();

        Assert.Empty(list);
        Assert.False(Directory.Exists(flows));
        Assert.False(Directory.Exists(runs));
    }

    [Fact]
    public void StaleWriterConflict_PreservesAppendedBindings_NoSilentOverwrite()
    {
        // 五轮 重6 危险顺序：引擎 Load[A] → 宿主追加 B 保存 [A,B] → 引擎拿旧对象写回
        // → RunStore 修订守卫响亮冲突（RunRecordConflictException），盘上受理事实不丢、状态不倒退
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1",
            handoff: new HandoffIdentity { IntentKey = "k1", ExecutionId = "e1", StepId = "s1", Mode = "start" });
        var engineView = _runs.Load(rec.RunId)!; // 引擎视角（旧修订，模拟在飞驱动持有的对象）

        var hostView = _runs.Load(rec.RunId)!;
        hostView.Handoffs.Add(new HandoffIdentity { IntentKey = "k2", ExecutionId = "e2", StepId = "s1", Mode = "resume" });
        _runs.Update(hostView);

        engineView.State = WorkflowRunState.Running;
        Assert.Throws<RunRecordConflictException>(() => _runs.Update(engineView)); // 响亮冲突，绝不静默覆盖

        var disk = _runs.Load(rec.RunId)!;
        Assert.Equal(2, disk.Handoffs.Count); // 受理事实不丢
        Assert.Equal("k2", disk.Handoffs[1].IntentKey);
        Assert.NotEqual(WorkflowRunState.Running, disk.State); // 状态不倒退
    }

    [Fact]
    public async Task Concurrent_SameKey_DoubleMissBarrier_SingleAcceptance()
    {
        // 五轮重6/矩阵1 确定性竞态（六轮 重要3 修正假阳性）：
        // 先完成恢复屏障（排除首调用同步抢跑）；两个请求独立 Task.Run 调度；
        // 快照提供方内 Barrier(2) 强制双方都完成首次台账 Miss 才放行（超时=夹具失败，不放行）；
        // provider 进入次数恰为 2 = 双 Miss 直接证据；随后锁内台账双检保证仅一个 Accepted
        var wf = Seed("流程A");
        var (host, boundary) = MakeHost();
        host.EnsureRecovered(); // 恢复屏障先行（幂等），请求路径不再含首次恢复等待
        using var barrier = new Barrier(2);
        var providerCalls = 0;
        var host2 = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null),
            () => true,
            () =>
            {
                Interlocked.Increment(ref providerCalls);
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("双 Miss 屏障超时——未形成确定性竞态汇合");
                return new ControlStatus { TaskRunning = false };
            });
        host2.EnsureRecovered();

        var results = await Task.WhenAll(
            Task.Run(() => host2.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", executionId: "exec-AA"))),
            Task.Run(() => host2.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", executionId: "exec-BB"))));

        Assert.Equal(2, providerCalls); // 两个请求都经历了首查 Miss 后的快照判定（双 Miss 直接证据）
        Assert.Equal(1, results.Count(r => r.Outcome == HandoffOutcome.Accepted));
        Assert.Equal(1, results.Count(r => r.Outcome == HandoffOutcome.AlreadyAccepted));
        Assert.Equal(results[0].RunId, results[1].RunId);
        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.IsTerminal); // 等驱动到受控终点（FakeBoundary 即终态）
        Assert.Single(boundary.Submissions); // 仅一次驱动提交
        await host2.ShutdownAsync();
    }

    [Fact]
    public void HandoffBindings_SurviveEngineStyleLoadModifyUpdate()
    {
        // 四轮 重要6：追加式多绑定的持久化证据——顺序的 Load→改→Update 整对象写回周期不丢宿主追加的身份绑定。
        // 注意：真实引擎在飞期间是「持有加载对象、写点时整对象写回」（并非各写点前重新 Load）——
        // 持有旧对象写回撞上宿主追加的并发交错由 StaleWriterConflict_* 与
        // ArmMount_AppendToRealWaitingDriver_StalesIt_ConvergesInterrupted_BindingsPreserved（七轮 重要2）覆盖
        var identityA = new HandoffIdentity { IntentKey = "timer:t1:2026-09-19", ExecutionId = "e1", StepId = "s1", Mode = "start" };
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1", handoff: identityA);

        // 宿主受理侧：向同一运行追加第二条绑定（resume/幂等挂载同路径）
        var loaded = _runs.Load(rec.RunId)!;
        loaded.Handoffs.Add(new HandoffIdentity { IntentKey = "timer:t1:2026-09-20", ExecutionId = "e2", StepId = "s1", Mode = "resume" });
        _runs.Update(loaded);

        // 引擎侧：重新 Load → 改状态 → Update（WorkflowRunner 写回同构）
        var engineView = _runs.Load(rec.RunId)!;
        engineView.State = WorkflowRunState.Running;
        _runs.Update(engineView);

        var final = _runs.Load(rec.RunId)!;
        Assert.Equal(2, final.Handoffs.Count);
        Assert.Equal("timer:t1:2026-09-19", final.Handoffs[0].IntentKey);
        Assert.Equal("timer:t1:2026-09-20", final.Handoffs[1].IntentKey);
        // 台账对两个键都命中
        Assert.Equal(HandoffLedgerState.Hit, _runs.QueryHandoffLedger("timer:t1:2026-09-19").State);
        Assert.Equal(HandoffLedgerState.Hit, _runs.QueryHandoffLedger("timer:t1:2026-09-20").State);
    }

    // ===== 六轮 重要4：TryAppendHandoffBinding 静态助手直接夹具（重试路径确定性驱动，无真实并发） =====

    private void BumpDiskRevision(string runId)
    {
        // 模拟引擎/他方并发推进：外部抬高盘上修订，使本次提交必然修订冲突
        var bump = _runs.Load(runId)!;
        bump.Note = (bump.Note is null ? "" : bump.Note + " ") + "外部推进";
        _runs.Update(bump);
    }

    [Fact]
    public void TryAppendBinding_ConflictThenReread_SucceedsOnce_SingleBinding()
    {
        // 首次提交修订冲突 → 重读最新记录重试成功：update 恰 2 次、绑定只追加一次、
        // committed=提交时快照（与落盘版本同修订），并发推进的留痕不被覆盖
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        var request = Req("wf-aaaaaaaa", "timer:t1:2026-09-19");
        var updateCalls = 0;
        var first = true;

        var reject = TaskCenterHost.TryAppendHandoffBinding(
            id => _runs.Load(id),
            r =>
            {
                updateCalls++;
                if (first) { first = false; BumpDiskRevision(r.RunId); }
                _runs.Update(r);
            },
            rec.RunId, request, "受理备注",
            _ => null, CancellationToken.None, out var committed);

        Assert.Null(reject);
        Assert.Equal(2, updateCalls);
        Assert.NotNull(committed);
        var disk = _runs.Load(rec.RunId)!;
        var binding = Assert.Single(disk.Handoffs); // 冲突重试不重复追加
        Assert.Equal("timer:t1:2026-09-19", binding.IntentKey);
        Assert.Equal(disk.RecordRevision, committed!.RecordRevision); // committed 即落盘版本
        Assert.Contains("受理备注", disk.Note);
        Assert.Contains("外部推进", disk.Note); // 并发推进事实不被覆盖
    }

    [Fact]
    public void TryAppendBinding_RereadStateGuardRejects_NoAppend()
    {
        // 首次提交冲突（并发把运行置为 Interrupted 可恢复中断）→ 重读后守卫重判拒绝——
        // 证明范围=助手每轮重读都重新过 stateGuard（非完整 resume 语义；预留属宿主调用方，不在本夹具）
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        var request = Req("wf-aaaaaaaa", "timer:t1:2026-09-19", mode: StartupHandoffModes.Resume);
        var first = true;
        var guardCalls = 0;
        var updateCalls = 0;

        var reject = TaskCenterHost.TryAppendHandoffBinding(
            id => _runs.Load(id),
            r =>
            {
                updateCalls++;
                if (first)
                {
                    first = false;
                    var bump = _runs.Load(r.RunId)!;
                    bump.State = WorkflowRunState.Interrupted; // 并发置 Interrupted（非终态，可恢复中断）+ 修订抬高
                    _runs.Update(bump);
                }
                _runs.Update(r);
            },
            rec.RunId, request, "受理备注",
            r =>
            {
                guardCalls++;
                if (r.State == WorkflowRunState.Interrupted)
                    return (HandoffReasonCodes.AlreadyRunning, "目标运行已不可用");
                return null;
            },
            CancellationToken.None, out var committed);

        Assert.NotNull(reject);
        Assert.Equal(HandoffReasonCodes.AlreadyRunning, reject!.Value.Code);
        Assert.Equal(2, guardCalls); // 两次重读都过守卫，第二次拦下
        Assert.Equal(1, updateCalls); // 拒绝后不再尝试落盘
        Assert.Null(committed);
        Assert.Empty(_runs.Load(rec.RunId)!.Handoffs); // 不追加
    }

    [Fact]
    public void TryAppendBinding_CancelBetweenRetries_ThrowsOce_NoAppend()
    {
        // 首次提交冲突后取消到达 → 下一轮入口复核即抛 OCE（入口/身份修改前/耗尽出口三道检查点），不追加
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        var request = Req("wf-aaaaaaaa", "timer:t1:2026-09-19");
        using var cts = new CancellationTokenSource();
        var first = true;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            TaskCenterHost.TryAppendHandoffBinding(
                id => _runs.Load(id),
                r =>
                {
                    if (first) { first = false; BumpDiskRevision(r.RunId); cts.Cancel(); }
                    _runs.Update(r);
                },
                rec.RunId, request, "受理备注",
                _ => null, cts.Token, out _));

        Assert.Empty(_runs.Load(rec.RunId)!.Handoffs);
    }

    [Fact]
    public void TryAppendBinding_PersistentConflicts_BoundedRetry_ReturnsHandoffError()
    {
        // 连续 3 次修订冲突 → 有界放弃返回 HandoffError，绝不拿旧对象覆盖，盘上绑定数不变
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        var request = Req("wf-aaaaaaaa", "timer:t1:2026-09-19");
        var updateCalls = 0;

        var reject = TaskCenterHost.TryAppendHandoffBinding(
            id => _runs.Load(id),
            r =>
            {
                updateCalls++;
                BumpDiskRevision(r.RunId); // 每次提交前都被并发抬高 → 必然冲突
                _runs.Update(r);
            },
            rec.RunId, request, "受理备注",
            _ => null, CancellationToken.None, out var committed);

        Assert.NotNull(reject);
        Assert.Equal(HandoffReasonCodes.HandoffError, reject!.Value.Code);
        Assert.Contains("冲突", reject.Value.Reason);
        Assert.Equal(3, updateCalls); // 有界 3 次，不死循环
        Assert.Null(committed);
        Assert.Empty(_runs.Load(rec.RunId)!.Handoffs);
    }

    // ===== 七轮处置回归：重要1/重要2/重要3 =====

    [Fact]
    public async Task TryReadRunState_CorruptFile_ReturnsNull_NoThrow()
    {
        // 七轮 重要1：受理后回执刷新的兜底证据——运行记录损坏时读取返回 null（不抛出），
        // 回执由调用方回落受理提交时快照（已受理结论不反转）
        var wf = Seed("流程A");
        var (host, _) = MakeHost();
        var result = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19"));
        Assert.Equal(HandoffOutcome.Accepted, result.Outcome);
        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.IsTerminal);
        await host.ShutdownAsync();

        File.WriteAllText(Path.Combine(_dir, "runs", run.RunId + ".run.json"), "{ 损坏");
        Assert.Null(host.TryReadRunState(run.RunId)); // 读取失败不抛出——回执兜底成立
    }

    [Fact]
    public async Task ArmMount_AppendToRealWaitingDriver_StalesIt_ConvergesInterrupted_BindingsPreserved()
    {
        // 七轮 重要2 真实驱动证据：向在等运行的记录追加挂载身份必然使驱动持有对象过期（结构性，非毫秒窗）——
        // 暂停释放等待时驱动下一次写回（Pause）即响亮修订冲突，观察器按 R4.6 收敛 Interrupted
        // （普通触发等待无未决外部事实，不是 Unknown）；两条受理绑定全保留、无任何提交、驱动出册
        var wf = Seed("流程A", withTrigger: true);
        var (host, boundary) = MakeHost();

        var first = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", mode: StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.Accepted, first.Outcome);
        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.State == WorkflowRunState.Waiting); // 真实驱动进等待

        var second = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-20", mode: StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, second.Outcome); // 幂等挂载：身份原子追加到在等运行
        Assert.Equal(2, _runs.Load(run.RunId)!.Handoffs.Count);

        // 暂停打断等待 → 驱动持旧修订对象写回（Pause）必然冲突 → 异常传播 → 观察器收敛
        var pause = host.RequestRunAction(run.RunId, WorkflowRunAction.Pause);
        Assert.Equal(HostActionStatus.Registered, pause.Status);
        // 八轮 建议1：状态到位≠驱动出册——直证观察器 finally 移除登记后才算不留假挂载
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.State == WorkflowRunState.Interrupted && !host.HasDrive(wf));

        var disk = _runs.Load(run.RunId)!;
        Assert.Equal(2, disk.Handoffs.Count); // 受理事实不丢（修订守卫挡住旧对象覆盖）
        Assert.Equal("timer:t1:2026-09-19", disk.Handoffs[0].IntentKey);
        Assert.Equal("timer:t1:2026-09-20", disk.Handoffs[1].IntentKey);
        Assert.Contains("RunRecordConflictException", disk.Note); // 旧对象写回冲突类型留痕
        Assert.Contains("按Interrupted收敛", disk.Note); // 非 Unknown（无未决外部事实）
        Assert.Empty(boundary.Submissions); // 从未提交

        await host.ShutdownAsync();
        var after = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-21", mode: StartupHandoffModes.ArmTrigger));
        Assert.Equal(HandoffOutcome.Rejected, after.Outcome); // 驱动出册+宿主关闭——不留假挂载窗口
        Assert.Equal(HandoffReasonCodes.Shutdown, after.ReasonCode);
    }

    [Fact]
    public async Task ArmLaunch_TriggerRemovedBeforeDrive_ConvergesInterrupted_NoSubmission()
    {
        // 七轮 重要3：arm 前提在驱动实际使用的定义快照上复验——受理（快照 A 有 trigger.time）→驱动重载
        // （快照 B 触发器已被移除）窗口内前提失效时：受理事实保留、运行收敛 Interrupted（可处置）、绝不顺势提交执行
        var wf = Seed("流程A", withTrigger: true);
        var boundary = new FakeBoundary();
        var runner = new WorkflowRunner(_workflows, _runs, boundary, new NoopPrerequisite(), new NoopTerminal());
        var run = _runs.CreateRun(wf, "rev-1",
            handoff: new HandoffIdentity { IntentKey = "timer:t1:2026-09-19", ExecutionId = "e1", StepId = "s1", Mode = "armTrigger" });

        // 受理→驱动窗口：流程被改为无任何触发器（新修订）
        var snapshot = _workflows.LoadSnapshot(wf);
        snapshot.Document.Triggers.Clear();
        _workflows.Save(snapshot.Document, snapshot.Revision);

        var result = await runner.StartExistingRunAsync(run.RunId, CancellationToken.None, armTriggerLaunch: true);

        Assert.Equal(WorkflowRunState.Interrupted, result.State); // 可处置状态，未激活执行
        Assert.Empty(boundary.Submissions); // 绝不顺势提交
        var disk = _runs.Load(run.RunId)!;
        Assert.Single(disk.Handoffs); // 受理事实保留
        Assert.Contains("挂载前提失效", disk.Note);
    }

    // ===== 八轮处置回归：重要1/重要2/建议2 =====

    [Fact]
    public async Task ArmLaunch_PremiseLostBetweenPrecheckAndDrive_ReceiptHonest_NoSubmission()
    {
        // 八轮 重要1 + 九轮 建议1 宿主全链（runnerFactory 接缝确定性注入定义变更）：
        // 变更时点=受理预检（快照 A 有 trigger.time）之后、权威预检与受理落盘之前——驱动重载见快照 B（无触发器），
        // 复验前提失效：Accepted 保留、回执不假报「已挂载」（按状态如实「运行已中断」）、状态 Interrupted、零提交
        var wf = Seed("流程A", withTrigger: true);
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) =>
            {
                // 受理预检已过（快照 A）→ 驱动重载前把流程改为无触发器（快照 B）
                var snap = _workflows.LoadSnapshot(wf);
                snap.Document.Triggers.Clear();
                _workflows.Save(snap.Document, snap.Revision);
                return new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal());
            },
            () => (true, null),
            () => true,
            () => new ControlStatus { TaskRunning = false });

        var result = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", mode: StartupHandoffModes.ArmTrigger));

        Assert.Equal(HandoffOutcome.Accepted, result.Outcome); // 受理事实保留
        Assert.Equal(WorkflowRunState.Interrupted, result.CurrentRunState); // 回执状态诚实
        Assert.DoesNotContain("已挂载", result.Reason); // 不假报挂载
        Assert.Contains("运行已中断", result.Reason); // 按状态如实描述，原因指向运行备注（九轮 重要1：不用状态反推原因）
        Assert.Empty(boundary.Submissions);
        var run = Assert.Single(_runs.List());
        Assert.Single(run.Handoffs);
        Assert.Contains("挂载前提失效", run.Note);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StateChangedSubscriberThrows_AcceptanceReceiptPreserved()
    {
        // 八轮 重要2 + 九轮 建议2 + 十轮 建议1：通知订阅者异常被隔离——已受理结论不反转（仍 Accepted）、
        // 同键重放 AlreadyAccepted、驱动最终出册；隔离留痕 ≥2 条 = LaunchDrive 与观察器 finally 两处通知的
        // 订阅者异常均被隔离（证明范围=通知隔离助手；观察器其他 _log 出口的非隔离残余边界见设计稿十轮记录）
        var wf = Seed("流程A");
        var boundary = new FakeBoundary();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, logs.Enqueue,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null), () => true, () => new ControlStatus { TaskRunning = false });
        host.StateChanged += (_, _) => throw new InvalidOperationException("订阅者爆炸");

        var result = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19"));
        Assert.Equal(HandoffOutcome.Accepted, result.Outcome); // 通知异常不把已受理反转为异常回执

        var replay = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", executionId: "exec-replay"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);
        Assert.Equal(result.RunId, replay.RunId);

        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.IsTerminal && !host.HasDrive(wf)); // 驱动出册
        await WaitUntilAsync(() => logs.Count(m => m.Contains("状态通知订阅者异常")) >= 2); // 两处通知均被隔离留痕
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task StateChangedAndIsolationLogBothThrow_AcceptanceReceiptPreserved()
    {
        // 九轮 重要2：隔离边界自身成立——订阅者异常 + 隔离留痕日志异常双重抛出，受理结论仍不反转、驱动出册
        var wf = Seed("流程A");
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null,
            msg => { if (msg.Contains("状态通知订阅者异常")) throw new InvalidOperationException("日志爆炸"); },
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null), () => true, () => new ControlStatus { TaskRunning = false });
        host.StateChanged += (_, _) => throw new InvalidOperationException("订阅者爆炸");

        var result = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19"));
        Assert.Equal(HandoffOutcome.Accepted, result.Outcome);

        var replay = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", executionId: "exec-replay"));
        Assert.Equal(HandoffOutcome.AlreadyAccepted, replay.Outcome);

        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.IsTerminal && !host.HasDrive(wf));
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task PausedStop_SubscriberThrows_EffectiveReceiptPreserved_RunCancelled()
    {
        // 九轮 建议2：暂停态显式停止路径的通知隔离——订阅者异常不反转 Effective 回执，运行终态 Cancelled 落盘
        var wf = Seed("流程A");
        var (host, _) = MakeHost();
        host.StateChanged += (_, _) => throw new InvalidOperationException("订阅者爆炸");
        var rec = _runs.CreateRun(wf, "rev-1");
        var paused = _runs.Load(rec.RunId)!;
        paused.State = WorkflowRunState.Paused;
        _runs.Update(paused);

        var result = host.RequestRunAction(rec.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(WorkflowRunState.Cancelled, _runs.Load(rec.RunId)!.State);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task ArmLaunch_WaitDriveFault_ConvergesInterrupted_ReceiptDoesNotMisattribute()
    {
        // 九轮 重要1 反例回归：触发器合法（前提复验通过）但等待驱动异常（DelayAsync 故障注入）——
        // 同样收敛 Interrupted，回执/留痕绝不误报「挂载前提失效」（Interrupted 是通用收敛态，不按状态反推原因）
        var wf = Seed("流程A", withTrigger: true);
        var boundary = new FakeBoundary();
        var delayCalls = 0;
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal(),
                new WorkflowRunnerOptions
                {
                    DelayAsync = (_, _) => { delayCalls++; throw new InvalidOperationException("延时爆炸"); },
                }),
            () => (true, null), () => true, () => new ControlStatus { TaskRunning = false });

        var result = await host.RegisterHandoffAsync(Req(wf, "timer:t1:2026-09-19", mode: StartupHandoffModes.ArmTrigger));

        // 故障任务在 LaunchDrive 内同步完成→观察器同步收敛（无真异步挂起）——回执时已是 Interrupted（确定性时序）
        Assert.Equal(HandoffOutcome.Accepted, result.Outcome);
        Assert.Equal(WorkflowRunState.Interrupted, result.CurrentRunState);
        Assert.Contains("运行已中断", result.Reason);
        Assert.DoesNotContain("前提失效", result.Reason); // 不得误报前提失效
        Assert.Equal(1, delayCalls); // 十轮 建议2：故障点=首个等待延时（直接证据，非碰巧满足负向断言）

        var run = Assert.Single(_runs.List());
        await WaitUntilAsync(() => _runs.Load(run.RunId)!.State == WorkflowRunState.Interrupted && !host.HasDrive(wf));
        var disk = _runs.Load(run.RunId)!;
        Assert.Contains("驱动异常", disk.Note); // 真实原因=等待驱动故障（非前提失效）
        Assert.Contains("InvalidOperationException", disk.Note); // 异常类型留痕（十轮 建议2）
        Assert.DoesNotContain("挂载前提失效", disk.Note);
        Assert.Empty(boundary.Submissions);
        await host.ShutdownAsync();
    }

    [Fact]
    public void TryAppendBinding_CancelInFinalAttempt_ExitCheck_ThrowsOce()
    {
        // 八轮 建议2 + 九轮 建议1：耗尽出口取消复核——末次尝试内取消到达（先于当次冲突抛出），耗尽出口以 OCE 表现（而非 HandoffError）
        var rec = _runs.CreateRun("wf-aaaaaaaa", "rev-1");
        var request = Req("wf-aaaaaaaa", "timer:t1:2026-09-19");
        using var cts = new CancellationTokenSource();
        var updateCalls = 0;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            TaskCenterHost.TryAppendHandoffBinding(
                id => _runs.Load(id),
                r =>
                {
                    updateCalls++;
                    BumpDiskRevision(r.RunId);
                    if (updateCalls == 3) cts.Cancel(); // 第三次尝试内取消（先于当次 Update 冲突抛出）
                    _runs.Update(r);
                },
                rec.RunId, request, "受理备注",
                _ => null, cts.Token, out _));

        Assert.Equal(3, updateCalls);
        Assert.Empty(_runs.Load(rec.RunId)!.Handoffs);
    }
}
