using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 2026-09-20 修复夹具：任务中心执行入口在 BGI 离线时经 ensure 委托自动拉起+有界等待就绪（此前直接「BGI 离线」拒绝）。
/// 合同钉死：
/// - 仅 Start/Resume/启动移交三个执行入口触发 ensure（锁外、就绪检查之前）；纯查询/监控路径不触达；
/// - ensure 使就绪 → 原流程继续（受理/驱动不变）；ensure 失败/异常 → 响亮拒绝且零副作用（不建运行、不消耗身份）；
/// - 未配置 ensure（测试接缝/默认）→ 保持原「BGI 离线」拒绝行为一字不变；已就绪 → ensure 不被调用。
/// </summary>
public class TaskCenterEnsureReadyTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public TaskCenterEnsureReadyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tc-ensure-" + Guid.NewGuid().ToString("N")[..8]);
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

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count));
        }

        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
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

    private string Seed(string name)
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
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    /// <summary>readyRef 闭包控制就绪实况；ensure 委托可空（null=不配置，测现状不变）。</summary>
    private (TaskCenterHost Host, FakeBoundary Boundary) MakeHost(
        Func<bool> readyRef, Func<CancellationToken, Task<string?>>? ensure,
        Func<ControlStatus?>? snapshotProvider = null, TimeSpan? snapshotBudget = null)
    {
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => readyRef() ? (true, null) : (false, "BGI 离线（测试就绪判定）"),
            () => true,
            snapshotProvider ?? (() => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false }),
            ensure, snapshotBudget);
        return (host, boundary);
    }

    private static StartupHandoffRequest Req(string workflowId, string intentKey)
        => new()
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            StepId = "step-1",
            IntentKey = intentKey,
            WorkflowId = workflowId,
            Mode = StartupHandoffModes.Start,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 500)
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "等待条件超时未满足");
    }

    [Fact]
    public async Task Start_NotReady_EnsureMakesReady_StartProceeds()
    {
        var workflowId = Seed("日常流程");
        var ready = false;
        var ensureCalls = 0;
        var (host, boundary) = MakeHost(() => ready, _ =>
        {
            ensureCalls++;
            ready = true; // 模拟：拉起 BGI + 通道就绪
            return Task.FromResult<string?>(null);
        });

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, ensureCalls);
        await WaitUntilAsync(() => _runs.List().Single().State == WorkflowRunState.Succeeded);
        Assert.Single(boundary.Submissions);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Start_EnsureFails_Unavailable_ZeroSideEffect()
    {
        var workflowId = Seed("日常流程");
        var ready = false;
        var (host, _) = MakeHost(() => ready,
            _ => Task.FromResult<string?>("BGI 未运行且启动调用失败（请检查助手配置中的 BGI 路径；未发送任何任务）"));

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("启动调用失败", result.Message);
        Assert.Empty(_runs.List()); // 零副作用：未建运行、未消耗身份
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Start_EnsureThrows_Unavailable_ZeroSideEffect()
    {
        var workflowId = Seed("日常流程");
        var ready = false;
        var (host, _) = MakeHost(() => ready,
            _ => throw new InvalidOperationException("模拟启动器爆炸"));

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("自动启动 BGI 过程异常", result.Message);
        Assert.Empty(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Start_AlreadyReady_EnsureNotCalled()
    {
        var workflowId = Seed("日常流程");
        var ensureCalls = 0;
        var (host, boundary) = MakeHost(() => true, _ =>
        {
            ensureCalls++;
            return Task.FromResult<string?>(null);
        });

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, ensureCalls); // 已就绪不打扰
        await WaitUntilAsync(() => _runs.List().Single().State == WorkflowRunState.Succeeded);
        Assert.Single(boundary.Submissions);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Start_NoEnsureDelegate_KeepsLegacyOfflineRejection()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(() => false, null);

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("BGI 离线（测试就绪判定）", result.Message); // 原行为一字不变
        Assert.Empty(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Resume_NotReady_EnsureMakesReady_ResumeProceeds()
    {
        var workflowId = Seed("日常流程");
        var interrupted = _runs.CreateRun(workflowId, "rev-1");
        interrupted.State = WorkflowRunState.Interrupted;
        _runs.Update(interrupted);

        var ready = false;
        var ensureCalls = 0;
        var (host, boundary) = MakeHost(() => ready, _ =>
        {
            ensureCalls++;
            ready = true;
            return Task.FromResult<string?>(null);
        });

        var result = await host.ResumeRunAsync(interrupted.RunId);

        Assert.True(result.Ok, result.Message); // 受理即合同（驱动推进由引擎负责，Resume 终态不在本夹具目标）
        Assert.Equal(1, ensureCalls);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_NotReady_EnsureMakesReady_Accepted()
    {
        var workflowId = Seed("日常流程");
        var ready = false;
        var ensureCalls = 0;
        var (host, _) = MakeHost(() => ready, _ =>
        {
            ensureCalls++;
            ready = true;
            return Task.FromResult<string?>(null);
        });

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:ensure-1"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.Accepted, result.Outcome);
        Assert.Equal(1, ensureCalls);
        await WaitUntilAsync(() => _runs.List().Single().State == WorkflowRunState.Succeeded);
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_EnsureFails_RejectedNotReady_ZeroSideEffect()
    {
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(() => false,
            _ => Task.FromResult<string?>("BGI 已启动但外部接口通道在 150 秒内未就绪（未发送任何任务）"));

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:ensure-2"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.Rejected, result.Outcome);
        Assert.Equal(HandoffReasonCodes.NotReady, result.ReasonCode);
        Assert.Empty(_runs.List()); // 不消耗 IntentKey、不留运行记录
        await host.ShutdownAsync();
    }

    // ================= 会诊处置夹具（异步竞态/取消/快照窗口） =================

    [Fact]
    public async Task Handoff_ConcurrentSameKey_DuringEnsureWait_OneAcceptedOneAlreadyAccepted()
    {
        // 会诊 阻断1/重要4：两个同 IntentKey 请求都未命中台账、双双进入 ensure 等待；
        // 放行后临界区台账双检必须让后者拿到 AlreadyAccepted（受理事实优先），而非拒绝或双跑
        var workflowId = Seed("日常流程");
        var ready = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var (host, _) = MakeHost(() => ready, async ct =>
        {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct); // 委托内不置 ready——否则第二路会在就绪预检直通，永不进入委托（会诊二轮 重要2 时序修正）
            return null;
        });

        var first = host.RegisterHandoffAsync(Req(workflowId, "manual:race-1"), CancellationToken.None);
        var second = host.RegisterHandoffAsync(Req(workflowId, "manual:race-1"), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 2);
        ready = true; // 两路都已在等待 → 模拟 BGI 就绪 → 放行
        release.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(o => o.Outcome == HandoffOutcome.Accepted));
        Assert.Equal(1, outcomes.Count(o => o.Outcome == HandoffOutcome.AlreadyAccepted));
        Assert.Equal(outcomes[0].RunId, outcomes[1].RunId); // 同一运行，绝无双跑
        Assert.Single(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_EnsureFails_AfterConcurrentCommit_LedgerRecheckReturnsAlreadyAccepted()
    {
        // 会诊 阻断1 钉死：ensure 等待期间同键已被并发受理（台账出现绑定），ensure 失败分支
        // 必须经台账复核回执 AlreadyAccepted——不得把「已受理」误报为 NotReady 拒绝
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(() => false, _ =>
        {
            _runs.CreateRun(workflowId, "rev-1", handoff: new HandoffIdentity
            {
                IntentKey = "manual:ledger-race", ExecutionId = "exec-other", StepId = "step-1", Mode = StartupHandoffModes.Start,
            });
            return Task.FromResult<string?>("BGI 已启动但外部接口通道在 150 秒内未就绪（未发送任何任务）");
        });

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:ledger-race"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.AlreadyAccepted, result.Outcome);
        Assert.Single(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_ShutdownDuringEnsure_Cancelled_ZeroSideEffect()
    {
        // 会诊 阻断2：ensure 等待纳入退出管理——宿主关闭即取消，OCE 沿启动链传播（与既有受理前取消纪律一致）
        var workflowId = Seed("日常流程");
        var ensureEntered = new TaskCompletionSource();
        var (host, _) = MakeHost(() => false, async ct =>
        {
            ensureEntered.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // 永不完成的等待，仅取消可退出
            return null;
        });

        var pending = host.RegisterHandoffAsync(Req(workflowId, "manual:shutdown-1"), CancellationToken.None);
        await ensureEntered.Task;
        await host.ShutdownAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(_runs.List()); // 未受理、未落盘
    }

    [Fact]
    public async Task Start_ShutdownDuringEnsure_UnavailableCancelled_ZeroSideEffect()
    {
        // 会诊 阻断2：面板路径无外部 ct——退出取消映射为响亮 Unavailable（不抛、不留副作用）
        var workflowId = Seed("日常流程");
        var ensureEntered = new TaskCompletionSource();
        var (host, _) = MakeHost(() => false, async ct =>
        {
            ensureEntered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });

        var pending = host.StartWorkflowAsync(workflowId);
        await ensureEntered.Task;
        await host.ShutdownAsync();

        var result = await pending;
        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("退出", result.Message);
        Assert.Empty(_runs.List());
    }

    [Fact]
    public async Task Handoff_SnapshotArrivesWithinBoundedWait_Accepted()
    {
        // 会诊 重要1：冷启动刚就绪时快照未到达 → 有界兜底窗口内到达即放行（旧序会立刻 StatusUncertain 挡死）
        var workflowId = Seed("日常流程");
        var snapshotCalls = 0;
        var (host, _) = MakeHost(() => true, null, () =>
        {
            snapshotCalls++;
            return snapshotCalls > 2 ? new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false } : null;
        });

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:snap-1"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.Accepted, result.Outcome);
        Assert.True(snapshotCalls > 2, "快照兜底等待未生效");
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_SnapshotPersistentlyMissing_RejectedStatusUncertain()
    {
        // 会诊二轮 建议：兜底窗口耗尽仍无快照 → 原语义 StatusUncertain 不变（不放宽「状态不可考则拒绝」）
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(() => true, null, () => null,
            TimeSpan.FromMilliseconds(300)); // 测试接缝缩短预算，语义不变

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:snap-2"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.Rejected, result.Outcome);
        Assert.Equal(HandoffReasonCodes.StatusUncertain, result.ReasonCode);
        Assert.Empty(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Handoff_SnapshotBusy_RejectedBgiBusy()
    {
        // 会诊二轮 建议：兜底窗口内到达的是忙态快照 → BgiBusy 冲突拒绝（就绪≠可跑，无双跑纪律不破）
        var workflowId = Seed("日常流程");
        var (host, _) = MakeHost(() => true, null,
            () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "9:900", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = true, CurrentTaskName = "别处在跑" });

        var result = await host.RegisterHandoffAsync(Req(workflowId, "manual:snap-3"), CancellationToken.None);

        Assert.Equal(HandoffOutcome.Rejected, result.Outcome);
        Assert.Equal(HandoffReasonCodes.BgiBusy, result.ReasonCode);
        Assert.Empty(_runs.List());
        await host.ShutdownAsync();
    }

    [Fact]
    public async Task Vm_EnsureReady_PreCancelled_ThrowsImmediately_ZeroSideEffect()
    {
        // 会诊三轮 建议（生产 VM 路径接缝）：预取消令牌 → 确保在通道门排队处即取消退出，
        // 不触碰进程枚举/RestartBgi/探测（零副作用）；证明取消链贯通到生产确保方法入口
        var vm = new MainViewModel();
        var method = typeof(MainViewModel).GetMethod("EnsureTaskCenterExecutionReadyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException("确保方法不存在");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = (Task<string?>)method.Invoke(vm, [cts.Token])!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Vm_TimerProbePath_NoBudget_ReallyWaitsChannelGate()
    {
        // 会诊四轮 阻断钉死：无预算轮询入口（deadlineUtc=null）不得在门被占用时误判「预算耗尽」秒回——
        // 必须真实等待通道门；放门后正常完成（观察者模式直通 false，不触管道）
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new AssistConfig { ObserverMode = true });
        var gate = (SemaphoreSlim)typeof(MainViewModel).GetField("_externalChannelGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm)!;
        var probe = typeof(MainViewModel).GetMethod("TryEstablishExternalChannelAsync",
            BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException("探测方法不存在");

        await gate.WaitAsync(); // 占用通道门
        Task<bool> pending;
        try
        {
            pending = (Task<bool>)probe.Invoke(vm, null)!;
            var early = await Task.WhenAny(pending, Task.Delay(300));
            Assert.NotSame(pending, early); // 门被占用时不得秒回（误诊预算耗尽=轮询路径被禁用）
        }
        finally
        {
            gate.Release();
        }
        Assert.False(await pending!); // 放门后正常进入 Core（观察者模式 → false，不触管道）
    }
}
