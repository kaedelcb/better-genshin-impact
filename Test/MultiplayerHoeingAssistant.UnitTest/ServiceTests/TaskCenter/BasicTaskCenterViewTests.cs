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
                var resourceList = Assert.IsType<ListBox>(view.FindName("ResourceList"));
                var resourceRow = Assert.IsType<ListBoxItem>(resourceList.ItemContainerGenerator.ContainerFromIndex(0));
                var resourceGrid = Descendants(resourceRow).OfType<Grid>().Single(grid => grid.Tag is NodeEditVm);
                Assert.Contains(Descendants(resourceRow).OfType<Button>(), button => Equals(button.Content, "⚙"));
                var blankNamePoint = new Point(resourceGrid.ColumnDefinitions[0].ActualWidth
                    + resourceGrid.ColumnDefinitions[1].ActualWidth - 80, resourceGrid.ActualHeight / 2);
                Assert.NotNull(VisualTreeHelper.HitTest(resourceGrid, blankNamePoint));
                Assert.NotNull(VisualTreeHelper.HitTest(resourceRow,
                    new Point(resourceRow.ActualWidth - 2, resourceRow.ActualHeight / 2)));
                Assert.NotNull(resourceList.ContextMenu);
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
    public async Task BasicView_RowSettingsShowsInternalTasksAndSavesBGIAndPlanReference()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "internal-flows"), Path.Combine(_root, "internal-runs"),
                    Path.Combine(_root, "internal-catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument
                {
                    Name = "内部任务设置",
                    Nodes = [new() { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                        Ref = new() { Config = "日常", Revision = "old" } },
                        new() { NodeId = "n-2", Kind = "resource.oneDragonConfig",
                            Ref = new() { Config = "日常", Revision = "old" } }],
                }, null);
                var transport = new InternalLayoutTransport();
                host.ResourceEditorTransportForTest = transport;
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                window = new Window { Content = view, Width = 1200, Height = 800,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                window.Show(); window.UpdateLayout();
                var resourceList = Assert.IsType<ListBox>(view.FindName("ResourceList"));
                var resourceRow = Assert.IsType<ListBoxItem>(resourceList.ItemContainerGenerator.ContainerFromIndex(0));
                var settings = Descendants(resourceRow).OfType<Button>().Single(button => Equals(button.Content, "⚙"));
                settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popup = Assert.IsType<Popup>(view.FindName("ResourceTasksPopup"));
                var tasks = Assert.IsType<ListBox>(view.FindName("ResourceTaskList"));
                try { PumpUntil(view, () => popup.IsOpen && tasks.Items.Count == 2); }
                catch { throw new Exception($"弹窗={popup.IsOpen}, 内部任务={tasks.Items.Count}, 反馈={((TextBlock)view.FindName("ResourceTasksFeedback")).Text}"); }
                Assert.Equal(new[] { "邮件", "委托" }, tasks.Items.Cast<object>()
                    .Select(item => item.GetType().GetProperty("Name")!.GetValue(item)?.ToString()));
                var sync = Assert.IsType<Button>(view.FindName("ResourceTaskSyncButton"));
                Assert.True(sync.IsEnabled);
                sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(view, () => host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId)
                    .Document.Nodes.All(node => node.Ref?.Revision == "r1"));
                PumpUntil(view, () => tasks.Items.Count == 2
                    && ((TextBlock)view.FindName("ResourceTasksFeedback")).Text.StartsWith("已读取"));
                var first = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(0));
                var enabled = Descendants(first).OfType<CheckBox>().Single();
                enabled.IsChecked = false;
                enabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                try { PumpUntil(view, () => transport.Revision == "r2"
                    && host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes[0].Ref?.Revision == "r2"); }
                catch { throw new Exception($"BGI修订={transport.Revision}, 计划修订={host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes[0].Ref?.Revision}, 反馈={((TextBlock)view.FindName("ResourceTasksFeedback")).Text}, 状态={vm.StatusMessage}"); }
                Assert.False(transport.TaskEnabled[0]);
                Assert.Equal("r2", vm.Editing!.Nodes[0].Model.Ref!.Revision);
                Assert.Equal("r2", vm.Editing.Nodes[1].Model.Ref!.Revision);

                PumpUntil(view, () => tasks.Items.Count == 2
                    && ((TextBlock)view.FindName("ResourceTasksFeedback")).Text.StartsWith("已读取"));
                var source = tasks.Items[1];
                var target = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(0));
                var constructor = typeof(DragEventArgs).GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null,
                    [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
                    null)!;
                var drop = (DragEventArgs)constructor.Invoke(
                    [new DataObject(source.GetType(), source), DragDropKeyStates.LeftMouseButton,
                        DragDropEffects.Move, target, new Point(8, target.ActualHeight / 2)]);
                drop.RoutedEvent = UIElement.DropEvent;
                target.RaiseEvent(drop);
                Assert.True(drop.Handled);
                PumpUntil(view, () => transport.Revision == "r3"
                    && host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes[0].Ref?.Revision == "r3");
                Assert.Equal(new[] { "b", "a" }, transport.Order);

                PumpUntil(view, () => tasks.Items.Count == 2
                    && ((TextBlock)view.FindName("ResourceTasksFeedback")).Text.StartsWith("已读取"));
                var startRow = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(0));
                var contextConstructor = typeof(ContextMenuEventArgs).GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, [typeof(object), typeof(bool)], null)!;
                var opening = (ContextMenuEventArgs)contextConstructor.Invoke([startRow, true]);
                opening.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                startRow.RaiseEvent(opening);
                Assert.False(opening.Handled);
                Assert.IsType<MenuItem>(tasks.ContextMenu!.Items[0])
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(view, () => transport.Revision == "r4"
                    && host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes[0].Ref?.Revision == "r4");
                Assert.Equal("b", transport.StartTaskId);
                popup.IsOpen = false;
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { window?.Close(); }
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
    public void BasicView_AddConfigClick_OpensCategoryPickerAndAppendsStoredResource()
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
            Window? window = null;
            try
            {
                var view = new BasicTaskCenterView { DataContext = vm };
                window = new Window { Content = view, Width = 1200, Height = 800,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                window.Show(); window.UpdateLayout();
                var button = Descendants(view).OfType<Button>().First(b => Equals(b.Content, "＋ 新增任务"));
                var before = vm.ResourceNodes.Count;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popup = Assert.IsType<Popup>(view.FindName("AddTaskPopup"));
                PumpUntil(view, () => popup.IsOpen);
                var choices = Assert.IsType<ListBox>(view.FindName("AddTaskChoices"));
                Assert.Equal(new[] { "日常配置" }, PickerLabels(choices));
                choices.SelectedIndex = 0;
                Assert.IsType<Button>(view.FindName("ConfirmAddTask"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(before + 1, vm.ResourceNodes.Count);
                Assert.False(popup.IsOpen);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        done.Task.Wait(TimeSpan.FromSeconds(10));
        done.Task.GetAwaiter().GetResult();
    }

    [Fact]
    public void BasicView_AddTaskPicker_SeparatesKindsSearchesSinglesAndScrollsInGoldStyle()
    {
        var host = new TaskCenterHost(
            Path.Combine(_root, "picker-flows"), Path.Combine(_root, "picker-runs"), Path.Combine(_root, "picker-catalog.json"),
            () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            Name = "分类选择计划",
            Nodes =
            [
                new() { NodeId = "group", Kind = "resource.configGroup", Ref = new() { Config = "任务组甲" } },
                new() { NodeId = "dragon", Kind = "resource.oneDragonConfig", Ref = new() { Config = "整龙乙" } },
            ],
        }, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var view = new BasicTaskCenterView { DataContext = vm };
                window = new Window { Content = view, Width = 1200, Height = 800,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                var goldViewer = new Style(typeof(ScrollViewer));
                window.Resources["GoldScrollViewer"] = goldViewer;
                window.Show(); window.UpdateLayout();
                Descendants(view).OfType<Button>().First(button => Equals(button.Content, "＋ 新增任务"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popup = Assert.IsType<Popup>(view.FindName("AddTaskPopup"));
                PumpUntil(view, () => popup.IsOpen);
                var editing = vm.Editing!;
                for (var i = 0; i < 20; i++)
                    editing.AppendSources.Add(new CatalogSourceVm(new TaskCenterResourceEntry
                    {
                        Kind = TaskCenterResourceKind.SingleTask,
                        StableId = $"single:任务组甲:task-{i}", DisplayName = $"任务{i:00}", OwnerConfig = "任务组甲",
                        Enabled = true, SingleExecutionSupported = true,
                    }));
                var choices = Assert.IsType<ListBox>(view.FindName("AddTaskChoices"));
                Assert.Equal(new[] { "任务组甲" }, PickerLabels(choices));
                Assert.False(Assert.IsType<Button>(view.FindName("ConfirmAddTask")).IsEnabled);
                Assert.IsType<Button>(view.FindName("AddTaskDragonCategory"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(new[] { "整龙乙" }, PickerLabels(choices));
                Assert.IsType<Button>(view.FindName("AddTaskSingleCategory"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(20, choices.Items.Count);
                Assert.All(PickerLabels(choices), label => Assert.StartsWith("任务组甲 / 任务", label));
                choices.UpdateLayout();
                var scroll = Descendants(choices).OfType<ScrollViewer>().Single();
                Assert.Same(goldViewer, scroll.Style);
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ScrollToBottom();
                scroll.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(scroll.VerticalOffset > 0);
                var search = Assert.IsType<TextBox>(view.FindName("AddTaskSearch"));
                search.Text = "任务07";
                Assert.Equal(new[] { "任务组甲 / 任务07" }, PickerLabels(choices));
                choices.SelectedIndex = 0;
                Assert.IsType<Button>(view.FindName("ConfirmAddTask"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(popup.IsOpen);
                var added = Assert.Single(editing.Nodes, node => node.Model.Kind == "resource.singleTask");
                Assert.Equal("任务组甲", added.Model.Ref?.Config);
                Assert.Equal("task-7", added.Model.Ref?.TaskId);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        done.Task.Wait(TimeSpan.FromSeconds(12));
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
    public async Task DeleteFlowCommand_YesOnOldUnknown_RemovesPlanAndKeepsRun()
    {
        var flows = Path.Combine(_root, "unknown-delete-flows");
        var runs = Path.Combine(_root, "unknown-delete-runs");
        var document = new WorkflowDocument { Name = "旧 Unknown 计划", Nodes = [] };
        new WorkflowStore(flows).Save(document, null);
        var store = new RunStore(runs);
        var run = store.CreateRun(document.WorkflowId!, "old-revision");
        run.State = WorkflowRunState.Unknown;
        store.Update(run);
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "unknown-delete-catalog.json"),
            () => null, () => true, () => null);
        try
        {
            var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var confirmed = false;
            vm.ConfirmDeleteUnknownOverride = _ => { confirmed = true; return true; };
            var item = Assert.Single(vm.Flows);

            vm.DeleteFlowCommand.Execute(item);

            Assert.True(confirmed);
            Assert.Empty(host.ListFlows());
            Assert.Single(host.ListDeletedFlows());
            Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(run.RunId)!.State);
        }
        finally { await host.ShutdownAsync(); }
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
    public async Task BasicView_DroppingThirdResourceOnFirstRowMovesItAcrossTheWholeList()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "reorder-flows"), Path.Combine(_root, "reorder-runs"),
                    Path.Combine(_root, "reorder-catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument
                {
                    Name = "拖动配置单",
                    Nodes = new[] { "a", "b", "c", "d" }.Select(id => new WorkflowNode
                    {
                        NodeId = id, Kind = "resource.oneDragonConfig", Ref = new() { Config = id },
                    }).ToList(),
                }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();

                var list = Assert.IsType<ListBox>(view.FindName("ResourceList"));
                var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var target = Descendants(firstRow).OfType<Grid>().Single(element => element.Tag is NodeEditVm);
                var dragged = vm.ResourceNodes[2];
                var constructor = typeof(DragEventArgs).GetConstructor(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null,
                    [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
                    null)!;
                var drop = (DragEventArgs)constructor.Invoke(
                    [new DataObject(typeof(NodeEditVm), dragged), DragDropKeyStates.LeftMouseButton,
                        DragDropEffects.Move, target, new Point(8, target.ActualHeight - 2)]);
                drop.RoutedEvent = UIElement.PreviewDropEvent;
                target.RaiseEvent(drop);

                Assert.True(drop.Handled);
                Assert.Equal(new[] { "c", "a", "b", "d" }, vm.ResourceNodes.Select(node => node.NodeId));
                Assert.Same(dragged, vm.ResourceNodes[0]);
                PumpUntil(view, () => host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes
                    .Select(node => node.NodeId).SequenceEqual(["c", "a", "b", "d"]));
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task BasicView_DeletingResourceRowsKeepsSelectionAndSavesThePlan()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new TaskCenterHost(
                    Path.Combine(_root, "delete-resource-flows"), Path.Combine(_root, "delete-resource-runs"),
                    Path.Combine(_root, "delete-resource-catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument
                {
                    Name = "删除配置单",
                    Nodes =
                    [
                        new() { NodeId = "first", Kind = "resource.oneDragonConfig", Ref = new() { Config = "第一个" } },
                        new() { NodeId = "second", Kind = "resource.oneDragonConfig", Ref = new() { Config = "第二个" } },
                        new() { NodeId = "end", Kind = "control.end" },
                    ],
                }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();

                var list = Assert.IsType<ListBox>(view.FindName("ResourceList"));
                var first = vm.ResourceNodes[0];
                var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
                menu.Tag = first;
                menu.PlacementTarget = row;
                menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除此配置单"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal("second", Assert.Single(vm.ResourceNodes).NodeId);
                Assert.Same(vm.ResourceNodes[0], vm.Editing!.SelectedNode);
                var boundResources = list.ItemsSource;
                vm.Editing.SelectedNode = null;
                Assert.Same(boundResources, list.ItemsSource);
                Assert.Same(vm.ResourceNodes[0], vm.Editing.SelectedNode);
                PumpUntil(view, () => host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes
                    .Count(node => node.Kind.StartsWith("resource.", StringComparison.Ordinal)) == 1);

                var deleteButton = Descendants(view).OfType<Button>().Single(button => Equals(button.Content, "删除"));
                Assert.True(deleteButton.IsEnabled);
                Assert.NotNull(deleteButton.Command);
                deleteButton.Command.Execute(deleteButton.CommandParameter);
                Assert.Empty(vm.ResourceNodes);
                PumpUntil(view, () => host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes
                    .All(node => !node.Kind.StartsWith("resource.", StringComparison.Ordinal)));
                Assert.Null(vm.Editing!.SelectedNode);
                Assert.Single(host.LoadFlowSnapshot(vm.Flows.Single().WorkflowId).Document.Nodes);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
                var verifyDeletion = Environment.GetEnvironmentVariable("TASKCENTER_DELETE_UI_TEST") == "1";
                if (verifyDeletion)
                {
                    Assert.Contains("taskcenter-delete-", Path.GetFullPath(dataRoot!), StringComparison.OrdinalIgnoreCase);
                    Assert.False(File.Exists(Path.Combine(dataRoot!, "assistant-config.json")));
                    new AssistConfigManager().Save(new AssistConfig
                    {
                        DisclaimerAccepted = true,
                        StandaloneMode = true,
                        ServerUrl = string.Empty,
                    });
                }
                var seedStore = new WorkflowStore(Path.Combine(dataRoot!, "flows"));
                var workflowId = "wf-production-ui-" + Guid.NewGuid().ToString("N");
                seedStore.Save(new WorkflowDocument
                {
                    WorkflowId = workflowId,
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
                var addPopup = Assert.IsType<Popup>(basic.FindName("AddTaskPopup"));
                PumpUntil(basic, () => addPopup.IsOpen);
                var addChoices = Assert.IsType<ListBox>(basic.FindName("AddTaskChoices"));
                Assert.Same(window.TryFindResource("GoldScrollViewer"), addChoices.Resources[typeof(ScrollViewer)]);
                RenderElement(Assert.IsAssignableFrom<FrameworkElement>(addPopup.Child), 540, 500,
                    Path.Combine(snapshotDir, "production-add-task-picker.png"));
                addPopup.IsOpen = false;
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

                if (verifyDeletion)
                {
                    settingsPopup.IsOpen = false;
                    var panel = Assert.IsType<TaskCenterPanelViewModel>(basic.DataContext);
                    var resourceList = Assert.IsType<ListBox>(basic.FindName("ResourceList"));
                    var resourceRow = Assert.IsType<ListBoxItem>(resourceList.ItemContainerGenerator.ContainerFromIndex(0));
                    var menu = Assert.IsType<ContextMenu>(resourceList.ContextMenu);
                    menu.Tag = Assert.Single(panel.ResourceNodes);
                    menu.PlacementTarget = resourceRow;
                    menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除此配置单"))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Empty(panel.ResourceNodes);
                    PumpUntil(basic, () => seedStore.LoadSnapshot(workflowId).Document.Nodes
                        .All(node => !node.Kind.StartsWith("resource.", StringComparison.Ordinal)));
                    Assert.Single(seedStore.LoadSnapshot(workflowId).Document.Nodes);
                }
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

    private static string[] PickerLabels(ListBox choices)
        => choices.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!).ToArray();

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
        Exception? tickFailure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        timer.Tick += (_, _) =>
        {
            try
            {
                if (condition() || DateTime.UtcNow >= deadline)
                {
                    timer.Stop();
                    frame.Continue = false;
                }
            }
            catch (Exception ex)
            {
                tickFailure = ex;
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        if (tickFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tickFailure).Throw();
        Assert.True(condition(), "等待任务中心运行终态超时。");
    }

    private sealed class InternalLayoutTransport : IResourceCatalogTransport
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        private int _revisionNumber = 1;
        public string Revision => "r" + _revisionNumber;
        public bool[] TaskEnabled { get; private set; } = [true, true];
        private string[] _order = ["a", "b"];
        private string? _start;
        public IReadOnlyList<string> Order => _order;
        public string? StartTaskId => _start;

        public Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            if (operation == "ext.config.describe")
                return Task.FromResult<string?>(JsonSerializer.Serialize(new
                {
                    configRevision = Revision, startTaskId = _start,
                    tasks = _order.Select((id, index) => new
                    {
                        taskId = id, name = id == "a" ? "邮件" : "委托", enabled = TaskEnabled[index],
                    }).ToArray(),
                }));
            if (operation == WorkflowResourceEditor.ApplyLayoutOperation)
            {
                var sent = JsonSerializer.SerializeToElement(payload);
                Assert.Equal(Revision, sent.GetProperty("expectedConfigRevision").GetString());
                var items = sent.GetProperty("tasks").EnumerateArray().ToArray();
                _order = items.Select(item => item.GetProperty("taskId").GetString()!).ToArray();
                TaskEnabled = items.Select(item => item.GetProperty("enabled").GetBoolean()).ToArray();
                _start = sent.GetProperty("startTaskId").ValueKind == JsonValueKind.String
                    ? sent.GetProperty("startTaskId").GetString() : null;
                _revisionNumber++;
                return Task.FromResult<string?>(JsonSerializer.Serialize(new { status = "config_applied", configRevision = Revision }));
            }
            return Task.FromResult<string?>(null);
        }
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
