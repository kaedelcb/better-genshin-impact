using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 任务中心流程图窗口：计划表带流程和车道时能画出整图，按最近一次运行点亮走过的车道；
/// 计划表上的「流程图」按钮能打开窗口。隔离宿主目录，不连接 BGI、不执行运行。
/// 设置 TASKCENTER_FLOWCHART_SNAPSHOT_PATH 时把窗口内容存成截图供人工查看。
/// </summary>
public sealed class TaskCenterFlowChartWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "taskcenter-flowchart-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FlowChart_DrawsPlanAndHighlightsTakenLane()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            TaskCenterFlowChartWindow? window = null;
            try
            {
                var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
                    () => null, () => true, () => null);
                var yesStep = new StartupStep { Kind = StartupStepKinds.Wait, NodeType = "action", WaitSeconds = 5 };
                var cond = new StartupStep { Kind = StartupStepKinds.Weekday, NodeType = "condition", Weekdays = [1, 2, 3], TrueSteps = [yesStep] };
                var timer = new StartupStep { Kind = StartupStepKinds.TimerTrigger, NodeType = "action", TriggerTime = "04:00",
                    FireSteps = [new StartupStep { Kind = StartupStepKinds.Wait, NodeType = "action" }] };
                var exit = new StartupStep { Kind = StartupStepKinds.TimeRange, NodeType = "condition", TimeStart = "08:00", TimeEnd = "12:00" };
                var doc = new WorkflowDocument
                {
                    Name = "流程图计划",
                    Nodes =
                    [
                        new() { NodeId = "n-a", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常" }, TimeWindow = new() { Start = "08:00", End = "12:00" } },
                        new() { NodeId = "n-f", Kind = "control.flow", Flow = new() { Name = "准备流程", Steps = [cond, timer] } },
                        new() { NodeId = "n-l", Kind = "control.lanes", Lanes = new() { Name = "选择路线", YesName = "上午", NoName = "下午",
                            Decision = new() { Steps = [exit], ExitStepId = exit.Id }, YesNodeIds = ["n-y"], NoNodeIds = ["n-n"] } },
                        new() { NodeId = "n-y", Kind = "resource.configGroup", Ref = new() { Config = "上午组" } },
                        new() { NodeId = "n-n", Kind = "resource.configGroup", Ref = new() { Config = "下午组" } },
                    ],
                };
                var revision = host.SaveFlow(doc, null);
                var workflowId = host.ListFlows().Single().WorkflowId;
                // 宿主的运行目录要求所有者能力；测试用同目录的普通 RunStore 预置一条已结束的运行记录。
                var seed = new RunStore(Path.Combine(_root, "runs"));
                var run = seed.CreateRun(workflowId, revision);
                run.State = WorkflowRunState.Succeeded;
                run.NodeOutcomes.Add(new() { NodeId = "n-a", Result = "succeeded" });
                run.NodeOutcomes.Add(new() { NodeId = "n-f", Result = "succeeded" });
                run.NodeOutcomes.Add(new() { NodeId = "n-l", Result = "branchYes" });
                run.NodeOutcomes.Add(new() { NodeId = "n-y", Result = "succeeded" });
                run.ExtensionData = new()
                {
                    [$"taskFlowStep:n-f:0:{cond.Id}"] = JsonSerializer.SerializeToElement("done"),
                    [$"taskFlowStep:n-f:0:{yesStep.Id}"] = JsonSerializer.SerializeToElement("done"),
                    [$"taskFlowStep:n-f:0:{timer.Id}"] = JsonSerializer.SerializeToElement("done"),
                };
                seed.Update(run);

                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.Refresh();
                Assert.NotNull(vm.Editing);
                Assert.True(vm.RunTrace.HasRun);
                Assert.True(vm.RunTrace.LaneChoice(vm.Editing!.Draft, vm.Editing.Draft.Nodes[2]));
                Assert.Equal(NodeRunState.CondTrue, vm.RunTrace.Step("n-f", cond.Id));

                // 计划表每行显示本轮运行状态；只是展示，不算未保存修改。
                var rows = vm.Editing.Nodes.ToDictionary(r => r.NodeId);
                Assert.Equal("✓ 已完成", rows["n-a"].RunStateText);
                Assert.Equal("✓ 已完成", rows["n-y"].RunStateText);
                Assert.Equal("", rows["n-n"].RunStateText);
                Assert.Equal(NodeRunState.CondTrue, rows["n-l"].RunState);
                Assert.False(vm.Editing.HasUnsavedChanges);

                // 计划表上的按钮能打开流程图窗口。
                var view = new BasicTaskCenterView { DataContext = vm };
                view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();
                // 测试视图不在窗口里，IsVisible 恒为 false；按自身及祖先的 Visibility 判断是否显示。
                static bool Shown(DependencyObject d) { for (; d is not null; d = VisualTreeHelper.GetParent(d)) if (d is UIElement { Visibility: not Visibility.Visible }) return false; return true; }
                var badges = Descendants(view).OfType<TextBlock>().Where(t => t.Text == "✓ 已完成" && Shown(t)).ToArray();
                Assert.Equal(2, badges.Length); // 任务行 n-a 与流程行 n-f 的标题；车道行显示「→ 满足条件」
                Assert.Contains(Descendants(view).OfType<TextBlock>(), t => t.Text == "→ 满足条件" && Shown(t));
                if (Environment.GetEnvironmentVariable("TASKCENTER_PLAN_SNAPSHOT_PATH") is { Length: > 0 } planShot)
                {
                    var bmp = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); bmp.Render(view);
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(planShot); enc.Save(fs);
                }
                var button = Descendants(view).OfType<Button>().Single(b => Equals(b.Content, "🗺 流程图"));
                Assert.True(button.IsEnabled);
                var opened = 0;
                vm.FlowChartRequested += () => opened++;
                button.Command.Execute(null);
                Assert.Equal(1, opened);
                var chart = typeof(BasicTaskCenterView).GetField("_flowChart", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Assert.IsType<TaskCenterFlowChartWindow>(chart.GetValue(view));
                ((Window)chart.GetValue(view)!).Close();
                Assert.Null(chart.GetValue(view));

                window = new TaskCenterFlowChartWindow(vm, (_, _) => { });
                window.Show();
                window.UpdateLayout();
                var texts = Descendants(window).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Assert.Contains(texts, t => t.Contains("流程 · 准备流程"));
                Assert.Contains(texts, t => t.Contains("满足 → 上午"));
                Assert.Contains(texts, t => t.Contains("其他 → 下午"));
                Assert.Contains(texts, t => t.Contains("⏰ 到点执行"));
                Assert.Contains(texts, t => t.Contains("最近一次运行：已完成"));
                Assert.Contains("→ 是", texts);
                Assert.Contains(texts, t => t.Contains("时段 08:00–12:00"));
                RenderSnapshotIfRequested(window);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { window?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void RenderSnapshotIfRequested(Window window)
    {
        var path = Environment.GetEnvironmentVariable("TASKCENTER_FLOWCHART_SNAPSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path) || window.Content is not FrameworkElement content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap((int)Math.Max(1, content.ActualWidth), (int)Math.Max(1, content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
