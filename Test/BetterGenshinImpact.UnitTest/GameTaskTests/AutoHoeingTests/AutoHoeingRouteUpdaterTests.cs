using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class AutoHoeingRouteUpdaterTests
{
    private const string GoodLog = "静默更新启动。\n写入完成，成功 3 个文件，失败 0 个，跳过 0 个。\n静默更新结束，成功 1 个，失败 0 个。\n";

    private sealed class ScriptProcess : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "BetterGI-UpdaterTests-" + Guid.NewGuid().ToString("N"));
        public string Log { get; }
        public int ProcessId { get; set; }
        public ScriptProcess()
        {
            Directory.CreateDirectory(_root);
            Log = Path.Combine(_root, "AutoHoeingUpdater.log");
        }
        public ProcessStartInfo Command(string script)
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(script);
            return info;
        }
        public string Append(string text)
            => "[IO.File]::AppendAllText('" + Log.Replace("'", "''") + "', '" + text.Replace("'", "''") + "', [Text.UTF8Encoding]::new($false)); ";
        public void AssertExited()
        {
            Assert.True(ProcessId > 0);
            try { using var process = Process.GetProcessById(ProcessId); Assert.True(process.HasExited); }
            catch (ArgumentException) { } // OS already reaped it.
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "BetterGI-UpdaterTests-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test output cleanup escaped its named directory");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public void DirectInvocationPreservesTargetPathAsOneArgument()
    {
        var info = AutoHoeingRouteUpdater.BuildStartInfo(@"C:\BGI folder\Tools\AutoHoeingUpdater.exe", @"C:\BGI folder");
        Assert.Equal(@"C:\BGI folder", info.WorkingDirectory);
        Assert.Equal(new[] { "--silent", "--target", @"C:\BGI folder", "--force-download" }, info.ArgumentList);
        Assert.True(info.CreateNoWindow);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public async Task ExitZeroAndFreshSuccessfulWriteSummaryAreRequiredForSuccess()
    {
        using var process = new ScriptProcess();
        // Old failure is not this invocation's outcome.
        File.WriteAllText(process.Log, "previous run failed\n", new UTF8Encoding(false));
        await AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(process.Command(process.Append(GoodLog)
            + "[Console]::Write('output'); [Console]::Error.Write('diagnostic'); exit 0"), process.Log,
            TimeSpan.FromSeconds(15), CancellationToken.None, pid => process.ProcessId = pid);
        process.AssertExited();
    }

    [Fact]
    public async Task NonzeroExitCannotBeReportedAsSuccessfulEvenWithGoodLog()
    {
        using var process = new ScriptProcess();
        var failure = await Assert.ThrowsAsync<IOException>(() => AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(
            process.Command(process.Append(GoodLog) + "exit 7"), process.Log, TimeSpan.FromSeconds(15),
            CancellationToken.None, pid => process.ProcessId = pid));
        Assert.Contains("7", failure.Message);
        process.AssertExited();
    }

    [Fact]
    public async Task ExitZeroStillRejectsSwallowedFileWriteFailureInUpdater()
    {
        using var process = new ScriptProcess();
        var partial = GoodLog.Replace("成功 3 个文件，失败 0 个", "成功 2 个文件，失败 1 个");
        await Assert.ThrowsAsync<InvalidDataException>(() => AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(
            process.Command(process.Append(partial) + "exit 0"), process.Log, TimeSpan.FromSeconds(15),
            CancellationToken.None, pid => process.ProcessId = pid));
        process.AssertExited();
    }

    [Fact]
    public async Task ExistingSuccessLogCannotStandInForThisRunsMissingResult()
    {
        using var process = new ScriptProcess();
        File.WriteAllText(process.Log, GoodLog, new UTF8Encoding(false));
        await Assert.ThrowsAsync<InvalidDataException>(() => AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(
            process.Command("exit 0"), process.Log, TimeSpan.FromSeconds(15), CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutTerminatesOwnedUpdaterAndReportsFailure()
    {
        using var process = new ScriptProcess();
        await Assert.ThrowsAsync<TimeoutException>(() => AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(
            process.Command("Start-Sleep -Seconds 30"), process.Log, TimeSpan.FromMilliseconds(250),
            CancellationToken.None, pid => process.ProcessId = pid));
        process.AssertExited();
    }

    [Fact]
    public async Task ManualCancellationPropagatesAfterOwnedUpdaterTerminates()
    {
        using var process = new ScriptProcess();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoHoeingRouteUpdater.ExecuteAndVerifyAsync(
            process.Command("Start-Sleep -Seconds 30"), process.Log, TimeSpan.FromSeconds(15), stop.Token,
            pid => process.ProcessId = pid));
        process.AssertExited();
    }
}
