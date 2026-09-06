using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;

using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.Services;

public class SignalRClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private string _roomCode = string.Empty;
    private string _playerUid = string.Empty;
    private string _playerName = string.Empty;
    private bool _isRemote;
    private string _clientInstanceId = "";

    // 持久的连接参数：供手动 RefreshAsync 重建连接使用
    private string _serverUrl = string.Empty;
    private string _password = string.Empty;
    private List<string> _teamUids = new();

    private bool _disposed;

    /// <summary>服务端不支持 ReportMemberScreenshot（旧服务端首次 HubException 后标记，停止重试；
    /// 新连接建立时重置，升级服务端后自动恢复）。volatile：Timer 线程读、Hub 调用线程写。</summary>
    private volatile bool _screenshotUnsupported;
    /// <summary>同 _screenshotUnsupported：旧服务端无 ReportMemberLogBatch 时停重试（房间实时日志汇聚）。</summary>
    private volatile bool _logUnsupported;
    /// <summary>同模式：旧服务端无 SubscribeMemberLog/UnsubscribeMemberLog 时停重试（日志按需订阅）。
    /// 观看端据此给远程来源项标注"（需新版服务端）"。</summary>
    private volatile bool _logSubscribeUnsupported;
    /// <summary>同模式：旧服务端无 RequestMemberLogFiles 等日志下载方法时停重试（远程日志下载）。
    /// 下载端 UI 据此标注"需新版服务端"。</summary>
    private volatile bool _logFileUnsupported;
    /// <summary>同模式：旧服务端无 RequestMemberScreenshot 时停重试（截图按需取图·观看端）。
    /// 观看端据此提示"需新版服务端"。</summary>
    private volatile bool _screenshotRequestUnsupported;

    // [P1-F 止血] 自愈定时器：仅在内置重连耗尽（Closed）后启动，每 30s 对同一连接 StartAsync。
    // 同一时刻只允许一条自愈定时器；_selfHealRunning 防止定时器回调重入（StartAsync 超 30s 时）。
    private Timer? _selfHealTimer;
    private int _selfHealRunning;

    public event Action<ControlRoomPlayersUpdate>? OnPlayersUpdated;
    public event Action<RemoteCommand>? OnRemoteCommand;
    public event Action<string>? OnJoinRejected;
    public event Action<bool>? OnConnectionStateChanged;
    /// <summary>收到成员桌面截图帧（嘟嘟可 P5；广播帧或按需应答帧，均按 uid 认领）。</summary>
    public event Action<MemberScreenshotFrame>? OnMemberScreenshot;
    /// <summary>有成员请求我的一帧桌面截图（截图按需取图·被查看端）。参数：requesterUid, requestId。</summary>
    public event Action<string, string>? OnMemberScreenshotRequested;
    /// <summary>收到成员实时日志批（房间日志汇聚；服务端纯转发，含自己的批需按 uid 自滤）。</summary>
    public event Action<MemberLogBatch>? OnMemberLogBatch;
    /// <summary>我的日志订阅数变化（观众驱动上报：0→停发，&gt;0→开始发）。服务端在订阅/退订/订阅者断线时推送。</summary>
    public event Action<int>? OnMemberLogSubscribersChanged;
    /// <summary>有成员请求我的日志文件列表（远程日志下载·被下载端）。参数：requesterUid, requestId。</summary>
    public event Action<string, string>? OnMemberLogFilesRequested;
    /// <summary>收到成员日志文件列表应答（远程日志下载·下载端，按 RequestId 认领）。</summary>
    public event Action<MemberLogFileList>? OnMemberLogFileList;
    /// <summary>有成员请求下载我的某个日志文件（被下载端）。参数：requesterUid, requestId, fileName。</summary>
    public event Action<string, string, string>? OnMemberLogDownloadRequested;
    /// <summary>收到成员日志文件分块（下载端，按 RequestId 认领重组）。</summary>
    public event Action<MemberLogFileChunk>? OnMemberLogFileChunk;
    /// <summary>旧服务端不支持日志订阅（HubException 后置位，新连接重置）。观看端 UI 标注用。</summary>
    public bool LogSubscribeUnsupported => _logSubscribeUnsupported;
    /// <summary>旧服务端不支持远程日志下载（HubException 后置位，新连接重置）。下载端 UI 标注用。</summary>
    public bool LogFileUnsupported => _logFileUnsupported;
    /// <summary>旧服务端不支持截图按需取图（HubException 后置位，新连接重置）。观看端 UI 标注用。</summary>
    public bool ScreenshotRequestUnsupported => _screenshotRequestUnsupported;
    /// <summary>全员就绪确认完成事件（各助手据此启动中断流程）。带 generation 参数，用于幂等保护。</summary>
    public event Action<int>? OnAllReadyConfirmed;
    /// <summary>收到 AllReadyConfirm 事件（服务端要求确认就绪，确认阶段用）。</summary>
    public event Action<int>? OnAllReadyConfirmReceived;
    /// <summary>日志回调（供外部输出探针日志）</summary>
    public Action<string>? OnLog { get; set; }

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote = false, string clientInstanceId = "")
    {
        _roomCode = roomCode;
        _playerUid = playerUid;
        _playerName = playerName;
        _isRemote = isRemote;
        _clientInstanceId = clientInstanceId ?? "";
        _serverUrl = serverUrl;
        _password = password;
        _teamUids = teamUids;
        _screenshotUnsupported = false; // 新连接重置（可能换上了支持截图汇聚的新服务端）
        _screenshotRequestUnsupported = false; // 同上：截图按需取图能力标记
        _logUnsupported = false;        // 同上：日志汇聚能力标记
        _logSubscribeUnsupported = false; // 同上：日志订阅能力标记
        _logFileUnsupported = false;    // 同上：远程日志下载能力标记

        await EstablishAsync(serverUrl, roomCode, password, playerUid, playerName, teamUids, isRemote);
    }

    /// <summary>
    /// 建立一条完整的 SignalR 连接：创建 HubConnection、注册事件、StartAsync、加入控制房间。
    /// 每次调用都会新建 connection，因此事件处理器必须在这里重新注册到最新连接上。
    /// </summary>
    private async Task EstablishAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/hub")
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        connection.On<ControlRoomPlayersUpdate>("ControlRoomPlayersUpdated", update =>
            OnPlayersUpdated?.Invoke(update));
        connection.On<RemoteCommand>("RemoteCommand", cmd =>
            OnRemoteCommand?.Invoke(cmd));
        connection.On<string>("JoinRejected", reason =>
            OnJoinRejected?.Invoke(reason));
        connection.On<int>("AllReady", generation =>
        {
            OnLog?.Invoke("[探针助手] SignalRClient 收到 AllReady 事件, generation=" + generation);
            OnAllReadyConfirmed?.Invoke(generation);
        });
        connection.On<int>("AllReadyConfirm", generation =>
        {
            OnAllReadyConfirmReceived?.Invoke(generation);
        });
        connection.On<MemberScreenshotFrame>("MemberScreenshot", frame =>
            OnMemberScreenshot?.Invoke(frame));
        connection.On<string, string>("MemberScreenshotRequested", (requesterUid, requestId) =>
            OnMemberScreenshotRequested?.Invoke(requesterUid, requestId));
        connection.On<MemberLogBatch>("MemberLogBatch", batch =>
            OnMemberLogBatch?.Invoke(batch));
        connection.On<int>("MemberLogSubscribersChanged", count =>
            OnMemberLogSubscribersChanged?.Invoke(count));
        connection.On<string, string>("MemberLogFilesRequested", (requesterUid, requestId) =>
            OnMemberLogFilesRequested?.Invoke(requesterUid, requestId));
        connection.On<MemberLogFileList>("MemberLogFileList", list =>
            OnMemberLogFileList?.Invoke(list));
        connection.On<string, string, string>("MemberLogDownloadRequested", (requesterUid, requestId, fileName) =>
            OnMemberLogDownloadRequested?.Invoke(requesterUid, requestId, fileName));
        connection.On<MemberLogFileChunk>("MemberLogFileChunk", chunk =>
            OnMemberLogFileChunk?.Invoke(chunk));

        // 重连中（SignalR 内置自动重连尝试期间）
        connection.Reconnecting += _ =>
        {
            System.Diagnostics.Debug.WriteLine("SignalR 重连中...");
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        // SignalR 内置自动重连成功
        connection.Reconnected += async _ =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("SignalR 已重连，重新加入控制房间");
                await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                // Reconnected 是 async void lambda，异常若不在此捕获会冒泡到全局
                // TaskScheduler.UnobservedTaskException → App.xaml.cs 弹"任务异常"框，非常粗暴。
                // 重连成功后 JoinControlRoom 偶发失败（如房间已被服务端回收），仅记日志，等待下次重连。
                System.Diagnostics.Debug.WriteLine($"SignalR 重连后加入房间失败: {ex.Message}");
            }
        };

        // 连接断开。内置自动重连期间会走 Reconnecting/Reconnected，最终耗尽后才来到这里。
        // 注意：异常参数绝不能忽略——SignalR 在 ServerTimeout（30s 无心跳）等场景会把
        //   TimeoutException 传到这里，不"观察"会作为未观察任务异常冒泡到全局
        //   TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框，非常粗暴。
        //   这里吞掉并转状态通知（IsConnected=false，徽章变"离线"）。
        // 血泪教训：自愈绝不能与内置重连并存——曾有过 ReconnectLoopAsync 自愈循环与内置重连并发，
        //   两者竞态：自愈循环在内置重连刚恢复后 Dispose 掉新连接、且重建失败时旧连接已被销毁
        //   → 彻底离线无法控制。因此自愈只允许在 Closed（内置重连 0s/2s/10s/30s 四次尝试耗尽）之后
        //   启动，且始终对同一连接 StartAsync，绝不 Dispose/重建连接。
        connection.Closed += exception =>
        {
            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"SignalR 连接已关闭: {exception.Message}");
            }
            OnConnectionStateChanged?.Invoke(false);
            // [P1-F 止血] 内置重连已耗尽，启动低频自愈：每 30s 对同一连接 StartAsync，
            // 成功后停止自愈并重新入房。避免"网络抖动 >42s 或服务器重启后助手永久离线"。
            StartSelfHeal(connection, roomCode, password, playerUid, playerName, teamUids, isRemote);
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);

        // 必须在 StartAsync + JoinControlRoom 全部成功之后才把 _connection 指向新连接。
        // 若提前赋值、StartAsync 又失败，_connection 会指向"失败的新连接"，
        // 导致自愈循环里 closedConnection != _connection 判断提前 return、放弃重连。
        _connection = connection;
    }

    /// <summary>
    /// [P1-F 止血] Closed（内置重连耗尽）后启动自愈定时器：每 30s 对同一 HubConnection 调 StartAsync。
    /// 成功后停止定时器、触发 OnConnectionStateChanged(true) 并重新 JoinControlRoom（入房失败仅记日志）。
    /// 严禁在内置重连进行期间启动（历史竞态教训，见 Closed 注册处注释）；Closed 即代表内置重连已耗尽，此刻启动安全。
    /// 所有异常在回调内捕获，不得冒泡到 TaskScheduler.UnobservedTaskException。
    /// </summary>
    private void StartSelfHeal(HubConnection closedConnection, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        if (_disposed) return;
        // 同一时刻只允许一条自愈定时器（如对旧连接的残留），先停掉再建
        StopSelfHeal();
        _selfHealTimer = new Timer(async _ =>
        {
            if (Interlocked.CompareExchange(ref _selfHealRunning, 1, 0) != 0) return;
            try
            {
                if (_disposed) return;
                // 连接已被替换（如用户手动 RefreshAsync 重建了新连接）或已恢复，放弃自愈
                if (!ReferenceEquals(closedConnection, _connection)
                    || closedConnection.State == HubConnectionState.Connected)
                {
                    StopSelfHeal();
                    return;
                }
                OnLog?.Invoke("[自愈] SignalR 内置重连已耗尽，尝试重新连接...");
                await closedConnection.StartAsync();
                // 重连成功：停止自愈，恢复在线状态，并重新加入控制房间（复用 Reconnected 的入房逻辑）
                StopSelfHeal();
                OnConnectionStateChanged?.Invoke(true);
                try
                {
                    await closedConnection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);
                    OnLog?.Invoke("[自愈] SignalR 重连成功，已重新加入控制房间");
                }
                catch (Exception ex)
                {
                    // 入房失败（如房间已被服务端回收）仅记日志，连接本身已恢复
                    OnLog?.Invoke($"[自愈] SignalR 重连成功但加入房间失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // 重连失败（服务器仍不可达等），仅记日志，等下一个 30s 周期
                OnLog?.Invoke($"[自愈] SignalR 重连失败: {ex.Message}");
            }
            finally
            {
                _selfHealRunning = 0;
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>停止并释放自愈定时器（幂等）。</summary>
    private void StopSelfHeal()
    {
        var timer = Interlocked.Exchange(ref _selfHealTimer, null);
        timer?.Dispose();
    }

    public async Task SendRemoteCommandAsync(RemoteCommand command)
    {
        if (_connection == null) return;
        command.RoomCode = _roomCode;
        try
        {
            await _connection.InvokeAsync("SendRemoteCommand", command);
        }
        catch (Exception ex)
        {
            // 发送瞬间断连等异常仅记日志不 throw：调用方多在 async void 事件处理器里，上抛会导致进程崩溃
            OnLog?.Invoke($"[探针助手] SendRemoteCommand({command.Cmd}) 调用失败: " + ex.Message);
        }
    }

    public async Task ConfirmAllReadyAsync(int generation)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ConfirmAllReady", generation);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke("[探针助手] ConfirmAllReady 调用失败: " + ex.Message);
        }
    }

    public async Task ReportControlStatusAsync(ControlStatus status)
    {
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected)
        {
            return; // 连接未就绪时静默跳过，避免连接断开瞬间大量并发调用被取消产生"状态上报失败"日志风暴
        }
        try
        {
            status.RoomCode = _roomCode;
            await _connection.InvokeAsync("ReportControlStatus", status);
        }
        catch (Exception ex)
        {
            // 连接断开时 InvokeAsync 会抛异常（TaskCanceledException / InvalidOperationException），
            // 仅记一条日志不再 throw；调用方 ReportStatusAsync 已有 catch 兜底，但门控+不 throw 可避免日志风暴。
            OnLog?.Invoke($"ReportControlStatusAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>上报上线事件（带 generation 代序号，供服务端状态机边沿检测）。</summary>
    public async Task ReportOnlineEventAsync(int generation, bool isOnlineReady)
    {
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected)
        {
            OnLog?.Invoke($"ReportOnlineEventAsync 跳过: 连接未就绪（State={_connection.State}）");
            return;
        }
        try
        {
            await _connection.InvokeAsync("ReportOnlineEvent", generation, isOnlineReady);
        }
        catch (Exception ex)
        {
            // 连接断开时 InvokeAsync 会抛异常（TaskCanceledException / InvalidOperationException），
            // 此处仅记日志不再 throw，避免上游调用方（如 ReportStatusAsync）连锁打印大量"状态上报失败"日志形成风暴。
            // 断线状态已由 Closed 事件同步 IsConnected=false，调用方也可通过 IsConnected 自行判断。
            OnLog?.Invoke($"ReportOnlineEventAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>清除指定成员的 OnlineHistory（已联机记录），由本人或房主调用。</summary>
    public async Task ClearOnlineHistoryAsync(string targetUid)
    {
        if (_connection == null)
        {
            OnLog?.Invoke("[清除记录] 清除失败: SignalR 未连接（_connection == null）");
            return;
        }
        try
        {
            await _connection.InvokeAsync("ClearOnlineHistory", targetUid);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[清除记录] ClearOnlineHistoryAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>上报本机桌面截图帧（旧版广播路径，保留兼容；新代码按需取图请用 ReportMemberScreenshotExAsync）。
    /// 未连接/未入房时静默跳过；旧服务端无此 Hub 方法时首次 HubException 后停重试（不反复刷失败日志）。</summary>
    public async Task ReportMemberScreenshotAsync(string jpegBase64, int width, int height, DateTime capturedAt)
    {
        if (_screenshotUnsupported) return;
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("ReportMemberScreenshot", _roomCode, _playerUid, jpegBase64, width, height, capturedAt);
        }
        catch (HubException ex)
        {
            // HubException = 服务端明确拒绝（旧服务端没有该方法）——标记后不再重试，新连接时重置
            _screenshotUnsupported = true;
            OnLog?.Invoke($"ReportMemberScreenshot 被服务端拒绝（疑似旧服务端不支持截图汇聚），本次连接内停止上报: {ex.Message}");
        }
        catch (Exception ex)
        {
            // 上报失败（断线等）仅记日志，截图汇聚是尽力而为的辅助通道
            OnLog?.Invoke($"ReportMemberScreenshotAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>请求目标成员的一帧桌面截图（截图按需取图·观看端）。未连接/未入房时静默跳过；
    /// 旧服务端无此 Hub 方法时首次 HubException 后停重试（同 _screenshotUnsupported 模式）。</summary>
    public async Task RequestMemberScreenshotAsync(string targetUid, string requestId)
    {
        if (_screenshotRequestUnsupported) return;
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("RequestMemberScreenshot", _roomCode, targetUid, requestId);
        }
        catch (HubException ex)
        {
            _screenshotRequestUnsupported = true;
            OnLog?.Invoke($"RequestMemberScreenshot 被服务端拒绝（疑似旧服务端不支持按需取图），本次连接内停止请求: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"RequestMemberScreenshotAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>应答成员的截图请求，带 requestId 上报一帧（截图按需取图·被查看端），服务端按映射单播回请求方。
    /// 未连接/未入房时静默跳过；旧服务端无此 Hub 方法时首次 HubException 后停重试（复用 _screenshotRequestUnsupported 标记）。</summary>
    public async Task ReportMemberScreenshotExAsync(string jpegBase64, int width, int height, DateTime capturedAt, string requestId)
    {
        if (_screenshotRequestUnsupported) return;
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("ReportMemberScreenshotEx", _roomCode, _playerUid, jpegBase64, width, height, capturedAt, requestId);
        }
        catch (HubException ex)
        {
            _screenshotRequestUnsupported = true;
            OnLog?.Invoke($"ReportMemberScreenshotEx 被服务端拒绝（疑似旧服务端不支持按需取图），本次连接内停止应答: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"ReportMemberScreenshotExAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>上报本机实时日志批（房间日志汇聚）。未连接/未入房时静默跳过；
    /// 旧服务端无此 Hub 方法时首次 HubException 后停重试（同 _screenshotUnsupported 模式）。
    /// infoOnly：发送端开启了省流（仅 INF+），随批带给观看端做状态提示。</summary>
    public async Task ReportMemberLogBatchAsync(List<string> lines, bool infoOnly)
    {
        if (_logUnsupported) return;
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected) return;
        if (lines.Count == 0) return;
        try
        {
            await _connection.InvokeAsync("ReportMemberLogBatch", _roomCode, _playerUid, _playerName, lines, infoOnly);
        }
        catch (HubException ex)
        {
            _logUnsupported = true;
            OnLog?.Invoke($"ReportMemberLogBatch 被服务端拒绝（疑似旧服务端不支持日志汇聚），本次连接内停止上报: {ex.Message}");
        }
        catch (Exception ex)
        {
            // 尽力而为通道：断线等失败仅记日志
            OnLog?.Invoke($"ReportMemberLogBatchAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>订阅某成员的实时日志流（观众驱动）。未连接静默跳过；旧服务端 HubException 后停重试。</summary>
    public async Task SubscribeMemberLogAsync(string targetUid)
    {
        if (_logSubscribeUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("SubscribeMemberLog", _roomCode, targetUid);
        }
        catch (HubException ex)
        {
            _logSubscribeUnsupported = true;
            OnLog?.Invoke($"SubscribeMemberLog 被服务端拒绝（疑似旧服务端不支持按需订阅），本次连接内停止尝试: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"SubscribeMemberLogAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>退订某成员的实时日志流。未连接/旧服务端静默跳过（服务端断线清理兜底）。</summary>
    public async Task UnsubscribeMemberLogAsync(string targetUid)
    {
        if (_logSubscribeUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("UnsubscribeMemberLog", _roomCode, targetUid);
        }
        catch (HubException)
        {
            _logSubscribeUnsupported = true; // 不需要再尝试退订
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"UnsubscribeMemberLogAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>请求目标成员的日志文件列表（远程日志下载·观众端）。requestId 由调用方生成（Guid.N），应答按它认领。
    /// 旧服务端无此 Hub 方法时首次 HubException 后停重试（同 _screenshotUnsupported 模式）。</summary>
    public async Task RequestMemberLogFilesAsync(string targetUid, string requestId)
    {
        if (_logFileUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("RequestMemberLogFiles", _roomCode, targetUid, requestId);
        }
        catch (HubException ex)
        {
            _logFileUnsupported = true;
            OnLog?.Invoke($"RequestMemberLogFiles 被服务端拒绝（疑似旧服务端不支持远程日志下载），本次连接内停止尝试: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"RequestMemberLogFilesAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>应答日志文件列表（被下载端）。未连接静默跳过。</summary>
    public async Task ReportMemberLogFilesAsync(string requestId, List<MemberLogFileDescriptor> files)
    {
        if (_logFileUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("ReportMemberLogFiles", _roomCode, _playerUid, requestId, files);
        }
        catch (HubException)
        {
            _logFileUnsupported = true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"ReportMemberLogFilesAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>请求下载目标成员的某个日志文件（观众端）。fileName 白名单由服务端与目标端双重校验。</summary>
    public async Task RequestMemberLogDownloadAsync(string targetUid, string requestId, string fileName)
    {
        if (_logFileUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("RequestMemberLogDownload", _roomCode, targetUid, requestId, fileName);
        }
        catch (HubException ex)
        {
            _logFileUnsupported = true;
            OnLog?.Invoke($"RequestMemberLogDownload 被服务端拒绝（疑似旧服务端不支持远程日志下载），本次连接内停止尝试: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"RequestMemberLogDownloadAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>分块上行日志文件（被下载端，gzip+base64）。未连接静默跳过（观众端超时兜底）。</summary>
    public async Task ReportMemberLogChunkAsync(string requestId, int seq, int totalChunks,
        string chunkBase64, string fileName, bool done)
    {
        if (_logFileUnsupported) return;
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;
        try
        {
            await _connection.InvokeAsync("ReportMemberLogChunk",
                _roomCode, _playerUid, requestId, seq, totalChunks, chunkBase64, fileName, done);
        }
        catch (HubException)
        {
            _logFileUnsupported = true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"ReportMemberLogChunkAsync 调用失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        // 连接对象 Dispose 时一并停掉自愈定时器，避免对已释放连接 StartAsync
        StopSelfHeal();
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}