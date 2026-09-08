using System.Windows;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 事发录像设置弹窗（异常监控 Tab「📂」旁的 ⚙ 打开）：缓存时长 / 截图间隔 / 保存事发前后秒数。
/// 仿 ConfirmStepWindow / PasswordSetupWindow 风格（本工程无 ThemedMessageBox）。
/// 保存时做数值域校验，非法输入红字提示且不关窗。
/// </summary>
public partial class IncidentSettingsWindow : Window
{
    /// <summary>校验通过后的结果（仅点「保存」时有值）。</summary>
    public (int BufferSeconds, double IntervalSeconds, int PreSeconds, int PostSeconds)? Result { get; private set; }

    private IncidentSettingsWindow(DodocoSettings current, Window? owner)
    {
        InitializeComponent();
        Owner = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        BufferInput.Text = current.IncidentBufferSeconds.ToString();
        IntervalInput.Text = current.IncidentCaptureIntervalSeconds.ToString("0.#");
        PreInput.Text = current.IncidentPreSeconds.ToString();
        PostInput.Text = current.IncidentPostSeconds.ToString();
    }

    /// <summary>弹出设置对话框；返回用户保存的新值，取消/关窗返回 null。</summary>
    public static (int BufferSeconds, double IntervalSeconds, int PreSeconds, int PostSeconds)? ShowEdit(
        DodocoSettings current, Window? owner = null)
    {
        // 托盘/静默启动的助手中主窗口从未显示，不能当 Owner
        // （WPF 抛"无法将 Owner 属性设置为之前未显示的 Window"）——此时无 Owner 居中屏幕显示
        var o = owner ?? Application.Current?.MainWindow;
        if (o is not { IsLoaded: true, IsVisible: true }) o = null;
        var dialog = new IncidentSettingsWindow(current, o);
        dialog.ShowDialog();
        return dialog.Result;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BufferInput.Text.Trim(), out var buffer) || buffer is < 5 or > 60)
        {
            ErrorText.Text = "缓存时长需为 5 ~ 60 的整数（秒）";
            return;
        }
        if (!double.TryParse(IntervalInput.Text.Trim(), out var interval) || interval is < 0.5 or > 5)
        {
            ErrorText.Text = "截图间隔需为 0.5 ~ 5 之间的数（秒，可填 0.5）";
            return;
        }
        if (!int.TryParse(PreInput.Text.Trim(), out var pre) || pre is < 1 or > 20)
        {
            ErrorText.Text = "保存事发前需为 1 ~ 20 的整数（秒）";
            return;
        }
        if (!int.TryParse(PostInput.Text.Trim(), out var post) || post is < 1 or > 20)
        {
            ErrorText.Text = "保存事发后需为 1 ~ 20 的整数（秒）";
            return;
        }
        Result = (buffer, interval, pre, post);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
