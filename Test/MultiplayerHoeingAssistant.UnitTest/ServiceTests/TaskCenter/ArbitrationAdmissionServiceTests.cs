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
        Assert.False(ledger.MarkTerminal("sub:1:1", 1, "").Success); // 空证据=evidence_required
        Assert.True(ledger.MarkTerminal("sub:1:1", 1, "bgi_snapshot_terminal").Success);
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

    // ── 21. 租约文件 v1 向后读兼容（Pending 保留/接管写入升 2）──

    [Fact]
    public void LeaseV1_BackwardRead_TakeoverWritesUpgradeToV2()
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

        // 写入一律 version 2（接管路径：单调观察满 TTL+锁内复核）
        var observer = new LeaseTakeoverObserver(() => _mono);
        Assert.Null(observer.Observe(store.Read()));
        _mono += TimeSpan.FromSeconds(20);
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence);
        var acq = store.TryAcquire("pid:test2", evidence: evidence);
        Assert.True(acq.Success, "接管失败 " + acq.Reason);
        Assert.Equal(2, store.Read().File!.Version); // 发布单点升级
        Assert.Null(store.Read().File!.Handoff?.Pending); // Pending 段保留
        Assert.Equal(6, store.Read().File!.Revision); // 修订单调延续不回退
    }

    // ── 22. v3 Unsupported + 写入一律 version 2 ─────────────────

    [Fact]
    public async Task LeaseV3_Unsupported_LoudReject()
    {
        var (svc, _, _, _) = BuildFacade();
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json")).Replace("\"version\": 2", "\"version\": 3");
        File.WriteAllText(Path.Combine(_dir, "arbitration-lease.json"), text);
        var result = await svc.SubmitAsync(Req());
        Assert.Equal(AdmissionResultKind.Error, result.Kind);
        Assert.Equal("unsupported_version", result.ReasonCode);
        Assert.Equal(ArbitrationLeaseStatus.Unsupported, NewStore().Read().Status);
    }

    [Fact]
    public async Task Publish_AlwaysVersion2()
    {
        var (svc, _, _, _) = BuildFacade();
        _ = await svc.SubmitAsync(Req());
        var text = File.ReadAllText(Path.Combine(_dir, "arbitration-lease.json"));
        Assert.Contains("\"version\": 2", text);
        Assert.Equal(2, NewStore().Read().File!.Version);
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

        Assert.True(ledger.MarkTerminal(accepted.SubmissionIdentity!, accepted.SendSeq, "bgi:job_terminal").Success);
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
        r1.CursorRef = "cur:1";
        r1.CursorRevision = 5;
        var accepted = await svc.SubmitAsync(r1);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);

        var r2 = Req(workflow: "group:c2"); // 不同候选——只共享游标
        r2.CursorRef = "cur:1";
        r2.CursorRevision = 5;
        var second = await svc.SubmitAsync(r2);
        Assert.Equal(AdmissionResultKind.TerminalRejected, second.Kind);
        Assert.Equal("cursor_already_consumed", second.ReasonCode);
        Assert.Equal(1, sends); // 第二次未发送
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
}
