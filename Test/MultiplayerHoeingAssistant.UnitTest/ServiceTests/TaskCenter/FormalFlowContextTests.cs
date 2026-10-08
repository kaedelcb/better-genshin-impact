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
        Button(first, "保存").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
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
            Assert.True(Button(view, "开始").IsEnabled);
            Assert.DoesNotContain(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text.Contains("只读候选"));
            Assert.Contains(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == candidate.ChoiceLabel);
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
            Assert.Contains(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == original.ChoiceLabel);
            Assert.DoesNotContain(Descendants(Choice(view)).OfType<TextBlock>(), text => text.Text == candidate.ChoiceLabel);
        }
        Assert.Empty(host.ListActiveRuns());
    });

    [Fact]
    public Task NormalPreview_UsesTheSelectedFlowAndSharedStartPoint() => OnSta((host, vm, first, second) =>
    {
        var active = vm.Flows.Single(f => f.IsActive);
        InvokePreview(first);
        Assert.Null(vm.Editing);
        Assert.Equal(active.WorkflowId, vm.Previewing!.WorkflowId);
        Assert.Equal(host.LoadFlowSnapshot(active.WorkflowId).Revision, vm.Previewing.Revision);
        var point = Assert.IsType<TaskCenterPanelViewModel.StartPointVm>(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second })
        {
            Layout(view);
            Assert.Same(active, Choice(view).SelectedItem);
            Assert.Same(point, ((ComboBox)view.FindName("StartPointChoice")).SelectedItem);
            Assert.True(StartButton(view).IsEnabled);
        }
        ((ComboBox)second.FindName("StartPointChoice")).SelectedItem = null;
        Assert.Null(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        ((ComboBox)second.FindName("StartPointChoice")).SelectedItem = point;
        foreach (var view in new[] { first, second }) { Layout(view); Assert.Same(point, ((ComboBox)view.FindName("StartPointChoice")).SelectedItem); Assert.True(StartButton(view).IsEnabled); }
        Button(second, "编辑").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.NotNull(vm.Editing);
        Assert.Null(vm.Previewing);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        Assert.Empty(host.ListActiveRuns());
    });

    [Fact]
    public Task NormalPreview_PreservesDirtyAndConflictingDrafts() => OnSta((host, vm, first, second) =>
    {
        var draft = vm.Editing!;
        draft.Nodes[0].ScheduleTimeText = "invalid raw input";
        InvokePreview(first);
        Assert.Same(draft, vm.Editing);
        Assert.Equal("invalid raw input", draft.Nodes[0].ScheduleTimeText);
        Assert.Null(vm.Previewing);
        Assert.True(vm.StatusIsError);
        draft.Nodes[0].ScheduleTimeText = "";
        var snapshot = host.LoadFlowSnapshot(draft.Draft.WorkflowId!);
        snapshot.Document.Name = "external revision";
        host.SaveFlow(snapshot.Document, snapshot.Revision);
        InvokePreview(second);
        Assert.Same(draft, vm.Editing);
        Assert.Null(vm.Previewing);
        Assert.True(vm.StatusIsError);
        foreach (var view in new[] { first, second }) Assert.Equal(draft.Draft.WorkflowId, ((WorkflowListItemVm)Choice(view).SelectedItem).WorkflowId);
    });

    [Fact]
    public Task CandidatePreview_DisablesMissingAndStaleStartPoints() => OnSta((host, vm, first, second) =>
    {
        vm.PreviewFlowCommand.Execute(vm.Flows.Single(f => f.IsActive));
        var old = vm.SelectedStartPoint;
        vm.PreviewFlowCommand.Execute(vm.Flows.Single(f => f.IsCandidate));
        AssertCandidate(vm, first, second);
        Assert.Empty(vm.StartPoints);
        Assert.Null(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        vm.SelectedStartPoint = old;
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        vm.StartFromNodeCommand.Execute(null);
        Assert.Empty(host.ListActiveRuns());
    });

    [Fact]
    public Task Preview_NewPopout_PreservesModeAndSelectedStartPoint() => OnSta((host, vm, first, second) =>
    {
        InvokePreview(first);
        var preview = vm.Previewing;
        var point = vm.SelectedStartPoint;
        Window? popup = null;
        try
        {
            OpenMenu(first, "ViewOptions", "⤢弹出窗口").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            popup = Assert.IsType<Window>(typeof(ScheduleListView).GetField("_popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var view = Assert.IsType<ScheduleListView>(popup.Content);
            Layout(view); Layout(first); Layout(second);
            Assert.Same(preview, vm.Previewing);
            Assert.Null(vm.Editing);
            Assert.Same(point, vm.SelectedStartPoint);
            foreach (var current in new[] { first, second, view })
            {
                Assert.Same(point, ((ComboBox)current.FindName("StartPointChoice")).SelectedItem);
                Assert.True(StartButton(current).IsEnabled);
            }
            Assert.Empty(host.ListActiveRuns());
        }
        finally { popup?.Close(); }
    });

    private static Button StartButton(ScheduleListView view) => Assert.IsType<Button>(view.FindName("StartFromPoint"));
    private static void InvokePreview(ScheduleListView view)
    {
        var button = OpenMenu(view, "PlanManagement", "预览 / 选择运行起点");
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
        button.Command.Execute(button.CommandParameter);
    }

    private static MenuItem OpenMenu(ScheduleListView view,string buttonName,string label)
    {
        var button=Assert.IsType<Button>(view.FindName(buttonName));
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var item=button.ContextMenu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,label));
        button.ContextMenu.IsOpen=false;
        return item;
    }

    private static void AssertCandidate(TaskCenterPanelViewModel vm, params ScheduleListView[] views)
    {
        var candidate = vm.Flows.Single(f => f.IsCandidate);
        Assert.Null(vm.Editing);
        Assert.NotNull(vm.Previewing);
        foreach (var view in views)
        {
            Layout(view);
            Assert.Same(candidate, Choice(view).SelectedItem);
            Assert.False(Button(view, "开始").IsEnabled);
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
