using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BgiCoordinatorServer.Services;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

public sealed partial class RemoteLogRoomTests
{
    private static async Task VerifyRoomBulkDownloadAsync(string url, RoomManager manager, string root)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var room = "bulk-own-" + suffix;
        var assistRoom = "bulk-help-" + suffix;
        const string password = "bulk-local-test-only";
        await using var self = await BulkLogEndpoint.ConnectAsync(url, room, password, "bulk-a-" + suffix, "01 本机执行端");
        await using var empty = await BulkLogEndpoint.ConnectAsync(url, room, password, "bulk-b-" + suffix, "02 未共享成员");
        await using var refused = await BulkLogEndpoint.ConnectAsync(url, room, password, "bulk-c-" + suffix, "03 拒绝单文件成员");
        await using var peer = await BulkLogEndpoint.ConnectAsync(url, room, password, "bulk-d-" + suffix, "04 后续健康成员");
        await using var offline = await BulkLogEndpoint.ConnectAsync(url, room, password, "bulk-e-" + suffix, "05 离线成员");
        await using var assist = await BulkLogEndpoint.ConnectAsync(url, assistRoom, password, "bulk-f-" + suffix, "06 被协助成员");
        await using var assistOffline = await BulkLogEndpoint.ConnectAsync(url, assistRoom, password, "bulk-g-" + suffix, "07 协助房离线成员");
        var ownEndpoints = new[] { self, empty, refused, peer, offline };
        var allEndpoints = ownEndpoints.Concat(new[] { assist, assistOffline }).ToArray();
        empty.ReturnEmptyList = true;
        refused.RefusedFile = refused.TodayFiles[2];
        await offline.DisposeAsync();
        await assistOffline.DisposeAsync();
        await Until(() => manager.GetControlRoomPlayers("CTRL_" + room).Any(p => p.PlayerUid == offline.Uid && !p.Online)
            && manager.GetControlRoomPlayers("CTRL_" + assistRoom).Any(p => p.PlayerUid == assistOffline.Uid && !p.Online));
        foreach (var endpoint in new[] { self, empty, refused, peer, assist })
            await endpoint.Client.ReportControlStatusAsync(new MultiplayerHoeingAssistant.Models.ControlStatus
            {
                PlayerUid = endpoint.Uid, PlayerName = endpoint.Name, BgiStatus = "running", TaskRunning = true,
                TaskStatusAvailable = true, CurrentTaskName = "批量日志隔离任务", ConfigGroups = ["保持原配置组"]
            });

