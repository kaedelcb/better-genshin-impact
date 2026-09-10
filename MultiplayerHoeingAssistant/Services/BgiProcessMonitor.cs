using System.Diagnostics;

using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.Services;

public class BgiProcessMonitor : IDisposable
{
    private readonly string _bgiPath;
    /// <summary>[P2 仲裁] 用户可见日志出口（MainViewModel 注入 AddLog）；null 时静默（仅 Debug.WriteLine）。</summary>
    private readonly Action<string>? _log;
    private Timer? _checkTimer;
    private bool _isRunning;
    /// <summary>边沿检测标记：上一次轮询时 BGI 是否在运行（P1-E）。</summary>
    private bool _wasRunning;
    /// <summary>[P2 仲裁] 杀/启操作串行化信号量：回退重启/崩溃守护/kill_bgi 节点三个并发写入者全部经此收编，
    /// 杜绝"有意杀进程的 2s 真空被守护误判崩溃、再拉一个实例抢单实例管道"的双实例事故。</summary>
    private readonly SemaphoreSlim _processGate = new(1, 1);
    /// <summary>[P2 仲裁] 有意杀死抑制窗口（UTC）：仲裁器杀/启进行中（含 spawn 真空余量）时，
    /// CheckBgiStatus 跳过"运行→消失"崩溃判定，避免把有意杀死误判为崩溃。</summary>
    private DateTime _intentionalKillUntilUtc = DateTime.MinValue;
    /// <summary>[P2 仲裁] 上次崩溃自动重启时间（UTC）：30s 冷却，防快速震荡循环（反复崩→反复拉）。</summary>
    private DateTime _lastCrashRestartUtc = DateTime.MinValue;
    /// <summary>[P2 仲裁] 崩溃自动重启冷却时长。</summary>
    private static readonly TimeSpan CrashRestartCooldown = TimeSpan.FromSeconds(30);

    public event Action? OnBgiCrashed;
    public event Action? OnBgiStarted;

    public bool IsBgiRunning => GetCurrentSessionBgiProcesses().Length > 0;

    /// <param name="log">[P2 仲裁] 可选日志回调（MainViewModel.AddLog），仲裁器关键决策打可见日志。</param>
    public BgiProcessMonitor(string bgiPath, Action<string>? log = null)
    {
        _bgiPath = bgiPath;
        _log = log;
    }

