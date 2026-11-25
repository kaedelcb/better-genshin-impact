using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class MultiRoomReadOnlyViewTests
{
    private sealed class FakeSpectatorSession : IRoomSpectatorSession
    {
        private List<ControlRoomPlayer> _players = [];

        public FakeSpectatorSession(AssistantRoomProfile profile, string serverUrl, bool bypassSystemProxy)
        {
            ProfileId = profile.Id;
            ServerUrl = serverUrl;
            RoomCode = profile.RoomCode;
            Password = profile.Password;
            BypassSystemProxy = bypassSystemProxy;
        }

        public string ProfileId { get; }
        public string ServerUrl { get; }
        public string RoomCode { get; }
        public string Password { get; }
        public bool BypassSystemProxy { get; }
        public bool IsConnected { get; set; } = true;
        public bool IsConnecting { get; set; }
        public string? Error { get; set; }
        public int StartCount { get; private set; }
        public int ReconnectCount { get; private set; }
        public bool Disposed { get; private set; }

        public event Action? Changed;

        public IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot() => _players;

        public void Start() => StartCount++;

        public void ForceReconnect() => ReconnectCount++;

        public void SetPlayers(params ControlRoomPlayer[] players)
        {
            _players = [.. players];
            Changed?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static (MainViewModel Vm, AssistantRoomProfile ActiveRoom, AssistantRoomProfile SecondRoom, Dictionary<string, FakeSpectatorSession> Sessions)
        CreateTwoRoomFixture(bool standalone = false, string? initialViewedId = null)
    {
        var active = new AssistantRoomProfile
        {
            Id = "room-active",
            Name = "罐传精队",
            RoomCode = "B54387",
            ServerUrl = "https://coord.example.test",
            Password = "pw-a",
            TeamUids = ["100000001", "100000002", "100000003"]
        };
        var second = new AssistantRoomProfile
        {
            Id = "room-1234",
            Name = "1234",
            RoomCode = "24D888",
            ServerUrl = "https://coord.example.test",
            Password = "pw-b",
            TeamUids = ["100000001", "100000002", "100000003", "100000004"]
        };
        var config = new AssistConfig
        {
            PlayerUid = "100000001",
            PlayerName = "茶包",
            ServerUrl = active.ServerUrl,
            ControlRoomPassword = active.Password,
            TeamUids = [.. active.TeamUids],
            StandaloneMode = standalone,
            ActiveRoomProfileId = active.Id,
            LocalRoomProfileId = active.Id,
            ViewedRoomProfileId = initialViewedId ?? AssistConfig.AllRoomsViewId,
            RoomProfiles = [active, second]
        };

        var vm = new MainViewModel();
        var sessions = new Dictionary<string, FakeSpectatorSession>(StringComparer.Ordinal);
        vm.RoomSpectatorFactory = (profile, serverUrl, bypass) =>
        {
            var fake = new FakeSpectatorSession(profile, serverUrl, bypass);
            sessions[profile.Id] = fake;
            return fake;
        };
        typeof(MainViewModel).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, config);
        vm.RoomCode = active.RoomCode;

        vm.Members.Add(new MemberViewModel
        {
            PlayerUid = "100000001",
            PlayerName = "茶包",
            Online = true,
            IsSelf = true,
            IsSelected = true,
            BgiStatus = "running"
        });
        vm.Members.Add(new MemberViewModel
        {
            PlayerUid = "100000002",
            PlayerName = "老鲨鱼",
            Online = false,
            IsSelected = false,
            BgiStatus = "offline"
        });
        vm.Members.Add(new MemberViewModel
        {
            PlayerUid = "100000003",
            PlayerName = "渺茫",
            Online = false,
            IsSelected = false,
            BgiStatus = "offline"
        });

        typeof(MainViewModel).GetMethod("NotifyRoomProfiles", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        return (vm, active, second, sessions);
    }

    [Fact]
    public void ViewedRoomProfileIdDefaultsToAllRoomsAndPersistsAcrossJsonRoundTrip()
    {
        var fresh = new AssistConfig();
        Assert.Equal(AssistConfig.AllRoomsViewId, fresh.ViewedRoomProfileId);

        fresh.ViewedRoomProfileId = "room-1234";
        var json = JsonSerializer.Serialize(fresh);
        var restored = JsonSerializer.Deserialize<AssistConfig>(json)!;
        Assert.Equal("room-1234", restored.ViewedRoomProfileId);

        // 指向已不存在的档案时自动回退到「所有房间」
        var (vm, _, _, _) = CreateTwoRoomFixture(initialViewedId: "deleted-room-id");
        Assert.True(vm.IsViewingAllRooms);
        Assert.Equal(AssistConfig.AllRoomsViewId, vm.ViewedRoomProfileId);
        Assert.Equal("所有房间", vm.HeaderViewingRoomName);
    }

    [Fact]
    public void AllRoomsViewDeduplicatesByUidPrefersOnlineShowsSingleOfflineAndLabelsEveryCard()
    {
        var (vm, active, second, sessions) = CreateTwoRoomFixture();
        Assert.True(vm.IsViewingAllRooms);
        Assert.True(vm.CanOperateDisplayedRoom);
        Assert.Equal("所有房间", vm.HeaderViewingRoomName);
        Assert.Single(sessions);
        var secondSession = Assert.Contains(second.Id, sessions);
        Assert.Equal(1, secondSession.StartCount);

        secondSession.SetPlayers(
            new ControlRoomPlayer { PlayerUid = "100000001", PlayerName = "茶包", Online = false, BgiStatus = "offline" },
            new ControlRoomPlayer { PlayerUid = "100000002", PlayerName = "老鲨鱼", Online = true, BgiStatus = "running" },
            new ControlRoomPlayer { PlayerUid = "100000003", PlayerName = "渺茫", Online = false, BgiStatus = "offline" },
            new ControlRoomPlayer { PlayerUid = "100000004", PlayerName = "兴起", Online = true, BgiStatus = "running" },
            new ControlRoomPlayer { PlayerUid = "web_logviewer_abc", PlayerName = "旁观", Online = true });

        // 去重后应只有 4 张卡，且 web_ 旁观连接被过滤
        Assert.Equal(4, vm.DisplayedMembers.Count);
        Assert.Equal(["100000001", "100000002", "100000003", "100000004"],
            vm.DisplayedMembers.Select(m => m.PlayerUid).ToArray());

        // 自机：登录房间在线优先
        var selfCard = vm.DisplayedMembers[0];
        Assert.True(selfCard.Online);
        Assert.False(selfCard.IsReadOnlyView);
        Assert.True(selfCard.CanOperateCard);
        Assert.True(selfCard.CanUpdateLocalBgi);
        Assert.True(selfCard.ShowRoomLabel);
        Assert.Equal(active.Name, selfCard.RoomLabel);

        // 老鲨鱼：登录房间离线、1234 在线 -> 展示 1234 在线只读卡片
        var sharkCard = vm.DisplayedMembers[1];
        Assert.True(sharkCard.Online);
        Assert.True(sharkCard.IsReadOnlyView);
        Assert.False(sharkCard.CanOperateCard);
        Assert.False(sharkCard.CanRemoteEdit);
        Assert.False(sharkCard.IsSelected);
        Assert.True(sharkCard.ShowRoomLabel);
        Assert.Equal(second.Name, sharkCard.RoomLabel);

        // 渺茫：两房均离线 -> 仅显示一张（登录房间优先），仍带房间标签
        var miaomangCard = vm.DisplayedMembers[2];
        Assert.False(miaomangCard.Online);
        Assert.False(miaomangCard.IsReadOnlyView);
        Assert.True(miaomangCard.ShowRoomLabel);
        Assert.Equal(active.Name, miaomangCard.RoomLabel);

        // 兴起：仅在 1234 -> 展示 1234 只读卡片与房间标签
        var xingqiCard = vm.DisplayedMembers[3];
        Assert.True(xingqiCard.Online);
        Assert.True(xingqiCard.IsReadOnlyView);
        Assert.True(xingqiCard.ShowRoomLabel);
        Assert.Equal(second.Name, xingqiCard.RoomLabel);

        // 登录房间 Members 绝不被旁观成员污染
        Assert.Equal(3, vm.Members.Count);
    }

    [Fact]
    public async Task ViewingNonActiveRoomShowsReadOnlyRoomMembersWithoutChangingActiveLogin()
    {
        var (vm, active, second, sessions) = CreateTwoRoomFixture();
        var secondSession = sessions[second.Id];
        secondSession.SetPlayers(
            new ControlRoomPlayer { PlayerUid = "100000001", PlayerName = "茶包", Online = false, BgiStatus = "offline" },
            new ControlRoomPlayer { PlayerUid = "100000004", PlayerName = "兴起", Online = true, BgiStatus = "running" });

        vm.SelectViewedRoom(second.Id, persist: false);

        Assert.Equal(active.Id, vm.ActiveRoomProfile?.Id);
        Assert.Equal(second.Id, vm.ViewedRoomProfileId);
        Assert.True(vm.IsViewingReadOnlyRoom);
        Assert.False(vm.IsViewingAllRooms);
        Assert.False(vm.CanOperateDisplayedRoom);
        Assert.Equal("1234", vm.HeaderViewingRoomName);

        Assert.Equal(2, vm.DisplayedMembers.Count);
        var selfInSecond = vm.DisplayedMembers[0];
        Assert.Equal("100000001", selfInSecond.PlayerUid);
        Assert.False(selfInSecond.Online); // 我在 1234 是离线的
        Assert.True(selfInSecond.IsReadOnlyView);
        Assert.False(selfInSecond.CanOperateCard);
        Assert.False(selfInSecond.CanUpdateLocalBgi);
        Assert.False(selfInSecond.ShowRoomLabel); // 单房间视图不重复显示房间标签

        // 只读卡片点击配置组/一条龙直接被安全拦截
        await vm.StartGroupFromConfigAsync(selfInSecond, "测试配置组");
        await vm.StartOneClickFromConfigAsync(selfInSecond, "测试一条龙");

        // 切回当前登录房间单房视图：释放非必要的旁观会话，且卡片不显示房间标签
        vm.SelectViewedRoom(active.Id, persist: false);
        Assert.True(vm.IsViewingActiveRoom);
        Assert.True(vm.CanOperateDisplayedRoom);
        Assert.Equal("罐传精队", vm.HeaderViewingRoomName);
        Assert.True(secondSession.Disposed);
        Assert.Equal(3, vm.DisplayedMembers.Count);
        Assert.All(vm.DisplayedMembers, m =>
        {
            Assert.False(m.IsReadOnlyView);
            Assert.True(m.CanOperateCard);
            Assert.False(m.ShowRoomLabel);
        });
    }

    [Fact]
    public void StandaloneModeDoesNotOpenSpectatorSessionsAndShowsNotice()
    {
        var (vm, _, second, sessions) = CreateTwoRoomFixture(standalone: true);
        Assert.Empty(sessions);
        Assert.Contains("单机模式", vm.RoomViewNoticeText);

        vm.SelectViewedRoom(second.Id, persist: false);
        Assert.Empty(sessions);
        Assert.Contains("单机模式", vm.RoomViewNoticeText);
        Assert.Empty(vm.DisplayedMembers);
    }

    [Fact]
    public void RoomSwitchChoicesMarksOnlyActiveRoomAsIsActiveRoom()
    {
        var (vm, active, second, _) = CreateTwoRoomFixture();
        Assert.Equal(2, vm.RoomSwitchChoices.Count);
        Assert.True(active.IsActiveRoom);
        Assert.False(second.IsActiveRoom);
    }
}
