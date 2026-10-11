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
    private ContextMenu? _activeContextMenu;
    internal ContextMenu? ActiveContextMenuForTest => _activeContextMenu;

    public BasicTaskCenterView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Host?.StartAutoRefresh();
            EnsureNodeSelection();
        };
        Unloaded += (_, _) => Host?.StopAutoRefresh();
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
            item.Focus();
            if (list.ContextMenu is { } contextMenu)
                contextMenu.PlacementTarget = item;
        }
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

    private void SaveClick(object sender, RoutedEventArgs e) => Host?.SaveFlowCommand.Execute(null);

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
        => _resourceDragStart = e.GetPosition(null);

    private void ResourceRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not DependencyObject source) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _resourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _resourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (FindNode(e.OriginalSource as DependencyObject) is not { } node) return;
        DragDrop.DoDragDrop(source, node, DragDropEffects.Move);
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
            ? (menu.PlacementTarget as FrameworkElement)?.DataContext as WorkflowListItemVm
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
        var menu = new ContextMenu();
        foreach (var source in editing.AppendSources)
        {
            var item = new MenuItem { Header = source.Line, Tag = source };
            item.Click += (_, _) =>
            {
                if (item.Tag is not CatalogSourceVm selected) return;
                editing.AppendSourceIndex = editing.AppendSources.IndexOf(selected);
                editing.AppendNodeCommand.Execute(null);
                host.ShowInlineActionFeedback($"已添加配置单「{selected.DisplayName}」，请点击“保存”使计划生效。", isError: false);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            var connect = new MenuItem { Header = "资源目录暂无配置单：连接 BGI" };
            connect.Click += (_, _) => host.ConnectResourcesCommand.Execute(null);
            menu.Items.Add(connect);
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
        => (sender as MenuItem)?.Parent is ContextMenu menu
            && menu.PlacementTarget is FrameworkElement target
            ? target.DataContext as NodeEditVm
            : null;

    private static MenuItem Menu(string header, System.Windows.Input.ICommand command, object? parameter = null)
        => new() { Header = header, Command = command, CommandParameter = parameter };

}
