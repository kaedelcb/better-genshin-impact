using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

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
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

/// <summary>监控配置文件结构（规则列表 + 全局静音开关）。</summary>
public class WatchConfig
{
    [JsonPropertyName("muteAll")] public bool MuteAll { get; set; }
    [JsonPropertyName("rules")] public List<WatchRule> Rules { get; set; } = [];
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
    /// <summary>防风暴合并计数：同规则 60 秒内超上限的命中合并到此计数（仅内存更新，JSONL 保留首次写入值）。</summary>
    [JsonPropertyName("repeatCount")] public int RepeatCount { get; set; } = 1;

    /// <summary>界面显示用：含合并计数的标题。</summary>
    [JsonIgnore] public string DisplayTitle => RepeatCount > 1 ? $"[{RuleName}] ×{RepeatCount}" : $"[{RuleName}]";
}

/// <summary>成员截图帧（P5-B 远程巡检墙，SignalR MemberScreenshot 事件的负载）。
/// 属性名与服务端匿名负载 camelCase 对应（SignalR JSON 反序列化不区分大小写）。</summary>
public class MemberScreenshotFrame
{
    public string Uid { get; set; } = "";
    public string JpegBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CapturedAt { get; set; }
}

/// <summary>日志文件列表项（日志浏览 Tab）。Group 用于界面分组（BGI 日志 / 助手日志）。</summary>
public class LogFileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    /// <summary>分组名："BGI 日志" / "助手日志"。</summary>
    public string Group { get; set; } = "";
    public DateTime LastWriteTime { get; set; }
    public long Length { get; set; }
    /// <summary>实例数（后台扫描统计，未扫描完为 null，界面显示"…"）。</summary>
    public int? InstanceCount { get; set; }

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

/// <summary>日志浏览查看区的一行（带文件字节偏移，供跳转定位）。</summary>
public class LogLineItem
{
    public long Offset { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>搜索结果项（按行内关键字/正则命中）。</summary>
public class SearchResultItem
{
    public long LineNumber { get; set; }
    public long Offset { get; set; }
    public string Preview { get; set; } = "";
    /// <summary>完整行原文（筛选导出用，Preview 可能被截断）。</summary>
    public string FullText { get; set; } = "";
    public override string ToString() => $"行 {LineNumber}: {Preview}";
}
