using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalViewportAndChoiceTests
{
    [Fact]
    public Task DeferredViewport_RejectsAWindowOutsideItsCurrentVisualTree() => OnSta((view, _) =>
    {
        var current = new Window { Content = view, Width = 1000, Height = 700, ShowActivated = false };
        var previous = new Window();
        try
        {
            current.Show(); current.UpdateLayout();
            Assert.True(view.IsLoaded);
            typeof(ScheduleListView).GetField("_observedWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, previous);
            var height = ((ScrollViewer)view.FindName("TimelineScroll")).Height;
            typeof(ScheduleListView).GetMethod("UpdateViewport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Assert.Equal(height, ((ScrollViewer)view.FindName("TimelineScroll")).Height);
        }
        finally { current.Close(); }
    });

    [Fact]
    public Task ExpandedMinuteLabels_AreReadableAndLaneControlIsBelowMidnightCards() => OnSta((view, vm) =>
    {
        var draft = vm.Editing!;
        draft.ScheduleNode(draft.Nodes[0], 1260);
        draft.ScheduleNode(draft.Nodes[1], 1295);
        Layout(view);
        var canvas = (Canvas)view.FindName("Timeline");
        var cluster = Assert.Single(canvas.Children.OfType<Button>().Where(b => b.Content is string s && s.StartsWith("▸")));
        cluster.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(view);
        var labels = canvas.Children.OfType<TextBlock>().Where(t => t.Text.StartsWith("21:")).OrderBy(Canvas.GetTop).ToArray();
        Assert.True(labels.Length >= 2);
        Assert.All(labels.Zip(labels.Skip(1)), pair => Assert.True(Canvas.GetTop(pair.Second) - Canvas.GetTop(pair.First) >= 12, "minute labels overlap"));
        draft.ScheduleNode(draft.Nodes[0], 1430); draft.ScheduleNode(draft.Nodes[1], 1435); Layout(view);
        var add = Assert.Single(canvas.Children.OfType<Button>().Where(b => Equals(b.Content, "＋车道")));
        var cards = canvas.Children.OfType<Button>().Where(b => b != add && Canvas.GetTop(b) > 1000).ToArray();
        Assert.All(cards, card => Assert.True(Canvas.GetTop(add) >= Canvas.GetTop(card) + card.ActualHeight + 8, "lane button is covered by a midnight card"));
    });

    [Fact]
    public Task ChoiceLabels_ExposeNodeNamesAndCandidateIdentity_AndDisableCandidateActions() => OnSta((view, vm) =>
    {
        var converter = new ScheduleChoiceLabelConverter();
        var node = vm.Editing!.Nodes[0];
        Assert.Equal(node.DisplayName, converter.Convert(node, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        vm.DiscardEditCommand.Execute(null);
        var candidate = vm.Flows.Single(f => f.IsCandidate);
        Assert.Contains("只读候选", (string)converter.Convert(candidate, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        ((ComboBox)view.FindName("FlowChoice")).SelectedItem = candidate; Layout(view);
        var buttons = Descendants(view).OfType<Button>().ToArray();
        Assert.False(buttons.Single(b => Equals(b.Content, "开始")).IsEnabled);
        Assert.False(buttons.Single(b => Equals(b.Content, "编辑")).IsEnabled);
        Assert.Null(vm.Editing);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Layout(ScheduleListView view)
    { view.Measure(new Size(1080, 700)); view.Arrange(new Rect(0, 0, 1080, 700)); view.UpdateLayout(); }
    private static async Task OnSta(Action<ScheduleListView, TaskCenterPanelViewModel> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var root = Path.Combine(Environment.GetEnvironmentVariable("FORMAL_UI_EVIDENCE") ?? Path.GetTempPath(), "viewport-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "计划", Nodes = [new() { NodeId = "a", Kind = "resource.oneDragonConfig", Ref = new() { Config = "任务A" } }, new() { NodeId = "b", Kind = "resource.oneDragonConfig", Ref = new() { Config = "任务B" } }] }, null);
                host.SaveFlow(new WorkflowDocument { Name = "计划", Activation = new() { Status = "candidate-ready" }, Nodes = [] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.EditFlowCommand.Execute(vm.Flows.Single(f => f.IsActive));
                var view = new ScheduleListView { DataContext = vm, Width = 1080, Height = 700 };
                check(view, vm); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
