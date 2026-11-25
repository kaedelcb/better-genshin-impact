using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, IRoomSpectatorSession> _roomSpectators = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ProfileId, string PlayerUid), MemberViewModel> _spectatorMemberCache = new();
    private readonly Dictionary<string, (string File, string Ring)> _spectatorAvatars = new(StringComparer.Ordinal);
    private int _spectatorAvatarCursor;

    /// <summary>测试注入用工厂：未设置时使用真实 SignalR 旁观连接。</summary>
    internal Func<AssistantRoomProfile, string, bool, IRoomSpectatorSession>? RoomSpectatorFactory { get; set; }

    /// <summary>耕地机主页面当前实际展示的成员集合（可能是登录房间、某个只读房间、或「所有房间」去重汇总）。</summary>
    public ObservableCollection<MemberViewModel> DisplayedMembers { get; } = new();

    /// <summary>当前查看的房间视图 ID：AssistConfig.AllRoomsViewId 表示「所有房间」，否则为具体房间档案 ID。</summary>
    public string ViewedRoomProfileId
    {
        get
        {
            var raw = _config?.ViewedRoomProfileId;
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, AssistConfig.AllRoomsViewId, StringComparison.Ordinal))
                return AssistConfig.AllRoomsViewId;
            if (_config?.RoomProfiles.Any(p => string.Equals(p.Id, raw, StringComparison.Ordinal)) == true)
                return raw;
            return AssistConfig.AllRoomsViewId;
        }
    }

    public bool IsViewingAllRooms => string.Equals(ViewedRoomProfileId, AssistConfig.AllRoomsViewId, StringComparison.Ordinal);

    public AssistantRoomProfile? ViewedRoomProfile => IsViewingAllRooms
        ? null
        : _config?.RoomProfiles.FirstOrDefault(p => string.Equals(p.Id, ViewedRoomProfileId, StringComparison.Ordinal));

    public bool IsViewingActiveRoom => !IsViewingAllRooms
        && ViewedRoomProfile is not null
        && ActiveRoomProfile is not null
        && string.Equals(ViewedRoomProfile.Id, ActiveRoomProfile.Id, StringComparison.Ordinal);

    public bool IsViewingReadOnlyRoom => !IsViewingAllRooms
        && ViewedRoomProfile is not null
        && !IsViewingActiveRoom;

    /// <summary>底部快捷指令是否允许对当前视图执行（查看其他只读房间时禁用，防止误操作到登录房间）。</summary>
    public bool CanOperateDisplayedRoom => !IsViewingReadOnlyRoom;

    public string HeaderViewingRoomName => IsViewingAllRooms
        ? "所有房间"
        : TruncateRoomLabel(ViewedRoomProfile?.Name, 4, "未选择");

    public string HeaderViewingRoomFullName => IsViewingAllRooms
        ? "所有房间"
        : string.IsNullOrWhiteSpace(ViewedRoomProfile?.Name) ? "未选择" : ViewedRoomProfile!.Name;

    public string HeaderViewingRoomToolTip => IsViewingAllRooms
        ? "当前耕地机页面正在查看：所有房间（其他房间成员为只读查看）"
        : IsViewingActiveRoom
            ? $"当前耕地机页面正在查看：{HeaderViewingRoomFullName}（当前登录房间）"
            : $"当前耕地机页面正在查看：{HeaderViewingRoomFullName}（只读查看，切换登录后可操作）";

    public string AllRoomsCountText => _config?.RoomProfiles is { Count: > 0 } rooms
        ? $"{rooms.Count} 个房间"
        : "暂无房间";

    public string RoomViewNoticeText
    {
        get
        {
            if (_config?.RoomProfiles is not { Count: > 0 }) return "";
            if (IsViewingReadOnlyRoom && ViewedRoomProfile is { } target)
            {
                if (IsStandaloneMode) return $"当前为单机模式，无法实时查看「{target.Name}」的在线状态。";
                if (_roomSpectators.TryGetValue(target.Id, out var session))
                {
                    var hasPlayers = session.GetPlayersSnapshot().Count > 0;
                    if (session.IsConnecting && !hasPlayers) return $"正在连接「{target.Name}」只读视图…";
                    if (!string.IsNullOrWhiteSpace(session.Error) && !hasPlayers)
                        return $"「{target.Name}」只读连接暂未就绪：{session.Error}";
                }
                return "";
            }
            if (IsViewingAllRooms && _config.RoomProfiles.Count > 1)
            {
                if (IsStandaloneMode) return "当前为单机模式，无法实时查看其他房间状态。";
                var failed = new List<string>();
                foreach (var profile in _config.RoomProfiles)
                {
                    if (string.Equals(profile.Id, ActiveRoomProfile?.Id, StringComparison.Ordinal)) continue;
                    if (_roomSpectators.TryGetValue(profile.Id, out var session)
                        && !string.IsNullOrWhiteSpace(session.Error)
                        && !session.IsConnecting
                        && session.GetPlayersSnapshot().Count == 0)
                    {
                        failed.Add($"{profile.Name}（{session.Error}）");
                    }
                }
                if (failed.Count > 0) return "部分房间只读连接未就绪：" + string.Join("；", failed);
            }
            return "";
        }
    }

    public RelayCommand SelectAllRoomsViewCommand => new(_ => SelectViewedRoom(AssistConfig.AllRoomsViewId));
    public RelayCommand SelectViewedRoomCommand => new(value =>
    {
        if (value is AssistantRoomProfile profile) SelectViewedRoom(profile.Id);
        else if (value is string id) SelectViewedRoom(id);
    });

    internal static string TruncateRoomLabel(string? name, int maxElements, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var characters = StringInfo.ParseCombiningCharacters(name);
        return characters.Length <= maxElements ? name : name[..characters[maxElements]];
    }

    /// <summary>切换耕地机当前查看的房间视图（不改变当前登录房间），并持久化记忆。</summary>
    internal void SelectViewedRoom(string? targetId, bool persist = true)
    {
        var normalized = string.IsNullOrWhiteSpace(targetId)
            || string.Equals(targetId, AssistConfig.AllRoomsViewId, StringComparison.Ordinal)
            || _config?.RoomProfiles.Any(p => string.Equals(p.Id, targetId, StringComparison.Ordinal)) != true
            ? AssistConfig.AllRoomsViewId
            : targetId;

        if (_config is not null && !string.Equals(_config.ViewedRoomProfileId, normalized, StringComparison.Ordinal))
        {
            _config.ViewedRoomProfileId = normalized;
            if (persist && _configManager is not null && !_configTransferInProgress)
            {
                _configManager.TrySave(_config);
            }
        }

        NotifyRoomViewProperties();
        SyncRoomViewSessions();
        RebuildDisplayedMembers();
    }

    private void NotifyRoomViewProperties()
    {
        OnPropertyChanged(nameof(ViewedRoomProfileId));
        OnPropertyChanged(nameof(IsViewingAllRooms));
        OnPropertyChanged(nameof(ViewedRoomProfile));
        OnPropertyChanged(nameof(IsViewingActiveRoom));
        OnPropertyChanged(nameof(IsViewingReadOnlyRoom));
        OnPropertyChanged(nameof(CanOperateDisplayedRoom));
        OnPropertyChanged(nameof(HeaderViewingRoomName));
        OnPropertyChanged(nameof(HeaderViewingRoomFullName));
        OnPropertyChanged(nameof(HeaderViewingRoomToolTip));
        OnPropertyChanged(nameof(AllRoomsCountText));
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    /// <summary>按当前视图模式与房间档案列表协调只读旁观连接：仅连接需要查看的非登录房间。</summary>
    internal void SyncRoomViewSessions()
    {
        var desired = new Dictionary<string, (AssistantRoomProfile Profile, string ServerUrl)>(StringComparer.Ordinal);
        if (!_disposing && !IsStandaloneMode && _config is { RoomProfiles.Count: > 0 } config
            && (_configManager is not null || RoomSpectatorFactory is not null))
        {
            var activeId = config.ActiveRoomProfileId;
            if (IsViewingAllRooms)
            {
                foreach (var profile in config.RoomProfiles)
                {
                    if (profile is null || string.Equals(profile.Id, activeId, StringComparison.Ordinal)) continue;
                    var serverUrl = config.GetRoomServerUrl(profile);
                    if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(profile.RoomCode)
                        && !string.IsNullOrEmpty(profile.Password))
                    {
                        desired[profile.Id] = (profile, serverUrl);
                    }
                }
            }
            else if (ViewedRoomProfile is { } viewed && !string.Equals(viewed.Id, activeId, StringComparison.Ordinal))
            {
                var serverUrl = config.GetRoomServerUrl(viewed);
                if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(viewed.RoomCode)
                    && !string.IsNullOrEmpty(viewed.Password))
                {
                    desired[viewed.Id] = (viewed, serverUrl);
                }
            }
        }

        var bypassProxy = _config?.BypassSystemProxy == true;
        var toDispose = new List<IRoomSpectatorSession>();
        foreach (var existingId in _roomSpectators.Keys.ToList())
        {
            var session = _roomSpectators[existingId];
            if (!desired.TryGetValue(existingId, out var want)
                || !RoomServerAddress.Same(session.ServerUrl, want.ServerUrl)
                || !string.Equals(session.RoomCode, want.Profile.RoomCode, StringComparison.Ordinal)
                || !string.Equals(session.Password, want.Profile.Password, StringComparison.Ordinal)
                || session.BypassSystemProxy != bypassProxy)
            {
                _roomSpectators.Remove(existingId);
                toDispose.Add(session);
                RemoveSpectatorMemberCacheForProfile(existingId);
            }
        }

        foreach (var (profileId, entry) in desired)
        {
            if (_roomSpectators.ContainsKey(profileId)) continue;
            var session = RoomSpectatorFactory?.Invoke(entry.Profile, entry.ServerUrl, bypassProxy)
                ?? new RoomSpectatorSession(profileId, entry.ServerUrl, entry.Profile.RoomCode, entry.Profile.Password, bypassProxy);
            session.Changed += () => OnSpectatorSessionChanged(profileId, session);
            _roomSpectators[profileId] = session;
            session.Start();
        }

        foreach (var stale in toDispose)
        {
            _ = DisposeSpectatorSessionAsync(stale);
        }

        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    internal void RefreshRoomSpectators()
    {
        if (_disposing || IsStandaloneMode) return;
        SyncRoomViewSessions();
        foreach (var session in _roomSpectators.Values.ToList())
        {
            session.ForceReconnect();
        }
    }

    internal void StopAllRoomSpectators()
    {
        var sessions = _roomSpectators.Values.ToList();
        _roomSpectators.Clear();
        _spectatorMemberCache.Clear();
        foreach (var session in sessions)
        {
            _ = DisposeSpectatorSessionAsync(session);
        }
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    private static async Task DisposeSpectatorSessionAsync(IRoomSpectatorSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch { /* 旁观连接释放异常不影响主程序 */ }
    }

    private void RemoveSpectatorMemberCacheForProfile(string profileId)
    {
        foreach (var key in _spectatorMemberCache.Keys.Where(k => string.Equals(k.ProfileId, profileId, StringComparison.Ordinal)).ToList())
        {
            _spectatorMemberCache.Remove(key);
        }
    }

    private void OnSpectatorSessionChanged(string profileId, IRoomSpectatorSession session)
    {
        if (_disposing) return;
        void Apply()
        {
            if (_disposing) return;
            if (!_roomSpectators.TryGetValue(profileId, out var current) || !ReferenceEquals(current, session)) return;
            RebuildDisplayedMembers();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Apply);
    }

    /// <summary>按当前视图模式重建耕地机展示列表（保持 Members 始终只存登录房间成员，不污染联机协调逻辑）。</summary>
    internal void RebuildDisplayedMembers()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RebuildDisplayedMembers);
            return;
        }

        var profiles = _config?.RoomProfiles;
        var activeProfile = ActiveRoomProfile;
        var activeId = activeProfile?.Id ?? "";
        var activeName = activeProfile?.Name ?? "";
        List<MemberViewModel> desired;

        if (profiles is not { Count: > 0 } || IsViewingActiveRoom)
        {
            foreach (var member in Members)
            {
                member.SourceRoomProfileId = activeId;
                member.RoomLabel = activeName;
                member.IsActiveRoomMember = true;
                member.IsReadOnlyView = false;
                member.ShowRoomLabel = false;
            }
            desired = [.. Members];
        }
        else if (IsViewingReadOnlyRoom && ViewedRoomProfile is { } target)
        {
            desired = BuildSpectatorMembersForProfile(target, showRoomLabel: false);
        }
        else
        {
            // 「所有房间」：所有卡片都显示房间标签；按 UID 去重（在线优先，同态时登录房间优先，其次档案顺序）。
            var orderedProfiles = new List<AssistantRoomProfile>(profiles.Count);
            if (activeProfile is not null) orderedProfiles.Add(activeProfile);
            foreach (var profile in profiles)
            {
                if (profile is null || ReferenceEquals(profile, activeProfile)) continue;
                orderedProfiles.Add(profile);
            }

            var uidOrder = new List<string>();
            var winnerByUid = new Dictionary<string, MemberViewModel>(StringComparer.Ordinal);

            foreach (var profile in orderedProfiles)
            {
                IEnumerable<MemberViewModel> roomMembers;
                if (string.Equals(profile.Id, activeId, StringComparison.Ordinal))
                {
                    foreach (var member in Members)
                    {
                        member.SourceRoomProfileId = profile.Id;
                        member.RoomLabel = profile.Name;
                        member.IsActiveRoomMember = true;
                        member.IsReadOnlyView = false;
                        member.ShowRoomLabel = true;
                    }
                    roomMembers = Members;
                }
                else
                {
                    roomMembers = BuildSpectatorMembersForProfile(profile, showRoomLabel: true);
                }

                foreach (var candidate in roomMembers)
                {
                    if (string.IsNullOrWhiteSpace(candidate.PlayerUid)) continue;
                    if (!winnerByUid.TryGetValue(candidate.PlayerUid, out var existing))
                    {
                        uidOrder.Add(candidate.PlayerUid);
                        winnerByUid[candidate.PlayerUid] = candidate;
                    }
                    else if (!existing.Online && candidate.Online)
                    {
                        winnerByUid[candidate.PlayerUid] = candidate;
                    }
                }
            }

            desired = new List<MemberViewModel>(uidOrder.Count);
            foreach (var uid in uidOrder)
            {
                desired.Add(winnerByUid[uid]);
            }
        }

        ReconcileDisplayedMembers(desired);
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    private List<MemberViewModel> BuildSpectatorMembersForProfile(AssistantRoomProfile profile, bool showRoomLabel)
    {
        if (!_roomSpectators.TryGetValue(profile.Id, out var session)) return [];
        var snapshot = session.GetPlayersSnapshot();
        var result = new List<MemberViewModel>(snapshot.Count);
        var aliveUids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var np in snapshot)
        {
            if (string.IsNullOrWhiteSpace(np.PlayerUid) || np.PlayerUid.StartsWith("web_", StringComparison.Ordinal)) continue;
            aliveUids.Add(np.PlayerUid);
            var key = (profile.Id, np.PlayerUid);
            if (!_spectatorMemberCache.TryGetValue(key, out var vm))
            {
                var (file, ring) = ResolveSpectatorAvatar(np.PlayerUid);
                vm = new MemberViewModel
                {
                    PlayerUid = np.PlayerUid,
                    AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png",
                    AvatarRing = ring,
                    IsSelected = false,
                    IsReadOnlyView = true,
                    IsActiveRoomMember = false
                };
                _spectatorMemberCache[key] = vm;
            }
            ApplyControlRoomPlayerToMember(vm, np, isReadOnly: true, profile.Id, profile.Name, showRoomLabel);
            result.Add(vm);
        }

        foreach (var staleKey in _spectatorMemberCache.Keys
            .Where(k => string.Equals(k.ProfileId, profile.Id, StringComparison.Ordinal) && !aliveUids.Contains(k.PlayerUid))
            .ToList())
        {
            _spectatorMemberCache.Remove(staleKey);
        }

        return result;
    }

    private (string File, string Ring) ResolveSpectatorAvatar(string playerUid)
    {
        var active = Members.FirstOrDefault(m => string.Equals(m.PlayerUid, playerUid, StringComparison.Ordinal));
        if (active is not null && !string.IsNullOrEmpty(active.AvatarPath))
        {
            var matched = AvatarPool.FirstOrDefault(a => active.AvatarPath.EndsWith($"/{a.file}.png", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(matched.file)) return matched;
        }
        if (!_spectatorAvatars.TryGetValue(playerUid, out var assigned))
        {
            assigned = AvatarPool[_spectatorAvatarCursor++ % AvatarPool.Length];
            _spectatorAvatars[playerUid] = assigned;
        }
        return assigned;
    }

    private void ApplyControlRoomPlayerToMember(MemberViewModel target, ControlRoomPlayer np,
        bool isReadOnly, string sourceRoomProfileId, string roomLabel, bool showRoomLabel)
    {
        target.PlayerName = np.PlayerName;
        target.ClientInstanceId = np.ClientInstanceId;
        target.MachineId = np.MachineId;
        target.Online = np.Online;
        target.IsSelf = np.PlayerUid == _config?.PlayerUid;
        target.IsObserverMode = IsObserverMode;
        target.BgiStatus = np.BgiStatus;
        target.BgiVersion = np.BgiVersion;
        target.ConfigGroups = np.ConfigGroups;
        target.OneClickConfigs = np.OneClickConfigs;
        target.AutoHoeingRunning = np.AutoHoeingRunning;
        target.AutoHoeingProgress = np.AutoHoeingProgress;
        target.ScriptTaskProgress = np.ScriptTaskProgress;
        target.FriendshipProgress = np.FriendshipProgress;
        target.TaskRunning = np.TaskRunning;
        target.TaskStatusAvailable = np.TaskStatusAvailable;
        target.TaskPaused = np.TaskPaused;
        target.ApplyHoeingStatus(np.HoeingBatchId, np.HoeingStartPhase,
            np.HoeingStartMessage, np.HoeingCompletionWaitReleased);
        target.HoeingCompletionAction = np.HoeingCompletionAction;
        target.CurrentTaskName = np.CurrentTaskName;
        target.CurrentTaskGroupName = np.CurrentTaskGroupName;
        target.CurrentRouteDisplay = np.CurrentRouteDisplay;
        target.CurrentScriptRouteName = np.CurrentScriptRouteName;
        target.Hotkeys = np.Hotkeys;
        target.ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus;
        target.OneClickTasksWithStatus = np.OneClickTasksWithStatus;
        target.OnlineReady = np.OnlineReady;
        target.OnlineMode = np.OnlineMode;
        target.ScheduledOnlineTime = np.ScheduledOnlineTime;
        target.ExpectedHoeingPlayers = np.ExpectedHoeingPlayers;
        target.OnlineHoeingGroupNames = np.OnlineHoeingGroupNames ?? [];
        target.OnlineHoeingGroupTypes = np.OnlineHoeingGroupTypes ?? [];
        target.QuickCommands = np.QuickCommands ?? new();
        target.OnlineHistory = np.OnlineHistory;
        target.SourceRoomProfileId = sourceRoomProfileId;
        target.RoomLabel = roomLabel;
        target.IsActiveRoomMember = !isReadOnly;
        target.IsReadOnlyView = isReadOnly;
        target.ShowRoomLabel = showRoomLabel;
        if (isReadOnly) target.IsSelected = false;
    }

    private void ReconcileDisplayedMembers(List<MemberViewModel> desired)
    {
        var desiredSet = new HashSet<MemberViewModel>(desired, ReferenceEqualityComparer.Instance);
        for (int i = DisplayedMembers.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(DisplayedMembers[i]))
            {
                DisplayedMembers.RemoveAt(i);
            }
        }

        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var item = desired[targetIndex];
            if (targetIndex < DisplayedMembers.Count && ReferenceEquals(DisplayedMembers[targetIndex], item))
                continue;
            var currentIndex = DisplayedMembers.IndexOf(item);
            if (currentIndex >= 0)
            {
                DisplayedMembers.Move(currentIndex, targetIndex);
            }
            else
            {
                DisplayedMembers.Insert(targetIndex, item);
            }
        }
    }
}
