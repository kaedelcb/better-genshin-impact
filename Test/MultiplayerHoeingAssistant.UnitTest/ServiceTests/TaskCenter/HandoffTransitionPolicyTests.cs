using System;
using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// HandoffTransitionPolicy（R5.1 冻结稿 v5 §4.1/§4.1a/§4.2）验收夹具：
/// - §4.1/§4.1a 冻结转换表：每条断言 (Next, SuppressionHeld, MayAdmitAuthorizedPreemptor, EnterIdle) 四元组；
/// - §4.1 权威退出四词唯一引用点 = IsAuthoritativeExitWord；
/// - §4.2 提交结果三态分类（受理词 / R4.10 副作用前拒绝白名单 / 其余 Unknown）；
/// - §6.3 接管观察器（单调计时，UTC 无关）与 §6.4 残件/版本前置判定。
/// 纯内存用例无副作用；涉盘用例自建临时目录，finally 清理（IDisposable）。
/// 本夹具不构建、不跑测试，仅承载合同断言。
/// </summary>
public class HandoffTransitionPolicyTests : IDisposable
{
    private readonly string _dir;

    public HandoffTransitionPolicyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "handoff-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 转换表四元组断言辅助 ─────────────────────────────────────

    private static void AssertTransition(
        HandoffPhase current,
        HandoffEventKind evt,
        bool executionFreeAndNoPending,
        HandoffPhase next,
        bool suppressionHeld,
        bool mayAdmitAuthorizedPreemptor,
        bool enterIdle)
    {
        var t = HandoffTransitionPolicy.Evaluate(current, evt, executionFreeAndNoPending);
        Assert.Equal(next, t.Next);
        Assert.Equal(suppressionHeld, t.SuppressionHeld);
        Assert.Equal(mayAdmitAuthorizedPreemptor, t.MayAdmitAuthorizedPreemptor);
        Assert.Equal(enterIdle, t.EnterIdle);
    }

    // ── 1. 取消受理≠已退出 ──────────────────────────────────────

