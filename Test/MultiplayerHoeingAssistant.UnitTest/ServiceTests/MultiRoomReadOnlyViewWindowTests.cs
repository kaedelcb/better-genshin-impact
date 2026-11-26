using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;
using Path = System.IO.Path;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>多房间视图、顶栏固定宽度与 Star 文案、房间弹窗金白配色、成员卡房间标签及单卡远控 UI 的实际 WPF 布局验收。</summary>
[Collection("MultiRoomWpf")]
public sealed class MultiRoomReadOnlyViewWindowTests
{
    private sealed class FakeRoomSpectator(AssistantRoomProfile profile, string serverUrl, bool bypassSystemProxy) : IRoomSpectatorSession
    {
        private List<ControlRoomPlayer> _players = [];
        public string ProfileId { get; } = profile.Id;
        public string ServerUrl { get; } = serverUrl;
        public string RoomCode { get; } = profile.RoomCode;
        public string Password { get; } = profile.Password;
        public bool BypassSystemProxy { get; } = bypassSystemProxy;
        public string SpectatorUid { get; } = "web_logviewer_wpf_" + Guid.NewGuid().ToString("N");
        public bool IsConnected => true;
        public bool IsConnecting => false;
        public string? Error => null;
        public event Action? Changed;
        public event Action<RemoteCommand>? ReplyReceived;
        public event Action<RemoteCommandResult>? CommandResultReceived;
        public IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot() => _players;
        public void Start() { }
        public void ForceReconnect() { }
        public Task<bool> SendRemoteCommandAsync(RemoteCommand command) => Task.FromResult(true);
        public Task<bool> ClearOnlineHistoryAsync(string targetUid) => Task.FromResult(true);
        public Task<CoordinatedBatchSnapshot?> GetCurrentCoordinatedBatchAsync(CancellationToken ct = default)
            => Task.FromResult<CoordinatedBatchSnapshot?>(null);
        public Task<CoordinatedBatchResetResult?> ResetCoordinatedBatchAsync(
            string resetId, IReadOnlyList<string> requestedTargets,
            int? expectedGeneration = null, string? expectedBatchId = null, CancellationToken ct = default)
            => Task.FromResult<CoordinatedBatchResetResult?>(null);
        public void SetPlayers(params ControlRoomPlayer[] players)
        {
            _players = [.. players];
            Changed?.Invoke();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MainWindow_RendersViewingBadgeCompactGearRoomPopupColorsAndCardRoomLabels()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须先为本进程设置隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                Assert.Null(Application.Current);
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    MainWindow? window = null;
                    MainViewModel? vm = null;
                    try
                    {
                        var activeRoom = new AssistantRoomProfile
                        {
                            Id = "room-active", Name = "罐传精队", RoomCode = "B54387", Note = "茶包，老鲨鱼，渺茫，兴起",
                            ServerUrl = "https://rooms-a.example.test", Password = "pw-a",
                            TeamUids = ["100000001", "100000002", "100000003"]
                        };
                        var secondRoom = new AssistantRoomProfile
                        {
                            Id = "room-1234", Name = "1234", RoomCode = "24D888", Note = "1234",
                            ServerUrl = "https://rooms-a.example.test", Password = "pw-b",
                            TeamUids = ["100000001", "100000004"]
                        };

                        var sessions = new Dictionary<string, FakeRoomSpectator>(StringComparer.Ordinal);
                        vm = new MainViewModel
                        {
                            RoomSpectatorFactory = (profile, serverUrl, bypass) =>
                            {
                                var fake = new FakeRoomSpectator(profile, serverUrl, bypass);
                                sessions[profile.Id] = fake;
                                return fake;
                            }
                        };
                        var isolatedBgi = Path.Combine(AssistantDataDirectory.Root, "nonexistent-bgi", "BetterGI.exe");
                        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm,
                            new AssistConfig
                            {
                                ObserverMode = true, StandaloneMode = false, BgiPath = isolatedBgi,
                                PlayerUid = "100000001", PlayerName = "茶包", DisclaimerAccepted = true,
                                ServerUrl = activeRoom.ServerUrl, ControlRoomPassword = activeRoom.Password,
                                TeamUids = [.. activeRoom.TeamUids],
                                ActiveRoomProfileId = activeRoom.Id, LocalRoomProfileId = activeRoom.Id,
                                ViewedRoomProfileId = AssistConfig.AllRoomsViewId,
                                RoomProfiles = [activeRoom, secondRoom]
                            });
                        vm.RoomCode = activeRoom.RoomCode;
                        vm.Members.Add(new MemberViewModel
                        {
                            PlayerUid = "100000001", PlayerName = "茶包", Online = true, IsSelf = true, BgiStatus = "running",
                            AvatarPath = "pack://application:,,,/Assets/Images/Ayaka.png", AvatarRing = "#8FC1E8"
                        });
                        vm.Members.Add(new MemberViewModel
                        {
                            PlayerUid = "100000002", PlayerName = "老鲨鱼", Online = false, BgiStatus = "offline",
                            AvatarPath = "pack://application:,,,/Assets/Images/Kazuha.png", AvatarRing = "#6FD8CE"
                        });
                        typeof(MainViewModel).GetMethod("NotifyRoomProfiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);

                        var secondSession = Assert.Contains(secondRoom.Id, sessions);
                        secondSession.SetPlayers(
                            new ControlRoomPlayer { PlayerUid = "100000001", PlayerName = "茶包", Online = false, BgiStatus = "offline" },
                            new ControlRoomPlayer { PlayerUid = "100000004", PlayerName = "一时兴起", Online = true, BgiStatus = "running" });

                        vm.CurrentPage = AppPage.Home;
                        window = new MainWindow(vm)
                        {
                            Width = 1180, Height = 820, ShowActivated = false, ShowInTaskbar = false,
                            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
                        };
                        window.Show();
                        await Task.Delay(120);
                        window.UpdateLayout();

                        // 1. 顶栏布局：紧凑齿轮设置靠右、左侧显示「查看 所有房间」标签、下方房间号框对齐无遮挡
                        static Rect Bounds(FrameworkElement e, Visual root)
                            => e.TransformToAncestor(root).TransformBounds(new Rect(new Point(), e.RenderSize));

                        foreach (var width in new[] { 980d, 1180d, 1440d })
                        {
                            window.Width = width;
                            window.UpdateLayout();
                            var settings = (Button)window.FindName("HeaderSettingsButton");
                            var viewing = (Button)window.FindName("HeaderViewingRoomButton");
                            var viewingText = (TextBlock)window.FindName("HeaderViewingRoomText");
                            var exit = (FrameworkElement)window.FindName("HeaderExitButton");
                            var room = (FrameworkElement)window.FindName("HeaderRoomButton");
                            var refresh = (FrameworkElement)window.FindName("HeaderRefreshButton");
                            var starMessage = (FrameworkElement)window.FindName("StarMessage");

                            Assert.Equal("⚙", settings.Content);
                            Assert.Equal("所有房间", viewingText.Text);
                            if (width >= 1180d)
                            {
                                Assert.Equal(Visibility.Visible, starMessage.Visibility);
                            }
                            var s = Bounds(settings, window);
                            var v = Bounds(viewing, window);
                            var x = Bounds(exit, window);
                            var r = Bounds(room, window);
                            var f = Bounds(refresh, window);
                            Assert.True(s.Width <= 40, $"齿轮设置按钮过宽: {s.Width}");
                            Assert.True(v.Right <= s.Left + 0.5, $"查看标签与齿轮按钮重叠: v={v} s={s}");
                            Assert.True(Math.Abs(v.Left - r.Left) <= 1.5, $"查看标签左缘未对齐房间号框: v={v} r={r}");
                            Assert.True(Math.Abs(s.Right - r.Right) <= 1.5, $"齿轮按钮右缘未对齐房间号框: s={s} r={r}");
                            Assert.True(Math.Abs(x.Right - f.Right) <= 1.5, $"退出与刷新按钮右缘未对齐: x={x} f={f}");
                            Assert.True(s.Right <= x.Left + 0.5 && x.Right <= window.ActualWidth + 0.5);
                        }

                        // 1b. 默认宽度 1180（及更宽窗口）切不同查看房间（所有房间 / 1234 / 罐传精队）时，
                        //     顶栏查看按钮与房间号框宽度严格固定，且左侧 GitHub Star 文案绝不被隐藏。
                        window.Width = 1180;
                        window.UpdateLayout();
                        var fixedViewingBtn = (Button)window.FindName("HeaderViewingRoomButton");
                        var fixedRoomBtn = (FrameworkElement)window.FindName("HeaderRoomButton");
                        var fixedStarMessage = (FrameworkElement)window.FindName("StarMessage");
                        var baselineViewingWidth = fixedViewingBtn.ActualWidth;
                        var baselineRoomWidth = fixedRoomBtn.ActualWidth;
                        Assert.Equal(Visibility.Visible, fixedStarMessage.Visibility);

                        foreach (var roomId in new[] { secondRoom.Id, activeRoom.Id, AssistConfig.AllRoomsViewId })
                        {
                            vm.SelectViewedRoom(roomId, persist: false);
                            await Task.Delay(40);
                            window.UpdateLayout();
                            Assert.InRange(Math.Abs(fixedViewingBtn.ActualWidth - baselineViewingWidth), 0, 0.5);
                            Assert.InRange(Math.Abs(fixedRoomBtn.ActualWidth - baselineRoomWidth), 0, 0.5);
                            Assert.Equal(Visibility.Visible, fixedStarMessage.Visibility);
                        }
                        // 在极窄 980 宽度下也验证切房时顶栏宽度严格固定
                        window.Width = 980;
                        window.UpdateLayout();
                        foreach (var roomId in new[] { secondRoom.Id, activeRoom.Id, AssistConfig.AllRoomsViewId })
                        {
                            vm.SelectViewedRoom(roomId, persist: false);
                            await Task.Delay(40);
                            window.UpdateLayout();
                            Assert.InRange(Math.Abs(fixedViewingBtn.ActualWidth - baselineViewingWidth), 0, 0.5);
                            Assert.InRange(Math.Abs(fixedRoomBtn.ActualWidth - baselineRoomWidth), 0, 0.5);
                        }
                        window.Width = 1180;
                        window.UpdateLayout();
                        // 重新补齐 1234 成员数据（切回 activeRoom 时会释放并重建 1234 旁观会话）
                        sessions[secondRoom.Id].SetPlayers(
                            new ControlRoomPlayer { PlayerUid = "100000001", PlayerName = "茶包", Online = false, BgiStatus = "offline" },
                            new ControlRoomPlayer { PlayerUid = "100000004", PlayerName = "一时兴起", Online = true, BgiStatus = "running" });
                        await Task.Delay(40);
                        window.UpdateLayout();

                        // 2. 所有房间视图下，全部成员卡片都在齿轮右侧显示所属房间标签
                        var cardsControl = (ItemsControl)window.FindName("DisplayedMembersControl");
                        cardsControl.UpdateLayout();
                        var roomBadges = FindVisualChildren<Border>(cardsControl)
                            .Where(b => b.Name == "MemberRoomLabelBadge")
                            .ToList();
                        Assert.Equal(3, roomBadges.Count);
                        Assert.All(roomBadges, badge => Assert.Equal(Visibility.Visible, badge.Visibility));
                        var badgeTexts = roomBadges
                            .Select(b => FindVisualChildren<TextBlock>(b).Single(t => t.Name == "MemberRoomLabelText").Text)
                            .ToList();
                        Assert.Equal(["罐传精队", "罐传精队", "1234"], badgeTexts);
                        // 验证第一个卡片的房间标签位于小齿轮按钮右侧且无重叠
                        var firstCard = (FrameworkElement)cardsControl.ItemContainerGenerator.ContainerFromIndex(0);
                        var firstGear = FindVisualChildren<Button>(firstCard).First(b => Equals(b.Content, "⚙"));
                        var firstBadge = FindVisualChildren<Border>(firstCard).Single(b => b.Name == "MemberRoomLabelBadge");
                        var gearRect = Bounds(firstGear, firstCard);
                        var badgeRect = Bounds(firstBadge, firstCard);
                        Assert.True(badgeRect.Left >= gearRect.Right + 5, $"房间标签应在齿轮右侧且保留间距: gear={gearRect} badge={badgeRect}");

                        // 3. 房间弹窗：点未登录房间（1234）时，登录房间保持金黄+勾选，1234 保持白色+无勾选，且耕地机实时切到 1234 只读视图
                        var trigger = (Button)window.FindName("HeaderRoomButton");
                        var popup = (Popup)window.FindName("AssistantRoomPopup");
                        var selector = (ListBox)window.FindName("AssistantRoomSelector");
                        var allRoomsBtn = (Button)window.FindName("AllRoomsViewButton");
                        var quickCommands = (Grid)window.FindName("QuickCommandsGrid");
                        var viewingLabel = (TextBlock)window.FindName("HeaderViewingRoomText");

                        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        selector.UpdateLayout();
                        Assert.True(popup.IsOpen);

                        selector.SelectedItem = secondRoom;
                        // Programmatic selected-item sync is not a user's view switch.
                        Assert.True(vm.IsViewingAllRooms);
                        var selectedRow = (ListBoxItem)selector.ItemContainerGenerator.ContainerFromItem(secondRoom);
                        selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = selectedRow });
                        await Task.Delay(80);
                        selector.UpdateLayout();
                        cardsControl.UpdateLayout();

