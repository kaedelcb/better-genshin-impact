using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalFlowContextTests
{
    [Fact]
    public Task SavedDraft_CanSelectSameNameCandidate_InBothViews() => OnSta((host, vm, first, second) =>
    {
        var draft = vm.Editing!;
        draft.NameText = "同名计划";
        Button(first, "保存新修订").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.False(vm.StatusIsError);
        Choice(first).SelectedItem = vm.Flows.Single(f => f.IsCandidate);
        AssertCandidate(vm, first, second);
        Assert.Equal("同名计划", host.LoadFlowSnapshot(draft.Draft.WorkflowId!).Document.Name);
    });

    [Fact]
    public Task UnchangedDraft_CanSwitchInBothViews() => OnSta((host, vm, first, second) =>
    {
        var active = vm.Flows.Single(f => f.IsActive);
        Choice(first).SelectedItem = vm.Flows.Single(f => f.IsCandidate);
        AssertCandidate(vm, first, second);
        Choice(second).SelectedItem = active;
        Assert.NotNull(vm.Editing);
        Assert.Same(active, Choice(first).SelectedItem);
        Assert.Null(vm.Previewing);
    });

    [Fact]
    public Task DirtyDraft_InvalidRawInput_RetainsBothContexts() => OnSta((host, vm, first, second) =>
    {
        var active = vm.Flows.Single(f => f.IsActive);
        var draft = vm.Editing!;
        draft.Nodes[0].ScheduleTimeText = "invalid raw input";
        Choice(first).SelectedItem = vm.Flows.Single(f => f.IsCandidate);
        Assert.Same(draft, vm.Editing);
        Assert.Equal("invalid raw input", draft.Nodes[0].ScheduleTimeText);
        Assert.Same(active, Choice(first).SelectedItem);
        Assert.Same(active, Choice(second).SelectedItem);
        Assert.Null(vm.Previewing);
        Assert.True(vm.StatusIsError);
    });

    [Fact]
    public Task RevisionConflict_EvenWithUnchangedDraft_CannotBeDiscardedBySelection() => OnSta((host, vm, first, second) =>
    {
        var draft = vm.Editing!;
        var snapshot = host.LoadFlowSnapshot(draft.Draft.WorkflowId!);
        snapshot.Document.Name = "external revision";
        host.SaveFlow(snapshot.Document, snapshot.Revision);
        Choice(first).SelectedItem = vm.Flows.Single(f => f.IsCandidate);
        Assert.Same(draft, vm.Editing);
        Assert.Null(vm.Previewing);
        Assert.Equal(draft.Draft.WorkflowId, ((WorkflowListItemVm)Choice(first).SelectedItem).WorkflowId);
        Assert.Equal(draft.Draft.WorkflowId, ((WorkflowListItemVm)Choice(second).SelectedItem).WorkflowId);
        Assert.True(vm.StatusIsError);
    });

    [Fact]
    public Task ImportedCandidate_RealActivation_RefreshesPreviewAndRenderedLabel() => OnSta((host, vm, first, second) =>
    {
        vm.DiscardEditCommand.Execute(null);
        var candidate = vm.Flows.Single(f => f.IsCandidate);
        Choice(first).SelectedItem = candidate;
        Layout(first); Layout(second);
        var oldRevision = host.LoadFlowSnapshot(candidate.WorkflowId).Revision;
        var result = host.ActivateMigrationCandidateAsync(candidate.WorkflowId).GetAwaiter().GetResult();
        Assert.True(result.Ok, result.Message);
        vm.Refresh(); Layout(first); Layout(second);
        var revision = host.LoadFlowSnapshot(candidate.WorkflowId).Revision;
        Assert.NotEqual(oldRevision, revision);
        Assert.True(candidate.CanStart && candidate.CanEdit);
        Assert.Null(vm.Previewing!.Notice);
        Assert.Contains(revision[..8], vm.Previewing.Lines[0]);
        foreach (var view in new[] { first, second })
        {
            Assert.Same(candidate, Choice(view).SelectedItem);
            Assert.True(Button(view, "启动").IsEnabled);
            Assert.DoesNotContain(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text.Contains("只读候选"));
            Assert.Contains(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == candidate.WorkflowId);
            var hint = Assert.IsType<string>(Choice(view).ToolTip);
            Assert.Contains(candidate.WorkflowId, hint);
            Assert.Contains(revision, hint);
        }
        var original = vm.Flows.Single(f => f.WorkflowId != candidate.WorkflowId);
        Assert.Equal(original.Name, candidate.Name);
        Choice(second).SelectedItem = original;
        foreach (var view in new[] { first, second })
        {
            Layout(view);
            Assert.Same(original, Choice(view).SelectedItem);
            Assert.Equal(original.WorkflowId, vm.Editing!.Draft.WorkflowId);
            Assert.Contains(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == original.WorkflowId);
            Assert.DoesNotContain(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == candidate.WorkflowId);
        }
        Assert.Empty(host.ListActiveRuns());
    });

    private static void AssertCandidate(TaskCenterPanelViewModel vm, params ScheduleListView[] views)
    {
        var candidate = vm.Flows.Single(f => f.IsCandidate);
        Assert.Null(vm.Editing);
        Assert.NotNull(vm.Previewing);
        foreach (var view in views)
        {
            Layout(view);
            Assert.Same(candidate, Choice(view).SelectedItem);
            Assert.False(Button(view, "启动").IsEnabled);
            Assert.False(Button(view, "编辑").IsEnabled);
        }
    }
    private static ComboBox Choice(ScheduleListView view) => (ComboBox)view.FindName("FlowChoice");
    private static Button Button(ScheduleListView view, string content) => Descendants(view).OfType<Button>().Single(b => Equals(b.Content, content));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
    private static void Layout(ScheduleListView view)
    { view.Measure(new Size(1080, 700)); view.Arrange(new Rect(0, 0, 1080, 700)); view.UpdateLayout(); }
    private static async Task OnSta(Action<TaskCenterHost, TaskCenterPanelViewModel, ScheduleListView, ScheduleListView> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var root = Path.Combine(Environment.GetEnvironmentVariable("FORMAL_UI_EVIDENCE")!, "context-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "同名计划", Nodes = [new() { NodeId = "a", Kind = "control.end" }] }, null);
                host.SaveFlow(new WorkflowDocument { Name = "同名计划", Activation = new() { Status = "candidate-ready" }, ExtensionData = new() { ["importedSchedule"] = JsonSerializer.SerializeToElement(true) }, Nodes = [new() { NodeId = "b", Kind = "control.end" }] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.EditFlowCommand.Execute(vm.Flows.Single(f => f.IsActive));
                var first = new ScheduleListView { DataContext = vm };
                var second = new ScheduleListView { DataContext = vm };
                Layout(first); Layout(second);
                check(host, vm, first, second); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
