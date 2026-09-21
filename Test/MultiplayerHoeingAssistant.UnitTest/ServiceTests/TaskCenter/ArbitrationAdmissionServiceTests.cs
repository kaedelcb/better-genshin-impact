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
/// 登记后入队前崩溃恢复、清理安全（到期墓碑确实删除+旧身份 stale_operation_identity）、
/// 接管台账（幂等合并/身份冲突/权威终态）、租约文件 v1 向后读/v3 Unsupported/写入一律 v2。
/// 涉盘用例走临时目录，finally 清理；场景全部内置，owner 0 点击。
/// </summary>
public class ArbitrationAdmissionServiceTests : IDisposable
{
    private readonly string _dir;
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
        Action<AdmissionHooks>? configure = null, bool takeover = false)
    {
        var store = NewStore();
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null)),
            TakeoverPersist = entry =>
            {
                var r = ledger.RecordAccepted(entry);
                if (!r.Success) return Task.FromResult<string?>("record_failed:" + r.Reason);
                return Task.FromResult<string?>(ledger.ConfirmRebuildable(entry.SubmissionIdentity, entry.SendSeq) ? null : "not_rebuildable");
            },
        };
        configure?.Invoke(hooks);
        var svc = new ArbitrationAdmissionService(store, hooks, () => _now);
        if (!takeover)
        {
            var acq = svc.EnsureOwnership("pid:test");
            Assert.True(acq.Success, "夹具前置：获取租约失败 " + acq.Reason);
        }
        else
        {
            var observer = new LeaseTakeoverObserver(() => _mono);
            Assert.Null(observer.Observe(store.Read()));
            _mono += TimeSpan.FromSeconds(20);
            var evidence = observer.Observe(store.Read());
            Assert.NotNull(evidence);
            var acq = store.TryAcquire("pid:test", evidence: evidence);
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
        string scope = "bgi:inst:ep1", string? trigger = null, string? wire = null, int priority = 0)
        => new()
        {
            Namespace = ns,
            SourceDetail = "fixture",
            WireSubmitKey = wire,
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

    // ── 16. 清理安全：到期墓碑确实删除→重启→旧身份重放=stale_operation_identity ──

    [Fact]
    public async Task Cleanup_ExpiredTombstonePhysicallyRemoved_OldIdentityRejected()
    {
        var (svc, store, _, _) = BuildFacade();
        Prefill(store, tombstones: 1, pendingTransfers: 0);
        _now += TimeSpan.FromHours(25); // 越过 24h 保留期
        RenewLease(store);
        var ok = await svc.SubmitAsync(Req()); // 触发同边界清理迁移
        Assert.Equal(AdmissionResultKind.Accepted, ok.Kind);
        Assert.DoesNotContain(ReadLease().File!.Handoff!.Operations, o => o.RequestIdentity == "tomb0"); // 确实删除

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
        Assert.Equal("abandoned_before_send", op.LastResult!.ReasonCode);
        Assert.Equal(OperationZone.Tombstone, op.Zone); // 槽位最终可释放
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
        };
        Assert.True(ledger.RecordAccepted(entry).Success);
        Assert.True(ledger.RecordAccepted(entry).Success); // 重复接管幂等
        Assert.Single(ledger.Read().File!.Entries);

        var conflict = ledger.RecordAccepted(new ExternalStartLedgerEntry
        {
            SubmissionIdentity = "sub:1:1", SendSeq = 1, CandidateId = "cand-OTHER", ResourceRef = "group:g1",
            ActionId = "act-a", TargetBgiEpoch = "ep1", AcceptedAtUtc = _now, EvidenceSource = "ipc:queued",
        });
        Assert.False(conflict.Success);
        Assert.Equal("identity_conflict", conflict.Reason);
        Assert.Single(ledger.Read().File!.Entries); // 不产第二份

        Assert.True(ledger.ConfirmRebuildable("sub:1:1", 1));
        // §24.2-2″：观察时点由调用方传入（首写保存、幂等重试严格比对）；缺参数/空证据=响亮拒绝。
        Assert.False(ledger.MarkTerminal("sub:1:1", 1, "", _now).Success); // 空证据=evidence_required
        Assert.True(ledger.MarkTerminal("sub:1:1", 1, "bgi_snapshot_terminal", _now).Success);
        Assert.Empty(ledger.GetOccupancy().Entries); // Terminal 不再占用
        Assert.False(ledger.GetOccupancy().Unknown);
        Assert.True(ledger.ConfirmRebuildable("sub:1:1", 1)); // 完整终态记录同样证明「曾受理」（快速完成 job 不阻断结清）
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

    // ── 21. 租约文件 v1 向后读兼容（Pending 保留/接管写入升 3，R5.3 §24.20-A）──

    [Fact]
    public void LeaseV1_BackwardRead_TakeoverWritesUpgradeToV3()
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

        // 写入一律 version 3（接管路径：单调观察满 TTL+锁内复核）
        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(20);
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence);
        var acq = store.TryAcquire("pid:test2", evidence: evidence);
        Assert.True(acq.Success, "接管失败 " + acq.Reason);
        Assert.Equal(3, store.Read().File!.Version); // 发布单点升级（v2→v3：承载责任事实字段）
        Assert.Null(store.Read().File!.Handoff?.Pending); // Pending 段保留
        Assert.Equal(6, store.Read().File!.Revision); // 修订单调延续不回退
    }

    // ── 22. 更高版本 Unsupported + 写入一律 version 3（R5.3 §24.20-A）─────────────────

    [Fact]
    public async Task LeaseFutureVersion_Unsupported_LoudReject()
    {
        var (svc, _, _, _) = BuildFacade();
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json")).Replace("\"version\": 3", "\"version\": 4");
        File.WriteAllText(Path.Combine(_dir, "arbitration-lease.json"), text);
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("unsupported_version", result.ReasonCode);
        Assert.Equal(ArbitrationLeaseStatus.Unsupported, NewStore().Read().Status);
    }

    [Fact]
    public async Task Publish_AlwaysVersion3()
    {
        var (svc, _, _, _) = BuildFacade();
        _ = await svc.SubmitAsync(Req());
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json"));
        Assert.Contains("\"version\": 3", text);
        Assert.Equal(3, NewStore().Read().File!.Version);
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
        var r1 = Req(workflow: "group:c1");
        r1.RunBinding = "run:same";
        r1.CursorRef = "cur:1";
        r1.CursorRevision = 5;
        var accepted = await svc.SubmitAsync(r1);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var r2 = Req(workflow: "group:c2"); // 不同候选——只共享游标
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

        var r1 = Req(workflow: "group:c1");
        r1.RunBinding = "run:1";
        r1.CursorRef = "n-1#0#0";
        r1.CursorRevision = 5;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r1)).Kind);

        var r2 = Req(workflow: "group:c2"); // 另一运行：同游标引用、同修订
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

        var r1 = Req(workflow: "group:c1");
        r1.RunBinding = "run:1";
        r1.CursorRef = "n-1#0#0";
        r1.CursorRevision = 5;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(r1)).Kind);

        // 独立终局→迁区（主槽位释放），但记录仍在盘上
        Assert.Equal(AdmissionResultKind.Accepted,
            svc.MarkOperationTerminal(r1.RequestIdentity, "node_outcome:已观察节点权威终态").Kind);
        Assert.NotEqual(OperationZone.Active, FindOp(r1.RequestIdentity)!.Zone);

        var r2 = Req(workflow: "group:c2"); // 另一候选，但同一消费键
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
        Assert.Equal(loser.WinnerCandidateId, loserOp.LastResult!.WinnerRef); // 压制依据持久化（I5）
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
            var r = Req(workflow: "wf-" + i);
            r.RunBinding = "run-" + i;
            r.Candidate!.NodeId = "n-" + i;
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
        // 占槽构造：Sender 返回 **Unknown** ⇒ 操作进入 Reconciling（**Active，不结清、不迁区**）持续占主槽位；
        // 注意：`TerminalRejected` 会经 TerminalPendingTransfer→Tombstone **释放**主槽位，不适用于本负向夹具。
        var (svc, _, _, _) = BuildFacade(h => h.Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")));

        for (var i = 1; i <= 32; i++)
        {
            var r = Req(workflow: "wf-" + i);
            r.RunBinding = "run-" + i;
            r.Candidate!.NodeId = "n-" + i;
            r.CursorRef = "n-" + i + "#0#0";
            r.CursorRevision = 1;
            var res = await svc.SubmitAsync(r); // 不可考路径：操作停在 Reconciling ⇒ 持续占主槽位
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
        overflow.Candidate!.NodeId = "n-overflow";
        overflow.CursorRef = "n-overflow#0#0";
        overflow.CursorRevision = 1;
        var last = await svc.SubmitAsync(overflow);
        Assert.Equal(AdmissionResultKind.Error, last.Kind);
        Assert.StartsWith("operations_capacity_full", last.ReasonCode, StringComparison.Ordinal);
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
        Assert.Equal("ticket_suppressed", op.LastResult!.ReasonCode);  // 原因持久化
        Assert.Equal("ticket", op.LastResult.SuppressionSource);       // 压制来源持久化
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
        r.Candidate!.NodeId = "n-1";
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
        first.Candidate!.NodeId = "n-9";
        first.RunBinding = "run-9";
        first.CursorRef = "n-9#0#0";
        first.CursorRevision = 1;
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.SubmitAsync(first)).Kind);
        Assert.Equal(AdmissionResultKind.Accepted, svc.MarkOperationTerminal(first.RequestIdentity, "node_outcome:被切源归类=cancelled（远端确认后）").Kind);

        // 自动路径：不复活。
        Assert.Equal("already_terminal", (await svc.RetryAsync(first.RequestIdentity)).ReasonCode);

        // 显式新提交（同一窗口、同一节点、**另一个出现身份**）⇒ 新操作获准。
        var second = Req(trigger: "fixture:defer-b", workflow: "wf-defer");
        second.Candidate!.NodeId = "n-9";
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
        r.Candidate!.NodeId = "n-7";

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
        a1.Candidate.NodeId = "n-3";
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
        reuse.Candidate.NodeId = "n-3";
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
        a2.Candidate.NodeId = "n-3";
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
        bool noJobIdReadHook = false)
    {
        var ledger = new ExternalStartLedger(_dir, () => _now);
        var jobCounter = 0;
        var confirmCalls = 0;
        var jobIdReadCalls = 0;
        var (svc, _, _, _) = BuildFacade(h =>
        {
            // 每笔发送各自独立句柄（台账禁止同一 jobId 归属两笔发送：夹具按真实语义给唯一句柄）。
            h.Sender = _ => senderUnknown
                ? Task.FromResult<SendOutcome>(new SendOutcome.Unknown("adapter_unknown", "ext:adapter"))
                : Task.FromResult<SendOutcome>(new SendOutcome.Accepted(
                    "ext:accepted", null, "job-" + System.Threading.Interlocked.Increment(ref jobCounter)));
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
                h.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, err, job, source) =>
                {
                    var r = ledger.MarkTerminal(sub, seq, evidence + "-mismatch", observed,
                        (raw ?? "") + "-mismatch", err, OperationType.ExternalStart, job, source);
                    return r.Success ? null : "ledger_terminal_failed:" + (r.Reason ?? "unknown");
                };
                h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, err, job, source, observed) =>
                {
                    var entry = ledger.Read().File?.Entries.FirstOrDefault(e =>
                        string.Equals(e.SubmissionIdentity, sub, StringComparison.Ordinal) && e.SendSeq == seq);
                    return entry is { State: LedgerEntryState.Terminal }
                        && string.Equals(entry.TerminalEvidence, raw, StringComparison.Ordinal)
                        && string.Equals(entry.RawTerminal, raw, StringComparison.Ordinal)
                        && string.Equals(entry.ExecutionErrorCode, err, StringComparison.Ordinal)
                        && string.Equals(entry.JobId, job, StringComparison.Ordinal)
                        && string.Equals(entry.TerminalEvidenceSource, source, StringComparison.Ordinal)
                        && entry.TerminalObservedAtUtc == observed;
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
            h.TakeoverTerminalPersist = (sub, seq, evidence, observed, raw, err, job, source) =>
            {
                var r = ledger.MarkTerminal(sub, seq, evidence, observed, raw, err, OperationType.ExternalStart, job, source);
                return r.Success ? null : "ledger_terminal_failed:" + (r.Reason ?? "unknown");
            };
            h.TakeoverTerminalPayloadConfirmed = (sub, seq, raw, err, job, source, observed) =>
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
                    && entry.TerminalObservedAtUtc == observed;
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
    public async Task SettleCompletion_NonExternalOperationType_FailClosed()
    {
        var (svc, ledger) = BuildExternalFacadeWithCompletion();
        // 同名入口但操作类型非外部启动（模拟节点/流程操作误用完成结算入口）。
        var req = Req(ns: "manual", workflow: "flow:cfg", payload: "p-node");
        req.OperationType = OperationType.NodeExecution;
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
        Assert.Equal(AdmissionResultKind.Error, conflict.Kind);
        Assert.Equal("terminal_conflict", conflict.ReasonCode); // 冲突终态：不覆盖、不重放、不释放占用（§24.13-3）
        Assert.Equal(ResponsibilityState.Pending, conflict.ResponsibilityState); // 冲突断言需权威裁决
        Assert.Equal(ExecutionResultKind.Cancelled, FindOp(rid)!.ExecutionResult!.Kind);
        Assert.Equal("cancelled", Assert.Single(ledger.Read().File!.Entries).TerminalEvidence); // 台账仍为首次终态
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
}
