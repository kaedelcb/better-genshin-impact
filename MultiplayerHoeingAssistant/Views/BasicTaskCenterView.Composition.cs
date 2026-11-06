using System.Windows;
using System.Windows.Controls;
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
        if (Host?.Editing is not { } editing) return;
        var window = new TaskFlowEditorWindow(new WorkflowFlowDefinition(), editing.Draft.Nodes, allowedPrograms: Host.AllowedTaskPrograms)
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || !ReferenceEquals(Host?.Editing, editing)) return;
        var node = editing.AddFlow(window.Result);
        if (window.Result.ExitStepId is not null) editing.AddPairedLanes(node);
        Host.ShowInlineActionFeedback("已添加流程，修改会保存到当前计划。", false);
    }

    private void AddLanesClick(object sender, RoutedEventArgs e)
    {
        AddTaskPopup.IsOpen = false;
        if (Host?.Editing is not { } editing) return;
        var selected = editing.SelectedNode;
        var flow = selected?.Model.Flow is not null ? selected : editing.MainPlanNodes.LastOrDefault(n => n.Model.Flow?.ExitStepId is not null
            && !editing.Nodes.Any(l => l.Model.Lanes?.SourceFlowNodeId == n.NodeId));
        if (flow is null)
        { Host.ShowInlineActionFeedback("先添加流程，在其中选择出口判断；会自动生成配对的两条车道。", true); return; }
        try { editing.AddPairedLanes(flow); Host.ShowInlineActionFeedback("已添加配对车道。", false); }
        catch (Exception ex) { Host.ShowInlineActionFeedback(ex.Message, true); }
    }

    internal void OpenFlowEditor(NodeEditVm node, string? stepId = null)
    {
        if (Host?.Editing is not { } editing || node.Model.Flow is not { } flow || !editing.Nodes.Contains(node)) return;
        var window = new TaskFlowEditorWindow(flow, editing.Draft.Nodes, stepId, Host.AllowedTaskPrograms) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || !ReferenceEquals(Host?.Editing, editing)) return;
        try
        {
            editing.ReplaceFlow(node, window.Result);
            if (window.Result.ExitStepId is not null && !editing.Nodes.Any(n => n.Model.Lanes?.SourceFlowNodeId == node.NodeId))
                editing.AddPairedLanes(node);
            Host.ShowInlineActionFeedback("流程已应用，正在保存计划。", false);
        }
        catch (Exception ex) { Host.ShowInlineActionFeedback(ex.Message, true); }
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
        ResourceList.ScrollIntoView(node);
        if (node.Model.Flow is not null) OpenFlowEditor(node, stepId);
    }

    internal void EditLanes(NodeEditVm node)
    {
        if (Host?.Editing is not { } editing || node.Model.Lanes is not { } lanes) return;
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
        var apply = new Button { Content = "应用", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 3, 12, 3) };
        apply.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(yes.Text) && !string.IsNullOrWhiteSpace(no.Text)) dialog.DialogResult = true; };
        panel.Children.Add(apply); dialog.Content = panel;
        if (dialog.ShowDialog() == true && ReferenceEquals(Host?.Editing, editing))
            editing.RenameLanes(node, name.Text, yes.Text, no.Text);
    }
}
