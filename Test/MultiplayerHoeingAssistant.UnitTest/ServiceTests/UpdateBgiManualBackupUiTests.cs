using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// 实际显示正式 WPF 窗口，验证卡片尺寸约束和 XAML 点击入口，不以服务单测替代 UI 证据。
[Collection("RegisterBankWpf")]
public sealed class UpdateBgiManualBackupUiTests
{
    [Fact]
    public async Task ManualBackup_PreservesCardSize_AndWorksWithoutUpdateSelectionOrToggle() => await OnSta(async () =>
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须以 NEXUSBGI_DATA_ROOT 隔离真实用户数据。");
        var root = Path.Combine(AssistantDataDirectory.Root, "manual-backup-ui-" + Guid.NewGuid().ToString("N"));
        foreach (var directory in new[] { "User", "Tools", "log" })
            Directory.CreateDirectory(Path.Combine(root, directory));
        File.WriteAllText(Path.Combine(root, "User", "config.json"), "ISOLATED-USER-DATA");
        var vm = new MainViewModel();
        var config = new AssistConfig
        {
            BgiPath = Path.Combine(root, "BetterGI.exe"), BgiPackageDir = Path.Combine(root, "packages"),
            BgiUpdateSourceId = BgiUpdateSources.LocalId, BgiPickedPackages = [],
            BgiUpdateBackupDirs = ["User", "Tools", "log"], BgiUpdateBackupBeforeUpdate = false
        };
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, config);
        var window = new UpdateBgiWindow(vm) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var button = (Button)window.FindName("ManualBackupButton");
            var checkbox = (CheckBox)window.FindName("BackupCheckBox");
            var row = (Grid)checkbox.Parent;
            var panel = (StackPanel)row.Parent;
            var card = (Border)panel.Parent;
            var index = panel.Children.IndexOf(row);
            foreach (var width in new[] { 950d, 520d })
            {
                window.Width = width;
                await Layout(window);
                var size = new Size(card.ActualWidth, card.ActualHeight);
                Assert.True(button.IsVisible);
                Assert.True(button.IsEnabled);
                var bounds = button.TransformToAncestor(card).TransformBounds(new Rect(button.RenderSize));
                Assert.InRange(bounds.Right, 0, card.ActualWidth);
                Assert.InRange(bounds.Bottom, 0, card.ActualHeight);
                var label = (TextBlock)window.FindName("ManualBackupButtonText");
                Assert.True(label.ActualWidth >= label.DesiredSize.Width);
                Capture(card, $"manual-backup-card-{width}");

                // 在同一正式窗口还原原复选框行，直接比较卡片宽高，避免字体/DPI 假设。
                row.Children.Remove(checkbox);
                panel.Children.Remove(row);
                checkbox.Margin = new Thickness(0, 6, 0, 0);
                panel.Children.Insert(index, checkbox);
                await Layout(window);
                Assert.Equal(size.Width, card.ActualWidth, 2);
                Assert.Equal(size.Height, card.ActualHeight, 2);
                panel.Children.Remove(checkbox);
                checkbox.Margin = new Thickness(0);
                row.Children.Insert(0, checkbox);
                panel.Children.Insert(index, row);
            }
            await Layout(window);
            Assert.False(checkbox.IsChecked);
            Assert.Null(((ListBox)window.FindName("PackageList")).SelectedItem);
            int dialogs = 0;
            var observed = new ConcurrentDictionary<string, byte>();
            var uiThread = GetCurrentThreadId();
            using var stopClosing = new CancellationTokenSource();
            // 原生模态框不依赖 WPF DispatcherTimer；从工作线程只枚举本测试 STA 的窗口。
            var closer = Task.Run(async () =>
            {
                while (!stopClosing.IsCancellationRequested)
                {
                    EnumThreadWindows(uiThread, (handle, _) =>
                    {
                        var title = new StringBuilder(256);
                        GetWindowText(handle, title, title.Capacity);
                        observed.TryAdd(title.ToString(), 0);
                        if (title.ToString() == "手动备份")
                        {
                            Interlocked.Increment(ref dialogs);
                            PostMessage(handle, 0x0010, 0, 0); // WM_CLOSE：只有“确定”的结果框。
                        }
                        return true;
                    }, 0);
                    await Task.Delay(50);
                }
            });
            try
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(button.IsEnabled);
                Assert.False(((Button)window.FindName("UpdateButton")).IsEnabled);
                Assert.False(((Button)window.FindName("AddBackupButton")).IsEnabled);
                Assert.Equal("备份中…", ((TextBlock)window.FindName("ManualBackupButtonText")).Text);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!button.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(50);
                Assert.True(button.IsEnabled, $"结果框关闭次数={dialogs}；窗口={string.Join(" | ", observed.Keys)}；状态={((TextBlock)window.FindName("StatusText")).Text}");
                Assert.True(dialogs > 0);
                Assert.Equal("立即备份", ((TextBlock)window.FindName("ManualBackupButtonText")).Text);
                Assert.Equal("手动备份完成。", ((TextBlock)window.FindName("StatusText")).Text);
                var backup = Assert.Single(Directory.GetDirectories(Path.Combine(root, "_update_backup")));
                Assert.DoesNotContain(".incomplete", backup);
                Assert.Equal("ISOLATED-USER-DATA", File.ReadAllText(Path.Combine(backup, "User", "config.json")));
                Assert.False(File.Exists(Path.Combine(root, "BetterGI.exe")));
                Assert.False(checkbox.IsChecked);
            }
            finally
            {
                stopClosing.Cancel();
                await closer;
            }
        }
        finally
        {
            window.Close();
            Directory.Delete(root, recursive: true);
        }
    });

    private static async Task Layout(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Capture(FrameworkElement visual, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth * 2, (int)visual.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 32, 63)), null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        png.Save(output);
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
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
