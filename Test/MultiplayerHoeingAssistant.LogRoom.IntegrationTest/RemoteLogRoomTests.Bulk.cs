using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
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
