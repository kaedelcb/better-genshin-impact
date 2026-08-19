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

    public bool IsBgiRunning => Process.GetProcessesByName("BetterGI").Length > 0;

    public BgiProcessMonitor(string bgiPath)
    {
        _bgiPath = bgiPath;
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

        var running = Process.GetProcessesByName("BetterGI").Length > 0;
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
        foreach (var proc in Process.GetProcessesByName("BetterGI"))
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