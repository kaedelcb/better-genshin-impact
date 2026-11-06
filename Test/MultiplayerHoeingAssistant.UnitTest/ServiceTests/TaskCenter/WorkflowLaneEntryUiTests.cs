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
    public Task AddLaneIsOneIndependentTaskWithItsOwnCondition() => OnSta(() => WithView(null, async f =>
    {
        await OpenPicker(f);
        await InLaneEditor(f, () => Click(PickerButton(f, "＋ 车道")), async editor =>
        {
            Assert.Equal("设置车道", editor.Title);
            Click(Button(editor, "保存车道"));
            Assert.True(editor.IsVisible);
            Assert.DoesNotContain(f.Panel.Editing!.Nodes, n => n.IsComposition);
            SelectWeekday(editor);
            await Layout(editor); Capture(editor, "lane-built-in-condition");
            var kind = Descendants<ComboBox>(editor).Single(c => c.Items.Count > 0 && c.Items[0] as string == "请选择判断方式");
            kind.IsDropDownOpen = true; await Layout(editor);
            var popup = Assert.IsType<Popup>(kind.Template.FindName("PART_Popup", kind));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#28223E"), Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(popup.Child).Background).Color);
            Capture(editor, "lane-condition-picker-open");
            CapturePopup(popup, "lane-condition-popup");
            kind.IsDropDownOpen = false;
            Click(Button(editor, "保存车道"));
        });
        await SaveAndCheckPair(f, expectedFlowCount: 0);
        var lane = Assert.Single(f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Nodes.Where(n => n.Lanes is not null));
        Assert.NotNull(lane.Lanes!.Decision);
        Assert.Equal("", lane.Lanes.SourceFlowNodeId);
        var editing = f.Panel.Editing!;
        var visible = editing.Nodes.Single(n => n.Model.Lanes is not null);
        visible.Model.Lanes!.YesNodeIds.Add("A"); editing.CompositionChanged();
        f.Panel.SaveFlowCommand.Execute(null); await Layout(f.Window);
        var row = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).Model.Lanes is not null);
        Assert.Single(Descendants<Expander>(row)).IsExpanded = true; await Layout(f.Window);
        var up = Button(row, "上移"); Assert.False(up.IsEnabled);
        Assert.Equal((Color)ColorConverter.ConvertFromString("#29253A"), Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(up.Template.FindName("Chrome", up)).Background).Color);
        Capture(f.Window, "standalone-lane-main-table");
    }));

    [Fact]
    public Task AddingLaneDoesNotTurnTheSelectedActionFlowIntoAnExtraSelector() => OnSta(() => WithView(OrdinaryFlow(), async f =>
    {
        f.Panel.Editing!.SelectedNode = f.Panel.Editing.Nodes.Single(n => n.NodeId == "flow");
        await OpenPicker(f);
        await InLaneEditor(f, () => Click(PickerButton(f, "＋ 车道")), editor =>
        { SelectWeekday(editor); Click(Button(editor, "保存车道")); return Task.CompletedTask; });
        await SaveAndCheckPair(f, expectedFlowCount: 1);
        var flow = f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Nodes.Single(n => n.NodeId == "flow").Flow!;
        Assert.Equal("收集准备", flow.Name); Assert.Null(flow.ExitStepId);
    }));

    [Fact]
    public Task LegacyPairedFlowIsDisplayedAndEditedInsideOneLaneTask() => OnSta(() => WithView(PairedFlow(), async f =>
    {
        Assert.DoesNotContain(f.Panel.PlanRows, n => n.NodeId == "flow");
        var row = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).Model.Lanes is not null);
        await InLaneEditor(f, () => Click(Button(row, "设置车道")), editor =>
        {
            Assert.Contains(Descendants<ComboBox>(editor), c => c.SelectedItem as string == "星期判断");
            Click(Button(editor, "保存车道")); return Task.CompletedTask;
        });
        var saved = f.Host.LoadFlowSnapshot("wf-lane-entry").Document;
        Assert.Equal("flow", saved.Nodes.Single(n => n.Lanes is not null).Lanes!.SourceFlowNodeId);
        Assert.Single(saved.Nodes.Where(n => n.Flow is not null));
        Assert.Single(saved.Nodes.Where(n => n.Lanes is not null));
        Assert.DoesNotContain(f.Panel.PlanRows, n => n.NodeId == "flow");
    }));

    [Fact]
    public Task PendingAutoSaveMayReplaceDraftButLaneEditorAppliesToSamePlan() => OnSta(() => WithView(null, async f =>
    {
        await OpenPicker(f); var oldDraft = f.Panel.Editing!; oldDraft.NameText = "自动保存后的计划";
        await InLaneEditor(f, () => Click(PickerButton(f, "＋ 车道")), async editor =>
        {
            await Task.Delay(900); await Layout(editor);
            Assert.NotSame(oldDraft, f.Panel.Editing);
            SelectWeekday(editor); Click(Button(editor, "保存车道"));
        });
        await SaveAndCheckPair(f, expectedFlowCount: 0);
        Assert.Equal("自动保存后的计划", f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Name);
    }));

    [Fact]
    public Task ChangedTargetShowsConflictAndRetainsEditorDraftWithoutOverwriting() => OnSta(() => WithView(OrdinaryFlow(), async f =>
    {
        var row = Descendants<CompositionPlanRow>(f.View).Single(r => r.IsVisible && ((NodeEditVm)r.DataContext).NodeId == "flow");
        await InEditor(f, () => Click(Button(row, "编辑步骤")), async editor =>
        {
            Descendants<TextBox>(editor).Single(b => b.Text == "收集准备").Text = "用户窗口中的修改";
            var live = f.Panel.Editing!;
            live.ReplaceFlow(live.Nodes.Single(n => n.NodeId == "flow"), new WorkflowFlowDefinition { Name = "另一操作已经修改", Steps = [StartupStepKinds.Create(StartupStepKinds.Wait)] });
            f.Panel.SaveFlowCommand.Execute(null); await Layout(editor);
            Click(Button(editor, "应用到计划"));
            Assert.True(editor.IsVisible); Assert.Equal("用户窗口中的修改", editor.Result.Name);
            Assert.Equal("另一操作已经修改", f.Host.LoadFlowSnapshot("wf-lane-entry").Document.Nodes.Single(n => n.NodeId == "flow").Flow!.Name);
            await Layout(editor); Capture(editor, "lane-entry-conflict-keeps-editor"); Click(Button(editor, "取消"));
        });
    }));

    private static void SelectWeekday(LanesEditorWindow editor)
    {
        var kind = Descendants<ComboBox>(editor).Single(c => c.Items.Count > 0 && c.Items[0] as string == "请选择判断方式");
        kind.SelectedItem = "星期判断";
    }

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
        Assert.NotNull(WorkflowComposition.GetDecision(saved, lane)?.ExitStepId);
        Assert.Single(f.Panel.PlanRows.Where(n => n.Model.Lanes is not null));
    }

    private static async Task OpenPicker(Fixture f)
    {
        Click(Button(f.View, "+任务")); await Layout(f.Window);
        Assert.True(((Popup)f.View.FindName("AddTaskPopup")).IsOpen);
    }
    private static Button PickerButton(Fixture f, string content)
        => Button(((Popup)f.View.FindName("AddTaskPopup")).Child, content);
    private static async Task InLaneEditor(Fixture f, Action open, Func<LanesEditorWindow, Task> edit)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            LanesEditorWindow? editor = null;
            try
            {
                editor = Assert.Single(f.Window.OwnedWindows.OfType<LanesEditorWindow>());
                FormalWpfTheme.Apply(editor); await Layout(editor); await edit(editor); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (editor?.IsVisible == true) editor.Close(); }
        }));
        open(); await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

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
    private static void CapturePopup(Popup popup, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder) || popup.Child is not FrameworkElement visual) return;
        Directory.CreateDirectory(folder);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
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
