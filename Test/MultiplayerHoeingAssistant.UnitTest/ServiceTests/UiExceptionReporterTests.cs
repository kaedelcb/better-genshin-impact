using System.Windows.Controls;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Helpers;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class UiExceptionReporterTests
{
    [Fact]
    public async Task RepeatedVisualExceptions_DuringNestedDispatcherLoop_ShowOneDialogAndKeepStack()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var logs = new List<string>();
            var messages = new List<string>();
            var handled = 0;
            var frames = 0;
            var maxFrames = 0;
            var reporter = new UiExceptionReporter(logs.Add, message =>
            {
                messages.Add(message);
                maxFrames = Math.Max(maxFrames, ++frames);
                // MessageBox 也会泵送嵌套消息循环：弹窗未关时，刷新仍可能抛出相同异常。
                var nested = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => nested.Continue = false));
                Dispatcher.PushFrame(nested);
                frames--;
            });
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                handled++;
                reporter.Report(args.Exception);
            };
            try
            {
                for (var i = 0; i < 20; i++)
                    dispatcher.BeginInvoke(new Action(ThrowDisconnectedVisual));
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                Assert.Equal(20, handled);
                Assert.Single(messages);
                Assert.Equal(1, maxFrames);
                Assert.Equal(5, logs.Count); // 1、2、4、8、16 次
                Assert.Contains("TransformToDescendant", logs[0]);
                Assert.Contains(nameof(ThrowDisconnectedVisual), logs[0]);
                Assert.Contains("累计 16 次", logs[^1]);
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void DifferentErrors_RemainVisible_AndNestedErrorIsLoggedWithoutStacking()
    {
        var logs = new List<string>();
        var messages = new List<string>();
        UiExceptionReporter? reporter = null;
        reporter = new UiExceptionReporter(logs.Add, message =>
        {
            messages.Add(message);
            reporter!.Report(new InvalidOperationException("弹窗期间的另一异常"));
        });

        reporter.Report(new InvalidOperationException("第一次界面异常"));
        reporter.Report(new ArgumentException("另一个入口异常"));

        Assert.Equal(2, messages.Count);
        Assert.Contains("另一个入口异常", messages[1]);
        Assert.Contains(logs, line => line.Contains("弹窗期间的另一异常"));
    }

    [Fact]
    public void FailedNotification_IsLogged_AndDoesNotPreventLaterErrorReporting()
    {
        var logs = new List<string>();
        var calls = 0;
        var reporter = new UiExceptionReporter(logs.Add, _ =>
        {
            calls++;
            throw new InvalidOperationException("提示窗口无法打开");
        });

        reporter.Report(new InvalidOperationException("第一处异常"));
        reporter.Report(new ArgumentException("第二处异常"));

        Assert.Equal(2, calls);
        Assert.Equal(2, logs.Count(line => line.Contains("[UI异常提示失败]")));
    }

    private static void ThrowDisconnectedVisual() => new Border().TransformToDescendant(new Border());
}
