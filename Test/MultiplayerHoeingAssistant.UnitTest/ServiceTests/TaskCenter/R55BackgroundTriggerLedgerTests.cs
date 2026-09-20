using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.5 统一后台触发器台账（机制五a）** 夹具（owner 0 点击）：
/// 登记／查询／撤销三件套、幂等（同标识不叠加）、按实例计数的挂载视图、三类作用域界限常量，
/// 以及「VM 已挂载集合 ↔ 台账同源同步」的**文本守卫**。
/// **能力边界（如实）**：文本守卫只证明源码中存在登记/撤销调用点，**不证明**运行期三维（arm/自动收场/取消）行为——
/// 那需要 UI 装配夹具，本阶段未建立；实际撤销仍走各触发器的既有取消入口。
/// </summary>
public class R55BackgroundTriggerLedgerTests
{
    private static BackgroundTriggerEntry Entry(string kind = "timer", string ownerRef = "startup-chain(process)/step:s1",
        string intentKey = "timer#1", string intent = "到点执行链（1 节点）")
        => new()
        {
            TriggerId = BackgroundTriggerLedger.TriggerIdOf(kind, TriggerOwnerKinds.StartupChain, ownerRef, intentKey),
            Kind = kind,
            OwnerKind = TriggerOwnerKinds.StartupChain,
            OwnerRef = ownerRef,
            MountedAtUtc = new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.Zero),
            Intent = intent,
            RevokeEntry = "流程视图「已挂载触发器」列表的「取消」按钮",
            Scope = TriggerScope.ProcessEphemeral,
        };

    [Fact]
    public void Register_List_Query_Revoke()
    {
        var ledger = new BackgroundTriggerLedger();
        var entry = Entry();

        var registered = ledger.Register(entry);
        Assert.Equal(entry.TriggerId, registered.TriggerId);
        Assert.Equal(1, ledger.Count);
        Assert.Single(ledger.List());
        Assert.Equal(entry.Intent, ledger.TryGet(entry.TriggerId)!.Intent);
        Assert.Equal(entry.MountedAtUtc, ledger.TryGet(entry.TriggerId)!.MountedAtUtc);
        Assert.Equal(entry.OwnerRef, ledger.TryGet(entry.TriggerId)!.OwnerRef);

        Assert.True(ledger.Revoke(entry.TriggerId));
        Assert.Equal(0, ledger.Count);
        Assert.Null(ledger.TryGet(entry.TriggerId));
        Assert.False(ledger.Revoke(entry.TriggerId)); // 幂等：再撤返回 false
    }

    /// <summary>同标识重复登记＝同一挂载（幂等，保留首次条目、不叠加）。</summary>
    [Fact]
    public void Register_SameId_IsIdempotent_KeepsFirst()
    {
        var ledger = new BackgroundTriggerLedger();
        var first = Entry();
        var second = Entry(intent: "被替换的意图");

        ledger.Register(first);
        var again = ledger.Register(second);

        Assert.Equal(1, ledger.Count);
        Assert.Equal(first.Intent, again.Intent);   // 保留首次
        Assert.Equal(first.Intent, ledger.List()[0].Intent);
    }

    /// <summary>按**挂载实例**计数：同一节点重复挂载＝多条（实例区分键进入标识）。</summary>
    [Fact]
    public void Register_PerInstance_MultipleEntries()
    {
        var ledger = new BackgroundTriggerLedger();
        ledger.Register(Entry(intentKey: "timer#1001"));
        ledger.Register(Entry(intentKey: "timer#1002"));
        Assert.Equal(2, ledger.Count);
    }

    [Fact]
    public void RevokeAll_ReturnsCount_AndClears()
    {
        var ledger = new BackgroundTriggerLedger();
        ledger.Register(Entry(kind: "timer", intentKey: "timer#1"));
        ledger.Register(Entry(kind: "watchdog", intentKey: "watchdog#2"));
        ledger.Register(Entry(kind: "log", intentKey: "log#3"));

        Assert.Equal(3, ledger.RevokeAll());
        Assert.Empty(ledger.List());
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void TriggerId_Deterministic_AndDistinguishesOwnerAndKind()
    {
        var a = BackgroundTriggerLedger.TriggerIdOf("timer", TriggerOwnerKinds.StartupChain, "ref", "k");
        Assert.Equal(a, BackgroundTriggerLedger.TriggerIdOf("timer", TriggerOwnerKinds.StartupChain, "ref", "k"));
        Assert.NotEqual(a, BackgroundTriggerLedger.TriggerIdOf("watchdog", TriggerOwnerKinds.StartupChain, "ref", "k"));
        Assert.NotEqual(a, BackgroundTriggerLedger.TriggerIdOf("timer", TriggerOwnerKinds.Manual, "ref", "k"));
        Assert.Contains("startup-chain", a, StringComparison.Ordinal);
    }

    /// <summary>**三类触发器界限入档**：作用域枚举三值互异，且启动中心背景触发器恒为进程级临时。</summary>
    [Fact]
    public void Scope_Boundaries_AreDistinct_AndStartupTriggersAreProcessEphemeral()
    {
        var scopes = new[] { TriggerScope.ProcessEphemeral, TriggerScope.PersistedRecoverable, TriggerScope.NodeCompanion };
        Assert.Equal(3, scopes.Distinct().Count());
        Assert.Equal(TriggerScope.ProcessEphemeral, Entry().Scope);
    }

    /// <summary>
    /// **接线文本守卫**：VM 的三个「已挂载」集合必须与台账**同源同步**（加入＝登记、移除＝撤销）。
    /// **能力边界**：文本匹配，不证明运行期行为、也不覆盖 arm 之外的反向访问路径。
    /// </summary>
    [Fact]
    public void VmWiring_ArmedCollections_SyncWithLedger()
    {
        var file = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant", "ViewModels", "MistletoeViewModel.cs");
        Assert.True(File.Exists(file), "未找到 MistletoeViewModel.cs（台账接线登记在案）。");
        var text = File.ReadAllText(file);

        foreach (var kind in new[] { "timer", "watchdog", "log" })
            Assert.Contains("SyncTriggerLedger(\"" + kind + "\"", text, StringComparison.Ordinal);
        Assert.Contains("TriggerLedger.Register(", text, StringComparison.Ordinal);
        Assert.Contains("TriggerLedger.Revoke(", text, StringComparison.Ordinal);
        Assert.Contains("public BackgroundTriggerLedger TriggerLedger", text, StringComparison.Ordinal);
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