                        var activeItem = (ListBoxItem)selector.ItemContainerGenerator.ContainerFromItem(activeRoom);
                        var secondItem = (ListBoxItem)selector.ItemContainerGenerator.ContainerFromItem(secondRoom);
                        var goldText = window.FindResource("GoldText").ToString();
                        var whiteText = window.FindResource("White").ToString();
                        Assert.Equal(goldText, activeItem.Foreground.ToString());
                        Assert.Equal(whiteText, secondItem.Foreground.ToString());

                        var activeCheck = FindVisualChildren<System.Windows.Shapes.Path>(activeItem).Single(p => p.Name == "ActiveRoomCheckMark");
                        var secondCheck = FindVisualChildren<System.Windows.Shapes.Path>(secondItem).Single(p => p.Name == "ActiveRoomCheckMark");
                        Assert.Equal(Visibility.Visible, activeCheck.Visibility);
                        Assert.Equal(Visibility.Hidden, secondCheck.Visibility);

                        // 耕地机已切到 1234 视图，且未改变当前登录房间；单卡片远控按钮可用，批量勾选与底部快捷指令保持隔离禁用
                        Assert.Equal(activeRoom.Id, vm.ActiveRoomProfile?.Id);
                        Assert.Equal(secondRoom.Id, vm.ViewedRoomProfileId);
                        Assert.Equal("1234", viewingLabel.Text);
                        Assert.False(quickCommands.IsEnabled);
                        Assert.Equal(2, vm.DisplayedMembers.Count);
                        Assert.False(vm.DisplayedMembers[0].Online); // 我在 1234 是离线的
                        var singleRoomBadges = FindVisualChildren<Border>(cardsControl).Where(b => b.Name == "MemberRoomLabelBadge").ToList();
                        Assert.All(singleRoomBadges, b => Assert.Equal(Visibility.Collapsed, b.Visibility));

