using System.Windows;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class LogBrowserViewModel
{
    private RemoteLogRoomSession? _assistedRoom;
    private CancellationTokenSource? _roomConnectCts;
    private bool _disposed;
    private int _remoteContextVersion;
    private string? _listMemberUid;

    public bool IsAssistedLogRoom => _assistedRoom != null;
    public bool IsConnectingLogRoom => _roomConnectCts != null;
    public bool CanReturnToOwnLogRoom => IsAssistedLogRoom || IsConnectingLogRoom;
    public string LogRoomServerUrl => _mainVm.Config?.ServerUrl ?? "";
    public string LogRoomLabel => _assistedRoom is { } room
        ? $"协助房间：{room.RoomCode}" : "本房间";
    private SignalRClient? LogClient => _assistedRoom?.Client ?? _mainVm.SignalR;
    private bool LogRoomReady => _assistedRoom?.IsReady ?? (!_mainVm.IsStandaloneMode && LogClient?.IsConnected == true);

    public RelayCommand ReturnToOwnLogRoomCommand => new(_ => ReturnToOwnLogRoom());

    private void NotifyLogRoom()
    {
        OnPropertyChanged(nameof(IsAssistedLogRoom));
        OnPropertyChanged(nameof(IsConnectingLogRoom));
        OnPropertyChanged(nameof(CanReturnToOwnLogRoom));
        OnPropertyChanged(nameof(LogRoomLabel));
        OnPropertyChanged(nameof(RemoteDownloadUnsupported));
    }

    /// <summary>连接成功才替换当前日志来源；失败、取消保留原房间。连接记录由弹窗在成功后保存。</summary>
    public async Task<bool> ConnectAssistedLogRoomAsync(string roomCode, string password,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || IsConnectingLogRoom) return false;
        roomCode = roomCode.Trim();
        var config = _mainVm.Config;
        if (string.IsNullOrWhiteSpace(roomCode) || string.IsNullOrEmpty(password))
        { RemoteStatus = "请输入房间号和密码"; return false; }
        if (string.IsNullOrWhiteSpace(config?.ServerUrl))
        { RemoteStatus = "请先在设置中填写协调服务器地址"; return false; }
        if (_mainVm.IsStandaloneMode)
        { RemoteStatus = "请先在设置中关闭单机模式，再连接协助房间"; return false; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _roomConnectCts = cts;
        NotifyLogRoom();
        RemoteStatus = "正在连接协助房间…";
        try
        {
            var room = await RemoteLogRoomSession.ConnectAsync(config.ServerUrl, roomCode, password,
                config.BypassSystemProxy, cts.Token);
            if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_roomConnectCts, cts))
            { await room.DisposeAsync(); return false; }
            var old = _assistedRoom;
            ResetRemoteRequests();
            _assistedRoom = room;
            room.Changed += () => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !ReferenceEquals(room, _assistedRoom)) return;
                if (!room.IsReady) ResetRemoteRequests();
                RefreshRemoteMembers();
                if (!room.IsReady) RemoteStatus = room.Error ?? "协助房间尚未连接";
            });
            EnsureSignalRHooked();
            RefreshRemoteMembers();
            NotifyLogRoom();
            RemoteStatus = RemoteMembers.Count == 0 ? "已连接协助房间，当前没有在线成员" : "已连接协助房间，请选择成员获取日志列表";
            if (old != null) await DisposeLogRoomAsync(old);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(_roomConnectCts, cts))
                RemoteStatus = cts.IsCancellationRequested ? "已取消连接" : "连接协助房间超时，请检查房间与网络";
            return false;
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_roomConnectCts, cts)) RemoteStatus = "连接协助房间失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_roomConnectCts, cts)) _roomConnectCts = null;
            if (!_disposed) NotifyLogRoom();
        }
    }

    public void ReturnToOwnLogRoom()
    {
        _roomConnectCts?.Cancel();
        _roomConnectCts = null;
        var room = _assistedRoom;
        _assistedRoom = null;
        ResetRemoteRequests();
        EnsureSignalRHooked();
        RefreshRemoteMembers();
        NotifyLogRoom();
        RemoteStatus = "已返回本房间，请选择成员获取日志列表";
        if (room != null) _ = DisposeLogRoomAsync(room);
    }

    private static async Task DisposeLogRoomAsync(RemoteLogRoomSession room)
    {
        try { await room.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"日志协助连接释放失败：{ex.Message}"); }
    }

    /// <summary>切换房间/成员后不接收旧请求；后台落盘仍保留在原来源目录，不回写新面板状态。</summary>
    private void ResetRemoteRequests(bool cancelRoomBatch = true)
    {
        if (cancelRoomBatch) CancelRoomLogDownload("日志房间或连接已切换，批量下载已取消");
        _remoteContextVersion++;
        _pendingListRequestId = null;
        _listMemberUid = null;
        _download = null;
        RemoteFiles.Clear();
        SelectedRemoteFile = null;
        DownloadProgress = 0;
        StopTimeoutTimerIfIdle();
    }
}
