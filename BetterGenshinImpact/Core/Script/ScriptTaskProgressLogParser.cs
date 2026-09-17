using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.Core.Script;

/// <summary>
/// 第三方 JS 脚本进度日志的只读解析器。
///
/// 背景：脚本内部算出的阶段计数（如采集cd管理的「路径组3 特产 第 28/119 个」）只存在于脚本变量中，
/// 既没有走 <c>dispatcher.SetTaskProgress</c>，宿主也无法从 <c>pathingScript.runFile</c> 调用反推
/// （宿主只看到被执行的路线文件，不知道它在路径组列表中的序号与总数）。
/// 脚本唯一把它送出来的地方就是自己的 <c>log.*</c> 文本，因此这里按脚本目录严格匹配这些既有文案，
/// 供 <see cref="BetterGenshinImpact.Helpers.ScriptTaskProgressLogSink"/> 在进程内订阅日志事件时调用。
///
/// 约束：
/// 1. 整行锚定（\A…\z）+ 数值范围校验，匹配失败必须返回 null：宁可没有信息，也不能显示错误进度；
/// 2. 只做文本抽取与轻量重排，不推算、不累计、不补一；
/// 3. 输入输出都限长；关键计数放在文本前部，避免截断时把 N/M 丢掉。
/// </summary>
internal static class ScriptTaskProgressLogParser
{
    /// <summary>观察文本的最大长度（超出截断，避免面板被超长日志撑坏）。</summary>
    private const int MaxOutputLength = 96;

    /// <summary>参与解析的日志文本长度上限（超长直接放弃，宁可无信息）。</summary>
    private const int MaxInputLength = 2048;

    /// <summary>正则匹配超时，防止异常长日志造成回溯开销（超时按匹配失败处理）。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private const RegexOptions CompiledOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>脚本可能承载进度的日志前缀（用于在渲染消息前做廉价过滤，避免高频拾取日志走正则）。</summary>
    private static readonly string[] ProgressPrefixes =
    [
        "当前进度：",
        "本任务cd信息已更新",
        "schedule任务CD信息已更新",
        "当前垂钓点:",
        "该垂钓点(",
        "开始执行第",
        "树脂状态：",
        "正在进行第",
        "战斗成功！当前完成",
        "进入战斗环境，开始第",
        "幽境危战：第",
        "你的序号是"
    ];

    // ---------- 采集cd管理 ----------

    /// <summary>路径组组内进度：`当前进度：执行路线 07-小灯草-明冠峡-18个，路径组3 特产 第 28/119 个`。</summary>
    /// <remarks>标签来自用户选择的目录名，可能含空格，故用贪婪 `.+` 并以「空白+第+数字/数字+个」收尾锚定。</remarks>
    private static readonly Regex CollectionGroupItemRegex = new(
        @"\A当前进度：执行路线[ \t]+(?<route>.+)，路径组(?<group>[0-9]+)[ \t]+(?<label>.+)[ \t]+第[ \t]*(?<cur>[0-9]+)[ \t]*/[ \t]*(?<total>[0-9]+)[ \t]*个\z",
        CompiledOptions,
        MatchTimeout);

    /// <summary>优先材料阶段：`当前进度：执行路线 xxx.json，剩余优先材料：萃凝晶*40`。</summary>
    private static readonly Regex CollectionPriorityRegex = new(
        @"\A当前进度：执行路线[ \t]+.+，(?<state>剩余一次性优先材料：.+|剩余优先材料：.+)\z",
        CompiledOptions,
        MatchTimeout);

