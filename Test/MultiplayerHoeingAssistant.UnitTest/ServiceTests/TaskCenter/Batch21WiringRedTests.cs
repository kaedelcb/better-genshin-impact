using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 21（SB21-1 等待判定接线）反例先行夹具**。
/// 钉死接线批义务（R5.3 §24.120 合同＋开工勘察定稿）：
/// ①BO-1——provider.Invoke／身份构造抛异常 ⇒ **零发送停驻**（State=LocalWaitParking＋Reason 留痕＋无在飞
///   提交），**绝不**走「异常按在飞事实收敛 Unknown/Interrupted」路径（批次 14 注释明载禁止）；
/// ②生产注入态——默认构造（生产形参）宿主须装配等待队列＋等待判定来源（反射守卫，编译绿、未接线红）；
/// ③EV1-R1——运行台账含未解析记录时，占用者级别解析**拒绝在缩减子集上判唯一命中**（保持未知＋留痕，
///   预填级别事实保留）——修复 RunStore.List 静默跳过造成的「假唯一命中」（owner ev1 裁决选项 a）。
/// </summary>
public sealed class Batch21WiringRedTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _hostLog = [];

    public Batch21WiringRedTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b21wire-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---------- ①BO-1：provider／身份构造异常 ⇒ 零发送停驻（绝不 Unknown/Interrupted 收敛） ----------

    [Fact]
    public async Task RegistrationFailures_ParkZeroSend_NotUnknownConvergence()
    {
        var (runner, queue, workflows) = MakeRunner("q-bo1a",
            referenceProvider: (_, _) => throw new InvalidOperationException("引用来源故障"),
            scopeProvider: _ => "bgi:local:e1");
        var run = await runner.StartAsync(SeedFlow(workflows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Contains("登记未完成", run.Note, StringComparison.Ordinal);
        Assert.Contains("异常", run.Note, StringComparison.Ordinal);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, run.CurrentSubmission?.Intent); // 零发送状态显式持久化
        Assert.False(run.CurrentSubmission?.InFlight ?? true);
        Assert.Equal(LocalWaitDecisionKind.Hold, run.LocalWaitDecision?.Kind);
        Assert.Null(run.LocalWaitDecision?.Binding); // 来源 provider 失败不得拼出队列绑定
        Assert.True(run.LocalWaitDecision?.NoSendConfirmed);
        Assert.Empty(queue.Load());           // 登记未完成 ⇒ 队列零变化

        var (sourceRunner, sourceQueue, sourceFlows) = MakeRunner("q-source-failure", null, null,
            waitDecisionSource: new WaitDecisionSource(_ => throw new InvalidOperationException("uid=123456789 typed source failure")));
        var sourceHold = await sourceRunner.StartAsync(SeedFlow(sourceFlows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, sourceHold.State);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, sourceHold.CurrentSubmission?.Intent);
        Assert.Equal(LocalWaitDecisionKind.Hold, sourceHold.LocalWaitDecision?.Kind);
        Assert.Null(sourceHold.LocalWaitDecision?.Binding);
        Assert.Contains("uid=***", sourceHold.LocalWaitDecision?.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", sourceHold.LocalWaitDecision?.Reason, StringComparison.Ordinal);
        var sourceRunStore = (RunStore)typeof(WorkflowRunner)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sourceRunner)!;
        var sourceRunPath = (string)typeof(RunStore)
            .GetMethod("PathFor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sourceRunStore, new object[] { sourceHold.RunId! })!;
        var persistedSourceJson = File.ReadAllText(sourceRunPath);
        Assert.Contains("uid=***", persistedSourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", persistedSourceJson, StringComparison.Ordinal);
        Assert.Empty(sourceQueue.Load());

        // 边界 Hold 是强于后续同步 Wait 的冲突事实：保留 Hold 原因且不能登记队列项。
        var decisionCalls = 0;
        var boundaryHoldSource = new WaitDecisionSource(request =>
            Interlocked.Increment(ref decisionCalls) == 1
                ? TestWaitDecision(request, kind: LocalWaitDecisionKind.ContinueAdmission)
                : TestWaitDecision(request, kind: LocalWaitDecisionKind.Wait));
        var (boundaryHoldRunner, boundaryHoldQueue, boundaryHoldFlows) = MakeRunner("q-boundary-hold",
            referenceProvider: null, scopeProvider: null, waitDecisionSource: boundaryHoldSource,
            boundaryResult: BoundarySubmitResult.HoldWith("boundary source conflict"));
        var boundaryHoldRun = await boundaryHoldRunner.StartAsync(SeedFlow(boundaryHoldFlows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, boundaryHoldRun.State);
        Assert.Equal(LocalWaitDecisionKind.Hold, boundaryHoldRun.LocalWaitDecision?.Kind);
        Assert.Contains("boundary source conflict", boundaryHoldRun.LocalWaitDecision?.Reason, StringComparison.Ordinal);
        Assert.Null(boundaryHoldRun.LocalWaitDecision?.Binding);
        Assert.Empty(boundaryHoldQueue.Load());

        var (typedIdentityRunner, typedIdentityQueue, identityFlows) = MakeRunner("q-source-identity",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1",
            waitDecisionSource: new WaitDecisionSource(request => TestWaitDecision(request, runId: "forged-run-id")));
        var identityHold = await typedIdentityRunner.StartAsync(SeedFlow(identityFlows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, identityHold.State);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, identityHold.CurrentSubmission?.Intent);
        Assert.Equal(LocalWaitDecisionKind.Hold, identityHold.LocalWaitDecision?.Kind);
        Assert.Null(identityHold.LocalWaitDecision?.Binding);
        Assert.Empty(typedIdentityQueue.Load());

        // 来源/Scope 在显式恢复时漂移：撤销旧队列绑定并 Hold，不能用新快照替换旧绑定。
        var driftScope = "bgi:local:resume-1";
        var (driftRunner, driftQueue, driftFlows) = MakeRunner("q-resume-drift",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-stable",
            scopeProvider: _ => driftScope);
        var driftRun = await driftRunner.StartAsync(SeedFlow(driftFlows, "n1", "n2"));
        var oldBinding = Assert.IsType<LocalWaitBinding>(driftRun.LocalWaitDecision?.Binding);
        Assert.Equal("bgi:local:resume-1", oldBinding.Scope);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, driftRun.CurrentSubmission?.Intent);
        Assert.Single(driftQueue.Load());
        driftScope = "bgi:local:resume-2";
        var driftResume = await driftRunner.ResumeAsync(driftRun.RunId!);
        Assert.Equal(WorkflowRunState.LocalWaitParking, driftResume.State);
        Assert.Equal(LocalWaitDecisionKind.Hold, driftResume.LocalWaitDecision?.Kind);
        Assert.Null(driftResume.LocalWaitDecision?.Binding);
        var driftItems = driftQueue.Load();
        var oldItem = Assert.Single(driftItems);
        Assert.Equal(oldBinding.ItemId, oldItem.ItemId);
        Assert.Equal(LocalWaitItemState.Cancelled, oldItem.State);

        // Resume 控制冲突必须先于任何台账写入：占住本 Runner 的驱动槽后，状态和修订均不变。
        var (resumeRunner, _, resumeFlows) = MakeRunner("q-resume-conflict", referenceProvider: null, scopeProvider: null);
        var runStore = (RunStore)typeof(WorkflowRunner)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resumeRunner)!;
        var resumeWorkflowId = SeedFlow(resumeFlows, "n1", "n2");
        var resumeSnapshot = resumeFlows.LoadSnapshot(resumeWorkflowId);
        var interrupted = runStore.CreateRun(resumeWorkflowId, resumeSnapshot.Revision);
        interrupted.State = WorkflowRunState.Interrupted;
        runStore.Update(interrupted);
        var revisionBeforeConflict = interrupted.RecordRevision;
        var controls = typeof(WorkflowRunner)
            .GetField("_controls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resumeRunner)!;
        var runControlType = controls.GetType().GetGenericArguments()[1];
        var occupiedControl = Activator.CreateInstance(runControlType, nonPublic: true)!;
        using var heldCts = new CancellationTokenSource();
        runControlType.GetProperty("RunCts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(occupiedControl, heldCts);
        Assert.True((bool)controls.GetType().GetMethod("TryAdd")!.Invoke(controls,
            new[] { (object)interrupted.RunId!, occupiedControl })!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumeRunner.ResumeAsync(interrupted.RunId!));
        var afterConflict = runStore.Load(interrupted.RunId!);
        Assert.Equal(WorkflowRunState.Interrupted, afterConflict?.State);
        Assert.Equal(revisionBeforeConflict, afterConflict?.RecordRevision);

        var (identityRunner, identityQueue, _) = MakeRunner("q-bo1c",
            referenceProvider: (_, _) => "wf-x/n1/0/100000000@ticket-1",
            scopeProvider: _ => "bgi:local:e1");
        var identityOutcome = identityRunner.TryRegisterLocalWait(new WorkflowRunRecord
        {
            RunId = "run-b21-identity",
            WorkflowId = "wf-b21-identity",
            AdmissionSourceScope = "bgi:local:e1",
        }, new WorkflowNodeOccurrence("n1", 0, 0, 100_000_000), attempt: 1);
        Assert.True(identityOutcome.Park);
        Assert.Contains("身份构造异常", identityOutcome.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(ArgumentOutOfRangeException), identityOutcome.Reason, StringComparison.Ordinal);
        Assert.Empty(identityQueue.Load());

    }

    [Fact]
    public async Task ScopeAndQueueFailures_ParkZeroSend_NotUnknownConvergence()
    {
        var (runner, queue, workflows) = MakeRunner("q-bo1b",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => throw new InvalidOperationException("scope 反查故障"));
        var run = await runner.StartAsync(SeedFlow(workflows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Contains("登记未完成", run.Note, StringComparison.Ordinal);
        Assert.Contains("异常", run.Note, StringComparison.Ordinal);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, run.CurrentSubmission?.Intent);
        Assert.False(run.CurrentSubmission?.InFlight ?? true);
        Assert.Empty(queue.Load());

        // 真实 Upsert 故障：队列目录路径被普通文件占用。Load 将缺失子路径视为空队列，
        // 随后的 Persist 在 Directory.CreateDirectory 处抛 IOException，必须由 Upsert catch 收敛。
        var occupiedQueueDirectory = Path.Combine(_dir, "q-bo1e");
        File.WriteAllText(occupiedQueueDirectory, "队列目录位置冲突");
        var (upsertRunner, upsertQueue, _) = MakeRunner("q-bo1e",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1");
        var upsertOutcome = upsertRunner.TryRegisterLocalWait(new WorkflowRunRecord
        {
            RunId = "run-b21-upsert",
            WorkflowId = "wf-b21-upsert",
            AdmissionSourceScope = "bgi:local:e1",
        }, new WorkflowNodeOccurrence("n1", 0, 0, 0), attempt: 1);
        Assert.True(upsertOutcome.Park);
        Assert.Contains("等待队列拒绝写入或存储异常", upsertOutcome.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(occupiedQueueDirectory));
        Assert.False(File.Exists(upsertQueue.FilePath));
        Assert.Empty(upsertQueue.Load());

        // 生产顺序：运行绑定先提交，队列镜像失败后仍保留 LocalWaitParking + LocalWaitDeferred，供显式恢复重建。
        var runFirstQueuePath = Path.Combine(_dir, "q-run-first");
        File.WriteAllText(runFirstQueuePath, "队列目录被普通文件占用");
        var (runFirstRunner, runFirstQueue, runFirstFlows) = MakeRunner("q-run-first",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1");
        var runFirst = await runFirstRunner.StartAsync(SeedFlow(runFirstFlows, "n1", "n2"));
        Assert.Equal(WorkflowRunState.LocalWaitParking, runFirst.State);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, runFirst.CurrentSubmission?.Intent);
        Assert.Equal(LocalWaitDecisionKind.Wait, runFirst.LocalWaitDecision?.Kind);
        Assert.NotNull(runFirst.LocalWaitDecision?.Binding);
        Assert.Contains("队列发布失败", runFirst.Note, StringComparison.Ordinal);
        Assert.Empty(runFirstQueue.Load());

        // 明确恢复时队列目录已经可写：只由 RunStore 中原绑定重建同一等待项，不签发新绑定或发送。
        var persistedBinding = runFirst.LocalWaitDecision!.Binding!;
        File.Delete(runFirstQueuePath);
        var rebuilt = await runFirstRunner.ResumeAsync(runFirst.RunId!);
        Assert.Equal(WorkflowRunState.LocalWaitParking, rebuilt.State);
        Assert.Equal(LocalWaitDecisionKind.Wait, rebuilt.LocalWaitDecision?.Kind);
        Assert.Equal(persistedBinding, rebuilt.LocalWaitDecision?.Binding);
        var rebuiltItem = Assert.Single(runFirstQueue.Load());
        Assert.Equal(persistedBinding.ItemId, rebuiltItem.ItemId);
        Assert.False(rebuiltItem.HasTrustedIdentity);

        // 若 parking/binding 的原子运行记录提交失败，不得先发布队列镜像形成无运行所有者的等待项。
        var (failedCommitRunner, failedCommitQueue, failedCommitFlows) = MakeRunner("q-run-commit-failure",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1");
        var failedCommitStore = (RunStore)typeof(WorkflowRunner)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failedCommitRunner)!;
        failedCommitStore.PublishFaultForTest = record =>
            record.State == WorkflowRunState.LocalWaitParking && record.LocalWaitDecision?.Binding is not null
                ? new IOException("injected local-wait run commit failure") : null;
        await Assert.ThrowsAsync<IOException>(() => failedCommitRunner.StartAsync(SeedFlow(failedCommitFlows, "n1", "n2")));
        Assert.Empty(failedCommitQueue.Load());
        Assert.DoesNotContain(failedCommitStore.List(), record => record.LocalWaitDecision?.Binding is not null);

        // 任意异常仍需停驻：注入时钟在 Upsert 调用前抛 ArgumentException，独立覆盖宽异常边界。
        var (clockRunner, clockQueue, _) = MakeRunner("q-bo1d",
            referenceProvider: (_, _) => "wf-x/n1/0/0@ticket-1",
            scopeProvider: _ => "bgi:local:e1",
            clock: () => throw new ArgumentException("登记时钟故障"));
        var clockOutcome = clockRunner.TryRegisterLocalWait(new WorkflowRunRecord
        {
            RunId = "run-b21-clock",
            WorkflowId = "wf-b21-clock",
            AdmissionSourceScope = "bgi:local:e1",
        }, new WorkflowNodeOccurrence("n1", 0, 0, 0), attempt: 1);
        Assert.True(clockOutcome.Park);
        Assert.Contains("登记未完成", clockOutcome.Reason, StringComparison.Ordinal);
        Assert.Contains("登记时钟故障", clockOutcome.Reason, StringComparison.Ordinal);
        Assert.Empty(clockQueue.Load());
    }

    // ---------- ②生产注入态（反射守卫：编译绿、未接线时红） ----------

    [Fact]
    public void DefaultConstructedHost_WiresLocalWaitStack()
    {
        var host = MakeHost(admissionWired: false); // 默认构造形参＝生产形状（admission 门未开）
        var queue = Assert.IsType<LocalWaitQueueStore>(HostMember(host, "LocalWaitQueue"));
        var source = Assert.IsType<WaitDecisionSource>(HostMember(host, "WaitDecisionSource"));
        Assert.True(Path.IsPathRooted(queue.FilePath));
        var decision = source.Decide(new WaitDecisionRequest
        {
            RunId = "run-closed-gate",
            WorkflowId = "wf-closed-gate",
            WorkflowRevision = "rev-1",
            NodeId = "n1",
        });
        Assert.Equal(LocalWaitDecisionKind.ContinueAdmission, decision.Kind); // 接线不打开生产准入双门
        var failedSource = new WaitDecisionSource(_ => throw new InvalidOperationException("source failure"));
        var failedDecision = failedSource.Decide(new WaitDecisionRequest { RunId = "r", WorkflowId = "wf", NodeId = "n" });
        Assert.Equal(LocalWaitDecisionKind.Hold, failedDecision.Kind);
        Assert.True(failedDecision.NoSendConfirmed);
        Assert.Null(failedDecision.Binding);

        using var client = new BgiExternalClient();
        var createRunner = typeof(TaskCenterHost).GetMethod("CreateRunner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var injectedRunner = Assert.IsType<WorkflowRunner>(createRunner.Invoke(host, new object?[] { client }));
        Assert.Same(queue, typeof(WorkflowRunner).GetField("_localWaitQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(injectedRunner));
        Assert.Same(source, typeof(WorkflowRunner).GetField("_waitDecisionSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(injectedRunner));

        // 双门显式开在夹具内时，缺唯一来源必须 Hold 且不生成可重建 binding。
        var gatedHost = MakeHost(admissionWired: true, successorAdmissionWired: true,
            admissionSeams: new TaskCenterAdmissionSeams { Occupied = false });
        var gatedRun = LegacySeedRuns(gatedHost).CreateRun("wf-no-parent", "rev-1");
        gatedRun.Cursor = new WorkflowNodeCursor { NodeId = "n1", Occurrence = 0, LoopIteration = 0 };
        LegacySeedRuns(gatedHost).Update(gatedRun);
        var noParent = gatedHost.WaitDecisionSource.Decide(new WaitDecisionRequest
        {
            RunId = gatedRun.RunId!, WorkflowId = gatedRun.WorkflowId!, WorkflowRevision = gatedRun.WorkflowRevision!,
            RecordRevision = gatedRun.RecordRevision, CursorNodeId = "n1", NodeId = "n1",
        });
        Assert.Equal(LocalWaitDecisionKind.Hold, noParent.Kind);
        Assert.Null(noParent.Context.SourceKind);
        Assert.Null(noParent.Context.Tier);
        Assert.Null(noParent.Context.Priority);
        Assert.Null(noParent.Binding);
        Assert.Empty(gatedHost.LocalWaitQueue.Load());

        // 有效的高 tier 父注册只证明来源，不把父排序继承给 successor；同步裁定和实际候选共用 Plan/0 映射。
        var rankingDir = Path.Combine(_dir, "arb-ranking");
        var rankingHost = MakeHost(admissionWired: true, arbitrationDir: rankingDir,
            successorAdmissionWired: true, admissionSeams: new TaskCenterAdmissionSeams { Occupied = true });
        var rankingStore = new ArbitrationLeaseStore(rankingDir);
        AcquireValidLease(rankingStore);
        AttachLeaseStore(rankingHost, rankingStore);
        var rankingRun = LegacySeedRuns(rankingHost).CreateRun("wf-ranking", "rev-1");
        rankingRun.Cursor = new WorkflowNodeCursor { NodeId = "n1", Occurrence = 0, LoopIteration = 0 };
        LegacySeedRuns(rankingHost).Update(rankingRun);
        AddFlowRegistrationOp(rankingStore, rankingRun.RunId, rankingRun.WorkflowId,
            "ranking-parent", ArbitrationTier.System, 99, "bgi:local:ranking");
        var rankingDecision = rankingHost.WaitDecisionSource.Decide(new WaitDecisionRequest
        {
            RunId = rankingRun.RunId!, WorkflowId = rankingRun.WorkflowId!,
            WorkflowRevision = rankingRun.WorkflowRevision!, RecordRevision = rankingRun.RecordRevision,
            CursorNodeId = "n1", NodeId = "n1",
        });
        Assert.Equal("ranking-parent", rankingDecision.Context.SourceIdentity);
        Assert.True(rankingDecision.Context.HasTrustedRankingFacts);
        Assert.Equal(ArbitrationTier.Plan, rankingDecision.Context.Tier);
        Assert.Equal(0, rankingDecision.Context.Priority);
        Assert.False(rankingDecision.Context.IsHoeingHighest);
        var actualSuccessorCandidate = TaskCenterHost.BuildSuccessorIdentityCandidate(
            "bgi:local:ranking", "wf-ranking", rankingRun.RunId!, "n1", 0, 0, 1);
        Assert.Equal(rankingDecision.Context.Tier, actualSuccessorCandidate.Tier);
        Assert.Equal(rankingDecision.Context.Priority, actualSuccessorCandidate.Priority);
    }

    // ---------- ③EV1-R1：台账有未解析记录 ⇒ 占用者级别解析拒绝缩减子集唯一命中 ----------

    [Fact]
    public async Task OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs()
    {
        var host = MakeHost(admissionWired: true, arbitrationDir: Path.Combine(_dir, "arb-ev1"));
        var store = new ArbitrationLeaseStore(Path.Combine(_dir, "arb-ev1"));
        AcquireValidLease(store);
        AttachLeaseStore(host, store);
        var runs = HostRuns(host);
        var targetRun = LegacySeedRuns(host).CreateRun("wf-ev1", "rev-1");
        AddFlowRegistrationOp(store, targetRun.RunId, targetRun.WorkflowId,
            "ev1-valid-parent", ArbitrationTier.System, 7, "bgi:local:ev1");
        File.WriteAllText(Path.Combine(runsDirOf(host), "run-corrupt.run.json"), "{ this is not json");

        var original = OccupiedTrustedWithLevels();
        var resolved = InvokeResolve(host, original, StatusWithExecution(Guid.Parse(targetRun.WireRunId)));

        // 可命中的有效等级链必须被完整性守卫挡住；否则只看 List() 的可解析子集会改写预填事实。
        Assert.Equal(original.Tier, resolved.Tier);
        Assert.Equal(original.Priority, resolved.Priority);
        Assert.Equal(original.HighestClass, resolved.HighestClass);
        Assert.Contains(_hostLog, l => l.Contains("未解析记录", StringComparison.Ordinal));

        // EV1-R1 交错：若完整性和记录分别扫描，第二次枚举才出现的损坏文件会被 List() 跳过，
        // 随后错误地按缩减集合解析等级。单次 ListWithIntegrity 必须只枚举一次。
        var raceHost = MakeHost(admissionWired: true, arbitrationDir: Path.Combine(_dir, "arb-ev1-race"));
        var raceStore = new ArbitrationLeaseStore(Path.Combine(_dir, "arb-ev1-race"));
        AcquireValidLease(raceStore);
        AttachLeaseStore(raceHost, raceStore);
        var raceRuns = HostRuns(raceHost);
        var raceTarget = LegacySeedRuns(raceHost).CreateRun("wf-ev1-race", "rev-1");
        AddFlowRegistrationOp(raceStore, raceTarget.RunId, raceTarget.WorkflowId,
            "ev1-race-parent", ArbitrationTier.System, 7, "bgi:local:ev1-race");
        var lateCorruptPath = Path.Combine(runsDirOf(raceHost), "run-late-corrupt.run.json");
        var enumerationCount = 0;
        raceRuns.FileOperationFaultForTest = operation =>
        {
            if (operation == "enumerate" && Interlocked.Increment(ref enumerationCount) == 2)
                File.WriteAllText(lateCorruptPath, "{ late corruption");
            return null;
        };
        var raceResolved = InvokeResolve(raceHost, OccupiedTrustedNoLevels(),
            StatusWithExecution(Guid.Parse(raceTarget.WireRunId)));
        Assert.Equal(1, enumerationCount);
        Assert.False(File.Exists(lateCorruptPath));
        Assert.Equal(ArbitrationTier.System, raceResolved.Tier);
        Assert.Equal(7, raceResolved.Priority);

        // 宿主 scope 解析必须与门面父绑定消费同一集合：归档中的唯一父可解析；活动+归档重复必须判歧义。
        var parentHost = MakeHost(admissionWired: true, arbitrationDir: Path.Combine(_dir, "arb-parent"));
        var parentStore = new ArbitrationLeaseStore(Path.Combine(_dir, "arb-parent"));
        AcquireValidLease(parentStore);
        AttachLeaseStore(parentHost, parentStore);
        var parentRun = LegacySeedRuns(parentHost).CreateRun("wf-parent", "rev-1");
        var parentScope = "bgi:local:parent-epoch";
        var archiveAt = DateTimeOffset.UtcNow;
        var lease = parentStore.Read().File!.Lease!;
        var archived = parentStore.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.ArchivedOperations.Add(new ArchivedOperationRecord
            {
                Operation = FlowRegistrationParent("parent-archived", parentRun.RunId, parentRun.WorkflowId,
                    parentScope, file.Revision + 1, archiveAt.AddHours(-25), archived: true),
                ArchivedAtUtc = archiveAt,
            });
            return null;
        });
        Assert.True(archived.Success, "夹具前置失败：归档父记录写入不成功 " + archived.Reason);
        var archivedParent = parentHost.AdmissionParentForTest(parentRun.RunId, parentRun.WorkflowId);
        Assert.Equal("parent-archived", archivedParent?.RequestIdentity);
        Assert.Equal(parentScope, archivedParent?.Scope);
        Assert.Equal(parentScope, InvokeResumeScope(parentHost, parentRun.RunId, parentRun.WorkflowId));

        // 同一 archived 来源解析在门面已初始化与未初始化两条路径都必须读取归档记录。
        typeof(TaskCenterHost).GetField("_admissionStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(parentHost, null);
        Assert.Equal(parentScope, InvokeResumeScope(parentHost, parentRun.RunId, parentRun.WorkflowId));

        var duplicate = parentStore.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Add(FlowRegistrationParent("parent-active", parentRun.RunId,
                parentRun.WorkflowId, parentScope, file.Revision + 1, DateTimeOffset.UtcNow, archived: false));
            return null;
        });
        Assert.True(duplicate.Success, "夹具前置失败：活动父记录写入不成功 " + duplicate.Reason);
        Assert.Null(parentHost.AdmissionParentForTest(parentRun.RunId, parentRun.WorkflowId));

        // LocalWaitParking 不能遮住尚未结清的收尾事实；启动恢复必须先收敛 Unknown。
        var recoveryRuns = new RunStore(Path.Combine(_dir, "runs-recovery-conflict"));
        var pendingCompletion = recoveryRuns.CreateRun("wf-recovery-pending", "rev-1");
        pendingCompletion.State = WorkflowRunState.LocalWaitParking;
        pendingCompletion.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-recovery-pending", NodeId = "n1", Attempt = 1,
            Intent = SubmitIntentState.LocalWaitDeferred,
        };
        pendingCompletion.PendingCompletion = new PendingCompletionRecord { State = "pending" };
        recoveryRuns.Update(pendingCompletion);
        var recoveredCompletion = Assert.Single(recoveryRuns.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recoveredCompletion.State);

        var sendConflictRuns = new RunStore(Path.Combine(_dir, "runs-recovery-send-conflict"));
        var sendConflict = sendConflictRuns.CreateRun("wf-recovery-send", "rev-1");
        sendConflict.State = WorkflowRunState.LocalWaitParking;
        sendConflict.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-recovery-send", NodeId = "n1", Attempt = 1,
            Intent = SubmitIntentState.LocalWaitDeferred, SendAttempted = true,
        };
        sendConflictRuns.Update(sendConflict);
        var recoveredSend = Assert.Single(sendConflictRuns.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Unknown, recoveredSend.State);

        // Interrupted 标签不能绕过 Accepted/job、独立 SendAttempted 或 PendingCompletion 外部事实。
        var interruptedFactsRuns = new RunStore(Path.Combine(_dir, "runs-recovery-interrupted-facts"));
        var interruptedAccepted = interruptedFactsRuns.CreateRun("wf-interrupted-accepted", "rev-1");
        interruptedAccepted.State = WorkflowRunState.Interrupted;
        interruptedAccepted.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-interrupted-accepted", NodeId = "n1", Attempt = 1,
            Intent = SubmitIntentState.Accepted, JobId = "job-accepted", SendAttempted = true,
        };
        interruptedFactsRuns.Update(interruptedAccepted);
        var interruptedAcceptedNoJob = interruptedFactsRuns.CreateRun("wf-interrupted-accepted-no-job", "rev-1");
        interruptedAcceptedNoJob.State = WorkflowRunState.Interrupted;
        interruptedAcceptedNoJob.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-interrupted-accepted-no-job", NodeId = "n1", Attempt = 1,
            Intent = SubmitIntentState.Accepted,
        };
        interruptedFactsRuns.Update(interruptedAcceptedNoJob);
        var interruptedSent = interruptedFactsRuns.CreateRun("wf-interrupted-sent", "rev-1");
        interruptedSent.State = WorkflowRunState.Interrupted;
        interruptedSent.CurrentSubmission = new WorkflowSubmission
        {
            Key = "idem-interrupted-sent", NodeId = "n1", Attempt = 1,
            Intent = SubmitIntentState.LocalWaitDeferred, SendAttempted = true,
        };
        interruptedFactsRuns.Update(interruptedSent);
        var interruptedCompletion = interruptedFactsRuns.CreateRun("wf-interrupted-completion", "rev-1");
        interruptedCompletion.State = WorkflowRunState.Interrupted;
        interruptedCompletion.PendingCompletion = new PendingCompletionRecord { State = "dispatching" };
        interruptedFactsRuns.Update(interruptedCompletion);
        var recoveredInterruptedFacts = interruptedFactsRuns.RecoverOnStart();
        Assert.Equal(4, recoveredInterruptedFacts.Count);
        Assert.All(recoveredInterruptedFacts, r => Assert.Equal(WorkflowRunState.Unknown, r.State));
        Assert.Equal(SubmitIntentState.Accepted, interruptedFactsRuns.Load(interruptedAccepted.RunId!)?.CurrentSubmission?.Intent);
        Assert.Equal("job-accepted", interruptedFactsRuns.Load(interruptedAccepted.RunId!)?.CurrentSubmission?.JobId);
        Assert.Equal(SubmitIntentState.Accepted,
            interruptedFactsRuns.Load(interruptedAcceptedNoJob.RunId!)?.CurrentSubmission?.Intent);
        Assert.Null(interruptedFactsRuns.Load(interruptedAcceptedNoJob.RunId!)?.CurrentSubmission?.JobId);
        Assert.True(interruptedFactsRuns.Load(interruptedSent.RunId!)?.CurrentSubmission?.SendAttempted);
        Assert.NotNull(interruptedFactsRuns.Load(interruptedCompletion.RunId!)?.PendingCompletion);

        // 显式 Resume 对相同三类旧/冲突记录执行同一未决事实守卫，不进入 pre-intent Wait/Hold 覆盖路径。
        var (directResumeRunner, _, directResumeFlows) = MakeRunner("q-resume-unresolved-facts",
            referenceProvider: null, scopeProvider: null);
        var directResumeRuns = (RunStore)typeof(WorkflowRunner)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(directResumeRunner)!;
        var directResumeCases = new (string Name, Action<WorkflowRunRecord> Apply)[]
        {
            ("accepted", r => r.CurrentSubmission = new WorkflowSubmission
            {
                Key = "idem-resume-accepted", NodeId = "n1", Attempt = 1,
                Intent = SubmitIntentState.Accepted, JobId = "job-resume-accepted", SendAttempted = true,
            }),
            ("accepted-no-job", r => r.CurrentSubmission = new WorkflowSubmission
            {
                Key = "idem-resume-accepted-no-job", NodeId = "n1", Attempt = 1,
                Intent = SubmitIntentState.Accepted,
            }),
            ("send-attempted", r => r.CurrentSubmission = new WorkflowSubmission
            {
                Key = "idem-resume-send", NodeId = "n1", Attempt = 1,
                Intent = SubmitIntentState.LocalWaitDeferred, SendAttempted = true,
            }),
            ("pending-completion", r => r.PendingCompletion = new PendingCompletionRecord { State = "dispatching" }),
        };
        foreach (var resumeCase in directResumeCases)
        {
            var resumeWorkflow = SeedFlow(directResumeFlows, "n1", "n2");
            var resumeRevision = directResumeFlows.LoadSnapshot(resumeWorkflow).Revision;
            var resumeFactRun = directResumeRuns.CreateRun(resumeWorkflow, resumeRevision);
            resumeFactRun.State = WorkflowRunState.Interrupted;
            resumeFactRun.Cursor = new WorkflowNodeCursor { NodeId = "n1", Occurrence = 0, LoopIteration = 0 };
            resumeCase.Apply(resumeFactRun);
            directResumeRuns.Update(resumeFactRun);
            var beforeGuardRevision = resumeFactRun.RecordRevision;
            await Assert.ThrowsAsync<InvalidOperationException>(() => directResumeRunner.ResumeAsync(resumeFactRun.RunId!));
            var afterGuard = directResumeRuns.Load(resumeFactRun.RunId!);
            Assert.Equal(WorkflowRunState.Unknown, afterGuard?.State);
            Assert.Equal(beforeGuardRevision + 1, afterGuard?.RecordRevision);
            Assert.True(RunStore.HasUnresolvedExternalFact(afterGuard!));
            if (resumeCase.Name == "accepted")
            {
                Assert.Equal(SubmitIntentState.Accepted, afterGuard?.CurrentSubmission?.Intent);
                Assert.Equal("job-resume-accepted", afterGuard?.CurrentSubmission?.JobId);
            }
            else if (resumeCase.Name == "accepted-no-job")
            {
                Assert.Equal(SubmitIntentState.Accepted, afterGuard?.CurrentSubmission?.Intent);
                Assert.Null(afterGuard?.CurrentSubmission?.JobId);
                Assert.False(afterGuard?.CurrentSubmission?.SendAttempted);
            }
            else if (resumeCase.Name == "send-attempted")
            {
                Assert.Equal(SubmitIntentState.LocalWaitDeferred, afterGuard?.CurrentSubmission?.Intent);
                Assert.True(afterGuard?.CurrentSubmission?.SendAttempted);
            }
            else
            {
                Assert.Equal("dispatching", afterGuard?.PendingCompletion?.State);
            }
        }
    }

    // ---------- helpers（对齐 LocalWaitParkingStateContractTests／TaskCenterHostOccupantLevelResolutionTests 模式） ----------

    private TaskCenterHost MakeHost(bool admissionWired = false, string? arbitrationDir = null,
        bool successorAdmissionWired = false, TaskCenterAdmissionSeams? admissionSeams = null)
        => new(
            Path.Combine(_dir, "flows-" + Guid.NewGuid().ToString("N")[..6]),
            Path.Combine(_dir, "runs-" + Guid.NewGuid().ToString("N")[..6]),
            Path.Combine(_dir, "catalog-" + Guid.NewGuid().ToString("N")[..6] + ".json"),
            clientAccessor: () => null,
            log: m => _hostLog.Add(m),
            runnerFactory: null,
            readinessOverride: null,
            localExecutionCapability: () => true,
            statusSnapshotProvider: null,
            admissionWired: admissionWired,
            arbitrationDir: arbitrationDir,
            admissionSeams: admissionSeams,
            successorAdmissionWired: successorAdmissionWired);

    private static object? HostMember(TaskCenterHost host, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var prop = host.GetType().GetProperty(name, flags);
        if (prop is not null) return prop.GetValue(host);
        var field = host.GetType().GetField(name, flags);
        return field?.GetValue(host);
    }

    // 只读解析夹具的历史种子；未让未取得资格的Host发布运行。
    private static RunStore LegacySeedRuns(TaskCenterHost host) => new(runsDirOf(host));

    private static RunStore HostRuns(TaskCenterHost host)
        => (RunStore)typeof(TaskCenterHost)
            .GetField("_runs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static string runsDirOf(TaskCenterHost host)
    {
        var store = HostRuns(host);
        var f = typeof(RunStore).GetField("_runsDir", BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(RunStore).GetField("_directory", BindingFlags.Instance | BindingFlags.NonPublic)
             ?? typeof(RunStore).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)
             ?? typeof(RunStore).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (string)f!.GetValue(store)!;
    }

    private static void AttachLeaseStore(TaskCenterHost host, ArbitrationLeaseStore store)
        => typeof(TaskCenterHost)
            .GetField("_admissionStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host, store);

    private static string? InvokeResumeScope(TaskCenterHost host, string runId, string workflowId)
        => (string?)typeof(TaskCenterHost)
            .GetMethod("TryGetAdmissionScopeForResume", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object?[] { runId, workflowId });

    private static void AcquireValidLease(ArbitrationLeaseStore store)
    {
        var acq = store.TryAcquire("b21-owner-epoch");
        Assert.True(acq.Success, "夹具前置失败：租约获取不成功 " + acq.Reason);
    }

    private static void AddFlowRegistrationOp(ArbitrationLeaseStore store, string runBinding, string workflowId,
        string requestIdentity, ArbitrationTier tier, int priority, string scope)
    {
        var lease = store.Read().File!.Lease!;
        var result = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.Operations.Add(new OperationRecord
            {
                RequestIdentity = requestIdentity,
                CandidateId = "candidate-" + requestIdentity,
                Candidate = new ArbitrationCandidate
                {
                    Scope = scope,
                    WorkflowId = workflowId,
                    NodeId = "",
                    Tier = tier,
                    Priority = priority,
                },
                RunBinding = runBinding,
                RequestState = OperationRequestState.Queued,
                Zone = OperationZone.Active,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedRevision = file.Revision + 1,
                OperationType = OperationType.FlowRegistration,
                Intent = "start",
                ResourceRef = "flow:" + workflowId,
            });
            return null;
        });
        Assert.True(result.Success, "夹具前置失败：流程登记父操作写入不成功 " + result.Reason);
    }

    private static OperationRecord FlowRegistrationParent(string requestIdentity, string runBinding,
        string workflowId, string scope, long revision, DateTimeOffset updatedAtUtc, bool archived)
        => new()
        {
            RequestIdentity = requestIdentity,
            CandidateId = "candidate-" + requestIdentity,
            Candidate = new ArbitrationCandidate { Scope = scope, WorkflowId = workflowId, NodeId = "" },
            RunBinding = runBinding,
            RequestState = archived ? OperationRequestState.TerminalCompleted : OperationRequestState.Queued,
            LastSendSeq = archived ? 1 : 0,
            SubmissionIdentity = archived ? "sub:" + requestIdentity + ":1" : null,
            Zone = archived ? OperationZone.Tombstone : OperationZone.Active,
            UpdatedAtUtc = updatedAtUtc,
            UpdatedRevision = revision,
            OperationType = OperationType.FlowRegistration,
            Intent = "start",
            ResourceRef = "flow:" + workflowId,
        };

    private static RunningOccupantFacts InvokeResolve(TaskCenterHost host, RunningOccupantFacts occupant, ControlStatus? status)
        => (RunningOccupantFacts)typeof(TaskCenterHost)
            .GetMethod("ResolveOccupantLevels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host, new object?[] { occupant, status })!;

    private static RunningOccupantFacts OccupiedTrustedWithLevels() => new()
    {
        State = OccupantFactsState.Occupied,
        HasTrustedIdentity = true,
        ExecutionInstanceId = "11111111-1111-1111-1111-111111111111",
        RunId = "run-b21-ev1",
        Kind = "workflow",
        HoeingClass = true,
        Tier = ArbitrationTier.Fixed,
        Priority = 3,
        HighestClass = false,
    };

    private static RunningOccupantFacts OccupiedTrustedNoLevels() => new()
    {
        State = OccupantFactsState.Occupied,
        HasTrustedIdentity = true,
        ExecutionInstanceId = "22222222-2222-2222-2222-222222222222",
        RunId = "run-b21-race",
        Kind = "workflow",
        HoeingClass = true,
    };

    private static ControlStatus StatusWithExecution(Guid? runId = null) => new()
    {
        TaskRunning = true,
        TaskStatusAvailable = true,
        TaskStatusBgiEpoch = "b21-epoch",
        TaskStatusObservedAtUtc = DateTimeOffset.Now,
        CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.NewGuid(), 1, RunId: runId ?? Guid.NewGuid(), JobId: null,
            Kind: "workflow", Source: "b21", Name: "b21-flow", StopRequested: false),
    };

    private (WorkflowRunner runner, LocalWaitQueueStore queue, WorkflowStore workflows) MakeRunner(
        string queueName,
        Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? referenceProvider,
        Func<WorkflowRunRecord, string?>? scopeProvider,
        Func<DateTimeOffset>? clock = null,
        WaitDecisionSource? waitDecisionSource = null,
        BoundarySubmitResult? boundaryResult = null)
    {
        var workflows = new WorkflowStore(Path.Combine(_dir, "flows-" + queueName));
        var runs = new RunStore(Path.Combine(_dir, "runs-" + queueName));
        var queue = new LocalWaitQueueStore(Path.Combine(_dir, queueName));
        var runner = new WorkflowRunner(workflows, runs, new WiringBoundary(boundaryResult), new WiringPrerequisite(),
            new WiringTerminal(), new WorkflowRunnerOptions
            {
                ShouldRegisterLocalWait = waitDecisionSource is null ? _ => true : null, // 旧测试接缝只用于指定路径
                Clock = clock ?? (() => DateTimeOffset.Now),
            }, localWaitQueue: queue,
            localWaitPrerequisiteReferenceProvider: referenceProvider,
            localWaitAdmissionScopeProvider: scopeProvider,
            waitDecisionSource: waitDecisionSource);
        return (runner, queue, workflows);
    }

    private static string SeedFlow(WorkflowStore flows, params string[] nodeIds)
    {
        var doc = new WorkflowDocument
        {
            Name = "接线夹具",
            Nodes = nodeIds.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.oneDragonConfig",
                Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
            }).ToList(),
        };
        doc.Activation = new WorkflowActivation { Status = "active" };
        flows.Save(doc, null);
        return doc.WorkflowId!;
    }

    private static LocalWaitDecisionRecord TestWaitDecision(WaitDecisionRequest request, string? runId = null,
        LocalWaitDecisionKind kind = LocalWaitDecisionKind.Wait)
        => new()
        {
            Kind = kind,
            Context = new LocalWaitDecisionContext
            {
                RunId = runId ?? request.RunId,
                WorkflowId = request.WorkflowId,
                WorkflowRevision = request.WorkflowRevision,
                RecordRevision = request.RecordRevision,
                CursorNodeId = request.CursorNodeId,
                CursorOccurrence = request.CursorOccurrence,
                CursorLoopIteration = request.CursorLoopIteration,
                NodeId = request.NodeId,
                SequenceIndex = request.SequenceIndex,
                Occurrence = request.Occurrence,
                LoopIteration = request.LoopIteration,
                Attempt = request.Attempt,
                SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
                SourceIdentity = request.RunId,
                Scope = "bgi:local:e1",
                Tier = ArbitrationTier.Plan,
                Priority = 0,
                HasTrustedRankingFacts = true,
            },
            Reason = "测试类型化等待裁定",
            NoSendConfirmed = kind != LocalWaitDecisionKind.ContinueAdmission,
        };

    private sealed class WiringBoundary : IWorkflowExecutionBoundary
    {
        private readonly BoundarySubmitResult? _submitResult;
        public WiringBoundary(BoundarySubmitResult? submitResult) => _submitResult = submitResult;
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => Task.FromResult(_submitResult ?? BoundarySubmitResult.UnknownWith("夹具：不应到达发送面（零发送）"));
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class WiringPrerequisite : IWorkflowPrerequisiteAdapter
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

    private sealed class WiringTerminal : IWorkflowTerminalExecutor
    {
        public bool[] SupportedKinds => new[] { true };
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("job-terminal"));
    }
}
