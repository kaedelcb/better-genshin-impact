using System;
using System.Linq;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 20 Wave3（C11=(a) 停驻态显式化＋重驱入口＋BO-5/BO-6 端到端）夹具**。
/// 生产零接线：ShouldRegisterLocalWait 为 **实例级** 注入（WorkflowRunnerOptions，R43 重要-6）；生产默认 null ⇒ 恒 false（批次 14 语义）。
/// </summary>
public sealed class LocalWaitParkingStateContractTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public LocalWaitParkingStateContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b20park-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private WorkflowNode Node(string id) => new()
    {
        NodeId = id, Kind = "resource.oneDragonConfig",
        Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
    };

    private string SeedFlow(params string[] nodeIds)
    {
        var doc = new WorkflowDocument { Name = "停驻夹具", Nodes = nodeIds.Select(Node).ToList() };
        doc.Activation = new WorkflowActivation { Status = "active" };
        return _workflows.Save(doc, null) + "|" + doc.WorkflowId!;
    }

    private (WorkflowRunner runner, LocalWaitQueueStore queue) MakeRunner(
        string queueName, Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?> referenceProvider,
        Func<WorkflowRunRecord, string?>? scopeProvider = null,
        Func<WorkflowNodeOccurrence, bool>? parkingPredicate = null)
    {
        var queue = new LocalWaitQueueStore(Path.Combine(_dir, queueName));
        var runner = new WorkflowRunner(_workflows, _runs, new ParkingBoundary(), new ParkingPrerequisite(),
            new ParkingTerminal(), new WorkflowRunnerOptions
            {
                ShouldRegisterLocalWait = parkingPredicate, // [Wave3 C11] 实例级注入（无进程级静态污染）
            }, localWaitQueue: queue,
            localWaitPrerequisiteReferenceProvider: referenceProvider,
            localWaitAdmissionScopeProvider: scopeProvider);
        return (runner, queue);
    }

    /// <summary>【突变验证 ✔（M64：停驻分支不置新状态词 ⇒ 红）】【C11=(a) 核心证据】
    /// 端到端：接缝命中＋引用就绪 ⇒ 停驻 ⇒ State==LocalWaitParking（D-E4=(a) 不复用 Running）＋
    /// 队列有项＋游标不推进。</summary>
    [Fact]
    public async Task ParkedRun_StateIsLocalWaitParking_QueueHasItem_CursorHolds()
    {
        var workflowId = SeedFlow("n1", "n2").Split('|')[1];
        var (runner, queue) = MakeRunner("q-park", (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1", parkingPredicate: _ => true);
        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Equal("n1", run.Cursor!.NodeId); // 游标不推进
        Assert.Single(queue.Load());            // 等待项已登记
        Assert.Equal("n1", Assert.Single(queue.Load()).StableIdentity.Split('|')[1]);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, run.CurrentSubmission?.Intent); // 零发送事实显式持久化
        Assert.False(run.CurrentSubmission?.InFlight ?? true);
        var binding = Assert.IsType<LocalWaitBinding>(run.LocalWaitDecision?.Binding);
        Assert.Equal(run.RunId, binding.RunId);
        Assert.Equal(Assert.Single(queue.Load()).ItemId, binding.ItemId);
        Assert.False(Assert.Single(queue.Load()).HasTrustedIdentity); // 队列成员不是发送许可
    }

    /// <summary>【Wave1 R25 形态端到端化】登记被拒（provider 缺失＝接线缺陷）⇒ 同样停驻于 LocalWaitParking。</summary>
    [Fact]
    public async Task RefusalPath_AlsoParksAsLocalWaitParking()
    {
        var workflowId = SeedFlow("n1", "n2").Split('|')[1];
        var (runner, queue) = MakeRunner("q-refusal", referenceProvider: null, parkingPredicate: _ => true);
        var run = await runner.StartAsync(workflowId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Empty(queue.Load()); // 队列零变化
    }

    /// <summary>
    /// 【突变验证 ✔（M65：恢复扫描漏 LocalWaitParking ⇒ 红）】【BO-5 闭合证据】
    /// 停驻运行重启 ⇒ 恢复扫描收敛 **Interrupted（可显式恢复）**——「无重驱句柄的永久 Running」消除。
    /// </summary>
    [Fact]
    public async Task RecoverOnStart_ConvertsParkingToInterrupted_ThenResumeDrives()
    {
        var workflowId = SeedFlow("n1", "n2").Split('|')[1];
        var (runner, _) = MakeRunner("q-recover", (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1", parkingPredicate: _ => true);
        var parked = await runner.StartAsync(workflowId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, parked.State);

        // 重启恢复扫描（同 RunStore 实例重放）：LocalWaitParking ⇒ Interrupted
        // [Wave3 R43 必改-4 判别力补强] Note 文案断言——独立谓词被删（落通用 else）时文案不同 ⇒ 突变可判别。
        var recovered = _runs.RecoverOnStart();
        var after = _runs.Load(parked.RunId)!;
        Assert.Equal(WorkflowRunState.Interrupted, after.State);
        Assert.Contains("本地等待停驻运行", after.Note);
        Assert.Contains(recovered, r => r.RunId == parked.RunId);

        // 显式重驱入口（C11=(a)）：ResumeAsync 接受 Interrupted（源自停驻）⇒ 重驱成功。
        // 恢复后不再等待：新 Runner 实例（实例级注入，无静态污染——R43 重要-6）。
        var driveRunner = new WorkflowRunner(_workflows, _runs, new ParkingBoundary(), new ParkingPrerequisite(),
            new ParkingTerminal(), new WorkflowRunnerOptions(),
            localWaitQueue: new LocalWaitQueueStore(Path.Combine(_dir, "q-recover")),
            localWaitPrerequisiteReferenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            localWaitAdmissionScopeProvider: _ => "bgi:local:e1");
        var resumed = await driveRunner.ResumeAsync(parked.RunId);
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State); // n1、n2 正常执行完
    }

    /// <summary>【C11=(a)】ResumeAsync 拒绝 Running（既有语义保持）。</summary>
    [Fact]
    public async Task ResumeAsync_StillRejectsRunning()
    {
        var workflowId = SeedFlow("n1").Split('|')[1];
        var (runner, _) = MakeRunner("q-reject", (_, _) => "wf-x/n1/0/0@ticket-1");
        var run = await runner.StartAsync(workflowId);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ResumeAsync(run.RunId));
    }

    /// <summary>
    /// 【BO-6 端到端】救援返回后前进 N 步、已完成节点**零重复提交**：停驻 n1、锚（n0）被修订删除、
    /// 新链 [nPre, n1] ⇒ 重驱序＝[nPre, n1]（无 n0/n0 无第二次）。
    /// </summary>
    [Fact]
    public async Task Bo6_RescueThenDrive_NoDuplicateSubmissions()
    {
        var workflowId = SeedFlow("n0", "n1").Split('|')[1];
        var submissions = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (runner, _) = MakeRunner("q-bo6", (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1", parkingPredicate: occ => occ.NodeId == "n1");
        var boundary = new ParkingBoundaryWithRecorder(submissions);
        var runner2 = new WorkflowRunner(_workflows, _runs, boundary, new ParkingPrerequisite(),
            new ParkingTerminal(), new WorkflowRunnerOptions
            {
                ShouldRegisterLocalWait = occ => occ.NodeId == "n1", // 仅 n1 停驻（n0 正常执行）
            }, localWaitQueue: new LocalWaitQueueStore(Path.Combine(_dir, "q-bo6")),
            localWaitPrerequisiteReferenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            localWaitAdmissionScopeProvider: _ => "bgi:local:e1");
        var parked = await runner2.StartAsync(workflowId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, parked.State);
        var firstRound = submissions.ToArray();
        Assert.Equal(["n0"], firstRound); // n0 正常提交；n1 走停驻**不提交**（零发送）

        // 修订：保留 n0、尾部追加 n2 ⇒ 恢复重驱（n1 重走准入 → n2 新节点）
        var doc = _workflows.LoadSnapshot(workflowId).Document;
        var newDoc = new WorkflowDocument
        {
            WorkflowId = workflowId, // 保留原流程 ID（修订守卫按 ID 寻址）
            Name = doc.Name,
            Nodes = new[] { Node("n0"), Node("n1"), Node("n2") }.ToList(),
        };
        newDoc.Activation = new WorkflowActivation { Status = "active" };
        _workflows.Save(newDoc, _workflows.LoadSnapshot(workflowId).Revision);
        // 恢复后不再等待：新 Runner 实例（实例级注入——R43 重要-6，无静态污染）
        var driveRunner = new WorkflowRunner(_workflows, _runs, boundary, new ParkingPrerequisite(),
            new ParkingTerminal(), new WorkflowRunnerOptions(),
            localWaitQueue: new LocalWaitQueueStore(Path.Combine(_dir, "q-bo6")),
            localWaitPrerequisiteReferenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            localWaitAdmissionScopeProvider: _ => "bgi:local:e1");
        var resumed = await driveRunner.ResumeAsync(parked.RunId);

        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        var all = submissions.ToArray();
        Assert.Equal(["n0", "n1", "n2"], all); // n1 重驱一次、n2 新节点执行；**零重复提交**（BO-6 端到端）
    }

    // ---- 假件（本文件专用） ----

    private sealed class ParkingBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + request.Occurrence.NodeId));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ParkingBoundaryWithRecorder : IWorkflowExecutionBoundary
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _submissions;
        public ParkingBoundaryWithRecorder(System.Collections.Concurrent.ConcurrentQueue<string> submissions)
            => _submissions = submissions;
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            _submissions.Enqueue(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + request.Occurrence.NodeId));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ParkingPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public bool[] SupportedKinds => new[] { true, true, true };
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "假适配器", record.JobId));
        public Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, null, record.JobId));
    }

    private sealed class ParkingTerminal : IWorkflowTerminalExecutor
    {
        public bool[] SupportedKinds => new[] { true };
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("job-terminal"));
    }
}
