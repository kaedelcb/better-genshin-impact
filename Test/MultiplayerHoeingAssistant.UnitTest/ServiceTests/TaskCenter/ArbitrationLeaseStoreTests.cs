using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// ArbitrationLeaseStore（R5.1 §6）验收夹具：
/// 单文件三段（lease/handoff/diag）现实契约——
/// Revision 在文件级（每次写入+1，释放清空 Lease 段后仍单调延续）；
/// HeartbeatSeq 在 LeaseSegment 上（仅所有者锁内写入刷新）；
/// TryRenew/TryRelease/TryPublishIntent/TryResolveIntent 对 Corrupt 响亮拒 "corrupt"；
/// Read 对残留 ".tmp" 文件只在 Detail 留痕、不落盘。
/// 涉盘用例走临时目录，finally 清理。
/// </summary>
public class ArbitrationLeaseStoreTests : IDisposable
{
    private readonly string _dir;

    public ArbitrationLeaseStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lease-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 1. 首次获取 ──────────────────────────────────────────────

    [Fact]
    public void Acquire_First_Generation1()
    {
        var store = new ArbitrationLeaseStore(_dir);

        var acq = store.TryAcquire("pid:1");

        Assert.True(acq.Success);
        Assert.Null(acq.Reason);
        Assert.NotNull(acq.Lease);
        Assert.Equal(1, acq.Lease!.Generation);
        Assert.Equal(1, acq.Lease.HeartbeatSeq);
        Assert.Equal("pid:1", acq.Lease.OwnerEpoch);

        var read = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
        Assert.NotNull(read.File);
        Assert.Equal(1, read.File!.Revision);
    }

    // ── 2. 释放后重获：代次递增、文件级修订单调不回退 ────────────

    [Fact]
    public void Release_Reacquire_GenerationIncrements_RevisionMonotonic()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var first = store.TryAcquire("pid:1");
        Assert.True(first.Success);
        var oldLeaseId = first.Lease!.LeaseId;
        var revFirst = store.Read().File!.Revision;

        var rel = store.TryRelease(oldLeaseId, "pid:1", revFirst);
        Assert.True(rel.Success);
        Assert.Null(rel.Lease);

