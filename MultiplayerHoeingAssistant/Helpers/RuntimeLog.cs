using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 运行日志写入助手：按运行日志约定写一行到 exe 目录 log/assistant_runtime.&lt;date&gt;.s&lt;sessionId&gt;.log。
/// 供 HangWatchdog 等需要留痕的后台组件复用。
/// （原 30 秒一行的 [MEM] 内存遥测已于 2026-09-06 移除——日志噪音大于排查价值，
/// 内存膨胀问题改用 HangWatchdog 的 hang dump + 本日志的关键事件行定位。）
/// </summary>
internal static class RuntimeLog
{
    private static readonly object WriteLock = new();

    internal static bool IsCritical(Exception error) => error is OutOfMemoryException or AccessViolationException
        || (error is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(IsCritical)
            : error.InnerException is { } inner && IsCritical(inner));

    internal static string FormatException(string origin, object? error, bool isTerminating, string outcome)
    {
        var assembly = typeof(RuntimeLog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        var revision = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SourceRevisionId")?.Value ?? "unknown";
        var thread = Thread.CurrentThread;
        return $"[异常记录] 来源={origin}; IsTerminating={isTerminating}; PID={Environment.ProcessId}; "
            + $"Session={Process.GetCurrentProcess().SessionId}; Thread={thread.ManagedThreadId}; "
            + $"ThreadName={thread.Name ?? "<unnamed>"}; Apartment={thread.GetApartmentState()}; Version={version}; "
            + $"Revision={revision}; Runtime={Environment.Version}; Process={Environment.ProcessPath}\n"
            + $"处理结果={outcome}\n{error}";
    }

    internal static void WriteException(string origin, object? error, bool isTerminating, string outcome) =>
        WriteLine(FormatException(origin, error, isTerminating, outcome));

    /// <summary>按运行日志约定写一行（exe 目录 log/assistant_runtime.&lt;date&gt;.s&lt;sessionId&gt;.log）。</summary>
    internal static void WriteLine(string message)
    {
        var now = DateTime.Now;
        var name = $"assistant_runtime.{now:yyyy-MM-dd}.s{Process.GetCurrentProcess().SessionId}.log";
        var primary = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log", name);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusBGI", "log", $"emergency.p{Environment.ProcessId}.{name}");
        WriteRecord(primary, fallback, $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
    }

    // 同进程多线程串行写入；其他写者占用主文件时写独立备用文件，不能丢失原异常。
    internal static void WriteRecord(string primary, string fallback, string record)
    {
        lock (WriteLock)
        {
            try { Append(primary, record); }
            catch (Exception error) when (!IsCritical(error))
            {
                var backupRecord = $"[主日志写入失败] path={primary}\n{error}\n{record}";
                try { Append(fallback, backupRecord); }
                catch (Exception backupError) when (!IsCritical(backupError))
                {
                    // 磁盘完全不可用时仍尝试标准错误输出；绝不弹窗或递归写日志。
                    try { Console.Error.WriteLine($"[备用日志写入失败] path={fallback}\n{backupError}\n{backupRecord}"); }
                    catch (Exception consoleError) when (!IsCritical(consoleError)) { }
                }
            }
        }
    }

    private static void Append(string path, string record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(record);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