        // UI 与自己的执行端同 UID，独立旁观连接不会抢走执行端，也不会抢答夹具的文件列表。
        await using var viewer = new SignalRClient();
        await viewer.ConnectAsync(url, room, password, self.Uid, "本机监控端", [], isRemote: true);
        var config = new AssistConfig
        {
            ServerUrl = url, PlayerUid = self.Uid, PlayerName = self.Name, ControlRoomPassword = password,
            TeamUids = ownEndpoints.Select(e => e.Uid).ToList(), DisclaimerAccepted = true, ObserverMode = true,
            BgiPath = Path.Combine(root, "bulk-window-" + suffix, "BetterGI.exe")
        };
        var main = new MainViewModel();
        SetField(main, "_config", config);
        SetField(main, "_signalRClient", viewer);
        main.RoomCode = room;
        foreach (var endpoint in ownEndpoints)
            main.Members.Add(new MemberViewModel
            {
                PlayerUid = endpoint.Uid, PlayerName = endpoint.Name, Online = endpoint != offline,
                IsSelf = endpoint == self, IsObserverMode = true
            });
        var window = new MainWindow(main)
        {
            Width = 1280, Height = 860, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000
        };
        RemoteMemberLogsWindow? popup = null;
        try
        {
            main.CurrentPage = AppPage.Dodoco;
            window.Dodoco.SelectedTabIndex = 1;
            window.Show();
            window.UpdateLayout();
            var page = Find<DodocoPage>(window)!;
            popup = await OpenDialogAsync<RemoteMemberLogsWindow>((Button)page.FindName("RemoteMemberLogsButton"));
            var browser = window.Dodoco.Browser;
            Assert.Same(browser, popup.DataContext);
            var download = (Button)popup.FindName("DownloadRoomTodayLogsButton");
            var cancel = (Button)popup.FindName("CancelRoomLogDownloadButton");
            var configBefore = JsonSerializer.Serialize(config);
            var ownStateBefore = BulkRoomState(manager, room);
            var assistStateBefore = BulkRoomState(manager, assistRoom);

            // 正式成员下拉和列表请求取得真实夹具文件，再以已填充、已有选中项的状态开始批量。
            browser.RefreshRemoteMembers();
            ((ComboBox)popup.FindName("RemoteMemberBox")).SetCurrentValue(ComboBox.SelectedItemProperty,
                browser.RemoteMembers.Single(member => member.Uid == self.Uid));
            Invoke(Find<Button>(popup, button => button.Content as string == "获取列表")!);
            var remoteFilesList = (ListBox)popup.FindName("RemoteFilesList");
            await Until(() => remoteFilesList.Items.Count >= 2 && browser.RemoteFiles.Any(file => file.Name == self.TodayFiles[0]));
            var selectedBgiFile = browser.RemoteFiles.Single(file => file.Name == self.TodayFiles[0]);
            remoteFilesList.SetCurrentValue(ListBox.SelectedItemProperty, selectedBgiFile);
            Assert.Same(selectedBgiFile, browser.SelectedRemoteFile);

            var first = await RunBulkButtonAsync(browser, download);
            Assert.False(first.Cancelled);
            Assert.Equal(ownEndpoints.Length, first.Members.Count);
            var selfResult = Assert.Single(first.Members.Where(m => m.Uid == self.Uid));
            Assert.True(selfResult.Complete);
            Assert.Equal(1, selfResult.BgiFiles);
            Assert.Equal(2, selfResult.AssistantFiles);
            Assert.True(Assert.Single(first.Members.Where(m => m.Uid == peer.Uid)).Complete);
            foreach (var failed in new[] { empty, refused, offline })
                Assert.False(Assert.Single(first.Members.Where(m => m.Uid == failed.Uid)).Complete);
            Assert.True(Assert.Single(first.Members.Where(m => m.Uid == refused.Uid)).FailedFiles > 0);
            Assert.Equal(0, offline.ListRequests);
            Assert.True(peer.ListRequests > 0);
            AssertBulkFiles(first.OutputDirectory, self.TodayFiles.Select(n => self.Bytes[n])
                .Concat(refused.TodayFiles.Where(n => n != refused.RefusedFile).Select(n => refused.Bytes[n]))
                .Concat(peer.TodayFiles.Select(n => peer.Bytes[n])));
            Assert.Equal(first.OutputDirectory, browser.RoomLogDownloadDirectory);
            Snapshot(popup, Path.Combine(root, "room-today-logs-result.png"));
            var firstReport = await File.ReadAllTextAsync(Path.Combine(first.OutputDirectory, "下载结果.txt"));
            Assert.Contains(offline.Name, firstReport);
            Assert.Contains(empty.Name, firstReport);
            foreach (var endpoint in ownEndpoints)
                Assert.All(endpoint.RequestedFiles, name => Assert.Contains(name, endpoint.TodayFiles));

            // 第二次使用独立目录；完成一份后在下一份的首块处取消，不再请求后续成员。
            var laterRequests = ownEndpoints.Skip(1).Select(e => e.ListRequests).ToArray();
            var priorResult = browser.LastRoomLogDownloadResult;
            self.HoldSecondDownload();
            await Until(() => download.IsEnabled);
            Invoke(download);
            await self.HeldTransferStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => browser.IsRoomLogDownloadRunning && cancel.IsEnabled);
            VerifyBulkRunningAppearance(popup, root);
            Invoke(cancel);
            await Until(() => !browser.IsRoomLogDownloadRunning && browser.LastRoomLogDownloadResult != null
                && !ReferenceEquals(priorResult, browser.LastRoomLogDownloadResult));
            var cancelled = browser.LastRoomLogDownloadResult!;
            Assert.True(cancelled.Cancelled);
            Assert.NotEqual(first.OutputDirectory, cancelled.OutputDirectory);
            AssertBulkFiles(cancelled.OutputDirectory, [self.Bytes[self.CompletedBeforeHold!]]);
            Assert.Equal(laterRequests, ownEndpoints.Skip(1).Select(e => e.ListRequests).ToArray());
            self.ReleaseHeldTransfer();
            await self.DrainCallbacksAsync();
            await Task.Delay(100);
            AssertBulkFiles(cancelled.OutputDirectory, [self.Bytes[self.CompletedBeforeHold!]]);

