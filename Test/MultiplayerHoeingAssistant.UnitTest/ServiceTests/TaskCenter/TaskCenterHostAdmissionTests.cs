using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5.2 B2（E1）宿主流线仲裁接线夹具（冻结稿 v8 §0/§2.2/§8 落地）：
/// 受理链全贯（仲裁受理→驱动启动→运行终态→操作终局回写）、F11 阻断无租约副作用、占用需抢占确认、
/// 双流程并发同一仲裁面唯一胜者、确定拒绝（可重试）/未知（待对账）分类与清理/保留语义、重启新实例恢复后再启动。
/// 假 Runner 工厂+事实/发送接缝注入，无真实 IPC；场景施工方内置，owner 0 点击。
/// </summary>
public class TaskCenterHostAdmissionTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public TaskCenterHostAdmissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcadm-" + Guid.NewGuid().ToString("N")[..8]);
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

        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>接线宿主：admissionWired=true + 事实/发送/屏障接缝注入；就绪判定恒真（仲裁面行为与既有就绪护栏分开测）。</summary>
    private readonly List<string> _hostLog = new();

    private TaskCenterHost MakeWiredHost(FakeBoundary boundary, TaskCenterAdmissionSeams seams)
        => new(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, m => _hostLog.Add(m),
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null),
            admissionWired: true, admissionSeams: seams);

    private LeaseReadResult ReadLease()
        => new ArbitrationLeaseStore(Path.Combine(_dir, "arbitration")).Read();

    /// <summary>入队收齐闸门（与 B1 夹具同原理：计数满 expected 才放行快照——并发场景确定性）。</summary>
    private static AdmissionBarriers GatedBarrier(int expected)
    {
        var arrived = 0;
        var allEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new AdmissionBarriers
        {
            AfterEnqueue = () =>
            {
                if (Interlocked.Increment(ref arrived) == expected) allEnqueued.TrySetResult();
                return Task.CompletedTask;
            },
            BeforeRoundSnapshot = () => allEnqueued.Task,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 1000) // 10s 预算抗并行负载
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
    }

    private static IReadOnlyList<OperationRecord> Ops(LeaseReadResult read)
        => read.File?.Handoff?.Operations ?? [];

    /// <summary>轮询安全读取：锁文件（FileShare.None）瞬时争用按「尚未就绪」处理（下轮再读，不炸谓词）。</summary>
    private IReadOnlyList<OperationRecord> SafeOps()
    {
        try { return Ops(ReadLease()); }
        catch (IOException) { return []; }
    }

    // ── 1. 受理链全贯：仲裁受理→驱动启动→运行终态→操作终局回写（§0 统一链路）──

    [Fact]
    public async Task PanelStart_Accepted_DriveLaunched_OperationTerminalized()
    {
        var workflowId = Seed("仲裁受理流程");
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams());
        try
        {
            var result = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Registered, result.Status);
            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            Assert.Single(boundary.Submissions); // 驱动实际提交节点（受理后真实启动）

            await WaitUntilAsync(() => host.ListActiveRuns().Count == 0); // 驱动收尾
            await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState == OperationRequestState.TerminalCompleted));
            var lease = ReadLease();
            var op = Assert.Single(Ops(lease));
            Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
            Assert.NotNull(op.RunBinding); // candidateId→runId 绑定持久化（§3.2）
            Assert.Equal("manual", op.Candidate!.Namespace);
            Assert.Equal("start", op.Intent);
            // I3 可证伪断言（会诊重要-2）：占位符必须已被门面身份回填，且回填值=本操作 RequestIdentity（32 位 Guid N）
            Assert.StartsWith("manual:panel:", op.Candidate.TriggerOccurrenceId);
            Assert.DoesNotContain("{requestIdentity}", op.Candidate.TriggerOccurrenceId);
            Assert.Equal(op.RequestIdentity, op.Candidate.TriggerOccurrenceId!["manual:panel:".Length..]);
            var run = _runs.Load(op.RunBinding!);
            Assert.Equal(WorkflowRunState.Succeeded, run!.State);
            Assert.Null(lease.File!.Handoff!.Submission); // 未决提交已关闭
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 2. F11 阻断：先于租约排序（无 Operations 副作用）+预建运行终态化清理 ──

    [Fact]
    public async Task PanelStart_F11Blocked_NoLeaseSideEffect_RunCleaned()
    {
        var workflowId = Seed("F11流程");
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { F11Active = true });
        try
        {
            var result = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("F11", result.Message);
            Assert.Empty(boundary.Submissions);

            Assert.Empty(Ops(ReadLease())); // 租约零副作用（§7.1：F11 先于一切排序）
            var run = Assert.Single(_runs.List());
            Assert.Equal(WorkflowRunState.Cancelled, run.State); // 预建运行已终态化清理（不留 Planned 僵尸）
            Assert.Contains("F11", run.Note);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 3. 执行占用：需抢占确认（交接存续非终局）+运行清理+不发送 ──

    [Fact]
    public async Task PanelStart_Occupied_NeedPreemptConfirm_RunCleaned_OpStaysQueued()
    {
        var workflowId = Seed("占用流程");
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { Occupied = true });
        try
        {
            var result = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("占用", result.Message);
            Assert.Empty(boundary.Submissions);

            var op = Assert.Single(Ops(ReadLease()));
            Assert.Equal(OperationRequestState.Queued, op.RequestState); // 交接存续非终局（§3.3；抢占确认归后续阶段）
            var run = Assert.Single(_runs.List());
            Assert.Equal(WorkflowRunState.Cancelled, run.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 4. 双流程并发：同一仲裁面唯一胜者（无双跑逻辑准入互斥）+落败方清理 ──

    [Fact]
    public async Task ConcurrentPanelStart_TwoFlows_ExactlyOneWinner()
    {
        var flowA = Seed("并发流程A");
        var flowB = Seed("并发流程B");
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { Barriers = GatedBarrier(2) });
        try
        {
            var results = await Task.WhenAll(host.StartWorkflowAsync(flowA), host.StartWorkflowAsync(flowB));
            Assert.Equal(1, results.Count(r => r.Status == HostActionStatus.Registered));
            Assert.Equal(1, results.Count(r => r.Status == HostActionStatus.Unavailable));
            Assert.Contains("未获选", results.First(r => r.Status == HostActionStatus.Unavailable).Message);

            await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState == OperationRequestState.TerminalCompleted));
            Assert.Single(boundary.Submissions); // 全局只有一个驱动真实提交（无双跑）
            var ops = Ops(ReadLease());
            Assert.Equal(2, ops.Count);
            var loser = Assert.Single(ops.Where(o => o.RequestState == OperationRequestState.NotSelected));
            var loserRun = _runs.Load(loser.RunBinding!);
            Assert.Equal(WorkflowRunState.Cancelled, loserRun!.State); // 落败方预建运行已清理
            var winner = Assert.Single(ops.Where(o => o.RequestState == OperationRequestState.TerminalCompleted));
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(winner.RunBinding!)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 5. 确定拒绝（可重试）：Submission 先关闭+窗口派生+运行清理 ──

    [Fact]
    public async Task PanelStart_SenderRetryableReject_RunCleaned_OpRetryableRejected()
    {
        var workflowId = Seed("拒绝流程");
        var boundary = new FakeBoundary();
        var seams = new TaskCenterAdmissionSeams
        {
            SenderOverride = _ => Task.FromResult<SendOutcome>(new SendOutcome.Rejected("task_running", true, "test:busy")),
        };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            var result = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("可重试", result.Message);
            Assert.Empty(boundary.Submissions);

            var lease = ReadLease();
            var op = Assert.Single(Ops(lease));
            Assert.Equal(OperationRequestState.RetryableRejected, op.RequestState);
            Assert.NotNull(op.RetryWindowDeadlineUtc); // 持久化重试窗口（不重置）
            Assert.Null(lease.File!.Handoff!.Submission); // 确定未受理先关闭
            Assert.Equal(WorkflowRunState.Cancelled, Assert.Single(_runs.List()).State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 6. 发送未知：Submission 未决保留+操作 Reconciling+运行不臆断取消（防双跑）──

    [Fact]
    public async Task PanelStart_SenderUnknown_Reconciling_SubmissionKept_RunNotCleaned()
    {
        var workflowId = Seed("未知流程");
        var boundary = new FakeBoundary();
        var seams = new TaskCenterAdmissionSeams
        {
            SenderOverride = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("test:timeout")),
        };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            var result = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("对账", result.Message);
            Assert.Empty(boundary.Submissions);

            var lease = ReadLease();
            var op = Assert.Single(Ops(lease));
            Assert.Equal(OperationRequestState.Reconciling, op.RequestState); // 保守待对账（不重发）
            Assert.NotNull(lease.File!.Handoff!.Submission); // 未决发送保留（不换键重跑）
            var run = Assert.Single(_runs.List());
            Assert.Equal(WorkflowRunState.Planned, run.State); // 不臆断取消（驱动或在飞——留待既有收敛/对账）
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 7. 重启新实例：所有权重获+恢复扫描幂等+再启动全贯 ──

    [Fact]
    public async Task Restart_NewHostInstance_RecoveryAndStartWorks()
    {
        var flowA = Seed("重启流程A");
        var flowB = Seed("重启流程B");
        var boundary1 = new FakeBoundary();
        var host1 = MakeWiredHost(boundary1, new TaskCenterAdmissionSeams { OwnershipTtlSeconds = 1 });
        var r1 = await host1.StartWorkflowAsync(flowA);
        Assert.Equal(HostActionStatus.Registered, r1.Status);
        await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState == OperationRequestState.TerminalCompleted));
        await host1.ShutdownAsync();

        var boundary2 = new FakeBoundary();
        var host2 = MakeWiredHost(boundary2, new TaskCenterAdmissionSeams { OwnershipTtlSeconds = 1 }); // 同目录新实例=进程重启（TTL=1s 加速接管观察）
        try
        {
            var r2 = await host2.StartWorkflowAsync(flowB);
            Assert.Equal(HostActionStatus.Registered, r2.Status); // 所有权重获+恢复后正常受理
            await WaitUntilAsync(() => boundary2.Submissions.Count == 1);
            await WaitUntilAsync(() => SafeOps().Count(o => o.RequestState == OperationRequestState.TerminalCompleted) == 2);
            Assert.Equal(2, Ops(ReadLease()).Count(o => o.RequestState == OperationRequestState.TerminalCompleted)); // 两笔操作各自终局不串
        }
        finally
        {
            await host2.ShutdownAsync();
        }
    }
}
