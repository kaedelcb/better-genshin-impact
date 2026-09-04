using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 关键字/正则异常监控服务（嘟嘟可 P2 / F3）。
/// 订阅 <see cref="BgiLogTailService"/> 的实时 LogEntry 流，按规则匹配：
/// - 命中记录（时间/规则/级别/实例/原文/前后各 5 行上下文/FileOffset）追加写 JSONL
///   （助手 exe 目录 log/dodoco_exceptions.{yyyy-MM-dd}.jsonl），重启不丢；
/// - 防风暴：同一规则 60 秒最多记 5 条，超出合并计数（RepeatCount ×N）；
/// - 命中 Alert=true 的规则触发 AlertRaised 事件（红点/托盘/提示音由 UI 层处理）。
///
/// 规则持久化到 %APPDATA%/NexusBGI/dodoco_watch_rules.json（跟随 AssistConfigManager 的配置目录约定）。
/// 所有匹配在日志后台线程执行；事件在后台线程触发，UI 层自行 Dispatcher。
/// </summary>
public sealed class KeywordWatchService : IDisposable
{
    /// <summary>同一规则的限流窗口。</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    /// <summary>窗口内同一规则最多记录条数，超出合并计数。</summary>
    private const int RateLimitPerWindow = 5;
    /// <summary>命中行后文上下文收集行数。</summary>
    private const int ContextAfterLines = 5;
    /// <summary>后文上下文等待超时（超时未收齐也落盘）。</summary>
    private static readonly TimeSpan ContextFlushTimeout = TimeSpan.FromSeconds(15);

    private readonly BgiLogTailService _tail;
    private readonly string _configPath;
    private readonly object _lock = new();
    /// <summary>全部静音判定（P4 起统一走 DodocoSettingsService；未注入时回落到本文件内的 muteAll 字段）。</summary>
    private readonly Func<bool>? _muteProvider;

    private WatchConfig _config = new();
    private readonly Dictionary<string, Regex?> _regexCache = new(); // 规则 Id → 编译后的正则（null=非法）
    /// <summary>限流窗口：分桶键（规则 Id|来源桶）→ 窗口内命中时间戳列表。
    /// 中危3：远程成员行（SourceFile 形如 "远程:玩家名"）与本机分桶，远程风暴不挤压本机告警配额。</summary>
    private readonly Dictionary<string, List<DateTime>> _hitWindows = new();
    /// <summary>合并计数目标：分桶键（规则 Id|来源桶）→ 窗口内最近一条记录（超出限流时 RepeatCount++）。
    /// 注意（审查中危6）：RepeatCount 的递增是运行期内存行为，仅供界面显示 ×N；
    /// JSONL 落盘保留记录首次落盘时的值，之后窗口内的合并不回写文件（重启后看到的是首次计数）。</summary>
    private readonly Dictionary<string, ExceptionRecord> _mergeTargets = new();
    /// <summary>待补后文上下文的记录（收满 5 行或超时后落盘）。</summary>
    private readonly List<PendingRecord> _pendingRecords = [];
    /// <summary>最近 5 条事件原文（命中时作为前文上下文）。</summary>
    private readonly Queue<string> _recentLines = new();
    private readonly Timer _flushTimer;

    /// <summary>新异常记录产生（含被限流合并前的首条）。后台线程触发。参数：记录、是否需要告警。</summary>
    public event Action<ExceptionRecord, bool>? RecordAdded;
    /// <summary>已有记录被合并计数（RepeatCount 增加），供 UI 刷新该条目。后台线程触发。</summary>
    public event Action<ExceptionRecord>? RecordMerged;

