using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView : UserControl
{
    private TaskCenterPanelViewModel? Host => DataContext as TaskCenterPanelViewModel;

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

    private void SaveClick(object sender, RoutedEventArgs e) => Host?.SaveFlowCommand.Execute(null);

    private void StartSingleClick(object sender, RoutedEventArgs e)
        => Host?.StartSelectedResourceCommand.Execute(Host.Editing?.SelectedNode);

    private void StartContinuousClick(object sender, RoutedEventArgs e)
    {
        if (FlowList.SelectedItem is WorkflowListItemVm item)
            Host?.StartSelectedPlanCommand.Execute(item);
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

    private void DeleteSelectedFlow(object sender, RoutedEventArgs e)
        => Host?.DeleteFlowCommand.Execute(FlowList.SelectedItem);

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
        if (Host?.Editing is not { } editing) return;
        var menu = new ContextMenu();
        foreach (var source in editing.AppendSources)
        {
            var item = new MenuItem { Header = source.Line, Tag = source };
            item.Click += (_, _) =>
            {
                if (item.Tag is not CatalogSourceVm selected) return;
                editing.AppendSourceIndex = editing.AppendSources.IndexOf(selected);
                editing.AppendNodeCommand.Execute(null);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "资源目录暂无可用配置单", IsEnabled = false });
        }
        menu.PlacementTarget = (Button)sender;
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
