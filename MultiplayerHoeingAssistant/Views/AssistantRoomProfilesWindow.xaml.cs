using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

internal sealed record AssistantRoomDraft(string Name, string ServerUrl, string RoomCode, string Password,
    string MemberUids, int ExpectedPlayers);

public partial class AssistantRoomProfilesWindow : Window
{
    private readonly string _activeId;
    private readonly string _defaultServer;
    private readonly string _selfUid;
    private readonly Func<AssistantRoomProfile?, AssistantRoomDraft, bool, Task> _save;
    private readonly Action<AssistantRoomProfile> _remove;
    private bool _saving;

    internal AssistantRoomProfilesWindow(IReadOnlyList<AssistantRoomProfile> profiles, string activeId, string server, string selfUid,
        Func<AssistantRoomProfile?, AssistantRoomDraft, bool, Task> save, Action<AssistantRoomProfile> remove)
    {
        _activeId = activeId;
        _defaultServer = server;
        _selfUid = selfUid;
        _save = save;
        _remove = remove;
        DpiAwarenessController.Initialize(this);
        InitializeComponent();
        ExpectedCountBox.ItemsSource = new[] { 1, 2, 3, 4 };
        ProfilesList.ItemsSource = profiles;
        ProfilesList.SelectedItem = profiles.FirstOrDefault(p => p.Id == activeId) ?? profiles.FirstOrDefault();
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
        EditorTitle.Text = room is null ? "添加房间" : active ? "当前房间" : "编辑房间";
        EditorHint.Text = room is null ? "已有房间请填写房间号；要新建房间，请留空并点「建房并保存」。"
            : active ? "当前连接的地址和房间号保持不变。可在这里改名或调整人数。"
            : "保存档案不会连接服务器；切到此档案时才会连接它的地址。";
        RoomNameBox.Text = room?.Name ?? "";
        ServerBox.Text = room?.ServerUrl ?? _defaultServer;
        RoomCodeBox.Text = room?.RoomCode ?? "";
        RoomPasswordBox.Password = room?.Password ?? "";
        MemberUidsBox.Text = room is null ? _selfUid : string.Join(", ", room.TeamUids);
        ExpectedCountBox.SelectedItem = Math.Clamp(room?.ExpectedHoeingPlayers ?? 4, 1, 4);
        ServerBox.IsReadOnly = RoomCodeBox.IsReadOnly = MemberUidsBox.IsReadOnly = active;
        RoomPasswordBox.IsEnabled = !active;
        SaveProfileButton.Content = room is null ? "保存已有房间" : "保存档案";
        CreateRoomButton.Visibility = room is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveProfileButton.Visibility = room is not null && !active ? Visibility.Visible : Visibility.Collapsed;
        HoeingDetails.Visibility = room is null ? Visibility.Collapsed : Visibility.Visible;
        if (room is not null)
        {
            var bindings = room.OnlineHoeingGroupNames.Count == 0 ? "未绑定" : string.Join("、", room.OnlineHoeingGroupNames);
            var scheduled = string.IsNullOrWhiteSpace(room.ScheduledOnlineTime) ? "未设置" : room.ScheduledOnlineTime
                + (room.ScheduledOnlineRequiresConfirmation ? "（待确认）" : "");
            var completion = room.OnlineHoeingCompletionPolicy switch
            {
                "stop" => "停止", "runSpecified" => "执行 " + room.OnlineHoeingSpecifiedTaskName, _ => "恢复被打断的任务"
            };
            var shortcuts = room.QuickCommands.Count(pair => !string.IsNullOrWhiteSpace(pair.Value));
            HoeingSummary.Text = $"绑定项目：{bindings}\n定时上线：{scheduled}　　完成后：{completion}\n快捷指令：已设置 {shortcuts} 项";
        }
        ShowResult("");
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
        if (create && !string.IsNullOrWhiteSpace(RoomCodeBox.Text))
        {
            ShowResult("建房由服务器分配房间号，请先留空；已有房间请点「保存已有房间」。");
            RoomCodeBox.Focus();
            return;
        }
        var selected = ProfilesList.SelectedItem as AssistantRoomProfile;
        var draft = new AssistantRoomDraft(RoomNameBox.Text, ServerBox.Text, RoomCodeBox.Text, RoomPasswordBox.Password,
            MemberUidsBox.Text, ExpectedCountBox.SelectedItem is int players ? players : 4);
        _saving = true;
        try
        {
            ProfilesList.IsEnabled = Editor.IsEnabled = SaveProfileButton.IsEnabled = CreateRoomButton.IsEnabled = RemoveProfileButton.IsEnabled = false;
            ShowResult(create ? "正在向目标服务器建房…" : "正在保存档案…");
            await _save(selected, draft, create);
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { ShowResult(ex.Message); }
        finally
        {
            _saving = false;
            ProfilesList.IsEnabled = Editor.IsEnabled = SaveProfileButton.IsEnabled = CreateRoomButton.IsEnabled = RemoveProfileButton.IsEnabled = true;
        }
    }

    private void RemoveProfileClick(object sender, RoutedEventArgs e)
    {
        if (_saving || ProfilesList.SelectedItem is not AssistantRoomProfile room || room.Id == _activeId) return;
        if (MessageBox.Show(this, "移除「" + room.Name + "」的本机档案？不删除服务器房间和历史记录。", "移除本机档案",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _remove(room); DialogResult = true; }
        catch (Exception ex) { ShowResult(ex.Message); }
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
