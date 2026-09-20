using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.2 B4：提交点清单结构提醒守卫**（施工方内置、owner 0 点击）。
/// 纪律（设计稿 §15）：执行提交点必须登记。**本夹具的触发条件限于**：①出现**匹配模式的新文件**；
/// ②已登记文件的**匹配计数**变化。其余情形（同文件新增旁路、改常量/别名等）**不会**让本夹具失败。
/// **能力边界（如实，勿夸大）**：本夹具是**文本匹配计数**（非语义调用点识别），且**不读 §15 文档**——
/// 注释/同形字面量会误报，变量/别名/`"ext.task.start"` 字面量会漏报，同文件内新增旁路/重复发送/搬移方法计数不变。
/// 它只能保证「已登记文本模式的数量未变」，**不证明**提交点完整、更不证明「授权先于发送」。
/// </summary>
public sealed class SubmissionPointInventoryTests
{
    /// <summary>登记清单：生产源码中允许出现的「执行提交」调用点（文件 → 次数）。</summary>
    private static readonly Dictionary<string, (int V2TaskStart, int ExtTaskStart)> Allowed = new(StringComparer.Ordinal)
    {
        // v2 IPC task.start：三个方法各 2 处静态语句（首次 + 冲突重试）= 2+2+2；
        // 分别服务 E3/E5（S2/S3）与既有抢占/策略指定任务（S4a/S4b，见设计稿 §15）。
        ["MultiplayerHoeingAssistant/Services/CommandExecutor.cs"] = (6, 0),
        // ext.task.start（托管节点提交的唯一出口：SendPreparedAsync → 端口）。
        ["MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs"] = (0, 1),
        // ext.task.start（客户端薄封装：ext 任务队列通道经此提交）。
        ["MultiplayerHoeingAssistant/Services/BgiExternalClient.cs"] = (0, 1),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（提交点清单守卫需要源码路径）。");
    }

    [Fact]
    public void ProductionSubmissionPoints_MatchRegisteredInventory()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var found = new Dictionary<string, (int V2TaskStart, int ExtTaskStart)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var v2 = CountOccurrences(text, "OpCode = \"task.start\"");
            var ext = CountOccurrences(text, "ExternalOperations.TaskStart,");
            if (v2 > 0 || ext > 0) found[rel] = (v2, ext);
        }

        // ①不得出现含未登记匹配模式的文件（**仅覆盖文件粒度**；同文件内新增旁路调用不会被本守卫发现）
        var unregistered = found.Keys.Where(k => !Allowed.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0,
            "发现**含未登记匹配模式的文件**：[" + string.Join(", ", unregistered)
            + "]——必须先在设计稿 §15 提交点清单登记（含仲裁归属与门禁），再更新本守卫。");

        // ②登记清单中的每个提交点必须仍然存在（删除/搬迁需同步更新清单）
        foreach (var (path, expected) in Allowed)
        {
            Assert.True(found.TryGetValue(path, out var actual), $"登记提交点缺失（已删除/搬迁？）：{path}——请同步更新设计稿 §15 与本守卫。");
            Assert.True(actual == expected,
                $"匹配计数变化：{path} 期望 v2={expected.V2TaskStart}/ext={expected.ExtTaskStart}，"
                + $"实际 v2={actual.V2TaskStart}/ext={actual.ExtTaskStart}——请先更新 §15 登记再改本守卫（禁止只改数字消警）。");
        }

        // ③总闸门数守恒（新增/删除提交点必须显式改清单）
        Assert.Equal(Allowed.Count, found.Count);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>守卫自身逻辑的最小反例：计数函数不得漏计/误计（含重复出现与不出现）。</summary>
    [Theory]
    [InlineData("", "x", 0)]
    [InlineData("x", "x", 1)]
    [InlineData("xx", "x", 2)]
    [InlineData("a|x|b|x", "x", 2)]
    [InlineData("OpCode = \"task.start\"", "OpCode = \"task.start\"", 1)]
    public void CountOccurrences_IsExact(string text, string needle, int expected)
        => Assert.Equal(expected, CountOccurrences(text, needle));
}
