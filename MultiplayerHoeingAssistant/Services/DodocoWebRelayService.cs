using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>以显式授权和网关单播请求开放本机嘟嘟可数据。</summary>
public sealed class DodocoWebRelayService : IDisposable
{
    private readonly DodocoSettingsService _settings;
    private readonly KeywordWatchService _watch;
    private readonly DailyReportService _reports;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly Func<string> _assistantLogDirProvider;
    private SignalRClient? _hooked;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private readonly DiagnosticPackageService? _diagnostic;
    private DiagnosticCache? _diagnosticCache;
    private readonly object _cacheLock = new();
    private sealed class DiagnosticCache
    {
        public required string Id { get; init; }
        public required string Owner { get; init; }
        public required string RoomCode { get; init; }
        public required byte[] Bytes { get; init; }
        public required DateTimeOffset Created { get; init; }
        public required DateTimeOffset LastAccess { get; set; }
        public required long Epoch { get; init; }
    }
    private long _connectionEpoch;

    public DodocoWebRelayService(DodocoSettingsService settings, KeywordWatchService watch,
        DailyReportService reports, Func<SignalRClient?> clientProvider, Func<string>? assistantLogDirProvider = null,
        DiagnosticPackageService? diagnostic = null)
    {
        (_settings, _watch, _reports, _clientProvider) = (settings, watch, reports, clientProvider);
        _assistantLogDirProvider = assistantLogDirProvider ?? (() => LogFileBrowser.AssistantLogDir);
        _diagnostic = diagnostic;
    }

    public void EnsureHooked()
    {
        ExpireCaches();
        var client = _clientProvider();
        if (client == _hooked) return;
        if (_hooked != null) { _hooked.OnDodocoRequested -= Requested; _hooked.OnConnectionStateChanged -= ConnectionChanged; }
        Interlocked.Increment(ref _connectionEpoch);
        _hooked = client;
        if (_hooked != null) { _hooked.OnDodocoRequested += Requested; _hooked.OnConnectionStateChanged += ConnectionChanged; }
    }

    private void ExpireCaches()
    {
        lock (_cacheLock)
        {
            var now = DateTimeOffset.UtcNow;
            var cache = _diagnosticCache;
            if (cache != null && (!_settings.Current.ShareDodocoWeb || !_settings.Current.ShareLogFiles ||
                now - cache.LastAccess > TimeSpan.FromSeconds(30) || now - cache.Created > TimeSpan.FromMinutes(5)))
                _diagnosticCache = null;
        }
    }

    private void ConnectionChanged(bool connected)
    {
        if (!connected)
        {
            Interlocked.Increment(ref _connectionEpoch);
            lock (_cacheLock) _diagnosticCache = null;
        }
    }

