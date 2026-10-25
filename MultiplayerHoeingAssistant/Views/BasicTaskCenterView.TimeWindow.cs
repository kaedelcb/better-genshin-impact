using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView
{
    private string? _timeWindowWorkflowId;
    private string? _timeWindowNodeId;

    private void OpenNodeTimeWindowClick(object sender, RoutedEventArgs e)
    {
        if (Host?.Editing is not { } editing
            || sender is not FrameworkElement { Tag: NodeEditVm node } target) return;
        CloseTransientEditors();
        editing.SelectedNode = node;
        _timeWindowWorkflowId = editing.Draft.WorkflowId;
        _timeWindowNodeId = node.NodeId;
        NodeTimeWindowTitle.Text = node.DisplayName;
        NodeTimeWindowStartPicker.Value = node.TimeWindowStart;
        NodeTimeWindowEndPicker.Value = node.TimeWindowEnd;
        NodeTimeWindowNotice.Text = editing.NodeTimeWindowEnabled
            ? "跨午夜可用 · 起止相同为全天 · 限制已开启"
            : "跨午夜可用 · 起止相同为全天 · 总开关关闭";
        var generation = _popupOpenGeneration;
        NodeTimeWindowPopup.PlacementTarget = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (generation != _popupOpenGeneration) return;
            NodeTimeWindowPopup.PlacementTarget = target;
            NodeTimeWindowPopup.IsOpen = true;
        }));
    }

    private NodeEditVm? ResolveTimeWindowNode()
    {
        if (Host?.Editing is not { } editing || editing.Draft.WorkflowId != _timeWindowWorkflowId)
            return null;
        return editing.Nodes.FirstOrDefault(node => node.NodeId == _timeWindowNodeId);
    }

    private void ApplyNodeTimeWindowClick(object sender, RoutedEventArgs e)
    {
        var window = new Models.WorkflowTimeWindow
        {
            Start = NodeTimeWindowStartPicker.Value,
            End = NodeTimeWindowEndPicker.Value,
        };
        if (!WorkflowTimeWindowPolicy.TryValidate(window, out var error))
        {
            NodeTimeWindowNotice.Text = error;
            return;
        }
        CommitTimeWindow(node => node.SetTimeWindow(window.Start, window.End));
    }

    private void ClearNodeTimeWindowClick(object sender, RoutedEventArgs e)
        => CommitTimeWindow(node => node.ClearTimeWindow());

    private void CommitTimeWindow(Action<NodeEditVm> apply)
    {
        if (ResolveTimeWindowNode() is not { } node)
        {
            NodeTimeWindowPopup.IsOpen = false;
            Host?.ShowInlineActionFeedback("计划或任务已变化，请重新打开时段设置。", isError: true);
            return;
        }
        apply(node);
        NodeTimeWindowPopup.IsOpen = false;
        Host?.ShowInlineActionFeedback($"已设置「{node.DisplayName}」执行时段：{node.TimeWindowDisplay}，正在自动保存计划。", isError: false);
    }

    private void CloseNodeTimeWindowClick(object sender, RoutedEventArgs e)
    {
        ++_popupOpenGeneration;
        NodeTimeWindowPopup.IsOpen = false;
    }
}
