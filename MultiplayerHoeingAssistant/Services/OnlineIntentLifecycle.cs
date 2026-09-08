using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>[P1] 上线意图状态：Idle 未上线 / Armed 已布防待全员就绪 / Executing AllReady 批次执行中。</summary>
public enum OnlineIntentState
{
    Idle,
    Armed,
    Executing,
}

/// <summary>[P1] 边沿上线意图处理结果（ApplyEdge 返回值）。</summary>
public enum OnlineEdgeResult
{
    /// <summary>无边沿（gen ≤ 基线，含 BGI 重启归零的下行对齐）。</summary>
    NoEdge,
    /// <summary>边沿存在但判定为历史残留/非新鲜触发，只同步基线不上线。</summary>
    BaselineSyncedOnly,
    /// <summary>60s 防抖窗内的重复边沿，只同步基线不上报。</summary>
    Debounced,
    /// <summary>意图被接受（已置 Armed），调用方应上报 generation。</summary>
    Accepted,
}

/// <summary>
/// [P1] 上线意图生命周期状态机：收编原散落在 MainViewModel 的 9 个状态字段
/// （_isOnlineReady/_onlineMode/_localOnlineGeneration/_genLock/_lastOnlineGeneration/
/// _lastProcessedAllReadyGeneration/_lastReportedOnlineGen/_lastOnlineEventReportedAtUtc/
/// _pendingOnlineReportGen/_onlineBaselineInitialized/_manuallyClearedOnline + generation 持久化）
/// 的全部读写。全部方法内部加锁（_gate）；状态跃迁经注入的日志回调打日志。
/// 事故背景：字段散落 12 处直接赋值点，异常路径漏改导致残留状态劫持后续行为（幻影上线循环、幻影开锄）。
/// 注意：网络 IO（SignalR 上报）不进状态机，由 MainViewModel 依据返回值/事件自行发起。
/// 日志回调（AddLog 内部走 Dispatcher.Invoke）一律在锁外触发，避免持锁等 UI 线程造成死锁。
/// </summary>
public sealed class OnlineIntentLifecycle
{
    private readonly object _gate = new();
    private readonly Action<string> _log;

    private OnlineIntentState _state = OnlineIntentState.Idle;
    private string _onlineMode = "none";

    /// <summary>本地定时上线自增 generation（驱动服务端 AllReady 判定，代替 BGI 的 onlineGeneration）。
    /// 持久化到 NexusBGI/assistant-online-generation.txt，重启后不复位（S3，复刻 BGI 侧 NotifyOnlineTask 模式：
    /// 服务端按 generation 边沿检测，丢弃 ≤ 历史值的事件，重启归零会被永久丢弃）。</summary>
    private int _localOnlineGeneration;

    // 边沿检测：记录上次处理过的 BGI 上线事件代序号与 AllReady 代序号，用于幂等保护
    private int _lastOnlineGeneration;
    private int _lastProcessedAllReadyGeneration;

    /// <summary>[B157] 最近一次成功送达服务端的上线事件 generation（0=本会话未成功上报过）。重连补报与 AllReadyAbort 判定用。</summary>
    private int _lastReportedOnlineGen;
    /// <summary>[B157] 最近一次上线事件成功送达时间（UTC）。命令路径 60s 防抖用：挡住任务流重跑风暴，
    /// 但不像旧 _isOnlineReady 守卫那样永久闩锁（永久闩锁是"偶发执行了不上线"的根因）。</summary>
    private DateTime _lastOnlineEventReportedAtUtc = DateTime.MinValue;
    /// <summary>[B157] 上报失败/通道未就绪时挂起的待补报 generation（0=无挂起）。由 10s 轮询与重连钩子补报。</summary>
    private int _pendingOnlineReportGen;
    /// <summary>[B157] 本会话是否已读过 onlineGeneration 字段。老 BGI 无 onlineTriggeredAt 时，
    /// 仅首见边沿做拦截（兼容语义），否则 BGI 持久化的历史 gen 会在助手启动/通道重建时幻影触发上线。</summary>
    private bool _onlineBaselineInitialized;
    /// <summary>[B157] 边沿新鲜度窗：触发时间距现在超过此时长 = 历史残留，只同步基线不自动上线。</summary>
    private static readonly TimeSpan OnlineEdgeFreshnessWindow = TimeSpan.FromMinutes(2);
    /// <summary>[B157] 命令上线重复边沿防抖窗：上线事件成功送达后此时长内的新边沿视为任务流重跑风暴，只同步基线不上报。</summary>
    private static readonly TimeSpan OnlineReportDebounce = TimeSpan.FromSeconds(60);
    /// <summary>用户手动清除上线后置 true，抑制定时自动上线。手动设定定时上线时清除。不持久化：
    /// 本会话内手动清除上线（ClearLocalOnline）后仍抑制到重新设定为止（语义不变）。</summary>
    private bool _manuallyClearedOnline = true;

