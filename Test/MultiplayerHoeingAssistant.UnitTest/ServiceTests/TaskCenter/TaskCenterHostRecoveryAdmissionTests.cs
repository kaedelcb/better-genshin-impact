using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5.2 B2-β（E2）恢复五路径仲裁接线夹具（冻结稿 v8 §5.1 落地）：
/// 恢复专用准入边界全贯（Interrupted 游标重定位/Paused 续行分支记录）、F11 阻断零副作用、
/// 发送未知保守待对账不动原运行、执行占用可重试拒绝不动原运行、启动移交 resume 经准入边界驱动。
/// 假 Runner 工厂+事实/发送接缝注入，无真实 IPC；场景施工方内置，owner 0 点击。
/// </summary>
public class TaskCenterHostRecoveryAdmissionTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public TaskCenterHostRecoveryAdmissionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcrec-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        private readonly object _sync = new();
        private readonly List<string> _submissions = [];

        public bool SingleNativeSupported => false;

        /// <summary>线程安全快照（会诊重要项：并发驱动下 List 非线程安全——每次读取返回副本，
        /// 避免竞态破坏计数而掩盖真双跑/漏计）。</summary>
        public IReadOnlyList<string> Submissions { get { lock (_sync) return _submissions.ToList(); } }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            int seq;
            lock (_sync)
            {
                _submissions.Add(request.Occurrence.NodeId);
                seq = _submissions.Count;
            }

            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + seq));
        }

        /// <summary>首作业终态闸门（真暂停场景：暂停请求先于终态到达，驱动在节点边界收 pause）。</summary>
        public Task? FirstTerminalGate { get; set; }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (FirstTerminalGate is { } gate && jobId == "job-1") await gate;
            return BoundaryTerminalResult.Observed("succeeded");
        }

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

    private string SeedFlow(string name)
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

    /// <summary>双节点流程种子（真暂停场景：节点1终态受闸门控制，节点2续行验证恢复后继提交）。</summary>
    private string SeedTwoNodeFlow(string name)
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
                new WorkflowNode
                {
                    NodeId = "n-2", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置B", Revision = "rev-1" },
                },
            ],
        };
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    /// <summary>预置可恢复运行（Interrupted/Paused——恢复五路径场景由施工方内置，不靠 owner 手工构造）。</summary>
    private WorkflowRunRecord SeedResumableRun(string workflowId, WorkflowRunState state)
    {
        var snapshot = _workflows.LoadSnapshot(workflowId);
        var run = _runs.CreateRun(workflowId, snapshot.Revision, note: "预置可恢复运行（B2-β 夹具）");
        run.State = state;
        _runs.Update(run);
        return run;
    }

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

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 1000) // 10s 预算抗并行负载
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
    }

    private static IReadOnlyList<OperationRecord> Ops(LeaseReadResult read)
        => read.File?.Handoff?.Operations ?? [];

    private IReadOnlyList<OperationRecord> SafeOps()
    {
        try { return Ops(ReadLease()); }
        catch (IOException) { return []; }
    }

    // ── 1. Interrupted 恢复全贯：准入受理→驱动恢复→运行终态→恢复操作终局回写（§5.1 行2+统一链路）──

    [Fact]
    public async Task ResumeRun_Interrupted_Accepted_DriveResumes_OperationTerminalized()
    {
        var workflowId = SeedFlow("中断恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams());
        try
        {
            var result = await host.ResumeRunAsync(seeded.RunId!);
            Assert.Equal(HostActionStatus.Registered, result.Status);
            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            Assert.Single(boundary.Submissions); // 恢复驱动真实提交后继节点

            await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState == OperationRequestState.TerminalCompleted));
            var lease = ReadLease();
            var op = Assert.Single(Ops(lease));
            Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
            Assert.Equal("resume", op.Intent);
            Assert.Equal("resume", op.Candidate!.Namespace); // namespace=resume（E2 入口类别）
            Assert.Equal(seeded.RunId, op.RunBinding); // 恢复绑定=原运行（不改写不新建）
            Assert.StartsWith("resume:interrupted-relocate:", op.Candidate.TriggerOccurrenceId); // 分支留痕
            Assert.Null(lease.File!.Handoff!.Submission); // 未决发送已关闭
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(seeded.RunId!)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 2. Paused 续行：分支记录 paused-continue（§5.1 行1——仅解除调度暂停的准入口径留痕）──

    [Fact]
    public async Task ResumeRun_Paused_BranchRecorded_Accepted()
    {
        // 真暂停场景（Paused 不带跨重启——RecoverOnStart 会把无驱动 Paused 收敛 Interrupted，R4 合同）：
        // 同宿主会话内 启动→节点1在飞→请求暂停→放行终态→节点边界收 pause→Paused→恢复→节点2 提交→终态。
        var workflowId = SeedTwoNodeFlow("暂停续行流程");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new FakeBoundary { FirstTerminalGate = gate.Task };
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams());
        try
        {
            var start = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            var runId = Assert.Single(host.ListActiveRuns()).RunId!;

            var pause = host.RequestRunAction(runId, WorkflowRunAction.Pause);
            Assert.True(pause.Ok, "暂停请求应被受理：" + pause.Message);
            gate.TrySetResult(); // 放行节点1终态——驱动在下一节点边界收 pause
            await WaitUntilAsync(() => _runs.Load(runId)?.State == WorkflowRunState.Paused);
            Assert.Equal(WorkflowRunState.Paused, _runs.Load(runId)!.State);
            // 驱动已退出（Paused 属活动状态语义，ListActiveRuns 不适用——按驱动在册判定）
            await WaitUntilAsync(() => !host.IsDriving(workflowId));
            Assert.Single(boundary.Submissions); // 暂停边界前不得提交节点2

            var resume = await host.ResumeRunAsync(runId);
            Assert.Equal(HostActionStatus.Registered, resume.Status);
            await WaitUntilAsync(() => boundary.Submissions.Count == 2);
            Assert.Equal("n-2", boundary.Submissions[1]); // 恢复后继提交=节点2（不重放节点1）
            await WaitUntilAsync(() => SafeOps().Count(o => o.RequestState == OperationRequestState.TerminalCompleted) == 2);

            var ops = Ops(ReadLease());
            Assert.Equal(2, ops.Count); // 启动 op + 恢复 op 共享同一 runBinding，运行终态一同终局
            var resumeOp = Assert.Single(ops.Where(o => o.Intent == "resume"));
            Assert.StartsWith("resume:paused-continue:", resumeOp.Candidate!.TriggerOccurrenceId);
            Assert.Equal(runId, resumeOp.RunBinding);
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(runId)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 3. F11 阻断：先于租约（零副作用）+原运行保持可恢复（不动原记录）──

    [Fact]
    public async Task ResumeRun_F11Blocked_ZeroSideEffect_RunKeptResumable()
    {
        var workflowId = SeedFlow("F11恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { F11Active = true });
        try
        {
            var result = await host.ResumeRunAsync(seeded.RunId!);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("F11", result.Message);
            Assert.Empty(boundary.Submissions);

            Assert.Empty(Ops(ReadLease())); // 租约零副作用（§7.1）
            Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(seeded.RunId!)!.State); // 原运行不动（可再恢复）
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 4. 发送未知：操作 Reconciling+Submission 未决保留+原运行不动（不臆断已恢复/不臆断取消）──

    [Fact]
    public async Task ResumeRun_SenderUnknown_Reconciling_RunUntouched()
    {
        var workflowId = SeedFlow("未知恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var seams = new TaskCenterAdmissionSeams
        {
            SenderOverride = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("test:timeout")),
        };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            var result = await host.ResumeRunAsync(seeded.RunId!);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("对账", result.Message);
            Assert.Empty(boundary.Submissions);

            var lease = ReadLease();
            var op = Assert.Single(Ops(lease));
            Assert.Equal(OperationRequestState.Reconciling, op.RequestState); // 保守待对账（不重发）
            Assert.NotNull(lease.File!.Handoff!.Submission); // 未决发送保留（不换键重跑）
            Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(seeded.RunId!)!.State); // 不臆断（驱动或在飞——留待对账）
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 5. 执行占用：可重试拒绝（窗口派生不重置）+原运行不动+不发送 ──

    [Fact]
    public async Task ResumeRun_Occupied_RetryableRejected_RunUntouched()
    {
        var workflowId = SeedFlow("占用恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { Occupied = true });
        try
        {
            var result = await host.ResumeRunAsync(seeded.RunId!);
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Contains("占用", result.Message);
            Assert.Empty(boundary.Submissions);

            var op = Assert.Single(Ops(ReadLease()));
            Assert.Equal(OperationRequestState.RetryableRejected, op.RequestState);
            Assert.NotNull(op.RetryWindowDeadlineUtc); // 持久化重试窗口（不重置）
            Assert.Equal(WorkflowRunState.Interrupted, _runs.Load(seeded.RunId!)!.State); // 恢复拒绝不动原运行记录
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 5b. 心跳续期（会诊 重要-2 补证 + 复核 重要-1 处置）：等待窗口越过「完整原生 TTL」后所有权仍存续 ──
    // 过期判定口径（写入准入）=单调 IsOwnerExpired（距上次所有者写入满 TTL 即过期）；LastHeartbeatUtc+Ttl 仅作诊断 Status——
    // 窗口必须 > 完整 TTL 才可证伪（无心跳则原生到期点必被越过→一切变更 lease_stale_generation）；
    // 起点设桩在 r1 受理成功后（组装/接管观察/恢复/心跳启动漂移全部被吸收进桩前段），窗口内只剩心跳续期竞争；
    // TTL=2s+Delay 2300ms（越过到期点仅 300ms 余量）：恰好压线的续期也会被「原生到期点已越过」判定捕获——有心跳续期才能存续。

    [Fact]
    public async Task HeartbeatKeepsOwnership_BeyondTtl_SubsequentAdmissionWorks()
    {
        var flowA = SeedFlow("心跳流程A");
        var flowB = SeedFlow("心跳流程B");
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { OwnershipTtlSeconds = 2 });
        try
        {
            var r1 = await host.StartWorkflowAsync(flowA);
            Assert.Equal(HostActionStatus.Registered, r1.Status); // 受理成功=所有权已获+心跳已启动（窗口起点在此之后，不早不晚）
            await Task.Delay(2300); // >完整 TTL=2000ms：无心跳则原生到期点必被越过（此后一切变更 lease_stale_generation），有心跳则续期存续
            var r2 = await host.StartWorkflowAsync(flowB);
            Assert.Equal(HostActionStatus.Registered, r2.Status); // 心跳续期=所有权存续
            await WaitUntilAsync(() => SafeOps().Count(o => o.RequestState == OperationRequestState.TerminalCompleted) == 2);
            Assert.Equal(2, Ops(ReadLease()).Count(o => o.RequestState == OperationRequestState.TerminalCompleted));
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 5c. 并发双恢复：同一运行仅一驱动（无双跑）+落败方终局中止不遗留 Queued 孤儿（会诊 重要-1/建议-5 钉死）──

    [Fact]
    public async Task ConcurrentResume_SameRun_ExactlyOneDriver_LoserTerminalized()
    {
        var workflowId = SeedFlow("并发恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams());
        try
        {
            var results = await Task.WhenAll(host.ResumeRunAsync(seeded.RunId!), host.ResumeRunAsync(seeded.RunId!));
            // 受理口径：恰一胜者（无双跑）；落败方可能 Registered（发送未决对账 Reconciling 经宿主映射为 Unavailable，
            // 或占位后被宿主兜底拒绝）——等待全部操作落定后统一判定（避免「胜者未决时落败方已被拒」的瞬态误判）。
            Assert.Equal(1, results.Count(r => r.Status == HostActionStatus.Registered));
            Assert.Equal(1, results.Count(r => r.Status == HostActionStatus.Unavailable));

            // 等待胜者驱动真实提交（并行负载下受理对账/提交可能慢于宿主返回——以「驱动真实提交」为唯一收敛桩）。
            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            Assert.Single(boundary.Submissions); // 无双跑：全局只有一个驱动真实提交（本夹具实质强度）
            var ops = Ops(ReadLease());
            // 真胜者=唯一 Accepted 操作（受理对账锁外进行——提交成功时胜者可能仍在 Granted/Sending 过渡态，
            // 等终局回写收敛到 Accepted/TerminalCompleted）；落败方路径③也会 LastSendSeq>0（占位后被宿主
            // 确定拒绝 run_state_changed——发送许可已消费但确定未受理），故不能按 LastSendSeq 判胜。
            await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted));
            ops = Ops(ReadLease());
            var winner = Assert.Single(ops.Where(o => o.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted));
            // 落败方合法路径：①宿主前置互斥先拦（无操作登记）②占位校验 submission_conflict 终局中止
            // （胜者 Submission 未关窗）③sender 台账核对 run_state_changed（胜者驱动极快、落败方进 sender 时运行已被置 Running）
            // ④占位后宿主在飞 task_running=RetryableRejected——无双跑门面层不自足、由宿主发送侧兜底（会诊复核如实登记）；
            // 若已登记则必落确定终局/可重试拒绝（绝不滞留 InRound/Queued 孤儿阻塞同胞终局回写与主槽位）。
            // 注：胜者已由上方 Single 断言锁定为唯一 Accepted/TerminalCompleted，故落败方只可能是这两种拒绝态。
            var loser = ops.SingleOrDefault(o => o.RequestIdentity != winner.RequestIdentity);
            if (loser is not null)
            {
                Assert.Contains(loser.RequestState, new[] { OperationRequestState.TerminalRejected, OperationRequestState.RetryableRejected });
                if (loser.RequestState == OperationRequestState.TerminalRejected)
                    Assert.Contains(loser.LastResult!.ReasonCode, new[] { "submission_conflict", "run_state_changed" });
                else if (loser.RequestState == OperationRequestState.RetryableRejected)
                    // 两条来源：①宿主发送侧在飞=task_running（ReconcileOutcome 原样保存远端/宿主原因码，不做改写）
                    // ②门面锁内执行占用复核=execution_occupied（FactsProvider 报占用时）。
                    Assert.Contains(loser.LastResult!.ReasonCode, new[] { "task_running", "execution_occupied" });
            }
            await WaitUntilAsync(() => _runs.Load(seeded.RunId!)!.State == WorkflowRunState.Succeeded);
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(seeded.RunId!)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 7. 生产 epoch 形状（含冒号）不得被截断（会诊阻断项：ExtractEpoch 按 ':' 全量切分只取 [2]）──
    // 本夹具此前恒用空 epoch / 单段 epoch，掩盖了生产 CurrentBgiEpoch()="{ProcessId}:{StartTicksUtc}" 被截成
    // pid 的缺陷——门面会拿截断值与完整值比较，确定性返回 stale_epoch 终局拒绝，恢复/启动在生产全不可用。

    [Fact]
    public async Task ResumeRun_ProductionEpochShape_NotTruncated_Accepted()
    {
        const string epoch = "4821:638912345678901234"; // 生产形状：{ProcessId}:{StartTicksUtc}（含冒号）
        var workflowId = SeedFlow("生产epoch流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams { Epoch = epoch });
        try
        {
            var resume = await host.ResumeRunAsync(seeded.RunId!);
            Assert.Equal(HostActionStatus.Registered, resume.Status);
            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            Assert.Single(boundary.Submissions); // 恢复驱动真实提交（未被 stale_epoch 误拒）
            Assert.Equal(epoch, Ops(ReadLease()).Single(o => o.Intent == "resume").TargetEpoch); // 目标 epoch 完整保留
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(seeded.RunId!)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 8. 并发首调者 + 外部进程仍持有租约：先接管者开始心跳后，后继者必须复用已完成门面──
    // 会诊阻断项：原实现只在「接管证据成熟」后才检查 _admission——先接管者持续心跳使后继者的观察永不成形，
    // 后继者会空转到接管观察预算耗尽后响亮失败（共享门面其实早已可用）。

    [Fact]
    public async Task ConcurrentFirstCalls_ForeignHeldLease_BothAdmittedWithoutTimeout()
    {
        var flowA = SeedFlow("并发初始化流程A");
        var flowB = SeedFlow("并发初始化流程B");
        var boundary = new FakeBoundary();
        // 预置「外部进程持有」的有效租约（未被本进程写入，故首调者必须先观察满 TTL 才取得接管证据）。
        var foreign = new ArbitrationLeaseStore(Path.Combine(_dir, "arbitration"));
        var acq = foreign.TryAcquire("pid:foreign", 1);
        Assert.True(acq.Success, "预置外部租约失败：" + acq.Reason);

        // 固定目标交错：两路都必须真正进入接管观察阶段后才继续——否则「第二路晚进入」或「两路旧证据几乎同时成熟」
        // 会让旧实现的空转缺陷偶发漏检（会诊复核）。Barrier 保证两者重叠于同一观察阶段。
        using var barrier = new Barrier(2);
        var observationEntries = 0;
        var observationOverlap = 1; // 0=任一路 Barrier 超时（交错未被固定）
        var seams = new TaskCenterAdmissionSeams
        {
            OwnershipTtlSeconds = 1,
            OnTakeoverObservationEntered = () =>
            {
                Interlocked.Increment(ref observationEntries);
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) Volatile.Write(ref observationOverlap, 0);
            },
        };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            var results = await Task.WhenAll(host.StartWorkflowAsync(flowA), host.StartWorkflowAsync(flowB));
            // 确定性前提（会诊四轮）：两路都必须真正进入接管观察阶段且重叠——否则本用例没有命中目标交错，不能算补证。
            Assert.Equal(2, observationEntries);
            Assert.Equal(1, observationOverlap);
            // 本夹具只钉「两个等待者都拿到共享门面」——不得回落到接管观察超时/初始化失败。
            // 启动请求如何分轮属仲裁职责：两者可能同轮，此时非胜者合法返回 NotSelected（会诊复核：不能按 Registered 全过）。
            Assert.All(results, r => Assert.DoesNotContain("接管观察", r.Message));
            Assert.All(results, r => Assert.DoesNotContain("仲裁面初始化失败", r.Message));
            Assert.Contains(results, r => r.Status == HostActionStatus.Registered); // 至少一路真实获选
            Assert.All(results, r => Assert.True(
                r.Status == HostActionStatus.Registered || r.Message.Contains("并发仲裁未获选"),
                "非预期结果：" + r.Status + "：" + r.Message));
            await WaitUntilAsync(() => boundary.Submissions.Count >= 1);
            Assert.True(boundary.Submissions.Count is 1 or 2, "提交次数异常：" + boundary.Submissions.Count); // 无第三路发送
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── 6. 启动移交 resume：绑定台账落盘+恢复动作经准入边界驱动（§5.1 行5+行2 组合）──

    [Fact]
    public async Task HandoffResume_Wired_BindingAppended_DriveViaRecoveryAdmission()
    {
        var workflowId = SeedFlow("移交恢复流程");
        var seeded = SeedResumableRun(workflowId, WorkflowRunState.Interrupted);
        var boundary = new FakeBoundary();
        var host = MakeWiredHost(boundary, new TaskCenterAdmissionSeams());
        try
        {
            var request = new StartupHandoffRequest
            {
                Mode = StartupHandoffModes.Resume,
                WorkflowId = workflowId,
                IntentKey = "test-intent-" + Guid.NewGuid().ToString("N")[..8],
                ExecutionId = Guid.NewGuid().ToString("N"),
            };
            var receipt = await host.RegisterHandoffAsync(request);
            Assert.Equal(HandoffOutcome.Accepted, receipt.Outcome);
            Assert.Equal(seeded.RunId, receipt.RunId); // resume 绑定既有可恢复运行

            await WaitUntilAsync(() => boundary.Submissions.Count == 1);
            Assert.Single(boundary.Submissions); // 恢复驱动经准入边界真实提交（非旧直通路径）
            await WaitUntilAsync(() => SafeOps().Any(o => o.RequestState == OperationRequestState.TerminalCompleted));
            var op = Assert.Single(Ops(ReadLease()));
            Assert.Equal("resume", op.Intent);
            Assert.Equal(seeded.RunId, op.RunBinding);
            Assert.Equal(WorkflowRunState.Succeeded, _runs.Load(seeded.RunId!)!.State);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    // ── P28：恢复沿用原固定 Scope/绑定（纪元变化时不得换身份） ──

    /// <summary>
    /// **P28（R5.3）正向**：运行由面板 E1 启动（登记固定 Scope＝`bgi:local:1:100`）后变为 Interrupted，
    /// **纪元不变**时发起恢复 ⇒ 恢复操作落盘 Scope/TargetEpoch 与启动操作**逐字一致**。
    /// **证明边界（[纠正·2026-09-21 会诊]）**：本用例**不能**独立区分「继承」与「重新读取当前值」——
    /// 真正检出本次修复的是换纪元用例（`ResumeRun_EpochChanged_RejectedNoSilentRebinding`）。
    /// 场景施工方内置，owner 0 点击。
    /// </summary>
    [Fact]
    public async Task ResumeRun_InheritsOriginalScope_WhenEpochUnchanged()
    {
        var workflowId = SeedFlow("恢复来源继承流程");
        var boundary = new FakeBoundary();
        var seams = new TaskCenterAdmissionSeams { Epoch = "1:100" };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            Assert.Equal(HostActionStatus.Registered, (await host.StartWorkflowAsync(workflowId)).Status);
            await WaitUntilAsync(() => boundary.Submissions.Count >= 1 && host.ListActiveRuns().Count == 0);
            var startOp = Assert.Single(Ops(ReadLease()).Where(o => o.Intent == "start"));
            var runId = startOp.RunBinding!;
            Assert.Equal("1:100", startOp.TargetEpoch); // 启动时固定的目标纪元

            // 令其变为可恢复态（**纪元不变**：本夹具先验证「继承」这一正向路径）
            var run = _runs.Load(runId)!;
            run.State = WorkflowRunState.Interrupted;
            run.Note = (run.Note ?? "") + "；夹具置为可恢复态";
            _runs.Update(run);

            var resume = await host.ResumeRunAsync(runId);
            Assert.Equal(HostActionStatus.Registered, resume.Status);

            // 恢复操作必须**继承该 run 的固定 Scope/epoch**（而不是任何「当前」值）
            var resumeOp = Assert.Single(Ops(ReadLease()).Where(o => o.Intent == "resume"));
            Assert.Equal("1:100", resumeOp.TargetEpoch);
            Assert.Equal("bgi:local:1:100", resumeOp.Candidate!.Scope);
            Assert.Equal(startOp.Candidate!.Scope, resumeOp.Candidate!.Scope); // 与启动操作逐字一致
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    /// <summary>
    /// **P28（R5.3）负向**：纪元已变（启动时 `1:100`，恢复时 `1:200`）时，
    /// 恢复必须**拒绝**（`stale_epoch`）——**不得**静默把恢复请求重绑到新纪元（那等于换身份）。
    /// 场景施工方内置，owner 0 点击。
    /// </summary>
    [Fact]
    public async Task ResumeRun_EpochChanged_RejectedNoSilentRebinding()
    {
        var workflowId = SeedFlow("恢复纪元变化流程");
        var boundary = new FakeBoundary();
        var seams = new TaskCenterAdmissionSeams { Epoch = "1:100" };
        var host = MakeWiredHost(boundary, seams);
        try
        {
            Assert.Equal(HostActionStatus.Registered, (await host.StartWorkflowAsync(workflowId)).Status);
            await WaitUntilAsync(() => boundary.Submissions.Count >= 1 && host.ListActiveRuns().Count == 0);
            var startOp = Assert.Single(Ops(ReadLease()).Where(o => o.Intent == "start"));
            var runId = startOp.RunBinding!;

            var run = _runs.Load(runId)!;
            run.State = WorkflowRunState.Interrupted;
            _runs.Update(run);
            seams.Epoch = "1:200"; // 纪元变化

            var resume = await host.ResumeRunAsync(runId);

            Assert.Equal(HostActionStatus.Unavailable, resume.Status);
            Assert.Contains("stale_epoch", resume.Message);            // 拒绝，不重绑
            // 恢复入口**先登记再锁内校验**（登记→校验拒绝），故会留下一条 resume 记录；关键是它必须
            // **仍是原纪元/原 Scope**（未重绑到 1:200）且处于**拒绝终态**（未获得发送许可）。
            var resumeOp = Assert.Single(Ops(ReadLease()).Where(o => o.Intent == "resume"));
            Assert.Equal("1:100", resumeOp.TargetEpoch);                       // 仍为原纪元（未重绑 1:200）
            Assert.Equal("bgi:local:1:100", resumeOp.Candidate!.Scope);
            // 精确锁定：本地预检的**终局拒绝**（非可重试拒绝），且**未取得发送许可**
            Assert.Equal(OperationRequestState.TerminalRejected, resumeOp.RequestState);
            Assert.Equal("stale_epoch", resumeOp.LastResult!.ReasonCode);
            Assert.False(resumeOp.LastResult.Retryable);
            Assert.Equal(0, resumeOp.LastSendSeq);
            Assert.True(string.IsNullOrEmpty(resumeOp.SubmissionIdentity));
            Assert.Null(ReadLease().File!.Handoff!.Submission);                  // 无新增未决发送
            var afterResume = _runs.Load(runId)!;                                // 原运行仍为可恢复态、绑定未变
            Assert.Equal(WorkflowRunState.Interrupted, afterResume.State);
            Assert.Equal(startOp.RunBinding, afterResume.RunId);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
}
