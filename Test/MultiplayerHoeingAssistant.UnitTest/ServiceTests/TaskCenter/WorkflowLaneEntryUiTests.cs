using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public sealed class WorkflowLaneEntryUiTests
{
    [Fact]
    public Task AddLanesButtonOpensEditorRequiresExitAndSavesExactlyOnePair() => OnSta(() => WithView(null, async f =>
    {
        await OpenPicker(f);
        await InEditor(f, () => Click(PickerButton(f, "＋ 车道")), async editor =>
        {
            Assert.Empty(editor.Result.Steps);
            // A valid ordinary action still cannot satisfy the lane entry without an exit.
            Click(Button(editor, "＋ 等待"));
            Click(Button(editor, "应用到计划"));
            Assert.True(editor.IsVisible);
            Assert.Null(editor.Result.ExitStepId);
            Assert.DoesNotContain(f.Panel.Editing!.Nodes, n => n.IsComposition);
            AddWeekdayExit(editor);
            Capture(editor, "lane-entry-required-exit");
            Click(Button(editor, "应用到计划"));
            await Layout(f.Window);
        });
        await SaveAndCheckPair(f, expectedFlowCount: 1);
    }));

    [Fact]
    public Task AddLanesOnOrdinaryFlowCompletesItsExitWithoutAddingAnotherFlow() => OnSta(() => WithView(OrdinaryFlow(), async f =>
    {
        f.Panel.Editing!.SelectedNode = f.Panel.Editing.Nodes.Single(n => n.NodeId == "flow");
        await OpenPicker(f);
        await InEditor(f, () => Click(PickerButton(f, "＋ 车道")), editor =>
        {
            Assert.Equal("收集准备", editor.Result.Name);
            Assert.Single(editor.Result.Steps);
            AddWeekdayExit(editor);
            Click(Button(editor, "应用到计划"));
            return Task.CompletedTask;
        });
        await SaveAndCheckPair(f, expectedFlowCount: 1);
        Assert.Contains(f.Panel.PlanRows, n => n.NodeId == "flow");
    }));

    [Fact]
    public Task AddLanesOnExistingPairExpandsTheSameRowRatherThanAddingAnotherPair() => OnSta(() => WithView(PairedFlow(), async f =>
    {
        var editing = f.Panel.Editing!;
        var lane = editing.Nodes.Single(n => n.Model.Lanes is not null);
        editing.SelectedNode = lane;
        var row = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).NodeId == lane.NodeId);
        Assert.False(Assert.Single(Descendants<Expander>(row)).IsExpanded);
        await OpenPicker(f);
        Click(PickerButton(f, "＋ 车道"));
        await Layout(f.Window);
        Assert.Empty(f.Window.OwnedWindows.OfType<TaskFlowEditorWindow>());
        Assert.Single(f.Panel.Editing!.Nodes.Where(n => n.Model.Flow is not null));
        Assert.Single(f.Panel.Editing.Nodes.Where(n => n.Model.Lanes is not null));
        var existing = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).NodeId == lane.NodeId);
        Assert.True(Assert.Single(Descendants<Expander>(existing)).IsExpanded);
        Capture(f.Window, "lane-entry-existing-pair");
    }));

    [Fact]
    public Task PendingAutoSaveMayReplaceDraftButLaneEditorAppliesToSamePlan() => OnSta(() => WithView(null, async f =>
    {
        await OpenPicker(f);
        var oldDraft = f.Panel.Editing!;
        oldDraft.NameText = "自动保存后的计划";
        await InEditor(f, () => Click(PickerButton(f, "＋ 车道")), async editor =>
        {
            await Task.Delay(900); await Layout(editor);
            Assert.NotSame(oldDraft, f.Panel.Editing);
            Assert.Equal("wf-lane-entry", f.Panel.SelectedWorkflowId);
            AddWeekdayExit(editor);
            Click(Button(editor, "应用到计划"));
        });
        await SaveAndCheckPair(f, expectedFlowCount: 1);
        Assert.Equal("自动保存后的计划", f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Name);
    }));

    [Fact]
    public Task ChangedTargetShowsConflictAndRetainsEditorDraftWithoutOverwriting() => OnSta(() => WithView(OrdinaryFlow(), async f =>
    {
        var row = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).NodeId == "flow");
        await InEditor(f, () => Click(Button(row, "编辑")), async editor =>
        {
            Descendants<TextBox>(editor).Single(b => b.Text == "收集准备").Text = "用户窗口中的修改";
            var live = f.Panel.Editing!;
            var target = live.Nodes.Single(n => n.NodeId == "flow");
            var originalStep = StartupStepKinds.Create(StartupStepKinds.Wait);
            live.ReplaceFlow(target, new WorkflowFlowDefinition { Name = "另一操作已经修改", Steps = [originalStep] });
            f.Panel.SaveFlowCommand.Execute(null);
            await Layout(editor);
            Assert.Equal("另一操作已经修改", f.Panel.Editing!.Nodes.Single(n => n.NodeId == "flow").Model.Flow!.Name);
            Click(Button(editor, "应用到计划"));
            Assert.True(editor.IsVisible);
            Assert.Equal("用户窗口中的修改", editor.Result.Name);
            Assert.Equal("另一操作已经修改", f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Nodes.Single(n => n.NodeId == "flow").Flow!.Name);
            Capture(editor, "lane-entry-conflict-keeps-editor");
            Click(Button(editor, "取消"));
        });
        Assert.Equal("另一操作已经修改", f.Panel.Editing!.Nodes.Single(n => n.NodeId == "flow").Model.Flow!.Name);
    }));

    private static WorkflowNode OrdinaryFlow() => new()
    {
        NodeId = "flow", Kind = "control.flow",
        Flow = new() { Name = "收集准备", Steps = [StartupStepKinds.Create(StartupStepKinds.Wait)] }
    };
    private static WorkflowNode PairedFlow()
    {
        var judge = StartupStepKinds.Create(StartupStepKinds.Weekday);
        return new() { NodeId = "flow", Kind = "control.flow", Flow = new() { Name = "周末安排", Steps = [judge], ExitStepId = judge.Id } };
    }
    private static void AddWeekdayExit(TaskFlowEditorWindow editor)
    {
        Click(Button(editor, "＋ 星期判断"));
        Click(Button(editor, "设为出口判断"));
        Assert.NotNull(editor.Result.ExitStepId);
        Assert.Equal(StartupStepKinds.Weekday, editor.Result.Steps.Last().Kind);
    }

    private sealed record Fixture(string Root, TaskCenterHost Host, TaskCenterPanelViewModel Panel, BasicTaskCenterView View, Window Window);
    private static async Task WithView(WorkflowNode? flow, Func<Fixture, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "lane-entry-ui-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        var nodes = new List<WorkflowNode>();
        if (flow is not null) nodes.Add(flow);
        if (flow?.Flow?.ExitStepId is not null)
            nodes.Add(new() { NodeId = "lanes", Kind = "control.lanes", Lanes = new() { Name = "两条车道", SourceFlowNodeId = "flow" } });
        nodes.Add(new() { NodeId = "A", Kind = "resource.configGroup", Ref = new() { Config = "任务 A" } });
        host.SaveFlow(new() { WorkflowId = "wf-lane-entry", Name = "车道入口验证", Nodes = nodes }, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var view = new BasicTaskCenterView { DataContext = panel };
        var window = new Window { Content = view, Width = 1180, Height = 760, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            window.Show(); await Layout(window);
            await action(new(root, host, panel, view, window));
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            window.Close(); panel.StopAutoRefresh(); await host.ShutdownAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task SaveAndCheckPair(Fixture f, int expectedFlowCount)
    {
        f.Panel.SaveFlowCommand.Execute(null); await Layout(f.Window);
        Assert.False(f.Panel.StatusIsError, f.Panel.StatusMessage);
        var saved = f.Host.LoadFlowSnapshot("wf-lane-entry").Document;
        Assert.Empty(WorkflowComposition.Validate(saved));
        Assert.Equal(expectedFlowCount, saved.Nodes.Count(n => n.Flow is not null));
        var lane = Assert.Single(saved.Nodes.Where(n => n.Lanes is not null));
        var source = saved.Nodes.Single(n => n.NodeId == lane.Lanes!.SourceFlowNodeId);
        Assert.NotNull(source.Flow!.ExitStepId);
        Assert.Single(f.Panel.PlanRows.Where(n => n.Model.Lanes is not null));
    }

    private static async Task OpenPicker(Fixture f)
    {
        Click(Button(f.View, "+任务")); await Layout(f.Window);
        Assert.True(((Popup)f.View.FindName("AddTaskPopup")).IsOpen);
    }
    private static Button PickerButton(Fixture f, string content)
        => Button(((Popup)f.View.FindName("AddTaskPopup")).Child, content);
    private static async Task InEditor(Fixture f, Action open, Func<TaskFlowEditorWindow, Task> edit)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            TaskFlowEditorWindow? editor = null;
            try
            {
                editor = Assert.Single(f.Window.OwnedWindows.OfType<TaskFlowEditorWindow>());
                FormalWpfTheme.Apply(editor);
                await Layout(editor); await edit(editor); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (editor?.IsVisible == true) editor.Close(); }
        }));
        open();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static Button Button(DependencyObject root, string content) => Descendants<Button>(root).Single(b => b.Content as string == content);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
