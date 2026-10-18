using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class PlanSettingsTimePickerTests
{
    [Fact]
    public Task PlanPopup_ThreePickersConfirmClearAndPersistWithoutTyping() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "plan-time-picker-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        host.SaveFlow(new WorkflowDocument
        {
            WorkflowId = "wf-time-picker", Name = "时间选择计划",
            Nodes = [new() { NodeId = "end", Kind = "control.end" }],
            Triggers = [new() { Kind = "trigger.time", Params = new()
            {
                ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("09:10"),
                ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
            } }],
            Loop = new() { Mode = "scheduled", Params = new()
            {
                ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("10:20"),
                ["deadline"] = System.Text.Json.JsonSerializer.SerializeToElement("22:15"),
                ["skipAcrossDays"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            } },
        }, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var basic = new BasicTaskCenterView { DataContext = panel };
        var window = TestWindow(basic);
        try
        {
            window.Show();
            await Layout(window);
            var open = Descendants<Button>(basic).Single(button =>
                Descendants<TextBlock>(button).Any(text => text.Text == "计划设置"));
            Click(open);
            var parentPopup = (Popup)basic.FindName("PlanSettingsPopup");
            await Until(window, () => parentPopup.IsOpen);
            var form = Descendants<BasicPlanSettingsView>(parentPopup.Child).Single();
            var start = (TimeOfDayPicker)form.FindName("PlanStartTimePicker");
            var loop = (TimeOfDayPicker)form.FindName("PlanLoopTimePicker");
            var deadline = (TimeOfDayPicker)form.FindName("PlanDeadlineTimePicker");
            Assert.Equal(3, Descendants<TimeOfDayPicker>(form).Count());
            Assert.Equal("09:10", start.Value);
            Assert.Equal("10:20", loop.Value);
            Assert.Equal("22:15", deadline.Value);

            // Opening, choosing and cancelling must neither change a time nor save an intermediate value.
            await Open(start, window);
            ((ListBox)start.FindName("HourList")).SelectedIndex = 7;
            await Layout(window);
            Assert.Equal("09:10", panel.Editing!.TriggerTimeText);
            Assert.Equal("09:10", host.Workflows.LoadSnapshot("wf-time-picker").Document.Triggers[0].GetString("time"));
            ((ToggleButton)start.FindName("DropButton")).IsChecked = false;

            await Select(start, 8, 35, window, parentPopup);
            await Select(loop, 11, 45, window, parentPopup);
            await Select(deadline, 23, 55, window, parentPopup);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Loop?.GetString("deadline") == "23:55");
            var saved = host.Workflows.LoadSnapshot("wf-time-picker").Document;
            Assert.Equal("08:35", saved.Triggers[0].GetString("time"));
            Assert.Equal("11:45", saved.Loop!.GetString("time"));
            Assert.Equal("nextDay", saved.Triggers[0].GetString("missPolicy"));
            Assert.True(saved.Loop.GetBool("skipAcrossDays"));

            // A second edit after saving exercises both the two-way binding and replacement edit model.
            await Select(start, 9, 50, window, parentPopup);
            await Select(loop, 12, 10, window, parentPopup);
            await Select(deadline, 20, 25, window, parentPopup);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Triggers[0].GetString("time") == "09:50");
            saved = host.Workflows.LoadSnapshot("wf-time-picker").Document;
            Assert.Equal("12:10", saved.Loop!.GetString("time"));
            Assert.Equal("20:25", saved.Loop.GetString("deadline"));

            await Clear(start, window);
            await Clear(deadline, window);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Triggers.Count == 0);
            saved = host.Workflows.LoadSnapshot("wf-time-picker").Document;
            Assert.Null(saved.Loop!.GetString("deadline"));
            panel.Editing!.LoopModeIndex = 0;
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Loop is null);

            parentPopup.IsOpen = false;
            Click(open);
            await Until(window, () => parentPopup.IsOpen);
            await Layout(window);
            Assert.Equal("", start.Value);
            Assert.Equal("", loop.Value);
            Assert.Equal("", deadline.Value);
            Assert.Equal("立即开始", Display(start));
            Assert.Equal("仅循环模式可设置", Display(deadline));
            await Select(start, 0, 0, window, parentPopup);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Triggers.Count == 1);
            Assert.Equal("00:00", host.Workflows.LoadSnapshot("wf-time-picker").Document.Triggers[0].GetString("time"));

            // Single-round plans cannot persist loop-only fields. The UI must explain and prevent discarded edits.
            Assert.False(loop.IsEnabled);
            Assert.False(deadline.IsEnabled);
            Assert.Equal("仅每天到点循环可设置", Display(loop));
            Assert.Equal("仅循环模式可设置", Display(deadline));
            var loopMode = Descendants<ComboBox>(form).Single(combo =>
                BindingOperations.GetBindingExpression(combo, Selector.SelectedIndexProperty)?.ParentBinding.Path?.Path
                    == nameof(WorkflowEditVm.LoopModeIndex));
            loopMode.SelectedIndex = 1;
            await Layout(window);
            Assert.True(loop.IsEnabled);
            Assert.True(deadline.IsEnabled);
            await Select(loop, 14, 35, window, parentPopup);
            await Select(deadline, 21, 5, window, parentPopup);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Loop?.GetString("time") == "14:35");
            saved = host.Workflows.LoadSnapshot("wf-time-picker").Document;
            Assert.Equal("scheduled", saved.Loop!.Mode);
            Assert.Equal("21:05", saved.Loop.GetString("deadline"));
            parentPopup.IsOpen = false;
            Click(open);
            await Until(window, () => parentPopup.IsOpen);
            await Layout(window);
            Assert.Equal("14:35", loop.Value);
            Assert.Equal("21:05", deadline.Value);
            loopMode.SelectedIndex = 2;
            await Layout(window);
            Assert.False(loop.IsEnabled);
            Assert.True(deadline.IsEnabled);
            await Select(deadline, 22, 10, window, parentPopup);
            panel.SaveFlowCommand.Execute(null);
            await Until(window, () => host.Workflows.LoadSnapshot("wf-time-picker").Document.Loop?.Mode == "immediate");
            Assert.Equal("22:10", host.Workflows.LoadSnapshot("wf-time-picker").Document.Loop!.GetString("deadline"));
        }
        finally
        {
            panel.StopAutoRefresh();
            window.Close();
            await host.ShutdownAsync();
        }
    });

    [Fact]
    public Task ExistingLogPicker_KeepsLiveSecondsAndTwoWayBinding() => OnSta(async () =>
    {
        var source = new PickerSource { Value = "12:34:56" };
        var picker = new TimeOfDayPicker();
        picker.SetBinding(TimeOfDayPicker.ValueProperty, new Binding(nameof(PickerSource.Value))
        {
            Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        var window = TestWindow(picker);
        try
        {
            window.Show();
            await Open(picker, window);
            Assert.True(picker.ShowSeconds);
            Assert.False(picker.AllowEmpty);
            Assert.False(picker.CommitOnSubmit);
            Assert.Equal(Visibility.Visible, ((StackPanel)picker.FindName("SecondsColumn")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((Button)picker.FindName("ClearButton")).Visibility);
            ((ListBox)picker.FindName("SecondList")).SelectedIndex = 57;
            await Layout(window);
            Assert.Equal("12:34:57", source.Value);
            ((ListBox)picker.FindName("HourList")).SelectedIndex = 13;
            await Layout(window);
            Assert.Equal("13:34:57", source.Value);
            Assert.NotNull(BindingOperations.GetBindingExpression(picker, TimeOfDayPicker.ValueProperty));
            source.Value = "00:01:02";
            await Layout(window);
            Assert.Equal(0, ((ListBox)picker.FindName("HourList")).SelectedIndex);
            Assert.Equal(1, ((ListBox)picker.FindName("MinuteList")).SelectedIndex);
            Assert.Equal(2, ((ListBox)picker.FindName("SecondList")).SelectedIndex);
        }
        finally { window.Close(); }
    });

    private static Window TestWindow(FrameworkElement content) => new()
    {
        Content = content, Width = 1080, Height = 800,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        ShowInTaskbar = false, ShowActivated = false,
    };

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Open(TimeOfDayPicker picker, Window window)
    {
        // Supply a marker resource to prove the list's internal viewer consumes the shared theme key.
        var theme = new Style(typeof(ScrollViewer));
        theme.Setters.Add(new Setter(FrameworkElement.TagProperty, "shared-gold-scroll-viewer"));
        var names = picker.ShowSeconds ? new[] { "HourList", "MinuteList", "SecondList" } : new[] { "HourList", "MinuteList" };
        foreach (var name in names) ((ListBox)picker.FindName(name)).Resources["GoldScrollViewer"] = theme;
        ((ToggleButton)picker.FindName("DropButton")).IsChecked = true;
        await Until(window, () => ((Popup)picker.FindName("PickerPopup")).IsOpen);
        await Layout(window);
        foreach (var name in names)
        {
            var list = (ListBox)picker.FindName(name);
            var viewer = Assert.IsType<ScrollViewer>(list.Template.FindName("TimeListScrollViewer", list));
            Assert.Same(theme, viewer.Style);
        }
    }

    private static async Task Select(TimeOfDayPicker picker, int hour, int minute, Window window, Popup parent)
    {
        var before = picker.Value;
        await Open(picker, window);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)picker.FindName("SecondsColumn")).Visibility);
        Assert.Equal(Visibility.Visible, ((Button)picker.FindName("ClearButton")).Visibility);
        ((ListBox)picker.FindName("HourList")).SelectedIndex = hour;
        ((ListBox)picker.FindName("MinuteList")).SelectedIndex = minute;
        Assert.Equal(before, picker.Value);
        Assert.True(parent.IsOpen);
        var popup = (Popup)picker.FindName("PickerPopup");
        Click(Descendants<Button>(popup.Child).Single(button => Equals(button.Content, "完成")));
        await Layout(window);
        Assert.Equal($"{hour:D2}:{minute:D2}", picker.Value);
        Assert.False(popup.IsOpen);
        Assert.True(parent.IsOpen);
        Assert.NotNull(BindingOperations.GetBindingExpression(picker, TimeOfDayPicker.ValueProperty));
    }

    private static async Task Clear(TimeOfDayPicker picker, Window window)
    {
        await Open(picker, window);
        Click((Button)picker.FindName("ClearButton"));
        await Layout(window);
        Assert.Equal("", picker.Value);
        Assert.NotNull(BindingOperations.GetBindingExpression(picker, TimeOfDayPicker.ValueProperty));
    }

    private static string Display(TimeOfDayPicker picker)
    {
        var drop = (ToggleButton)picker.FindName("DropButton");
        return ((TextBlock)drop.Template.FindName("ValueText", drop)).Text;
    }

    private static async Task Layout(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task Until(Window window, Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "The isolated popup/save operation did not complete.");
            await Layout(window);
            await Task.Delay(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)), "The isolated popup dispatcher did not exit.");
    }

    private sealed class PickerSource : INotifyPropertyChanged
    {
        private string _value = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
    }
}