                        var otherOnlineCard = (FrameworkElement)cardsControl.ItemContainerGenerator.ContainerFromIndex(1);
                        var otherCheckBox = FindVisualChildren<CheckBox>(otherOnlineCard).First();
                        Assert.False(otherCheckBox.IsEnabled); // 非登录房不参与登录房批量勾选
                        var otherStopBtn = FindVisualChildren<Button>(otherOnlineCard).First(b => Equals(b.Content, "停止"));
                        var otherStartBgiBtn = FindVisualChildren<Button>(otherOnlineCard).First(b => Equals(b.Content, "启动BGI"));
                        var otherGearBtn = FindVisualChildren<Button>(otherOnlineCard).First(b => Equals(b.Content, "⚙"));
                        Assert.True(otherStopBtn.IsEnabled);
                        Assert.True(otherStartBgiBtn.IsEnabled);
                        Assert.True(otherGearBtn.IsEnabled);

                        // 4. 点「所有房间」切回全房间视图
                        allRoomsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        cardsControl.UpdateLayout();
                        Assert.True(vm.IsViewingAllRooms);
                        Assert.Equal("所有房间", viewingLabel.Text);
                        Assert.True(quickCommands.IsEnabled);
                        Assert.Equal(3, vm.DisplayedMembers.Count);

