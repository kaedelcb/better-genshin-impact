using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.8「同轮互斥 × 恢复边界」对抗夹具（组件/组装层）**（施工方内置、owner 0 点击）。
/// 覆盖：①**七个合成启动候选**（仅用于**入口标签多样性**，**不代表真实入口一一对应**——E2 恢复走独立边界、
/// E5b/E7 属 BGI 原生排除面，见 §23）同轮并发 ⇒ **恰一胜者、发送计数＝1**；②F11 激活 ⇒ 全部 `F11Blocked`、**计数＝0** 且**无租约副作用**；
/// ③执行占用 ⇒ 全员未受理、零发送；④**恢复边界 × 启动轮次互斥**（恢复先获准后，启动候选在占用事实下全部未受理）。
/// **证据分层（不得互相替代）**：本夹具＝**组件/组装层**证据；**协议集成层**（适配器/台账）与**真实入口/实机层**（R5.8 验收单）另见 §23；
/// **失联/接管**证据由 R5.1 接管夹具承接；**真实入口/协议集成层不在此文件范围内**（§23）。
/// </summary>
public sealed class R58DualRunContestTests : IDisposable
{
    private readonly string _dir;
    private DateTimeOffset _now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private TimeSpan _mono = TimeSpan.Zero;

    public R58DualRunContestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "r58-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ArbitrationLeaseStore NewStore() => new(_dir, () => _now, () => _mono);

    /// <summary>线程安全发送计数（**实时读取**；不再返回 int 副本）。</summary>
    private sealed class SendCounter
    {
        private int _n;
        public int Count => Volatile.Read(ref _n);
        public void Inc() => Interlocked.Increment(ref _n);
    }

