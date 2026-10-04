using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.7 旧补丁退出：静态残留守卫**（施工方内置、owner 0 点击）。
/// R3.1 已删除旧计划表/连续运行/定时循环的字段与 UI 写入者（见 R3.1 分解与审计 §8）；本守卫在**助手侧**持续锁定
/// 「这些旧标识符不得回归」。**能力边界（如实）**：①文本匹配（非语义识别），注释/同形字面量会误报、别名会漏报；
/// ②**只扫助手侧 `*.cs`**（不含 XAML/改名实现），范围为**12 个指定标识符**；BGI 侧（`BetterGenshinImpact/`）同类标识符以**一次性全仓扫描**取证（见 §22.2），
/// 其持续守卫归 R6 diff 收敛；③本守卫不证明「不存在两个调度器同时活跃」，并存防护论证见 §22.3。
/// </summary>
public sealed class LegacySchedulerResidueTests
{
    /// <summary>R3.1 删除的旧计划表/连续运行/定时循环标识符（字段或类型名）——助手侧不得出现。</summary>
    private static readonly string[] LegacyIdentifiers =
    [
        "_scheduleList",
        "_scheduleLoop",
        "_cycleTime",
        "_cycleMode",
        "_scheduleLoopSkip",
        "_scheduleStartOnTime",
        "_selectedOneDragonFlowPlanName",
        "_continuousCompletionAction",
        "ScheduleName",
        "ContinuousCompletionAction",
        "IndexId",
        "NextConfiguration",
    ];

    [Fact]
    public void Assistant_NoLegacySchedulerResidue()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            foreach (var name in LegacyIdentifiers)
            {
                if (text.Contains(name, StringComparison.Ordinal))
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + ":" + name);
            }
        }

        Assert.True(found.Count == 0,
            "助手侧发现旧调度残留（R5.7 旧补丁退出的删除项不得回归）：[" + string.Join(", ", found) + "]");
    }

    /// <summary>
    /// **三类触发器界限**（§20.1）在助手侧**只有**登记的那几种：启动中心临时（timer/watchdog/log）＋任务中心根级触发器
    /// 由 WorkflowKindCatalog 登记。本守卫锁定「旧计划表/连续运行类」不再以新名字偷偷回归的**可检出面**。
    /// </summary>
    [Fact]
    public void Assistant_SchedulerEntryPoints_AreRegisteredKindsOnly()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var banned = new[] { "schedule.plan", "schedule.continuous", "schedule.cycle", "trigger.cycle", "loop.schedule" };
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            foreach (var name in banned)
                if (text.Contains("\"" + name + "\"", StringComparison.Ordinal))
                    found.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + ":" + name);
        }
        Assert.True(found.Count == 0, "发现未登记的调度 kind 字面量：[" + string.Join(", ", found) + "]");
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
        throw new InvalidOperationException("未能定位仓库根目录（旧补丁残留守卫需要源码路径）。");
    }
}