using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 6：低优先级本地持久等待**夹具。语义（owner B.1）：低优先级到来者登记为本地等待（**零发送、不入 BGI 队列**）；
/// 当前占用结束后按批次 4 的选择规则重判；更高优先级可插队；同身份重复登记幂等；重启后从落盘恢复重判；
/// 对应流程/票据失效的等待项被清理；文件损坏/版本过高响亮拒绝（不当作空队列放行）。
/// </summary>
public sealed class LocalWaitQueueTests
{
    private static LocalWaitItem Item(string identity, ArbitrationTier tier, int priority,
        bool hoeingHighest = false, bool trusted = true)
        => new()
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(identity),
            StableIdentity = identity,
            CandidateId = "cand-" + identity,
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = tier,
            Priority = priority,
            IsHoeingHighest = hoeingHighest,
            HasTrustedIdentity = trusted,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DecideEnqueue_OnlyWaitLocallyEnqueues_AndNeverPermitsSend()
    {
        var wait = LocalWaitQueuePolicy.DecideEnqueue(new RunningEncounter(
            RunningEncounterVerdict.WaitLocally, "更低", null));
        Assert.True(wait.Enqueued);
        Assert.False(wait.SendPermitted); // 等待项不携带发送许可

        foreach (var verdict in new[]
                 {
                     RunningEncounterVerdict.PreemptNow,
                     RunningEncounterVerdict.ProceedIdle,
                     RunningEncounterVerdict.HoldUnknownOccupant,
                     RunningEncounterVerdict.HoldFactsUnknown,
                 })
        {
            var decision = LocalWaitQueuePolicy.DecideEnqueue(new RunningEncounter(verdict, "x", null));
            Assert.False(decision.Enqueued);
            Assert.False(decision.SendPermitted);
        }
    }

