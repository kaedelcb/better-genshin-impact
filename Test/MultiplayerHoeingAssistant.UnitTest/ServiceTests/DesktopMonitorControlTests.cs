using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BgiCoordinatorServer.Models;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 验证 PC 观看端的授权、帧身份、单次输入及撤销合同。仅替换发送器/时钟/截图来源，
/// 不连接服务器、不调用 SendInput、不截取当前桌面。真实 XAML 入口复用 OverviewTests 的同一个资源 App。
/// </summary>
public sealed class DesktopMonitorControlTests
{
    [Fact]
    public Task ControlStartsOff_RequiresLatestAuthorizedFrame_AndHistoryStaysReadOnly() => RunSta(async () =>
    {
        using var h = new Harness();
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.AllowDesktopControl);
        Assert.Contains("Win+D", h.Monitor.ControlKeys);
        Assert.False(h.Monitor.CanEnableControl);
        h.Deliver(allowed: false);
        h.Monitor.IsControlMode = true;
        Assert.False(h.Monitor.IsControlMode);
        await h.Monitor.SendKeyAsync("Win+D");
        Assert.Empty(h.Inputs);

        h.Deliver();
        Assert.True(h.Monitor.CanEnableControl);
        h.Monitor.IsControlMode = true;
        Assert.True(h.Monitor.CanSendControl);
        h.Monitor.SelectedThumb = h.Monitor.Frames.Last();
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl); // 即使选的是最后一张缩略图，也明确按历史查看。
        h.Monitor.IsControlMode = true;
        await h.Monitor.SendKeyAsync("Enter");
        Assert.Empty(h.Inputs);
        Assert.Single(h.Cancels);

