using System.Windows;

namespace MultiplayerHoeingAssistant.Views;

public partial class PasswordSetupWindow : Window
{
    private readonly bool _isSetupMode;

    private PasswordSetupWindow(bool isSetupMode)
    {
        InitializeComponent();
        _isSetupMode = isSetupMode;

        if (!isSetupMode)
        {
            Title = "输入密码";
            TitleText.Text = "输入控制房间密码";
        }
    }

    /// <summary>
    /// 显示密码对话框。isSetupMode=true 为房主设置模式（需确认两次一致），
    /// false 为成员输入模式。返回密码；取消返回 null。
    /// </summary>
    public static string? ShowPasswordDialog(bool isSetupMode, Window? owner = null)
    {
        var dialog = new PasswordSetupWindow(isSetupMode) { Owner = owner ?? Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.GetPassword() : null;
    }

    private string? GetPassword()
    {
        if (!_isSetupMode) return PasswordInput.Password;

        // 设置模式：两次输入必须一致
        var pwd = PasswordInput.Password;
        var confirm = ConfirmInput.Password;
        return string.Equals(pwd, confirm) ? pwd : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var pwd = PasswordInput.Password;
        if (pwd.Length < 4 || pwd.Length > 8)
        {
            ErrorText.Text = "密码长度需为 4-8 位";
            return;
        }

        if (_isSetupMode && PasswordInput.Password != ConfirmInput.Password)
        {
            ErrorText.Text = "两次输入的密码不一致";
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}