            // 同一正式窗口切换协助房，目标必须取自协助房快照，不能继续下载本房间成员。
            Assert.True(await browser.ConnectAssistedLogRoomAsync(assistRoom, password));
            var ownRequestsBeforeAssist = ownEndpoints.Select(e => e.ListRequests).ToArray();
            var assisted = await RunBulkButtonAsync(browser, download);
            Assert.False(assisted.Cancelled);
            Assert.Equal(2, assisted.Members.Count);
            Assert.True(Assert.Single(assisted.Members.Where(m => m.Uid == assist.Uid)).Complete);
            Assert.False(Assert.Single(assisted.Members.Where(m => m.Uid == assistOffline.Uid)).Complete);
            Assert.DoesNotContain(assisted.Members, m => ownEndpoints.Any(e => e.Uid == m.Uid));
            Assert.Equal(ownRequestsBeforeAssist, ownEndpoints.Select(e => e.ListRequests).ToArray());
            AssertBulkFiles(assisted.OutputDirectory, assist.TodayFiles.Select(n => assist.Bytes[n]));
            Assert.Equal(0, assistOffline.ListRequests);
            Invoke((Button)popup.FindName("ReturnOwnLogRoomButton"));
            await Until(() => !browser.IsAssistedLogRoom);

            Assert.Equal(configBefore, JsonSerializer.Serialize(config));
            Assert.Equal(ownStateBefore, BulkRoomState(manager, room));
            Assert.Equal(assistStateBefore, BulkRoomState(manager, assistRoom));
            Assert.Same(viewer, typeof(MainViewModel).GetField("_signalRClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main));
            Assert.All(allEndpoints, endpoint => Assert.Equal(0, endpoint.CommandCount));
            foreach (var endpoint in allEndpoints)
            {
                await endpoint.DrainCallbacksAsync();
                Assert.Empty(endpoint.CallbackErrors);
            }
        }
        finally
        {
            self.ReleaseHeldTransfer();
            popup?.Close();
            window.Hide();
            window.Dodoco.Dispose();
        }
    }

