using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView : UserControl
{
    private TaskCenterPanelViewModel? Host => DataContext as TaskCenterPanelViewModel;
    private Point _resourceDragStart;
    private NodeEditVm? _resourceDragNode;
    private NodeEditVm? _lastResourceDropTarget;
    private bool _resourceDragging;
    private bool _lastResourceDropAfter;
    private ContextMenu? _activeContextMenu;
    internal ContextMenu? ActiveContextMenuForTest => _activeContextMenu;

    public BasicTaskCenterView()
    {
        InitializeComponent();
        ApplyScrollViewerStyles();
        Loaded += (_, _) =>
        {
            ApplyScrollViewerStyles();
            Host?.StartAutoRefresh();
            EnsureNodeSelection();
        };
        Unloaded += (_, _) => Host?.StopAutoRefresh();
    }

    private void ApplyScrollViewerStyles()
    {
        if (TryFindResource("GoldScrollViewer") is not Style style) return;
        FlowList.Resources[typeof(ScrollViewer)] = style;
        ResourceList.Resources[typeof(ScrollViewer)] = style;
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

    private static void ScrollNestedList(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;
        var scrollViewer = FindVisualChild<ScrollViewer>(source);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0) return;

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
        Host.ShowInlineActionFeedback($"已移除配置单「{node.DisplayName}」，计划已自动保存。", isError: false);
    }

    private void ResourceContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not NodeEditVm node || Host?.Editing is not { } editing)
            return;
        editing.SelectedNode = node;
        if (row.ContextMenu is { } menu)
        {
            menu.Tag = node;
            menu.PlacementTarget = row;
        }
    }

    private void OpenNodeSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Host?.Editing is not { } editing) return;
        var node = (sender as FrameworkElement)?.Tag as NodeEditVm;
        if (node is null) return;
        editing.SelectedNode = node;
        NodeSettingsPopup.PlacementTarget = sender as FrameworkElement;
        NodeSettingsPopup.IsOpen = true;
    }

    private void CloseNodeSettingsClick(object sender, RoutedEventArgs e)
        => NodeSettingsPopup.IsOpen = false;

    private void OpenPlanSettingsClick(object sender, RoutedEventArgs e)
    {
        PlanSettingsPopup.PlacementTarget = sender as FrameworkElement;
        PlanSettingsPopup.IsOpen = true;
    }

    private void ResourceRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _resourceDragStart = e.GetPosition(null);
        _resourceDragNode = FindNode(e.OriginalSource as DependencyObject);
        _resourceDragging = false;
        _lastResourceDropTarget = null;
    }

    private void ResourceRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _resourceDragNode is null) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _resourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _resourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _resourceDragging = true;
        if (sender is UIElement list) list.CaptureMouse();
        Host?.ShowInlineActionFeedback("正在拖动配置单…", isError: false);

        var over = sender is UIElement listElement
            ? listElement.InputHitTest(e.GetPosition(listElement)) as DependencyObject
            : null;
        over ??= Mouse.DirectlyOver as DependencyObject ?? e.OriginalSource as DependencyObject;
        if (FindNode(over) is not { } target || ReferenceEquals(target, _resourceDragNode)) return;
        if (FindRowElement(over) is not { } row) return;
        var after = Mouse.GetPosition(row).Y >= row.ActualHeight / 2;
        if (ReferenceEquals(_lastResourceDropTarget, target) && _lastResourceDropAfter == after) return;

        if (Host?.Editing is { } editing)
        {
            editing.MoveNodeTo(_resourceDragNode, target, after);
            editing.SelectedNode = _resourceDragNode;
            _lastResourceDropTarget = target;
            _lastResourceDropAfter = after;
            Host.ShowInlineActionFeedback($"已调整「{_resourceDragNode.DisplayName}」顺序，正在自动保存。", isError: false);
        }
    }

    private void ResourceRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resourceDragging && sender is UIElement list && list.IsMouseCaptured)
            list.ReleaseMouseCapture();
        _resourceDragNode = null;
        _lastResourceDropTarget = null;
        _resourceDragging = false;
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
        if (FindRowElement(e.OriginalSource as DependencyObject) is not { } targetElement) return;
        var after = e.GetPosition(targetElement).Y >= targetElement.ActualHeight / 2;
        editing.MoveNodeTo(source, target, after);
        editing.SelectedNode = source;
        Host.RefreshCommand.Execute(null);
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

    private static FrameworkElement? FindRowElement(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is FrameworkElement element && element.Tag is NodeEditVm) return element;
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
        var menu = new ContextMenu();
        foreach (var source in editing.AppendSources)
        {
            var item = new MenuItem { Header = source.Line, Tag = source };
            item.Click += (_, _) =>
            {
                if (item.Tag is not CatalogSourceVm selected) return;
                editing.AppendSourceIndex = editing.AppendSources.IndexOf(selected);
                editing.AppendNodeCommand.Execute(null);
                host.ShowInlineActionFeedback($"已添加配置单「{selected.DisplayName}」，计划已自动保存。", isError: false);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            var connect = new MenuItem { Header = "资源目录暂无可用配置单：连接 BGI" };
            connect.Click += (_, _) => host.ConnectResourcesCommand.Execute(null);
            menu.Items.Add(connect);
            host.ShowInlineActionFeedback("当前没有可追加的 resource 配置单；可先连接 BGI 读取配置目录。", isError: true);
        }
        menu.PlacementTarget = (Button)sender;
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu)) _activeContextMenu = null;
        };
        menu.IsOpen = true;
    }

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
