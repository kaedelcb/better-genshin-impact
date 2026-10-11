using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>隔离数据根下从生产 MainWindow 到 MistletoePage/任务中心的入口冒烟。</summary>
public sealed class ProductionTaskCenterEntryTests
{
    [Fact]
    public async Task MainWindow_MistletoeEntry_UsesBasicModeAndSwitchesAdvanced()
    {
        Assert.True(MultiplayerHoeingAssistant.Services.AssistantDataDirectory.IsExplicit,
            "此测试必须由外部为本进程指定 NEXUSBGI_DATA_ROOT，不能触碰真实用户目录。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            try
            {
                app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var workflows = new WorkflowStore(WorkflowStore.DefaultFlowsDir());
                workflows.Save(new WorkflowDocument
                {
                    Name = "窗口验收计划",
                    Nodes =
                    [
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "窗口整龙", Revision = "r1" } },
                        new() { NodeId = "n-end", Kind = "control.end" },
                    ],
                }, null);
                var mainVm = new MainViewModel();
                window = new MainWindow(mainVm);
                mainVm.CurrentPage = AppPage.Mistletoe;
                window.Show();
                window.Mistletoe.TaskCenter.RefreshCommand.Execute(null);
                window.UpdateLayout();

                var mistletoe = FindVisualChild<MistletoePage>(window);
                Assert.NotNull(mistletoe);
                var shell = window.Mistletoe;
                shell.SelectedTabIndex = 1;
                shell.TaskCenterModeIndex = 0;
                window.UpdateLayout();
                PumpUntil(window, () => shell.TaskCenter.Editing is not null);
                var basic = FindVisualChild<BasicTaskCenterView>(mistletoe!);
                Assert.NotNull(basic);
                Assert.Equal(Visibility.Visible, basic!.Visibility);
                Assert.NotNull(shell.TaskCenter.Editing);
                Assert.Contains("窗口整龙", shell.TaskCenter.Editing!.Nodes.Select(node => node.DisplayName));
                var nameText = FindVisualChild<TextBlock>(basic, text => text.Text == "窗口整龙");
                Assert.NotNull(nameText);
                Assert.True(nameText!.IsVisible);
                Assert.True(nameText.ActualWidth > 0);

                shell.TaskCenterModeIndex = 1;
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, basic.Visibility);
                shell.TaskCenterModeIndex = 0;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, basic.Visibility);
                RenderSnapshotIfRequested(window);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (window is not null)
                {
                    window.Mistletoe.TaskCenter.StopAutoRefresh();
                    if (typeof(MistletoeViewModel).GetField("_bgiStatusRefresh", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?.GetValue(window.Mistletoe) is System.Windows.Threading.DispatcherTimer timer)
                        timer.Stop();
                    window.Hide();
                }
                if (app is not null && app != Application.Current)
                    app.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static T? FindVisualChild<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match && (predicate is null || predicate(match))) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindVisualChild(VisualTreeHelper.GetChild(root, i), predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static void RenderSnapshotIfRequested(Window window)
    {
        var path = Environment.GetEnvironmentVariable("PRODUCTION_UI_SNAPSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpUntil(Window window, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "任务中心刷新未在限定时间内完成。");
    }
}
