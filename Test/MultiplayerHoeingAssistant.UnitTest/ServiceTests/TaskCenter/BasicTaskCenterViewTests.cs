using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.ComponentModel;
using System.Text.Json;
using MultiplayerHoeingAssistant;
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
                Assert.Contains("▶ 启动单个任务", texts);
                Assert.Contains("⏵⏵ 连续一条龙", texts);
                Assert.Contains("＋ 新增任务", texts);
                Assert.Contains("复制", texts);
                Assert.Contains("修改", texts);
                Assert.Contains("删除", texts);
                Assert.Contains("导入全部配置", texts);
                Assert.Contains("导出全部配置", texts);
                Assert.DoesNotContain("设置", texts);
                Assert.DoesNotContain("更多计划操作", texts);
                Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == "计划设置");
                Assert.DoesNotContain("切换到配置单", texts);
                Assert.DoesNotContain("从此处开始执行 ›", texts);
                Assert.DoesNotContain("上移", texts);
                Assert.DoesNotContain("下移", texts);
                Assert.All(Descendants(view).OfType<ComboBox>(), combo => Assert.False(combo.IsVisible));

                var single = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动单个任务"));
                Assert.Same(vm.Editing!.Nodes[0], vm.Editing.SelectedNode);
                Assert.Single(vm.ResourceNodes);
                Assert.Equal(1, vm.ResourceNodes[0].ResourceIndex);
                Assert.DoesNotContain(vm.Editing.Nodes, node => !node.IsResource && vm.ResourceNodes.Contains(node));
                Assert.True(single.IsEnabled);
                vm.Editing!.SelectedNode = vm.Editing.Nodes[0];
                view.UpdateLayout();
                Assert.True(single.IsEnabled);
                Assert.Contains(Descendants(view).OfType<Button>(), button => button.Tag is NodeEditVm);
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
    public void BasicView_OffersStoredResourceWhenBgiCatalogIsOffline()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "stored-flows"), Path.Combine(_root, "stored-runs"), Path.Combine(_root, "stored-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            Name = "已有计划",
            Nodes =
            [
                new()
                {
                    NodeId = "stored-resource",
                    Kind = "resource.oneDragonConfig",
                    Ref = new() { Config = "日常一条龙", Revision = "stored-revision" },
                },
                new() { NodeId = "stored-end", Kind = "control.end" },
            ],
            Activation = new() { Status = "active" },
        }, null);

        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);

        var storedSource = Assert.Single(vm.Editing!.AppendSources, source => source.DisplayName == "日常一条龙");
        Assert.DoesNotContain("rev", storedSource.Line, StringComparison.OrdinalIgnoreCase);
        var before = vm.ResourceNodes.Count;
        vm.Editing.AppendSourceIndex = 0;
        vm.Editing.AppendNodeCommand.Execute(null);
        Assert.Equal(before + 1, vm.ResourceNodes.Count);
        vm.SaveFlowCommand.Execute(null);
        var saved = host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document;
        Assert.Equal(before + 1, saved.Nodes.Count(node => node.Kind.StartsWith("resource.", StringComparison.Ordinal)));
    }

    [Fact]
    public void BasicPlan_PreservesTeaBagOrderPeriodUidAndLegacyParticipation()
    {
        var userRoot = Path.Combine(_root, "legacy-user");
        var oneDragon = Path.Combine(userRoot, "OneDragon");
        Directory.CreateDirectory(oneDragon);
        File.WriteAllText(Path.Combine(oneDragon, "工作日-1.json"), """
        {"Name":"工作日-1","ScheduleName":"工作日计划","IndexId":1,"Period":"自定义","PeriodList":{"周三":true},"AccountBinding":true,"GenshinUid":"123456789","TaskEnabledList":{"1":{"Item1":true,"Item2":"领取邮件"}}}
        """);
        File.WriteAllText(Path.Combine(oneDragon, "工作日-2.json"), """
        {"Name":"工作日-2","ScheduleName":"工作日计划","IndexId":2,"Period":"每日","PeriodList":{"每日":true},"AccountBinding":false,"GenshinUid":"","TaskEnabledList":{"1":{"Item1":true,"Item2":"合成树脂"}}}
        """);

        var host = new TaskCenterHost(
            Path.Combine(_root, "legacy-flows"), Path.Combine(_root, "legacy-runs"), Path.Combine(_root, "legacy-catalog.json"),
            () => null, () => true, () => null);
        var migration = host.EnsureLegacyCompatibility(userRoot);
        Assert.Equal(1, migration.Added);

        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        Assert.Equal("工作日计划", vm.Editing?.NameText);
        Assert.Equal(new[] { "工作日-1", "工作日-2" }, vm.ResourceNodes.Select(node => node.DisplayName));
        Assert.Equal("三", vm.ResourceNodes[0].WeekdaysDisplay);
        Assert.Equal("123***89", vm.ResourceNodes[0].AccountDisplay);
        Assert.Equal("每天", vm.ResourceNodes[1].WeekdaysDisplay);
        Assert.False(vm.ResourceNodes[1].IncludedInPlan);
    }

    [Fact]
    public void BasicPlan_UpdatesEnabledSummaryImmediately_AndNewWeekdayFilterStartsSelected()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "instant-flows"), Path.Combine(_root, "instant-runs"), Path.Combine(_root, "instant-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            Name = "即时编辑计划",
            Nodes =
            [
                new() { NodeId = "enabled", Kind = "resource.oneDragonConfig", Ref = new() { Config = "启用配置" } },
                new() { NodeId = "disabled", Kind = "resource.oneDragonConfig", Ref = new() { Config = "停用配置" },
                    ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) } },
            ],
        }, null);

        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        Assert.Equal(1, vm.EnabledNodeCount);
        Assert.True(vm.CanStartSelectedPlan);

        var enabled = vm.ResourceNodes[0];
        enabled.IncludedInPlan = false;
        Assert.Equal(0, vm.EnabledNodeCount);
        Assert.False(vm.CanStartSelectedPlan);

        enabled.IncludedInPlan = true;
        Assert.Equal(1, vm.EnabledNodeCount);
        Assert.True(vm.CanStartSelectedPlan);

        enabled.AddWeekdaysCommand.Execute(null);
        Assert.Equal("一/二/三/四/五/六/日", enabled.WeekdaysDisplay);
    }

    [Fact]
    public void BasicView_AddConfigClick_OpensMenuAndAppendsStoredResource()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "append-flows"), Path.Combine(_root, "append-runs"), Path.Combine(_root, "append-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            Name = "可追加计划",
            Nodes = [new() { NodeId = "stored", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常配置" } }],
        }, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();
                var button = Descendants(view).OfType<Button>().First(b => Equals(b.Content, "＋ 新增任务"));
                var before = vm.ResourceNodes.Count;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(view.ActiveContextMenuForTest);
                var item = Assert.IsType<MenuItem>(view.ActiveContextMenuForTest!.Items[0]);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(before + 1, vm.ResourceNodes.Count);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        done.Task.Wait(TimeSpan.FromSeconds(10));
        done.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void DeleteFlowCommand_DeletesSelectedPlanThroughConfirmedPath()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "delete-flows"), Path.Combine(_root, "delete-runs"), Path.Combine(_root, "delete-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument { Name = "待删除计划", Nodes = [] }, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        vm.ConfirmDeleteOverride = _ => true;
        var item = Assert.Single(vm.Flows);
        vm.DeleteFlowCommand.Execute(item);
        Assert.Empty(host.ListFlows().Where(flow => flow.WorkflowId == item.WorkflowId));
    }

    [Fact]
    public void ExportAllPlans_UsesDistinctNamesForDuplicatePlanNames()
    {
        var folder = Path.Combine(_root, "export-names");
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = TaskCenterPanelViewModel.BuildUniqueExportFilePath(folder, "默认计划表", "wf-first", used);
        var second = TaskCenterPanelViewModel.BuildUniqueExportFilePath(folder, "默认计划表", "wf-second", used);
        Assert.NotEqual(first, second);
        Assert.EndsWith(".json", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("f-second", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BasicView_RightClickPlanMenuDeletesTheSelectedRow()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "delete-menu-flows"), Path.Combine(_root, "delete-menu-runs"), Path.Combine(_root, "delete-menu-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument { Name = "右键删除计划", Nodes = [] }, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();
                var list = Assert.IsType<ListBox>(view.FindName("FlowList"));
                list.SelectedIndex = 0;
                var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var menu = list.ContextMenu;
                Assert.NotNull(menu);
                menu!.PlacementTarget = container;
                var delete = Assert.IsType<MenuItem>(menu.Items[0]);
                delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Empty(host.ListFlows());
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        done.Task.Wait(TimeSpan.FromSeconds(10));
        done.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public async Task BasicView_UnknownStartClickShowsVisibleActionFeedback()
    {
        var boundary = new ImmediateBoundary();
        var host = new TaskCenterHost(
            Path.Combine(_root, "unknown-flows"), Path.Combine(_root, "unknown-runs"), Path.Combine(_root, "unknown-catalog.json"),
            () => null, null,
            (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, boundary, boundary),
            () => (true, null), localExecutionCapability: () => true);
        host.SaveFlow(new WorkflowDocument
        {
            Name = "Unknown 计划",
            Nodes = [new() { NodeId = "unknown-node", Kind = "resource.oneDragonConfig", Ref = new() { Config = "配置" } }],
        }, null);
        var run = host.Runs.CreateRun(host.ListFlows().Single().WorkflowId, host.Workflows.LoadSnapshot(host.ListFlows().Single().WorkflowId).Revision);
        run.State = WorkflowRunState.Unknown;
        host.Runs.Update(run);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();
                var button = Descendants(view).OfType<Button>().Single(b => Equals(b.Content, "⏵⏵ 连续一条龙"));
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => vm.StatusIsError && !string.IsNullOrWhiteSpace(vm.StatusMessage));
                Assert.Contains("无法确认", vm.StatusMessage);
                Assert.Equal(0, boundary.Submissions);
                Assert.Empty(Descendants(view).OfType<Button>().Where(b => Equals(b.Content, "核对状态")));
                Assert.Equal(0, boundary.Submissions);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
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
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常一条龙", Revision = "r1" },
                            ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) } },
                        new() { NodeId = "n-2", Kind = "resource.oneDragonConfig", Ref = new() { Config = "采集一条龙", Revision = "r2" } },
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

                var single = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动单个任务"));
                Assert.True(single.IsEnabled);
                single.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => host.Runs.List().Count == 1 && host.Runs.List()[0].IsTerminal);
                Assert.Equal(1, boundary.Submissions);
                Assert.Equal(WorkflowRunState.Succeeded, host.Runs.List().Single().State);
                Assert.Equal(new[] { "n-1" }, host.Runs.List().Single().NodeOutcomes
                    .Where(outcome => outcome.Result == "succeeded")
                    .Select(outcome => outcome.NodeId));

                PumpUntil(view, () => !host.IsDriving(vm.Flows.Single().WorkflowId));
                var continuous = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "⏵⏵ 连续一条龙"));
                Assert.True(continuous.IsEnabled);
                continuous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => host.Runs.List().Count == 2 && host.Runs.List().All(run => run.IsTerminal));
                Assert.Equal(2, boundary.Submissions);
                Assert.All(host.Runs.List(), run => Assert.Equal(WorkflowRunState.Succeeded, run.State));
                Assert.Contains(host.Runs.List()[1].NodeOutcomes, outcome => outcome.NodeId == "n-1" && outcome.Result == "skippedFilter");
                Assert.Contains(host.Runs.List()[1].NodeOutcomes, outcome => outcome.NodeId == "n-2" && outcome.Result == "succeeded");
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
                Assert.False(Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "▶ 启动单个任务")).IsEnabled);
                var continuous = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "⏵⏵ 连续一条龙"));
                Assert.True(continuous.IsEnabled);
                continuous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => vm.StatusIsError && !string.IsNullOrWhiteSpace(vm.StatusMessage));
                Assert.Contains("没有启用", vm.StatusMessage);
                Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == "这张计划表还是空的" && text.Visibility == Visibility.Visible);
                Assert.Contains("新增任务", vm.WorkspaceHint);
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
    public async Task ProductionMainWindow_RendersBasicTaskCenterAtRequiredSizes()
    {
        var snapshotDir = Environment.GetEnvironmentVariable("PRODUCTION_UI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(snapshotDir)) return;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            try
            {
                var dataRoot = Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT");
                Assert.False(string.IsNullOrWhiteSpace(dataRoot), "生产入口截图必须使用隔离 NEXUSBGI_DATA_ROOT。");
                var seedStore = new WorkflowStore(Path.Combine(dataRoot!, "flows"));
                seedStore.Save(new WorkflowDocument
                {
                    WorkflowId = "wf-production-ui-" + Guid.NewGuid().ToString("N"),
                    Name = "隔离计划表",
                    Nodes =
                    [
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常一条龙", Revision = "r1" } },
                        new() { NodeId = "end", Kind = "control.end" },
                    ],
                    Activation = new() { Status = "active" },
                }, null);
                app = new App();
                app.InitializeComponent();
                var viewModel = new MainViewModel();
                window = new MainWindow(viewModel)
                {
                    Width = 1180,
                    Height = 860,
                    ShowInTaskbar = false,
                };
                viewModel.CurrentPage = AppPage.Mistletoe;
                window.Mistletoe.SelectedTabIndex = 1;
                window.Show();
                window.UpdateLayout();

                var basic = Descendants(window).OfType<BasicTaskCenterView>().Single();
                Assert.Equal(Visibility.Visible, basic.Visibility);
                // 生产壳层也走一次真实按钮事件，确保 MainWindow → MistletoePage → BasicTaskCenterView
                // 不是只把 UserControl 单独渲染出来。
                var productionAdd = Descendants(basic).OfType<Button>()
                    .First(button => Equals(button.Content, "＋ 新增任务"));
                Assert.True(productionAdd.IsEnabled);
                productionAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(basic.ActiveContextMenuForTest);
                basic.ActiveContextMenuForTest!.IsOpen = false;
                RenderWindow(window, 1180, 860, Path.Combine(snapshotDir, "production-mainwindow-1180x860.png"));

                var settingsButton = Descendants(basic).OfType<Button>()
                    .First(button => button.Tag is NodeEditVm);
                settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                RenderWindow(window, 1180, 860, Path.Combine(snapshotDir, "production-mainwindow-settings.png"));
                var settingsPopup = Assert.IsType<Popup>(basic.FindName("NodeSettingsPopup"));
                var settingsContent = Assert.IsAssignableFrom<DependencyObject>(settingsPopup.Child);
                if (settingsContent is FrameworkElement settingsElement)
                    RenderElement(settingsElement, 480, 460, Path.Combine(snapshotDir, "production-settings-popup.png"));
                Assert.Contains(Descendants(settingsContent).OfType<TextBlock>(), text => text.Text == "执行日期");
                Assert.Contains(Descendants(settingsContent).OfType<TextBlock>(), text => text.Text == "账号");
                Assert.Contains(Descendants(settingsContent).OfType<TextBlock>(), text => text.Text == "此配置单的失败处理");

                var visibleText = Descendants(basic).OfType<TextBlock>()
                    .Where(text => text.Visibility == Visibility.Visible)
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();
                Assert.DoesNotContain(visibleText, text => text.Contains("control.end", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(visibleText, text => text.Contains("control.condition", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(visibleText, text => text.Contains("运行时间", StringComparison.Ordinal));
                Assert.DoesNotContain(visibleText, text => text.Contains("纳入计划", StringComparison.Ordinal));
                Assert.DoesNotContain(visibleText, text => text.Contains("兑换码前置", StringComparison.Ordinal));

                window.Width = 980;
                window.Height = 720;
                window.UpdateLayout();
                RenderWindow(window, 980, 720, Path.Combine(snapshotDir, "production-mainwindow-980x720.png"));
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                try { window?.Hide(); } catch { }
                try { app?.Shutdown(); } catch { }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
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

    private static void RenderWindow(Window window, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (window.Content is FrameworkElement content)
        {
            if (content is Panel panel) panel.Background = window.Background;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            bitmap.Render(content);
        }
        else bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderElement(FrameworkElement element, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
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
            TerminalReleaseFixtureFacts.FreezeBody(request);
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
