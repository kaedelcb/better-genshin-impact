using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6/R5.8「迁移演练」入口** 夹具（施工方内置、**owner 0 点击**）：验证一键演练在**独立配置根**上跑完整事务
/// （快照→引用更新→激活→回滚演练→提交→真实回滚→**逐字节比对**）并产出结构化报告；
/// 同时验证**拒绝**把真实 User 目录当作演练配置根。**本入口不执行真实 User 目录切换**（须 owner 另行下令）。
/// </summary>
public sealed class R58MigrationRehearsalTests : IDisposable
{
    private readonly string _root;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R58MigrationRehearsalTests()
        => _root = Path.Combine(Path.GetTempPath(), "r58m-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Rehearsal_RunsFullCycle_OnIndependentRoot_AndReports()
    {
        var report = MigrationRehearsal.Run(_root, userConfigRoot: Path.Combine(_root, "real-user"), () => Now);

        Assert.True(report.Success, report.Summary);
        Assert.All(report.Steps, s => Assert.True(s.Success, s.Name + ":" + s.Detail));
        Assert.Contains(report.Steps, s => s.Name.Contains("逐字节比对") && s.Success);
        Assert.Contains(report.Steps, s => s.Name.Contains("回滚演练") && s.Success);
        Assert.False(string.IsNullOrEmpty(report.SnapshotManifestHash));       // 快照清单哈希可取证
        Assert.True(File.Exists(report.ManifestPath));                         // manifest 可查
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(report.ConfigRoot), StringComparison.OrdinalIgnoreCase);
        // 独立根不变量：演练配置根与事务根都必须位于演练根内（因而与真实 User 目录无关；
        // 注意「临时目录路径里可能包含 Users 字样」，故不能按字符串判定是否真实 User 目录）
        Assert.True(Path.GetFullPath(report.TransactionRoot).StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rehearsal_RejectsRealUserConfigRoot()
    {
        // 防御性：若调用方把「独立配置根」传成真实 User 目录等同路径 ⇒ 拒绝
        var same = Path.Combine(_root, "independent-config-root");
        var report = MigrationRehearsal.Run(_root, userConfigRoot: same, () => Now);

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
    }

    [Fact]
    public void Rehearsal_RepeatedRuns_AreIndependent()
    {
        var first = MigrationRehearsal.Run(Path.Combine(_root, "run1"), null, () => Now);
        var second = MigrationRehearsal.Run(Path.Combine(_root, "run2"), null, () => Now);
        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        Assert.NotEqual(first.RehearsalRoot, second.RehearsalRoot);
    }
}