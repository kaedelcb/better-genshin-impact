using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 房间实时日志汇聚（观众驱动的按需订阅：没人观看时零渲染零入队零发送）。
/// 对齐 MemberScreenshotRelayService 写法：
/// 1) 上报——订阅 SignalR 的 MemberLogSubscribersChanged 维护订阅者计数，仅当计数 >0
///    且 shareRealtimeLog 开启且已连房时，才把本机 <see cref="BgiLogTailService"/> 的实时
///    LogEntry 渲染成单行文本、500ms 合批上报（EntryReceived 只含实时新行；首次倒读的
///    2000 行走 HistoryBatchReceived，天然不转发）。
/// 2) 补发——始终维护最近 300 条 LogEntry 小环形（引用入队，成本极低）；订阅数 0→1 边沿时
///    把最近 200 行渲染成一批补发（批首插"以下为最近历史补发"标记行），让观众切过来立即有上下文。
/// 3) 省流——shareLogInfoOnly 开启时只放行 INF/WRN/ERR（DBG 丢弃）。
/// 4) 接收——懒订阅 SignalRClient.OnMemberLogBatch（实例更换自动换绑），BatchReceived 分发。
/// 断线重连：连接断开时订阅数清零（服务端订阅表已清），重连后由观看端重新订阅驱动恢复。
/// </summary>
public sealed class MemberLogRelayService : IDisposable
{
    /// <summary>合批间隔（与服务端限流 4 批/秒留一倍余量）。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>单批行数上限（与服务端负载上限一致；超出截断并插标记行）。</summary>
    private const int MaxLinesPerBatch = 500;
    /// <summary>队列上限兜底：超过即丢弃最旧并计数，防止任何路径下无上限增长。</summary>
    private const int MaxPendingLines = 2000;
    /// <summary>补发环形缓冲容量（LogEntry 引用）。</summary>
    private const int RecentRingCapacity = 300;
    /// <summary>订阅生效时补发的行数。</summary>
    private const int BackfillLines = 200;

    private readonly DodocoSettingsService _settingsService;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly BgiLogTailService _tail;
    private readonly Timer _flushTimer;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    /// <summary>最近条目环形（补发用；无论有无订阅者都维护，只存引用不渲染）。</summary>
    private readonly ConcurrentQueue<LogEntry> _recentEntries = new();

    private SignalRClient? _hooked;
    /// <summary>当前订阅者数（服务端 MemberLogSubscribersChanged 推送；断线清零）。</summary>
    private int _subscriberCount;
    /// <summary>因截断/超限丢弃的行数（下一批尾部插标记行用；入队线程与 Timer 线程并发，走 Interlocked）。</summary>
    private int _droppedSinceLastBatch;

    /// <summary>收到成员日志批（含自己的批——订阅方按 uid 自滤）。</summary>
    public event Action<MemberLogBatch>? BatchReceived;

    /// <summary>当前订阅者数（界面显示"N 人在看"用）。</summary>
    public int SubscriberCount => Volatile.Read(ref _subscriberCount);

    public MemberLogRelayService(
        BgiLogTailService tail,
        DodocoSettingsService settingsService,
        Func<SignalRClient?> clientProvider)
    {
        _tail = tail;
        _settingsService = settingsService;
        _clientProvider = clientProvider;
        _tail.EntryReceived += OnLocalEntry;
        _flushTimer = new Timer(Tick, null, FlushInterval, FlushInterval);
    }

