using System.IO;
using MultiplayerHoeingAssistant.Helpers;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RuntimeLogTests
{
    [Fact]
    public void ExceptionRecord_PreservesAllAggregateBranchesAndRuntimeIdentity()
    {
        Exception thrown;
        try { throw new InvalidOperationException("inner-stack-probe"); }
        catch (Exception e) { thrown = e; }
        var error = new AggregateException("outer", thrown, new IOException("second-branch"));
        var text = RuntimeLog.FormatException("AppDomain", error, true, "未恢复");
        foreach (var value in new[] { "inner-stack-probe", "second-branch", nameof(ExceptionRecord_PreservesAllAggregateBranchesAndRuntimeIdentity),
                     "IsTerminating=True", "PID=", "Session=", "Thread=", "Version=", "未恢复" })
            Assert.Contains(value, text);
        Assert.True(RuntimeLog.IsCritical(new AggregateException(new IOException(), new OutOfMemoryException())));
        Assert.False(RuntimeLog.IsCritical(error));
    }

    [Fact]
    public void LockedPrimaryLog_WritesOriginalRecordAndFailureToFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var primary = Path.Combine(root, "main.log");
        var fallback = Path.Combine(root, "fallback.log");
        using (var locked = new FileStream(primary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            RuntimeLog.WriteRecord(primary, fallback, "original-exception-stack\n");
            var text = File.ReadAllText(fallback);
            Assert.Contains("original-exception-stack", text);
            Assert.Contains("主日志写入失败", text);
            Assert.Contains(primary, text);
        }
        File.Delete(primary);
        File.Delete(fallback);
        Directory.Delete(root);
    }

    [Fact]
    public void ConcurrentRecords_AreCompleteAndNotLost()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-log-test-" + Guid.NewGuid().ToString("N"));
        var primary = Path.Combine(root, "main.log");
        var fallback = Path.Combine(root, "fallback.log");
        Parallel.For(0, 24, i => RuntimeLog.WriteRecord(primary, fallback, $"record-{i}\n"));
        var lines = File.ReadAllLines(primary);
        Assert.Equal(24, lines.Distinct().Count());
        Assert.False(File.Exists(fallback));
        File.Delete(primary);
        Directory.Delete(root);
    }
}
