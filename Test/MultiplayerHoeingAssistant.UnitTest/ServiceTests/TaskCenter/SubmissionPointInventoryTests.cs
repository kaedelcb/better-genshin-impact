using System.Text.RegularExpressions;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.2 B4：提交点清单结构提醒守卫**（施工方内置、owner 0 点击）。
/// 纪律（设计稿 §15）：执行提交点必须登记。**本夹具的触发条件限于**：①出现**匹配模式的新文件**；
/// ②已登记文件的**匹配计数**变化（**含同文件内新增「已匹配形态」的语句**——那会使计数变化并失败）。
/// **不会**被发现的形态：变量化／别名／拼装操作名、拼写不同或计数守恒式的等价替换（见下方能力边界）。
/// **能力边界（如实，勿夸大）**：本夹具是**文本匹配计数**（非语义调用点识别），且**不读 §15 文档**——
/// 注释/同形字面量会误报，变量/别名/`"ext.task.start"` 字面量会漏报，同文件内新增旁路/重复发送/搬移方法计数不变。
/// 它只能保证「已登记文本模式的数量未变」，**不证明**提交点完整、更不证明「授权先于发送」。
/// </summary>
public sealed class SubmissionPointInventoryTests
{
    /// <summary>
    /// **S5 粗化（[2026-09-21 批次三十四] §24.41-C#11）**：「**执行类操作**」的**直发字面量**也纳入结构守卫——
    /// 此前只覆盖 `task.start`／`ExternalOperations.TaskStart`，而通用 `SendCommandAsync(operation, …)` 的
    /// **停机／取消／热键执行**等直发点**没有守卫** ⇒ 新增未准入调用方不会被本清单发现。
    /// **能力边界（与既有守卫同口径，如实）**：文本匹配计数、非语义识别；变量/别名/拼装字面量会漏报；
    /// 同文件内新增同形语句会因计数变化被发现；**不证明**授权先于发送。
    /// </summary>
    private static readonly Dictionary<string, int[]> AllowedExecutionSends = new(StringComparer.Ordinal)
    {
        // 顺序＝[OpCode="task.start", OpCode="task.stop", OpCode="action.execute_hotkey",
        //         ext.TaskStart, ext.TaskStop, ext.TaskCancel]
        // 顺序＝[v2 task.start, v2 task.stop, v2 action.execute_hotkey, ext.TaskStart, ext.TaskStop, ext.TaskCancel,
        //         "ext.task.start", "ext.task.stop", "ext.task.cancel"]（末三项＝**ext 线协议字面量**，防新增硬编码直发）
        ["MultiplayerHoeingAssistant/Services/CommandExecutor.cs"] = [6, 1, 1, 0, 0, 0, 0, 0, 0],
        ["MultiplayerHoeingAssistant/Services/BgiExternalClient.cs"] = [0, 0, 0, 1, 1, 1, 1, 1, 1],
        ["MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs"] = [0, 0, 0, 1, 0, 0, 0, 0, 0],
    };

    private static readonly string[] ExecutionSendPatterns =
    [
        "OpCode = \"task.start\"", "OpCode = \"task.stop\"", "OpCode = \"action.execute_hotkey\"",
        "ExternalOperations.TaskStart,", "ExternalOperations.TaskStop,", "ExternalOperations.TaskCancel,",
        // ext 线协议**字面量**（常量定义或硬编码直发都计入）：任何新文件硬编码这些词即被标记
        "\"ext.task.start\"", "\"ext.task.stop\"", "\"ext.task.cancel\"",
    ];

