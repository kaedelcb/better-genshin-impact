namespace BetterGenshinImpact.Core.Script;

/// <summary>
/// 配置组内脚本任务「当前执行线路」与「任务进度」跟踪器（只读内存快照，供 IPC <c>task.status</c> 的
/// <c>currentScriptRouteName</c> / <c>scriptTaskProgress</c> 字段与 ext 事件观察器读取）。
///
/// 背景：配置组里的 JS 脚本任务，任务名恒为该脚本自身（如「锄地一条龙」「AAA-Artifacts-Bulk-Supply」），
/// 脚本内部逐条执行地图追踪路线，BGI 侧原先只有 AutoHoeingProgress（联机锄地原生任务专用）
/// 才知道路线名 —— 于是联机助手成员卡片的「配置组 · 任务名」与嘟嘟可成员状态墙的「路线」行
/// 在 JS 任务期间恒为空。本类补上这一层：JS 通过 <c>pathingScript.runFile</c> / <c>runFileFromUser</c>
/// 执行路线时记下线路文件名，由 ScriptService 在项目边界清空。
///
/// 进度文本的三个来源（优先级从高到低）：
/// 1. 脚本显式上报 <c>dispatcher.SetTaskProgress</c>；
/// 2. 宿主侧 <see cref="ScriptTaskProgressAdapter"/> 按项目生命周期与 pathingScript 调用合成；
/// 3. 脚本既有日志文本的只读观察（<see cref="BetterGenshinImpact.Helpers.ScriptTaskProgressLogSink"/> +
///    <see cref="ScriptTaskProgressLogParser"/>），用于补齐脚本没有显式上报的计数
///    （例如采集cd管理的「第 28/119 个 · 路径组3 特产」）。
/// 合成规则：显式存在 → `适配器文本 · 脚本状态 显式文本`（完全忽略观察值，行为与引入观察前一致）；
/// 显式不存在 → `适配器文本 · 日志进度 观察文本`；单方存在则单方返回；都没有则 null。
///
/// 生命周期（与配置组项目边界对齐，防残留）：
/// - <see cref="BetterGenshinImpact.Service.ScriptService"/> 的 ExecuteProject 开始/结束（finally）→ <see cref="Clear"/>；
/// - AutoPathingScript.RunFile/RunFileFromUser 每条路线开始时覆盖；
/// - 单条路线结束**不**清空：保留"最近执行的线路"，否则 10s 一拍的助手状态轮询
///   很容易恰好落在两条路线的间隙里，显示恒为空。
///
/// 与 AutoHoeingProgress 的分工：后者是联机锄地原生任务（含第X/Y条线路与预计用时），
/// 本类是配置组内脚本任务的具体线路名，两者互不覆盖，助手端各占一个显示位。
///
/// 锁约定：本类 Sync 内允许调用 <see cref="ScriptTaskProgressAdapter"/>（顺序固定为 Sync → 适配器锁）；
/// 适配器不会回调本类，故不存在反向嵌套。
/// </summary>
public static class ScriptRouteProgress
{
    private static readonly object Sync = new();

    private static string? _currentRouteName;

    /// <summary>脚本显式上报的进度文本（如「第 1 组第 3/20 条: xxx.json」）。</summary>
    private static string? _progressText;

    /// <summary>脚本既有日志中观察到的进度（如「第 28/119 个 · 路径组3 特产」）；显式上报存在时不参与展示。</summary>
    private static string? _observedProgressText;

    /// <summary>当前运行的配置组脚本目录名（决定日志观察使用哪套规则）；项目结束为 null。</summary>
    private static string? _activeProjectFolderName;

    /// <summary>项目代次：项目开始/结束时自增，用于拒绝跨项目的迟到观察结果。</summary>
    private static long _projectGeneration;

    /// <summary>
    /// 观察代次：任何"清空观察槽"的动作（项目开始/结束、显式进度写入）都自增，
    /// 用于拒绝"解析途中被清空后又回写"的迟到结果（同目录重跑、显式清空场景）。
    /// </summary>
    private static long _observationEpoch;

    /// <summary>观察请求的单调序号（Interlocked 分配），用于拒绝同一代次内"旧日志晚回写"。</summary>
    private static long _nextObservationSequence;

    /// <summary>已提交观察结果的序号。</summary>
    private static long _committedObservationSequence;

    /// <summary>当前（最近）正在执行的线路文件名；无脚本任务在跑或项目已结束为 null。</summary>
    public static string? CurrentRouteName
    {
        get
        {
            lock (Sync)
            {
                return _currentRouteName;
            }
        }
    }