                        // Monitoring mode exposes the same entry and remembers both directions.
                        // The unconnected monitor must not display its local room mirror as executor data.
                        var testConfig = vm.Config!;
                        var monitorManager = new AssistConfigManager();
                        Assert.True(monitorManager.TrySave(testConfig));
                        typeof(MainViewModel).GetField("_configManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, monitorManager);
                        testConfig.StandaloneMode = true; // no network access from the UI fixture
                        if (!popup.IsOpen) trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(70);
                        var monitorEntry = (Button)window.FindName("RoomOrchestrationEntryButton");
                        Assert.Equal(Visibility.Visible, monitorEntry.Visibility);
                        monitorEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(70);
                        Assert.True(vm.IsShowingRoomOrchestration);
                        Assert.True(monitorManager.Load().ShowRoomOrchestration);
                        Assert.Equal("任务编排", viewingLabel.Text);
                        Assert.Empty(vm.GetRoomOrchestrationPresentation().Cards);
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("RoomOrchestrationHomeContent")).Visibility);
                        var monitorRestart = new MainViewModel(); monitorRestart.PrimeStartupRoomView(monitorManager.Load());
                        Assert.True(monitorRestart.IsShowingRoomOrchestration);
                        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(70);
                        monitorEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(70);
                        Assert.False(vm.IsShowingRoomOrchestration);
                        Assert.False(monitorManager.Load().ShowRoomOrchestration);
                        Assert.Equal(activeRoom.Id, vm.ActiveRoomProfile!.Id);
                        testConfig.StandaloneMode = false;
                        // Enter the dedicated Home content in an executor fixture, without creating a BGI or network connection.
                        testConfig.ObserverMode = false;
                        testConfig.OnlineHoeingGroupNames = ["罐传精英", "精英收尾"];
                        testConfig.OnlineHoeingGroupTypes = ["group", "group"];
                        secondRoom.OnlineHoeingGroupNames = ["精英讨伐"];
                        secondRoom.OnlineHoeingGroupTypes = ["group"];
                        CompletionPolicyFields.ApplyForProfile(testConfig, activeRoom.Id, new TaskConflictPolicySettings
                        { Policy = TaskConflictPolicy.Stop, RoomSwitch = RoomSwitchCompletion.Choices(testConfig, activeRoom.Id).Single() });
                        // Add a multi-room route and an independent profile to exercise the actual
                        // vertical scroller, while keeping all execution/connection facts synthetic and isolated.
                        var third = new AssistantRoomProfile { Id = "room-third", Name = "地脉房", RoomCode = "C12345",
                            ServerUrl = activeRoom.ServerUrl, TeamUids = ["100000001"], OnlineHoeingGroupNames = ["地脉循环"], OnlineHoeingGroupTypes = ["group"] };
                        var fourth = new AssistantRoomProfile { Id = "room-fourth", Name = "素材采集房", RoomCode = "D09281",
                            ServerUrl = activeRoom.ServerUrl, TeamUids = ["100000001"], OnlineHoeingGroupNames = ["素材采集与日常准备"], OnlineHoeingGroupTypes = ["group"] };
                        var fifth = new AssistantRoomProfile { Id = "room-fifth", Name = "每日任务房", RoomCode = "E07126",
                            ServerUrl = activeRoom.ServerUrl, TeamUids = ["100000001"], OnlineHoeingGroupNames = ["日常准备"], OnlineHoeingGroupTypes = ["group"] };
                        testConfig.RoomProfiles.AddRange([third, fourth, fifth]);
                        foreach (var (sourceId, targetId) in new[] { (secondRoom.Id, third.Id), (third.Id, fourth.Id) })
                            CompletionPolicyFields.ApplyForProfile(testConfig, sourceId, new TaskConflictPolicySettings
                            { Policy = TaskConflictPolicy.Stop, RoomSwitch = RoomSwitchCompletion.Choices(testConfig, sourceId).Single(x => x.TargetProfileId == targetId) });
                        var displayState = (RoomOrchestrationStateStore)typeof(MainViewModel).GetField("_roomOrchestrationState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                        var key = vm.GetRoomOrchestrationKey(activeRoom)!;
                        displayState.Begin(key, "fixture-room-completed", 1, [new("罐传精英", "group"), new("精英收尾", "group")]);
                        const string fixtureJob = "6e97a488393f41cbbff3597b64288fad";
                        const string fixtureNote = "绑定完成动作尚未确认，保留原请求：计划表原运行尚未退出，保留同一完成动作";
                        displayState.SetItem(key, "fixture-room-completed", 1, 0, "completed", $"本机作业 {fixtureJob} 已确认成功");
                        displayState.SetItem(key, "fixture-room-completed", 1, 1, "completed", "");
                        displayState.SetPhase(key, "fixture-room-completed", 1, "succeeded", "最近一轮已确认完成", settled: true);
                        var secondKey = vm.GetRoomOrchestrationKey(secondRoom)!;
                        displayState.Begin(secondKey, "fixture-room-settling", 1, [new("精英讨伐", "group")]);
                        displayState.SetItem(secondKey, "fixture-room-settling", 1, 0, "completed", $"本机作业 {fixtureJob} 已确认成功");
                        displayState.SetPhase(secondKey, "fixture-room-settling", 1, "completion_wait", fixtureNote);
                        typeof(MainViewModel).GetMethod("NotifyRoomProfiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
                        // Establish a real isolated configuration store so the test verifies persistence, not only in-memory selection.
                        var manager = new AssistConfigManager();
                        Assert.True(manager.TrySave(testConfig));
                        typeof(MainViewModel).GetField("_configManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, manager);
                        var otherRoomEdit = vm.ReadRoomOrchestrationEdit(secondRoom.Id);
                        var activeBindingBefore = testConfig.OnlineHoeingGroupNames.ToArray();
                        // A waiting switch and ordinary read/command effects may live for a long
                        // time. They are not concurrent configuration writers and must not block
                        // this independent profile save, even through the remote save handler.
                        const BindingFlags roomFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var switchGate = (SemaphoreSlim)typeof(MainViewModel).GetField("_assistantRoomSwitchGate", roomFlags)!.GetValue(vm)!;
                        IDisposable HoldRoomEffect() => (IDisposable)typeof(MainViewModel).GetMethod("TryEnterAssistantRoomEffect", roomFlags)!.Invoke(vm, null)!;
                        Assert.True(switchGate.Wait(0));
                        try
                        {
                            using var concurrentRead = HoldRoomEffect();
                            vm.CommitRoomOrchestrationEdit(otherRoomEdit, new RoomOrchestrationEditChanges(ExpectedPlayers: 3, Schedule: "19:30"), null);
                            var activeEdit = vm.ReadRoomOrchestrationEdit(activeRoom.Id);
                            vm.CommitRoomOrchestrationEdit(activeEdit, new RoomOrchestrationEditChanges(ExpectedPlayers: 3), null);
                            Assert.Equal(3, testConfig.ExpectedHoeingPlayers);
                            Assert.Equal(3, manager.Load().ExpectedHoeingPlayers);
                            using var remoteRequest = HoldRoomEffect();
                            var read = await vm.ExecuteRoomOrchestrationRemoteOperationAsync(new RemoteCenterRequest
                            { Operation = "room.orchestration.edit", TargetInstanceId = testConfig.ClientInstanceId, PayloadJson = System.Text.Json.JsonSerializer.Serialize(third.Id) });
                            Assert.Equal("ok", read.Status);
                            var remoteEdit = System.Text.Json.JsonSerializer.Deserialize<RemoteOrchestrationEdit>(read.PayloadJson!)!;
                            var savedRemote = await vm.ExecuteRoomOrchestrationRemoteOperationAsync(new RemoteCenterRequest
                            {
                                Operation = "room.orchestration.save", TargetInstanceId = testConfig.ClientInstanceId, ExpectedRevision = remoteEdit.Revision,
                                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new RemoteOrchestrationSaveRequest(third.Id, new(ExpectedPlayers: 3), null))
                            });
                            Assert.True(savedRemote.Status == "ok", savedRemote.Message);
                            Assert.Equal(3, manager.Load().RoomProfiles.Single(p => p.Id == third.Id).ExpectedHoeingPlayers);
                            var stale = await vm.ExecuteRoomOrchestrationRemoteOperationAsync(new RemoteCenterRequest
                            {
                                Operation = "room.orchestration.save", TargetInstanceId = testConfig.ClientInstanceId, ExpectedRevision = remoteEdit.Revision,
                                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new RemoteOrchestrationSaveRequest(third.Id, new(ExpectedPlayers: 2), null))
                            });
                            Assert.Equal("rejected", stale.Status);
                        }
                        finally { switchGate.Release(); }
                        var latestEdit = vm.ReadRoomOrchestrationEdit(secondRoom.Id);
                        var beforeActualSwitch = manager.ReadRawJson();
                        Assert.True((bool)typeof(MainViewModel).GetMethod("TryBlockAssistantRoomEffects", roomFlags)!.Invoke(vm, null)!);
                        try
                        {
                            var blocked = Assert.Throws<InvalidOperationException>(() => vm.CommitRoomOrchestrationEdit(latestEdit,
                                new RoomOrchestrationEditChanges(Schedule: "20:00"), null));
                            Assert.Contains("实际离开或加入房间", blocked.Message);
                            Assert.Equal(beforeActualSwitch, manager.ReadRawJson());
                        }
                        finally { typeof(MainViewModel).GetField("_changingAssistantRoom", roomFlags)!.SetValue(vm, false); }
                        typeof(MainViewModel).GetField("_roomSwitchRecordUnreadable", roomFlags)!.SetValue(vm, true);
                        try
                        {
                            var blocked = Assert.Throws<InvalidOperationException>(() => vm.CommitRoomOrchestrationEdit(latestEdit,
                                new RoomOrchestrationEditChanges(Schedule: "20:00"), null));
                            Assert.Contains("切换结果尚未确认", blocked.Message);
                            Assert.Equal(beforeActualSwitch, manager.ReadRawJson());
                        }
                        finally { typeof(MainViewModel).GetField("_roomSwitchRecordUnreadable", roomFlags)!.SetValue(vm, false); }
                        Assert.Equal(activeBindingBefore, testConfig.OnlineHoeingGroupNames);
                        Assert.Equal(activeRoom.Id, testConfig.ActiveRoomProfileId);
                        Assert.Equal(3, secondRoom.ExpectedHoeingPlayers);
                        Assert.Equal("19:30", secondRoom.ScheduledOnlineTime);
                        Assert.Equal(third.Id, secondRoom.OnlineHoeingRoomSwitchCompletion?.TargetProfileId);
                        Assert.Equal(3, manager.Load().RoomProfiles.Single(p => p.Id == secondRoom.Id).ExpectedHoeingPlayers);
                        Assert.Throws<InvalidOperationException>(() => vm.CommitRoomOrchestrationEdit(otherRoomEdit,
                            new RoomOrchestrationEditChanges(ExpectedPlayers: 2), null));
                        typeof(MainViewModel).GetMethod("NotifyModeBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
                        var orchestrationEntry = (Button)window.FindName("RoomOrchestrationEntryButton");
                        var graphPage = (FrameworkElement)window.FindName("RoomOrchestrationHomeContent");
                        if (!popup.IsOpen) trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        orchestrationEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        Assert.True(vm.IsShowingRoomOrchestration);
                        Assert.True(manager.Load().ShowRoomOrchestration);
                        Assert.Empty(vm.RoomViewPreferenceMessage);
                        var restartedPreview = new MainViewModel();
                        restartedPreview.PrimeStartupRoomView(manager.Load());
                        Assert.True(restartedPreview.IsShowingRoomOrchestration);
                        Assert.Equal("任务编排", restartedPreview.HeaderViewingRoomName);
                        Assert.Equal("任务编排", viewingLabel.Text);
                        Assert.Equal(Visibility.Visible, graphPage.Visibility);
                        Assert.Equal(activeRoom.Id, vm.ActiveRoomProfile?.Id);
                        Assert.True(vm.IsViewingAllRooms);
                        foreach (var width in new[] { 1180d, 980d })
                        {
                            window.Width = width;
                            window.Height = width == 980 ? 720 : 860;
                            window.UpdateLayout();
                            var graph = (RoomOrchestrationPage)graphPage;
                            graph.RefreshPresentation();
                            await Task.Delay(80);
                            window.UpdateLayout();
                            var scroll = (ScrollViewer)graph.FindName("RoomGraphScrollViewer");
                            var presentationRoot = (Grid)graph.FindName("PresentationRoot");
                            Assert.Equal(2, presentationRoot.RowDefinitions.Count);
                            Assert.Equal(2, presentationRoot.Children.Count);
                            Assert.Equal(1, Grid.GetRow(scroll));
                            Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
                            Assert.True(scroll.ActualWidth > 0 && scroll.ActualHeight > 0);
                            Assert.Equal(5, vm.GetRoomOrchestrationPresentation().Cards.Length);
                            Assert.True(vm.GetRoomOrchestrationPresentation().Cards[0].CompletedCount == 2);
                            var headerLayout = (Grid)graph.FindName("HeaderLayout");
                            var header = Assert.IsType<RoomOrchestrationHeaderDisplay>(headerLayout.DataContext);
                            Assert.Equal("最后确认", header.LoginLabel);
                            Assert.Equal(activeRoom.Name, header.RoomName);
                            Assert.Equal(activeRoom.RoomCode, header.RoomCode);
                            Assert.Equal("unknown", header.Status.Kind);
                            Assert.Equal(0, Grid.GetRow((FrameworkElement)graph.FindName("HeaderStatusPane")));
                            Assert.True(headerLayout.ActualHeight <= 36, $"顶部状态信息行过高：{headerLayout.ActualHeight}");
                            Assert.Equal(Visibility.Collapsed, ((Grid)graph.FindName("MonitorTargetRow")).Visibility);
                            Assert.Contains(FindVisualChildren<Border>(graph), b => b.Name == "RoomSectionTag" && b.Visibility == Visibility.Visible
                                && b.DataContext is RoomOrchestrationCard c && c.Id == fifth.Id && c.SectionTitle == "独立房间");
                            var titles = FindVisualChildren<Grid>(graph).Where(g => g.Name == "CardTitleGrid").ToArray();
                            Assert.Equal(5, titles.Length);
                            foreach (var title in titles)
                            {
                                var progressPane = Assert.Single(FindVisualChildren<Border>(title).Where(b => b.Name == "CardProgressPane"));
                                var action = Assert.Single(FindVisualChildren<Button>(title).Where(b => b.Name == "CardCompletionButton"));
                                Assert.Equal(0, Grid.GetRow(progressPane)); Assert.Equal(0, Grid.GetRow(action));
                                Assert.True(title.ActualHeight <= 34, $"卡片标题不应增加一行：{title.ActualHeight}");
                                Assert.True(action.ActualWidth > 75, $"完成动作空间不足：{action.ActualWidth}");
                            }
                            var firstTitle = Assert.Single(titles.Where(t => t.DataContext is RoomOrchestrationCard c && c.Id == activeRoom.Id));
                            var taskBadges = FindVisualChildren<Border>(graph).Where(b => b.Name == "TaskStatusChrome").ToArray();
                            Assert.NotEmpty(taskBadges);
                            Assert.All(taskBadges, b => Assert.Equal(new Thickness(6, 1.5, 6, 1.5), b.Padding));
                            var titleBadge = Assert.Single(FindVisualChildren<Border>(firstTitle).Where(b => b.DataContext is RoomOrchestrationStatus s && s.Kind == "done" && b.Padding == new Thickness(6, 3, 6, 3)));
                            Assert.All(taskBadges.Where(b => b.DataContext is RoomOrchestrationStatus s && s.Kind == "done"), b =>
                            {
                                Assert.True(b.ActualHeight < titleBadge.ActualHeight, "任务状态框应比房间状态框略矮");
                                Assert.Contains(FindVisualChildren<TextBlock>(b), t => t.FontSize == 12 && t.Text.Contains("已完成"));
                            });
                            Assert.Contains(FindVisualChildren<TextBlock>(firstTitle), t => t.Name == "CardCompactProgressText" && t.Text == "2/2");
                            Assert.Contains(FindVisualChildren<ProgressBar>(firstTitle), p => p.Name == "CardCompactProgressBar" && p.Visibility == Visibility.Visible);
                            var time = Assert.Single(FindVisualChildren<TextBlock>(firstTitle).Where(t => t.Name == "CardUpdatedText"));
                            Assert.Equal(width == 1180 ? Visibility.Visible : Visibility.Collapsed, time.Visibility);
                            var fifthTitle = Assert.Single(titles.Where(t => t.DataContext is RoomOrchestrationCard c && c.Id == fifth.Id));
                            Assert.Contains(FindVisualChildren<TextBlock>(fifthTitle), t => t.Name == "CardCompactProgressText" && t.Text == "1 项绑定");
                            Assert.Contains(FindVisualChildren<ProgressBar>(fifthTitle), p => p.Name == "CardCompactProgressBar" && p.Visibility == Visibility.Collapsed);
                            var detailButtons = FindVisualChildren<Button>(graph).Where(b => b.Name == "RoomDetailButton").ToArray();
                            var secondDetails = Assert.Single(detailButtons.Where(b => b.DataContext is RoomOrchestrationCard c && c.Id == secondRoom.Id));
                            Assert.Equal(Visibility.Visible, secondDetails.Visibility);
                            Assert.Contains(FindVisualChildren<TextBlock>(secondDetails), t => t.Name == "RoomDetailSummary" && t.Text == "计划表尚未退出");
                            Assert.All(detailButtons.Where(b => b.DataContext is RoomOrchestrationCard c && c.Id == third.Id), b => Assert.Equal(Visibility.Collapsed, b.Visibility));
                            var jobLabels = FindVisualChildren<TextBlock>(graph).Where(t => t.Name == "CompactJobText"
                                && t.DataContext is RoomOrchestrationTaskRow row && row.Status.Detail.Contains(fixtureJob)).ToArray();
                            Assert.NotEmpty(jobLabels);
                            if (width == 1180) Assert.Contains(jobLabels, t => t.IsVisible && t.Text == "作业 6e97a488…");
                            else Assert.All(jobLabels, t => Assert.Equal(Visibility.Collapsed, t.Visibility));
                            Assert.All(FindVisualChildren<TextBlock>(graph).Where(t => t.Name == "TaskInlineDetail"
                                && t.DataContext is RoomOrchestrationTaskRow row && row.Status.Detail.Contains(fixtureJob)), t => Assert.Equal(Visibility.Collapsed, t.Visibility));
                            if (width == 1180)
                            {
                                Exception? detailFailure = null;
                                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                                {
                                    var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Title.StartsWith("房间执行详情"));
                                    try
                                    {
                                        Assert.NotNull(dialog);
                                        var raw = Assert.Single(FindVisualChildren<TextBox>(dialog!).Where(t => t.Name == "RoomDetailTextBox"));
                                        Assert.True(raw.IsReadOnly);
                                        Assert.Contains(fixtureNote, raw.Text);
                                        Assert.Contains(fixtureJob, raw.Text);
                                    }
                                    catch (Exception ex) { detailFailure = ex; }
                                    finally { dialog?.Close(); }
                                }));
                                secondDetails.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                if (detailFailure is not null) throw detailFailure;
                                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                                {
                                    var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "当前登录及执行详情");
                                    try
                                    {
                                        Assert.NotNull(dialog);
                                        var raw = Assert.Single(FindVisualChildren<TextBox>(dialog!).Where(t => t.Name == "RoomDetailTextBox"));
                                        Assert.True(raw.IsReadOnly);
                                        Assert.Contains("尚未确认连接", raw.Text);
                                        Assert.Contains(activeRoom.RoomCode, raw.Text);
                                    }
                                    catch (Exception ex) { detailFailure = ex; }
                                    finally { dialog?.Close(); }
                                }));
                                ((Button)graph.FindName("HeaderDetailButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                if (detailFailure is not null) throw detailFailure;
                                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                                {
                                    var dialog = app.Windows.OfType<Window>().FirstOrDefault(w => w.Title.StartsWith("房间执行详情"));
                                    try
                                    {
                                        Assert.NotNull(dialog);
                                        var raw = Assert.Single(FindVisualChildren<TextBox>(dialog!).Where(t => t.Name == "RoomDetailTextBox"));
                                        Assert.True(raw.IsReadOnly);
                                        Assert.Contains("分类：独立房间", raw.Text);
                                        Assert.Contains("完成后：恢复任务", raw.Text);
                                        Assert.Contains("任务进度：共 1 项绑定", raw.Text);
                                    }
                                    catch (Exception ex) { detailFailure = ex; }
                                    finally { dialog?.Close(); }
                                }));
                                Assert.Single(FindVisualChildren<Button>(fifthTitle).Where(b => b.Name == "CardCompletionButton"))
                                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                if (detailFailure is not null) throw detailFailure;
                            }
                            var visual = (UIElement)window.Content;
                            var image = new RenderTargetBitmap((int)Math.Ceiling(visual.RenderSize.Width), (int)Math.Ceiling(visual.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
                            image.Render(visual);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(image));
                            Directory.CreateDirectory(AssistantDataDirectory.Root);
                            using var output = File.Create(Path.Combine(AssistantDataDirectory.Root, $"room-orchestration-wpf-{width:0}.png"));
                            encoder.Save(output);
                            if (scroll.ScrollableHeight > 1)
                            {
                                scroll.ScrollToEnd();
                                window.UpdateLayout();
                                Assert.True(scroll.VerticalOffset > 0);
                                if (width == 980)
                                {
                                    var bottom = new RenderTargetBitmap((int)Math.Ceiling(visual.RenderSize.Width), (int)Math.Ceiling(visual.RenderSize.Height), 96, 96, PixelFormats.Pbgra32);
                                    bottom.Render(visual);
                                    var bottomEncoder = new PngBitmapEncoder(); bottomEncoder.Frames.Add(BitmapFrame.Create(bottom));
                                    using var bottomOutput = File.Create(Path.Combine(AssistantDataDirectory.Root, "room-orchestration-wpf-bottom-980.png"));
                                    bottomEncoder.Save(bottomOutput);
                                }
                                scroll.ScrollToHome();
                            }
                        }
                        window.Width = 1180;
                        trigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        selector.SelectedItem = secondRoom;
                        Assert.True(vm.IsShowingRoomOrchestration); // a refresh cannot leave the remembered graph
                        Assert.True(vm.IsViewingAllRooms);
                        var roomRow = (ListBoxItem)selector.ItemContainerGenerator.ContainerFromItem(secondRoom);
                        roomRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = roomRow });
                        await Task.Delay(80);
                        Assert.False(vm.IsShowingRoomOrchestration);
                        Assert.Equal(secondRoom.Id, vm.ViewedRoomProfileId);
                        Assert.Equal(activeRoom.Id, vm.ActiveRoomProfile?.Id);
                        var reloaded = manager.Load();
                        Assert.False(reloaded.ShowRoomOrchestration);
                        Assert.Equal(secondRoom.Id, reloaded.ViewedRoomProfileId);
                        Assert.Equal(activeRoom.Id, reloaded.ActiveRoomProfileId);
                        var returnedPreview = new MainViewModel();
                        returnedPreview.PrimeStartupRoomView(reloaded);
                        Assert.False(returnedPreview.IsShowingRoomOrchestration);
                        Assert.Equal(secondRoom.Id, returnedPreview.ViewedRoomProfileId);
                        popup.IsOpen = false;
                        done.SetResult();
                    }
                    catch (Exception ex) { done.SetException(ex); }
                    finally
                    {
                        window?.Close();
                        vm?.Shutdown();
                        app.Shutdown();
                    }
                });
                app.Run();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
                app?.Shutdown();
            }
        }) { IsBackground = true, Name = "multi-room-readonly-wpf-test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
