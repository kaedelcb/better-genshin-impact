using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>在隔离数据根加载真实桌面监控页，验证多成员画面墙和卡片入口。</summary>
public sealed class DesktopMonitorOverviewTests
{
    [Fact]
    public async Task ProductionPage_ShowsAllMemberTiles_UpdatesFrame_AndOpensDetail()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须指定隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            try
            {
                var config = new AssistConfig
                {
                    DisclaimerAccepted = true,
                    ObserverMode = true, // 本机画面走远端，测试绝不截取当前用户桌面
                    StandaloneMode = false,
                    ServerUrl = "http://127.0.0.1:1", // 不调用 InitializeAsync，不会建立连接
                    PlayerUid = "owner",
                };
                new AssistConfigManager().Save(config);
                app = new App();
                app.InitializeComponent();
                var mainVm = new MainViewModel();
                // MainViewModel 构造后才在 InitializeAsync 加载配置；此 UI 冒烟不启动服务，
                // 只注入隔离配置以让本机画面走监控模式远端路径，避免触碰当前桌面。
                typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(mainVm, config);
                Assert.True(mainVm.IsObserverMode);
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "owner", PlayerName = "执行端", IsSelf = true });
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "uidB", PlayerName = "成员 B" });
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "uidC", PlayerName = "成员 C" });
                window = new MainWindow(mainVm) { Width = 1180, Height = 820, ShowInTaskbar = false };
                mainVm.CurrentPage = AppPage.Dodoco;
                window.Dodoco.SelectedTabIndex = 4;
                window.Show();
                window.UpdateLayout();

                var page = Descendants(window).OfType<DodocoPage>().Single();
                var monitor = window.Dodoco.Monitor;
                Assert.Equal(ScreenshotViewModel.OverviewKey, monitor.SelectedMonitor?.Key);
                var sourceSelector = Descendants(page).OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, monitor.MonitorOptions));
                Assert.Equal("所有成员（同屏）", sourceSelector.SelectionBoxItem?.ToString());
                Assert.Equal(3, monitor.OverviewTiles.Count);
                Assert.Empty(monitor.OverviewScreenTiles);
                Assert.Equal(3, monitor.OverviewStatusTiles.Count);
                Assert.All(monitor.OverviewTiles, tile => Assert.Contains("暂无画面", tile.Info));
                Assert.DoesNotContain(Descendants(page).OfType<CheckBox>(), box => Equals(box.Content, "共享我的桌面截图"));

                foreach (var suffix in new[] { "D", "E", "F" })
                    mainVm.Members.Add(new MemberViewModel { PlayerUid = "uid" + suffix, PlayerName = "成员 " + suffix });
                window.UpdateLayout();
                Assert.Equal(6, monitor.OverviewTiles.Count);
                Assert.Equal(6, monitor.OverviewStatusTiles.Count);

                var refreshTimer = (DispatcherTimer)typeof(ScreenshotViewModel)
                    .GetField("_autoTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(monitor)!;
                monitor.AutoRefresh = true;
                Assert.True(refreshTimer.IsEnabled);
                mainVm.CurrentPage = AppPage.Home;
                Assert.False(refreshTimer.IsEnabled);
                mainVm.CurrentPage = AppPage.Dodoco;
                Assert.True(refreshTimer.IsEnabled);
                monitor.AutoRefresh = false;

                mainVm.Members.Single(member => member.PlayerUid == "uidB").Online = true;
                mainVm.Members.Single(member => member.PlayerUid == "uidC").Online = true;
                monitor.RefreshForObserverModeChange();

                using var bitmap = new Bitmap(1280, 720);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Jpeg);
                var frameBase64 = Convert.ToBase64String(stream.ToArray());
                foreach (var uid in new[] { "uidB", "uidC" })
                {
                    monitor.OnRemoteFrame(new MemberScreenshotFrame
                    {
                        Uid = uid,
                        JpegBase64 = frameBase64,
                        Width = 1280,
                        Height = 720,
                        CapturedAt = DateTime.UtcNow,
                    });
                }
                var memberTile = monitor.OverviewTiles.Single(tile => tile.Key == "uidB");
                Assert.NotNull(memberTile.Image);
                Assert.Contains("最近画面", memberTile.Info);
                window.UpdateLayout();

                Assert.Equal(2, monitor.OverviewScreenTiles.Count);
                Assert.Equal(4, monitor.OverviewStatusTiles.Count);
                Assert.Equal(2, monitor.OverviewColumns);
                Assert.Equal(4, monitor.OverviewStatusColumns);
                var screenItems = Descendants(page).OfType<ItemsControl>()
                    .Single(items => ReferenceEquals(items.ItemsSource, monitor.OverviewScreenTiles));
                var statusItems = Descendants(page).OfType<ItemsControl>()
                    .Single(items => ReferenceEquals(items.ItemsSource, monitor.OverviewStatusTiles));
                Assert.Equal(2, Descendants(screenItems).OfType<System.Windows.Controls.Primitives.UniformGrid>().Single().Columns);
                Assert.Equal(4, Descendants(statusItems).OfType<System.Windows.Controls.Primitives.UniformGrid>().Single().Columns);
                var statusScroll = Descendants(page).OfType<ScrollViewer>()
                    .Last(scroll => Descendants(scroll).Any(child => ReferenceEquals(child, statusItems)));
                Assert.True(statusScroll.ActualHeight <= 50, $"离线状态区高度应紧凑，实际 {statusScroll.ActualHeight:F0}");

                var tileBorder = Descendants(page).OfType<Border>()
                    .Single(border => ReferenceEquals(border.DataContext, memberTile) && border.Cursor == Cursors.Hand);
                Assert.True(tileBorder.ActualWidth > 100 && tileBorder.ActualHeight > 220);
                var preview = Descendants(tileBorder).OfType<System.Windows.Controls.Image>().Single();
                var visibleImageWidth = Math.Min(preview.ActualWidth, preview.ActualHeight * 1280.0 / 720.0);
                Assert.True(visibleImageWidth >= tileBorder.ActualWidth * 0.8,
                    $"画面宽度 {visibleImageWidth:F0} 应充分使用卡片宽度 {tileBorder.ActualWidth:F0}");
                var offlineTile = monitor.OverviewStatusTiles.Single(tile => tile.Key == "uidD");
                var offlineBorder = Descendants(page).OfType<Border>()
                    .Single(border => ReferenceEquals(border.DataContext, offlineTile) && border.Cursor == Cursors.Hand);
                Assert.InRange(offlineBorder.ActualHeight, 35, 45);
                tileBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
                Assert.Equal("uidB", monitor.SelectedMonitor?.Key);
                Assert.True(monitor.HasFrame);
                Assert.NotNull(monitor.CurrentImage);

                monitor.SelectedMonitor = monitor.MonitorOptions.Single(option => option.Key == ScreenshotViewModel.OverviewKey);
                config.PlayerUid = string.Empty;
                monitor.RefreshForObserverModeChange();
                Assert.Equal("未配置本机 UID，无法向执行端取图",
                    monitor.OverviewTiles.Single(tile => tile.Key == ScreenshotViewModel.LocalKey).Info);
                Assert.Equal(2, monitor.RemoteFrameCount); // 监控端缺 UID 时不截当前桌面
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                try { window?.Hide(); } catch { }
                try { app?.Shutdown(); } catch { }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }
}
