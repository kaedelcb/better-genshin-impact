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
        var stage = "启动 STA 测试线程";
        void Mark(string value) => Volatile.Write(ref stage, value);
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var config = new AssistConfig
                {
                    DisclaimerAccepted = true,
                    ObserverMode = true, // 本机画面走远端，测试绝不截取当前用户桌面
                    StandaloneMode = false,
                    ServerUrl = "http://127.0.0.1:1", // 不调用 InitializeAsync，不会建立连接
                    PlayerUid = "owner",
                };
                new AssistConfigManager().Save(config);
                Mark("加载应用资源");
                app = CreateResourceApp();
                app.InitializeComponent();
                Mark("构造 MainViewModel");
                var mainVm = new MainViewModel();
                // MainViewModel 构造后才在 InitializeAsync 加载配置；此 UI 冒烟不启动服务，
                // 只注入隔离配置以让本机画面走监控模式远端路径，避免触碰当前桌面。
                typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(mainVm, config);
                Assert.True(mainVm.IsObserverMode);
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "owner", PlayerName = "执行端", IsSelf = true });
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "uidB", PlayerName = "成员 B" });
                mainVm.Members.Add(new MemberViewModel { PlayerUid = "uidC", PlayerName = "成员 C" });
                Mark("构造真实 MainWindow");
                window = new MainWindow(mainVm) { Width = 1180, Height = 820, ShowInTaskbar = false };
                var clock = new MonitorTimeProvider();
                typeof(ScreenshotViewModel).GetField("_timeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(window.Dodoco.Monitor, clock);
                mainVm.CurrentPage = AppPage.Dodoco;
                window.Dodoco.SelectedTabIndex = 4;
                Mark("显示真实窗口并布局");
                window.Show();
                window.UpdateLayout();
                Mark("验证画面墙与成员卡片");

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
                        RequestId = MemberScreenshotRelayService.CreateRequestId(monitor.SessionId!),
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

                Mark("验证复用耕地机快捷键弹窗");
                mainVm.Members.Single(m => m.PlayerUid == "uidB").Hotkeys =
                [System.Text.Json.JsonSerializer.SerializeToElement(new
                    { configName = "SuspendHotkey", functionName = "暂停当前脚本/独立任务", hotkeyText = "D9" })];
                var hotkeyDialogShown = false;
                Exception? hotkeyDialogError = null;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    Window? dialog = null;
                    try
                    {
                        dialog = app.Windows.OfType<Window>().Single(w => w.Title == "选择快捷键");
                        var panel = Assert.IsType<StackPanel>(dialog.Content);
                        var list = panel.Children.OfType<ListBox>().Single();
                        Assert.Equal("暂停当前脚本/独立任务 (D9)", list.Items[0]);
                        hotkeyDialogShown = true;
                        var cancel = panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>())
                            .Single(b => Equals(b.Content, "取消"));
                        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { hotkeyDialogError = ex; dialog?.Close(); }
                }));
                var hotkeyButton = Assert.IsType<Button>(page.FindName("MonitorBgiHotkeyButton"));
                hotkeyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(hotkeyDialogError);
                Assert.True(hotkeyDialogShown, "监控页必须复用原快捷键选择器，取消不得发出任何输入或任务指令。");

                Mark("验证桌面控制真实 UI 入口");
                DesktopMonitorControlTests.VerifyProductionControlUi(window, page, mainVm, clock, clock.Advance, stream.ToArray());
                var retainedFrameCount = monitor.RemoteFrameCount;
                monitor.SelectedMonitor = monitor.MonitorOptions.Single(option => option.Key == ScreenshotViewModel.OverviewKey);
                config.PlayerUid = string.Empty;
                monitor.RefreshForObserverModeChange();
                Assert.Equal("未配置本机 UID，无法向执行端取图",
                    monitor.OverviewTiles.Single(tile => tile.Key == ScreenshotViewModel.LocalKey).Info);
                Assert.Equal(retainedFrameCount, monitor.RemoteFrameCount); // 监控端缺 UID 时不截当前桌面

                // 验证实际页面入口的三分钟约定；推进单调时钟，不等待三分钟、不截当前桌面。
                // 自动刷新关闭时仍必须主动提示，重复激活/配置重载/切成员不能续期。
                var sessionId = monitor.SessionId!;
                Assert.True(Guid.TryParseExact(sessionId, "N", out _));
                var staleFrame = new MemberScreenshotFrame
                {
                    Uid = "uidB", RequestId = MemberScreenshotRelayService.CreateRequestId(sessionId),
                    JpegBase64 = frameBase64, Width = 1280, Height = 720, CapturedAt = DateTime.UtcNow,
                };
                clock.Advance(TimeSpan.FromMinutes(2));
                monitor.OnTabActivated();
                monitor.SelectedMonitor = monitor.MonitorOptions.Single(option => option.Key == "uidB");
                monitor.ReloadConfigTransferSettings();
                monitor.RefreshForObserverModeChange();
                Assert.Equal(sessionId, monitor.SessionId);
                Assert.DoesNotContain("已满", monitor.PrivacyNote);
                clock.Advance(TimeSpan.FromMinutes(1));
                Mark("调度首次到期回调");
                PumpDispatcher();
                Mark("验证到期拒绝刷新与 Tab 重入");
                Assert.Contains("已满 3 分钟", monitor.StatusText);
                Assert.Contains("退出后重新进入", monitor.PrivacyNote);
                Assert.False(refreshTimer.IsEnabled);
                monitor.OnTabActivated();
                monitor.AutoRefresh = true;
                monitor.IntervalIndex = 0;
                monitor.ReloadConfigTransferSettings();
                monitor.SelectedMonitor = monitor.MonitorOptions.Single(option => option.Key == "uidC");
                monitor.CaptureCommand.Execute(null);
                monitor.OnRemoteFrame(staleFrame);
                Assert.Equal(sessionId, monitor.SessionId);
                Assert.False(refreshTimer.IsEnabled);
                Assert.Equal(retainedFrameCount, monitor.RemoteFrameCount);
                Assert.True(monitor.HasFrame); // 过期后依然可以查看历史画面
                Assert.Single(monitor.Frames);

                // 切 Tab 才是新会话；旧会话响应不能进入新历史。
                window.Dodoco.SelectedTabIndex = 0;
                Assert.Null(monitor.SessionId);
                monitor.OnRemoteFrame(staleFrame);
                monitor.CaptureCommand.Execute(null);
                Assert.Equal(retainedFrameCount, monitor.RemoteFrameCount);
                window.Dodoco.SelectedTabIndex = 4;
                Assert.NotEqual(sessionId, monitor.SessionId);
                Assert.DoesNotContain("已满", monitor.PrivacyNote);
                monitor.OnRemoteFrame(staleFrame);
                Assert.Equal(retainedFrameCount, monitor.RemoteFrameCount);
                staleFrame.RequestId = MemberScreenshotRelayService.CreateRequestId(monitor.SessionId!);
                monitor.OnRemoteFrame(staleFrame);
                Assert.Equal(retainedFrameCount + 1, monitor.RemoteFrameCount);

                // 离开整个嘟嘟可页面同样取消会话；排队中的旧超时回调不能结束新会话。
                sessionId = monitor.SessionId!;
                clock.Advance(TimeSpan.FromMinutes(3));
                mainVm.CurrentPage = AppPage.Home;
                Assert.Null(monitor.SessionId);
                Assert.False(refreshTimer.IsEnabled);
                mainVm.CurrentPage = AppPage.Dodoco;
                Assert.NotEqual(sessionId, monitor.SessionId);
                Mark("调度重入前排队的旧到期回调");
                PumpDispatcher();
                Mark("验证重入后手动截止检查");
                Assert.DoesNotContain("已满", monitor.PrivacyNote);
                monitor.AutoRefresh = false;
                clock.Advance(TimeSpan.FromMinutes(3));
                // 尚未调度 timer 回调时，手动入口也必须自行检查截止时间。
                monitor.CaptureCommand.Execute(null);
                Assert.Contains("已满 3 分钟", monitor.StatusText);
                Assert.Equal(retainedFrameCount + 1, monitor.RemoteFrameCount);
                Mark("调度末次到期回调");
                PumpDispatcher();
                Mark("释放监控定时器");
                monitor.Dispose();
                Assert.Equal(0, clock.ActiveTimerCount);
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
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"桌面监控真实页面测试超时，停留阶段：{Volatile.Read(ref stage)}", ex);
        }
    }

    [Fact]
    public void RelayRequestIds_KeepSessionPrefix_AndHaveUniqueRequestTokens()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var first = MemberScreenshotRelayService.CreateRequestId(sessionId);
        var second = MemberScreenshotRelayService.CreateRequestId(sessionId);
        Assert.StartsWith($"screen:{sessionId}:", first);
        Assert.StartsWith($"screen:{sessionId}:", second);
        Assert.Equal(72, first.Length);
        Assert.True(Guid.TryParseExact(first.Split(':')[2], "N", out _));
        Assert.NotEqual(first, second);
        Assert.Throws<ArgumentException>(() => MemberScreenshotRelayService.CreateRequestId("invalid"));
    }

    // 保留真实 App 类型以加载编译后的 app.xaml；仅取消构造时排队的启动操作，
    // 避免验证监控 timer 时启动托盘、单例检测和本测试未请求的业务。
    private static App CreateResourceApp()
    {
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        var operations = new List<DispatcherOperation>();
        void Posted(object? sender, DispatcherHookEventArgs e) => operations.Add(e.Operation);
        App app;
        hooks.OperationPosted += Posted;
        try { app = new App(); }
        finally { hooks.OperationPosted -= Posted; }
        var startup = Assert.Single(operations.Where(op => op.Priority == DispatcherPriority.Send));
        Assert.True(startup.Abort(), "必须在消息泵运行前隔离应用启动操作。");
        return app;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        // 会话 timer 使用默认 Normal 优先级 BeginInvoke。FIFO 标记只等待已排队的
        // 同优先级回调，无需进入 Loaded/Render/Background 等低优先级的整窗空闲循环。
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class MonitorTimeProvider : TimeProvider
    {
        private long _ticks;
        private readonly List<MonitorTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public int ActiveTimerCount => _timers.Count(timer => !timer.Disposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new MonitorTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _ticks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class MonitorTimer(MonitorTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due;
            private bool _fired;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                _due = clock._ticks + dueTime.Ticks;
                _fired = false;
                return true;
            }
            public void FireIfDue()
            {
                if (Disposed || _fired || clock._ticks < _due) return;
                _fired = true;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
                yield return child;
    }
}