    /// <summary>本地 generation 持久化文件路径（与 assistant-config.json 同目录，%APPDATA%/NexusBGI）。</summary>
    private static string LocalGenerationFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI", "assistant-online-generation.txt");

    /// <param name="log">日志回调（MainViewModel.AddLog）。构造时即恢复持久化的本地 generation。</param>
    public OnlineIntentLifecycle(Action<string> log)
    {
        _log = log;
        // S3：generation 持久化恢复——服务端按代序号边沿检测（丢弃 ≤ 历史值），重启归零会导致上线事件被永久丢弃。
        _localOnlineGeneration = LoadPersistedLocalGeneration();
    }

    // ================= 只读快照（全部加锁） =================

    /// <summary>当前状态（Idle/Armed/Executing）。</summary>
    public OnlineIntentState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>是否已上线（原 _isOnlineReady：Armed 或 Executing 均为 true）。</summary>
    public bool IsOnlineReady
    {
        get { lock (_gate) { return _state != OnlineIntentState.Idle; } }
    }

    /// <summary>上线模式（none/command/scheduled，原 _onlineMode）。</summary>
    public string OnlineMode
    {
        get { lock (_gate) { return _onlineMode; } }
    }

    /// <summary>手动清除上线抑制标志（原 _manuallyClearedOnline）。</summary>
    public bool ManuallyClearedOnline
    {
        get { lock (_gate) { return _manuallyClearedOnline; } }
    }

    /// <summary>最近已处理的 AllReady 代序号（原 _lastProcessedAllReadyGeneration，确认回执路径二次守卫用）。</summary>
    public int LastProcessedAllReadyGeneration
    {
        get { lock (_gate) { return _lastProcessedAllReadyGeneration; } }
    }

    /// <summary>挂起的待补报 generation（原 _pendingOnlineReportGen，10s 轮询是否触发补报的判定用）。</summary>
    public int PendingOnlineReportGen
    {
        get { lock (_gate) { return _pendingOnlineReportGen; } }
    }

    /// <summary>最近一次联机上线代序号（onlineGeneration 边沿检测的最近已处理值，原 _lastOnlineGeneration 读点）。
    /// 嘟嘟可批次统计用做批次键；0/int.MaxValue 为"未知"兜底值，返回 null，调用方应视为拿不到。</summary>
    public int? CurrentOnlineGeneration
    {
        get { lock (_gate) { return _lastOnlineGeneration is > 0 and < int.MaxValue ? _lastOnlineGeneration : null; } }
    }

    // ================= 1. 边沿上线意图（原 ApplyOnlineGenerationEdge） =================

