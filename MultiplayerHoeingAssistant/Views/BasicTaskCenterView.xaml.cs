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
        Loaded += (_, _) => Host?.StartAutoRefresh();
        Unloaded += (_, _) => Host?.StopAutoRefresh();
    }

    private void FlowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FlowList.SelectedItem is WorkflowListItemVm item)
            Host?.SelectFlowCommand.Execute(item);
    }

    private void SaveClick(object sender, RoutedEventArgs e) => Host?.SaveFlowCommand.Execute(null);

    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (FlowList.SelectedItem is WorkflowListItemVm item)
            Host?.StartSelectedPlanCommand.Execute(item);
    }

    private void PlanSettingsClick(object sender, RoutedEventArgs e)
    {
        // 计划设置由右侧 SchedulePlanSettingsView 常驻显示；保留按钮作为 BGI 原生布局的明确入口。
        PlanSettingsPanel.BringIntoView();
    }

    private void ManageClick(object sender, RoutedEventArgs e)
    {
        if (Host is null) return;
        var menu = new ContextMenu();
        menu.Items.Add(Menu("新建计划", Host.NewFlowCommand));
        menu.Items.Add(Menu("导入计划…", Host.ImportFlowCommand));
        menu.Items.Add(Menu("导出当前计划…", Host.ExportFlowCommand, FlowList.SelectedItem));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("预览/选择运行起点", Host.PreviewFlowCommand, FlowList.SelectedItem));
        menu.Items.Add(Menu("删除当前计划…", Host.DeleteFlowCommand, FlowList.SelectedItem));
        var restore = new MenuItem { Header = "恢复已删除的计划" };
        foreach (var receipt in Host.DeletedFlows)
        {
            var item = new MenuItem { Header = receipt.Name + " · " + receipt.DeletedAt.LocalDateTime.ToString("MM-dd HH:mm") };
            item.Click += (_, _) => Host.RestoreDeletedFlow(receipt.RecoveryId);
            restore.Items.Add(item);
        }
        if (restore.Items.Count == 0) restore.IsEnabled = false;
        menu.Items.Add(restore);
        menu.IsOpen = true;
    }

    private static MenuItem Menu(string header, System.Windows.Input.ICommand command, object? parameter = null)
        => new() { Header = header, Command = command, CommandParameter = parameter };
}
