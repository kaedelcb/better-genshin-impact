using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>线路更新的专用进程边界：退出码和本次写入结论都明确成功后才允许用于开队预判。</summary>
public static class AutoHoeingRouteUpdater
{
    public static Task RunAsync(string baseDirectory, CancellationToken ct)
    {
        var root = Path.GetFullPath(baseDirectory);
        var executable = Path.Combine(root, "Tools", "AutoHoeingUpdater", "AutoHoeingUpdater.exe");
        var info = BuildStartInfo(executable, root);
        return ExecuteAndVerifyAsync(info, Path.Combine(Path.GetDirectoryName(executable)!, "AutoHoeingUpdater.log"),
            TimeSpan.FromSeconds(120), ct);
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string targetRoot)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = targetRoot, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("--silent");
        info.ArgumentList.Add("--target");
        info.ArgumentList.Add(targetRoot);
        info.ArgumentList.Add("--force-download");
        return info;
    }

    internal static async Task ExecuteAndVerifyAsync(ProcessStartInfo info, string logPath, TimeSpan timeout,
        CancellationToken ct, Action<int>? started = null)
    {
        ct.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var before = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        if (!process.Start()) throw new IOException("线路更新器未启动");
        // 同时排空两条管道，避免输出填满使更新器永远无法退出。
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            started?.Invoke(process.Id);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            await StopOwnedProcessAsync(process);
            // 确认退出后等待重定向管道关闭，不能留下后台读任务。
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("线路更新器执行超时，已终止更新进程");
            throw;
        }
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException($"线路更新器退出码为 {process.ExitCode}，不能确认更新成功");
        if (!File.Exists(logPath)) throw new InvalidDataException("线路更新器没有产生本次更新日志");
        using var log = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (log.Length <= before || log.Length - before > 4 * 1024 * 1024)
            throw new InvalidDataException("本次线路更新日志缺失、截断或超出可靠读取范围");
        log.Position = before;
        using var reader = new StreamReader(log, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var appended = await reader.ReadToEndAsync(ct);
        RequireSuccessfulWriteSummary(appended);
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            throw new RouteUpdateProcessCleanupException("线路更新器退出未确认，禁止同时继续读取或执行线路", ex);
        }
    }

    internal static void RequireSuccessfulWriteSummary(string log)
    {
        // 当前随包更新器 RunUpdate 会吞逐文件写入异常，甚至最终返回 0；只能读取本次追加的详细总结。
        // 同一日志里出现多个启动/总结意味着并发更新或不完整归属，本次预判不得据此跳过世界。
        var starts = Regex.Matches(log, "静默更新启动。", RegexOptions.CultureInvariant);
        var ends = Regex.Matches(log, @"静默更新结束，成功\s+(\d+)\s+个，失败\s+(\d+)\s+个。", RegexOptions.CultureInvariant);
        var writes = Regex.Matches(log, @"写入完成，成功\s+(\d+)\s+个文件，失败\s+(\d+)\s+个，跳过\s+(\d+)\s+个。", RegexOptions.CultureInvariant);
        if (starts.Count != 1 || ends.Count != 1 || starts[0].Index >= ends[0].Index
            || !int.TryParse(ends[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var targets)
            || targets <= 0 || ends[0].Groups[2].Value != "0" || writes.Count != targets
            || writes.Cast<Match>().Any(m => m.Index < starts[0].Index || m.Index > ends[0].Index
                || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                || count <= 0 || m.Groups[2].Value != "0")
            || log.Contains("写入失败：", StringComparison.Ordinal) || log.Contains("跳过不安全条目：", StringComparison.Ordinal))
            throw new InvalidDataException("更新器未确认所有目标文件写入成功，本次线路状态保持未知");
    }
}

public sealed class RouteUpdateProcessCleanupException(string message, Exception inner) : IOException(message, inner);