        h.Deliver();
        h.Monitor.IsControlMode = true;
        h.Monitor.SelectedMonitor = h.Monitor.MonitorOptions.Single(option => option.Key == ScreenshotViewModel.OverviewKey);
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl);
        await h.Monitor.SendMouseAsync(new Point(50, 50), new Size(100, 100), new Size(100, 100));
        Assert.Empty(h.Inputs);
    });

    [Theory]
    [InlineData("accepted")]
    [InlineData("denied")]
    [InlineData("error")]
    [InlineData("cancelled")]
    public Task AnyResultConsumesItsFrame_AndNewFrameStillHonorsTwoSeconds(string status) => RunSta(async () =>
    {
        using var h = new Harness { ResultStatus = status };
        var first = h.Deliver();
        h.Monitor.IsControlMode = true;
        await h.Monitor.SendKeyAsync("Win+D");
        var input = Assert.Single(h.Inputs);
        Assert.Equal("uidB", input.Command.TargetUid);
        Assert.False(input.Local);
        Assert.Equal("key", input.Command.Action);
        Assert.Equal("Win+D", input.Command.Key);
        Assert.Equal(first.RequestId, input.Command.FrameId);
        Assert.True(Guid.TryParseExact(input.Command.RequestId, "N", out _));
        Assert.Contains(status, h.Monitor.ControlStatusText);
        Assert.DoesNotContain("系统已接受输入", h.Monitor.ControlStatusText);
        await h.Monitor.SendKeyAsync("Win+D");
        Assert.Single(h.Inputs);

        // 重复上报不能给同一 frameId 重新计时或解锁。
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Monitor.OnRemoteFrame(first);
        Assert.False(h.Monitor.CanSendControl);
        h.Deliver();
        Assert.True(h.Monitor.CanSendControl);
        await h.Monitor.SendKeyAsync("Esc");
        Assert.Equal(2, h.Inputs.Count);
        h.Deliver();
        h.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(h.Monitor.CanSendControl);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(h.Monitor.CanSendControl);
    });

    [Fact]
    public Task ObserverLocalSource_UsesExecutorUid_AndNeverLocalInput() => RunSta(async () =>
    {
        using var h = new Harness();
        h.Monitor.SelectedMonitor = h.Monitor.MonitorOptions.Single(option => option.Key == ScreenshotViewModel.LocalKey);
        h.Deliver(uid: "owner");
        h.Monitor.IsControlMode = true;
        await h.Monitor.SendKeyAsync("Win+D");
        var input = Assert.Single(h.Inputs);
        Assert.Equal("owner", input.Command.TargetUid);
        Assert.False(input.Local);
        Assert.False(h.Monitor.AllowDesktopControl); // 本机接收开关不能冒充远端执行端的授权。
    });

    [Fact]
    public Task ExecutedLocalInput_PausesRefresh_RejectsSecondInput_AndWaits1100ms() => RunSta(async () =>
    {
        using var h = new Harness(local: true) { ResultStatus = "executed" };
        h.Monitor.AllowDesktopControl = true;
        Assert.True(new DodocoSettingsService(h.Root).Current.AllowDesktopControl);
        await h.CaptureLocal();
        Assert.Equal(1, h.Captures);
        Assert.True(h.Monitor.CanEnableControl);
        h.Monitor.IntervalIndex = 3; // 实时 DispatcherTimer 使用 10 秒，测试中的时间由假时钟推进。
        h.Monitor.AutoRefresh = true;
        Assert.True(h.AutoTimer.IsEnabled);
        h.Monitor.IsControlMode = true;
        var sending = h.Monitor.SendKeyAsync("Win+D");
        Assert.True(h.Monitor.ControlBusy);
        Assert.False(h.AutoTimer.IsEnabled);
        Assert.False(h.Monitor.CanRefresh);
        await h.Monitor.SendKeyAsync("Enter");
        h.Monitor.CaptureCommand.Execute(null);
        Assert.Single(h.Inputs);
        Assert.Equal(1, h.Captures);
        Assert.Equal("local", h.Inputs[0].Command.TargetUid);
        Assert.True(h.Inputs[0].Local);
        Assert.Contains("系统已接受输入", h.Monitor.ControlStatusText);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1099));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.False(sending.IsCompleted);
        Assert.Equal(1, h.Captures);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await sending;
        Assert.Equal(2, h.Captures);
        Assert.True(h.Monitor.CanRefresh);
        Assert.True(h.AutoTimer.IsEnabled);
        Assert.False(h.Monitor.CanSendControl);
        h.Clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(h.Monitor.CanSendControl);
        h.Monitor.AllowDesktopControl = false;
        Assert.False(new DodocoSettingsService(h.Root).Current.AllowDesktopControl);
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl);
    });

    [Fact]
    public Task RevokingLocalPermissionOutsideTheView_CancelsPendingInput() => RunSta(async () =>
    {
        using var h = new Harness(local: true) { HoldResult = true };
        h.Monitor.AllowDesktopControl = true;
        await h.CaptureLocal();
        h.Monitor.IsControlMode = true;
        var sending = h.Monitor.SendKeyAsync("Win+D");
        Assert.True(h.Monitor.ControlBusy);
        var settings = (DodocoSettingsService)Field(h.Monitor, "_settings")!;
        Assert.True(settings.TryUpdate(value => value.AllowDesktopControl = false));
        await sending;
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.ControlBusy);
        Assert.False(h.Monitor.CanEnableControl);
        Assert.True(h.Inputs.Single().Cancellation.IsCancellationRequested);
        Assert.Equal("local", h.Inputs[0].Command.TargetUid);
        Assert.Empty(h.Cancels); // 本机立即通道靠 CancellationToken 撤销，不发送远程取消。
        Assert.Contains("无法撤回", h.Monitor.ControlStatusText);
    });

    [Fact]
    public Task RemoteFrameCannotReplaceInputBasis_AndTimeoutDoesNotRetry() => RunSta(async () =>
    {
        using var h = new Harness { HoldResult = true };
        var first = h.Deliver();
        h.Monitor.IsControlMode = true;
        var sending = h.Monitor.SendKeyAsync("Enter");
        var image = h.Monitor.CurrentImage;
        var count = h.Monitor.RemoteFrameCount;
        h.Deliver();
        Assert.Same(image, h.Monitor.CurrentImage);
        Assert.Equal(count, h.Monitor.RemoteFrameCount);
        await h.Monitor.SendKeyAsync("Enter");
        Assert.Single(h.Inputs);
        h.Clock.Advance(DesktopControlProtocol.RequestLifetime);
        await sending;
        Assert.Single(h.Inputs);
        Assert.True(h.Inputs[0].Cancellation.IsCancellationRequested);
        Assert.Contains("超时", h.Monitor.ControlStatusText);
        Assert.Contains("未知", h.Monitor.ControlStatusText);
        Assert.False(h.Monitor.CanSendControl);
        Assert.Single(h.Cancels);
        h.Pending.TrySetResult(new DesktopControlResult
        {
            RequestId = h.Inputs[0].Command.RequestId, TargetUid = "uidB", Status = "executed"
        });
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Contains("未知", h.Monitor.ControlStatusText);
        h.Monitor.OnRemoteFrame(first);
        Assert.False(h.Monitor.CanSendControl);
        h.Deliver();
        Assert.True(h.Monitor.CanSendControl);
    });

    [Theory]
    [InlineData("history")]
    [InlineData("member")]
    [InlineData("memberGone")]
    [InlineData("leave")]
    [InlineData("disconnect")]
    [InlineData("room")]
    [InlineData("expiry")]
    [InlineData("clear")]
    public Task ContextChangeCancelsPendingInput_AndNeverRenewsSession(string change) => RunSta(async () =>
    {
        using var h = new Harness { HoldResult = true };
        h.Deliver();
        h.Monitor.IsControlMode = true;
        var session = h.Monitor.SessionId;
        var sending = h.Monitor.SendKeyAsync("Win+D");
        Assert.True(h.Monitor.ControlBusy);
        switch (change)
        {
            case "history": h.Monitor.SelectedThumb = h.Monitor.Frames.Single(); break;
            case "member": h.Monitor.SelectedMonitor = h.Monitor.MonitorOptions.Single(option => option.Key == "uidC"); break;
            case "memberGone": h.Main.Members.Remove(h.Main.Members.Single(member => member.PlayerUid == "uidB")); break;
            case "leave": h.Monitor.OnTabDeactivated(); break;
            case "disconnect": h.Connected = false; h.Main.IsConnected = false; break;
            case "room": h.Main.RoomCode = "another-room"; break;
            case "expiry":
                h.Clock.Advance(TimeSpan.FromMinutes(3));
                h.Monitor.CaptureCommand.Execute(null); // 不依赖 timer 已调度，入口必须自行守住截止。
                break;
            case "clear": h.Monitor.ClearHistoryCommand.Execute(null); break;
        }
        await sending;
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.ControlBusy);
        Assert.True(h.Inputs[0].Cancellation.IsCancellationRequested);
        Assert.Contains(h.Cancels, cancel => cancel.Session == session && cancel.Target == "uidB");
        h.Pending.TrySetResult(new DesktopControlResult
        {
            RequestId = h.Inputs[0].Command.RequestId, TargetUid = "uidB", Status = "executed"
        });
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.False(h.Monitor.IsControlMode);
        Assert.Single(h.Inputs);
        if (change == "leave") Assert.Null(h.Monitor.SessionId);
        else Assert.Equal(session, h.Monitor.SessionId);
        if (change == "expiry")
        {
            h.Monitor.OnTabActivated();
            h.Monitor.ReloadConfigTransferSettings();
            Assert.Equal(session, h.Monitor.SessionId);
            Assert.Contains("已满 3 分钟", h.Monitor.StatusText);
        }
    });

    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public Task RemoteWallClockOffset_DoesNotRejectFreshFrames_OrExtendTheirReceivedAge(int seconds) => RunSta(() =>
    {
        using var h = new Harness();
        var frame = h.Deliver(capturedAt: h.Clock.GetUtcNow().UtcDateTime.AddSeconds(seconds));
        Assert.True(h.Monitor.CanEnableControl);
        h.Clock.Advance(DesktopControlProtocol.FrameLifetime);
        Assert.False(h.Monitor.CanEnableControl);
        h.Monitor.OnRemoteFrame(frame);
        Assert.False(h.Monitor.CanEnableControl); // 同编号重放不能刷新本机单调收帧时间。
        return Task.CompletedTask;
    });

    [Fact]
    public Task MissingGeometry_StaleFrame_Revocation_AndReconnectNeedNewFrame() => RunSta(async () =>
    {
        using var h = new Harness();
        h.Deliver(geometry: false);
        Assert.False(h.Monitor.CanEnableControl);
        var frame = h.Deliver();
        h.Monitor.IsControlMode = true;
        h.Clock.Advance(DesktopControlProtocol.FrameLifetime);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl);
        h.Monitor.OnRemoteFrame(frame);
        Assert.False(h.Monitor.CanEnableControl);
        h.Deliver(capturedAt: h.Clock.GetUtcNow().UtcDateTime.AddSeconds(-16));
        Assert.True(h.Monitor.CanEnableControl); // 远端墙钟偏差不当作帧龄，真实捕获年龄由目标服务检查。
        h.Deliver();
        h.Monitor.IsControlMode = true;
        h.Deliver(allowed: false);
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl);
        h.Deliver();
        h.Monitor.IsControlMode = true;
        h.Connected = false;
        h.Main.IsConnected = false;
        h.Connected = true;
        h.Main.IsConnected = true;
        Assert.False(h.Monitor.IsControlMode);
        Assert.False(h.Monitor.CanEnableControl);
        h.Deliver();
        Assert.True(h.Monitor.CanEnableControl);
    });

    [Fact]
    public Task MouseActionsUseNormalizedContentCoordinates_AndRejectLetterbox() => RunSta(async () =>
    {
        using var h = new Harness();
        h.Deliver();
        h.Monitor.IsControlMode = true;
        await h.Monitor.SendMouseAsync(new Point(500, 50), new Size(1000, 800), new Size(1920, 1080));
        Assert.Empty(h.Inputs);
        foreach (var (index, action) in new[] { (0, "left"), (1, "right"), (2, "double") })
        {
            h.Monitor.MouseActionIndex = index;
            await h.Monitor.SendMouseAsync(new Point(750, 400), new Size(1000, 800), new Size(1920, 1080));
            Assert.Equal(action, h.Inputs.Last().Command.Action);
            Assert.Equal(0.75, h.Inputs.Last().Command.X, 8);
            Assert.Equal(0.5, h.Inputs.Last().Command.Y, 8);
            h.Clock.Advance(TimeSpan.FromSeconds(2));
            h.Deliver();
        }
        await h.Monitor.SendKeyAsync("Win+R");
        Assert.Equal(3, h.Inputs.Count);
    });

    // 与 OverviewTests 共用已取消启动操作的真实资源 App，避免一个 testhost 创建第二个 Application。
    internal static void VerifyProductionControlUi(MainWindow window, DodocoPage page, MainViewModel main,
        TimeProvider clock, Action<TimeSpan> advance, byte[] jpeg)
    {
        var monitor = window.Dodoco.Monitor;
        var oldSend = Field(monitor, "_sendControl");
        var oldCancel = Field(monitor, "_cancelControl");
        var oldConnected = Field(monitor, "_controlConnected");
        var inputs = new List<DesktopControlCommand>();
        var cancels = new List<(string Session, string? Target)>();
        var settings = (DodocoSettingsService)Field(monitor, "_settings")!;
        var originalAllow = settings.Current.AllowDesktopControl;
        var oldObserver = main.Config!.ObserverMode;
        var config = main.Config;
        try
        {
            SetField(monitor, "_sendControl", (Func<DesktopControlCommand, bool, CancellationToken, Task<DesktopControlResult>>)
                ((command, local, cancellation) =>
                {
                    Assert.False(local); // 生产页只测远端假通道，永不进入本机输入服务。
                    inputs.Add(command);
                    return Task.FromResult(new DesktopControlResult
                    {
                        RequestId = command.RequestId, TargetUid = command.TargetUid, Status = "denied", Message = "隔离假输入"
                    });
                }));
            SetField(monitor, "_cancelControl", (Func<string, string?, Task>)((session, target) =>
            {
                cancels.Add((session, target));
                return Task.CompletedTask;
            }));
            SetField(monitor, "_controlConnected", (Func<bool>)(() => true));
            config.ObserverMode = false;
            typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(main, [nameof(MainViewModel.IsObserverMode)]);
            window.UpdateLayout();
            PumpUi();
            var allow = (CheckBox)page.FindName("AllowDesktopControlToggle");
            Assert.True(allow.IsVisible);
            allow.IsChecked = true;
            allow.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            Assert.True(settings.Current.AllowDesktopControl);
            Assert.True(new DodocoSettingsService(AssistantDataDirectory.Root).Current.AllowDesktopControl);
            allow.IsChecked = false;
            allow.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
            Assert.False(settings.Current.AllowDesktopControl);

            void NewFrame(string uid = "uidB") => monitor.OnRemoteFrame(new MemberScreenshotFrame
            {
                Uid = uid, RequestId = MemberScreenshotRelayService.CreateRequestId(monitor.SessionId!),
                JpegBase64 = Convert.ToBase64String(jpeg), Width = 1280, Height = 720,
                CapturedAt = clock.GetUtcNow().UtcDateTime, Geometry = Geometry, ControlAllowed = true
            });
            void Enable()
            {
                NewFrame();
                var toggle = (CheckBox)page.FindName("DesktopControlModeToggle");
                PumpUi();
                Assert.True(toggle.IsEnabled);
                toggle.IsChecked = true;
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource();
                PumpUi();
                Assert.True(monitor.IsControlMode);
            }
            var mode = (CheckBox)page.FindName("DesktopControlModeToggle");
            Assert.False(monitor.IsControlMode);
            Assert.Same(Field(window.Dodoco, "_desktopControl"), Field(window.Dodoco.LocalBrowserService, "_desktopControl"));
            Enable();
            var mouseAction = (ComboBox)page.FindName("DesktopMouseAction");
            Assert.Equal(monitor.MouseActions, mouseAction.Items.Cast<string>());
            mouseAction.SetCurrentValue(Selector.SelectedIndexProperty, 2);
            mouseAction.GetBindingExpression(Selector.SelectedIndexProperty)!.UpdateSource();
            Assert.Equal(2, monitor.MouseActionIndex);
            var keys = (ItemsControl)page.FindName("DesktopControlKeys");
            Assert.True(keys.IsVisible);
            var keyButtons = Descendants(keys).OfType<Button>().ToArray();
            Assert.Equal(DesktopControlProtocol.Keys, keyButtons.Select(button => Assert.IsType<string>(button.Content)));
            var winD = keyButtons.Single(button => Equals(button.Content, "Win+D"));
            Assert.False(winD.Focusable);
            winD.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            winD.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Empty(inputs); // 自动 Click/实体键盘事件不能发送固定键。
            winD.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            Assert.Empty(inputs); // 没有真实按下状态的抬起不是一次有效点击。
            Assert.True(page.SendMonitorKeyPointerAsync("Win+D", true, false, 1).IsCompletedSuccessfully);
            Assert.True(page.SendMonitorKeyPointerAsync("Win+D", false, true, 1).IsCompletedSuccessfully);
            Assert.True(page.SendMonitorKeyPointerAsync("Win+D", true, true, 2).IsCompletedSuccessfully);
            Assert.Empty(inputs); // 拖出、没有按下、第二次双击均不能发送。
            Assert.True(page.SendMonitorKeyPointerAsync("Win+D", true, true, 1).IsCompletedSuccessfully);
            Assert.Equal("Win+D", Assert.Single(inputs).Key);
            Assert.Equal("uidB", inputs[0].TargetUid);
            PumpUi();
            Assert.False(winD.IsEnabled);
            Assert.True(monitor.IsControlMode);
            advance(TimeSpan.FromSeconds(2));
            NewFrame();
            PumpUi();
            Assert.True(winD.IsEnabled);

            // 历史缩略图的实际 ListBox 绑定必须关闭控制并触发取消。
            var history = Descendants(page).OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, monitor.Frames));
            history.SelectedItem = monitor.Frames[0];
            history.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            PumpUi();
            Assert.False(monitor.IsControlMode);
            Assert.False(mode.IsChecked);
            Assert.NotEmpty(cancels);
            Assert.False(monitor.CanEnableControl);

            // 关闭控制时点击大图仍打开放大窗口，窗口只有查看行为。
            var image = (System.Windows.Controls.Image)page.FindName("MonitorImage");
            image.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            var zoom = window.OwnedWindows.OfType<ImageZoomWindow>().Single();
            Assert.Single(inputs);
            zoom.Close();
            Enable();
            var source = Descendants(page).OfType<ComboBox>()
                .Single(combo => ReferenceEquals(combo.ItemsSource, monitor.MonitorOptions));
            source.SelectedItem = monitor.MonitorOptions.Single(option => option.Key == "uidC");
            source.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            PumpUi();
            Assert.False(monitor.IsControlMode);
            source.SelectedItem = monitor.MonitorOptions.Single(option => option.Key == "uidB");
            source.GetBindingExpression(Selector.SelectedItemProperty)!.UpdateSource();
            Enable();
            var session = monitor.SessionId;
            window.Dodoco.SelectedTabIndex = 0;
            Assert.False(monitor.IsControlMode);
            Assert.Null(monitor.SessionId);
            Assert.Contains(cancels, cancel => cancel.Session == session && cancel.Target == "uidB");
            window.Dodoco.SelectedTabIndex = 4;
            Assert.NotEqual(session, monitor.SessionId);
            Assert.False(monitor.IsControlMode);
            Assert.False(monitor.CanEnableControl); // 上一观看会话的缓存帧仅可查看。
        }
        finally
        {
            monitor.IsControlMode = false;
            settings.TryUpdate(value => value.AllowDesktopControl = originalAllow);
            config.ObserverMode = oldObserver;
            typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(main, [nameof(MainViewModel.IsObserverMode)]);
            SetField(monitor, "_sendControl", oldSend);
            SetField(monitor, "_cancelControl", oldCancel);
            SetField(monitor, "_controlConnected", oldConnected);
        }
    }

    private static readonly DesktopCaptureGeometry Geometry = new()
        { Left = -320, Top = 0, Width = 1920, Height = 1080, DisplayId = "fake-primary", LayoutId = "fake-layout-1" };
    private sealed record Input(DesktopControlCommand Command, bool Local, CancellationToken Cancellation);

    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NexusBGI-monitor-control-" + Guid.NewGuid().ToString("N"));
        public ManualClock Clock { get; } = new();
        public MainViewModel Main { get; } = new();
        public ScreenshotViewModel Monitor { get; }
        public List<Input> Inputs { get; } = [];
        public List<(string Session, string? Target)> Cancels { get; } = [];
        public bool Connected { get; set; } = true;
        public bool HoldResult { get; set; }
        public string ResultStatus { get; set; } = "denied";
        public TaskCompletionSource<DesktopControlResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures;
        private readonly byte[] _jpeg;
        private readonly MemberScreenshotRelayService _relay;
        public DispatcherTimer AutoTimer => (DispatcherTimer)Field(Monitor, "_autoTimer")!;

        public Harness(bool local = false)
        {
            Assert.True(AssistantDataDirectory.IsExplicit, "测试必须指定隔离 NEXUSBGI_DATA_ROOT。");
            Directory.CreateDirectory(Root);
            using var bitmap = new Bitmap(1280, 720);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            _jpeg = stream.ToArray();
            SetField(Main, "_config", new AssistConfig
            {
                PlayerUid = "owner", PlayerName = "假观看端", ObserverMode = !local,
                StandaloneMode = local, ServerUrl = "http://127.0.0.1:1", DisclaimerAccepted = true
            });
            Main.Members.Add(new MemberViewModel { PlayerUid = "owner", PlayerName = "执行端", IsSelf = true, Online = true });
            Main.Members.Add(new MemberViewModel { PlayerUid = "uidB", PlayerName = "成员 B", Online = true });
            Main.Members.Add(new MemberViewModel { PlayerUid = "uidC", PlayerName = "成员 C", Online = true });
            var settings = new DodocoSettingsService(Root);
            var screenshots = new ScreenshotService(() => 1280, Clock, () => Geometry, (_, _) =>
            {
                Interlocked.Increment(ref Captures);
                return new ScreenFrame { JpegBytes = _jpeg, Width = 1280, Height = 720, Geometry = Geometry };
            });
            _relay = new MemberScreenshotRelayService(screenshots, settings, () => null);
            Monitor = new ScreenshotViewModel(screenshots, settings, Main, _relay, Clock,
                (command, localInput, cancellation) =>
                {
                    Inputs.Add(new Input(command, localInput, cancellation));
                    return HoldResult ? Pending.Task : Task.FromResult(new DesktopControlResult
                    {
                        RequestId = command.RequestId, TargetUid = command.TargetUid,
                        Status = ResultStatus, Message = "隔离假输入"
                    });
                }, (session, target) =>
                {
                    Cancels.Add((session, target));
                    return Task.CompletedTask;
                }, () => Connected);
            Monitor.SelectedMonitor = Monitor.MonitorOptions.Single(option => option.Key == (local ? "local" : "uidB"));
            Monitor.OnTabActivated();
        }

        public MemberScreenshotFrame Deliver(bool allowed = true, bool geometry = true, DateTime? capturedAt = null,
            string uid = "uidB")
        {
            var frame = new MemberScreenshotFrame
            {
                Uid = uid, RequestId = MemberScreenshotRelayService.CreateRequestId(Monitor.SessionId!),
                JpegBase64 = Convert.ToBase64String(_jpeg), Width = 1280, Height = 720,
                CapturedAt = capturedAt ?? Clock.GetUtcNow().UtcDateTime,
                Geometry = geometry ? Geometry : null, ControlAllowed = allowed
            };
            Monitor.OnRemoteFrame(frame);
            return frame;
        }

        public async Task CaptureLocal()
        {
            Monitor.CaptureCommand.Execute(null);
            var watch = Stopwatch.StartNew();
            while (!Monitor.HasFrame || (bool)Field(Monitor, "_isCapturing")!)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "假截图应在五秒内回到 UI。");
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        public void Dispose()
        {
            Monitor.Dispose();
            _relay.Dispose();
            Pending.TrySetCanceled();
            Assert.Equal(0, Clock.ActiveTimerCount);
            Directory.Delete(Root, true); // 本夹具唯一创建的小型设置目录，不包含产物或真实用户数据。
        }
    }

    private static async Task RunSta(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void SetField(object instance, string name, object? value) => instance.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static void PumpUi()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow;
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => _origin + TimeSpan.FromTicks(GetTimestamp());
        public int ActiveTimerCount => _timers.Count(timer => !timer.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due;
            private bool _fired;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                _fired = false;
                return true;
            }
            public void FireIfDue()
            {
                if (Disposed || _fired || clock.GetTimestamp() < _due) return;
                _fired = true;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
