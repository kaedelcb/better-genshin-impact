using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView
{
    internal TaskCenterPanelViewModel? CompositionHost => Host;
    private NodeEditVm? _addingLane;
    private bool _addingLaneYes;

    private void AddFlowClick(object sender, RoutedEventArgs e)
    {
        AddTaskPopup.IsOpen = false;
        ShowFlowEditor(null);
    }

    private void AddLanesClick(object sender, RoutedEventArgs e)
    {
        AddTaskPopup.IsOpen = false;
        if (Host?.Editing is not { } editing) return;
        var selected = editing.SelectedNode;
        var existing = selected?.Model.Lanes is not null ? selected
            : editing.Nodes.FirstOrDefault(n => n.Model.Lanes is { } paired && paired.SourceFlowNodeId == selected?.NodeId);
        if (existing is not null)
        {
            ExpandLaneRow(existing.NodeId);
            Host.ShowInlineActionFeedback("这个流程已有车道，已展开；可在是 / 否车道中添加任务。", false);
            return;
        }
        var flow = selected?.Model.Flow is not null ? selected : editing.MainPlanNodes.LastOrDefault(n => n.Model.Flow?.ExitStepId is not null
            && !editing.Nodes.Any(l => l.Model.Lanes?.SourceFlowNodeId == n.NodeId));
        if (string.IsNullOrWhiteSpace(flow?.Model.Flow?.ExitStepId))
        { ShowFlowEditor(flow, requireExit: true); return; }
        try
        {
            var lanes = editing.AddPairedLanes(flow);
            ExpandLaneRow(lanes.NodeId);
            Host.ShowInlineActionFeedback("已添加配对车道，可在两条车道中分别添加任务。", false);
        }
        catch (Exception ex) { Host.ShowInlineActionFeedback(ex.Message, true); }
    }

    internal void OpenFlowEditor(NodeEditVm node, string? stepId = null)
        => ShowFlowEditor(node, selectedStepId: stepId);

    private void ShowFlowEditor(NodeEditVm? source, bool requireExit = false, string? selectedStepId = null)
    {
        var owner = Host;
        if (owner?.Editing is not { } initial) return;
        if (source is not null && (source.Model.Flow is null || !initial.Nodes.Contains(source))) return;
        var workflowId = initial.Draft.WorkflowId;
        var sourceId = source?.NodeId;
        var original = source is null ? null : JsonSerializer.Serialize(source.Model.Flow);
        var flow = source?.Model.Flow ?? new WorkflowFlowDefinition { Name = requireExit ? "车道判断" : "流程" };
        var window = new TaskFlowEditorWindow(flow, initial.Draft.Nodes, selectedStepId, owner.AllowedTaskPrograms, requireExit)
        { Owner = Window.GetWindow(this) };
        window.ApplyToPlan = result =>
        {
            // 模态窗口仍会运行自动保存和刷新。按计划/节点身份重绑，不能用旧 VM 的引用判断丢掉结果。
            if (!ReferenceEquals(Host, owner) || owner.Editing is not { } editing || editing.Draft.WorkflowId != workflowId)
                return "当前计划已切换，编辑内容仍保留在此窗口；请返回原计划后再应用。";
            NodeEditVm node;
            if (sourceId is not null)
            {
                var current = editing.Nodes.FirstOrDefault(n => n.NodeId == sourceId);
                if (current?.Model.Flow is null || JsonSerializer.Serialize(current.Model.Flow) != original)
                    return "原流程已被修改或删除，未覆盖新内容。此窗口保留你的草稿，请核对后再处理。";
                editing.ReplaceFlow(current, result); node = current;
            }
            else node = editing.AddFlow(result);
            var lanes = editing.Nodes.FirstOrDefault(n => n.Model.Lanes?.SourceFlowNodeId == node.NodeId);
            if (result.ExitStepId is not null && lanes is null) lanes = editing.AddPairedLanes(node);
            if (lanes is not null) ExpandLaneRow(lanes.NodeId);
            owner.ShowInlineActionFeedback(lanes is null ? "流程已应用，正在保存计划。" : "流程和两条车道已应用，可分别添加任务。", false);
            return null;
        };
        window.ShowDialog();
    }

    private void ExpandLaneRow(string nodeId) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (Host?.Editing?.Nodes.FirstOrDefault(n => n.NodeId == nodeId) is not { } node) return;
        Host.Editing.SelectedNode = node;
        ResourceList.ScrollIntoView(node); ResourceList.UpdateLayout();
        if (ResourceList.ItemContainerGenerator.ContainerFromItem(node) is DependencyObject item)
            FindVisualChild<CompositionPlanRow>(item)?.ExpandContents();
    }));

    internal void AddLaneTask(NodeEditVm node, bool yes)
    {
        if (node.Model.Lanes is null) return;
        AddConfigClick(this, new RoutedEventArgs());
        _addingLane = node; _addingLaneYes = yes;
    }

    internal void OpenLaneTask(NodeEditVm node, FrameworkElement anchor)
    {
        if (Host?.Editing is not { } editing) return;
        node = editing.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId) ?? node;
        if (!editing.Nodes.Contains(node)) return;
        anchor.Tag = node;
        editing.SelectedNode = node;
        // Reuse the ordinary task's complete date/account/failure controls.
        OpenNodeSettingsClick(anchor, new RoutedEventArgs());
    }

    internal void OpenLaneResourceTasks(NodeEditVm node, FrameworkElement anchor)
    {
        anchor.Tag = node;
        OpenResourceTasksClick(anchor, new RoutedEventArgs());
    }

    private void LocateCompositionStep(string nodeId, string? stepId)
    {
        if (Host?.Editing?.Nodes.FirstOrDefault(n => n.NodeId == nodeId) is not { } node) return;
        ResourceList.ScrollIntoView(node);
        if (node.Model.Flow is not null) OpenFlowEditor(node, stepId);
    }

    internal void EditLanes(NodeEditVm node)
    {
        var owner = Host;
        if (owner?.Editing is not { } editing || node.Model.Lanes is not { } lanes) return;
        var workflowId = editing.Draft.WorkflowId;
        var original = JsonSerializer.Serialize(lanes);
        var dialog = new Window { Title = "车道名称", Width = 410, Height = 265, Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.MidnightBlue, Foreground = System.Windows.Media.Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(18) };
        TextBox Field(string title, string value)
        {
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 3, 0, 3) });
            var box = new TextBox { Text = value }; panel.Children.Add(box); return box;
        }
        var name = Field("车道行名称", lanes.Name);
        var yes = Field("是出口的车道名称", lanes.YesName);
        var no = Field("否出口的车道名称", lanes.NoName);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
        var apply = new Button { Content = "应用", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 3, 12, 3) };
        apply.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(yes.Text) || string.IsNullOrWhiteSpace(no.Text))
            { error.Text = "请填写车道行和两个出口的名称。"; return; }
            if (!ReferenceEquals(Host, owner) || owner.Editing is not { } current || current.Draft.WorkflowId != workflowId)
            { error.Text = "当前计划已切换，窗口中的名称尚未应用。"; return; }
            var target = current.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId);
            if (target?.Model.Lanes is null || JsonSerializer.Serialize(target.Model.Lanes) != original)
            { error.Text = "原车道已被修改或删除，未覆盖新内容。"; return; }
            current.RenameLanes(target, name.Text, yes.Text, no.Text);
            dialog.DialogResult = true;
        };
        panel.Children.Add(error); panel.Children.Add(apply); dialog.Content = panel;
        dialog.ShowDialog();
    }
}
