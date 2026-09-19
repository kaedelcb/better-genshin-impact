using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R4.10 生产接线集成夹具（ASTRA 途中会诊 阻断3 + 终审 阻断2/重要2/重要3 处置）：
/// A. Waiting 不占 BGI 槽位（离线优先 §1）：同宿主一流程 arm 挂载等待中，另一流程完整跑完终态；
/// B. 重连/纪元对账判定（TryMatchReconcileHit 纯函数接缝，终审复核 重要4：三重纪元判定已并入纯函数可测）：
///    一致性链=查询前连接纪元=查询后连接纪元=快照自报 bgiEpoch=提交冻结纪元，任一不符/缺失 → null；
///    零命中/多命中/身份四元不符 → 绝不绑定、绝不重发；唯一命中才绑定。
///    合同目标=进程纪元级一致性（同进程断连重连纪元不变，不否决已观察事实）；真实重连端到端离线不可全真模拟，诚实留界；
/// C. 生产组装接线（MainViewModel 真实构造路径；反射布置测试状态并读取接线，不新增生产测试接口——
///    绕过正常状态转换是本组证据边界）：
///    C1 惰性宿主单例——两次访问同实例，槲寄生面板（Tab1）共用同一宿主；
///    C2 提供方实时接线——客户端/能力/快照三个提供方是活委托（随 _externalClient/_config/LatestLocalStatus 实况变化）；
///    C3 监控端入口副作用面——Start/移交均被能力守卫响亮拒绝（NoCapability）且先于恢复屏障返回
///       （断言范围=未新建运行/流程目录+未登记驱动；IPC/台账面无触达由守卫先于一切副作用的代码顺序支撑）；
///    C4 并发首访单例（终审 重要3 回归：并行 16 路同时首访同一实例）；
///    C5 启动链接线（终审复核 阻断2 收窄：直接调用移交方法的负向路径 + Runner 委托绑定证据，
///       不宣称完整「启动→移交→受理→等待」实测链）：委托绑定=Runner._enterTaskCenter.Target/Method 断言；
///       监控端 VM 守卫 NoCapability（VM 专有文案区分拒绝层次）；执行端（无 BGI 连接、本机运行目录干净时）
///       穿透 VM→共享宿主→StatusUncertain（宿主专有词，链路到达证据）；前提不满足时明确报告未验证。
/// </summary>
public class R410ProductionWiringTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public R410ProductionWiringTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "r410wire-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ================= A. Waiting 不占槽位 =================

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
                Params = new Dictionary<string, JsonElement> { ["time"] = JsonDocument.Parse($"\"{DateTime.Now.AddHours(2):HH:mm}\"").RootElement.Clone() },
            });
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    private (TaskCenterHost Host, FakeBoundary Boundary) MakeHost()
    {
        var boundary = new FakeBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog-cache.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null),
            () => true,
            () => new ControlStatus { TaskRunning = false });
        return (host, boundary);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int spins = 500)
    {
        for (var i = 0; i < spins && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "等待条件超时未满足");
    }

    [Fact]
    public async Task A_WaitingRun_DoesNotOccupySlot_ConcurrentWorkflowRunsToCompletion()
    {
        var wfWaiting = Seed("定时流程（挂载等待）", withTrigger: true);
        var wfRunning = Seed("日常流程（立即执行）");
        var (host, boundary) = MakeHost();

        try
        {
            // 流程一：armTrigger 挂载 → 真实进入 Waiting（等待不占 BGI 槽位，零提交）
            var arm = await host.RegisterHandoffAsync(new StartupHandoffRequest
            {
                ExecutionId = Guid.NewGuid().ToString("N"), StepId = "step-1",
                IntentKey = "manual:arm-slot", WorkflowId = wfWaiting, Mode = StartupHandoffModes.ArmTrigger,
            });
            Assert.Equal(HandoffOutcome.Accepted, arm.Outcome);
            await WaitUntilAsync(() => _runs.Load(arm.RunId!)?.State == WorkflowRunState.Waiting);

            // 流程二：Waiting 运行在场时，另一流程照常受理并跑完（槽位未被等待占用）
            var start = await host.StartWorkflowAsync(wfRunning);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => _runs.List().FirstOrDefault(r => r.WorkflowId == wfRunning)?.State == WorkflowRunState.Succeeded);

            // 等待流程仍 Waiting 且驱动在册；跑完流程驱动出册（终态落盘与出册有窗口，等待收敛——终审 重要2）；
            // 仅跑完流程提交过一次
            Assert.Equal(WorkflowRunState.Waiting, _runs.Load(arm.RunId!)!.State);
            Assert.True(host.HasDrive(wfWaiting));
            await WaitUntilAsync(() => !host.HasDrive(wfRunning));
            Assert.Equal(new[] { "n-1" }, boundary.Submissions);
        }
        finally
        {
            await host.ShutdownAsync(); // 终审 重要2：断言失败也收敛驱动，不留 Waiting 驱动泄漏
        }
    }

    // ================= B. 重连/纪元对账判定 =================

    private static BgiJobListSnapshot SnapshotWith(BgiEpoch? epoch, params BgiJobInfo[] jobs)
        => new() { Epoch = epoch, Jobs = jobs };

    private static BgiJobInfo Job(string key, string runId, string nodeId, int iteration, string? jobId)
        => new() { IdempotencyKey = key, WorkflowRunId = runId, NodeId = nodeId, Iteration = iteration, JobId = jobId };

    // 新签名（终审复核 重要4）：TryMatchReconcileHit(snapshot, 查询前连接纪元, 查询后连接纪元, 冻结纪元, 四元身份…)
    // 一致性链：查询前=查询后=快照自报=冻结，任一不符/缺失 → null

    [Fact]
    public void B_ReconcileHit_UniqueIdentityMatch_Binds()
    {
        var snapshot = SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 },
            Job("key-1", "run-1", "n-1", 0, "job-9"),
            Job("key-other", "run-1", "n-1", 0, "job-x")); // 无关作业不干扰

        var hit = BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            snapshot, "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0);

        Assert.NotNull(hit);
        Assert.Equal("job-9", hit!.JobId);
    }

    [Fact]
    public void B_ReconcileHit_EpochChainBroken_NeverBinds()
    {
        var snapshot = SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-1", "run-1", "n-1", 0, "job-9"));

        // 查询窗口内连接纪元变化（查询前后读到不同纪元——快照可能来自旧连接）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            snapshot, "1:100", "1:200", "1:100", "key-1", "run-1", "n-1", 0));
        // 快照自报纪元缺失（载荷无 bgiEpoch——来源不可考）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(null, Job("key-1", "run-1", "n-1", 0, "job-9")), "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        // 快照自报纪元与连接纪元不符（旧连接快照/异常载荷）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 9, StartTicksUtc = 9 }, Job("key-1", "run-1", "n-1", 0, "job-9")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        // 冻结纪元不符（BGI 重启换新纪元：旧事实不可沿用，命中也绝不绑定）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            snapshot, "1:100", "1:100", "1:200", "key-1", "run-1", "n-1", 0));
        // 连接纪元不明（通道瞬态）/冻结纪元缺失：不猜
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            snapshot, null, null, "1:100", "key-1", "run-1", "n-1", 0));
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            snapshot, "1:100", "1:100", null, "key-1", "run-1", "n-1", 0));
        // 快照缺失：查不到 ≠ 未执行证明
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            null, "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
    }

    [Fact]
    public void B_ReconcileHit_ZeroOrMultiHit_NeverBinds()
    {
        // 零命中：未证实受理 → Unknown（不重发）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }), "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        // 多命中：身份歧义 → Unknown（绝不择一绑定）
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 },
                Job("key-1", "run-1", "n-1", 0, "job-9"), Job("key-1", "run-1", "n-1", 0, "job-10")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
    }

    [Fact]
    public void B_ReconcileHit_IdentityMismatch_Excluded()
    {
        // 四元身份任一不符即排除；缺 jobId 的占位行不参与绑定
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-1", "run-2", "n-1", 0, "job-9")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-1", "run-1", "n-2", 0, "job-9")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-1", "run-1", "n-1", 1, "job-9")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-2", "run-1", "n-1", 0, "job-9")),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
        Assert.Null(BgiWorkflowExecutionBoundary.TryMatchReconcileHit(
            SnapshotWith(new BgiEpoch { ProcessId = 1, StartTicksUtc = 100 }, Job("key-1", "run-1", "n-1", 0, null)),
            "1:100", "1:100", "1:100", "key-1", "run-1", "n-1", 0));
    }
    // ================= C. 生产组装接线（反射布置状态+读取接线，不新增生产测试接口） =================

    private static T GetPrivateField<T>(object obj, string name)
        => (T)(obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"字段 {name} 不存在于 {obj.GetType().Name}")).GetValue(obj)!;

    private static void SetPrivateField(object obj, string name, object? value)
        => (obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"字段 {name} 不存在于 {obj.GetType().Name}")).SetValue(obj, value);

    [Fact]
    public void C1_ProductionHost_Singleton_SharedByMistletoePanel()
    {
        var mainVm = new MainViewModel();

        var h1 = mainVm.TaskCenterHost;
        var h2 = mainVm.TaskCenterHost;
        Assert.Same(h1, h2); // 惰性单例：四入口（主流程/定时/电子狗/日志 + 面板）共享同一宿主

        // 槲寄生 Tab1 面板（任务中心三卡）持有的宿主与启动移交路径同一实例
        var mistletoe = new MistletoeViewModel(mainVm);
        try
        {
            var panelHost = GetPrivateField<TaskCenterHost>(mistletoe.TaskCenter, "_host");
            Assert.Same(h1, panelHost);
        }
        finally
        {
            mistletoe.TaskCenter.StopAutoRefresh();
            // 终审 重要2：槲寄生页自有的 BGI 状态刷新计时器一并停止（不属于面板刷新表）
            GetPrivateField<System.Windows.Threading.DispatcherTimer>(mistletoe, "_bgiStatusRefresh").Stop();
        }
    }

    [Fact]
    public void C2_ProductionHost_Providers_LiveWired()
    {
        var mainVm = new MainViewModel();
        var host = mainVm.TaskCenterHost;

        // 客户端访问器：实况委托（_externalClient 未建立 → null；不是常量接线）
        var clientAccessor = GetPrivateField<Func<BgiExternalClient?>>(host, "_clientAccessor");
        Assert.Null(clientAccessor());

        // 能力提供方：实时跟随 IsExecutorMode——注入监控端配置后同一委托翻 false（先于一切副作用的守卫接线证据）
        var capability = GetPrivateField<Func<bool>>(host, "_localExecutionCapability");
        Assert.True(capability()); // 未加载配置 → 非监控端
        SetPrivateField(mainVm, "_config", new AssistConfig { ObserverMode = true });
        Assert.True(mainVm.IsObserverMode);
        Assert.False(capability());

        // 快照提供方：实时跟随 LatestLocalStatus（同一对象引用穿透）
        var snapshotProvider = GetPrivateField<Func<ControlStatus?>>(host, "_statusSnapshotProvider");
        Assert.Null(snapshotProvider());
        var sentinel = new ControlStatus { TaskRunning = true, CurrentTaskName = "哨兵任务" };
        SetPrivateField(mainVm, "<LatestLocalStatus>k__BackingField", sentinel);
        Assert.Same(sentinel, snapshotProvider());
    }

    [Fact]
    public async Task C3_ProductionHost_MonitorEnd_EntryRefused_NoNewDirectories()
    {
        // 终审 重要2：目录存在性在任何构造之前记录（构造阶段建目录也能被发现）；
        // 断言范围=未新建运行/流程目录+未登记驱动（不宣称覆盖全部副作用面）
        var runsExistedBefore = Directory.Exists(RunStore.DefaultRunsDir());
        var flowsExistedBefore = Directory.Exists(WorkflowStore.DefaultFlowsDir());

        var mainVm = new MainViewModel();
        SetPrivateField(mainVm, "_config", new AssistConfig { ObserverMode = true }); // 监控端
        var host = mainVm.TaskCenterHost;

        // 执行入口：能力守卫响亮拒绝（文案可辨，不是离线原因）
        var start = await host.StartWorkflowAsync("wf-不存在");
        Assert.Equal(HostActionStatus.Unavailable, start.Status);
        Assert.Contains("监控端", start.Message);

        // 移交入口：同一能力守卫（NoCapability；绕过 VM 直接调宿主同样被拦）
        var handoff = await host.RegisterHandoffAsync(new StartupHandoffRequest
        {
            ExecutionId = Guid.NewGuid().ToString("N"), StepId = "step-1",
            IntentKey = "manual:probe", WorkflowId = "wf-不存在", Mode = StartupHandoffModes.Start,
        });
        Assert.Equal(HandoffOutcome.Rejected, handoff.Outcome);
        Assert.Equal(HandoffReasonCodes.NoCapability, handoff.ReasonCode);

        // 能力预检先于恢复屏障返回：此前不存在的运行/流程目录不得被创建
        Assert.False(host.IsDriving("wf-不存在"));
        if (!runsExistedBefore) Assert.False(Directory.Exists(RunStore.DefaultRunsDir()));
        if (!flowsExistedBefore) Assert.False(Directory.Exists(WorkflowStore.DefaultFlowsDir()));
    }

    [Fact]
    public void C4_ProductionHost_ConcurrentFirstAccess_SingleInstance()
    {
        // 终审复核（重要3 回归 + 建议7）：Barrier 受控起跑，16 路尽量同时撞首访；
        // 单例正确性的主证据是锁实现（MainViewModel.BgiExternal.cs TaskCenterHost 属性），本夹具为并发回归探针
        var mainVm = new MainViewModel();
        const int lanes = 16;
        var hosts = new TaskCenterHost[lanes];
        // 终审复核（重要3）：显式线程 + 有界等待（Parallel.For 分区后并发度不足时 Barrier 会调度性挂死）
        using var starter = new Barrier(lanes);
        var threads = Enumerable.Range(0, lanes).Select(i => new Thread(() =>
        {
            starter.SignalAndWait();
            hosts[i] = mainVm.TaskCenterHost;
        })).ToArray();
        foreach (var th in threads) th.Start();
        foreach (var th in threads) Assert.True(th.Join(TimeSpan.FromSeconds(30)), "并发首访线程未在有界时间内完成");
        Assert.All(hosts, h => Assert.Same(hosts[0], h));
    }

    [Fact]
    public async Task C5_StartupChain_HandoffDelegate_BindingAndNegativePaths()
    {
        // 终审复核（阻断2 收窄）：本夹具=「直接调用移交方法的负向路径验证 + Runner 委托绑定证据」，
        // 不与夹具 A 拼接宣称完整「启动→移交→受理→等待」实测链（L 行两项证据分别陈述）
        var mainVm = new MainViewModel();
        var mistletoe = new MistletoeViewModel(mainVm);
        try
        {
            // 委托绑定证据：StartupFlowRunner 持有的移交委托就是本 VM 的 EnterTaskCenterAsync（不是别的实现）
            var runner = GetPrivateField<StartupFlowRunner>(mistletoe, "_runner");
            var handoffDelegate = GetPrivateField<Func<StartupHandoffRequest, CancellationToken, Task<StartupHandoffResult>>>(
                runner, "_enterTaskCenter");
            Assert.Same(mistletoe, handoffDelegate.Target);
            // 终审复核（建议6）：MethodInfo 直接比较（同名方法变化不削弱证明）
            Assert.Equal(typeof(MistletoeViewModel).GetMethod("EnterTaskCenterAsync", BindingFlags.NonPublic | BindingFlags.Instance),
                handoffDelegate.Method);

            var method = typeof(MistletoeViewModel).GetMethod("EnterTaskCenterAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("EnterTaskCenterAsync 不存在（启动链接线入口变更？）");
            async Task<StartupHandoffResult> EnterAsync(StartupHandoffRequest req)
                => await (Task<StartupHandoffResult>)method.Invoke(mistletoe, [req, CancellationToken.None])!;

            StartupHandoffRequest Req() => new()
            {
                ExecutionId = Guid.NewGuid().ToString("N"), StepId = "step-1",
                IntentKey = "manual:chain-probe", WorkflowId = "wf-探针", Mode = StartupHandoffModes.Start,
            };

            // 监控端：拒绝发生在 VM 层（文案为 VM 守卫专有「不能受理任务中心移交」，与宿主守卫文案不同——可区分层次）；
            // 宿主零触达的补充证据：能力预检先于恢复屏障（代码顺序）+ 目录断言（夹具 C3 同口径）
            SetPrivateField(mainVm, "_config", new AssistConfig { ObserverMode = true });
            var monitor = await EnterAsync(Req());
            Assert.Equal(HandoffOutcome.Rejected, monitor.Outcome);
            Assert.Equal(HandoffReasonCodes.NoCapability, monitor.ReasonCode);
            Assert.Contains("不能受理任务中心移交", monitor.Reason);
            Assert.True(monitor.TerminateChain);

            // 执行端（无 BGI 连接）：请求穿透 VM→共享宿主→深层守卫（StatusUncertain 只可能来自宿主快照辅助判定——
            // VM 层无此词，链路到达证据）；前提：本机运行目录无既有数据（恢复扫描不触碰真实记录）
            SetPrivateField(mainVm, "_config", new AssistConfig { ObserverMode = false });
            var runsDir = RunStore.DefaultRunsDir();
            if (!Directory.Exists(runsDir) || !Directory.GetFiles(runsDir, "*.run.json").Any())
            {
                var before = mainVm.TaskCenterHost;
                var executor = await EnterAsync(Req());
                Assert.Equal(HandoffOutcome.Rejected, executor.Outcome);
                Assert.Equal(HandoffReasonCodes.StatusUncertain, executor.ReasonCode);
                Assert.Same(before, mainVm.TaskCenterHost); // 链路上宿主未重建
                _output.WriteLine("C5 执行端穿透分支：已执行（本机运行目录干净）");
            }
            else
            {
                // 终审复核（阻断2）：前提不满足时明确报告未验证，不静默通过
                _output.WriteLine("C5 执行端穿透分支：未验证（本机运行目录存在既有记录，为保护真实数据跳过；监控端分支与委托绑定断言不受影响）");
            }
        }
        finally
        {
            mistletoe.TaskCenter.StopAutoRefresh();
            GetPrivateField<System.Windows.Threading.DispatcherTimer>(mistletoe, "_bgiStatusRefresh").Stop();
        }
    }
}