    /// <summary>
    /// 获取「当前 Windows 会话」内的 BetterGI 进程。
    /// 注意：必须按 SessionId 过滤，否则多用户会话下会把别桌面的 BetterGI 也算进来，
    /// 导致"本会话 BGI 已被杀却仍显示已启动"，且 KillBgi 会误杀别会话的进程。
    /// </summary>
    internal static Process[] GetCurrentSessionBgiProcesses()
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        return Process.GetProcessesByName("BetterGI")
            .Where(p => p.SessionId == currentSession)
            .ToArray();
    }

    /// <summary>只读枚举本机所有会话的 BGI 实例，供启动中心状态条件使用；不用于控制、杀进程或重启。</summary>
    public static Process[] GetAllSessionBgiProcesses() =>
        Process.GetProcessesByName("BetterGI");

    private static readonly StartupSessionBindings StatusBindings = new();

    internal static int ResolveStatusSession(string source, int order, string userName)
    {
        if (source == Models.StartupStatusSource.CurrentSession)
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        if (source == Models.StartupStatusSource.UserName)
        {
            var matches = WindowsSessionIdentity.GetSessionIds().Where(id =>
                WindowsSessionIdentity.Matches(WindowsSessionIdentity.GetUserName(id), userName)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("用户名未匹配到唯一登录会话，状态未知");
            return matches[0];
        }
        if (source != Models.StartupStatusSource.StartupOrder)
            throw new InvalidOperationException("未知状态来源");
        var processes = GetAllSessionBgiProcesses();
        try
        {
            var ordered = processes.OrderBy(p => p.StartTime.ToUniversalTime()).ThenBy(p => p.Id)
                .Select(p => new SessionLifetime(p.SessionId, WindowsSessionIdentity.GetLogonTime(p.SessionId)))
                .Distinct().ToArray();
            var bound = StatusBindings.Resolve(order, ordered);
            if (WindowsSessionIdentity.GetLogonTime(bound.SessionId) != bound.LogonTime)
                throw new InvalidOperationException("原目标会话已注销，拒绝绑定到复用的会话编号；重启助手可重新绑定");
            return bound.SessionId;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static Process[] SelectBgiProcesses(string source, int order, string userName)
    {
        var session = ResolveStatusSession(source, order, userName);
        var all = GetAllSessionBgiProcesses();
        Process[] selected = [];
        try
        {
            var matches = all.Where(p => p.SessionId == session).ToArray();
            if (source != Models.StartupStatusSource.CurrentSession && matches.Length > 1)
                throw new InvalidOperationException("目标会话存在多个 BGI 实例，状态未知");
            selected = matches;
            return selected;
        }
        finally
        {
            foreach (var process in all)
                if (!selected.Contains(process)) process.Dispose();
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        // 守护启动即视为已武装：若此时 BGI 未运行，首轮轮询会触发一次崩溃事件将其拉起。
        // 否则 _wasRunning 初值为 false，必须先手动开过一次 BGI 才能形成"运行→消失"边沿，
        // 导致"先开助手、BGI 未运行"场景下守护永远不生效。
        _wasRunning = true;
        _checkTimer = new Timer(CheckBgiStatus, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _isRunning = false;
        _checkTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    private void CheckBgiStatus(object? state)
    {
        if (!_isRunning) return;

        var running = GetCurrentSessionBgiProcesses().Length > 0;
        if (running)
        {
            // 进程（重）出现后重新武装边沿检测
            _wasRunning = true;
            return;
        }
        // [P2 仲裁] 有意杀死抑制窗口内（仲裁器杀/启进行中或启动后 spawn 真空余量期），
        // "运行→消失"是预期行为，跳过崩溃判定（否则守护会把回退重启的 2s 真空误判为崩溃，
        // 再拉一个无参实例与带参实例抢单实例管道，败方参数被静默丢弃）
        if (DateTime.UtcNow < _intentionalKillUntilUtc)
        {
            return;
        }
        // [P1-E 止血] 仅在"运行 → 消失"跳变时触发一次崩溃事件；
        // 触发后进入"等待重启"状态（_wasRunning=false），进程重新出现前不再重复触发，
        // 避免 BGI 启动慢（>5s）时每 5s 轮询重复 RestartBgi 导致双开/多开 BGI。
        if (_wasRunning)
        {
            // [P2 仲裁] 崩溃重启冷却：距上次崩溃重启 <30s 跳过并记日志，防"崩溃→拉起→又崩"快速震荡循环。
            // 注意：冷却跳过时不卸除边沿武装（_wasRunning 保持 true）——若先卸除再跳过，
            // 进程永远不会自己重新出现来重新武装，守护将永久失活（本次"消失"既没拉起也不再重判）。
            // 保持武装后冷却期每轮轮询都会重判，冷却一结束即延迟拉起。
            var sinceLast = DateTime.UtcNow - _lastCrashRestartUtc;
            if (sinceLast < CrashRestartCooldown)
            {
                _log?.Invoke($"[崩溃守护] 距上次崩溃处理仅 {sinceLast.TotalSeconds:F0}s（<{CrashRestartCooldown.TotalSeconds:F0}s 冷却），跳过本次自动重启，待冷却结束后重试");
                return;
            }
            _wasRunning = false;
            _lastCrashRestartUtc = DateTime.UtcNow;
            OnBgiCrashed?.Invoke();
        }
    }

    public void RestartBgi(string? args = null)
    {
        // [DUPLAUNCH_PROBE] 探针：记录每次 BGI 被启动的时间、参数、调用堆栈
        // 目的：确认远程一键锄地/上线人齐触发时，助手是否多次调用 RestartBgi 带 --startGroups
        try
        {
            // 日志写入助手程序目录 log/ 子目录，按日期 + Windows 会话 ID 分文件
            var logDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DUPLAUNCH_PROBE][BgiProcessMonitor.RestartBgi] 启动 BGI args={args}\n");
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DUPLAUNCH_PROBE][BgiProcessMonitor.RestartBgi] 堆栈:\n{Environment.StackTrace}\n");
        }
        catch
        {
            // 文件写入失败不影响主流程
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _bgiPath,
                Arguments = args ?? "",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(startInfo);
            OnBgiStarted?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"启动 BGI 失败: {ex.Message}");
        }
    }

    public void KillBgi()
    {
        foreach (var proc in GetCurrentSessionBgiProcesses())
        {
            try
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                // 提权运行的 BGI 会抛 AccessDenied，此处吞掉后进程仍存活——
                // 调用方必须经 WaitUntilNoBgiProcessAsync 确认真退净（见受控方法），不能默认杀成功
                System.Diagnostics.Debug.WriteLine($"终止 BGI 进程失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// [P2 仲裁] 受控重启（杀 + 带参拉起）：SemaphoreSlim 串行化全部杀/启操作（单一写入者）。
    /// 流程：进入即置有意杀死抑制（守护轮询窗口内跳过崩溃判定）→ KillBgi → 200ms 轮询等进程
    /// 真正退出（上限 10s）→ 仍有本会话 BGI 残留（提权杀不掉，AccessDenied 已被 KillBgi 吞掉）：
    /// 打可见日志并返回 false，不启动新实例（起了也会被 BGI 单实例转发丢弃参数，假成功更坏）；
    /// 退净则 RestartBgi(args)，启动后留 2s 抑制余量覆盖新进程 spawn 真空。
    /// </summary>
    /// <returns>true = 已杀净并完成拉起；false = 旧进程杀不掉，未启动新实例。</returns>
    public async Task<bool> RestartBgiControlledAsync(string? args, string reason)
    {
        await _processGate.WaitAsync();
        try
        {
            // 抑制窗口覆盖整个杀+等退出过程（最坏 10s），启动成功后再收窄为 2s 余量
            _intentionalKillUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            _log?.Invoke($"[进程仲裁] 受控重启开始（{reason}），args={args ?? "(无)"}");
            KillBgi();
            if (!await WaitUntilNoBgiProcessAsync(TimeSpan.FromSeconds(10)))
            {
                // 进程还活着：收窄抑制窗口，不该继续抑制崩溃判定 15s
                _intentionalKillUntilUtc = DateTime.UtcNow;
                _log?.Invoke($"[进程仲裁] 受控重启中止（{reason}）：10s 内 BGI 进程未退净（可能提权运行杀不掉），不启动新实例以避免双实例抢单实例管道");
                return false;
            }
            // 有意杀死 ≠ 崩溃：卸除边沿武装，防止抑制窗口结束后守护把本次杀死补判为崩溃
            _wasRunning = false;
            RestartBgi(args);
            // 启动后留 2s 抑制余量：覆盖新进程 spawn 到可被枚举到的真空期
            _intentionalKillUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            return true;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// [P2 仲裁] 受控终止（只杀不启）：与 RestartBgiControlledAsync 同一信号量串行 + 有意杀死抑制 + 等退出。
    /// 杀净后卸除边沿武装（_wasRunning=false），守护不会把有意杀死误判为崩溃再拉起。
    /// </summary>
    /// <returns>true = 已杀净；false = 10s 内仍有本会话 BGI 进程残留（提权杀不掉）。</returns>
    public async Task<bool> KillBgiControlledAsync(string reason)
    {
        await _processGate.WaitAsync();
        try
        {
            _intentionalKillUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            _log?.Invoke($"[进程仲裁] 受控终止开始（{reason}）");
            KillBgi();
            if (!await WaitUntilNoBgiProcessAsync(TimeSpan.FromSeconds(10)))
            {
                // 进程还活着：收窄抑制窗口，不该继续抑制崩溃判定 15s
                _intentionalKillUntilUtc = DateTime.UtcNow;
                _log?.Invoke($"[进程仲裁] 受控终止失败（{reason}）：10s 内 BGI 进程未退净（可能提权运行杀不掉）");
                return false;
            }
            _wasRunning = false; // 有意杀死 ≠ 崩溃：卸除边沿武装，守护不拉起
            _log?.Invoke($"[进程仲裁] 受控终止完成（{reason}）");
            return true;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>[P2 仲裁] 轮询等本会话 BGI 进程退净（200ms 间隔）。返回 true = 已退净。</summary>
    private static async Task<bool> WaitUntilNoBgiProcessAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetCurrentSessionBgiProcesses().Length == 0) return true;
            await Task.Delay(200);
        }
        return GetCurrentSessionBgiProcesses().Length == 0;
    }

    public void Dispose()
    {
        Stop();
        _processGate.Dispose();
    }
}