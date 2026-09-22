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

    // ── S5 粗化余项（[2026-09-22 批次五十一] §24.41-C#11） ───────────────────────────────────────

    /// <summary>
    /// **[§24.41-C#11 余项·间接直发形态] 零容忍清单**（实测全仓 **0 处**；出现即失败）。
    /// **[第 1 轮会诊阻断项处置]** 四类口径均已强化，且**逐支配正反例**（见 `IndirectSendFormPatterns_HaveExpectedSamples`，
    /// 防止「正则被改成永不匹配 ⇒ 生产 0 命中 ⇒ 假绿」）：
    /// ①**反射按名取方法**：`GetMethod`／`GetRuntimeMethod` 的**字符串或 `nameof`**（含**限定成员名**
    ///   `nameof(IpcClient.SendCommandAsync)`）；
    /// ①′**反射按「名称」筛选**：`GetMethods()` ＋ `.Name == "…"`（含反向比较/`is`/`"X".Equals(...)`）与
    ///   `IgnoreCase` 取单方法；
    /// ②**方法组/别名使用**：**任何** `.SendCommandAsync` 后**不跟 `(`** 的用法（赋值/实参/强转/数组元素/括号包裹——
    ///   上一版只覆盖 `= 对象.SendCommandAsync;` 一种语法）；
    /// ③**拼接·插值构造操作名**：`"ext." + …`／`$"task.{…}"`／`string|String` ＋ **空白容错** 的 `.Concat("config."…)`。
    /// **能力边界（如实，勿夸大）**：仍是**文本匹配**（甚至**不剥注释/字符串**：生产注释里写出上述写法会**误报**——
    /// 保守方向）；**不识别** 前缀变量化 `$"{prefix}.task.start"`、复杂表达式/别名链、IL 级间接调用
    /// （`MethodInfo.Invoke` 经变量、`dynamic`、`Delegate.CreateDelegate`、`Reflection.Emit`）；**不证明**
    /// 「授权先于发送」（由运行期证据承担）。
    /// </summary>
    private static readonly Dictionary<string, Regex> IndirectSendFormPatterns = new(StringComparer.Ordinal)
    {
        // 名称表达式：**限定名可选**（裸 `nameof(SendCommandAsync)` 与 `nameof(IpcClient.SendCommandAsync)` 都要命中）；
        // [第 2 轮会诊重要项] 允许 `GetMethod (` 空白排版与**逐字字符串** `@"SendCommandAsync"`。
        ["reflection-by-name"] = new Regex(
            @"(GetMethod|GetRuntimeMethod)\s*\(\s*(?:""SendCommandAsync""|@""SendCommandAsync"""
            + @"|nameof\s*\(\s*(?:[A-Za-z_][A-Za-z0-9_\.]*\s*\.\s*)?SendCommandAsync\s*\))"
            + @"|nameof\s*\(\s*(?:[A-Za-z_][A-Za-z0-9_\.]*\s*\.\s*)?SendCommandAsync\s*\)",
            RegexOptions.Compiled),
        // [第 2 轮会诊重要项] ①`(?!\s*\()` 避免「带空格的正常调用」误报（上一版 `\s*` 可回溯到零）；
        // ②并**补无点前缀**的方法组/委托传递（`Register(SendCommandAsync)`／`= SendCommandAsync;`）。
        ["method-group-usage"] = new Regex(
            @"\.\s*SendCommandAsync(?!\s*\()|(?<![A-Za-z0-9_\.])SendCommandAsync(?!\s*\()", RegexOptions.Compiled),
        // [第 3 轮会诊重要项] 反射的**另一种常见形态**：`GetMethods()` ＋ 按**名称**筛选（`.Name == "…"`），
        // 以及**大小写不敏感**取单方法（`GetMethod("…", BindingFlags.IgnoreCase)`）——同一 `IgnoreCase` 口径覆盖两者。
        ["reflection-name-filter"] = new Regex(
            @"\.Name\s*==\s*(?:""SendCommandAsync""|@""SendCommandAsync"""
            + @"|nameof\s*\(\s*(?:[A-Za-z_][A-Za-z0-9_\.]*\s*\.\s*)?SendCommandAsync\s*\))"
            + @"|\.Name\.Equals\s*\(\s*(?:""SendCommandAsync""|@""SendCommandAsync"")"
            // [第 5 轮会诊建议] 反向比较与「字面量在左」的等价写法（`"X" == x.Name`／`.Name is "X"`／`"X".Equals(x.Name)`）
            + @"|(?:\""|@\"")SendCommandAsync(?:\"")\s*==\s*[A-Za-z_][A-Za-z0-9_\.]*\.Name"
            + @"|\.Name\s+is\s+(?:""SendCommandAsync""|@""SendCommandAsync"")"
            // [第 6／7 轮会诊重要项] 参数**必须**是**完整成员** `.Name` 且**紧跟右括号**
            // （否则 `"SendCommandAsync".Equals(x.NameSuffix)`／`.Equals(x.Name.Trim())`／`.Equals(x.Name, comparer)` 会被误报）
            + @"|(?:\""|@\"")SendCommandAsync(?:\"")\s*\.Equals\s*\(\s*[A-Za-z_][A-Za-z0-9_\.]*\.Name\s*\)"
            + @"|GetMethods?\s*\(\s*(?:""|@"")(?:sendcommandasync)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        ["opcode-concat"] = new Regex(
            @"""(ext|task|action|config|job|instance)\.""\s*\+"
            + @"|\$""(ext|task|action|config|job|instance)\.\{"
            + @"|(string|String)\s*\.\s*Concat\s*\(\s*""(ext|task|action|config|job|instance)\.",
            RegexOptions.Compiled),
    };

    [Fact]
    public void ProductionIndirectSendForms_AreAbsent()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            foreach (var (id, pattern) in IndirectSendFormPatterns)
            {
                var count = pattern.Matches(text).Count;
                if (count > 0) hits.Add($"{rel} ⇒ {id} × {count}");
            }
        }

        Assert.True(hits.Count == 0,
            "发现**未登记的间接直发形态**（反射按名取方法／反射按名称筛选／方法组·别名使用／拼接·插值构造操作名）：\n"
            + string.Join("\n", hits)
            + "\n——必须先在设计稿 §24.41-C#11／§24.63 登记（含仲裁归属与门禁）并评估可否改回直发，再更新本守卫。");
    }

    /// <summary>
    /// **[第 1 轮会诊阻断项处置] 零容忍四类正则的逐支正反例**：每支至少一个**应命中**与一个**不应命中**样例
    /// （反向样例含「已由其它守卫承担的完整字面量」与「本批明确登记为能力边界的前缀变量化」）。
    /// </summary>
    [Theory]
    // ①反射按名（普通字符串 / 裸 nameof / **限定成员 nameof**）
    [InlineData("reflection-by-name", "var m = typeof(IpcClient).GetMethod(\"SendCommandAsync\");", true)]
    [InlineData("reflection-by-name", "var m = typeof(IpcClient).GetMethod(nameof(SendCommandAsync));", true)]
    [InlineData("reflection-by-name", "var m = typeof(IpcClient).GetMethod(nameof(IpcClient.SendCommandAsync));", true)]
    [InlineData("reflection-by-name", "var m = t.GetMethod (\"SendCommandAsync\");", true)]              // 空白排版
    [InlineData("reflection-by-name", "var m = t.GetMethod(@\"SendCommandAsync\");", true)]              // 逐字字符串
    [InlineData("reflection-by-name", "var m = t.GetRuntimeMethod (nameof(IpcClient.SendCommandAsync), types);", true)]
    [InlineData("reflection-by-name", "var m = typeof(IpcClient).GetMethod(\"DoOther\");", false)]
    [InlineData("reflection-by-name", "var s = nameof(OtherAsync);", false)]
    // ②方法组/别名使用（`.SendCommandAsync` 后不跟 `(`）
    [InlineData("method-group-usage", "Func<string, Task> f = client.SendCommandAsync;", true)]
    [InlineData("method-group-usage", "Register(client.SendCommandAsync);", true)]
    [InlineData("method-group-usage", "var f = (client.SendCommandAsync);", true)]
    [InlineData("method-group-usage", "Func<string, Task> f2 = SendCommandAsync;", true)]                // 无点前缀
    [InlineData("method-group-usage", "Register(SendCommandAsync);", true)]                             // 无点前缀实参
    [InlineData("method-group-usage", "await client.SendCommandAsync(op, payload, ct);", false)]
    [InlineData("method-group-usage", "await client.SendCommandAsync (op, payload, ct);", false)]        // 带空格调用＝正常调用
    [InlineData("method-group-usage", "public Task<X> SendCommandAsync(string op, CancellationToken ct) { }", false)]
    [InlineData("method-group-usage", "public Task<X> SendCommandAsync (string op, CancellationToken ct) { }", false)]
    // [第 3 轮会诊建议] 已登记的**保守误报面**（注释/字符串/`nameof`）＋点分支的其它既有形态（须命中）
    [InlineData("method-group-usage", "// 说明：这里曾用 client.SendCommandAsync 直发", true)]
    [InlineData("method-group-usage", "var s = \"client.SendCommandAsync\";", true)]
    [InlineData("method-group-usage", "var m = nameof(SendCommandAsync);", true)]
    [InlineData("method-group-usage", "var t = client?.SendCommandAsync;", true)]
    [InlineData("method-group-usage", "var u = this.SendCommandAsync;", true)]
    [InlineData("method-group-usage", "var v = client\n    .SendCommandAsync;", true)]
    // ④反射按**名称**筛选（`GetMethods()` ＋ `.Name ==` / `IgnoreCase` 取单方法）
    [InlineData("reflection-name-filter", "var m = typeof(IpcClient).GetMethods().First(x => x.Name == \"SendCommandAsync\");", true)]
    [InlineData("reflection-name-filter", "var m = typeof(IpcClient).GetMethods().First(x => x.Name == nameof(SendCommandAsync));", true)]
    [InlineData("reflection-name-filter", "var m = typeof(IpcClient).GetMethods().First(x => x.Name.Equals(\"SendCommandAsync\"));", true)]
    [InlineData("reflection-name-filter", "var m = t.GetMethod(\"sendcommandasync\", BindingFlags.IgnoreCase);", true)]
    [InlineData("reflection-name-filter", "var m = typeof(IpcClient).GetMethods().First(x => x.Name == \"DoOther\");", false)]
    [InlineData("reflection-name-filter", "var n = list.First(x => x.Name == \"somethingelse\");", false)]
    // [第 5 轮会诊建议] 反向/等价比较写法（字面量在左、`is`、`"X".Equals`）
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\" == x.Name) { }", true)]
    [InlineData("reflection-name-filter", "if (x.Name is \"SendCommandAsync\") { }", true)]
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(x.Name)) { }", true)]
    [InlineData("reflection-name-filter", "if (\"DoOther\" == x.Name) { }", false)]
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(otherValue)) { }", false)]   // 参数非 `.Name` ⇒ 不命中
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(\"literal\")) { }", false)]
    // [第 7 轮会诊重要项] 参数必须是**完整成员** `.Name` 且紧跟右括号
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(x.NameSuffix)) { }", false)]
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(x.Name.Trim())) { }", false)]
    [InlineData("reflection-name-filter", "if (\"SendCommandAsync\".Equals(x.Name, comparer)) { }", false)]
    // ③拼接/插值构造操作名
    [InlineData("opcode-concat", "var a = \"ext.\" + suffix;", true)]
    [InlineData("opcode-concat", "var b = $\"task.{kind}\";", true)]
    [InlineData("opcode-concat", "var c = string . Concat(\"config.\", name);", true)]
    [InlineData("opcode-concat", "var d = \"ext.task.start\";", false)]           // 完整字面量由字面量守卫承担
    [InlineData("opcode-concat", "var e = $\"{prefix}.task.start\";", false)]     // 前缀变量化＝已登记能力边界
    public void IndirectSendFormPatterns_HaveExpectedSamples(string id, string sample, bool expected)
        => Assert.Equal(expected, IndirectSendFormPatterns[id].IsMatch(sample));

    /// <summary>
    /// **[§24.41-C#11 余项·全量 `OpCode` 字面量登记（含只读操作）]**：`new IpcRequest { OpCode = "…" }` 是
    /// 直发的另一入口形态——**包括非执行类只读操作**（`config.list`／`task.status`／`ping`：不产生远端副作用，
    /// 但同样属「直发」面，此前**未被任何守卫覆盖**）。登记＝**文件 → 计数 ＋ 去重值集合**：
    /// 新增/删除任一操作名字面量即失败（防「换一个新拼写绕过已登记集合」）。
    /// **[第 1 轮会诊阻断项处置]** 登记粒度＝**文件 → 值 → 次数**（**直方图**）——上一版的「计数＋去重值集合」
    /// 无法发现「计数守恒式替换」（例如把一处 `task.start` 改成 `config.list`）；正则放宽为**任意内容**的字符串
    /// 字面量（`OpCode = "…"`／`OpCode = @"…"`），非白名单字符集的新值**同样会被捕获**并因未登记而失败。
    /// **能力边界**：文本匹配（不剥注释）；`OpCode = <表达式>` 由**下方单独登记**承接（[会诊重要项处置]
    /// 上一版误称其由「非字面量直发」守卫覆盖——该守卫只覆盖 `SendCommandAsync(<标识符>, …)` 形态）。
    /// </summary>
    /// <summary>**`OpCode = <非字面量表达式>` 赋值登记**（文件 → 次数；实测 4 文件各 1 处）。</summary>
    private static readonly Dictionary<string, int> AllowedOpCodeNonLiteralAssignments = new(StringComparer.Ordinal)
    {
        ["MultiplayerHoeingAssistant/Services/CommandExecutor.cs"] = 1,              // OpCode = v2OpCode
        ["MultiplayerHoeingAssistant/Services/RemoteConfigEditService.cs"] = 1,      // OpCode = opCode
        ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.BgiExternal.cs"] = 1,  // OpCode = v2OpCode
        ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.RemoteConfig.cs"] = 1, // OpCode = operation
    };

    /// <summary>
    /// **[第 6／7 轮会诊处置] `OpCode` 赋值的真·单遍正则**：一次 `Matches` 捕获**整段 RHS**（到行尾或 `}` 前），
    /// 分类由**两个精确锚定正则**完成（`^…$`）：字面量（普通/逐字字符串）／简单标识符表达式；
    /// **其余一律未分类**（只计入总量 ⇒ 守恒失败）。[第 7 轮阻断项] 这样可避免「复杂 RHS 的**前缀**被误分类」
    /// （`OpCode = operation + suffix`／`= operation()`／`= "task.start" + suffix`／`= new Something()`）。
    /// </summary>
    private static readonly Regex OpCodeAssignmentPattern = new(
        // [第 8 轮会诊重要项] RHS 捕获**识别字符串边界**：普通字符串（含转义）／逐字字符串（`""` 转义）
        // 内部的 `,`／`;`／`}` 不再截断；其余字符按 `,`／`;`／`}`／行尾 终止。
        // [第 9 轮会诊重要项] 等号后**只允许空格/制表符**（不是 `\s*`）⇒ `OpCode =` 换行给值**不**被捕获为
        // 已分类形态（RHS 为空 ⇒ 未分类 ⇒ 守恒失败），与「跨行 RHS 保守失败」的登记一致。
        // [第 10 轮会诊阻断项] **等号前**仍允许跨行空白（`OpCode` 与 `=` 分行是合法写法，若一并收窄会**漏扫**
        // ⇒ 总量为 0、守恒假绿）。
        // [第 11 轮会诊阻断项] `OpCode` 与 `=` 之间的 **token 间隔**还允许**注释**（块注释/行注释）——
        // 否则 `OpCode/*c*/= "x"` 会被**完全漏扫**（总量 0 ⇒ 守恒假绿）。
        // [第 12／13 轮会诊重要项] **左边界＝完整 C# 标识符字符集**（Unicode 字母/数字/连接符/组合记号）：
        // 排除 `NotOpCode`／`SomeOpCode`／`变量OpCode` 等**后缀同名**误报，同时仍匹配
        // `x.OpCode`／`this.OpCode`／`global::X.OpCode` 与**逐字标识符** `@OpCode`。
        @"(?<![\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}])OpCode(?:[\s]|/\*[\s\S]*?\*/|//[^\r\n]*)*=[ \t]*(?<rhs>(?:""(?:[^""\\]|\\.)*""|@""(?:[^""]|"""")*""|[^,;}\r\n])*)",
        RegexOptions.Compiled);

    /// <summary>分类锚点：**整段 RHS** 恰为**普通**字符串字面量（**允许转义** `\"`／`\\`）。</summary>
    private static readonly Regex ClassifiedNormalLiteralRhs =
        new(@"^""(?<value>(?:[^""\\]|\\.)*)""$", RegexOptions.Compiled);

    /// <summary>分类锚点：**整段 RHS** 恰为**逐字**字符串字面量（`""` 转义，值按 `""`→`"` 归一）。</summary>
    private static readonly Regex ClassifiedVerbatimLiteralRhs =
        new(@"^@""(?<value>(?:[^""]|"""")*)""$", RegexOptions.Compiled);

    /// <summary>分类锚点：**整段 RHS** 恰为简单标识符/成员表达式（如 `operation`／`v2OpCode`）。</summary>
    private static readonly Regex ClassifiedIdentifierRhs =
        new(@"^[A-Za-z_][A-Za-z0-9_\.]*$", RegexOptions.Compiled);

    /// <summary>
    /// **[第 3 轮会诊重要项处置]** `OpCode =` 赋值分类计数（**守恒断言的唯一实现**，便于逐支自测）：
    /// 返回「总赋值数／字符串字面量支／简单标识符表达式支」——**两支互斥**；
    /// 总 ≠ 字面量 ＋ 表达式 ⇒ 存在**两支都未识别**的形态（`= (x)`／`= $"…"`／`= (string)x`）。
    /// </summary>
    private static (int Total, int Literal, int NonLiteral) OpCodeAssignmentCounts(string text)
    {
        var (_, literal, nonLiteral, total) = OpCodeScan(text);
        return (total, literal, nonLiteral);
    }

    /// <summary>
    /// **[第 5 轮会诊重要项处置]** `OpCode` 扫描的**单遍唯一实现**：一次正则遍历同时得到
    /// **直方图＋字面量计数＋表达式计数＋总计数**（守卫循环与自测都只调用本函数／其包装，
    /// 不再各自 `Regex.Matches`，杜绝「扫描两次、口径漂移」）。
    /// </summary>
    private static (Dictionary<string, int> Histogram, int Literal, int NonLiteral, int Total) OpCodeScan(string text)
    {
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var literal = 0;
        var nonLiteral = 0;
        var total = 0;
        foreach (Match m in OpCodeAssignmentPattern.Matches(text))
        {
            total++;
            // [第 7 轮会诊阻断项] 分类基于**整段 RHS 的精确锚定**：前缀匹配不算分类
            // （`operation + suffix`／`operation()`／`"task.start" + suffix`／`new Something()` ⇒ 未分类 ⇒ 守恒失败）。
            var rhs = m.Groups["rhs"].Value.Trim().TrimEnd(',', ';');
            // [第 9 轮会诊建议] 分类器扩展为普通（含转义）与逐字（`""`）两支，值按各自语义归一
            var normal = ClassifiedNormalLiteralRhs.Match(rhs);
            var verbatim = normal.Success ? Match.Empty : ClassifiedVerbatimLiteralRhs.Match(rhs);
            if (normal.Success || verbatim.Success)
            {
                literal++;
                var value = normal.Success
                    ? normal.Groups["value"].Value
                    : verbatim.Groups["value"].Value.Replace("\"\"", "\"");
                histogram[value] = histogram.TryGetValue(value, out var c) ? c + 1 : 1;
            }
            else if (ClassifiedIdentifierRhs.IsMatch(rhs))
            {
                nonLiteral++;
            }
        }
        return (histogram, literal, nonLiteral, total);
    }

    private static readonly Dictionary<string, Dictionary<string, int>> AllowedOpCodeHistogram =
        new(StringComparer.Ordinal)
        {
            ["MultiplayerHoeingAssistant/Services/CommandExecutor.cs"] = new(StringComparer.Ordinal)
            {
                ["action.close_game"] = 1, ["action.execute_hotkey"] = 1, ["config.list"] = 1,
                ["config.set_task_enabled"] = 1, ["task.resume"] = 1, ["task.start"] = 6,
                ["task.stop"] = 1, ["task.suspend"] = 1,
            },
            ["MultiplayerHoeingAssistant/Services/IpcClient.cs"] = new(StringComparer.Ordinal) { ["ping"] = 1 },
            ["MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs"] = new(StringComparer.Ordinal)
            {
                ["config.list"] = 1, ["task.status"] = 2,
            },
        };

    [Fact]
    public void ProductionOpCodeLiterals_MatchRegisteredHistogram()
    {
        var root = Path.Combine(RepoRoot(), "MultiplayerHoeingAssistant");
        var found = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var foundNonLiteral = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Path.GetDirectoryName(root)!, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            // **[第 2 轮会诊阻断项处置] 「漏报即失败」的总量守恒断言**：`OpCode =` 的全部赋值必须**恰好**
            // 等于「字面量支 ＋ 表达式支」——否则说明出现两支都未识别的形态（如 `OpCode = (x)`／`$"…"`／
            // `(string)x`），**必须**先扩展登记口径而不是静默通过。
            // **[第 4／5 轮会诊重要项处置] 单遍唯一实现**：本循环的**全部**结果（直方图、`foundNonLiteral`、
            // 守恒断言）都取自 `OpCodeScan`，**不再**各自 `Regex.Matches`（避免「扫描两次、口径漂移」）。
            var (histogram, literalCount, nonLiteralCount, total) = OpCodeScan(text);
            if (histogram.Count > 0) found[rel] = histogram;
            if (nonLiteralCount > 0) foundNonLiteral[rel] = nonLiteralCount;
            Assert.True(total == literalCount + nonLiteralCount,
                $"`OpCode =` 赋值总量守恒被破坏：{rel} 总 {total} ≠ 字面量 {literalCount} ＋ 表达式 {nonLiteralCount}"
                + "——存在**两支都未识别**的赋值形态；请先扩展登记口径（§24.41-C#11／§24.63）再改本守卫。");
        }

        var unregistered = found.Keys.Where(k => !AllowedOpCodeHistogram.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0,
            "发现**未登记的 `OpCode` 字面量文件或新操作名**：[" + string.Join(", ", unregistered)
            + "]（含新值而非新文件的情形）——必须先在设计稿 §24.41-C#11／§24.63 登记（含是否执行类、仲裁归属与门禁）"
            + "再更新本守卫。");

        foreach (var (path, expected) in AllowedOpCodeHistogram)
        {
            Assert.True(found.TryGetValue(path, out var actual), $"登记的 OpCode 字面量文件已无匹配：{path}");
            Assert.Equal(
                expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "×" + kv.Value),
                actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "×" + kv.Value));
        }
        Assert.Equal(AllowedOpCodeHistogram.Count, found.Count);

        // `OpCode = <表达式>` 单独登记（上一版误称由「非字面量直发」守卫覆盖——该守卫只覆盖首参数形态）
        var nonLiteralUnregistered = foundNonLiteral.Keys
            .Where(k => !AllowedOpCodeNonLiteralAssignments.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(nonLiteralUnregistered.Count == 0,
            "发现**未登记的 `OpCode = <表达式>` 赋值**：[" + string.Join(", ", nonLiteralUnregistered)
            + "]——必须先登记 §24.41-C#11／§24.63（说明其值来源与是否执行类）再更新本守卫。");
        foreach (var (path, expected) in AllowedOpCodeNonLiteralAssignments)
        {
            Assert.True(foundNonLiteral.TryGetValue(path, out var actual) && actual == expected,
                $"`OpCode = <表达式>` 计数变化：{path} 期望 {expected}，实际 {(foundNonLiteral.TryGetValue(path, out var a) ? a : 0)}");
        }
        Assert.Equal(AllowedOpCodeNonLiteralAssignments.Count, foundNonLiteral.Count);
    }

    /// <summary>
    /// **[第 1 轮会诊阻断项处置]** `OpCode` 两支持的正反例：普通字符串/逐字字符串被**字面量支**捕获；
    /// 表达式赋值被**非字面量支**捕获且**不**被字面量支重复计入（**两支互斥**——但**不**声称覆盖全部形态：
    /// 未覆盖形态由下方**总量守恒断言**拒绝，见 `OpCodeConservation_RejectsUnclassifiedForms`）。
    /// </summary>
    [Theory]
    [InlineData("OpCode = \"task.start\"", true, false)]
    [InlineData("OpCode = @\"task.start\"", true, false)]
    [InlineData("OpCode = \"Task.Start\"", true, false)]        // 非白名单字符集同样被捕获（会因未登记而失败）
    [InlineData("OpCode = v2OpCode", false, true)]
    [InlineData("OpCode = operation", false, true)]
    [InlineData("var opCode = \"task.stop\";", false, false)]    // 非赋值点不计数
    public void OpCodePatterns_CaptureLiteralsAndExpressions(string sample, bool literal, bool nonLiteral)
    {
        // [第 6 轮会诊处置] 直接经**单遍实现** `OpCodeScan` 分类（不再使用独立的两支正则）
        var (_, literalCount, nonLiteralCount, _) = OpCodeScan(sample);
        Assert.Equal(literal, literalCount > 0);
        Assert.Equal(nonLiteral, nonLiteralCount > 0);
    }

    /// <summary>
    /// **[第 3 轮会诊重要项处置]** **总量守恒判据的直接自测**：三类「两支都未识别」的形态必须使守恒**失败**
    /// （这正是守恒断言存在的理由：漏报即失败），而已识别形态守恒成立。
    /// </summary>
    [Theory]
    [InlineData("OpCode = (x)", false)]
    [InlineData("OpCode = $\"task.{kind}\"", false)]
    [InlineData("OpCode = (string)x", false)]
    // [第 7 轮会诊阻断项] 复杂 RHS 的**前缀**曾被误分类 ⇒ 以下四支必须守恒失败
    [InlineData("OpCode = operation + suffix", false)]
    [InlineData("OpCode = operation()", false)]
    [InlineData("OpCode = \"task.start\" + suffix", false)]
    [InlineData("OpCode = new Something()", false)]
    [InlineData("OpCode = \"task.start\"", true)]
    [InlineData("OpCode = @\"task.start\"", true)]
    [InlineData("OpCode = \"task.start\",", true)]     // 对象初始化器后续属性 ⇒ 末尾逗号不影响分类
    [InlineData("OpCode = \"task.start\", Payload = x", true)]   // [第 8 轮建议] 同一行多属性
    [InlineData("OpCode = \"a,b\"", true)]             // [第 8 轮重要项] 字符串内的 `,`／`;`／`}` 不截断
    [InlineData("OpCode = \"a;b\"", true)]
    [InlineData("OpCode = @\"a}b\"", true)]
    // [第 9 轮会诊处置] 跨行 RHS ⇒ **不**被捕获为已分类 ⇒ 守恒失败（与登记一致）；
    // 含**转义引号**的普通/逐字字面量 ⇒ 分类器已扩展，正常分类。
    [InlineData("OpCode =\n operation", false)]
    [InlineData("OpCode =\n \"task.start\"", false)]
    [InlineData("OpCode = \"a\\\",b\"", true)]
    [InlineData("OpCode = @\"a\"\"}b\"", true)]
    // [第 10 轮会诊阻断项] **等号前**跨行是合法写法 ⇒ 必须仍被扫描并分类（否则总量漏扫＝假绿）
    [InlineData("OpCode\n      = \"new.operation\"", true)]
    [InlineData("OpCode\n      = operation", true)]
    // [第 11 轮会诊阻断项] `OpCode` 与 `=` 之间为**注释**也是合法 token 间隔 ⇒ 必须仍被扫描并分类
    [InlineData("OpCode/*c*/= \"new.operation\"", true)]
    [InlineData("OpCode // 说明\n      = operation", true)]
    [InlineData("OpCode = operation", true)]
    public void OpCodeConservation_RejectsUnclassifiedForms(string sample, bool expectedConserved)
    {
        var (total, literal, nonLiteral) = OpCodeAssignmentCounts(sample);
        Assert.Equal(expectedConserved, total == literal + nonLiteral);
    }

    /// <summary>
    /// **[第 11 轮会诊阻断项处置] 精确计数断言**（不只守恒）：等号前跨行/注释间隔等**合法 token 间隔**
    /// 必须**确实被扫描**（`Total==1` 且恰有一支分类）——否则「总量 0 ⇒ 0==0」会**假绿**（这正是第 10 轮的教训）。
    /// </summary>
    [Theory]
    [InlineData("OpCode\n      = \"x\"", 1, 1, 0)]
    [InlineData("OpCode\n      = operation", 1, 0, 1)]
    [InlineData("OpCode/*c*/= \"x\"", 1, 1, 0)]
    [InlineData("OpCode // 说明\n= operation", 1, 0, 1)]
    [InlineData("OpCode = \"x\"", 1, 1, 0)]
    [InlineData("OpCode = operation", 1, 0, 1)]
    [InlineData("OpCode =\n \"x\"", 1, 0, 0)]          // 等号**后**换行 ⇒ 未分类（守恒失败）
    [InlineData("OpCode = operation + suffix", 1, 0, 0)]
    // [第 12 轮会诊重要项] 左边界：成员赋值应计入；后缀同名标识符不得误报
    [InlineData("x.OpCode = \"y\"", 1, 1, 0)]
    [InlineData("x.NotOpCode = \"y\"", 0, 0, 0)]
    [InlineData("SomeOpCode = \"y\"", 0, 0, 0)]
    // [第 13 轮会诊重要项] 逐字标识符／限定成员仍须计入；**Unicode 后缀同名**不得误报
    [InlineData("@OpCode = \"y\"", 1, 1, 0)]
    [InlineData("this.OpCode = \"y\"", 1, 1, 0)]
    [InlineData("global::X.OpCode = \"y\"", 1, 1, 0)]
    [InlineData("变量OpCode = \"y\"", 0, 0, 0)]
    // [第 14 轮会诊处置] 逐类别后缀反例（Nd／Mn／Mc／Pc／Cf）＋制表符 token 间隔正例
    [InlineData("A\u0031OpCode = \"y\"", 0, 0, 0)]        // Nd
    [InlineData("A\u2160OpCode = \"y\"", 0, 0, 0)]        // Nl（罗马数字一，letter number）
    [InlineData("A\u0301OpCode = \"y\"", 0, 0, 0)]        // Mn（组合记号）
    [InlineData("A\u0903OpCode = \"y\"", 0, 0, 0)]        // Mc（间隔组合记号）
    [InlineData("A\u203FOpCode = \"y\"", 0, 0, 0)]        // Pc（连接符）
    [InlineData("变量\u200COpCode = \"y\"", 0, 0, 0)]     // Cf（格式字符 U+200C）
    [InlineData("OpCode\t=\t\"y\"", 1, 1, 0)]             // 制表符间隔
    public void OpCodeScan_CountsLegalTokenSeparators_Exactly(string sample, int total, int literal, int nonLiteral)
        => Assert.Equal((total, literal, nonLiteral), OpCodeAssignmentCounts(sample));

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
