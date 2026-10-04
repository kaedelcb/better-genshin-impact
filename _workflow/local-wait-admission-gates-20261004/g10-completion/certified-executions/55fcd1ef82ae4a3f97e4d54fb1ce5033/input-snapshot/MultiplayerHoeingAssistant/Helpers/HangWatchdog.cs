using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// UI 挂起自诊断看门狗：UI 线程每秒打一次心跳，后台线程发现心跳停顿超过 10 秒
/// （界面卡死、但日志无任何异常的那类问题）时，自动把进程转储写到
/// exe 目录 log/hang_yyyyMMdd_HHmmss.dmp（每会话最多一次），并在运行日志里记录停顿时长。
/// 只写 MiniDumpNormal+ThreadInfo 的小转储（几 MB）：挂起分析要的是 UI 线程调用栈；
/// 血泪教训：FullMemory 对内存已膨胀的进程会写出 25GB+ 文件，且在内存压力下易截断
/// （实测缺 system info 流，dotnet-dump/ClrMD 均无法解析），小转储总能快速写完。
/// </summary>
internal static class HangWatchdog
{
    private static long _uiHeartbeatTick = Environment.TickCount64;
    private static int _dumped;

    /// <summary>启动看门狗（应用启动时调用一次，须在 UI 线程）。</summary>
    public static void Start()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Volatile.Write(ref _uiHeartbeatTick, Environment.TickCount64);
        timer.Start();
        var thread = new Thread(WatchLoop) { IsBackground = true, Name = "HangWatchdog" };
        thread.Start();
    }

    private static void WatchLoop()
    {
        while (true)
        {
            Thread.Sleep(5000);
            var stalledMs = Environment.TickCount64 - Volatile.Read(ref _uiHeartbeatTick);
            if (stalledMs > 10_000 && Interlocked.Exchange(ref _dumped, 1) == 0)
                TryDump(stalledMs);
        }
    }

    private static void TryDump(long stalledMs)
    {
        try
        {
            var logDir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            Directory.CreateDirectory(logDir);
            var dumpPath = Path.Combine(logDir, $"hang_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");
            RuntimeLog.WriteLine($"[HANG_WATCHDOG] UI 停顿 {stalledMs}ms，已写转储 {dumpPath}");
            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var process = Process.GetCurrentProcess();
            MiniDumpWriteDump(process.Handle, (uint)process.Id, fs.SafeFileHandle.DangerousGetHandle(),
                MiniDumpType.MiniDumpNormal | MiniDumpType.MiniDumpWithThreadInfo,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 转储失败不影响主流程
        }
    }

    [Flags]
    private enum MiniDumpType : uint
    {
        MiniDumpNormal = 0x00000000,
        MiniDumpWithThreadInfo = 0x00001000,
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile,
        MiniDumpType dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
