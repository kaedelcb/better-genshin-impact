using System.Windows;
using System.Windows.Controls;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 助手设置页面（用户控件）。放在 MainWindow 右侧内容区使用。
/// 通过 DataContext 继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // 奥黛塔设置区每次进入都强制收起（不记忆展开状态，用户要求）
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) PetSettingsExpander.IsExpanded = false;
            if (e.NewValue is true) RefreshWebBrowsers();
        };
        _codeTimer.Tick += (_, _) => RefreshWebCode();
    }

    private void OpenLocalWebClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenLocalBrowser();
    }

    // ---------- 网页授权 ----------
    private readonly System.Windows.Threading.DispatcherTimer _codeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _codeExpires;

    private ViewModels.MainViewModel? Vm => DataContext as ViewModels.MainViewModel;

    private void CreateWebCodeClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var (code, expires) = vm.WebOwners.CreateCode();
        _codeExpires = expires;
        WebCodeText.Text = code[..4] + " " + code[4..];
        WebCodePanel.Visibility = Visibility.Visible;
        WebCodeCopyStatus.Visibility = Visibility.Collapsed;
        WebCodeHint.Visibility = Visibility.Visible;
        CreateWebCodeButton.Content = "重新生成";
        _codeTimer.Start();
        RefreshWebCode();
    }

    private void CopyWebCodeClick(object sender, RoutedEventArgs e)
    {
        RefreshWebCode();
        if (WebCodePanel.Visibility != Visibility.Visible) return;
        try
        {
            Clipboard.SetText(WebCodeText.Text.Replace(" ", ""));
            WebCodeCopyStatus.Text = "授权码已复制到剪贴板。";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            WebCodeCopyStatus.Text = "复制失败：剪贴板不可用，请稍后重试。";
        }
        WebCodeCopyStatus.Visibility = Visibility.Visible;
    }

    private void RefreshWebCode()
    {
        var pending = Vm?.WebOwners.PendingCodeExpires;
        var left = _codeExpires - DateTimeOffset.UtcNow;
        if (pending is null || left <= TimeSpan.Zero)
        {
            _codeTimer.Stop();
            WebCodePanel.Visibility = Visibility.Collapsed;
            WebCodeCopyStatus.Visibility = Visibility.Collapsed;
            WebCodeHint.Text = pending is null && left > TimeSpan.Zero ? "授权码已使用或已作废。" : "授权码已过期。";
            CreateWebCodeButton.Content = "生成授权码";
            RefreshWebBrowsers();
            return;
        }
        WebCodeHint.Text = $"在网页「槲寄生」里点「授权此浏览器」并输入上面的数字；剩余 {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void RefreshWebBrowsers()
    {
        if (Vm is not { } vm) return;
        try
        {
            var rows = vm.WebOwners.List().OrderByDescending(b => b.LastUsedAt).Select(b => new
            {
                b.Id,
                Line = $"{b.Label} · 授权于 {b.CreatedAt.ToLocalTime():MM-dd HH:mm} · 最近使用 {b.LastUsedAt.ToLocalTime():MM-dd HH:mm}"
            }).ToArray();
            WebBrowserList.ItemsSource = rows;
            WebBrowserEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            WebBrowserList.ItemsSource = null;
            WebBrowserEmpty.Text = "授权记录无法读取：" + ex.Message;
            WebBrowserEmpty.Visibility = Visibility.Visible;
        }
    }

    private void RevokeWebBrowserClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.Tag is not string id) return;
        try { vm.WebOwners.Revoke(id); vm.AddLog("[网页授权] 已撤销一个网页浏览器的授权"); }
        catch (Exception ex) { vm.AddLog("[网页授权] 撤销失败：" + ex.Message); }
        RefreshWebBrowsers();
    }
}
