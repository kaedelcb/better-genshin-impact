using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 每日配置组运行日报（嘟嘟可 · 锄地数据 Tab）。
/// 直接解析 BGI 按天滚动日志（&lt;BGI&gt;\log\better-genshin-impact[yyyyMMdd][_NNN].log，
/// 无日期后缀 = 今天的活跃文件），不依赖助手当时是否在运行，可回看日志保留期（21 天）内任意一天。
///
/// 组时长模板与 BGI 自带 LogParse（GameTask/LogParse/LogParse.cs）一致：
/// 配对「配置组 "X" 加载完成，共N个脚本，开始执行」↔「配置组 "X" 执行结束」
/// （Serilog 对 string 属性默认带引号渲染；ScriptService.cs 中模板改动时需同步这里的正则）。
/// 组时间窗内的「[第 R/T 轮 房主名] 本轮锄地结束统计…」行归入该组，作为联机轮次明细。
/// </summary>
public sealed class DailyReportService
{
    /// <summary>组开始：配置组 "X" 加载完成，共25个脚本，开始执行。</summary>
    private static readonly Regex GroupStartRegex = new(
        @"配置组 ""(?<name>.+?)"" 加载完成，共\d+个脚本，开始执行",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>组结束：配置组 "X" 执行结束。</summary>
    private static readonly Regex GroupEndRegex = new(
        @"配置组 ""(?<name>.+?)"" 执行结束",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>联机轮次统计（AutoHoeingTask 三种变体：正常 / （被中断）/ （经验上限退出））。</summary>
    private static readonly Regex RoundStatRegex = new(
        @"(?:\[第 \d+/\d+ 轮(?: [^\]]+)?\] )?本轮锄地结束统计(?:（被中断）|（经验上限退出）)?：用时 \d+时\d+分\d+秒，完成 \d+ 条 / 跳过 \d+ 条（计划共 \d+ 条）",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>从文件名提取日期段（无日期段 = 今天的活跃文件）。</summary>
    private static readonly Regex FileDateRegex = new(
        @"^better-genshin-impact(?<date>\d{8})?(?:_\d{3})*\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // ===== 全天任务总览用的标记（OneDragonFlowViewModel / TaskRunner；模板改动时需同步） =====

    /// <summary>连续一条龙·配置单开始：正在执行 "计划" 计划的第 1 / 2 个配置单："名称"，绑定UID "…"</summary>
    private static readonly Regex ContinuousConfigStartRegex = new(
        @"正在执行 ""(?<schedule>.+?)"" 计划的第 \d+ / \d+ 个配置单：""(?<name>.+?)""，绑定UID",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>单次一条龙开始：启用任务总数量: N（半角冒号；名字由结束行回填）。</summary>
    private static readonly Regex SingleDragonStartRegex = new(
        @"启用任务总数量: \d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>一条龙配置单结束：配置单 "X" 绑定UID "…" 一条龙和配置组任务结束（单次/连续通用）。</summary>
    private static readonly Regex DragonConfigEndRegex = new(
        @"配置单 ""(?<name>.+?)"" 绑定UID .*?一条龙和配置组任务结束",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>独立任务统一包络（TaskRunner.RunCurrentAsync）：→ "任务启动！" / → "任务结束"。不含任务名。
    /// 注意：此包络不是独立任务专属——ScriptService.RunMulti 跑整个配置组时也套同一包络
    /// （且"配置组加载完成"先于包络输出，包络恒落在配置组单元内部），OneDragonFlowViewModel
    /// 的 UID 验证/兑换码检查等小包装也会产生。配置组内的外壳包络由 ParseOverviewFile
    /// 标记为 shellEnvelopes，闭合时一律丢弃壳层、子单元上移——组内真正的独立任务
    /// 由 ProjectStartRegex/ProjectEndRegex 配对成独立子单元，不靠外壳命名。</summary>
    private static readonly Regex SoloStartRegex = new(
        @"→ ""任务启动！""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SoloEndRegex = new(
        @"→ ""任务结束""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>配置组内项目任务开始（ScriptService.ExecuteProject，模板改动时需同步）：
    /// → 开始执行JS脚本: "X" / → 开始执行键鼠脚本: "X" / → 开始执行地图追踪任务: "X" /
    /// → 开始执行shell: "X" / → 开始执行独立任务: "X"。Kind 显示名对齐 ScriptGroupProjectExtensions.TypeDescriptions。</summary>
    private static readonly Regex ProjectStartRegex = new(
        @"→ 开始执行(?<kind>JS脚本|键鼠脚本|地图追踪任务|shell|独立任务): ""(?<name>.+?)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>配置组内项目任务结束：→ 脚本执行结束: "X"[, 耗时: M分S秒]（ScriptService 带耗时后缀；
    /// RouteExecutionEngine 独立任务版锄地逐线路补打同款但无后缀——其开始行无 "→ " 前缀不会开单元，此处按名找不到即忽略）。</summary>
    private static readonly Regex ProjectEndRegex = new(
        @"→ 脚本执行结束: ""(?<name>.+?)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>项目任务 Kind 集合（ProjectEndRegex 按名配对时只在这几类里找，避免误闭合同名配置组/一条龙）。</summary>
    private static readonly HashSet<string> ProjectKinds = new()
    {
        "JS脚本", "键鼠脚本", "地图追踪", "Shell", "独立任务"
    };

    /// <summary>项目任务日志 Kind 文本 → 总览显示 Kind（对齐 ScriptGroupProjectExtensions.TypeDescriptions）。</summary>
    private static string ProjectKindDisplay(string logKind) => logKind switch
    {
        "地图追踪任务" => "地图追踪",
        "shell" => "Shell",
        var k => k,
    };

    /// <summary>独立任务页直启独立任务的命名规则：包络内第一条命中的特征行决定任务名（顺序即优先级）。
    /// 组内独立任务不走这里（由 ProjectStartRegex 直接带名建单元）。</summary>
    private static readonly (Regex Rx, Func<Match, string> Name)[] SoloNameRules =
    {
        (new Regex(@"锄地一条龙任务启动 \[配置组: ""(?<n>.+?)""\]", RegexOptions.Compiled),
            m => $"锄地一条龙[{m.Groups["n"].Value}]"),
        (new Regex(@"锄地一条龙任务启动", RegexOptions.Compiled), _ => "锄地一条龙"),
        (new Regex(@"→ ""自动钓鱼，启动！""", RegexOptions.Compiled), _ => "自动钓鱼"),
        (new Regex(@"→ ""自动伐木，启动！""", RegexOptions.Compiled), _ => "自动伐木"),
        (new Regex(@"→ ""自动秘境，", RegexOptions.Compiled), _ => "自动秘境"),
        (new Regex(@"自动烹饪任务启动", RegexOptions.Compiled), _ => "自动烹饪"),
        (new Regex(@"自动吃药任务启动", RegexOptions.Compiled), _ => "自动吃药"),
        (new Regex(@"配对界面切换角色任务启动", RegexOptions.Compiled), _ => "配对界面切换角色"),
        (new Regex(@"OCR切换武器任务启动", RegexOptions.Compiled), _ => "OCR切换武器"),
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly Func<string?> _bgiLogDirProvider;

    /// <param name="bgiLogDirProvider">BGI 日志目录提供者（与 LogFileBrowser 同一接线，可为 null=未配置）。</param>
    public DailyReportService(Func<string?> bgiLogDirProvider)
    {
        _bgiLogDirProvider = bgiLogDirProvider;
    }

    /// <summary>枚举有日志文件的日期（倒序，最新在前）。</summary>
    public List<DateOnly> EnumerateDates()
    {
        var dates = new HashSet<DateOnly>();
        var dir = _bgiLogDirProvider();
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "better-genshin-impact*.log"))
            {
                var date = DateFromFileName(Path.GetFileName(f));
                if (date != null) dates.Add(date.Value);
            }
        }
        return dates.OrderByDescending(d => d).ToList();
    }

    /// <summary>构建指定日期的日报；当天无日志文件或无配置组记录时 Groups 为空。</summary>
    public DailyReport BuildReport(DateOnly date)
    {
        var report = new DailyReport { Date = date };
        var dir = _bgiLogDirProvider();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return report;

        var merged = new Dictionary<string, DailyReportGroup>(StringComparer.Ordinal);
        foreach (var file in FilesForDate(dir, date).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ParseFileInto(file, merged);
            }
            catch (Exception)
            {
                // 单个文件读取失败（占用/损坏）不影响其它文件与其它天
            }
        }

        foreach (var g in merged.Values.OrderBy(g => g.FirstStart))
            report.Groups.Add(g);
        report.TotalDuration = TimeSpan.FromSeconds(report.Groups.Sum(g => g.TotalDuration.TotalSeconds));
        return report;
    }

    /// <summary>该日期对应的全部日志文件（含多实例 _NNN；今天取无日期后缀的活跃文件）。</summary>
    private static List<string> FilesForDate(string dir, DateOnly date)
    {
        var result = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "better-genshin-impact*.log"))
        {
            var d = DateFromFileName(Path.GetFileName(f));
            // 无日期段的活跃文件已被 DateFromFileName 归为今天
            if (d == date) result.Add(f);
        }
        return result;
    }

    /// <summary>文件名 → 日期；无日期段（活跃文件）视为今天；不匹配返回 null。</summary>
    private static DateOnly? DateFromFileName(string name)
    {
        var m = FileDateRegex.Match(name);
        if (!m.Success) return null;
        if (!m.Groups["date"].Success) return DateOnly.FromDateTime(DateTime.Today);
        return DateOnly.TryParseExact(m.Groups["date"].Value, "yyyyMMdd", out var d) ? d : null;
    }

    /// <summary>逐行解析单个日志文件，把组时长/轮次明细合并进 merged（按组名）。</summary>
    private static void ParseFileInto(string path, Dictionary<string, DailyReportGroup> merged)
    {
        // 打开中的组栈（支持嵌套/交叉）：组结束按名字匹配最近的一次开始
        var open = new List<(string Name, TimeSpan Start)>();
        // BGI 日志模板是「行头行 + 消息行」两行结构（{SourceContext}{NewLine}{Message}），
        // 业务行本身无时间戳，事件时间取上一行头时间（紧邻其上，误差毫秒级）
        TimeSpan lastTime = default;
        var anyTime = false;

        foreach (var line in ReadLinesShared(path))
        {
            if (LogLineTime.TryGetTimeOfDay(line, out var tod))
            {
                lastTime = tod;
                anyTime = true;
                continue;
            }

            var eventTime = anyTime ? lastTime : default;

            var ms = GroupStartRegex.Match(line);
            if (ms.Success)
            {
                open.Add((ms.Groups["name"].Value, eventTime));
                continue;
            }

            var me = GroupEndRegex.Match(line);
            if (me.Success)
            {
                var name = me.Groups["name"].Value;
                var idx = open.FindLastIndex(g => g.Name == name);
                if (idx >= 0)
                {
                    var duration = eventTime - open[idx].Start;
                    if (duration < TimeSpan.Zero) duration += TimeSpan.FromDays(1); // 跨午夜兜底
                    Accumulate(merged, name, open[idx].Start, duration, unclosed: false);
                    open.RemoveAt(idx);
                }
                continue;
            }

            var mr = RoundStatRegex.Match(line);
            if (mr.Success && open.Count > 0)
            {
                // 归属最近打开（最内层）的组；同名组一天跑多轮时明细随之合并
                GetOrCreate(merged, open[^1].Name, open[^1].Start).Rounds.Add(mr.Value);
            }
        }

        // 未闭合的组：进行中（今天）或 BGI 崩溃/强退（历史），时长计至文件最后一行
        foreach (var g in open)
        {
            var duration = anyTime && lastTime > g.Start ? lastTime - g.Start : TimeSpan.Zero;
            Accumulate(merged, g.Name, g.Start, duration, unclosed: true);
        }
    }

    private static void Accumulate(Dictionary<string, DailyReportGroup> merged,
        string name, TimeSpan start, TimeSpan duration, bool unclosed)
    {
        var g = GetOrCreate(merged, name, start);
        g.TotalDuration += duration;
        g.RunCount++;
        if (start < g.FirstStart) g.FirstStart = start;
        if (unclosed) g.HasUnclosedRun = true;
    }

    private static DailyReportGroup GetOrCreate(
        Dictionary<string, DailyReportGroup> merged, string name, TimeSpan start)
    {
        if (!merged.TryGetValue(name, out var g))
        {
            g = new DailyReportGroup { Name = name, FirstStart = start };
            merged[name] = g;
        }
        return g;
    }

    /// <summary>逐行共享读（FileShare.ReadWrite|Delete，兼容 BGI 正在写入的当天文件）。</summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8NoBom,
            detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    // ========== 全天任务总览（一条龙配置单 / 配置组 / 独立任务 三层嵌套树） ==========

    /// <summary>构建指定日期的全天任务总览；顶层单元时间段互斥，合计即当天 BGI 总运行时长。</summary>
    public DayOverview BuildOverview(DateOnly date)
    {
        var overview = new DayOverview();
        var dir = _bgiLogDirProvider();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return overview;

        foreach (var file in FilesForDate(dir, date).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ParseOverviewFile(file, overview);
            }
            catch (Exception)
            {
                // 单个文件读取失败（占用/损坏）不影响其它文件与其它天
            }
        }

        overview.TopUnits.Sort((a, b) => a.Start.CompareTo(b.Start));
        overview.TotalDuration = TimeSpan.FromSeconds(overview.TopUnits.Sum(u => u.Duration.TotalSeconds));
        return overview;
    }

    /// <summary>逐行解析单个日志文件，把运行单元嵌套树挂进 overview（栈式配对，跨文件不续接）。</summary>
    private static void ParseOverviewFile(string path, DayOverview overview)
    {
        var stack = new List<OverviewUnit>(); // 打开中的单元（栈底最外层）
        TimeSpan lastTime = default;
        var anyTime = false;
        // TaskRunner 包络单元全集（含外壳）：SoloEndRegex 只闭合包络，
        // 不碰项目级"独立任务"子单元（由 ProjectEndRegex 按名配对）。
        var soloEnvelopes = new HashSet<OverviewUnit>();
        // RunMulti 外壳包络单元：ScriptService.RunMulti 用 TaskRunner 跑整个配置组，
        // 其 "任务启动！/任务结束" 包络出现在配置组单元内部（"配置组加载完成"先于包络输出）。
        // 外壳不是真正的任务单元（组内独立任务由项目级开始/结束行单独成单元），
        // 闭合时一律丢弃壳层、子单元上移，避免配置组被误标为"独立任务 · 未知独立任务"。
        var shellEnvelopes = new HashSet<OverviewUnit>();

        // 闭合 stack[idx] 及其内层全部单元（内层按 idx 之后的顺序先闭合，统一用 endTime 收尾）
        void CloseFrom(int idx, TimeSpan endTime, string? fallbackName, bool unclosed)
        {
            for (var i = stack.Count - 1; i >= idx; i--)
            {
                var u = stack[i];
                stack.RemoveAt(i);
                u.Duration = endTime - u.Start;
                if (u.Duration < TimeSpan.Zero) u.Duration += TimeSpan.FromDays(1); // 跨午夜兜底
                // RunMulti 外壳：不进树，子单元（组内项目任务）直接挂到外层
                if (shellEnvelopes.Contains(u))
                {
                    if (stack.Count > 0) stack[^1].Children.AddRange(u.Children);
                    else overview.TopUnits.AddRange(u.Children);
                    continue;
                }
                // 兜底名：目标单元用调用方给的（如结束行回填的配置单名），内层按类型默认
                if (string.IsNullOrEmpty(u.Name))
                    u.Name = i == idx && fallbackName != null
                        ? fallbackName
                        : u.Kind == "独立任务" ? "未知独立任务" : u.Kind;
                u.Unclosed = unclosed;
                if (stack.Count > 0) stack[^1].Children.Add(u);
                else overview.TopUnits.Add(u);
            }
        }

        foreach (var line in ReadLinesShared(path))
        {
            // 行头行只更新事件时间（两行结构，见 ParseFileInto 注释）
            if (LogLineTime.TryGetTimeOfDay(line, out var tod))
            {
                lastTime = tod;
                anyTime = true;
                continue;
            }
            var t = anyTime ? lastTime : default;

            var mc = ContinuousConfigStartRegex.Match(line);
            if (mc.Success)
            {
                stack.Add(new OverviewUnit { Kind = "一条龙", Name = mc.Groups["name"].Value, Start = t });
                continue;
            }
            if (SingleDragonStartRegex.IsMatch(line))
            {
                stack.Add(new OverviewUnit { Kind = "一条龙", Name = "", Start = t }); // 名由结束行回填
                continue;
            }
            var md = DragonConfigEndRegex.Match(line);
            if (md.Success)
            {
                var idx = stack.FindLastIndex(u => u.Kind == "一条龙");
                if (idx >= 0) CloseFrom(idx, t, md.Groups["name"].Value, unclosed: false);
                continue;
            }
            var ms = GroupStartRegex.Match(line);
            if (ms.Success)
            {
                stack.Add(new OverviewUnit { Kind = "配置组", Name = ms.Groups["name"].Value, Start = t });
                continue;
            }
            var me = GroupEndRegex.Match(line);
            if (me.Success)
            {
                var name = me.Groups["name"].Value;
                var idx = stack.FindLastIndex(u => u.Kind == "配置组" && u.Name == name);
                if (idx >= 0) CloseFrom(idx, t, null, unclosed: false);
                continue;
            }
            if (SoloStartRegex.IsMatch(line))
            {
                var unit = new OverviewUnit { Kind = "独立任务", Name = "", Start = t };
                soloEnvelopes.Add(unit);
                // 包络出现在配置组单元内部 = RunMulti 跑整组的外壳（非真实任务），标记后闭合时丢弃。
                if (stack.Count > 0 && stack[^1].Kind == "配置组")
                    shellEnvelopes.Add(unit);
                stack.Add(unit);
                continue;
            }
            if (SoloEndRegex.IsMatch(line))
            {
                var idx = stack.FindLastIndex(u => soloEnvelopes.Contains(u));
                if (idx >= 0) CloseFrom(idx, t, "未知独立任务", unclosed: false);
                continue;
            }

            // 配置组内项目任务（地图追踪/JS脚本/键鼠脚本/Shell/独立任务）：开始/结束按名配对
            var mp = ProjectStartRegex.Match(line);
            if (mp.Success)
            {
                stack.Add(new OverviewUnit
                {
                    Kind = ProjectKindDisplay(mp.Groups["kind"].Value),
                    Name = mp.Groups["name"].Value,
                    Start = t
                });
                continue;
            }
            var mpe = ProjectEndRegex.Match(line);
            if (mpe.Success)
            {
                var name = mpe.Groups["name"].Value;
                var idx = stack.FindLastIndex(u => u.Name == name && ProjectKinds.Contains(u.Kind));
                if (idx >= 0) CloseFrom(idx, t, null, unclosed: false);
                continue;
            }

            // 包络内特征行：给最内层未命名独立任务命名
            if (stack.Count > 0 && stack[^1] is { Kind: "独立任务", Name: "" } solo)
            {
                foreach (var (rx, name) in SoloNameRules)
                {
                    var m = rx.Match(line);
                    if (m.Success)
                    {
                        solo.Name = name(m);
                        break;
                    }
                }
            }
        }

        // 文件结束仍未闭合的单元：进行中（今天）或崩溃/强退（历史），时长计至最后一行
        if (stack.Count > 0)
            CloseFrom(0, anyTime ? lastTime : default, null, unclosed: true);
    }
}