    private static void VerifyBulkRunningAppearance(RemoteMemberLogsWindow popup, string root)
    {
        popup.UpdateLayout();
        var list = (ListBox)popup.FindName("RemoteFilesList");
        var assembly = typeof(RemoteMemberLogsWindow).Assembly;
        File.WriteAllText(Path.Combine(root, "loaded-window-identity.json"), JsonSerializer.Serialize(new
        {
            AssemblyPath = assembly.Location,
            ModuleVersionId = assembly.ManifestModule.ModuleVersionId,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            SelectedItemCount = list.SelectedItems.Count,
            ItemCount = list.Items.Count,
            SelectionActive = System.Windows.Controls.Primitives.Selector.GetIsSelectionActive(list)
        }));
        Assert.True(list.Items.Count >= 2);
        var selectedFile = Assert.IsType<MemberLogFileDescriptor>(list.SelectedItem);
        Assert.True(LogFileBrowser.BgiLogFileNameRegex.IsMatch(selectedFile.Name));
        Assert.True(list.IsEnabled);
        Assert.False(Find<Button>(popup, button => button.Content as string == "获取列表")!.IsEnabled);
        Assert.False(Find<Button>(popup, button => button.Content as string == "下载")!.IsEnabled);
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(popup)
            .TransformBounds(new Rect(element.RenderSize));
        Assert.InRange(Bounds(list).Top, 0, 150);
        Assert.True(list.ActualHeight > 0);
        void CheckListColors(string screenshot)
        {
            list.ScrollIntoView(selectedFile);
            popup.UpdateLayout();
            var selectedRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(selectedFile));
            Assert.True(selectedRow.IsSelected);
            var lastRowBottom = Enumerable.Range(0, list.Items.Count)
                .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ListBoxItem>()
                .Max(row => row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize)).Bottom);
            Assert.True(lastRowBottom + 8 < list.ActualHeight, "本次夹具必须留下可采样的列表空白区。");
            var blankPoint = list.TranslatePoint(new Point(list.ActualWidth - 24,
                (lastRowBottom + list.ActualHeight) / 2), popup);
            var selectedPoint = selectedRow.TranslatePoint(new Point(selectedRow.ActualWidth - 16,
                selectedRow.ActualHeight / 2), popup);
            Snapshot(popup, Path.Combine(root, screenshot));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth),
                (int)Math.Ceiling(popup.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(popup);
            foreach (var sample in new[] { (Name: "列表空白区", Point: blankPoint), (Name: "选中行右侧背景", Point: selectedPoint) })
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)sample.Point.X, (int)sample.Point.Y, 1, 1), pixel, 4, 0);
                Assert.True(pixel[3] > 0 && pixel[0] < 128 && pixel[1] < 128 && pixel[2] < 128,
                    $"{sample.Name}必须保持深色，实际 BGRA={string.Join(",", pixel)}。");
            }
        }
        CheckListColors("running-populated.png");
        var enabledValue = list.ReadLocalValue(UIElement.IsEnabledProperty);
        var wasEnabled = list.IsEnabled;
        try
        {
            list.SetCurrentValue(UIElement.IsEnabledProperty, false);
            CheckListColors("running-disabled.png");
        }
        finally
        {
            if (enabledValue == DependencyProperty.UnsetValue) list.ClearValue(UIElement.IsEnabledProperty);
            else list.SetCurrentValue(UIElement.IsEnabledProperty, wasEnabled);
            popup.UpdateLayout();
        }

        var originalWidth = popup.Width;
        try
        {
            popup.Width = popup.MinWidth;
            popup.UpdateLayout();
            Assert.True(list.ActualHeight > 0);
            Assert.True(((FrameworkElement)popup.FindName("RoomLogActionsRow")).ActualHeight > 0);
            Assert.True(((FrameworkElement)popup.FindName("SingleLogFiltersRow")).ActualHeight > 0);
            IEnumerable<Button> Buttons(DependencyObject parent)
            {
                if (parent is Button button) yield return button;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    foreach (var child in Buttons(VisualTreeHelper.GetChild(parent, i))) yield return child;
            }
            var controls = Buttons(popup).Cast<FrameworkElement>().Concat(new[]
            {
                (FrameworkElement)popup.FindName("RemoteMemberBox"),
                (FrameworkElement)popup.FindName("RemoteDateFilterBox")
            }).Where(control => control.IsVisible).ToArray();
            var bounds = controls.Select(Bounds).ToArray();
            for (var i = 0; i < bounds.Length; i++)
            {
                Assert.True(bounds[i].Width > 0 && bounds[i].Height > 0);
                Assert.InRange(bounds[i].Left, 0, popup.ActualWidth);
                Assert.InRange(bounds[i].Right, 0, popup.ActualWidth);
                Assert.InRange(bounds[i].Top, 0, popup.ActualHeight);
                Assert.InRange(bounds[i].Bottom, 0, popup.ActualHeight);
                for (var j = i + 1; j < bounds.Length; j++)
                {
                    var overlap = Rect.Intersect(bounds[i], bounds[j]);
                    Assert.True(overlap.IsEmpty || overlap.Width <= 0.5 || overlap.Height <= 0.5,
                        $"窄窗口控件发生重叠：{controls[i].Name}/{controls[j].Name}。");
                }
            }
        }
        finally
        {
            popup.Width = originalWidth;
            popup.UpdateLayout();
        }
    }

    private static async Task<RoomLogDownloadResult> RunBulkButtonAsync(LogBrowserViewModel browser, Button button)
    {
        var previous = browser.LastRoomLogDownloadResult;
        await Until(() => button.IsEnabled);
        Invoke(button);
        await Until(() => !browser.IsRoomLogDownloadRunning && browser.LastRoomLogDownloadResult != null
            && !ReferenceEquals(previous, browser.LastRoomLogDownloadResult));
        return browser.LastRoomLogDownloadResult!;
    }

    private static string[] BulkRoomState(RoomManager manager, string room) => manager.GetControlRoomPlayers("CTRL_" + room)
        .OrderBy(p => p.PlayerUid).Select(p => JsonSerializer.Serialize(new
        {
            p.PlayerUid, p.ConnectionId, p.Online, p.TaskRunning, p.TaskStatusAvailable,
            p.CurrentTaskName, p.ConfigGroups
        })).ToArray();

    private static void AssertBulkFiles(string directory, IEnumerable<byte[]> expected)
    {
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "下载结果.txt")));
        var paths = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Assert.All(paths, path => Assert.True(Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path) == "下载结果.txt", "不能遗留未完成文件：" + path));
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(expected.Select(Hash).OrderBy(h => h).ToArray(), paths.Where(p => Path.GetExtension(p) == ".log")
            .Select(p => Hash(File.ReadAllBytes(p))).OrderBy(h => h).ToArray());
    }

    private sealed class BulkLogEndpoint : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentBag<Task> _callbacks = new();
        private TaskCompletionSource _releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listRequests;
        private int _commandCount;
        private int _downloadsInRun;
        private int _disposed;
        private volatile bool _holdSecondDownload;

        public SignalRClient Client { get; } = new();
        public string Uid { get; }
        public string Name { get; }
        public string[] TodayFiles { get; }
        public Dictionary<string, byte[]> Bytes { get; } = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> RequestedFiles { get; } = new();
        public ConcurrentQueue<Exception> CallbackErrors { get; } = new();
        public TaskCompletionSource HeldTransferStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CompletedBeforeHold { get; private set; }
        public string? RefusedFile { get; set; }
        public bool ReturnEmptyList { get; set; }
        public int ListRequests => Volatile.Read(ref _listRequests);
        public int CommandCount => Volatile.Read(ref _commandCount);

        private BulkLogEndpoint(string uid, string name)
        {
            Uid = uid;
            Name = name;
            var day = DateTime.Today.ToString("yyyyMMdd");
            var assistantDay = DateTime.Today.ToString("yyyy-MM-dd");
            TodayFiles = [$"better-genshin-impact{day}.log", $"assistant_runtime.{assistantDay}.log", $"assistant_runtime.{assistantDay}.001.log"];
            foreach (var file in TodayFiles.Concat(new[]
            {
                $"better-genshin-impact{DateTime.Today.AddDays(-1):yyyyMMdd}.log",
                $"assistant_runtime.{DateTime.Today.AddDays(-1):yyyy-MM-dd}.log", "other-kind.log"
            }))
                Bytes[file] = Encoding.UTF8.GetBytes($"[10:00:00.000] [INF] {uid} {file}\n" + new string('测', 2048));
            Client.OnMemberLogFilesRequested += FilesRequested;
            Client.OnMemberLogDownloadRequested += DownloadRequested;
            Client.OnRemoteCommand += RemoteCommandReceived;
        }

        public static async Task<BulkLogEndpoint> ConnectAsync(string url, string room, string password, string uid, string name)
        {
            var endpoint = new BulkLogEndpoint(uid, name);
            try
            {
                await endpoint.Client.ConnectAsync(url, room, password, uid, name, []);
                return endpoint;
            }
            catch { await endpoint.DisposeAsync(); throw; }
        }

        public void HoldSecondDownload()
        {
            _downloadsInRun = 0;
            CompletedBeforeHold = null;
            HeldTransferStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdSecondDownload = true;
        }

        public void ReleaseHeldTransfer() => _releaseHeld.TrySetResult();
        private void RemoteCommandReceived(RemoteCommand _) => Interlocked.Increment(ref _commandCount);

        private void FilesRequested(string requester, string request) => Observe(async () =>
        {
            Interlocked.Increment(ref _listRequests);
            await Client.ReportMemberLogFilesAsync(request, ReturnEmptyList ? [] : Bytes.Select(pair => new MemberLogFileDescriptor
            {
                Name = pair.Key, Size = pair.Value.Length,
                LastWrite = TodayFiles.Contains(pair.Key) || pair.Key == "other-kind.log" ? DateTime.Today : DateTime.Today.AddDays(-1)
            }).ToList());
        });

        private void DownloadRequested(string requester, string request, string file) => Observe(async () =>
        {
            RequestedFiles.Enqueue(file);
            if (file == RefusedFile)
            {
                await Client.ReportMemberLogChunkAsync(request, 0, 0, "", file, done: true);
                return;
            }
            Assert.True(Bytes.TryGetValue(file, out var bytes), "请求了夹具未提供的文件：" + file);
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(bytes, _lifetime.Token);
            var payload = compressed.ToArray();
            const int count = 3;
            var slice = (payload.Length + count - 1) / count;
            var ordinal = Interlocked.Increment(ref _downloadsInRun);
            for (var seq = 0; seq < count; seq++)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var start = seq * slice;
                var chunk = Convert.ToBase64String(payload, start, Math.Min(slice, payload.Length - start));
                if (_holdSecondDownload && ordinal == 1 && seq == count - 1) CompletedBeforeHold = file;
                await Client.ReportMemberLogChunkAsync(request, seq, count, chunk, file, done: seq == count - 1);
                if (_holdSecondDownload && ordinal == 2 && seq == 0)
                {
                    HeldTransferStarted.TrySetResult();
                    await _releaseHeld.Task.WaitAsync(_lifetime.Token);
                }
                if (seq < count - 1) await Task.Delay(45, _lifetime.Token);
            }
        });

        private void Observe(Func<Task> callback) => _callbacks.Add(Task.Run(async () =>
        {
            try { await callback(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { CallbackErrors.Enqueue(ex); }
        }));

        public async Task DrainCallbacksAsync() => await Task.WhenAll(_callbacks.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Client.OnMemberLogFilesRequested -= FilesRequested;
            Client.OnMemberLogDownloadRequested -= DownloadRequested;
            Client.OnRemoteCommand -= RemoteCommandReceived;
            _lifetime.Cancel();
            await DrainCallbacksAsync();
            await Client.DisposeAsync();
            _lifetime.Dispose();
        }
    }
}