    private void OnLocalEntry(LogEntry entry)
    {
        try
        {
            if (!_settingsService.Current.ShareRealtimeLog) return;

            // 补发环形始终维护（只存引用，成本极低），有订阅者切来时才有上下文可发
            _recentEntries.Enqueue(entry);
            while (_recentEntries.Count > RecentRingCapacity && _recentEntries.TryDequeue(out _)) { }

            // 观众驱动核心门槛：没人看就零渲染零入队零发送
            if (Volatile.Read(ref _subscriberCount) <= 0) return;
            if (_clientProvider()?.IsConnected != true) return;
            // 省流模式：只放行 INF/WRN/ERR
            if (_settingsService.Current.ShareLogInfoOnly && entry.Level == LogLevels.Dbg) return;

            _pendingLines.Enqueue(MemberLogLineCodec.Render(entry));
            // 队列上限兜底，超了丢最旧并计数（下一批批尾插丢弃标记行）
            while (_pendingLines.Count > MaxPendingLines && _pendingLines.TryDequeue(out _))
                Interlocked.Increment(ref _droppedSinceLastBatch);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberLogRelay] 渲染失败: {ex.Message}");
        }
    }

    /// <summary>订阅数变化（SignalR 回调线程）：0→1 边沿补发最近 200 行；1→0 边沿清空待发队列。</summary>
    private void HandleSubscribersChanged(int count)
    {
        try
        {
            var prev = Interlocked.Exchange(ref _subscriberCount, count);
            if (count > 0 && prev == 0)
            {
                SendBackfill();
            }
            else if (count == 0 && prev > 0)
            {
                // 观众走光：清掉待发队列，下次有人订阅时从补发重新开始（不发陈旧行）
                while (_pendingLines.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _droppedSinceLastBatch, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberLogRelay] 订阅数变化处理失败: {ex.Message}");
        }
    }

    /// <summary>订阅生效补发：最近 200 行渲染成一批发出（批首插标记行）。</summary>
    private void SendBackfill()
    {
        var client = _clientProvider();
        if (client?.IsConnected != true) return;
        if (!_settingsService.Current.ShareRealtimeLog) return;

        var infoOnly = _settingsService.Current.ShareLogInfoOnly;
        var snapshot = _recentEntries
            .Where(e => !infoOnly || e.Level != LogLevels.Dbg)
            .TakeLast(BackfillLines)
            .Select(MemberLogLineCodec.Render)
            .ToList();
        if (snapshot.Count == 0) return;
        snapshot.Insert(0, "...[以下为最近历史补发]...");
        _ = client.ReportMemberLogBatchAsync(snapshot, infoOnly);
    }

    private void Tick(object? state)
    {
        try
        {
            EnsureHooked();

            if (_pendingLines.IsEmpty) return;
            // 共享关闭 / 无人订阅 / 未连接：清空不发送（语义：关闭期间与无观众期间不留积压）
            if (!_settingsService.Current.ShareRealtimeLog
                || Volatile.Read(ref _subscriberCount) <= 0
                || _clientProvider()?.IsConnected != true)
            {
                while (_pendingLines.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _droppedSinceLastBatch, 0);
                return;
            }
            var client = _clientProvider()!;

            var batch = new List<string>(MaxLinesPerBatch);
            while (batch.Count < MaxLinesPerBatch && _pendingLines.TryDequeue(out var line))
                batch.Add(line);
            // 防积压：超上限的行丢弃并计数，批尾插标记行
            var overflow = 0;
            while (_pendingLines.TryDequeue(out _)) overflow++;
            var dropped = Interlocked.Exchange(ref _droppedSinceLastBatch, 0) + overflow;
            if (dropped > 0)
                batch.Add($"...[日志转发丢弃 {dropped} 行]...");

            _ = client.ReportMemberLogBatchAsync(batch, _settingsService.Current.ShareLogInfoOnly);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberLogRelay] 上报失败: {ex.Message}");
        }
    }

    /// <summary>客户端实例懒解析换绑（_signalRClient 可能晚于本服务创建）。</summary>
    private void EnsureHooked()
    {
        var client = _clientProvider();
        if (ReferenceEquals(client, _hooked)) return;
        if (_hooked != null)
        {
            _hooked.OnMemberLogBatch -= HandleBatch;
            _hooked.OnMemberLogSubscribersChanged -= HandleSubscribersChanged;
            _hooked.OnConnectionStateChanged -= HandleConnectionState;
        }
        _hooked = client;
        if (_hooked != null)
        {
            _hooked.OnMemberLogBatch += HandleBatch;
            _hooked.OnMemberLogSubscribersChanged += HandleSubscribersChanged;
            _hooked.OnConnectionStateChanged += HandleConnectionState;
        }
    }

    /// <summary>断线重连（一致性细节 11）：连接断开时订阅数清零——服务端订阅表已被断线清理。</summary>
    private void HandleConnectionState(bool connected)
    {
        if (!connected) Interlocked.Exchange(ref _subscriberCount, 0);
    }

    private void HandleBatch(MemberLogBatch batch)
    {
        try { BatchReceived?.Invoke(batch); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberLogRelay] 批分发失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        _tail.EntryReceived -= OnLocalEntry;
        if (_hooked != null)
        {
            _hooked.OnMemberLogBatch -= HandleBatch;
            _hooked.OnMemberLogSubscribersChanged -= HandleSubscribersChanged;
            _hooked.OnConnectionStateChanged -= HandleConnectionState;
            _hooked = null;
        }
    }
}

/// <summary>
/// 成员日志行编解码：LogEntry ↔ 单行文本（跨端传输格式）。
/// 渲染格式：`[HH:mm:ss.fff] [LVL] [实例] 来源 | 消息`，多行/异常折叠成一行（换行→⏎）。
/// 接收端用同款正则解析回 LogEntry 复用现有级别颜色列；解析失败降级为 INF 原文行。
/// </summary>
public static class MemberLogLineCodec
{
    private static readonly Regex LineRegex = new(
        @"^\[(?<t>\d{2}:\d{2}:\d{2}\.\d{3})\] \[(?<lvl>[A-Z]{3})\] \[(?<inst>[^\]]*)\](?: (?<src>[^|]*?))? \| (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>LogEntry → 单行文本。实例段始终保留（可为空），保证解析端格式稳定。</summary>
    public static string Render(LogEntry e)
    {
        var msg = Flatten(e.Message);
        if (e.Exception != null) msg += " ⏎ " + Flatten(e.Exception);
        return $"[{e.Time:HH:mm:ss.fff}] [{e.Level}] [{e.Instance ?? ""}] {e.Source} | {msg}";
    }

    /// <summary>单行文本 → LogEntry（接收端用）。sourceTag 标来源成员（如 "远程:玩家名"），
    /// 填进 SourceFile 供异常监控区分远程命中；解析失败降级为 INF 原文。</summary>
    public static LogEntry Parse(string line, DateTime fallbackTime, string sourceTag)
    {
        var m = LineRegex.Match(line);
        if (!m.Success)
            return new LogEntry(fallbackTime, LogLevels.Inf, null, "", line, null, 0, sourceTag);

        if (!DateTime.TryParseExact(m.Groups["t"].Value, "HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t))
            t = fallbackTime;
        // 日期部分取今天（行内只有时分秒；跨天场景偏差可接受，显示用）
        var time = DateTime.Today.Add(t.TimeOfDay);
        return new LogEntry(
            time,
            m.Groups["lvl"].Value,
            string.IsNullOrEmpty(m.Groups["inst"].Value) ? null : m.Groups["inst"].Value,
            m.Groups["src"].Success ? m.Groups["src"].Value.Trim() : "",
            m.Groups["msg"].Value,
            null, 0, sourceTag);
    }

    private static string Flatten(string text) => text.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
}
