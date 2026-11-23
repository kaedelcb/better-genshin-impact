using System.Text.Json;
using System.Windows;
using System.Reflection;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Views;

public partial class SettingsWindow : Window
{
    private readonly AssistConfig _sourceConfig;
    private readonly string? _editedRoomId;
    private readonly string? _editedRoomCode;
    private AssistConfig? _savedConfig;

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
        var room = config.ActiveRoomProfile;
        _editedRoomId = room?.Id;
        _editedRoomCode = room?.RoomCode;
        DpiAwarenessController.Initialize(this);
        InitializeComponent();
        Title = GetVersionedTitle("设置");

        ServerUrlBox.Text = config.GetRoomServerUrl(room);
        if (room is not null)
        {
            ServerUrlLabel.Text = "当前房间的服务器地址";
            ServerUrlBox.IsReadOnly = true;
            ServerUrlHint.Text = "每个房间可单独设置地址。请在「管理房间」中添加或编辑房间档案。";
        }
        PlayerNameBox.Text = config.PlayerName ?? "";
        PlayerUidBox.Text = config.PlayerUid ?? "";
        BgiPathBox.Text = config.BgiPath ?? "";
        ObserverModeCheckBox.IsChecked = config.ObserverMode;
        BypassSystemProxyCheckBox.IsChecked = config.BypassSystemProxy;

        // 新用户先填写本机信息，再以房间号加入或登记房间。只编辑已明确归属的当前档案。
        RoomCredentialsPanel.Visibility = room is null ? Visibility.Collapsed : Visibility.Visible;
        RoomSetupHint.Visibility = room is null ? Visibility.Visible : Visibility.Collapsed;
        if (room != null)
        {
            RoomEditingHint.Text = $"正在编辑：{room.Name}（{room.RoomCode}）";
            PasswordBox.Password = config.ControlRoomPassword ?? "";
            TeamUidsBox.Text = string.Join(",", config.TeamUids ?? []);
            ExpectedPlayersBox.Text = config.ExpectedHoeingPlayers.ToString();
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
    public static AssistConfig? ShowSettingsDialog(AssistConfig config, Window? owner = null)
    {
        var dialog = new SettingsWindow(config) { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog._savedConfig : null;
    }

    private AssistConfig? BuildConfig()
    {
        var room = _sourceConfig.ActiveRoomProfile;
        if (room?.Id != _editedRoomId || room?.RoomCode != _editedRoomCode)
        {
            ErrorText.Text = "当前房间已变化，请关闭后重新打开设置。";
            return null;
        }

        var serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        var playerName = PlayerNameBox.Text.Trim();
        var playerUid = PlayerUidBox.Text.Trim();
        var editingOnlineSetup = !_sourceConfig.StandaloneMode && serverUrl.Length > 0;
        if (editingOnlineSetup && (playerName.Length == 0 || playerUid.Length == 0))
        {
            ErrorText.Text = "联机需填写本机玩家名称和 UID。";
            return null;
        }

        // 复制整个设置，包含其他房间和全部列表；不让窗口中的编辑触碰正在运行的对象。
        var result = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.SerializeToUtf8Bytes(_sourceConfig))!;
        result.ServerUrl = serverUrl;
        result.PlayerName = playerName;
        result.PlayerUid = playerUid;
        result.BgiPath = BgiPathBox.Text.Trim();
        result.ObserverMode = ObserverModeCheckBox.IsChecked == true;
        result.BypassSystemProxy = BypassSystemProxyCheckBox.IsChecked == true;
        if (result.ActiveRoomProfile is not { } editedRoom) return result;

        var password = PasswordBox.Password;
        var teamUids = TeamUidsBox.Text.Split([',', '，', ';', '；', ' ', '\n', '\r'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (editingOnlineSetup && (string.IsNullOrEmpty(password) || !teamUids.Contains(playerUid, StringComparer.Ordinal)))
        {
            ErrorText.Text = "请填写当前房间密码，并确认本机玩家 UID 在成员 UID 列表中。";
            return null;
        }

        result.ControlRoomPassword = password;
        result.TeamUids = teamUids;
        if (!result.ObserverMode)
        {
            if (!int.TryParse(ExpectedPlayersBox.Text.Trim(), out var count) || count is < 1 or > 4)
            {
                ErrorText.Text = "预期开锄人数须为 1–4。";
                return null;
            }
            result.ExpectedHoeingPlayers = count;
        }
        // 设置弹窗不改变档案 ID、房间号或激活指针。
        editedRoom.Capture(result);
        return result;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _savedConfig = BuildConfig();
        if (_savedConfig == null) return;
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
