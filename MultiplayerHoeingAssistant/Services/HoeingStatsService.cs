using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 锄地运行数据统计服务（嘟嘟可 P3 / F5-B）。
/// 订阅 <see cref="BgiLogTailService"/> 的 LogEntry 流，用设计文档 §2.2 的日志模板做事件配对：
/// - 配置组级：锄地一条龙任务启动 ↔ 下一组启动/任务取消/批次收尾 → 每组实际运行时长；
/// - 路线级：开始执行地图追踪任务 / 开始执行JS脚本 ↔ 脚本执行结束 → 路线耗时；"执行路线 X 出错"无结束 → 中断；
/// - 联机级：人齐/所有成员已就绪 ↔ 联机锄地退出 → 联机会话时长与退出原因；
/// - 收益预估行解析保存（P5 报表用）。
///
/// 批次：从组启动到结束/取消为一个批次。批次键优先用联机上线 generation（主 VM 10s 状态流），
/// 拿不到用时间聚类（距上一批次收尾 >10 分钟视为新批次）。
/// 批次结束时追加写 log/dodoco_stats.{yyyy-MM-dd}.jsonl。
///
/// 容错原则：所有匹配 try/catch，匹配不上就跳过，绝不抛异常打断日志流。
/// 事件在后台线程触发，UI 层自行 Dispatcher。
/// </summary>
public sealed class HoeingStatsService : IDisposable
{
    // ===== 日志模板正则（容错：模板参数渲染可能带引号，统一用 "? 吸收）=====
    private static readonly Regex GroupStartRegex = new(
        @"锄地一条龙任务启动 \[配置组: ""?(?<group>[^\]""]+?)""?\]", RegexOptions.Compiled);
    private static readonly Regex TaskCancelledRegex = new(
        @"锄地一条龙任务被取消", RegexOptions.Compiled);
    private static readonly Regex RouteStartMapRegex = new(
        @"开始执行地图追踪任务: ""?(?<name>.+?)""?\s*$", RegexOptions.Compiled);
    private static readonly Regex RouteStartJsRegex = new(
        @"→ 开始执行JS脚本: ""?(?<name>.+?)""?\s*$", RegexOptions.Compiled);
    private static readonly Regex RouteEndRegex = new(
        @"→ 脚本执行结束: ""?(?<name>.+?)""?\s*$", RegexOptions.Compiled);
    private static readonly Regex RouteErrorRegex = new(
        @"执行路线 ""?(?<name>.+?)""? 出错", RegexOptions.Compiled);
    private static readonly Regex MpStartCountRegex = new(
        @"\[联机\] 人齐，共 (?<n>\d+) 人，开始锄地", RegexOptions.Compiled);
    private static readonly Regex MpStartReadyRegex = new(
        @"\[联机\] 所有成员已就绪，开始锄地", RegexOptions.Compiled);
    private static readonly Regex MpExitRegex = new(
        @"\[联机\] ===== 联机锄地退出（原因: (?<reason>.+?)）=====", RegexOptions.Compiled);
    private static readonly Regex ProfitRegex = new(
        @"路线组合结果：精英 (?<e>\d+), 小怪 (?<m>\d+), 收益 (?<g>\d+) 摩拉, 预计用时 (?<eta>.*)", RegexOptions.Compiled);

    /// <summary>时间聚类阈值：距上一批次收尾超过该间隔再有组启动时开新批次。</summary>
    private static readonly TimeSpan BatchGapThreshold = TimeSpan.FromMinutes(10);
    /// <summary>空闲收尾：当前批次超过该时长无任何已识别事件则自动收尾落盘。</summary>
    private static readonly TimeSpan IdleFinalizeThreshold = TimeSpan.FromMinutes(10);

    private readonly BgiLogTailService _tail;
    /// <summary>联机上线 generation 提供者（主 VM 10s 状态流已有值；int.MaxValue 兜底值视为拿不到）。</summary>
    private readonly Func<int?> _generationProvider;
    private readonly object _lock = new();
    private readonly Timer _finalizeTimer;

