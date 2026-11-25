using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("RegisterBankWpf")]
public sealed class AutoCloseNoticeWindowTests
{
    [Fact]
    public async Task AutoCloseNoticeWindow_CountsDownAndAutoCloses_WhenNotManuallyClosed() => await OnSta(async () =>
    {
        Assert.Equal(10, UpdateBgiWindow.UpdateCompletedAutoCloseSeconds);

        var observedButtons = new List<string>();
        var dialog = new AutoCloseNoticeWindow(
            owner: null,
            title: "更新BGI",
            message: "更新完成：覆盖 10 个文件。",
            countdownSeconds: 3,
            tickInterval: TimeSpan.FromMilliseconds(40))
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };

        var okButton = (Button)dialog.FindName("OkButton");
        var countdownText = (TextBlock)dialog.FindName("CountdownText");
        Assert.Equal("确定 (3s)", okButton.Content);
        Assert.Equal("3 秒后未主动关闭将自动关闭", countdownText.Text);

        dialog.Loaded += (_, _) => observedButtons.Add(okButton.Content?.ToString() ?? "");
        dialog.Closed += (_, _) => observedButtons.Add(okButton.Content?.ToString() ?? "");

        var showTask = Dispatcher.CurrentDispatcher.InvokeAsync(() => dialog.ShowDialog());
        await showTask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dialog.AutoClosed);
        Assert.Equal(0, dialog.RemainingSeconds);
        Assert.Equal("确定 (3s)", observedButtons[0]);
        Assert.Equal("确定 (1s)", observedButtons[^1]);
    });

    [Fact]
    public async Task AutoCloseNoticeWindow_ClosesImmediately_WhenOkButtonClicked() => await OnSta(async () =>
    {
        var dialog = new AutoCloseNoticeWindow(
            owner: null,
            title: "更新BGI",
            message: "测试点击确定关闭。",
            countdownSeconds: 10,
            tickInterval: TimeSpan.FromSeconds(10))
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };

        var okButton = (Button)dialog.FindName("OkButton");
        var countdownText = (TextBlock)dialog.FindName("CountdownText");
        Assert.Equal("确定 (10s)", okButton.Content);
        Assert.Equal("10 秒后未主动关闭将自动关闭", countdownText.Text);

        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));

        var showTask = Dispatcher.CurrentDispatcher.InvokeAsync(() => dialog.ShowDialog());
        await showTask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(dialog.AutoClosed);
        Assert.Equal(10, dialog.RemainingSeconds);
    });

    [Fact]
    public async Task AutoCloseNoticeWindow_ClosesImmediately_WhenWindowClosedManually() => await OnSta(async () =>
    {
        var dialog = new AutoCloseNoticeWindow(
            owner: null,
            title: "更新BGI",
            message: "测试直接关闭窗口。",
            countdownSeconds: 10,
            tickInterval: TimeSpan.FromSeconds(10))
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };

        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => dialog.Close()));

        var showTask = Dispatcher.CurrentDispatcher.InvokeAsync(() => dialog.ShowDialog());
        await showTask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(dialog.AutoClosed);
        Assert.Equal(10, dialog.RemainingSeconds);
    });

    private static async Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
