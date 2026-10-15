using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class QuickHoeingDialogWindowTests
{
    [Fact]
    public async Task ActualConfirmationDisplaysBoundActionsAndReturnsOnlyTheSelectedSwitch()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
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
                        Assert.True(dialog.ShowDialog());
                        if (failure != null) throw failure;
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