    public KeywordWatchService(BgiLogTailService tail, Func<bool>? muteProvider = null)
    {
        _tail = tail;
        _muteProvider = muteProvider;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "NexusBGI");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "dodoco_watch_rules.json");

        Load();
        _tail.EntryReceived += OnEntry;
        _flushTimer = new Timer(FlushTimeoutRecords, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>当前规则列表（UI 绑定用副本；修改后调 SaveRules 持久化）。</summary>
    public List<WatchRule> GetRules()
    {
        lock (_lock) return _config.Rules.Select(CloneRule).ToList();
    }

    /// <summary>当前是否静音：优先走统一设置（注入的 muteProvider），否则用本文件的 muteAll 字段（向后兼容）。</summary>
    private bool IsMuted() => _muteProvider?.Invoke() ?? _config.MuteAll;

    [Obsolete("P4 起静音统一持久化到 dodoco_settings.json，请改用 DodocoSettingsService；此属性仅保留向后兼容")]
    public bool MuteAll
    {
        get { lock (_lock) return _config.MuteAll; }
        set { lock (_lock) { _config.MuteAll = value; SaveLocked(); } }
    }

    /// <summary>整体替换规则列表并持久化（增删改后调用）。</summary>
    public void SaveRules(List<WatchRule> rules)
    {
        lock (_lock)
        {
            _config.Rules = rules.Select(CloneRule).ToList();
            _regexCache.Clear();
            SaveLocked();
        }
    }

    private static WatchRule CloneRule(WatchRule r) => new()
    {
        Id = r.Id, Name = r.Name, Pattern = r.Pattern, IsRegex = r.IsRegex,
        MinLevel = r.MinLevel, Enabled = r.Enabled, Alert = r.Alert, Note = r.Note
    };

    /// <summary>加载规则；文件不存在时写入内置预置规则（开箱即用，语料见设计文档 §2.2）。</summary>
    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<WatchConfig>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new WatchConfig();
                    return;
                }
            }
            catch { /* 配置损坏则回落到预置规则 */ }
            _config = new WatchConfig { Rules = DefaultRules() };
            SaveLocked();
        }
    }

    /// <summary>内置预置规则（设计文档 F3）。</summary>
    private static List<WatchRule> DefaultRules() =>
    [
        new WatchRule
        {
            Name = "通用错误宽匹配", Pattern = "出错|异常|失败|错误", IsRegex = true,
            MinLevel = LogLevels.Dbg, Enabled = true, Alert = false,
            Note = "宽匹配，默认只记不告警，避免打扰"
        },
        new WatchRule
        {
            Name = "路线执行出错", Pattern = @"执行路线 .* 出错", IsRegex = true,
            MinLevel = LogLevels.Dbg, Enabled = true, Alert = true,
            Note = "AutoHoeingTask 路线失败（无堆栈，靠上下文定位）"
        },
        new WatchRule
        {
            Name = "锄地任务被取消", Pattern = "锄地一条龙任务被取消", IsRegex = false,
            MinLevel = LogLevels.Dbg, Enabled = true, Alert = true, Note = ""
        },
        new WatchRule
        {
            Name = "联机锄地退出", Pattern = @"\[联机\] ===== 联机锄地退出", IsRegex = true,
            MinLevel = LogLevels.Dbg, Enabled = true, Alert = true, Note = ""
        },
        new WatchRule
        {
            Name = "ERR 级别兜底", Pattern = "", IsRegex = false,
            MinLevel = LogLevels.Err, Enabled = true, Alert = false,
            Note = "任何 Error 级日志自动入异常库（Pattern 为空 = 仅按级别匹配）"
        },
    ];

    private void SaveLocked()
    {
        try
        {
            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KeywordWatchService] 规则保存失败: {ex.Message}");
        }
    }

    /// <summary>喂入远程成员日志行（房间实时日志汇聚）。与本机 tail 路径隔离：本机行只走 EntryReceived，
    /// 远程行只走这里，不会重复。entry.SourceFile 形如 "远程:玩家名"，命中记录的 Instance/SourceFile
    /// 可直接区分是哪台机器触发。</summary>
    public void FeedRemoteEntry(LogEntry entry) => OnEntry(entry);

    private void OnEntry(LogEntry entry)
    {
        List<(ExceptionRecord record, bool alert)>? added = null;
        List<ExceptionRecord>? merged = null;

        lock (_lock)
        {
            // 维护前文上下文窗口（原文行：头行 + 正文首行）
            _recentLines.Enqueue(FormatLine(entry));
            while (_recentLines.Count > ContextAfterLines) _recentLines.Dequeue();

            // 中危3：限流/合并按"规则 + 来源"分桶——远程成员（SourceFile="远程:玩家名"）各自一桶，
            // 本机共用空桶；远程日志风暴不再挤占本机的窗口配额与合并目标
            var srcBucket = entry.SourceFile.StartsWith("远程:") ? entry.SourceFile : "";

            foreach (var rule in _config.Rules)
            {
                if (!rule.Enabled) continue;
                if (!LogLevels.AtLeast(entry.Level, rule.MinLevel)) continue;
                if (!IsMatch(rule, entry)) continue;

                var bucketKey = rule.Id + "|" + srcBucket;
                var now = DateTime.Now;
                // 限流：窗口内超上限则合并计数，不产生新记录
                if (!_hitWindows.TryGetValue(bucketKey, out var hits))
                    _hitWindows[bucketKey] = hits = [];
                hits.RemoveAll(t => now - t > RateWindow);
                if (hits.Count >= RateLimitPerWindow)
                {
                    if (_mergeTargets.TryGetValue(bucketKey, out var target))
                    {
                        target.RepeatCount++;
                        (merged ??= []).Add(target);
                    }
                    continue;
                }
                hits.Add(now);

                var record = new ExceptionRecord
                {
                    Time = entry.Time,
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    Level = entry.Level,
                    Instance = entry.Instance,
                    Message = FormatLine(entry),
                    ContextBefore = _recentLines.Take(_recentLines.Count - 1).ToList(),
                    FileOffset = entry.FileOffset,
                    SourceFile = entry.SourceFile
                };
                _mergeTargets[bucketKey] = record;
                _pendingRecords.Add(new PendingRecord
                {
                    Record = record,
                    Deadline = now + ContextFlushTimeout
                });
                (added ??= []).Add((record, rule.Alert && !IsMuted()));
            }

            // 后文上下文：本条事件补充给所有待收齐的记录
            foreach (var p in _pendingRecords)
            {
                // 不把自己加进自己的后文
                if (p.Record.Message != FormatLine(entry) || p.SelfSkipped)
                {
                    if (p.Record.ContextAfter.Count < ContextAfterLines)
                        p.Record.ContextAfter.Add(FormatLine(entry));
                }
                else
                {
                    p.SelfSkipped = true;
                }
            }
            var done = _pendingRecords.Where(p => p.Record.ContextAfter.Count >= ContextAfterLines).ToList();
            foreach (var p in done)
            {
                _pendingRecords.Remove(p);
                WriteRecordLocked(p.Record);
            }
        }

        // 事件在锁外触发，防订阅者重入死锁
        if (added != null)
            foreach (var (record, alert) in added)
                RecordAdded?.Invoke(record, alert);
        if (merged != null)
            foreach (var record in merged)
                RecordMerged?.Invoke(record);
    }

    /// <summary>超时冲刷：后文上下文未收齐的记录先落盘。</summary>
    private void FlushTimeoutRecords(object? state)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var expired = _pendingRecords.Where(p => now >= p.Deadline).ToList();
            foreach (var p in expired)
            {
                _pendingRecords.Remove(p);
                WriteRecordLocked(p.Record);
            }
        }
    }

    /// <summary>匹配判定：Pattern 为空 = 仅级别匹配（已过级别过滤）；否则按关键字/正则匹配消息+来源。</summary>
    private bool IsMatch(WatchRule rule, LogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return true;
        var text = entry.Source.Length > 0 ? entry.Source + "\n" + entry.Message : entry.Message;
        if (entry.Exception != null) text += "\n" + entry.Exception;

        if (!rule.IsRegex)
            return text.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);

        if (!_regexCache.TryGetValue(rule.Id, out var regex))
        {
            try
            {
                regex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                regex = null; // 非法正则：缓存 null，视为永不匹配
            }
            _regexCache[rule.Id] = regex;
        }
        if (regex == null) return false;
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>格式化事件为原文行（上下文用：头行 + 正文首行截断）。</summary>
    private static string FormatLine(LogEntry e)
    {
        var instance = e.Instance != null ? $" [{e.Instance}]" : "";
        var head = $"[{e.Time:HH:mm:ss.fff}] [{e.Level}]{instance} {e.Source}".TrimEnd();
        var firstLine = e.Message.Split('\n')[0];
        return string.IsNullOrEmpty(firstLine) ? head : $"{head} → {firstLine}";
    }

    /// <summary>追加写 JSONL 异常库（当天文件）。</summary>
    private void WriteRecordLocked(ExceptionRecord record)
    {
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"dodoco_exceptions.{record.Time:yyyy-MM-dd}.jsonl");
            File.AppendAllText(path,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = false }) + "\n");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KeywordWatchService] 异常库写入失败: {ex.Message}");
        }
    }

    /// <summary>读取历史异常记录（全部 JSONL 文件，按时间倒序；容错：坏行跳过）。</summary>
    public List<ExceptionRecord> LoadHistoryRecords()
    {
        var result = new List<ExceptionRecord>();
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "dodoco_exceptions.*.jsonl")
                         .OrderByDescending(f => f))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonSerializer.Deserialize<ExceptionRecord>(line,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (r != null) result.Add(r);
                    }
                    catch { /* 单行损坏不丢整文件 */ }
                }
            }
        }
        catch { /* 目录读取失败返回空 */ }
        return result.OrderByDescending(r => r.Time).ToList();
    }

    public void Dispose()
    {
        _tail.EntryReceived -= OnEntry;
        _flushTimer.Dispose();
        lock (_lock)
        {
            foreach (var p in _pendingRecords) WriteRecordLocked(p.Record);
            _pendingRecords.Clear();
        }
    }

    private sealed class PendingRecord
    {
        public ExceptionRecord Record = new();
        public DateTime Deadline;
        /// <summary>命中行自身已跳过后文收集。</summary>
        public bool SelfSkipped;
    }
}
