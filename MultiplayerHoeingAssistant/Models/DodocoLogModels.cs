using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>日志行头时间戳提取（BGI 日志行头 [HH:mm:ss.fff]；助手日志行头 [yyyy-MM-dd HH:mm:ss.fff]）。</summary>
public static class LogLineTime
{
    /// <summary>行头正则：可选日期段 + 时分秒（毫秒可选）。与 DiagnosticPackageService.HeaderRegex 同源扩展。</summary>
    public static readonly Regex HeaderRegex = new(
        @"^\[(?:(?<date>\d{4}-\d{2}-\d{2}) )?(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>提取行头时间（time-of-day）；无行头时间（堆栈续行等）返回 false。</summary>
    public static bool TryGetTimeOfDay(string line, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        var m = HeaderRegex.Match(line);
        return m.Success && TimeSpan.TryParseExact(m.Groups["time"].Value, @"hh\:mm\:ss",
            System.Globalization.CultureInfo.InvariantCulture, out timeOfDay);
    }

    /// <summary>提取显示用时间文本（助手日志带 MM-dd 日期前缀；无行头返回空串）。</summary>
    public static string DisplayText(string line)
    {
        var m = HeaderRegex.Match(line);
        if (!m.Success) return "";
        var date = m.Groups["date"].Success ? m.Groups["date"].Value[5..] + " " : "";
        return date + m.Groups["time"].Value;
    }
}

/// <summary>
/// 结构化日志条目（嘟嘟可日志系统核心模型）。
/// 由 BgiLogTailService 从 BGI 日志文件解析产出，供实时显示/关键字监控/锄地统计等多个消费者订阅。
/// </summary>
/// <param name="Time">事件时间（日志头只有时分秒，日期部分取自日志文件名或当天）</param>
/// <param name="Level">级别：DBG/INF/WRN/ERR（BGI 实际只用这四个，Fatal 无人用）</param>
/// <param name="Instance">多实例标识（形如 Main:S1:P12345:T...），无实例标识的旧格式为 null（界面显示"未知实例"）</param>
/// <param name="Source">SourceContext（来源类名），可能为空</param>
/// <param name="Message">消息正文（多行事件已合并）</param>
/// <param name="Exception">异常文本（能从消息中分离出异常段时填充；BGI 大量错误无堆栈，常为 null）</param>
/// <param name="FileOffset">该条目头行在源文件中的字节偏移（用于异常记录跳转定位）</param>
/// <param name="SourceFile">来源日志文件完整路径</param>
public sealed record LogEntry(
    DateTime Time,
    string Level,
    string? Instance,
    string Source,
    string Message,
    string? Exception,
    long FileOffset,
    string SourceFile);

/// <summary>日志级别工具：排序与显示。级别只有 DBG/INF/WRN/ERR 四档。</summary>
public static class LogLevels
{
    public const string Dbg = "DBG";
    public const string Inf = "INF";
    public const string Wrn = "WRN";
    public const string Err = "ERR";

    public static readonly string[] All = { Dbg, Inf, Wrn, Err };

    /// <summary>级别排序值（越大越严重），未知级别按 INF 处理。</summary>
    public static int Rank(string? level) => level switch
    {
        Dbg => 0,
        Inf => 1,
        Wrn => 2,
        Err => 3,
        _ => 1
    };

