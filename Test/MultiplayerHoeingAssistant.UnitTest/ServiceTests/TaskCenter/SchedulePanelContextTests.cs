using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class SchedulePanelContextTests
{
    [Fact]
    public Task RefreshCommand_PreservesPlanSettingsAndSharedSelectedNode() => OnSta((host, vm, first, second) =>
    {
        var draft = vm.Editing!;
        var node = draft.Nodes[0];
        draft.SelectedNode = node;
        Click(first, "计划设置");
        AssertVisible(first, "PlanSettingsPane");
        AssertVisible(second, "InspectorPane");

        vm.RefreshCommand.Execute(null);
        Layout(first); Layout(second);

        Assert.Same(draft, vm.Editing);
        Assert.Same(node, vm.Editing!.SelectedNode);
        AssertVisible(first, "PlanSettingsPane");
        AssertCollapsed(first, "InspectorPane");
        AssertVisible(second, "InspectorPane");
        Assert.Empty(host.Runs.List());
    });

    [Fact]
    public Task RefreshCommand_PreservesCatalogWhileNodeRemainsSelected() => OnSta((host, vm, first, _) =>
    {
        var node = vm.Editing!.Nodes[0];
        vm.Editing.SelectedNode = node;
        Click(first, "资源目录");
        AssertVisible(first, "CatalogPane");

        vm.RefreshCommand.Execute(null);
        Layout(first);

        Assert.Same(node, vm.Editing!.SelectedNode);
        AssertVisible(first, "CatalogPane");
        AssertCollapsed(first, "InspectorPane");
        Assert.Empty(host.Runs.List());
    });

    [Fact]
    public Task NewDraftAndNewHost_UseTheirDefaultSelectionContext() => OnSta((host, vm, first, _) =>
    {
        var previous = vm.Editing!;
        previous.SelectedNode = previous.Nodes[0];
        Click(first, "计划设置");

        vm.EditFlowCommand.Execute(Assert.Single(vm.Flows));
        Layout(first);

        Assert.NotSame(previous, vm.Editing);
        Assert.Null(vm.Editing!.SelectedNode);
        AssertCollapsed(first, "PlanSettingsPane");
        AssertCollapsed(first, "InspectorPane");

        var other = new TaskCenterPanelViewModel(host, autoRefresh: false);
        other.Editing!.SelectedNode = other.Editing.Nodes[0];
        first.DataContext = other;
        Layout(first);

        AssertVisible(first, "InspectorPane");
        AssertCollapsed(first, "PlanSettingsPane");
        Assert.Same(other.Editing.Nodes[0], other.Editing.SelectedNode);
        Assert.Empty(host.Runs.List());
    });

    private static void Click(ScheduleListView view, string label)
        => Children(view).OfType<Button>().Single(button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertVisible(ScheduleListView view, string name)
        => Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName(name)).Visibility);

    private static void AssertCollapsed(ScheduleListView view, string name)
        => Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName(name)).Visibility);

    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }

    private static void Layout(ScheduleListView view)
    {
        view.Measure(new Size(1080, 700));
        view.Arrange(new Rect(0, 0, 1080, 700));
        view.UpdateLayout();
    }

    private static async Task OnSta(Action<TaskCenterHost, TaskCenterPanelViewModel, ScheduleListView, ScheduleListView> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var evidenceRoot = Environment.GetEnvironmentVariable("FORMAL_UI_EVIDENCE");
                if (string.IsNullOrWhiteSpace(evidenceRoot))
                    throw new InvalidOperationException("FORMAL_UI_EVIDENCE必须由受控测试入口指定。");
                var root = Path.Combine(Path.GetFullPath(evidenceRoot), "schedule-panel-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
                    Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "面板上下文", Nodes = [new() { NodeId = "end", Kind = "control.end" }] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var first = new ScheduleListView { DataContext = vm };
                var second = new ScheduleListView { DataContext = vm };
                Layout(first); Layout(second);
                check(host, vm, first, second);
                first.DataContext = null; second.DataContext = null;
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
