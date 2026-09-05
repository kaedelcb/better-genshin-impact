using System.Diagnostics;
using System.IO;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 运行日志写入助手：按运行日志约定写一行到 exe 目录 log/assistant_runtime.&lt;date&gt;.s&lt;sessionId&gt;.log。
/// 供 HangWatchdog 等需要留痕的后台组件复用。
/// （原 30 秒一行的 [MEM] 内存遥测已于 2026-09-06 移除——日志噪音大于排查价值，
/// 内存膨胀问题改用 HangWatchdog 的 hang dump + 本日志的关键事件行定位。）
/// </summary>
internal static class RuntimeLog
{
    /// <summary>按运行日志约定写一行（exe 目录 log/assistant_runtime.&lt;date&gt;.s&lt;sessionId&gt;.log）。</summary>
    internal static void WriteLine(string message)
    {
        try
        {
            var logDir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{Process.GetCurrentProcess().SessionId}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