    /// <summary>脚本任务进度文本；无显式上报、无日志观察且无可合成内容时为 null。</summary>
    public static string? ProgressText
    {
        get
        {
            lock (Sync)
            {
                var adaptedText = ScriptTaskProgressAdapter.GetProgressText();
                return ComposeProgressText(adaptedText, _progressText, _observedProgressText);
            }
        }
    }

    /// <summary>
    /// 通知宿主侧适配器开始执行一个 JS 项目；不会修改或注入脚本源码。
    /// 同时清理上一轮的线路名与进度，使本方法自身即构成完整项目边界（避免依赖调用顺序）。
    /// </summary>
    public static void BeginProject(string? folderName, object? settings)
    {
        lock (Sync)
        {
            _currentRouteName = null;
            _progressText = null;
            _observedProgressText = null;
            _committedObservationSequence = 0;
            _activeProjectFolderName = string.IsNullOrWhiteSpace(folderName) ? null : folderName.Trim();
            _projectGeneration++;
            _observationEpoch++;
            ScriptTaskProgressAdapter.BeginProject(folderName, settings);
        }
    }

    /// <summary>记录当前执行的线路名（空值等价于 <see cref="Clear"/>）。</summary>
    public static void SetCurrentRoute(string? routeName)
    {
        var name = string.IsNullOrWhiteSpace(routeName) ? null : routeName.Trim();
        lock (Sync)
        {
            _currentRouteName = name;
        }
    }

    /// <summary>记录路线开始，并驱动宿主侧脚本进度适配器。</summary>
    public static void StartRoute(string? path)
    {
        lock (Sync)
        {
            _currentRouteName = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFileName(path);
            ScriptTaskProgressAdapter.RouteStarted(path);
        }
    }

    /// <summary>记录路线执行返回；线路名继续保留到项目结束。</summary>
    public static void CompleteRoute(string? path)
    {
        lock (Sync)
        {
            ScriptTaskProgressAdapter.RouteCompleted(path);
        }
    }

    /// <summary>
    /// 记录脚本显式上报的进度文本（空值清除）。每次写入都会丢弃当前日志观察值（观察代次自增），
    /// 避免显式清空后把更早的日志进度"复活"出来。
    /// </summary>
    public static void SetProgressText(string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        lock (Sync)
        {
            _progressText = value;
            _observedProgressText = null;
            _committedObservationSequence = 0;
            _observationEpoch++;
        }
    }

    /// <summary>
    /// 只读观察一行脚本日志（由 <see cref="BetterGenshinImpact.Helpers.ScriptTaskProgressLogSink"/> 调用）：
    /// 按当前项目目录解析，仅在项目代次与观察代次都未变、时间序号最新且没有显式上报时写入观察槽。
    /// 解析失败不改变任何状态——宁可没有信息，也不能显示错误进度。
    /// </summary>
    internal static void ObserveLogMessage(string? renderedMessage)
    {
        if (string.IsNullOrWhiteSpace(renderedMessage))
        {
            return;
        }

        var sequence = System.Threading.Interlocked.Increment(ref _nextObservationSequence);

        string folder;
        long generation;
        long epoch;
        lock (Sync)
        {
            if (_activeProjectFolderName == null || _progressText != null)
            {
                return;
            }

            folder = _activeProjectFolderName;
            generation = _projectGeneration;
            epoch = _observationEpoch;
        }

        var observed = ScriptTaskProgressLogParser.TryParse(folder, renderedMessage);
        if (observed == null)
        {
            return;
        }

        lock (Sync)
        {
            if (_activeProjectFolderName == null
                || !string.Equals(folder, _activeProjectFolderName, System.StringComparison.OrdinalIgnoreCase)
                || _progressText != null
                || generation != _projectGeneration
                || epoch != _observationEpoch
                || sequence <= _committedObservationSequence)
            {
                return;
            }

            _committedObservationSequence = sequence;
            _observedProgressText = observed;
        }
    }

    /// <summary>清空当前线路名与全部进度（配置组项目开始/结束、任务取消收尾时调用）。</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            _currentRouteName = null;
            _progressText = null;
            _observedProgressText = null;
            _activeProjectFolderName = null;
            _committedObservationSequence = 0;
            _projectGeneration++;
            _observationEpoch++;
            ScriptTaskProgressAdapter.Clear();
        }
    }

    private static string? ComposeProgressText(string? adaptedText, string? explicitText, string? observedText)
    {
        // 显式上报拥有绝对优先级：与引入日志观察之前的行为逐字一致。
        if (explicitText != null)
        {
            return adaptedText == null ? explicitText : $"{adaptedText} · 脚本状态 {explicitText}";
        }

        if (observedText != null)
        {
            return adaptedText == null ? $"日志进度 {observedText}" : $"{adaptedText} · 日志进度 {observedText}";
        }

        return adaptedText;
    }
}