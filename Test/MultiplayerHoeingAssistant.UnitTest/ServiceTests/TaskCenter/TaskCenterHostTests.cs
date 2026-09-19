using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// TaskCenterHost（R4.8 Batch C）组件夹具（设计稿 §4.6 + ASTRA 一轮 B4/B5/I5/I8）：
/// 启动屏障（RecoverOnStart 先于启动）、离线响亮拒绝与同流程互斥分开测、活动态全集合护栏
/// （Unknown 禁止新跑、Interrupted 允许另开不动旧记录）、并发 Start 互斥（预留覆盖 CreateRun 窗口）、
/// Paused 停止宿主终态化（在飞拒绝）、驱动异常收敛（在飞→Unknown，不留 Running 僵尸）。
/// 假 Runner 工厂 + 假就绪判定接缝，无真实 IPC。
/// </summary>
public class TaskCenterHostTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public TaskCenterHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tchost-" + Guid.NewGuid().ToString("N")[..8]);
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
        public List<string> CancelRequests { get; } = new();
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count));
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => BoundaryTerminalResult.Observed(OnAwait is not null ? await OnAwait(jobId, ct) : "succeeded");

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelRequests.Add(jobId);
            return Task.CompletedTask;
        }
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

    /// <summary>测试接缝宿主：runnerFactory=假边界组装；readinessOverride=假就绪判定（离线拒绝与互斥分开测）。</summary>
    private (TaskCenterHost Host, FakeBoundary Boundary) MakeHost(bool ready = true, Action<string>? log = null)
    {
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, log,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => ready ? (true, null) : (false, "BGI 离线（测试就绪判定）"));
        return (host, boundary);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 300)
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Start_Offline_LoudReject_NoRunCreated()
    {
        var workflowId = Seed("离线流程");
        var (host, _) = MakeHost(ready: false);

        var result = await host.StartWorkflowAsync(workflowId);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("离线", result.Message);
        Assert.Empty(_runs.List()); // 未建运行
    }

    [Fact]
    public async Task Start_ActiveRunExists_LoudReject()
    {
        var workflowId = Seed("互斥流程");
        var (host, boundary) = MakeHost();
        var gate = new TaskCompletionSource();
        boundary.OnAwait = async (_, _) => { await gate.Task; return "succeeded"; };

        var first = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, first.Status);
        await WaitUntilAsync(() => _runs.List().FirstOrDefault()?.State == WorkflowRunState.Running);

        var second = await host.StartWorkflowAsync(workflowId);

        Assert.Equal(HostActionStatus.Unavailable, second.Status);
        Assert.Contains("活动运行", second.Message);
        gate.TrySetResult(); // 放行首个运行收尾
        await WaitUntilAsync(() => !host.IsDriving(workflowId));
        Assert.Equal(WorkflowRunState.Succeeded, _runs.List().Single().State);
    }

    [Fact]
    public async Task Start_Concurrent_OnlyOneAccepted()
    {
        var workflowId = Seed("并发流程");
        var (host, boundary) = MakeHost();
        boundary.OnAwait = async (_, ct) => { await Task.Delay(120, ct); return "succeeded"; };

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.StartWorkflowAsync(workflowId)));

        Assert.Equal(1, results.Count(r => r.Status == HostActionStatus.Registered)); // 预留覆盖 CreateRun 窗口
        Assert.Equal(7, results.Count(r => r.Status == HostActionStatus.Unavailable));
        await WaitUntilAsync(() => !host.IsDriving(workflowId));
        Assert.Single(_runs.List()); // 只建了一个运行
    }

    [Fact]
    public async Task Start_UnknownRunExists_Rejected_InterruptedAllowedAndUntouched()
    {
        var workflowId = Seed("对账流程");
        var (host, _) = MakeHost();

        // 手工造 Unknown 运行（在飞事实未对账）→ 禁止同流程新跑
        var unknownRun = _runs.CreateRun(workflowId, "rev-x");
        unknownRun.State = WorkflowRunState.Unknown;
        _runs.Update(unknownRun);
        var r1 = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Unavailable, r1.Status);
        Assert.Contains("Unknown", r1.Message);

        // Unknown 经人工对账终态化后，Interrupted 记录不阻塞新跑且原样保留
        unknownRun.State = WorkflowRunState.Cancelled;
        _runs.Update(unknownRun);
        var interrupted = _runs.CreateRun(workflowId, "rev-x");
        interrupted.State = WorkflowRunState.Interrupted;
        _runs.Update(interrupted);
        var r2 = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, r2.Status);
        await WaitUntilAsync(() => !host.IsDriving(workflowId));
        Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(interrupted.RunId)!.State); // 旧记录不动
    }

    [Fact]
    public void RequestAction_PausedStop_FinalizesLocally()
    {
        var workflowId = Seed("暂停停止");
        var (host, _) = MakeHost();
        var run = _runs.CreateRun(workflowId, "rev-x");
        run.State = WorkflowRunState.Paused; // 暂停态无驱动（引擎控制登记已移除）
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status); // 宿主终态化（不无声吞）
        var fresh = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Cancelled, fresh.State);
        Assert.Contains("暂停态显式停止", fresh.Note);
    }

    [Fact]
    public void RequestAction_PausedStop_InflightSubmission_RejectedForReconcile()
    {
        var workflowId = Seed("暂停在飞");
        var (host, _) = MakeHost();
        var run = _runs.CreateRun(workflowId, "rev-x");
        run.State = WorkflowRunState.Paused;
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-test", NodeId = "n-1", Intent = SubmitIntentState.Submitted, // 在飞：已发送未观察终态
        };
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status); // 有在飞事实 → 拒绝对账优先
        Assert.Contains("对账", result.Message);
        Assert.Equal(WorkflowRunState.Paused, _runs.Load(run.RunId)!.State); // 状态不动
    }

    [Fact]
    public void RequestAction_TerminalRun_UnavailableWithGuidance()
    {
        var workflowId = Seed("动作闭环");
        var (host, _) = MakeHost();
        var run = _runs.CreateRun(workflowId, "rev-x");
        run.State = WorkflowRunState.Succeeded;
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.SkipCurrent);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("终态", result.Message); // 结构化反馈，不无声吞
    }

    [Fact]
    public async Task DriveException_InflightSubmission_ConvergesToUnknown_NotRunningZombie()
    {
        var workflowId = Seed("异常收敛");
        var (host, boundary) = MakeHost();
        boundary.OnAwait = (_, _) => throw new InvalidOperationException("假边界爆炸"); // 已受理未观察 → 在飞

        var started = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, started.Status);
        await WaitUntilAsync(() => !host.IsDriving(workflowId));

        var run = _runs.List().Single();
        Assert.Equal(WorkflowRunState.Unknown, run.State); // 一轮 B5：在飞事实 → Unknown（不留 Running 僵尸）
        Assert.Contains("驱动异常", run.Note);
    }

    [Fact]
    public async Task RecoverBarrier_ScanBeforeStart_MarksZombieInterrupted()
    {
        var workflowId = Seed("屏障流程");
        // 造一条「上次进程遗留」的 Running 僵尸（本进程未驱动）
        var zombie = _runs.CreateRun(workflowId, "rev-x");
        zombie.State = WorkflowRunState.Running;
        _runs.Update(zombie);

        var logs = new List<string>();
        var (host, _) = MakeHost(log: logs.Add);

        var first = await host.StartWorkflowAsync(workflowId); // 屏障：先扫描标记再判互斥

        Assert.Equal(HostActionStatus.Registered, first.Status);
        Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(zombie.RunId)!.State); // 僵尸被标记后可另开新跑
        Assert.Contains(logs, l => l.Contains("恢复扫描"));
        await WaitUntilAsync(() => !host.IsDriving(workflowId));
    }
}