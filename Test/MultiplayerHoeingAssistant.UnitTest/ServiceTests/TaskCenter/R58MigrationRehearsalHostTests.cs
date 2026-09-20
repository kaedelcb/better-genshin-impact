using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.8 §21.4「迁移演练」宿主入口** 夹具（施工方内置、owner 0 点击）：验证 `TaskCenterHost.RunMigrationRehearsal()`
/// 在**助手数据根下的独立目录**跑演练并返回报告，且**不接触真实 User 目录**。
/// **能力边界**：本夹具走**宿主服务入口**（尚未接线到 XAML 按钮 ⇒ 「owner 1 步点按钮」形态未达，§23.6 已登记）。
/// </summary>
public sealed class R58MigrationRehearsalHostTests : IDisposable
{
    private readonly string _dir;

    public R58MigrationRehearsalHostTests()
        => _dir = Path.Combine(Path.GetTempPath(), "r58h-" + Guid.NewGuid().ToString("N")[..8]);

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

        var report = host.RunMigrationRehearsal();     // 未注入 User 根来源

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        Assert.False(Directory.Exists(Path.Combine(_dir, "migration-rehearsal")));   // 写入前拒绝
    }

    [Fact]
    public void HostEntry_OverlappingUserRoot_RefusedBeforeAnyWrite()
    {
        var host = new TaskCenterHost(
            Path.Combine(_dir, "flows"), Path.Combine(_dir, "runs"), Path.Combine(_dir, "catalog.json"),
            () => null, () => true, () => null);
        host.UserConfigRootProvider = () => _dir;      // 伪造「真实 User 根」＝包含助手数据根

        var report = host.RunMigrationRehearsal();

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
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
}