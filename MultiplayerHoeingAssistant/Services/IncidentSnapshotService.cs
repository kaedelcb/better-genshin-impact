using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 事发录像服务（嘟嘟可·异常监控联动，纯本地"飞行记录仪"）：
/// 开启后且本机 BGI 运行中，后台按设定间隔（默认 1 秒，0.5~5 秒可调）截一帧 JPEG（低规格 960px/质量 60）
/// 进环形缓冲（默认 10 秒，5~60 秒可调）——
/// 优先截游戏窗口画面（PrintWindow 绘窗口自身内容：副屏/遮挡/DPI 缩放全免疫），找不到游戏窗口时回退主屏全屏；
/// 命中标记了"存快照"（WatchRule.Snapshot）的监控规则时：
///   1) 立即把触发时刻前 N 秒（默认 3，1~20 可调）的帧落盘（前段先落，防封盘前进程退出丢现场）；
///   2) 继续采集触发后 M 秒（默认 3，1~20 可调）的帧，齐后写 trigger.json 封盘。
/// 落盘结构：助手 exe 目录 log/incidents/yyyy-MM-dd/yyyyMMdd_HHmmss_规则名/ 下 frame_-03.0.jpg…frame_+03.0.jpg + trigger.json
/// （按日期分层，避免所有日期的快照混在一层；trigger.json = ExceptionRecord 序列化，自带前后各 5 行日志上下文
/// 与命中时的配置组/路线，由 KeywordWatchService 写好）。
/// 录像参数（间隔/缓冲/前后秒数）在事发录像设置弹窗里调整，持久化到 dodoco_settings.json，
/// 服务每拍动态读取生效（触发瞬间的参数快照进 PendingIncident，事件进行中改设置不影响在途事件）。
///
/// 防刷：同规则 30 秒冷却（卡死持续刷日志时不连拍）；事件目录只留最近 50 个，超出自动删最旧。
/// 远程成员命中（SourceFile 形如 "远程:玩家名"）不触发——截本机屏幕对他机没意义。
/// 线程模型：Timer 回调与 NotifyTrigger 都在后台线程；环形缓冲/待封盘列表共用 _lock；
/// 截图/写盘全部后台执行，绝不碰 UI 线程。异常与"为何没录到"均写 assistant_runtime 日志
/// （[IncidentSnapshot] 前缀，60s 节流；零帧事件目录内另留 _no_frames.txt 说明）。
/// </summary>
public sealed class IncidentSnapshotService : IDisposable
{
    /// <summary>同规则快照冷却（防持续刷日志连拍）。</summary>
    private static readonly TimeSpan CooldownPerRule = TimeSpan.FromSeconds(30);
    /// <summary>事件目录保留个数（超出删最旧）。</summary>
    private const int MaxIncidentDirs = 50;
    /// <summary>录像帧规格（独立低规格，与监控 Tab 高清帧互不影响）：960px / JPEG 质量 60。</summary>
    private const int FrameWidth = 960;
    private const long FrameQuality = 60L;
    /// <summary>参数合法域（与 DodocoSettingsService.NormalizeLocked 一致，这里再钳一道防手改 JSON）。</summary>
    private const double MinIntervalSec = 0.5, MaxIntervalSec = 5;
    private const int MinBufferSec = 5, MaxBufferSec = 60;
    private const int MinSaveSec = 1, MaxSaveSec = 20;

    private readonly ScreenshotService _screenshot;
    /// <summary>总开关（DodocoSettings.IncidentSnapshotEnabled）。</summary>
    private readonly Func<bool> _enabledProvider;
    /// <summary>本机 BGI 是否在运行（不在跑就不录——没有任务可出事；注意不能用日志活跃度门控：卡死恰恰日志停写）。</summary>
    private readonly Func<bool> _bgiRunningProvider;
    /// <summary>规则查询（ruleId → WatchRule，判 Snapshot 开关用）；查不到视为不开快照。</summary>
    private readonly Func<string, WatchRule?> _ruleProvider;
    /// <summary>录像参数提供者（间隔/缓冲时长/前后保存秒数；每拍动态读取，改设置即生效）。</summary>
    private readonly Func<DodocoSettings> _settingsProvider;
    private readonly Timer _timer;
    /// <summary>当前 Timer 实际节拍（设置里的截图间隔变更时在下一拍 Change 跟进）。</summary>
    private TimeSpan _appliedInterval;
    private readonly object _lock = new();
    private readonly List<(DateTime Time, byte[] Jpeg)> _ring = [];
    private readonly List<PendingIncident> _pending = [];
    /// <summary>冷却表：ruleId → 上次触发快照时刻。</summary>
    private readonly Dictionary<string, DateTime> _cooldown = new();
    /// <summary>防重入：一帧截图未完成时跳过下一拍。</summary>
    private int _capturing;
    private bool _disposed;
    /// <summary>诊断日志节流：录像跳过/截图失败原因每 60 秒最多写一行（assistant_runtime 日志，防刷屏）。</summary>
    private DateTime _lastDiagLog = DateTime.MinValue;
    /// <summary>上一拍的 BGI 进程检测结果（null=尚未检测）。"录像暂停/恢复"日志边沿触发用：仅状态跳变时报一行，稳态静默（监控/观察模式本机无 BGI 是常态，周期报是纯噪音）。</summary>
    private bool? _lastBgiRunning;