    private (ArbitrationAdmissionService Svc, SendCounter Sends) BuildFacade(bool f11 = false, AdmissionBarriers? barriers = null)
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var sends = new SendCounter();
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            F11Active = () => f11,
            Sender = _ => { sends.Inc(); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); },
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                if (!r.Success) return Task.FromResult<string?>("record_failed:" + r.Reason);
                return Task.FromResult<string?>(ledger.ConfirmRebuildable(entry) ? null : "not_rebuildable");
            },
            Barriers = barriers,
        };
        var svc = new ArbitrationAdmissionService(store, hooks, () => _now);
        Assert.True(svc.EnsureOwnership("pid:test").Success, "夹具前置：获取租约失败");
        return (svc, sends);
    }

    /// <summary>七个入口命名空间各一候选（E1/E2/E3/E4/E5/v2/触发）——与 §23 验收单的入口一一对应。</summary>
    private static readonly string[] SevenEntryNamespaces =
        ["manual", "resume", "web", "hotkey", "remote", "v2", "trigger"];

    private static AdmissionRequest Req(string ns)
        => new()
        {
            Namespace = ns,
            SourceDetail = "fixture:" + ns,
            // §24.17-3：创建必须携带可信操作类型（合成候选按外部启动类入口标注）。
            OperationType = OperationType.ExternalStart,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:inst:ep1",
                Namespace = ns,
                WorkflowId = "wf-" + ns,
                TriggerOccurrenceId = ns + ":occurrence",
                RunId = "",
                NodeId = "",
                Occurrence = 0,
                LoopIteration = 0,
                Attempt = 0,
                Tier = ArbitrationTier.Plan,
                Priority = 0,
                PayloadFingerprint = "p-" + ns,
                ResourceRef = "group:g-" + ns,
                Intent = "start",
            },
        };

    /// <summary>**跨入口对抗**：七个入口同轮并发 ⇒ 恰一胜者（Accepted）＋其余未获选/终局拒绝；**发送次数恒为 1**（无双跑）。</summary>
    [Fact]
    public async Task SevenSyntheticCandidates_Concurrent_ExactlyOneWinner_SendsOnce()
    {
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, sends) = BuildFacade(barriers: new AdmissionBarriers
        {
            AfterEnqueue = () => { if (Interlocked.Increment(ref arrived) == SevenEntryNamespaces.Length) allArrived.TrySetResult(); return Task.CompletedTask; },
            BeforeRoundSnapshot = () => allArrived.Task,
        });

        var tasks = SevenEntryNamespaces.Select(ns => svc.SubmitAsync(Req(ns))).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Where(r => r.Kind == AdmissionResultKind.Accepted));
        Assert.All(results.Where(r => r.Kind != AdmissionResultKind.Accepted), r =>
            Assert.True(r.Kind is AdmissionResultKind.NotSelected or AdmissionResultKind.TerminalRejected,
                "非胜者应为未获选/终局拒绝，实际 " + r.Kind + "/" + r.ReasonCode));
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.Accepted));   // 恰一胜者
        Assert.Equal(1, sends.Count);                                                  // **实时计数**：Sender 恰被调用一次（不存在双发）
    }

    /// <summary>**F11 优先**：F11 激活时七个入口一律 `F11Blocked`、**零发送**（先于排序与租约副作用）。</summary>
    [Fact]
    public async Task SevenSyntheticCandidates_F11Active_AllBlocked_ZeroSend_NoLeaseSideEffect()
    {
        var (svc, sends) = BuildFacade(f11: true);
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        var before = File.Exists(leasePath) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(leasePath))) : "<none>";

        foreach (var ns in SevenEntryNamespaces)
        {
            var r = await svc.SubmitAsync(Req(ns));
            Assert.Equal(AdmissionResultKind.F11Blocked, r.Kind);
            Assert.Equal("f11_active", r.ReasonCode);
        }

        Assert.Equal(0, sends.Count);                                   // **实时计数**：零发送
        var after = File.Exists(leasePath) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(leasePath))) : "<none>";
        Assert.Equal(before, after);                                    // **无租约副作用**（持久化状态不变）
    }

    /// <summary>
    /// **恢复边界 × 启动轮次（限定结论）**：恢复走**独立边界**（`AdmitRecoveryAsync`，不参与轮次排序）；
    /// 本用例只证明「**恢复回调次数 ≤1**」＋「**注入占用事实后启动候选全部未受理**」。
    /// **证明边界（勿外推）**：`paused-continue` 按 §5.1 只解除调度暂停，**实际节点执行另经 §4 提交边界**，本用例**不含**该边界；
    /// 故**不证明执行发送互斥**、不证明并发交错、也不证明生产占用事实已接线。
    /// </summary>
    [Fact]
    public async Task RecoveryBoundary_ThenStarts_OccupancyBlocksStarts_CallbackAtMostOnce()
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var sends = new SendCounter();
        var occupied = false;
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied },
            Sender = _ => { sends.Inc(); occupied = true; return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); },
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                return Task.FromResult<string?>(r.Success ? null : "record_failed");
            },
        };
        var svc = new ArbitrationAdmissionService(store, hooks, () => _now);
        Assert.True(svc.EnsureOwnership("pid:test").Success);

        var recovery = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:recovery",
            RunId = "run-1",
            WorkflowId = "wf-1",
            RestoreBranch = "paused-continue",
            Scope = "bgi:inst:ep1",
        });
        Assert.Equal(AdmissionResultKind.Accepted, recovery.Kind);
        Assert.Equal(1, sends.Count);

        foreach (var ns in SevenEntryNamespaces)
            Assert.NotEqual(AdmissionResultKind.Accepted, (await svc.SubmitAsync(Req(ns))).Kind);

        Assert.Equal(1, sends.Count);   // **回调次数**（Sender 桩）＝1；不代表执行发送互斥（见证明边界）
    }

    /// <summary>**执行占用**：占用事实下七个合成启动候选全部未受理、零发送（授权方亦须过执行权检查）。</summary>
    [Fact]
    public async Task SevenSyntheticCandidates_ExecutionOccupied_NoSendAtAll()
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var sends = new SendCounter();
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true },
            Sender = _ => { sends.Inc(); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); },
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                return Task.FromResult<string?>(r.Success ? null : "record_failed");
            },
        };
        var svc = new ArbitrationAdmissionService(store, hooks, () => _now);
        Assert.True(svc.EnsureOwnership("pid:test").Success);

        foreach (var ns in SevenEntryNamespaces)
        {
            var r = await svc.SubmitAsync(Req(ns));
            Assert.NotEqual(AdmissionResultKind.Accepted, r.Kind);   // 全部未受理
        }
        Assert.Equal(0, sends.Count);
    }
}
