using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;
using Path = System.IO.Path;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>多房间只读查看、顶栏齿轮与查看标签、房间弹窗金白配色、成员卡房间标签的实际 WPF 布局验收。</summary>
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
        public bool IsConnected => true;
        public bool IsConnecting => false;
        public string? Error => null;
        public event Action? Changed;
        public IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot() => _players;
        public void Start() { }
        public void ForceReconnect() { }
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

                            Assert.Equal("⚙", settings.Content);
                            Assert.Equal("所有房间", viewingText.Text);
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

                        // 耕地机已切到 1234 只读视图，且未改变当前登录房间
                        Assert.Equal(activeRoom.Id, vm.ActiveRoomProfile?.Id);
                        Assert.Equal(secondRoom.Id, vm.ViewedRoomProfileId);
                        Assert.Equal("1234", viewingLabel.Text);
                        Assert.False(quickCommands.IsEnabled);
                        Assert.Equal(2, vm.DisplayedMembers.Count);
                        Assert.False(vm.DisplayedMembers[0].Online); // 我在 1234 是离线的
                        var singleRoomBadges = FindVisualChildren<Border>(cardsControl).Where(b => b.Name == "MemberRoomLabelBadge").ToList();
                        Assert.All(singleRoomBadges, b => Assert.Equal(Visibility.Collapsed, b.Visibility));

                        // 4. 点「所有房间」切回全房间视图
                        allRoomsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(80);
                        cardsControl.UpdateLayout();
                        Assert.True(vm.IsViewingAllRooms);
                        Assert.Equal("所有房间", viewingLabel.Text);
                        Assert.True(quickCommands.IsEnabled);
                        Assert.Equal(3, vm.DisplayedMembers.Count);

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