    /// <summary>level 是否达到 minLevel（含）以上。</summary>
    public static bool AtLeast(string? level, string minLevel) => Rank(level) >= Rank(minLevel);
}

/// <summary>
/// 异常监控规则（关键字/正则检测）。持久化到 %APPDATA%/NexusBGI/dodoco_watch_rules.json。
/// Pattern 为空串时按"仅级别匹配"（用于 ERR 兜底规则：任何 Error 级自动入异常库）。
/// </summary>
public class WatchRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("isRegex")] public bool IsRegex { get; set; }
    /// <summary>最低命中级别：DBG/INF/WRN/ERR，只有达到该级别的日志才参与匹配。</summary>
    [JsonPropertyName("minLevel")] public string MinLevel { get; set; } = LogLevels.Dbg;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>是否告警：true=命中后红点+托盘气泡+提示音；false=只记录。</summary>
    [JsonPropertyName("alert")] public bool Alert { get; set; }
    /// <summary>命中时保存事发快照（本地录像环前后各 3 秒帧 + 触发日志，落 log/incidents/）。默认 false。</summary>
    [JsonPropertyName("snapshot")] public bool Snapshot { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>监控配置文件结构（规则列表 + 全局静音开关）。</summary>
public class WatchConfig
{
    [JsonPropertyName("muteAll")] public bool MuteAll { get; set; }
    [JsonPropertyName("rules")] public List<WatchRule> Rules { get; set; } = [];
    /// <summary>内置规则"疑似卡死（内置检测）"是否已补录过（防用户删除后每次启动又回来）。</summary>
    [JsonPropertyName("builtinStallSeeded")] public bool BuiltinStallSeeded { get; set; }
}

/// <summary>
/// 异常命中记录。持久化为 JSONL：助手 exe 目录 log/dodoco_exceptions.{yyyy-MM-dd}.jsonl。
/// </summary>
public class ExceptionRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; }
    [JsonPropertyName("ruleId")] public string RuleId { get; set; } = "";
    [JsonPropertyName("ruleName")] public string RuleName { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("instance")] public string? Instance { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    /// <summary>命中行前 5 行上下文（原文，含行头）。</summary>
    [JsonPropertyName("contextBefore")] public List<string> ContextBefore { get; set; } = [];
    /// <summary>命中行后 5 行上下文（延迟聚合写入，超时 15 秒未收齐则先落盘已有的）。</summary>
    [JsonPropertyName("contextAfter")] public List<string> ContextAfter { get; set; } = [];
    [JsonPropertyName("fileOffset")] public long FileOffset { get; set; }
    [JsonPropertyName("sourceFile")] public string SourceFile { get; set; } = "";
    /// <summary>命中规则的那个原始行文本（BGI 一条事件=行头+多行正文，FileOffset 只指到行头；
    /// 跳转定位用它精确定位到正文里的命中行。旧记录无此字段 → 回退到行头行）。</summary>
    [JsonPropertyName("matchedLine")] public string? MatchedLine { get; set; }
    /// <summary>防风暴合并计数：同规则 60 秒内超上限的命中合并到此计数（仅内存更新，JSONL 保留首次写入值）。</summary>
    [JsonPropertyName("repeatCount")] public int RepeatCount { get; set; } = 1;

    /// <summary>界面显示用：含合并计数的标题。</summary>
    [JsonIgnore] public string DisplayTitle => RepeatCount > 1 ? $"[{RuleName}] ×{RepeatCount}" : $"[{RuleName}]";

    /// <summary>界面分组用：按天分组头（异常记录列表 GroupDescriptions）。</summary>
    [JsonIgnore] public string DayGroup => Time.ToString("yyyy-MM-dd dddd");

    /// <summary>界面用：该记录是否已有事发快照目录（查 log/incidents/ 实际落盘；规则未开存快照/零帧事件被清理后均为 false，
    /// 快照按钮据此显隐——只有真录到事发录像的记录才给入口）。</summary>
    [JsonIgnore] public bool HasIncidentSnapshot =>
        Services.IncidentSnapshotService.FindIncidentDir(Time, RuleName) != null;
}

/// <summary>成员截图帧（P5 远程成员画面·按需取图，SignalR MemberScreenshot 事件的负载）。
/// 属性名与服务端匿名负载 camelCase 对应（SignalR JSON 反序列化不区分大小写）。</summary>
public class MemberScreenshotFrame
{
    public string Uid { get; set; } = "";
    public string JpegBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CapturedAt { get; set; }
}

/// <summary>成员实时日志批（房间日志汇聚，SignalR MemberLogBatch 事件的负载）。
/// 与 MemberScreenshotFrame 同款模式：服务端纯转发不存储，发送者也会收到（客户端按 uid 自滤）。</summary>
public class MemberLogBatch
{
    public string Uid { get; set; } = "";
    public string SenderName { get; set; } = "";
    public List<string> Lines { get; set; } = [];
    /// <summary>发送端开启了省流（仅 INF+），观看端状态栏据此提示。旧服务端负载无此字段时默认 false。</summary>
    public bool InfoOnly { get; set; }
    /// <summary>服务端转发时刻（UTC），行内时间解析失败时的兜底。</summary>
    public DateTime ServerTime { get; set; }
}

