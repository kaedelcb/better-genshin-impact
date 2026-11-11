using System.IO;
using System.Windows;
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

/// <summary>
/// 任务中心跳转第 2 阶段（PC 编辑）：编号、跳转目标下拉框（流程 / 车道路线）、计划表跳转说明、
/// 标红与不可达提示、删除被跳到的行前确认；展示字段不算未保存修改。隔离目录，不连接 BGI。
/// 设置 TASKCENTER_JUMP_UI_SHOTS 时把计划表与编辑窗口存成截图供人工查看。
/// </summary>
[Collection("RegisterBankWpf")]
public sealed class CompositionJumpUiTests
{
    private static WorkflowNode R(string id) => new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = "配置" + id } };
    private static StartupStep Wait0() { var w = StartupStepKinds.Create(StartupStepKinds.Wait); w.WaitSeconds = 0; return w; }
    private static WorkflowNode Flow(string id, string? jumpTo = null)
        => new() { NodeId = id, Kind = "control.flow", Flow = new() { Name = "流程" + id, Steps = [Wait0()], JumpTo = jumpTo } };
    private static WorkflowNode Lane(string id, string[] yes, string[] no, string? yesJump = null, string? noJump = null)
    {
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday); c.Weekdays = [6, 7];
        return new() { NodeId = id, Kind = "control.lanes", Lanes = new()
        { Name = "车道" + id, Decision = new() { Steps = [c], ExitStepId = c.Id }, YesNodeIds = [.. yes], NoNodeIds = [.. no], YesJumpTo = yesJump, NoJumpTo = noJump } };
    }
    private static WorkflowEditVm Edit(WorkflowDocument doc)
        => new(doc, "rev", new ResourceCatalogService(() => null, Path.Combine(Path.GetTempPath(), "jump-ui-" + Guid.NewGuid().ToString("N"), "catalog.json")));

    // ---------------- 编号与目标列表（纯逻辑） ----------------

    [Fact]
    public void RowNumbersFollowDisplayOrderAndLaneMembersUseSubNumbers()
    {
        // 存储顺序里车道成员排在后面；编号按显示顺序。
        var edit = Edit(new() { Name = "编号", Nodes = [R("A"), Lane("L", ["Y1", "Y2"], ["N"]), R("D"), R("Y1"), R("Y2"), R("N")] });
        var no = edit.RowNumbers();
        Assert.Equal("#1", no["A"]); Assert.Equal("#2", no["L"]); Assert.Equal("#3", no["D"]);
        Assert.Equal("#2.1", no["Y1"]); Assert.Equal("#2.2", no["Y2"]); Assert.Equal("#2.3", no["N"]);
    }

    [Fact]
    public void JumpChoicesListOnlyLaterMainRowsAndKeepIllegalCurrentVisible()
    {
        var edit = Edit(new() { Name = "目标", Nodes = [R("A"), Flow("F"), Lane("L", ["Y"], []), R("D"), R("Y")] });
        var choices = edit.JumpTargetChoices("F", null);
        Assert.Equal(new string?[] { null, "L", "D" }, choices.Select(c => c.NodeId));
        Assert.Equal("继续下一行", choices[0].Label);
        Assert.StartsWith("跳到 #3", choices[1].Label);
        // 当前目标在前面（被拖动后）：仍列出并注明，不悄悄换成别的。
        var withIllegal = edit.JumpTargetChoices("F", "A");
        Assert.Contains(withIllegal, c => c.NodeId == "A" && c.Label.Contains("在前面"));
        // 车道里的任务不能作为目标。
        Assert.DoesNotContain(choices, c => c.NodeId == "Y");
        // 新建的流程（还没加入计划）没有可跳的目标。
        Assert.Equal(new string?[] { null }, edit.JumpTargetChoices(null, null).Select(c => c.NodeId));
    }

    [Fact]
    public void RowLabelsShowJumpProblemAndUnreachableWithoutCountingAsEdits()
    {
        var edit = Edit(new() { Name = "标注", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("D"), Flow("G", jumpTo: "F")] });
        edit.RefreshJumpLabels();
        var rows = edit.Nodes.ToDictionary(n => n.NodeId);
        Assert.Equal("#1", rows["F"].RowNumber);
        Assert.Equal("执行完后 → #3 配置D（跳过 #2）", rows["F"].JumpText);
        Assert.True(rows["G"].HasJumpProblem);
        Assert.Contains("只能往后跳", rows["G"].RowNotice);
        // 计划有跳转问题时不计算不可达（避免和标红同时出两种提示）。
        Assert.False(rows["B"].IsUnreachable);
        Assert.False(edit.HasUnsavedChanges);

        var ok = Edit(new() { Name = "不可达", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("D")] });
        ok.RefreshJumpLabels();
        var b = ok.Nodes.Single(n => n.NodeId == "B");
        Assert.True(b.IsUnreachable);
        Assert.Contains("没有路径会走到", b.RowNotice);
        Assert.False(ok.HasUnsavedChanges);
        Assert.False(ok.UndoSchedule()); // 展示字段变化不产生撤销步骤
    }

    [Fact]
    public void DeleteWarningOnlyWhenRowIsAJumpTarget()
    {
        var edit = Edit(new() { Name = "删除", Nodes = [Flow("F", jumpTo: "D"), Lane("L", ["Y"], [], yesJump: "D"), R("B"), R("D"), R("Y")] });
        var d = edit.Nodes.Single(n => n.NodeId == "D");
        var warning = edit.DeleteJumpWarning(d);
        Assert.NotNull(warning);
        Assert.Contains("#1", warning); Assert.Contains("#2", warning);
        Assert.Null(edit.DeleteJumpWarning(edit.Nodes.Single(n => n.NodeId == "B")));
        // 删除车道时，它自己的跳转不算「别人指向被删行」。
        Assert.Null(edit.DeleteJumpWarning(edit.Nodes.Single(n => n.NodeId == "Y")));
    }

    // ---------------- 窗口与计划表（STA） ----------------

    [Fact]
    public Task FlowEditorJumpPickerWritesJumpToResult() => OnSta(async () =>
    {
        var edit = Edit(new() { Name = "流程选择", Nodes = [Flow("F"), R("B"), R("D")] });
        var source = edit.Draft.Nodes[0].Flow!;
        var window = new TaskFlowEditorWindow(source, edit.Draft.Nodes, null) { JumpChoices = edit.JumpTargetChoices("F", null) };
        FormalWpfTheme.Apply(window);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Layout(window);
                var combo = Assert.IsType<ComboBox>(window.JumpChoiceForTest);
                Assert.Equal("继续下一行", ((JumpTargetChoice)combo.SelectedItem).Label);
                combo.SelectedItem = ((IEnumerable<JumpTargetChoice>)combo.ItemsSource).Single(c => c.NodeId == "D");
                Capture(window, "jump-flow-editor");
                Button(window, "应用到计划").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                done.SetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); window.Close(); }
        };
        Assert.True(window.ShowDialog());
        await done.Task;
        Assert.Equal("D", window.Result.JumpTo);
        Assert.Null(source.JumpTo); // 应用前不改原计划
    });

    [Fact]
    public Task LaneEditorRoutePickersWriteBothJumps() => OnSta(async () =>
    {
        var edit = Edit(new() { Name = "车道选择", Nodes = [Lane("L", ["Y"], []), R("B"), R("D"), R("Y")] });
        var lane = edit.Draft.Nodes[0];
        var window = new LanesEditorWindow(lane.Lanes!, lane.Lanes!.Decision, edit.Draft.Nodes) { JumpChoices = edit.JumpTargetChoices("L", null) };
        FormalWpfTheme.Apply(window);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Layout(window);
                var yes = Assert.IsType<ComboBox>(window.YesJumpForTest);
                var no = Assert.IsType<ComboBox>(window.NoJumpForTest);
                no.SelectedItem = ((IEnumerable<JumpTargetChoice>)no.ItemsSource).Single(c => c.NodeId == "D");
                Assert.Null(((JumpTargetChoice)yes.SelectedItem).NodeId);
                Capture(window, "jump-lane-editor");
                Button(window, "保存车道").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                done.SetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); window.Close(); }
        };
        Assert.True(window.ShowDialog());
        await done.Task;
        Assert.Null(window.Result.YesJumpTo);
        Assert.Equal("D", window.Result.NoJumpTo);
    });

    [Fact]
    public Task PlanTableShowsNumbersJumpTextAndProblem() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "jump-plan-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
            host.SaveFlow(new WorkflowDocument { Name = "跳转计划表", Nodes = [R("A"), Flow("F", jumpTo: "D"), R("B"), R("D"), Flow("G", jumpTo: "A")] }, null);
            var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var view = new BasicTaskCenterView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            await Layout(window);
            var texts = Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Contains("#1", texts); Assert.Contains("#2", texts); Assert.Contains("#5", texts);
            Assert.Contains(texts, t => t.Contains("执行完后 → #4 配置D"));
            Assert.Contains(texts, t => t.StartsWith("⚠") && t.Contains("只能往后跳"));
            Assert.False(new WorkflowPlan(vm.Editing!.Draft).Preflight(true).Executable);
            Capture(window, "jump-plan-table");

            // 删除被跳到的 D：先确认；取消则不删。
            var asked = new List<string>();
            vm.ConfirmDelete = message => { asked.Add(message); return false; };
            vm.RemoveSelectedResourceCommand.Execute(vm.Editing.Nodes.Single(n => n.NodeId == "D"));
            Assert.Single(asked);
            Assert.Contains(vm.Editing.Nodes, n => n.NodeId == "D");
            vm.ConfirmDelete = _ => true;
            vm.RemoveSelectedResourceCommand.Execute(vm.Editing.Nodes.Single(n => n.NodeId == "D"));
            Assert.DoesNotContain(vm.Editing.Nodes, n => n.NodeId == "D");
            Assert.Null(vm.Editing.Draft.Nodes.Single(n => n.NodeId == "F").Flow!.JumpTo);
            window.Close();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    });

    // ---------------- 第 3 阶段：被跳过的行与流程图连线 ----------------

    private static WorkflowRunRecord Run(string workflowId, string cursor, params (string Node, string Result)[] outcomes) => new()
    {
        RunId = "run-1", WorkflowId = workflowId, State = WorkflowRunState.Running, CreatedAt = DateTimeOffset.Now,
        Cursor = new() { NodeId = cursor }, NodeOutcomes = outcomes.Select(o => new WorkflowNodeOutcome { NodeId = o.Node, Result = o.Result }).ToList(),
    };

    [Fact]
    public void TraceMarksRowsSkippedByFlowAndLaneJumps()
    {
        var doc = new WorkflowDocument { Name = "跳过", WorkflowId = "wf-j", Nodes = [Flow("F", jumpTo: "C"), R("B"), Lane("L", ["Y"], [], noJump: "E"), R("C"), R("D"), R("E"), R("Y")] };
        // 流程执行完 → 跳过 B 和整条车道（含车道成员 Y）
        var t1 = TaskCenterRunTrace.Build(doc, [Run("wf-j", "C", ("F", "succeeded"))]);
        Assert.True(t1.JumpedOver("B")); Assert.True(t1.JumpedOver("L")); Assert.True(t1.JumpedOver("Y"));
        Assert.False(t1.JumpedOver("C")); Assert.False(t1.JumpedOver("E"));
        // 流程被过滤（没执行）→ 不跳，什么都不算跳过
        var t2 = TaskCenterRunTrace.Build(doc, [Run("wf-j", "B", ("F", "skippedFilter"))]);
        Assert.Empty(t2.JumpedOverRows);
        // 车道「其他」空路线跳到 E → 跳过 C、D；「满足」路线没走完时不算
        var lanePlan = new WorkflowDocument { Name = "车道跳过", WorkflowId = "wf-j", Nodes = [Lane("L", ["Y"], [], yesJump: "E", noJump: "E"), R("C"), R("D"), R("E"), R("Y")] };
        var t3 = TaskCenterRunTrace.Build(lanePlan, [Run("wf-j", "E", ("L", "branchNo"))]);
        Assert.True(t3.JumpedOver("C")); Assert.True(t3.JumpedOver("D"));
        var t4 = TaskCenterRunTrace.Build(lanePlan, [Run("wf-j", "Y", ("L", "branchYes"))]);
        Assert.Empty(t4.JumpedOverRows); // Y 还在执行，路线没走完
        var t5 = TaskCenterRunTrace.Build(lanePlan, [Run("wf-j", "E", ("L", "branchYes"), ("Y", "succeeded"))]);
        Assert.True(t5.JumpedOver("C"));
    }

    [Fact]
    public Task PlanTableAndFlowChartShowJumpedOverRowsAndJumpLines() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "jump-p3-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
            var revision = host.SaveFlow(new WorkflowDocument { Name = "跳转状态", Nodes = [R("A"), Flow("F", jumpTo: "D"), R("B"), R("C"), R("D")] }, null);
            var workflowId = host.ListFlows().Single().WorkflowId;
            var seed = new RunStore(Path.Combine(root, "runs"));
            var run = seed.CreateRun(workflowId, revision);
            run.State = WorkflowRunState.Succeeded;
            foreach (var (id, result) in new[] { ("A", "succeeded"), ("F", "succeeded"), ("D", "succeeded") })
                run.NodeOutcomes.Add(new() { NodeId = id, Result = result });
            seed.Update(run);

            var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
            vm.Refresh();
            var rows = vm.Editing!.Nodes.ToDictionary(n => n.NodeId);
            Assert.Equal("本轮跳过（跳转）", rows["B"].RunStateText);
            Assert.Equal("本轮跳过（跳转）", rows["C"].RunStateText);
            Assert.Equal("✓ 已完成", rows["D"].RunStateText);
            Assert.False(vm.Editing.HasUnsavedChanges);

            var view = new BasicTaskCenterView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            await Layout(window);
            Assert.Equal(2, Descendants<TextBlock>(view).Count(t => t.IsVisible && t.Text == "本轮跳过（跳转）"));
            Capture(window, "jump-plan-skipped");

            var chart = new TaskCenterFlowChartWindow(vm, (_, _) => { });
            chart.Show();
            await Layout(chart);
            var texts = Descendants<TextBlock>(chart).Select(t => t.Text).ToArray();
            Assert.Contains("↪ #5", texts);
            Assert.Equal(2, texts.Count(t => t == "本轮跳过（跳转）"));
            Assert.Contains(Descendants<System.Windows.Shapes.Polyline>(chart), l => l.StrokeDashArray.Count > 0 && l.Points.Count == 4);
            Capture(chart, "jump-flowchart");
            chart.Close(); window.Close();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    });

    // ---------------- 显示改进：路线绕开另一条路线专用的行 ----------------

    /// <summary>
    /// 周末/工作日例子：车道「周末」路线走完继续下一行，「工作日」空路线跳到 #3；#2 周末收尾执行完跳到 #4，跳过 #3。
    /// 计划表写明跳过哪些行、#3 从哪里跳进来；流程图上 #2→#3 只画灰虚线（停用/跳过时），工作日路线不画汇合到 #2 的线。
    /// </summary>
    [Fact]
    public Task RouteBypassExampleShowsSkipsEntriesAndFallthroughClearly() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "jump-bypass-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
            host.SaveFlow(new WorkflowDocument { Name = "周末工作日", Nodes = [
                Lane("L", ["Y"], [], noJump: "W"), Flow("S", jumpTo: "E"), Flow("W"), R("E"), R("Y")] }, null);
            var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var rows = vm.Editing!.Nodes.ToDictionary(n => n.NodeId);
            Assert.Equal("执行完后 → #4 配置E（跳过 #3）", rows["S"].JumpText);
            Assert.Contains("跳过 #2", rows["L"].JumpText);
            Assert.Equal("从 #1 其他情况 跳到这里", rows["W"].JumpedFrom);
            Assert.StartsWith("↪ 从 #1 其他情况 跳到这里", rows["W"].RowNotice);
            Assert.False(rows["W"].IsUnreachable);
            Assert.False(vm.Editing.HasUnsavedChanges);

            var view = new BasicTaskCenterView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            await Layout(window);
            Assert.Contains(Descendants<TextBlock>(view), t => t.IsVisible && t.Text.Contains("跳到这里"));
            Capture(window, "jump-bypass-plan");

            var chart = new TaskCenterFlowChartWindow(vm, (_, _) => { });
            chart.Show();
            await Layout(chart);
            var texts = Descendants<TextBlock>(chart).Select(t => t.Text).ToArray();
            Assert.Contains("停用/跳过时", texts);
            Assert.Contains("↪ #4", texts);
            Assert.Contains("其他 ↪ #3", texts);
            Capture(chart, "jump-bypass-chart");
            chart.Close(); window.Close();
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    });

    // ---------------- 工具 ----------------

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
        var folder = Environment.GetEnvironmentVariable("TASKCENTER_JUMP_UI_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var visual = window.Content as FrameworkElement ?? window;
        var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rect = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background ?? Brushes.Black, null, rect);
            context.DrawRectangle(new VisualBrush(visual), null, rect);
        }
        bmp.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(stream);
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
