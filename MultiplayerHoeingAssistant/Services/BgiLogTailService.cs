using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// BGI 日志实时 tail 服务（嘟嘟可 P1 核心）。
/// 以共享读方式打开 BGI 当前日志文件，增量读取新增内容，按行头正则切分多行事件，
/// 产出结构化 <see cref="LogEntry"/> 流供订阅者消费（实时显示、关键字监控等）。
///
/// 设计要点：
/// - FileShare.ReadWrite | FileShare.Delete：BGI 持有写入锁时也能打开；
/// - FileSystemWatcher + 1 秒轮询双保险（Watcher 在某些环境会丢事件）；
/// - 跨天滚动自动切换 tail 目标（每次轮询重选"最近写入的匹配文件"）；
/// - 行头不匹配的行并入上一条（容错降级，绝不丢行）；
/// - 首次打开只从文件尾倒读最近 2000 行（HistoryBatchReceived），之后增量（EntryReceived）；
/// - 所有文件操作在单个后台线程串行执行，事件在后台线程触发，UI 层自行 Dispatcher。
///
/// 行头正则与文件枚举正则同步自 BGI 侧 BetterGenshinImpact/GameTask/LogParse/LogParse.cs:22-24, 340。
/// BGI 若改日志模板需同步此处。
/// </summary>
public sealed class BgiLogTailService : IDisposable
{
    // 行头：[HH:mm:ss.fff] [LVL] [实例:S会话:P进程:T时间戳] SourceContext
    // 实例段可选（旧格式没有），SourceContext 也可为空
    private static readonly Regex HeaderRegex = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d+)\] \[(?<level>[^\]]{1,8})\](?: \[(?<instance>[A-Za-z][A-Za-z0-9]*:S\d+:P\d+:T\d+)\])?(?: (?<source>[^\r\n]+?))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 日志文件名枚举：better-genshin-impact.log / better-genshin-impactYYYYMMDD.log / 多实例 _NNN 后缀
    // （比 LogParse.cs:340 放宽了日期段为可选，以覆盖"当天写入"的无日期文件名）
    private static readonly Regex LogFileNameRegex = new(
        @"^better-genshin-impact(\d{8})?(_\d{3})*\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>疑似异常段的起始行模式（如 "System.Exception: ..."），用于把消息拆成 Message + Exception。</summary>
    private static readonly Regex ExceptionStartRegex = new(
        @"^([A-Za-z][\w.]*\.)?[A-Za-z][\w]*(Exception|Error)(:|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>首次打开时从文件尾倒读的最大字节数（防止单文件几万行全量加载卡死）。</summary>
    private const int InitialBackfillMaxBytes = 2 * 1024 * 1024;
    /// <summary>首次打开保留的最近行数。</summary>
    private const int InitialBackfillMaxLines = 2000;
    /// <summary>单次读取的字节块大小。</summary>
    private const int ReadChunkSize = 64 * 1024;
    /// <summary>轮询兜底间隔（Watcher 丢事件时保证 1 秒内感知）。</summary>
    private const int PollIntervalMs = 1000;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>日志目录提供者（每次轮询调用，支持用户在运行中配置/修改 BGI 路径后自动生效）。返回 null 表示暂未配置。</summary>
    private readonly Func<string?> _logDirProvider;

    private readonly Thread _worker;
    private readonly AutoResetEvent _poke = new(false);
    private volatile bool _disposed;

    private FileSystemWatcher? _watcher;
    private string? _watchedDir;

    // 以下字段仅 _worker 线程访问，无需加锁
    private FileStream? _stream;
    private string? _currentPath;
    private long _offset;
    private DateTime _currentFileDate = DateTime.Today;
    private readonly List<byte> _pendingBytes = []; // 未凑够一整行的残留字节
    private PendingEntry? _pendingEntry;            // 正在累积的多行事件
    /// <summary>最近一次从文件读到新字节的时刻（S2 空闲超时冲刷用：停写约 2 秒才收尾多行事件）。</summary>
    private DateTime _lastDataTime = DateTime.MinValue;

    /// <summary>多行事件空闲超时：文件停写超过该时长才把累积中的事件冲刷发出（防止跨轮询拆行）。</summary>
    private static readonly TimeSpan IdleFlushDelay = TimeSpan.FromSeconds(2);

    /// <summary>新增日志事件（实时增量，后台线程触发）。</summary>
    public event Action<LogEntry>? EntryReceived;
    /// <summary>首次打开时的历史回填批次（最近 2000 行解析结果，后台线程触发，仅一次/每次切换文件）。</summary>
    public event Action<IReadOnlyList<LogEntry>>? HistoryBatchReceived;
    /// <summary>tail 目标文件变化（含首次定位/跨天滚动/文件消失），参数为当前文件完整路径或 null。</summary>
    public event Action<string?>? TargetFileChanged;

    /// <summary>最后一次收到新日志时间（卡死心跳检测用，P3 用）。</summary>
    public DateTime LastEntryTime { get; private set; } = DateTime.MinValue;
    /// <summary>当前 tail 的文件路径（未定位到时为 null）。</summary>
    public string? CurrentFilePath => _currentPath;

    /// <param name="logDirProvider">BGI 日志目录（&lt;BGI安装目录&gt;\log）的提供者，允许返回 null（未配置时服务空转等待）。</param>
    public BgiLogTailService(Func<string?> logDirProvider)
    {
        _logDirProvider = logDirProvider;
        _worker = new Thread(WorkLoop) { IsBackground = true, Name = "BgiLogTailService" };
        _worker.Start();
    }

    /// <summary>解析 BGI 日志目录：优先用配置的 BGI exe 路径，其次从正在运行的 BGI 进程反推。</summary>
    public static string? ResolveBgiLogDir(string? configuredBgiExePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(configuredBgiExePath))
            {
                var dir = Path.GetDirectoryName(configuredBgiExePath);
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "log");
            }
            // 配置缺失时尝试从运行中的 BGI 进程反推安装目录
            var procs = BgiProcessMonitor.GetCurrentSessionBgiProcesses();
            foreach (var p in procs)
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    var dir = exe == null ? null : Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "log");
                }
                catch { /* 单进程取模块失败（权限等）不影响其它进程 */ }
            }
        }
        catch { /* 解析失败返回 null，服务空转等待 */ }
        return null;
    }

    /// <summary>手动唤醒一次轮询（例如刚配置好 BGI 路径想立即生效）。</summary>
    public void Poke() => _poke.Set();

    private void WorkLoop()
    {
        while (!_disposed)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // tail 服务永不因单次异常退出；记录后继续
                System.Diagnostics.Debug.WriteLine($"[BgiLogTailService] 轮询异常: {ex}");
            }
            _poke.WaitOne(PollIntervalMs);
        }
    }

    private void Tick()
    {
        var dir = _logDirProvider();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            CloseStream(notify: _currentPath != null);
            return;
        }

        EnsureWatcher(dir);

        // 选择 tail 目标：最近写入的匹配文件（跨天滚动后新日期文件 LastWriteTime 最新，自动切换）
        var target = Directory.EnumerateFiles(dir, "better-genshin-impact*.log")
            .Where(f => LogFileNameRegex.IsMatch(Path.GetFileName(f)))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (target == null)
        {
            CloseStream(notify: _currentPath != null);
            return;
        }

        if (_currentPath != target.FullName)
        {
            // 防抖（中危1）：当前目标 5 秒内仍有写入就不切换——多实例交替写日志时，
            // "最近写入文件"会在两个文件间抖动，每 tick 重开流+重发 2000 行历史。
            // 只在当前文件停写且出现更新文件时才换目标。
            if (_stream != null)
            {
                try
                {
                    var cur = new FileInfo(_currentPath);
                    if (cur.Exists && DateTime.UtcNow - cur.LastWriteTimeUtc < TimeSpan.FromSeconds(5))
                    {
                        DrainNewData(isLive: true);
                        FlushIfIdle();
                        return;
                    }
                }
                catch { /* 取不到文件信息时按原逻辑切换 */ }
            }
            // 切换目标：先把旧文件剩余内容读完，再无缝接到新文件
            if (_stream != null) DrainNewData(isLive: true);
            CloseStream(notify: false);
            OpenNewTarget(target.FullName);
            return;
        }

        // 文件被截断/重建（长度小于已读偏移）→ 从头重读
        if (_stream != null && _stream.Length < _offset)
        {
            CloseStream(notify: false);
            OpenNewTarget(target.FullName);
            return;
        }

        DrainNewData(isLive: true);
        // S2：drain 末尾不再无条件冲刷（会把跨轮询的多行事件拆成孤儿条目）；
        // 新行头到达时的冲刷在 FeedLine 内完成，这里只在文件停写约 2 秒后收尾最后一条。
        FlushIfIdle();
    }

    /// <summary>空闲超时冲刷：文件停写超过 <see cref="IdleFlushDelay"/> 才把累积中的多行事件发出。</summary>
    private void FlushIfIdle()
    {
        if (_pendingEntry != null && DateTime.Now - _lastDataTime > IdleFlushDelay)
            FlushPending(EmitLive);
    }

    /// <summary>逐订阅者隔离分发（中危3）：一个订阅者抛异常不影响当批其余行和其它订阅者。</summary>
    private void EmitLive(LogEntry e)
    {
        LastEntryTime = DateTime.Now;
        var handlers = EntryReceived;
        if (handlers == null) return;
        foreach (Action<LogEntry> h in handlers.GetInvocationList())
        {
            try { h(e); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BgiLogTailService] 订阅者处理日志行异常: {ex}");
            }
        }
    }

    private void EnsureWatcher(string dir)
    {
        if (_watchedDir == dir && _watcher != null) return;
        _watcher?.Dispose();
        _watchedDir = dir;
        _watcher = new FileSystemWatcher(dir, "better-genshin-impact*.log")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => _poke.Set();
        _watcher.Created += (_, _) => _poke.Set();
        _watcher.Renamed += (_, _) => _poke.Set();
    }

    private void CloseStream(bool notify)
    {
        // 收尾冲刷：切换/丢失目标前把累积中的最后一条事件发出去，不丢行（S2 配套）
        FlushPending(EmitLive);
        _stream?.Dispose();
        _stream = null;
        _currentPath = null;
        _offset = 0;
        _pendingBytes.Clear();
        _pendingEntry = null;
        if (notify) TargetFileChanged?.Invoke(null);
    }

    /// <summary>打开新的 tail 目标：倒读最近 2000 行作为历史上下文，然后从文件尾开始增量。</summary>
    private void OpenNewTarget(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _currentPath = path;
        _currentFileDate = ExtractFileDate(path);
        _pendingBytes.Clear();
        _pendingEntry = null;

        var length = _stream.Length;
        if (length > 0)
        {
            var history = Backfill(path, length);
            _offset = length;
            // 不再丢弃回填末尾的未完结事件（原 FlushPending() 无 emit 直接丢弃）：
            // 保留 _pendingEntry，后续续行会继续累积，完结/空闲后经 EntryReceived 实时通道发出。
            if (history.Count > 0) HistoryBatchReceived?.Invoke(history);
        }
        else
        {
            _offset = 0;
        }
        TargetFileChanged?.Invoke(path);
    }

    /// <summary>从文件名提取日期（better-genshin-impact20240101.log → 2024-01-01），无日期段用今天。</summary>
    private static DateTime ExtractFileDate(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"better-genshin-impact(?<d>\d{8})");
        if (m.Success && DateTime.TryParseExact(m.Groups["d"].Value, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return DateTime.Today;
    }

    /// <summary>倒读文件尾部，解析最近 <see cref="InitialBackfillMaxLines"/> 行为历史条目（不全量加载）。</summary>
    private List<LogEntry> Backfill(string path, long fileLength)
    {
        var result = new List<LogEntry>();
        var readSize = (int)Math.Min(fileLength, InitialBackfillMaxBytes);
        var start = fileLength - readSize;
        _stream!.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[readSize];
        var read = 0;
        while (read < readSize)
        {
            var n = _stream.Read(buffer, read, readSize - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == 0) return result;

        // 丢掉首行残段（从中间开始读的）
        var firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, read);
        var bodyStart = firstNewline >= 0 ? firstNewline + 1 : read;
        if (bodyStart >= read) return result;

        var bodyOffset = start + bodyStart;
        var text = Utf8NoBom.GetString(buffer, bodyStart, read - bodyStart);
        var lines = SplitLines(text);
        if (lines.Count == 0) return result;

        // 只保留最近 N 行
        var skip = Math.Max(0, lines.Count - InitialBackfillMaxLines);
        long lineOffset = bodyOffset;
        for (var i = 0; i < lines.Count; i++)
        {
            var (line, byteLen) = lines[i];
            if (i >= skip) FeedLine(line, lineOffset, e => result.Add(e));
            lineOffset += byteLen;
        }
        return result;
    }

    /// <summary>读取当前文件从 _offset 起的新增字节并解析（实时增量）。</summary>
    private void DrainNewData(bool isLive)
    {
        if (_stream == null || _currentPath == null) return;
        if (_stream.Length <= _offset) return;

        _stream.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[ReadChunkSize];
        int n;
        while ((n = _stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++) _pendingBytes.Add(buffer[i]);
            _offset += n;
        }
        _lastDataTime = DateTime.Now;

        // 只处理到最后一整行（0x0A 不会出现在 UTF-8 多字节序列中，按字节找换行安全）
        var lastNewline = _pendingBytes.LastIndexOf((byte)'\n');
        if (lastNewline < 0) return;

        var completeBytes = lastNewline + 1;
        var text = Utf8NoBom.GetString(_pendingBytes.GetRange(0, completeBytes).ToArray());
        _pendingBytes.RemoveRange(0, completeBytes);

        var lines = SplitLines(text);
        // 新增数据的起始行偏移 = 已读偏移 - 本次总字节 + 首行之前的字节（此处从 0 起）
        long lineOffset = _offset - _pendingBytes.Count - SumBytes(lines);
        foreach (var (line, byteLen) in lines)
        {
            FeedLine(line, lineOffset, e =>
            {
                if (isLive) EmitLive(e);
            });
            lineOffset += byteLen;
        }
        // 注意：这里不做末尾冲刷——多行事件可能跨轮询到达，无条件冲刷会把续行拆成
        // "INF/日期零点"的孤儿条目（S2）。冲刷时机：新行头到达（FeedLine 内）或停写约 2 秒（FlushIfIdle）。
    }

    private static long SumBytes(List<(string line, int byteLen)> lines)
    {
        long sum = 0;
        foreach (var (_, len) in lines) sum += len;
        return sum;
    }

    private static List<(string line, int byteLen)> SplitLines(string text)
    {
        var result = new List<(string, int)>();
        var span = text.AsSpan();
        var pos = 0;
        while (pos < span.Length)
        {
            var idx = span[pos..].IndexOf('\n');
            string line;
            int byteLen;
            if (idx < 0)
            {
                line = span[pos..].TrimEnd('\r').ToString();
                byteLen = Utf8NoBom.GetByteCount(text[pos..]);
                pos = span.Length;
            }
            else
            {
                var raw = span.Slice(pos, idx).TrimEnd('\r').ToString();
                line = raw;
                // 该行在文件中的字节数 = 行文本 + \r? + \n
                byteLen = Utf8NoBom.GetByteCount(span.Slice(pos, idx).ToString()) + 1;
                pos += idx + 1;
            }
            result.Add((line, byteLen));
        }
        return result;
    }

    /// <summary>逐行喂给多行事件切分状态机。onEmit 在每收到新行头时冲刷上一条事件。</summary>
    private void FeedLine(string line, long lineOffset, Action<LogEntry> onEmit)
    {
        var m = HeaderRegex.Match(line);
        if (m.Success)
        {
            FlushPending(onEmit);
            DateTime time;
            if (!DateTime.TryParseExact(m.Groups["time"].Value, "HH:mm:ss.fff",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t))
                t = DateTime.Now;
            time = _currentFileDate.Add(t.TimeOfDay);

            _pendingEntry = new PendingEntry
            {
                Time = time,
                Level = NormalizeLevel(m.Groups["level"].Value),
                Instance = m.Groups["instance"].Success ? m.Groups["instance"].Value : null,
                Source = m.Groups["source"].Success ? m.Groups["source"].Value.Trim() : "",
                Offset = lineOffset,
                HeaderLine = line
            };
        }
        else
        {
            // 不匹配行头 → 并入上一条（容错降级，绝不丢行）；
            // 文件开头残段（尚无头行）合成一条占位事件，同样不丢
            if (_pendingEntry == null)
            {
                _pendingEntry = new PendingEntry
                {
                    Time = _currentFileDate,
                    Level = LogLevels.Inf,
                    Instance = null,
                    Source = "",
                    Offset = lineOffset,
                    HeaderLine = ""
                };
            }
            _pendingEntry.BodyLines.Add(line);
        }
    }

    private void FlushPending(Action<LogEntry>? onEmit = null)
    {
        if (_pendingEntry == null || _currentPath == null) { _pendingEntry = null; return; }
        var p = _pendingEntry;
        _pendingEntry = null;

        // 消息体拆分：找到疑似异常段起始行，之前为 Message，之后为 Exception
        string? exception = null;
        var messageLines = p.BodyLines;
        var exIndex = p.BodyLines.FindIndex(l => ExceptionStartRegex.IsMatch(l));
        if (exIndex > 0)
        {
            exception = string.Join('\n', p.BodyLines.Skip(exIndex));
            messageLines = p.BodyLines.Take(exIndex).ToList();
        }
        else if (exIndex == 0)
        {
            exception = string.Join('\n', p.BodyLines);
            messageLines = [];
        }

        var entry = new LogEntry(
            p.Time, p.Level, p.Instance, p.Source,
            string.Join('\n', messageLines).TrimEnd(),
            exception,
            p.Offset, _currentPath);
        onEmit?.Invoke(entry);
    }

    private static string NormalizeLevel(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "DBG" or "DEBUG" => LogLevels.Dbg,
        "INF" or "INFO" => LogLevels.Inf,
        "WRN" or "WARN" or "WARNING" => LogLevels.Wrn,
        "ERR" or "ERROR" or "FTL" or "FATAL" => LogLevels.Err,
        var other => other.Length > 3 ? other[..3] : other
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poke.Set();
        // 中危2：先等后台线程退出再释放 _poke / stream，
        // 避免 worker 正 WaitOne/读流时被 Dispose 打中抛 ObjectDisposedException（后台线程未观察异常可终止进程）
        _worker.Join(2000);
        _watcher?.Dispose();
        _watcher = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _poke.Dispose();
    }

    /// <summary>正在累积中的多行事件（行头 + 后续正文行）。</summary>
    private sealed class PendingEntry
    {
        public DateTime Time;
        public string Level = LogLevels.Inf;
        public string? Instance;
        public string Source = "";
        public long Offset;
        public string HeaderLine = "";
        public List<string> BodyLines = [];
    }
}