/// <summary>远程成员日志文件列表项（远程成员完整日志下载：目标端上报 → 服务端透传广播）。
/// 与服务端 BgiCoordinatorServer.Models.MemberLogFileDescriptor 对应。</summary>
public class MemberLogFileDescriptor
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / 1024.0 / 1024.0:F2} MB"
    };

    public string DateText => LastWrite.ToString("yyyy-MM-dd HH:mm");
    public override string ToString() => Name;
}

/// <summary>远程成员日志文件列表应答（SignalR MemberLogFileList 事件负载，观众端按 RequestId 认领）。</summary>
public class MemberLogFileList
{
    public string Uid { get; set; } = "";
    public string RequestId { get; set; } = "";
    public List<MemberLogFileDescriptor> Files { get; set; } = [];
}

/// <summary>远程成员日志文件分块（SignalR MemberLogFileChunk 事件负载，gzip+base64 上行）。
/// TotalChunks=0 且 Done=true 是"对方正忙/拒绝/文件超限"标记块。</summary>
public class MemberLogFileChunk
{
    public string Uid { get; set; } = "";
    public string RequestId { get; set; } = "";
    public int Seq { get; set; }
    public int TotalChunks { get; set; }
    public string ChunkBase64 { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>日志文件列表项（日志浏览 Tab）。Group 用于界面分组（BGI 日志 / 助手日志）。</summary>
public class LogFileItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    /// <summary>分组名："本机 · BGI 日志" / "本机 · 助手日志" / "已下载的成员日志"。</summary>
    public string Group { get; set; } = "";
    public DateTime LastWriteTime { get; set; }
    public long Length { get; set; }

    private int? _instanceCount;
    /// <summary>实例数（后台扫描统计，未扫描完为 null，界面显示"…"）。INPC：扫描完成就地刷新，不整项替换。</summary>
    public int? InstanceCount
    {
        get => _instanceCount;
        set
        {
            if (_instanceCount == value) return;
            _instanceCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InstanceCount)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InstanceCountText)));
        }
    }

    public string SizeText => Length switch
    {
        < 1024 => $"{Length} B",
        < 1024 * 1024 => $"{Length / 1024.0:F1} KB",
        _ => $"{Length / 1024.0 / 1024.0:F2} MB"
    };

    public string DateText => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    public string InstanceCountText => InstanceCount?.ToString() ?? "…";
    public override string ToString() => Name;
}

/// <summary>日志浏览查看区的一行（带行号与文件字节偏移，供跳转定位）。</summary>
public class LogLineItem
{
    /// <summary>行号（从 1 起）。</summary>
    public int LineNumber { get; set; }
    public long Offset { get; set; }
    public string Text { get; set; } = "";
    /// <summary>行级别 DBG/INF/WRN/ERR（无行头级别的行延续上一行级别；文件头无级别行为空串）。加载时解析，界面按级别上色。</summary>
    public string Level { get; set; } = "";
}

/// <summary>整文件加载结果（日志浏览「记事本式」全量视图）。</summary>
public sealed class FullLogLoad
{
    public List<LogLineItem> Lines { get; set; } = [];
    /// <summary>true=文件超过内存上限，只加载了尾部一段（Lines[0].Offset &gt; 0）。</summary>
    public bool Truncated { get; set; }
    public long FileLength { get; set; }
}

/// <summary>远程成员下拉项（日志浏览 Tab·远程日志下载的成员选择）。</summary>
public class RemoteMemberOption
{
    public string Uid { get; }
    public string Name { get; }
    public RemoteMemberOption(string uid, string name) { Uid = uid; Name = name; }
    public override string ToString() => Name;
}

/// <summary>搜索结果项（按行内关键字/正则命中）。</summary>
public class SearchResultItem
{
    public long LineNumber { get; set; }
    public long Offset { get; set; }
    public string Preview { get; set; } = "";
    /// <summary>完整行原文（筛选导出用，Preview 可能被截断）。</summary>
    public string FullText { get; set; } = "";
    /// <summary>行头时间（界面显示用；无行头时间的行显示为空）。</summary>
    public string TimeText => LogLineTime.DisplayText(FullText);
    /// <summary>列表显示文本（时间 + 预览，高亮控件绑此属性）。</summary>
    public string DisplayText => string.IsNullOrEmpty(TimeText) ? Preview : $"{TimeText}  {Preview}";
    public override string ToString() => $"{TimeText} {Preview}";
}
