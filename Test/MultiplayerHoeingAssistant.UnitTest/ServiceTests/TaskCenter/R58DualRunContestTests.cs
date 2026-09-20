using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.8 端到端「无双跑」对抗夹具（组件/组装层）**（施工方内置、owner 0 点击）。
/// 覆盖：①**跨入口对抗**——七个入口命名空间（E1 面板/E2 恢复/E3 网页/E4 热键/E5 助手承载/直连 v2/触发）在同一仲裁面并发，
/// **恰一胜者、零二次发送**；②**F11 优先**——F11 激活时全入口一律 `F11Blocked` 且零发送（先于排序，不作候选）。
/// **证据分层（不得互相替代）**：本夹具＝**组件/组装层**证据；**协议集成层**（适配器/台账）与**真实入口/实机层**（R5.8 验收单）另见 §23；
/// **失联/接管**不双跑证据由 R5.1 接管夹具承接（本文件不重复）。
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

    private (ArbitrationAdmissionService Svc, int Sends) BuildFacade(bool f11 = false, AdmissionBarriers? barriers = null)
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var sends = 0;
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            F11Active = () => f11,
            Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); },
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                if (!r.Success) return Task.FromResult<string?>("record_failed:" + r.Reason);
                return Task.FromResult<string?>(ledger.ConfirmRebuildable(entry.SubmissionIdentity, entry.SendSeq) ? null : "not_rebuildable");
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
    public async Task SevenEntries_Concurrent_ExactlyOneWinner_NoSecondSend()
    {
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _) = BuildFacade(barriers: new AdmissionBarriers
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
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.Accepted));   // 恰一胜者（其余不得发送）
    }

    /// <summary>**F11 优先**：F11 激活时七个入口一律 `F11Blocked`、**零发送**（先于排序与租约副作用）。</summary>
    [Fact]
    public async Task SevenEntries_F11Active_AllBlocked_ZeroSend()
    {
        var (svc, _) = BuildFacade(f11: true);

        foreach (var ns in SevenEntryNamespaces)
        {
            var r = await svc.SubmitAsync(Req(ns));
            Assert.Equal(AdmissionResultKind.F11Blocked, r.Kind);
            Assert.Equal("f11_active", r.ReasonCode);
        }
    }

    /// <summary>**跨入口对抗（含恢复入口）且执行已占用**：占用事实下无人发送（授权方亦须过执行权检查）。</summary>
    [Fact]
    public async Task SevenEntries_ExecutionOccupied_NoSendAtAll()
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var sends = 0;
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true },
            Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); },
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
        Assert.Equal(0, sends);
    }
}