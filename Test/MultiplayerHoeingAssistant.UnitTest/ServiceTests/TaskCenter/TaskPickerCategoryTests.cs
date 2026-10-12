using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskPickerCategoryTests
{
    [Fact]
    public async Task AddTaskDialog_SeparatesCategories_AndAddsSelectedSingleTask()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? owner = null;
            DispatcherTimer? timer = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "task-picker-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
                    Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "分类添加", Nodes = [new() { NodeId = "end", Kind = "control.end" }] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.EditFlowCommand.Execute(vm.Flows.Single());
                var draft = vm.Editing!;
                draft.AppendSources.Add(Source(TaskCenterResourceKind.ConfigGroup, "group:清体", "清体"));
                draft.AppendSources.Add(Source(TaskCenterResourceKind.OneDragonConfig, "onedragon:每日", "每日"));
                for (var i = 0; i < 20; i++)
                    draft.AppendSources.Add(Source(TaskCenterResourceKind.SingleTask,
                        $"single:清体:task-{i}", $"任务{i:00}", "清体"));

                var view = new ScheduleListView { DataContext = vm };
                owner = new Window { Content = view, Width = 1000, Height = 650,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                var goldViewer = new Style(typeof(ScrollViewer));
                owner.Resources["GoldScrollViewer"] = goldViewer;
                owner.Show();
                owner.UpdateLayout();
                var dialogOwner = owner;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                Exception? dialogError = null;
                timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(20),
                };
                timer.Tick += (_, _) =>
                {
                    var dialog = dialogOwner.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.Title == "添加任务");
                    if (dialog is null)
                    {
                        if (DateTime.UtcNow < deadline) return;
                        dialogError = new TimeoutException("添加任务弹窗未打开。");
                        timer.Stop();
                        return;
                    }
                    timer.Stop();
                    try
                    {
                        dialog.UpdateLayout();
                        Assert.Same(goldViewer, dialog.Resources[typeof(ScrollViewer)]);
                        var pick = Find<ListBox>(dialog, box => box.Name == "TaskPickerChoices")!;
                        var next = Find<Button>(dialog, button => button.Name == "TaskPickerNext")!;
                        var search = Find<TextBox>(dialog, box => box.Name == "TaskPickerSearch")!;
                        var time = Find<TextBox>(dialog, box => box.Name == "TaskPickerTime")!;
                        Assert.NotNull(pick);
                        Assert.NotNull(next);
                        Assert.NotNull(search);
                        Assert.Equal(Visibility.Collapsed, time.Visibility);
                        Assert.Same(goldViewer, Find<ScrollViewer>(pick)!.Style);

                        Assert.Equal(new[] { "清体" }, Labels(pick));
                        Assert.False(next.IsEnabled);
                        Category(dialog, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(new[] { "每日" }, Labels(pick));
                        Category(dialog, 2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(20, pick.Items.Count);
                        Assert.All(Labels(pick), label => Assert.StartsWith("清体 / 任务", label));
                        dialog.UpdateLayout();
                        Assert.Equal(Visibility.Visible, Find<ScrollBar>(pick, bar => bar.Orientation == Orientation.Vertical)!.Visibility);
                        var scroller = Find<ScrollViewer>(pick)!;
                        Assert.True(scroller.ScrollableHeight > 0);
                        scroller.ScrollToBottom();
                        dialog.UpdateLayout();
                        Assert.True(scroller.VerticalOffset > 0);
                        search.Text = "任务07";
                        Assert.Equal(new[] { "清体 / 任务07" }, Labels(pick));
                        Category(dialog, 3).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Contains("动作 · 结束流程", Labels(pick));
                        Assert.DoesNotContain("清体", Labels(pick));
                        Category(dialog, 2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(20, pick.Items.Count);
                        pick.SelectedIndex = 7;
                        Assert.True(next.IsEnabled);
                        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(Visibility.Visible, time.Visibility);
                        time.Text = "12:30";
                        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal("确认添加", next.Content);
                        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { dialogError = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                };
                timer.Start();
                var add = Find<Button>(view, button => Equals(button.Content, "＋ 添加任务"));
                Assert.NotNull(add);
                add!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (dialogError is not null) throw dialogError;
                var added = Assert.Single(draft.Nodes, node => node.Model.Kind == "resource.singleTask");
                Assert.Equal("清体", added.Model.Ref?.Config);
                Assert.Equal("task-7", added.Model.Ref?.TaskId);
                Assert.Equal(750, added.ScheduleMinute);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { timer?.Stop(); owner?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static CatalogSourceVm Source(TaskCenterResourceKind kind, string stableId, string name, string? owner = null)
        => new(new TaskCenterResourceEntry
        {
            Kind = kind, StableId = stableId, DisplayName = name, OwnerConfig = owner,
            ConfigRevision = "r1", Enabled = true, SingleExecutionSupported = true,
        });

    private static Button Category(DependencyObject root, int index)
        => Find<Button>(root, button => button.Name == $"TaskPickerCategory{index}")!;

    private static string[] Labels(ListBox pick)
        => pick.Items.Cast<object>().Select(item => item.ToString()!).ToArray();

    private static T? Find<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match && (predicate is null || predicate(match))) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Find(VisualTreeHelper.GetChild(root, i), predicate);
            if (child is not null) return child;
        }
        return null;
    }
}
