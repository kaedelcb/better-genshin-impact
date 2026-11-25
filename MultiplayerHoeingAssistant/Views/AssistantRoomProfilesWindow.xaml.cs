using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

internal sealed record AssistantRoomDraft(string Name, string Note, string ServerUrl, string RoomCode, string Password,
    List<string> MemberUids, int ExpectedPlayers)
{
    internal static List<string> ParseMemberUids(string text) => text
        .Split([',', '，', ' ', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal).ToList();

    internal static bool HasValidMemberUids(List<string> uids, string selfUid) =>
        uids.Count is >= 1 and <= 64 && uids.Contains(selfUid, StringComparer.Ordinal)
        && uids.All(uid => uid.Length is >= 1 and <= 16 && uid.All(char.IsAsciiDigit));
}

public partial class AssistantRoomProfilesWindow : Window
{
    private readonly string _activeId;
    private readonly Func<string> _localId;
    private readonly string _defaultServer;
    private readonly string _selfUid;
    private readonly Func<AssistantRoomProfile?, AssistantRoomDraft, bool, Task<AssistantRoomProfile>> _save;
    private readonly Action<AssistantRoomProfile> _remove;
    private readonly List<AssistantRoomProfile> _profiles;
    private string _calculatedRoomCode = "";
    private bool _filling;
    private bool _saving;

    internal AssistantRoomProfilesWindow(IReadOnlyList<AssistantRoomProfile> profiles, string activeId, Func<string> localId, string server, string selfUid,
        Func<AssistantRoomProfile?, AssistantRoomDraft, bool, Task<AssistantRoomProfile>> save, Action<AssistantRoomProfile> remove)
    {
        _activeId = activeId;
        _localId = localId;
        _defaultServer = server;
        _selfUid = selfUid;
        _save = save;
        _remove = remove;
        _profiles = profiles.ToList();
        UpdateLocalMarkers();
        DpiAwarenessController.Initialize(this);
        InitializeComponent();
        ExpectedCountBox.ItemsSource = new[] { 1, 2, 3, 4 };
        MemberUidsBox.TextChanged += (_, _) =>
        {
            if (!_filling) UpdateLegacyRoomCode(AssistantRoomDraft.ParseMemberUids(MemberUidsBox.Text));
        };
        ProfilesList.ItemsSource = _profiles.ToList();
        ProfilesList.SelectedItem = _profiles.FirstOrDefault(p => p.Id == activeId) ?? _profiles.FirstOrDefault();
        Fill(ProfilesList.SelectedItem as AssistantRoomProfile);
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == ProfilesList) Fill(ProfilesList.SelectedItem as AssistantRoomProfile);
    }

    private void NewProfileClick(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        ProfilesList.SelectedIndex = -1;
        Fill(null);
        RoomNameBox.Focus();
    }

    private void Fill(AssistantRoomProfile? room)
    {
        var active = room is not null && room.Id == _activeId;
        var local = room is not null && room.Id == _localId();
        var localKnown = _profiles.Count(p => p.Id == _localId()) == 1 && !string.IsNullOrEmpty(_localId());
        EditorTitle.Text = room is null ? "添加房间" : local ? "本机房间" : active ? "当前房间" : "编辑房间";
        EditorHint.Text = room is null ? "填写完整成员 UID 可自动填入旧房间号。新建房间由服务器分配号码。"
            : local ? "本机房间不可删除。房间凭据和成员可在设置页面中的「本机设置」中修改。"
            : !localKnown ? "本机设置的房间无法确认，暂不能移除档案。"
            : active ? "当前房间不可删除，可在这里修改名称、备注或调整人数。"
            : string.IsNullOrWhiteSpace(room.RoomCode) ? "填写完整成员 UID 可自动填入旧房间号。"
            : "保存档案后，切到此房间时会连接这里的服务器。";
        RoomNameBox.Text = room?.Name ?? "";
        RoomNoteBox.Text = room?.Note ?? "";
        ServerBox.Text = room?.ServerUrl ?? _defaultServer;
        _calculatedRoomCode = "";
        ServerBox.IsReadOnly = RoomCodeBox.IsReadOnly = MemberUidsBox.IsReadOnly = active || local;
        RoomCodeBox.Text = room?.RoomCode ?? "";
        RoomPasswordBox.Password = room?.Password ?? "";
        _filling = true;
        try { MemberUidsBox.Text = room is null ? _selfUid : string.Join(", ", room.TeamUids); }
        finally { _filling = false; }
        if (room is null || string.IsNullOrWhiteSpace(room.RoomCode))
            UpdateLegacyRoomCode(AssistantRoomDraft.ParseMemberUids(MemberUidsBox.Text));
        ExpectedCountBox.SelectedItem = Math.Clamp(room?.ExpectedHoeingPlayers ?? 4, 1, 4);
        RoomPasswordBox.IsEnabled = !active && !local;
        SaveProfileButton.Content = room is null ? "保存已有房间" : "保存档案";
        CreateRoomButton.Visibility = room is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveProfileButton.Visibility = room is not null && !active && !local && localKnown ? Visibility.Visible : Visibility.Collapsed;
        HoeingDetails.Visibility = room is null ? Visibility.Collapsed : Visibility.Visible;
        if (room is not null)
        {
            var bindings = room.OnlineHoeingGroupNames.Count == 0 ? "未绑定" : string.Join("、", room.OnlineHoeingGroupNames);
            var scheduled = string.IsNullOrWhiteSpace(room.ScheduledOnlineTime) ? "未设置" : room.ScheduledOnlineTime
                + (room.ScheduledOnlineRequiresConfirmation ? "（待确认）" : "");
            var completion = DescribeCompletion(room);
            var shortcuts = room.QuickCommands.Count(pair => !string.IsNullOrWhiteSpace(pair.Value));
            HoeingSummary.Text = $"绑定项目：{bindings}\n定时上线：{scheduled}　　完成后：{completion}\n快捷指令：已设置 {shortcuts} 项";
        }
        ShowResult("");
    }

    private string DescribeCompletion(AssistantRoomProfile room)
    {
        if (room.OnlineHoeingCompletionPolicy != "switchRoom") return room.OnlineHoeingCompletionPolicy switch
        {
            "stop" => "停止", "runSpecified" => "执行 " + room.OnlineHoeingSpecifiedTaskName, _ => "恢复被打断的任务"
        };
        if (room.OnlineHoeingRoomSwitchCompletion is not { } link || !link.IsValid || link.SourceProfileId != room.Id
            || !MatchesIdentity(room, link.SourceServerUrl, link.SourceRoomCode))
            return "切换助手房间（源房间身份待重新确认）";
        var targets = _profiles.Where(p => p.Id == link.TargetProfileId).ToArray();
        if (targets.Length != 1 || !MatchesIdentity(targets[0], link.TargetServerUrl, link.TargetRoomCode))
            return $"切换助手房间「{link.TargetName} · {link.TargetRoomCode}」（目标已失效，请重新选择）";
        var target = targets[0];
        var after = link.RunAfterSwitch
            ? "执行 " + (room.OnlineHoeingSpecifiedTaskType switch { "group" => "配置组", "onedragon" => "一条龙", "workflow" => "计划表", _ => "指定任务" })
                + "「" + room.OnlineHoeingSpecifiedTaskName + "」"
            : "待命";
        if (link.RunAfterSwitch && (room.OnlineHoeingSpecifiedStartMode is not ("beginning" or "node")
            || string.IsNullOrWhiteSpace(room.OnlineHoeingSpecifiedConfigRevision))) after += "（任务待重新确认）";
        return $"切换到「{target.Name} · {target.RoomCode}」；切换成功后：{after}";

        bool MatchesIdentity(AssistantRoomProfile profile, string server, string roomCode)
            => profile.RoomCode == roomCode && RoomServerAddress.Same(
                string.IsNullOrWhiteSpace(profile.ServerUrl) ? _defaultServer : profile.ServerUrl, server);
    }

    private void UpdateLegacyRoomCode(List<string> memberUids)
    {
        if (RoomCodeBox.IsReadOnly) return;
        _calculatedRoomCode = AssistantRoomDraft.HasValidMemberUids(memberUids, _selfUid)
            ? AssistConfigManager.GenerateControlRoomCode(memberUids) : "";
        RoomCodeBox.Text = _calculatedRoomCode;
    }

    private async void SaveProfileClick(object sender, RoutedEventArgs e) => await SaveAsync(create: false);
    private async void CreateRoomClick(object sender, RoutedEventArgs e) => await SaveAsync(create: true);

    private async Task SaveAsync(bool create)
    {
        if (_saving) return;
        if (!RoomServerAddress.TryNormalize(ServerBox.Text, out _))
        {
            ShowResult("服务器地址需要填写有效的 http:// 或 https:// 地址，不能带账号、查询参数或片段。");
            ServerBox.Focus();
            return;
        }
        var memberUids = AssistantRoomDraft.ParseMemberUids(MemberUidsBox.Text);
        if (create && RoomCodeBox.Text.Trim() != _calculatedRoomCode && !string.IsNullOrWhiteSpace(RoomCodeBox.Text))
        {
            ShowResult("已填写现有房间号，请点「保存已有房间」。");
            RoomCodeBox.Focus();
            return;
        }
        var selected = ProfilesList.SelectedItem as AssistantRoomProfile;
        var draft = new AssistantRoomDraft(RoomNameBox.Text, RoomNoteBox.Text, ServerBox.Text, create ? "" : RoomCodeBox.Text, RoomPasswordBox.Password,
            memberUids, ExpectedCountBox.SelectedItem is int players ? players : 4);
        _saving = true;
        try
        {
            NewProfileButton.IsEnabled = ProfilesList.IsEnabled = Editor.IsEnabled = SaveProfileButton.IsEnabled = CreateRoomButton.IsEnabled = RemoveProfileButton.IsEnabled = false;
            ShowResult(create ? "正在向目标服务器建房…" : "正在保存档案…");
            var saved = await _save(selected, draft, create);
            var index = selected is null ? -1 : _profiles.IndexOf(selected);
            if (index < 0) _profiles.Add(saved);
            else _profiles[index] = saved;
            RefreshProfiles(saved);
            ShowResult("档案已保存。");
        }
        catch (Exception ex) { ShowResult(ex.Message); }
        finally
        {
            _saving = false;
            NewProfileButton.IsEnabled = ProfilesList.IsEnabled = Editor.IsEnabled = SaveProfileButton.IsEnabled = CreateRoomButton.IsEnabled = RemoveProfileButton.IsEnabled = true;
        }
    }

    private void RemoveProfileClick(object sender, RoutedEventArgs e)
    {
        if (_saving || ProfilesList.SelectedItem is not AssistantRoomProfile room || room.Id == _activeId
            || room.Id == _localId() || _profiles.Count(p => p.Id == _localId()) != 1) return;
        if (MessageBox.Show(this, "移除「" + room.Name + "」的本机档案？不删除服务器房间和历史记录。", "移除本机档案",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _remove(room);
            _profiles.Remove(room);
            RefreshProfiles(_profiles.FirstOrDefault(p => p.Id == _activeId) ?? _profiles.FirstOrDefault());
            ShowResult("档案已移除。");
        }
        catch (Exception ex) { ShowResult(ex.Message); }
    }

    private void UpdateLocalMarkers()
    {
        var localId = _localId();
        var unique = !string.IsNullOrEmpty(localId) && _profiles.Count(p => p.Id == localId) == 1;
        var activeUnique = !string.IsNullOrEmpty(_activeId) && _profiles.Count(p => p.Id == _activeId) == 1;
        foreach (var profile in _profiles)
        {
            profile.IsLocalRoom = unique && profile.Id == localId;
            profile.IsActiveRoom = activeUnique && profile.Id == _activeId;
        }
    }

    private void RefreshProfiles(AssistantRoomProfile? selected)
    {
        UpdateLocalMarkers();
        ProfilesList.ItemsSource = _profiles.ToList();
        ProfilesList.SelectedItem = selected;
        if (selected is null) Fill(null);
    }

    private void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
