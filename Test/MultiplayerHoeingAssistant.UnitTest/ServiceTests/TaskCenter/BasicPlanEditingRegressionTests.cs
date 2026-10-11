using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>Normal editing sequence: display, switch, save and copy current row values.</summary>
public sealed class BasicPlanEditingRegressionTests
{
    private static TaskCenterPanelViewModel Panel(out TaskCenterHost host)
    {
        var root = Path.Combine(Path.GetTempPath(), "basic-edit-regression-" + Guid.NewGuid().ToString("N"));
        host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            WorkflowId = "wf-first", Name = "第一张计划",
            Nodes = [new() { NodeId = "n-first", Kind = "resource.oneDragonConfig", Ref = new() { Config = "配置 A" } }],
        }, null);
        host.SaveFlow(new WorkflowDocument
        {
            WorkflowId = "wf-second", Name = "第二张计划",
            Nodes = [new() { NodeId = "n-second", Kind = "resource.oneDragonConfig", Ref = new() { Config = "配置 B" } }],
        }, null);
        return new TaskCenterPanelViewModel(host, autoRefresh: false);
    }

    [Fact]
    public void DisplayingResourceOrder_DoesNotDirtyPlan_AndAllowsSwitch()
    {
        var panel = Panel(out _);
        Assert.Equal(1, Assert.Single(panel.ResourceNodes).ResourceIndex);
        Assert.False(panel.Editing!.HasUnsavedChanges);
        var other = panel.Flows.Single(f => f.WorkflowId != panel.SelectedWorkflowId);
        panel.SelectFlowCommand.Execute(other);
        Assert.Equal(other.WorkflowId, panel.SelectedWorkflowId);
        Assert.False(panel.StatusIsError);
    }

    [Fact]
    public void Save_ReloadsCurrentPlan_AndKeepsSelectedResourceEditable()
    {
        var panel = Panel(out var host);
        var id = panel.SelectedWorkflowId!;
        var nodeId = panel.Editing!.SelectedNode!.NodeId;
        panel.Editing.NameText = "保存后的计划";
        panel.SaveFlowCommand.Execute(null);
        Assert.False(panel.StatusIsError);
        Assert.NotNull(panel.Editing);
        Assert.Equal(id, panel.SelectedWorkflowId);
        Assert.Equal(nodeId, panel.Editing!.SelectedNode!.NodeId);
        Assert.Single(panel.ResourceNodes);
        Assert.False(panel.Editing.HasUnsavedChanges);
        panel.Editing.NameText = "再次修改";
        panel.SaveFlowCommand.Execute(null);
        Assert.Equal("再次修改", host.LoadFlowSnapshot(id).Document.Name);
    }

    [Fact]
    public void Copy_PreservesPendingUidWeekdaysAndEnabledValue()
    {
        var panel = Panel(out var host);
        var original = panel.Editing!.SelectedNode!;
        original.AddAccountCommand.Execute(null);
        original.AccountUidEdit = "123456789";
        original.AddWeekdaysCommand.Execute(null);
        original.W1 = false;
        original.IncludedInPlan = false;
        panel.Editing.DuplicateSelectedNodeCommand.Execute(null);
        var copy = panel.Editing.SelectedNode!;
        Assert.NotEqual(original.NodeId, copy.NodeId);
        Assert.Equal("123***89", copy.AccountDisplay);
        Assert.False(copy.W1);
        Assert.False(copy.IncludedInPlan);
        panel.SaveFlowCommand.Execute(null);
        var nodes = host.LoadFlowSnapshot(panel.SelectedWorkflowId!).Document.Nodes;
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node => Assert.Equal("123456789",
            node.Strategies.Single(s => s.Kind == "prerequisite.account").GetString("uid")));
        Assert.Equal(nodes[0].Strategies.Single(s => s.Kind == "condition.weekdays").GetStringArray("days"),
            nodes[1].Strategies.Single(s => s.Kind == "condition.weekdays").GetStringArray("days"));
    }
}