    [Fact]
    public void CancelReceipt_IsNotExit()
    {
        // Confirming 不等于已退出：受理只进 Confirming 等权威终态；无关候选继续压制、不准入。
        AssertTransition(
            HandoffPhase.PreemptRequested,
            HandoffEventKind.CancelReceiptAccepted,
            executionFreeAndNoPending: false,
            next: HandoffPhase.Confirming,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 2. 权威退出允许交接（§4.1）────────────────────────────────

    [Fact]
    public void AuthoritativeExit_AllowsHandoff()
    {
        // 权威退出四词+身份/epoch/关联匹配 → 允许交接（授权方进入提交准入），压制解除、非 Idle。
        AssertTransition(
            HandoffPhase.Confirming,
            HandoffEventKind.AuthoritativeExitMatched,
            executionFreeAndNoPending: false,
            next: HandoffPhase.None,
            suppressionHeld: false,
            mayAdmitAuthorizedPreemptor: true,
            enterIdle: false);
    }

    // ── 3. A6 挂起/移交仅授权方准入（§4.1a）──────────────────────

    [Fact]
    public void A6Handover_OnlyAuthorizedAdmits()
    {
        // PreemptRequested + A6 确认：仅授权抢占方准入，压制保持至 settle。
        AssertTransition(
            HandoffPhase.PreemptRequested,
            HandoffEventKind.A6SuspendHandoverConfirmed,
            executionFreeAndNoPending: false,
            next: HandoffPhase.None,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: true,
            enterIdle: false);

        // Confirming + A6 确认走同一分支（不套用取消重跑路径）。
        AssertTransition(
            HandoffPhase.Confirming,
            HandoffEventKind.A6SuspendHandoverConfirmed,
            executionFreeAndNoPending: false,
            next: HandoffPhase.None,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: true,
            enterIdle: false);
    }

    // ── 4. 抢占方终态仅触发 settle（不解除压制）─────────────────

    [Fact]
    public void PreemptorTerminal_OnlySettle()
    {
        // 六轮 P2：仅 PreemptRequested/Confirming 可凭抢占方终态进 SettlePending（与 IsLegalPhaseAdvance 对齐）。
        foreach (var current in new[] { HandoffPhase.PreemptRequested, HandoffPhase.Confirming })
        {
            AssertTransition(
                current,
                HandoffEventKind.PreemptorTerminal,
                executionFreeAndNoPending: true,
                next: HandoffPhase.SettlePending,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);
        }

        // None（无交接上下文）/ReconcilePending（待对账期间事实未澄清，保留原责任约束）→ 保守兜底待对账，
        // 禁止待对账期间凭单一事件降级或重启责任阶段。
        foreach (var current in new[] { HandoffPhase.None, HandoffPhase.ReconcilePending })
        {
            AssertTransition(
                current,
                HandoffEventKind.PreemptorTerminal,
                executionFreeAndNoPending: true,
                next: HandoffPhase.ReconcilePending,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);
        }

        // 幂等（会诊二轮 P2-⑤）：settle/restore 期间重复或重放的抢占方终态不回退已选择的恢复进度。
        foreach (var current in new[] { HandoffPhase.SettlePending, HandoffPhase.RestorePending })
        {
            AssertTransition(
                current,
                HandoffEventKind.PreemptorTerminal,
                executionFreeAndNoPending: true,
                next: current,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);
        }

        // 重复受理回执幂等：Confirming 保持 Confirming（不退化为对账）。
        AssertTransition(
            HandoffPhase.Confirming,
            HandoffEventKind.CancelReceiptAccepted,
            executionFreeAndNoPending: false,
            next: HandoffPhase.Confirming,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 5. 选择原票据恢复保持压制 ───────────────────────────────

    [Fact]
    public void RestoreChosen_KeepsSuppression()
    {
        AssertTransition(
            HandoffPhase.SettlePending,
            HandoffEventKind.RestoreChosen,
            executionFreeAndNoPending: true,
            next: HandoffPhase.RestorePending,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 6. 原任务恢复确认 = 运行跟踪非 Idle（§4.1a）──────────────

    [Fact]
    public void RestoreConfirmed_TrackNotIdle()
    {
        AssertTransition(
            HandoffPhase.RestorePending,
            HandoffEventKind.RestoreConfirmed,
            executionFreeAndNoPending: true,
            next: HandoffPhase.None,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 7. 不恢复且协议终结：仅无占用无未决才允许 Idle ───────────

    [Fact]
    public void NoRestoreProtocolEnded_Idle_OnlyWhenFree()
    {
        // 无占用且无未决提交 → 允许空闲仲裁（Idle），解除压制。
        AssertTransition(
            HandoffPhase.SettlePending,
            HandoffEventKind.NoRestoreProtocolEnded,
            executionFreeAndNoPending: true,
            next: HandoffPhase.None,
            suppressionHeld: false,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: true);

        // 占用/未决提交未证实清空 → 保守待对账，禁止推导空闲。
        AssertTransition(
            HandoffPhase.SettlePending,
            HandoffEventKind.NoRestoreProtocolEnded,
            executionFreeAndNoPending: false,
            next: HandoffPhase.ReconcilePending,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 8. 超时/不符/未知 → 保守待对账 ──────────────────────────

    [Fact]
    public void TimeoutOrUnknown_Reconcile()
    {
        var currents = new[]
        {
            HandoffPhase.None, HandoffPhase.PreemptRequested, HandoffPhase.Confirming,
            HandoffPhase.SettlePending, HandoffPhase.RestorePending, HandoffPhase.ReconcilePending,
        };
        foreach (var current in currents)
        {
            AssertTransition(
                current,
                HandoffEventKind.TimeoutOrMismatchOrUnknown,
                executionFreeAndNoPending: true,
                next: HandoffPhase.ReconcilePending,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);
        }
    }

    // ── 9. 停止优先 + 迟到 succeeded 不撤销停止 ─────────────────

    [Fact]
    public void Stop_Priority_And_LateSucceeded()
    {
        var currents = new[]
        {
            HandoffPhase.None, HandoffPhase.PreemptRequested, HandoffPhase.Confirming,
            HandoffPhase.SettlePending, HandoffPhase.RestorePending, HandoffPhase.ReconcilePending,
        };
        foreach (var current in currents)
        {
            // Stop/F11/epoch 变化优先：交接状态保持（Next=current），压制保持、不准入。
            AssertTransition(
                current,
                HandoffEventKind.StopOrF11OrEpochChanged,
                executionFreeAndNoPending: true,
                next: current,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);

            // 迟到 succeeded 同样不改变交接状态（不撤销已生效停止）。
            AssertTransition(
                current,
                HandoffEventKind.LateSucceededArrived,
                executionFreeAndNoPending: true,
                next: current,
                suppressionHeld: true,
                mayAdmitAuthorizedPreemptor: false,
                enterIdle: false);
        }
    }

    // ── 10. 未冻结组合保守待对账 ────────────────────────────────

    [Fact]
    public void UnfrozenTransition_Conservative()
    {
        // Confirming + RestoreChosen 未在冻结转换表中 → 保守待对账。
        AssertTransition(
            HandoffPhase.Confirming,
            HandoffEventKind.RestoreChosen,
            executionFreeAndNoPending: true,
            next: HandoffPhase.ReconcilePending,
            suppressionHeld: true,
            mayAdmitAuthorizedPreemptor: false,
            enterIdle: false);
    }

    // ── 11. 权威退出四词（§4.1 唯一引用点）──────────────────────

    [Fact]
    public void AuthoritativeExitWord_FourWords()
    {
        Assert.True(HandoffTransitionPolicy.IsAuthoritativeExitWord("cancelled"));
        Assert.True(HandoffTransitionPolicy.IsAuthoritativeExitWord("failed"));
        Assert.True(HandoffTransitionPolicy.IsAuthoritativeExitWord("succeeded"));
        Assert.True(HandoffTransitionPolicy.IsAuthoritativeExitWord("skipped"));

        // skipped 不映射 skippedUser（映射待真实线路验证）。
        Assert.False(HandoffTransitionPolicy.IsAuthoritativeExitWord("skippedUser"));
        // 受理词不属权威退出集合。
        Assert.False(HandoffTransitionPolicy.IsAuthoritativeExitWord("accepted"));
        Assert.False(HandoffTransitionPolicy.IsAuthoritativeExitWord("queued"));
        // null → false（响亮拒绝，不猜）。
        Assert.False(HandoffTransitionPolicy.IsAuthoritativeExitWord(null));
    }

    // ── 12. 提交结果三态分类（§4.2）─────────────────────────────

    [Fact]
    public void Classifier_ThreeStates()
    {
        // 受理词（已观测三词兼容）→ Accepted。
        Assert.Equal(SubmissionVerdict.Accepted, SubmissionResultClassifier.Classify("queued", null));
        Assert.Equal(SubmissionVerdict.Accepted, SubmissionResultClassifier.Classify("accepted", null));
        Assert.Equal(SubmissionVerdict.Accepted, SubmissionResultClassifier.Classify("adopted", null));

        // R4.10 副作用前拒绝白名单 → Rejected（复用既有白名单，不另立词表）。
        Assert.Equal(SubmissionVerdict.Rejected, SubmissionResultClassifier.Classify(null, "stale_epoch"));
        Assert.Equal(SubmissionVerdict.Rejected, SubmissionResultClassifier.Classify(null, "capability_required"));

        // 无码/非受理词/白名单外码 → 一律 Unknown（不猜）。
        Assert.Equal(SubmissionVerdict.Unknown, SubmissionResultClassifier.Classify(null, null));
        Assert.Equal(SubmissionVerdict.Unknown, SubmissionResultClassifier.Classify("weird", null));
        Assert.Equal(SubmissionVerdict.Unknown, SubmissionResultClassifier.Classify(null, "unknown_code"));
    }

    // ── 13. 心跳持续不变达 TTL 才允许接管（§6.3）─────────────────

    [Fact]
    public void TakeoverObserver_UnchangedHeartbeatOverTtl_AllowsTakeover()
    {
        var t = TimeSpan.Zero;
        var ttl = TimeSpan.FromSeconds(15);
        var store = new ArbitrationLeaseStore(_dir); // 真实临时目录
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var observer = new LeaseTakeoverObserver(() => t); // 期限取自被观察租约 TtlSeconds=15

        // 首次观察建立计时基线 → 未成熟（无证据）。
        Assert.Null(observer.Observe(store.Read()));

        // 快进 TTL-1s → 仍未成熟。
        t = ttl - TimeSpan.FromSeconds(1);
        Assert.Null(observer.Observe(store.Read()));

        // 心跳前进（续期）→ 重置计时 → 未成熟。
        var rev = store.Read().File!.Revision;
        Assert.True(store.TryRenew(acq.Lease!.LeaseId, "pid:1", rev).Success);
        Assert.Null(observer.Observe(store.Read()));

        // 再快进满 TTL → 产出接管证据令牌。
        t += ttl;
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence);

        // 证据令牌锁内复核 → 接管成功（代次+1）；过期/伪证据（心跳不符）→ heartbeat_advanced。
        var reacq = store.TryAcquire("pid:2", 15, evidence);
        Assert.True(reacq.Success);
        Assert.Equal(2, reacq.Lease!.Generation);
    }

    // ── 14. 观察中更替所有者 → 重新计时（§6.3）───────────────────

    [Fact]
    public void TakeoverObserver_OwnerChange_Resets()
    {
        var t = TimeSpan.Zero;
        var ttl = TimeSpan.FromSeconds(15);
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        var observer = new LeaseTakeoverObserver(() => t); // 期限取自被观察租约 TtlSeconds=15

        // 观察所有者 A → 建立基线。
        Assert.Null(observer.Observe(store.Read()));

        // 时间已过 TTL（若未重置本应产证）。
        t = ttl + TimeSpan.FromSeconds(5);
        var evA = observer.Observe(store.Read());
        Assert.NotNull(evA);

        // 接管发生：新所有者 B 上位。
        var b = store.TryAcquire("pid:2", 15, evA);
        Assert.True(b.Success);

        // 观察到新所有者 → 重新计时 → 未成熟（即便单调钟已过 TTL）。
        Assert.Null(observer.Observe(store.Read()));

        // 新所有者心跳不变达 TTL 后 → 产出新证据。
        t += ttl;
        Assert.NotNull(observer.Observe(store.Read()));
    }

    // ── 15. UTC 拨动不影响单调观察（§6.3）───────────────────────

    [Fact]
    public void TakeoverObserver_UtcJump_DoesNotMatter()
    {
        var utc = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var t = TimeSpan.Zero;
        var ttl = TimeSpan.FromSeconds(15);
        var store = new ArbitrationLeaseStore(_dir, () => utc); // 注入可拨动 UTC
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var observer = new LeaseTakeoverObserver(() => t); // 期限取自被观察租约 TtlSeconds=15

        Assert.Null(observer.Observe(store.Read()));

        // UTC 快拨（会使租约诊断性过期，但单调观察结果不受影响）：未到 TTL → 未成熟。
        utc = utc.AddSeconds(1_000);
        t = ttl - TimeSpan.FromSeconds(1);
        Assert.Null(observer.Observe(store.Read()));

        // UTC 回拨：到 TTL → 产证。
        utc = utc.AddSeconds(-2_000);
        t += TimeSpan.FromSeconds(1);
        Assert.NotNull(observer.Observe(store.Read()));
    }

    // ── 16. 先判版本：未来版本 + 垃圾类型 = Unsupported 非 Corrupt ─

    [Fact]
    public void VersionFirst_GarbageFutureVersion_Unsupported_NotCorrupt()
    {
        Directory.CreateDirectory(_dir);
        var store = new ArbitrationLeaseStore(_dir);
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");

        // version 超前（>5：当前历史归档格式代）+ lease 垃圾类型 → 先判版本 → Unsupported（不降级解析、不当 Corrupt）。
        File.WriteAllText(leasePath, "{\"version\":6,\"lease\":\"垃圾类型\"}");
        Assert.Equal(ArbitrationLeaseStatus.Unsupported, store.Read().Status);

        // version=5（当前支持格式代）+ lease 垃圾类型 → 结构解析失败 = Corrupt。
        File.WriteAllText(leasePath, "{\"version\":5,\"lease\":\"垃圾类型\"}");
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, store.Read().Status);

        // version 缺失 → Corrupt（不当作合法 v1）。
        File.WriteAllText(leasePath, "{}");
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, store.Read().Status);

        // version=1 但 Lease 段结构非法（空身份/零代次）→ Corrupt（结构校验）。
        File.WriteAllText(leasePath, "{\"version\":1,\"lease\":{\"leaseId\":\"\",\"generation\":0}}");
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, store.Read().Status);
    }

    // ── 17. 残件阻断准入直至隔离（§6.4）─────────────────────────

    [Fact]
    public void Residue_BlocksAcquire_UntilQuarantined()
    {
        Directory.CreateDirectory(_dir);
        var store = new ArbitrationLeaseStore(_dir);
        var residuePath = Path.Combine(_dir, ".lease-x.tmp");
        File.WriteAllText(residuePath, "residue");

        // 正式文件缺失但有崩窗残件 → 拒获取（不得据此宣称无未决动作）。
        var acq = store.TryAcquire("pid:1");
        Assert.False(acq.Success);
        Assert.Equal("residue_uncertain", acq.Reason);

        // 控制面写入同样拒绝（不凭空建正式文件，否则后续读取不再检出残件）。
        var gate = store.SetSwitchGate(true, "x");
        Assert.False(gate.Success);
        Assert.Equal("residue_uncertain", gate.Reason);

        // 残件隔离：移入 _backup 留痕（不删除、不覆盖——目标名带时间戳后缀）。
        var quarantined = store.QuarantineResidues();
        Assert.True(quarantined.Success);
        Assert.False(File.Exists(residuePath));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "_backup"), ".lease-x.tmp.*"));

        // 隔离≠对账完成（P1-④复核）：准入约束持续 → 仍拒（residue_reconcile_pending）。
        var blocked = store.TryAcquire("pid:1");
        Assert.False(blocked.Success);
        Assert.Equal("residue_reconcile_pending", blocked.Reason);

        // 空对账依据 → 拒；关联证据对账后显式清除 → 允许新获取。
        Assert.Equal("evidence_required", store.ClearResidueUncertainty("").Reason);
        Assert.True(store.ClearResidueUncertainty("对账完成：BGI 作业列表查询无关联在跑（人工确认记录 #1）").Success);
        var acq2 = store.TryAcquire("pid:1");
        Assert.True(acq2.Success);
    }

    // ── 六轮 P2：策略后继与持久化合法性交叉一致（全阶段×全事件×未知枚举）─────

    [Fact]
    public void EvaluateSuccessors_AlwaysPersistentlyLegal()
    {
        var phases = new[]
        {
            HandoffPhase.None, HandoffPhase.PreemptRequested, HandoffPhase.Confirming,
            HandoffPhase.SettlePending, HandoffPhase.RestorePending, HandoffPhase.ReconcilePending,
            (HandoffPhase)999, // 未知枚举不得产生不可持久化的后继
        };
        foreach (var phase in phases)
        foreach (var evt in Enum.GetValues<HandoffEventKind>())
        foreach (var free in new[] { false, true })
        {
            var next = HandoffTransitionPolicy.Evaluate(phase, evt, free).Next;
            if (next == HandoffPhase.None || next == HandoffPhase.ReconcilePending || next == phase)
                continue; // →None 属证据消解路径（TryResolveIntent）；→ReconcilePending/原地=保守/幂等，均合法
            Assert.True(
                HandoffTransitionPolicy.IsLegalPhaseAdvance(phase, next),
                $"Evaluate({phase},{evt},{free})→{next} 但持久化推进拒该转换——两表分叉");
        }
    }

}