    /// <summary>
    /// [P0-B/切片4/P1] onlineGeneration 边沿检测（事件帧/快照/v2 轮询共用）：BGI 重启归零先下行对齐基线，再比较触发。
    /// [P1] 新鲜度新语义：triggeredAtUtc 有值时每条边沿都必须过 2 分钟新鲜度窗（不再仅首见校验），
    /// 陈旧则只同步基线不上线；triggeredAtUtc 无值（老 BGI）保持兼容——仅首见边沿拦截，后续边沿接受。
    /// 保留双来源对齐（BGI gen 冲高时本地计数器向上对齐并写盘）与 60s 防抖。
    /// 返回 Accepted 时 genToReport 为应上报的 generation（SignalR 调用留在调用方）。
    /// </summary>
    public OnlineEdgeResult ApplyEdge(int gen, DateTime? triggeredAtUtc, out int genToReport)
    {
        string? pendingLog = null;
        string? transitionLog = null;
        OnlineEdgeResult result;
        lock (_gate)
        {
            genToReport = 0;
            // 首见判定在锁内完成（原由调用方读取 _onlineBaselineInitialized 后传入，现收编）
            var firstSight = !_onlineBaselineInitialized;
            _onlineBaselineInitialized = true;

            // [P0-B 止血] 下行对齐：BGI 重启后进程内代序号归零，先对齐再比较，避免边沿检测永久静音
            if (gen < _lastOnlineGeneration)
            {
                _lastOnlineGeneration = gen;
            }

            if (gen <= _lastOnlineGeneration)
            {
                result = OnlineEdgeResult.NoEdge;
            }
            else
            {
                _lastOnlineGeneration = gen;
                // [双来源对齐] BGI 计数器与助手本地计数器（定时上线用）共用服务端同一槽位，
                // 服务端只收 gen>历史值。BGI 侧冲高后（标记任务重跑），本地定时路径的更小 gen
                // 会被服务端当旧事件静默丢弃 → 永不开锄。这里把本地计数器向上对齐并写盘，
                // 保证后续定时上报严格大于服务端槽位。
                if (gen > _localOnlineGeneration)
                {
                    _localOnlineGeneration = gen;
                    PersistLocalGenerationLocked(gen);
                }

                // [P1] 新鲜度校验：有触发时间戳时每条边沿都必须过新鲜窗；无时间戳（老 BGI）仅首见拦截。
                bool stale;
                if (triggeredAtUtc.HasValue)
                {
                    stale = (DateTime.UtcNow - triggeredAtUtc.Value) >= OnlineEdgeFreshnessWindow;
                }
                else
                {
                    stale = firstSight;
                }

                if (stale)
                {
                    pendingLog = triggeredAtUtc.HasValue
                        ? $"检测到历史上线标记（generation={gen}，触发于 {triggeredAtUtc.Value:yyyy-MM-dd HH:mm:ss}Z），非新鲜触发，仅同步基线不上线"
                        : $"检测到历史上线标记（generation={gen}），非本次会话的新鲜触发，仅同步基线不自动上线";
                    result = OnlineEdgeResult.BaselineSyncedOnly;
                }
                // [B157] 60s 防抖（替代旧 _isOnlineReady 永久守卫）：上线事件刚成功送达且本地仍处"已上线"，
                // 短时间内的新边沿视为任务流重跑风暴（游戏反复关停重拉标记任务），只同步基线。
                // 上报失败不会布防（_lastOnlineEventReportedAtUtc 只在送达后更新），用户重跑即刻重试。
                else if (_state != OnlineIntentState.Idle
                    && (DateTime.UtcNow - _lastOnlineEventReportedAtUtc) < OnlineReportDebounce)
                {
                    pendingLog = $"检测到上线标记重复执行（generation={gen}），60 秒内已上报，跳过重复上报";
                    result = OnlineEdgeResult.Debounced;
                }
                else
                {
                    // 意图被接受：置 Armed（命令模式）并清零 AllReady 执行守卫的旧轮次残留，
                    // 避免历史高 gen 压住本轮（守卫只应防同一轮重复执行，不应跨轮压制新轮）
                    transitionLog = TransitionLocked(OnlineIntentState.Armed, $"边沿上线意图 generation={gen}");
                    _onlineMode = "command";
                    _lastProcessedAllReadyGeneration = 0;
                    genToReport = gen;
                    result = OnlineEdgeResult.Accepted;
                }
            }
        }
        if (pendingLog != null) _log(pendingLog);
        if (transitionLog != null) _log(transitionLog);
        return result;
    }

    // ================= 2. 定时/手动上线意图（原 MarkOnlineAsync 状态部分） =================

    /// <summary>
    /// [P1] 本地上线意图（定时/手动）：gen 自增+写盘+清零 AllReady 执行守卫+置 Armed，返回新 gen。
    /// 连接就绪判定依赖 SignalRClient，留在 MainViewModel——未就绪时不调用本方法（不增 gen、不置 Armed）。
    /// </summary>
    public int RaiseLocalIntent(string mode)
    {
        string? transitionLog;
        int gen;
        lock (_gate)
        {
            gen = ++_localOnlineGeneration;
            // S3：自增后立即写盘，保证重启后单调递增。写盘失败仅记日志，不影响本次上线事件。
            PersistLocalGenerationLocked(gen);
            // 新一轮上线意图：清掉 AllReady 执行守卫的旧轮次残留
            // （守卫只应防同一轮重复执行；历史高 gen 不应跨轮压制本轮）
            _lastProcessedAllReadyGeneration = 0;
            transitionLog = TransitionLocked(OnlineIntentState.Armed, $"本地上线意图（{mode}）generation={gen}");
            _onlineMode = mode;
        }
        if (transitionLog != null) _log(transitionLog);
        return gen;
    }

