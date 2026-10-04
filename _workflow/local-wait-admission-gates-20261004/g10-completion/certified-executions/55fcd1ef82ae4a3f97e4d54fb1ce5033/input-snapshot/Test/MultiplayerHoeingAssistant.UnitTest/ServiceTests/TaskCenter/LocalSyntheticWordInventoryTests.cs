using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.3.6（R-8 本地合成词）结构提醒守卫**（施工方内置、owner 0 点击）。
/// 纪律（设计稿 §18.4）：`cancelUnconfirmed` 是**助手本地合成词**，**不是 BGI 线路词**；
/// 其生产条件仅两处——①跳过请求后**远端终态不可考**（`terminal.Uncertain`）；②跳过确认**超时**（OCE 且非用户取消）。
/// **能力边界（如实，勿夸大）**：本守卫是**文本匹配计数**（非语义识别），且**不读 §18.4 文档**；
/// 注释/同形字面量会误报，变量/别名会漏报。它只保证「**已登记文本模式**的合成点计数未变」且「该词未出现在**已登记**的线路层文件」，
/// **不证明**远端没有同形词、也不替代 R5.8 的远端词表对照（远端线路＝本阶段不闭合）。
/// </summary>
public sealed class LocalSyntheticWordInventoryTests
{
    private const string Word = "cancelUnconfirmed";

    /// <summary>合成点登记（返回语句字面量——生产条件逐处对应；`WorkflowRunner` 为唯一合成文件）。</summary>
    private const int RegisteredSynthesisSites = 2;

    /// <summary>已登记的**线路层**文件（这些文件里出现该词＝把本地合成词误当线路词，必须响亮失败）。</summary>
    private static readonly string[] WireLayerFiles =
    [
        "MultiplayerHoeingAssistant/Services/BgiExternalClient.cs",
        "MultiplayerHoeingAssistant/Services/Gateway/GatewayProtocol.cs",
        "MultiplayerHoeingAssistant/Services/Gateway/GatewayEnvelope.cs",
        "MultiplayerHoeingAssistant/Services/SignalRClient.cs",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（本地合成词守卫需要源码路径）。");
    }

    [Fact]
    public void LocalSyntheticWord_NotPresentInWireLayerFiles()
    {
        var root = RepoRoot();
        // 会诊整改：登记的线路层文件**缺失即失败**（不得因 `Where(File.Exists)` 静默跳过而使守卫空转）。
        var missing = WireLayerFiles
            .Where(rel => !File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        Assert.True(missing.Count == 0,
            "登记的线路层文件缺失（本地合成词守卫必须响亮失败）：[" + string.Join(", ", missing) + "]");

        var offenders = WireLayerFiles
            .Where(rel => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
            .Where(rel => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))).Contains(Word, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "`" + Word + "` 出现在**线路层**文件：[" + string.Join(", ", offenders)
            + "]——该词是助手本地合成词，**不得**作为线路词收发（§18.4）。");
    }

    [Fact]
    public void LocalSyntheticWord_SynthesisSitesMatchRegisteredInventory()
    {
        var file = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant", "Services", "TaskCenter", "WorkflowRunner.cs");
        Assert.True(File.Exists(file), "未找到 WorkflowRunner.cs（本地合成词合成点登记在案）。");
        var text = File.ReadAllText(file);

        // 合成点＝`return ("cancelUnconfirmed"`（两个生产条件各一处）；数量变化必须同步本守卫与 §18.4 登记。
        var sites = CountOccurrences(text, "return (\"" + Word + "\"");
        Assert.True(sites == RegisteredSynthesisSites,
            "`" + Word + "` 合成点数量由 " + RegisteredSynthesisSites + " 变为 " + sites
            + "——须先在设计稿 §18.4 逐处登记生产条件，再更新本守卫。");
    }

    [Fact]
    public void LocalSyntheticWord_AggregateTreatsItAsBadOutcome()
    {
        var file = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant", "Services", "TaskCenter", "WorkflowRunner.cs");
        var text = File.ReadAllText(file);

        // 聚合判定必须把它计入坏结果（未确认不得被成功覆盖）——`hadBadOutcome` 行含该词。
        var hasAggregateCheck = text.Contains("\"failed\" or \"rejected\" or \"cancelled\" or \"" + Word + "\" or \"unknown\"", StringComparison.Ordinal);
        Assert.True(hasAggregateCheck, "聚合判定未把 `" + Word + "` 计入坏结果（不得被后续成功覆盖）。");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