    /// <summary>路线 CD 写回：`本任务cd信息已更新，下一次可用时间为 2026/9/19 4:00:00`。</summary>
    private static readonly Regex CollectionRouteCdRegex = new(
        @"\A(?<state>(schedule任务CD信息已更新|本任务cd信息已更新)，下一次可用时间为[ \t]*.+)\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- 提瓦特自动钓鱼 ----------

    /// <summary>垂钓点进度：`当前垂钓点: 蒙德-清泉镇(进度: 7/119)`。</summary>
    private static readonly Regex FishingPointRegex = new(
        @"\A当前垂钓点:[ \t]*(?<point>.+)\(进度:[ \t]*(?<cur>[0-9]+)[ \t]*/[ \t]*(?<total>[0-9]+)\)\z",
        CompiledOptions,
        MatchTimeout);

    /// <summary>垂钓点冷却：`该垂钓点(白天)处于冷却状态，剩余时间: 2天 1小时 3分钟 4秒`。</summary>
    private static readonly Regex FishingCooldownRegex = new(
        @"\A该垂钓点\((?<phase>白天|夜晚)\)处于冷却状态，剩余时间:[ \t]*(?<remain>.+)\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- AAA 狗粮批发 ----------

    /// <summary>路线计数兜底：`当前进度：xxx.json为assets/xxx第3/98个`（正常情况由脚本显式上报覆盖）。</summary>
    private static readonly Regex ArtifactBulkRegex = new(
        @"\A当前进度：(?<route>.+)为(?<folder>.+)第(?<cur>[0-9]+)[ \t]*/[ \t]*(?<total>[0-9]+)[ \t]*个\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- 锄地一条龙 ----------
    //
    // 注意：这里**不**解析「开始处理第 X 组第 N/M 个…」——脚本在打印该行之后才检查路线 CD，
    // 被 CD 跳过的路线没有对应的清除日志，观察会导致面板把已跳过路线显示成"正在执行"。
    // 锄地一条龙本身有完整显式上报（dispatcher.SetTaskProgress），观察只保留"已完成"行作兜底。

    /// <summary>路线完成：`当前进度：第 1 组第 3/268 个  A003…json已完成，该组预计剩余: 1 时 20 分 3 秒`。</summary>
    private static readonly Regex HoeingRouteDoneRegex = new(
        @"\A当前进度：第[ \t]*(?<group>[0-9]+)[ \t]*组第[ \t]*(?<cur>[0-9]+)[ \t]*/[ \t]*(?<total>[0-9]+)[ \t]*个[ \t]+(?<route>.+)已完成，该组预计剩余[:：][ \t]*(?<remaining>.+)\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- 全自动枫丹地脉花 ----------

    /// <summary>地脉花进度：`开始执行第 2 线路的第 3/6 朵地脉花...`。</summary>
    private static readonly Regex LeyLineFlowerRegex = new(
        @"\A开始执行第[ \t]*(?<line>[0-9]+)[ \t]*线路的第[ \t]*(?<cur>[0-9]+)[ \t]*/[ \t]*(?<total>[0-9]+)[ \t]*朵地脉花.*\z",
        CompiledOptions,
        MatchTimeout);

    /// <summary>树脂状态：`树脂状态：浓缩1 原粹20 脆弱3 须臾0`（OCR 文本可能是非纯数字，只限长度不限格式）。</summary>
    private static readonly Regex LeyLineResinRegex = new(
        @"\A(?<state>树脂状态：.{1,64})\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- 全自动周一 ----------

    /// <summary>秘境挑战：`正在进行第2次秘境挑战` / `战斗成功！当前完成 2 次`。</summary>
    private static readonly Regex MondayDomainRegex = new(
        @"\A(?<state>正在进行第(?<n>[0-9]+)次秘境挑战|战斗成功！当前完成[ \t]*(?<m>[0-9]+)[ \t]*次)\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- 自动幽境危战 ----------

    /// <summary>幽境危战次数：`进入战斗环境，开始第 2 次战斗` / `幽境危战：第 2 次领奖...`。</summary>
    private static readonly Regex StygianOnslaughtRegex = new(
        @"\A(?<state>进入战斗环境，开始第[ \t]*(?<n1>[0-9]+)[ \t]*次战斗|幽境危战：第[ \t]*(?<n2>[0-9]+)[ \t]*次领奖.*)\z",
        CompiledOptions,
        MatchTimeout);

    // ---------- AAA 狗粮联机团购 ----------

    /// <summary>上线次序：`你的序号是3号，将在第2个执行`（脚本自身校验序号 1-4；未排进队伍时第 0 个应拒绝）。</summary>
    private static readonly Regex ArtifactGroupOrderRegex = new(
        @"\A你的序号是(?<idx>[0-9]+)号，将在第(?<pos>[0-9]+)个执行\z",
        CompiledOptions,
        MatchTimeout);

    /// <summary>
    /// 廉价前缀过滤：只有这些前缀开头的脚本日志才可能承载进度，避免对高频拾取日志做渲染与正则。
    /// </summary>
    public static bool MayContainProgress(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var prefix in ProgressPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断模板是否是"纯占位符型"（不含中文字面量），例如 `{Message}`。
    /// 真实消息若被包装在这种模板里，渲染前的前缀过滤会失效，Sink 需要放行并在渲染后重新校验前缀。
    /// </summary>
    public static bool IsPlaceholderOnlyTemplate(string? messageTemplate)
    {
        // 模板为空/缺失时无法廉价判断，按"未知"处理并放行渲染，由渲染后的前缀校验兜底。
        if (string.IsNullOrEmpty(messageTemplate))
        {
            return true;
        }

        foreach (var ch in messageTemplate)
        {
            if (IsCjk(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u3000' and <= '\u303F'   // CJK 标点
            or >= '\u4E00' and <= '\u9FFF'  // CJK 统一表意文字
            or >= '\uFF00' and <= '\uFFEF'; // 全角字符

    /// <summary>
    /// 按当前配置组脚本目录解析一行脚本日志；无法识别或校验不通过时返回 null。
    /// 只应在脚本项目运行期间调用（调用方负责项目边界清理）。
    /// </summary>
    public static string? TryParse(string? folderName, string? message)
    {
        if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = message.Trim();
        if (text.Length == 0 || text.Length > MaxInputLength)
        {
            return null;
        }

        if (IsFolder(folderName, "采集cd管理"))
        {
            return ParseCollectionCd(text);
        }

        if (IsFolder(folderName, "AutoFishingTeyvat"))
        {
            return ParseFishing(text);
        }

        if (IsFolder(folderName, "AAA-Artifacts-Bulk-Supply"))
        {
            return ParseArtifactBulk(text);
        }

        if (IsFolder(folderName, "AutoHoeingOneDragon"))
        {
            return ParseHoeingOneDragon(text);
        }

        if (IsFolder(folderName, "AutoFontaineLeyLine"))
        {
            return ParseFontaineLeyLine(text);
        }

        if (IsFolder(folderName, "AutoMonday"))
        {
            return TryMatch(MondayDomainRegex, text, m =>
                IsInRange(m, "n", 1, int.MaxValue) || IsInRange(m, "m", 1, int.MaxValue)
                    ? Compact(m.Groups["state"].Value)
                    : null);
        }

        if (IsFolder(folderName, "AutoStygianOnslaught"))
        {
            return TryMatch(StygianOnslaughtRegex, text, m =>
                IsInRange(m, "n1", 1, int.MaxValue) || IsInRange(m, "n2", 1, int.MaxValue)
                    ? Compact(m.Groups["state"].Value)
                    : null);
        }

        if (IsFolder(folderName, "ArtifactsGroupPurchasing"))
        {
            return TryMatch(ArtifactGroupOrderRegex, text, m =>
                IsInRange(m, "idx", 1, 4) && IsInRange(m, "pos", 1, 4)
                    ? Compact($"序号 {m.Groups["idx"].Value} · 第 {m.Groups["pos"].Value} 个执行")
                    : null);
        }

        return null;
    }

    private static string? ParseCollectionCd(string text)
    {
        var groupItem = TryMatch(CollectionGroupItemRegex, text, m =>
            IsInRange(m, "group", 1, 99) && IsValidRange(m, "cur", "total")
                ? Compact($"第 {m.Groups["cur"].Value}/{m.Groups["total"].Value} 个 · 路径组{m.Groups["group"].Value} {m.Groups["label"].Value}")
                : null);
        if (groupItem != null)
        {
            return groupItem;
        }

        var priority = TryMatch(CollectionPriorityRegex, text, m => Compact(m.Groups["state"].Value));
        if (priority != null)
        {
            return priority;
        }

        return TryMatch(CollectionRouteCdRegex, text, m => Compact(m.Groups["state"].Value));
    }

    private static string? ParseFishing(string text)
    {
        var point = TryMatch(FishingPointRegex, text, m =>
            IsValidRange(m, "cur", "total")
                ? Compact($"第 {m.Groups["cur"].Value}/{m.Groups["total"].Value} 个 · 垂钓点 {m.Groups["point"].Value}")
                : null);
        if (point != null)
        {
            return point;
        }

        return TryMatch(FishingCooldownRegex, text, m =>
            Compact($"垂钓点({m.Groups["phase"].Value})冷却剩余 {m.Groups["remain"].Value}"));
    }

    private static string? ParseArtifactBulk(string text) =>
        TryMatch(ArtifactBulkRegex, text, m =>
            IsValidRange(m, "cur", "total")
                ? Compact($"路线 第 {m.Groups["cur"].Value}/{m.Groups["total"].Value} 个")
                : null);

    private static string? ParseHoeingOneDragon(string text) =>
        TryMatch(HoeingRouteDoneRegex, text, m =>
            IsInRange(m, "group", 1, 10) && IsValidRange(m, "cur", "total")
                ? Compact($"第 {m.Groups["group"].Value} 组 第 {m.Groups["cur"].Value}/{m.Groups["total"].Value} 个已完成 · 该组预计剩余 {m.Groups["remaining"].Value}")
                : null);

    private static string? ParseFontaineLeyLine(string text)
    {
        var flower = TryMatch(LeyLineFlowerRegex, text, m =>
            IsInRange(m, "line", 1, int.MaxValue) && IsValidRange(m, "cur", "total")
                ? Compact($"第 {m.Groups["cur"].Value}/{m.Groups["total"].Value} 朵地脉花 · 第 {m.Groups["line"].Value} 线路")
                : null);
        if (flower != null)
        {
            return flower;
        }

        return TryMatch(LeyLineResinRegex, text, m => Compact(m.Groups["state"].Value));
    }

    /// <summary>统一匹配入口：正则异常（含超时）一律按未命中处理。</summary>
    private static string? TryMatch(Regex regex, string text, Func<Match, string?> projector)
    {
        try
        {
            var match = regex.Match(text);
            return match.Success ? projector(match) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>校验 `cur/total` 语义合法（分母为正、分子落在 1..分母）。</summary>
    private static bool IsValidRange(Match match, string currentGroup, string totalGroup)
    {
        return TryParseNumber(match, currentGroup, out var current)
               && TryParseNumber(match, totalGroup, out var total)
               && total > 0
               && current >= 1
               && current <= total;
    }

    /// <summary>校验编号落在 [minimum, maximum]；未参与匹配的组返回 false。</summary>
    private static bool IsInRange(Match match, string group, int minimum, int maximum)
    {
        return match.Groups[group].Success
               && TryParseNumber(match, group, out var value)
               && value >= minimum
               && value <= maximum;
    }

    private static bool TryParseNumber(Match match, string group, out int value) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool IsFolder(string folderName, string expected) =>
        string.Equals(folderName.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>截断时保留前部关键信息（计数已放在文本前部，因此不会先丢 N/M）。</summary>
    private static string Compact(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= MaxOutputLength)
        {
            return trimmed;
        }

        var cut = MaxOutputLength - 1;
        if (cut > 0 && char.IsHighSurrogate(trimmed[cut - 1]))
        {
            cut--;
        }

        return trimmed[..cut] + "…";
    }
}