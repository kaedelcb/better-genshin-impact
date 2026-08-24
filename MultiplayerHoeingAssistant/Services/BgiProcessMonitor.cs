using System.Diagnostics;

using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.Services;

public class BgiProcessMonitor : IDisposable
{
    private readonly string _bgiPath;
    private Timer? _checkTimer;
    private bool _isRunning;

    public event Action? OnBgiCrashed;
    public event Action? OnBgiStarted;

    public bool IsBgiRunning => GetCurrentSessionBgiProcesses().Length > 0;

    public BgiProcessMonitor(string bgiPath)
    {
        _bgiPath = bgiPath;
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

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
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
        if (!running)
        {
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
                System.Diagnostics.Debug.WriteLine($"终止 BGI 进程失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}