    /// <summary>节流写诊断行（默认 60 秒一行；事发录像排障专用，正常时零噪音）。</summary>
    private void DiagLogThrottled(string message)
    {
        var now = DateTime.Now;
        if (now - _lastDiagLog < TimeSpan.FromSeconds(60)) return;
        _lastDiagLog = now;
        RuntimeLog.WriteLine($"[IncidentSnapshot] {message}");
    }

    /// <summary>事件快照根目录（助手 exe 目录 log/incidents）。</summary>
    public static string IncidentRootDir => Path.Combine(LogFileBrowser.AssistantLogDir, "incidents");

    public IncidentSnapshotService(
        ScreenshotService screenshot,
        Func<bool> enabledProvider,
        Func<bool> bgiRunningProvider,
        Func<string, WatchRule?> ruleProvider,
        Func<DodocoSettings> settingsProvider)
    {
        _screenshot = screenshot;
        _enabledProvider = enabledProvider;
        _bgiRunningProvider = bgiRunningProvider;
        _ruleProvider = ruleProvider;
        _settingsProvider = settingsProvider;
        _appliedInterval = GetInterval();
        _timer = new Timer(Tick, null, _appliedInterval, _appliedInterval);
    }

    /// <summary>当前设置的截图间隔（钳到 0.5~5 秒；NaN/∞ 等手改 JSON 的非法值回落默认 1 秒——
    /// Math.Clamp 不拦 NaN，TimeSpan.FromSeconds(NaN) 会直接抛）。</summary>
    private TimeSpan GetInterval()
    {
        var v = _settingsProvider().IncidentCaptureIntervalSeconds;
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 1;
        return TimeSpan.FromSeconds(Math.Clamp(v, MinIntervalSec, MaxIntervalSec));
    }

    /// <summary>当前设置的环形缓冲时长（钳到 5~60 秒）。</summary>
    private int GetBufferSeconds() =>
        Math.Clamp(_settingsProvider().IncidentBufferSeconds, MinBufferSec, MaxBufferSec);

    /// <summary>
    /// 规则命中转发入口（由 DodocoViewModel 订阅 KeywordWatchService.RecordAdded 后转发；后台线程调用）。
    /// 只响应：总开关开 + 规则标了 Snapshot + 本机记录（非远程）+ 过冷却。
    /// </summary>
    public void NotifyTrigger(ExceptionRecord record)
    {
        try
        {
            if (_disposed) return;
            if (record.SourceFile.StartsWith("远程:")) return; // 远程成员命中不截本机屏
            if (!_enabledProvider()) return;
            if (_ruleProvider(record.RuleId)?.Snapshot != true) return;

            var now = DateTime.Now;
            // 触发瞬间的参数快照进事件：事件进行中改设置不影响在途事件
            var cfg = _settingsProvider();
            var intervalSec = GetInterval().TotalSeconds;
            var preSeconds = Math.Clamp(cfg.IncidentPreSeconds, MinSaveSec, MaxSaveSec);
            var postSeconds = Math.Clamp(cfg.IncidentPostSeconds, MinSaveSec, MaxSaveSec);
            PendingIncident pending;
            lock (_lock)
            {
                if (_cooldown.TryGetValue(record.RuleId, out var last) && now - last < CooldownPerRule)
                    return;
                _cooldown[record.RuleId] = now;
                pending = new PendingIncident
                {
                    Record = record,
                    TriggerTime = now,
                    PreSeconds = preSeconds,
                    PostFramesWanted = Math.Max(1, (int)Math.Ceiling(postSeconds / intervalSec)),
                    // 封盘兜底时限：后段时长 + 1.5 个间隔的补采宽限，超了无论补几帧都封盘
                    Deadline = now + TimeSpan.FromSeconds(postSeconds + intervalSec * 1.5),
                    Dir = BuildIncidentDir(now, record.RuleName)
                };
                _pending.Add(pending);
            }

            // 前段立即落盘（防封盘前进程退出丢现场）；文件 IO 放线程池，不堵日志线程
            RuntimeLog.WriteLine($"[IncidentSnapshot] 触发: 规则「{record.RuleName}」，环形缓冲 {RingSnapshotCount()} 帧");
            Task.Run(() => DumpPreFrames(pending));
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteLine($"[IncidentSnapshot] 触发处理失败: {ex.Message}");
        }
    }

