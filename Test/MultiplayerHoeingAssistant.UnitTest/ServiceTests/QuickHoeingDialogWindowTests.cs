using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class QuickHoeingDialogWindowTests
{
    [Fact]
    public async Task ActualConfirmationDisplaysBoundActionsAndReturnsOnlyTheSelectedSwitch()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        Directory.CreateDirectory(AssistantDataDirectory.Root);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            app.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var members = new QuickHoeingConfirmationMember[]
                    {
                        new("a", "成员甲", "联机锄地", new("resume", "group", "", 0)),
                        new("b", "成员乙", "联机锄地 → 拾取", new("stop", "group", "", 0)),
                        new("c", "成员丙", "联机锄地", new("runSpecified", "group", "绑定中的收尾任务", 0)),
                        new("d", "成员丁", string.Join(" → ", Enumerable.Repeat("很长的绑定任务名称", 12)), new("runSpecified", "onedragon", "绑定中的一条龙", 0))
                    };
                    foreach (var selected in new[] { false, true })
                    {
                        var dialog = Create(members, true);
                        Assert.False(dialog.UseCompletionActions);
                        Exception? failure = null;
                        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            try
                            {
                                dialog.CompletionOption.IsChecked = selected;
                                dialog.UpdateLayout();
                                var text = All<TextBlock>(dialog).Select(t => t.Text).ToArray();
                                foreach (var member in members)
                                    Assert.Contains("锄地后动作：" + member.CompletionAction!.Description, text);
                                Assert.True(dialog.ConfirmButton.IsVisible);
                                Assert.True(dialog.ConfirmButton.ActualHeight > 0);
                                var point = dialog.ConfirmButton.TransformToAncestor(dialog).Transform(new Point());
                                Assert.True(point.Y + dialog.ConfirmButton.ActualHeight <= dialog.ActualHeight);
                                Save(dialog, Path.Combine(AssistantDataDirectory.Root, selected ? "quick-hoeing-checked.png" : "quick-hoeing-default.png"));
                                dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception error) { failure = error; dialog.Close(); }
                        }));
                        var dialogResult = dialog.ShowDialog();
                        if (failure != null) throw failure;
                        Assert.True(dialogResult);
                        Assert.True(dialog.Confirmed);
                        Assert.Equal(selected, dialog.UseCompletionActions);
                    }
                    var unsupported = Create(members, false);
                    Assert.False(unsupported.CompletionOption.IsEnabled);
                    Assert.False(unsupported.UseCompletionActions);
                    unsupported.Close();
                    var missing = Create([new("old", "旧助手", "锄地", null)], true);
                    Assert.False(missing.CompletionOption.IsEnabled);
                    missing.Close();
                    AssertQuickCommandBindingDialogs(app);
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { app.Shutdown(); }
            });
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static QuickHoeingConfirmationWindow Create(IReadOnlyList<QuickHoeingConfirmationMember> members, bool capable)
        => new(members, capable) { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };

    private static void AssertQuickCommandBindingDialogs(Application app)
    {
        string[] keys = ["一键传奇", "一键次数盾", "一键精英", "一键小怪", "一键自定义"];
        var owner = new Window { Width = 1, Height = 1, Opacity = 0 };
        Prepare(owner);
        app.MainWindow = owner;
        owner.Show();
        var vm = new MainViewModel();
        var config = new AssistConfig
        {
            PlayerUid = "self", OnlineHoeingCompletionPolicy = "runSpecified",
            OnlineHoeingSpecifiedTaskType = "onedragon", OnlineHoeingSpecifiedTaskName = "绑定中的一条龙",
            QuickCommands = keys.ToDictionary(key => key, _ => "GROUP:本机快捷任务")
        };
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, config);
        var gateway = new MhaGatewayClient();
        typeof(MhaGatewayClient).GetProperty(nameof(MhaGatewayClient.ServerCapabilities))!.SetValue(gateway,
            new[] { GatewayProtocol.QuickCommandCompletionCapability, GatewayProtocol.CompletionChoiceCapability });
        var client = new SignalRClient();
        typeof(SignalRClient).GetField("_gateway", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, gateway);
        typeof(MainViewModel).GetField("_signalRClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, client);
        Assert.False(client.IsConnected);
        var actions = new CoordinatedCompletionAction[]
        {
            new("runSpecified", "onedragon", "绑定中的一条龙", 0),
            new("stop", "group", "", 0), new("resume", "group", "", 0),
            new("runSpecified", "group", string.Concat(Enumerable.Repeat("很长的绑定任务名称", 6)), 0)
        };
        for (var i = 0; i < actions.Length; i++)
        {
            vm.Members.Add(new MemberViewModel
            {
                PlayerUid = i == 0 ? "self" : $"member-{i}", PlayerName = $"成员{i + 1}",
                Online = true, IsSelected = true,
                QuickCommands = keys.ToDictionary(key => key, _ => "GROUP:成员快捷任务"),
                HoeingCompletionAction = i == 0 ? null : actions[i]
            });
        }
        foreach (var key in keys)
        {
            foreach (var selected in new[] { false, true })
            {
                var dialog = vm.CreateQuickCommandBindingWindow(key, vm.Members.ToArray());
                Prepare(dialog);
                Assert.True(dialog.CompletionChoice.CompletionOption.IsEnabled);
                Assert.False(dialog.UseCompletionActions);
                Exception? failure = null;
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        dialog.CompletionChoice.CompletionOption.IsChecked = selected;
                        dialog.UpdateLayout();
                        var text = All<TextBlock>(dialog).Select(t => t.Text).ToArray();
                        foreach (var action in actions) Assert.Contains("锄地后动作：" + action.Description, text);
                        Assert.True(dialog.ConfirmButton.IsVisible);
                        Assert.True(dialog.ConfirmButton.ActualHeight > 0);
                        var confirm = dialog.ConfirmButton.TransformToAncestor(dialog).Transform(new Point());
                        var option = dialog.CompletionChoice.TransformToAncestor(dialog).Transform(new Point());
                        Assert.True(confirm.Y + dialog.ConfirmButton.ActualHeight <= dialog.ActualHeight);
                        Assert.True(option.Y + dialog.CompletionChoice.ActualHeight <= confirm.Y);
                        if (key == keys[0] && selected)
                            Save(dialog, Path.Combine(AssistantDataDirectory.Root, "quick-command-completion-checked.png"));
                        dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                }));
                var result = dialog.ShowDialog();
                if (failure != null) throw failure;
                Assert.True(result);
                Assert.Equal(selected, dialog.UseCompletionActions);
            }

            // Exercise the real modal entry: cancellation must return before its execution helper runs.
            var logCount = vm.CommandLogs.Count;
            Exception? cancelFailure = null;
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = app.Windows.OfType<QuickCommandBindingWindow>().Single();
                try
                {
                    Prepare(dialog);
                    dialog.CompletionChoice.CompletionOption.IsChecked = true;
                    All<Button>(dialog).Single(button => Equals(button.Content, "取消"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(dialog.DialogResult);
                }
                catch (Exception error) { cancelFailure = error; dialog.Close(); }
            }));
            var pending = (Task)typeof(MainViewModel).GetMethod("ShowQuickCommandBindForMembersDialog",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [key])!;
            pending.GetAwaiter().GetResult();
            if (cancelFailure != null) throw cancelFailure;
            Assert.Equal(logCount, vm.CommandLogs.Count);
        }

        typeof(MhaGatewayClient).GetProperty(nameof(MhaGatewayClient.ServerCapabilities))!.SetValue(gateway, Array.Empty<string>());
        foreach (var key in keys)
        {
            var dialog = vm.CreateQuickCommandBindingWindow(key, vm.Members.ToArray());
            Assert.False(dialog.CompletionChoice.CompletionOption.IsEnabled);
            dialog.CompletionChoice.CompletionOption.IsChecked = true;
            Assert.False(dialog.UseCompletionActions);
            dialog.Close();
        }
        owner.Close();
    }

    private static void Prepare(Window dialog)
    {
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = false;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -20000;
        dialog.Top = -20000;
    }

    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in All<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
}
