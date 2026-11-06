using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView
{
    internal TaskCenterPanelViewModel? CompositionHost => Host;
    // Disclosure is transient view state, scoped to the selected executor and plan.
    private TaskCenterPanelViewModel? _expansionHost;
    private string? _expansionPlan;
    private readonly HashSet<string> _expandedRows = new(StringComparer.Ordinal);
    private HashSet<string> CurrentExpandedRows()
    {
        var plan = Host?.Editing?.Draft.WorkflowId;
        if (!ReferenceEquals(Host, _expansionHost) || _expansionPlan != plan)
        { _expandedRows.Clear(); _expansionHost = Host; _expansionPlan = plan; }
        return _expandedRows;
    }
    internal bool IsRowExpanded(string id) => CurrentExpandedRows().Contains(id);
    internal void RememberRowExpansion(string id, bool expanded)
    {
        var rows = CurrentExpandedRows();
        if (expanded) rows.Add(id); else rows.Remove(id);
    }

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
        ShowLaneEditor(null);
    }

    private void ShowLaneEditor(NodeEditVm? source)
    {
        var owner = Host;
        if (owner?.Editing is not { } initial) return;
        var workflowId = initial.Draft.WorkflowId;
        var nodeId = source?.NodeId;
        var originalLane = source is null ? null : JsonSerializer.Serialize(source.Model.Lanes);
        var decision = source is null ? null : WorkflowComposition.GetDecision(initial.Draft, source.Model);
        var originalDecision = decision is null ? null : JsonSerializer.Serialize(decision);
        var lanes = source?.Model.Lanes ?? new WorkflowLanesDefinition { Name = "选择路线", YesName = "路线 1", NoName = "路线 2" };
        var window = new LanesEditorWindow(lanes, decision, initial.Draft.Nodes, owner.AllowedTaskPrograms) { Owner = Window.GetWindow(this) };
        window.ApplyToPlan = result =>
        {
            if (!ReferenceEquals(Host, owner) || owner.Editing is not { } editing || editing.Draft.WorkflowId != workflowId)
                return "当前计划已切换，车道草稿仍保留在此窗口。";
            NodeEditVm node;
            if (nodeId is null) node = editing.AddStandaloneLanes(result);
            else
            {
                var current = editing.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
                if (current?.Model.Lanes is null || JsonSerializer.Serialize(current.Model.Lanes) != originalLane
                    || JsonSerializer.Serialize(WorkflowComposition.GetDecision(editing.Draft, current.Model)) != originalDecision)
                    return "这条车道或它的条件已被修改，未覆盖新内容；你的草稿仍保留在窗口中。";
                editing.ReplaceLaneDecision(current, result); node = current;
            }
            RevealCompositionRow(node.NodeId);
            owner.ShowInlineActionFeedback("车道已保存。在条件满足和其他情况两条路线中分别添加任务即可。", false);
            return null;
        };
        window.ShowDialog();
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
            RevealCompositionRow((editing.Nodes.FirstOrDefault(n => n.Model.Lanes?.SourceFlowNodeId == node.NodeId) ?? node).NodeId);
            owner.ShowInlineActionFeedback("流程已保存，按顺序执行这些步骤。需要分两路时请单独添加车道。", false);
            return null;
        };
        window.ShowDialog();
    }

    private void RevealCompositionRow(string nodeId, bool expand = false)
    {
        var host = Host;
        var plan = host?.Editing?.Draft.WorkflowId;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(Host, host) || host?.Editing is not { } editing || editing.Draft.WorkflowId != plan
                || editing.Nodes.FirstOrDefault(n => n.NodeId == nodeId) is not { } node) return;
            editing.SelectedNode = node;
            ResourceList.ScrollIntoView(node); ResourceList.UpdateLayout();
            if (expand && ResourceList.ItemContainerGenerator.ContainerFromItem(node) is DependencyObject item)
                FindVisualChild<CompositionPlanRow>(item)?.ExpandContents();
        }));
    }

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
        var lane = node.Model.Lanes is not null ? node : Host.Editing.Nodes.FirstOrDefault(n => n.Model.Lanes?.SourceFlowNodeId == nodeId);
        if (lane is not null) { RevealCompositionRow(lane.NodeId, expand: true); ShowLaneEditor(lane); return; }
        ResourceList.ScrollIntoView(node);
        if (node.Model.Flow is not null) OpenFlowEditor(node, stepId);
    }

    internal void EditLanes(NodeEditVm node) => ShowLaneEditor(node);
}