    /// <summary>当前进行中的批次（null=未在批次中）。</summary>
    private HoeingBatchRecord? _current;
    /// <summary>当前打开的配置组（_current.Groups 的最后一项 EndTime==null）。</summary>
    private GroupStat? _openGroup;
    /// <summary>当前打开的联机会话。</summary>
    private MultiplayerSessionStat? _openSession;
    /// <summary>批次内最近一次已识别事件时间（空闲收尾用）。</summary>
    private DateTime _lastEventTime;
    /// <summary>上一批次收尾时间（时间聚类用）。</summary>
    private DateTime _lastBatchEndTime = DateTime.MinValue;

    /// <summary>批次状态变化（新增/进行中更新/收尾）。后台线程触发，参数为当前批次快照（可能为 null=刚收尾完成）。</summary>
    public event Action<HoeingBatchRecord?>? BatchChanged;
    /// <summary>批次收尾完成并已落盘。后台线程触发。</summary>
    public event Action<HoeingBatchRecord>? BatchFinalized;

    public HoeingStatsService(BgiLogTailService tail, Func<int?> generationProvider)
    {
        _tail = tail;
        _generationProvider = generationProvider;
        _tail.EntryReceived += OnEntry;
        _finalizeTimer = new Timer(_ => FinalizeIdleBatch(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>当前批次快照（UI 轮询用；后台线程安全副本）。</summary>
    public HoeingBatchRecord? GetCurrentBatchSnapshot()
    {
        lock (_lock) return _current == null ? null : DeepCopy(_current);
    }

    private void OnEntry(LogEntry entry)
    {
        try
        {
            ProcessEntry(entry);
        }
        catch (Exception ex)
        {
            // 绝不因单条日志解析异常打断统计流
            System.Diagnostics.Debug.WriteLine($"[HoeingStatsService] 处理日志异常: {ex.Message}");
        }
    }

    private void ProcessEntry(LogEntry entry)
    {
        var msg = entry.Message;
        if (string.IsNullOrEmpty(msg)) return;
        // 多行事件只取首行做模板匹配（模板都在单行内）
        var firstNl = msg.IndexOf('\n');
        var line = firstNl >= 0 ? msg[..firstNl] : msg;

        bool changed = false;
        lock (_lock)
        {
            // ---- 组启动 ----
            var m = GroupStartRegex.Match(line);
            if (m.Success)
            {
                StartGroup(m.Groups["group"].Value.Trim(), entry.Time);
                changed = true;
                NotifyChanged();
                return;
            }

            // ---- 任务取消 → 收尾批次 ----
            if (TaskCancelledRegex.IsMatch(line))
            {
                if (_current != null)
                {
                    FinalizeCurrentBatchLocked("锄地一条龙任务被取消", entry.Time);
                    changed = true;
                }
                if (changed) NotifyChanged();
                return;
            }

            // ---- 联机退出 → 关会话 + 收尾批次 ----
            m = MpExitRegex.Match(line);
            if (m.Success)
            {
                var reason = m.Groups["reason"].Value.Trim();
                if (_openSession != null)
                {
                    _openSession.EndTime = entry.Time;
                    _openSession.ExitReason = reason;
                    _openSession = null;
                }
                if (_current != null) FinalizeCurrentBatchLocked($"联机退出（{reason}）", entry.Time);
                NotifyChanged();
                return;
            }

            // 以下事件只在批次内记录；批次未开时忽略（或按需开批次）
            // ---- 联机开始 ----
            m = MpStartCountRegex.Match(line);
            if (m.Success)
            {
                EnsureBatch(entry.Time);
                if (_openSession == null)
                {
                    _openSession = new MultiplayerSessionStat
                    {
                        StartTime = entry.Time,
                        PlayerCount = int.TryParse(m.Groups["n"].Value, out var n) ? n : null
                    };
                    _current!.Sessions.Add(_openSession);
                    Touch(entry.Time);
                    changed = true;
                }
                if (changed) NotifyChanged();
                return;
            }
            if (MpStartReadyRegex.IsMatch(line))
            {
                EnsureBatch(entry.Time);
                if (_openSession == null)
                {
                    _openSession = new MultiplayerSessionStat { StartTime = entry.Time };
                    _current!.Sessions.Add(_openSession);
                    Touch(entry.Time);
                    changed = true;
                }
                if (changed) NotifyChanged();
                return;
            }

            // ---- 收益预估 ----
            m = ProfitRegex.Match(line);
            if (m.Success)
            {
                EnsureBatch(entry.Time);
                var estimate = new ProfitEstimate
                {
                    Elite = int.TryParse(m.Groups["e"].Value, out var e) ? e : 0,
                    Mobs = int.TryParse(m.Groups["m"].Value, out var mo) ? mo : 0,
                    Mora = int.TryParse(m.Groups["g"].Value, out var g) ? g : 0,
                    EtaText = m.Groups["eta"].Value.Trim(),
                    Time = entry.Time
                };
                _current!.Estimate = estimate;
                _current.Estimates.Add(estimate);
                Touch(entry.Time);
                NotifyChanged();
                return;
            }

            // ---- 路线开始 ----
            m = RouteStartMapRegex.Match(line);
            if (!m.Success) m = RouteStartJsRegex.Match(line);
            if (m.Success)
            {
                // 路线事件属于锄地过程：批次未开时也开一个（单机锄地场景无"任务启动"前缀也能统计）
                EnsureBatch(entry.Time);
                var route = new RouteStat
                {
                    Name = m.Groups["name"].Value.Trim(),
                    GroupName = _openGroup?.Name,
                    StartTime = entry.Time
                };
                _current!.Routes.Add(route);
                Touch(entry.Time);
                NotifyChanged();
                return;
            }

            // ---- 路线结束 ----
            m = RouteEndRegex.Match(line);
            if (m.Success && _current != null)
            {
                var name = m.Groups["name"].Value.Trim();
                var open = FindOpenRoute(name);
                if (open != null)
                {
                    open.EndTime = entry.Time;
                    Touch(entry.Time);
                    changed = true;
                }
                if (changed) NotifyChanged();
                return;
            }

            // ---- 路线出错（无结束 → 中断标记）----
            m = RouteErrorRegex.Match(line);
            if (m.Success && _current != null)
            {
                var name = m.Groups["name"].Value.Trim();
                var open = FindOpenRoute(name);
                if (open != null)
                {
                    open.EndTime = entry.Time;
                    open.Interrupted = true;
                    _current.InterruptedCount++;
                    Touch(entry.Time);
                    changed = true;
                }
                if (changed) NotifyChanged();
                return;
            }
        }
    }

    /// <summary>找最近一条未结束且同名的路线（容错：同名配对不上就取最近未结束的）。</summary>
    private RouteStat? FindOpenRoute(string name)
    {
        if (_current == null) return null;
        for (var i = _current.Routes.Count - 1; i >= 0; i--)
        {
            var r = _current.Routes[i];
            if (r.EndTime == null && r.Name == name) return r;
        }
        return null;
    }

    private void StartGroup(string groupName, DateTime time)
    {
        // 当前批次空闲超阈值 → 先收尾再开新批次（时间聚类）
        if (_current != null && time - _lastEventTime > BatchGapThreshold)
            FinalizeCurrentBatchLocked("空闲超时收尾", time);
        EnsureBatch(time);

        // 上一组未关 → 关闭（下一次组启动 = 上一组结束）
        if (_openGroup != null) _openGroup.EndTime = time;

        _openGroup = new GroupStat { Name = groupName, StartTime = time };
        _current!.Groups.Add(_openGroup);
        Touch(time);
    }

    /// <summary>开批次（幂等：已有进行中批次则只更新时间戳）。批次键优先 generation，否则时间聚类。</summary>
    private void EnsureBatch(DateTime time)
    {
        if (_current != null) return;
        int? generation = null;
        try
        {
            var gen = _generationProvider();
            // int.MaxValue 是主 VM 的"未知"兜底值，不作为批次键
            if (gen is > 0 and < int.MaxValue) generation = gen;
        }
        catch { /* 拿不到就走时间聚类 */ }
        _current = new HoeingBatchRecord { StartTime = time, Generation = generation };
        Touch(time);
    }

    private void Touch(DateTime time) => _lastEventTime = time;

    /// <summary>收尾当前批次：关组/关会话、写 JSONL、触发事件。调用方须持锁。</summary>
    private void FinalizeCurrentBatchLocked(string endReason, DateTime time)
    {
        if (_current == null) return;
        if (_openGroup != null) { _openGroup.EndTime ??= time; _openGroup = null; }
        if (_openSession != null) { _openSession.EndTime ??= time; _openSession = null; }
        // 未配对结束的路线一律标记中断
        foreach (var r in _current.Routes)
        {
            if (r.EndTime == null)
            {
                r.EndTime = time;
                r.Interrupted = true;
                _current.InterruptedCount++;
            }
        }
        _current.EndTime = time;
        _current.EndReason = endReason;
        var finished = _current;
        _current = null;
        _lastBatchEndTime = time;

        WriteBatch(finished);
        BatchFinalized?.Invoke(finished);
    }

    /// <summary>空闲收尾定时器：批次超 10 分钟无已识别事件 → 自动收尾落盘。
    /// 中危7 收敛：仍有未关闭的组或未结束的路线时不收尾——长战斗/长脚本可能 10 分钟
    /// 无"已识别事件"但任务还在跑，过早收尾会把后续路线切进下一批次。</summary>
    private void FinalizeIdleBatch()
    {
        try
        {
            lock (_lock)
            {
                if (_current != null && DateTime.Now - _lastEventTime > IdleFinalizeThreshold
                    && _openGroup == null && _current.Routes.All(r => r.EndTime != null))
                {
                    FinalizeCurrentBatchLocked("空闲超时收尾", _lastEventTime);
                    BatchChanged?.Invoke(null);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HoeingStatsService] 空闲收尾异常: {ex.Message}");
        }
    }

    private void NotifyChanged()
    {
        var snapshot = _current == null ? null : DeepCopy(_current);
        BatchChanged?.Invoke(snapshot);
    }

    /// <summary>批次深拷贝（事件快照，防 UI 读到后台正在改的引用）。</summary>
    private static HoeingBatchRecord DeepCopy(HoeingBatchRecord b) =>
        JsonSerializer.Deserialize<HoeingBatchRecord>(JsonSerializer.Serialize(b)) ?? b;

    /// <summary>批次落盘 JSONL。</summary>
    private static void WriteBatch(HoeingBatchRecord batch)
    {
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"dodoco_stats.{batch.StartTime:yyyy-MM-dd}.jsonl");
            File.AppendAllText(path,
                JsonSerializer.Serialize(batch, new JsonSerializerOptions { WriteIndented = false }) + "\n");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HoeingStatsService] 批次落盘失败: {ex.Message}");
        }
    }

    /// <summary>读取历史批次（全部 JSONL，按开始时间倒序；坏行跳过）。</summary>
    public List<HoeingBatchRecord> LoadHistoryBatches()
    {
        var result = new List<HoeingBatchRecord>();
        try
        {
            var dir = LogFileBrowser.AssistantLogDir;
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "dodoco_stats.*.jsonl")
                         .OrderByDescending(f => f))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var b = JsonSerializer.Deserialize<HoeingBatchRecord>(line,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (b != null) result.Add(b);
                    }
                    catch { /* 单行损坏不丢整文件 */ }
                }
            }
        }
        catch { /* 目录读取失败返回空 */ }
        return result.OrderByDescending(b => b.StartTime).ToList();
    }

    public void Dispose()
    {
        _tail.EntryReceived -= OnEntry;
        _finalizeTimer.Dispose();
    }
}
