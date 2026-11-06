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

[Collection("RegisterBankWpf")]
public sealed class AdaptivePlanListUiTests
{
    [Theory]
    [InlineData(980, 8)]
    [InlineData(1180, 8)]
    [InlineData(980, 20)]
    [InlineData(1180, 20)]
    public Task FullWorkspaceKeepsCompactRowsAndLastTaskReachableWhenExpandedAndResized(int width, int ordinaryCount)
        => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "adaptive-plan-list-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        host.SaveFlow(BuildPlan(ordinaryCount), null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var registerSnapshot = new RegisterBankSnapshot { Available = true,
            Entries = Enumerable.Range(1, 10).Select(n => new RegisterEntry { Number = n }).ToList() };
        var bank = new RegisterBankViewModel(_ => Task.FromResult(registerSnapshot));
        bank.Apply(registerSnapshot);
        // Exercise the real task-center ancestors, not a list measured on its own.
        var page = new MistletoePage { DataContext = new PageContext(panel, bank) };
        var view = (BasicTaskCenterView)page.FindName("TaskCenterScroller");
        var window = new Window { Content = page, Width = width, Height = 760,
            ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            window.Show(); await Layout(window);
            Assert.True(view.IsVisible);
            var list = (ListBox)view.FindName("ResourceList");
            var viewport = (FrameworkElement)view.FindName("ResourceListViewport");
            Assert.Equal(ordinaryCount + 2, list.Items.Count);
            Assert.InRange(((FrameworkElement)view.FindName("PlanListPane")).ActualWidth, 180, 220);
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            AssertWorkspaceFilled(list, viewport);
            Assert.True(list.ActualHeight > 360, $"列表仍被限在 {list.ActualHeight:F1} DIP；工作区 {viewport.ActualHeight:F1} DIP。");

            var compact = await CompositionRowsAtTop(list, window);
            Assert.Equal(2, compact.Length);
            foreach (var row in compact)
            {
                var expander = Assert.Single(Descendants<Expander>(row));
                Assert.False(expander.IsExpanded);
                Assert.InRange(row.ActualHeight, 30, 36);
                var header = Assert.IsType<Grid>(expander.Header);
                Assert.Empty(header.RowDefinitions);
                Assert.InRange(header.ActualHeight, 26, 28.5);
                Assert.All(Descendants<Button>(header), b => Assert.InRange(b.ActualHeight, 23, 25));
                Assert.All(Descendants<TextBlock>(header), t => Assert.True(t.ActualHeight < 28));
            }
            Capture(window, $"adaptive-plan-{width}-{ordinaryCount}-collapsed");

            await AssertLastTaskVisible(list, window);
            AssertWorkspaceFilled(list, viewport);

            compact = await CompositionRowsAtTop(list, window);
            foreach (var row in compact)
            {
                var expander = Assert.Single(Descendants<Expander>(row));
                var toggle = Assert.Single(Descendants<System.Windows.Controls.Primitives.ToggleButton>(expander));
                var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
                ((System.Windows.Automation.Provider.IToggleProvider)peer).Toggle();
                await Layout(window);
                Assert.True(expander.IsExpanded);
            }
            await Layout(window);
            AssertWorkspaceFilled(list, viewport);
            var lane = compact.Single(r => ((NodeEditVm)r.DataContext).Model.Lanes is not null);
            Assert.True(lane.ActualHeight > 36);
            Assert.Contains(Descendants<TextBlock>(lane), t => t.Text.Contains("上车道采集"));
            Assert.Contains(Descendants<TextBlock>(lane), t => t.Text.Contains("下车道采集"));
            Capture(window, $"adaptive-plan-{width}-{ordinaryCount}-expanded");
            await AssertLastTaskVisible(list, window);

            var restored = await CompositionRowsAtTop(list, window);
            foreach (var row in restored)
            {
                var expander = Assert.Single(Descendants<Expander>(row));
                Assert.True(expander.IsExpanded);
                var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(row).First();
                ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle)).Toggle();
            }
            await Layout(window);
            Assert.All(restored, row => Assert.InRange(row.ActualHeight, 29, 32.5));

            var oldHeight = list.ActualHeight;
            window.Width = width == 980 ? 1180 : 980;
            window.Height = 900;
            await Layout(window);
            AssertWorkspaceFilled(list, viewport);
            Assert.True(list.ActualHeight >= oldHeight + 100,
                $"窗口变高后列表未扩展：{oldHeight:F1} → {list.ActualHeight:F1}。");
            await AssertLastTaskVisible(list, window);
            Capture(window, $"adaptive-plan-{width}-{ordinaryCount}-resized-last");

            window.Width = width; window.Height = 760;
            await Layout(window);
            AssertWorkspaceFilled(list, viewport);
            await AssertLastTaskVisible(list, window);
        }
        finally
        {
            window.Close(); panel.StopAutoRefresh(); await host.ShutdownAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    private static WorkflowDocument BuildPlan(int ordinaryCount)
    {
        var condition = StartupStepKinds.Create(StartupStepKinds.Weekday);
        condition.Weekdays = [6, 7];
        var nodes = new List<WorkflowNode>
        {
            new() { NodeId = "prepare", Kind = "control.flow", Flow = new()
                { Name = "采集前准备流程与较长的说明名称", Steps = [StartupStepKinds.Create(StartupStepKinds.Wait)] } },
            new() { NodeId = "routes", Kind = "control.lanes", Lanes = new()
                { Name = "根据星期选择今天采集的路线", YesName = "上车道采集", NoName = "下车道采集",
                  Decision = new() { Name = "星期条件", Steps = [condition], ExitStepId = condition.Id },
                  YesNodeIds = ["upper-1", "upper-2", "upper-3"], NoNodeIds = ["lower-1", "lower-2", "lower-3"] } }
        };
        for (var i = 1; i <= ordinaryCount; i++)
            nodes.Add(new() { NodeId = $"normal-{i}", Kind = "resource.configGroup", Ref = new() { Config = $"普通任务 {i:00}" } });
        foreach (var id in new[] { "upper-1", "upper-2", "upper-3", "lower-1", "lower-2", "lower-3" })
            nodes.Add(new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = $"车道内任务 {id}" } });
        return new() { WorkflowId = "wf-adaptive-list", Name = "自适应计划表", Nodes = nodes };
    }

    private static void AssertWorkspaceFilled(ListBox list, FrameworkElement viewport)
    {
        Assert.True(double.IsFinite(list.ActualHeight) && list.ActualHeight > 0);
        Assert.InRange(Math.Abs(list.ActualHeight - viewport.ActualHeight), 0, 1);
        var bounds = list.TransformToAncestor(viewport).TransformBounds(new Rect(list.RenderSize));
        Assert.InRange(Math.Abs(bounds.Top), 0, 1);
        Assert.InRange(Math.Abs(bounds.Bottom - viewport.ActualHeight), 0, 1);
        var scroll = Assert.Single(Descendants<ScrollViewer>(list));
        Assert.True(double.IsFinite(scroll.ViewportHeight) && scroll.ViewportHeight > 0);
    }

    private static async Task<CompositionPlanRow[]> CompositionRowsAtTop(ListBox list, Window window)
    {
        list.ScrollIntoView(list.Items[0]); await Layout(window);
        var rows = Descendants<CompositionPlanRow>(list)
            .Where(r => r.IsVisible && r.DataContext is NodeEditVm { IsComposition: true }).ToArray();
        Assert.Equal(2, rows.Length);
        return rows;
    }

    private static async Task AssertLastTaskVisible(ListBox list, Window window)
    {
        var last = Assert.IsType<NodeEditVm>(list.Items[list.Items.Count - 1]);
        Assert.StartsWith("normal-", last.NodeId);
        list.ScrollIntoView(last); await Layout(window);
        var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(last));
        var bounds = container.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
        Assert.True(container.IsVisible);
        Assert.InRange(container.ActualHeight, 26, 32.5);
        Assert.True(bounds.Top >= -1, $"最后任务上沿在视口外：{bounds.Top:F1}。");
        Assert.True(bounds.Bottom <= list.ActualHeight + 1,
            $"最后任务仍在列表底部以外：{bounds.Bottom:F1} > {list.ActualHeight:F1}。");
    }

    public sealed class PageContext(TaskCenterPanelViewModel panel, RegisterBankViewModel registers)
    {
        public TaskCenterPanelViewModel TaskCenter => panel;
        public RegisterBankViewModel Registers => registers;
        public int SelectedTabIndex => 1;
        public bool CanShowTaskCenter => true;
        public bool IsTaskCenterUiVisible => true;
        public bool IsBasicTaskCenterMode => true;
        public bool IsAdvancedTaskCenterMode => false;
    }

    private static async Task Layout(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var visual = window.Content as FrameworkElement ?? window;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rect = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, rect);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, rect);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(stream);
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
