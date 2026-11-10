using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>任务中心流程图窗口与「执行时展开走过的分支」。</summary>
public partial class BasicTaskCenterView
{
    private TaskCenterFlowChartWindow? _flowChart;
    private TaskCenterPanelViewModel? _flowChartHost;

    /// <summary>DataContext 切换时改挂事件；旧执行端的流程图随之关闭，避免显示另一端的计划。</summary>
    private void BindFlowChartHost(TaskCenterPanelViewModel? host)
    {
        if (ReferenceEquals(_flowChartHost, host)) return;
        if (_flowChartHost is { } old)
        {
            old.FlowChartRequested -= ShowFlowChart;
            old.LaneDecided -= ExpandDecidedLane;
        }
        _flowChart?.Close();
        _flowChartHost = host;
        if (host is null) return;
        host.FlowChartRequested += ShowFlowChart;
        host.LaneDecided += ExpandDecidedLane;
    }

    private void ShowFlowChart()
    {
        if (Host is not { } host) return;
        if (_flowChart is { } open)
        {
            if (!open.IsVisible) open.Show();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        // 不设 Owner：流程图与主窗口各自置前，点击节点跳回计划表时主窗口才能盖过流程图。
        _flowChart = new TaskCenterFlowChartWindow(host, LocateFromFlowChart);
        _flowChart.Closed += (_, _) => { _flowChart = null; SyncLocalRefreshVisibility(); };
        _flowChart.Show();
        SyncLocalRefreshVisibility();
    }

    /// <summary>流程图节点点击：回到计划表选中该行；流程步骤打开流程编辑并选中，车道展开。</summary>
    private void LocateFromFlowChart(string nodeId, string? stepId)
    {
        if (Host?.Editing is not { } editing || editing.Nodes.FirstOrDefault(n => n.NodeId == nodeId) is not { } node) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var win = Window.GetWindow(this) ?? Application.Current.MainWindow;
            if (win is null) return;
            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            var lane = editing.Nodes.FirstOrDefault(n => n.Model.Lanes is { } l && (l.YesNodeIds.Contains(nodeId) || l.NoNodeIds.Contains(nodeId)))
                ?? editing.Nodes.FirstOrDefault(n => n.Model.Lanes?.SourceFlowNodeId == nodeId);
            if (stepId is not null) { LocateCompositionStep(nodeId, stepId); return; }
            if (lane is not null) { RevealCompositionRow(lane.NodeId, expand: true); return; }
            RevealCompositionRow(node.NodeId, expand: node.Model.Lanes is not null);
        }));
    }

    /// <summary>车道在执行中判断出结果：开关打开时展开该车道，显示选中的路线。</summary>
    private void ExpandDecidedLane(string laneNodeId)
    {
        if (Host is not { AutoExpandTakenBranch: true } || Host.Editing?.Nodes.FirstOrDefault(n => n.NodeId == laneNodeId) is not { } lane) return;
        RememberRowExpansion(lane.NodeId, true);
        if (ResourceList.ItemContainerGenerator.ContainerFromItem(lane) is DependencyObject item)
            FindVisualChild<CompositionPlanRow>(item)?.ExpandContents();
    }
}
