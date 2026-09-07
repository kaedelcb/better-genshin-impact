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
/// - 顺带从日志流被动跟踪"当前配置组/当前路线"（锄地启动/路线开始/取消/退出日志），
///   命中记录时快照进 ExceptionRecord.TaskGroup/RouteName（列表显示 + JSONL/trigger.json/诊断包导出自动带上）。
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
    /// <summary>任务上下文跟踪：来源桶（同限流分桶：本机=""，远程成员="远程:玩家名"）→ 当前配置组/路线。
    /// 从日志流被动跟踪（"锄地一条龙任务启动 [配置组: X]"、"开始执行地图追踪任务/JS脚本/路线: X"），
    /// 命中记录时快照进 ExceptionRecord.TaskGroup/RouteName，供列表显示与导出定位事发线路。</summary>
    private readonly Dictionary<string, (string? Group, string? Route)> _taskContexts = new();
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
        MinLevel = r.MinLevel, Enabled = r.Enabled, Alert = r.Alert, Snapshot = r.Snapshot, Note = r.Note
    };

    /// <summary>加载规则；文件不存在/损坏时从空规则起步（不再预置规则，由用户自行添加）。
    /// 无论新旧文件都补录内置卡死规则（一次性种子：用户删除后不再补回）。</summary>
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
                    SeedBuiltinStallRuleLocked();
                    return;
                }
            }
            catch { /* 配置损坏则按空规则重建 */ }
            _config = new WatchConfig();
            SeedBuiltinStallRuleLocked();
            SaveLocked();
        }
    }

    /// <summary>补录内置卡死规则（一次性种子：BuiltinStallSeeded 置位后不再补，用户删除/停用都保留其选择）。</summary>
    private void SeedBuiltinStallRuleLocked()
    {
        if (_config.BuiltinStallSeeded) return;
        _config.BuiltinStallSeeded = true;
        if (_config.Rules.All(r => r.Id != BuiltinStallRuleId))
            _config.Rules.Add(BuiltinStallRule());
        SaveLocked();
    }

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

    /// <summary>内置规则 Id：卡死心跳（非日志匹配，由 HoeingStatsViewModel 卡死检测经 RecordExternal 注入）。</summary>
    public const string BuiltinStallRuleId = "builtin-stall";

    /// <summary>内置规则定义：疑似卡死（内置检测）。Pattern "(?!)" 是永不匹配正则——
    /// 该规则不参与日志匹配，只作为卡死记录的开关载体（启用/告警/存快照均可在规则列表控制）。</summary>
    private static WatchRule BuiltinStallRule() => new()
    {
        Id = BuiltinStallRuleId,
        Name = "疑似卡死（内置检测）", Pattern = "(?!)", IsRegex = true,
        MinLevel = LogLevels.Dbg, Enabled = true, Alert = true, Snapshot = true,
        Note = "内置：任务运行中但日志超时无新行时注入（卡死心跳），不靠日志匹配；删除后不再自动补回"
    };

    /// <summary>
    /// 外部注入一条异常记录（非日志匹配的内置检测用，如卡死心跳）：
    /// 规则被停用则忽略；否则落盘 JSONL + 触发 RecordAdded（异常列表/告警/事发快照全走现成通道）。
    /// 告警与否取决于规则 Alert 与全局静音（与日志命中同语义）。后台线程调用。
    /// </summary>
    public void RecordExternal(ExceptionRecord record)
    {
        WatchRule? rule;
        lock (_lock)
        {
            rule = _config.Rules.FirstOrDefault(r => r.Id == record.RuleId);
            if (rule is { Enabled: false }) return;
            // 内置检测注入（如卡死心跳）没有日志行可关联，补跟踪到的本机任务上下文（空桶=本机）
            if (record.TaskGroup == null && record.RouteName == null
                && _taskContexts.TryGetValue("", out var ctx))
            {
                record.TaskGroup = ctx.Group;
                record.RouteName = ctx.Route;
            }
            WriteRecordLocked(record);
        }
        RecordAdded?.Invoke(record, rule?.Alert == true && !IsMuted());
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
            // 当前任务上下文快照（规则匹配前取：本条事件若是"任务取消/联机退出"，其记录仍带上退出前所在路线）
            _taskContexts.TryGetValue(srcBucket, out var taskCtx);

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
                    SourceFile = entry.SourceFile,
                    MatchedLine = FindMatchedLine(rule, entry),
                    TaskGroup = taskCtx.Group,
                    RouteName = taskCtx.Route
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

            // 任务上下文跟踪放最后更新（本条事件先参与规则匹配，再改变上下文）
            TrackTaskContext(entry, srcBucket);
        }

        // 事件在锁外触发，防订阅者重入死锁
        if (added != null)
            foreach (var (record, alert) in added)
                RecordAdded?.Invoke(record, alert);
        if (merged != null)
            foreach (var record in merged)
                RecordMerged?.Invoke(record);
    }

    /// <summary>
    /// 任务上下文跟踪（锁内调用，在本条事件参与完规则匹配后再更新——
    /// 这样"锄地一条龙任务被取消/联机锄地退出"这类记录仍能带上取消/退出前所在的配置组与路线）。
    /// 语料（BGI 侧出处见设计文档 §2.2）：
    ///   配置组：锄地一条龙任务启动 [配置组: "X"]，数据目录: ...（Serilog 字符串属性带引号渲染）
    ///   路线："→ 开始执行地图追踪任务: \"X\"" / "→ 开始执行JS脚本: \"X\""（配置组任务流，ScriptService）
    ///        / "开始执行路线: \"X\"" / "开始执行地图追踪任务: \"X\""（独立任务版，RouteExecutionEngine）
    /// </summary>
    private void TrackTaskContext(LogEntry entry, string srcBucket)
    {
        var msg = entry.Message;
        if (msg.Contains("锄地一条龙任务启动"))
        {
            // 新任务启动：记配置组并清掉上一任务残留的路线
            _taskContexts[srcBucket] = (ExtractGroupName(msg), null);
        }
        else if (msg.Contains("开始执行地图追踪任务") || msg.Contains("开始执行JS脚本") || msg.Contains("开始执行路线"))
        {
            var route = ExtractRouteName(msg);
            if (route == null) return;
            _taskContexts.TryGetValue(srcBucket, out var ctx);
            _taskContexts[srcBucket] = (ctx.Group, route);
        }
        else if (msg.Contains("锄地一条龙任务被取消") || msg.Contains("联机锄地退出"))
        {
            _taskContexts.Remove(srcBucket);
        }
    }

    /// <summary>路线开始行判定正则：取"开始执行…: "冒号后的引号段（无引号则取到行尾/句读）。
    /// 非贪婪 + 结尾断言保证引号内名称完整（与 BGI LogParse.cs:125,138 的判定口径一致，此处只需提取名字）。</summary>
    private static readonly Regex RouteStartRegex = new(
        @"开始执行(?:地图追踪任务|JS脚本|路线):\s*""?(?<name>[^""\r\n，]+?)""?\s*(?:\r?\n|$|，)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>提取路线/脚本名（未匹配到返回 null）。</summary>
    private static string? ExtractRouteName(string msg)
    {
        var m = RouteStartRegex.Match(msg);
        if (!m.Success) return null;
        var name = m.Groups["name"].Value.Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>提取配置组名："[配置组:" 到 "]" 之间，去掉 Serilog 字符串引号（无配置组段返回 null）。</summary>
    private static string? ExtractGroupName(string msg)
    {
        const string start = "[配置组:";
        var i = msg.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        i += start.Length;
        var j = msg.IndexOf(']', i);
        if (j < 0) return null;
        var v = msg[i..j].Trim().Trim('"');
        return v.Length > 0 ? v : null;
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

        var regex = GetCachedRegex(rule);
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

    /// <summary>定位命中行：一条事件=行头+多行正文，FileOffset 只指行头；
    /// 找到正文中真正命中规则的那一行原文（供跳转精确定位）。命中的是来源/行头或级别兜底规则时返回 null（回退行头行）。</summary>
    private string? FindMatchedLine(WatchRule rule, LogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return null;
        Regex? regex = null;
        if (rule.IsRegex)
        {
            regex = GetCachedRegex(rule);
            if (regex == null) return null;
        }
        // 候选行：正文各行 + 异常段各行（来源 Source 在行头上，命中它时返回 null=定位行头）
        foreach (var line in EntryBodyLines(entry))
        {
            try
            {
                var hit = regex != null
                    ? regex.IsMatch(line)
                    : line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
                if (hit) return line.TrimEnd();
            }
            catch (RegexMatchTimeoutException) { /* 本行超时当不命中，继续下一行 */ }
        }
        return null;
    }

    private static IEnumerable<string> EntryBodyLines(LogEntry entry)
    {
        foreach (var l in entry.Message.Split('\n')) yield return l;
        if (entry.Exception != null)
            foreach (var l in entry.Exception.Split('\n')) yield return l;
    }

    /// <summary>规则正则缓存（非法正则缓存 null=永不匹配；缓存由规则 Id 索引，规则编辑后 Id 不变但 Pattern 变，
    /// 靠 SaveRules 时清缓存——见 SaveRulesLocked）。</summary>
    private Regex? GetCachedRegex(WatchRule rule)
    {
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
        return regex;
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

    /// <summary>列出异常库已有记录的日期（仅从文件名 dodoco_exceptions.yyyy-MM-dd.jsonl 解析，不读内容），倒序。
    /// 供界面建日期筛选项——未选中的日期不预加载（历史量大时全量读会卡）。</summary>
    public List<string> ListRecordDates()
    {
        const string prefix = "dodoco_exceptions.";
        const string suffix = ".jsonl";
        var result = new List<string>();
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "dodoco_exceptions.*.jsonl"))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= prefix.Length + suffix.Length) continue;
                var date = name[prefix.Length..^suffix.Length];
                if (DateTime.TryParseExact(date, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    result.Add(date);
            }
        }
        catch { /* 目录读取失败返回空 */ }
        return result.OrderByDescending(d => d).ToList();
    }

    /// <summary>读取指定日期（yyyy-MM-dd）的异常记录（单文件，容错：坏行跳过），时间倒序。</summary>
    public List<ExceptionRecord> LoadRecordsOfDay(string date)
    {
        var result = new List<ExceptionRecord>();
        try
        {
            var path = Path.Combine(LogFileBrowser.AssistantLogDir, $"dodoco_exceptions.{date}.jsonl");
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadLines(path))
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
        catch { /* 文件读取失败返回空 */ }
        return result.OrderByDescending(r => r.Time).ToList();
    }

    /// <summary>删除异常库记录文件：date=null 删全部日期；否则只删指定日期（yyyy-MM-dd）。返回删除的文件数。
    /// 注意：删除当天文件后，尚在等后文上下文的在途记录（≤15 秒窗口）落盘时会重建当天文件，属可接受边角。</summary>
    public int DeleteRecords(string? date)
    {
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            if (!Directory.Exists(dir)) return 0;
            IEnumerable<string> files = date == null
                ? Directory.EnumerateFiles(dir, "dodoco_exceptions.*.jsonl")
                : [Path.Combine(dir, $"dodoco_exceptions.{date}.jsonl")];
            var n = 0;
            foreach (var f in files)
            {
                if (!File.Exists(f)) continue;
                File.Delete(f);
                n++;
            }
            return n;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KeywordWatchService] 异常库删除失败: {ex.Message}");
            return 0;
        }
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