    // ================= 3. AllReady 守卫与批次收尾 =================

    /// <summary>
    /// [P1] AllReady 幂等守卫（原 _lastProcessedAllReadyGeneration 判定）：同一 generation 只处理一次；
    /// 通过后状态 Armed → Executing。返回 false = 重复/过期轮次，调用方直接 return。
    /// </summary>
    public bool TryBeginAllReady(int generation)
    {
        string? transitionLog = null;
        lock (_gate)
        {
            if (generation <= _lastProcessedAllReadyGeneration)
            {
                return false;
            }
            _lastProcessedAllReadyGeneration = generation;
            transitionLog = TransitionLocked(OnlineIntentState.Executing, $"AllReady 确认 generation={generation}");
        }
        if (transitionLog != null) _log(transitionLog);
        return true;
    }

    /// <summary>[P1b] 批次完成/异常/未能启动的统一复位出口：Executing → Idle（复位已上线标记与模式）。
    /// 按 generation 校验归属：被新一轮顶替的旧批次迟到退出时（_lastProcessedAllReadyGeneration 已被新轮冲高）
    /// 不复位新轮状态——否则新批次执行中会被旧批次的收尾误回 Idle（幻影下线）。</summary>
    public void OnBatchFinished(int generation, string reason)
    {
        string? transitionLog = null;
        lock (_gate)
        {
            if (_lastProcessedAllReadyGeneration == generation)
            {
                transitionLog = TransitionLocked(OnlineIntentState.Idle, reason);
            }
        }
        if (transitionLog != null) _log(transitionLog);
    }

    // ================= 4. 其余事件收口 =================

    /// <summary>
    /// [B157/P1] 服务端确认超时放弃本轮（OnAllReadyAbort，原 4013-4016）：仅当本地处于已上线才复位。
    /// 返回 true = 发生了复位（调用方据此记日志+上报状态）；false = 本端本就不在上线状态，无需处理。
    /// </summary>
    public bool OnAllReadyAborted()
    {
        string? transitionLog = null;
        bool changed = false;
        lock (_gate)
        {
            if (_state != OnlineIntentState.Idle)
            {
                transitionLog = TransitionLocked(OnlineIntentState.Idle, "服务端放弃本轮 AllReady");
                _pendingOnlineReportGen = 0;
                changed = true;
            }
        }
        if (transitionLog != null) _log(transitionLog);
        return changed;
    }

    /// <summary>
    /// [P1] 手动清除上线（原 ClearLocalOnline 状态部分，2326-2329）：立即复位为 Idle、
    /// 清挂起补报（不再补报已撤销的上线意图）、置手动清除抑制（抑制定时自动上线）。
    /// 基线封口是异步 IPC 后的第二步，见 SealBaselineAfterManualClear。
    /// </summary>
    public void OnManualCleared()
    {
        string? transitionLog;
        lock (_gate)
        {
            transitionLog = TransitionLocked(OnlineIntentState.Idle, "手动清除上线");
            _pendingOnlineReportGen = 0;
            _manuallyClearedOnline = true;
        }
        if (transitionLog != null) _log(transitionLog);
    }

    /// <summary>
    /// [P1] 手动清除后的基线封口（原 2330-2368）：把边沿基线提升到 BGI 当前 onlineGeneration，
    /// 使 ReportStatusAsync 的边沿探测（gen > 基线）不再触发重复上线；真正的命令上线（BGI 新执行
    /// "联机锄地上线"，generation 递增）仍能触发（新值 > 当前值）。读取失败由调用方传 int.MaxValue 兜底
    /// （保证本会话内不再被旧 generation 触发；BGI 重启归零后由下行对齐天然解除封口）。
    /// </summary>
    public void SealBaselineAfterManualClear(int bgiOnlineGeneration)
    {
        lock (_gate)
        {
            _lastOnlineGeneration = bgiOnlineGeneration;
        }
    }

    /// <summary>
    /// [P1] 切换到遥控器模式（原 ApplyModeRuntime 4721-4725）：复位上线状态与挂起补报，
    /// 基线封口 int.MaxValue，避免切回执行模式后 onlineGeneration 边沿检测自动触发上线。
    /// </summary>
    public void OnModeSwitched()
    {
        string? transitionLog;
        lock (_gate)
        {
            transitionLog = TransitionLocked(OnlineIntentState.Idle, "切换到遥控器模式");
            _pendingOnlineReportGen = 0;
            _lastOnlineGeneration = int.MaxValue;
        }
        if (transitionLog != null) _log(transitionLog);
    }

