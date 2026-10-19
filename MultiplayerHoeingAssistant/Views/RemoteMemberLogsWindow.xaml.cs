using System.Windows;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RemoteMemberLogsWindow : Window
{
    private readonly LogBrowserViewModel _browser;

    public RemoteMemberLogsWindow(LogBrowserViewModel browser)
    {
        InitializeComponent();
        _browser = browser;
        // 复用页面状态；关闭窗口不取消列表、下载或当前协助房间。
        DataContext = browser;
    }

    private void AssistLogRoom_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RemoteLogRoomWindow(_browser) { Owner = this };
        dialog.Resources.MergedDictionaries.Add(Resources);
        dialog.ShowDialog();
    }
}
