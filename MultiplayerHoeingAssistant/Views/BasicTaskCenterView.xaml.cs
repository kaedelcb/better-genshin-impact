using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView : UserControl
{
    private TaskCenterPanelViewModel? Host => DataContext as TaskCenterPanelViewModel;
    private Point _resourceDragStart;
    private NodeEditVm? _resourceDragNode;
    private int _popupOpenGeneration;
    private ContextMenu? _activeContextMenu;
    internal ContextMenu? ActiveContextMenuForTest => _activeContextMenu;
    private sealed record AddTaskChoice(CatalogSourceVm Source, string Label);
    private TaskCenterPanelViewModel? _addTaskHost;
    private WorkflowEditVm? _addTaskEditing;
    private int _addTaskCategory;

    public BasicTaskCenterView()
    {
        InitializeComponent();
        AddTaskPopup.CustomPopupPlacementCallback = PlaceAddTaskPopup;
        ResourceTaskList.ItemsSource = _resourceTaskRows;
        AddHandler(UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(RouteTaskCenterMouseWheel), handledEventsToo: true);
        ApplyScrollViewerStyles();
        Loaded += (_, _) =>
        {
            ApplyScrollViewerStyles();
            Host?.StartAutoRefresh();
            EnsureNodeSelection();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TaskCenterPanelViewModel oldHost)
            {
                oldHost.PropertyChanged -= OnTaskCenterEditingChanged;
                oldHost.SnapshotRefreshed -= ObserveResourceTasks;
            }
            if (e.NewValue is TaskCenterPanelViewModel newHost)
            {
                newHost.PropertyChanged += OnTaskCenterEditingChanged;
                newHost.SnapshotRefreshed += ObserveResourceTasks;
            }
            CloseTransientEditors();
        };
        Unloaded += (_, _) => { Host?.StopAutoRefresh(); CloseTransientEditors(); };
    }

    private async void OnTaskCenterEditingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskCenterPanelViewModel.Editing) || _resourceTasksBusy
            || !ResourceTasksPopup.IsOpen || !ReferenceEquals(sender, Host)) return;
        if (!TryRebindResourceTaskNode(out _, out var node))
        {
            ResourceTasksPopup.IsOpen = false;
            return;
        }
        if (_resourceTaskLayout is { } layout && node.Model.Ref?.Revision != layout.Revision)
            await ReloadResourceTasksAsync();
    }

    private void CloseTransientEditors()
    {
        ++_popupOpenGeneration;
        AddTaskPopup.IsOpen = false;
        NodeSettingsPopup.IsOpen = false;
        NodeTimeWindowPopup.IsOpen = false;
        PlanSettingsPopup.IsOpen = false;
        ResourceTasksPopup.IsOpen = false;
    }

    private void ApplyScrollViewerStyles()
    {
        if (TryFindResource("GoldScrollViewer") is not Style style) return;
        FlowList.Resources[typeof(ScrollViewer)] = style;
        ResourceList.Resources[typeof(ScrollViewer)] = style;
        AddTaskChoices.Resources[typeof(ScrollViewer)] = style;
        ResourceTaskList.Resources[typeof(ScrollViewer)] = style;
    }

    private void FlowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FlowList.SelectedItem is WorkflowListItemVm item)
        {
            Host?.SelectFlowCommand.Execute(item);
            EnsureNodeSelection();
        }
    }

    private void FlowListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var directItem = e.OriginalSource as ListBoxItem ?? e.Source as ListBoxItem;
        var data = (e.OriginalSource as FrameworkElement)?.DataContext as WorkflowListItemVm
            ?? (directItem?.DataContext as WorkflowListItemVm);
        var item = data is not null
            ? list.ItemContainerGenerator.ContainerFromItem(data) as ListBoxItem
            : directItem
                ?? FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)
                ?? FindVisualParent<ListBoxItem>(list.InputHitTest(Mouse.GetPosition(list)) as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
            if (item.DataContext is WorkflowListItemVm selectedRow)
                list.SelectedItem = selectedRow;
            item.Focus();
        }
    }

    private void FlowListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)
            ?? e.OriginalSource as ListBoxItem
            ?? FindVisualParent<ListBoxItem>(list.InputHitTest(Mouse.GetPosition(list)) as DependencyObject);
        if (item is null && list.SelectedItem is WorkflowListItemVm selected)
            item = list.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem;
        if (item is not null)
        {
            item.IsSelected = true;
            if (item.DataContext is WorkflowListItemVm selectedRow2)
                list.SelectedItem = selectedRow2;
            item.Focus();
            // ContextMenu lives in a separate visual tree. Keep the resolved row on the
            // menu itself so the click handler never has to rediscover it from hit testing.
            if (list.ContextMenu is { } contextMenu)
            {
                contextMenu.Tag = item.DataContext;
                contextMenu.PlacementTarget = item;
            }
        }
    }

    private void FlowListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => ScrollNestedList(sender, e);

    private void ResourceListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => ScrollNestedList(sender, e);

    private void RouteTaskCenterMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var list = FindVisualParent<ListBox>(e.OriginalSource as DependencyObject);
        if (list is null) return;
        ScrollNestedList(list, e);
    }

    private static void ScrollNestedList(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source) return;
        var scrollViewer = FindVisualChild<ScrollViewer>(source);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0) return;

        // Line scrolling respects both item-based ListBoxes and pixel-based panels.
        // Applying pixel-sized offsets to a logical list skips dozens of items at once.
        if (e.Delta > 0) scrollViewer.LineUp();
        else if (e.Delta < 0) scrollViewer.LineDown();
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject? current) where T : DependencyObject
    {
        if (current is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void StartSingleClick(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host) return;
        if (host.Editing?.SelectedNode is not { IsResource: true } node)
        {
            host.ShowInlineActionFeedback("请先点击一行配置单，再启动当前配置单。", isError: true);
            return;
        }
        host.StartSelectedResourceCommand.Execute(node);
    }

    private void StartContinuousClick(object sender, RoutedEventArgs e)
    {
        if (Host is not { } host) return;
        var item = FlowList.SelectedItem as WorkflowListItemVm
            ?? (host.SelectedWorkflowId is { } id
                ? host.Flows.FirstOrDefault(flow => flow.WorkflowId == id)
                : null);
        if (item is null)
        {
            host.ShowInlineActionFeedback("请先从左侧选择一张计划表。", isError: true);
            return;
        }
        host.StartSelectedPlanCommand.Execute(item);
    }

    private void RemoveSelectedNode(object sender, RoutedEventArgs e)
    {
        var node = ContextNode(sender) ?? Host?.Editing?.SelectedNode;
        if (node is null || Host?.Editing is not { } editing) return;
        editing.SelectedNode = node;
        editing.RemoveNodeCommand.Execute(node);
        Host.RefreshCommand.Execute(null);
        Host.ShowInlineActionFeedback($"已移除配置单「{node.DisplayName}」，正在自动保存计划。", isError: false);
    }

    private void SetStartFromNode(object sender, RoutedEventArgs e)
    {
        var node = ContextNode(sender) ?? Host?.Editing?.SelectedNode;
        if (node is null) return;
        Host?.SetStartFromNodeCommand.Execute(node);
    }

    private void ClearStartFromNode(object sender, RoutedEventArgs e)
    {
        Host?.ClearStartFromNodeCommand.Execute(null);
    }

    private void ResourceContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var host = Host;
        var row = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not NodeEditVm node || host?.Editing is not { } editing)
        {
            e.Handled = true;
            return;
        }
        editing.SelectedNode = node;
        if ((sender as ListBox)?.ContextMenu is { } menu)
        {
            menu.Tag = node;
            menu.PlacementTarget = row;
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == "在此任务开始执行") is { } setStart)
                setStart.IsEnabled = !host.IsStartFromNode(node);
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Header as string == "清除在此执行") is { } clearStart)
                clearStart.IsEnabled = host.IsStartFromNode(node);
        }
    }

    private void OpenNodeSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Host?.Editing is not { } editing) return;
        var node = (sender as FrameworkElement)?.Tag as NodeEditVm;
        var target = sender as FrameworkElement;
        if (node is null || target is null) return;
        editing.SelectedNode = node;
        var generation = ++_popupOpenGeneration;
        // Popup.StaysOpen=false 会在外部点击时异步关闭；先清掉旧的 IsOpen/PlacementTarget，
        // 再排到下一轮 Dispatcher 打开，避免第一次关闭后的 Popup 状态吞掉后续点击。
        NodeSettingsPopup.IsOpen = false;
        NodeSettingsPopup.PlacementTarget = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation != _popupOpenGeneration) return;
            NodeSettingsPopup.PlacementTarget = target;
            NodeSettingsPopup.IsOpen = true;
        }));
    }

    private void CloseNodeSettingsClick(object sender, RoutedEventArgs e)
    {
        _popupOpenGeneration++;
        NodeSettingsPopup.IsOpen = false;
        NodeSettingsPopup.PlacementTarget = null;
    }

    private void OpenPlanSettingsClick(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement;
        if (target is null) return;
        var generation = ++_popupOpenGeneration;
        PlanSettingsPopup.IsOpen = false;
        PlanSettingsPopup.PlacementTarget = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation != _popupOpenGeneration) return;
            PlanSettingsPopup.PlacementTarget = target;
            PlanSettingsPopup.IsOpen = true;
        }));
    }

    private void ResourceRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _resourceDragStart = e.GetPosition(null);
        _resourceDragNode = FindNode(e.OriginalSource as DependencyObject);
    }

    private void ResourceRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _resourceDragNode is null || sender is not UIElement list) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _resourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _resourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Reordering during MouseMove replaces ResourceNodes/ItemsSource and interrupts
        // the current mouse gesture. Keep the original row until the final Drop.
        var dragged = _resourceDragNode;
        _resourceDragNode = null;
        try
        {
            DragDrop.DoDragDrop(list, new DataObject(typeof(NodeEditVm), dragged), DragDropEffects.Move);
        }
        finally { _resourceDragNode = null; }
    }

    private void ResourceRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        _resourceDragNode = null;
    }

    private void ResourceRowsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(NodeEditVm)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ResourceRowsDrop(object sender, DragEventArgs e)
    {
        if (Host?.Editing is not { } editing || e.Data.GetData(typeof(NodeEditVm)) is not NodeEditVm source) return;
        if (FindNode(e.OriginalSource as DependencyObject) is not { } target || ReferenceEquals(source, target)) return;
        var sourceIndex = editing.Nodes.IndexOf(source);
        var targetIndex = editing.Nodes.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0) return;
        // Dropping on a row takes that row's position: moving upward inserts before it,
        // moving downward inserts after it. The precise pixel within the row is irrelevant.
        var after = sourceIndex < targetIndex;
        editing.MoveNodeTo(source, target, after);
        editing.SelectedNode = source;
        Host.ShowInlineActionFeedback($"已调整「{source.DisplayName}」顺序，正在自动保存。", isError: false);
        e.Handled = true;
    }

    private static NodeEditVm? FindNode(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is NodeEditVm node) return node;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void DeleteSelectedFlow(object sender, RoutedEventArgs e)
    {
        var item = (sender as MenuItem)?.Parent is ContextMenu menu
            ? menu.Tag as WorkflowListItemVm
                ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as WorkflowListItemVm
            : null;
        item ??= FlowList.SelectedItem as WorkflowListItemVm;
        item ??= Host?.SelectedWorkflowId is { } id
            ? Host.Flows.FirstOrDefault(flow => flow.WorkflowId == id)
            : null;
        if (item is not null) Host?.DeleteFlowCommand.Execute(item);
        else Host?.ShowInlineActionFeedback("请先选择要删除的计划表。", isError: true);
    }

    private void ManageClick(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        var current = FlowList.SelectedItem as WorkflowListItemVm;
        var menu = new ContextMenu();
        menu.Items.Add(Menu("新建计划表", Host.NewFlowCommand));
        menu.Items.Add(Menu("导入计划表…", Host.ImportFlowCommand));
        if (current is not null)
        {
            menu.Items.Add(Menu("导出当前计划表…", Host.ExportFlowCommand, current));
            menu.Items.Add(Menu("重置执行状态…", Host.ResetExecutionStateCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("删除当前计划表…", Host.DeleteFlowCommand, current));
        }

        var restore = new MenuItem { Header = "恢复已删除的计划表" };
        foreach (var receipt in Host.DeletedFlows)
        {
            var item = new MenuItem
            {
                Header = receipt.Name + " · " + receipt.DeletedAt.LocalDateTime.ToString("MM-dd HH:mm"),
            };
            item.Click += (_, _) => Host.RestoreDeletedFlow(receipt.RecoveryId);
            restore.Items.Add(item);
        }
        if (restore.Items.Count == 0) restore.IsEnabled = false;
        menu.Items.Add(restore);
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
    }

    private void AddConfigClick(object sender, RoutedEventArgs e)
    {
        var host = Host;
        if (host is null || host.Editing is not { } editing)
        {
            host?.ShowInlineActionFeedback("请先选择或新建一张计划表，再添加配置单。", isError: true);
            return;
        }
        // Refresh the view-model projection before creating the popup. This is important
        // after the async catalog pull and when an offline plan only has stored resources.
        host.RefreshEditingResourceSources();
        var target = Window.GetWindow(this) as FrameworkElement ?? this;
        var generation = ++_popupOpenGeneration;
        AddTaskPopup.IsOpen = false;
        AddTaskPopup.PlacementTarget = null;
        NodeSettingsPopup.IsOpen = false;
        PlanSettingsPopup.IsOpen = false;
        _addTaskHost = host;
        _addTaskEditing = editing;
        _addTaskCategory = editing.AppendSources.Any(source => source.Kind == TaskCenterResourceKind.ConfigGroup) ? 0
            : editing.AppendSources.Any(source => source.Kind == TaskCenterResourceKind.OneDragonConfig) ? 1
            : editing.AppendSources.Any(source => source.Kind == TaskCenterResourceKind.SingleTask) ? 2 : 0;
        AddTaskSearch.Clear();
        RefreshAddTaskChoices();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation != _popupOpenGeneration || !ReferenceEquals(Host, host) || !ReferenceEquals(host.Editing, editing)) return;
            ApplyScrollViewerStyles();
            AddTaskPopup.PlacementTarget = target;
            AddTaskPopup.IsOpen = true;
        }));
    }

    private CustomPopupPlacement[] PlaceAddTaskPopup(Size popupSize, Size targetSize, Point offset)
    {
        var center = new Point(targetSize.Width / 2, targetSize.Height / 2);
        if (AddTaskPopup.PlacementTarget is FrameworkElement target && Window.GetWindow(target) is { } owner)
        {
            var work = Helpers.DpiAwarenessController.GetLogicalWorkAreaRect(owner) ?? SystemParameters.WorkArea;
            var dpi = VisualTreeHelper.GetDpi(owner);
            var origin = target.PointToScreen(new Point());
            // Popup supplies device-pixel sizes to this callback, even at high DPI.
            center = new Point((work.Left + work.Width / 2) * dpi.DpiScaleX - origin.X,
                (work.Top + work.Height / 2) * dpi.DpiScaleY - origin.Y);
        }
        return [new CustomPopupPlacement(new Point(center.X - popupSize.Width / 2,
            center.Y - popupSize.Height / 2), PopupPrimaryAxis.None)];
    }

    private static readonly TaskCenterResourceKind[] AddTaskKinds =
        [TaskCenterResourceKind.ConfigGroup, TaskCenterResourceKind.OneDragonConfig, TaskCenterResourceKind.SingleTask];
    private static readonly string[] AddTaskKindNames = ["配置组", "整龙", "单项"];

    private void RefreshAddTaskChoices()
    {
        var editing = _addTaskEditing;
        if (editing is null) return;
        var buttons = new[] { AddTaskGroupCategory, AddTaskDragonCategory, AddTaskSingleCategory };
        for (var i = 0; i < buttons.Length; i++)
        {
            var selected = i == _addTaskCategory;
            buttons[i].Content = $"{AddTaskKindNames[i]} ({editing.AppendSources.Count(source => source.Kind == AddTaskKinds[i])})";
            buttons[i].Background = new SolidColorBrush(selected ? Color.FromRgb(232, 201, 109) : Color.FromRgb(53, 48, 82));
            buttons[i].Foreground = new SolidColorBrush(selected ? Color.FromRgb(33, 27, 50) : Color.FromRgb(244, 242, 250));
        }
        var kind = AddTaskKinds[_addTaskCategory];
        var candidates = editing.AppendSources.Where(source => source.Kind == kind)
            .Select(source => new AddTaskChoice(source, kind == TaskCenterResourceKind.SingleTask
                ? $"{source.OwnerConfig} / {source.DisplayName}" : source.DisplayName)).ToArray();
        var filter = AddTaskSearch.Text.Trim();
        var matches = candidates.Where(choice => choice.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        AddTaskChoices.ItemsSource = matches;
        AddTaskEmpty.Text = candidates.Length == 0
            ? kind == TaskCenterResourceKind.SingleTask
                ? "当前没有可添加的单项。连接 BGI 后，会显示已启用且支持单项执行的任务。"
                : $"当前没有可添加的{AddTaskKindNames[_addTaskCategory]}。可连接 BGI 刷新资源目录。"
            : "没有匹配的任务，请修改搜索词。";
        AddTaskEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddTaskFeedback.Visibility = Visibility.Collapsed;
        ConfirmAddTask.IsEnabled = false;
    }

    private void AddTaskCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string index } || !int.TryParse(index, out var category)) return;
        _addTaskCategory = category;
        AddTaskSearch.Clear();
        RefreshAddTaskChoices();
    }

    private void AddTaskSearchChanged(object sender, TextChangedEventArgs e) => RefreshAddTaskChoices();

    private void AddTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ConfirmAddTask.IsEnabled = AddTaskChoices.SelectedItem is AddTaskChoice;

    private void ConfirmAddTaskClick(object sender, RoutedEventArgs e)
    {
        var host = _addTaskHost;
        var editing = _addTaskEditing;
        if (host is null || editing is null || !ReferenceEquals(Host, host) || !ReferenceEquals(host.Editing, editing))
        {
            AddTaskPopup.IsOpen = false;
            Host?.ShowInlineActionFeedback("计划已切换，请重新选择任务。", isError: true);
            return;
        }
        if (AddTaskChoices.SelectedItem is not AddTaskChoice choice) return;
        var selected = editing.AppendSources.FirstOrDefault(source =>
            source.Kind == choice.Source.Kind && source.StableId == choice.Source.StableId);
        if (selected is null)
        {
            RefreshAddTaskChoices();
            AddTaskFeedback.Text = "资源目录已变化，请重新选择任务。";
            AddTaskFeedback.Visibility = Visibility.Visible;
            return;
        }
        var before = editing.Nodes.Count;
        editing.AppendSourceIndex = editing.AppendSources.IndexOf(selected);
        editing.AppendNodeCommand.Execute(null);
        if (editing.Nodes.Count != before + 1)
        {
            AddTaskFeedback.Text = "未能添加任务，请刷新资源目录后重试。";
            AddTaskFeedback.Visibility = Visibility.Visible;
            return;
        }
        AddTaskPopup.IsOpen = false;
        host.ShowInlineActionFeedback($"已添加{AddTaskKindNames[_addTaskCategory]}「{selected.DisplayName}」，正在自动保存计划。", isError: false);
    }

    private void ConnectTaskPickerResourcesClick(object sender, RoutedEventArgs e)
    {
        AddTaskPopup.IsOpen = false;
        _addTaskHost?.ConnectResourcesCommand.Execute(null);
    }

    private void CloseAddTaskPopupClick(object sender, RoutedEventArgs e) => AddTaskPopup.IsOpen = false;

    private void EnsureNodeSelection()
    {
        if (Host?.Editing is { } editing && editing.SelectedNode is not { IsResource: true })
            editing.SelectedNode = editing.Nodes.FirstOrDefault(node => node.IsResource);
    }

    private static NodeEditVm? ContextNode(object sender)
    {
        if ((sender as MenuItem)?.Parent is not ContextMenu menu) return null;
        return menu.Tag as NodeEditVm
            ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as NodeEditVm;
    }

    private static MenuItem Menu(string header, System.Windows.Input.ICommand command, object? parameter = null)
        => new() { Header = header, Command = command, CommandParameter = parameter };

}