    /// <summary>[P1] 配置了定时上线时间（启动时已有配置 / 用户手动设定或清除）：解除手动清除抑制（原 _manuallyClearedOnline=false 置位点）。</summary>
    public void NoteScheduledOnlineConfigured()
    {
        lock (_gate)
        {
            _manuallyClearedOnline = false;
        }
    }

    /// <summary>[P1] 清除了自己的联机记录（ClearOnlineHistory 自清）：解除手动清除抑制，允许重新上线。</summary>
    public void NoteOnlineHistoryCleared()
    {
        lock (_gate)
        {
            _manuallyClearedOnline = false;
        }
    }

    // ================= 5. 上报跟踪与补报（原 ReportOnlineEventTrackedAsync/ReReportOnlineIntentIfNeededAsync 记账部分） =================

    /// <summary>[B157] 上线事件已送达服务端：记录送达 gen/时间（60s 防抖与重连补报基准），清掉 ≤ gen 的挂起。</summary>
    public void NoteReportDelivered(int gen)
    {
        lock (_gate)
        {
            _lastReportedOnlineGen = Math.Max(_lastReportedOnlineGen, gen);
            _lastOnlineEventReportedAtUtc = DateTime.UtcNow;
            if (_pendingOnlineReportGen <= gen) _pendingOnlineReportGen = 0;
        }
    }

    /// <summary>[B157] 上线事件未送达（无客户端/未连接/调用失败）：挂起待补报，由 10s 轮询与重连钩子自动补报。</summary>
    public void NoteReportPending(int gen)
    {
        lock (_gate)
        {
            _pendingOnlineReportGen = Math.Max(_pendingOnlineReportGen, gen);
        }
    }

    /// <summary>[B157] 补报判定：取 max(挂起待补报, 已上线时最近送达 gen)，> 0 则需要补报。</summary>
    public bool TryGetReReportGen(out int gen)
    {
        lock (_gate)
        {
            gen = Math.Max(_pendingOnlineReportGen, _state != OnlineIntentState.Idle ? _lastReportedOnlineGen : 0);
            return gen > 0;
        }
    }

    /// <summary>[B157] 补报送达：清挂起、更新送达 gen/时间。</summary>
    public void NoteReReportDelivered(int gen)
    {
        lock (_gate)
        {
            _pendingOnlineReportGen = 0;
            _lastReportedOnlineGen = Math.Max(_lastReportedOnlineGen, gen);
            _lastOnlineEventReportedAtUtc = DateTime.UtcNow;
        }
    }

    // ================= 内部 =================

    /// <summary>锁内状态跃迁：Idle 时顺带复位模式为 none；返回待打日志（锁外触发）。</summary>
    private string? TransitionLocked(OnlineIntentState next, string reason)
    {
        if (_state == next) return null;
        var message = $"[上线意图] {_state} → {next}（{reason}）";
        _state = next;
        if (next == OnlineIntentState.Idle)
        {
            _onlineMode = "none";
        }
        return message;
    }

    /// <summary>启动时读取持久化 generation。文件不存在/解析失败则从 0 开始（单机/未联机用户无感知）。</summary>
    private int LoadPersistedLocalGeneration()
    {
        try
        {
            var path = LocalGenerationFilePath;
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var saved) && saved >= 0)
            {
                return saved;
            }
        }
        catch (Exception ex)
        {
            _log($"[定时上线] 读取 assistant-online-generation.txt 失败，generation 从 0 开始: {ex.Message}");
        }
        return 0;
    }

    /// <summary>自增/对齐后写盘（锁内调用）。失败仅记日志，不影响本次上线事件。</summary>
    private void PersistLocalGenerationLocked(int generation)
    {
        string? errorLog = null;
        try
        {
            File.WriteAllText(LocalGenerationFilePath, generation.ToString());
        }
        catch (Exception ex)
        {
            errorLog = $"[定时上线] 写入 assistant-online-generation.txt 失败，本次 generation 未持久化: {ex.Message}";
        }
        // 写盘失败的日志在锁外发（回调会 Dispatcher.Invoke，持锁等 UI 线程有死锁风险）
        if (errorLog != null)
        {
            var captured = errorLog;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => _log(captured));
        }
    }
}
