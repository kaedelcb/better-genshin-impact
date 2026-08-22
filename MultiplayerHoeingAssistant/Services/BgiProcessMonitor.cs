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