using System.Windows;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RemoteLogRoomWindow : Window
{
    private readonly LogBrowserViewModel _browser;
    private readonly CancellationTokenSource _closed = new();

    public RemoteLogRoomWindow(LogBrowserViewModel browser)
    {
        InitializeComponent();
        _browser = browser;
        Loaded += (_, _) => RoomCodeBox.Focus();
        Closed += (_, _) => _closed.Cancel();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        RoomCodeBox.IsEnabled = RoomPasswordBox.IsEnabled = false;
        StatusText.Text = "正在连接…";
        try
        {
            var connected = await _browser.ConnectAssistedLogRoomAsync(RoomCodeBox.Text,
                RoomPasswordBox.Password, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            if (connected) DialogResult = true;
            else StatusText.Text = _browser.RemoteStatus;
        }
        finally
        {
            RoomPasswordBox.Clear();
            if (!_closed.IsCancellationRequested)
            {
                ConnectButton.IsEnabled = true;
                RoomCodeBox.IsEnabled = RoomPasswordBox.IsEnabled = true;
            }
        }
    }
}