    private void Requested(string requestId, string operation, JsonObject args, DateTimeOffset deadline, string requesterConnectionId, string roomCode)
    {
        var client = _hooked;
        if (client == null || _disposed) return;
        var epoch = Interlocked.Read(ref _connectionEpoch);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await _gate.WaitAsync(TimeSpan.FromSeconds(20)))
                {
                    await client.ReportDodocoAsync(requestId, "error", "busy", "执行端繁忙", null);
                    return;
                }
                try
                {
                    if (_disposed || DateTimeOffset.UtcNow >= deadline || !ReferenceEquals(client, _hooked)
                        || !client.IsControlRoomReady || string.IsNullOrEmpty(roomCode) || client.CurrentRoomCode != roomCode
                        || epoch != Interlocked.Read(ref _connectionEpoch))
                    {
                        await client.ReportDodocoAsync(requestId, "error", "request_expired", "请求已过期或连接已变化，未执行", null);
                        return;
                    }
                    var cfg = _settings.Current;
                    var denied = AuthorizationError(cfg, operation);
                    if (denied != null)
                    {
                        await client.ReportDodocoAsync(requestId, "denied", denied, "成员未开启所需共享权限", null);
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(requesterConnectionId)) throw new ArgumentException("缺少请求连接身份");
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("请求已过期，未执行");
                    using var timeout = new CancellationTokenSource(remaining);
                    var result = Execute(operation, args, requesterConnectionId, epoch, timeout.Token, roomCode);
                    if (DateTimeOffset.UtcNow >= deadline || !client.IsControlRoomReady || string.IsNullOrEmpty(roomCode) || client.CurrentRoomCode != roomCode
                        || epoch != Interlocked.Read(ref _connectionEpoch)) return;
                    var deniedAfter = AuthorizationError(_settings.Current, operation);
                    if (deniedAfter != null)
                    {
                        ExpireCaches();
                        await client.ReportDodocoAsync(requestId, "denied", deniedAfter, "成员已关闭所需共享权限", null);
                        return;
                    }
                    // 包含帧和转义后的 JSON 全部计入限额，避免服务端静默丢包。
                    if (JsonSerializer.SerializeToUtf8Bytes(result, GatewayJson.Options).Length > 480 * 1024)
                        throw new InvalidOperationException("结果过大，请缩小查询范围");
                    await client.ReportDodocoAsync(requestId, "ok", null, "", result);
                }
                finally { _gate.Release(); }
            }
            catch (Exception ex)
            {
                try { await client.ReportDodocoAsync(requestId, "error", ex is OperationCanceledException ? "request_expired" : ex is IOException or UnauthorizedAccessException ? "io_error" : "invalid_args", ex.Message, null); }
                catch { /* 断线由网页超时提示 */ }
            }
        });
    }

    internal static string? AuthorizationError(DodocoSettings cfg, string operation)
        => !cfg.ShareDodocoWeb ? "sharing_disabled" : (operation.StartsWith("report.", StringComparison.Ordinal) || operation.StartsWith("watch.diagnostic.", StringComparison.Ordinal)) && !cfg.ShareLogFiles
            ? "log_sharing_disabled" : null;

    internal object Execute(string operation, JsonObject args, string requesterConnectionId = "", long epoch = 0, CancellationToken cancellationToken = default, string roomCode = "")
    {
        switch (operation)
        {
            case "watch.read":
                var s = _settings.Current;
                return new { rules = _watch.GetRules(), settings = new { s.MuteAll, s.IncidentSnapshotEnabled, s.IncidentBufferSeconds,
                    s.IncidentCaptureIntervalSeconds, s.IncidentPreSeconds, s.IncidentPostSeconds, s.IncidentRetentionCount, s.StallThresholdMinutes }, dates = RecordDates() };
            case "watch.records":
                var date = Date(args);
                var offset = Integer(args, "offset", 0, 0, 10000);
                var limit = Integer(args, "limit", 50, 1, 100);
                var keyword = OptionalText(args, "keyword", 256);
                var level = OptionalText(args, "level", 3).ToUpperInvariant();
                var ruleId = OptionalText(args, "ruleId", 64);
                if (level.Length != 0 && level is not ("DBG" or "INF" or "WRN" or "ERR"))
                    throw new ArgumentException("日志级别无效");
                DateTime? beforeTime = null;
                var beforeLine = 0;
                if (args["beforeTime"] != null)
                {
                    if (!DateTime.TryParse(Text(args, "beforeTime", 64), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cursorTime))
                        throw new ArgumentException("分页时间无效");
                    beforeTime = cursorTime;
                    beforeLine = Integer(args, "beforeLine", 0, 1, int.MaxValue);
                    if (offset != 0) throw new ArgumentException("游标分页不能同时使用 offset");
                }
                var newest = new PriorityQueue<(ExceptionRecord Record, int Line), (long Ticks, int Line)>();
                var total = 0;
                var lineNumber = 0;
                foreach (var record in ReadRecords(date))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (level.Length != 0 && !string.Equals(record.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ruleId.Length != 0 && !string.Equals(record.RuleId, ruleId, StringComparison.Ordinal)) continue;
                    if (keyword.Length != 0 && !RecordContains(record, keyword)) continue;
                    total++;
                    if (beforeTime is { } cursor && (record.Time.Ticks > cursor.Ticks ||
                        (record.Time.Ticks == cursor.Ticks && lineNumber >= beforeLine))) continue;
                    newest.Enqueue((record, lineNumber), (record.Time.Ticks, lineNumber));
                    if (newest.Count > offset + limit + 1) newest.Dequeue();
                }
                var page = newest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Record.Time.Ticks)
                    .ThenByDescending(x => x.Line).Skip(offset).Take(limit + 1).ToList();
                var hasMore = page.Count > limit;
                if (hasMore) page.RemoveAt(page.Count - 1);
                var last = page.LastOrDefault();
                return new { records = page.Select(x => x.Record).ToArray(), total, offset, limit,
                    nextCursor = hasMore ? new { beforeTime = last.Record.Time, beforeLine = last.Line } : null };
            case "watch.rule.upsert":
                var rule = args["rule"]?.Deserialize<WatchRule>(GatewayJson.Options) ?? throw new ArgumentException("缺少规则");
                ValidateRule(rule);
                _watch.MutateRuleForWeb(rule, null);
                return new { rule };
            case "watch.rule.delete":
                var id = Text(args, "id", 64);
                _watch.MutateRuleForWeb(null, id);
                return new { id };
            case "watch.settings":
                var settings = args["settings"] as JsonObject ?? throw new ArgumentException("缺少设置");
                if (!_settings.TryUpdate(x => ApplySettings(x, settings))) throw new IOException("设置保存失败或配置迁移正在进行");
                return new { saved = true };
            case "watch.clear":
                date = Date(args);
                var existed = _watch.DeleteDayForWeb(date);
                return new { date, deleted = existed };
            case "watch.incidents":
                var frames = IncidentFrames(args);
                return new { frames = frames.Select((p, i) => new { index = i, name = Path.GetFileName(p) }).ToArray() };
            case "watch.frame":
                frames = IncidentFrames(args);
                var index = Integer(args, "index", -1, 0, 1000);
                if (index >= frames.Count) throw new ArgumentException("快照帧不存在");
                var info = new FileInfo(frames[index]);
                if (info.Length > 340 * 1024) throw new InvalidOperationException("快照帧超过传输大小上限");
                return new { index, name = info.Name, jpegBase64 = Convert.ToBase64String(File.ReadAllBytes(info.FullName)) };
            case "watch.diagnostic.start":
                if (_diagnostic == null || string.IsNullOrEmpty(requesterConnectionId)) throw new InvalidOperationException("诊断包服务不可用");
                ExpireCaches();
                lock (_cacheLock)
                    if (_diagnosticCache != null) throw new InvalidOperationException("已有诊断包正在下载，请稍后重试");
                var start = ParseTime(args, "start");
                var end = ParseTime(args, "end");
                if (end < start || end - start > TimeSpan.FromDays(7)) throw new ArgumentException("诊断时间范围必须在 7 天内");
                using (var stream = new BoundedMemoryStream(8 * 1024 * 1024))
                {
                    var summary = _diagnostic.Export(start, end, stream, strict: true, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var now = DateTimeOffset.UtcNow;
                    var created = new DiagnosticCache { Id = Guid.NewGuid().ToString("N"), Owner = requesterConnectionId,
                        Bytes = stream.ToArray(), Created = now, LastAccess = now, Epoch = epoch, RoomCode = roomCode };
                    lock (_cacheLock) _diagnosticCache = created;
                    return new { id = created.Id, size = created.Bytes.Length, summary };
                }
            case "watch.diagnostic.chunk":
                var requestedId = Text(args, "id", 64);
                lock (_cacheLock)
                {
                    var cache = _diagnosticCache;
                    var nowTime = DateTimeOffset.UtcNow;
                    if (cache == null || cache.Id != requestedId || cache.Owner != requesterConnectionId || cache.RoomCode != roomCode)
                        throw new InvalidOperationException("诊断包不存在或不属于当前连接");
                    if (cache.Epoch != epoch || nowTime - cache.LastAccess > TimeSpan.FromSeconds(30) || nowTime - cache.Created > TimeSpan.FromMinutes(5))
                    {
                        _diagnosticCache = null;
                        throw new InvalidOperationException("诊断包已过期或连接已变化");
                    }
                    var chunkOffset = Integer(args, "offset", 0, 0, cache.Bytes.Length);
                    var size = Math.Min(192 * 1024, cache.Bytes.Length - chunkOffset);
                    var payload = Convert.ToBase64String(cache.Bytes, chunkOffset, size);
                    var done = chunkOffset + size == cache.Bytes.Length;
                    cache.LastAccess = nowTime;
                    return new { id = requestedId, offset = chunkOffset, total = cache.Bytes.Length, base64 = payload, done };
                }
            case "report.dates": return new { dates = _reports.EnumerateDatesStrict() };
            case "report.day":
                date = Date(args);
                var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!_reports.EnumerateDatesStrict().Contains(day)) throw new InvalidOperationException("该日期没有可用日志");
                return new { date, report = _reports.BuildReport(day, strict: true), overview = _reports.BuildOverview(day, strict: true) };
            default: throw new ArgumentException("不支持的操作");
        }
    }

    private static DateTime ParseTime(JsonObject args, string key)
    {
        var value = Text(args, key, 64);
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException($"{key} 时间格式错误");
        return time;
    }

    private sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly long _limit;
        public BoundedMemoryStream(long limit) => _limit = limit;
        private void Check(long count) { if (count > _limit || Position > _limit - count) throw new IOException("诊断包超过 8 MiB，请缩短时间范围"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void SetLength(long value) { if (value > _limit) throw new IOException("诊断包超过 8 MiB"); base.SetLength(value); }
    }

    private static string Date(JsonObject args)
    {
        var value = Text(args, "date", 10);
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("日期格式错误");
        return value;
    }
    private static string OptionalText(JsonObject o, string key, int max)
    {
        var value = o[key]?.GetValue<string>()?.Trim() ?? "";
        if (value.Length > max) throw new ArgumentException($"{key} 超出长度限制");
        return value;
    }

    private static bool RecordContains(ExceptionRecord record, string keyword)
    {
        static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        return Contains(record.Message, keyword) || Contains(record.RuleName, keyword)
            || Contains(record.Instance, keyword) || Contains(record.TaskGroup, keyword) || Contains(record.RouteName, keyword)
            || (record.ContextBefore?.Any(x => Contains(x, keyword)) ?? false)
            || (record.ContextAfter?.Any(x => Contains(x, keyword)) ?? false);
    }

    private static string Text(JsonObject o, string key, int max)
    {
        var value = o[key]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException($"{key} 无效");
        return value;
    }
    private static int Integer(JsonObject o, string key, int fallback, int min, int max)
    {
        var value = o[key]?.GetValue<int>() ?? fallback;
        if (value < min || value > max) throw new ArgumentException($"{key} 超出范围");
        return value;
    }
    private string RecordPath(string date) => Path.Combine(_assistantLogDirProvider(), $"dodoco_exceptions.{date}.jsonl");
    private List<string> RecordDates()
    {
        var dir = _assistantLogDirProvider();
        IEnumerable<string> paths;
        try { paths = Directory.GetFiles(dir, "dodoco_exceptions.*.jsonl"); }
        catch (DirectoryNotFoundException) { return []; }
        return paths.Select(Path.GetFileName)
            .Where(n => n != null && n.Length == 34)
            .Select(n => n![18..28]).Where(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", out _))
            .OrderByDescending(d => d).ToList();
    }
    private IEnumerable<ExceptionRecord> ReadRecords(string date)
    {
        var path = RecordPath(date);
        StreamReader reader;
        try { reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        return Parse(reader);
        static IEnumerable<ExceptionRecord> Parse(StreamReader source)
        {
            using (source)
            {
                while (source.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    yield return JsonSerializer.Deserialize<ExceptionRecord>(line, GatewayJson.Options)
                        ?? throw new InvalidDataException("异常记录格式错误");
                }
            }
        }
    }
    private List<string> IncidentFrames(JsonObject args)
    {
        var date = Date(args);
        var timeText = Text(args, "time", 64);
        var ruleName = Text(args, "ruleName", 256);
        if (!DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            || time.ToString("yyyy-MM-dd") != date) throw new ArgumentException("事发时间无效");
        if (!ReadRecords(date).Any(r => r.Time == time && r.RuleName == ruleName)) throw new ArgumentException("异常记录不存在");
        var dir = IncidentSnapshotService.FindIncidentDir(time, ruleName, Path.Combine(_assistantLogDirProvider(), "incidents"), strict: true);
        if (dir == null) return [];
        return Directory.EnumerateFiles(dir, "frame_*.jpg")
            .Select(p => (Path: p, Name: Path.GetFileNameWithoutExtension(p)))
            .Select(x => (x.Path, Valid: double.TryParse(x.Name.AsSpan(6), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var offset), Offset: offset))
            .Where(x => x.Valid).OrderBy(x => x.Offset).ThenBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => x.Path).Take(1000).ToList();
    }
    private static void ValidateRule(WatchRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 64 || r.Name.Length > 256 || r.Pattern.Length > 2048 || r.Note.Length > 1024
            || r.MinLevel is not ("DBG" or "INF" or "WRN" or "ERR")) throw new ArgumentException("规则字段无效");
        if (r.IsRegex && r.Pattern.Length > 0) _ = new Regex(r.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
    }
    private static void ApplySettings(DodocoSettings s, JsonObject o)
    {
        foreach (var pair in o)
        {
            if (pair.Value == null) throw new ArgumentException("设置值不能为空");
            switch (pair.Key)
            {
                case "muteAll": s.MuteAll = pair.Value.GetValue<bool>(); break;
                case "incidentSnapshotEnabled": s.IncidentSnapshotEnabled = pair.Value.GetValue<bool>(); break;
                case "incidentBufferSeconds": s.IncidentBufferSeconds = Integer(o, pair.Key, 0, 5, 60); break;
                case "incidentCaptureIntervalSeconds":
                    var d = pair.Value.GetValue<double>();
                    if (!double.IsFinite(d) || d < 0.5 || d > 5) throw new ArgumentException("截图间隔超出范围");
                    s.IncidentCaptureIntervalSeconds = d; break;
                case "incidentPreSeconds": s.IncidentPreSeconds = Integer(o, pair.Key, 0, 1, 20); break;
                case "incidentPostSeconds": s.IncidentPostSeconds = Integer(o, pair.Key, 0, 1, 20); break;
                case "incidentRetentionCount": s.IncidentRetentionCount = Integer(o, pair.Key, 0, 0, 10000); break;
                case "stallThresholdMinutes": s.StallThresholdMinutes = Integer(o, pair.Key, 0, 1, 1440); break;
                default: throw new ArgumentException("不支持的设置字段");
            }
        }
    }
    public void Dispose()
    {
        _disposed = true;
        lock (_cacheLock) _diagnosticCache = null;
        if (_hooked != null) { _hooked.OnDodocoRequested -= Requested; _hooked.OnConnectionStateChanged -= ConnectionChanged; }
        Interlocked.Increment(ref _connectionEpoch);
        _hooked = null;
    }
}
