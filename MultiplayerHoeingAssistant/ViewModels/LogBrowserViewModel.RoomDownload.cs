using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class LogBrowserViewModel
{
    private CancellationTokenSource? _roomLogDownloadCts;
    private string? _roomLogCancellationReason;
    private bool _isRoomLogDownloadRunning;
    public bool IsRoomLogDownloadRunning
    {
        get => _isRoomLogDownloadRunning;
        private set
        {
            if (!SetProperty(ref _isRoomLogDownloadRunning, value)) return;
            OnPropertyChanged(nameof(CanUseSingleRemoteLogs));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanUseSingleRemoteLogs => !IsRoomLogDownloadRunning;
    private string _roomLogDownloadStatus = "";
    public string RoomLogDownloadStatus { get => _roomLogDownloadStatus; private set => SetProperty(ref _roomLogDownloadStatus, value); }
    private double _roomLogDownloadProgress;
    public double RoomLogDownloadProgress { get => _roomLogDownloadProgress; private set => SetProperty(ref _roomLogDownloadProgress, value); }
    private string _roomLogDownloadDirectory = "";
    public string RoomLogDownloadDirectory
    {
        get => _roomLogDownloadDirectory;
        private set
        {
            if (!SetProperty(ref _roomLogDownloadDirectory, value)) return;
            OnPropertyChanged(nameof(HasRoomLogDownloadDirectory));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HasRoomLogDownloadDirectory => Directory.Exists(RoomLogDownloadDirectory);
    public RoomLogDownloadResult? LastRoomLogDownloadResult { get; private set; }

    public RelayCommand DownloadRoomTodayLogsCommand => new(async _ => await DownloadRoomTodayLogsAsync(),
        _ => !IsRoomLogDownloadRunning);
    public RelayCommand CancelRoomLogDownloadCommand => new(_ => CancelRoomLogDownload("已取消批量下载"),
        _ => IsRoomLogDownloadRunning);
    public RelayCommand OpenRoomLogDownloadFolderCommand => new(_ =>
    {
        try { Process.Start(new ProcessStartInfo(RoomLogDownloadDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { RoomLogDownloadStatus = "打开日志目录失败：" + ex.Message; }
    }, _ => HasRoomLogDownloadDirectory);

    private void CancelRoomLogDownload(string reason)
    {
        if (_roomLogDownloadCts == null) return;
        _roomLogCancellationReason = reason;
        _roomLogDownloadCts.Cancel();
    }

    public async Task DownloadRoomTodayLogsAsync()
    {
        if (_disposed || IsRoomLogDownloadRunning) return;
        EnsureSignalRHooked();
        if (_pendingListRequestId != null || _download != null)
        { RoomLogDownloadStatus = "请等待当前列表请求或单文件下载结束，再收集全房间日志。"; return; }
        var client = LogClient;
        if (!LogRoomReady || client?.IsConnected != true)
        { RoomLogDownloadStatus = "当前日志房间尚未连接，无法收集全房间日志。"; return; }
        if (client.LogFileUnsupported)
        { RoomLogDownloadStatus = "当前服务端不支持远程日志下载（需新版服务端）。"; return; }

        var assisted = _assistedRoom;
        var ownRoomCode = _mainVm.RoomCode;
        var members = assisted != null
            ? assisted.PlayersSnapshot().Select(p => new RoomLogMember(p.PlayerUid, p.PlayerName, p.Online)).ToArray()
            : _mainVm.Members.Select(p => new RoomLogMember(p.PlayerUid, p.PlayerName, p.Online)).ToArray();
        members = members.Where(p => !string.IsNullOrWhiteSpace(p.Uid)).DistinctBy(p => p.Uid).ToArray();
        if (members.Length == 0)
        { RoomLogDownloadStatus = "当前房间没有可识别的成员，无法收集日志。"; return; }

        using var cts = new CancellationTokenSource();
        _roomLogDownloadCts = cts;
        _roomLogCancellationReason = null;
        LastRoomLogDownloadResult = null;
        OnPropertyChanged(nameof(LastRoomLogDownloadResult));
        RoomLogDownloadDirectory = "";
        RoomLogDownloadProgress = 0;
        IsRoomLogDownloadRunning = true;
        RoomLogDownloadStatus = $"正在收集 {DateTime.Today:yyyy-MM-dd} 的全房间日志，共 {members.Length} 位成员…";
        PropertyChangedEventHandler roomChanged = (_, e) =>
        {
            if (assisted == null && (e.PropertyName is nameof(MainViewModel.RoomCode) or nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsStandaloneMode)))
                if (_mainVm.RoomCode != ownRoomCode || _mainVm.IsStandaloneMode || !client.IsConnected
                    || !ReferenceEquals(client, LogClient))
                    CancelRoomLogDownload("本房间或连接已切换，批量下载已取消");
        };
        _mainVm.PropertyChanged += roomChanged;
        try
        {
            var progress = new Progress<RoomLogDownloadProgress>(value =>
            {
                if (_disposed || !ReferenceEquals(_roomLogDownloadCts, cts)) return;
                RoomLogDownloadProgress = value.Fraction;
                RoomLogDownloadStatus = value.Message;
            });
            var roomName = assisted?.RoomCode ?? (string.IsNullOrWhiteSpace(ownRoomCode) ? "本房间" : ownRoomCode);
            var result = await new RoomLogDownloadService(client).DownloadAsync(roomName, members, DateTime.Today,
                Path.Combine(LogFileBrowser.AssistantLogDir, "remote_downloads"), progress, cts.Token);
            LastRoomLogDownloadResult = result;
            OnPropertyChanged(nameof(LastRoomLogDownloadResult));
            RoomLogDownloadDirectory = result.OutputDirectory;
            var files = result.Members.Sum(member => member.BgiFiles + member.AssistantFiles);
            var complete = result.Members.Count(member => member.Complete);
            var title = result.Cancelled ? (_roomLogCancellationReason ?? "批量下载已取消或连接中断") : "全房间日志收集结束";
            RoomLogDownloadStatus = $"{title}：已下载 {files} 个文件；BGI 和助手均收齐 {complete}/{members.Length} 人。\n"
                + string.Join("\n", result.Members.Select(member => $"{member.Name}：{member.Message}"))
                + $"\n保存到 {result.OutputDirectory}（详见下载结果.txt）";
            if (!result.Cancelled) RoomLogDownloadProgress = 1;
            if (!_disposed && files > 0) RefreshFiles();
        }
        catch (OperationCanceledException)
        { RoomLogDownloadStatus = _roomLogCancellationReason ?? "批量下载已取消"; }
        catch (Exception ex)
        { RoomLogDownloadStatus = "全房间日志收集失败：" + ex.Message; }
        finally
        {
            _mainVm.PropertyChanged -= roomChanged;
            if (ReferenceEquals(_roomLogDownloadCts, cts)) _roomLogDownloadCts = null;
            IsRoomLogDownloadRunning = false;
        }
    }
}
