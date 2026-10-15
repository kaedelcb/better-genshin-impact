using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

internal sealed class HoeingStartupException(string stage, string message, Exception? inner = null)
    : InvalidOperationException($"hoeing_startup_failed: {stage}: {message}", inner)
{
    internal string Stage { get; } = stage;
}

/// <summary>首线之前的失败必须有终态；未收到确认不是空轮或成功。</summary>
internal static class HoeingStartupBoundary
{
    internal static void RequireConfirmed(bool? confirmed, string stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (confirmed != true) throw new HoeingStartupException(stage, "未取得成功确认");
    }

    internal static void RequireRouteCoverage(int authoritativeCount, int localCount,
        bool cooperative, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if ((cooperative || BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive)
            && localCount != authoritativeCount)
            throw new HoeingStartupException("房主路线映射",
                $"本地仅找到 {localCount}/{authoritativeCount} 条权威路线，不能按空轮完成");
    }

    internal static void RequireParticipants(string? expectedBatch, IReadOnlyCollection<string> expected,
        string? actualBatch, IReadOnlyCollection<string> actual, IReadOnlyCollection<string> joined,
        bool ready, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var roster = new HashSet<string>(expected, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(expectedBatch) || roster.Count == 0 || roster.Count != expected.Count
            || roster.Any(string.IsNullOrWhiteSpace) || actual.Count != roster.Count
            || joined.Count != roster.Count || !roster.SetEquals(actual) || !roster.SetEquals(joined)
            || expectedBatch != actualBatch || !ready)
            throw new HoeingStartupException("固定成员就绪", "批次、成员或全员进入世界状态未确认");
    }

    internal static void RethrowAfterCleanup(Exception? failure, CancellationToken ct)
    {
        if (failure is not HoeingStartupException) return;
        ct.ThrowIfCancellationRequested();
        ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>监听先由调用方注册；退出时取消并等待上报任务，之后才能释放 CTS。</summary>
    internal static async Task WaitForWorldReadyAsync(TaskCompletionSource<bool> ready,
        Func<CancellationToken, Task> report, TimeSpan timeout, CancellationToken ct,
        TimeSpan? reportInterval = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(timeout);
        var token = lifetime.Token;
        var reporter = ReportAsync();
        try
        {
            RequireConfirmed(await ready.Task.WaitAsync(token), "全员进入世界", ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HoeingStartupException("全员进入世界", "等待全员就绪超时");
        }
        finally
        {
            lifetime.Cancel();
            await reporter;
        }

        async Task ReportAsync()
        {
            try
            {
                while (!ready.Task.IsCompleted)
                {
                    token.ThrowIfCancellationRequested();
                    await report(token);
                    if (!ready.Task.IsCompleted)
                        await Task.Delay(reportInterval ?? TimeSpan.FromSeconds(5), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ready.TrySetException(new HoeingStartupException("全员进入世界上报", ex.Message, ex));
            }
        }
    }
}
