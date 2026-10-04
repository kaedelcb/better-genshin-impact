using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.8 §21.4「迁移演练」宿主入口** 夹具（施工方内置、owner 0 点击）：验证 `TaskCenterHost.RunMigrationRehearsal()`
/// 在**助手数据根下的独立目录**跑演练并返回报告，且**不接触真实 User 目录**。
/// **能力边界**：本夹具包含 XAML/VM 的**静态接线守卫**，但运行期点击仍需 owner 实机验证；
/// 生产解析器另由 `R58MigrationRehearsalTests` 直接覆盖（不得以替身 provider 测试替代）。
/// </summary>
public sealed class R58MigrationRehearsalHostTests : IDisposable
{
    private readonly string _dir;

    public R58MigrationRehearsalHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "r58h-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "assistant-config.json"), "{\"sentinel\":1}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void HostEntry_RunsRehearsal_UnderAssistantDataRoot()
    {
        var logs = new List<string>();
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null, logs.Add);
        host.UserConfigRootProvider = () => Path.Combine(_dir, "real-user");   // 非重叠「真实 User 根」来源

        var report = host.RunMigrationRehearsal();

        Assert.True(report.Success, report.Summary);
        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(report.RehearsalRoot), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("RolledBack", report.FinalStage);
        Assert.True(File.Exists(report.ManifestPath));
        Assert.Contains(logs, m => m.Contains("迁移演练"));
    }

    /// <summary>
    /// **UI 接线文本守卫（R5.8 §21.4）**：任务中心卡片须有「迁移演练」按钮并绑定 `TaskCenter.MigrationRehearsalCommand`，
    /// 且该命令调用宿主入口 `RunMigrationRehearsal()`（独立配置根）。
    /// **能力边界**：文本匹配，**不证明**运行期点击行为（UI 装配夹具未建立；owner 实机点击属 §23.1 S-演练）。
    /// </summary>
    [Fact]
    public void UiWiring_MigrationRehearsalButton_IsBound()
    {
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "Views", "MistletoePage.xaml"));
        var vm = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "ViewModels", "TaskCenterPanelViewModel.cs"));

        Assert.Contains("Content=\"迁移演练\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TaskCenter.MigrationRehearsalCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("MigrationRehearsalSummary", xaml, StringComparison.Ordinal);
        Assert.Contains("public RelayCommand MigrationRehearsalCommand", vm, StringComparison.Ordinal);
        Assert.Contains("_host.RunMigrationRehearsal()", vm, StringComparison.Ordinal);
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
        throw new InvalidOperationException("未能定位仓库根目录（UI 接线守卫需要源码路径）。");
    }
    [Fact]
    public void HostEntry_UserRootUnknown_RefusesConservatively()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();     // 未注入 User 根来源

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));   // 写入前拒绝
    }

    [Fact]
    public void HostEntry_OverlappingUserRoot_RefusedBeforeAnyWrite()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () => _dir;      // 伪造「真实 User 根」＝包含助手数据根
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));   // 写入前拒绝
    }

    [Fact]
    public void HostEntry_LogThrows_StillReturnsReport()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null, _ => throw new InvalidOperationException("日志抛错"));
        host.UserConfigRootProvider = () => Path.Combine(_dir, "real-user");

        var report = host.RunMigrationRehearsal();      // 日志异常不得吞掉报告

        Assert.True(report.Success, report.Summary);
        Assert.True(File.Exists(report.ManifestPath));
    }

    /// <summary>
    /// **来源异常必须保守拒绝**：`UserConfigRootProvider` 自身抛错时，宿主必须在报告边界内
    /// 转换为结构化拒绝，不能把异常传播给 UI；本场景允许诊断日志写入，
    /// 但不得写演练产物或改动配置。
    /// </summary>
    [Fact]
    public void HostEntry_UserRootProviderThrows_RefusesConservatively()
    {
        var sentinel = Path.Combine(_dir, "assistant-config.json");
        var logPath = Path.Combine(_dir, "host.log");
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null, _ => File.AppendAllText(logPath, "refused\n"));
        host.UserConfigRootProvider = () => throw new InvalidOperationException("来源故障");
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success
            && s.Detail.Contains("来源调用", StringComparison.Ordinal));
        Assert.True(File.Exists(logPath));                                     // 诊断日志允许，但仅此例外
        Assert.Equal("{\"sentinel\":1}", File.ReadAllText(sentinel));          // 配置内容哨兵
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));
    }

    /// <summary>非法路径来源同样必须在写入前形成结构化拒绝，且不创建任何演练产物。</summary>
    [Fact]
    public void HostEntry_UserRootProviderInvalidPath_RefusesBeforeWrite()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () => "bad\0user";
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));
    }

    [Fact]
    public void HostEntry_RepeatedRuns_UseFreshIndependentRoots()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () => Path.Combine(_dir, "real-user");

        var first = host.RunMigrationRehearsal();
        var second = host.RunMigrationRehearsal();

        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        Assert.NotEqual(first.RehearsalRoot, second.RehearsalRoot);
    }

    /// <summary>生产解析路径与宿主拒绝链一致性：User 目录不存在 ⇒ 生产解析返回 null ⇒ 宿主零演练产物拒绝。</summary>
    [Fact]
    public void HostEntry_ProductionResolverMissingUserDir_RefusesBeforeWrite()
    {
        var bgiDir = Path.Combine(_dir, "bgi");
        Directory.CreateDirectory(bgiDir);
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () => MainViewModel.TryResolveBgiUserConfigRoot(exePath);
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));
    }

    /// <summary>生产运行实例不匹配 ⇒ 生产判定核心返回 null ⇒ 宿主在写入前拒绝。</summary>
    [Fact]
    public void HostEntry_ProductionResolverRunningImageMismatch_RefusesBeforeWrite()
    {
        var configuredDir = Path.Combine(_dir, "configured");
        var runningDir = Path.Combine(_dir, "running");
        Directory.CreateDirectory(Path.Combine(configuredDir, "User"));
        Directory.CreateDirectory(runningDir);
        var configuredExe = Path.Combine(configuredDir, "BetterGenshinImpact.exe");
        var runningExe = Path.Combine(runningDir, "BetterGenshinImpact.exe");
        File.WriteAllText(configuredExe, "fake");
        File.WriteAllText(runningExe, "fake");
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () =>
            MainViewModel.ResolveBgiUserConfigRootCore(configuredExe, [runningExe]);
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));
    }

    /// <summary>严格枚举不完整 ⇒ 生产判定核心返回 null ⇒ 宿主在写入前拒绝。</summary>
    [Fact]
    public void HostEntry_ProductionResolverEnumerationIncomplete_RefusesBeforeWrite()
    {
        var bgiDir = Path.Combine(_dir, "bgi");
        Directory.CreateDirectory(Path.Combine(bgiDir, "User"));
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () =>
            MainViewModel.ResolveBgiUserConfigRootCore(exePath, [exePath], enumerationComplete: false);
        var before = SnapshotFiles();

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        AssertNoChangesExceptLog(before);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));
    }

    private Dictionary<string, byte[]> SnapshotFiles()
        => Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(_dir, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private void AssertNoChangesExceptLog(Dictionary<string, byte[]> before)
    {
        var after = SnapshotFiles();
        var changed = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path =>
                !before.TryGetValue(path, out var beforeBytes)
                || !after.TryGetValue(path, out var afterBytes)
                || !beforeBytes.AsSpan().SequenceEqual(afterBytes))
            .ToArray();
        Assert.All(changed, path => Assert.Equal("host.log", path));
    }
}
