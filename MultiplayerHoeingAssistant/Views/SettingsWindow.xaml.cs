using System.Windows;
using System.Reflection;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Views;

public partial class SettingsWindow : Window
{
    private readonly AssistConfig _configCopy;

    /// <summary>
    /// 获取带版本号的窗口标题，与 MainWindow 共享。
    /// </summary>
    private static string GetVersionedTitle(string suffix)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return $"Nexus-BGI · {version ?? "0.0.0"} - {suffix}";
    }

    private SettingsWindow(AssistConfig config)
    {
        _configCopy = config;
        DpiAwarenessController.Initialize(this);
        InitializeComponent();
        Title = GetVersionedTitle("设置");

        // 预填现有配置
        if (!string.IsNullOrEmpty(config.ServerUrl))
            ServerUrlBox.Text = config.ServerUrl;
        if (!string.IsNullOrEmpty(config.ControlRoomPassword))
            PasswordBox.Password = config.ControlRoomPassword;
        if (!string.IsNullOrEmpty(config.PlayerName))
            PlayerNameBox.Text = config.PlayerName;
        if (!string.IsNullOrEmpty(config.PlayerUid))
            PlayerUidBox.Text = config.PlayerUid;
        if (config.TeamUids.Count > 0)
            TeamUidsBox.Text = string.Join(",", config.TeamUids);
        if (!string.IsNullOrEmpty(config.BgiPath))
            BgiPathBox.Text = config.BgiPath;
        ExpectedPlayersBox.Text = config.ExpectedHoeingPlayers.ToString();
    }

    /// <summary>
    /// 显示完整设置弹窗。返回用户填写的配置；取消或失败返回 null。
    /// </summary>
    public static AssistConfig? ShowSettingsDialog(AssistConfig config, Window? owner = null)
    {
        var dialog = new SettingsWindow(config) { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.BuildConfig() : null;
    }

    private AssistConfig? BuildConfig()
    {
        var serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        var password = PasswordBox.Password;
        var playerName = PlayerNameBox.Text.Trim();
        var playerUid = PlayerUidBox.Text.Trim();
        var bgiPath = BgiPathBox.Text.Trim();

        // 校验 TeamUids
        var teamUids = TeamUidsBox.Text
            .Split(',', '，', ';', '；', ' ', '\n', '\r')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (string.IsNullOrEmpty(serverUrl))
        {
            ErrorText.Text = "服务器地址不能为空";
            return null;
        }
        if (password.Length < 4 || password.Length > 8)
        {
            ErrorText.Text = "密码长度需为 4-8 位";
            return null;
        }
        if (teamUids.Count < 1)
        {
            ErrorText.Text = "请至少填写 1 个队伍 UID";
            return null;
        }
        if (teamUids.Any(u => u == playerUid) == false)
        {
            ErrorText.Text = "玩家 UID 必须包含在队伍 UID 中";
            return null;
        }

        return new AssistConfig
        {
            ServerUrl = serverUrl,
            ControlRoomPassword = password,
            PlayerName = playerName,
            PlayerUid = playerUid,
            TeamUids = teamUids,
            BgiPath = bgiPath,
            // 保留其他字段（不因设置弹窗保存而重置）
            ExpectedHoeingPlayers = int.TryParse(ExpectedPlayersBox.Text.Trim(), out var ep) && ep >= 1 && ep <= 4 ? ep : 4,
            ScheduledOnlineTime = _configCopy?.ScheduledOnlineTime ?? "",
            OnlineHoeingGroupNames = _configCopy?.OnlineHoeingGroupNames ?? [],
            OnlineHoeingGroupIndex = _configCopy?.OnlineHoeingGroupIndex ?? 0,
            DisclaimerAccepted = _configCopy?.DisclaimerAccepted ?? false,
            QuickCommands = _configCopy?.QuickCommands ?? new(),
            AutoLaunchWithBgi = _configCopy?.AutoLaunchWithBgi ?? false,
            AutoLaunchWithBgiMinimized = _configCopy?.AutoLaunchWithBgiMinimized ?? true,
            AutoLaunchOnBoot = _configCopy?.AutoLaunchOnBoot ?? false,
            AutoLaunchOnBootMinimized = _configCopy?.AutoLaunchOnBootMinimized ?? true,
            GuardBgi = _configCopy?.GuardBgi ?? false
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = BuildConfig();
        if (cfg == null) return;
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