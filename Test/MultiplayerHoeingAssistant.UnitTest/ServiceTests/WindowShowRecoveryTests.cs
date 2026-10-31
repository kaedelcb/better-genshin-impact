using System.Windows.Controls;
using System.Windows.Interop;
using MultiplayerHoeingAssistant.Helpers;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class WindowShowRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualWpfRootConflict_RetriesOnce_AndReportsRealOutcome(bool repair)
    {
        await OnSta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("root-conflict-probe")
                { Width = 1, Height = 1, WindowStyle = 0 });
            var parent = new Border();
            var child = new Border();
            parent.Child = child;
            var queue = new Queue<Action>();
            var logs = new List<string>();
            var failures = new List<string>();
            var calls = 0;
            var recovery = new WindowShowRecovery(queue.Enqueue,
                (ex, outcome) => failures.Add(outcome + "\n" + ex), logs.Add);
            void Show()
            {
                calls++;
                // HwndSource 会缓存失败的赋值；探针只重置自己拥有的显示源，保留真实父级冲突。
                source.RootVisual = null;
                source.RootVisual = child;
                Assert.Same(child, source.CompositionTarget.RootVisual);
            }

            recovery.Show(Show, () => true);
            Assert.Equal(1, calls);
            Assert.Single(queue);
            Assert.Contains("CompositionTarget.SetRootVisual", logs[0]);
            Assert.Contains("尚未恢复", logs[0]);
            recovery.Show(Show, () => true); // 等待期间合并重复请求
            Assert.Equal(1, calls);
            if (repair) parent.Child = null;
            queue.Dequeue()();
            Assert.Equal(2, calls);
            Assert.Empty(queue);
            if (repair)
            {
                Assert.Same(child, source.CompositionTarget.RootVisual);
                Assert.Empty(failures);
                Assert.Contains(logs, s => s.Contains("一次延后重试完成"));
            }
            else
            {
                Assert.Single(failures);
                Assert.Contains("未恢复", failures[0]);
                Assert.DoesNotContain(logs, s => s.Contains("一次延后重试完成"));
                recovery.Show(Show, () => true);
                Assert.Empty(queue); // 持续失败不循环自动重试
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenOrShutdownWindow_CancelsPendingRetry(bool explicitCancel)
    {
        await OnSta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("cancel-probe")
                { Width = 1, Height = 1, WindowStyle = 0 });
            var child = new Border();
            var parent = new Border { Child = child };
            var queue = new Queue<Action>();
            var canShow = true;
            var calls = 0;
            var recovery = new WindowShowRecovery(queue.Enqueue, (_, _) => { }, _ => { });
            recovery.Show(() => { calls++; source.RootVisual = child; }, () => canShow);
            if (explicitCancel) recovery.Cancel();
            else canShow = false;
            queue.Dequeue()();
            Assert.Equal(1, calls);
            GC.KeepAlive(parent);
        });
    }

    [Fact]
    public async Task SchedulingFailure_PreservesOriginalConflictAndReportsFailure()
    {
        await OnSta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("schedule-probe")
                { Width = 1, Height = 1, WindowStyle = 0 });
            var child = new Border();
            var parent = new Border { Child = child };
            var logs = new List<string>();
            var failures = new List<string>();
            var recovery = new WindowShowRecovery(_ => throw new InvalidOperationException("queue-unavailable"),
                (ex, outcome) => failures.Add(outcome + "\n" + ex), logs.Add);
            recovery.Show(() => source.RootVisual = child, () => true);
            Assert.Contains("CompositionTarget.SetRootVisual", Assert.Single(logs));
            Assert.Contains("queue-unavailable", Assert.Single(failures));
            Assert.Contains("未恢复", failures[0]);
            GC.KeepAlive(parent);
        });
    }

    [Fact]
    public void UnknownError_IsReportedWithoutRetry_AndCriticalErrorEscapes()
    {
        var queued = new List<Action>();
        var failures = new List<Exception>();
        var recovery = new WindowShowRecovery(queued.Add, (e, _) => failures.Add(e), _ => { });
        var unknown = new ArgumentException("VisualTarget 的根 Visual 不能具有父级。");
        recovery.Show(() => throw unknown, () => true);
        Assert.Same(unknown, Assert.Single(failures));
        Assert.Empty(queued); // 文案相同不能冒充有实际框架堆栈的异常
        Assert.Throws<OutOfMemoryException>(() => recovery.Show(() => throw new OutOfMemoryException(), () => true));
    }

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception e) { completion.SetException(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
