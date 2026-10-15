using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RemoteLogRoomWindow : Window
{
    private readonly LogBrowserViewModel _browser;
    private readonly RemoteLogRoomHistoryStore _history = new();
    private readonly string _serverUrl;
    private readonly CancellationTokenSource _closed = new();
    private bool _fillingHistory;

    public RemoteLogRoomWindow(LogBrowserViewModel browser)
    {
        InitializeComponent();
        _browser = browser;
        _serverUrl = browser.LogRoomServerUrl;
        RoomCodeBox.TextChanged += (_, _) =>
        {
            if (_fillingHistory) return;
            // 手动换房间时不能把上一个房间的密码带过去。
            if (HistoryRoomsBox.SelectedItem is RemoteLogRoomHistoryEntry entry && RoomCodeBox.Text.Trim() != entry.RoomCode)
            {
                HistoryRoomsBox.SelectedItem = null;
                RoomPasswordBox.Clear();
            }
        };
        ReloadHistory(selectLatest: true);
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(RoomPasswordBox.Password)) ConnectButton.Focus();
            else RoomCodeBox.Focus();
        };
        Closed += (_, _) => _closed.Cancel();
    }

    private void ReloadHistory(bool selectLatest)
    {
        _fillingHistory = true;
        try
        {
            var entries = _history.GetRooms(_serverUrl);
            HistoryRoomsBox.ItemsSource = entries;
            HistoryPanel.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryRoomsBox.SelectedIndex = selectLatest && entries.Count > 0 ? 0 : -1;
        }
        catch (Exception ex) { StatusText.Text = "读取房间记录失败，可手动输入：" + ex.Message; }
        finally { _fillingHistory = false; }
        FillSelectedHistory();
    }

    private void HistoryRooms_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingHistory) FillSelectedHistory();
    }

    private void FillSelectedHistory()
    {
        var entry = HistoryRoomsBox.SelectedItem as RemoteLogRoomHistoryEntry;
        DeleteHistoryButton.IsEnabled = entry != null;
        if (entry == null) return;
        _fillingHistory = true;
        try
        {
            RoomCodeBox.Text = entry.RoomCode;
            RoomPasswordBox.Password = _history.GetPassword(_serverUrl, entry.RoomCode) ?? "";
            StatusText.Text = "已填入记录，可直接连接或选择其他房间。";
        }
        catch (Exception ex)
        {
            RoomPasswordBox.Clear();
            StatusText.Text = "这条记录的密码无法读取，请重新输入：" + ex.Message;
        }
        finally { _fillingHistory = false; }
    }

    private void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryRoomsBox.SelectedItem is not RemoteLogRoomHistoryEntry entry) return;
        try
        {
            _history.Forget(_serverUrl, entry.RoomCode);
            ReloadHistory(selectLatest: false);
            RoomCodeBox.Clear();
            RoomPasswordBox.Clear();
            StatusText.Text = "已删除该房间的记录。";
        }
        catch (Exception ex) { StatusText.Text = "删除记录失败：" + ex.Message; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        RoomCodeBox.IsEnabled = RoomPasswordBox.IsEnabled = false;
        HistoryRoomsBox.IsEnabled = DeleteHistoryButton.IsEnabled = false;
        StatusText.Text = "正在连接…";
        try
        {
            var serverUrl = _browser.LogRoomServerUrl;
            var roomCode = RoomCodeBox.Text;
            var password = RoomPasswordBox.Password;
            var connected = await _browser.ConnectAssistedLogRoomAsync(roomCode, password, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            if (connected)
            {
                try { _history.Remember(serverUrl, roomCode, password); }
                catch (Exception ex) { _browser.RemoteStatus += "；房间记录保存失败：" + ex.Message; }
                DialogResult = true;
            }
            else StatusText.Text = _browser.RemoteStatus;
        }
        finally
        {
            RoomPasswordBox.Clear();
            if (!_closed.IsCancellationRequested)
            {
                ConnectButton.IsEnabled = true;
                RoomCodeBox.IsEnabled = RoomPasswordBox.IsEnabled = true;
                HistoryRoomsBox.IsEnabled = true;
                DeleteHistoryButton.IsEnabled = HistoryRoomsBox.SelectedItem != null;
            }
        }
    }
}