    [Fact]
    public void DeriveItemId_IsDeterministicPerIdentity()
    {
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId("s-1"), LocalWaitQueuePolicy.DeriveItemId("s-1"));
        Assert.NotEqual(LocalWaitQueuePolicy.DeriveItemId("s-1"), LocalWaitQueuePolicy.DeriveItemId("s-2"));
    }

    [Fact]
    public void Store_UpsertIsIdempotent_AndRejectsConflictingPayload()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            Assert.True(store.Upsert(Item("s-1", ArbitrationTier.Plan, 0)));
            Assert.False(store.Upsert(Item("s-1", ArbitrationTier.Plan, 0))); // 幂等：不新增
            Assert.Single(store.Load());

            var before = File.ReadAllText(store.FilePath);
            // 同身份但登记载荷不同（级别/优先级/最高级标记/可信标记/流程等）⇒ 响亮冲突，且**原文件不变**
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(Item("s-1", ArbitrationTier.System, 5)));
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(Item("s-1", ArbitrationTier.Plan, 0, hoeingHighest: true)));
            var changed = Item("s-1", ArbitrationTier.Plan, 0);
            changed.WorkflowId = "other-workflow";
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(changed));
            var untrusted = Item("s-1", ArbitrationTier.Plan, 0, trusted: false);
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(untrusted));
            Assert.Equal(before, File.ReadAllText(store.FilePath));
            Assert.Single(store.Load());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_ReloadsFromDisk_AndSelectsNextByPolicyRule()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            store.Upsert(Item("s-plan", ArbitrationTier.Plan, 0));
            store.Upsert(Item("s-sys", ArbitrationTier.System, 5));
            store.Upsert(Item("s-hoeing", ArbitrationTier.Plan, 0, hoeingHighest: true));

            // 重启：新实例从落盘恢复并重新比较
            var reloaded = new LocalWaitQueueStore(dir);
            Assert.Equal(3, reloaded.Load().Count);
            Assert.Equal("s-hoeing", LocalWaitQueuePolicy.SelectNext(reloaded.Load())!.StableIdentity);

            reloaded.Remove(LocalWaitQueuePolicy.DeriveItemId("s-hoeing"));
            Assert.Equal("s-sys", LocalWaitQueuePolicy.SelectNext(reloaded.Load())!.StableIdentity);

            reloaded.Remove(LocalWaitQueuePolicy.DeriveItemId("s-sys"));
            Assert.Equal("s-plan", LocalWaitQueuePolicy.SelectNext(reloaded.Load())!.StableIdentity);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_PersistCleanup_CancelsInvalidatedItems_AndTheyNoLongerCompete()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            store.Upsert(Item("s-hoeing", ArbitrationTier.Plan, 0, hoeingHighest: true));
            store.Upsert(Item("s-sys", ArbitrationTier.System, 5));

            // "s-hoeing 对应流程/票据已失效" ⇒ 清理（置 Cancelled 并记录原因），不再参与选择
            var cleaned = store.PersistCleanup(
                item => item.StableIdentity == "s-hoeing" ? "ticket_invalidated" : null,
                DateTimeOffset.UtcNow);

            Assert.Equal(1, cleaned.Cancelled);
            Assert.Equal(0, cleaned.Pruned);
            var items = store.Load();
            var cancelled = items.Single(i => i.StableIdentity == "s-hoeing");
            Assert.Equal(LocalWaitItemState.Cancelled, cancelled.State);
            Assert.Equal("ticket_invalidated", cancelled.Reason);
            Assert.NotNull(cancelled.CancelledAtUtc);
            Assert.Equal("s-sys", LocalWaitQueuePolicy.SelectNext(items)!.StableIdentity);
            // 幂等：无变化不再写
            Assert.Equal(0, store.PersistCleanup(_ => null, DateTimeOffset.UtcNow).Cancelled);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_LoadRejectsCorruptOrFutureVersion_AndMissingFileIsEmpty()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            Assert.Empty(store.Load()); // 缺失 = 空集合

            File.WriteAllText(store.FilePath, "{ not json");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());

            File.WriteAllText(store.FilePath, "{\"version\":99,\"items\":[]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());

            File.WriteAllText(store.FilePath, "{\"version\":1,\"items\":[{\"itemId\":\"\",\"stableIdentity\":\"s\"}]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());

            // 缺 version / items:null / 重复 ItemId / 未定义枚举 ⇒ 一律响亮拒绝（不得当作空队列）
            File.WriteAllText(store.FilePath, "{\"items\":[]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            File.WriteAllText(store.FilePath, "{\"version\":1,\"items\":null}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            File.WriteAllText(store.FilePath, "{\"version\":1,\"items\":[null]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":["
                + "{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":0,\"priority\":0},"
                + "{\"itemId\":\"wait-a\",\"stableIdentity\":\"s2\",\"tier\":0,\"priority\":0}]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":99,\"priority\":0}]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            // state 必须是整数；字符串 state 不得被默认为 Waiting
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":0,\"priority\":0,\"state\":\"waiting\"}]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
            // 显式 null 同样不得落到缺省 Waiting（键存在即必须是整数）
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":0,\"priority\":0,\"state\":null}]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_UnreadablePathIsCorrupt_NotTreatedAsEmptyQueue()
    {
        var dir = TempDir();
        try
        {
            // 把固定文件名占成**目录**：读取必然失败 ⇒ 必须响亮拒绝，而不是返回空队列
            Directory.CreateDirectory(Path.Combine(dir, "wait-queue.json"));
            var store = new LocalWaitQueueStore(dir);

            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void NewItem_DefaultsToUntrusted()
    {
        // 调用方遗漏赋值 ⇒ 不得默认可信（保守）
        var item = new LocalWaitItem { ItemId = "wait-x", StableIdentity = "s-x" };

        Assert.False(item.HasTrustedIdentity);
    }

    [Fact]
    public void SelectNext_UntrustedHoeingFlag_DoesNotJumpAhead()
    {
        var untrusted = Item("s-untrusted", ArbitrationTier.Plan, 0, hoeingHighest: true, trusted: false);
        var system = Item("s-sys", ArbitrationTier.System, 1);

        // 自报 key 等不可信来源不得被当作最高级：System 项优先
        Assert.Equal("s-sys", LocalWaitQueuePolicy.SelectNext([untrusted, system])!.StableIdentity);
    }

    [Fact]
    public void SelectNext_UntrustedItemCannotOutrankTrustedBySelfReportedLevel()
    {
        // 自报 System/99 的不可信项不得越过可信 Plan/0 项（投影时按最低处理）
        var untrustedHigh = Item("s-untrusted-high", ArbitrationTier.System, 99, trusted: false);
        var trustedLow = Item("s-trusted-low", ArbitrationTier.Plan, 0);

        Assert.Equal("s-trusted-low", LocalWaitQueuePolicy.SelectNext([untrustedHigh, trustedLow])!.StableIdentity);
    }

    [Fact]
    public void Cleanup_IsPure_AndStoreAppliesDecisions()
    {
        var item = Item("s-x", ArbitrationTier.Plan, 0);
        var decisions = LocalWaitQueuePolicy.Cleanup([item], _ => "flow_cancelled", DateTimeOffset.UtcNow);

        Assert.Single(decisions);
        Assert.Equal("flow_cancelled", decisions[0].Reason);
        Assert.Equal(LocalWaitItemState.Waiting, item.State); // 纯函数：不改动输入对象
        Assert.Null(item.Reason);
    }

    [Fact]
    public void Store_ReactivatesCancelledItem_WhenSameRegistrationPayloadReturns()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            store.Upsert(Item("s-1", ArbitrationTier.System, 3));
            store.PersistCleanup(_ => "ticket_invalidated", DateTimeOffset.UtcNow);
            Assert.Equal(LocalWaitItemState.Cancelled, store.Load().Single().State);

            // 同身份同载荷再次到来 ⇒ 重新激活（视为同一请求再次登记）
            Assert.True(store.Upsert(Item("s-1", ArbitrationTier.System, 3)));
            var reactivated = store.Load().Single();
            Assert.Equal(LocalWaitItemState.Waiting, reactivated.State);
            Assert.Null(reactivated.Reason);
            Assert.Null(reactivated.CancelledAtUtc);

            // 再次登记同一等待项 ⇒ 幂等（不新增）
            Assert.False(store.Upsert(Item("s-1", ArbitrationTier.System, 3)));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_PrunesCancelledTombstonesAfterRetention()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir, cancelledRetention: TimeSpan.FromMinutes(10));
            store.Upsert(Item("s-old", ArbitrationTier.Plan, 0));
            var cancelledAt = DateTimeOffset.UtcNow;
            store.PersistCleanup(_ => "ticket_invalidated", cancelledAt);
            Assert.Single(store.Load());

            // 未到期：保留墓碑
            Assert.Equal(0, store.PersistCleanup(_ => null, cancelledAt.AddMinutes(5)).Pruned);
            Assert.Single(store.Load());

            // 到期：裁剪墓碑（防无界增长）
            Assert.Equal(1, store.PersistCleanup(_ => null, cancelledAt.AddMinutes(11)).Pruned);
            Assert.Empty(store.Load());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Store_MissingTrustedFlagIsTreatedAsUntrusted()
    {
        var dir = TempDir();
        try
        {
            var store = new LocalWaitQueueStore(dir);
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":2,\"priority\":9}]}");

            var item = store.Load().Single();

            Assert.False(item.HasTrustedIdentity); // 缺失 ⇒ 保守按不可信
            // 不可信项仍可被选中，但**不得越级**：可信 Plan/0 项必须排在它前面
            var trusted = Item("s-trusted", ArbitrationTier.Plan, 0);
            Assert.Equal("s-trusted", LocalWaitQueuePolicy.SelectNext([item, trusted])!.StableIdentity);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void EnqueueDecision_CannotBeForgedWithSendPermission()
    {
        // SendPermitted 恒 false 且类型不可由调用方构造 ⇒ 不是"可被外部改成 true"的普通记录
        Assert.Empty(typeof(LocalWaitEnqueueDecision).GetConstructors());
        Assert.False(LocalWaitQueuePolicy.DecideEnqueue(new RunningEncounter(
            RunningEncounterVerdict.WaitLocally, "更低", null)).SendPermitted);
    }

    [Fact]
    public void SelectNext_ReturnsNullWhenEmptyOrAllCancelled()
    {
        Assert.Null(LocalWaitQueuePolicy.SelectNext([]));
        Assert.Null(LocalWaitQueuePolicy.SelectNext(null));
        var cancelled = Item("s-x", ArbitrationTier.System, 9);
        cancelled.State = LocalWaitItemState.Cancelled;
        Assert.Null(LocalWaitQueuePolicy.SelectNext([cancelled]));
    }
}
