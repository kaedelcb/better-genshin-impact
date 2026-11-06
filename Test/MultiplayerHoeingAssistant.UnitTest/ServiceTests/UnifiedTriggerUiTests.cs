using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("RegisterBankWpf")]
public sealed class UnifiedTriggerUiTests
{
    [Fact]
    public Task RealStartupPageKeepsFourAndTwentyFourTriggersFoldedAndRoutesTheirActions() => OnSta(async () =>
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")),
            "正式页面检查必须指定隔离 NEXUSBGI_DATA_ROOT。");
        var main = new MainViewModel();
        var shell = new MistletoeViewModel(main);
        var page = new MistletoePage { DataContext = shell };
        var window = new Window { Content = page, Width = 1180, Height = 800, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var run = new WorkflowRunRecord { RunId = "card-ui-" + Guid.NewGuid().ToString("N"), WorkflowId = "wf-card-" + Guid.NewGuid().ToString("N")[..8] };
        FormalWpfTheme.Apply(window);
        var runtime = main.TaskCenterHost.FlowRuntime;
        main.TaskCenterHost.Workflows.Save(new WorkflowDocument { WorkflowId = run.WorkflowId,
            Name = "触发器验收计划", Nodes = [] }, null);
        var startup = new ArmedTimerViewModel(new StartupStep { Name = "同名定时", Kind = StartupStepKinds.TimerTrigger,
            TriggerTime = "23:59" }, DateTime.Now.AddDays(1), shell);
        shell.ArmedTimersMutable.Add(startup);
        try
        {
            await Mount(3);
            window.Show(); await Layout(window);
            var expander = Assert.IsType<Expander>(page.FindName("UnifiedTriggerExpander"));
            var scroll = Assert.IsType<ScrollViewer>(page.FindName("UnifiedTriggerScroll"));
            Assert.False(expander.IsExpanded);
            var header = Descendants<TextBlock>(expander).Single(t => t.Text == "运行中触发器");
            Assert.True(header.IsVisible && header.ActualWidth > 0);
            Assert.Equal(Color.FromRgb(242, 227, 182), Assert.IsType<SolidColorBrush>(window.TryFindResource("GoldText")).Color);
            Assert.Equal(Assert.IsType<SolidColorBrush>(window.TryFindResource("Dim")).Color,
                Assert.IsType<SolidColorBrush>(header.Foreground).Color);
            Assert.Equal("4", shell.UnifiedTriggerCount);
            Assert.Equal("启动中心 1 · 任务中心 3", shell.UnifiedTriggerSummary);
            var fourHeight = expander.ActualHeight;
            await Mount(20); await Layout(window);
            Assert.False(expander.IsExpanded);
            Assert.Equal("24", shell.UnifiedTriggerCount);
            Assert.Equal(fourHeight, expander.ActualHeight, 1);
            foreach (var width in new[] { 980d, 1180d })
            {
                window.Width = width; await Layout(window);
                Assert.False(expander.IsExpanded);
                Capture(window, "trigger-card-" + width + "-folded.png");
                expander.IsExpanded = true; await Layout(window);
                Assert.True(scroll.ActualHeight <= 210.1);
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.Equal(24, shell.UnifiedTriggerRows.Count);
                Assert.Equal(1, shell.UnifiedTriggerRows.Count(r => r.SourceLabel == "启动中心"));
                Assert.Equal(23, shell.UnifiedTriggerRows.Count(r => r.SourceLabel == "任务中心"));
                Capture(window, "trigger-card-" + width + "-expanded.png");
                expander.IsExpanded = false; await Layout(window);
            }
            var captured = shell.CaptureUnifiedTriggers();
            var startupInfo = Assert.Single(captured.Items.Where(t => t.Source == "startup"));
            var taskInfo = captured.Items.First(t => t.Source == "taskCenter");
            Assert.Contains("触发器验收计划", taskInfo.Text);
            Assert.Contains("本轮计划", taskInfo.Note);
            Assert.DoesNotContain(run.WorkflowId, taskInfo.Text);
            Assert.NotEqual(startupInfo.Key, taskInfo.Key);
            await shell.CancelUnifiedTriggerAsync(taskInfo); await Layout(window);
            Assert.Equal(22, runtime.List().Count);
            Assert.Single(shell.ArmedTimers);
            await shell.CancelUnifiedTriggerAsync(startupInfo); await Layout(window);
            Assert.Empty(shell.ArmedTimers);
            Assert.Equal(22, runtime.List().Count);
            shell.TriggerSourceFilter = 1; await Layout(window);
            Assert.Empty(shell.UnifiedTriggerRows);
            shell.TriggerSourceFilter = 2; await Layout(window);
            Assert.Equal(22, shell.UnifiedTriggerRows.Count);
        }
        finally
        {
            await runtime.CloseRunAsync(run.RunId);
            foreach (var timer in shell.ArmedTimers.ToArray()) shell.CancelTimer(timer);
            shell.StopRemoteRefresh();
            window.Close();
            await main.TaskCenterHost.ShutdownAsync();
            main.Registers.Dispose();
        }

        async Task Mount(int count)
        {
            var node = new WorkflowNode { NodeId = "f-" + Guid.NewGuid().ToString("N"), Kind = "control.flow",
                Flow = new WorkflowFlowDefinition { Name = "测试挂载", Steps = Enumerable.Range(0, count).Select(i =>
                    new StartupStep { Name = "同名定时", Kind = StartupStepKinds.TimerTrigger, TriggerScope = "round",
                        TriggerTime = DateTime.Now.AddHours(1).ToString("HH:mm"), FireSteps = [] }).ToList() } };
            await runtime.ExecuteAsync(run, node, 0, () => { }, CancellationToken.None);
            await Layout(window);
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Layout(Window window)
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void Capture(Window window, string name)
    {
        var root = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(root, name)); encoder.Save(output);
    }
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
