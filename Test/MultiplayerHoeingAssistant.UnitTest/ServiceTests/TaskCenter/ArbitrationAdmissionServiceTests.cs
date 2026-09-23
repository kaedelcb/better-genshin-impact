using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// ArbitrationAdmissionService（R5.2 B1 冻结稿 v8 §3/§4）验收夹具底座：
/// 仲裁轮次（原子入队快照/并发只一胜/整组冲突拒绝/去重合并/请求状态分类表全行）、
/// 提交边界（Submission 先于发送/发布失败不发送/未知 Reconciling 不重发/锁内复核失败拒发/受理→台账→关闭顺序）、
/// 授权记录（外部自报无效）、F11 优先级（激活时无租约副作用）、故障注入（Corrupt/Expired/占用未知）、
/// 热键双路竞争、容量公式（primarySlotsUsed/迁移后重判/诊断四项）、重试窗口持久化与到期转终局、
/// 登记后入队前崩溃恢复、清理安全（到期墓碑归档+旧身份 stale_operation_identity）、
/// 接管台账（幂等合并/身份冲突/权威终态）、租约文件 v1 向后读/v3 Unsupported/写入一律 v2。
/// 涉盘用例走临时目录，finally 清理；场景全部内置，owner 0 点击。
/// </summary>
public class ArbitrationAdmissionServiceTests : IDisposable
{
    private readonly string _dir;
    private ArbitrationLeaseStore? _lastStore;
    private AdmissionHooks? _lastHooks;
    private DateTimeOffset _now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private TimeSpan _mono = TimeSpan.Zero;

    public ArbitrationAdmissionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "admit-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 基础设施 ────────────────────────────────────────────────

    private ArbitrationLeaseStore NewStore() => new(_dir, () => _now, () => _mono);

    /// <summary>takeover=true=模拟进程重启后接管（LeaseTakeoverObserver 单调观察满 TTL+锁内复核，§6.3 唯一接管依据）。</summary>
    private (ArbitrationAdmissionService Svc, ArbitrationLeaseStore Store, ExternalStartLedger Ledger, AdmissionHooks Hooks) BuildFacade(
        Action<AdmissionHooks>? configure = null, bool takeover = false, string ownerPid = "pid:test")
    {
        var store = NewStore();
        _lastStore = store;
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)),
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                if (!r.Success) return Task.FromResult<string?>("record_failed:" + r.Reason);
                return Task.FromResult<string?>(ledger.ConfirmRebuildable(entry) ? null : "not_rebuildable");
            },
            LateAcceptanceReceiptPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                if (!r.Success) return Task.FromResult<string?>("record_failed:" + r.Reason);
                return Task.FromResult<string?>(ledger.ConfirmRebuildable(entry) ? null : "not_rebuildable");
            },
        };
        configure?.Invoke(hooks);
        _lastHooks = hooks;
        var svc = new ArbitrationAdmissionService(store, hooks, () => _now);
        if (!takeover)
        {
            var acq = svc.EnsureOwnership(ownerPid);
            Assert.True(acq.Success, "夹具前置：获取租约失败 " + acq.Reason);
        }
        else
        {
            var observer = new LeaseTakeoverObserver(() => _mono);
            Assert.Null(observer.Observe(store.Read()));
            _mono += TimeSpan.FromSeconds(20);
            var evidence = observer.Observe(store.Read());
            Assert.NotNull(evidence);
            var acq = store.TryAcquire(ownerPid, evidence: evidence);
            Assert.True(acq.Success, "夹具前置：接管租约失败 " + acq.Reason);
        }

        return (svc, store, ledger, hooks);
    }

    /// <summary>入队收齐闸门：AfterEnqueue 计数满 expected 才放行 BeforeRoundSnapshot——当轮快照确定性含全部并发候选（消除线程调度先后敏感性）。</summary>
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

    private void RenewLease(ArbitrationLeaseStore store)
    {
        var read = store.Read();
        if (read.Status != ArbitrationLeaseStatus.Expired || read.File?.Lease is null) return;
        var renew = store.TryRenew(read.File.Lease.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision);
        Assert.True(renew.Success, "夹具续期失败 " + renew.Reason);
    }

    private static AdmissionRequest Req(string ns = "manual", string workflow = "group:g1", string payload = "p1",
        string scope = "bgi:inst:ep1", string? trigger = null, string? wire = null, int priority = 0,
        OperationType operationType = OperationType.FlowRegistration)
        => new()
        {
            Namespace = ns,
            SourceDetail = "fixture",
            WireSubmitKey = wire,
            // [Batch B 收尾] §24.17-3：创建必须携带可信操作类型（调用方可显式指定节点执行等类型）。
            OperationType = operationType,
            Candidate = new ArbitrationCandidate
            {
                Scope = scope,
                Namespace = ns,
                WorkflowId = workflow,
                TriggerOccurrenceId = trigger ?? (ns + ":panel:" + Guid.NewGuid().ToString("N")[..6]),
                RunId = "",
                NodeId = "",
                Occurrence = 0,
                LoopIteration = 0,
                Attempt = 0,
                Tier = ArbitrationTier.Plan,
                Priority = priority,
                ScheduledAt = null,
                PayloadFingerprint = payload,
                ResourceRef = workflow,
                Intent = "start",
            },
        };

    private static AdmissionRequest ContinueOf(AdmissionRequest created)
        => new() { Namespace = created.Namespace, Kind = AdmissionKind.ContinueUse, RequestIdentity = created.RequestIdentity, Candidate = created.Candidate };

    private LeaseReadResult ReadLease() => NewStore().Read();

    private OperationRecord? FindOp(string requestIdentity)
        => ReadLease().File?.Handoff?.Operations.FirstOrDefault(o => o.RequestIdentity == requestIdentity);

    // ── 1. 仲裁轮次：并发只一胜（无双跑）+决策集合完整 ────────────

    [Fact]
    public async Task Round_Concurrent_ExactlyOneWinner()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Barriers = GatedBarrier(2));
        var r1 = Req(priority: 1);
        var r2 = Req(priority: 0);
        var t1 = svc.SubmitAsync(r1);
        var t2 = svc.SubmitAsync(r2);
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.Accepted));
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.NotSelected));
        var loser = results.First(r => r.Kind == AdmissionResultKind.NotSelected);
        Assert.NotNull(loser.WinnerCandidateId); // 非胜者终局含胜者引用
        Assert.NotNull(loser.Decision);
        Assert.Equal(loser.WinnerCandidateId, loser.Decision!.WinnerCandidateId); // 决策集合完整（胜者引用一致）
        Assert.Equal(AdmissionResultKind.Accepted, results[0].Kind); // 高优先级胜出（确定性）
        Assert.Equal(OperationRequestState.NotSelected, FindOp(loser.RequestIdentity)!.RequestState);
    }

    // ── 2. 整组冲突拒绝（同 candidateId 不同载荷）────────────────

    [Fact]
    public async Task Round_IdentityConflict_WholeGroupRejected()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Barriers = GatedBarrier(2));
        var r1 = Req(payload: "p1", trigger: "manual:panel:fixed1");
        var r2 = Req(payload: "p2", trigger: "manual:panel:fixed1"); // 同身份不同载荷
        var t1 = svc.SubmitAsync(r1);
        var t2 = svc.SubmitAsync(r2);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Equal(AdmissionResultKind.TerminalRejected, r.Kind));
        Assert.All(results, r => Assert.Equal("identity_conflict", r.ReasonCode));
        Assert.All(results, r => Assert.Equal(OperationRequestState.TerminalRejected, FindOp(r.RequestIdentity)!.RequestState));
    }

    // ── 3. 去重合并（同身份+同载荷+同排序键）：合并项共享胜者结果、不新增发送 ──

    [Fact]
    public async Task Round_Dedupe_MergedSharesWinnerResult_NoSecondSend()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var r1 = Req(trigger: "manual:panel:dup1");
        var r2 = Req(trigger: "manual:panel:dup1"); // 完全一致
        var t1 = svc.SubmitAsync(r1);
        var t2 = svc.SubmitAsync(r2);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Equal(AdmissionResultKind.Accepted, r.Kind)); // 并发重试者合并共享胜者受理
        Assert.Equal(1, sends); // 不新增发送者
        var merged = results.Select(r => FindOp(r.RequestIdentity)!).First(o => o.LastResult!.EvidenceSource.StartsWith("merged:", StringComparison.Ordinal));
        Assert.Equal(OperationRequestState.Accepted, merged.RequestState); // 胜者已受理→合并项镜像 Accepted（非 NotSelected）
        Assert.Equal(OperationOutcome.Accepted, merged.LastResult!.Outcome);
        var cont = await svc.SubmitAsync(new AdmissionRequest // 续用既有受理：返回 already_accepted，不新增发送
        {
            Namespace = r1.Namespace, Kind = AdmissionKind.ContinueUse,
            RequestIdentity = results[0].RequestIdentity, Candidate = r1.Candidate, SourceDetail = "fixture",
        });
        Assert.Equal(AdmissionResultKind.Accepted, cont.Kind);
        Assert.Equal("already_accepted", cont.ReasonCode);
        Assert.Equal(1, sends);
    }

    // ── 4. 提交边界：Submission 先于发送；受理→台账→关闭顺序 ──────

    [Fact]
    public async Task Boundary_SubmissionBeforeSend_LedgerBeforeClose()
    {
        var sawSubmissionBeforeSend = false;
        var ledgerEmptyAtAccept = false;
        var ledgerFilledBeforeClose = false;
        var submissionPresentBeforeClose = false;
        ArbitrationLeaseStore? storeRef = null;
        ExternalStartLedger? ledgerRef = null;
        var (svc, store, ledger, _) = BuildFacade(h =>
        {
            h.Sender = d =>
            {
                var read = storeRef!.Read();
                sawSubmissionBeforeSend = read.File?.Handoff?.Submission?.SubmissionIdentity == d.SubmissionIdentity
                                        && read.File.Handoff.Submission.State == SubmissionState.Submitting;
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterAcceptBeforeLedger = () => { ledgerEmptyAtAccept = ledgerRef!.Read().File is null; return Task.CompletedTask; },
                AfterLedgerBeforeClose = () =>
                {
                    ledgerFilledBeforeClose = ledgerRef!.Read().File?.Entries.Count == 1;
                    submissionPresentBeforeClose = storeRef!.Read().File?.Handoff?.Submission is not null;
                    return Task.CompletedTask;
                },
            };
        });
        storeRef = store;
        ledgerRef = ledger;
        var r = Req();
        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.True(sawSubmissionBeforeSend, "Submission 必须先于发送落盘");
        Assert.True(ledgerEmptyAtAccept, "受理后、台账持久化前台账为空");
        Assert.True(ledgerFilledBeforeClose, "关闭前台账已在册");
        Assert.True(submissionPresentBeforeClose, "关闭前 Submission 保持未决（两记录不得同时缺失）");
        Assert.Null(ReadLease().File!.Handoff!.Submission); // 已关闭
        Assert.Equal(OperationRequestState.Accepted, FindOp(r.RequestIdentity)!.RequestState);
        Assert.Equal(1, ReadLease().File!.Handoff!.Operations[0].LastSendSeq);
    }

    // ── 5. 发布失败不发送（切换闸门在占位前激活）─────────────────

    [Fact]
    public async Task Boundary_SwitchGateBlocksOccupy_NoSend()
    {
        var sends = 0;
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () => { storeRef!.SetSwitchGate(true, "fixture"); return Task.CompletedTask; },
            };
        });
        storeRef = store;
        var r = Req();
        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("switch_gate_active", result.ReasonCode);
        Assert.Equal(0, sends); // 锁内复核失败拒发
        Assert.Equal(OperationRequestState.Queued, FindOp(r.RequestIdentity)!.RequestState); // 未发布发送许可
    }

    // ── 6. 锁内复核失败拒发（epoch 变化：只比较不重写，身份类不透明重试）──

    [Fact]
    public async Task Boundary_EpochMismatch_TerminalReject_NoSend()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.BgiEpochProvider = () => "ep2"; // epoch 首次构造固定 ep1，提交时只比较
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var r = Req();
        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
        Assert.Equal("stale_epoch", result.ReasonCode);
        Assert.Equal(0, sends);
        Assert.Equal(OperationZone.Tombstone, FindOp(r.RequestIdentity)!.Zone); // 终局→迁移墓碑（有空位）
    }

    // ── 7. 未知 Reconciling 不重发（持续停驻待对账）──────────────

    [Fact]
    public async Task Boundary_Unknown_Reconciling_NoResend()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("timeout")); });
        var r = Req();
        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.Reconciling, result.Kind);
        Assert.Equal(SubmissionState.Reconciling, ReadLease().File!.Handoff!.Submission!.State);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(r.RequestIdentity)!.RequestState);

        var again = await svc.SubmitAsync(ContinueOf(r));
        Assert.Equal(AdmissionResultKind.Reconciling, again.Kind); // 返回对账状态
        var retry = await svc.RetryAsync(r.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Reconciling, retry.Kind); // 在途不重发
        Assert.Equal(1, sends);
    }

    // ── 8. 请求状态分类表全行（续用）─────────────────────────────

    [Fact]
    public async Task Classification_AllRows()
    {
        // 行1：排队/在途——切换闸门阻止占位后操作保持 Queued
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, _, _) = BuildFacade(h => h.Barriers = new AdmissionBarriers
        {
            BeforeOccupyPublish = () => { storeRef!.SetSwitchGate(true, "f"); return Task.CompletedTask; },
        });
        storeRef = store;
        var rQueued = Req();
        var first = await svc.SubmitAsync(rQueued);
        Assert.Equal(AdmissionResultKind.Error, first.Kind); // 闸门激活→占位锁内复核响亮拒绝
        Assert.Equal("switch_gate_active", first.ReasonCode);
        var inflight = await svc.SubmitAsync(ContinueOf(rQueued));
        Assert.Equal(AdmissionResultKind.Error, inflight.Kind); // 无在途处理者→重新驱动；闸门仍激活→再次响亮拒绝（不静默成功）
        Assert.Equal("switch_gate_active", inflight.ReasonCode);
        Assert.Equal(OperationRequestState.Queued, FindOp(rQueued.RequestIdentity)!.RequestState); // 未发布发送许可→回 Queued 可再驱动
        store.SetSwitchGate(false, null);

        // 行5：身份冲突——同身份不同载荷=终局拒绝，不抹掉原事实
        var conflict = await svc.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = rQueued.RequestIdentity,
            Candidate = Req(payload: "different").Candidate,
        });
        Assert.Equal(AdmissionResultKind.TerminalRejected, conflict.Kind);
        Assert.Equal("identity_conflict", conflict.ReasonCode);
        Assert.Equal(OperationRequestState.Queued, FindOp(rQueued.RequestIdentity)!.RequestState); // 原事实不变

        // 缺失记录=stale_operation_identity（绝不回退创建）
        var stale = await svc.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = Guid.NewGuid().ToString("N"),
            Candidate = Req().Candidate,
        });
        Assert.Equal(AdmissionResultKind.Error, stale.Kind);
        Assert.Equal("stale_operation_identity", stale.ReasonCode);

        // 行3：已受理→返回既有结果（重启后新实例接管——授权以租约文件为准、不可自报）
        var (svc2, _, _, _) = BuildFacade(takeover: true);
        var rAccepted = Req();
        var accepted = await svc2.SubmitAsync(rAccepted);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);
        var cached = await svc2.SubmitAsync(ContinueOf(rAccepted));
        Assert.Equal(AdmissionResultKind.Accepted, cached.Kind);
        Assert.Equal("already_accepted", cached.ReasonCode);
        Assert.Equal(accepted.SubmissionIdentity, cached.SubmissionIdentity);

        // 行5b：终局拒绝——不静默返回成功
        var (svc3, _, _, _) = BuildFacade(h => h.BgiEpochProvider = () => "epX", takeover: true);
        var rTerminal = Req();
        _ = await svc3.SubmitAsync(rTerminal);
        var terminal = await svc3.SubmitAsync(ContinueOf(rTerminal));
        Assert.Equal(AdmissionResultKind.TerminalRejected, terminal.Kind);
        Assert.Equal("stale_epoch", terminal.ReasonCode);
    }

    // ── 9. F11 优先级：激活时 Admit 直接 F11Blocked，无租约副作用 ──

    [Fact]
    public async Task F11_Active_NoLeaseSideEffect()
    {
        var (svc, store, _, _) = BuildFacade(h => h.F11Active = () => true);
        var revisionBefore = store.Read().File!.Revision;
        var result = await svc.SubmitAsync(Req());

        Assert.Equal(AdmissionResultKind.F11Blocked, result.Kind);
        Assert.Equal(revisionBefore, store.Read().File!.Revision); // 不发生租约副作用
        Assert.Empty(store.Read().File!.Handoff?.Operations ?? []);
    }

    // ── 10. 故障注入：Corrupt/Expired/占用未知 ──────────────────

    [Fact]
    public async Task Fault_CorruptLease_LoudReject()
    {
        var (svc, _, _, _) = BuildFacade();
        File.WriteAllText(Path.Combine(_dir, "arbitration-lease.json"), "{ not json");
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("corrupt", result.ReasonCode);
    }

    [Fact]
    public async Task Fault_ExpiredLease_StaleGeneration()
    {
        var store = new ArbitrationLeaseStore(_dir, () => _now, () => _mono);
        var acq = store.TryAcquire("pid:test", ttlSeconds: 1);
        Assert.True(acq.Success);
        var svc = new ArbitrationAdmissionService(store, new AdmissionHooks { BgiEpochProvider = () => "ep1" }, () => _now);
        _mono += TimeSpan.FromSeconds(5); // 单调时钟越过 TTL（过期即禁启）
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("lease_stale_generation", result.ReasonCode);
    }

    [Fact]
    public async Task Fault_UnknownOccupancy_NeedReconcile_NoStart()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionFactsUnknown = true, FactsReference = "sub:abc" };
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
        Assert.Equal(0, sends); // 未知占用不解释为空闲
    }

    // ── 11. 热键双路竞争（本地 manual/远程 v2 同一仲裁面）─────────

    [Fact]
    public async Task Hotkey_DualPath_SamePlane_OneWinner()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Barriers = GatedBarrier(2));
        var local = Req(ns: "manual", workflow: "hotkey:hk1");
        var remote = Req(ns: "v2", workflow: "hotkey:hk1");
        var t1 = svc.SubmitAsync(local);
        var t2 = svc.SubmitAsync(remote);
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.Accepted));
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.NotSelected));
    }

    // ── 12. 重试流程：可重试拒绝→RetryAsync 重新占位（sendSeq 递增）→受理 ──

    [Fact]
    public async Task Retry_SameIdentity_SendSeqIncrements_LedgerTracksNewSeq()
    {
        var call = 0;
        var (svc, _, ledger, _) = BuildFacade(h => h.Sender = d =>
        {
            var seq = Interlocked.Increment(ref call);
            return Task.FromResult<SendOutcome>(seq == 1
                ? new SendOutcome.Rejected("task_running", Retryable: true, "ipc:任务运行中")
                : new SendOutcome.Accepted("ipc:queued", null));
        });
        var r = Req();
        var first = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        var op = FindOp(r.RequestIdentity)!;
        Assert.NotNull(op.RetryWindowDeadlineUtc); // 首次确定拒绝派生窗口
        Assert.Equal(1, op.LastSendSeq);
        Assert.Null(ReadLease().File!.Handoff!.Submission); // 确定未受理先关闭 Submission

        var second = await svc.RetryAsync(r.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, second.Kind);
        Assert.Equal(2, second.SendSeq);
        var op2 = FindOp(r.RequestIdentity)!;
        Assert.Equal(2, op2.LastSendSeq);
        Assert.Equal(1, op2.LastResult!.RetryBudgetUsed); // 重试签发预算水位递增
        Assert.NotNull(ledger.Read().File);
        Assert.Contains(ledger.Read().File!.Entries, e => e.SubmissionIdentity == second.SubmissionIdentity && e.SendSeq == 2);
    }

    [Fact]
    public async Task RetryablePrecheckReject_PreservesPriorSendIdentityAndSettledResponsibility()
    {
        var injectOccupiedAtPublish = false;
        var occupied = false;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Rejected("task_running", Retryable: true, "fixture:first_rejection"));
            };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    if (injectOccupiedAtPublish) occupied = true;
                    return Task.CompletedTask;
                },
            };
        });

        var request = Req(ns: "v2", workflow: "wf-precheck-prior-send", operationType: OperationType.ExternalStart);
        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        var prior = FindOp(request.RequestIdentity)!;
        Assert.Equal(1, prior.LastSendSeq);
        injectOccupiedAtPublish = true;

        var precheck = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.RetryableRejected, precheck.Kind);
        Assert.Equal(ResponsibilityState.Settled, precheck.ResponsibilityState);
        Assert.Equal(prior.SubmissionIdentity, precheck.SubmissionIdentity);
        Assert.Equal(prior.LastSendSeq, precheck.SendSeq);
        var afterPrecheck = FindOp(request.RequestIdentity)!;
        Assert.Equal("task_running", afterPrecheck.LastResult?.ReasonCode);
        Assert.Equal("fixture:first_rejection", afterPrecheck.LastResult?.EvidenceSource);
        Assert.Equal(1, afterPrecheck.LastResult?.AnsweredSendSeq);
        Assert.Equal("execution_occupied", afterPrecheck.LastPrecheckResult?.ReasonCode);
        Assert.Equal("final_precheck", afterPrecheck.LastPrecheckResult?.EvidenceSource);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    // ── 13. 重试窗口：持久化不重置（重启沿用）+到期锁内复核转终局+Unknown/Reconciling 不动 ──

    [Fact]
    public async Task RetryWindow_PersistedAcrossRestart_ExpiryTerminates_UnknownUntouched()
    {
        var t0 = _now;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(
            new SendOutcome.Rejected("task_running", Retryable: true, "ipc:任务运行中")));
        var rRetry = Req(wire: "wire-retry");
        _ = await svc.SubmitAsync(rRetry);
        var deadline = FindOp(rRetry.RequestIdentity)!.RetryWindowDeadlineUtc!.Value;
        Assert.Equal(t0 + ArbitrationAdmissionService.RetryWindow, deadline);

        // 重启（接管新实例）+10s：窗口沿用持久化截止，不得重置；重试仍可用
        _now = t0 + TimeSpan.FromSeconds(10);
        var (svc2, store2, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Rejected("task_running", true, "ipc:任务运行中")), takeover: true);
        Assert.Equal(deadline, FindOp(rRetry.RequestIdentity)!.RetryWindowDeadlineUtc!.Value);
        var retry = await svc2.RetryAsync(rRetry.RequestIdentity);
        Assert.Equal(AdmissionResultKind.RetryableRejected, retry.Kind);
        Assert.Equal(deadline, FindOp(rRetry.RequestIdentity)!.RetryWindowDeadlineUtc!.Value); // 再次拒绝不重置窗口
        Assert.Equal(2, FindOp(rRetry.RequestIdentity)!.LastSendSeq);

        // 窗口到期：续期租约（UTC TTL 诊断）后由所有者锁内复核→确定未受理+无更新发送责任→转终局
        _now = deadline + TimeSpan.FromSeconds(1);
        RenewLease(store2);
        var expired = await svc2.RetryAsync(rRetry.RequestIdentity);
        Assert.Equal(AdmissionResultKind.TerminalRejected, expired.Kind);
        Assert.Equal("retry_window_expired", expired.ReasonCode);
        var opFinal = FindOp(rRetry.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, opFinal.RequestState);
        Assert.Equal(OperationZone.Tombstone, opFinal.Zone); // 槽位释放（迁移墓碑）
    }

    // ── 13b. 到期扫描不触碰 Unknown/Reconciling（持续停驻待对账，§4.1a 判据表第四行）──

    [Fact]
    public async Task Sweep_UnknownReconciling_NeverTerminated()
    {
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("timeout")));
        var rUnknown = Req(wire: "wire-unknown");
        _ = await svc.SubmitAsync(rUnknown);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(rUnknown.RequestIdentity)!.RequestState);

        _now += TimeSpan.FromDays(2); // 注入远超任何窗口的时间
        RenewLease(store);
        Assert.Equal(0, svc.SweepExpiredRetryWindows()); // Unknown/Reconciling 不得终局
        Assert.Equal(OperationRequestState.Reconciling, FindOp(rUnknown.RequestIdentity)!.RequestState);
        Assert.Equal(SubmissionState.Reconciling, ReadLease().File!.Handoff!.Submission!.State);
    }

    // ── 14. 容量：32 个 Active 满员→第 33 个响亮拒绝（诊断四项）──

    [Fact]
    public async Task Capacity_PrimarySlotsFull_LoudRejectWithDiagnostics()
    {
        var (svc, _, _, _) = BuildFacade();
        for (var i = 0; i < ArbitrationAdmissionService.PrimarySlotLimit; i++)
        {
            var ok = await svc.SubmitAsync(Req(workflow: "group:g" + i));
            Assert.Equal(AdmissionResultKind.Accepted, ok.Kind); // 已受理=Active（非终局）
        }

        var overflow = await svc.SubmitAsync(Req(workflow: "group:overflow"));
        Assert.Equal(AdmissionResultKind.Error, overflow.Kind);
        Assert.StartsWith("operations_capacity_full", overflow.ReasonCode);
        Assert.Contains("active=32", overflow.ReasonCode);
        Assert.Contains("pendingTransfer=0", overflow.ReasonCode);
        Assert.Contains("tombstone=", overflow.ReasonCode);
        Assert.Contains("earliestCleanable=", overflow.ReasonCode);
    }

    // ── 15. 容量公式状态组合：墓碑满+32 待迁移→先迁移后重判（同一权威串行边界）──

    [Fact]
    public async Task Capacity_TombstoneFullPlusPendingTransfer_MigrateThenRejudge()
    {
        var (svc, store, _, _) = BuildFacade();
        Prefill(store, tombstones: ArbitrationAdmissionService.TombstoneLimit, pendingTransfers: ArbitrationAdmissionService.PrimarySlotLimit);

        // 墓碑未到期且满、主槽位 32（全为待迁移占位）→新操作响亮拒绝（不放行第 33 个原槽位）
        var rejected = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, rejected.Kind);
        Assert.Contains("pendingTransfer=32", rejected.ReasonCode);
        Assert.Contains("tombstone=256", rejected.ReasonCode);

        // 时钟越过 24h：同一串行边界内先清理到期墓碑→待迁移迁入→主槽位释放→创建放行
        _now += TimeSpan.FromHours(25);
        RenewLease(store);
        var accepted = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);
        var ops = ReadLease().File!.Handoff!.Operations;
        Assert.Equal(0, ops.Count(o => o.Zone == OperationZone.TerminalPendingTransfer)); // 全部迁墓碑
        Assert.True(ops.Count(o => o.Zone == OperationZone.Tombstone) <= ArbitrationAdmissionService.TombstoneLimit);
    }

    /// <summary>权威变更点预填（夹具技术：与生产同一 MutateHandoff 串行边界）。</summary>
    private void Prefill(ArbitrationLeaseStore store, int tombstones, int pendingTransfers)
    {
        var read = store.Read();
        var m = store.MutateHandoff(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            for (var i = 0; i < tombstones; i++)
                file.Handoff.Operations.Add(new OperationRecord
                {
                    RequestIdentity = "tomb" + i, CandidateId = "cand-t" + i, RequestState = OperationRequestState.TerminalRejected,
                    Zone = OperationZone.Tombstone, UpdatedAtUtc = _now, UpdatedRevision = file.Revision + 1,
                });
            for (var i = 0; i < pendingTransfers; i++)
                file.Handoff.Operations.Add(new OperationRecord
                {
                    RequestIdentity = "pend" + i, CandidateId = "cand-p" + i, RequestState = OperationRequestState.TerminalRejected,
                    Zone = OperationZone.TerminalPendingTransfer, UpdatedAtUtc = _now, UpdatedRevision = file.Revision + 1,
                });
            return null;
        });
        Assert.True(m.Success, "预填失败 " + m.Reason);
    }

    // ── 16. 清理安全：到期墓碑移出热区并保留归档→重启→旧身份重放=stale_operation_identity ──

    [Fact]
    public async Task Cleanup_ExpiredTombstoneArchived_OldIdentityRejected()
    {
        var (svc, store, _, _) = BuildFacade();
        Prefill(store, tombstones: 1, pendingTransfers: 0);
        _now += TimeSpan.FromHours(25); // 越过 24h 保留期
        RenewLease(store);
        var ok = await svc.SubmitAsync(Req()); // 触发同边界清理迁移
        Assert.Equal(AdmissionResultKind.Accepted, ok.Kind);
        var cleaned = ReadLease().File!.Handoff!;
        Assert.DoesNotContain(cleaned.Operations, o => o.RequestIdentity == "tomb0");
        Assert.Contains(cleaned.ArchivedOperations, o => o.Operation.RequestIdentity == "tomb0");

        // 重启（接管新实例）→重新注入旧身份：不发送/不重新绑定/响亮拒绝
        var (svc2, _, _, _) = BuildFacade(takeover: true);
        var replay = await svc2.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = "tomb0",
            Candidate = Req().Candidate,
        });
        Assert.Equal(AdmissionResultKind.Error, replay.Kind);
        Assert.Equal("stale_operation_identity", replay.ReasonCode);
    }

    [Fact]
    public async Task ArchivedFlowRegistrationStillProvidesParentBindingForNewNode()
    {
        var (svc, store, _, _) = BuildFacade();
        var parentIdentity = "archived-flow-parent";
        var oldAt = _now.AddHours(-25);
        var lease = ReadLease().File!.Lease!;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff!.ArchivedOperations.Add(new ArchivedOperationRecord
            {
                Operation = new OperationRecord
                {
                    RequestIdentity = parentIdentity,
                    CandidateId = "candidate-archived-flow-parent",
                    Candidate = new ArbitrationCandidate
                    {
                        WorkflowId = "workflow-archive-parent",
                        NodeId = "",
                    },
                    RunBinding = "run:archive-parent",
                    RequestState = OperationRequestState.TerminalCompleted,
                    LastSendSeq = 1,
                    SubmissionIdentity = $"sub:{parentIdentity}:1",
                    Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt,
                    UpdatedRevision = file.Revision + 1,
                    OperationType = OperationType.FlowRegistration,
                    Intent = "start",
                    ResourceRef = "flow:workflow-archive-parent",
                },
                ArchivedAtUtc = _now,
            });
            return null;
        });
        Assert.True(seeded.Success, seeded.Reason);

        var child = Req(ns: "archived-parent-child", workflow: "workflow-archive-parent",
            operationType: OperationType.NodeExecution);
        child.RunBinding = "run:archive-parent";
        child.Candidate.NodeId = "node:next";
        var result = await svc.SubmitAsync(child);

        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.Equal(parentIdentity, FindOp(result.RequestIdentity!)!.ParentRequestIdentity);
    }

    [Fact]
    public async Task Cleanup_ExpiredTombstonesWithCompletedHistory_FreeCapacityAndKeepCursorConsumed()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null));
        });
        var oldAt = _now.AddHours(-25);
        var lease = ReadLease().File!.Lease!;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            for (var i = 0; i < ArbitrationAdmissionService.TombstoneLimit; i++)
            {
                var identity = "history-tomb-" + i;
                var type = i == 0 ? OperationType.NodeExecution : OperationType.ExternalStart;
                var submissionIdentity = $"sub:{identity}:1";
                file.Handoff.Operations.Add(new OperationRecord
                {
                    RequestIdentity = identity,
                    CandidateId = "cand-" + identity,
                    RequestState = i == 0 ? OperationRequestState.TerminalCompleted : OperationRequestState.TerminalRejected,
                    Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt,
                    UpdatedRevision = file.Revision + 1,
                    OperationType = type,
                    TargetEpoch = "ep1",
                    SubmissionIdentity = submissionIdentity,
                    LastSendSeq = 1,
                    RunBinding = i == 0 ? "run:archive-cursor" : null,
                    CursorRef = i == 0 ? "node-0" : null,
                    CursorRevision = i == 0 ? 12 : null,
                });
                file.Handoff.PreObservations.Add(new PreObservationRecord
                {
                    SubmissionIdentity = submissionIdentity,
                    SendSeq = 1,
                    OperationType = type,
                    TargetEpoch = "ep1",
                    QueryBasis = "fixture:completed-history",
                    OwnerEpoch = "owner:old",
                    CreatedAtUtc = oldAt,
                    State = "completed",
                });
            }
            return null;
        });
        Assert.True(seeded.Success, "历史墓碑夹具写入失败 " + seeded.Reason);

        _now += TimeSpan.FromHours(25);
        RenewLease(store);
        var created = await svc.SubmitAsync(Req(ns: "after-history-cleanup", workflow: "group:after-history-cleanup"));

        Assert.Equal(AdmissionResultKind.Accepted, created.Kind);
        Assert.Equal(1, sends);
        var cleaned = ReadLease();
        Assert.Equal(ArbitrationLeaseStatus.Valid, cleaned.Status);
        Assert.Equal(ArbitrationAdmissionService.TombstoneLimit + 1, cleaned.File!.Handoff!.PreObservations.Count);
        Assert.Equal(ArbitrationAdmissionService.TombstoneLimit, cleaned.File.Handoff.ArchivedOperations.Count);
        Assert.Contains(cleaned.File.Handoff.ArchivedOperations,
            archived => archived.Operation.RequestIdentity == "history-tomb-0"
                && archived.Operation.CursorRef == "node-0"
                && archived.Operation.CursorRevision == 12);

        var staleReplay = await svc.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = "history-tomb-0",
            Candidate = Req().Candidate,
        });
        Assert.Equal(AdmissionResultKind.Error, staleReplay.Kind);
        Assert.Equal("stale_operation_identity", staleReplay.ReasonCode);

        var cursorReplayRequest = Req(ns: "cursor-replay", workflow: "group:cursor-replay",
            operationType: OperationType.NodeExecution);
        cursorReplayRequest.RunBinding = "run:archive-cursor";
        cursorReplayRequest.CursorRef = "node-0";
        cursorReplayRequest.CursorRevision = 12;
        var cursorReplay = await svc.SubmitAsync(cursorReplayRequest);
        Assert.Equal(AdmissionResultKind.TerminalRejected, cursorReplay.Kind);
        Assert.Equal("cursor_already_consumed", cursorReplay.ReasonCode);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Cleanup_ArchivesLinkedTombstonesTogether_AndKeepsChainWithHotDependency()
    {
        var (svc, store, _, _) = BuildFacade();
        var oldAt = _now.AddHours(-25);
        var lease = ReadLease().File!.Lease!;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.Operations.AddRange(
            [
                new OperationRecord
                {
                    RequestIdentity = "archive-chain-a", CandidateId = "candidate-archive-chain-a",
                    RequestState = OperationRequestState.TerminalRejected, Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt, UpdatedRevision = file.Revision + 1, MergedInto = "archive-chain-b",
                },
                new OperationRecord
                {
                    RequestIdentity = "archive-chain-b", CandidateId = "candidate-archive-chain-b",
                    RequestState = OperationRequestState.TerminalRejected, Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt, UpdatedRevision = file.Revision + 1, MergedInto = "archive-chain-c",
                },
                new OperationRecord
                {
                    RequestIdentity = "archive-chain-c", CandidateId = "candidate-archive-chain-c",
                    RequestState = OperationRequestState.TerminalRejected, Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt, UpdatedRevision = file.Revision + 1,
                },
                new OperationRecord
                {
                    RequestIdentity = "archive-pair-a", CandidateId = "candidate-archive-pair-a",
                    RequestState = OperationRequestState.TerminalRejected, Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt, UpdatedRevision = file.Revision + 1, MergedInto = "archive-pair-b",
                },
                new OperationRecord
                {
                    RequestIdentity = "archive-pair-b", CandidateId = "candidate-archive-pair-b",
                    RequestState = OperationRequestState.TerminalRejected, Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt, UpdatedRevision = file.Revision + 1,
                },
            ]);
            return null;
        });
        Assert.True(seeded.Success, "关联墓碑夹具写入失败 " + seeded.Reason);

        _now += TimeSpan.FromHours(25);
        var keepChainHot = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var dependency = file.Handoff!.Operations.Single(o => o.RequestIdentity == "archive-chain-c");
            dependency.UpdatedAtUtc = _now;
            dependency.UpdatedRevision = file.Revision + 1;
            return null;
        });
        Assert.True(keepChainHot.Success, "更新关联热记录失败 " + keepChainHot.Reason);
        RenewLease(store);

        var created = await svc.SubmitAsync(Req(ns: "after-linked-archive", workflow: "group:after-linked-archive"));

        Assert.Equal(AdmissionResultKind.Accepted, created.Kind);
        var cleaned = ReadLease();
        Assert.Equal(ArbitrationLeaseStatus.Valid, cleaned.Status);
        var handoff = cleaned.File!.Handoff!;
        Assert.Equal(2, handoff.ArchivedOperations.Count);
        Assert.Contains(handoff.ArchivedOperations, archived => archived.Operation.RequestIdentity == "archive-pair-a");
        Assert.Contains(handoff.ArchivedOperations, archived => archived.Operation.RequestIdentity == "archive-pair-b");
        Assert.DoesNotContain(handoff.ArchivedOperations,
            archived => archived.Operation.RequestIdentity is "archive-chain-a" or "archive-chain-b" or "archive-chain-c");
        Assert.Contains(handoff.Operations, operation => operation.RequestIdentity == "archive-chain-a");
        Assert.Contains(handoff.Operations, operation => operation.RequestIdentity == "archive-chain-b");
        Assert.Contains(handoff.Operations, operation => operation.RequestIdentity == "archive-chain-c");
    }

    [Fact]
    public async Task Cleanup_ExpiredSettledExternalAcceptanceClaim_ReclaimsHistoryCapacity()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion();
        var (requestIdentity, submissionIdentity, sendSeq) = await AcceptedExternalOpAsync(svc);
        var terminal = await svc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq,
            ExternalStartCompletion.SucceededWith("completed", "owner:archive-claim", _now, "job-1"));
        Assert.Equal(ResponsibilityState.Settled, terminal.ResponsibilityState);

        var settled = FindOp(requestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, settled.RequestState);
        Assert.True(settled.AcceptanceClaim is { LedgerPersisted: true });

        _now += TimeSpan.FromHours(25);
        RenewLease(_lastStore!);
        var next = await svc.SubmitAsync(Req(ns: "after-settled-claim", workflow: "group:after-settled-claim"));

        Assert.Equal(AdmissionResultKind.Accepted, next.Kind);
        var archived = Assert.Single(ReadLease().File!.Handoff!.ArchivedOperations,
            item => item.Operation.RequestIdentity == requestIdentity);
        Assert.True(archived.Operation.AcceptanceClaim is { LedgerPersisted: true });
    }

    [Fact]
    public async Task Cleanup_FullTombstonesWithMatureRelatedTerminalTransfer_ArchivesGroup()
    {
        var (svc, store, _, _) = BuildFacade();
        Prefill(store, tombstones: ArbitrationAdmissionService.TombstoneLimit, pendingTransfers: 1);
        var lease = ReadLease().File!.Lease!;
        var linked = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            foreach (var tombstone in file.Handoff!.Operations.Where(o => o.RequestIdentity.StartsWith("tomb", StringComparison.Ordinal)))
                tombstone.MergedInto = "pend0";
            return null;
        });
        Assert.True(linked.Success, "满墓碑关联夹具写入失败 " + linked.Reason);

        _now += TimeSpan.FromHours(25);
        RenewLease(store);
        var created = await svc.SubmitAsync(Req(ns: "after-full-linked-archive", workflow: "group:after-full-linked-archive"));

        Assert.Equal(AdmissionResultKind.Accepted, created.Kind);
        var read = ReadLease();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
        Assert.Contains(read.File!.Handoff!.ArchivedOperations,
            archived => archived.Operation.RequestIdentity == "pend0");
        Assert.Equal(ArbitrationAdmissionService.TombstoneLimit + 1, read.File.Handoff.ArchivedOperations.Count);
        Assert.Empty(read.File.Handoff.Operations.Where(o => o.RequestIdentity.StartsWith("tomb", StringComparison.Ordinal)));
    }

    // ── 17. 登记后入队前崩溃恢复（八轮实施关注 1）：终局中止、槽位释放、无发送 ──

    [Fact]
    public async Task Recovery_RegisteredBeforeRoundCrash_TerminalAbort_NoSend()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers { AfterRoundSnapshot = () => throw new InvalidOperationException("模拟轮次快照后裁决前崩溃") };
        });
        var r = Req();
        var crashed = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Error, crashed.Kind);
        Assert.Equal(OperationRequestState.Queued, FindOp(r.RequestIdentity)!.RequestState); // 登记在册、从未发布发送许可

        // 重启恢复：权威边界确认三无→终局中止（§4.1a 判据表第一行）
        var (svc2, _, _, _) = BuildFacade(takeover: true);
        Assert.Equal(1, svc2.RecoverAfterRestart());
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
        Assert.Equal("abandoned_before_send", op.LastPrecheckResult!.ReasonCode);
        Assert.Equal(OperationZone.Tombstone, op.Zone); // 槽位最终可释放
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Recovery_PreemptConfirmationPending_PreservesSuccessorWithoutSending()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterRoundSnapshot = () => throw new InvalidOperationException("模拟交接确认期间崩溃"),
            };
        });
        var request = Req(ns: "v2", workflow: "wf-recovery-preempt-pending", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Error, (await svc.SubmitAsync(request)).Kind);

        var lease = store.Read().File!.Lease!;
        var marked = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var operation = file.Handoff!.Operations.Single(op => op.RequestIdentity == request.RequestIdentity);
            operation.PreemptConfirmPending = true;
            return null;
        });
        Assert.True(marked.Success, marked.Reason);

        var (recovered, _, _, _) = BuildFacade(takeover: true);
        Assert.Equal(0, recovered.RecoverAfterRestart());
        var retained = FindOp(request.RequestIdentity)!;
        Assert.Equal(OperationZone.Active, retained.Zone);
        Assert.Equal(OperationRequestState.Queued, retained.RequestState);
        Assert.True(retained.PreemptConfirmPending);
        Assert.True(string.IsNullOrEmpty(retained.SubmissionIdentity));
        Assert.Equal(0, sends);
    }

    // ── 18. Submission.Submitting 重启→Reconciling（不得仅因 Submitting 发送）──

    [Fact]
    public async Task Recovery_PendingSubmissionAtRestart_ConservativeReconcile()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers { AfterOccupyBeforeSend = () => throw new InvalidOperationException("模拟发布后未发送崩溃") };
        });
        var r = Req();
        _ = await svc.SubmitAsync(r);
        Assert.Equal(SubmissionState.Submitting, ReadLease().File!.Handoff!.Submission!.State);

        var (svc2, _, _, _) = BuildFacade(takeover: true);
        Assert.Equal(0, svc2.RecoverAfterRestart()); // 无孤儿登记（该操作有发送责任）
        Assert.Equal(SubmissionState.Reconciling, ReadLease().File!.Handoff!.Submission!.State); // 保守转对账
        Assert.Equal(OperationRequestState.Reconciling, FindOp(r.RequestIdentity)!.RequestState);
        Assert.Equal(0, sends); // 恢复者不发送
    }

    // ── 19. 接管台账：幂等合并/身份冲突/权威终态/准入读取 ────────

    [Fact]
    public void Ledger_IdempotentMerge_IdentityConflict_Terminal()
    {
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var entry = new ExternalStartLedgerEntry
        {
            SubmissionIdentity = "sub:1:1", SendSeq = 1, CandidateId = "cand-a", ResourceRef = "group:g1",
            ActionId = "act-a", TargetBgiEpoch = "ep1", AcceptedAtUtc = _now, EvidenceSource = "ipc:queued",
            OperationType = OperationType.ExternalStart,
        };
        Assert.True(ledger.RecordAccepted(entry).Success);
        Assert.True(ledger.RecordAccepted(entry).Success); // 重复接管幂等
        Assert.Single(ledger.Read().File!.Entries);
        Assert.True(ledger.ConfirmRebuildable(entry));
        Assert.False(ledger.ConfirmRebuildable(new ExternalStartLedgerEntry
        {
            SubmissionIdentity = entry.SubmissionIdentity, SendSeq = entry.SendSeq,
            CandidateId = entry.CandidateId, ResourceRef = entry.ResourceRef, ActionId = entry.ActionId,
            TargetBgiEpoch = entry.TargetBgiEpoch, AcceptedAtUtc = entry.AcceptedAtUtc,
            EvidenceSource = "different-source", OperationType = entry.OperationType,
        }));
        Assert.False(ledger.ConfirmRebuildable(new ExternalStartLedgerEntry
        {
            SubmissionIdentity = entry.SubmissionIdentity, SendSeq = entry.SendSeq,
            CandidateId = entry.CandidateId, ResourceRef = entry.ResourceRef, ActionId = entry.ActionId,
            TargetBgiEpoch = entry.TargetBgiEpoch, AcceptedAtUtc = entry.AcceptedAtUtc.AddSeconds(1),
            EvidenceSource = entry.EvidenceSource, OperationType = entry.OperationType,
        }));
        Assert.False(ledger.ConfirmRebuildable(new ExternalStartLedgerEntry
        {
            SubmissionIdentity = entry.SubmissionIdentity, SendSeq = entry.SendSeq,
            CandidateId = entry.CandidateId, ResourceRef = entry.ResourceRef, ActionId = entry.ActionId,
            TargetBgiEpoch = entry.TargetBgiEpoch, AcceptedAtUtc = entry.AcceptedAtUtc,
            EvidenceSource = entry.EvidenceSource, OperationType = OperationType.NodeExecution,
        }));
        var enriched = new ExternalStartLedgerEntry
        {
            SubmissionIdentity = entry.SubmissionIdentity, SendSeq = entry.SendSeq,
            CandidateId = entry.CandidateId, ResourceRef = entry.ResourceRef, ActionId = entry.ActionId,
            TargetBgiEpoch = entry.TargetBgiEpoch, AcceptedAtUtc = entry.AcceptedAtUtc,
            EvidenceSource = entry.EvidenceSource, OperationType = entry.OperationType, JobId = "job-1",
        };
        Assert.True(ledger.RecordAccepted(enriched).Success); // 台账句柄只允许从空补齐
        Assert.True(ledger.ConfirmRebuildable(entry)); // claim 无句柄时允许单调后补
        Assert.True(ledger.ConfirmRebuildable(enriched));
        enriched.JobId = "job-2";
        Assert.False(ledger.ConfirmRebuildable(enriched));

        var conflict = ledger.RecordAccepted(new ExternalStartLedgerEntry
        {
            SubmissionIdentity = "sub:1:1", SendSeq = 1, CandidateId = "cand-OTHER", ResourceRef = "group:g1",
            ActionId = "act-a", TargetBgiEpoch = "ep1", AcceptedAtUtc = _now, EvidenceSource = "ipc:queued",
            OperationType = OperationType.ExternalStart,
        });
        Assert.False(conflict.Success);
        Assert.Equal("identity_conflict", conflict.Reason);
        Assert.Single(ledger.Read().File!.Entries); // 不产第二份

        Assert.True(ledger.ConfirmRebuildable(entry));
        // §24.2-2″：观察时点由调用方传入（首写保存、幂等重试严格比对）；缺参数/空证据=响亮拒绝。
        Assert.False(ledger.MarkTerminal("sub:1:1", 1, "", _now).Success); // 空证据=evidence_required
        Assert.True(ledger.MarkTerminal("sub:1:1", 1, "bgi_snapshot_terminal", _now).Success);
        Assert.Empty(ledger.GetOccupancy().Entries); // Terminal 不再占用
        Assert.False(ledger.GetOccupancy().Unknown);
        Assert.True(ledger.ConfirmRebuildable(entry)); // 完整终态记录同样证明「曾受理」（快速完成 job 不阻断结清）
        File.WriteAllText(Path.Combine(_dir, "external-start-ledger.json"), "{\"version\":0,\"entries\":[]}"); // 非法版本=损坏台账：Unknown=true（保守待对账，绝不推导空闲）
        Assert.True(ledger.GetOccupancy().Unknown);
        Assert.Empty(ledger.GetOccupancy().Entries);
    }

    // ── 20. 执行占用→NeedPreemptConfirm（无双跑：不重试抢占；胜者回 Queued 待交接）──

    [Fact]
    public async Task Occupied_NeedPreemptConfirm_WinnerStaysQueued()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true };
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = GatedBarrier(2);
        });
        var r1 = Req(priority: 1);
        var r2 = Req(priority: 0);
        var t1 = svc.SubmitAsync(r1);
        var t2 = svc.SubmitAsync(r2);
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.NeedPreemptConfirm));
        Assert.Equal(1, results.Count(r => r.Kind == AdmissionResultKind.NotSelected));
        Assert.Equal(0, sends); // 不抢占在跑执行
        var winner = results.First(r => r.Kind == AdmissionResultKind.NeedPreemptConfirm);
        Assert.Equal(OperationRequestState.Queued, FindOp(winner.RequestIdentity)!.RequestState); // 交接存续非终局
    }

    [Fact]
    public async Task Occupied_DeduplicatedPreemptConfirm_KeepsMirrorLinkedAndNonterminal()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true };
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("unused", null)); };
            h.Barriers = GatedBarrier(2);
        });
        var first = Req(trigger: "manual:busy-dedupe");
        var second = Req(trigger: "manual:busy-dedupe");

        var results = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(second));

        Assert.All(results, result => Assert.Contains(result.Kind,
            new[] { AdmissionResultKind.NeedPreemptConfirm, AdmissionResultKind.NeedReconcile }));
        Assert.Contains(results, result => result.Kind == AdmissionResultKind.NeedPreemptConfirm);
        Assert.Equal(0, sends);
        var operations = ReadLease().File!.Handoff!.Operations.Where(operation =>
            operation.RequestIdentity == first.RequestIdentity || operation.RequestIdentity == second.RequestIdentity).ToList();
        var winner = operations.Single(operation => operation.MergedInto is null);
        var mirror = operations.Single(operation => operation.MergedInto is not null);
        Assert.Equal(winner.RequestIdentity, mirror.MergedInto);
        Assert.Equal(OperationRequestState.Queued, winner.RequestState);
        Assert.Equal(OperationRequestState.Queued, mirror.RequestState);
        var continued = await svc.SubmitAsync(ContinueOf(mirror.RequestIdentity == first.RequestIdentity ? first : second));
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, continued.Kind);
        Assert.Equal(0, sends);
    }

    // ── 21. 租约文件 v1 向后读兼容（Pending 保留/接管写入升 3，R5.3 §24.20-A）──

    // ── 20b. §12.3 M1③/M1⑤：自有父登记占用的**限定**豁免（组件矩阵，六支 fail-closed）──

    /// <summary>
    /// **[§12.3 M1③][2026-09-22 批次四十四]**「执行占用」**只**豁免「可证明属于本宿主自有驱动、同一父授权的
    /// 节点子提交」。构造（确定性）：同一 `runBinding` 的流程登记**父操作**先受理并**完成接管关闭**
    /// （`Accepted`／`TerminalCompleted`、无开放未决发送），随后该 run 的**节点执行**在「执行占用＝真」下提交
    /// （生产时序对应：E1 登记时无占用 → 自有驱动起跑后占用成立）。
    /// **父子绑定不由调用方自报**（[批次四十四 会诊重要项处置]）：由门面在创建事务内按严格父判据自行反查，
    /// 故本夹具不再传任何父身份字段——`exempt` 支断言落盘绑定确被自动写出。
    /// 十一支：`exempt`＝唯一豁免路径（父子绑定已持久化 ⇒ 首节点**另行取得**自己的发送许可）；其余十支
    /// **必须**零**节点**发送且不签发许可——轮次级占用为 `NeedPreemptConfirm`＋`execution_occupied`（胜者回
    /// `Queued`，非终局）：`own_run_not_declared`（归属集不含本 run）／`ownership_absent`（归属不可证明＝空集）／
    /// `own_multiple_runs`（多个自有驱动并存 ⇒ 归属不唯一，不豁免）／`parent_unresolved`（父登记责任未结清：
    /// 发送结果不可考、未决发送仍在册）／`parent_wrong_type`（**父类型不属于租约侧准入来源类**——租约侧来源类
    /// 仅 `FlowRegistration`（面板启动）；启动移交的来源权威在**运行台账**（G4a），不在租约；本支用 `NodeExecution`）／
    /// `parent_source_shape_invalid`（父 `ResourceRef` 非 `flow:` 来源形状）⇒ 二者均**不产生绑定**（fail-closed）／
    /// `workflow_mismatch`（非同父授权 workflow）／`non_node_shape`（流程登记形状不豁免）／
    /// `other_node_in_flight`（同 run 另有**已受理未终结**的节点操作 ⇒ 其他节点在飞仍须阻挡）；
    /// 以及**锁内** ④ 复核拦下的 `own_withdrawn_before_occupy`（轮次已按豁免放行、占位前撤回归属）
    /// ⇒ `RetryableRejected`＋`execution_occupied`（证明锁内判定本身承重，而非只靠轮次前筛）。
    /// **范围**：组件层；**不**证明宿主自带归属事实来源与真实入口层（宿主级另有端到端夹具）。
    /// </summary>
    [Theory]
    [InlineData("exempt")]
    [InlineData("own_run_not_declared")]
    [InlineData("ownership_absent")]
    [InlineData("own_multiple_runs")]
    [InlineData("parent_unresolved")]
    [InlineData("parent_wrong_type")]
    [InlineData("parent_source_shape_invalid")]
    [InlineData("parent_workflow_mismatch")]
    [InlineData("workflow_mismatch")]
    [InlineData("non_node_shape")]
    [InlineData("other_node_in_flight")]
    [InlineData("own_withdrawn_before_occupy")]
    public async Task Ownership_ExecutionOccupied_OnlyVerifiedOwnParentChildExempted(string mode)
    {
        const string run = "run-m1";
        const string wf = "wf-m1";
        var sends = 0;
        var occupied = false;   // E1 登记时无占用（生产前置），驱动起跑后置真
        var phase = 1;          // 1＝父登记；2＝节点提交（用于把「归属在轮次后撤回」的对插点限定到节点笔）
        IReadOnlyCollection<string> own = mode switch
        {
            "own_run_not_declared" => ["run-other"],
            "ownership_absent" => [],
            "own_multiple_runs" => [run, "run-other"],
            _ => [run],
        };
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = own,
            };
            h.Sender = _ =>
            {
                // parent_unresolved 支：父登记发送结果不可考（责任未结清、未决发送仍在册）⇒ 一律不豁免
                if (mode == "parent_unresolved" && Volatile.Read(ref phase) == 1)
                    return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown"));
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true; // 父登记终局出口另测；本夹具聚焦占用豁免
            // 轮次（已按豁免归一＝放行）之后、锁内占用复核之前撤回归属 ⇒ 只可能被**锁内** ④ 复核拦下
            // （用于证明锁内豁免判定本身承重，而不是只靠轮次前筛）。
            if (mode == "own_withdrawn_before_occupy")
                h.Barriers = new AdmissionBarriers
                {
                    BeforeOccupyPublish = () =>
                    {
                        if (Volatile.Read(ref phase) == 2) own = [];
                        return Task.CompletedTask;
                    },
                };
        });

        // ① 父登记：受理 → （除 parent_unresolved 支外）终局关闭。
        //    两支持意构造「形状像父、但不满足严格判据」：类型不属于来源类／来源非 `flow:` 形状。
        var parent = Req(workflow: wf,
            operationType: mode == "parent_wrong_type" ? OperationType.NodeExecution : OperationType.FlowRegistration);
        parent.RunBinding = run;
        // parent_workflow_mismatch 支：父记录**自身自洽**（`flow:{自己的 workflow}`）但与本运行 workflow 不同
        parent.Candidate!.WorkflowId = mode == "parent_workflow_mismatch" ? "wf-other" : wf;
        parent.Candidate.ResourceRef = mode == "parent_source_shape_invalid"
            ? "flowx:" + parent.Candidate.WorkflowId
            : "flow:" + parent.Candidate.WorkflowId;
        var parentRes = await svc.SubmitAsync(parent);
        Assert.Contains(parentRes.Kind, new[] { AdmissionResultKind.Accepted, AdmissionResultKind.Reconciling });
        if (mode != "parent_unresolved")
        {
            Assert.Equal(AdmissionResultKind.Accepted,
                svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
            Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(parent.RequestIdentity)!.RequestState);
        }

        // ② other_node_in_flight 支：先建一个**同 run 的节点操作**并令其停在 `Accepted`（远端未终结）
        if (mode == "other_node_in_flight")
        {
            var prev = Req(workflow: wf, operationType: OperationType.NodeExecution);
            prev.RunBinding = run;
            prev.Candidate!.NodeId = "n-0";
            prev.Candidate.ResourceRef = "node:n-0";
            prev.Candidate.Attempt = 1;
            prev.CursorRef = "n-0#0#1";
            prev.CursorRevision = 1;
            var prevRes = await svc.SubmitAsync(prev);   // 此刻占用未成立：正常受理＝该节点责任在飞
            Assert.Equal(AdmissionResultKind.Accepted, prevRes.Kind);
        }

        // ③ 自有驱动起跑 ⇒ 执行占用成立；该 run 的节点执行提交
        var sendsBeforeTarget = sends;   // 父登记/先行节点自身那次发送不计入「本笔是否发送」的判据
        occupied = true;
        var node = Req(workflow: mode == "workflow_mismatch" ? "wf-other" : wf,
            operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#1";
        node.CursorRevision = 1;
        if (mode == "non_node_shape")
        {
            node.OperationType = OperationType.FlowRegistration;
            node.Candidate.NodeId = "";
            node.Candidate.ResourceRef = "flow:" + wf;
            node.CursorRef = null;
            node.CursorRevision = null;
        }

        Volatile.Write(ref phase, 2);
        var res = await svc.SubmitAsync(node);
        var nodeOp = FindOp(node.RequestIdentity);
        Assert.NotNull(nodeOp);

        if (mode == "exempt")
        {
            Assert.Equal(AdmissionResultKind.Accepted, res.Kind);
            Assert.Equal(sendsBeforeTarget + 1, sends);
            // 父子绑定由门面在创建事务内**自行反查并持久化**（M1⑤；不由调用方自报）
            Assert.Equal(parent.RequestIdentity, nodeOp!.ParentRequestIdentity);
            // 首节点**另行取得自己的**发送许可（M1①②）：本笔 sendSeq=1、身份自证、与父登记身份不同
            Assert.Equal(1, nodeOp.LastSendSeq);
            Assert.Equal("sub:" + node.RequestIdentity + ":1", nodeOp.SubmissionIdentity);
            var parentOp = FindOp(parent.RequestIdentity)!;
            Assert.Equal(OperationRequestState.TerminalCompleted, parentOp.RequestState);
            Assert.NotEqual(parentOp.SubmissionIdentity, nodeOp.SubmissionIdentity);
            Assert.Equal(OperationZone.Active, nodeOp.Zone);
        }
        else if (mode == "own_withdrawn_before_occupy")
        {
            // 锁内 ④ 复核拦下（轮次已放行）：可重试拒绝，操作停在可重试状态、零发送、零占位
            Assert.Equal(AdmissionResultKind.RetryableRejected, res.Kind);
            Assert.Equal("execution_occupied", res.ReasonCode);
            Assert.Equal(sendsBeforeTarget, sends);
            Assert.Equal(OperationRequestState.RetryableRejected, nodeOp!.RequestState);
            Assert.Equal(0, nodeOp.LastSendSeq);
            Assert.True(string.IsNullOrEmpty(nodeOp.SubmissionIdentity));
            Assert.Null(ReadLease().File?.Handoff?.Submission);
        }
        else
        {
            // 轮次级「执行占用→需安全交接确认」：交接未闭环 ⇒ 胜者回 Queued（非终局），零发送、零占位
            Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, res.Kind);
            Assert.Equal("execution_occupied", res.ReasonCode);
            Assert.Equal(sendsBeforeTarget, sends);   // 零**本笔**发送（父登记/先行节点那次不计）
            Assert.Equal(OperationRequestState.Queued, nodeOp!.RequestState);
            Assert.Equal(0, nodeOp.LastSendSeq);                     // 未签发许可
            Assert.True(string.IsNullOrEmpty(nodeOp.SubmissionIdentity));
            // 零**节点**占位：未决发送槽要么为空，要么仍只承载父登记自己那笔（parent_unresolved 支）
            var openSubmission = ReadLease().File?.Handoff?.Submission;
            if (openSubmission is not null)
                Assert.Equal("sub:" + parent.RequestIdentity + ":1", openSubmission.SubmissionIdentity);
            // 严格父判据不成立的两支：**不产生绑定**（不得补造）
            if (mode is "parent_wrong_type" or "parent_source_shape_invalid" or "parent_workflow_mismatch")
                Assert.Null(nodeOp.ParentRequestIdentity);
        }
    }

    /// <summary>
    /// **[§12.3 M1③ 验证会诊反例·混轮粒度]** 占用豁免**按候选**生效：同一轮里「可证明属于本宿主自有父授权的
    /// 合法节点子提交」与「非豁免候选（另一流程的流程登记）」并发入队时——节点**仍须获准**（不得因同轮混入无关
    /// 候选而被整体改判 `NeedPreemptConfirm`），非豁免者仍按占用回 `Queued`、零发送。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_MixedRound_ExemptNodeStillAdmitted()
    {
        const string run = "run-mix";
        const string wf = "wf-mix";
        var sends = 0;
        var occupied = false;
        // 入队收齐闸门**只在并发阶段武装**（父登记先行单独提交，若开局即等收齐会自死锁）。
        var barrierArmed = false;
        var arrived = 0;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (barrierArmed && Interlocked.Increment(ref arrived) == 2) bothEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => barrierArmed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        occupied = true;   // 自有驱动在飞
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        // 非豁免候选：另一流程的流程登记（不满足节点形状 ⇒ 一律不豁免）
        var other = Req(workflow: "wf-other", operationType: OperationType.FlowRegistration);
        other.RunBinding = "run-other";
        other.Candidate!.ResourceRef = "flow:wf-other";

        barrierArmed = true;   // 两笔并发入队收齐后再开轮（确定性同轮）
        var nodeTask = svc.SubmitAsync(node);
        var otherTask = svc.SubmitAsync(other);
        var results = await Task.WhenAll(nodeTask, otherTask);

        var nodeResult = results[0];
        var otherResult = results[1];
        Assert.Equal(AdmissionResultKind.Accepted, nodeResult.Kind);           // 合法节点**不被同轮无关候选连坐**
        Assert.Equal(sendsAfterParent + 1, sends);                             // 恰一次节点发送
        var nodeOp = FindOp(node.RequestIdentity);
        Assert.Equal(parent.RequestIdentity, nodeOp!.ParentRequestIdentity);
        Assert.Equal(1, nodeOp.LastSendSeq);
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, otherResult.Kind); // 非豁免候选仍按占用
        Assert.Equal("execution_occupied", otherResult.ReasonCode);
        var otherOp = FindOp(other.RequestIdentity);
        Assert.Equal(OperationRequestState.Queued, otherOp!.RequestState);      // 交接存续（非终局）
        Assert.Equal(0, otherOp.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(otherOp.SubmissionIdentity));
    }

    /// <summary>
    /// **[§12.3 M1③ 第四／五轮验证会诊反例·分流前置]**：分流**只允许发生在「纯占用」事实下**——占用与
    /// **事实未知**（或 **F11 激活**）并存时整轮沿用原语义结清，**不得**把可豁免节点单独拆出来放行。
    /// 本夹具为**真混轮**：可豁免节点与**非豁免候选**（另一流程的流程登记）并发入队（收齐闸门固定同轮），
    /// 断言：**零发送**；节点**不获准**（未签发许可、零占位）；返回类别与事实一致
    /// （未知 ⇒ `NeedReconcile`／`Queued`；F11 ⇒ `F11Blocked`／`TerminalRejected`）。
    /// </summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("f11")]
    public async Task Ownership_ExecutionOccupied_WithUnknownOrF11_MixedRoundNotSplit(string mode)
    {
        const string run = "run-guard";
        const string wf = "wf-guard";
        var sends = 0;
        var guardActive = false;   // 父登记阶段须为干净事实；节点阶段才同时成立「占用＋未知/F11」
        var armed = false;
        var arrived = 0;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = Volatile.Read(ref guardActive),
                ExecutionFactsUnknown = Volatile.Read(ref guardActive) && mode == "unknown",
                F11Active = Volatile.Read(ref guardActive) && mode == "f11",
                OwnInFlightRunBindings = [run],
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (armed && Interlocked.Increment(ref arrived) == 2) bothEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => armed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        // 已关闭的父登记（若分流被错误触发，本节点会被放行并真的发送）
        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        guardActive = true;   // 占用与「事实未知／F11」并存：整轮不得拆分
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        // 非豁免同轮候选：另一流程的流程登记（形状上不可能是节点子提交 ⇒ 一律不豁免）
        var other = Req(workflow: "wf-guard-other", operationType: OperationType.FlowRegistration);
        other.RunBinding = "run-guard-other";
        other.Candidate!.ResourceRef = "flow:wf-guard-other";
        armed = true;
        var results = await Task.WhenAll(svc.SubmitAsync(node), svc.SubmitAsync(other));
        var res = results[0];

        Assert.Equal(sendsAfterParent, sends);                       // 零发送（未拆分放行）
        var op = FindOp(node.RequestIdentity);
        Assert.NotNull(op);
        Assert.Equal(0, op!.LastSendSeq);                            // 未签发许可
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
        Assert.Null(ReadLease().File?.Handoff?.Submission);          // 零占位
        Assert.Equal(0, FindOp(other.RequestIdentity)!.LastSendSeq); // 非豁免候选同样零许可
        if (mode == "unknown")
        {
            Assert.Equal(AdmissionResultKind.NeedReconcile, res.Kind);
            Assert.Equal("facts_unknown", res.ReasonCode);
            Assert.Equal(ExecutionDisposition.Unknown, res.ExecutionDisposition);
            Assert.Equal(ResponsibilityState.Pending, res.ResponsibilityState);
            Assert.Equal(OperationRequestState.Queued, op.RequestState);
        }
        else
        {
            Assert.Equal(AdmissionResultKind.F11Blocked, res.Kind);
            Assert.Equal("f11_active", res.ReasonCode);
            Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
        }
    }

    /// <summary>
    /// **[§12.3 M1③ 第四轮验证会诊反例·身份唯一性前置]**：同一轮内**同候选号**（stable identity 相同、
    /// 绑定不同）的同伴必须继续互相比较——**不得**因一侧可豁免而拆开比较并放行。断言：**零发送**、两侧均不获准
    /// （`Accepted` 必须缺席）、两侧许可水位均为 0、无占位。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_SameCandidateIdPeer_PreventsSplitAndSending()
    {
        const string run = "run-dup";
        const string wf = "wf-dup";
        var sends = 0;
        var occupied = false;
        var arrived = 0;
        var armed = false;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (armed && Interlocked.Increment(ref arrived) == 2) bothEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => armed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        occupied = true;   // 自有驱动在飞
        // 两笔**同候选号**（同触发出现身份）但**载荷不同** ⇒ 排序层判整组身份冲突（不得因一侧可豁免而拆开）：
        // 一笔属可豁免 run（同父登记 workflow），另一笔属无父登记 run。
        var a = Req(workflow: wf, operationType: OperationType.NodeExecution, trigger: "successor:dup", payload: "p1");
        a.RunBinding = run;
        a.Candidate!.NodeId = "n-1";
        a.Candidate.ResourceRef = "node:n-1";
        a.Candidate.Attempt = 1;
        a.CursorRef = "n-1#0#0";
        a.CursorRevision = 1;
        var b = Req(workflow: wf, operationType: OperationType.NodeExecution, trigger: "successor:dup", payload: "p2");
        b.RunBinding = "run-other";
        b.Candidate!.NodeId = "n-1";
        b.Candidate.ResourceRef = "node:n-1";
        b.Candidate.Attempt = 1;
        b.CursorRef = "n-1#0#0";
        b.CursorRevision = 1;

        armed = true;
        var results = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));

        Assert.Equal(sendsAfterParent, sends);                                  // **零发送**（不得拆开比较后放行）
        // 整组身份冲突按既有合同终局拒绝（而非被拆成「一侧放行、一侧交接确认」）
        Assert.All(results, r => Assert.Equal(AdmissionResultKind.TerminalRejected, r.Kind));
        Assert.All(results, r => Assert.Equal("identity_conflict", r.ReasonCode));
        var opA = FindOp(a.RequestIdentity)!;
        var opB = FindOp(b.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, opA.RequestState);
        Assert.Equal(OperationRequestState.TerminalRejected, opB.RequestState);
        Assert.Equal("identity_conflict", opA.LastPrecheckResult?.ReasonCode);
        Assert.Equal("identity_conflict", opB.LastPrecheckResult?.ReasonCode);
        Assert.Equal(0, opA.LastSendSeq);
        Assert.Equal(0, opB.LastSendSeq);
        Assert.Null(ReadLease().File?.Handoff?.Submission);
    }

    /// <summary>
    /// **[§12.3 M1③ 第五轮验证会诊反例·不拆分回退]**：当被阻断子集的子裁决**不是** `NeedPreemptConfirm`
    /// （此处：非豁免候选资格不成立 ⇒ `NoEligibleCandidate`）时，**不得拆分**——整轮沿用原语义结清，
    /// 可豁免节点同样**不得放行**。断言：零发送、节点未签发许可、节点停在非终局 `Queued`。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_BlockedSubDecisionNotPreempt_NoSplitNoSend()
    {
        const string run = "run-fallback";
        const string wf = "wf-fallback";
        var sends = 0;
        var occupied = false;
        var armed = false;
        var arrived = 0;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            // 非本 run 的候选**资格不成立** ⇒ 其子裁决为 NoEligibleCandidate（非 NeedPreemptConfirm）
            h.EligibilityProvider = r => string.Equals(r.RunBinding, run, StringComparison.Ordinal)
                ? new CandidateEligibility()
                : new CandidateEligibility { IsDue = false, PrerequisiteReady = true, FlexibleWindowOpen = true };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (armed && Interlocked.Increment(ref arrived) == 2) bothEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => armed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        occupied = true;
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        var notDue = Req(workflow: "wf-notdue", operationType: OperationType.FlowRegistration);
        notDue.RunBinding = "run-notdue";
        notDue.Candidate!.ResourceRef = "flow:wf-notdue";

        armed = true;
        var results = await Task.WhenAll(svc.SubmitAsync(node), svc.SubmitAsync(notDue));

        Assert.Equal(sendsAfterParent, sends);                       // 零发送（未拆分放行）
        var op = FindOp(node.RequestIdentity);
        Assert.NotNull(op);
        Assert.Equal(0, op!.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
        Assert.Equal(OperationRequestState.Queued, op.RequestState); // 非终局（可再驱动）
        Assert.Null(ReadLease().File?.Handoff?.Submission);
        Assert.All(results, r => Assert.NotEqual(AdmissionResultKind.Accepted, r.Kind)); // 两侧均未获准
        Assert.Equal("not_due", FindOp(notDue.RequestIdentity)!.LastPrecheckResult?.ReasonCode);  // 子裁决确为「资格不成立」
    }

    /// <summary>
    /// **[§12.3 M1③ 第六轮验证会诊反例·真实 F11 闸门]（`fact.F11Active == false` 而 `_hooks.F11Active()==true`）**：
    /// 真实闸门在**入队之后**翻转（入口检查已过），事实快照尚未同步 ⇒ 整轮仍必须按 **F11 阻断**结清
    /// （不得落成 `NeedPreemptConfirm`）。断言：零发送、节点终局拒绝、返回 `F11Blocked`／`f11_active`。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_RealF11GateFlipsAfterEnqueue_RoundStillF11Blocked()
    {
        const string run = "run-f11gate";
        const string wf = "wf-f11gate";
        var sends = 0;
        var occupied = false;
        var gateActive = false;
        var armed = false;
        var arrived = 0;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            // 事实快照**不含** F11（模拟独立事实源未同步）；真实闸门由 hook 提供并在入队后翻转。
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.F11Active = () => Volatile.Read(ref gateActive);
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (armed && Interlocked.Increment(ref arrived) == 2)
                    {
                        Volatile.Write(ref gateActive, true);   // 入口检查已过之后真实闸门才激活
                        bothEnqueued.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => armed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        occupied = true;
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        var other = Req(workflow: "wf-f11-other", operationType: OperationType.FlowRegistration);
        other.RunBinding = "run-f11-other";
        other.Candidate!.ResourceRef = "flow:wf-f11-other";

        armed = true;
        var results = await Task.WhenAll(svc.SubmitAsync(node), svc.SubmitAsync(other));

        var nodeResult = results[0];
        Assert.Equal(sendsAfterParent, sends);                                  // 零发送
        Assert.Equal(AdmissionResultKind.F11Blocked, nodeResult.Kind);          // 按 F11 结清（非 NeedPreemptConfirm）
        Assert.Equal("f11_active", nodeResult.ReasonCode);
        // 整轮统一：同轮另一候选同样按 F11 结清（证明「整轮」而非仅本笔）
        Assert.All(results, r => Assert.Equal(AdmissionResultKind.F11Blocked, r.Kind));
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(other.RequestIdentity)!.RequestState);
        var op = FindOp(node.RequestIdentity);
        Assert.Equal(OperationRequestState.TerminalRejected, op!.RequestState);
        Assert.Equal(0, op.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
        Assert.Null(ReadLease().File?.Handoff?.Submission);
    }

    /// <summary>
    /// **[§12.3 M1③ 第六轮验证会诊反例·冲突待决]（占用 ＋ 盘上 `ConflictPending`）**：盘上任一冲突待决必须
    /// 计入「事实未知」——整轮按 `NeedReconcile`／`facts_unknown` 结清（节点回 `Queued`、零发送），
    /// **不得**落成 `NeedPreemptConfirm`。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_DiskConflictPending_RoundStaysFactsUnknown()
    {
        const string run = "run-conflict";
        const string wf = "wf-conflict";
        var sends = 0;
        var occupied = false;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);

        // 盘上植入一条**冲突待决**记录（与生产同一 MutateHandoff 串行边界）
        var read = store.Read();
        var planted = store.MutateHandoff(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.Operations.Add(new OperationRecord
            {
                RequestIdentity = "conflict-pending-fixture",
                CandidateId = "cand-conflict-fixture",
                RunBinding = "run-fixture-other",
                OperationType = OperationType.NodeExecution,
                RequestState = OperationRequestState.Reconciling,
                ConflictPending = true,
                Zone = OperationZone.Active,
                UpdatedAtUtc = _now,
                UpdatedRevision = file.Revision + 1,
            });
            return null;
        });
        Assert.True(planted.Success, "前置：冲突待决记录植入失败 " + planted.Reason);
        var sendsAfterParent = sends;

        occupied = true;
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        var res = await svc.SubmitAsync(node);

        Assert.Equal(sendsAfterParent, sends);                 // 零发送
        Assert.Equal(AdmissionResultKind.NeedReconcile, res.Kind);
        Assert.Equal("facts_unknown", res.ReasonCode);
        var op = FindOp(node.RequestIdentity);
        Assert.Equal(OperationRequestState.Queued, op!.RequestState);   // 非终局（可再驱动）
        Assert.Equal(0, op.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
    }

    /// <summary>
    /// **[§12.3 M1③ 第七轮验证会诊反例·并存优先级]**：**真实 F11 闸门**与**盘上冲突待决**并存（且执行为占用、
    /// 本笔节点本可豁免）时，整轮必须按 **F11 阻断**结清（F11 在 `Decide` 内优先级高于「待对账」），
    /// 而不是 `NeedPreemptConfirm`／`NeedReconcile`。断言：零发送、整轮统一 `F11Blocked`、节点终局拒绝、零许可。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_F11AndDiskConflict_F11WinsWholeRound()
    {
        const string run = "run-both";
        const string wf = "wf-both";
        var sends = 0;
        var occupied = false;
        var gateActive = false;
        var armed = false;
        var arrived = 0;
        var bothEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.F11Active = () => Volatile.Read(ref gateActive);
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (armed && Interlocked.Increment(ref arrived) == 2)
                    {
                        // 入口检查已过之后：占用、真实 F11、盘上冲突三者并存
                        Volatile.Write(ref occupied, true);
                        Volatile.Write(ref gateActive, true);
                        bothEnqueued.TrySetResult();
                    }
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => armed ? bothEnqueued.Task : Task.CompletedTask,
            };
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);

        // 盘上冲突待决（事实未知面）
        var read = store.Read();
        var planted = store.MutateHandoff(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, file =>
        {
            file.Handoff ??= new LeaseHandoffSegment();
            file.Handoff.Operations.Add(new OperationRecord
            {
                RequestIdentity = "conflict-with-f11",
                CandidateId = "cand-conflict-with-f11",
                RunBinding = "run-fixture-other",
                OperationType = OperationType.NodeExecution,
                RequestState = OperationRequestState.Reconciling,
                ConflictPending = true,
                Zone = OperationZone.Active,
                UpdatedAtUtc = _now,
                UpdatedRevision = file.Revision + 1,
            });
            return null;
        });
        Assert.True(planted.Success, "前置：冲突待决记录植入失败 " + planted.Reason);
        var sendsAfterParent = sends;

        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;
        var other = Req(workflow: "wf-both-other", operationType: OperationType.FlowRegistration);
        other.RunBinding = "run-both-other";
        other.Candidate!.ResourceRef = "flow:wf-both-other";

        armed = true;
        var results = await Task.WhenAll(svc.SubmitAsync(node), svc.SubmitAsync(other));

        Assert.Equal(sendsAfterParent, sends);                                   // 零发送
        Assert.All(results, r => Assert.Equal(AdmissionResultKind.F11Blocked, r.Kind)); // F11 胜出：整轮统一
        Assert.All(results, r => Assert.Equal("f11_active", r.ReasonCode));
        var op = FindOp(node.RequestIdentity);
        Assert.Equal(OperationRequestState.TerminalRejected, op!.RequestState);
        Assert.Equal(0, op.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
    }

    /// <summary>
    /// **[§12.3 M1③ 第八轮验证会诊反例·占位后发送前 F11 翻转]**：F11 是**外部活信号**，可在锁内初读之后、
    /// 占位发布之前翻转（本夹具由 `FactsProvider` 在占位事务内翻转闸门，构造确定性交错）⇒ **发送前复核**必须命中：
    /// 以「占位后、网络前的本地未发送证明」关闭该笔占位（§4.2c），返回 `F11Blocked`，**绝不发送**。
    /// 断言：零发送、无开放未决发送、操作 `TerminalRejected`＋`f11_active`＋证据源 `local_not_sent_pre_send`、
    /// 且该拒绝答复本笔轮次（`AnsweredSendSeq == LastSendSeq`）。
    /// </summary>
    [Fact]
    public async Task Ownership_ExecutionOccupied_F11FlipsDuringOccupy_NoSendAndLocalNotSentClose()
    {
        const string run = "run-presend";
        const string wf = "wf-presend";
        var sends = 0;
        var occupied = false;
        var gateActive = false;
        var flipArmed = false;
        var phase = 1;   // 1＝父登记；2＝节点提交（翻转只对节点笔武装）
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () =>
            {
                // **占位事务内**（锁内 F11 初读之后、紧接着的 facts 读取）翻转真实闸门：
                // 确定性构造「锁内读取后、许可发布前」的外部信号变化（轮次阶段不受影响）。
                if (Volatile.Read(ref flipArmed)) Volatile.Write(ref gateActive, true);
                return new ArbitrationFacts
                {
                    ExecutionOccupied = occupied,
                    OwnInFlightRunBindings = [run],
                };
            };
            h.F11Active = () => Volatile.Read(ref gateActive);
            // 轮次前段（快照/投影/锁内复核）之后、进入占位事务之前武装翻转
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    if (Volatile.Read(ref phase) == 2) Volatile.Write(ref flipArmed, true);
                    return Task.CompletedTask;
                },
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);
        var sendsAfterParent = sends;

        occupied = true;
        Volatile.Write(ref phase, 2);
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#0";
        node.CursorRevision = 1;

        var res = await svc.SubmitAsync(node);

        Assert.Equal(sendsAfterParent, sends);                                   // **零发送**
        Assert.Equal(AdmissionResultKind.F11Blocked, res.Kind);
        Assert.Equal("f11_active", res.ReasonCode);
        Assert.Equal(ResponsibilityState.Settled, res.ResponsibilityState);      // 责任已结清（本地未发送关闭）
        Assert.Equal("local_not_sent_pre_send", res.EvidenceSource);             // 证据来源回显
        Assert.False(string.IsNullOrEmpty(res.SubmissionIdentity));              // 回显本笔发送关联
        Assert.Null(ReadLease().File?.Handoff?.Submission);                      // 占位已关闭（无开放未决发送）
        var op = FindOp(node.RequestIdentity);
        Assert.Equal(OperationRequestState.TerminalRejected, op!.RequestState);
        Assert.Equal("f11_active", op.LastPrecheckResult?.ReasonCode);
        Assert.Equal("local_not_sent_pre_send", op.LastPrecheckResult?.EvidenceSource);  // §4.2c 本地未发送证明
        Assert.Equal(0, op.LastPrecheckResult?.AnsweredSendSeq);                          // 本地预检不伪装为远端发送回执
    }

    /// <summary>
    /// **[§12.3 M1③ 第九轮验证会诊反例·恢复准入同构]**：**恢复专用准入边界**（`AdmitRecoveryAsync`）同样必须
    /// 在**占位后、发送前**复核真实 F11——交错与节点路径同构（锁内 F11 初读 false、紧接着的 facts 读取把真实
    /// 闸门翻 true）。断言：零发送、占位已按本地未发送证明关闭、返回 `F11Blocked`／`f11_active`、
    /// 责任维 `Settled` 且回显本笔发送关联与证据来源。
    /// </summary>
    [Fact]
    public async Task RecoveryAdmission_F11FlipsDuringOccupy_NoSendAndLocalNotSentClose()
    {
        var sends = 0;
        var gateActive = false;
        var flipArmed = false;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () =>
            {
                if (Volatile.Read(ref flipArmed)) Volatile.Write(ref gateActive, true);
                return new ArbitrationFacts { ExecutionOccupied = false };
            };
            h.F11Active = () => Volatile.Read(ref gateActive);
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", "run:take"));
            };
        });

        // 恢复路径**无**占位前屏障接缝 ⇒ 用同一构造原理：入口 F11 检查（false）之后、占位事务内的
        // `FactsProvider` 调用把真实闸门翻为 true（占位已发布、尚未发送）。
        Volatile.Write(ref flipArmed, true);
        var res = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:f11-presend-recovery",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "paused-continue",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(0, sends);                                  // **零发送**
        Assert.Equal(AdmissionResultKind.F11Blocked, res.Kind);
        Assert.Equal("f11_active", res.ReasonCode);
        Assert.Equal(ResponsibilityState.Settled, res.ResponsibilityState);
        Assert.False(string.IsNullOrEmpty(res.SubmissionIdentity));   // 回显本笔发送关联
        Assert.True(res.SendSeq >= 1);
        Assert.Equal("local_not_sent_pre_send", res.EvidenceSource);
        Assert.Null(ReadLease().File?.Handoff?.Submission);       // 占位已关闭
    }

    /// <summary>
    /// **[§12.3 M1⑤ 会诊证据补强]** 既有 v4 租约中**缺 `parentRequestIdentity`** 的操作：读侧必须**仍判合法**
    /// （该字段是可选加法字段，缺省＝「父子关系不可证明」，**不是**损坏），且**不得**被读侧补造出任何值。
    /// 构造：走通 `exempt` 场景（父登记 + 已受理节点操作，绑定已落盘）后，把该字段从盘上 JSON 剥掉，重新读取。
    /// **范围**：只证明读取/反序列化口径与「不补造」；该记录当时为 `Accepted`（不可重驱动），故不重驱动；
    /// 也不证明历史 v2→v3 迁移路径（另由 §24.20-A′ 迁移口径覆盖）。
    /// </summary>
    [Fact]
    public async Task ParentBinding_MissingFieldInExistingV4_ReadsValidAndNotBackfilled()
    {
        const string run = "run-m1";
        const string wf = "wf-m1";
        var sends = 0;
        var occupied = false;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts
            {
                ExecutionOccupied = occupied,
                OwnInFlightRunBindings = [run],
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("host:drive_registered", run));
            };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        var parent = Req(workflow: wf, operationType: OperationType.FlowRegistration);
        parent.RunBinding = run;
        parent.Candidate!.ResourceRef = "flow:" + wf;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(parent)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(parent.RequestIdentity, "host:drive_registered").Kind);

        occupied = true;
        var node = Req(workflow: wf, operationType: OperationType.NodeExecution);
        node.RunBinding = run;
        node.Candidate!.NodeId = "n-1";
        node.Candidate.ResourceRef = "node:n-1";
        node.Candidate.Attempt = 1;
        node.CursorRef = "n-1#0#1";
        node.CursorRevision = 1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(node)).Kind);
        Assert.Equal(parent.RequestIdentity, FindOp(node.RequestIdentity)!.ParentRequestIdentity); // 前置：绑定确已落盘

        // 剥掉该字段（parentRequestIdentity 是可选字段，不属于 v4 新增的必备 acceptanceClaim）
        var path = Path.Combine(_dir, "arbitration-lease.json");
        var text = File.ReadAllText(path);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text, ",\\s*\"parentRequestIdentity\"\\s*:\\s*\"[^\"]*\"", "");
        Assert.NotEqual(text, stripped);
        File.WriteAllText(path, stripped);

        var read = NewStore().Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);        // 缺字段 ≠ 损坏
        var op = read.File!.Handoff!.Operations.FirstOrDefault(o => o.Candidate?.NodeId == "n-1");
        Assert.NotNull(op);
        Assert.Null(op!.ParentRequestIdentity);                         // 缺省＝不可证明；读侧**不补造**
        Assert.Equal(OperationRequestState.Accepted, op.RequestState);
        Assert.False(string.IsNullOrEmpty(op.SubmissionIdentity));
    }

    [Fact]
    public void LeaseV1_BackwardRead_TakeoverWritesUpgradeToV4()
    {
        var v1 = """
        {
          "version": 1,
          "revision": 5,
          "lastGeneration": 1,
          "lease": {
            "leaseId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ownerEpoch": "pid:old",
            "generation": 1,
            "heartbeatSeq": 3,
            "acquiredAtUtc": "2026-09-20T12:00:00+00:00",
            "lastHeartbeatUtc": "2026-09-20T12:00:00+00:00",
            "ttlSeconds": 15
          },
          "handoff": { "pending": null },
          "diag": null
        }
        """;
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "arbitration-lease.json"), v1);
        var store = NewStore();
        var read = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status); // v1 向后读兼容
        Assert.Equal(1, read.File!.Version);
        Assert.Null(read.File.Handoff?.Pending); // Pending 段原样保留
        Assert.Empty(read.File.Handoff?.Operations ?? []); // 缺字段视为空（向后读）

        // 写入一律 version 5（接管路径：单调观察满 TTL+锁内复核）
        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(20);
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence);
        var acq = store.TryAcquire("pid:test2", evidence: evidence);
        Assert.True(acq.Success, "接管失败 " + acq.Reason);
        Assert.Equal(5, store.Read().File!.Version); // 发布单点升级到历史归档格式
        Assert.Null(store.Read().File!.Handoff?.Pending); // Pending 段保留
        Assert.Equal(6, store.Read().File!.Revision); // 修订单调延续不回退
    }

    // ── 22. 更高版本 Unsupported + 写入一律 version 5（R5.3 §24）─────────────────

    [Fact]
    public async Task LeaseFutureVersion_Unsupported_LoudReject()
    {
        var (svc, _, _, _) = BuildFacade();
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json")).Replace("\"version\": 5", "\"version\": 6");
        File.WriteAllText(Path.Combine(_dir, "arbitration-lease.json"), text);
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("unsupported_version", result.ReasonCode);
        Assert.Equal(ArbitrationLeaseStatus.Unsupported, NewStore().Read().Status);
    }

    [Fact]
    public async Task Publish_AlwaysVersion5()
    {
        var (svc, _, _, _) = BuildFacade();
        _ = await svc.SubmitAsync(Req());
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json"));
        Assert.Contains("\"version\": 5", text);
        Assert.Equal(5, NewStore().Read().File!.Version);
    }

    // ── 23. 混合冲突组+合法胜者（逐候选分流：冲突组整组终局拒绝、合法候选照常获选受理）──

    [Fact]
    public async Task Round_MixedConflictGroup_WinnerStillAccepted()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(3);
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var c1 = Req(payload: "p1", trigger: "manual:panel:mix1"); // 冲突组 a
        var c2 = Req(payload: "p2", trigger: "manual:panel:mix1"); // 冲突组 b（同身份不同载荷）
        var legit = Req(payload: "p3");                            // 合法候选
        var t1 = svc.SubmitAsync(c1);
        var t2 = svc.SubmitAsync(c2);
        var t3 = svc.SubmitAsync(legit);
        var results = await Task.WhenAll(t1, t2, t3);

        Assert.Equal(2, results.Count(r => r.Kind == AdmissionResultKind.TerminalRejected && r.ReasonCode == "identity_conflict"));
        var winner = Assert.Single(results.Where(r => r.Kind == AdmissionResultKind.Accepted));
        Assert.Equal(legit.RequestIdentity, winner.RequestIdentity);
        Assert.Equal(1, sends); // 冲突组不阻断合法候选，且不新增发送
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(c1.RequestIdentity)!.RequestState);
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(c2.RequestIdentity)!.RequestState);
    }

    // ── 24. 锁内复核反例：占位发布前 F11/epoch 翻转（校验后变化同样阻断占位与发送）──

    [Fact]
    public async Task LockRecheck_F11FlippedBeforeOccupy_BlocksNoSend()
    {
        var f11 = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.F11Active = () => f11;
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers { BeforeOccupyPublish = () => { f11 = true; return Task.CompletedTask; } };
        });
        var r = Req();
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.F11Blocked, result.Kind);
        Assert.Equal("f11_active", result.ReasonCode);
        Assert.Equal(0, sends); // 校验后激活同样阻断发送
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(r.RequestIdentity)!.RequestState);
    }

    [Fact]
    public async Task LockRecheck_EpochFlippedBeforeOccupy_TerminalRejectNoSend()
    {
        var epoch = "ep1";
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.BgiEpochProvider = () => epoch;
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers { BeforeOccupyPublish = () => { epoch = "epX"; return Task.CompletedTask; } };
        });
        var r = Req();
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
        Assert.Equal("stale_epoch", result.ReasonCode);
        Assert.Equal(0, sends); // epoch 只比较不重写：翻转后不得发送
    }

    // ── 25. 重试完整身份：按持久化快照重建（发送目标/载荷/动作/资源/线键逐项一致，sendSeq 递增）──

    [Fact]
    public async Task Retry_RebuildsFullIdentity_FromPersistedSnapshot()
    {
        var dispatches = new List<SubmissionDispatch>();
        var call = 0;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = d =>
        {
            lock (dispatches) dispatches.Add(d);
            var seq = Interlocked.Increment(ref call);
            return Task.FromResult<SendOutcome>(seq == 1
                ? new SendOutcome.Rejected("task_running", Retryable: true, "ipc:任务运行中")
                : new SendOutcome.Accepted("ipc:queued", null));
        });
        var r = Req(wire: "wire:k1");
        _ = await svc.SubmitAsync(r);
        var retry = await svc.RetryAsync(r.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, retry.Kind);

        Assert.Equal(2, dispatches.Count);
        var d1 = dispatches[0];
        var d2 = dispatches[1];
        Assert.Equal(d1.StableIdentity, d2.StableIdentity);
        Assert.Equal(d1.CandidateId, d2.CandidateId);
        Assert.Equal(d1.ActionId, d2.ActionId);
        Assert.Equal(d1.ResourceRef, d2.ResourceRef);
        Assert.Equal(d1.Intent, d2.Intent);
        Assert.Equal(d1.TargetEpoch, d2.TargetEpoch);
        Assert.Equal(d1.WireSubmitKey, d2.WireSubmitKey);
        Assert.Equal(d1.Candidate.PayloadFingerprint, d2.Candidate.PayloadFingerprint);
        Assert.Equal(d1.Candidate.WorkflowId, d2.Candidate.WorkflowId);
        Assert.Equal(1, d1.SendSeq);
        Assert.Equal(2, d2.SendSeq); // 重试签发 sendSeq 递增（Submission 身份换新）
        Assert.NotEqual(d1.SubmissionIdentity, d2.SubmissionIdentity);
    }

    // ── 26. 显式对账两分支：受理分支（先台账后关闭）/确定未受理分支（关闭→终局拒绝）──

    [Fact]
    public async Task SettleReconciled_AcceptedBranch_LedgerThenClose()
    {
        var (svc, _, ledger, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("ipc_timeout")));
        var r = Req();
        var submit = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Reconciling, submit.Kind);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(r.RequestIdentity)!.RequestState);

        var settle = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.Accepted(submit.SubmissionIdentity!, submit.SendSeq, "owner:权威对账确认受理", "run-1"));
        Assert.Equal(AdmissionResultKind.Accepted, settle.Kind);
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, op.RequestState);
        Assert.Null(ReadLease().File!.Handoff!.Submission); // 关闭即同次原子发布移除 Submission
        Assert.Contains(ledger.Read().File!.Entries, e => e.SubmissionIdentity == submit.SubmissionIdentity && e.RunId == "run-1"); // 受理分支先台账后关闭
    }

    [Fact]
    public async Task SettleReconciled_NotAcceptedBranch_CloseThenTerminal()
    {
        var (svc, _, ledger, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("ipc_timeout")));
        var r = Req();
        var submit = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Reconciling, submit.Kind);

        var settle = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.NotAccepted(submit.SubmissionIdentity!, submit.SendSeq, "bgi_rejected", Retryable: false, "owner:权威对账确定未受理"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, settle.Kind);
        Assert.Equal("bgi_rejected", settle.ReasonCode);
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
        Assert.Empty(ledger.GetOccupancy().Entries); // 确定未受理不产生台账占用
    }

    // ── 27. 权威终态完成：台账交叉确认→TerminalCompleted→主槽位释放；未确认=保守不终局 ──

    [Fact]
    public async Task MarkOperationTerminal_LedgerCrossConfirm_ThenSlotReleased()
    {
        var (svc, _, ledger, hooks) = BuildFacade(); // 默认无 TakeoverTerminalConfirmed 钩子
        var r = Req();
        var accepted = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var denied = svc.MarkOperationTerminal(r.RequestIdentity, "bgi:job_terminal");
        Assert.Equal(AdmissionResultKind.Error, denied.Kind);
        Assert.Equal("ledger_not_terminal", denied.ReasonCode); // 未配置钩子=保守不允许终局
        Assert.Equal(OperationRequestState.Accepted, FindOp(r.RequestIdentity)!.RequestState); // 未持久化终局不报告——状态不变

        hooks.TakeoverTerminalConfirmed = (sub, seq) =>
            ledger.Read().File?.Entries.Any(e => e.SubmissionIdentity == sub && e.SendSeq == seq && e.State == LedgerEntryState.Terminal) == true;
        var stillDenied = svc.MarkOperationTerminal(r.RequestIdentity, "bgi:job_terminal");
        Assert.Equal("ledger_not_terminal", stillDenied.ReasonCode); // 台账未确认权威终态=不终局

        Assert.True(ledger.MarkTerminal(accepted.SubmissionIdentity!, accepted.SendSeq, "bgi:job_terminal", _now).Success);
        var done = svc.MarkOperationTerminal(r.RequestIdentity, "bgi:job_terminal");
        Assert.Equal(AdmissionResultKind.Accepted, done.Kind);
        Assert.Equal("terminal_completed", done.ReasonCode);
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
        Assert.Equal(OperationZone.Tombstone, op.Zone); // 同边界迁移→主槽位释放
    }

    // ── 28. 所有权更替：旧流程身份写入=lease_stale_generation（未决事实不被旧身份消解）──

    [Fact]
    public async Task OwnershipChanged_OldFlowWrite_LeaseStaleGeneration()
    {
        var sends = 0;
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers
            {
                AfterOccupyBeforeSend = () =>
                {
                    // 模拟重启后新实例接管（单调观察满 TTL+锁内复核，§6.3 唯一接管依据）
                    var observer = new LeaseTakeoverObserver(() => _mono);
                    Assert.Null(observer.Observe(storeRef!.Read()));
                    _mono += TimeSpan.FromSeconds(20);
                    var evidence = observer.Observe(storeRef!.Read());
                    Assert.NotNull(evidence);
                    var acq = storeRef!.TryAcquire("pid:other", evidence: evidence);
                    Assert.True(acq.Success, "夹具前置：新实例接管失败 " + acq.Reason);
                    return Task.CompletedTask;
                },
            };
        });
        storeRef = store;
        var r = Req();
        var result = await svc.SubmitAsync(r); // 流程起点捕获旧所有者身份；接管后受理分支关闭=旧身份写入
        Assert.Equal(AdmissionResultKind.Reconciling, result.Kind); // 关闭失败→保守待对账（台账已在册）
        Assert.Equal("lease_stale_generation", result.ReasonCode);
        Assert.Equal(1, sends);
        Assert.NotNull(store.Read().File!.Handoff!.Submission); // 旧身份不得关闭新所有者名下的 Submission
        Assert.Equal(OperationRequestState.Granted, FindOp(r.RequestIdentity)!.RequestState); // 未决事实不被旧身份推进
    }

    // ── 29. B1：状态被外部推进后，旧轮次不得回退覆盖（mark 守卫+当前事实分类）──

    [Fact]
    public async Task StateAdvancedExternally_OldRoundDoesNotOverwrite()
    {
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, _, _) = BuildFacade(h => h.Barriers = new AdmissionBarriers
        {
            AfterRoundSnapshot = () =>
            {
                // 模拟跨实例处理者：本轮 mark 前把操作推进为 Accepted
                var rd = storeRef!.Read();
                var m = storeRef.MutateHandoff(rd.File!.Lease!.LeaseId, rd.File.Lease.OwnerEpoch, rd.File.Revision, file =>
                {
                    var op = file.Handoff!.Operations.Single();
                    op.RequestState = OperationRequestState.Accepted;
                    op.SubmissionIdentity = "sub:other:1";
                    op.LastSendSeq = 1;
                    return null;
                });
                Assert.True(m.Success);
                return Task.CompletedTask;
            },
        });
        storeRef = store;
        var r = Req();
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Accepted, result.Kind); // 分类返回当前事实（不覆盖不回退）
        Assert.Equal("already_accepted", result.ReasonCode);
        Assert.Equal(OperationRequestState.Accepted, FindOp(r.RequestIdentity)!.RequestState); // 未被退回 InRound/Queued
    }

    // ── 30. B2：重试经 Queued 重入不绕过窗口（ASTRA 反例链：拒绝→facts_unknown 回队→窗口到期→续用重驱动→同边界终局不再发送）──

    [Fact]
    public async Task RetryViaQueuedRedrive_PersistedWindowStillEnforced()
    {
        var facts = new ArbitrationFacts();
        var call = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => facts;
            h.Sender = _ =>
            {
                var s = Interlocked.Increment(ref call);
                return Task.FromResult<SendOutcome>(s == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "ipc:任务运行中")
                    : new SendOutcome.Accepted("ipc:queued", null));
            };
        });
        var r = Req();
        var first = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        Assert.NotNull(FindOp(r.RequestIdentity)!.RetryWindowDeadlineUtc);

        // 重试时事实未知→回 Queued（不发送）
        facts.ExecutionFactsUnknown = true;
        var retry1 = await svc.RetryAsync(r.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retry1.Kind);
        Assert.Equal("facts_unknown", retry1.ReasonCode);
        Assert.Equal(1, call);
        Assert.Equal(OperationRequestState.Queued, FindOp(r.RequestIdentity)!.RequestState);

        // 窗口到期后事实恢复→ContinueUse 重驱动→占位按持久化发送史核验→同边界转终局（窗口不被绕过）
        _now += TimeSpan.FromSeconds(31);
        facts.ExecutionFactsUnknown = false;
        var cont = await svc.SubmitAsync(ContinueOf(r));
        Assert.Equal(AdmissionResultKind.TerminalRejected, cont.Kind);
        Assert.Equal("retry_window_expired", cont.ReasonCode);
        Assert.Equal(1, call); // 未再发送
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(r.RequestIdentity)!.RequestState);
    }

    // ── 31. B3：调用方占位后修改候选不影响已登记事实（内部冻结副本）──

    [Fact]
    public async Task CallerMutationAfterOccupy_DispatchUnchanged()
    {
        SubmissionDispatch? captured = null;
        var r = Req(payload: "original");
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = d => { captured = d; return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers
            {
                AfterOccupyBeforeSend = () =>
                {
                    r.Candidate.PayloadFingerprint = "hacked"; // 调用方后置修改/替换——不得改变发送目标/载荷
                    r.Candidate.ActionId = "act:hacked";
                    return Task.CompletedTask;
                },
            };
        });
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.NotNull(captured);
        Assert.Equal("original", captured!.Candidate.PayloadFingerprint); // 分派=登记冻结快照
        Assert.NotEqual("act:hacked", captured.ActionId);
        Assert.Equal("original", FindOp(r.RequestIdentity)!.Candidate!.PayloadFingerprint);
    }

    // ── 32. B3：续用换 workflow/scope=候选身份不符（同载荷/排序键不得命中他者缓存）──

    [Fact]
    public async Task ContinueUse_DifferentWorkflow_IdentityConflict()
    {
        var (svc, _, _, _) = BuildFacade();
        var r = Req(workflow: "group:g1");
        _ = await svc.SubmitAsync(r);
        var foreign = await svc.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = r.RequestIdentity,
            Candidate = Req(workflow: "group:g2").Candidate, // 同载荷/排序键，不同候选身份
        });
        Assert.Equal(AdmissionResultKind.TerminalRejected, foreign.Kind);
        Assert.Equal("identity_conflict", foreign.ReasonCode);
        Assert.Equal(OperationRequestState.Accepted, FindOp(r.RequestIdentity)!.RequestState); // 原事实不变
    }

    // ── 33. B4：旧轮次证据不得关闭新轮次责任（证据必须携带原发送关联）──

    [Fact]
    public async Task StaleEvidence_OldRoundCannotCloseNewRound()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("ipc_timeout")));
        var r = Req();
        var s1 = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Reconciling, s1.Kind);
        Assert.Equal(1, s1.SendSeq);
        var st1 = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.NotAccepted(s1.SubmissionIdentity!, 1, "bgi_rejected", Retryable: true, "owner:对账"));
        Assert.Equal(AdmissionResultKind.RetryableRejected, st1.Kind);

        var s2 = await svc.RetryAsync(r.RequestIdentity); // 第 2 轮发送→未知→Reconciling
        Assert.Equal(AdmissionResultKind.Reconciling, s2.Kind);
        Assert.Equal(2, s2.SendSeq);

        // 旧证据（第 1 轮身份）→响亮拒绝，第 2 轮责任不被消解
        var stale = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.NotAccepted(s1.SubmissionIdentity!, 1, "bgi_rejected", Retryable: true, "owner:对账"));
        Assert.Equal(AdmissionResultKind.Error, stale.Kind);
        Assert.Equal("stale_evidence", stale.ReasonCode);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);
        Assert.Equal(2, ReadLease().File!.Handoff!.Submission!.SendSeq);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(r.RequestIdentity)!.RequestState);

        // 正确关联（第 2 轮身份）→正常关闭
        var ok = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.Accepted(s2.SubmissionIdentity!, 2, "owner:确认受理", null));
        Assert.Equal(AdmissionResultKind.Accepted, ok.Kind);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    [Fact]
    public async Task SettleCompletion_LedgerJobIdArrivesLate_DoesNotReleaseSlotUntilBackfilled()
    {
        // TOCTOU 反例：台账句柄在「载体写入之后、终局之前」才可见 ⇒ 本轮**不得**释放占用；
        // 下一轮（句柄可见）补齐载体句柄后方可终局。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true, jobIdReadLate: "job-late");
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext-toctou");
        req.OperationType = OperationType.ExternalStart;
        var unknown = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);

        var first = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:rc", null, null,
                ExternalStartCompletion.SucceededWith("completed", "owner:rc", _now)));
        Assert.Equal(ResponsibilityState.Pending, first.ResponsibilityState);            // 停驻（不得释放占用）
        Assert.Equal("terminal_job_id_backfill_required", first.ReasonCode);             // 停驻原因原样透传（合同码）
        Assert.Equal(OperationRequestState.Reconciling, FindOp(unknown.RequestIdentity)!.RequestState);
        Assert.Null(FindOp(unknown.RequestIdentity)!.ExecutionResult!.JobId);            // 载体句柄暂为空

        var second = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:rc", null, null,
                ExternalStartCompletion.SucceededWith("completed", "owner:rc", _now)));
        Assert.Equal(ResponsibilityState.Settled, second.ResponsibilityState);
        Assert.Equal("job-late", second.JobId);
        var op = FindOp(unknown.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
        Assert.Equal("job-late", op.ExecutionResult!.JobId);                             // 补齐后与台账一致
        Assert.Equal("job-late", Assert.Single(ledger.Read().File!.Entries).JobId);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    [Fact]
    public async Task SettleCompletion_LedgerUnreadableDuringFinalize_DoesNotReleaseSlot()
    {
        // 读取故障反例：终局事务内台账复读失败/不可确认 ⇒ 不得当作「无句柄」继续（保守停驻、责任保留）。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true, ledgerUnreadableInFinalize: true);
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext-unreadable");
        req.OperationType = OperationType.ExternalStart;
        var unknown = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);

        var held = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:rc", null, null,
                ExternalStartCompletion.SucceededWith("completed", "owner:rc", _now)));
        Assert.Equal(ResponsibilityState.Pending, held.ResponsibilityState);
        Assert.Equal("terminal_ledger_unreadable", held.ReasonCode);
        var op = FindOp(unknown.RequestIdentity)!;
        Assert.NotEqual(OperationRequestState.TerminalCompleted, op.RequestState);   // 未终局
        Assert.NotNull(op.PendingTerminal);                                          // 责任保留
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);                       // Submission 未关闭
        // 台账步本身已成功（记录转 Terminal），但**终局事务内复读不可确认** ⇒ 门面拒绝在租约侧释放占用
        // （责任保留：既不关闭 Submission、也不清 PendingTerminal、也不迁墓碑）。
        Assert.Equal(LedgerEntryState.Terminal, Assert.Single(ledger.Read().File!.Entries).State);
    }

    // ── 24. 冲突登记与裁决（R5.3 §24.2-2″，Batch B 续）：追加不覆盖／四项原子事务／幂等／待决保护 ──

    /// <summary>夹具前置：确认**确定拒绝**收尾（制造「拒绝后收到冲突证据」的起点），并接真实台账三个钩子。</summary>
    private (ArbitrationAdmissionService Svc, ExternalStartLedger Ledger, ArbitrationLeaseStore Store) BuildRejectedExternalFacade(
        bool retryable = false)
    {
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Rejected("fixture_reject", retryable, "fixture:reject"));
            // [第五轮会诊] 权威未受理观察可信性校验器（夹具：仅接受 owner:* 来源）。
            h.NotAcceptedObservationVerifier = o => o.EvidenceSource.StartsWith("owner:", StringComparison.Ordinal) ? null : "untrusted_source";
            h.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, err, job, source, kind) =>
            {
                var r = ledger.MarkTerminal(sub, seq, evidence, observed, raw, err, OperationType.ExternalStart, job, source, kind);
                return r.Success ? null : "ledger_terminal_failed:" + (r.Reason ?? "unknown");
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, err, job, source, observed, kind) =>
            {
                var read = ledger.Read();
                if (!read.Valid) return false;
                var e = read.File?.Entries.FirstOrDefault(x =>
                    string.Equals(x.SubmissionIdentity, sub, StringComparison.Ordinal) && x.SendSeq == seq);
                return e is { State: LedgerEntryState.Terminal }
                    && string.Equals(e.TerminalEvidence, raw, StringComparison.Ordinal)
                    && string.Equals(e.RawTerminal, raw, StringComparison.Ordinal)
                    && string.Equals(e.ExecutionErrorCode, err, StringComparison.Ordinal)
                    && string.Equals(e.JobId, job, StringComparison.Ordinal)
                    && string.Equals(e.TerminalEvidenceSource, source, StringComparison.Ordinal)
                    && e.TerminalObservedAtUtc == observed
                    && e.TerminalKind == kind;
            };
            h.TakeoverJobIdRead = (sub, seq) =>
            {
                var read = ledger.Read();
                if (!read.Valid) return LedgerHandleProbe.Unreadable();
                var e = read.File?.Entries.FirstOrDefault(x =>
                    string.Equals(x.SubmissionIdentity, sub, StringComparison.Ordinal) && x.SendSeq == seq);
                return string.IsNullOrEmpty(e?.JobId) ? LedgerHandleProbe.Absent() : LedgerHandleProbe.Present(e!.JobId);
            };
        });
        return (svc, ledger, store);
    }

    private async Task<(string Rid, string Sub, int Seq)> RejectedExternalOpAsync(ArbitrationAdmissionService svc)
    {
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-conflict");
        req.OperationType = OperationType.ExternalStart;
        var rejected = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        return (rejected.RequestIdentity, rejected.SubmissionIdentity!, rejected.SendSeq);
    }

    [Fact]
    public async Task ConflictRegister_AfterRejection_AppendsEvidenceWithoutRelease()
    {
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);

        var reg = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-1", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        Assert.Equal(AdmissionResultKind.NeedReconcile, reg.Kind);
        Assert.Equal(ResponsibilityState.Pending, reg.ResponsibilityState);   // 冲突待决 ⇒ 不得报已结清
        var op = FindOp(rid)!;
        Assert.True(op.ConflictPending);
        Assert.Single(op.ConflictEvidence);                                   // 追加式（不覆盖既有事实）
        Assert.Equal("ev-1", op.ConflictEvidence[0].EvidenceId);
        Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
        Assert.Null(op.ExecutionResult);
    }

    [Fact]
    public async Task ConflictAdjudicate_NotAccepted_WritesEvidenceAndAuditAtomically()
    {
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-1", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        var done = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:reconcile_query",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected",
                "owner:reconcile_query", _now, sub, seq));
        Assert.Equal(AdmissionResultKind.TerminalRejected, done.Kind);        // 原拒绝终局继续有效
        Assert.Equal(ResponsibilityState.Settled, done.ResponsibilityState);

        // 读侧完整性：`_store.Read()` 必须仍然合法（审计↔证据↔Operation 双向绑定与判别式字段通过校验）。
        var lease = ReadLease();
        Assert.NotNull(lease.File);
        var op = FindOp(rid)!;
        Assert.False(op.ConflictPending);
        Assert.NotNull(op.ConflictResolutionAuditId);
        var audit = Assert.Single(lease.File!.Handoff!.ConflictResolutionAudits!);
        Assert.Equal(op.ConflictResolutionAuditId, audit.AuditId);
        Assert.Equal(ConflictResolutionKind.ResolvedNotAccepted, audit.Resolution);
        Assert.Null(audit.ResolutionEvidenceSnapshot);                        // 判别式：未受理分支不得有快照
        var evidence = Assert.Single(lease.File.Handoff.ReconciledNotAcceptedEvidence!);
        Assert.Equal(audit.ResolutionEvidenceRef!.EvidenceId, evidence.EvidenceId);
    }

    [Fact]
    public async Task Cleanup_ExpiredTombstoneWithAuditAndEvidence_ArchivesCompleteReferences()
    {
        var (svc, _, store) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-archive",
            RawTerminal = "completed",
            EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now,
            SubmissionIdentity = sub,
            SendSeq = seq,
        });
        var settled = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted,
            "owner:reconcile_query", notAccepted: new NotAcceptedObservation(
                ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected", "owner:reconcile_query", _now, sub, seq));
        Assert.Equal(AdmissionResultKind.TerminalRejected, settled.Kind);

        var oldAt = _now.AddHours(-25);
        var lease = store.Read().File!.Lease!;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var handoff = file.Handoff!;
            var audited = handoff.Operations.Single(op => op.RequestIdentity == rid);
            audited.Zone = OperationZone.Tombstone;
            audited.UpdatedAtUtc = oldAt;
            audited.UpdatedRevision = file.Revision + 1;
            for (var i = 0; i < ArbitrationAdmissionService.TombstoneLimit - 1; i++)
                handoff.Operations.Add(new OperationRecord
                {
                    RequestIdentity = "audit-capacity-" + i,
                    CandidateId = "candidate-audit-capacity-" + i,
                    RequestState = OperationRequestState.TerminalRejected,
                    Zone = OperationZone.Tombstone,
                    UpdatedAtUtc = oldAt,
                    UpdatedRevision = file.Revision + 1,
                    OperationType = OperationType.FlowRegistration,
                });
            return null;
        });
        Assert.True(seeded.Success, seeded.Reason);

        _now += TimeSpan.FromHours(25);
        RenewLease(store);
        var next = await svc.SubmitAsync(Req(ns: "after-audit-archive", workflow: "group:after-audit-archive"));

        Assert.Equal(AdmissionResultKind.TerminalRejected, next.Kind);
        Assert.NotEqual("operations_capacity_full", next.ReasonCode);
        var read = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
        var handoffAfter = read.File!.Handoff!;
        var archived = Assert.Single(handoffAfter.ArchivedOperations, item => item.Operation.RequestIdentity == rid);
        Assert.Equal(OperationZone.Tombstone, archived.Operation.Zone);
        Assert.Equal("ev-archive", Assert.Single(archived.Operation.ConflictEvidence!).EvidenceId);
        Assert.Single(handoffAfter.ConflictResolutionAudits);
        Assert.Single(handoffAfter.ReconciledNotAcceptedEvidence);
        Assert.DoesNotContain(handoffAfter.Operations, op => op.RequestIdentity == rid);
    }

    [Fact]
    public async Task ConflictAdjudicate_Idempotent_ReusesAuditAndEvidenceIds()
    {
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-1", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        _ = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:reconcile_query",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected",
                "owner:reconcile_query", _now, sub, seq));

        var again = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:reconcile_query",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected",
                "owner:reconcile_query", _now, sub, seq));
        Assert.Equal(ResponsibilityState.Settled, again.ResponsibilityState);
        var lease = ReadLease();
        Assert.Single(lease.File!.Handoff!.ConflictResolutionAudits!);         // 幂等：复用同一 auditId，不产第二份
        Assert.Single(lease.File.Handoff.ReconciledNotAcceptedEvidence!);      // 证据同理
    }

    [Fact]
    public async Task ConflictAdjudicate_NotAccepted_RetryableRoundKeepsRetryAndHistoricalAuditValid()
    {
        var (svc, _, store) = BuildRejectedExternalFacade(retryable: true);
        var request = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-retry-after-adjudication",
            operationType: OperationType.ExternalStart);
        var rejected = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejected.Kind);
        var first = FindOp(rejected.RequestIdentity)!;
        var sub1 = first.SubmissionIdentity!;
        var seq1 = first.LastSendSeq;
        _ = await svc.RegisterConflictEvidenceAsync(first.RequestIdentity, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-retry-adjudication", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub1, SendSeq = seq1,
        });
        var adjudicated = await svc.AdjudicateConflictAsync(first.RequestIdentity,
            ConflictResolutionKind.ResolvedNotAccepted, "owner:reconcile_query",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted,
                "rejected", "owner:reconcile_query", _now, sub1, seq1));
        Assert.Equal(AdmissionResultKind.RetryableRejected, adjudicated.Kind);
        Assert.Equal(ResponsibilityState.Settled, adjudicated.ResponsibilityState);
        Assert.Equal(OperationRequestState.RetryableRejected, FindOp(first.RequestIdentity)!.RequestState);

        var retried = await svc.RetryAsync(first.RequestIdentity);
        Assert.Equal(AdmissionResultKind.RetryableRejected, retried.Kind);
        Assert.Equal(2, FindOp(first.RequestIdentity)!.LastSendSeq);
        var read = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
        var lease = Assert.IsType<LogicalOwnerLeaseFile>(read.File);
        var history = Assert.Single(lease.Handoff!.ConflictResolutionAudits!);
        Assert.Equal(sub1, history.SubmissionIdentity);
        Assert.Equal(seq1, history.SendSeq);
        Assert.Contains(history.AuditId, FindOp(first.RequestIdentity)!.ConflictResolutionAuditHistoryIds);
        Assert.Single(FindOp(first.RequestIdentity)!.ConflictEvidence!);
    }

    [Fact]
    public async Task ConflictAdjudicate_AcceptedTerminal_SettlesThenAudits()
    {
        var (svc, ledger, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-1", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        var done = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_query", _now));
        Assert.Equal(ResponsibilityState.Settled, done.ResponsibilityState);
        var op = FindOp(rid)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState); // 冲突修正后完成独立终局
        Assert.False(op.ConflictPending);
        Assert.Equal(ExecutionResultKind.Succeeded, op.ExecutionResult!.Kind);
        var lease = ReadLease();
        var audit = Assert.Single(lease.File!.Handoff!.ConflictResolutionAudits!);
        Assert.Equal(ConflictResolutionKind.ResolvedAcceptedTerminal, audit.Resolution);
        Assert.NotNull(audit.ResolutionEvidenceSnapshot);
        Assert.Equal(op.ExecutionResult.RawTerminal, audit.ResolutionEvidenceSnapshot!.RawTerminal);
        Assert.Equal(LedgerEntryState.Terminal, Assert.Single(ledger.Read().File!.Entries).State);
    }

    [Fact]
    public async Task ConflictAdjudicate_AcceptedTerminal_ClearsTerminalMirrorConflictState()
    {
        var (svc, _, store) = BuildRejectedExternalFacade();
        _lastHooks!.Barriers = GatedBarrier(2);
        var first = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-mirror-conflict",
            trigger: "manual:panel:mirror-conflict", operationType: OperationType.ExternalStart);
        var duplicate = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-mirror-conflict",
            trigger: "manual:panel:mirror-conflict", operationType: OperationType.ExternalStart);
        var submitted = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(duplicate));
        Assert.All(submitted, result => Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind));
        var linked = store.Read().File!.Handoff!.Operations.Where(op => op.MergedInto is not null).ToArray();
        var mirror = Assert.Single(linked);
        var winnerId = mirror.MergedInto!;
        var winner = FindOp(winnerId)!;
        Assert.Equal(winner.SubmissionIdentity, mirror.SubmissionIdentity);
        Assert.Equal(winner.LastSendSeq, mirror.LastSendSeq);

        _ = await svc.RegisterConflictEvidenceAsync(mirror.RequestIdentity, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-terminal-mirror-own", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = winner.SubmissionIdentity!, SendSeq = winner.LastSendSeq,
        });
        var mirrorDone = await svc.AdjudicateConflictAsync(mirror.RequestIdentity,
            ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_query", _now));
        Assert.Equal(ResponsibilityState.Settled, mirrorDone.ResponsibilityState);
        var afterMirrorAudit = ReadLease();
        Assert.True(afterMirrorAudit.File is not null, afterMirrorAudit.Detail);
        var mirrorAuditId = FindOp(mirror.RequestIdentity)!.ConflictResolutionAuditId;
        Assert.NotNull(mirrorAuditId);

        _ = await svc.RegisterConflictEvidenceAsync(winnerId, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-terminal-winner", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = winner.SubmissionIdentity!, SendSeq = winner.LastSendSeq,
        });
        var done = await svc.AdjudicateConflictAsync(winnerId, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_query", _now));

        Assert.True(done.ResponsibilityState == ResponsibilityState.Settled,
            $"{done.Kind}/{done.ReasonCode}: {done.Detail}");
        var resolvedWinner = FindOp(winnerId)!;
        var resolvedMirror = FindOp(mirror.RequestIdentity)!;
        Assert.False(resolvedWinner.ConflictPending);
        Assert.False(resolvedMirror.ConflictPending);
        Assert.Equal(OperationRequestState.TerminalCompleted, resolvedMirror.RequestState);
        Assert.Equal(ExecutionResultKind.Succeeded, resolvedMirror.ExecutionResult!.Kind);
        Assert.Equal(resolvedWinner.ExecutionResult!.RawTerminal, resolvedMirror.ExecutionResult.RawTerminal);
        Assert.Equal(mirrorAuditId, resolvedMirror.ConflictResolutionAuditId);
        Assert.Equal(2, ReadLease().File!.Handoff!.ConflictResolutionAudits!.Count);
    }

    [Fact]
    public async Task RejectedClose_RacingTerminalPublication_PersistsConflictAndKeepsSubmissionOpen()
    {
        var (svc, store, _, hooks) = BuildFacade(h =>
        {
            h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("adapter_unknown", "ext:adapter"));
            h.NotAcceptedObservationVerifier = _ => null;
        });
        var request = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-reject-terminal-race",
            operationType: OperationType.ExternalStart);
        var unknown = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);

        var atClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Barriers = new AdmissionBarriers
        {
            BeforeRejectedSubmissionClose = async () =>
            {
                atClose.TrySetResult();
                await continueClose.Task;
            },
        };

        var rejectionTask = svc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(unknown.SubmissionIdentity!, unknown.SendSeq,
                "bgi_rejected", Retryable: true, "owner:race-probe"));
        await atClose.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 模拟另一个进程在拒绝快照之后、关闭事务之前，原子发布了执行终态载体。
        var lease = store.Read().File!.Lease!;
        var now = _now;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.Single(o => o.RequestIdentity == request.RequestIdentity);
            op.ExecutionResult = new ExecutionResult
            {
                Kind = ExecutionResultKind.Cancelled,
                RawTerminal = "cancelled",
                EvidenceSource = "ext:task.event",
                SubmissionIdentity = unknown.SubmissionIdentity!,
                SendSeq = unknown.SendSeq,
                ObservedAtUtc = now,
            };
            op.PendingTerminal = new PendingTerminal
            {
                Kind = ExecutionResultKind.Cancelled,
                RawTerminal = "cancelled",
                EvidenceSource = "ext:task.event",
                SubmissionIdentity = unknown.SubmissionIdentity!,
                SendSeq = unknown.SendSeq,
                OperationType = OperationType.ExternalStart,
                ObservedAtUtc = now,
                RecordedAtUtc = now,
            };
            return null;
        });
        Assert.True(seeded.Success, "并发终态载体造景失败：" + seeded.Reason);

        continueClose.TrySetResult();
        var rejected = await rejectionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AdmissionResultKind.NeedReconcile, rejected.Kind);
        var current = FindOp(request.RequestIdentity)!;
        Assert.True(current.ConflictPending);
        Assert.NotNull(current.ExecutionResult);
        Assert.NotNull(current.PendingTerminal);
        Assert.NotEqual(OperationRequestState.RetryableRejected, current.RequestState);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission); // 拒绝不得关闭仍需裁决的责任
        Assert.Contains(current.ConflictEvidence!, e => e.SupersededReasonCode == "authoritative_execution_fact_exists");
    }

    [Fact]
    public async Task CompletionStage_RacingClosedRetryableRejection_CannotPublishExecutionFact()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true);
        var hooks = _lastHooks!;
        var concurrentSvc = new ArbitrationAdmissionService(_lastStore!, hooks, () => _now);
        hooks.NotAcceptedObservationVerifier = _ => null;
        var request = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-completion-before-stage",
            operationType: OperationType.ExternalStart);
        var unknown = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);
        var atStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Barriers = new AdmissionBarriers
        {
            BeforeTerminalCarrierStage = async () =>
            {
                atStage.TrySetResult();
                await continueStage.Task;
            },
        };
        var completionTask = svc.SettleCompletionAsync(request.RequestIdentity, unknown.SubmissionIdentity!, unknown.SendSeq,
            ExternalStartCompletion.CancelledWith("cancelled", "ext:task.event", _now));
        await atStage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejection = await concurrentSvc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(unknown.SubmissionIdentity!, unknown.SendSeq,
                "bgi_rejected", Retryable: true, "owner:race-probe"));
        Assert.Equal(AdmissionResultKind.NeedReconcile, rejection.Kind);
        continueStage.TrySetResult();
        var completion = await completionTask.WaitAsync(TimeSpan.FromSeconds(5));

        var current = FindOp(request.RequestIdentity)!;
        Assert.NotEqual(OperationRequestState.RetryableRejected, current.RequestState);
        Assert.True(current.ConflictPending);
        Assert.NotNull(current.AcceptanceClaim);
        Assert.True(current.AcceptanceClaim!.LedgerPersisted);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);
        Assert.NotEqual(ResponsibilityState.Settled, completion.ResponsibilityState);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution, Assert.Single(ledger.Read().File!.Entries).State);
        var retry = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retry.Kind);
        Assert.Equal(unknown.SendSeq, FindOp(request.RequestIdentity)!.LastSendSeq);
    }

    [Fact]
    public async Task AcceptanceClaimRecovery_ReplaysLedgerAndClosesMatchingSubmission_WithoutResend()
    {
        ExternalStartLedger? ledgerRef = null;
        var persistCalls = 0;
        var sends = 0;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("owner:accepted", "run-recovery", "job-recovery"));
            };
            h.TakeoverPersist = entry =>
            {
                if (Interlocked.Increment(ref persistCalls) == 1)
                    return Task.FromResult<string?>("injected_before_ledger_write");
                var recorded = ledgerRef!.RecordAccepted(entry);
                return Task.FromResult<string?>(recorded.Success
                    && ledgerRef.ConfirmRebuildable(entry)
                    ? null
                    : "recovery_ledger_write_failed");
            };
        });
        ledgerRef = ledger;
        var request = Req(operationType: OperationType.ExternalStart);

        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Reconciling, first.Kind);
        Assert.Equal(1, sends);
        var beforeRecovery = FindOp(request.RequestIdentity)!;
        Assert.NotNull(beforeRecovery.AcceptanceClaim);
        Assert.False(beforeRecovery.AcceptanceClaim!.LedgerPersisted);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);

        var ownerBeforeTakeover = ReadLease().File!.Lease!;
        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(ownerBeforeTakeover.TtlSeconds + 1);
        var takeoverEvidence = observer.Observe(store.Read());
        Assert.NotNull(takeoverEvidence);
        var acquired = store.TryAcquire("pid:acceptance-recovery", evidence: takeoverEvidence);
        Assert.True(acquired.Success, "模拟重启接管失败 " + acquired.Reason);
        var recoveredSvc = new ArbitrationAdmissionService(store, hooks, () => _now);

        var report = await recoveredSvc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, report.AcceptanceClaimsReconciled);
        Assert.Equal(0, report.AcceptanceClaimFailures);
        Assert.Equal(1, sends);
        var recovered = FindOp(request.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, recovered.RequestState);
        Assert.True(recovered.AcceptanceClaim!.LedgerPersisted);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
        var entry = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(beforeRecovery.SubmissionIdentity, entry.SubmissionIdentity);
        Assert.Equal(beforeRecovery.LastSendSeq, entry.SendSeq);
        Assert.Equal("job-recovery", entry.JobId);
    }

    [Fact]
    public async Task AcceptanceClaimRecovery_PersistsClaimUsingItsOriginalRoundAfterOperationAdvanced()
    {
        var sends = 0;
        var (svc, store, ledger, _) = BuildFacade(h => h.Sender = _ =>
        {
            var send = Interlocked.Increment(ref sends);
            return Task.FromResult<SendOutcome>(send == 1
                ? new SendOutcome.Rejected("first_round_rejected", Retryable: true, "sender")
                : new SendOutcome.Unknown("second_round_unknown", "sender"));
        });
        var request = Req(operationType: OperationType.ExternalStart);
        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        var originalSubmissionIdentity = first.SubmissionIdentity!;
        var originalSendSeq = first.SendSeq;
        var second = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Reconciling, second.Kind);
        Assert.True(second.SendSeq > originalSendSeq);

        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.Single(o => o.RequestIdentity == request.RequestIdentity);
            op.ConflictPending = true;
            op.AcceptanceClaim = new AcceptanceClaimRecord
            {
                RequestIdentity = op.RequestIdentity,
                SubmissionIdentity = originalSubmissionIdentity,
                SendSeq = originalSendSeq,
                OwnerLeaseId = lease.LeaseId,
                OwnerEpoch = lease.OwnerEpoch,
                ClaimedAtUtc = _now,
                EvidenceSource = "owner:late-round-one",
                RunId = "run-round-one",
                JobId = "job-round-one",
                LedgerPersisted = false,
            };
            return null;
        }).Success);

        var recovery = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, recovery.AcceptanceClaimsReconciled);
        Assert.Equal(0, recovery.AcceptanceClaimFailures);
        Assert.Equal(2, sends);
        var entry = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(originalSubmissionIdentity, entry.SubmissionIdentity);
        Assert.Equal(originalSendSeq, entry.SendSeq);
        Assert.Equal("job-round-one", entry.JobId);
        Assert.Equal(second.SubmissionIdentity, store.Read().File!.Handoff!.Submission!.SubmissionIdentity);
    }

    [Fact]
    public async Task CompletionFinalize_RacingConflictRegistration_CannotCloseSubmissionOrSettle()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true);
        var hooks = _lastHooks!;
        var concurrentSvc = new ArbitrationAdmissionService(_lastStore!, hooks, () => _now);
        hooks.NotAcceptedObservationVerifier = _ => null;
        var request = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-completion-before-finalize",
            operationType: OperationType.ExternalStart);
        var unknown = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);
        var atFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Barriers = new AdmissionBarriers
        {
            BeforeTerminalFinalize = async () =>
            {
                atFinalize.TrySetResult();
                await continueFinalize.Task;
            },
        };
        var completionTask = svc.SettleCompletionAsync(request.RequestIdentity, unknown.SubmissionIdentity!, unknown.SendSeq,
            ExternalStartCompletion.CancelledWith("cancelled", "ext:task.event", _now));
        await atFinalize.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejection = await concurrentSvc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(unknown.SubmissionIdentity!, unknown.SendSeq,
                "bgi_rejected", Retryable: true, "owner:race-probe"));
        Assert.Equal(AdmissionResultKind.NeedReconcile, rejection.Kind);
        continueFinalize.TrySetResult();
        var completion = await completionTask.WaitAsync(TimeSpan.FromSeconds(5));

        var current = FindOp(request.RequestIdentity)!;
        Assert.True(current.ConflictPending);
        Assert.NotNull(current.ExecutionResult);
        Assert.NotNull(current.PendingTerminal);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);
        Assert.NotEqual(OperationRequestState.TerminalCompleted, current.RequestState);
        Assert.Equal(LedgerEntryState.Terminal, Assert.Single(ledger.Read().File!.Entries).State);
        Assert.NotEqual(ResponsibilityState.Settled, completion.ResponsibilityState);
    }

    [Fact]
    public async Task CompletionFinalize_ReadbackAfterAnotherSettlementAndNewConflict_RemainsPending()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion();
        var hooks = _lastHooks!;
        var store = _lastStore!;
        var concurrentSvc = new ArbitrationAdmissionService(store, hooks, () => _now);
        var (requestIdentity, submissionIdentity, sendSeq) = await AcceptedExternalOpAsync(svc);
        var atFirstFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFinalize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalizeCalls = 0;
        hooks.Barriers = new AdmissionBarriers
        {
            BeforeTerminalFinalize = async () =>
            {
                if (Interlocked.Increment(ref finalizeCalls) != 1) return;
                atFirstFinalize.TrySetResult();
                await releaseFirstFinalize.Task;
            },
        };
        var completion = ExternalStartCompletion.CancelledWith("cancelled", "owner:completion-A", _now);
        var first = svc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq, completion);
        await atFirstFinalize.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var settled = await concurrentSvc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq, completion);
        Assert.Equal(ResponsibilityState.Settled, settled.ResponsibilityState);
        var contradictory = await concurrentSvc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq,
            ExternalStartCompletion.CancelledWith("cancelled-different", "owner:completion-B", _now));
        Assert.NotEqual(ResponsibilityState.Settled, contradictory.ResponsibilityState);
        Assert.True(FindOp(requestIdentity)!.ConflictPending);

        releaseFirstFinalize.TrySetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));

        var final = FindOp(requestIdentity)!;
        Assert.True(final.ConflictPending);
        Assert.NotEqual(ResponsibilityState.Settled, firstResult.ResponsibilityState);
    }

    [Fact]
    public async Task ConflictPending_TombstoneProtectedFromRetentionTrim_AndRecoverySkipsTerminalize()
    {
        // §24.2-2″／§24.12-3 第四类恢复集合：带冲突待决的记录**不得**被保留期裁剪，也不得被恢复扫描补终局。
        var (svc, _, store) = BuildRejectedExternalFacade(); // 复用同一 store 实例（所有权单调基线）
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-1", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        // 手工置为「已过期墓碑」（超过 24h 保留期）——受保护墓碑不得被 MigrateAndClean 裁剪。
        var lease = store.Read().File!.Lease!;
        var mutate = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, rid, StringComparison.Ordinal));
            op.Zone = OperationZone.Tombstone;
            op.UpdatedAtUtc = _now - TimeSpan.FromHours(30);
            return null;
        });
        Assert.True(mutate.Success, "夹具前置：置墓碑失败 " + mutate.Reason);

        _ = svc.RecoverAfterRestart();

        var after = ReadLease();
        var op2 = after.File!.Handoff!.Operations.FirstOrDefault(o => string.Equals(o.RequestIdentity, rid, StringComparison.Ordinal));
        Assert.NotNull(op2);                   // 唯一恢复依据保留（不得裁剪）
        Assert.True(op2!.ConflictPending);
        Assert.NotEqual(OperationRequestState.TerminalCompleted, op2.RequestState); // 冲突待决不得补终局
    }

    [Fact]
    public async Task ConflictPending_BlocksRetryAndWindowExpiry_WithoutStateChange()
    {
        // 可重试拒绝 + 冲突登记 ⇒ 重试资格失效、窗口到期不得转终局、状态不得被改写（禁止重发）。
        var (svc, _, _) = BuildRejectedExternalFacade(retryable: true);
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-conflict-retry");
        req.OperationType = OperationType.ExternalStart;
        var rejected = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejected.Kind);
        var (rid, sub, seq) = (rejected.RequestIdentity, rejected.SubmissionIdentity!, rejected.SendSeq);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-retry", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        var retry = await svc.RetryAsync(rid);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retry.Kind);
        Assert.Equal("conflict_pending", retry.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, retry.ResponsibilityState);

        _now += TimeSpan.FromMinutes(2);                  // 越过 30s 重试窗口
        _ = svc.SweepExpiredRetryWindows();
        var op = FindOp(rid)!;
        Assert.Equal(OperationRequestState.RetryableRejected, op.RequestState); // 未被窗口到期转终局
        Assert.True(op.ConflictPending);
    }

    [Fact]
    public async Task ConflictPending_BlocksNewStartAsFactsUnknown()
    {
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-block", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        var other = Req(ns: "manual", workflow: "onedragon:other", payload: "p-other");
        other.OperationType = OperationType.ExternalStart;
        var blocked = await svc.SubmitAsync(other);
        Assert.Equal(AdmissionResultKind.NeedReconcile, blocked.Kind);          // 冲突未裁决 ⇒ 准入事实未知
        Assert.Equal("facts_unknown", blocked.ReasonCode);
    }

    [Fact]
    public async Task ConflictPending_TerminalPendingTransfer_StaysInPrimarySlot()
    {
        var (svc, _, store) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-tpt", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        var lease = store.Read().File!.Lease!;
        var moved = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, rid, StringComparison.Ordinal));
            op.Zone = OperationZone.TerminalPendingTransfer;   // 冲突到达时已在待迁移区
            return null;
        });
        Assert.True(moved.Success, "夹具前置：置待迁移失败 " + moved.Reason);

        _ = svc.RecoverAfterRestart();                         // 触发迁移清理

        var op2 = FindOp(rid)!;
        Assert.Equal(OperationZone.TerminalPendingTransfer, op2.Zone); // 继续占主槽位（不得迁墓碑）
        Assert.True(op2.ConflictPending);
    }

    [Fact]
    public async Task ConflictAdjudicate_AcceptedTerminal_MapsCancelledResultDimension()
    {
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-cancel", RawTerminal = "cancelled", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });

        var done = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.CancelledWith("cancelled", "owner:reconcile_query", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, done.Kind);                 // 结果维以 ExecutionResult 为权威
        Assert.Equal(ExecutionDisposition.Cancelled, done.ExecutionDisposition);
        Assert.Equal(ResponsibilityState.Settled, done.ResponsibilityState);
        Assert.Equal(ExecutionResultKind.Cancelled, FindOp(rid)!.ExecutionResult!.Kind);
    }

    [Fact]
    public async Task ConflictAdjudicate_FromTombstone_StaysTombstoneWithoutRegainingPrimarySlot()
    {
        // §24.2-2″：墓碑冲突「不虚称重新占有已释放的主槽位」——裁决后仍留在墓碑区。
        var (svc, _, store) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-tomb", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        var lease = store.Read().File!.Lease!;
        var moved = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, rid, StringComparison.Ordinal));
            op.Zone = OperationZone.Tombstone;   // 冲突到达时已在墓碑
            return null;
        });
        Assert.True(moved.Success, "夹具前置：置墓碑失败 " + moved.Reason);

        var done = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_query", _now));
        Assert.Equal(ResponsibilityState.Settled, done.ResponsibilityState);
        var op2 = FindOp(rid)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op2.RequestState);
        Assert.Equal(OperationZone.Tombstone, op2.Zone);   // 不得迁回主槽位区
    }

    [Fact]
    public async Task ConflictAdjudicate_OppositeDirectionClaimInFlight_IsRefused()
    {
        // 并发方向锁定反例：已有「受理终态」方向声明在处理中时，相反方向裁决必须被拒绝（不得写入矛盾审计）。
        var (svc, _, store) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-claim", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        var lease = store.Read().File!.Lease!;
        var claimed = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, rid, StringComparison.Ordinal));
            op.ConflictAdjudicationClaim = nameof(ConflictResolutionKind.ResolvedAcceptedTerminal);
            return null;
        });
        Assert.True(claimed.Success, "夹具前置：置方向声明失败 " + claimed.Reason);

        var refused = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:rc",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected",
                "owner:rc", _now, sub, seq));
        Assert.Equal("conflict_adjudication_in_progress", refused.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, refused.ResponsibilityState);
        Assert.Empty(ReadLease().File!.Handoff!.ConflictResolutionAudits!);   // 未写入矛盾审计
    }

    [Fact]
    public async Task ConflictAdjudicate_SameIdDifferentPayload_FailsClosed()
    {
        // 全载荷幂等反例：同 evidenceId/auditId 但观察载荷不同 ⇒ 必须判冲突（不得静默当作幂等成功）。
        var (svc, _, _) = BuildRejectedExternalFacade();
        var (rid, sub, seq) = await RejectedExternalOpAsync(svc);
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-payload", RawTerminal = "completed", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        _ = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:rc",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "rejected",
                "owner:rc", _now, sub, seq));

        // 同 ID、同来源，但原始证据词/观察时点不同 ⇒ 异载荷。
        var conflict = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedNotAccepted, "owner:rc",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted, "other_word",
                "owner:rc", _now + TimeSpan.FromMinutes(1), sub, seq));
        Assert.Equal(AdmissionResultKind.Error, conflict.Kind);
        Assert.Contains("conflict", conflict.ReasonCode, StringComparison.Ordinal);
        Assert.Single(ReadLease().File!.Handoff!.ConflictResolutionAudits!);   // 不追加第二份
    }

    [Fact]
    public async Task ConflictAdjudicate_CancelledFact_SurvivesContinueUseAndRestart()
    {
        // §24.6-4／§24.13-2：裁决后的**取消/失败事实**必须在续用与重启后保持（不得被改写成成功）。
        var (svc, _, _) = BuildRejectedExternalFacade();
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-conflict-continue");
        req.OperationType = OperationType.ExternalStart;
        var rejected = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        var rid = rejected.RequestIdentity;
        var sub = rejected.SubmissionIdentity!;
        var seq = rejected.SendSeq;
        _ = await svc.RegisterConflictEvidenceAsync(rid, new ConflictEvidenceRecord
        {
            EvidenceId = "ev-continue", RawTerminal = "cancelled", EvidenceSource = "owner:late_evidence",
            ObservedAtUtc = _now, SubmissionIdentity = sub, SendSeq = seq,
        });
        var done = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:reconcile_query", ExternalStartCompletion.CancelledWith("cancelled", "owner:reconcile_query", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, done.Kind);

        // 续用（同一请求对象 + ContinueUse + 同一身份）⇒ 必须仍返回取消事实。
        req.Kind = AdmissionKind.ContinueUse;
        req.RequestIdentity = rid;
        var continued = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Cancelled, continued.Kind);
        Assert.Equal(ResponsibilityState.Settled, continued.ResponsibilityState);
        Assert.Equal(ExecutionDisposition.Cancelled, continued.ExecutionDisposition);

        // 重启恢复后再续用 ⇒ 结果事实不变。
        _ = svc.RecoverAfterRestart();
        var afterRestart = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Cancelled, afterRestart.Kind);
        Assert.Equal("cancelled", afterRestart.RawTerminal);
    }

    [Fact]
    public async Task Create_UnknownOperationType_FailsClosedWithoutRegistration()
    {
        // §24.17-3：创建必须携带可信操作类型；缺失/Unknown ⇒ 响亮拒绝且**零登记、零副作用**。
        var (svc, _, _, _) = BuildFacade();
        var req = Req();
        req.OperationType = OperationType.Unknown;

        var result = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("operation_type_required", result.ReasonCode);
        Assert.Empty(ReadLease().File?.Handoff?.Operations ?? []);
    }

    [Fact]
    public async Task PersistedUnknownOperationType_RefusesResend()
    {
        // §24.17-3／§24.20-A：**旧格式代隔离产物（持久化类型 Unknown）**不得重新占位/发送——
        // 续用/重试路径一律以持久化类型为准（调用方即便携带类型也不得覆盖）。
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-legacy");
        req.OperationType = OperationType.ExternalStart;
        var accepted = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var lease = store.Read().File!.Lease!;
        var marked = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, accepted.RequestIdentity, StringComparison.Ordinal));
            op.OperationType = OperationType.Unknown;   // 模拟旧格式代隔离产物
            op.AcceptanceClaim = null;                  // 历史未知类型记录不承载 v4 ExternalStart 认领
            op.RequestState = OperationRequestState.Queued;
            // 与预观察记录的类型关联保持一致（否则读侧会按 v3 引用完整性判损坏）。
            foreach (var pre in file.Handoff.PreObservations ?? [])
                if (string.Equals(pre.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal)
                    && pre.SendSeq == op.LastSendSeq)
                    pre.OperationType = OperationType.Unknown;
            return null;
        });
        Assert.True(marked.Success, "夹具前置：置 Unknown 类型失败 " + marked.Reason);

        req.Kind = AdmissionKind.ContinueUse;
        req.RequestIdentity = accepted.RequestIdentity;
        var blocked = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Error, blocked.Kind);
        Assert.Equal("legacy_operation_type_unresolved", blocked.ReasonCode);
        Assert.Equal(1, sends);                                       // 未发生第二次发送
        Assert.Null(ReadLease().File!.Handoff!.Submission);           // 未产生未决发送（未占位）
        // 重试路径同样前置拒绝（不改写状态、不重发），且责任维保持 Pending（§24.6-5）。
        var retry = await svc.RetryAsync(accepted.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Error, retry.Kind);
        Assert.Equal("legacy_operation_type_unresolved", retry.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, retry.ResponsibilityState);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task OccupyStage_LegacyUnknownType_RefusedWithoutSend()
    {
        // 反例：**入队后、占位前**把持久化类型改成 Unknown（模拟旧格式代隔离产物窗口）⇒ 占位阶段响亮拒绝、零发送。
        ArbitrationLeaseStore? storeRef = null;
        var mutated = false;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    if (!mutated && storeRef is not null)
                    {
                        mutated = true;
                        var lease = storeRef.Read().File!.Lease!;
                        storeRef.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                        {
                            var op = file.Handoff!.Operations.First();
                            op.OperationType = OperationType.Unknown;
                            op.AcceptanceClaim = null;
                            return null;
                        });
                    }
                    return Task.CompletedTask;
                },
            };
        });
        storeRef = store;

        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-occupy-legacy");
        req.OperationType = OperationType.ExternalStart;
        var result = await svc.SubmitAsync(req);

        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("legacy_operation_type_unresolved", result.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        Assert.Equal(0, sends);                                     // 未签发发送许可
        Assert.Null(ReadLease().File!.Handoff!.Submission);          // 未占位
    }

    [Fact]
    public async Task ContinueUse_MergedUnknownRecordWithConflictedTarget_ReportsConflictFirst()
    {
        // 顺序纪律反例：合并记录自身类型为 Unknown，但**合并目标冲突待决** ⇒ 必须先报告 `conflict_pending`
        // （冲突待裁决优先于类型隔离；两者都 fail-closed）。
        var (svc, store, _, _) = BuildFacade();
        var a = Req(ns: "manual", workflow: "onedragon:winner", payload: "p-winner");
        a.OperationType = OperationType.ExternalStart;
        var wa = await svc.SubmitAsync(a);
        Assert.Equal(AdmissionResultKind.Accepted, wa.Kind);
        var m = Req(ns: "manual", workflow: "onedragon:merged", payload: "p-merged");
        m.OperationType = OperationType.ExternalStart;
        var wm = await svc.SubmitAsync(m);
        Assert.Equal(AdmissionResultKind.Accepted, wm.Kind);

        var lease = store.Read().File!.Lease!;
        var mutated = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var winner = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, wa.RequestIdentity, StringComparison.Ordinal));
            var merged = file.Handoff.Operations.First(o => string.Equals(o.RequestIdentity, wm.RequestIdentity, StringComparison.Ordinal));
            winner.ConflictPending = true;                                   // 胜者冲突待决
            winner.AcceptanceClaim = null;                                    // 夹具模拟无认领的历史冲突快照
            merged.MergedInto = winner.RequestIdentity;                      // 合并项指向胜者
            merged.OperationType = OperationType.Unknown;                    // 合并项类型缺失（旧格式代隔离产物）
            merged.AcceptanceClaim = null;                                    // v4 认领只允许绑定 ExternalStart 类型
            merged.RequestState = OperationRequestState.RetryableRejected;   // 触发合并共享分类分支
            foreach (var pre in file.Handoff.PreObservations ?? [])
                if (string.Equals(pre.SubmissionIdentity, merged.SubmissionIdentity, StringComparison.Ordinal)
                    && pre.SendSeq == merged.LastSendSeq)
                    pre.OperationType = OperationType.Unknown;
            return null;
        });
        Assert.True(mutated.Success, "夹具前置：构造合并+冲突失败 " + mutated.Reason);

        m.Kind = AdmissionKind.ContinueUse;
        m.RequestIdentity = wm.RequestIdentity;
        var result = await svc.SubmitAsync(m);
        Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
        Assert.Equal("conflict_pending", result.ReasonCode);   // 冲突优先（不得先报类型隔离）
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
    }

    [Fact]
    public async Task ContinueUseRacingRetry_OnlyOnePathReservesTheIdentity()
    {
        var continuePassedCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseContinue = false;
        var pauseRetry = false;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable")
                    : new SendOutcome.Accepted("fixture:accepted", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterContinueInFlightCheck = () =>
                {
                    if (!pauseContinue) return Task.CompletedTask;
                    continuePassedCheck.TrySetResult();
                    return releaseContinue.Task;
                },
                AfterRetryReservation = () =>
                {
                    if (!pauseRetry) return Task.CompletedTask;
                    retryReserved.TrySetResult();
                    return releaseRetry.Task;
                },
            };
        });

        var request = Req(ns: "v2", workflow: "wf-continue-retry-reservation", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(request)).Kind);
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == request.RequestIdentity).RequestState = OperationRequestState.Queued;
            return null;
        }).Success);

        pauseContinue = true;
        var continueTask = svc.SubmitAsync(ContinueOf(request));
        await continuePassedCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pauseRetry = true;
        var retryTask = Task.Run(() => svc.RetryAsync(request.RequestIdentity));
        await retryReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseContinue.TrySetResult();
        var continued = await continueTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.NeedReconcile, continued.Kind);
        Assert.Equal(ResponsibilityState.Pending, continued.ResponsibilityState);

        releaseRetry.TrySetResult();
        var retried = await retryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.Accepted, retried.Kind);
        Assert.Equal(2, Volatile.Read(ref sends));
        Assert.Equal(2, FindOp(request.RequestIdentity)!.LastSendSeq);
    }

    [Fact]
    public async Task SettleCompletion_JobIdReadHookMissing_FailsClosedAsUnreadable()
    {
        // 配置缺失反例：终态写入/确认钩子在位，但**句柄读取器未配置** ⇒ 归为「不可确认」，一律保守停驻。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(noJobIdReadHook: true);
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var held = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal("terminal_ledger_unreadable", held.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, held.ResponsibilityState);
        var op = FindOp(rid)!;
        Assert.NotEqual(OperationRequestState.TerminalCompleted, op.RequestState);   // 未终局
        // 读取器缺失在**写终态载体之前**即停驻 ⇒ 两载体均未写（责任仍由 Operation=Accepted/Active 承载）。
        Assert.Null(op.PendingTerminal);
        Assert.Null(op.ExecutionResult);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
            Assert.Single(ledger.Read().File!.Entries).State);                       // 未写终态副本
    }

    // ── 34. B5：缺字段不得默认为合法（台账 {} / {version:1} / 记录缺关联字段；租约 handoff:{}）──

    [Fact]
    public void StrictRead_MissingFields_NeverDerivedValid()
    {
        Directory.CreateDirectory(_dir);
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var ledgerPath = Path.Combine(_dir, "external-start-ledger.json");
        File.WriteAllText(ledgerPath, "{}"); // version/entries 缺失=非法（旧：属性初始化默认为合法空台账）
        Assert.True(ledger.GetOccupancy().Unknown);
        File.WriteAllText(ledgerPath, "{\"version\":1}"); // entries 缺失=非法
        Assert.True(ledger.GetOccupancy().Unknown);
        File.WriteAllText(ledgerPath, "{\"version\":1,\"entries\":[{\"submissionIdentity\":\"sub:a:1\",\"sendSeq\":1,\"state\":0}]}"); // 记录缺关联字段=非法
        Assert.True(ledger.GetOccupancy().Unknown);
        File.WriteAllText(ledgerPath, "{\"version\":1,\"revision\":0,\"entries\":[]}"); // 合法空台账不受影响
        var occ = ledger.GetOccupancy();
        Assert.False(occ.Unknown);
        Assert.Empty(occ.Entries);

        var store = NewStore();
        Assert.True(store.TryAcquire("pid:test").Success);
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        File.WriteAllText(leasePath, File.ReadAllText(leasePath).Replace("\"handoff\": null", "\"handoff\": {}")); // handoff:{} → operations 缺失=Corrupt
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, NewStore().Read().Status);
    }

    // ── 35. B5：Submission 无关联 Operations=交叉不一致（不得推导空闲/可发送）──

    [Fact]
    public void Lease_OrphanSubmission_Corrupt()
    {
        var store = NewStore();
        Assert.True(store.TryAcquire("pid:test").Success);
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        var handoff = "\"handoff\": { \"pending\": null, \"submission\": { \"submissionIdentity\": \"sub:orphan:1\", \"sendSeq\": 1, \"actionId\": \"a\", \"targetEpoch\": \"ep1\", \"candidateId\": \"c\", \"state\": 0, \"recordedAtUtc\": \"2026-09-20T12:00:00+00:00\" }, \"operations\": [] }";
        File.WriteAllText(leasePath, File.ReadAllText(leasePath).Replace("\"handoff\": null", handoff));
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, NewStore().Read().Status);
    }

    // ── 36. B6：合并关联发送前落盘 + 「镜像前崩溃」恢复按 MergedInto 共同结清 ──

    [Fact]
    public async Task MergedInto_PersistedBeforeSend_AndRecoveryMirrors()
    {
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            var gated = GatedBarrier(2);
            gated.AfterOccupyBeforeSend = () =>
            {
                var handoff = storeRef!.Read().File!.Handoff!;
                Assert.NotNull(handoff.Submission); // 占位已落盘
                Assert.Contains(handoff.Operations, o => o.MergedInto != null); // 合并关联发送前已落盘（B6）
                return Task.CompletedTask;
            };
            h.Barriers = gated;
        });
        storeRef = store;
        var r1 = Req(trigger: "manual:panel:dup-b6");
        var r2 = Req(trigger: "manual:panel:dup-b6");
        var t1 = svc.SubmitAsync(r1);
        var t2 = svc.SubmitAsync(r2);
        var results = await Task.WhenAll(t1, t2);
        Assert.All(results, x => Assert.Equal(AdmissionResultKind.Accepted, x.Kind));
        var merged = ReadLease().File!.Handoff!.Operations.First(o => o.MergedInto is not null);
        Assert.Equal(OperationRequestState.Accepted, merged.RequestState); // 正常路径=共同结清已镜像

        // 模拟「胜者关闭后、镜像前崩溃」：回退合并项到 InRound（保留 mergedInto 关联）
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        var doc = JsonNode.Parse(File.ReadAllText(leasePath))!;
        foreach (var o in doc["handoff"]!["operations"]!.AsArray())
        {
            if (o!["requestIdentity"]!.GetValue<string>() == merged.RequestIdentity)
            {
                o["requestState"] = 1; // InRound
                o["lastResult"] = null;
                o["submissionIdentity"] = "";
                o["lastSendSeq"] = 0;
                o["takeoverRef"] = null;
            }
        }
        File.WriteAllText(leasePath, doc.ToJsonString());

        // 重启接管+恢复：按 MergedInto 关联镜像胜者终态（不判孤儿、不再发送）
        var (svc2, _, _, _) = BuildFacade(takeover: true);
        var recovered = svc2.RecoverAfterRestart();
        Assert.True(recovered >= 1, "恢复应镜像合并孤儿");
        var after = ReadLease().File!.Handoff!.Operations.First(o => o.RequestIdentity == merged.RequestIdentity);
        Assert.Equal(OperationRequestState.Accepted, after.RequestState);
        Assert.StartsWith("merged:", after.LastResult!.EvidenceSource);
    }

    [Fact]
    public async Task RetryAsync_MergedMirrorWithRetryableWinner_DoesNotCreateIndependentSend()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable")
                    : new SendOutcome.Accepted("fixture:accepted", null));
            };
        });
        var winnerRequest = Req(trigger: "manual:retry-merged");
        var mirrorRequest = Req(trigger: "manual:retry-merged");
        var submissions = await Task.WhenAll(svc.SubmitAsync(winnerRequest), svc.SubmitAsync(mirrorRequest));
        Assert.All(submissions, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        Assert.Equal(1, sends);

        var mirror = ReadLease().File!.Handoff!.Operations.Single(o => o.MergedInto is not null);
        var winner = ReadLease().File!.Handoff!.Operations.Single(o => o.RequestIdentity == mirror.MergedInto);
        var retry = await svc.RetryAsync(mirror.RequestIdentity);
        Assert.Equal(AdmissionResultKind.RetryableRejected, retry.Kind);
        Assert.Equal(ResponsibilityState.Settled, retry.ResponsibilityState);
        Assert.Equal(winner.SubmissionIdentity, retry.SubmissionIdentity);
        Assert.Equal(winner.LastSendSeq, retry.SendSeq);
        Assert.Equal(1, sends);

        var winnerRetry = await svc.RetryAsync(winner.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, winnerRetry.Kind);
        Assert.Equal(2, sends);
        var mirrorContinue = await svc.SubmitAsync(ContinueOf(mirrorRequest));
        var mirrorRetry = await svc.RetryAsync(mirror.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, mirrorContinue.Kind);
        Assert.Equal(AdmissionResultKind.Accepted, mirrorRetry.Kind);
        Assert.Equal(winnerRetry.SubmissionIdentity, mirrorContinue.SubmissionIdentity);
        Assert.Equal(winnerRetry.SubmissionIdentity, mirrorRetry.SubmissionIdentity);
        Assert.Equal(winnerRetry.SendSeq, mirrorContinue.SendSeq);
        Assert.Equal(winnerRetry.SendSeq, mirrorRetry.SendSeq);

        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == mirror.RequestIdentity).ConflictPending = true;
            return null;
        }).Success);
        var conflicted = await svc.RetryAsync(mirror.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, conflicted.Kind);
        Assert.Equal("conflict_pending", conflicted.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, conflicted.ResponsibilityState);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task PrecheckRetryableWinner_MirrorsRelationBeforeWinnerCanRetry()
    {
        var occupied = false;
        var injectOccupiedAtPublish = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:accepted", null));
            };
            var barriers = GatedBarrier(2);
            barriers.BeforeOccupyPublish = () =>
            {
                if (injectOccupiedAtPublish) occupied = true;
                return Task.CompletedTask;
            };
            h.Barriers = barriers;
        });

        var a = Req(ns: "v2", workflow: "wf-precheck-merge", trigger: "manual:precheck-merge", operationType: OperationType.ExternalStart);
        var b = Req(ns: "v2", workflow: "wf-precheck-merge", trigger: "manual:precheck-merge", operationType: OperationType.ExternalStart);
        injectOccupiedAtPublish = true;
        var firstRound = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        Assert.All(firstRound, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        Assert.Equal(0, sends);
        var mirror = ReadLease().File!.Handoff!.Operations.Single(o => o.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;
        Assert.Equal(OperationRequestState.RetryableRejected, mirror.RequestState);
        Assert.Equal(OperationZone.Active, mirror.Zone);
        Assert.Equal(OperationRequestState.RetryableRejected, winner.RequestState);
        Assert.Equal("execution_occupied", mirror.LastPrecheckResult?.ReasonCode);

        occupied = false;
        injectOccupiedAtPublish = false;
        var retried = await svc.RetryAsync(winner.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, retried.Kind);
        Assert.Equal(1, sends);
        var continuedMirror = await svc.SubmitAsync(ContinueOf(a.RequestIdentity == mirror.RequestIdentity ? a : b));
        var retriedMirror = await svc.RetryAsync(mirror.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, continuedMirror.Kind);
        Assert.Equal(AdmissionResultKind.Accepted, retriedMirror.Kind);
        Assert.Equal(retried.SubmissionIdentity, continuedMirror.SubmissionIdentity);
        Assert.Equal(retried.SubmissionIdentity, retriedMirror.SubmissionIdentity);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task RetryAsync_IncompatibleMergeAppearingAfterSnapshot_IsHeldAtAtomicOccupy()
    {
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseReservation = false;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq switch
                {
                    2 => new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable"),
                    3 => new SendOutcome.Unknown("fixture:unknown"),
                    _ => new SendOutcome.Accepted("fixture:accepted", null),
                });
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterRetryReservation = () =>
                {
                    if (!pauseReservation) return Task.CompletedTask;
                    reserved.TrySetResult();
                    return releaseRetry.Task;
                },
            };
        });

        var targetRequest = Req(ns: "v2", workflow: "wf-merge-target", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(targetRequest)).Kind);
        var retryRequest = Req(ns: "v2", workflow: "wf-merge-late", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(retryRequest)).Kind);
        var blocker = Req(ns: "v2", workflow: "wf-merge-global-submission", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Reconciling, (await svc.SubmitAsync(blocker)).Kind);
        Assert.Equal(3, sends);
        Assert.NotNull(store.Read().File!.Handoff!.Submission); // 有未决发送时也必须先镜像分类，不得终局化/释放

        pauseReservation = true;
        var retryTask = Task.Run(() => svc.RetryAsync(retryRequest.RequestIdentity));
        await reserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == retryRequest.RequestIdentity).MergedInto = targetRequest.RequestIdentity;
            return null;
        }).Success);

        releaseRetry.TrySetResult();
        var result = await retryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
        Assert.Equal("merged_target_conflict", result.ReasonCode);
        Assert.Equal(retryRequest.RequestIdentity, result.RequestIdentity);
        Assert.Equal(3, sends); // late MergedInto mutation blocked a fourth independent send permit
        Assert.Equal(FindOp(blocker.RequestIdentity)!.SubmissionIdentity, store.Read().File!.Handoff!.Submission!.SubmissionIdentity);
        var mirror = FindOp(retryRequest.RequestIdentity)!;
        var target = FindOp(targetRequest.RequestIdentity)!;
        Assert.True(mirror.RequestState == OperationRequestState.InRound,
            $"mirror={mirror.RequestState}/{mirror.Zone}/merged={mirror.MergedInto}, target={target.RequestState}/{target.Zone}, result={result.Kind}/{result.ReasonCode}/{result.Detail}");
        Assert.Equal(OperationZone.Active, mirror.Zone);
        Assert.Equal(targetRequest.RequestIdentity, mirror.MergedInto);
        Assert.NotEqual(target.SubmissionIdentity, mirror.SubmissionIdentity);
    }

    [Fact]
    public async Task RetryablePrecheckReject_ExpiresBeforeFirstSend_WithoutCallingSender()
    {
        var occupied = false;
        var injectOccupiedAtPublish = true;
        var advanceClockAtPublish = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    if (injectOccupiedAtPublish) occupied = true;
                    if (advanceClockAtPublish)
                    {
                        _now += TimeSpan.FromDays(1);
                        advanceClockAtPublish = false;
                    }
                    return Task.CompletedTask;
                },
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });

        var request = Req(ns: "v2", workflow: "wf-precheck-expiry", operationType: OperationType.ExternalStart);
        var rejected = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejected.Kind);
        Assert.Equal(0, sends);
        Assert.Equal(0, FindOp(request.RequestIdentity)!.LastSendSeq);

        occupied = false;
        injectOccupiedAtPublish = false;
        advanceClockAtPublish = true;
        var expired = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.TerminalRejected, expired.Kind);
        Assert.Equal("retry_window_expired", expired.ReasonCode);
        Assert.Equal(0, sends);
        var operation = FindOp(request.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, operation.RequestState);
        Assert.Equal(OperationZone.Tombstone, operation.Zone);
        Assert.Equal("retry_window_expired", operation.LastPrecheckResult?.ReasonCode);
    }

    [Fact]
    public async Task SweepExpiredRetryWindow_UsesClockInsideAtomicPublish()
    {
        var occupied = false;
        var injectOccupied = true;
        Action? beforeExpiryPublish = null;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    if (injectOccupied) occupied = true;
                    return Task.CompletedTask;
                },
                BeforeRetryExpiryPublish = () => beforeExpiryPublish?.Invoke(),
            };
            h.Sender = _ => throw new Xunit.Sdk.XunitException("预检拒绝不得发送");
        });
        var request = Req(ns: "v2", workflow: "wf-sweep-lock-clock", operationType: OperationType.ExternalStart);
        var rejected = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejected.Kind);
        occupied = false;
        injectOccupied = false;
        var operation = FindOp(request.RequestIdentity)!;
        var deadline = Assert.IsType<DateTimeOffset>(operation.RetryWindowDeadlineUtc);

        _now = deadline + TimeSpan.FromSeconds(1); // 外层预筛看到已过期
        beforeExpiryPublish = () => _now = deadline - TimeSpan.FromSeconds(1); // 锁前回拨：锁内时间尚未过期
        Assert.Equal(0, svc.SweepExpiredRetryWindows());
        Assert.Equal(OperationRequestState.RetryableRejected, FindOp(request.RequestIdentity)!.RequestState);

        beforeExpiryPublish = null;
        _now = deadline + TimeSpan.FromSeconds(1);
        Assert.Equal(1, svc.SweepExpiredRetryWindows());
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(request.RequestIdentity)!.RequestState);
    }

    [Fact]
    public async Task DedupeWithDifferentOperationType_IsHeldWithoutLinkingOrSending()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });
        var node = Req(ns: "v2", workflow: "wf-incompatible-merge", trigger: "manual:incompatible-merge", operationType: OperationType.NodeExecution);
        var external = Req(ns: "v2", workflow: "wf-incompatible-merge", trigger: "manual:incompatible-merge", operationType: OperationType.ExternalStart);

        var results = await Task.WhenAll(svc.SubmitAsync(node), svc.SubmitAsync(external));
        Assert.All(results, result =>
        {
            Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
            Assert.Equal("merged_target_conflict", result.ReasonCode);
            Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        });
        Assert.Equal(0, sends);
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            // Simulate an old two-publication crash image: both peers are InRound but the compatibility hold
            // had not been published yet.
            foreach (var operation in file.Handoff!.Operations.Where(operation =>
                         operation.RequestIdentity == node.RequestIdentity || operation.RequestIdentity == external.RequestIdentity))
                operation.LastPrecheckResult = null;
            return null;
        }).Success);
        _ = svc.RecoverAfterRestart();
        var operations = new[] { FindOp(node.RequestIdentity)!, FindOp(external.RequestIdentity)! };
        Assert.All(operations, operation =>
        {
            Assert.Equal(OperationRequestState.InRound, operation.RequestState);
            Assert.Equal(OperationZone.Active, operation.Zone);
            Assert.Null(operation.MergedInto);
            Assert.Equal("merged_target_conflict", operation.LastPrecheckResult?.ReasonCode);
            Assert.Equal("merge_conflict_hold", operation.LastPrecheckResult?.EvidenceSource);
        });
    }

    [Fact]
    public async Task Recovery_HoldsIncompatibleUnlinkedGroupWithPriorSendHistory()
    {
        var sends = 0;
        var (svc, store, _, hooks) = BuildFacade(h => h.Sender = _ =>
        {
            sends++;
            return Task.FromResult<SendOutcome>(new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable"));
        });
        var prior = Req(ns: "v2", workflow: "wf-incompatible-retry-recovery", trigger: "manual:incompatible-retry-recovery",
            operationType: OperationType.NodeExecution);
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(prior)).Kind);
        Assert.Equal(1, FindOp(prior.RequestIdentity)!.LastSendSeq);

        var barriers = GatedBarrier(2);
        hooks.Barriers = barriers;
        var incoming = Req(ns: "v2", workflow: "wf-incompatible-retry-recovery", trigger: "manual:incompatible-retry-recovery",
            operationType: OperationType.ExternalStart);
        var results = await Task.WhenAll(svc.RetryAsync(prior.RequestIdentity), svc.SubmitAsync(incoming));
        Assert.All(results, result => Assert.Equal("merged_target_conflict", result.ReasonCode));
        Assert.Equal(1, sends);

        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            // Recreate the legacy crash image with an unlinked retry winner that has real send history.
            foreach (var operation in file.Handoff!.Operations.Where(operation =>
                         operation.RequestIdentity == prior.RequestIdentity || operation.RequestIdentity == incoming.RequestIdentity))
                operation.LastPrecheckResult = null;
            return null;
        }).Success);
        _ = svc.RecoverAfterRestart();

        var winner = FindOp(prior.RequestIdentity)!;
        var mirror = FindOp(incoming.RequestIdentity)!;
        Assert.Equal(1, winner.LastSendSeq);
        foreach (var operation in new[] { winner, mirror })
        {
            Assert.Equal(OperationRequestState.InRound, operation.RequestState);
            Assert.Equal(OperationZone.Active, operation.Zone);
            Assert.Equal("merged_target_conflict", operation.LastPrecheckResult?.ReasonCode);
            Assert.Equal("merge_conflict_hold", operation.LastPrecheckResult?.EvidenceSource);
        }
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task RetryablePrecheckMirror_PreservesPriorSendEvidenceAndCurrentReason()
    {
        var occupied = false;
        var injectOccupiedAtPublish = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Barriers.BeforeOccupyPublish = () =>
            {
                if (injectOccupiedAtPublish) occupied = true;
                return Task.CompletedTask;
            };
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:first_rejection")
                    : new SendOutcome.Accepted("fixture:accepted", null));
            };
        });
        var a = Req(ns: "v2", workflow: "wf-precheck-evidence", trigger: "manual:precheck-evidence", operationType: OperationType.ExternalStart);
        var b = Req(ns: "v2", workflow: "wf-precheck-evidence", trigger: "manual:precheck-evidence", operationType: OperationType.ExternalStart);
        var firstRound = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        Assert.All(firstRound, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;

        injectOccupiedAtPublish = true;
        var precheck = await svc.RetryAsync(winner.RequestIdentity);
        injectOccupiedAtPublish = false;
        Assert.Equal(AdmissionResultKind.RetryableRejected, precheck.Kind);
        Assert.Equal("execution_occupied", precheck.ReasonCode);
        Assert.Equal(1, sends);
        var currentWinner = FindOp(winner.RequestIdentity)!;
        var currentMirror = FindOp(mirror.RequestIdentity)!;
        Assert.Equal("task_running", currentWinner.LastResult?.ReasonCode);
        Assert.Equal("execution_occupied", currentWinner.LastPrecheckResult?.ReasonCode);
        Assert.Equal("task_running", currentMirror.LastResult?.ReasonCode);
        Assert.Equal("execution_occupied", currentMirror.LastPrecheckResult?.ReasonCode);
        var continued = await svc.SubmitAsync(ContinueOf(a.RequestIdentity == mirror.RequestIdentity ? a : b));
        Assert.Equal(AdmissionResultKind.RetryableRejected, continued.Kind);
        Assert.Equal("execution_occupied", continued.ReasonCode);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task NewMirrorJoiningRetryableWinner_PreservesWinnerSendEvidence()
    {
        var occupied = false;
        var sends = 0;
        var (svc, _, _, hooks) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:prior-send-rejection")
                    : new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });
        var prior = Req(trigger: "manual:new-mirror-history");
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(prior)).Kind);
        Assert.Equal(1, sends);

        var injectOccupied = true;
        var barriers = GatedBarrier(2);
        barriers.BeforeOccupyPublish = () =>
        {
            if (injectOccupied) occupied = true;
            return Task.CompletedTask;
        };
        hooks.Barriers = barriers;
        var incoming = Req(trigger: "manual:new-mirror-history"); // 同候选的新请求加入既有拒绝操作的重试轮
        var retryTask = svc.RetryAsync(prior.RequestIdentity);
        var incomingTask = svc.SubmitAsync(incoming);
        var results = await Task.WhenAll(retryTask, incomingTask);
        injectOccupied = false;

        Assert.All(results, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        Assert.Equal(1, sends);
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        Assert.Equal("task_running", mirror.LastResult?.ReasonCode);
        Assert.Equal("execution_occupied", mirror.LastPrecheckResult?.ReasonCode);
    }

    [Fact]
    public async Task Recovery_DoesNotCopyTransientWinnerStateIntoMergedMirror()
    {
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Barriers.AfterOccupyBeforeSend = () => Task.FromException(new InvalidOperationException("simulated crash after durable occupy"));
            h.Sender = _ => throw new Xunit.Sdk.XunitException("the crash barrier must prevent send");
        });
        var a = Req(ns: "v2", workflow: "wf-recovery-merge-state", trigger: "manual:recovery-merge-state", operationType: OperationType.ExternalStart);
        var b = Req(ns: "v2", workflow: "wf-recovery-merge-state", trigger: "manual:recovery-merge-state", operationType: OperationType.ExternalStart);
        _ = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winnerId = mirror.MergedInto!;

        Assert.True(svc.RecoverAfterRestart() > 0);
        mirror = FindOp(mirror.RequestIdentity)!;
        var winner = FindOp(winnerId)!;
        Assert.Equal(OperationRequestState.InRound, mirror.RequestState);
        Assert.Equal(OperationRequestState.Reconciling, winner.RequestState);

        var continued = await svc.SubmitAsync(ContinueOf(a.RequestIdentity == mirror.RequestIdentity ? a : b));
        Assert.Equal(AdmissionResultKind.Reconciling, continued.Kind);
        Assert.Equal(winner.SubmissionIdentity, continued.SubmissionIdentity);
    }

    [Fact]
    public async Task UnknownMergedWinner_RemainsLinkedUntilReconciliationClosesBoth()
    {
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture:unknown"));
        });
        var a = Req(trigger: "manual:unknown-merged");
        var b = Req(trigger: "manual:unknown-merged");
        var results = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        Assert.All(results, result => Assert.Equal(AdmissionResultKind.Reconciling, result.Kind));

        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;
        Assert.Equal(OperationRequestState.InRound, mirror.RequestState);
        Assert.Equal(OperationRequestState.Reconciling, winner.RequestState);
        Assert.Equal(ResponsibilityState.Pending, (await svc.SubmitAsync(ContinueOf(a.RequestIdentity == mirror.RequestIdentity ? a : b))).ResponsibilityState);

        var settled = await svc.SettleReconciledAsync(winner.RequestIdentity,
            new ReconcileSettlement.Accepted(winner.SubmissionIdentity!, winner.LastSendSeq, "fixture:reconciled", "run:merged"));
        Assert.Equal(AdmissionResultKind.Accepted, settled.Kind);
        var finalMirror = FindOp(mirror.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, finalMirror.RequestState);
        Assert.Equal(winner.SubmissionIdentity, finalMirror.SubmissionIdentity);
    }

    [Fact]
    public async Task SweepExpiredRetryWindow_TerminalizesPrecheckWinnerAndMirrorsTogether()
    {
        var occupied = false;
        var injectOccupiedAtPublish = true;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Barriers.BeforeOccupyPublish = () =>
            {
                if (injectOccupiedAtPublish) occupied = true;
                return Task.CompletedTask;
            };
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });
        var a = Req(ns: "v2", workflow: "wf-precheck-sweep", trigger: "manual:precheck-sweep", operationType: OperationType.ExternalStart);
        var b = Req(ns: "v2", workflow: "wf-precheck-sweep", trigger: "manual:precheck-sweep", operationType: OperationType.ExternalStart);
        var firstRound = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        Assert.All(firstRound, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        injectOccupiedAtPublish = false;
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winnerIdentity = mirror.MergedInto!;

        _now += TimeSpan.FromDays(1);
        Assert.Equal(1, svc.SweepExpiredRetryWindows());
        Assert.Equal(0, sends);
        foreach (var identity in new[] { winnerIdentity, mirror.RequestIdentity })
        {
            var expired = FindOp(identity)!;
            Assert.Equal(OperationRequestState.TerminalRejected, expired.RequestState);
            Assert.Equal(OperationZone.Tombstone, expired.Zone);
            Assert.Equal("retry_window_expired", expired.LastPrecheckResult?.ReasonCode);
        }
    }

    [Fact]
    public async Task SweepExpiredQueuedPrecheck_AfterPreemptConfirmReleasesSlot()
    {
        var occupied = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    occupied = true;
                    return Task.CompletedTask;
                },
            };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });
        var request = Req(ns: "v2", workflow: "wf-precheck-queued-expiry", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(request)).Kind);
        Assert.Equal(0, sends);

        _now += TimeSpan.FromDays(1);
        var blockedRetry = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, blockedRetry.Kind);
        Assert.Equal(OperationRequestState.Queued, FindOp(request.RequestIdentity)!.RequestState);
        Assert.Equal(1, svc.SweepExpiredRetryWindows());
        var expired = FindOp(request.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalRejected, expired.RequestState);
        Assert.Equal(OperationZone.Tombstone, expired.Zone);
        Assert.Equal("retry_window_expired", expired.LastPrecheckResult?.ReasonCode);
        Assert.False(expired.PreemptConfirmPending);
        Assert.Equal(AdmissionResultKind.TerminalRejected, (await svc.RetryAsync(request.RequestIdentity)).Kind);
        Assert.Equal(AdmissionResultKind.TerminalRejected, (await svc.SubmitAsync(ContinueOf(request))).Kind);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task PreemptConfirmPending_FreeOccupancyDoesNotAuthorizeContinueOrRetry()
    {
        var occupied = true;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = occupied };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
        });
        var request = Req(ns: "v2", workflow: "wf-preempt-confirm-gate", operationType: OperationType.ExternalStart);

        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, first.Kind);
        Assert.True(FindOp(request.RequestIdentity)!.PreemptConfirmPending);

        occupied = false;
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm,
            (await svc.SubmitAsync(ContinueOf(request))).Kind);
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm,
            (await svc.RetryAsync(request.RequestIdentity)).Kind);
        Assert.True(FindOp(request.RequestIdentity)!.PreemptConfirmPending);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task PreemptConfirmPending_SetBeforeFinalOccupy_CannotCreateSubmission()
    {
        ArbitrationLeaseStore? storeRef = null;
        AdmissionRequest? requestRef = null;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = false };
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("fixture:must-not-send", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                BeforeOccupyPublish = () =>
                {
                    var read = storeRef!.Read();
                    var lease = read.File!.Lease!;
                    var changed = storeRef.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                    {
                        var operation = file.Handoff!.Operations.Single(op => op.RequestIdentity == requestRef!.RequestIdentity);
                        operation.PreemptConfirmPending = true;
                        return null;
                    });
                    Assert.True(changed.Success, changed.Reason);
                    return Task.CompletedTask;
                },
            };
        });
        storeRef = store;
        var request = Req(ns: "v2", workflow: "wf-final-preempt-gate", operationType: OperationType.ExternalStart);
        requestRef = request;

        var result = await svc.SubmitAsync(request);

        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, result.Kind);
        Assert.True(FindOp(request.RequestIdentity)!.PreemptConfirmPending);
        Assert.Equal(OperationRequestState.Queued, FindOp(request.RequestIdentity)!.RequestState);
        Assert.True(string.IsNullOrEmpty(FindOp(request.RequestIdentity)!.SubmissionIdentity));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task SweepExpiredRetryableWinner_DoesNotRewriteConflictPendingMirror()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(2);
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable"));
            };
        });
        var a = Req(trigger: "manual:conflicted-expiry-mirror");
        var b = Req(trigger: "manual:conflicted-expiry-mirror");
        var results = await Task.WhenAll(svc.SubmitAsync(a), svc.SubmitAsync(b));
        Assert.All(results, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(operation => operation.RequestIdentity == mirror.RequestIdentity).ConflictPending = true;
            return null;
        }).Success);

        _now += TimeSpan.FromDays(1);
        Assert.Equal(0, svc.SweepExpiredRetryWindows());
        Assert.Equal(1, sends);
        Assert.Equal(OperationRequestState.RetryableRejected, FindOp(winner.RequestIdentity)!.RequestState);
        var conflictedMirror = FindOp(mirror.RequestIdentity)!;
        Assert.Equal(OperationRequestState.RetryableRejected, conflictedMirror.RequestState);
        Assert.True(conflictedMirror.ConflictPending);
        Assert.Equal("task_running", conflictedMirror.LastResult?.ReasonCode);
    }

    // ── 37. B7：续用绑定不一致=终局拒绝；同候选不同绑定并发创建=身份冲突（不去重共享）──

    [Fact]
    public async Task Binding_Mismatch_Rejected_NoDedupeSharing()
    {
        var (svc, _, _, _) = BuildFacade();
        var r = Req();
        r.RunBinding = "rb:1";
        _ = await svc.SubmitAsync(r);
        var conflict = await svc.SubmitAsync(new AdmissionRequest
        {
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = r.RequestIdentity,
            Candidate = r.Candidate,
            RunBinding = "rb:2", // 再绑不一致
        });
        Assert.Equal(AdmissionResultKind.TerminalRejected, conflict.Kind);
        Assert.Equal("binding_conflict", conflict.ReasonCode);

        var (svc2, _, _, _) = BuildFacade(h => h.Barriers = GatedBarrier(2), takeover: true); // 同 _dir 第二门面=接管
        var c1 = Req(trigger: "manual:panel:bind1");
        c1.RunBinding = "rb:x";
        var c2 = Req(trigger: "manual:panel:bind1");
        c2.RunBinding = "rb:y"; // 同候选不同绑定=不同排序键
        var t1 = svc2.SubmitAsync(c1);
        var t2 = svc2.SubmitAsync(c2);
        var res = await Task.WhenAll(t1, t2);
        Assert.All(res, x => Assert.Equal(AdmissionResultKind.TerminalRejected, x.Kind));
        Assert.All(res, x => Assert.Equal("identity_conflict", x.ReasonCode));
    }

    // ── 38. B7：同一游标已被消费=终局拒绝（唯一消费约束，防双跑）──

    [Fact]
    public async Task Cursor_AlreadyConsumed_TerminalReject()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        var r1 = Req(workflow: "group:c1", operationType: OperationType.NodeExecution);
        r1.Candidate!.NodeId = "n-1";
        r1.Candidate.ResourceRef = "node:n-1";
        r1.RunBinding = "run:same";
        r1.CursorRef = "cur:1";
        r1.CursorRevision = 5;
        var accepted = await svc.SubmitAsync(r1);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var r2 = Req(workflow: "group:c2", operationType: OperationType.NodeExecution);
        r2.Candidate!.NodeId = "n-1";
        r2.Candidate.ResourceRef = "node:n-1"; // 不同候选——只共享游标
        r2.RunBinding = "run:same";          // 会诊要求：明确绑定**同一非空 run**（不靠 null==null）
        r2.CursorRef = "cur:1";
        r2.CursorRevision = 5;
        var second = await svc.SubmitAsync(r2);
        Assert.Equal(AdmissionResultKind.TerminalRejected, second.Kind);
        Assert.Equal("cursor_already_consumed", second.ReasonCode);
        Assert.Equal(1, sends); // 第二次未发送
    }

    // ── 38b. [新增·2026-09-21 会诊阻断处置] ⑪b 唯一消费键必须**含运行归属** ──

    /// <summary>
    /// 两个**不同 run** 完全可能同时是同一 `cursorRef`（节点#出现#轮次）与同一 `cursorRevision`
    /// （运行记录修订）——缺 `RunBinding` 会把它们互判为「同一游标已消费」而**误拒合法提交**。
    /// 本夹具＝该误拒的可执行反例；同 run 内的唯一消费约束由上一夹具继续把守。
    /// </summary>
    [Fact]
    public async Task Cursor_SameRefDifferentRunBinding_NotTreatedAsConsumed()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });

        var r1 = Req(workflow: "group:c1", operationType: OperationType.NodeExecution);
        r1.Candidate!.NodeId = "n-1";
        r1.Candidate.ResourceRef = "node:n-1";
        r1.RunBinding = "run:1";
        r1.CursorRef = "n-1#0#0";
        r1.CursorRevision = 5;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r1)).Kind);

        var r2 = Req(workflow: "group:c2", operationType: OperationType.NodeExecution);
        r2.Candidate!.NodeId = "n-1";
        r2.Candidate.ResourceRef = "node:n-1"; // 另一运行：同游标引用、同修订
        r2.RunBinding = "run:2";
        r2.CursorRef = "n-1#0#0";
        r2.CursorRevision = 5;
        var second = await svc.SubmitAsync(r2);

        Assert.NotEqual("cursor_already_consumed", second.ReasonCode); // 不得误判为「同一游标已消费」
        Assert.Equal(AdmissionResultKind.Accepted, second.Kind);        // 明确受理（排除「发了但接管失败」）
        Assert.Equal(2, sends);                                        // 两个运行各自合法发送一次
    }

    /// <summary>
    /// [新增·2026-09-21 会诊重要项处置] ⑪b **不得按 Zone 过滤**：消费记录经 `TerminalPendingTransfer→Tombstone`
    /// 迁区后仍在盘上，同一 (runBinding, cursorRef, cursorRevision) **不得被再次消费**——否则 G8 让节点操作
    /// 提前终局（迁区释放主槽位）会让「同游标唯一消费」的防双跑保护静默消失。
    /// </summary>
    [Fact]
    public async Task Cursor_AlreadyConsumed_AfterMigrationStillBlocks()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.TakeoverTerminalConfirmed = (_, _) => true; // 本夹具聚焦消费保护，权威终态确认另测
        });

        var r1 = Req(workflow: "group:c1", operationType: OperationType.NodeExecution);
        r1.Candidate!.NodeId = "n-1";
        r1.Candidate.ResourceRef = "node:n-1";
        r1.RunBinding = "run:1";
        r1.CursorRef = "n-1#0#0";
        r1.CursorRevision = 5;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r1)).Kind);

        // 独立终局→迁区（主槽位释放），但记录仍在盘上
        Assert.Equal(AdmissionResultKind.Accepted,
            svc.MarkOperationTerminal(r1.RequestIdentity, "node_outcome:已观察节点权威终态").Kind);
        Assert.NotEqual(OperationZone.Active, FindOp(r1.RequestIdentity)!.Zone);

        var r2 = Req(workflow: "group:c2", operationType: OperationType.NodeExecution);
        r2.Candidate!.NodeId = "n-1";
        r2.Candidate.ResourceRef = "node:n-1"; // 另一候选，但同一消费键
        r2.RunBinding = "run:1";
        r2.CursorRef = "n-1#0#0";
        r2.CursorRevision = 5;
        var second = await svc.SubmitAsync(r2);

        Assert.Equal("cursor_already_consumed", second.ReasonCode); // 迁区后仍必须拦住
        Assert.Equal(1, sends);                                     // 未再发送
    }

    // ── 39. I1：发送回调异常=保守待对账（不抛出不悬置）；显式对账结清可恢复 ──

    [Fact]
    public async Task SenderThrows_Reconciling_ThenSettleCloses()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => throw new InvalidOperationException("boom"));
        var r = Req();
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Reconciling, result.Kind);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(r.RequestIdentity)!.RequestState);
        var settle = await svc.SettleReconciledAsync(r.RequestIdentity, new ReconcileSettlement.Accepted(result.SubmissionIdentity!, result.SendSeq, "owner:权威确认受理", null));
        Assert.Equal(AdmissionResultKind.Accepted, settle.Kind);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    // ── 40. I2：并发重试经在途检查短路合并（不重复发送、不重复耗预算、迟到重试不误报终局拒绝；
    //    覆盖口径=进程级 _inflight 短路；轮次级去重合并由夹具 3 覆盖）──

    [Fact]
    public async Task ConcurrentRetry_InflightShortCircuit_NoDoubleBudgetConsumption()
    {
        var call = 0;
        var barrierArmed = false; // 首次提交轮不挂屏障（否则未武装即死锁）
        var armedArrived = 0;
        var retryEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                var s = Interlocked.Increment(ref call);
                return Task.FromResult<SendOutcome>(s == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "ipc:任务运行中")
                    : new SendOutcome.Accepted("ipc:queued", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    // 收齐首个重试入队即放行：并发重试者在途短路（in_flight 不新增入队）或重入轮内去重——两种交错断言均成立，计数 2 会在短路交错下死锁。
                    if (barrierArmed && Interlocked.Increment(ref armedArrived) == 1) retryEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                BeforeRoundSnapshot = () => barrierArmed ? retryEnqueued.Task : Task.CompletedTask,
            };
        });
        var r = Req();
        var first = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);

        barrierArmed = true;
        var t1 = svc.RetryAsync(r.RequestIdentity);
        var t2 = svc.RetryAsync(r.RequestIdentity); // 并发重试者
        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(2, call); // 第一次+唯一一次重试
        Assert.Contains(results, x => x.Kind == AdmissionResultKind.Accepted);
        Assert.All(results, x => Assert.NotEqual(AdmissionResultKind.TerminalRejected, x.Kind));
        Assert.Equal(1, FindOp(r.RequestIdentity)!.LastResult!.RetryBudgetUsed); // 预算只消耗一次
    }

    // ── 41. I4：Pending 与未决 Submission 并存时同一意图重复发布=幂等成功；新意图仍被阻断 ──

    [Fact]
    public async Task Pending_IdempotentRepublish_WithUnresolvedSubmission()
    {
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("ipc_timeout")));
        var r = Req();
        var stableIdentity = ArbitrationOrdering.BuildStableIdentity(r.Candidate);
        var read1 = store.Read();
        var intent = new PendingHandoffIntent
        {
            ActionId = "act:i4",
            SuspendedRunIdentity = "run:old",
            AuthorizedPreemptor = stableIdentity,
            TargetEpoch = "ep1",
            Phase = HandoffPhase.PreemptRequested,
            SubmissionIdentity = "sub:i4:1",
        };
        Assert.True(store.TryPublishIntent(read1.File!.Lease!.LeaseId, read1.File.Lease.OwnerEpoch, read1.File.Revision, intent).Success);

        var submit = await svc.SubmitAsync(r); // 授权方占位→发送未知→Submission 未决
        Assert.Equal(AdmissionResultKind.Reconciling, submit.Kind);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);

        var read2 = store.Read();
        var republish = store.TryPublishIntent(read2.File!.Lease!.LeaseId, read2.File.Lease.OwnerEpoch, read2.File.Revision, intent);
        Assert.True(republish.Success, "同一意图幂等重放不应被拒: " + republish.Reason); // I4

        var other = new PendingHandoffIntent
        {
            ActionId = "act:other",
            SuspendedRunIdentity = "run:old",
            AuthorizedPreemptor = stableIdentity,
            TargetEpoch = "ep1",
            Phase = HandoffPhase.PreemptRequested,
            SubmissionIdentity = "sub:other:1",
        };
        var blocked = store.TryPublishIntent(read2.File.Lease.LeaseId, read2.File.Lease.OwnerEpoch, read2.File.Revision, other);
        Assert.False(blocked.Success);
        Assert.Equal("submission_unresolved", blocked.Reason); // 新意图仍受未决发送阻断
    }

    // ── 42. I3："{requestIdentity}" 占位符回填（§2.2 触发出现身份含请求身份；适配器无需预知）──

    [Fact]
    public async Task TriggerPlaceholder_BackfilledWithRequestIdentity()
    {
        SubmissionDispatch? captured = null;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = d => { captured = d; return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        var r = Req(trigger: "manual:panel:{requestIdentity}");
        var result = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.NotNull(captured);
        Assert.Equal("manual:panel:" + r.RequestIdentity, captured!.Candidate.TriggerOccurrenceId);
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal("manual:panel:" + r.RequestIdentity, op.Candidate!.TriggerOccurrenceId); // 登记快照与分派一致
        Assert.Equal(op.CandidateId, captured.CandidateId);
    }

    // ── 43. N1：冲突组+多个合法候选——落选合法候选=NotSelected not_selected（不串写冲突组原因）──

    [Fact]
    public async Task Round_ConflictGroupPlusLegit_LoserNotSelectedNotConflicting()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Barriers = GatedBarrier(4);
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
        });
        var x1 = Req(payload: "p1", trigger: "manual:panel:cg1"); // 冲突组（同身份不同载荷）
        var x2 = Req(payload: "p2", trigger: "manual:panel:cg1");
        var y1 = Req(priority: 1); // 合法候选 a
        var y2 = Req(priority: 0); // 合法候选 b
        var tasks = new[] { svc.SubmitAsync(x1), svc.SubmitAsync(x2), svc.SubmitAsync(y1), svc.SubmitAsync(y2) };
        var results = await Task.WhenAll(tasks);

        Assert.Equal(2, results.Count(r => r.Kind == AdmissionResultKind.TerminalRejected && r.ReasonCode == "identity_conflict")); // 冲突组整组终局
        var accepted = Assert.Single(results.Where(r => r.Kind == AdmissionResultKind.Accepted));
        var loser = Assert.Single(results.Where(r => r.Kind == AdmissionResultKind.NotSelected));
        Assert.Contains(loser.RequestIdentity, new[] { y1.RequestIdentity, y2.RequestIdentity });
        Assert.NotEqual(accepted.RequestIdentity, loser.RequestIdentity);
        Assert.Equal("not_selected", loser.ReasonCode); // 合格落选=not_selected（不回退串写冲突组原因）
        Assert.NotNull(loser.WinnerCandidateId); // 含胜者引用
        var loserOp = FindOp(loser.RequestIdentity)!;
        Assert.Equal(OperationRequestState.NotSelected, loserOp.RequestState);
        Assert.Equal(loser.WinnerCandidateId, loserOp.LastPrecheckResult!.WinnerRef); // 压制依据持久化（I5）
        Assert.Equal(1, sends);
    }

    // ── 44. 快照前屏障崩溃（BeforeRoundSnapshot 移出 _gate 后语义锁定）：整队响亮失败、无发送、在途清理可重驱 ──

    [Fact]
    public async Task BeforeRoundSnapshotCrash_WholeQueueFailsLoudly_NoSend_InflightCleaned()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.Barriers = new AdmissionBarriers { BeforeRoundSnapshot = () => throw new InvalidOperationException("模拟快照前屏障崩溃") };
        });
        var r = Req();
        var crashed = await svc.SubmitAsync(r);
        Assert.Equal(AdmissionResultKind.Error, crashed.Kind);
        Assert.Equal("internal_error", crashed.ReasonCode);
        Assert.Equal(0, sends); // 未发布发送许可
        Assert.Equal(OperationRequestState.Queued, FindOp(r.RequestIdentity)!.RequestState); // 登记在册、从未发布发送许可

        var redrive = await svc.SubmitAsync(ContinueOf(r)); // 在途已清理→重新驱动（非 in_flight 短路）→屏障仍崩溃→再次响亮失败
        Assert.Equal(AdmissionResultKind.Error, redrive.Kind);
        Assert.Equal("internal_error", redrive.ReasonCode);
        Assert.Equal(0, sends);
    }

    // ── 41. R5.3.1／G8：主槽位容量 —— 逐节点结清后可持续创建（33 节点）＋ 不结清时第 33 个创建被拒 ──

    /// <summary>
    /// **容量正向（确定性，组件级）**：连续 33 个**不同节点**候选，每个获准后立即按「节点权威终态」结清（`MarkOperationTerminal`），
    /// 则后续创建**始终获准**（主槽位随责任结清释放）——不得出现 `operations_capacity_full`。
    /// </summary>
    [Fact]
    public async Task Capacity_33NodeCandidates_AllAccepted_WhenEachSettled()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.TakeoverTerminalConfirmed = (_, _) => true; // 节点权威终态另测；本夹具聚焦容量释放
        });

        for (var i = 1; i <= 33; i++)
        {
            var r = Req(workflow: "wf-" + i, operationType: OperationType.NodeExecution);
            r.RunBinding = "run-" + i;
            r.Candidate!.NodeId = "n-" + i;
            r.Candidate.ResourceRef = "node:n-" + i;
            r.Candidate.Attempt = 1;
            r.CursorRef = "n-" + i + "#0#0";
            r.CursorRevision = 1;
            var accepted = await svc.SubmitAsync(r);
            Assert.True(accepted.Kind == AdmissionResultKind.Accepted,
                "第 " + i + " 个节点创建应获准，实际 " + accepted.Kind + "/" + accepted.ReasonCode);
            Assert.Equal(AdmissionResultKind.Accepted,
                svc.MarkOperationTerminal(r.RequestIdentity, "node_outcome:已观察节点权威终态").Kind);
            Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(r.RequestIdentity)!.RequestState);
        }

        Assert.Equal(33, sends);
        Assert.DoesNotContain(ReadLease().File!.Handoff!.Operations,
            o => o.LastResult?.ReasonCode?.StartsWith("operations_capacity_full", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// **容量负向（确定性）**：32 个**不可结清**（仍 Active/Queued）的操作占满主槽位后，第 33 个创建必须被
    /// `operations_capacity_full` 拒绝（本夹具即该上限的可执行证据）。
    /// </summary>
    [Fact]
    public async Task Capacity_MainSlotsExhausted_33rdCreateRejected()
    {
        // 占槽构造：32 笔各自已受理但尚未终结的远端作业保持 Active；
        // Unknown 只允许一笔未决 Submission，后续 submission_conflict 已按 owner 裁决终局释放，
        // 不再能用「32 次冲突拒绝」充当真实占槽反例。
        var acceptedId = 0;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(
            new SendOutcome.Accepted("ext:accepted", null, "job-running-" + Interlocked.Increment(ref acceptedId))));

        for (var i = 1; i <= 32; i++)
        {
            var r = Req(workflow: "wf-" + i, operationType: OperationType.NodeExecution);
            r.RunBinding = "run-" + i;
            r.Candidate!.NodeId = "n-" + i;
            r.Candidate.ResourceRef = "node:n-" + i;
            r.CursorRef = "n-" + i + "#0#0";
            r.CursorRevision = 1;
            var res = await svc.SubmitAsync(r); // 已受理未终结：操作保持 Active，持续占主槽位
            Assert.True(res.Kind == AdmissionResultKind.Accepted, $"第 {i} 笔：{res.Kind}/{res.ReasonCode}");
            Assert.False(res.ReasonCode.StartsWith("operations_capacity_full", StringComparison.Ordinal));
            // 占槽判据＝**Zone=Active**（主槽位＝Active+TerminalPendingTransfer；终局迁墓碑后才释放）
            Assert.Equal(OperationZone.Active, FindOp(r.RequestIdentity)!.Zone);
        }

        // 会诊要求：第 33 次调用前**一次性**确认 32 个不同身份仍为 Active（占槽在调用时刻成立）
        var activeIds = ReadLease().File!.Handoff!.Operations
            .Where(o => o.Zone == OperationZone.Active).Select(o => o.RequestIdentity).Distinct().ToList();
        Assert.Equal(32, activeIds.Count);

        var overflow = Req(workflow: "wf-overflow");
        overflow.RunBinding = "run-overflow";
        overflow.OperationType = OperationType.NodeExecution;
        overflow.Candidate!.NodeId = "n-overflow";
        overflow.Candidate.ResourceRef = "node:n-overflow";
        overflow.CursorRef = "n-overflow#0#0";
        overflow.CursorRevision = 1;
        var last = await svc.SubmitAsync(overflow);
        Assert.Equal(AdmissionResultKind.Error, last.Kind);
        Assert.StartsWith("operations_capacity_full", last.ReasonCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// R5 owner 裁决：未决发送仍占原槽，因 submission_conflict 被拒的后续请求须留审计并释放自身主槽。
    /// 连续 32 次被拒不得挤掉新的请求；权威对账关闭原责任后，新请求能获得自己的发送许可。
    /// </summary>
    [Fact]
    public async Task SubmissionConflict_RepeatedRejections_ReleaseOwnSlots_KeepOriginalResponsibility()
    {
        var sends = 0;
        var accept = false;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult<SendOutcome>(accept
                ? new SendOutcome.Accepted("ext:accepted", null, "job-after")
                : new SendOutcome.Unknown("fixture_unknown"));
        });

        var original = Req(ns: "v2", workflow: "wf-original", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Reconciling, (await svc.SubmitAsync(original)).Kind);
        var originalOp = FindOp(original.RequestIdentity)!;
        var originalSubmission = ReadLease().File!.Handoff!.Submission!;
        Assert.Equal(1, sends);

        for (var i = 1; i <= 32; i++)
        {
            var rejected = Req(ns: "v2", workflow: "wf-blocked-" + i, operationType: OperationType.ExternalStart);
            var result = await svc.SubmitAsync(rejected);
            Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
            Assert.Equal(ResponsibilityState.Settled, result.ResponsibilityState);
            Assert.Equal("submission_conflict", result.ReasonCode);
            var op = FindOp(rejected.RequestIdentity)!;
            Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
            Assert.NotEqual(OperationZone.Active, op.Zone);
            Assert.Equal("submission_conflict", op.LastPrecheckResult?.ReasonCode);
            Assert.Equal(0, op.LastSendSeq);
            Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));

            if (i == 1)
            {
                var continueUse = Req(ns: "v2", workflow: "wf-blocked-1", operationType: OperationType.ExternalStart);
                continueUse.Kind = AdmissionKind.ContinueUse;
                continueUse.RequestIdentity = rejected.RequestIdentity;
                continueUse.Candidate = ArbitrationAdmissionService.CloneCandidate(op.Candidate!);
                var repeated = await svc.SubmitAsync(continueUse);
                Assert.Equal(AdmissionResultKind.TerminalRejected, repeated.Kind);
                Assert.True(repeated.ResponsibilityState == ResponsibilityState.Settled,
                    $"kind={repeated.Kind}, responsibility={repeated.ResponsibilityState}, reason={repeated.ReasonCode}, detail={repeated.Detail}, identity={repeated.RequestIdentity}");
                Assert.Equal(rejected.RequestIdentity, repeated.RequestIdentity);
                Assert.Equal("submission_conflict", repeated.ReasonCode);
                Assert.Equal(1, sends);
            }
        }

        Assert.Equal(1, sends);
        Assert.Single(ReadLease().File!.Handoff!.Operations.Where(o => o.Zone == OperationZone.Active));
        Assert.Equal(originalOp.SubmissionIdentity, ReadLease().File!.Handoff!.Submission!.SubmissionIdentity);
        Assert.Equal(originalSubmission.SendSeq, ReadLease().File!.Handoff!.Submission!.SendSeq);

        var settled = await svc.SettleReconciledAsync(original.RequestIdentity,
            new ReconcileSettlement.NotAccepted(originalOp.SubmissionIdentity, originalOp.LastSendSeq,
                "bgi_rejected", Retryable: false, "fixture:权威对账确定未受理"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, settled.Kind);
        Assert.Null(ReadLease().File!.Handoff!.Submission);

        accept = true;
        var after = Req(ns: "v2", workflow: "wf-after", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(after)).Kind);
        Assert.Equal(2, sends);
        Assert.Equal(1, FindOp(after.RequestIdentity)!.LastSendSeq);
    }

    /// <summary>
    /// Tombstones keep the frozen 256-entry / 24-hour capacity contract: the 256th
    /// audited conflict rejection releases its primary slot, and the next create
    /// is loudly backpressured until a tombstone becomes cleanable.
    /// </summary>
    [Fact]
    public async Task SubmissionConflict_LastTombstoneSlot_ReleasesPrimarySlot_ThenCapacityBackpressures()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ =>
        {
            sends++;
            return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown"));
        });
        Prefill(store, tombstones: ArbitrationAdmissionService.TombstoneLimit - 1, pendingTransfers: 0);

        var original = Req(ns: "v2", workflow: "wf-original-capacity", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Reconciling, (await svc.SubmitAsync(original)).Kind);
        var rejected = Req(ns: "v2", workflow: "wf-conflict-last-tombstone", operationType: OperationType.ExternalStart);

        var result = await svc.SubmitAsync(rejected);
        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
        Assert.Equal(ResponsibilityState.Settled, result.ResponsibilityState);
        Assert.Equal("submission_conflict", result.ReasonCode);
        Assert.Equal(OperationZone.Tombstone, FindOp(rejected.RequestIdentity)!.Zone);
        Assert.Single(ReadLease().File!.Handoff!.Operations.Where(o => o.Zone == OperationZone.Active));
        Assert.Equal(1, sends);

        var blocked = Req(ns: "v2", workflow: "wf-after-tombstone-capacity", operationType: OperationType.ExternalStart);
        var blockedResult = await svc.SubmitAsync(blocked);
        Assert.Equal(AdmissionResultKind.Error, blockedResult.Kind);
        Assert.StartsWith("operations_capacity_full", blockedResult.ReasonCode, StringComparison.Ordinal);
        Assert.Contains("tombstone=256", blockedResult.ReasonCode, StringComparison.Ordinal);
        Assert.Null(FindOp(blocked.RequestIdentity));
        Assert.Equal(1, sends);
        Assert.Single(ReadLease().File!.Handoff!.Operations.Where(o => o.Zone == OperationZone.Active));
    }

    // ── 42. R5.3.4：A6 票据压制 —— 授权抢占方保留资格、无关候选被压制（组件级） ──

    /// <summary>
    /// **R5.3.4①（授权抢占方保留资格）**：存在存续 A6 票据且**授权抢占方身份＝本候选 stableIdentity** 时，
    /// 候选**不被票据压制**（不得以 `ticket_suppressed` 拒绝；空闲事实下应正常获准并发送一次）。
    /// </summary>
    [Fact]
    public async Task Ticket_AuthorizedPreemptorRetainsEligibility()
    {
        var r = Req(trigger: "fixture:preemptor");
        var stable = ArbitrationOrdering.BuildStableIdentity(r.Candidate);
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding
                {
                    SuspendedRunIdentity = "run-suspended",
                    AuthorizedPreemptorIdentity = stable,
                    Epoch = "ep1",
                },
            };
        });

        var result = await svc.SubmitAsync(r);

        Assert.NotEqual("ticket_suppressed", result.ReasonCode); // 授权抢占方保留资格
        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.Equal(1, sends);
    }

    /// <summary>
    /// **R5.3.4②（压制无关候选）**：票据存续期间**非授权方**候选一律被压制 ⇒
    /// **`NotSelected`＋原因码 `ticket_suppressed`＋`SuppressionSource == "ticket"`**。
    /// **定性**：压制经「**资格筛选→无胜者**」路径落地 ⇒ 这是**资格筛选产生的本地未获选终局**，
    /// **不是**「发送后取得的远端拒绝回执」，也**不是**「确定未受理」（依据 §3.1 非胜者终局＋§4.1a 第一行）。
    /// **不得泛化（均为有条件结果，勿写成无条件规则）**：本用例在**事实已知、其余前置满足**时经票据筛选无胜者 ⇒ `NotSelected`；
    /// 若与 `ExecutionFactsUnknown` 并存 ⇒ `NeedReconcile`（退回 `Queued`，**非** NotSelected）。
    /// **获选后、占位前**锁内复核判 `ticket_suppressed` 且终局落盘成功 ⇒ `TerminalRejected`；**票据变化本身不必然拒绝**
    /// （撤销票据、或替换后仍匹配授权身份均可能放行；**[已整改]** 锁内现已校验 `Epoch`／`SuspendedRunIdentity` 并加**双源一致性**与缺字段阻断）。恢复专用边界同理。
    /// 后两条**分类分支已有实现、本批尚未补反例夹具**（登记为 R5.3.4③–⑦）。
    /// **作用域收窄**：本用例只证明「未发送（`LastSendSeq==0`／发送身份为空）＋未占位（无 Submission）」，
    /// **不**声称「零副作用」——请求仍会被登记进 `Operations` 并推进到 `NotSelected` 终局。
    /// </summary>
    [Fact]
    public async Task Ticket_UnrelatedCandidateSuppressed_NoSendNoPlaceholder()
    {
        var unrelated = Req(trigger: "fixture:unrelated");
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding
                {
                    SuspendedRunIdentity = "run-suspended",
                    AuthorizedPreemptorIdentity = "not-this-candidate",
                    Epoch = "ep1",
                },
            };
        });

        var result = await svc.SubmitAsync(unrelated);

        // 结果面：本地未获选终局（**非**远端拒绝回执）＋压制来源可鉴别。
        Assert.Equal(AdmissionResultKind.NotSelected, result.Kind);
        Assert.Equal("ticket_suppressed", result.ReasonCode);
        Assert.Equal("ticket", result.SuppressionSource);
        Assert.Equal(0, sends);                                       // 未发送

        // 持久化面：操作登记在册、停在 NotSelected 终局，受理从未发生。
        var op = FindOp(unrelated.RequestIdentity);
        Assert.NotNull(op);
        Assert.Equal(OperationRequestState.NotSelected, op!.RequestState);
        Assert.Equal("ticket_suppressed", op.LastPrecheckResult!.ReasonCode);  // 原因持久化
        Assert.Equal("ticket", op.LastPrecheckResult.SuppressionSource);       // 压制来源持久化
        Assert.Equal(0, op.LastSendSeq);                               // 未发布发送许可
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));      // 发送身份为空
        Assert.Null(ReadLease().File!.Handoff!.Submission);            // 未占位（无发送许可）
    }

    // ── 43. R5.3.4③–⑦：票据三要素（epoch／被挂起运行）＋本地面事实源＋settle 生命周期＋A6 原票据恢复 ──

    /// <summary>发布一条未决交接责任（夹具：权威票据三要素在本地的可推导面＝`Handoff.Pending`）。</summary>
    private PendingHandoffIntent PublishPending(ArbitrationLeaseStore store, string authorizedPreemptor,
        HandoffPhase phase, string suspendedRun = "run:suspended", string targetEpoch = "ep1", string actionId = "act:r534")
    {
        var read = store.Read();
        var intent = new PendingHandoffIntent
        {
            ActionId = actionId,
            SuspendedRunIdentity = suspendedRun,
            AuthorizedPreemptor = authorizedPreemptor,
            TargetEpoch = targetEpoch,
            Phase = phase,
            SubmissionIdentity = "sub:" + actionId + ":1",
        };
        var pub = store.TryPublishIntent(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, intent);
        Assert.True(pub.Success, "夹具前置：发布未决交接责任失败 " + pub.Reason);
        return intent;
    }

    /// <summary>
    /// **R5.3.4③（票据 epoch 要素）**：授权抢占方身份匹配，但**票据 epoch ≠ 当前 BGI 纪元** ⇒ **不得放行**
    /// （`stale_epoch` 终局拒绝、零发送）——「票据失效」不等于「解除责任／可立即启动」。
    /// </summary>
    [Fact]
    public async Task Ticket_StaleEpoch_AuthorizedPreemptorNotAdmitted()
    {
        var sends = 0;
        var r = Req(trigger: "fixture:stale-ticket");
        var stable = ArbitrationOrdering.BuildStableIdentity(r.Candidate);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding { SuspendedRunIdentity = "run:suspended", AuthorizedPreemptorIdentity = stable, Epoch = "ep0" },
            };
        });

        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
        Assert.Equal("stale_epoch", result.ReasonCode);
        Assert.Equal(0, sends);
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(r.RequestIdentity)!.RequestState);
    }

    /// <summary>
    /// **R5.3.4④（本地面事实源）**：宿主**不供给** `ActiveTicket` 时，锁内以**已持久化的未决交接责任**
    /// （`Handoff.Pending`）推导三要素 ⇒ 无关候选仍被压制（`ticket_suppressed`／零发送），
    /// **绝不因缺票据事实而推导空闲**（保守方向）。
    /// </summary>
    [Fact]
    public async Task Ticket_FactFromPersistedPending_UnrelatedSuppressedWithoutInjectedFacts()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.PreemptRequested);

        var other = Req(trigger: "fixture:unrelated-no-injected-facts");
        var result = await svc.SubmitAsync(other);

        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind); // 锁内复核路径（非轮次筛选的 NotSelected）
        Assert.Equal("ticket_suppressed", result.ReasonCode);
        Assert.Equal(0, sends);
        var op = FindOp(other.RequestIdentity)!;
        Assert.Equal(0, op.LastSendSeq);
        Assert.True(string.IsNullOrEmpty(op.SubmissionIdentity));
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    /// <summary>
    /// **R5.3.4⑤（settle 生命周期）**：抢占方终态只把责任推进到 `SettlePending` ⇒ **压制保持**
    /// （无关候选仍 `ticket_suppressed`）；且该阶段**不得准入授权抢占方**（`pending_conflict`）——
    /// 「抢占方终态／Submission 关闭」均**不直接**解除压制。
    /// </summary>
    [Fact]
    public async Task Ticket_SettlePending_HoldsSuppression_AndBlocksPreemptor()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });

        var preemptor = Req(trigger: "fixture:preemptor-settle");
        var stable = ArbitrationOrdering.BuildStableIdentity(preemptor.Candidate);
        PublishPending(store, authorizedPreemptor: stable, HandoffPhase.SettlePending);

        var suppressed = await svc.SubmitAsync(Req(trigger: "fixture:other-settle"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, suppressed.Kind);
        Assert.Equal("ticket_suppressed", suppressed.ReasonCode);

        var authorized = await svc.SubmitAsync(preemptor);
        Assert.Equal(AdmissionResultKind.TerminalRejected, authorized.Kind);
        Assert.Equal("pending_conflict", authorized.ReasonCode);

        Assert.Equal(0, sends); // 压制保持期间双方均不得发
    }

    /// <summary>
    /// **R5.3.4⑤（settle 闭环才解除压制）**：仅当未决交接责任经**关联权威证据**消解（`Pending` 清空）后，
    /// 无关候选方可重新准入——「责任存续」是压制的唯一本地判据（消解前零放行）。
    /// </summary>
    [Fact]
    public async Task Ticket_SuppressionReleasedOnlyAfterIntentResolved()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        var intent = PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.PreemptRequested);

        var blocked = await svc.SubmitAsync(Req(trigger: "fixture:before-resolve"));
        Assert.Equal("ticket_suppressed", blocked.ReasonCode);
        Assert.Equal(0, sends);

        var read = store.Read();
        var resolved = store.TryResolveIntent(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision,
            new IntentResolveEvidence
            {
                ActionId = intent.ActionId,
                SubmissionIdentity = intent.SubmissionIdentity,
                Epoch = intent.TargetEpoch,
                ObservedFact = "cancelled",
            });
        Assert.True(resolved.Success, "夹具前置：消解未决交接责任失败 " + resolved.Reason);

        var admitted = await svc.SubmitAsync(Req(trigger: "fixture:after-resolve"));
        Assert.Equal(AdmissionResultKind.Accepted, admitted.Kind);
        Assert.Equal(1, sends);
    }

    /// <summary>
    /// **R5.3.4⑥（A6 原票据恢复）**：责任阶段=`RestorePending`（分支=`interrupted-relocate`）＋ **恢复目标＝被挂起运行**
    /// ＋ 目标 epoch 相符时，该恢复动作**正是完成该交接**（不另建替代作业）⇒ 获准；且**恢复本身不消解交接责任**。
    /// **范围如实（小节会诊收窄）**：本用例证明「Pending 条件下的**恢复准入分支**」——**不证明**发送确实消费了原票据协议、
    /// 也不证明完整恢复闭环（`restore_confirmed` 回流）；该部分＝**§17 P57 未闭合**。
    /// </summary>
    [Fact]
    public async Task Ticket_A6RestoreAllowedInRestorePendingWithMatchingRun()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.RestorePending, suspendedRun: "run:take", actionId: "act:restore");

        var admitted = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:a6-restore",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.Accepted, admitted.Kind);
        Assert.Equal(1, sends);
        Assert.NotNull(ReadLease().File!.Handoff!.Pending); // 用原票据：恢复动作不消解交接责任
    }

    /// <summary>**R5.3.4⑥（反例·禁止抢跑）**：阶段未到 `RestorePending` 时，同一被挂起运行的恢复一律 `pending_conflict`（零发送）。</summary>
    [Fact]
    public async Task Ticket_A6RestoreBeforeRestorePending_RejectedNoSend()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.Confirming, suspendedRun: "run:take");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:a6-early",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("pending_conflict", rejected.ReasonCode);
        Assert.Equal(0, sends);
    }

    /// <summary>**R5.3.4③（反例·不得另建替代作业）**：恢复目标 ≠ 票据被挂起运行 ⇒ 压制（零发送，不得换键重跑）。</summary>
    [Fact]
    public async Task Ticket_A6RestoreWrongRun_SuppressedNoSend()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.RestorePending, suspendedRun: "run:take");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:a6-wrong-run",
            RunId = "run:other",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("ticket_suppressed", rejected.ReasonCode);
        Assert.Equal(0, sends);
    }
    // ── 44. R5.3.1③④⑥⑦：交接责任存续期的准入推进／执行权另检／不凭超时放行 ──

    /// <summary>
    /// **R5.3.1③（交接责任存续期·授权方准入推进）**：`PreemptRequested` 阶段＋**执行空闲**事实下，
    /// **同一交接的授权抢占方**（同一 stableIdentity）经准入链获准并发送一次——「资格保留」在该窗口是**可推进**的，
    /// 不是永久全禁；无关候选同窗口零放行（见 `Ticket_*` 系列）。
    /// </summary>
    [Fact]
    public async Task Handoff_AuthorizedPreemptorAdmitted_WhenExecutionFree()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });

        var preemptor = Req(trigger: "fixture:preempt-window");
        var stable = ArbitrationOrdering.BuildStableIdentity(preemptor.Candidate);
        PublishPending(store, authorizedPreemptor: stable, HandoffPhase.PreemptRequested, actionId: "act:preempt-window");

        var admitted = await svc.SubmitAsync(preemptor);

        Assert.Equal(AdmissionResultKind.Accepted, admitted.Kind);
        Assert.Equal(1, sends);
        Assert.Equal(1, FindOp(preemptor.RequestIdentity)!.LastSendSeq);
    }

    /// <summary>
    /// **R5.3.1⑥（资格 ≠ 执行权）**：同一票据窗口中 **执行被占用** 时，**授权抢占方仍被执行权检查阻断**
    /// （`NeedPreemptConfirm`／`execution_occupied`，操作回 `Queued`、**零发送**）——
    /// 「保留资格」不授予执行权；同窗口无关候选则被票据压制。
    /// </summary>
    [Fact]
    public async Task Handoff_AuthorizedPreemptorHeldByExecutionOccupancy_NoSend()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts { ExecutionOccupied = true }; // 执行占用（票据另由本地面推导）
        });

        var preemptor = Req(trigger: "fixture:preempt-occupied");
        var stable = ArbitrationOrdering.BuildStableIdentity(preemptor.Candidate);
        PublishPending(store, authorizedPreemptor: stable, HandoffPhase.PreemptRequested, actionId: "act:preempt-occupied");

        var occupied = await svc.SubmitAsync(preemptor);
        Assert.Equal(AdmissionResultKind.NeedPreemptConfirm, occupied.Kind);
        Assert.Equal("execution_occupied", occupied.ReasonCode);
        Assert.Equal(OperationRequestState.Queued, FindOp(preemptor.RequestIdentity)!.RequestState); // 未发布发送许可

        // 无关候选：本轮在**轮次级**先被执行占用判定拦下（`Decide` 的占用分支先于锁内票据复核），
        // 结果同样**零放行**——票据压制在「执行空闲」窗口由其单独证明（见 `Ticket_FactFromPersistedPending_*`）。
        var unrelated = await svc.SubmitAsync(Req(trigger: "fixture:preempt-occupied-other"));
        Assert.NotEqual(AdmissionResultKind.Accepted, unrelated.Kind);
        Assert.Equal(OperationRequestState.Queued, FindOp(unrelated.RequestIdentity)!.RequestState);

        Assert.Equal(0, sends); // 执行权与压制两道闸门都不放行
    }

    /// <summary>
    /// **R5.3.1④（不凭超时放行）**：超时／关联不符／事实未知把交接推进到 `ReconcilePending` ⇒
    /// **任何候选（含授权方）一律零放行**，且**责任保持**（`Pending` 不被超时清空）——
    /// missPolicy 只处置本次触发，不构成放行依据。
    /// </summary>
    [Fact]
    public async Task Handoff_ReconcilePending_NoOneAdmitted_NoTimeoutRelease()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });

        var preemptor = Req(trigger: "fixture:reconcile-window");
        var stable = ArbitrationOrdering.BuildStableIdentity(preemptor.Candidate);
        PublishPending(store, authorizedPreemptor: stable, HandoffPhase.ReconcilePending, actionId: "act:reconcile");

        var authorized = await svc.SubmitAsync(preemptor);
        Assert.Equal(AdmissionResultKind.TerminalRejected, authorized.Kind);
        Assert.Equal("pending_conflict", authorized.ReasonCode);

        var unrelated = await svc.SubmitAsync(Req(trigger: "fixture:reconcile-other"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, unrelated.Kind);
        Assert.Equal("ticket_suppressed", unrelated.ReasonCode);

        Assert.Equal(0, sends);
        Assert.NotNull(ReadLease().File!.Handoff!.Pending); // 超时不解除责任
    }

    /// <summary>
    /// **R5.3.1⑤（错误确认不解除责任）**：与阶段不相容的证据（`SettlePending` 阶段收到权威退出词而非
    /// `protocol_ended`）被拒 ⇒ **责任保持**，后续准入仍被压制（零放行）。
    /// </summary>
    [Fact]
    public async Task Handoff_WrongEvidenceRejected_ResponsibilityHeld()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        var intent = PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.SettlePending, actionId: "act:wrong-evidence");

        var read = store.Read();
        var rejected = store.TryResolveIntent(read.File!.Lease!.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision,
            new IntentResolveEvidence
            {
                ActionId = intent.ActionId,
                SubmissionIdentity = intent.SubmissionIdentity,
                Epoch = intent.TargetEpoch,
                ObservedFact = "cancelled", // SettlePending 只认 protocol_ended
            });
        Assert.False(rejected.Success);
        Assert.Equal("evidence_phase_incompatible", rejected.Reason);
        Assert.NotNull(ReadLease().File!.Handoff!.Pending);

        var stillSuppressed = await svc.SubmitAsync(Req(trigger: "fixture:wrong-evidence-other"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, stillSuppressed.Kind);
        Assert.Equal("ticket_suppressed", stillSuppressed.ReasonCode);
        Assert.Equal(0, sends);
    }
    // ── 45. R5.3.2 被切任务三选（放弃／顺延／显式重投）：准入面不变量 ──

    /// <summary>
    /// **R5.3.2（三选之一·放弃）**：被切源按 D15/D12 钉死归类 ⇒ 操作**终局完成**；此后 `RetryAsync`
    /// **只返回既有终局事实（`already_terminal`）**，**不重发、不消耗重试预算、不再触发有界重试**。
    /// </summary>
    [Fact]
    public async Task Preempted_Abandon_TerminalNoRetryNoResend()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        var r = Req(trigger: "fixture:preempted-abandon", workflow: "wf-abandon");
        r.OperationType = OperationType.NodeExecution;
        r.Candidate!.NodeId = "n-1";
        r.Candidate.ResourceRef = "node:n-1";
        r.RunBinding = "run-1";
        r.CursorRef = "n-1#0#0";
        r.CursorRevision = 1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(r.RequestIdentity, "node_outcome:被切源归类=cancelled（远端确认后；五类表，阻断收尾）").Kind);

        var afterTerminal = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, afterTerminal.RequestState);
        var sendsBefore = sends;

        var retry = await svc.RetryAsync(r.RequestIdentity);

        Assert.Equal("already_terminal", retry.ReasonCode);                      // 返回既有终局事实（非新发送）
        Assert.Equal(sendsBefore, sends);                                        // 不重发
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(r.RequestIdentity)!.RequestState); // 不回退
    }

    /// <summary>
    /// **R5.3.2（三选之二·顺延）——只证准入面不变量**：①自动路径（`RetryAsync`）**不复活**已终局的被切操作；
    /// ②「重新参选」在准入面必须由**显式新提交**承载（另一个出现身份 ⇒ 新操作），旧操作**不被改写**。
    /// **范围如实**：本用例**不**证明顺延的身份/游标/attempt 语义（同一 attempt 重入 vs 新 attempt）——
    /// 该语义＝**§17 P56 待裁决**；亦不证明「抢占方结束后自动重排参选」的调度实现（R5.4/R5.5 联调）。
    /// </summary>
    [Fact]
    public async Task Preempted_Deferred_AutomaticPathDoesNotRevive_ExplicitNewSubmissionRequired()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        var first = Req(trigger: "fixture:defer-a", workflow: "wf-defer");
        first.OperationType = OperationType.NodeExecution;
        first.Candidate!.NodeId = "n-9";
        first.Candidate.ResourceRef = "node:n-9";
        first.RunBinding = "run-9";
        first.CursorRef = "n-9#0#0";
        first.CursorRevision = 1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(first)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(first.RequestIdentity, "node_outcome:被切源归类=cancelled（远端确认后）").Kind);

        // 自动路径：不复活。
        Assert.Equal("already_terminal", (await svc.RetryAsync(first.RequestIdentity)).ReasonCode);

        // 显式新提交（同一窗口、同一节点、**另一个出现身份**）⇒ 新操作获准。
        var second = Req(trigger: "fixture:defer-b", workflow: "wf-defer");
        second.OperationType = OperationType.NodeExecution;
        second.Candidate!.NodeId = "n-9";
        second.Candidate.ResourceRef = "node:n-9";
        second.Candidate.Occurrence = 1;      // 出现身份与候选字段同步变化（避免「只改游标」的自相矛盾）
        second.RunBinding = "run-9";
        second.CursorRef = "n-9#1#0";
        second.CursorRevision = 1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(second)).Kind);

        Assert.NotEqual(first.RequestIdentity, second.RequestIdentity);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(first.RequestIdentity)!.RequestState); // 旧操作未被改写
        Assert.Equal(2, sends);
    }

    /// <summary>
    /// **R5.3.2（三选之二·跨窗不补跑）**：窗口关闭（资格事实不满足）⇒ 提交**终局拒绝**（`eligibility_lost`）、
    /// **零发送**——跨窗**不补跑**，也不得凭旧操作自动复活。
    /// </summary>
    [Fact]
    public async Task Preempted_OutsideWindow_RejectedNoBackfill()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.EligibilityProvider = _ => new CandidateEligibility { IsDue = true, PrerequisiteReady = true, FlexibleWindowOpen = false }; // 跨窗
        });

        var r = Req(trigger: "fixture:backfill-closed", workflow: "wf-backfill");
        r.OperationType = OperationType.NodeExecution;
        r.Candidate!.NodeId = "n-7";
        r.Candidate.ResourceRef = "node:n-7";

        var result = await svc.SubmitAsync(r);

        // 资格筛选在**轮次级**完成：窗口关闭 ⇒ 本地未获选终局（**非**发送后的远端拒绝回执），零发送、不补跑。
        Assert.Equal(AdmissionResultKind.NotSelected, result.Kind);
        Assert.Equal("window_closed", result.ReasonCode);
        Assert.Equal("eligibility", result.SuppressionSource);
        Assert.Equal(0, sends);
        var op = FindOp(r.RequestIdentity)!;
        Assert.Equal(0, op.LastSendSeq);
        Assert.Equal(OperationRequestState.NotSelected, op.RequestState);
    }

    /// <summary>
    /// <summary>
    /// **R5.3.2（三选之三·显式重投）**：重投**不是**复活旧操作——①**同 attempt＋同游标修订**再投被
    /// **唯一消费约束**阻断（`cursor_already_consumed`，防复用已取消作业）；②**新 attempt＋新游标代次＋新幂等提交键**
    /// 才获准，且新键**实际随请求携带并持久化**（`OperationRecord.WireSubmitKey`）；③旧被切操作**不被改写**。
    /// **范围如实**：本用例**不**裁决「顺延/重投应采用哪种游标代次」——该语义＝**§17 P56 待裁决**；
    /// 此处只记录实现的**可观测约束**。
    /// </summary>
    [Fact]
    public async Task Preempted_ReRunWithNewAttempt_NewKeyNewIdentity_OldOpUnchanged()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.TakeoverTerminalConfirmed = (_, _) => true;
        });

        // ① 原被切操作（attempt=1）：同一节点出现/循环轮次，游标 ref 归一、修订=1、键按 attempt 派生。
        var key1 = RunStore.DeriveSubmissionKey("run-3", "n-3", 0, 0, 1);
        var a1 = Req(trigger: "fixture:rerun", workflow: "wf-rerun");
        a1.Candidate!.Attempt = 1;
        a1.OperationType = OperationType.NodeExecution;
        a1.Candidate.NodeId = "n-3";
        a1.Candidate.ResourceRef = "node:n-3";
        a1.RunBinding = "run-3";
        a1.CursorRef = "n-3#0#0";
        a1.CursorRevision = 1;
        a1.WireSubmitKey = key1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(a1)).Kind);
        Assert.Equal(key1, FindOp(a1.RequestIdentity)!.WireSubmitKey); // 键实际持久化
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(a1.RequestIdentity, "node_outcome:被切源归类=cancelled（远端确认后）").Kind);

        // ② 反例：同 attempt＋同游标修订再投 ⇒ 唯一消费约束阻断（不得复用已取消作业、不得重发）。
        var reuse = Req(trigger: "fixture:rerun", workflow: "wf-rerun");
        reuse.Candidate!.Attempt = 1;
        reuse.OperationType = OperationType.NodeExecution;
        reuse.Candidate.NodeId = "n-3";
        reuse.Candidate.ResourceRef = "node:n-3";
        reuse.RunBinding = "run-3";
        reuse.CursorRef = "n-3#0#0";
        reuse.CursorRevision = 1;
        reuse.WireSubmitKey = key1;
        var blocked = await svc.SubmitAsync(reuse);
        Assert.Equal(AdmissionResultKind.TerminalRejected, blocked.Kind);
        Assert.Equal("cursor_already_consumed", blocked.ReasonCode);
        Assert.Equal(1, sends); // 仍是首次那一笔

        // ③ 正向：新 attempt ＋ 新游标代次 ＋ 新键（同一节点出现/循环轮次）⇒ 获准，且新键随请求持久化。
        var key2 = RunStore.DeriveSubmissionKey("run-3", "n-3", 0, 0, 2);
        var a2 = Req(trigger: "fixture:rerun", workflow: "wf-rerun");
        a2.Candidate!.Attempt = 2;
        a2.OperationType = OperationType.NodeExecution;
        a2.Candidate.NodeId = "n-3";
        a2.Candidate.ResourceRef = "node:n-3";
        a2.RunBinding = "run-3";
        a2.CursorRef = "n-3#0#0";
        a2.CursorRevision = 2;
        a2.WireSubmitKey = key2;
        var accepted2 = await svc.SubmitAsync(a2);

        Assert.Equal(AdmissionResultKind.Accepted, accepted2.Kind);
        Assert.Equal(key2, FindOp(a2.RequestIdentity)!.WireSubmitKey);        // 新键确实随本次重投落地
        Assert.NotEqual(key1, key2);                                          // 新 attempt ⇒ 新幂等键
        Assert.NotEqual(a1.RequestIdentity, a2.RequestIdentity);
        Assert.NotEqual(FindOp(a1.RequestIdentity)!.CandidateId, FindOp(a2.RequestIdentity)!.CandidateId);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(a1.RequestIdentity)!.RequestState); // 旧操作未被改写
        Assert.Equal(2, sends);
    }
    // ── 46. R5.3.4 会诊整改：双源校验的冲突/缺字段/分支误用反例 ──

    /// <summary>
    /// **反例（会诊必改项 1）**：注入的 BGI 侧票据快照与本地未决责任**冲突**时，A6 恢复豁免**不得**绕过双源校验——
    /// 两源要素不一致 ⇒ `ticket_conflict` 终局拒绝、零发送、**交接责任保留**（**两个事实源并列校验、不取或**）。
    /// </summary>
    [Fact]
    public async Task Ticket_ConflictingInjectedSnapshotAndLocalPending_BlocksRestore()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding { SuspendedRunIdentity = "run:other", AuthorizedPreemptorIdentity = "stable:someone-else", Epoch = "ep0" },
            };
        });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.RestorePending, suspendedRun: "run:take", actionId: "act:conflict");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:conflicting-ticket",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("ticket_conflict", rejected.ReasonCode); // 双源一致性先于单源要素判定
        Assert.Equal(0, sends);
        Assert.NotNull(ReadLease().File!.Handoff!.Pending);
    }

    /// <summary>
    /// **反例（会诊必改项 1）**：票据**缺必要要素**（此处缺被挂起运行）时**不得**当「无票据」放行 ⇒
    /// `ticket_malformed` 终局拒绝、零发送。
    /// </summary>
    [Fact]
    public async Task Ticket_MissingElement_NotTreatedAsNoTicket()
    {
        var sends = 0;
        var r = Req(trigger: "fixture:malformed-ticket");
        var stable = ArbitrationOrdering.BuildStableIdentity(r.Candidate);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding { SuspendedRunIdentity = "", AuthorizedPreemptorIdentity = stable, Epoch = "ep1" },
            };
        });

        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
        Assert.Equal("ticket_malformed", result.ReasonCode);
        Assert.Equal(0, sends);
    }

    /// <summary>
    /// **反例（会诊必改项 2）**：**暂停续行**（`paused-continue`）**不得借用** A6 原票据恢复豁免——
    /// 即使目标运行与票据被挂起运行一致，责任阶段未到 ⇒ `pending_conflict`、零发送。
    /// </summary>
    [Fact]
    public async Task Ticket_PausedContinueDoesNotUseA6RestoreExemption()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h => h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); });
        PublishPending(store, authorizedPreemptor: "stable:someone-else", HandoffPhase.RestorePending, suspendedRun: "run:take", actionId: "act:paused");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:paused-continue-exemption",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "paused-continue",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("pending_conflict", rejected.ReasonCode);
        Assert.Equal(0, sends);
    }
    /// <summary>
    /// **反例（验证轮必改项 1）**：两源**仅授权抢占方身份不同**（其余要素全等）时，恢复豁免**不得**通过 ⇒
    /// `ticket_conflict` 终局拒绝、**零发送、零占位**，且**交接责任保留**（`Pending` 不被清除）。
    /// </summary>
    [Fact]
    public async Task Ticket_TwoSourcesDifferOnlyInPreemptor_BlocksRestore()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding { SuspendedRunIdentity = "run:take", AuthorizedPreemptorIdentity = "stable:B", Epoch = "ep1" },
            };
        });
        PublishPending(store, authorizedPreemptor: "stable:A", HandoffPhase.RestorePending, suspendedRun: "run:take", actionId: "act:preemptor-conflict");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:preemptor-conflict",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("ticket_conflict", rejected.ReasonCode);
        Assert.Equal(0, sends);
        Assert.Null(ReadLease().File!.Handoff!.Submission);              // 零占位
        Assert.NotNull(ReadLease().File!.Handoff!.Pending);              // 交接责任保留
    }

    /// <summary>
    /// **反例（验证轮必改项 1）**：两源**仅被挂起运行不同**（授权方与 epoch 全等）⇒ 同样保守阻断
    /// （`ticket_conflict`）、零发送、责任保留。
    /// </summary>
    [Fact]
    public async Task Ticket_TwoSourcesDifferOnlyInSuspendedRun_BlocksRestore()
    {
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)); };
            h.FactsProvider = () => new ArbitrationFacts
            {
                ActiveTicket = new TicketBinding { SuspendedRunIdentity = "run:other", AuthorizedPreemptorIdentity = "stable:A", Epoch = "ep1" },
            };
        });
        PublishPending(store, authorizedPreemptor: "stable:A", HandoffPhase.RestorePending, suspendedRun: "run:take", actionId: "act:run-conflict");

        var rejected = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:run-conflict",
            RunId = "run:take",
            WorkflowId = "group:g1",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });

        Assert.Equal(AdmissionResultKind.TerminalRejected, rejected.Kind);
        Assert.Equal("ticket_conflict", rejected.ReasonCode);
        Assert.Equal(0, sends);
        Assert.Null(ReadLease().File!.Handoff!.Submission);   // 零占位
        Assert.NotNull(ReadLease().File!.Handoff!.Pending);   // 交接责任保留
    }

    // ── 23. 完成结算入口（R5.3 §24.3-4／§24.15，Batch B）：三分支＋幂等重放＋陈旧身份拒绝 ──

    /// <summary>
    /// 夹具前置：终态钩子**接真实 `ExternalStartLedger`**（RecordAccepted／MarkTerminal／读回逐字段比对），
    /// 不用外层字符串记录冒充台账（[第三轮验证会诊阻断处置]）。
    /// </summary>
    private (ArbitrationAdmissionService Svc, ExternalStartLedger Ledger) BuildExternalFacadeWithCompletion(
        bool senderUnknown = false, bool acceptanceFails = false,
        bool noTerminalHooks = false, bool terminalPersistFailsWithMismatchedPayload = false,
        bool ledgerConfirmFailsOnce = false, string? jobIdReadLate = null, bool ledgerUnreadableInFinalize = false,
        bool noJobIdReadHook = false, bool gateDuplicates = false, Func<int, SendOutcome>? senderOverride = null)
    {
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var jobCounter = 0;
        var confirmCalls = 0;
        var jobIdReadCalls = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            if (gateDuplicates) h.Barriers = GatedBarrier(2);
            // 每笔发送各自独立句柄（台账禁止同一 jobId 归属两笔发送：夹具按真实语义给唯一句柄）。
            h.Sender = _ =>
            {
                var sequence = System.Threading.Interlocked.Increment(ref jobCounter);
                if (senderOverride is not null) return Task.FromResult(senderOverride(sequence));
                return senderUnknown
                    ? Task.FromResult<SendOutcome>(new SendOutcome.Unknown("adapter_unknown", "ext:adapter"))
                    : Task.FromResult<SendOutcome>(new SendOutcome.Accepted(
                        "ext:accepted", null, "job-" + sequence));
            };
            if (acceptanceFails) h.TakeoverPersist = _ => Task.FromResult<string?>("record_failed:fixture");
            // §24.3-3 句柄合并读回（**所有分支都需要**：钩子缺失/不匹配分支同样要能读出台账权威句柄）。
            if (!noJobIdReadHook) h.TakeoverJobIdRead = (sub, seq) =>
            {
                // 反例支持：首次读回为 null（模拟「台账句柄并发后补」的 TOCTOU 窗口），其后返回指定句柄。
                var call = System.Threading.Interlocked.Increment(ref jobIdReadCalls);
                if (jobIdReadLate is not null && call == 1) return LedgerHandleProbe.Absent();
                // 反例支持：终局事务内复读失败（读取故障不得被当作「无句柄」而释放占用）。
                if (ledgerUnreadableInFinalize && call >= 2) return LedgerHandleProbe.Unreadable();
                var entry = ledger.Read().File?.Entries.FirstOrDefault(e =>
                    string.Equals(e.SubmissionIdentity, sub, StringComparison.Ordinal) && e.SendSeq == seq);
                if (jobIdReadLate is not null)
                    return string.IsNullOrEmpty(entry?.JobId)
                        ? LedgerHandleProbe.Present(jobIdReadLate)
                        : LedgerHandleProbe.Present(entry!.JobId);
                return string.IsNullOrEmpty(entry?.JobId)
                    ? LedgerHandleProbe.Absent()
                    : LedgerHandleProbe.Present(entry!.JobId);
            };
            if (noTerminalHooks) return; // 钩子缺失：验证「终态回写钩子缺失 ⇒ 保守停驻」
            if (terminalPersistFailsWithMismatchedPayload)
            {
                // **真实 MarkTerminal 写入不同载荷**（原始终态词/证据词加后缀）⇒ 生产式**逐字段**读回必然不确认
                // ⇒ 必须停驻（不得用宽松确认继续关闭/终局）。此处不使用硬编码 false，以真实台账驱动反例。
                h.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, err, job, source, kind) =>
                {
                    var r = ledger.MarkTerminal(sub, seq, evidence + "-mismatch", observed,
                        (raw ?? "") + "-mismatch", err, OperationType.ExternalStart, job, source, kind);
                    return r.Success ? null : "ledger_terminal_failed:" + (r.Reason ?? "unknown");
                };
                h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, err, job, source, observed, kind) =>
                {
                    var entry = ledger.Read().File?.Entries.FirstOrDefault(e =>
                        string.Equals(e.SubmissionIdentity, sub, StringComparison.Ordinal) && e.SendSeq == seq);
                    return entry is { State: LedgerEntryState.Terminal }
                        && string.Equals(entry.TerminalEvidence, raw, StringComparison.Ordinal)
                        && string.Equals(entry.RawTerminal, raw, StringComparison.Ordinal)
                        && string.Equals(entry.ExecutionErrorCode, err, StringComparison.Ordinal)
                        && string.Equals(entry.JobId, job, StringComparison.Ordinal)
                        && string.Equals(entry.TerminalEvidenceSource, source, StringComparison.Ordinal)
                        && entry.TerminalObservedAtUtc == observed
                        && entry.TerminalKind == kind;
                };
                h.TakeoverJobIdRead = (sub, seq) =>
                {
                    var read = ledger.Read();
                    if (!read.Valid) return LedgerHandleProbe.Unreadable();
                    var entry = read.File?.Entries.FirstOrDefault(e =>
                        string.Equals(e.SubmissionIdentity, sub, StringComparison.Ordinal) && e.SendSeq == seq);
                    return string.IsNullOrEmpty(entry?.JobId)
                        ? LedgerHandleProbe.Absent()
                        : LedgerHandleProbe.Present(entry!.JobId);
                };
                return;
            }
            h.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, err, job, source, kind) =>
            {
                var r = ledger.MarkTerminal(sub, seq, evidence, observed, raw, err, OperationType.ExternalStart, job, source, kind);
                return r.Success ? null : "ledger_terminal_failed:" + (r.Reason ?? "unknown");
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, err, job, source, observed, kind) =>
            {
                // 反例支持：首次读回不确认（模拟「发布结果不明」），随后按真实台账逐字段确认。
                if (ledgerConfirmFailsOnce && System.Threading.Interlocked.Increment(ref confirmCalls) == 1) return false;
                var entry = ledger.Read().File?.Entries.FirstOrDefault(e =>
                    string.Equals(e.SubmissionIdentity, sub, StringComparison.Ordinal) && e.SendSeq == seq);
                return entry is { State: LedgerEntryState.Terminal }
                    && string.Equals(entry.TerminalEvidence, raw, StringComparison.Ordinal)
                    && string.Equals(entry.ExecutionErrorCode, err, StringComparison.Ordinal)
                    && string.Equals(entry.JobId, job, StringComparison.Ordinal)
                    && string.Equals(entry.TerminalEvidenceSource, source, StringComparison.Ordinal)
                    && entry.TerminalObservedAtUtc == observed
                    && entry.TerminalKind == kind;
            };
            // §24.3-3 句柄合并读回由上方**统一**赋值（含 `jobIdReadLate` 反例支持，不得在此覆盖）。
        });
        return (svc, ledger);
    }

    private async Task<(string RequestIdentity, string SubmissionIdentity, int SendSeq)> AcceptedExternalOpAsync(
        ArbitrationAdmissionService svc)
    {
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext");
        // §24.17：外部启动的可信操作类型由适配器提供（这里模拟 E3/E4/E5 适配层）。
        req.OperationType = OperationType.ExternalStart;
        var accepted = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);
        return (accepted.RequestIdentity, accepted.SubmissionIdentity!, accepted.SendSeq);
    }

    [Fact]
    public async Task SettleCompletion_NullOrUnknown_WritesNoTerminalCarriers()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var plain = await svc.SettleCompletionAsync(rid, sub, seq, null);
        Assert.Equal(AdmissionResultKind.Accepted, plain.Kind);
        Assert.Equal("accepted_no_terminal", plain.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, plain.ResponsibilityState); // 已登记后不得回落 None（§24.6-5）
        Assert.Equal(ExecutionDisposition.None, plain.ExecutionDisposition);

        var unknown = await svc.SettleCompletionAsync(rid, sub, seq, ExternalStartCompletion.UnknownWith("not yet observed"));
        Assert.Equal(AdmissionResultKind.NeedReconcile, unknown.Kind);
        Assert.Equal(ResponsibilityState.Pending, unknown.ResponsibilityState);
        Assert.Equal(ExecutionDisposition.Unknown, unknown.ExecutionDisposition);

        var op = FindOp(rid)!;
        Assert.Null(op.PendingTerminal);   // §24.19-2：Unknown 不得生成 PendingTerminal
        Assert.Null(op.ExecutionResult);   // 且不得借未知写权威终态
        // 台账：仍为「已受理未终结」（未写终态副本 ⇒ 继续占用，且无观察时点副本）。
        var ledgerEntry = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution, ledgerEntry.State);
        Assert.Null(ledgerEntry.TerminalObservedAtUtc);
        Assert.Single(ledger.GetOccupancy().Entries);
    }

    [Fact]
    public async Task SettleCompletion_Terminal_WritesCarriersThenFinalizes_AndReplaysIdempotently()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var done = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Accepted, done.Kind);
        Assert.Equal("terminal_completed", done.ReasonCode);
        Assert.Equal(ResponsibilityState.Settled, done.ResponsibilityState);
        Assert.Equal(ExecutionDisposition.None, done.ExecutionDisposition);
        // 真实台账：终态副本已写、观察时点与落盘时点分离、终局后不再占用。
        var ledgerEntry = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(LedgerEntryState.Terminal, ledgerEntry.State);
        Assert.Equal("completed", ledgerEntry.TerminalEvidence);
        Assert.Equal(_now, ledgerEntry.TerminalObservedAtUtc);
        Assert.Empty(ledger.GetOccupancy().Entries);

        var op = FindOp(rid)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
        Assert.NotEqual(OperationZone.Active, op.Zone);
        Assert.Null(op.PendingTerminal);                 // 终局完成 ⇒ 责任已结清（终态事实由 ExecutionResult 承载）
        Assert.NotNull(op.ExecutionResult);
        Assert.Equal(ExecutionResultKind.Succeeded, op.ExecutionResult!.Kind);
        Assert.Equal(_now, op.ExecutionResult.ObservedAtUtc);
        Assert.Null(ReadLease().File!.Handoff!.Submission);

        // 幂等重放：返回既有终态事实，不重复写台账、不改责任状态（§24.13-2）。
        var replay = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal("already_terminal", replay.ReasonCode);
        Assert.Equal(ResponsibilityState.Settled, replay.ResponsibilityState);
        Assert.Single(ledger.Read().File!.Entries); // 幂等重放：不写第二份终态
    }

    [Fact]
    public async Task SettleCompletion_FinalizesActiveMergedExternalMirrorAtomically()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion(gateDuplicates: true);
        var first = Req(ns: "manual", workflow: "onedragon:completion-merge", payload: "p-ext",
            trigger: "manual:completion-merge", operationType: OperationType.ExternalStart);
        var second = Req(ns: "manual", workflow: "onedragon:completion-merge", payload: "p-ext",
            trigger: "manual:completion-merge", operationType: OperationType.ExternalStart);
        var accepted = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(second));
        Assert.All(accepted, result => Assert.Equal(AdmissionResultKind.Accepted, result.Kind));

        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;
        var completed = await svc.SettleCompletionAsync(winner.RequestIdentity, winner.SubmissionIdentity!, winner.LastSendSeq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal(ResponsibilityState.Settled, completed.ResponsibilityState);

        var finalMirror = FindOp(mirror.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, finalMirror.RequestState);
        Assert.NotEqual(OperationZone.Active, finalMirror.Zone);
        Assert.Equal(winner.SubmissionIdentity, finalMirror.SubmissionIdentity);
        Assert.Equal(winner.LastSendSeq, finalMirror.LastSendSeq);
        var continued = await svc.SubmitAsync(ContinueOf(first.RequestIdentity == mirror.RequestIdentity ? first : second));
        Assert.Equal(AdmissionResultKind.Accepted, continued.Kind);
        Assert.Equal(ResponsibilityState.Settled, continued.ResponsibilityState);
    }

    [Fact]
    public async Task ReconcilingExecutionFact_IsNotDowngradedForWinnerOrMirror()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion(senderUnknown: true, noTerminalHooks: true, gateDuplicates: true);
        var first = Req(ns: "manual", workflow: "onedragon:reconciling-merge", payload: "p-ext",
            trigger: "manual:reconciling-merge", operationType: OperationType.ExternalStart);
        var second = Req(ns: "manual", workflow: "onedragon:reconciling-merge", payload: "p-ext",
            trigger: "manual:reconciling-merge", operationType: OperationType.ExternalStart);
        var initial = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(second));
        Assert.All(initial, result => Assert.Equal(AdmissionResultKind.Reconciling, result.Kind));

        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winner = FindOp(mirror.MergedInto!)!;
        var observed = await svc.SettleCompletionAsync(winner.RequestIdentity, winner.SubmissionIdentity!, winner.LastSendSeq,
            ExternalStartCompletion.CancelledWith("cancelled", "owner:reconcile_query", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, observed.Kind);
        Assert.Equal(ResponsibilityState.Pending, observed.ResponsibilityState); // carriers are durable; terminal-ledger step is intentionally unavailable
        var winnerAfter = FindOp(winner.RequestIdentity)!;
        var mirrorAfter = FindOp(mirror.RequestIdentity)!;
        Assert.Equal(winnerAfter.SubmissionIdentity, winnerAfter.ExecutionResult?.SubmissionIdentity);
        Assert.Equal(winnerAfter.LastSendSeq, winnerAfter.ExecutionResult?.SendSeq);
        Assert.Equal(winnerAfter.SubmissionIdentity, mirrorAfter.SubmissionIdentity);
        Assert.Equal(winnerAfter.LastSendSeq, mirrorAfter.LastSendSeq);

        var winnerView = await svc.SubmitAsync(ContinueOf(winner.RequestIdentity == first.RequestIdentity ? first : second));
        var mirrorView = await svc.SubmitAsync(ContinueOf(mirror.RequestIdentity == first.RequestIdentity ? first : second));
        foreach (var view in new[] { winnerView, mirrorView })
        {
            Assert.Equal(AdmissionResultKind.Cancelled, view.Kind);
            Assert.Equal(ExecutionDisposition.Cancelled, view.ExecutionDisposition);
            Assert.Equal(ResponsibilityState.Pending, view.ResponsibilityState);
        }
    }

    [Fact]
    public async Task NotAcceptedReconciliation_CannotDowngradeExistingExecutionFactOrAuthorizeRetry()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion(senderUnknown: true, noTerminalHooks: true);
        var request = Req(ns: "manual", workflow: "onedragon:contradictory-reconcile", payload: "p-ext",
            trigger: "manual:contradictory-reconcile", operationType: OperationType.ExternalStart);
        var initial = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Reconciling, initial.Kind);
        var op = FindOp(request.RequestIdentity)!;

        var observed = await svc.SettleCompletionAsync(op.RequestIdentity, op.SubmissionIdentity!, op.LastSendSeq,
            ExternalStartCompletion.CancelledWith("cancelled", "owner:terminal-query", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, observed.Kind);
        Assert.Equal(ResponsibilityState.Pending, observed.ResponsibilityState);
        var executionBefore = FindOp(request.RequestIdentity)!.ExecutionResult;
        Assert.NotNull(executionBefore);

        var contradictory = await svc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(op.SubmissionIdentity!, op.LastSendSeq,
                "not_accepted", Retryable: true, EvidenceSource: "owner:contradictory-query"));

        Assert.Equal(AdmissionResultKind.NeedReconcile, contradictory.Kind);
        Assert.Equal(ResponsibilityState.Pending, contradictory.ResponsibilityState);
        var after = FindOp(request.RequestIdentity)!;
        Assert.NotNull(after.ExecutionResult);
        Assert.Equal(executionBefore!.Kind, after.ExecutionResult!.Kind);
        Assert.Equal(executionBefore.SubmissionIdentity, after.ExecutionResult.SubmissionIdentity);
        Assert.Equal(executionBefore.SendSeq, after.ExecutionResult.SendSeq);
        Assert.Equal(executionBefore.RawTerminal, after.ExecutionResult.RawTerminal);
        Assert.True(after.ConflictPending);
        Assert.NotEqual(OperationRequestState.RetryableRejected, after.RequestState);
        Assert.Equal(op.LastSendSeq, after.LastSendSeq);
    }

    [Fact]
    public async Task RetryUnknownWinner_AtomicallyAdvancesExistingMirrorSendIdentity()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion(
            senderOverride: seq => seq == 1
                ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:first")
                : new SendOutcome.Unknown("fixture:second_unknown"),
            gateDuplicates: true);
        var first = Req(ns: "manual", workflow: "onedragon:retry-mirror-identity", payload: "p-ext",
            trigger: "manual:retry-mirror-identity", operationType: OperationType.ExternalStart);
        var second = Req(ns: "manual", workflow: "onedragon:retry-mirror-identity", payload: "p-ext",
            trigger: "manual:retry-mirror-identity", operationType: OperationType.ExternalStart);
        var initial = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(second));
        Assert.All(initial, result => Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind));
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winnerId = mirror.MergedInto!;

        var retry = await svc.RetryAsync(winnerId);

        Assert.Equal(AdmissionResultKind.Reconciling, retry.Kind);
        var winner = FindOp(winnerId)!;
        var updatedMirror = FindOp(mirror.RequestIdentity)!;
        Assert.Equal(2, winner.LastSendSeq);
        Assert.Equal(winner.SubmissionIdentity, updatedMirror.SubmissionIdentity);
        Assert.Equal(winner.LastSendSeq, updatedMirror.LastSendSeq);
        Assert.Equal(2, winner.LastSendSeq);
        Assert.Equal(ResponsibilityState.Pending, retry.ResponsibilityState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_FinalizesAcceptedMirrorsWithWinnerInSamePublication(bool injectConflictAfterClassification)
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(gateDuplicates: true);
        var hooks = _lastHooks!;
        var first = Req(ns: "manual", workflow: "onedragon:recovery-merge-terminal", payload: "p-ext",
            trigger: "manual:recovery-merge-terminal", operationType: OperationType.ExternalStart);
        var second = Req(ns: "manual", workflow: "onedragon:recovery-merge-terminal", payload: "p-ext",
            trigger: "manual:recovery-merge-terminal", operationType: OperationType.ExternalStart);
        var accepted = await Task.WhenAll(svc.SubmitAsync(first), svc.SubmitAsync(second));
        Assert.All(accepted, result => Assert.Equal(AdmissionResultKind.Accepted, result.Kind));
        var mirror = ReadLease().File!.Handoff!.Operations.Single(operation => operation.MergedInto is not null);
        var winnerId = mirror.MergedInto!;
        var winner = FindOp(winnerId)!;
        var terminal = ExternalStartCompletion.CancelledWith("cancelled", "fixture:crash-window", _now);
        var completed = await svc.SettleCompletionAsync(winnerId, winner.SubmissionIdentity!, winner.LastSendSeq, terminal);
        Assert.Equal(AdmissionResultKind.Cancelled, completed.Kind);
        winner = FindOp(winnerId)!;
        mirror = FindOp(mirror.RequestIdentity)!;
        var mirrorSnapshot = mirror;
        var ledgerEntry = ledger.Read().File!.Entries.Single(entry => entry.SubmissionIdentity == winner.SubmissionIdentity);
        var lease = ReadLease().File!.Lease!;
        var crashWindow = _lastStore!.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var restoredWinner = file.Handoff!.Operations.Single(operation => operation.RequestIdentity == winnerId);
            restoredWinner.RequestState = OperationRequestState.Accepted;
            restoredWinner.Zone = OperationZone.Active;
            restoredWinner.ExecutionResult = new ExecutionResult
            {
                Kind = ExecutionResultKind.Cancelled,
                RawTerminal = terminal.RawTerminal,
                JobId = ledgerEntry.JobId,
                EvidenceSource = terminal.EvidenceSource,
                SubmissionIdentity = winner.SubmissionIdentity,
                SendSeq = winner.LastSendSeq,
                ObservedAtUtc = _now,
            };
            restoredWinner.PendingTerminal = new PendingTerminal
            {
                Kind = ExecutionResultKind.Cancelled,
                RawTerminal = terminal.RawTerminal,
                JobId = ledgerEntry.JobId,
                EvidenceSource = terminal.EvidenceSource,
                SubmissionIdentity = winner.SubmissionIdentity,
                SendSeq = winner.LastSendSeq,
                OperationType = OperationType.ExternalStart,
                ObservedAtUtc = _now,
                RecordedAtUtc = _now,
            };
            var restoredMirror = file.Handoff.Operations.Single(operation => operation.RequestIdentity == mirrorSnapshot.RequestIdentity);
            restoredMirror.RequestState = OperationRequestState.Accepted;
            restoredMirror.Zone = OperationZone.Active;
            restoredMirror.SubmissionIdentity = restoredWinner.SubmissionIdentity;
            restoredMirror.LastSendSeq = restoredWinner.LastSendSeq;
            restoredMirror.PendingTerminal = null;
            restoredMirror.ExecutionResult = null;
            file.Handoff.Submission = null;
            return null;
        });
        Assert.True(crashWindow.Success, crashWindow.Reason);

        if (injectConflictAfterClassification)
        {
            hooks.BeforeRestartTerminalPersist = () =>
            {
                var latest = _lastStore!.Read().File!;
                var currentLease = latest.Lease!;
                var conflict = _lastStore.MutateHandoffLatest(currentLease.LeaseId, currentLease.OwnerEpoch, file =>
                {
                    var current = file.Handoff!.Operations.Single(operation => operation.RequestIdentity == winnerId);
                    current.ConflictPending = true;
                    current.ConflictEvidence ??= [];
                    current.ConflictEvidence.Add(new ConflictEvidenceRecord
                    {
                        EvidenceId = "accepted-receipt-arrived-after-classification",
                        RawTerminal = "accepted_receipt",
                        EvidenceSource = "fixture:late-acceptance",
                        ObservedAtUtc = _now,
                        SubmissionIdentity = current.SubmissionIdentity,
                        SendSeq = current.LastSendSeq,
                    });
                    return null;
                });
                Assert.True(conflict.Success, conflict.Reason);
            };
        }

        var recoveredCount = svc.RecoverAfterRestart();
        if (injectConflictAfterClassification)
        {
            var held = FindOp(winnerId)!;
            Assert.Equal(OperationRequestState.Accepted, held.RequestState);
            Assert.Equal(OperationZone.Active, held.Zone);
            Assert.True(held.ConflictPending);
            Assert.NotNull(held.PendingTerminal);
            Assert.NotNull(held.ExecutionResult);
            Assert.Equal(OperationRequestState.Accepted, FindOp(mirrorSnapshot.RequestIdentity)!.RequestState);
            return;
        }
        Assert.True(recoveredCount > 0);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(winnerId)!.RequestState);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(mirrorSnapshot.RequestIdentity)!.RequestState);
        Assert.NotEqual(OperationZone.Active, FindOp(mirrorSnapshot.RequestIdentity)!.Zone);
    }

    [Fact]
    public async Task SettleCompletion_CancelledAndFailed_MapResultDimension()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var cancelled = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.CancelledWith("cancelled", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, cancelled.Kind);
        Assert.Equal(ExecutionDisposition.Cancelled, cancelled.ExecutionDisposition);
        Assert.Equal(ResponsibilityState.Settled, cancelled.ResponsibilityState);
        Assert.Equal("cancelled", cancelled.RawTerminal);
        Assert.Equal(ExecutionResultKind.Cancelled, FindOp(rid)!.ExecutionResult!.Kind);

        // 台账：两条记录均转 Terminal，且保留各自原始终态词（不得被后续冲突改写）。
        Assert.Equal("cancelled", Assert.Single(ledger.Read().File!.Entries).TerminalEvidence);

        var (rid2, sub2, seq2) = await AcceptedExternalOpAsync(svc);
        var failed = await svc.SettleCompletionAsync(rid2, sub2, seq2,
            ExternalStartCompletion.ExecutionFailedWith("failed", "E_TASK_FAIL", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.ExecutionFailed, failed.Kind);
        Assert.Equal(ExecutionDisposition.ExecutionFailed, failed.ExecutionDisposition);
        Assert.Equal("E_TASK_FAIL", failed.ExecutionErrorCode);
        var op2 = FindOp(rid2)!;
        Assert.Equal(ExecutionResultKind.Failed, op2.ExecutionResult!.Kind);
        Assert.Equal("E_TASK_FAIL", op2.ExecutionResult.ExecutionErrorCode);
    }

    [Fact]
    public async Task SettleCompletion_StaleIdentity_LoudRejectWithoutStateChange()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion();
        var (rid, _, seq) = await AcceptedExternalOpAsync(svc);

        var stale = await svc.SettleCompletionAsync(rid, "sub:someone-else:1", seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Error, stale.Kind);
        Assert.Equal("stale_evidence", stale.ReasonCode); // 旧轮次/他人身份证据不得结算本笔责任
        Assert.Equal(OperationRequestState.Accepted, FindOp(rid)!.RequestState);
        Assert.Null(FindOp(rid)!.PendingTerminal);
    }

    [Fact]
    public async Task SettleCompletion_TerminalPersistNotConfigured_HoldsWithPendingTerminal()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion(noTerminalHooks: true);
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc); // 发送句柄＝job-1（受理时已并入接管台账）

        var held = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        // 结果维：已观察成功但结算未完成 ⇒ 待对账（**不得**因后续持久化失败改写成取消/失败，也不得冒充已结清）。
        Assert.Equal(AdmissionResultKind.NeedReconcile, held.Kind);
        Assert.Equal(ExecutionDisposition.Unknown, held.ExecutionDisposition);
        Assert.Equal("terminal_persist_not_configured", held.ReasonCode); // §24.3-5：钩子缺失＝保守停驻
        Assert.Equal(ResponsibilityState.Pending, held.ResponsibilityState);
        Assert.Equal("job-1", held.JobId); // [第五轮] 停驻返回同样必须携带合并后的有效句柄

        var op = FindOp(rid)!;
        Assert.NotNull(op.ExecutionResult);   // 第一段已提交（§24.12-7 合法中间态）
        Assert.NotNull(op.PendingTerminal);
        Assert.Equal(OperationRequestState.Accepted, op.RequestState); // 未终局、不释放占用、不重发
    }

    [Fact]
    public async Task Recovery_ResumesDurableTerminalWhenLedgerIsStillAcceptedPending()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: false, noTerminalHooks: true);
        var hooks = _lastHooks!;
        var (requestIdentity, submissionIdentity, sendSeq) = await AcceptedExternalOpAsync(svc);
        var observedAt = _now.AddSeconds(-3);
        var held = await svc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq,
            ExternalStartCompletion.SucceededWith("completed", "ext:task.event", observedAt, "job-1"));
        Assert.Equal("terminal_persist_not_configured", held.ReasonCode);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution, ledger.Read().File!.Entries.Single().State);

        hooks.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, error, job, source, kind) =>
        {
            var result = ledger.MarkTerminal(sub, seq, evidence, observed, raw, error,
                OperationType.ExternalStart, job, source, kind);
            return result.Success ? null : result.Reason;
        };
        hooks.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, error, job, source, observed, kind) =>
        {
            var item = ledger.Read().File?.Entries.SingleOrDefault(e => e.SubmissionIdentity == sub && e.SendSeq == seq);
            return item is { State: LedgerEntryState.Terminal }
                && item.RawTerminal == raw && item.ExecutionErrorCode == error && item.JobId == job
                && item.TerminalEvidenceSource == source && item.TerminalObservedAtUtc == observed
                && item.TerminalKind == kind;
        };
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(submissionIdentity, sendSeq, Terminal: false, JobId: "job-1",
                EvidenceSource: "ext:task.event", OperationType: OperationType.ExternalStart)]);

        var recovery = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, recovery.TerminalizationCompleted);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(requestIdentity)!.RequestState);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
        Assert.Equal(LedgerEntryState.Terminal, ledger.Read().File!.Entries.Single().State);
    }

    [Fact]
    public async Task Recovery_DoesNotFinalizeCarrierChangedAfterClassification()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: false, noTerminalHooks: true);
        var hooks = _lastHooks!;
        var (requestIdentity, submissionIdentity, sendSeq) = await AcceptedExternalOpAsync(svc);
        var observedAt = _now.AddSeconds(-3);
        var held = await svc.SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq,
            ExternalStartCompletion.SucceededWith("completed", "ext:task.event", observedAt, "job-1"));
        Assert.Equal("terminal_persist_not_configured", held.ReasonCode);

        hooks.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, error, job, source, kind) =>
        {
            var result = ledger.MarkTerminal(sub, seq, evidence, observed, raw, error,
                OperationType.ExternalStart, job, source, kind);
            return result.Success ? null : result.Reason;
        };
        hooks.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, error, job, source, observed, kind) =>
        {
            var item = ledger.Read().File?.Entries.SingleOrDefault(e => e.SubmissionIdentity == sub && e.SendSeq == seq);
            return item is { State: LedgerEntryState.Terminal }
                && item.RawTerminal == raw && item.JobId == job && item.TerminalEvidenceSource == source
                && item.TerminalObservedAtUtc == observed && item.TerminalKind == kind;
        };
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(submissionIdentity, sendSeq, Terminal: false, JobId: "job-1",
                EvidenceSource: "ext:task.event", OperationType: OperationType.ExternalStart)]);
        hooks.Barriers ??= new AdmissionBarriers();
        hooks.Barriers.BeforeTerminalFinalize = () =>
        {
            var lease = ReadLease().File!.Lease!;
            var mutate = _lastStore!.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op = file.Handoff!.Operations.Single(o => o.RequestIdentity == requestIdentity);
                op.ExecutionResult!.RawTerminal = "changed-after-classification";
                op.PendingTerminal!.RawTerminal = "changed-after-classification";
                return null;
            });
            Assert.True(mutate.Success, mutate.Reason);
            return Task.CompletedTask;
        };

        var recovery = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, recovery.TerminalizationCompleted);
        Assert.Equal(1, recovery.TerminalizationFailed);
        var current = FindOp(requestIdentity)!;
        Assert.NotEqual(OperationRequestState.TerminalCompleted, current.RequestState);
        Assert.Equal("changed-after-classification", current.ExecutionResult!.RawTerminal);
        Assert.NotNull(current.PendingTerminal);
    }

    [Fact]
    public async Task SettleCompletion_NonExternalOperationType_FailClosed()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        // 同名入口但操作类型非外部启动（模拟节点/流程操作误用完成结算入口）。
        var req = Req(ns: "manual", workflow: "flow:cfg", payload: "p-node");
        req.OperationType = OperationType.NodeExecution;
        req.Candidate!.NodeId = "n-cfg";
        req.Candidate.ResourceRef = "node:n-cfg";   // 节点类型的来源引用必须一致（生产接管校验口径）
        var accepted = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var refused = await svc.SettleCompletionAsync(accepted.RequestIdentity, accepted.SubmissionIdentity!, accepted.SendSeq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Error, refused.Kind);
        Assert.Equal("operation_type_not_external_start", refused.ReasonCode);
        // 非外部类型**不得写终态副本**（受理接管由通用夹具落盘，但终态/关闭/终局一概不得发生）。
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution,
            Assert.Single(ledger.Read().File!.Entries).State);
        Assert.Equal(OperationRequestState.Accepted, FindOp(accepted.RequestIdentity)!.RequestState); // 不释放占用
    }

    [Fact]
    public async Task SettleCompletion_TerminalConflictWithExistingFact_NotOverwritten()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var cancelled = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.CancelledWith("cancelled", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, cancelled.Kind);

        // 同一发送轮次再来一个**不同**终态：不得覆盖既有事实（冲突/损坏 fail-closed，且不写第二次台账）。
        var conflict = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.NeedReconcile, conflict.Kind);
        Assert.Equal("conflict_pending", conflict.ReasonCode); // 冲突终态已持久化：不覆盖、不重放、不释放占用（§24.13-3）
        Assert.Equal(ResponsibilityState.Pending, conflict.ResponsibilityState); // 冲突断言需权威裁决
        var conflicted = FindOp(rid)!;
        Assert.True(conflicted.ConflictPending);
        Assert.Single(conflicted.ConflictEvidence!);
        Assert.Equal(ExecutionResultKind.Cancelled, conflicted.ExecutionResult!.Kind);
        Assert.Equal("cancelled", Assert.Single(ledger.Read().File!.Entries).TerminalEvidence); // 台账仍为首次终态

        var replay = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.CancelledWith("cancelled", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.NeedReconcile, replay.Kind);
        Assert.Equal(ResponsibilityState.Pending, replay.ResponsibilityState);
        Assert.True(FindOp(rid)!.ConflictPending);

        // 同轮迟到 Accepted 只是额外回执证据；有匹配的权威同轮终态审计后，必须允许解除 pending 并保留证据。
        var lease = ReadLease().File!.Lease!;
        Assert.True(_lastStore!.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == rid).ConflictEvidence!.Add(new ConflictEvidenceRecord
            {
                EvidenceId = "accepted-receipt-fixture:" + seq,
                RawTerminal = "accepted_receipt",
                EvidenceSource = "sender:late-accepted",
                ObservedAtUtc = _now,
                SubmissionIdentity = sub,
                SendSeq = seq,
            });
            return null;
        }).Success);

        var adjudicated = await svc.AdjudicateConflictAsync(rid, ConflictResolutionKind.ResolvedAcceptedTerminal,
            "owner:terminal-audit", ExternalStartCompletion.CancelledWith("cancelled", "ext:watch", _now));
        Assert.Equal(AdmissionResultKind.Cancelled, adjudicated.Kind);
        Assert.Equal(ResponsibilityState.Settled, adjudicated.ResponsibilityState);
        var settled = FindOp(rid)!;
        Assert.False(settled.ConflictPending, $"result={adjudicated.ReasonCode} claim={settled.ConflictAdjudicationClaim} audit={settled.ConflictResolutionAuditId}");
        Assert.Equal(ExecutionResultKind.Cancelled, settled.ExecutionResult!.Kind);
        var audit = Assert.Single(ReadLease().File!.Handoff!.ConflictResolutionAudits!);
        Assert.Null(audit.SupersededRejectedResultSnapshot);
        Assert.Equal(ExecutionResultKind.Cancelled, audit.SupersededExecutionResultSnapshot!.Kind);
        Assert.Equal(ArbitrationLeaseStatus.Valid, ReadLease().Status);
        Assert.Contains(settled.ConflictEvidence!, e => e.RawTerminal == "accepted_receipt");
    }

    [Fact]
    public async Task SettleCompletion_TerminalConflictEvidence_PreservesJobIdAndTypedPayload()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion();
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);
        var first = ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now, "job-1");
        Assert.Equal(AdmissionResultKind.Accepted,
            (await svc.SettleCompletionAsync(rid, sub, seq, first)).Kind);

        // All visible words/source/time match; only the remote task handle differs.
        var contradictory = ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now, "job-other");
        var conflict = await svc.SettleCompletionAsync(rid, sub, seq, contradictory);
        Assert.Equal(AdmissionResultKind.NeedReconcile, conflict.Kind);
        var op = FindOp(rid)!;
        Assert.True(op.ConflictPending);
        var evidence = Assert.Single(op.ConflictEvidence!);
        var snapshot = Assert.IsType<ExecutionResult>(evidence.ConflictingExecutionResultSnapshot);
        Assert.Equal(ExecutionResultKind.Succeeded, snapshot.Kind);
        Assert.Equal("completed", snapshot.RawTerminal);
        Assert.Equal("job-other", snapshot.JobId);
        Assert.Equal(sub, snapshot.SubmissionIdentity);
        Assert.Equal(seq, snapshot.SendSeq);
        Assert.Equal("job-1", op.ExecutionResult!.JobId);
        Assert.Equal(ArbitrationLeaseStatus.Valid, ReadLease().Status);
    }

    [Fact]
    public async Task SettleReconciled_WithTerminalCompletion_RoutesToCompletionSettlement()
    {
        // **真实对账场景**：发送结果未知 ⇒ Reconciling（Submission 未关闭、台账无受理记录），
        // 随后 owner 对账确认「曾受理」并携带权威终态 ⇒ 必须先补受理接管，再按 §24.15 完成唯一顺序结算。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true);
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext");
        req.OperationType = OperationType.ExternalStart;
        var unknown = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission); // 发送未关闭
        Assert.Empty(new ExternalStartLedger(_dir, () => _now).GetOccupancy().Entries); // 台账尚无受理记录

        var settled = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:reconcile_query", null, "job-reconciled",
                ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_query", _now)));

        Assert.NotEqual("completion_settlement_not_implemented", settled.ReasonCode);
        Assert.Equal(ResponsibilityState.Settled, settled.ResponsibilityState);
        // 受理接管确实补上了台账记录，且终态副本已写、句柄来自对账（不丢字段），终局后不再占用。
        var entry = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(LedgerEntryState.Terminal, entry.State);
        Assert.Equal("job-reconciled", entry.JobId);
        Assert.Empty(ledger.GetOccupancy().Entries);
        var op = FindOp(unknown.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    [Fact]
    public async Task MarkOperationTerminal_ExternalStart_RefusesBypass()
    {
        var (svc, _) = BuildExternalFacadeWithCompletion();
        var (rid, _, _) = await AcceptedExternalOpAsync(svc);

        var bypass = svc.MarkOperationTerminal(rid, "bgi:job_terminal");
        Assert.Equal(AdmissionResultKind.Error, bypass.Kind);
        Assert.Equal("external_start_requires_completion_settlement", bypass.ReasonCode);
        Assert.Equal(OperationRequestState.Accepted, FindOp(rid)!.RequestState); // 不得旁路释放占用
    }

    [Fact]
    public async Task SettleCompletion_NullWhenAcceptanceFails_DoesNotReportPlainAcceptance()
    {
        // 发送未知 ⇒ Reconciling（未关闭）；接管台账落盘失败 ⇒ 普通受理**未完成**，不得报「已受理」。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true, acceptanceFails: true);
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext-fail");
        req.OperationType = OperationType.ExternalStart;
        var unknown = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);

        var held = await svc.SettleCompletionAsync(unknown.RequestIdentity, unknown.SubmissionIdentity!, unknown.SendSeq, null);
        Assert.NotEqual(AdmissionResultKind.Accepted, held.Kind);                       // 不得冒充「普通受理已完成」
        Assert.Equal(ResponsibilityState.Pending, held.ResponsibilityState);
        Assert.Equal(OperationRequestState.Reconciling, FindOp(unknown.RequestIdentity)!.RequestState); // 保守停驻
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);                          // Submission 未被误关
        Assert.True(ledger.Read().File is null);                                        // 接管落盘失败 ⇒ 台账无记录
    }

    [Fact]
    public async Task SettleCompletion_TerminalPersistFailedWithMismatchedLedgerPayload_Stops()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion(terminalPersistFailsWithMismatchedPayload: true);
        var (rid, sub, seq) = await AcceptedExternalOpAsync(svc);

        var held = await svc.SettleCompletionAsync(rid, sub, seq,
            ExternalStartCompletion.SucceededWith("completed", "ext:watch", _now));
        // 台账确有一条终态记录，但其载荷与本次事实**不符**（生产式逐字段读回不确认）⇒ 不得继续关闭/终局。
        Assert.Equal(AdmissionResultKind.NeedReconcile, held.Kind);
        Assert.StartsWith("terminal_persist_unconfirmed", held.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, held.ResponsibilityState);
        var op = FindOp(rid)!;
        Assert.Equal(OperationRequestState.Accepted, op.RequestState); // 未终局、不释放占用
        Assert.NotNull(op.PendingTerminal);
        var mismatched = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(LedgerEntryState.Terminal, mismatched.State);
        Assert.EndsWith("-mismatch", mismatched.TerminalEvidence);      // 台账载荷不同 ⇒ 门面据此停驻
    }

    [Fact]
    public async Task SettleCompletion_RetryAfterUnconfirmedReadBack_BackfillsJobIdIntoCarriers()
    {
        // 两阶段：①首次读回不确认 ⇒ 停驻（载体句柄为空、台账暂无句柄）；
        //        ②对账带来句柄 ⇒ 接管合并句柄 → 载体**补齐**句柄 → 台账确认 → 终局（全链句柄一致）。
        var (svc, ledger) = BuildExternalFacadeWithCompletion(senderUnknown: true, ledgerConfirmFailsOnce: true);
        var req = Req(ns: "manual", workflow: "onedragon:cfg", payload: "p-ext-retry");
        req.OperationType = OperationType.ExternalStart;
        var unknown = await svc.SubmitAsync(req);
        Assert.Equal(AdmissionResultKind.Reconciling, unknown.Kind);

        var first = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:reconcile_1", null, null,
                ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_1", _now)));
        Assert.Equal(ResponsibilityState.Pending, first.ResponsibilityState); // 读回不确认 ⇒ 保守停驻
        Assert.Equal(OperationRequestState.Reconciling, FindOp(unknown.RequestIdentity)!.RequestState);

        var second = await svc.SettleReconciledAsync(unknown.RequestIdentity,
            // 同一观测事实重试（证据来源/原始终态词不变），只是这次带来了远端句柄 ⇒ 载体按合并值补齐句柄。
            new ReconcileSettlement.Accepted(unknown.SubmissionIdentity!, unknown.SendSeq, "owner:reconcile_1", null, "job-r2",
                ExternalStartCompletion.SucceededWith("completed", "owner:reconcile_1", _now)));
        Assert.Equal(ResponsibilityState.Settled, second.ResponsibilityState);
        Assert.Equal("job-r2", second.JobId);
        var op = FindOp(unknown.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState);
        Assert.Equal("job-r2", op.ExecutionResult!.JobId);   // 载体句柄已按台账合并值**补齐**
        Assert.Equal("job-r2", Assert.Single(ledger.Read().File!.Entries).JobId);
        Assert.Null(ReadLease().File!.Handoff!.Submission);
    }

    /// <summary>
    /// **锁外发送期间换主（发送身份未变）**：租约被他人接管后，旧层迟到的 `Accepted` 只按本轮身份写入共享台账；
    /// 旧层不得关闭 Submission 或改 Operation。当前所有者恢复扫描后接纳此回执并关闭发送占位。
    /// </summary>
    [Fact]
    public async Task ExternalStartSend_OwnershipChangedDuringSend_LateAcceptIsAdoptedByCurrentOwner()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, store, ledger, hooks) = BuildFacade(h => h.Sender = async _ =>
        {
            inSend.TrySetResult();
            await releaseSend.Task;
            return new SendOutcome.Accepted("ext:accepted", null);
        });

        var submit = svc.SubmitAsync(Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart));
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 锁外期间：换主（单调观察满 TTL＋锁内复核，§6.3 唯一接管依据）——发送身份保持不变。
        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(20);
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence);
        Assert.True(store.TryAcquire("pid:other", evidence: evidence).Success);

        releaseSend.TrySetResult();
        var result = await submit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AdmissionResultKind.NeedReconcile, result.Kind);
        Assert.Equal("late_acceptance_receipt_saved", result.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        var receipt = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(result.SubmissionIdentity, receipt.SubmissionIdentity);
        Assert.Equal(result.SendSeq, receipt.SendSeq);
        Assert.Equal("ext:accepted", receipt.EvidenceSource);
        Assert.Equal(OperationType.ExternalStart, receipt.OperationType);
        Assert.Equal(OperationRequestState.Granted, FindOp(result.RequestIdentity!)!.RequestState); // 旧所有者只记事实
        Assert.NotNull(store.Read().File!.Handoff!.Submission); // 旧所有者未关闭占位
        var staleCompletion = await svc.SettleCompletionAsync(result.RequestIdentity!, result.SubmissionIdentity!, result.SendSeq,
            null, expectedOwnerLeaseId: result.CapturedLeaseId, expectedOwnerEpoch: result.CapturedOwnerEpoch);
        Assert.Equal("lease_stale_generation", staleCompletion.ReasonCode); // 旧完成观察不能借新租约
        Assert.NotNull(store.Read().File!.Handoff!.Submission);

        // 新所有者从共享逐轮台账读回事实，按正常受理结算关闭占位，不再次发送。
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true, ledger.Read().File!.Entries
            .Select(e => new TakeoverLedgerFact(e.SubmissionIdentity, e.SendSeq,
                Terminal: e.State == LedgerEntryState.Terminal, JobId: e.JobId,
                AcceptedReceipt: true, EvidenceSource: e.EvidenceSource, AcceptedAtUtc: e.AcceptedAtUtc,
                RunId: e.RunId, OperationType: e.OperationType)).ToList());
        var currentOwner = new ArbitrationAdmissionService(store, hooks, () => _now);
        var recovered = await currentOwner.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, recovered.AcceptanceReceiptsAdopted);
        Assert.Equal(OperationRequestState.Accepted, FindOp(result.RequestIdentity!)!.RequestState);
        Assert.Null(store.Read().File!.Handoff!.Submission);
        Assert.Single(ledger.Read().File!.Entries);
    }

    [Fact]
    public async Task HistoricalAcceptedReceiptCannotBeClearedByCurrentRoundNotAcceptedAdjudication()
    {
        var sends = 0;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                sends++;
                return Task.FromResult<SendOutcome>(new SendOutcome.Rejected("not_accepted", true, "owner:reconcile"));
            };
            h.NotAcceptedObservationVerifier = _ => null;
        });
        var request = Req(ns: "v2", workflow: "group:historical-late-accept", operationType: OperationType.ExternalStart);
        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        var requestIdentity = first.RequestIdentity;
        var firstOp = FindOp(requestIdentity)!;
        var oldSubmissionIdentity = firstOp.SubmissionIdentity;
        Assert.Equal(1, firstOp.LastSendSeq);

        var second = await svc.RetryAsync(requestIdentity);
        Assert.Equal(AdmissionResultKind.RetryableRejected, second.Kind);
        var current = FindOp(request.RequestIdentity)!;
        Assert.Equal(2, current.LastSendSeq);
        var lateReceipt = new ExternalStartLedgerEntry
        {
            SubmissionIdentity = oldSubmissionIdentity,
            SendSeq = 1,
            CandidateId = current.CandidateId,
            ResourceRef = current.ResourceRef ?? "",
            ActionId = current.Candidate!.ActionId ?? ArbitrationOrdering.DeriveActionId(current.CandidateId),
            TargetBgiEpoch = current.TargetEpoch,
            AcceptedAtUtc = _now,
            EvidenceSource = "owner:late-round-1",
            State = LedgerEntryState.AcceptedPendingExecution,
            RunId = current.RunBinding,
            JobId = "job-old-round",
            OperationType = OperationType.ExternalStart,
        };
        Assert.True(ledger.RecordAccepted(lateReceipt).Success);
        Assert.True(ledger.ConfirmRebuildable(lateReceipt));
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true, ledger.Read().File!.Entries
            .Select(e => new TakeoverLedgerFact(e.SubmissionIdentity, e.SendSeq,
                Terminal: e.State == LedgerEntryState.Terminal, JobId: e.JobId,
                AcceptedReceipt: true, EvidenceSource: e.EvidenceSource, AcceptedAtUtc: e.AcceptedAtUtc,
                RunId: e.RunId, OperationType: e.OperationType,
                TerminalEvidence: e.TerminalEvidence, RawTerminal: e.RawTerminal, ExecutionErrorCode: e.ExecutionErrorCode,
                TerminalObservedAtUtc: e.TerminalObservedAtUtc, TerminalEvidenceSource: e.TerminalEvidenceSource,
                TerminalKind: e.TerminalKind)).ToList());
        hooks.TakeoverTerminalPayloadConfirmed = (submissionIdentity, sendSeq, rawTerminal, executionErrorCode,
            jobId, evidenceSource, observedAt, terminalKind) =>
        {
            var entry = ledger.Read().File?.Entries.FirstOrDefault(e => e.SubmissionIdentity == submissionIdentity && e.SendSeq == sendSeq);
            return entry is { State: LedgerEntryState.Terminal }
                   && entry.TerminalEvidence == rawTerminal && entry.RawTerminal == rawTerminal
                   && entry.ExecutionErrorCode == executionErrorCode && entry.JobId == jobId
                   && entry.TerminalEvidenceSource == evidenceSource && entry.TerminalObservedAtUtc == observedAt
                   && entry.TerminalKind == terminalKind;
        };

        var recovered = await svc.RecoverExternalStartObservationsAsync();
        Assert.Equal(1, recovered.HistoricalAcceptanceReceiptsHeld);
        var held = store.Read().File?.Handoff?.Operations.FirstOrDefault(o => o.RequestIdentity == requestIdentity);
        Assert.True(held is not null, "request=" + requestIdentity + "; status=" + store.Read().Status
            + "; operations=" + string.Join(",", store.Read().File?.Handoff?.Operations?.Select(o => o.RequestIdentity + "/" + o.Zone + "/" + o.RequestState) ?? []));
        Assert.True(held!.ConflictPending);
        Assert.Equal("AcceptedAwaitingTerminal", held.ConflictResolutionState);
        Assert.Contains(held.ConflictEvidence!, e => e.SubmissionIdentity == oldSubmissionIdentity && e.SendSeq == 1
            && e.RawTerminal == "accepted_receipt" && e.JobId == "job-old-round");
        var earlyAdjudication = await svc.AdjudicateConflictAsync(requestIdentity,
            ConflictResolutionKind.ResolvedNotAccepted, "owner:round-2-check",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted,
                "rejected", "owner:round-2-check", _now, held!.SubmissionIdentity, held.LastSendSeq));
        Assert.Equal("historical_acceptance_receipt_pending", earlyAdjudication.ReasonCode);
        var terminalObservedAt = _now.AddSeconds(5);
        Assert.True(ledger.MarkTerminal(oldSubmissionIdentity, 1, "completed", terminalObservedAt,
            rawTerminal: "completed", operationType: OperationType.ExternalStart, jobId: "job-old-round",
            terminalEvidenceSource: "observer:job-old-round", terminalKind: ExecutionResultKind.Succeeded).Success);
        var terminalRecovery = await svc.RecoverExternalStartObservationsAsync();
        Assert.True(terminalRecovery.HistoricalAcceptanceReceiptsHeld == 2, terminalRecovery.ToString());
        Assert.Equal(1, terminalRecovery.HistoricalAcceptanceTerminalsFinalized);
        var terminalHeld = FindOp(requestIdentity)!;
        Assert.False(terminalHeld.ConflictPending);
        Assert.Equal("ResolvedHistoricalAcceptedTerminal", terminalHeld.ConflictResolutionState);
        Assert.Equal(OperationRequestState.TerminalCompleted, terminalHeld.RequestState);
        Assert.True(terminalHeld.Zone is OperationZone.TerminalPendingTransfer or OperationZone.Tombstone,
            "终态可在同一事务的 MigrateAndClean 中迁入墓碑；两种状态都已结清而非占槽待决。");
        Assert.Equal(oldSubmissionIdentity, terminalHeld.ExecutionResult!.SubmissionIdentity);
        Assert.Equal(1, terminalHeld.ExecutionResult.SendSeq);
        Assert.Equal(ExecutionResultKind.Succeeded, terminalHeld.ExecutionResult.Kind);
        Assert.Contains(terminalHeld.ConflictEvidence!, e => e.SubmissionIdentity == oldSubmissionIdentity && e.SendSeq == 1
            && e.RawTerminal == "completed" && e.JobId == "job-old-round"
            && e.EvidenceSource == "observer:job-old-round" && e.ObservedAtUtc == terminalObservedAt
            && e.ConflictingExecutionResultSnapshot?.Kind == ExecutionResultKind.Succeeded);
        var historicalAudit = Assert.Single(store.Read().File!.Handoff!.ConflictResolutionAudits!
            .Where(a => a.Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal));
        Assert.Equal(oldSubmissionIdentity, historicalAudit.SubmissionIdentity);
        Assert.Equal(1, historicalAudit.SendSeq);
        Assert.Equal(2, historicalAudit.RelatedCurrentRoundRejectedResultSnapshot!.AnsweredSendSeq);
        Assert.Equal(terminalHeld.LastResult!.ReasonCode, historicalAudit.RelatedCurrentRoundRejectedResultSnapshot.ReasonCode);
        var adjudication = await svc.AdjudicateConflictAsync(requestIdentity,
            ConflictResolutionKind.ResolvedNotAccepted, "owner:round-2-check",
            notAccepted: new NotAcceptedObservation(ReconciledNotAcceptedFactKinds.ReconcileQueryNotAccepted,
                "rejected", "owner:round-2-check", _now, terminalHeld.SubmissionIdentity, terminalHeld.LastSendSeq));
        Assert.Equal("historical_acceptance_receipt_pending", adjudication.ReasonCode);
        var repeatedScan = await svc.RecoverExternalStartObservationsAsync();
        Assert.False(FindOp(requestIdentity)!.ConflictPending);
        Assert.Equal("ResolvedHistoricalAcceptedTerminal", FindOp(requestIdentity)!.ConflictResolutionState);
        Assert.Single(store.Read().File!.Handoff!.ConflictResolutionAudits!
            .Where(a => a.Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal));
        var retry = await svc.RetryAsync(requestIdentity);
        Assert.Equal(ResponsibilityState.Settled, retry.ResponsibilityState);
        Assert.Equal(2, FindOp(requestIdentity)!.LastSendSeq);
        Assert.Equal(2, sends);
        Assert.Null(store.Read().File!.Handoff!.Submission);

        // 写侧校验拒绝会令后续读取损坏的候选状态；原租约与历史轮终态审计必须保持有效。
        var lease = store.Read().File!.Lease!;
        var invalidMutation = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == requestIdentity).ConflictResolutionState = null;
            return null;
        });
        Assert.False(invalidMutation.Success);
        Assert.Equal("invalid_mutation_state", invalidMutation.Reason);
        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);
        Assert.Equal("ResolvedHistoricalAcceptedTerminal", FindOp(requestIdentity)!.ConflictResolutionState);

        var nullRequiredSet = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.PreObservations = null!;
            return null;
        });
        Assert.False(nullRequiredSet.Success);
        Assert.Equal("invalid_mutation_state", nullRequiredSet.Reason);
        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);

        var invalidLease = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Lease!.TtlSeconds = 0;
            return null;
        });
        Assert.False(invalidLease.Success);
        Assert.Equal("invalid_mutation_state", invalidLease.Reason);
        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);
    }

    [Fact]
    public async Task LateAcceptanceFromRoundOneAfterRoundTwoCannotOverwriteCurrentClaim()
    {
        var firstSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var (oldOwnerService, store, ledger, hooks) = BuildFacade(h => h.Sender = async _ =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstSendEntered.TrySetResult();
                await releaseFirstSend.Task;
                return new SendOutcome.Accepted("owner:late-round-1", null, "job-round-1");
            }
            return new SendOutcome.Rejected("not_accepted", false, "owner:round-reconcile");
        });
        hooks.NotAcceptedObservationVerifier = _ => null;
        var request = Req(ns: "v2", workflow: "group:late-round-1-after-round-2", operationType: OperationType.ExternalStart);
        var firstSend = oldOwnerService.SubmitAsync(request);
        await firstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstSubmission = store.Read().File!.Handoff!.Submission!;

        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(20);
        var takeoverEvidence = observer.Observe(store.Read());
        Assert.NotNull(takeoverEvidence);
        Assert.True(store.TryAcquire("pid:late-round-owner", evidence: takeoverEvidence).Success);
        var currentOwnerService = new ArbitrationAdmissionService(store, hooks, () => _now);

        var rejectedRoundOne = await currentOwnerService.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(firstSubmission.SubmissionIdentity, firstSubmission.SendSeq,
                "not_accepted", Retryable: true, "owner:round-1-reconcile"));
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejectedRoundOne.Kind);
        var rejectedRoundTwo = await currentOwnerService.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.TerminalRejected, rejectedRoundTwo.Kind);
        Assert.Equal(2, FindOp(request.RequestIdentity)!.LastSendSeq);
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(request.RequestIdentity)!.RequestState);

        releaseFirstSend.TrySetResult();
        var lateRoundOne = await firstSend.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("late_acceptance_receipt_saved", lateRoundOne.ReasonCode);
        var receipt = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(firstSubmission.SubmissionIdentity, receipt.SubmissionIdentity);
        Assert.Equal(1, receipt.SendSeq);
        Assert.Equal("job-round-1", receipt.JobId);

        var currentOperationBeforeArchive = FindOp(request.RequestIdentity)!;
        Assert.Equal(2, currentOperationBeforeArchive.LastSendSeq);
        Assert.False(currentOperationBeforeArchive.ConflictPending); // 旧发送者只追加台账，冲突由当前 owner 的恢复扫描登记

        // 归档发生在旧回执已进入共享台账、但当前 owner 尚未扫描这条事实时。
        _now += TimeSpan.FromHours(25);
        RenewLease(store);
        await currentOwnerService.SubmitAsync(Req(ns: "archive-trigger", workflow: "group:archive-trigger"));
        Assert.Contains(store.Read().File!.Handoff!.ArchivedOperations,
            archived => archived.Operation.RequestIdentity == request.RequestIdentity);

        var archivedBeforeRecovery = Assert.Single(store.Read().File!.Handoff!.ArchivedOperations,
            archived => archived.Operation.RequestIdentity == request.RequestIdentity);
        Assert.Equal(2, archivedBeforeRecovery.Operation.LastSendSeq);
        Assert.False(archivedBeforeRecovery.Operation.ConflictPending); // 旧发送者只能追加台账，不能改当前任务
        Assert.Null(store.Read().File!.Handoff!.Submission);

        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true, ledger.Read().File!.Entries
            .Select(entry => new TakeoverLedgerFact(entry.SubmissionIdentity, entry.SendSeq,
                Terminal: entry.State == LedgerEntryState.Terminal, JobId: entry.JobId,
                AcceptedReceipt: true, EvidenceSource: entry.EvidenceSource, AcceptedAtUtc: entry.AcceptedAtUtc,
                RunId: entry.RunId, OperationType: entry.OperationType,
                TerminalEvidence: entry.TerminalEvidence, RawTerminal: entry.RawTerminal,
                ExecutionErrorCode: entry.ExecutionErrorCode, TerminalObservedAtUtc: entry.TerminalObservedAtUtc,
                TerminalEvidenceSource: entry.TerminalEvidenceSource, TerminalKind: entry.TerminalKind)).ToList());
        var recovery = await currentOwnerService.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, recovery.OrphanLedgerEntries);
        Assert.Equal(1, recovery.HistoricalAcceptanceReceiptsHeld);
        Assert.DoesNotContain(store.Read().File!.Handoff!.ArchivedOperations,
            archived => archived.Operation.RequestIdentity == request.RequestIdentity);
        var op = FindOp(request.RequestIdentity)!;
        Assert.Equal(2, op.LastSendSeq);
        Assert.Equal(OperationZone.TerminalPendingTransfer, op.Zone); // 当前 owner 重新占槽并等待核查
        Assert.True(op.ConflictPending);
        Assert.Contains(op.ConflictEvidence!, evidence => evidence.SubmissionIdentity == firstSubmission.SubmissionIdentity
            && evidence.SendSeq == 1 && evidence.RawTerminal == "accepted_receipt");
        var retry = await currentOwnerService.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retry.Kind);
        Assert.Equal(3, sends);
    }

    [Fact]
    public async Task AcceptedReceiptIsSavedWhenOwnershipChangesImmediatelyBeforeClaim()
    {
        var sends = 0;
        var (svc, store, ledger, hooks) = BuildFacade(h => h.Sender = _ =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("owner:accepted", null, "job-race"));
        });
        var ownershipChanged = false;
        hooks.BeforeAcceptanceClaim = () =>
        {
            var observer = new LeaseTakeoverObserver(() => _mono);
            Assert.Null(observer.Observe(store.Read()));
            _mono += TimeSpan.FromSeconds(20);
            var evidence = observer.Observe(store.Read());
            Assert.NotNull(evidence);
            Assert.True(store.TryAcquire("pid:new-owner", evidence: evidence).Success);
            ownershipChanged = true;
            return Task.CompletedTask;
        };

        var result = await svc.SubmitAsync(Req(ns: "v2", workflow: "group:claim-takeover-race",
            operationType: OperationType.ExternalStart));

        Assert.True(ownershipChanged);
        Assert.Equal(1, sends);
        var receipt = Assert.Single(ledger.Read().File!.Entries);
        Assert.Equal(result.SubmissionIdentity, receipt.SubmissionIdentity);
        Assert.Equal(result.SendSeq, receipt.SendSeq);
        Assert.Equal("owner:accepted", receipt.EvidenceSource);
        Assert.Equal("job-race", receipt.JobId);
        Assert.NotNull(store.Read().File!.Handoff!.Submission);
        Assert.NotEqual(OperationRequestState.Accepted, FindOp(result.RequestIdentity!)!.RequestState);
    }

    /// <summary>
    /// **锁外发送后的责任归属复核（§24.18-3/4）**：发送窗口内本笔发送责任被其他入口推进（夹具直接改写本轮
    /// `Submission.SendSeq` 模拟换主恢复/对账/轮次改写）⇒ 迟到的 `Accepted` **不得**写接管台账、不得关闭、
    /// 不得释放占用，必须保守停驻（`NeedReconcile`／责任 `Pending`／禁止重发）。
    /// </summary>
    [Fact]
    public async Task ExternalStartSend_ReconcilingSameRound_LateAcceptPersistsAndCloses()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, store, ledger, _) = BuildFacade(h => h.Sender = async _ =>
        {
            inSend.TrySetResult();
            await releaseSend.Task;
            return new SendOutcome.Accepted("ext:accepted", null);
        });

        var submit = svc.SubmitAsync(Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart));
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 锁外期间：另一入口把本笔操作转为 Reconciling，但发送身份保持不变。Sender 随后返回的受理
        // 是同一发送轮的权威事实，必须继续落账并关闭，不能因状态标签变化而丢弃。
        var read = store.Read();
        var lease = read.File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.First().RequestState = OperationRequestState.Reconciling;
            return null;
        }).Success);

        releaseSend.TrySetResult();
        var result = await submit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        Assert.Equal(OperationRequestState.Accepted, FindOp(result.RequestIdentity!)!.RequestState);
        Assert.Null(store.Read().File!.Handoff!.Submission);
        Assert.Single(ledger.Read().File!.Entries);
    }

    [Fact]
    public async Task ExternalStartLateAcceptanceAfterRetryableRejection_PersistsConflictAndBlocksAnotherRetry()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, store, ledger, hooks) = BuildFacade(h => h.Sender = async _ =>
        {
            inSend.TrySetResult();
            await releaseSend.Task;
            return new SendOutcome.Accepted("owner:late-accepted", "run-late", "job-late");
        });
        hooks.NotAcceptedObservationVerifier = _ => null;
        var concurrentSvc = new ArbitrationAdmissionService(store, hooks, () => _now);
        var request = Req(ns: "v2", workflow: "group:late-accept", operationType: OperationType.ExternalStart);
        var submit = svc.SubmitAsync(request);
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.Read().File!;
        var oldSubmission = snapshot.Handoff!.Submission!;

        var rejected = await concurrentSvc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(oldSubmission.SubmissionIdentity, oldSubmission.SendSeq,
                "retryable_rejection", Retryable: true, "owner:reconcile"));
        Assert.Equal(AdmissionResultKind.RetryableRejected, rejected.Kind);

        releaseSend.TrySetResult();
        var lateAccepted = await submit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AdmissionResultKind.Reconciling, lateAccepted.Kind);
        var op = FindOp(request.RequestIdentity)!;
        Assert.True(op.ConflictPending);
        Assert.Equal(oldSubmission.SubmissionIdentity, op.AcceptanceClaim!.SubmissionIdentity);
        Assert.Equal(oldSubmission.SendSeq, op.AcceptanceClaim.SendSeq);
        Assert.True(op.AcceptanceClaim.LedgerPersisted);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution, Assert.Single(ledger.Read().File!.Entries).State);
        var retry = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retry.Kind);
        Assert.Equal(oldSubmission.SendSeq, FindOp(request.RequestIdentity)!.LastSendSeq);
    }

    [Fact]
    public async Task AcceptanceClaimBeforeLedger_RacingNotAcceptedKeepsSubmissionOpenForAdjudication()
    {
        var atLedger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLedger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("owner:accepted", null));
            };
            h.NotAcceptedObservationVerifier = _ => null;
            h.Barriers = new AdmissionBarriers
            {
                AfterAcceptBeforeLedger = async () =>
                {
                    atLedger.TrySetResult();
                    await releaseLedger.Task;
                },
            };
        });
        var concurrentSvc = new ArbitrationAdmissionService(store, hooks, () => _now);
        var request = Req(ns: "v2", workflow: "group:acceptance-claim-race", operationType: OperationType.ExternalStart);
        var submissionTask = svc.SubmitAsync(request);
        await atLedger.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = FindOp(request.RequestIdentity)!;
        var submission = ReadLease().File!.Handoff!.Submission!;
        Assert.NotNull(initial.AcceptanceClaim);
        Assert.False(initial.AcceptanceClaim!.LedgerPersisted);

        var rejected = await concurrentSvc.SettleReconciledAsync(request.RequestIdentity,
            new ReconcileSettlement.NotAccepted(submission.SubmissionIdentity, submission.SendSeq,
                "reported_not_accepted", Retryable: true, "owner:reconcile"));
        Assert.Equal(AdmissionResultKind.NeedReconcile, rejected.Kind);
        Assert.True(FindOp(request.RequestIdentity)!.ConflictPending);

        releaseLedger.TrySetResult();
        var accepted = await submissionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AdmissionResultKind.Reconciling, accepted.Kind);
        var final = FindOp(request.RequestIdentity)!;
        Assert.True(final.ConflictPending);
        Assert.True(final.AcceptanceClaim!.LedgerPersisted);
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);
        Assert.Equal(LedgerEntryState.AcceptedPendingExecution, Assert.Single(ledger.Read().File!.Entries).State);
        Assert.Equal(1, sends);
        Assert.Equal(AdmissionResultKind.NeedReconcile, (await svc.RetryAsync(request.RequestIdentity)).Kind);
    }

    /// <summary>
    /// **锁外发送异常后的配对与可用性（§24.18-3/5）**：外部启动 Sender 抛异常 ⇒ 本笔落 `Reconciling`（责任
    /// `Pending`、禁止重发），且**后续请求仍能取得门面锁**（证明重取/释放严格配对，无漏释放或死锁）。
    /// </summary>
    [Fact]
    public async Task ExternalStartSend_Throws_ReacquiresGate_AndLaterRequestsStillProceed()
    {
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => throw new InvalidOperationException("boom"));

        var failed = await svc.SubmitAsync(Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart));
        Assert.Equal(AdmissionResultKind.Reconciling, failed.Kind);
        Assert.Equal(ResponsibilityState.Pending, failed.ResponsibilityState);

        var later = await svc.SubmitAsync(Req(ns: "v2", workflow: "group:g2", operationType: OperationType.ExternalStart))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(later);
    }

    /// <summary>
    /// **边界对照（同一份代码的类型分派）**：流程登记的发送**保持既有单段串行**——发送窗口内提交的另一笔请求
    /// 在发送返回前**不得**取得串行权（这是 B2-γ 既有夹具所依赖的顺序；放开则 E1 启动窗口内提交的节点操作
    /// 会被并发轮次误拒，实测见 §24.21-C）。
    /// </summary>
    [Fact]
    public async Task FlowRegistrationSend_KeepsGate_SerializedUntilSendReturns()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h => h.Sender = async _ =>
        {
            inSend.TrySetResult();
            await releaseSend.Task;
            return new SendOutcome.Accepted("flow:accepted", null);
        });

        var first = svc.SubmitAsync(Req(ns: "manual", workflow: "group:flow1"));
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = svc.SubmitAsync(Req(ns: "manual", workflow: "group:flow2"));
        // 发送返回前不得完成（若此处未超时，说明流程登记也被放到了锁外＝边界被误放宽）。
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(300)));

        releaseSend.TrySetResult();
        Assert.Equal(AdmissionResultKind.Accepted, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// **恢复扫描不得绕过冲突裁决（§24.12-3④）**：`conflict.pending=true` 的记录即使台账已终态、本地已有
    /// `PendingTerminal`，也**只登记计数**（既不普通结算、也不改写状态/占用）。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_ConflictPendingRecord_IsSkippedNotSettled()
    {
        ExternalStartLedger? ledgerRef = null;
        var (svc, store, ledger, hooks) = BuildFacade();
        ledgerRef = ledger;
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var accepted = FindOp(r.RequestIdentity)!;
        var observedAt = _now.AddSeconds(-5);
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, r.RequestIdentity, StringComparison.Ordinal));
            op.RequestState = OperationRequestState.Accepted;
            op.ConflictPending = true;   // 冲突待决（归裁决入口）
            op.PendingTerminal = new PendingTerminal
            {
                Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-conflict",
                EvidenceSource = "ext:task.event", SubmissionIdentity = accepted.SubmissionIdentity,
                SendSeq = accepted.LastSendSeq, OperationType = OperationType.ExternalStart,
                ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
            };
            return null;
        }).Success);
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(accepted.SubmissionIdentity, accepted.LastSendSeq, Terminal: true, JobId: "job-conflict")]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, report.ConflictPendingSkipped);
        Assert.Equal(0, report.TerminalizationCompleted);
        var after = FindOp(r.RequestIdentity)!;
        Assert.True(after.ConflictPending);                                     // 冲突待决标记保留
        Assert.Equal(OperationRequestState.Accepted, after.RequestState);       // 未被普通结算推进
        Assert.Null(after.ExecutionResult);                                     // 未补造终态事实

        // **旁路封堵**：直接调用公开完成结算也不能绕过待决冲突；必须先走冲突裁决。
        var direct = await svc.SettleCompletionAsync(r.RequestIdentity, accepted.SubmissionIdentity, accepted.LastSendSeq,
            ExternalStartCompletion.SucceededWith("completed", "ext:task.event", observedAt, "job-conflict"),
            "ext:task.event", acceptanceRunId: null, acceptanceJobId: "job-conflict");
        Assert.Equal("conflict_pending", direct.ReasonCode);
        Assert.Equal(ResponsibilityState.Pending, direct.ResponsibilityState);
        var afterDirect = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, afterDirect.RequestState);
        Assert.Null(afterDirect.ExecutionResult);
    }

    /// <summary>
    /// **恢复补终局不得补造载荷**（§24.2-2／§24.12-6）：`PendingTerminal` 缺证据来源（或 `Failed` 缺执行错误码）时
    /// **不驱动结算**——只登记，不写终局、不释放责任。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_IncompletePendingPayload_DoesNotTerminalize()
    {
        var (svc, store, ledger, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var accepted = FindOp(r.RequestIdentity)!;
        var observedAt = _now.AddSeconds(-5);
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, r.RequestIdentity, StringComparison.Ordinal));
            op.RequestState = OperationRequestState.Accepted;
            op.PendingTerminal = new PendingTerminal
            {
                Kind = ExecutionResultKind.Failed, RawTerminal = "failed", JobId = "job-incomplete",
                EvidenceSource = "",                       // 载荷缺失：证据来源为空
                ExecutionErrorCode = null,                 // 且失败结果缺执行错误码
                SubmissionIdentity = accepted.SubmissionIdentity, SendSeq = accepted.LastSendSeq,
                OperationType = OperationType.ExternalStart, ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
            };
            return null;
        }).Success);
        Assert.True(ledger.MarkTerminal(accepted.SubmissionIdentity, accepted.LastSendSeq, "failed", observedAt,
            rawTerminal: "failed", executionErrorCode: "E_X", jobId: "job-incomplete",
            operationType: OperationType.ExternalStart, terminalEvidenceSource: "ext:task.event").Success);
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(accepted.SubmissionIdentity, accepted.LastSendSeq, Terminal: true, JobId: "job-incomplete")]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, report.TerminalizationCompleted);
        Assert.Equal(0, report.TerminalizationFailed);   // 不驱动 ⇒ 既不算成功也不算失败
        Assert.Equal(1, report.IncompletePendingPayload); // 必须**如实登记**（不得静默消失）
        var after = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, after.RequestState);   // 责任保留、未终局
    }

    // ── 恢复扫描集合②/③（R5.3 §24.12-3；[Batch B 收尾之五]）────────────────────────────────────

    /// <summary>
    /// **[C 表 #10 余项／批次四十八] 恢复入口「再次取许可」的许可语义**：
    /// ①**不得复用**既有未决许可——同一 runBinding 尚存未决 `Submission` 时，恢复准入被 `submission_conflict`
    /// **确定拒绝**（因未发布发送许可而终局中止）：**零新增发送**、恢复操作 `LastSendSeq == 0`、原未决发送不变；
    /// ②该未决发送经**权威对账确定未受理**关闭后，恢复**再次发起**必须**新签发本轮许可**
    /// （`LastSendSeq == 1`、本笔发送身份 `sub:{rid}:1`、Sender 恰一次、结算后无未决发送）——
    /// 即「再次取许可」是**重新签发**，不是沿用旧身份。
    /// </summary>
    [Fact]
    public async Task RecoveryEntry_ReacquiresOwnPermit_AfterUnresolvedSubmissionClosed()
    {
        var sends = 0;
        var acceptSend = false;
        var dispatches = new List<SubmissionDispatch>();
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ =>
        {
            dispatches.Add(_);          // **逐次保存派发对象**（直接证据：Sender 实际消费的发送身份）
            Interlocked.Increment(ref sends);
            return Task.FromResult<SendOutcome>(acceptSend
                ? new SendOutcome.Accepted("ext:accepted", "run-rec")
                : new SendOutcome.Unknown("ipc_timeout"));
        });

        // 甲：制造**未决发送**（Unknown ⇒ `Reconciling` ＋ `Submission` 在册）
        var unresolved = Req(ns: "v2", workflow: "wf-rec", operationType: OperationType.ExternalStart);
        unresolved.RunBinding = "run-rec";
        Assert.Equal(AdmissionResultKind.Reconciling, (await svc.SubmitAsync(unresolved)).Kind);
        var unresolvedOp = FindOp(unresolved.RequestIdentity)!;
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);
        var sendsAfterUnresolved = sends;

        // ① 未决发送在册 ⇒ 恢复准入**确定拒绝**（零许可、零发送）
        var blocked = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:recovery-reacquire",
            RunId = "run-rec",
            WorkflowId = "wf-rec",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });
        Assert.Equal(AdmissionResultKind.TerminalRejected, blocked.Kind);
        Assert.Equal(ResponsibilityState.Settled, blocked.ResponsibilityState);
        Assert.Equal("submission_conflict", blocked.ReasonCode);
        Assert.Equal(sendsAfterUnresolved, sends);                       // 零新增发送
        var blockedOp = FindOp(blocked.RequestIdentity)!;
        Assert.Equal(0, blockedOp.LastSendSeq);                          // 未签发许可
        Assert.True(string.IsNullOrEmpty(blockedOp.SubmissionIdentity));
        // **终局中止**的直接证据（不只断返回码）：操作终局拒绝＋原因码＋迁区（不悬置在活跃区）
        Assert.Equal(OperationRequestState.TerminalRejected, blockedOp.RequestState);
        Assert.Equal("submission_conflict", blockedOp.LastPrecheckResult?.ReasonCode);
        Assert.NotEqual(OperationZone.Active, blockedOp.Zone);
        Assert.Single(dispatches);                                       // 被拒的恢复**未派发**给 Sender（仍只有甲那一次）
        Assert.NotNull(ReadLease().File!.Handoff!.Submission);           // 原未决发送**未被复用/未被动过**
        Assert.Equal(unresolvedOp.SubmissionIdentity, ReadLease().File!.Handoff!.Submission!.SubmissionIdentity);

        // ② 权威对账**确定未受理** ⇒ 关闭该未决发送
        var settle = await svc.SettleReconciledAsync(unresolved.RequestIdentity,
            new ReconcileSettlement.NotAccepted(unresolvedOp.SubmissionIdentity, unresolvedOp.LastSendSeq,
                "bgi_rejected", Retryable: false, "fixture:对账确定未受理"));
        Assert.Equal(AdmissionResultKind.TerminalRejected, settle.Kind);
        Assert.Null(ReadLease().File!.Handoff!.Submission);

        // ③ 恢复**再次发起** ⇒ 新签发本轮许可（重新签发，非沿用）
        acceptSend = true;
        var admitted = await svc.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = "fixture:recovery-reacquire-2",
            RunId = "run-rec",
            WorkflowId = "wf-rec",
            RestoreBranch = "interrupted-relocate",
            Scope = "bgi:inst:ep1",
        });
        Assert.Equal(AdmissionResultKind.Accepted, admitted.Kind);
        Assert.Equal(sendsAfterUnresolved + 1, sends);                   // 恰一次新发送（无重发）
        var recoveryOp = FindOp(admitted.RequestIdentity)!;
        Assert.Equal(1, recoveryOp.LastSendSeq);                         // **新签发**
        Assert.Equal("sub:" + admitted.RequestIdentity + ":1", recoveryOp.SubmissionIdentity);
        Assert.NotEqual(unresolvedOp.SubmissionIdentity, recoveryOp.SubmissionIdentity);  // 不复用旧身份
        // **Sender 实际消费的许可身份**（跨边界直接证据）：恢复那次的派发对象身份＝新许可，且 ≠ 旧许可
        var recoveryDispatch = Assert.Single(dispatches.Where(d => d.RequestIdentity == admitted.RequestIdentity));
        Assert.Equal(recoveryOp.SubmissionIdentity, recoveryDispatch.SubmissionIdentity);
        Assert.Equal(1, recoveryDispatch.SendSeq);
        Assert.Equal("ep1", recoveryDispatch.TargetEpoch);
        Assert.NotEqual(unresolvedOp.SubmissionIdentity, recoveryDispatch.SubmissionIdentity);
        // **旧操作重新读盘**：身份/许可水位原样保留在册，未被恢复操作覆盖或清洗
        var oldAfter = FindOp(unresolved.RequestIdentity)!;
        Assert.Equal(unresolvedOp.SubmissionIdentity, oldAfter.SubmissionIdentity);
        Assert.Equal(unresolvedOp.LastSendSeq, oldAfter.LastSendSeq);
        Assert.Equal(OperationRequestState.TerminalRejected, oldAfter.RequestState);   // 对账确定未受理的终局
        Assert.NotEqual(unresolved.RequestIdentity, admitted.RequestIdentity);
        Assert.Null(ReadLease().File!.Handoff!.Submission);              // 新许可已按唯一顺序结算关闭
    }

    [Fact]
    public async Task InFlightContinueAndRetry_PreservePendingResponsibilityAndSendIdentity()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h => h.Sender = async _ =>
        {
            inSend.TrySetResult();
            await releaseSend.Task;
            return new SendOutcome.Unknown("fixture_unknown");
        });

        var request = Req(ns: "v2", workflow: "wf-inflight-classification", operationType: OperationType.ExternalStart);
        var submit = svc.SubmitAsync(request);
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var op = FindOp(request.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Granted, op.RequestState);

        var continued = await svc.SubmitAsync(ContinueOf(request));
        Assert.Equal(AdmissionResultKind.NeedReconcile, continued.Kind);
        Assert.Equal(ExecutionDisposition.None, continued.ExecutionDisposition);
        Assert.Equal(ResponsibilityState.Pending, continued.ResponsibilityState);
        Assert.Equal(op.SubmissionIdentity, continued.SubmissionIdentity);
        Assert.Equal(op.LastSendSeq, continued.SendSeq);

        var retried = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, retried.Kind);
        Assert.Equal(ExecutionDisposition.None, retried.ExecutionDisposition);
        Assert.Equal(ResponsibilityState.Pending, retried.ResponsibilityState);
        Assert.Equal(op.SubmissionIdentity, retried.SubmissionIdentity);
        Assert.Equal(op.LastSendSeq, retried.SendSeq);

        releaseSend.TrySetResult();
        var original = await submit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.Reconciling, original.Kind);
        Assert.Single(ReadLease().File!.Handoff!.Operations.Where(o => o.RequestIdentity == request.RequestIdentity));
    }

    [Fact]
    public async Task ConcurrentRetry_DuringEnqueueBeforeStateTransition_ReturnsPendingInsteadOfRetryable()
    {
        var retryEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRetryEnqueue = false;
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable")
                    : new SendOutcome.Accepted("fixture:accepted", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterRetryReservation = () =>
                {
                    if (!pauseRetryEnqueue) return Task.CompletedTask;
                    retryEnqueued.TrySetResult();
                    return releaseRetry.Task;
                },
            };
        });

        var request = Req(ns: "v2", workflow: "wf-concurrent-retry", operationType: OperationType.ExternalStart);
        var first = await svc.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.RetryableRejected, first.Kind);
        var prior = FindOp(request.RequestIdentity)!;
        pauseRetryEnqueue = true;

        var retryTask = Task.Run(() => svc.RetryAsync(request.RequestIdentity));
        await retryEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationRequestState.RetryableRejected, FindOp(request.RequestIdentity)!.RequestState);

        var concurrent = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.NeedReconcile, concurrent.Kind);
        Assert.Equal(ResponsibilityState.Pending, concurrent.ResponsibilityState);
        Assert.Equal(prior.SubmissionIdentity, concurrent.SubmissionIdentity);
        Assert.Equal(prior.LastSendSeq, concurrent.SendSeq);
        Assert.Equal(1, Volatile.Read(ref sends));

        releaseRetry.TrySetResult();
        var retried = await retryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.Accepted, retried.Kind);
        Assert.Equal(2, Volatile.Read(ref sends));
        Assert.Equal(2, FindOp(request.RequestIdentity)!.LastSendSeq);
    }

    [Fact]
    public async Task ContinueUseRacingRetry_UsesSingleIdentityReservationAndSingleSend()
    {
        var continueChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseContinue = false;
        var pauseRetry = false;
        var sends = 0;
        var (svc, store, _, _) = BuildFacade(h =>
        {
            h.Sender = _ =>
            {
                var seq = Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(seq == 1
                    ? new SendOutcome.Rejected("task_running", Retryable: true, "fixture:retryable")
                    : new SendOutcome.Accepted("fixture:accepted", null));
            };
            h.Barriers = new AdmissionBarriers
            {
                AfterContinueInFlightCheck = () =>
                {
                    if (!pauseContinue) return Task.CompletedTask;
                    continueChecked.TrySetResult();
                    return releaseContinue.Task;
                },
                AfterRetryReservation = () =>
                {
                    if (!pauseRetry) return Task.CompletedTask;
                    retryReserved.TrySetResult();
                    return releaseRetry.Task;
                },
            };
        });

        var request = Req(ns: "v2", workflow: "wf-continue-retry-race", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.RetryableRejected, (await svc.SubmitAsync(request)).Kind);
        var lease = store.Read().File!.Lease!;
        Assert.True(store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            file.Handoff!.Operations.Single(o => o.RequestIdentity == request.RequestIdentity).RequestState = OperationRequestState.Queued;
            return null;
        }).Success);

        pauseContinue = true;
        var continueTask = svc.SubmitAsync(ContinueOf(request));
        await continueChecked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pauseRetry = true;
        var retryTask = Task.Run(() => svc.RetryAsync(request.RequestIdentity));
        await retryReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseContinue.TrySetResult();
        var continued = await continueTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.NeedReconcile, continued.Kind);
        Assert.Equal(ResponsibilityState.Pending, continued.ResponsibilityState);

        releaseRetry.TrySetResult();
        var retried = await retryTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.Accepted, retried.Kind);
        Assert.Equal(2, Volatile.Read(ref sends));
        Assert.Equal(2, FindOp(request.RequestIdentity)!.LastSendSeq);
    }

    [Fact]
    public async Task LateRetry_AfterTerminalCommitBeforeInflightRelease_ReturnsSettledTerminalFact()
    {
        var terminalCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Rejected("permanent_reject", Retryable: false, "fixture:terminal"));
            h.Barriers = new AdmissionBarriers
            {
                BeforeInFlightRelease = () =>
                {
                    terminalCommitted.TrySetResult();
                    return releaseRound.Task;
                },
            };
        });

        var request = Req(ns: "v2", workflow: "wf-late-retry", operationType: OperationType.ExternalStart);
        var submit = svc.SubmitAsync(request);
        await terminalCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationRequestState.TerminalRejected, FindOp(request.RequestIdentity)!.RequestState);

        var lateRetry = await svc.RetryAsync(request.RequestIdentity);
        Assert.Equal(AdmissionResultKind.TerminalRejected, lateRetry.Kind);
        Assert.Equal(ResponsibilityState.Settled, lateRetry.ResponsibilityState);
        Assert.Equal("permanent_reject", lateRetry.ReasonCode);
        Assert.Equal(1, lateRetry.SendSeq);
        Assert.Equal(FindOp(request.RequestIdentity)!.SubmissionIdentity, lateRetry.SubmissionIdentity);

        releaseRound.TrySetResult();
        var original = await submit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.TerminalRejected, original.Kind);
    }

    /// <summary>夹具辅助：把某笔外部启动操作**推进到下一发送轮次**（模拟「扫描快照后责任被并发推进」）。</summary>
    private string? PromoteToNextSendSeq(ArbitrationLeaseStore store, string requestIdentity)
    {
        var read = store.Read();
        if (read.File?.Lease is null) return "lease_missing";
        var mutate = store.MutateHandoff(read.File.Lease.LeaseId, read.File.Lease.OwnerEpoch, read.File.Revision, file =>
        {
            var op = file.Handoff!.Operations.FirstOrDefault(o => o.RequestIdentity == requestIdentity);
            if (op is null) return "operation_missing";
            op.LastSendSeq += 1;                       // 责任被推进到新一轮（身份随之改变）
            op.AcceptanceClaim = null;                  // 夹具模拟没有持久认领的历史状态推进
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = _now;
            return null;
        });
        return mutate.Success ? null : (mutate.Reason ?? "invalid_request");
    }

    /// <summary>
    /// **集合②未终结台账**：恢复扫描只**保留观察责任**——不改状态、不写终态载体、不释放占用、不重发。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_UnterminatedLedger_KeepsResponsibilityOnly()
    {
        var (svc, _, ledger, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var op = FindOp(r.RequestIdentity)!;
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(op.SubmissionIdentity, op.LastSendSeq, Terminal: false)]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.False(report.LedgerUnreadable);
        Assert.Equal(1, report.ObservationKept);
        Assert.Equal(0, report.TerminalizationCompleted);
        var after = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Accepted, after.RequestState);   // 责任保留（未改写）
        Assert.Null(after.PendingTerminal);                                 // 无权威终态 ⇒ 不得写终态载体
        Assert.Null(after.ExecutionResult);
        Assert.Contains(ledger.Read().File!.Entries, e => e.State == LedgerEntryState.AcceptedPendingExecution);
    }

    /// <summary>
    /// **[C 表 #6／批次四十六] 集合②「持续观察**重绑**观察责任」**：重启后扫描发现台账**未终结**且本笔仍负
    /// 责任 ⇒ **持久化**观察义务（重绑时点＋句柄＋单调次数），责任状态/占用/发送身份**一律不变**、不重发；
    /// 连续两轮扫描次数递增（「停驻≠放弃」＝可追溯、可续扫），且报告区分「观察保留」与「本轮重绑」。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_UnterminatedLedger_RebindsDurableObservationResponsibility()
    {
        var (svc, _, ledger, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var before = FindOp(r.RequestIdentity)!;
        Assert.Null(before.ObservationReboundAtUtc);                       // 首次观察前：无重绑载体
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(before.SubmissionIdentity, before.LastSendSeq, Terminal: false, JobId: "job-obs-1")]);

        var first = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, first.ObservationKept);
        Assert.True(first.ObservationRebound == 1,
            "本轮必须重绑成功；失败原因=" + (first.ObservationRebindFailure ?? "<none>")); // 本轮确实重绑成功
        Assert.Equal(0, first.TerminalizationCompleted);
        var afterFirst = FindOp(r.RequestIdentity)!;
        Assert.True(afterFirst.ObservationReboundAtUtc is not null,
            "重绑载体必须落盘；state=" + afterFirst.RequestState + " zone=" + afterFirst.Zone
            + " applied=" + first.ObservationRebound + " failure=" + (first.ObservationRebindFailure ?? "<none>"));
        Assert.Equal("job-obs-1", afterFirst.ObservationJobId);            // 句柄随重绑落盘（供后续取证/结算）
        Assert.Equal(1, afterFirst.ObservationRebindCount);
        Assert.Equal(OperationRequestState.Accepted, afterFirst.RequestState); // 责任状态不变
        Assert.Equal(before.SubmissionIdentity, afterFirst.SubmissionIdentity); // 发送身份不变
        Assert.Equal(before.LastSendSeq, afterFirst.LastSendSeq);               // 未新增发送许可
        Assert.Null(afterFirst.PendingTerminal);
        Assert.Null(afterFirst.ExecutionResult);
        Assert.Equal(OperationZone.Active, afterFirst.Zone);               // 未迁区（主槽位不释放）
        // 占用仍由**台账未终结**承载（外部启动受理后 Submission 已关闭，占位≠未决发送）
        Assert.Contains(ledger.Read().File!.Entries, e => e.State == LedgerEntryState.AcceptedPendingExecution);

        var second = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, second.ObservationRebound);
        Assert.Equal(2, FindOp(r.RequestIdentity)!.ObservationRebindCount); // **可续扫**（次数单调递增）
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 会诊处置] 重绑写事务的**完整发送身份复核**（跨进程推进反例）**：扫描快照后同一笔
    /// 已被推进到**新的发送轮次**（`sendSeq` 不同）时，重绑**不得**把旧轮次句柄写到新责任上——整笔跳过、
    /// 计数与句柄**均不写入**，报告如实登记「未全部落盘」（`ObservationRebindFailure`）。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_RebindTargetAdvanced_SkipsWithoutOverwriting()
    {
        var (svc, store, _, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var before = FindOp(r.RequestIdentity)!;
        // 事实指向**当前轮**；在「分类完成 → 重绑写事务」之间由接缝把责任**推进到下一轮**
        // （确定性复现「扫描快照之后、写事务之前被其他处理者推进」）。
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(before.SubmissionIdentity, SendSeq: 1, Terminal: false, JobId: "job-stale")]);
        hooks.BeforeObservationRebindPersist = () =>
        {
            var advance = PromoteToNextSendSeq(store, r.RequestIdentity);
            Assert.True(advance is null, "造景：推进发送轮次失败（" + advance + "）");
            return Task.CompletedTask;
        };

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, report.ObservationRebound);                                   // 整笔跳过
        Assert.Equal("observation_rebind_state_advanced", report.ObservationRebindFailure);
        Assert.True(report.AnythingReported);                                        // 失败在报告中可见
        var after = FindOp(r.RequestIdentity)!;
        Assert.Null(after.ObservationReboundAtUtc);                                   // 载体未被污染
        Assert.Equal(0, after.ObservationRebindCount);
        Assert.Null(after.ObservationJobId);
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 会诊处置] 句柄冲突保护**：已持久化句柄 `job-A` 时，扫描给出**不同非空句柄**
    /// `job-B` ⇒ **不得覆盖**（fail-closed，报 `observation_job_id_conflict`）；相同句柄 ⇒ 幂等重绑。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_JobIdConflict_DoesNotOverwrite_EqualJobIdIdempotent()
    {
        var (svc, _, _, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var op = FindOp(r.RequestIdentity)!;
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(op.SubmissionIdentity, op.LastSendSeq, Terminal: false, JobId: "job-A")]);
        var first = await svc.RecoverExternalStartObservationsAsync();
        Assert.Equal(1, first.ObservationRebound);
        Assert.Equal("job-A", FindOp(r.RequestIdentity)!.ObservationJobId);

        // 同句柄 ⇒ 幂等重绑（句柄不变、次数继续递增）
        var second = await svc.RecoverExternalStartObservationsAsync();
        Assert.Equal(1, second.ObservationRebound);
        Assert.Null(second.ObservationRebindFailure);
        Assert.Equal(2, FindOp(r.RequestIdentity)!.ObservationRebindCount);

        // 不同非空句柄 ⇒ 冲突：不覆盖、不计数、如实上报
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(op.SubmissionIdentity, op.LastSendSeq, Terminal: false, JobId: "job-B")]);
        var third = await svc.RecoverExternalStartObservationsAsync();
        Assert.Equal(0, third.ObservationRebound);
        Assert.Equal("observation_job_id_conflict", third.ObservationRebindFailure);
        var after = FindOp(r.RequestIdentity)!;
        Assert.Equal("job-A", after.ObservationJobId);                                // **不覆盖历史句柄**
        Assert.Equal(2, after.ObservationRebindCount);
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 第二轮验证会诊反例] 同轮重复扫描事实的**规范化**（顺序无关）**：
    /// ①`(null, job-A)` ⇒ 稳定合并出 `job-A` 并正常重绑；②`(job-A, job-B)` ⇒ **整组冲突不处理**
    /// （不重绑、不写句柄、计数不递增）；③同组 `Terminal=false` 与 `true` 并存 ⇒ 同样整组冲突
    /// （既不重绑也不补终局）。
    /// </summary>
    [Theory]
    [InlineData("merge_null_then_job", 1, 0, "job-A")]
    [InlineData("merge_job_then_null", 1, 0, "job-A")]
    [InlineData("conflict_two_jobs", 0, 1, null)]
    [InlineData("conflict_terminal_flags", 0, 1, null)]
    public async Task RecoverObservations_DuplicateScanFacts_NormalizedOrderIndependently(
        string mode, int expectedRebound, int expectedConflicts, string? expectedJobId)
    {
        var (svc, _, _, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var op = FindOp(r.RequestIdentity)!;
        var sub = op.SubmissionIdentity;
        var seq = op.LastSendSeq;
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true, mode switch
        {
            "merge_null_then_job" =>
            [
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: null),
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: "job-A"),
            ],
            "merge_job_then_null" =>
            [
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: "job-A"),
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: null),
            ],
            "conflict_two_jobs" =>
            [
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: "job-A"),
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: "job-B"),
            ],
            _ =>
            [
                new TakeoverLedgerFact(sub, seq, Terminal: false, JobId: "job-A"),
                new TakeoverLedgerFact(sub, seq, Terminal: true, JobId: "job-A"),
            ],
        });

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(expectedRebound, report.ObservationRebound);
        Assert.Equal(expectedConflicts, report.ScanFactConflicts);
        if (expectedConflicts > 0) Assert.True(report.AnythingReported);
        var after = FindOp(r.RequestIdentity)!;
        Assert.Equal(expectedJobId, after.ObservationJobId);                    // 冲突组**不写句柄**（零污染）
        Assert.Equal(expectedRebound, after.ObservationRebindCount);
        Assert.Equal(OperationRequestState.Accepted, after.RequestState);       // 冲突组不补终局
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 第二轮验证会诊反例] 计数溢出零污染**：已持久化计数为 `int.MaxValue`（非法/越界）
    /// ⇒ 该笔**保守失败**：不写时点、不写句柄、计数不回绕；报告如实给出原因。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_RebindCountOverflow_NoFieldPollution()
    {
        var (svc, store, _, hooks) = BuildFacade();
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var op = FindOp(r.RequestIdentity)!;
        var seeded = store.MutateHandoff(store.Read().File!.Lease!.LeaseId, store.Read().File!.Lease.OwnerEpoch,
            store.Read().File!.Revision, file =>
            {
                file.Handoff!.Operations.First(o => o.RequestIdentity == r.RequestIdentity).ObservationRebindCount = int.MaxValue;
                return null;
            });
        Assert.True(seeded.Success, "造景写入失败：" + seeded.Reason);
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(op.SubmissionIdentity, op.LastSendSeq, Terminal: false, JobId: "job-overflow")]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, report.ObservationRebound);
        Assert.Equal("observation_rebind_count_overflow", report.ObservationRebindFailure);
        var after = FindOp(r.RequestIdentity)!;
        Assert.Equal(int.MaxValue, after.ObservationRebindCount);   // 不回绕
        Assert.Null(after.ObservationJobId);                       // **句柄零污染**
        Assert.Null(after.ObservationReboundAtUtc);
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 第二轮验证会诊反例] 交错接缝异常不得吞掉本轮**：接缝抛异常 ⇒ 只放弃**本轮重绑**
    /// 并如实登记原因，**集合③补终局照常继续**（同一轮内互不连坐）。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_RebindSeamThrows_ReportedAndSettlementStillRuns()
    {
        var (svc, store, _, hooks) = BuildFacade(h =>
            h.BeforeObservationRebindPersist = () => throw new InvalidOperationException("fixture seam boom"));
        // 甲：集合②（本应重绑，因接缝异常放弃）
        var a = Req(ns: "v2", workflow: "group:gap", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(a)).Kind);
        var opA = FindOp(a.RequestIdentity)!;
        // 乙：集合③（台账已终态＋已有 PendingTerminal 载体）⇒ 即使接缝异常也必须照常补终局
        var b = Req(ns: "v2", workflow: "group:gbq", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(b)).Kind);
        var opB = FindOp(b.RequestIdentity)!;
        var observedAt = _now;
        hooks.F11Active = () => false;
        hooks.TakeoverTerminalPersist = (sub, seq, evidence, at, raw, code, jobId, source, kind) => null;
        hooks.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, code, jobId, source, at, kind) => true;
        hooks.TakeoverJobIdRead = (sub, seq) => LedgerHandleProbe.Present("job-seam");
        var seeded = store.MutateHandoff(store.Read().File!.Lease!.LeaseId, store.Read().File!.Lease.OwnerEpoch,
            store.Read().File!.Revision, file =>
            {
                var target = file.Handoff!.Operations.First(o => o.RequestIdentity == b.RequestIdentity);
                target.ExecutionResult = new ExecutionResult
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-seam",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = opB.SubmissionIdentity,
                    SendSeq = opB.LastSendSeq, ObservedAtUtc = observedAt,
                };
                target.PendingTerminal = new PendingTerminal
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-seam",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = opB.SubmissionIdentity,
                    SendSeq = opB.LastSendSeq, OperationType = OperationType.ExternalStart,
                    ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
                };
                return null;
            });
        Assert.True(seeded.Success, "造景写入失败：" + seeded.Reason);
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
        [
            new TakeoverLedgerFact(opA.SubmissionIdentity, opA.LastSendSeq, Terminal: false, JobId: "job-a"),
            new TakeoverLedgerFact(opB.SubmissionIdentity, opB.LastSendSeq, Terminal: true, JobId: "job-seam"),
        ]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(0, report.ObservationRebound);                                  // 本轮重绑放弃
        Assert.Equal("observation_rebind_seam_exception", report.ObservationRebindFailure);
        Assert.Equal(1, report.TerminalizationCompleted);                            // 集合③照常
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(b.RequestIdentity)!.RequestState);
        Assert.Null(FindOp(a.RequestIdentity)!.ObservationReboundAtUtc);              // 甲笔未被写入
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 第三轮验证会诊反例] 诊断原因码不得被遮蔽**：同一轮同时存在
    /// 「扫描事实冲突组」＋「可重绑目标」＋「接缝抛异常」时，`ObservationRebindFailure` 必须是
    /// **`observation_rebind_seam_exception`**（更具体的原因），扫描冲突只经 `ScanFactConflicts` 报告。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_ScanConflictPlusSeamThrow_SeamReasonNotMasked()
    {
        var (svc, _, _, hooks) = BuildFacade(h =>
            h.BeforeObservationRebindPersist = () => throw new InvalidOperationException("fixture seam boom"));
        // 甲：可重绑目标（本因接缝异常放弃重绑）
        var a = Req(ns: "v2", workflow: "group:mask-a", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(a)).Kind);
        var opA = FindOp(a.RequestIdentity)!;
        // 乙：同轮另有一组**自相矛盾**的扫描事实（两不同非空句柄）
        var b = Req(ns: "v2", workflow: "group:mask-b", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(b)).Kind);
        var opB = FindOp(b.RequestIdentity)!;
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
        [
            new TakeoverLedgerFact(opA.SubmissionIdentity, opA.LastSendSeq, Terminal: false, JobId: "job-a"),
            new TakeoverLedgerFact(opB.SubmissionIdentity, opB.LastSendSeq, Terminal: false, JobId: "job-b1"),
            new TakeoverLedgerFact(opB.SubmissionIdentity, opB.LastSendSeq, Terminal: false, JobId: "job-b2"),
        ]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, report.ScanFactConflicts);                              // 冲突组经专用计数报告
        Assert.Equal(0, report.ObservationRebound);
        Assert.Equal("observation_rebind_seam_exception", report.ObservationRebindFailure); // **原因不得被遮蔽**
        var opAAfter = FindOp(a.RequestIdentity)!;                              // 三个重绑字段**均无部分写入**
        Assert.Null(opAAfter.ObservationReboundAtUtc);
        Assert.Null(opAAfter.ObservationJobId);
        Assert.Equal(0, opAAfter.ObservationRebindCount);
        Assert.Null(FindOp(b.RequestIdentity)!.ObservationJobId);               // 冲突组不写句柄
    }

    /// <summary>
    /// **[C 表 #6／批次四十六 会诊处置] 混合批次**：同一轮里「一笔集合②重绑」＋「一笔集合③补终局」并存 ⇒
    /// 两者各自如实计数（`ObservationRebound=1` 且 `TerminalizationCompleted=1`），互不吞并。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_MixedBatch_RebindsAndSettlesIndependently()
    {
        ExternalStartLedger? ledgerRef = null;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.TakeoverTerminalPersist = (sub, seq, evidence, observedAt, raw, code, jobId, source, kind) =>
            {
                var m = ledgerRef!.MarkTerminal(sub, seq, evidence, observedAt, raw, code,
                    OperationType.ExternalStart, jobId, source, kind);
                return m.Success ? null : m.Reason;
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, code, jobId, source, observedAt, kind) => true;
            h.TakeoverJobIdRead = (sub, seq) => LedgerHandleProbe.Present("job-settle");
        });
        ledgerRef = ledger;

        // 甲：集合②（台账未终结）
        var a = Req(ns: "v2", workflow: "group:gA", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(a)).Kind);
        var opA = FindOp(a.RequestIdentity)!;
        // 乙：集合③（台账已终态、Operation 未终局）——先造 PendingTerminal 载体与台账终态
        var b = Req(ns: "v2", workflow: "group:gB", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(b)).Kind);
        var opB = FindOp(b.RequestIdentity)!;
        var observedAt = _now;
        var seeded = store.MutateHandoff(ReadLease().File!.Lease!.LeaseId, ReadLease().File!.Lease.OwnerEpoch,
            ReadLease().File!.Revision, file =>
            {
                var target = file.Handoff!.Operations.First(o => o.RequestIdentity == b.RequestIdentity);
                target.ExecutionResult = new ExecutionResult
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-settle",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = opB.SubmissionIdentity,
                    SendSeq = opB.LastSendSeq, ObservedAtUtc = observedAt,
                };
                target.PendingTerminal = new PendingTerminal
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-settle",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = opB.SubmissionIdentity,
                    SendSeq = opB.LastSendSeq, OperationType = OperationType.ExternalStart,
                    ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
                };
                return null;
            });
        Assert.True(seeded.Success, "造景写入失败：" + seeded.Reason);
        Assert.True(ledger.MarkTerminal(opB.SubmissionIdentity, opB.LastSendSeq, "completed", observedAt,
            rawTerminal: "completed", jobId: "job-settle", operationType: OperationType.ExternalStart,
            terminalEvidenceSource: "ext:task.event").Success, "造景前置：台账终态写入失败");
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
        [
            new TakeoverLedgerFact(opA.SubmissionIdentity, opA.LastSendSeq, Terminal: false, JobId: "job-rebind"),
            new TakeoverLedgerFact(opB.SubmissionIdentity, opB.LastSendSeq, Terminal: true, JobId: "job-settle"),
        ]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, report.ObservationKept);
        Assert.Equal(1, report.ObservationRebound);          // 甲：重绑
        Assert.Equal(1, report.TerminalizationCompleted);    // 乙：补终局
        Assert.Equal("job-rebind", FindOp(a.RequestIdentity)!.ObservationJobId);
        Assert.Equal(OperationRequestState.TerminalCompleted, FindOp(b.RequestIdentity)!.RequestState);
    }

    /// <summary>
    /// **[C 表 #6／批次四十六] 重绑后可**后续结算****：同一笔先按集合②重绑（台账未终结），随后台账报
    /// **权威终态** ⇒ 恢复扫描按集合③用已持久化 `PendingTerminal` 补终局（`TerminalCompleted`＋未决发送关闭），
    /// 即「停驻」不吞掉责任、后续证据到达即可结清。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_ReboundThenAuthoritativeTerminal_SettlesLater()
    {
        ExternalStartLedger? ledgerRef = null;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.TakeoverTerminalPersist = (sub, seq, evidence, observedAt, raw, code, jobId, source, kind) =>
            {
                var m = ledgerRef!.MarkTerminal(sub, seq, evidence, observedAt, raw, code,
                    OperationType.ExternalStart, jobId, source, kind);
                return m.Success ? null : m.Reason;
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, code, jobId, source, observedAt, kind) =>
            {
                var read = ledgerRef!.Read();
                if (!read.Valid || read.File is null) return false;
                var e = read.File.Entries.FirstOrDefault(x =>
                    string.Equals(x.SubmissionIdentity, sub, StringComparison.Ordinal) && x.SendSeq == seq);
                return e is { State: LedgerEntryState.Terminal }
                       && string.Equals(e.RawTerminal, raw, StringComparison.Ordinal)
                       && string.Equals(e.JobId, jobId, StringComparison.Ordinal);
            };
            h.TakeoverJobIdRead = (sub, seq) => LedgerHandleProbe.Present("job-obs-late");
        });
        ledgerRef = ledger;
        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var before = FindOp(r.RequestIdentity)!;

        // 第一轮：台账未终结 ⇒ 重绑观察责任（不终局）
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(before.SubmissionIdentity, before.LastSendSeq, Terminal: false, JobId: "job-obs-late")]);
        var first = await svc.RecoverExternalStartObservationsAsync();
        Assert.Equal(1, first.ObservationRebound);
        Assert.Equal(0, first.TerminalizationCompleted);
        Assert.Equal(OperationRequestState.Accepted, FindOp(r.RequestIdentity)!.RequestState);

        // 之后权威终态到达：先落 PendingTerminal 载体（生产由完成观察写入），再以台账终态驱动补终局
        var observedAt = _now;
        var submission = before.SubmissionIdentity;
        var seq = before.LastSendSeq;
        var seeded = store.MutateHandoff(ReadLease().File!.Lease!.LeaseId, ReadLease().File!.Lease.OwnerEpoch,
            ReadLease().File!.Revision, file =>
            {
                var op = file.Handoff!.Operations.First(o => o.RequestIdentity == r.RequestIdentity);
                op.ExecutionResult = new ExecutionResult
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-obs-late",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = submission, SendSeq = seq, ObservedAtUtc = observedAt,
                };
                op.PendingTerminal = new PendingTerminal
                {
                    Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-obs-late",
                    EvidenceSource = "ext:task.event", SubmissionIdentity = submission, SendSeq = seq,
                    OperationType = OperationType.ExternalStart, ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
                };
                return null;
            });
        Assert.True(seeded.Success, "造景写入失败：" + seeded.Reason);
        Assert.True(ledger.MarkTerminal(submission, seq, "completed", observedAt, rawTerminal: "completed",
            jobId: "job-obs-late", operationType: OperationType.ExternalStart,
            terminalEvidenceSource: "ext:task.event").Success, "造景前置：台账终态写入失败");
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(submission, seq, Terminal: true, JobId: "job-obs-late")]);

        var second = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, second.TerminalizationCompleted);                   // **重绑后可后续结算**
        Assert.Equal(0, second.ObservationRebound);                          // 已结算 ⇒ 本轮无待观察笔（不再重绑）
        var settled = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, settled.RequestState);
        Assert.Equal(ExecutionResultKind.Succeeded, settled.ExecutionResult!.Kind);
        Assert.Equal(1, settled.ObservationRebindCount);                      // **重绑载体保留**（历史可追溯，不回退/不清洗）
        Assert.NotNull(settled.ObservationReboundAtUtc);
        Assert.Equal("job-obs-late", settled.ObservationJobId);
        Assert.Null(ReadLease().File!.Handoff!.Submission);                  // 未决发送已关闭
        Assert.Equal(LedgerEntryState.Terminal, ledger.Read().File!.Entries.Single().State);
    }

    /// <summary>
    /// **集合③台账已终态、Operation 未终局**：恢复扫描用**已持久化 `PendingTerminal`** 补终局
    /// （§24.15 顺序：台账 Terminal 幂等 → 关闭 → Operation 终局），不得依赖重新取证、不得改写成别的结果。
    /// </summary>
    [Fact]
    public async Task RecoverObservations_LedgerTerminalButOperationNotFinal_TerminalizesFromPendingTerminal()
    {
        ExternalStartLedger? ledgerRef = null;
        var (svc, store, ledger, hooks) = BuildFacade(h =>
        {
            h.TakeoverTerminalPersist = (sub, seq, evidence, observedAt, raw, code, jobId, source, kind) =>
            {
                var m = ledgerRef!.MarkTerminal(sub, seq, evidence, observedAt, raw, code,
                    OperationType.ExternalStart, jobId, source, kind);
                return m.Success ? null : m.Reason;
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, code, jobId, source, observedAt, kind) =>
            {
                var read = ledgerRef!.Read();
                if (!read.Valid || read.File is null) return false;
                var e = read.File.Entries.FirstOrDefault(x =>
                    string.Equals(x.SubmissionIdentity, sub, StringComparison.Ordinal) && x.SendSeq == seq);
                return e is { State: LedgerEntryState.Terminal }
                       && string.Equals(e.RawTerminal, raw, StringComparison.Ordinal)
                       && string.Equals(e.ExecutionErrorCode, code, StringComparison.Ordinal)
                       && string.Equals(e.JobId, jobId, StringComparison.Ordinal)
                       && string.Equals(e.TerminalEvidenceSource, source, StringComparison.Ordinal)
                       && e.TerminalObservedAtUtc == observedAt;
            };
            h.TakeoverJobIdRead = (sub, seq) =>
            {
                var read = ledgerRef!.Read();
                if (!read.Valid) return LedgerHandleProbe.Unreadable();
                var e = read.File?.Entries.FirstOrDefault(x =>
                    string.Equals(x.SubmissionIdentity, sub, StringComparison.Ordinal) && x.SendSeq == seq);
                return e is null || string.IsNullOrEmpty(e.JobId)
                    ? LedgerHandleProbe.Absent()
                    : LedgerHandleProbe.Present(e.JobId);
            };
        });
        ledgerRef = ledger;

        var r = Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart);
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r)).Kind);
        var accepted = FindOp(r.RequestIdentity)!;
        var submission = accepted.SubmissionIdentity;
        var seq = accepted.LastSendSeq;
        var observedAt = _now.AddSeconds(-5);

        // 造景：权威终态已取得（PendingTerminal/ExecutionResult 已落盘）、台账已 Terminal，但 Operation 仍 Accepted。
        var lease = store.Read().File!.Lease!;
        var seeded = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = file.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, r.RequestIdentity, StringComparison.Ordinal));
            op.RequestState = OperationRequestState.Accepted;
            op.ExecutionResult = new ExecutionResult
            {
                Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-fin",
                EvidenceSource = "ext:task.event", SubmissionIdentity = submission, SendSeq = seq, ObservedAtUtc = observedAt,
            };
            op.PendingTerminal = new PendingTerminal
            {
                Kind = ExecutionResultKind.Succeeded, RawTerminal = "completed", JobId = "job-fin",
                EvidenceSource = "ext:task.event", SubmissionIdentity = submission, SendSeq = seq,
                OperationType = OperationType.ExternalStart, ObservedAtUtc = observedAt, RecordedAtUtc = observedAt,
            };
            return null;
        });
        Assert.True(seeded.Success, "造景写入失败：" + seeded.Reason);
        Assert.True(ledger.MarkTerminal(submission, seq, "completed", observedAt, rawTerminal: "completed",
            jobId: "job-fin", operationType: OperationType.ExternalStart,
            terminalEvidenceSource: "ext:task.event").Success, "造景前置：台账终态写入失败");
        hooks.TakeoverLedgerScan = () => new TakeoverLedgerScan(true,
            [new TakeoverLedgerFact(submission, seq, Terminal: true)]);

        var report = await svc.RecoverExternalStartObservationsAsync();

        Assert.Equal(1, report.TerminalizationCompleted);
        var repaired = FindOp(r.RequestIdentity)!;
        Assert.Equal(OperationRequestState.TerminalCompleted, repaired.RequestState);   // 终局已补齐
        Assert.Equal(ExecutionResultKind.Succeeded, repaired.ExecutionResult!.Kind);
        Assert.Equal("completed", repaired.ExecutionResult.RawTerminal);
        Assert.Equal("job-fin", repaired.ExecutionResult.JobId);
        Assert.Null(ReadLease().File!.Handoff!.Submission);                             // Submission 已关闭
        Assert.Equal(LedgerEntryState.Terminal, ledger.Read().File!.Entries.Single().State);
    }

    // ── 两段式 `_gate`（R5.3 §24.18-2／§24.14-2；[Batch B 收尾之四]）──────────────────────────────

    /// <summary>
    /// **外部启动的启动/取证必须在门面锁外**：发送期间另一笔请求必须仍能走完准入（旧实现持 `_gate` 跨发送，
    /// 本夹具会在 5s 预算内超时失败）；发送结束后本笔仍按占位身份正常结算。
    /// 对照：流程登记/节点执行的发送保持既有单段串行（`TaskCenterSuccessorPathGateTests` 既有夹具为证，
    /// 其并发语义需与「在飞父操作占用判定」一并调整＝B2-γ 批次范围）。
    /// </summary>
    [Fact]
    public async Task ExternalStartSend_ReleasesGate_OtherRequestAdmittedDuringSend()
    {
        var inSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h => h.Sender = async _ =>
        {
            Interlocked.Increment(ref sends);
            inSend.TrySetResult();
            await releaseSend.Task;   // 模拟外部启动网络期（§24.14-2 要求此间不占权威串行权）
            return new SendOutcome.Accepted("ext:accepted", null);
        });

        var first = svc.SubmitAsync(Req(ns: "v2", workflow: "group:g1", operationType: OperationType.ExternalStart));
        await inSend.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 发送窗口内：另一笔请求必须能完成准入（结论不限：占用/合并/拒绝均可接受，关键是**未被阻塞**）。
        var second = await svc.SubmitAsync(Req(ns: "v2", workflow: "group:g2", operationType: OperationType.ExternalStart))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(second);

        releaseSend.TrySetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AdmissionResultKind.Accepted, firstResult.Kind);   // 锁外并发不改变本笔结算
        Assert.Equal(1, Volatile.Read(ref sends));                     // 发送仍恰好一次（无重发）
        Assert.Null(ReadLease().File!.Handoff!.Submission);            // 关闭已完成
    }

    // ── §24.36 范围残余：**宿主重建后**未决责任仍阻挡再次发送（[新增·2026-09-22 批次四十一]）──

    /// <summary>
    /// **重启/宿主重建后：未决发送责任仍然阻挡、不得新增许可、不得换键**（组件级模拟）：
    /// ①门面 A 使某一候选进入**不可考**（`Unknown`）⇒ 操作 `Reconciling`、许可水位 1、未决 `Submission` 在册；
    /// ②模拟重启：**同一租约目录**上经**接管观察**新建门面 B（新实例、新所有权代次）；
    /// ③门面 B 对**同一候选**再发起创建 ⇒ 仍必须因**未决发送**拒绝（`submission_conflict`），
    /// 且 **B 侧零发送**、原许可水位与发送身份**不变**（**不换键、不新增许可**）。
    /// 说明：这是**组件级**「宿主重建」；子进程级重启仍归 B4（§24.41-C#9）。
    /// </summary>
    [Fact]
    public async Task RestartAfterUnknown_SameCandidateStillBlocked_NoNewPermitNoKeyChange()
    {
        AdmissionRequest NewSameIdentity() => new()
        {
            Namespace = "successor",
            Kind = AdmissionKind.Create,
            SourceDetail = "fixture",
            OperationType = OperationType.NodeExecution,
            RunBinding = "run-r1",
            CursorRef = "n-r1#0#0",
            CursorRevision = 1,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:inst:ep1",
                Namespace = "successor",
                WorkflowId = "wf-r1",
                TriggerOccurrenceId = "manual:panel:r1",
                RunId = "run-r1",
                NodeId = "n-r1",
                Occurrence = 0,
                LoopIteration = 0,
                Attempt = 1,
                Tier = ArbitrationTier.Plan,
                PayloadFingerprint = "p-r1",
                ResourceRef = "node:n-r1",
                Intent = "start",
            },
            WireSubmitKey = "wire-r1",
        };

        // ① 门面 A：制造不可考（未决发送）
        var sendsA = 0;
        var (svcA, _, _, _) = BuildFacade(h =>
            h.Sender = _ => { Interlocked.Increment(ref sendsA); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); });
        var r1 = NewSameIdentity();
        Assert.Equal(AdmissionResultKind.Reconciling, (await svcA.SubmitAsync(r1)).Kind);
        Assert.Equal(1, sendsA);
        var op1 = FindOp(r1.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Reconciling, op1.RequestState);
        Assert.Equal(1, op1.LastSendSeq);
        var sub1 = ReadLease().File!.Handoff!.Submission!;
        Assert.Equal(op1.SubmissionIdentity, sub1.SubmissionIdentity);
        Assert.Equal(SubmissionState.Reconciling, sub1.State);

        // ② 模拟重启：同一租约目录上经**接管观察**新建门面 B（**不同所有者**＝新实例、新所有权代次）
        var leaseBefore = ReadLease().File!.Lease!;
        var sendsB = 0;
        var (svcB, _, _, _) = BuildFacade(h =>
            h.Sender = _ => { Interlocked.Increment(ref sendsB); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null, "job-new")); },
            takeover: true, ownerPid: "pid:testB");
        var leaseAfterTakeover = ReadLease().File!.Lease!;
        Assert.NotEqual(leaseBefore.OwnerEpoch, leaseAfterTakeover.OwnerEpoch);   // 所有权代次确实更替
        Assert.NotEqual(leaseBefore.LeaseId, leaseAfterTakeover.LeaseId);

        // ③ 门面 B 对同一候选再发起创建 ⇒ 未决责任仍阻挡；零发送、许可与身份不变
        var preObsBefore = ReadLease().File!.Handoff!.PreObservations?.Count ?? 0;
        var r2 = NewSameIdentity();
        var second = await svcB.SubmitAsync(r2);
        Assert.Equal(AdmissionResultKind.TerminalRejected, second.Kind);
        Assert.Equal("submission_conflict", second.ReasonCode);
        Assert.Equal(0, sendsB);                                          // **B 侧零发送**
        Assert.NotEqual(r1.RequestIdentity, r2.RequestIdentity);          // 两笔为**新创建**
        var op2 = FindOp(r2.RequestIdentity);
        if (op2 is not null)
        {
            Assert.Equal(op1.CandidateId, op2.CandidateId);               // 同一候选
            Assert.Equal(0, op2.LastSendSeq);                             // **B 侧未新增许可**
            Assert.True(string.IsNullOrEmpty(op2.SubmissionIdentity));
        }
        Assert.Equal(preObsBefore, ReadLease().File!.Handoff!.PreObservations?.Count ?? 0);   // 未新增预观察

        var opAfter = FindOp(r1.RequestIdentity)!;
        Assert.Equal(1, opAfter.LastSendSeq);                             // **不新增许可**
        Assert.Equal("wire-r1", opAfter.WireSubmitKey);                   // **不换键**
        Assert.Equal(op1.WireSubmitKey, opAfter.WireSubmitKey);
        var subAfter = ReadLease().File!.Handoff!.Submission!;
        Assert.Equal(op1.SubmissionIdentity, subAfter.SubmissionIdentity); // **不换发送身份**
        Assert.Equal(sub1.SendSeq, subAfter.SendSeq);
        Assert.Equal(SubmissionState.Reconciling, subAfter.State);         // 未决状态保持
        // 第二次被拒**不得**改变 B 的所有权（不得借这次拒绝重新接管/续期）
        var leaseAfterReject = ReadLease().File!.Lease!;
        Assert.Equal(leaseAfterTakeover.OwnerEpoch, leaseAfterReject.OwnerEpoch);
        Assert.Equal(leaseAfterTakeover.LeaseId, leaseAfterReject.LeaseId);
    }

    // ── §24.41-C#17：未决发送责任**阻挡再次发送**（门面级直接证据；[新增·2026-09-21 批次三十七]）──

    /// <summary>
    /// **未决责任阻挡再次发送（门面级直接证据）**：第 1 笔发送结果为**不可考**（`Unknown`）⇒ 该操作停在
    /// `Reconciling` 且**保留完整发送身份**；此时对**同一候选**发起新的创建（**绕过运行器意图预检**，直接进门面），
    /// 门面必须因**未决发送**拒绝（`submission_conflict`），并保证：**零新增发送**、**许可水位不推进**
    /// （`LastSendSeq` 仍为 1）、**未决发送身份与状态不变**（提交键/身份不被替换、`Reconciling` 保持）。
    /// 说明：本夹具钉住「**阻挡**」这一预期语义；后来被拒请求释放自己的槽位并保留墓碑审计记录。
    /// </summary>
    [Fact]
    public async Task UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance()
    {
        var sends = 0;
        var (svc, _, _, _) = BuildFacade(h =>
            h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); });

        AdmissionRequest NewSameIdentity() => new()
        {
            Namespace = "successor",
            Kind = AdmissionKind.Create,
            SourceDetail = "fixture",
            OperationType = OperationType.NodeExecution,
            RunBinding = "run-1",
            CursorRef = "n-1#0#0",
            CursorRevision = 1,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:inst:ep1",
                Namespace = "successor",
                WorkflowId = "wf-x",
                TriggerOccurrenceId = "manual:panel:same1",
                RunId = "run-1",
                NodeId = "n-1",
                Occurrence = 0,
                LoopIteration = 0,
                Attempt = 1,
                Tier = ArbitrationTier.Plan,
                PayloadFingerprint = "p1",
                ResourceRef = "node:n-1",
                Intent = "start",
            },
        };

        var r1 = NewSameIdentity();
        var first = await svc.SubmitAsync(r1);
        Assert.Equal(AdmissionResultKind.Reconciling, first.Kind);      // 不可考 ⇒ 保守停驻
        Assert.Equal(1, sends);
        var op1 = FindOp(r1.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Reconciling, op1.RequestState);
        Assert.Equal(1, op1.LastSendSeq);
        Assert.False(string.IsNullOrEmpty(op1.SubmissionIdentity));
        var sub1 = ReadLease().File!.Handoff!.Submission!;
        Assert.Equal(op1.SubmissionIdentity, sub1.SubmissionIdentity);
        Assert.Equal(SubmissionState.Reconciling, sub1.State);

        var continuation = NewSameIdentity();
        continuation.Kind = AdmissionKind.ContinueUse;
        continuation.RequestIdentity = r1.RequestIdentity;
        var continued = await svc.SubmitAsync(continuation);
        Assert.Equal(AdmissionResultKind.Reconciling, continued.Kind);
        Assert.Equal(ResponsibilityState.Pending, continued.ResponsibilityState);
        Assert.Equal(ExecutionDisposition.Unknown, continued.ExecutionDisposition);
        Assert.Equal(op1.SubmissionIdentity, continued.SubmissionIdentity);
        Assert.Equal(op1.LastSendSeq, continued.SendSeq);
        Assert.Equal(1, sends);

        // **绕过运行器意图预检**：直接对同一候选再发起创建 ⇒ 门面必须因未决发送拒绝
        var preObsBefore = ReadLease().File!.Handoff!.PreObservations?.Count ?? 0;
        var r2 = NewSameIdentity();
        var second = await svc.SubmitAsync(r2);
        Assert.Equal(AdmissionResultKind.TerminalRejected, second.Kind);
        Assert.Equal("submission_conflict", second.ReasonCode);
        Assert.Equal(1, sends);                                        // **零新增发送**

        // 语义：两笔为**同一候选的「新创建」**（请求身份不同、候选号相同 ⇒ 非续用短路）
        Assert.NotEqual(r1.RequestIdentity, r2.RequestIdentity);
        var op2 = FindOp(r2.RequestIdentity);                          // §24.44／C#19 待裁：被拒尝试「未登记」或「已登记」均可接受
        if (op2 is not null)
        {
            Assert.Equal(op1.CandidateId, op2.CandidateId);            // 同一候选
            Assert.Equal(0, op2.LastSendSeq);                          // **第二笔未签发发送许可**
            Assert.True(string.IsNullOrEmpty(op2.SubmissionIdentity)); // 也未形成发送身份
        }
        // 第二笔**不得**新增预观察记录（占位前发布的观察依据只属于第 1 笔）
        Assert.Equal(preObsBefore, ReadLease().File!.Handoff!.PreObservations?.Count ?? 0);

        // 第 1 笔在第二笔被拒之后**状态不变**（复核而非复用先前快照）
        var opAfter = FindOp(r1.RequestIdentity)!;
        Assert.Equal(OperationRequestState.Reconciling, opAfter.RequestState);
        Assert.Equal(1, opAfter.LastSendSeq);                          // **许可水位不推进**
        Assert.Equal(op1.SubmissionIdentity, opAfter.SubmissionIdentity);
        var subAfter = ReadLease().File!.Handoff!.Submission!;
        Assert.Equal(op1.SubmissionIdentity, subAfter.SubmissionIdentity);   // **提交键/发送身份不变**
        Assert.Equal(sub1.SendSeq, subAfter.SendSeq);
        Assert.Equal(SubmissionState.Reconciling, subAfter.State);           // 未决状态保持
    }

    // ── §17 P54：发送层三态 → 准入结果／责任维／许可水位的**显式映射**（[新增·2026-09-21 批次二十六]）──

    /// <summary>
    /// **§17 P54（接管失败映射的显式断言）**：发送层报告的三态必须在**准入结果层**逐项显式映射——
    /// ①**受理** ⇒ `Accepted`＋`IsTerminal=false`＋责任 **`Pending`**（远端作业仍在跑 ⇒ 执行责任未结清；
    /// **不得**因「本层发送责任已交付」而报 `Settled`）＋台账在册＋`Submission` 已关闭；
    /// ②**接管落盘失败**（`host:takeover_persist_failed`）⇒ `Reconciling`＋责任 **`Pending`**（**不得**报成功、
    /// **不得**反解为「确定未受理」）＋节点操作 `Reconciling`＋未决 `Submission` 在册＋`LastSendSeq==1`
    /// （许可已发布、**不得重发**）＋**零台账写入**；
    /// ③**副作用前确定拒绝** ⇒ `TerminalRejected`＋责任**已结清**＋`Submission` 已关闭＋零台账写入。
    /// </summary>
    [Theory]
    [InlineData("accepted")]
    [InlineData("takeover-persist-failed")]
    [InlineData("precheck-rejected")]
    [InlineData("retryable-rejected")]
    public async Task SendLayerOutcome_MapsToAdmissionResultAndResponsibility(string mode)
    {
        var sends = 0;
        var (svc, _, ledger, _) = BuildFacade(h =>
        {
            // **接管落盘失败必须被"真的制造"**（[会诊阻断处置]）：Sender 仍报**已受理（带 jobId）**，
            // 只让接管钩子 `TakeoverPersist` 失败（且**不写台账**）——这才覆盖「远端已受理、接管未落盘」的映射。
            h.Sender = _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult<SendOutcome>(mode switch
                {
                    "precheck-rejected" => new SendOutcome.Rejected("boundary_precheck_rejected", false, "host:boundary"),
                    // 可重试拒绝（§3.3 白名单内）：**首次**拒绝为可重试；**重试**（第 2 次发送）放行，
                    // 用于验证「重试须重新签发新许可（sendSeq 递增）」（§3.2a）。
                    "retryable-rejected" => Volatile.Read(ref sends) == 1
                        ? new SendOutcome.Rejected("queue_full", true, "host:precheck")
                        : new SendOutcome.Accepted("ext:accepted", null, "job-1"),
                    _ => new SendOutcome.Accepted("ext:accepted", null, "job-1"),
                });
            };
            if (mode == "takeover-persist-failed")
                h.TakeoverPersist = _ => Task.FromResult<string?>("takeover_persist_not_persisted");   // 失败且**不写台账**
        });
        var r = Req(operationType: OperationType.NodeExecution);
        r.Candidate.ResourceRef = "node:n-1";
        r.Candidate.NodeId = "n-1";
        var result = await svc.SubmitAsync(r);
        var op = FindOp(r.RequestIdentity)!;
        var handoff = ReadLease().File!.Handoff!;
        var ledgerEntries = ledger.Read().File?.Entries?.Count ?? 0;

        Assert.Equal(1, sends);
        switch (mode)
        {
            case "accepted":
                Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
                // 受理≠结清：远端作业存续 ⇒ 责任维为 `Pending`（§24.6-5）
                Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
                // 结果维：本层只有「发送受理」，**无权威终态** ⇒ `ExecutionDisposition.None`（终态只由完成层承载）
                Assert.Equal(ExecutionDisposition.None, result.ExecutionDisposition);
                Assert.Equal("job-1", result.JobId);
                Assert.Equal(OperationRequestState.Accepted, op.RequestState);
                Assert.Null(handoff.Submission);                                  // 关闭
                Assert.Equal(1, ledgerEntries);                                   // 接管台账在册
                var entry = ledger.Read().File!.Entries.Single();
                Assert.Equal(op.SubmissionIdentity, entry.SubmissionIdentity);    // 台账身份＝本轮发送身份
                Assert.Equal(op.LastSendSeq, entry.SendSeq);
                Assert.Equal("job-1", entry.JobId);
                Assert.Equal(op.CandidateId, entry.CandidateId);
                Assert.False(string.IsNullOrEmpty(entry.ActionId));               // 台账动作号（派生）不得为空
                break;
            case "takeover-persist-failed":
                Assert.Equal(AdmissionResultKind.Reconciling, result.Kind);
                Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);   // 责任保留（不得结清）
                // 结果维：**已受理但接管未落盘 ⇒ 结果不可考**（`Unknown`）——与「普通受理且无终态」的 `None` 区分
                Assert.Equal(ExecutionDisposition.Unknown, result.ExecutionDisposition);
                Assert.Equal(OperationRequestState.Reconciling, op.RequestState);
                Assert.False(string.IsNullOrEmpty(op.SubmissionIdentity));
                Assert.NotNull(handoff.Submission);                              // 未决发送责任在册
                Assert.Equal(op.SubmissionIdentity, handoff.Submission!.SubmissionIdentity);
                Assert.Equal(op.LastSendSeq, handoff.Submission.SendSeq);
                Assert.Equal(op.SubmissionIdentity, result.SubmissionIdentity);  // 结果↔操作↔责任三侧身份对齐
                Assert.Equal(op.LastSendSeq, result.SendSeq);
                Assert.Equal(1, op.LastSendSeq);                                 // 许可已发布；不得重发
                Assert.Equal(0, ledgerEntries);                                  // 接管未落盘 ⇒ **不得**写台账
                break;
            case "retryable-rejected":
                // **可重试拒绝**（§3.3 白名单内）：本轮发送责任**已确定结清**（无未决 `Submission`），
                // 但**许可确已签发并被本次尝试消费**（`LastSendSeq==1`）；后续重试须**重新签发新许可**（§3.2a）。
                Assert.Equal(AdmissionResultKind.RetryableRejected, result.Kind);
                Assert.Equal(ResponsibilityState.Settled, result.ResponsibilityState);
                // 操作状态＝**可重试拒绝**（与终局拒绝区分；后续重试须重新签发许可，§3.2a/§3.3）
                Assert.Equal(OperationRequestState.RetryableRejected, op.RequestState);
                Assert.Equal("queue_full", result.ReasonCode);                     // 精确原因码（`queue_full` 在可重试白名单）
                Assert.Equal("queue_full", op.LastResult?.ReasonCode);             // 落盘侧原因码同源
                Assert.True(op.LastResult?.Retryable == true);
                Assert.Equal(1, op.LastSendSeq);
                Assert.Null(handoff.Submission);
                Assert.Equal(0, ledgerEntries);
                // **重试须重新签发新许可**（§3.2a）：同身份重试 ⇒ `sendSeq` 递增（1→2），且本支 Sender 对第 2 次发送放行
                var retry = await svc.RetryAsync(r.RequestIdentity);
                Assert.Equal(AdmissionResultKind.Accepted, retry.Kind);
                Assert.Equal(2, FindOp(r.RequestIdentity)!.LastSendSeq);
                Assert.Equal(2, sends);
                Assert.Null(ReadLease().File!.Handoff!.Submission);      // 重试成功路径也已关闭
                Assert.Equal(1, ledger.Read().File?.Entries?.Count ?? 0); // 接管台账 1 条（重试受理）
                break;
            default:
                Assert.Equal(AdmissionResultKind.TerminalRejected, result.Kind);
                Assert.Equal(ResponsibilityState.Settled, result.ResponsibilityState);
                Assert.Equal(OperationRequestState.TerminalRejected, op.RequestState);
                Assert.Contains("boundary_precheck_rejected", result.ReasonCode);
                Assert.Contains("host:boundary", op.LastResult?.EvidenceSource ?? "");
                Assert.Null(handoff.Submission);
                Assert.Equal(0, ledgerEntries);
                break;
        }
    }

    // ── §17 P6：发送段取消令牌**按身份**原样透传（[新增·2026-09-21 批次二十三]）──

    /// <summary>
    /// **调用方令牌必须按身份原样到达 Sender**（§17 P6）：门面交给 Sender 的 `SubmissionDispatch.CallerToken`
    /// 必须等于调用方传入的**同一枚令牌**（`CancellationToken` 按底层源比较）——仅断言「可取消」不足以排除
    /// 「内部另建可取消令牌/链接令牌/budget 令牌」造成的误绿；并断言该令牌取消后可被发送段观察
    /// （`IsCancellationRequested`），即发送窗口取消真的能透到发送段。
    /// </summary>
    [Fact]
    public async Task Dispatch_CarriesCallerTokenByIdentity()
    {
        using var cts = new CancellationTokenSource();
        SubmissionDispatch? seen = null;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = d =>
            {
                seen = d;
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null));
            };
        });
        var r = Req();
        r.CallerToken = cts.Token;

        var result = await svc.SubmitAsync(r);

        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.NotNull(seen);
        Assert.True(seen!.CallerToken.CanBeCanceled);
        Assert.Equal(cts.Token, seen.CallerToken);   // **同一枚令牌**（按底层取消源比较），非「另一个可取消令牌」
        Assert.False(seen.CallerToken.IsCancellationRequested);
        cts.Cancel();
        Assert.True(seen.CallerToken.IsCancellationRequested);   // 取消真实可被发送段观察
    }

    /// <summary>
    /// **未显式提供令牌的调用方保持既有行为**（§17 P6 范围限定）：`CallerToken` 缺省＝`default`（不可取消），
    /// 门面**不得**自行铸造可取消令牌（那会伪造一个调用方从未持有的取消源）。
    /// </summary>
    [Fact]
    public async Task Dispatch_WithoutCallerToken_StaysNonCancelable()
    {
        SubmissionDispatch? seen = null;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            h.Sender = d =>
            {
                seen = d;
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null));
            };
        });

        var result = await svc.SubmitAsync(Req());   // 未设置 CallerToken

        Assert.Equal(AdmissionResultKind.Accepted, result.Kind);
        Assert.NotNull(seen);
        Assert.False(seen!.CallerToken.CanBeCanceled);   // 门面不得自行铸造令牌
    }

    // ── §16② 调用者≠获选者（§12.2 B1／§13.10 A1·A2；[Batch B 收尾·批次十八]）────────────

    /// <summary>
    /// **调用者的请求不得决定发送归属**（§12.2 B1／§13.10 A1·A2）：A（低优先级＝落选）与 B（高优先级＝获选）
    /// 并发入队于**同一轮次**，且两笔请求的**八段身份逐段取不同值**（调用者 A 的请求对象与其
    /// `ProcessLocalContext` 全程存活）；断言门面交给 Sender 的 `SubmissionDispatch` 的请求身份／
    /// **完整发送身份（按 B 的请求身份与 `sendSeq` 确定性派生）**／八段身份／载荷指纹／资源引用／Intent／
    /// 线上提交键／动作号／候选号（B 的确定性派生）／**进程内不可变上下文** **全部等于 B 的期望值**；
    /// A 自身停在 `NotSelected` 且**未发布发送许可**（`LastSendSeq==0`），本轮发送恰好一次（无双跑），
    /// 落盘台账只留 B 一笔、胜者 `Submission` 已关闭。
    /// **判定核心＝身份归属断言本身**：任何「按调用方请求／执行上下文／最新 Operation 拼装身份」的实现都会在
    /// 逐字段断言上变红。本用例**不**把「`AsyncLocal` 是否跨越 await 继续传播」设为合同——§12.2 B1 的判据是
    /// **发送归属**而非上下文流行性（生产即使在某处抑制上下文流动，身份仍必须属于获选者）；也不存在
    /// 「用处理上下文决定放行、再断言上下文属于谁」的闸门自证：放行条件只有「两笔入队收齐」。
    /// </summary>
    [Fact]
    public async Task CallerContextNotSelected_DispatchCarriesWinnerIdentityOnly()
    {
        var aCtx = new object();   // A（落选调用者）的进程内不可变请求上下文（§13.10 A1）
        var bCtx = new object();   // B（获选者）的进程内不可变请求上下文
        SubmissionDispatch? seen = null;
        LogicalOwnerLeaseFile? fileAtSend = null;
        var arrived = 0;
        var allEnqueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        ArbitrationLeaseStore? storeRef = null;
        var (svc, store, ledger, _) = BuildFacade(h =>
        {
            h.Barriers = new AdmissionBarriers
            {
                AfterEnqueue = () =>
                {
                    if (Interlocked.Increment(ref arrived) == 2) allEnqueued.TrySetResult();
                    return Task.CompletedTask;
                },
                // 放行条件**只有**「两笔入队收齐」（与执行上下文无关——谁启动的 drain 处理本轮都不影响结论）；
                // 等待**有界**，屏障异常/超时按门面既有语义响亮完成本轮（不留下悬挂提交任务）。
                BeforeRoundSnapshot = () => allEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            };
            h.Sender = d =>
            {
                Interlocked.Increment(ref sends);
                seen = d;
                fileAtSend = storeRef!.Read().File;
                return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null));
            };
        });
        storeRef = store;

        // A＝落选（低优先级）＋调用者上下文；B＝获选（高优先级）。八段身份逐段取不同值（含 scope/namespace）。
        var a = Req(ns: "v2", scope: "bgi:inst-a:ep1", payload: "p-A", workflow: "group:gA",
            trigger: "manual:panel:a1", wire: "wire-A", priority: 0);
        var b = Req(payload: "p-B", workflow: "group:gB", trigger: "manual:panel:b1", wire: "wire-B", priority: 5);
        b.Candidate.Scope = "bgi:inst-b:ep1";
        a.Candidate.ActionId = "act-A";
        b.Candidate.ActionId = "act-B";
        b.Candidate.RunId = "run-B";
        b.Candidate.NodeId = "n-B";
        b.Candidate.Occurrence = 3;
        b.Candidate.LoopIteration = 1;
        b.Candidate.Attempt = 2;
        a.ProcessLocalContext = aCtx;
        b.ProcessLocalContext = bCtx;

        var taskA = Task.Run(() => svc.SubmitAsync(a));
        var taskB = Task.Run(() => svc.SubmitAsync(b));
        var results = await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(10));

        var winner = results.Single(r => r.RequestIdentity == b.RequestIdentity);
        var loser = results.Single(r => r.RequestIdentity == a.RequestIdentity);
        Assert.Equal(AdmissionResultKind.Accepted, winner.Kind);      // 获选者＝B（高优先级）
        Assert.Equal(AdmissionResultKind.NotSelected, loser.Kind);    // 调用者＝A 落选
        Assert.Equal(1, Volatile.Read(ref sends));

        Assert.NotNull(seen);
        var d = seen!;
        // ① 请求身份／完整发送身份：全部等于获选者 B 在锁内原子发布的占位事实
        Assert.Equal(b.RequestIdentity, d.RequestIdentity);
        Assert.NotEqual(a.RequestIdentity, d.RequestIdentity);
        Assert.Equal("sub:" + b.RequestIdentity + ":1", d.SubmissionIdentity);   // 按 B 的请求身份与 sendSeq 确定性派生
        Assert.Equal(winner.SubmissionIdentity, d.SubmissionIdentity);
        Assert.Equal(1, d.SendSeq);
        Assert.Equal(SubmissionState.Submitting, fileAtSend!.Handoff!.Submission!.State);
        Assert.Equal(fileAtSend.Handoff.Submission.SubmissionIdentity, d.SubmissionIdentity);
        Assert.Equal(fileAtSend.Handoff.Submission.SendSeq, d.SendSeq);
        Assert.Equal(fileAtSend.Handoff.Submission.ActionId, d.ActionId);
        Assert.Equal(fileAtSend.Handoff.Submission.CandidateId, d.CandidateId);
        Assert.Equal(fileAtSend.Handoff.Submission.TargetEpoch, d.TargetEpoch);
        // ② 候选八段身份逐段绑定 B 的期望值（A/B 逐段取不同值——从 A／最新 Operation 拼入任一字段即红）
        Assert.Equal("bgi:inst-b:ep1", d.Candidate.Scope);
        Assert.NotEqual(a.Candidate.Scope, d.Candidate.Scope);
        Assert.Equal("manual", d.Candidate.Namespace);
        Assert.NotEqual(a.Candidate.Namespace, d.Candidate.Namespace);
        Assert.Equal("group:gB", d.Candidate.WorkflowId);
        Assert.Equal("manual:panel:b1", d.Candidate.TriggerOccurrenceId);
        Assert.Equal("run-B", d.Candidate.RunId);
        Assert.Equal("n-B", d.Candidate.NodeId);
        Assert.Equal(3, d.Candidate.Occurrence);
        Assert.Equal(1, d.Candidate.LoopIteration);
        Assert.Equal(2, d.Candidate.Attempt);
        Assert.Equal("p-B", d.Candidate.PayloadFingerprint);
        Assert.Equal("group:gB", d.Candidate.ResourceRef);
        Assert.Equal("start", d.Candidate.Intent);
        Assert.Equal("act-B", d.Candidate.ActionId);
        Assert.Equal(5, d.Candidate.Priority);
        // ③ 派发标量＝八段身份的确定性派生＋线上提交键＋目标纪元
        Assert.Equal("group:gB", d.ResourceRef);
        Assert.Equal("start", d.Intent);
        Assert.Equal("ep1", d.TargetEpoch);
        Assert.Equal("act-B", d.ActionId);
        Assert.Equal("wire-B", d.WireSubmitKey);
        Assert.Equal(ArbitrationOrdering.BuildStableIdentity(b.Candidate), d.StableIdentity);
        Assert.Equal(ArbitrationOrdering.DeriveCandidateId(ArbitrationOrdering.BuildStableIdentity(b.Candidate)), d.CandidateId);
        Assert.NotEqual(ArbitrationOrdering.BuildStableIdentity(a.Candidate), d.StableIdentity);
        Assert.NotSame(b.Candidate, d.Candidate);   // 派发携带内部冻结副本（B3：调用方后置修改不改变已登记事实）
        // ④ 进程内不可变上下文必须原样属于 B（§13.10 A1/A2：不得丢弃后由 Sender 重建）
        Assert.Same(bCtx, d.ProcessLocalContext);
        Assert.NotSame(aCtx, d.ProcessLocalContext);
        // ⑤ 落盘面无串写：A 未发布发送许可、台账只留 B 一笔
        var ops = ReadLease().File!.Handoff!.Operations;
        var opA = ops.Single(o => o.RequestIdentity == a.RequestIdentity);
        var opB = ops.Single(o => o.RequestIdentity == b.RequestIdentity);
        Assert.Equal(OperationRequestState.NotSelected, opA.RequestState);
        Assert.Equal(0, opA.LastSendSeq);
        Assert.Equal("p-A", opA.PayloadFingerprint);
        Assert.NotEqual(opA.CandidateId, opB.CandidateId);
        Assert.Equal(OperationRequestState.Accepted, opB.RequestState);
        Assert.Equal(1, opB.LastSendSeq);
        Assert.Equal("p-B", opB.PayloadFingerprint);
        Assert.Null(ReadLease().File!.Handoff!.Submission);            // 胜者 Submission 已关闭
        var entries = ledger.Read().File?.Entries;
        Assert.NotNull(entries);
        Assert.Single(entries!);
        Assert.Equal(d.SubmissionIdentity, entries![0].SubmissionIdentity);
    }
}
