using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>生产成员卡片的实际 WPF 布局；截图仅证明窗口显示，不代表游戏线路执行。</summary>
public sealed class HoeingMemberStatusWindowTests
{
    [Fact]
    public async Task MemberCards_ShowLiveHoeingPhases_WithoutOverlappingOldResult_AndCollapseEmptyResult()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须先为本进程设置隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                Assert.Null(Application.Current); // 此类必须单独 testhost 运行，不借用其他窗口的 Application。
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow? window = null;
                    MainViewModel? vm = null;
                    try
                    {
                        vm = new MainViewModel();
                        var isolatedBgi = Path.Combine(AssistantDataDirectory.Root, "nonexistent-bgi", "BetterGI.exe");
                        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm,
                            new AssistConfig
                            {
                                ObserverMode = true, StandaloneMode = true, BgiPath = isolatedBgi,
                                PlayerUid = "100000001", PlayerName = "隔离布局验收", DisclaimerAccepted = true,
                            });
                        Assert.Equal(Path.Combine(Path.GetDirectoryName(isolatedBgi)!, "log"),
                            BgiLogTailService.ResolveBgiLogDir(isolatedBgi));
                        var preparing = Member("100000001", "准备中的成员", "preparing", "正在准备联机锄地");
                        var running = Member("100000002", "执行中的成员", "running", "第 1/2 项：联机锄地（恢复 0/1）");
                        var cleanup = Member("100000003", "清理待确认的成员", "cleanupPending", "本端已结束，服务端清理回执待确认");
                        vm.Members.Add(preparing); vm.Members.Add(running); vm.Members.Add(cleanup);
                        vm.CurrentPage = AppPage.Home;
                        window = new MainWindow(vm)
                        {
                            ShowActivated = false, ShowInTaskbar = false,
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Left = -20000, Top = -20000, Width = 1000, Height = 850,
                        };
                        // MainWindow 构造会建桌宠只读轮询；立即关闭，不等待、不依赖任何真实执行端。
                        window.Pet.Shutdown();
                        window.Show();
                        await LayoutAsync(window);

                        foreach (var member in new[] { preparing, running, cleanup })
                        {
                            var text = BoundText(window, member, nameof(MemberViewModel.HoeingStartMessage));
                            Assert.Equal(member.HoeingStartMessage, text.Text);
                            Assert.True(text.IsVisible);
                            Assert.True(text.ActualWidth > 0 && text.ActualHeight > 0);
                            Assert.Equal(Visibility.Visible, text.Visibility);
                        }
                        var cleanupText = BoundText(window, cleanup, nameof(MemberViewModel.HoeingStartMessage));
                        Assert.Equal(((SolidColorBrush)window.FindResource("Pyro")).Color,
                            Assert.IsType<SolidColorBrush>(cleanupText.Foreground).Color);

                        // 旧远程任务结果保留在独立行，不能占据或覆盖当前联机批次状态行。
                        const string previousError = "旧任务回执：启动失败，BGI 状态未确认";
                        preparing.RemoteStartResult = previousError;
                        preparing.RemoteStartResultState = "failed";
                        await LayoutAsync(window);
                        var live = BoundText(window, preparing, nameof(MemberViewModel.HoeingStartMessage));
                        var previous = BoundText(window, preparing, nameof(MemberViewModel.RemoteStartResult));
                        Assert.Equal("正在准备联机锄地", live.Text);
                        Assert.Equal(previousError, previous.Text);
                        Assert.True(previous.IsVisible);
                        var liveBounds = Bounds(live, window);
                        var previousBounds = Bounds(previous, window);
                        Assert.True(previousBounds.Top >= liveBounds.Bottom,
                            $"旧结果覆盖了联机状态：current={liveBounds} previous={previousBounds}");

                        // 改变同一个生产成员对象，确认 INPC 会更新实际卡片，不保留旧阶段文字。
                        preparing.HoeingStartPhase = "running";
                        preparing.HoeingStartMessage = "正在执行本批第 1 项";
                        await LayoutAsync(window);
                        Assert.Equal("正在执行本批第 1 项", live.Text);
                        Assert.Equal(previousError, previous.Text);
                        preparing.HoeingStartPhase = "preparing";
                        preparing.HoeingStartMessage = "正在准备联机锄地";
                        preparing.RemoteStartResult = "";
                        preparing.RemoteStartResultState = "";
                        await LayoutAsync(window);
                        foreach (var member in new[] { preparing, running, cleanup })
                        {
                            var emptyResult = BoundText(window, member, nameof(MemberViewModel.RemoteStartResult));
                            Assert.Equal(Visibility.Collapsed, emptyResult.Visibility);
                            Assert.Equal(0, emptyResult.DesiredSize.Height);
                            Assert.False(emptyResult.IsVisible);
                        }

