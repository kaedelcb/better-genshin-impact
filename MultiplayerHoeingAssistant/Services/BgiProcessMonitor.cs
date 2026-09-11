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
    /// <summary>[常开守护] 守护豁免标志：助手自己有意关闭 BGI（stop 命令 / 启动中心 kill_bgi 节点）后置位，
    /// 守护不再把它拉回来——这是"不在就拉"的唯一例外。
    /// 解除条件有两个且都必须存在，防止它变成第二个"永久失忆点"：①任何"要求 BGI 运行"的动作
    /// （RestartBgi，含回退重启/启动中心启动）；②轮询观察到进程存在（用户自己又把它开起来了）。
    /// 跨线程：仲裁器线程写、守护轮询线程读 → volatile。</summary>
    private volatile bool _guardSuspended;
    /// <summary>[P2 仲裁] 杀/启操作串行化信号量：回退重启/崩溃守护/kill_bgi 节点三个并发写入者全部经此收编，
    /// 杜绝"有意杀进程的 2s 真空被守护误判崩溃、再拉一个实例抢单实例管道"的双实例事故。</summary>
    private readonly SemaphoreSlim _processGate = new(1, 1);
    /// <summary>[常开守护] 守护让路截止时间（UTC）：仲裁器杀/启在途、或我方刚发起启动（冷启动宽限期）时，
    /// 守护跳过判定——避免把"杀进程真空"和"新进程还没被枚举到"误判成又一次崩溃而重复拉起。</summary>
    private DateTime _guardSuppressUntilUtc = DateTime.MinValue;
    /// <summary>[常开守护] 我方启动后的冷启动宽限期：覆盖新进程 spawn 到可被枚举到的真空期
    /// （旧实现只给 2s，而轮询周期是 5s，新进程极易"从未被观测到"）。</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);
    /// <summary>[P2 仲裁] 上次崩溃自动重启时间（UTC）：30s 冷却，防快速震荡循环（反复崩→反复拉）。</summary>
    private DateTime _lastCrashRestartUtc = DateTime.MinValue;
    /// <summary>[P2 仲裁] 崩溃自动重启冷却时长。</summary>
    private static readonly TimeSpan CrashRestartCooldown = TimeSpan.FromSeconds(30);
    /// <summary>[常开守护] 冷却跳过日志去重：同一冷却窗只打一行（旧实现每 5s 一条，实机日志噪音大）。</summary>
    private bool _cooldownSkipLogged;

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
    /// [稳健性] 进程可能在"枚举"与"读 SessionId"之间退出，此时 SessionId 抛
    /// InvalidOperationException——旧实现让该异常直接冒泡到守护轮询线程/UI 绑定线程
    /// （定时器回调里的未处理异常会打到全局处理器，托盘场景下等于无声卡住）。
    /// 这里统一按"不存在"处理，并释放被丢弃的句柄。
    /// </summary>
    internal static Process[] GetCurrentSessionBgiProcesses()
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        var matched = new List<Process>();
        foreach (var process in Process.GetProcessesByName("BetterGI"))
        {
            try
            {
                if (process.SessionId == currentSession)
                {
                    matched.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程在枚举之后已退出：按"不存在"处理
            }

            process.Dispose();
        }

        return matched.ToArray();
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
        // [常开守护] 判定已改为电平式（"本会话没有 BGI 进程就拉起"），不再依赖"必须先见过一次运行"的边沿武装，
        // 因此不存在旧实现的失活态：旧版重启后会把武装标记清掉，若新进程没被 5 秒轮询抓到就永久静默、
        // 再也不拉起（实机"手动关掉 BGI 后助手卡在 IPC 不可用、BGI 不再重启"的根因）。
        // 启动即生效：此时若 BGI 未运行，首轮轮询就会拉起它（语义与旧版 _wasRunning=true 一致）。
        _checkTimer = new Timer(CheckBgiStatus, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _isRunning = false;
        _checkTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    /// <summary>
    /// [常开守护] 电平式判定：本会话只要没有 BGI 进程就拉起——不管它是崩溃、闪退还是被手动关掉，
    /// 也不管有没有任务在做（用户决策 2026-09-11："不管怎么样都拉起"）。
    /// 两个让路条件：①_guardSuspended（助手自己有意关闭，唯一豁免）；②让路截止时间未到（仲裁器杀/启在途、
    /// 或我方刚发起启动的冷启动宽限期）。冷却 30s 防止"崩→拉→又崩"的快速震荡。
    /// 注意：判定不再使用"运行→消失"边沿——边沿一旦丢掉（重启后新进程未被轮询抓到）守护就永久静默，
    /// 这正是实机"BGI 再也不重启"的根因；电平式判定没有可丢失的记忆。
    /// </summary>
    private void CheckBgiStatus(object? state)
    {
        if (!_isRunning) return;

        try
        {
            if (GetCurrentSessionBgiProcesses().Length > 0)
            {
                // 进程在：此前若因"助手有意关闭"而豁免，说明它又被开起来了（用户手动开 / 外部拉起）→ 守护重新接手
                if (_guardSuspended)
                {
                    _guardSuspended = false;
                    _log?.Invoke("[崩溃守护] 检测到 BGI 进程存在，守护恢复（解除有意关闭豁免）");
                }

                _cooldownSkipLogged = false;
                return;
            }

            // 以下均为"本会话没有 BGI 进程"
            if (_guardSuspended)
            {
                return; // 唯一豁免：助手自己让它关的（stop 命令 / 启动中心 kill_bgi 节点），不拉回
            }

            if (DateTime.UtcNow < _guardSuppressUntilUtc)
            {
                return; // 仲裁器杀/启在途或我方刚启动，处于冷启动宽限期：让路，避免重复拉起双实例
            }

            var sinceLast = DateTime.UtcNow - _lastCrashRestartUtc;
            if (sinceLast < CrashRestartCooldown)
            {
                // 同一冷却窗只打一行：旧实现每 5s 一条，实机日志被刷屏
                if (!_cooldownSkipLogged)
                {
                    _cooldownSkipLogged = true;
                    _log?.Invoke(
                        $"[崩溃守护] 距上次重启处理仅 {sinceLast.TotalSeconds:F0}s（<{CrashRestartCooldown.TotalSeconds:F0}s 冷却），等待冷却结束后重试拉起");
                }

                return;
            }

            _cooldownSkipLogged = false;
            _lastCrashRestartUtc = DateTime.UtcNow;
            _log?.Invoke("[崩溃守护] 未发现 BGI 进程，触发自动重启");
            OnBgiCrashed?.Invoke();
        }
        catch (Exception ex)
        {
            // 巡检异常绝不冒泡：定时器回调里的未处理异常会进全局处理器（App 弹"未处理异常"框），
            // 托盘运行场景下那等于无声卡住。记一行、守护继续跑下一轮。
            _log?.Invoke($"[崩溃守护] 巡检异常（已忽略，守护继续）：{ex.Message}");
        }
    }

    /// <summary>
    /// [常开守护] 启动 BGI（纯启动，不杀进程）。
    /// 返回 false = 启动调用失败（异常已写可见日志，不再静默吞掉——旧实现吞掉异常后调用方仍打
    /// "BGI 已自动重启"，是"假成功"）。
    /// 副作用（有意为之）：任何"要求 BGI 运行"的动作都会①解除"有意关闭"豁免、②给出冷启动宽限期，
    /// 让守护在宽限期内不重复拉起同一个实例。
    /// </summary>
    public bool RestartBgi(string? args = null)
    {
        _guardSuspended = false;
        _guardSuppressUntilUtc = DateTime.UtcNow + StartupGrace;

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
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"启动 BGI 失败: {ex.Message}");
            _log?.Invoke($"[崩溃守护] 启动 BGI 失败：{ex.Message}（将在冷却结束后自动重试；请检查 BGI 路径与权限）");
            return false;
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
    /// 流程：进入即置守护让路截止时间（守护轮询窗口内跳过判定）→ KillBgi → 200ms 轮询等进程
    /// 真正退出（上限 10s）→ 仍有本会话 BGI 残留（提权杀不掉，AccessDenied 已被 KillBgi 吞掉）：
    /// 打可见日志并返回 false，不启动新实例（起了也会被 BGI 单实例转发丢弃参数，假成功更坏）；
    /// 退净则 RestartBgi(args)（内部再给 20s 冷启动宽限期）。
    /// [常开守护] 有意重启 ≠ 结束守护：本方法**不再**清除守护武装（旧实现清掉后若新进程没被轮询抓到，
    /// 守护会永久静默）。因此这里不置 _guardSuspended——那是"有意关闭"的语义，只归 KillBgiControlledAsync。
    /// </summary>
    /// <returns>true = 已杀净并完成拉起；false = 旧进程杀不掉（未启动新实例）或启动调用失败。</returns>
    public async Task<bool> RestartBgiControlledAsync(string? args, string reason)
    {
        await _processGate.WaitAsync();
        try
        {
            // 让路窗口覆盖整个杀+等退出过程（最坏 10s）
            _guardSuppressUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            _log?.Invoke($"[进程仲裁] 受控重启开始（{reason}），args={args ?? "(无)"}");
            KillBgi();
            if (!await WaitUntilNoBgiProcessAsync(TimeSpan.FromSeconds(10)))
            {
                // 进程还活着：收窄让路窗口，不该继续让守护沉默 15s
                _guardSuppressUntilUtc = DateTime.UtcNow;
                _log?.Invoke($"[进程仲裁] 受控重启中止（{reason}）：10s 内 BGI 进程未退净（可能提权运行杀不掉），不启动新实例以避免双实例抢单实例管道");
                return false;
            }

            // RestartBgi 内部会解除"有意关闭"豁免并给出 20s 冷启动宽限期
            var started = RestartBgi(args);
            if (!started)
            {
                _log?.Invoke($"[进程仲裁] 受控重启（{reason}）：进程已杀净但启动调用失败，守护将在冷却结束后自动重试");
            }

            return started;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <summary>
    /// [P2 仲裁] 受控终止（只杀不启）：与 RestartBgiControlledAsync 同一信号量串行 + 守护让路 + 等退出。
    /// 杀净后置 _guardSuspended（"助手自己有意关闭"）：守护不会把它误判为崩溃再拉起——
    /// 这是"不在就拉"的唯一豁免，且可被下一次启动动作或"观察到进程又存在"自动解除。
    /// </summary>
    /// <returns>true = 已杀净；false = 10s 内仍有本会话 BGI 进程残留（提权杀不掉）。</returns>
    public async Task<bool> KillBgiControlledAsync(string reason)
    {
        await _processGate.WaitAsync();
        try
        {
            _guardSuppressUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            _log?.Invoke($"[进程仲裁] 受控终止开始（{reason}）");
            KillBgi();
            if (!await WaitUntilNoBgiProcessAsync(TimeSpan.FromSeconds(10)))
            {
                // 进程还活着：收窄让路窗口，不该继续让守护沉默 15s
                _guardSuppressUntilUtc = DateTime.UtcNow;
                _log?.Invoke($"[进程仲裁] 受控终止失败（{reason}）：10s 内 BGI 进程未退净（可能提权运行杀不掉）");
                return false;
            }

            _guardSuspended = true; // 有意关闭 ≠ 崩溃：置豁免，守护不拉起
            _log?.Invoke($"[进程仲裁] 受控终止完成（{reason}）：守护已暂停（本次为助手有意关闭，BGI 不会被自动拉起）");
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