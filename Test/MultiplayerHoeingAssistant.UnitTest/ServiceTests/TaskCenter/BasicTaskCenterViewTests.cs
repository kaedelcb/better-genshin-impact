using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 基础任务中心的真实 WPF 视图冒烟：确认新布局能加载、两个启动入口存在，
/// 且基础页没有回退到配置单切换页/常驻配置单下拉框。
/// 运行在隔离宿主目录，不连接 BGI、不创建真实运行。
/// </summary>
public sealed class BasicTaskCenterViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "basic-task-center-ui-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BasicView_LoadsWithTwoStartActions_AndNoConfigSwitchSurface()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
                    () => null, () => true, () => null);
                var document = new WorkflowDocument
                {
                    Name = "基础 UI 计划",
                    Nodes =
                    [
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常" } },
                        new() { NodeId = "n-2", Kind = "control.end" },
                    ],
                };
                host.SaveFlow(document, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800));
                view.Arrange(new Rect(0, 0, 1200, 800));
                view.UpdateLayout();
                RenderSnapshotIfRequested(view);

                var texts = Descendants(view).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
                Assert.Contains("▶ 启动配置单", texts);
                Assert.Contains("⏵⏵ 连续一条龙", texts);
                Assert.Contains("＋ 新增配置单", texts);
                Assert.Contains("复制", texts);
                Assert.Contains("删除", texts);
                Assert.Contains("更多计划操作", texts);
                Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == "计划设置");
                Assert.DoesNotContain("切换到配置单", texts);
                Assert.DoesNotContain("从此处开始执行 ›", texts);
                Assert.DoesNotContain("上移", texts);
                Assert.DoesNotContain("下移", texts);
                Assert.All(Descendants(view).OfType<ComboBox>(), combo => Assert.False(combo.IsVisible));

                var single = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动配置单"));
                Assert.Same(vm.Editing!.Nodes[0], vm.Editing.SelectedNode);
                Assert.True(single.IsEnabled);
                Assert.Contains(Descendants(view).OfType<ScheduleNodeStrategiesView>(), detail => detail.Visibility == Visibility.Visible);
                vm.Editing!.SelectedNode = vm.Editing.Nodes[0];
                view.UpdateLayout();
                Assert.True(single.IsEnabled);
                Assert.Empty(host.Runs.List());
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task TaskCenterShell_EntersBasicMode_AndSwitchesToAdvancedPlaceholder()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "shell-flows"), Path.Combine(_root, "shell-runs"), Path.Combine(_root, "shell-catalog.json"),
                    () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument
                {
                    Name = "壳层计划",
                    Nodes = [new() { NodeId = "end", Kind = "control.end" }],
                }, null);
                var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var shell = new ShellData(panel);
                var page = new MistletoePage { DataContext = shell };
                page.Measure(new Size(1280, 900));
                page.Arrange(new Rect(0, 0, 1280, 900));
                page.UpdateLayout();

                var basic = Assert.IsType<BasicTaskCenterView>(page.FindName("TaskCenterScroller"));
                Assert.Equal(Visibility.Visible, basic.Visibility);
                shell.TaskCenterModeIndex = 1;
                page.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, basic.Visibility);
                shell.TaskCenterModeIndex = 0;
                page.UpdateLayout();
                Assert.Equal(Visibility.Visible, basic.Visibility);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task BasicView_StartButtons_SubmitThroughTaskCenterHost_AndReachTerminal()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var boundary = new ImmediateBoundary();
                var host = new TaskCenterHost(
                    Path.Combine(_root, "run-flows"), Path.Combine(_root, "run-runs"), Path.Combine(_root, "run-catalog.json"),
                    () => null, null,
                    (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, boundary, boundary),
                    () => (true, null),
                    localExecutionCapability: () => true,
                    statusSnapshotProvider: () => null);
                var document = new WorkflowDocument
                {
                    Name = "可启动计划",
                    Nodes =
                    [
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常一条龙", Revision = "r1" } },
                        new() { NodeId = "n-end", Kind = "control.end" },
                    ],
                };
                host.SaveFlow(document, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800));
                view.Arrange(new Rect(0, 0, 1200, 800));
                view.UpdateLayout();
                var flowList = Assert.IsType<ListBox>(view.FindName("FlowList"));
                flowList.SelectedIndex = 0;
                vm.Editing!.SelectedNode = vm.Editing.Nodes[0];
                view.UpdateLayout();

                var single = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动配置单"));
                Assert.True(single.IsEnabled);
                single.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => host.Runs.List().Count == 1 && host.Runs.List()[0].IsTerminal);
                Assert.Equal(1, boundary.Submissions);
                Assert.Equal(WorkflowRunState.Succeeded, host.Runs.List().Single().State);

                var continuous = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "⏵⏵ 连续一条龙"));
                continuous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => host.Runs.List().Count == 2 && host.Runs.List().All(run => run.IsTerminal));
                Assert.Equal(2, boundary.Submissions);
                Assert.All(host.Runs.List(), run => Assert.Equal(WorkflowRunState.Succeeded, run.State));
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task EmptyPlan_DisablesBothStartActions_AndExplainsNextStep()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "empty-flows"), Path.Combine(_root, "empty-runs"), Path.Combine(_root, "empty-catalog.json"),
                    () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "空计划", Nodes = [] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800));
                view.Arrange(new Rect(0, 0, 1200, 800));
                view.UpdateLayout();

                Assert.False(vm.CanStartSelectedPlan);
                Assert.False(Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动配置单")).IsEnabled);
                Assert.False(Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "⏵⏵ 连续一条龙")).IsEnabled);
                Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == "这张计划表还是空的" && text.Visibility == Visibility.Visible);
                Assert.Contains("新增配置单", vm.WorkspaceHint);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void RenderSnapshotIfRequested(FrameworkElement view)
    {
        var path = Environment.GetEnvironmentVariable("BASIC_UI_SNAPSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpUntil(FrameworkElement view, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher)
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
        Assert.True(condition(), "等待任务中心运行终态超时。");
    }

    private sealed class ShellData(TaskCenterPanelViewModel taskCenter) : INotifyPropertyChanged
    {
        private int _taskCenterModeIndex;

        public TaskCenterPanelViewModel TaskCenter { get; } = taskCenter;
        public bool IsExecutorMode => true;
        public bool IsTaskCenterUiVisible => true;
        public int SelectedTabIndex => 1;
        public int TaskCenterModeIndex
        {
            get => _taskCenterModeIndex;
            set
            {
                var next = value == 1 ? 1 : 0;
                if (_taskCenterModeIndex == next) return;
                _taskCenterModeIndex = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TaskCenterModeIndex)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBasicTaskCenterMode)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdvancedTaskCenterMode)));
            }
        }

        public bool IsBasicTaskCenterMode => TaskCenterModeIndex == 0;
        public bool IsAdvancedTaskCenterMode => TaskCenterModeIndex == 1;
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class ImmediateBoundary : IWorkflowExecutionBoundary, IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public bool SingleNativeSupported => true;
        public int Submissions { get; private set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions++;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("ui-job-" + Submissions));
        }

        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));

        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;

        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);

        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed(null));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
