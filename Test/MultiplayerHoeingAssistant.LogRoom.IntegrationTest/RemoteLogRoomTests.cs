using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;
using ClientPlayer = MultiplayerHoeingAssistant.Models.ControlRoomPlayer;
using ClientStatus = MultiplayerHoeingAssistant.Models.ControlStatus;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

/// <summary>隔离本地服务器、独立数据根，走正式 WPF 按钮与真实网关验证日志切换和 gzip 下载。</summary>
public sealed partial class RemoteLogRoomTests
{
    [Fact]
    public async Task LogPanel_SwitchFetchDownloadReturn_LeavesOwnRoomAndTasksUntouched()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await VerifyAsync(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { app.Shutdown(); }
            });
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(100));
    }

    private static async Task VerifyAsync()
    {
        var root = Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")!;
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new RoomManager(50));
        builder.Services.AddSingleton<GatewaySessionTracker>();
        builder.Services.AddSingleton<GatewayBroadcaster>();
        builder.Services.AddSingleton<RoomPhaseObserver>();
        builder.Services.AddSingleton<RoomOperations>();
        builder.Services.AddSingleton<GatewayDispatcher>();
        builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 10 * 1024 * 1024);
        await using var server = builder.Build();
        server.MapHub<GatewayHub>("/gateway");
        server.MapHub<CoordinatorHub>("/hub");
        await server.StartAsync();
        var url = server.Urls.Single();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var ownRoom = "own-" + suffix;
        var targetRoom = "help-" + suffix;
        var thirdRoom = "third-" + suffix;
        const string thirdPassword = "third-local-test-only";
        const string password = "local-test-only";
        await using var own = new SignalRClient();
        await using var peer = new SignalRClient();
        await using var target = new SignalRClient();
        await using var third = new SignalRClient();
        await own.ConnectAsync(url, ownRoom, password, "own", "本人", []);
        await peer.ConnectAsync(url, ownRoom, password, "peer", "本房间成员" + suffix, []);
        await target.ConnectAsync(url, targetRoom, password, "target", "被协助成员", []);
        await third.ConnectAsync(url, thirdRoom, thirdPassword, "third", "第三房间成员", []);
        await own.ReportControlStatusAsync(new ClientStatus { PlayerUid = "own", PlayerName = "本人", TaskRunning = true,
            TaskStatusAvailable = true, CurrentTaskName = "隔离运行中任务" });
        var manager = server.Services.GetRequiredService<RoomManager>();
        var before = manager.GetControlRoomPlayers("CTRL_" + ownRoom).Select(p => (p.PlayerUid, p.ConnectionId, p.TaskRunning)).ToArray();
        var targetBefore = manager.GetControlRoomPlayers("CTRL_" + targetRoom).Select(p => (p.PlayerUid, p.ConnectionId)).ToArray();
        var commandCount = 0;
        own.OnRemoteCommand += _ => Interlocked.Increment(ref commandCount);
        target.OnRemoteCommand += _ => Interlocked.Increment(ref commandCount);

        var sourceDir = Path.Combine(root, "source-logs");
        Directory.CreateDirectory(sourceDir);
        var fileName = "better-genshin-impact20991015.log";
        var source = Path.Combine(sourceDir, fileName);
        var bytes = Encoding.UTF8.GetBytes("[11:02:00.000] [INF] 隔离下载验证\n" + new string('x', 180000));
        await File.WriteAllBytesAsync(source, bytes);
        var shareEnabled = true;
        using var share = new MemberLogShareService(new DodocoSettingsService(), () => shareEnabled ? target : null, () => sourceDir);
        share.EnsureHooked();
        using var ownShare = new MemberLogShareService(new DodocoSettingsService(), () => peer, () => sourceDir);
        ownShare.EnsureHooked();

        var vm = new MainViewModel();
        SetField(vm, "_config", new AssistConfig { ServerUrl = url, PlayerUid = "own", PlayerName = "本人",
            ControlRoomPassword = password, TeamUids = ["own", "peer"], DisclaimerAccepted = true, ObserverMode = false,
            BgiPath = Path.Combine(root, "main-window", "BetterGI.exe") });
        SetField(vm, "_signalRClient", own);
        vm.Members.Add(new MemberViewModel { PlayerUid = "own", PlayerName = "本人", IsSelf = true, Online = true });
        vm.Members.Add(new MemberViewModel { PlayerUid = "peer", PlayerName = "本房间成员" + suffix, Online = true });
        var membersBefore = vm.Members.ToArray();
        var window = new MainWindow(vm) { Width = 1280, Height = 860, ShowActivated = false, Left = -20000, Top = -20000 };
        try
        {
            vm.CurrentPage = AppPage.Dodoco;
            window.Dodoco.SelectedTabIndex = 1;
            window.Show();
            window.UpdateLayout();
            var page = Find<DodocoPage>(window)!;
            Assert.NotNull(page);
            var browser = window.Dodoco.Browser;
            browser.RefreshRemoteMembers();
            Assert.Equal("peer", Assert.Single(browser.RemoteMembers).Uid);
            browser.SelectedRemoteMember = browser.RemoteMembers[0];
            var remoteLogsButton = (Button)page.FindName("RemoteMemberLogsButton");
            var localFiles = (ListBox)page.FindName("LocalLogFilesList");
            Assert.True(localFiles.ActualHeight > 0);
            var localFilesHeight = localFiles.ActualHeight;
            var remotePopup = await OpenDialogAsync<RemoteMemberLogsWindow>(remoteLogsButton);
            Assert.Same(browser, remotePopup.DataContext);
            var assistButton = (Button)remotePopup.FindName("AssistLogRoomButton");
            var returnButton = (Button)remotePopup.FindName("ReturnOwnLogRoomButton");
            Assert.False(returnButton.IsVisible);

            // 从真实「协助房间」按钮打开弹窗；错误密码保留原房间，同一弹窗可重试。
            var popup = await OpenDialogAsync(assistButton);
            var codeBox = (TextBox)popup.FindName("RoomCodeBox");
            var passwordBox = (PasswordBox)popup.FindName("RoomPasswordBox");
            var connectButton = (Button)popup.FindName("ConnectButton");
            codeBox.Text = targetRoom;
            passwordBox.Password = "wrong";
            Invoke(connectButton);
            await Until(() => ((TextBlock)popup.FindName("StatusText")).Text.Contains("失败"));
            Assert.True(connectButton.IsEnabled);
            Assert.False(browser.IsAssistedLogRoom);
            Assert.Contains("拒绝", ((TextBlock)popup.FindName("StatusText")).Text);
            Assert.Equal("peer", Assert.Single(browser.RemoteMembers).Uid);
            Assert.Empty(passwordBox.Password);
            Snapshot(popup, Path.Combine(root, "dialog.png"));
            passwordBox.Password = password;
            Invoke(connectButton);
            await Until(() => !popup.IsVisible && browser.IsAssistedLogRoom);
            Assert.Equal("target", Assert.Single(browser.RemoteMembers).Uid);
            Assert.True(returnButton.IsVisible);
            Assert.Contains(targetRoom, browser.LogRoomLabel);

            var reopened = await OpenDialogAsync(assistButton);
            Assert.Equal(targetRoom, ((TextBox)reopened.FindName("RoomCodeBox")).Text);
            Assert.Equal(password, ((PasswordBox)reopened.FindName("RoomPasswordBox")).Password);
            reopened.Close();

            browser.SelectedRemoteMember = browser.RemoteMembers[0];
            Invoke(Find<Button>(remotePopup, b => b.Content as string == "获取列表")!);
            await Until(() => browser.RemoteFiles.Any(f => f.Name == fileName));
            browser.SelectedRemoteFile = browser.RemoteFiles.First(f => f.Name == fileName);
            await remotePopup.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), ((ComboBox)remotePopup.FindName("RemoteDateFilterBox")).SelectionBoxItem);
            Snapshot(remotePopup, Path.Combine(root, "remote-member-logs-today.png"));
            VerifyRemoteDateFilter(remotePopup, browser);
            VerifyRemotePopupLayout(remotePopup, browser, localFiles, localFilesHeight);
            ((ComboBox)remotePopup.FindName("RemoteDateFilterBox"))
                .SetCurrentValue(ComboBox.SelectedItemProperty, "全部日期");
            Assert.Equal("全部日期", browser.SelectedRemoteDateFilter);

            // 关闭和重开只影响窗口，不重置成员、文件、日期、状态或协助房间。
            var selectedMember = browser.SelectedRemoteMember;
            var selectedFile = browser.SelectedRemoteFile;
            var remoteFiles = browser.RemoteFiles.ToArray();
            var remoteStatus = browser.RemoteStatus;
            var remoteDateFilter = browser.SelectedRemoteDateFilter;
            remotePopup.Close();
            Assert.Same(selectedMember, browser.SelectedRemoteMember);
            Assert.Same(selectedFile, browser.SelectedRemoteFile);
            Assert.Equal(remoteFiles, browser.RemoteFiles.ToArray());
            Assert.Equal(remoteStatus, browser.RemoteStatus);
            Assert.Equal(remoteDateFilter, browser.SelectedRemoteDateFilter);
            remotePopup = await OpenDialogAsync<RemoteMemberLogsWindow>(remoteLogsButton);
            Assert.Same(browser, remotePopup.DataContext);
            Assert.Same(selectedMember, browser.SelectedRemoteMember);
            Assert.Same(selectedFile, browser.SelectedRemoteFile);
            Assert.Equal(remoteFiles, browser.RemoteFiles.ToArray());
            Assert.Equal(remoteStatus, browser.RemoteStatus);
            Assert.Equal(remoteDateFilter, browser.SelectedRemoteDateFilter);
            Assert.Equal(remoteDateFilter, ((ComboBox)remotePopup.FindName("RemoteDateFilterBox")).SelectedItem);
            Assert.True(browser.IsAssistedLogRoom);
            assistButton = (Button)remotePopup.FindName("AssistLogRoomButton");
            returnButton = (Button)remotePopup.FindName("ReturnOwnLogRoomButton");
            Assert.True(returnButton.IsVisible);
            Invoke(Find<Button>(remotePopup, b => b.Content as string == "下载")!);
            await Until(() => browser.RemoteStatus.StartsWith("已下载到 "));
            var downloaded = browser.RemoteStatus["已下载到 ".Length..];
            Assert.Contains("协助房间-" + targetRoom, downloaded);
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(downloaded)));
            await Until(() => !browser.IsLoading);
            Snapshot(window, Path.Combine(root, "assisted-room.png"));
            Snapshot(remotePopup, Path.Combine(root, "remote-member-logs.png"));

            // 在途列表切回时，迟到结果不填入本房间。
            var delayedRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            shareEnabled = false;
            share.EnsureHooked();
            target.OnMemberLogFilesRequested += (_, id) => delayedRequest.TrySetResult(id);
            browser.FetchRemoteFilesCommand.Execute(null);
            var oldRequest = await delayedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Invoke(returnButton);
            await target.ReportMemberLogFilesAsync(oldRequest, [new() { Name = fileName }]);
            await Task.Delay(150);
            Assert.False(browser.IsAssistedLogRoom);
            Assert.False(returnButton.IsVisible);
            Assert.Empty(browser.RemoteFiles);
            Assert.Null(browser.SelectedRemoteFile);
            Assert.Equal("peer", Assert.Single(browser.RemoteMembers).Uid);
            browser.SelectedRemoteMember = browser.RemoteMembers[0];
            browser.FetchRemoteFilesCommand.Execute(null);
            await Until(() => browser.RemoteFiles.Any(f => f.Name == fileName));
            browser.SelectedRemoteFile = browser.RemoteFiles.First(f => f.Name == fileName);
            browser.DownloadRemoteFileCommand.Execute(null);
            await Until(() => browser.RemoteStatus.StartsWith("已下载到 "));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(browser.RemoteStatus["已下载到 ".Length..]));
            Snapshot(window, Path.Combine(root, "own-room.png"));

            // 在线增量、离线移除；跨房间同名文件不会覆盖。
            Assert.True(await browser.ConnectAssistedLogRoomAsync(targetRoom, password));
            await using (var another = new SignalRClient())
            {
                await another.ConnectAsync(url, targetRoom, password, "another", "另一在线成员", []);
                await Until(() => browser.RemoteMembers.Count == 2);
            }
            await Until(() => browser.RemoteMembers.Count == 1);
            Assert.True(await browser.ConnectAssistedLogRoomAsync(ownRoom, password));
            Assert.Contains(browser.RemoteMembers, m => m.Uid == "own");
            Assert.False(await browser.ConnectAssistedLogRoomAsync(targetRoom, "bad-password"));
            Assert.Contains(ownRoom, browser.LogRoomLabel);
            browser.ReturnToOwnLogRoomCommand.Execute(null);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.False(await browser.ConnectAssistedLogRoomAsync(targetRoom, password, cancelled.Token));
                Assert.False(browser.IsAssistedLogRoom);
                Assert.Equal("peer", Assert.Single(browser.RemoteMembers).Uid);
            }

            // 通过正式弹窗再保存两个不同房间，重开后可逐个选择并自动填入各自密码。
            foreach (var (room, secret) in new[] { (ownRoom, password), (thirdRoom, thirdPassword) })
            {
                var dialog = await OpenDialogAsync(assistButton);
                ((TextBox)dialog.FindName("RoomCodeBox")).Text = room;
                ((PasswordBox)dialog.FindName("RoomPasswordBox")).Password = secret;
                Invoke((Button)dialog.FindName("ConnectButton"));
                await Until(() => !dialog.IsVisible);
                Assert.Contains(room, browser.LogRoomLabel);
            }
            var historyDialog = await OpenDialogAsync(assistButton);
            var historyBox = (ComboBox)historyDialog.FindName("HistoryRoomsBox");
            Assert.Equal(3, historyBox.Items.Count);
            Assert.Equal(thirdRoom, ((TextBox)historyDialog.FindName("RoomCodeBox")).Text);
            Assert.Equal(thirdPassword, ((PasswordBox)historyDialog.FindName("RoomPasswordBox")).Password);
            foreach (var (room, secret) in new[] { (targetRoom, password), (ownRoom, password), (thirdRoom, thirdPassword) })
            {
                historyBox.SelectedItem = historyBox.Items.Cast<RemoteLogRoomHistoryEntry>().Single(r => r.RoomCode == room);
                Assert.Equal(room, ((TextBox)historyDialog.FindName("RoomCodeBox")).Text);
                Assert.Equal(secret, ((PasswordBox)historyDialog.FindName("RoomPasswordBox")).Password);
            }
            Snapshot(historyDialog, Path.Combine(root, "three-room-history.png"));
            var historyPath = Path.Combine(root, "remote-log-rooms.json");
            var historyBeforeFailure = await File.ReadAllBytesAsync(historyPath);
            ((TextBox)historyDialog.FindName("RoomCodeBox")).Text = targetRoom;
            Assert.Empty(((PasswordBox)historyDialog.FindName("RoomPasswordBox")).Password);
            ((PasswordBox)historyDialog.FindName("RoomPasswordBox")).Password = "bad-history-password";
            Invoke((Button)historyDialog.FindName("ConnectButton"));
            await Until(() => ((TextBlock)historyDialog.FindName("StatusText")).Text.Contains("失败"));
            Assert.Equal(historyBeforeFailure, await File.ReadAllBytesAsync(historyPath));
            Assert.Contains(thirdRoom, browser.LogRoomLabel);

            historyBox.SelectedItem = historyBox.Items.Cast<RemoteLogRoomHistoryEntry>().Single(r => r.RoomCode == targetRoom);
            Invoke((Button)historyDialog.FindName("DeleteHistoryButton"));
            await Until(() => historyBox.Items.Count == 2);
            Assert.Empty(((TextBox)historyDialog.FindName("RoomCodeBox")).Text);
            Assert.Empty(((PasswordBox)historyDialog.FindName("RoomPasswordBox")).Password);
            historyDialog.Close();
            var afterDeleteDialog = await OpenDialogAsync(assistButton);
            var remaining = ((ComboBox)afterDeleteDialog.FindName("HistoryRoomsBox")).Items.Cast<RemoteLogRoomHistoryEntry>().ToArray();
            Assert.Equal(2, remaining.Length);
            Assert.DoesNotContain(remaining, r => r.RoomCode == targetRoom);
            Assert.Contains(remaining, r => r.RoomCode == ownRoom);
            Assert.Contains(remaining, r => r.RoomCode == thirdRoom);
            afterDeleteDialog.Close();
            Invoke(returnButton);
            await Until(() => !browser.IsAssistedLogRoom);

            remotePopup.Close();
            await VerifyRoomBulkDownloadAsync(url, manager, root);
            await VerifySameUidObserverAsync(url, manager, root);
            Assert.Same(own, typeof(MainViewModel).GetField("_signalRClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
            Assert.True(own.IsConnected);
            Assert.Equal(membersBefore, vm.Members.ToArray());
            Assert.Equal(before, manager.GetControlRoomPlayers("CTRL_" + ownRoom).Select(p => (p.PlayerUid, p.ConnectionId, p.TaskRunning)).ToArray());
            Assert.Equal(targetBefore, manager.GetControlRoomPlayers("CTRL_" + targetRoom).Where(p => p.Online).Select(p => (p.PlayerUid, p.ConnectionId)).ToArray());
            Assert.Equal(0, commandCount);
            Assert.True(manager.GetControlRoomPlayers("CTRL_" + ownRoom).Single(p => p.PlayerUid == "own").TaskRunning);
        }
        finally
        {
            window.Hide();
            await server.StopAsync();
        }
    }

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static Task<RemoteLogRoomWindow> OpenDialogAsync(Button button) =>
        OpenDialogAsync<RemoteLogRoomWindow>(button);

    private static async Task<T> OpenDialogAsync<T>(Button button) where T : Window
    {
        var result = new TaskCompletionSource<T>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible);
            if (dialog != null) { timer.Stop(); result.TrySetResult(dialog); }
        };
        timer.Start();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => Invoke(button));
        try { return await result.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { timer.Stop(); }
    }

    private static void VerifyRemoteDateFilter(RemoteMemberLogsWindow popup, LogBrowserViewModel browser)
    {
        var filter = (ComboBox)popup.FindName("RemoteDateFilterBox");
        var list = (ListBox)popup.FindName("RemoteFilesList");
        var originalFiles = browser.RemoteFiles.ToArray();
        var originalSelection = browser.SelectedRemoteFile;
        var originalFilter = browser.SelectedRemoteDateFilter;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var todayFile = new MemberLogFileDescriptor { Name = "isolated-today.log", LastWrite = DateTime.Today };
        var yesterdayFile = new MemberLogFileDescriptor { Name = "isolated-yesterday.log", LastWrite = DateTime.Today.AddDays(-1) };
        void SelectDate(string date)
        {
            filter.SetCurrentValue(ComboBox.SelectedItemProperty, date);
            popup.UpdateLayout();
            Assert.Equal(date, browser.SelectedRemoteDateFilter);
        }
        try
        {
            Assert.Equal(today, originalFilter);
            Assert.Equal(today, filter.SelectedItem);
            Assert.Same(browser.RemoteFilesView, list.ItemsSource);
            browser.RemoteFiles.Clear();
            browser.RemoteFiles.Add(todayFile);
            browser.RemoteFiles.Add(yesterdayFile);
            popup.UpdateLayout();
            Assert.Contains(today, filter.Items.Cast<string>());
            Assert.Contains(yesterday, filter.Items.Cast<string>());
            Assert.Same(todayFile, Assert.Single(list.Items.Cast<MemberLogFileDescriptor>()));
            list.SetCurrentValue(ListBox.SelectedItemProperty, todayFile);
            Assert.Same(todayFile, browser.SelectedRemoteFile);

            SelectDate(yesterday);
            Assert.Same(yesterdayFile, Assert.Single(list.Items.Cast<MemberLogFileDescriptor>()));
            Assert.Null(browser.SelectedRemoteFile);
            Assert.Null(list.SelectedItem);
            SelectDate("全部日期");
            Assert.Equal(new[] { todayFile, yesterdayFile }, list.Items.Cast<MemberLogFileDescriptor>().ToArray());
            Assert.Equal(new[] { todayFile, yesterdayFile }, browser.RemoteFiles.ToArray());

            SelectDate(today);
            list.SetCurrentValue(ListBox.SelectedItemProperty, todayFile);
            browser.RemoteFiles.Remove(todayFile);
            popup.UpdateLayout();
            Assert.Equal(today, browser.SelectedRemoteDateFilter);
            Assert.Equal(today, filter.SelectedItem);
            Assert.Empty(list.Items);
            Assert.Same(yesterdayFile, Assert.Single(browser.RemoteFiles));
            Assert.Null(browser.SelectedRemoteFile);
        }
        finally
        {
            browser.RemoteFiles.Clear();
            foreach (var file in originalFiles) browser.RemoteFiles.Add(file);
            browser.SelectedRemoteDateFilter = originalFilter;
            browser.SelectedRemoteFile = originalSelection;
            popup.UpdateLayout();
        }
    }

    private static void VerifyRemotePopupLayout(RemoteMemberLogsWindow popup, LogBrowserViewModel browser,
        ListBox localFiles, double originalLocalHeight)
    {
        var remoteFiles = (ListBox)popup.FindName("RemoteFilesList");
        var originalFiles = browser.RemoteFiles.ToArray();
        var selectedFile = browser.SelectedRemoteFile;
        try
        {
            // 仅扩充隔离列表，验证长列表由弹窗内部滚动承担，不再挤占本机日志列表。
            for (var i = 0; i < 24; i++)
                browser.RemoteFiles.Add(new MemberLogFileDescriptor
                {
                    Name = $"isolated-layout-{i:D2}.log", Size = 1024, LastWrite = DateTime.Today
                });
            popup.UpdateLayout();
            localFiles.UpdateLayout();
            Assert.InRange(localFiles.ActualHeight, originalLocalHeight - 0.5, originalLocalHeight + 0.5);
            Assert.True(remoteFiles.ActualHeight > 0);
            var bounds = remoteFiles.TransformToAncestor(popup).TransformBounds(new Rect(remoteFiles.RenderSize));
            Assert.InRange(bounds.Top, 0, popup.ActualHeight);
            Assert.InRange(bounds.Bottom, 0, popup.ActualHeight);
            var scroll = Find<ScrollViewer>(remoteFiles)!;
            Assert.NotNull(scroll);
            Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd();
            remoteFiles.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            Assert.InRange(localFiles.ActualHeight, originalLocalHeight - 0.5, originalLocalHeight + 0.5);
        }
        finally
        {
            browser.RemoteFiles.Clear();
            foreach (var file in originalFiles) browser.RemoteFiles.Add(file);
            browser.SelectedRemoteFile = selectedFile;
            popup.UpdateLayout();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "真实入口未在限定时间内达到预期状态。");
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T found && (predicate == null || predicate(found))) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i), predicate) is { } child) return child;
        return null;
    }

    private static void Snapshot(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
