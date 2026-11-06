using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("RegisterBankWpf")]
public sealed class WorkflowCompositionUiTests
{
    [Fact]
    public Task FlowEditorAppliesWeekdayThroughRealControlsAndLeavesOriginalUntouched() => OnSta(async () =>
    {
        var wait = StartupStepKinds.Create(StartupStepKinds.Wait);
        var weekday = StartupStepKinds.Create(StartupStepKinds.Weekday);
        weekday.Weekdays = [6, 7];
        var source = new WorkflowFlowDefinition { Name = "周末分流", Steps = [wait, weekday] };
        var window = new TaskFlowEditorWindow(source, [], weekday.Id);
        FormalWpfTheme.Apply(window);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Layout(window);
                Assert.Equal(2, window.StepTreeForTest.Items.Count);
                Button(window, "设为出口判断").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Layout(window);
                var monday = Descendants<CheckBox>(window.ParameterPanelForTest).Single(c => c.Content as string == "周一");
                monday.IsChecked = true;
                Capture(window, "composition-f2-weekday");
                Button(window, "应用到计划").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                completed.SetResult();
            }
            catch (Exception ex) { completed.TrySetException(ex); window.Close(); }
        };
        Assert.True(window.ShowDialog());
        await completed.Task;
        Assert.Null(source.ExitStepId);
        Assert.DoesNotContain(1, source.Steps[1].Weekdays);
        Assert.Equal(weekday.Id, window.Result.ExitStepId);
        Assert.Contains(1, window.Result.Steps[1].Weekdays);
        Assert.Empty(TaskFlowNodeCatalog.Validate(window.Result));
    });

    [Fact]
    public Task FlowEditorCanAddRealRegisterCallbackAndTaskScope() => OnSta(async () =>
    {
        var log = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        log.LogKeyword = "今天执行B线"; log.TriggerScope = "task"; log.TargetNodeId = "a";
        log.WatchRepeat = false;
        var source = new WorkflowFlowDefinition { Name = "准备监听", Steps = [log] };
        var target = new WorkflowNode { NodeId = "a", Kind = "resource.configGroup", Ref = new() { Config = "狗粮收集" } };
        var window = new TaskFlowEditorWindow(source, [target], log.Id);
        FormalWpfTheme.Apply(window);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Layout(window);
                var insertion = Descendants<ComboBox>(window).Single(c => c.Items.Count > 0 && c.Items[0]?.ToString() == "主链");
                insertion.SelectedIndex = 1;
                Button(window, "＋ 设置寄存器").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Layout(window);
                var registers = Descendants<ComboBox>(window.ParameterPanelForTest).Where(c => c.Items.Count == 3 && c.Items[0] as string == "保持").ToArray();
                Assert.Equal(10, registers.Length);
                registers[0].SelectedIndex = 2;
                Capture(window, "composition-f2-log-callback");
                Button(window, "应用到计划").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); window.Close(); }
        };
        Assert.True(window.ShowDialog()); await done.Task;
        var trigger = Assert.Single(window.Result.Steps);
        Assert.Equal("a", trigger.TargetNodeId);
        Assert.Equal(1, Assert.Single(trigger.FireSteps).RegisterWrite!.Values[0]);
        Assert.Empty(source.Steps[0].FireSteps);
    });

    [Fact]
    public Task BasicViewShowsFoldedMixedRowsAndBothLanesThenReopensSavedPlan() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "composition-ui-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument { WorkflowId = "wf-composition-ui", Name = "日常收集",
            Nodes = new[] { "B", "C", "收尾" }.Select(n => new WorkflowNode { NodeId = n, Kind = "resource.configGroup", Ref = new() { Config = n } }).ToList() }, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var view = new BasicTaskCenterView { DataContext = panel };
        var window = new Window { Content = view, Width = 1180, Height = 760, ShowInTaskbar = false, ShowActivated = false,
            Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            var editing = panel.Editing!;
            var condition = StartupStepKinds.Create(StartupStepKinds.Weekday); condition.Weekdays = [6, 7];
            var flow = editing.AddFlow(new() { Name = "本日路线", Steps = [condition], ExitStepId = condition.Id });
            editing.MoveNodeTo(flow, editing.Nodes[0], false);
            var lanes = editing.AddPairedLanes(flow);
            lanes.Model.Lanes!.YesName = "B 线"; lanes.Model.Lanes.NoName = "C 线";
            lanes.Model.Lanes.YesNodeIds.Add("B"); lanes.Model.Lanes.NoNodeIds.Add("C");
            editing.CompositionChanged();
            window.Show(); await Layout(window);
            Assert.Equal(3, panel.PlanRows.Count);
            Assert.Equal(new[] { flow.NodeId, lanes.NodeId, "收尾" }, panel.PlanRows.Select(n => n.NodeId));
            var rows = Descendants<CompositionPlanRow>(view).Where(x => x.IsVisible).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => Assert.False(Assert.Single(Descendants<Expander>(row)).IsExpanded));
            var laneExpander = Assert.Single(Descendants<Expander>(rows.Single(x => ReferenceEquals(x.DataContext, lanes))));
            laneExpander.IsExpanded = true; await Layout(window);
            Assert.Contains(Descendants<TextBlock>(rows[1]), t => t.Text.Contains("是 → B 线"));
            Assert.Contains(Descendants<TextBlock>(rows[1]), t => t.Text.Contains("否 → C 线"));
            Capture(window, "composition-basic-lanes");
            var branchTask = panel.Editing!.Nodes.Single(n => n.NodeId == "B");
            view.OpenLaneTask(branchTask, rows[1]); await Layout(window);
            // Opening the popup can allow an already queued auto-save to reload the draft.
            // Identity is stable; the live editor and popup must use the new B instance.
            editing = panel.Editing!;
            branchTask = editing.Nodes.Single(n => n.NodeId == "B");
            Assert.Equal("B", editing.SelectedNode!.NodeId);
            Assert.Same(branchTask, editing.SelectedNode);
            var popup = (System.Windows.Controls.Primitives.Popup)view.FindName("NodeSettingsPopup");
            var strategies = Assert.Single(Descendants<ScheduleNodeStrategiesView>(popup.Child));
            Assert.Equal("B", Assert.IsType<NodeEditVm>(strategies.DataContext).NodeId);
            Assert.Same(branchTask, strategies.DataContext);
            branchTask.AddWeekdaysCommand.Execute(null); branchTask.W1 = false;
            popup.IsOpen = false;
            lanes = editing.Nodes.Single(n => n.Model.Lanes is not null);
            editing.RenameLanes(lanes, "采集分流", "B 线", "C 线");
            panel.SaveFlowCommand.Execute(null);
            Assert.False(panel.StatusIsError, panel.StatusMessage);
            var saved = host.LoadFlowSnapshot("wf-composition-ui").Document;
            Assert.Empty(WorkflowComposition.Validate(saved));
            var storedLanes = Assert.Single(saved.Nodes.Where(n => n.Lanes is not null)).Lanes!;
            Assert.Equal(new[] { "B" }, storedLanes.YesNodeIds); Assert.Equal(new[] { "C" }, storedLanes.NoNodeIds);
            Assert.DoesNotContain("周一", saved.Nodes.Single(n => n.NodeId == "B").Strategies.Single(s => s.Kind == "condition.weekdays").GetStringArray("days")!);
            Assert.DoesNotContain(saved.Nodes.Single(n => n.NodeId == "C").Strategies, s => s.Kind == "condition.weekdays");
            Assert.Equal("采集分流", panel.PlanRows[1].DisplayName);
            await Layout(window);
            var reopened = Descendants<CompositionPlanRow>(view).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).Model.Lanes is not null);
            Assert.Single(Descendants<Expander>(reopened)).IsExpanded = true;
            await Layout(window);
            Capture(window, "composition-basic-lanes");
        }
        finally { window.Close(); panel.StopAutoRefresh(); await host.ShutdownAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public void CopyGroupRemapsInternalTriggerTargetAndDeleteDoesNotReleaseBranchTasks()
    {
        var trigger = StartupStepKinds.Create(StartupStepKinds.LogTrigger); trigger.TriggerScope = "task"; trigger.TargetNodeId = "B"; trigger.LogKeyword = "test";
        var judge = StartupStepKinds.Create(StartupStepKinds.Weekday);
        var model = new WorkflowDocument { Nodes = [
            new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Steps = [trigger, judge], ExitStepId = judge.Id } },
            new() { NodeId = "lanes", Kind = "control.lanes", Lanes = new() { SourceFlowNodeId = "flow", YesNodeIds = ["B"] } },
            new() { NodeId = "B", Kind = "resource.configGroup", Ref = new() { Config = "B" } }
        ] };
        var catalogPath = Path.Combine(Path.GetTempPath(), "composition-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        var editor = new WorkflowEditVm(model, "r1", new ResourceCatalogService(() => null, catalogPath));
        editor.SelectedNode = editor.Nodes[0]; editor.DuplicateSelectedNodeCommand.Execute(null);
        Assert.Equal(6, editor.Nodes.Count);
        var clonedFlow = editor.SelectedNode!;
        var clonedLane = editor.Nodes.Single(n => n.Model.Lanes?.SourceFlowNodeId == clonedFlow.NodeId).Model.Lanes!;
        Assert.NotEqual("B", Assert.Single(clonedLane.YesNodeIds));
        Assert.Equal(clonedLane.YesNodeIds[0], clonedFlow.Model.Flow!.Steps[0].TargetNodeId);
        Assert.NotEqual(judge.Id, clonedFlow.Model.Flow.ExitStepId);
        editor.RemoveNodeCommand.Execute(clonedFlow);
        Assert.Equal(3, editor.Nodes.Count);
        Assert.Single(editor.MainPlanNodes.Where(n => n.Model.Lanes is not null));
        Assert.DoesNotContain(editor.MainPlanNodes, n => n.NodeId == "B");
    }

    private static Button Button(DependencyObject root, string label) => Descendants<Button>(root).Single(b => b.Content as string == label);
    private static async Task Layout(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var visual = window.Content as FrameworkElement ?? window;
        var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rect = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, rect);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, rect);
        }
        bmp.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(Path.Combine(folder, name + ".png")); png.Save(stream);
    }
    private static Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); } catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