    /// <summary>每拍：按需跟进间隔设置 + 录像进环 + 给待封盘事件补后段帧 + 到期封盘。</summary>
    private void Tick(object? state)
    {
        if (_disposed) return;
        try
        {
            // 截图间隔设置变更时下一拍跟进（Timer.Change 在自身回调里调用是安全的）
            var desired = GetInterval();
            if (desired != _appliedInterval)
            {
                _appliedInterval = desired;
                _timer.Change(desired, desired);
            }
            (DateTime Time, byte[] Jpeg)? frame = null;
            // 总开关关 / BGI 没在跑 → 不录（BGI 进程检查有成本，本就无法触发时别白截）
            if (!_enabledProvider())
            {
                // 总开关关着：完全静默（用户主动关的功能不该有日志）
            }
            else if (!_bgiRunningProvider())
            {
                // 边沿触发：仅在"运行 → 未检测到"跳变时报一行，稳态静默。
                // 文案如实描述检测来源：当前 Windows 会话的 BetterGI 进程枚举（非 MainViewModel.IsBgiRunning）。
                if (_lastBgiRunning != false)
                {
                    _lastBgiRunning = false;
                    RuntimeLog.WriteLine("[IncidentSnapshot] 录像暂停：总开关已开但未检测到本机 BGI 进程（当前会话无 BetterGI 进程）");
                }
            }
            else if (Interlocked.Exchange(ref _capturing, 1) == 0)
            {
                // 恢复边沿：上一拍未检测到、本拍检测到 → 报一行恢复（放在本分支内避免包裹截图体重排缩进；
                // 若上一帧截图尚未完成则顺延到下一拍补报，最多晚一秒，不会重复）
                if (_lastBgiRunning == false)
                {
                    RuntimeLog.WriteLine("[IncidentSnapshot] 录像恢复：检测到本机 BGI 进程");
                }
                _lastBgiRunning = true;
                try
                {
                    // 优先截游戏画面（游戏可能在副屏，主屏桌面没用）；找不到游戏窗口/最小化时回退主屏全屏
                    var jpeg = _screenshot.CaptureGameWindowJpeg(FrameWidth, FrameQuality, out _, out _);
                    if (jpeg == null)
                    {
                        jpeg = _screenshot.CaptureJpeg(FrameWidth, FrameQuality, out _, out _);
                        DiagLogThrottled("未截到游戏窗口（游戏未启动/最小化/锁屏），本帧回退主屏全屏");
                    }
                    if (jpeg is { Length: > 0 })
                        frame = (DateTime.Now, jpeg);
                    else
                        DiagLogThrottled("截图返回空帧：可能处于锁屏/安全桌面");
                }
                catch (Exception capEx)
                {
                    DiagLogThrottled($"截图异常：{capEx.GetType().Name}: {capEx.Message}（锁屏/桌面切换时常见）");
                }
                finally
                {
                    Interlocked.Exchange(ref _capturing, 0);
                }
            }

            lock (_lock)
            {
                var now = DateTime.Now;
                if (frame != null)
                {
                    _ring.Add(frame.Value);
                    // 环形缓冲按时间裁剪（缓冲时长/间隔都可热调，按帧数裁剪会失真）
                    var cutoff = now.AddSeconds(-GetBufferSeconds());
                    while (_ring.Count > 0 && _ring[0].Time < cutoff) _ring.RemoveAt(0);
                }

                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    // 后段帧：本拍新帧且晚于触发时刻（防同一帧既算前段又算后段）
                    if (frame != null && frame.Value.Time > p.TriggerTime && p.PostFrames.Count < p.PostFramesWanted)
                        p.PostFrames.Add(frame.Value);
                    if (p.PostFrames.Count < p.PostFramesWanted && now < p.Deadline) continue;
                    _pending.RemoveAt(i);
                    Task.Run(() => FinalizeIncident(p)); // 封盘写 trigger.json + 清理旧目录
                }
            }
        }
        catch (Exception ex)
        {
            DiagLogThrottled($"录像拍失败: {ex.Message}");
        }
    }

    /// <summary>当前环形缓冲帧数（诊断日志用）。</summary>
    private int RingSnapshotCount()
    {
        lock (_lock) return _ring.Count;
    }

    /// <summary>前段落盘：触发时刻前 PreSeconds 秒内的环形帧（后段帧由封盘时补写）。</summary>
    private void DumpPreFrames(PendingIncident p)
    {
        try
        {
            List<(DateTime Time, byte[] Jpeg)> preFrames;
            lock (_lock)
            {
                preFrames = _ring.Where(f => f.Time >= p.TriggerTime.AddSeconds(-p.PreSeconds - 0.5)
                    && f.Time <= p.TriggerTime).ToList();
            }
            Directory.CreateDirectory(p.Dir);
            foreach (var f in preFrames)
                WriteFrame(p.Dir, FrameFileName(f.Time - p.TriggerTime), f.Jpeg);
            p.PreFrameCount = preFrames.Count;
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteLine($"[IncidentSnapshot] 前段落盘失败: {ex.Message}");
        }
    }

    /// <summary>封盘：补写后段帧 + trigger.json + 清理超量旧目录。</summary>
    private void FinalizeIncident(PendingIncident p)
    {
        try
        {
            Directory.CreateDirectory(p.Dir);
            foreach (var f in p.PostFrames)
                WriteFrame(p.Dir, FrameFileName(f.Time - p.TriggerTime), f.Jpeg);
            // trigger.json 是给人看的独立文件：用宽松转义让中文日志原文可读
            // （区别于 KeywordWatchService 的 JSONL 异常库——那个沿用默认转义，不在此处统一）
            File.WriteAllText(Path.Combine(p.Dir, "trigger.json"),
                JsonSerializer.Serialize(p.Record, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
            // 一帧都没有时留诊断说明（否则用户只看到 trigger.json 会以为功能坏了）
            if (!Directory.EnumerateFiles(p.Dir, "frame_*.jpg").Any())
            {
                File.WriteAllText(Path.Combine(p.Dir, "_no_frames.txt"),
                    "本次触发没有保存到任何画面帧。\r\n" +
                    "可能原因（详见助手 log/assistant_runtime 日志的 [IncidentSnapshot] 行）：\r\n" +
                    "  1. 触发时正处于锁屏/安全桌面/显示器休眠，系统禁止截屏；\r\n" +
                    "  2. 未检测到本机 BGI 进程，录像未激活（观察模式或未配置 BGI 路径时如此）；\r\n" +
                    "  3. 总开关刚打开，环形缓冲还没积累到帧。\r\n");
                RuntimeLog.WriteLine($"[IncidentSnapshot] 封盘但零帧: {p.Dir}（前段 {p.PreFrameCount} 帧 / 后段 {p.PostFrames.Count} 帧）");
            }
            else
            {
                RuntimeLog.WriteLine($"[IncidentSnapshot] 已封盘: {p.Dir}（前段 {p.PreFrameCount} 帧 / 后段 {p.PostFrames.Count} 帧）");
            }
            PruneOldIncidents();
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteLine($"[IncidentSnapshot] 封盘失败: {ex.Message}");
        }
    }

    /// <summary>帧文件名：frame_-03.0.jpg（触发前 3 秒）… frame_+00.0.jpg（触发当拍）… frame_+03.0.jpg。
    /// 保留一位小数：截图间隔最小 0.5 秒，亚秒偏移（如 -02.5）不能四舍五入撞名丢帧。
    /// 用 InvariantCulture：自定义格式的小数点随区域文化变（如 de-DE 会变逗号），文件名必须稳定。</summary>
    private static string FrameFileName(TimeSpan offset)
    {
        return FormattableString.Invariant($"frame_{offset.TotalSeconds:+00.0;-00.0}.jpg");
    }

    private static void WriteFrame(string dir, string name, byte[] jpeg)
    {
        File.WriteAllBytes(Path.Combine(dir, name), jpeg);
    }

    private static string BuildIncidentDir(DateTime time, string ruleName)
    {
        // 按日期分层：incidents/yyyy-MM-dd/yyyyMMdd_HHmmss_规则名/
        return Path.Combine(IncidentRootDir, $"{time:yyyy-MM-dd}", $"{time:yyyyMMdd_HHmmss}_{SanitizeRuleName(ruleName)}");
    }

    /// <summary>目录名规则名段落的清洗（与建目录同一算法，查找匹配靠它保持一致）。</summary>
    private static string SanitizeRuleName(string ruleName)
    {
        var safe = ruleName;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        if (safe.Length > 24) safe = safe[..24];
        return safe;
    }

    /// <summary>查找某条异常记录对应的事发事件目录：按"目录名前缀时刻与记录时间差 ≤5 秒 + 规则名段落一致"匹配。
    /// 找不到（规则未开快照/触发时零帧/已被数量清理）返回 null。
    /// 新布局 incidents/yyyy-MM-dd/时刻_规则名（±5 秒匹配可能跨零点，前后各多看一天）；
    /// 旧布局（平铺在 incidents/ 根下）兼容查找。</summary>
    public static string? FindIncidentDir(DateTime recordTime, string ruleName)
    {
        try
        {
            if (!Directory.Exists(IncidentRootDir)) return null;
            var safe = SanitizeRuleName(ruleName);
            for (var d = -1; d <= 1; d++)
            {
                var dateDir = Path.Combine(IncidentRootDir, recordTime.Date.AddDays(d).ToString("yyyy-MM-dd"));
                var hit = FindIncidentInDir(dateDir, recordTime, safe);
                if (hit != null) return hit;
            }
            // 旧平铺布局兼容
            return FindIncidentInDir(IncidentRootDir, recordTime, safe);
        }
        catch { /* 目录枚举失败按未找到处理 */ }
        return null;
    }

    /// <summary>在指定目录的直接子目录里找匹配的事件目录。</summary>
    private static string? FindIncidentInDir(string dir, DateTime recordTime, string safeRuleName)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.Length < 17) continue;
            if (!DateTime.TryParseExact(name[..15], "yyyyMMdd_HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t)) continue;
            if (Math.Abs((t - recordTime).TotalSeconds) > 5) continue;
            if (name[16..] == safeRuleName) return sub;
        }
        return null;
    }

    /// <summary>目录名是否为事件目录（yyyyMMdd_HHmmss_ 前缀）。</summary>
    private static bool IsIncidentDirName(string name)
    {
        return name.Length >= 17 && name[15] == '_'
            && DateTime.TryParseExact(name[..15], "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>目录名是否为日期分层目录（yyyy-MM-dd）。</summary>
    private static bool IsDateDirName(string name)
    {
        return DateTime.TryParseExact(name, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }

    /// <summary>事件目录名带时间戳前缀，字典序=时间序；新（日期分层）旧（平铺）布局一起参与清点，只留最近 MaxIncidentDirs 个；
    /// 顺手删掉清空后的日期目录。</summary>
    private static void PruneOldIncidents()
    {
        try
        {
            if (!Directory.Exists(IncidentRootDir)) return;
            var roots = Directory.GetDirectories(IncidentRootDir);
            var dirs = roots.Where(d => IsIncidentDirName(Path.GetFileName(d))) // 旧平铺
                .Concat(roots.Where(d => IsDateDirName(Path.GetFileName(d)))
                    .SelectMany(Directory.GetDirectories)) // 新：日期分层下的事件目录
                .OrderByDescending(d => Path.GetFileName(d))
                .ToList();
            foreach (var old in dirs.Skip(MaxIncidentDirs))
                Directory.Delete(old, true);
            // 清空后的日期目录不留空壳
            foreach (var dateDir in roots.Where(d => IsDateDirName(Path.GetFileName(d))))
                if (Directory.Exists(dateDir) && !Directory.EnumerateFileSystemEntries(dateDir).Any())
                    Directory.Delete(dateDir);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteLine($"[IncidentSnapshot] 清理旧事件目录失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        // 退出前尽力封盘在途事件（前段已在触发时落盘，这里补 trigger.json 保住日志上下文）
        List<PendingIncident> rest;
        lock (_lock)
        {
            rest = [.. _pending];
            _pending.Clear();
        }
        foreach (var p in rest) FinalizeIncident(p);
    }

    private sealed class PendingIncident
    {
        public ExceptionRecord Record = new();
        public DateTime TriggerTime;
        public DateTime Deadline;
        public string Dir = "";
        /// <summary>触发时快照的参数：前段保存秒数 / 后段补采帧数（= 事发后秒数 ÷ 截图间隔，向上取整至少 1 帧）。</summary>
        public int PreSeconds;
        public int PostFramesWanted;
        public List<(DateTime Time, byte[] Jpeg)> PostFrames = [];
        /// <summary>前段落盘帧数（DumpPreFrames 写完后置位，封盘日志/零帧诊断用）。</summary>
        public int PreFrameCount;
    }
}
