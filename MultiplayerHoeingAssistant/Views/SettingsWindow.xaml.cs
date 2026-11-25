using System.Text.Json;
using System.Windows;
using System.Reflection;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

public partial class SettingsWindow : Window
{
    private readonly AssistConfig _sourceConfig;
    private readonly string? _editedRoomId;
    private readonly string? _editedRoomCode;
    private AssistConfig? _savedConfig;
    private Func<AssistConfig, string?>? _validateSavedConfig;

    /// <summary>
    /// 获取带版本号的窗口标题，与 MainWindow 共享。
    /// </summary>
    private static string GetVersionedTitle(string suffix)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return $"Nexus BGI · {version ?? "0.0.0"} - {suffix}";
    }

    private SettingsWindow(AssistConfig config)
    {
        _sourceConfig = config;
        config.EnsureLocalRoomProfile();
        var room = config.LocalRoomProfile;
        _editedRoomId = room?.Id;
        _editedRoomCode = room?.RoomCode;
        DpiAwarenessController.Initialize(this);
        InitializeComponent();
        Title = GetVersionedTitle("设置");

        ServerUrlBox.Text = config.GetRoomServerUrl(room);
        if (room is not null)
        {
            ServerUrlLabel.Text = "本机房间的服务器地址";
            ServerUrlBox.IsReadOnly = true;
            ServerUrlHint.Text = "每个房间可单独设置地址。请在「管理房间」中添加或编辑房间档案。";
        }
        PlayerNameBox.Text = config.PlayerName ?? "";
        PlayerUidBox.Text = config.PlayerUid ?? "";
        BgiPathBox.Text = config.BgiPath ?? "";
        ObserverModeCheckBox.IsChecked = config.ObserverMode;
        BypassSystemProxyCheckBox.IsChecked = config.BypassSystemProxy;

        // 本机房间首次沿用旧设置的当前房间，之后独立于当前连接的房间。
        RoomCredentialsPanel.Visibility = room is null ? Visibility.Collapsed : Visibility.Visible;
        RoomSetupHint.Visibility = room is null ? Visibility.Visible : Visibility.Collapsed;
        if (room != null)
        {
            RoomEditingHint.Text = $"本机房间：{room.Name}（{room.RoomCode}）";
            PasswordBox.Password = room.Password ?? "";
            TeamUidsBox.Text = string.Join(",", room.TeamUids ?? []);
            ExpectedPlayersBox.Text = room.ExpectedHoeingPlayers.ToString();
        }
        else if (config.RoomProfiles.Count > 0)
        {
            RoomSetupHint.Text = "无法确认本机设置的房间，请核对当前房间或恢复原配置。";
            ServerUrlLabel.Text = "当前房间的服务器地址";
            ServerUrlBox.IsReadOnly = true;
        }
        // 监控模式（遥控器）不入房间成员表，本机填的预期开锄人数不参与服务端取 min。
        UpdateExpectedPlayersVisibility(!config.ObserverMode);
        ObserverModeCheckBox.Checked += (_, _) => UpdateExpectedPlayersVisibility(false);
        ObserverModeCheckBox.Unchecked += (_, _) => UpdateExpectedPlayersVisibility(true);
    }

    /// <summary>预期开锄人数整段（标签+输入框+说明）显隐：遥控器模式下隐藏。</summary>
    private void UpdateExpectedPlayersVisibility(bool visible)
    {
        var v = visible && _editedRoomId != null ? Visibility.Visible : Visibility.Collapsed;
        ExpectedPlayersLabel.Visibility = v;
        ExpectedPlayersBox.Visibility = v;
        ExpectedPlayersHint.Visibility = v;
    }

    /// <summary>
    /// 显示完整设置弹窗。返回用户填写的配置；取消或失败返回 null。
    /// </summary>
    public static AssistConfig? ShowSettingsDialog(AssistConfig config, Window? owner = null, Func<AssistConfig, string?>? validate = null)
    {
        var dialog = new SettingsWindow(config) { Owner = owner ?? Application.Current?.MainWindow, _validateSavedConfig = validate };
        return dialog.ShowDialog() == true ? dialog._savedConfig : null;
    }

    private AssistConfig? BuildConfig()
    {
        var room = _sourceConfig.LocalRoomProfile;
        if (room?.Id != _editedRoomId || room?.RoomCode != _editedRoomCode)
        {
            ErrorText.Text = "本机房间已变化，请关闭后重新打开设置。";
            return null;
        }

        var serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        var playerName = PlayerNameBox.Text.Trim();
        var playerUid = PlayerUidBox.Text.Trim();
        var editingOnlineSetup = !_sourceConfig.StandaloneMode && serverUrl.Length > 0;
        if (playerUid != _sourceConfig.PlayerUid && _sourceConfig.ActiveRoomProfile is { } active
            && active.Id != room?.Id && !active.TeamUids.Contains(playerUid, StringComparer.Ordinal))
        {
            ErrorText.Text = "修改本机玩家 UID 前，请确认新 UID 也在当前连接房间的成员列表中。";
            return null;
        }
        if (editingOnlineSetup && (playerName.Length == 0 || playerUid.Length == 0))
        {
            ErrorText.Text = "联机需填写本机玩家名称和 UID。";
            return null;
        }

        // 复制整个设置，包含其他房间和全部列表；不让窗口中的编辑触碰正在运行的对象。
        var result = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.SerializeToUtf8Bytes(_sourceConfig))!;
        // 活动档案的权威值是运行字段。普通设置随后 ApplyTo 时不能把绑定/完成动作和定时恢复成较旧档案摘要。
        result.ActiveRoomProfile?.Capture(result);
        if (room is null && result.RoomProfiles.Count == 0) result.ServerUrl = serverUrl;
        result.PlayerName = playerName;
        result.PlayerUid = playerUid;
        result.BgiPath = BgiPathBox.Text.Trim();
        result.ObserverMode = ObserverModeCheckBox.IsChecked == true;
        result.BypassSystemProxy = BypassSystemProxyCheckBox.IsChecked == true;
        if (result.LocalRoomProfile is not { } editedRoom) return result;

        var password = PasswordBox.Password;
        var teamUids = TeamUidsBox.Text.Split([',', '，', ';', '；', ' ', '\n', '\r'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        if (editingOnlineSetup && (string.IsNullOrEmpty(password) || !AssistantRoomDraft.HasValidMemberUids(teamUids, playerUid)))
        {
            ErrorText.Text = "请填写本机房间密码和有效成员 UID，并包含本机玩家 UID。";
            return null;
        }

        var membersChanged = !teamUids.OrderBy(uid => uid, StringComparer.Ordinal)
            .SequenceEqual(editedRoom.TeamUids.OrderBy(uid => uid, StringComparer.Ordinal), StringComparer.Ordinal);
        if (membersChanged)
        {
            if (!AssistantRoomDraft.HasValidMemberUids(teamUids, playerUid))
            {
                ErrorText.Text = "修改成员时请填写有效 UID，并包含本机玩家 UID。";
                return null;
            }
            var code = AssistConfigManager.GenerateControlRoomCode(teamUids);
            if (result.RoomProfiles.Any(p => p.Id != editedRoom.Id && p.RoomCode == code
                && RoomServerAddress.Same(result.GetRoomServerUrl(p), result.GetRoomServerUrl(editedRoom))))
            {
                ErrorText.Text = "根据成员计算的房间号已被其他档案使用，请核对成员。";
                return null;
            }
            if (editedRoom.RoomCode != code)
            {
                editedRoom.ScheduledOnlineLastFireDate = null;
                editedRoom.ScheduledOnlineRequiresConfirmation = !string.IsNullOrEmpty(editedRoom.ScheduledOnlineTime);
            }
            editedRoom.RoomCode = code;
        }
        editedRoom.Password = password;
        editedRoom.TeamUids = teamUids;
        if (!result.ObserverMode)
        {
            if (!int.TryParse(ExpectedPlayersBox.Text.Trim(), out var count) || count is < 1 or > 4)
            {
                ErrorText.Text = "预期开锄人数须为 1–4。";
                return null;
            }
            editedRoom.ExpectedHoeingPlayers = count;
        }
        if (editedRoom.Id == result.ActiveRoomProfileId) editedRoom.ApplyTo(result);
        return result;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _savedConfig = BuildConfig();
        if (_savedConfig == null) return;
        if (_validateSavedConfig?.Invoke(_savedConfig) is { Length: > 0 } error)
        {
            _savedConfig = null;
            ErrorText.Text = error;
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BrowseBgiButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 BetterGI.exe",
            Filter = "BetterGI|BetterGI.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            BgiPathBox.Text = dlg.FileName;
        }
    }
}