                        await VerifyCompletionHintAsync(window, preparing, running, cleanup);
                        await VerifyReleasedStatusUpdatesAsync(window, vm);

                        SaveWindow(window, Path.Combine(AssistantDataDirectory.Root, "hoeing-member-status-window.png"));
                        Assert.Null(vm.SignalR);
                    }
                    catch (Exception error) { done.TrySetException(error); }
                    finally
                    {
                        try
                        {
                            if (window != null)
                            {
                                window.Pet.Shutdown();
                                window.Mistletoe.TaskCenter.StopAutoRefresh();
                                StopTimer(window.Mistletoe, "_bgiStatusRefresh");
                                StopTimer(window.Mistletoe, "_saveDebounce");
                                if (vm != null)
                                {
                                    typeof(MainViewModel).GetField("_disposing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
                                    await vm.TaskCenterHost.ShutdownAsync();
                                }
                                window.Close();
                            }
                        }
                        catch (Exception cleanupError) { done.TrySetException(cleanupError); }
                        finally
                        {
                            // Dodoco 在 Application.Exit 已接线统一 Dispose：关闭 tail、监控、录像、共享同步及子页定时器。
                            try { app.Shutdown(); }
                            catch (Exception shutdownError) { done.TrySetException(shutdownError); }
                            done.TrySetResult();
                        }
                    }
                });
                app.Run();
            }
            catch (Exception error) { done.TrySetException(error); }
        }) { IsBackground = true, Name = "HoeingMemberStatusWindow" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离窗口 Dispatcher 尚未退出。");
        Assert.True(File.Exists(Path.Combine(AssistantDataDirectory.Root, "hoeing-member-status-window.png")));
    }

    private static async Task VerifyCompletionHintAsync(MainWindow window, MemberViewModel preparing,
        MemberViewModel running, MemberViewModel cleanup)
    {
        preparing.PlayerName = "小鲨鱼";
        preparing.HoeingStartPhase = "completed";
        preparing.HoeingStartMessage = MemberViewModel.PostHoeingCompletedMessage;
        await LayoutAsync(window);
        var text = BoundText(window, preparing, nameof(MemberViewModel.HoeingStartMessage));
        var line = Assert.IsType<DockPanel>(VisualTreeHelper.GetParent(text));
        var baselineHeight = line.ActualHeight;
        var button = Assert.Single(VisualChildren<Button>(line)
            .Where(item => item.Name == "CompletionActionHintButton"));
        Assert.Equal(Visibility.Collapsed, button.Visibility);

        // 远端旧版文案也转换为当前展示；不要求对方更新程序。
        preparing.HoeingStartPhase = "completionPending";
        preparing.HoeingStartMessage = "正在跟踪同一个绑定完成动作";
        running.HoeingStartPhase = "completed";
        running.HoeingStartMessage = "联机锄地已完整完成";
        cleanup.HoeingStartPhase = "completed";
        cleanup.HoeingStartMessage = MemberViewModel.PostHoeingCompletedMessage;
        await LayoutAsync(window);
        Assert.Equal(MemberViewModel.PostHoeingRunningMessage, text.Text);
        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, running.HoeingStartMessage);
        Assert.True(preparing.IsPostHoeingTaskRunning);
        Assert.True(button.IsVisible);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.InRange(Math.Abs(line.ActualHeight - baselineHeight), 0, 0.5);
        Assert.InRange(Bounds(button, window).Left - Bounds(text, window).Right, 0, 8);
        SaveWindow(window, Path.Combine(AssistantDataDirectory.Root, "completion-running-window.png"));

        // 验证狭窄可用宽度下文字让位于图标，而非新增一行或溢出。
        line.MaxWidth = 190;
        await LayoutAsync(window);
        Assert.True(text.ActualWidth < 190);
        Assert.True(button.IsVisible && button.ActualWidth > 0);
        Assert.InRange(Math.Abs(line.ActualHeight - baselineHeight), 0, 0.5);
        Assert.True(Bounds(button, window).Right <= Bounds(line, window).Right + 0.5);
        SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "completion-narrow-line.png"));
        line.ClearValue(FrameworkElement.MaxWidthProperty);
        await LayoutAsync(window);

        var hint = Assert.IsType<ToolTip>(button.ToolTip);
        Assert.Equal(MemberViewModel.PostHoeingHelp, Assert.IsType<TextBlock>(hint.Content).Text);
        Assert.True(ToolTipService.GetIsEnabled(button));
        Assert.True(hint.StaysOpen); // WPF ToolTipService 不支持 StaysOpen=false。
        // 走日志中真正失败的 WPF 悬停服务，而非直接设置 ToolTip.IsOpen。
        var serviceType = typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService", throwOnError: true)!;
        var service = serviceType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        var show = serviceType.GetMethod("ShowToolTip", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(DependencyObject), typeof(bool)], modifiers: null)!;
        var close = serviceType.GetMethod("DismissCurrentToolTip", BindingFlags.Instance | BindingFlags.NonPublic)!;
        close.Invoke(service, null); // 清理布局过程中其他控件可能已触发的提示，建立本次操作的明确前置状态。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // 离屏窗口没有真实鼠标悬停；在同一次调度内验证服务开关，避免让位后被输入系统自动收起。
            show.Invoke(service, [button, false]);
            Assert.True(hint.IsOpen, $"WPF 提示服务未打开浮层，attempt={attempt}");
            close.Invoke(service, null);
            Assert.False(hint.IsOpen);
            await LayoutAsync(window);
        }
        var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(button.Tag);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await LayoutAsync(window);
        Assert.True(popup.IsOpen);
        Assert.Same(button, popup.PlacementTarget);
        Assert.InRange(Math.Abs(line.ActualHeight - baselineHeight), 0, 0.5);
        SaveWindow(Assert.IsAssignableFrom<FrameworkElement>(popup.Child), Path.Combine(AssistantDataDirectory.Root, "completion-help-popup.png"));

        // 同一个成员断线时，旧执行中文字不能继续冒充当前状态；原阶段仍保留。
        foreach (var message in new[] { "正在跟踪同一个绑定完成动作", MemberViewModel.PostHoeingRunningMessage })
        {
            preparing.HoeingStartMessage = message;
            preparing.Online = false;
            await LayoutAsync(window);
            Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, text.Text);
            Assert.Equal("completionPending", preparing.HoeingStartPhase);
            Assert.True(preparing.IsPostHoeingTaskOffline);
            Assert.False(preparing.IsPostHoeingTaskRunning);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.False(popup.IsOpen);
            Assert.Equal(((SolidColorBrush)window.FindResource("Dim")).Color,
                Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            Assert.InRange(Math.Abs(line.ActualHeight - baselineHeight), 0, 0.5);
            SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "completion-offline-line.png"));

            preparing.Online = true;
            await LayoutAsync(window);
            Assert.Equal(MemberViewModel.PostHoeingRunningMessage, text.Text);
            Assert.False(preparing.IsPostHoeingTaskOffline);
            Assert.True(button.IsVisible);
        }
        preparing.Online = false;
        preparing.HoeingStartPhase = "offlineCompletion";
        preparing.HoeingStartMessage = MemberViewModel.PostHoeingOfflineReleasedMessage;
        await LayoutAsync(window);
        Assert.Equal(MemberViewModel.PostHoeingOfflineReleasedMessage, text.Text);
        Assert.False(button.IsVisible);
        Assert.False(popup.IsOpen);
        Assert.InRange(Math.Abs(line.ActualHeight - baselineHeight), 0, 0.5);
        SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "completion-offline-released-line.png"));
        preparing.Online = true;

        // 已知完成事实在断线后仍保留，不能改成未知或执行中。
        running.Online = false;
        await LayoutAsync(window);
        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, running.HoeingStartMessage);
        Assert.False(running.IsPostHoeingTaskOffline);

        // 完成、停止及未知状态都不能遗留执行中的说明图标或浮层。
        foreach (var phase in new[] { "completed", "cancelled", "unknown" })
        {
            preparing.HoeingStartPhase = phase;
            preparing.HoeingStartMessage = phase == "completed" ? MemberViewModel.PostHoeingCompletedMessage : "本轮已结束";
            await LayoutAsync(window);
            Assert.False(preparing.IsPostHoeingTaskRunning);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.False(popup.IsOpen);
        }
        preparing.HoeingStartPhase = "completed";
        preparing.HoeingStartMessage = MemberViewModel.PostHoeingCompletedMessage;
        await LayoutAsync(window);
    }

    private static async Task VerifyReleasedStatusUpdatesAsync(MainWindow window, MainViewModel vm)
    {
        await using var client = new SignalRClient();
        typeof(MainViewModel).GetMethod("WireSignalRClient", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [client]);
        var receive = (Action<ControlRoomPlayersUpdate>)typeof(SignalRClient)
            .GetField("OnPlayersUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        ControlRoomPlayer Packet(bool online, bool? released) => System.Text.Json.JsonSerializer.Deserialize<ControlRoomPlayer>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                playerUid = "100000009", playerName = "Dream", online, hoeingBatchId = "old-post",
                hoeingStartPhase = "completionPending", hoeingStartMessage = "正在跟踪同一个绑定完成动作",
                hoeingCompletionWaitReleased = released,
            }), options)!;

        // 实际接收模型 → 全量新增 → 卡片，覆盖没有 offlineCompletion 新阶段的旧数据。
        receive(new ControlRoomPlayersUpdate { Full = true, Revision = 1, Players = [Packet(false, true)] });
        await LayoutAsync(window);
        var member = Assert.Single(vm.Members);
        var text = BoundText(window, member, nameof(MemberViewModel.HoeingStartMessage));
        var line = Assert.IsType<DockPanel>(VisualTreeHelper.GetParent(text));
        var button = Assert.Single(VisualChildren<Button>(line).Where(b => b.Name == "CompletionActionHintButton"));
        var height = line.ActualHeight;
        Assert.Equal(MemberViewModel.PostHoeingOfflineReleasedMessage, text.Text);
        Assert.False(button.IsVisible);
        SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "released-offline-line.png"));

        // 增量重连仍在跑原后续：不重新显示会暗示占用本轮的说明图标。
        receive(new ControlRoomPlayersUpdate { Full = false, Revision = 2, Changed = [Packet(true, true)] });
        await LayoutAsync(window);
        Assert.Same(member, Assert.Single(vm.Members));
        Assert.Equal(MemberViewModel.PostHoeingReleasedRunningMessage, text.Text);
        Assert.False(button.IsVisible);
        Assert.InRange(Math.Abs(line.ActualHeight - height), 0, 0.5);
        SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "released-reconnected-line.png"));

        // 新轮次确实持有等待，不能继承旧后续“不影响”的显示。
        receive(new ControlRoomPlayersUpdate { Full = false, Revision = 3, Changed = [Packet(true, false)] });
        await LayoutAsync(window);
        Assert.Equal(MemberViewModel.PostHoeingRunningMessage, text.Text);
        Assert.True(button.IsVisible);

        // 旧服务端没有提供解除事实时，只显示离线，不显示带有错误暗示的未确认短句。
        receive(new ControlRoomPlayersUpdate { Full = false, Revision = 4, Changed = [Packet(false, null)] });
        await LayoutAsync(window);
        Assert.Equal("成员已离线", text.Text);
        Assert.False(button.IsVisible);
        Assert.DoesNotContain("状态未确认", text.Text);
        Assert.DoesNotContain("不影响", text.Text);
        SaveWindow(line, Path.Combine(AssistantDataDirectory.Root, "legacy-offline-line.png"));
    }

    private static MemberViewModel Member(string uid, string name, string phase, string message) => new()
    {
        PlayerUid = uid, PlayerName = name, Online = true, IsSelected = true,
        BgiStatus = "running", BgiVersion = "隔离窗口布局", IsObserverMode = true,
        HoeingBatchId = "window-only-" + uid, HoeingStartPhase = phase, HoeingStartMessage = message,
    };

    private static async Task LayoutAsync(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static TextBlock BoundText(DependencyObject root, MemberViewModel member, string path) =>
        Assert.Single(VisualChildren<TextBlock>(root).Where(text => ReferenceEquals(text.DataContext, member)
            && BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == path));

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static void StopTimer(object owner, string field)
    {
        if (owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) is DispatcherTimer timer)
            timer.Stop();
    }

    private static void SaveWindow(FrameworkElement window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        if (window is Window) bitmap.Render(window);
        else
        {
            // 子控件带有父布局偏移，使用 VisualBrush 将其自身内容绘制到截图原点。
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            bitmap.Render(drawing);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