        var afterRelease = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Absent, afterRelease.Status);
        var revAfterRelease = afterRelease.File!.Revision;

        var second = store.TryAcquire("pid:2");
        Assert.True(second.Success);
        Assert.Equal(2, second.Lease!.Generation);
        Assert.Equal(revAfterRelease + 1, store.Read().File!.Revision);
        Assert.True(store.Read().File!.Revision > revAfterRelease); // 单调不回退

        // 旧 leaseId 不再有效（新租约已换 leaseId/Generation）。
        var renewOld = store.TryRenew(oldLeaseId, "pid:1", store.Read().File!.Revision);
        Assert.False(renewOld.Success);
        Assert.Equal("lease_stale_generation", renewOld.Reason);
    }

    // ── 3. 续期：代次不变、心跳递增、文件级修订递增 ──────────────

    [Fact]
    public void Renew_KeepsGeneration_AdvancesHeartbeat()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;
        var rev = store.Read().File!.Revision;

        var renew = store.TryRenew(leaseId, "pid:1", rev);

        Assert.True(renew.Success);
        Assert.Equal(1, renew.Lease!.Generation);      // 代次不变
        Assert.Equal(2, renew.Lease.HeartbeatSeq);     // 心跳+1
        Assert.Equal(rev + 1, store.Read().File!.Revision); // 文件级修订+1

        var newRev = store.Read().File!.Revision;
        Assert.Equal("lease_stale_generation",
            store.TryRenew(leaseId, "pid:1", expectedRevision: 999).Reason); // 错误 expectedRevision
        Assert.Equal("lease_stale_generation",
            store.TryRenew(leaseId, "other-owner", newRev).Reason);          // 错误 ownerEpoch
    }

    // ── 4. 过期不得续期复活，只能重新获取 ────────────────────────

    [Fact]
    public void Expired_CannotRenew_MustReacquire()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var mono = TimeSpan.Zero;
        var store = new ArbitrationLeaseStore(_dir, () => now, () => mono);

        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var leaseId = acq.Lease!.LeaseId;
        var rev = store.Read().File!.Revision;

        mono += TimeSpan.FromSeconds(16); // 单调钟过 TTL（§6.3：所有者资格判定用单调时间）
        now = now.AddSeconds(16);
        Assert.Equal(ArbitrationLeaseStatus.Expired, store.Read().Status); // Read 是 UTC 诊断态

        var renew = store.TryRenew(leaseId, "pid:1", rev);
        Assert.False(renew.Success);
        Assert.Equal("lease_expired_no_renew", renew.Reason); // §6.1 过期不得续期复活

        // §6.3：接管唯一依据=观察器单调观察满 TTL 产出的证据令牌+锁内复核；未观察直接获取 → held。
        Assert.Equal("held", store.TryAcquire("pid:1").Reason);
        var obsMono = TimeSpan.Zero;
        var observer = new LeaseTakeoverObserver(() => obsMono);
        Assert.Null(observer.Observe(store.Read())); // 首次观察启动计时
        obsMono += TimeSpan.FromSeconds(16);
        var evidence = observer.Observe(store.Read());
        Assert.NotNull(evidence); // 观察满 TTL 产出证据
        var reacq = store.TryAcquire("pid:1", 15, evidence);
        Assert.True(reacq.Success);
        Assert.Equal(2, reacq.Lease!.Generation); // 代次+1
    }

    // ── 5. 旧所有者不得操作新所有者租约 ──────────────────────────

    [Fact]
    public void OldOwner_CannotOperate_NewOwnerLease()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var store = new ArbitrationLeaseStore(_dir, () => now);

        var a = store.TryAcquire("pid:A", ttlSeconds: 15);
        var aLeaseId = a.Lease!.LeaseId;

        now = now.AddSeconds(16);
        Assert.Equal(ArbitrationLeaseStatus.Expired, store.Read().Status);

        Assert.Equal("held", store.TryAcquire("pid:B").Reason); // 未观察不得接管（即便 UTC 判过期）
        var obsMono5 = TimeSpan.Zero;
        var observer5 = new LeaseTakeoverObserver(() => obsMono5);
        Assert.Null(observer5.Observe(store.Read()));
        obsMono5 += TimeSpan.FromSeconds(16);
        var ev5 = observer5.Observe(store.Read());
        Assert.NotNull(ev5);
        var b = store.TryAcquire("pid:B", 15, ev5);
        Assert.True(b.Success);
        Assert.Equal(2, b.Lease!.Generation);
        var revB = store.Read().File!.Revision;

        // A 用旧 leaseId 对 B 的租约操作 → 拒。
        Assert.Equal("lease_stale_generation", store.TryRenew(aLeaseId, "pid:A", revB).Reason);
        Assert.Equal("lease_stale_generation", store.TryRelease(aLeaseId, "pid:A", revB).Reason);

        // B 的租约仍有效。
        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);
        Assert.Equal(b.Lease.LeaseId, store.Read().File!.Lease!.LeaseId);
    }

    // ── 6. 两实例经文件锁竞争唯一胜者 ────────────────────────────

    [Fact]
    public void TwoStores_ConcurrentAcquire_ExactlyOneWinner()
    {
        var s1 = new ArbitrationLeaseStore(_dir);
        var s2 = new ArbitrationLeaseStore(_dir);
        var totalSuccess = 0;

        for (int round = 0; round < 16; round++)
        {
            var results = new LeaseOpResult?[2];
            var barrier = new Barrier(2);

            Parallel.For(0, 2, k =>
            {
                var store = k == 0 ? s1 : s2;
                barrier.SignalAndWait(); // 两实例同时发起竞争
                try
                {
                    results[k] = store.TryAcquire("pid:" + k, ttlSeconds: 300);
                }
                catch (IOException)
                {
                    // 固定锁对象以 FileShare.None 打开：争用即共享冲突，视为未获锁。
                    results[k] = null;
                }
            });

            // 每次竞争恰好一个 Success（胜者持有租约，败者读 Valid→held）。
            var winners = results.Where(r => r is { Success: true }).ToList();
            Assert.Single(winners);
            totalSuccess += winners.Count;

            // 胜者让出，供下一轮竞争。
            for (int k = 0; k < 2; k++)
            {
                if (results[k] is not { Success: true } winner) continue;
                var store = k == 0 ? s1 : s2;
                var rev = store.Read().File!.Revision;
                var rel = store.TryRelease(winner.Lease!.LeaseId, "pid:" + k, rev);
                Assert.True(rel.Success);
            }
        }

        Assert.Equal(16, totalSuccess);
    }

    // ── 7. 五态 ─────────────────────────────────────────────────

    [Fact]
    public void Read_FiveStates()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var store = new ArbitrationLeaseStore(_dir, () => now);
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");

        // 无文件 → Absent
        Assert.Equal(ArbitrationLeaseStatus.Absent, store.Read().Status);

        // 写入乱码 → Corrupt，原件保留，二次 Read 仍 Corrupt（不降级 Absent）
        Directory.CreateDirectory(_dir);
        File.WriteAllText(leasePath, "{ 损坏");
        var bytesBefore = File.ReadAllBytes(leasePath);

        Assert.Equal(ArbitrationLeaseStatus.Corrupt, store.Read().Status);
        Assert.True(File.Exists(leasePath));
        Assert.Equal(bytesBefore, File.ReadAllBytes(leasePath));
        Assert.Equal(ArbitrationLeaseStatus.Corrupt, store.Read().Status);

        // 手写 version=3（高于支持版本 2，R5.2 §4.0）→ Unsupported
        File.WriteAllText(leasePath, "{\"version\":3}");
        Assert.Equal(ArbitrationLeaseStatus.Unsupported, store.Read().Status);

        // 手写 version=2 无 Lease 段 → Absent（v2 为当前支持格式代）
        File.WriteAllText(leasePath, "{\"version\":2}");
        Assert.Equal(ArbitrationLeaseStatus.Absent, store.Read().Status);

        // 正常获取 → Valid
        File.Delete(leasePath);
        Assert.True(store.TryAcquire("pid:1").Success);
        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);

        // 时钟快进 → Expired
        now = now.AddSeconds(20);
        Assert.Equal(ArbitrationLeaseStatus.Expired, store.Read().Status);
    }

    // ── 8. 原子准入边界：过期代次发布被拒 ────────────────────────

    [Fact]
    public void PublishIntent_StaleGeneration_Rejected()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;
        var rev = store.Read().File!.Revision;

        var pub = store.TryPublishIntent(leaseId, "pid:1", rev, MakeIntent("act-1"));
        Assert.True(pub.Success);

        // 重新 Read 验证 Handoff.Pending 已落盘
        var afterPublish = store.Read();
        Assert.Equal("act-1", afterPublish.File!.Handoff!.Pending!.ActionId);

        // 用过期 expectedRevision 再发 → lease_stale_generation
        var stale = store.TryPublishIntent(leaseId, "pid:1", rev, MakeIntent("act-1"));
        Assert.False(stale.Success);
        Assert.Equal("lease_stale_generation", stale.Reason);
    }

    // ── 9. 幂等发布 + 冲突拒 ────────────────────────────────────

    [Fact]
    public void PublishIntent_Idempotent_And_Conflict()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;
        var rev0 = store.Read().File!.Revision;

        Assert.True(store.TryPublishIntent(leaseId, "pid:1", rev0, MakeIntent("act-1")).Success);
        var rev1 = store.Read().File!.Revision;

        // 同 ActionId + 同内容重复发布 → 幂等成功（不改写）
        var again = store.TryPublishIntent(leaseId, "pid:1", rev1, MakeIntent("act-1"));
        Assert.True(again.Success);
        Assert.Equal(rev1, store.Read().File!.Revision);

        // 同 ActionId + 不同内容 → intent_conflict
        var changed = MakeIntent("act-1");
        changed.SuspendedRunIdentity = "run:changed";
        var conflict = store.TryPublishIntent(leaseId, "pid:1", rev1, changed);
        Assert.False(conflict.Success);
        Assert.Equal("intent_conflict", conflict.Reason);

        // 不同 ActionId → intent_conflict（已有未决意图不覆盖）
        var other = store.TryPublishIntent(leaseId, "pid:1", rev1, MakeIntent("act-2"));
        Assert.False(other.Success);
        Assert.Equal("intent_conflict", other.Reason);

        // 原未决意图未被覆盖
        Assert.Equal("act-1", store.Read().File!.Handoff!.Pending!.ActionId);
    }

    // ── 10. 更替继承未决意图 ────────────────────────────────────

    [Fact]
    public void Acquire_InheritsPendingHandoff()
    {
        var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var store = new ArbitrationLeaseStore(_dir, () => now);

        var a = store.TryAcquire("pid:A", ttlSeconds: 15);
        var aLeaseId = a.Lease!.LeaseId;
        var revA = store.Read().File!.Revision;
        Assert.True(store.TryPublishIntent(aLeaseId, "pid:A", revA, MakeIntent("act-hand")).Success);

        now = now.AddSeconds(16);
        Assert.Equal(ArbitrationLeaseStatus.Expired, store.Read().Status);

        Assert.Equal("held", store.TryAcquire("pid:B").Reason);
        var obsMono10 = TimeSpan.Zero;
        var observer10 = new LeaseTakeoverObserver(() => obsMono10);
        Assert.Null(observer10.Observe(store.Read()));
        obsMono10 += TimeSpan.FromSeconds(16);
        var b = store.TryAcquire("pid:B", 15, observer10.Observe(store.Read())); // 更替继承
        Assert.True(b.Success);
        Assert.Equal(2, b.Lease!.Generation);

        var readB = store.Read();
        Assert.Equal(ArbitrationLeaseStatus.Valid, readB.Status);
        Assert.NotNull(readB.File!.Handoff!.Pending);
        Assert.Equal("act-hand", readB.File.Handoff!.Pending!.ActionId);
    }

    // ── 11. 消解须基于权威证据 ─────────────────────────────────

    [Fact]
    public void ResolveIntent_RequiresAuthoritativeEvidence()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;
        var rev0 = store.Read().File!.Revision;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", rev0, MakeIntent("act-r")).Success);
        var rev1 = store.Read().File!.Revision;

        // 空证据/空事实（查询未命中、超时）→ evidence_required，Pending 仍在（§6.2 不能单独消解）
        Assert.Equal("evidence_required", store.TryResolveIntent(leaseId, "pid:1", rev1, null).Reason);
        Assert.Equal("evidence_required", store.TryResolveIntent(leaseId, "pid:1", rev1,
            new IntentResolveEvidence { ActionId = "act-r", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "" }).Reason);
        Assert.Equal("act-r", store.Read().File!.Handoff!.Pending!.ActionId);

        // 关联不符（旧 epoch 迟到事实/提交身份不符）→ evidence_mismatch，压制保持
        Assert.Equal("evidence_mismatch", store.TryResolveIntent(leaseId, "pid:1", rev1,
            new IntentResolveEvidence { ActionId = "act-r", SubmissionIdentity = "sub:1", Epoch = "epoch:OLD", ObservedFact = "cancelled" }).Reason);
        Assert.Equal("evidence_mismatch", store.TryResolveIntent(leaseId, "pid:1", rev1,
            new IntentResolveEvidence { ActionId = "act-r", SubmissionIdentity = "sub:OTHER", Epoch = "epoch:1", ObservedFact = "cancelled" }).Reason);

        // 关联权威证据（ActionId+提交身份+epoch 全匹配）→ 成功清除
        var resolved = store.TryResolveIntent(leaseId, "pid:1", rev1,
            new IntentResolveEvidence { ActionId = "act-r", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "cancelled" });
        Assert.True(resolved.Success);
        Assert.Null(store.Read().File!.Handoff!.Pending);
    }

    // ── 12. 切换闸门封锁意图发布 ───────────────────────────────

    [Fact]
    public void SwitchGate_BlocksPublish()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;

        Assert.True(store.SetSwitchGate(true, "test").Success);
        var rev1 = store.Read().File!.Revision;

        var blocked = store.TryPublishIntent(leaseId, "pid:1", rev1, MakeIntent("act-g"));
        Assert.False(blocked.Success);
        Assert.Equal("switch_gate_active", blocked.Reason);

        // 解除闸门 → 恢复可发布
        Assert.True(store.SetSwitchGate(false, null).Success);
        var rev2 = store.Read().File!.Revision;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", rev2, MakeIntent("act-g")).Success);
    }

    // ── 13. 诊断写入不刷新心跳 ─────────────────────────────────

    [Fact]
    public void DiagWrites_DoNotRefreshHeartbeat()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var h1 = acq.Lease!.HeartbeatSeq;
        var r1 = store.Read().File!.Revision;

        Assert.True(store.SetSwitchGate(true, "x").Success);
        Assert.True(store.RecordDiagNote("note-1").Success);

        var read = store.Read();
        Assert.Equal(h1, read.File!.Lease!.HeartbeatSeq);   // 心跳不续命
        Assert.True(read.File.Revision > r1);               // 文件级修订递增
        Assert.Equal(r1 + 2, read.File.Revision);
    }

    // ── 14. 残件按 Absent 处理且 Detail 留痕 ───────────────────

    [Fact]
    public void TmpResidue_TreatedAsAbsent_WithDetail()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, ".lease-abc.tmp"), "residue");

        var store = new ArbitrationLeaseStore(_dir);
        var read = store.Read();

        Assert.Equal(ArbitrationLeaseStatus.Absent, read.Status); // 残件按无正式文件处理
        Assert.False(string.IsNullOrEmpty(read.Detail));
        Assert.Contains(".lease-abc.tmp", read.Detail!);
    }

    // ── 15. 续期刷新单调基线（三轮 P1-① 反例）─────────────────────

    [Fact]
    public void Renew_RefreshesMonotonicBaseline()
    {
        var mono = TimeSpan.Zero;
        var store = new ArbitrationLeaseStore(_dir, monotonic: () => mono);
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var leaseId = acq.Lease!.LeaseId;

        mono += TimeSpan.FromSeconds(10); // t=10 续期成功
        Assert.True(store.TryRenew(leaseId, "pid:1", store.Read().File!.Revision).Success);

        // t=16（距获取已过 TTL，距续期仅 6s）：发布应按续期基线放行。
        mono += TimeSpan.FromSeconds(6);
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, MakeIntent("act-mono")).Success);
    }

    // ── 16. 所有者操作不受 UTC 双向跳变影响（三轮 P1-①）─────────────

    [Fact]
    public void OwnerOps_UtcJump_DoesNotMatter()
    {
        var utc = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var mono = TimeSpan.Zero;
        var store = new ArbitrationLeaseStore(_dir, () => utc, () => mono);
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var leaseId = acq.Lease!.LeaseId;

        // UTC 前跳 1 小时（诊断态 Expired），单调仅过 1s → 续期仍成功（UTC 不撤权）。
        utc = utc.AddHours(1);
        mono += TimeSpan.FromSeconds(1);
        Assert.Equal(ArbitrationLeaseStatus.Expired, store.Read().Status); // 诊断态
        Assert.True(store.TryRenew(leaseId, "pid:1", store.Read().File!.Revision).Success);

        // UTC 回拨 2 小时，单调过 TTL → 续期仍拒（UTC 不续命）。
        utc = utc.AddHours(-2);
        mono += TimeSpan.FromSeconds(16);
        Assert.Equal("lease_expired_no_renew", store.TryRenew(leaseId, "pid:1", store.Read().File!.Revision).Reason);
    }

    // ── 17. 已有所有者发布受残件对账约束阻断（三轮 P1-③）─────────────

    [Fact]
    public void PublishIntent_Blocked_ByResidueReconcilePending()
    {
        Directory.CreateDirectory(_dir);
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        Assert.True(acq.Success);
        var leaseId = acq.Lease!.LeaseId;

        // 运行中出现崩窗残件（另一写入者崩窗）→隔离 → 现有所有者发布同样被拒。
        File.WriteAllText(Path.Combine(_dir, ".lease-crash.tmp"), "residue");
        Assert.True(store.QuarantineResidues().Success);
        var blocked = store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, MakeIntent("act-blocked"));
        Assert.False(blocked.Success);
        Assert.Equal("residue_reconcile_pending", blocked.Reason);

        // 对账清除后恢复可发布。
        Assert.True(store.ClearResidueUncertainty("对账完成").Success);
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, MakeIntent("act-blocked")).Success);
    }

    // ── 18. 消解事实与交接阶段相容（三轮 P1-④）─────────────────────

    [Fact]
    public void ResolveIntent_PhaseCompatibleFacts()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;

        // RestorePending 阶段：权威退出四词+protocol_ended 均不得清除恢复责任（§4.1a 六词矩阵）。
        var intent = MakeIntent("act-restore");
        intent.Phase = HandoffPhase.RestorePending;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, intent).Success);
        var ev = new IntentResolveEvidence { ActionId = "act-restore", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "" };
        foreach (var fact in new[] { "cancelled", "failed", "succeeded", "skipped", "protocol_ended" })
        {
            ev.ObservedFact = fact;
            var revBefore = store.Read().File!.Revision;
            Assert.Equal("evidence_phase_incompatible", store.TryResolveIntent(leaseId, "pid:1", revBefore, ev).Reason);
            Assert.Equal(revBefore, store.Read().File!.Revision); // 拒绝后文件不变
        }
        Assert.NotNull(store.Read().File!.Handoff!.Pending); // 压制保持

        // restore_confirmed 才可消解。
        ev.ObservedFact = "restore_confirmed";
        Assert.True(store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ev).Success);

        // SettlePending 阶段：只认 protocol_ended（其余五词全拒；七轮 P3：拒绝前后文件字节不变）。
        var intent2 = MakeIntent("act-settle");
        intent2.Phase = HandoffPhase.SettlePending;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, intent2).Success);
        var bytesBefore = File.ReadAllBytes(Path.Combine(_dir, "arbitration-lease.json"));
        var ev2 = new IntentResolveEvidence { ActionId = "act-settle", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "" };
        foreach (var fact in new[] { "cancelled", "failed", "succeeded", "skipped", "restore_confirmed" })
        {
            ev2.ObservedFact = fact;
            Assert.Equal("evidence_phase_incompatible", store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ev2).Reason);
        }
        Assert.Equal(bytesBefore, File.ReadAllBytes(Path.Combine(_dir, "arbitration-lease.json")));
        ev2.ObservedFact = "protocol_ended";
        Assert.True(store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ev2).Success);
    }

    // ── 19. 观察期限取自租约 TTL（三轮 P1-②）─────────────────────

    [Fact]
    public void TakeoverObserver_TtlFromLease_NotCaller()
    {
        var mono = TimeSpan.Zero;
        var store = new ArbitrationLeaseStore(_dir, monotonic: () => mono);
        Assert.True(store.TryAcquire("pid:1", ttlSeconds: 30).Success); // 租约 TTL=30s
        var observer = new LeaseTakeoverObserver(() => mono); // 调用方不传期限

        Assert.Null(observer.Observe(store.Read()));
        mono += TimeSpan.FromSeconds(15); // 过了惯例 15s 但未满租约 30s → 未成熟
        Assert.Null(observer.Observe(store.Read()));
        mono += TimeSpan.FromSeconds(15); // 满 30s → 产证
        var ev = observer.Observe(store.Read());
        Assert.NotNull(ev);
        Assert.Equal(30, ev!.TtlSeconds);
        Assert.True(store.TryAcquire("pid:2", 30, ev).Success);
    }

    // ── 20. ReconcilePending 阶段消解严格化（四轮 P1-① + 五轮建议项矩阵）─────

    [Fact]
    public void ResolveIntent_Rejected_InReconcilePending()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;

        // 真实路径进入待对账：Confirming --(超时/事实未知)--> ReconcilePending（责任阶段持久化=Confirming）。
        var intent = MakeIntent("act-recon");
        intent.Phase = HandoffPhase.Confirming;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, intent).Success);
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-recon", HandoffPhase.ReconcilePending).Success);
        Assert.Equal(HandoffPhase.Confirming, store.Read().File!.Handoff!.Pending!.ReconcileFromPhase); // 责任阶段已落盘

        // ReconcilePending=保守待对账：权威退出四词与 settle 协议事实一律拒绝消解，持久化责任保持。
        foreach (var fact in new[] { "cancelled", "failed", "succeeded", "skipped", "restore_confirmed", "protocol_ended" })
        {
            var revBefore = store.Read().File!.Revision;
            var ev = new IntentResolveEvidence { ActionId = "act-recon", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = fact };
            Assert.Equal("evidence_phase_incompatible", store.TryResolveIntent(leaseId, "pid:1", revBefore, ev).Reason);
            Assert.Equal(revBefore, store.Read().File!.Revision); // 拒绝后文件不变
        }
        Assert.NotNull(store.Read().File!.Handoff!.Pending); // 压制保持

        // 退出待对账须回到原责任阶段（ReconcilePending→Confirming 合法，因 ReconcileFromPhase=Confirming）。
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-recon", HandoffPhase.Confirming).Success);
        Assert.Null(store.Read().File!.Handoff!.Pending!.ReconcileFromPhase); // 退出后清除
        var ok = new IntentResolveEvidence { ActionId = "act-recon", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "cancelled" };
        Assert.True(store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ok).Success);
    }

    [Fact]
    public void ResolveIntent_Rejected_InUnknownPhase()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;

        // 未知/未来枚举阶段：一律拒绝消解（不允许默认分支放行）。
        var intent = MakeIntent("act-unknown");
        intent.Phase = (HandoffPhase)999;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, intent).Success);
        var revBefore = store.Read().File!.Revision;
        var ev = new IntentResolveEvidence { ActionId = "act-unknown", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "cancelled" };
        Assert.Equal("evidence_phase_incompatible", store.TryResolveIntent(leaseId, "pid:1", revBefore, ev).Reason);
        Assert.Equal(revBefore, store.Read().File!.Revision); // 拒绝后文件不变
        Assert.NotNull(store.Read().File!.Handoff!.Pending);
    }

    // ── 21. 阶段推进持久化闭环 + 恢复责任不可降级（四轮 P1-③ / 五轮 P1-①②）─────

    [Fact]
    public void AdvanceIntentPhase_PersistedSettleRestoreLoop()
    {
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;

        // 真实初始阶段 PreemptRequested 起步（五轮 P1-②：受理回执转换必须持久化可达）。
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, MakeIntent("act-loop")).Success);
        Assert.Equal(HandoffPhase.PreemptRequested, store.Read().File!.Handoff!.Pending!.Phase);
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.Confirming).Success);
        Assert.Equal(HandoffPhase.Confirming, store.Read().File!.Handoff!.Pending!.Phase); // 落盘读回

        // 非法转换：Confirming→RestorePending 拒 illegal_transition；ActionId 不匹配拒 intent_not_found。
        Assert.Equal("illegal_transition", store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.RestorePending).Reason);
        Assert.Equal("intent_not_found", store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-other", HandoffPhase.SettlePending).Reason);

        // Confirming→SettlePending→RestorePending 逐步落盘（每步读回验证持久化）。
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.SettlePending).Success);
        Assert.Equal(HandoffPhase.SettlePending, store.Read().File!.Handoff!.Pending!.Phase);
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.RestorePending).Success);
        Assert.Equal(HandoffPhase.RestorePending, store.Read().File!.Handoff!.Pending!.Phase);

        // 五轮 P1-① 绕行反例：RestorePending→ReconcilePending（合法，责任阶段落盘=RestorePending）
        // → 试降回 Confirming/SettlePending 必须拒 illegal_transition（恢复责任不可降级）。
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.ReconcilePending).Success);
        Assert.Equal(HandoffPhase.RestorePending, store.Read().File!.Handoff!.Pending!.ReconcileFromPhase);
        Assert.Equal("illegal_transition", store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.Confirming).Reason);
        Assert.Equal("illegal_transition", store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.SettlePending).Reason);
        Assert.Equal(HandoffPhase.ReconcilePending, store.Read().File!.Handoff!.Pending!.Phase); // 拒绝后阶段不变
        // 回到原责任阶段合法。
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-loop", HandoffPhase.RestorePending).Success);

        // 闭环：restore_confirmed 消解，Pending 清除。
        var ev = new IntentResolveEvidence { ActionId = "act-loop", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "restore_confirmed" };
        Assert.True(store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ev).Success);
        Assert.Null(store.Read().File!.Handoff!.Pending);

        // 三连校验：旧修订号不得再推进任何阶段。
        Assert.Equal("lease_stale_generation", store.TryAdvanceIntentPhase(leaseId, "pid:1", 1, "act-x", HandoffPhase.ReconcilePending).Reason);
    }

    // ── 22. 发布失败窗口不续命（四轮 P1-② 反例；五轮 P2-③ 时序修正）─────

    [Fact]
    public void PublishFailure_DoesNotRefreshBaseline()
    {
        var mono = TimeSpan.Zero;
        var store = new ArbitrationLeaseStore(_dir, monotonic: () => mono);
        var acq = store.TryAcquire("pid:1", ttlSeconds: 15);
        Assert.True(acq.Success);
        var leaseId = acq.Lease!.LeaseId;

        mono += TimeSpan.FromSeconds(10);
        var revBefore = store.Read().File!.Revision;
        var seqBefore = store.Read().File!.Lease!.HeartbeatSeq;
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        // 以只读共享占用租约文件：读取可过，原子替换（File.Move overwrite）必失败 → 发布阶段真实失败。
        using (var hold = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Windows 上原子替换撞到占用句柄抛 IOException 或 UnauthorizedAccessException（均属发布失败）。
            var publishFailure = Record.Exception(() => store.TryRenew(leaseId, "pid:1", revBefore));
            Assert.True(publishFailure is IOException or UnauthorizedAccessException, $"应抛发布失败异常，实际: {publishFailure?.GetType().Name}");
        }
        // 发布失败不得改变正式文件内容。
        Assert.Equal(revBefore, store.Read().File!.Revision);
        Assert.Equal(seqBefore, store.Read().File!.Lease!.HeartbeatSeq);

        // 五轮 P2-③：t=16（失败后仅过 6s）检查——距成功基线 16s>15s 必须拒；
        // 若失败窗口错误刷新基线至 t=10，此处仅 6s<15s 会误放行（可检出原缺陷）。
        mono += TimeSpan.FromSeconds(6);
        Assert.Equal("lease_expired_no_renew", store.TryRenew(leaseId, "pid:1", store.Read().File!.Revision).Reason);
    }

    // ── 23. 缺 reconcileFromPhase 的待对账文件（六轮建议项：兼容合同）─────

    [Fact]
    public void ReconcilePending_WithoutFromPhase_ConservativeReject()
    {
        // 兼容合同（六轮建议项处置）：R5.1 租约文件为新建产物、无历史版本；reconcileFromPhase 缺字段只可能来自
        // 手改/损坏/未来降级写入。此时退出待对账与消解一律保守拒绝，处置=重新对账或人工清理，不自动放行。
        var store = new ArbitrationLeaseStore(_dir);
        var acq = store.TryAcquire("pid:1");
        var leaseId = acq.Lease!.LeaseId;
        var intent = MakeIntent("act-legacy");
        intent.Phase = HandoffPhase.Confirming;
        Assert.True(store.TryPublishIntent(leaseId, "pid:1", store.Read().File!.Revision, intent).Success);
        Assert.True(store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-legacy", HandoffPhase.ReconcilePending).Success);

        // 模拟缺字段文件：从 JSON 文本中移除 reconcileFromPhase 行（读回即为 null）。
        var leasePath = Path.Combine(_dir, "arbitration-lease.json");
        var json = File.ReadAllText(leasePath);
        var stripped = json.Replace("\"reconcileFromPhase\": 2,", ""); // Confirming=2（枚举序数序列化）
        Assert.NotEqual(json, stripped); // 确认字段确实被移除
        File.WriteAllText(leasePath, stripped);
        Assert.Null(store.Read().File!.Handoff!.Pending!.ReconcileFromPhase);

        // 退出待对账：缺责任阶段记录 → 一律 illegal_transition（保守，不猜目标阶段）。
        Assert.Equal("illegal_transition", store.TryAdvanceIntentPhase(leaseId, "pid:1", store.Read().File!.Revision, "act-legacy", HandoffPhase.Confirming).Reason);
        // 消解：ReconcilePending 阶段事实一律不相容。
        var ev = new IntentResolveEvidence { ActionId = "act-legacy", SubmissionIdentity = "sub:1", Epoch = "epoch:1", ObservedFact = "cancelled" };
        Assert.Equal("evidence_phase_incompatible", store.TryResolveIntent(leaseId, "pid:1", store.Read().File!.Revision, ev).Reason);
        Assert.NotNull(store.Read().File!.Handoff!.Pending); // 压制保持，等待人工/对账处置
    }

    // ── 辅助 ────────────────────────────────────────────────────

    private static PendingHandoffIntent MakeIntent(string actionId)
        => new()
        {
            ActionId = actionId,
            SuspendedRunIdentity = "run:suspended",
            AuthorizedPreemptor = "preemptor:1",
            TargetEpoch = "epoch:1",
            Phase = HandoffPhase.PreemptRequested,
            RestoreBranch = "restore:original",
            SubmissionIdentity = "sub:1",
        };
}
