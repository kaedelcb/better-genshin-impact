using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
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
    private static async Task VerifySameUidObserverAsync(string url, RoomManager manager, string root)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var room = "observer-self-" + suffix;
        var uid = "same-uid-" + suffix;
        var playerName = "自己的执行端-" + suffix;
        const string password = "observer-self-local-only";
        var executorRoot = Path.Combine(root, "executor-" + suffix);
        var monitorRoot = Path.Combine(root, "monitor-" + suffix);
        Directory.CreateDirectory(Path.Combine(executorRoot, "log"));
        Directory.CreateDirectory(Path.Combine(monitorRoot, "log"));
        var remoteFileName = "better-genshin-impact20991016.log";
        var remoteContent = Encoding.UTF8.GetBytes("[19:10:00.000] [INF] 仅执行端目录中的完整日志 " + suffix + "\n");
        await File.WriteAllBytesAsync(Path.Combine(executorRoot, "log", remoteFileName), remoteContent);
        await File.WriteAllTextAsync(Path.Combine(monitorRoot, "log", "better-genshin-impact20991017.log"),
            "[19:10:00.000] [INF] 监控电脑本机日志\n");
        await using var executor = new SignalRClient();
        await using var observer = new SignalRClient();
        await executor.ConnectAsync(url, room, password, uid, playerName, []);
        await executor.ReportControlStatusAsync(new MultiplayerHoeingAssistant.Models.ControlStatus
        {
            PlayerUid = uid, PlayerName = playerName, BgiStatus = "running", TaskRunning = true,
            TaskStatusAvailable = true, CurrentTaskName = "跨电脑隔离任务"
        });
        var executionConnection = Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room)).ConnectionId;
        await observer.ConnectAsync(url, room, password, uid, "监控端", [], isRemote: true);

        var executionVm = LogEndpointVm(executor, url, uid, playerName, executorRoot, observerMode: false);
        var monitorVm = LogEndpointVm(observer, url, uid, playerName, monitorRoot, observerMode: true);
        var executionWindow = new MainWindow(executionVm) { ShowActivated = false, Left = -20000, Top = -20000 };
        var monitorWindow = new MainWindow(monitorVm) { Width = 1280, Height = 860, ShowActivated = false, Left = -20000, Top = -20000 };
        try
        {
            executionVm.CurrentPage = monitorVm.CurrentPage = AppPage.Dodoco;
            executionWindow.Dodoco.SelectedTabIndex = 1;
            executionWindow.Show();
            monitorWindow.Show();
            var logs = monitorWindow.Dodoco;
            var browser = logs.Browser;
            browser.RefreshRemoteMembers();
            Assert.Equal(uid, Assert.Single(browser.RemoteMembers).Uid);
            Assert.Contains("我的执行端", browser.RemoteMembers[0].Name);
            Assert.DoesNotContain(executionWindow.Dodoco.LogSources, s => s.Key == uid);
            Assert.Empty(executionWindow.Dodoco.Browser.RemoteMembers);

            // 同 UID 的两个独立网关连接：监控端订阅执行端；执行端收到自身回送仍去重。
            var remoteSource = Assert.Single(logs.LogSources.Where(s => s.Key == uid));
            logs.SelectedSource = remoteSource;
            await Until(() => manager.GetLogSubscriberCount("CTRL_" + room, uid) == 1);
            await executor.SubscribeMemberLogAsync(uid);
            var selfDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var marker = "跨电脑同UID实时日志-" + suffix;
            void ReceiveSelf(MemberLogBatch batch)
            { if (batch.Lines.Any(line => line.Contains(marker))) selfDelivery.TrySetResult(); }
            executor.OnMemberLogBatch += ReceiveSelf;
            var monitorRelay = typeof(DodocoViewModel).GetField("_logRelay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(logs);
            var executorRelay = typeof(DodocoViewModel).GetField("_logRelay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executionWindow.Dodoco);
            var boundClient = typeof(MemberLogRelayService).GetField("_hooked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Until(() => ReferenceEquals(boundClient.GetValue(monitorRelay), observer)
                && ReferenceEquals(boundClient.GetValue(executorRelay), executor));
            await executor.ReportMemberLogBatchAsync([$"[19:11:00.000] [INF] {marker}"], false);
            await selfDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => logs.VisibleEntries.Any(e => e.Message.Contains(marker)));
            Assert.DoesNotContain(executionWindow.Dodoco.LogSources, s => s.Key == uid);
            executor.OnMemberLogBatch -= ReceiveSelf;
            await executor.UnsubscribeMemberLogAsync(uid);
            logs.SelectedSource = logs.LogSources.Single(s => s.Key == DodocoViewModel.LocalSourceKey);
            await Until(() => manager.GetLogSubscriberCount("CTRL_" + room, uid) == 0);
            Assert.DoesNotContain(logs.VisibleEntries, e => e.Message.Contains(marker));

            // 监控电脑本机目录不同；必须经成员列表请求同 UID 执行端的文件并下载。
            logs.SelectedTabIndex = 1;
            Assert.DoesNotContain(browser.Files.Where(f => f.Group == "本机 · BGI 日志"), f => f.Name == remoteFileName);
            browser.SelectedRemoteMember = browser.RemoteMembers.Single(m => m.Uid == uid);
            var page = Find<DodocoPage>(monitorWindow)!;
            Invoke(Find<Button>(page, b => b.Content as string == "获取列表")!);
            await Until(() => browser.RemoteFiles.Any(f => f.Name == remoteFileName));
            browser.SelectedRemoteFile = browser.RemoteFiles.Single(f => f.Name == remoteFileName);
            Invoke(Find<Button>(page, b => b.Content as string == "下载")!);
            await Until(() => browser.RemoteStatus.StartsWith("已下载到 "));
            Assert.Equal(remoteContent, await File.ReadAllBytesAsync(browser.RemoteStatus["已下载到 ".Length..]));
            Snapshot(monitorWindow, Path.Combine(root, "observer-own-executor-log.png"));

            // 角色变化立即刷新，执行模式不能把监控时缓存的同 UID 流重新补回下拉。
            SetObserverRole(monitorVm, false);
            Assert.Empty(browser.RemoteMembers);
            Assert.DoesNotContain(logs.LogSources, s => s.Key == uid);
            SetObserverRole(monitorVm, true);
            Assert.Equal(uid, Assert.Single(browser.RemoteMembers).Uid);
            Assert.Contains(logs.LogSources, s => s.Key == uid);
            var actual = Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room));
            Assert.Equal(executionConnection, actual.ConnectionId);
            Assert.True(actual.TaskRunning);
            Assert.True(executor.IsConnected && observer.IsConnected);
        }
        finally { executionWindow.Hide(); monitorWindow.Hide(); }
    }

    private static MainViewModel LogEndpointVm(SignalRClient client, string url, string uid,
        string name, string bgiRoot, bool observerMode)
    {
        var vm = new MainViewModel();
        SetField(vm, "_config", new AssistConfig
        {
            ServerUrl = url, PlayerUid = uid, PlayerName = name, ControlRoomPassword = "observer-self-local-only",
            TeamUids = [uid], DisclaimerAccepted = true, ObserverMode = observerMode,
            BgiPath = Path.Combine(bgiRoot, "BetterGI.exe")
        });
        SetField(vm, "_signalRClient", client);
        vm.Members.Add(new MemberViewModel { PlayerUid = uid, PlayerName = name, Online = true, IsSelf = true,
            IsObserverMode = observerMode, MachineId = "executor-computer" });
        return vm;
    }

    private static void SetObserverRole(MainViewModel vm, bool observerMode)
    {
        vm.Config!.ObserverMode = observerMode;
        typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [nameof(MainViewModel.IsObserverMode)]);
    }
}
