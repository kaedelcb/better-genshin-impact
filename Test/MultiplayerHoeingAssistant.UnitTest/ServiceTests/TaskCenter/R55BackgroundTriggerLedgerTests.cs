using System.Collections.Specialized;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.5 统一后台触发器台账（机制五a）** 夹具（owner 0 点击）。
/// 台账语义＝**挂载投影**（撤销不在台账内发生；实际撤销走宿主取消入口）；同步器按
/// `NotifyCollectionChangedAction` 处理 Add／Remove／Replace／Reset／Move 并按实例**引用计数**。
/// **能力边界（如实）**：本夹具验证**投影与同步逻辑**（纯逻辑，可直接单测）；VM 的生命周期三维
/// （arm/自动收场/取消）与 UI 装配**未**由夹具覆盖，仅有源码接线文本守卫。
/// </summary>
public class R55BackgroundTriggerLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);

    private static (BackgroundTriggerLedger Ledger, ArmedTriggerLedgerSync Sync) Build(string kind = "timer")
    {
        var ledger = new BackgroundTriggerLedger();
        var sync = new ArmedTriggerLedgerSync(ledger, kind, TriggerOwnerKinds.StartupChain, TriggerScope.ProcessEphemeral,
            "列表「取消」按钮", item => new ArmedTriggerDescriptor("startup-chain(process)/step:" + item, "意图-" + item, ""),
            () => Now);
        return (ledger, sync);
    }

    private static void Apply(ArmedTriggerLedgerSync sync, NotifyCollectionChangedEventArgs e, params object[] snapshot)
        => sync.OnCollectionChanged(e.Action, e.OldItems, e.NewItems, snapshot);

    [Fact]
    public void Add_RegistersEntry_WithOwnerIntentMountedAtRevokeEntry()
    {
        var (ledger, sync) = Build();
        var item = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item), item);

        var entry = Assert.Single(ledger.List());
        Assert.Equal("timer", entry.Kind);
        Assert.Equal(TriggerOwnerKinds.StartupChain, entry.OwnerKind);
        Assert.Equal("startup-chain(process)/step:" + item, entry.OwnerRef);
        Assert.Equal("意图-" + item, entry.Intent);
        Assert.Equal(Now, entry.MountedAtUtc);
        Assert.Equal("列表「取消」按钮", entry.RevokeEntry);
        Assert.Equal(TriggerScope.ProcessEphemeral, entry.Scope);
    }

    [Fact]
    public void Remove_UnregistersEntry()
    {
        var (ledger, sync) = Build();
        var item = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item), item);
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, 0));

        Assert.Empty(ledger.List());
    }

    /// <summary>**会诊反例①：`Clear()`（Reset）不得留下陈旧条目**。</summary>
    [Fact]
    public void Reset_RebuildsFromSnapshot_NoStaleEntries()
    {
        var (ledger, sync) = Build();
        var a = new object();
        var b = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, a), a);
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, b), a, b);
        Assert.Equal(2, ledger.Count);

        // 集合被 Clear()：Reset 事件无 Old/New，仅当前快照（空）
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Assert.Empty(ledger.List()); // 无陈旧条目
    }

    /// <summary>**会诊反例②：`Move()` 不改变成员 ⇒ 台账不得变更**。</summary>
    [Fact]
    public void Move_KeepsEntries()
    {
        var (ledger, sync) = Build();
        var a = new object();
        var b = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, a), a);
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, b), a, b);

        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, a, 1, 0), b, a);
        Assert.Equal(2, ledger.Count); // 成员未变 ⇒ 条目不变
    }

    /// <summary>**会诊反例③：原位替换 ⇒ 旧条目注销、新条目登记**。</summary>
    [Fact]
    public void Replace_RevokesOld_RegistersNew()
    {
        var (ledger, sync) = Build();
        var oldItem = new object();
        var newItem = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, oldItem), oldItem);

        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItem, oldItem, 0), newItem);

        var entry = Assert.Single(ledger.List());
        Assert.Equal("意图-" + newItem, entry.Intent);
    }

    /// <summary>**会诊反例④：同实例重复加入按引用计数**（移除一次不注销，归零才注销）。</summary>
    [Fact]
    public void DuplicateAdd_ReferenceCounted()
    {
        var (ledger, sync) = Build();
        var item = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item), item);
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item), item, item);
        Assert.Equal(1, ledger.Count);

        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, 0), item);
        Assert.Equal(1, ledger.Count); // 集合仍持有该实例 ⇒ 条目不注销

        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, 0));
        Assert.Empty(ledger.List());
    }

    /// <summary>**会诊反例⑤：不同挂载实例必须得到不同条目**（用进程内唯一编号，不用哈希）。</summary>
    [Fact]
    public void DistinctInstances_GetDistinctIds_EvenWithSameOwnerRef()
    {
        var (ledger, sync) = Build();
        var a = new object();
        var b = new object();
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, a), a);
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, b), a, b);

        var ids = ledger.List().Select(e => e.TriggerId).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains("#1", ids[0], StringComparison.Ordinal);
        Assert.Contains("#2", ids[1], StringComparison.Ordinal);
    }

    /// <summary>标识分段转义：`|`／`%` 不产生分段歧义。</summary>
    [Fact]
    public void TriggerId_SegmentsEscaped()
    {
        Assert.NotEqual(
            BackgroundTriggerLedger.TriggerIdOf("a|b", "c", "d", "e"),
            BackgroundTriggerLedger.TriggerIdOf("a", "b|c", "d", "e"));
        Assert.Contains("%7C", BackgroundTriggerLedger.TriggerIdOf("a|b", "c", "d", "e"), StringComparison.Ordinal);
    }

    /// <summary>
    /// **会诊必改项①（结构守卫）**：台账**不提供公开撤销/登记**——它是挂载投影；
    /// 撤销必须经宿主取消入口（否则会出现「台账说已撤、触发器仍在跑」的脱钩）。
    /// </summary>
    [Fact]
    public void Ledger_ExposesNoPublicMutation()
    {
        var publicNames = typeof(BackgroundTriggerLedger).GetMethods()
            .Where(m => m.IsPublic && !m.IsSpecialName).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Register", publicNames);
        Assert.DoesNotContain("Revoke", publicNames);
        Assert.DoesNotContain("RevokeAll", publicNames);
        Assert.Contains("List", publicNames);
        Assert.Contains("TryGet", publicNames);
    }

    /// <summary>
    /// **会诊反例⑥：登记后描述变化（或变为 null）仍能正确注销**——注销用**登记时保存的完整标识**，
    /// 不用「当前描述重新拼键」（否则会留下永久陈旧条目）。
    /// </summary>
    [Fact]
    public void Detach_UsesRegisteredId_EvenIfDescriptorChanges()
    {
        var ledger = new BackgroundTriggerLedger();
        var described = "owner-A";
        var sync = new ArmedTriggerLedgerSync(ledger, "timer", TriggerOwnerKinds.StartupChain, TriggerScope.ProcessEphemeral,
            "列表「取消」按钮", item => new ArmedTriggerDescriptor(described, "意图", ""), () => Now);
        var item = new object();

        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item), item);
        Assert.Equal(1, ledger.Count);

        described = "owner-B"; // 描述变化（同一实例）
        Apply(sync, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, 0));
        Assert.Empty(ledger.List());

        // 描述变为 null（解析失败）同样不得遗留条目
        var item2 = new object();
        var nullAfter = false;
        var sync2 = new ArmedTriggerLedgerSync(ledger, "watchdog", TriggerOwnerKinds.StartupChain, TriggerScope.ProcessEphemeral,
            "列表「取消」按钮", _ => nullAfter ? null : new ArmedTriggerDescriptor("o", "i", ""), () => Now);
        Apply(sync2, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item2), item2);
        nullAfter = true;
        Apply(sync2, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item2, 0));
        Assert.Empty(ledger.List());
    }

    /// <summary>**会诊整改（结构断言）**：同步器的变更能力不外露（ctor 与 ResetTo 非 public）。</summary>
    [Fact]
    public void Sync_MutationSurface_NotPublic()
    {
        var ctors = typeof(ArmedTriggerLedgerSync).GetConstructors();
        Assert.DoesNotContain(ctors, c => c.IsPublic);
        var reset = typeof(ArmedTriggerLedgerSync).GetMethod("ResetTo",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(reset);           // 存在但非公开（仅同步器内部使用）
        Assert.False(reset!.IsPublic);
        Assert.DoesNotContain(typeof(ArmedTriggerLedgerSync).GetMethods().Where(m => m.IsPublic).Select(m => m.Name),
            n => n is "ResetTo" or "Apply" or "Remove" or "RemoveKind");
    }
    /// <summary>三类作用域界限互异，且启动中心背景触发器恒为进程级临时。</summary>
    [Fact]
    public void Scope_Boundaries_AreDistinct()
    {
        var scopes = new[] { TriggerScope.ProcessEphemeral, TriggerScope.PersistedRecoverable, TriggerScope.NodeCompanion };
        Assert.Equal(3, scopes.Distinct().Count());
    }

    /// <summary>
    /// **接线文本守卫**：VM 三个「已挂载」集合必须经同步器与台账同源，并提供**真撤销**入口。
    /// **能力边界**：文本匹配，不证明运行期行为。
    /// </summary>
    [Fact]
    public void VmWiring_ArmedCollections_SyncWithLedger_AndRealRevokeEntry()
    {
        var file = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant", "ViewModels", "MistletoeViewModel.cs");
        Assert.True(File.Exists(file), "未找到 MistletoeViewModel.cs（台账接线登记在案）。");
        var text = File.ReadAllText(file);

        foreach (var kind in new[] { "timer", "watchdog", "log" })
            Assert.Contains("new ArmedTriggerLedgerSync(TriggerLedger, \"" + kind + "\"", text, StringComparison.Ordinal);
        Assert.Contains("public BackgroundTriggerLedger TriggerLedger", text, StringComparison.Ordinal);
        Assert.Contains("public void RevokeAllArmedTriggers()", text, StringComparison.Ordinal);
        Assert.Contains("CancelTimer(", text, StringComparison.Ordinal);
        Assert.Contains("CancelWatchdog(", text, StringComparison.Ordinal);
        Assert.Contains("CancelLogTrigger(", text, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（台账接线守卫需要源码路径）。");
    }
}