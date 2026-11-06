using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("RegisterBankWpf")]
public sealed class FlowContextEditorUiTests
{
    [Fact]
    public Task LogActionsAreAddedAtTheirVisibleGroupAndRepeatedAddsStayThere() => OnSta(async () =>
    {
        var log = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        log.LogKeyword = "今天执行B线"; log.TriggerScope = "task"; log.TargetNodeId = "A"; log.WatchRepeat = false;
        var editor = Create(new() { Name = "采集日志准备", Steps = [log] });
        try
        {
            editor.Show(); await Layout(editor);
            Assert.DoesNotContain(Descendants<TextBlock>(editor), x => x.Text.Contains("插入到") || x.Text == "主链");
            Assert.DoesNotContain(Descendants<Button>(editor), b => (b.Content as string)?.Contains("出口") == true);
            Assert.Contains(Descendants<TextBlock>(editor), x => x.Text.Contains("今天执行B线"));

            Assert.False(Button(editor, "返回流程步骤").IsVisible);
            Click(NamedButton(editor, "日志出现时添加动作")); await Layout(editor);
            Assert.Contains(Descendants<TextBlock>(editor), x => x.Text.Contains("添加到：日志触发器 → 日志出现时"));
            Click(Button(editor, "＋ 等待")); await Layout(editor);
            Assert.True(editor.ParameterPanelForTest.IsVisible);
            Click(Button(editor, "＋ 添加步骤")); await Layout(editor);
            Click(Button(editor, "＋ 设置寄存器")); await Layout(editor);
            Register(editor, 2); await Layout(editor);
            var savedLog = Assert.Single(editor.Result.Steps);
            Assert.Equal(new[] { StartupStepKinds.Wait, StartupStepKinds.SetRegister }, savedLog.FireSteps.Select(s => s.Kind));
            Assert.Equal(1, savedLog.FireSteps[1].RegisterWrite!.Values[0]);
            Assert.Empty(savedLog.BeforeAttemptSteps);

            Click(NamedButton(editor, "任务每次开始前添加动作")); await Layout(editor);
            Click(Button(editor, "＋ 设置寄存器")); await Layout(editor);
            Register(editor, 1); await Layout(editor);
            Assert.Equal(0, Assert.Single(savedLog.BeforeAttemptSteps).RegisterWrite!.Values[0]);
            Assert.Equal(2, savedLog.FireSteps.Count);
            Assert.Empty(TaskFlowNodeCatalog.Validate(editor.Result));
            AssertStepHeadingsVisible(editor);
            Capture(editor, "flow-context-log-actions");
            Assert.Empty(log.FireSteps); Assert.Empty(log.BeforeAttemptSteps);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task ConditionsUseNamedBranchesAndReturnButtonAddsTheNextFlowStep() => OnSta(async () =>
    {
        var condition = StartupStepKinds.Create(StartupStepKinds.Weekday);
        condition.Weekdays = [6, 7];
        var editor = Create(new() { Name = "周末额外准备", Steps = [condition] });
        try
        {
            editor.Show(); await Layout(editor);
            Click(NamedButton(editor, "条件满足时添加动作")); await Layout(editor);
            Click(Button(editor, "＋ 等待")); await Layout(editor);
            Click(Button(editor, "＋ 添加步骤")); await Layout(editor);
            Click(Button(editor, "＋ 等待")); await Layout(editor);
            var saved = Assert.Single(editor.Result.Steps);
            Assert.Equal(2, saved.TrueSteps.Count); Assert.Empty(saved.FalseSteps);
            Click(Button(editor, "＋ 添加步骤")); await Layout(editor);
            Click(Button(editor, "返回流程步骤")); await Layout(editor);
            Click(Button(editor, "＋ 日志触发器")); await Layout(editor);
            Assert.Equal(2, editor.Result.Steps.Count);
            Assert.Equal(StartupStepKinds.LogTrigger, editor.Result.Steps[1].Kind);
            Assert.Equal(2, editor.Result.Steps[0].TrueSteps.Count);
            Assert.DoesNotContain(Descendants<Button>(editor), b => (b.Content as string)?.Contains("作为车道") == true);
            AssertStepHeadingsVisible(editor);
            Capture(editor, "flow-context-condition-actions");
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task MovementAndDropDownUseTheSameDarkTemplateIncludingDisabledAndPopupStates() => OnSta(async () =>
    {
        var log = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        log.TriggerScope = "round"; log.LogKeyword = "采集完成";
        var editor = Create(new() { Name = "控件状态", Steps = [log] });
        try
        {
            editor.Show(); await Layout(editor);
            var treeScroll = Descendants<ScrollViewer>(editor.StepTreeForTest).First();
            Assert.Same(editor.FindResource("CompositionScrollViewer"), treeScroll.Style);
            foreach (var text in new[] { "上移", "下移" })
            {
                var button = Button(editor, text);
                Assert.False(button.IsEnabled);
                Assert.Same(editor.FindResource("CompositionButton"), button.Style);
                var chrome = Assert.IsType<Border>(button.Template.FindName("Chrome", button));
                Assert.Equal((Color)ColorConverter.ConvertFromString("#29253A"), Assert.IsType<SolidColorBrush>(chrome.Background).Color);
            }
            var combo = Descendants<ComboBox>(editor.ParameterPanelForTest)
                .Single(c => AutomationProperties.GetName(c) == "保持多久");
            Assert.Same(editor.FindResource("CompositionComboBox"), combo.Style);
            Assert.Same(editor.FindResource("CompositionComboItem"), combo.ItemContainerStyle);
            combo.IsDropDownOpen = true; await Layout(editor);
            var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            Assert.True(popup.IsOpen);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#28223E"),
                Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(popup.Child).Background).Color);
            combo.IsDropDownOpen = false;
            Assert.All(Descendants<ScrollViewer>(editor).Where(s => s.Style is not null && s.Style == editor.FindResource("CompositionScrollViewer")),
                s => Assert.NotNull(s.Template));
        }
        finally { editor.Close(); }
    });

    private static TaskFlowEditorWindow Create(WorkflowFlowDefinition source)
    {
        var editor = new TaskFlowEditorWindow(source,
            [new WorkflowNode { NodeId = "A", Kind = "resource.configGroup", Ref = new() { Config = "狗粮收集" } }])
        { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(editor); return editor;
    }
    private static void Register(TaskFlowEditorWindow window, int selectedIndex)
    {
        var combo = Descendants<ComboBox>(window.ParameterPanelForTest)
            .Single(c => AutomationProperties.GetName(c) == "R1 设置");
        combo.SelectedIndex = selectedIndex;
    }
    private static void AssertStepHeadingsVisible(TaskFlowEditorWindow editor)
    {
        var titles = Descendants<TreeViewItem>(editor.StepTreeForTest)
            .Select(i => i.Header).OfType<StackPanel>()
            .Select(p => p.Children.OfType<TextBlock>().First()).ToArray();
        Assert.NotEmpty(titles);
        Assert.All(titles, title =>
        {
            Assert.True(title.IsVisible);
            Assert.True(title.ActualWidth > 0 && title.ActualHeight > 0, "步骤名称应实际排版并绘制。");
        });
    }
    private static Button Button(DependencyObject root, string text) => Descendants<Button>(root).Single(b => b.Content as string == text);
    private static Button NamedButton(DependencyObject root, string name) => Descendants<Button>(root).Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(Button button) { Assert.True(button.IsVisible && button.IsEnabled); button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); }
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
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name + ".png")); png.Save(file);
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
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