    [Fact]
    public void ProductionExecutionSends_MatchRegisteredInventory()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var found = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            var counts = ExecutionSendPatterns.Select(p => CountOccurrences(text, p)).ToArray();
            if (counts.Sum() > 0) found[rel] = counts;
        }

        // ①不得出现含未登记执行类直发字面量的文件（仅文件粒度；同文件新增旁路不入本守卫能力范围）
        var unregistered = found.Keys.Where(k => !AllowedExecutionSends.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0,
            "发现**含未登记「执行类」直发字面量的文件**：[" + string.Join(", ", unregistered)
            + "]——必须先在设计稿 §15／§24.41-C#11 登记（含仲裁归属与门禁），再更新本守卫。");

        // ②登记清单每项必须存在且计数不变
        foreach (var (path, expected) in AllowedExecutionSends)
        {
            Assert.True(found.TryGetValue(path, out var actual), $"登记的执行类直发点缺失（已删除/搬迁？）：{path}——请同步更新 §15 与本守卫。");
            Assert.True(actual!.SequenceEqual(expected),
                $"执行类直发计数变化：{path} 期望 [{string.Join(",", expected)}]，实际 [{string.Join(",", actual)}]"
                + "——请先更新 §15 登记再改本守卫（禁止只改数字消警）。");
        }

        // ③文件集合守恒
        Assert.Equal(AllowedExecutionSends.Count, found.Count);
    }

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

    /// <summary>
    /// **S5 非字面量粗化（[2026-09-22 批次四十三]）**：登记「**操作名非字面量**」的直发形态——
    /// `xx.SendCommandAsync(&lt;标识符&gt;, …)`（操作名来自变量/参数/常量表达式），此前只登记**字面量**拼写，
    /// 因此「先把操作名拼进变量再直发」可绕过既有守卫。登记现状（文件 → 次数，全部为 1）：
    /// 端口转发层 `BgiExecutionPort` 与两个适配器（`BgiWorkflowPrerequisiteAdapter`／`ResourceCatalogService`）
    /// 属**入口透传**（操作名由调用方给出）；其余为既有发送点。
    /// **能力边界（如实）**：仍为**文本匹配**；不覆盖「反射调用」「别名变量间接调用」「字符串拼接后传参」等形态；
    /// **不证明**授权先于发送，也不替代 §17 原条款与真实入口证据。
    /// </summary>
    private static readonly Dictionary<string, int> AllowedNonLiteralSends = new(StringComparer.Ordinal)
    {
        ["MultiplayerHoeingAssistant/Services/CommandExecutor.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/RemoteConfigEditService.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowPrerequisiteAdapter.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowTerminalExecutor.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/TaskCenter/IBgiExecutionPort.cs"] = 1,
        ["MultiplayerHoeingAssistant/Services/TaskCenter/ResourceCatalogService.cs"] = 1,
        ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.BgiExternal.cs"] = 1,
        ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs"] = 1,
        ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.RemoteConfig.cs"] = 1,
    };

    [Fact]
    public void ProductionNonLiteralSends_MatchRegisteredInventory()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var pattern = new Regex(@"\.\s*SendCommandAsync\(\s*(?!ExternalOperations\.)(?!new\b)([A-Za-z_][A-Za-z0-9_\.]*)");
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            var count = pattern.Matches(File.ReadAllText(file)).Count;
            if (count > 0) found[rel] = count;
        }

        var unregistered = found.Keys.Where(k => !AllowedNonLiteralSends.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0,
            "发现**未登记的「操作名非字面量」直发点**：[" + string.Join(", ", unregistered)
            + "]——必须先在设计稿 §15／§24.46 与 §24.41-C#11 登记（含仲裁归属与门禁），再更新本守卫。");

        foreach (var (path, expected) in AllowedNonLiteralSends)
        {
            Assert.True(found.TryGetValue(path, out var actual) && actual == expected,
                $"非字面量直发计数变化：{path} 期望 {expected}，实际 {(found.TryGetValue(path, out var a) ? a : 0)}"
                + "——请先更新 §15／§24.46 登记再改本守卫（禁止只改数字消警）。");
        }
        Assert.Equal(AllowedNonLiteralSends.Count, found.Count);
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
