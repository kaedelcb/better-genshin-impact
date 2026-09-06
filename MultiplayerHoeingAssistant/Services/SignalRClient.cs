using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services.Gateway;

using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 控制房间 SignalR 客户端（切片 9 已换芯：/hub 旧协议 → /gateway v3 信封，
/// 传输层委托 <see cref="MhaGatewayClient"/>，公开面零变化）。
/// 迁移依据：服务器 GatewayProtocol.LegacyMethodMap/LegacyEventMap（control.* 6 + log.* 7 + screenshot.* 3
/// 方法、13 事件全覆盖）；血泪语义（P1-F 断线自愈、断线门控不 throw、_clientInstanceId、
/// ControlRoomPlayersUpdated 全量/增量两态）逐条保留，见各方法注释。
/// 2026-09-06 新增第四条恢复机制：入房重试（StartRejoinRetry）——内置重连（Reconnecting 窗口内）
/// 与 P1-F 自愈（Closed 后）都以传输层状态为介入条件，重连成功后 hello/入房失败时两者都不会
/// 再介入（传输层已 Connected），业务层必须自己兜底重试入房，否则永久"徽章离线 + 零日志"。
/// </summary>
public class SignalRClient : IAsyncDisposable
{
    private MhaGatewayClient? _gateway;
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

    /// <summary>URL 归一化告警去重（按原始配置值）：10s 重试循环场景防告警刷屏。</summary>
    private string? _lastUrlWarned;

    /// <summary>服务端不支持 screenshot.report（首次 GatewayErrorException 后标记，停止重试；
    /// 新连接建立时重置，升级服务端后自动恢复）。volatile：Timer 线程读、调用线程写。</summary>
    private volatile bool _screenshotUnsupported;
    /// <summary>同 _screenshotUnsupported：服务端无 log.reportBatch 时停重试（房间实时日志汇聚）。</summary>
    private volatile bool _logUnsupported;
    /// <summary>同模式：服务端无 log.subscribe/log.unsubscribe 时停重试（日志按需订阅）。
    /// 观看端据此给远程来源项标注"（需新版服务端）"。</summary>
    private volatile bool _logSubscribeUnsupported;
    /// <summary>同模式：服务端无 log.requestFiles 等日志下载方法时停重试（远程日志下载）。
    /// 下载端 UI 据此标注"需新版服务端"。</summary>
    private volatile bool _logFileUnsupported;
    /// <summary>同模式：服务端无 screenshot.request 时停重试（截图按需取图·观看端）。
    /// 观看端据此提示"需新版服务端"。</summary>
    private volatile bool _screenshotRequestUnsupported;

    // [P1-F 止血] 自愈定时器：仅在内置重连耗尽（Closed）后启动，每 30s 对同一连接 StartAsync。
    // 同一时刻只允许一条自愈定时器；_selfHealRunning 防止定时器回调重入（StartAsync 超 30s 时）。
    private Timer? _selfHealTimer;
    private int _selfHealRunning;

    // [入房重试] 传输层已 Connected 但 hello/入房失败时的业务层兜底定时器（每 10s 重试入房）。
    // 此场景下内置重连与自愈都不会再介入，没有它会永久卡在"徽章离线 + 状态上报被服务端静默丢弃"。
    private Timer? _rejoinTimer;
    private int _rejoinRunning;

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
    /// <summary>旧服务端不支持日志订阅（error 响应后置位，新连接重置）。观看端 UI 标注用。</summary>
    public bool LogSubscribeUnsupported => _logSubscribeUnsupported;
    /// <summary>旧服务端不支持远程日志下载（error 响应后置位，新连接重置）。下载端 UI 标注用。</summary>
    public bool LogFileUnsupported => _logFileUnsupported;
    /// <summary>旧服务端不支持截图按需取图（error 响应后置位，新连接重置）。观看端 UI 标注用。</summary>
    public bool ScreenshotRequestUnsupported => _screenshotRequestUnsupported;
    /// <summary>全员就绪确认完成事件（各助手据此启动中断流程）。带 generation 参数，用于幂等保护。</summary>
    public event Action<int>? OnAllReadyConfirmed;
    /// <summary>收到 AllReadyConfirm 事件（服务端要求确认就绪，确认阶段用）。</summary>
    public event Action<int>? OnAllReadyConfirmReceived;
    /// <summary>日志回调（供外部输出探针日志）</summary>
    public Action<string>? OnLog { get; set; }

    public bool IsConnected => _gateway?.IsConnected == true;

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
    /// 建立一条完整的网关连接：创建 MhaGatewayClient、注册事件分发与生命周期回调、
    /// StartAsync + session.hello、加入控制房间。
    /// 每次调用都会新建 gateway 实例，因此事件处理器必须在这里重新注册到最新实例上。
    /// </summary>
    private async Task EstablishAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        // URL 归一化（《通信方案》§4.8，切片 8 同款咽喉姿势）：配置只填基地址，SDK 内部拼 /gateway。
        // 旧配置带 /hub 尾巴的剥掉并告警一次（按原始配置值去重，防 10s 重试循环刷屏）。
        var baseUrl = MhaGatewayClient.NormalizeBaseUrl(serverUrl, out var stripped);
        if (stripped && !string.Equals(_lastUrlWarned, serverUrl, StringComparison.Ordinal))
        {
            _lastUrlWarned = serverUrl;
            OnLog?.Invoke("[连接] 配置的服务器地址带旧格式 /hub 尾巴，已自动归一化为基地址（新协议固定走 /gateway）");
        }

        var gateway = new MhaGatewayClient();

        gateway.EnvelopeReceived += DispatchEvt;

        // 重连中（SignalR 内置自动重连尝试期间）
        gateway.Reconnecting += error =>
        {
            // 必须落 OnLog：此前只有 Debug.WriteLine，断线时 UI/文件零痕迹，
            // 出现"徽章离线但日志无任何提示"的不可排查状态
            OnLog?.Invoke(error != null
                ? $"[连接] SignalR 连接断开，正在自动重连: {error.Message}"
                : "[连接] SignalR 连接断开，正在自动重连...");
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        // SignalR 内置自动重连成功
        gateway.Reconnected += async _ =>
        {
            try
            {
                OnLog?.Invoke("[连接] SignalR 已重连，重新加入控制房间");
                await ResumeAfterReconnectAsync(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                // Reconnected 是 async void lambda，异常若不在此捕获会冒泡到全局
                // TaskScheduler.UnobservedTaskException → App.xaml.cs 弹"任务异常"框，非常粗暴。
                // 重连成功后 hello/入房偶发失败（如房间已被服务端回收）：此时传输层已 Connected，
                // 内置重连与 P1-F 自愈都不会再介入；若仅 Debug.WriteLine 吞掉（历史 bug），
                // 会永久卡在"徽章离线 + 零日志"，且状态上报在服务端按旧 ConnectionId 找不到人
                // 被静默丢弃。必须记日志、显式置离线，并启动入房重试兜底。
                OnLog?.Invoke($"[连接] SignalR 重连成功但重新加入控制房间失败: {ex.Message}（10 秒后重试入房）");
                OnConnectionStateChanged?.Invoke(false);
                StartRejoinRetry(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
            }
        };

        // 连接断开。内置自动重连期间会走 Reconnecting/Reconnected，最终耗尽后才来到这里。
        // 注意：异常参数绝不能忽略——SignalR 在 ServerTimeout（30s 无心跳）等场景会把
        //   TimeoutException 传到这里，不"观察"会作为未观察任务异常冒泡到全局
        //   TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框，非常粗暴。
        //   这里读取 exception.Message 记日志并转状态通知（IsConnected=false，徽章变"离线"）。
        // 血泪教训：自愈绝不能与内置重连并存——曾有过 ReconnectLoopAsync 自愈循环与内置重连并发，
        //   两者竞态：自愈循环在内置重连刚恢复后 Dispose 掉新连接、且重建失败时旧连接已被销毁
        //   → 彻底离线无法控制。因此自愈只允许在 Closed（内置重连 0s/2s/10s/30s 四次尝试耗尽）之后
        //   启动，且始终对同一连接 StartAsync，绝不 Dispose/重建连接。
        gateway.Closed += exception =>
        {
            OnLog?.Invoke(exception != null
                ? $"[连接] SignalR 连接已关闭: {exception.Message}（自动重连已耗尽，将每 30 秒尝试自愈）"
                : "[连接] SignalR 连接已关闭（自动重连已耗尽，将每 30 秒尝试自愈）");
            OnConnectionStateChanged?.Invoke(false);
            // [P1-F 止血] 内置重连已耗尽，启动低频自愈：每 30s 对同一连接 StartAsync，
            // 成功后停止自愈并重新入房。避免"网络抖动 >42s 或服务器重启后助手永久离线"。
            StartSelfHeal(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
            return Task.CompletedTask;
        };

        try
        {
            // 连接 + session.hello 握手（DAP 时序：握手完成前服务端拒绝其它消息）
            await gateway.ConnectAsync(baseUrl);
            await JoinControlRoomAsync(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
        }
        catch
        {
            // 首连失败：释放半成品实例，异常原样冒泡给 MainViewModel（记日志 + 10s 重试定时器）
            await gateway.DisposeAsync();
            throw;
        }

        // 必须在 StartAsync + hello + JoinControlRoom 全部成功之后才把 _gateway 指向新实例。
        // 若提前赋值、StartAsync 又失败，_gateway 会指向"失败的新实例"，
        // 导致自愈循环里 closedGateway != _gateway 判断提前 return、放弃重连。
        var old = _gateway;
        _gateway = gateway;
        // 旧实例（上次失败/掉线残留）在新实例就位后释放，避免双连接并发收发
        if (old != null)
        {
            await old.DisposeAsync();
        }
    }

    /// <summary>加入控制房间（v3：control.joinRoom）。三处调用点：首连 EstablishAsync、
    /// 内置重连 Reconnected、P1-F 自愈成功后；每处调用前均已先完成 session.hello。
    /// clientInstanceId 语义保留（多实例/重连去重）。JoinRejected 走 evt 事件不走响应，与旧协议一致。</summary>
    private Task JoinControlRoomAsync(MhaGatewayClient gateway, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
        => gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlJoinRoom, new
        {
            roomCode,
            password,
            playerUid,
            playerName,
            allowedUids = teamUids, // 服务器路由键名是 allowedUids（GatewayDispatcher.ControlRoom.cs）
            isRemote,
            clientInstanceId = _clientInstanceId
        });

    /// <summary>
    /// evt 信封 → 13 个 C# 事件分发（迁移自旧 13 个 connection.On 订阅，
    /// 映射依据 = 服务器 GatewayProtocol.LegacyEventMap + RoomOperations 广播点实读的 evt payload 形状）。
    /// 未知名静默忽略（与旧协议"未订阅的事件名天然忽略"等价）；解析异常就地捕获，绝不外抛回流 SignalR 管线。
    /// </summary>
    private void DispatchEvt(GatewayEnvelope env)
    {
        try
        {
            switch (env.Name)
            {
                case GatewayProtocol.Events.ControlPlayersUpdated:
                {
                    // payload 即 ControlRoomPlayersUpdate 本体（全量/增量两态，da5df45d 带宽优化）；
                    // 两态合并逻辑在 MainViewModel，原样保留不动
                    var update = env.DeserializePayload<ControlRoomPlayersUpdate>();
                    if (update != null) OnPlayersUpdated?.Invoke(update);
                    break;
                }
                case GatewayProtocol.Events.ControlRemoteCommand:
                {
                    var cmd = env.Get<RemoteCommand>("command");
                    if (cmd != null) OnRemoteCommand?.Invoke(cmd);
                    break;
                }
                case GatewayProtocol.Events.ControlJoinRejected:
                    OnJoinRejected?.Invoke(env.GetString("reason"));
                    break;
                case GatewayProtocol.Events.ControlAllReady:
                {
                    var generation = env.GetInt("generation");
                    OnLog?.Invoke("[探针助手] SignalRClient 收到 AllReady 事件, generation=" + generation);
                    OnAllReadyConfirmed?.Invoke(generation);
                    break;
                }
                case GatewayProtocol.Events.ControlAllReadyConfirm:
                    OnAllReadyConfirmReceived?.Invoke(env.GetInt("generation"));
                    break;
                case GatewayProtocol.Events.ScreenshotMember:
                {
                    // payload 即帧本体 {uid,jpegBase64,width,height,capturedAt}
                    var frame = env.DeserializePayload<MemberScreenshotFrame>();
                    if (frame != null) OnMemberScreenshot?.Invoke(frame);
                    break;
                }
                case GatewayProtocol.Events.ScreenshotRequested:
                    OnMemberScreenshotRequested?.Invoke(env.GetString("requesterUid"), env.GetString("requestId"));
                    break;
                case GatewayProtocol.Events.LogBatch:
                {
                    // payload 即批本体 {uid,senderName,lines,infoOnly,serverTime}
                    var batch = env.DeserializePayload<MemberLogBatch>();
                    if (batch != null) OnMemberLogBatch?.Invoke(batch);
                    break;
                }
                case GatewayProtocol.Events.LogSubscribersChanged:
                    OnMemberLogSubscribersChanged?.Invoke(env.GetInt("count"));
                    break;
                case GatewayProtocol.Events.LogFilesRequested:
                    OnMemberLogFilesRequested?.Invoke(env.GetString("requesterUid"), env.GetString("requestId"));
                    break;
                case GatewayProtocol.Events.LogFileList:
                {
                    // payload 即应答本体 {uid,requestId,files}
                    var list = env.DeserializePayload<MemberLogFileList>();
                    if (list != null) OnMemberLogFileList?.Invoke(list);
                    break;
                }
                case GatewayProtocol.Events.LogDownloadRequested:
                    OnMemberLogDownloadRequested?.Invoke(env.GetString("requesterUid"), env.GetString("requestId"), env.GetString("fileName"));
                    break;
                case GatewayProtocol.Events.LogFileChunk:
                {
                    // payload 即块本体 {uid,requestId,seq,totalChunks,chunkBase64,fileName,done}
                    var chunk = env.DeserializePayload<MemberLogFileChunk>();
                    if (chunk != null) OnMemberLogFileChunk?.Invoke(chunk);
                    break;
                }
                // default：未知名静默忽略（双发期服务器只向 v3 连接发 evt，无旧名混入）
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[探针助手] evt 事件分发失败（已吞掉）: {env.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// [P1-F 止血] Closed（内置重连耗尽）后启动自愈定时器：每 30s 对同一连接调 StartAsync。
    /// 成功后停止定时器、重新 hello + 加入控制房间，全部成功才触发 OnConnectionStateChanged(true)；
    /// 入房失败转 StartRejoinRetry 兜底（否则徽章假在线、状态上报被服务端静默丢弃）。
    /// 严禁在内置重连进行期间启动（历史竞态教训，见 Closed 注册处注释）；Closed 即代表内置重连已耗尽，此刻启动安全。
    /// 所有异常在回调内捕获，不得冒泡到 TaskScheduler.UnobservedTaskException。
    /// </summary>
    private void StartSelfHeal(MhaGatewayClient closedGateway, string roomCode, string password,
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
                if (!ReferenceEquals(closedGateway, _gateway)
                    || closedGateway.IsConnected)
                {
                    StopSelfHeal();
                    return;
                }
                OnLog?.Invoke("[自愈] SignalR 内置重连已耗尽，尝试重新连接...");
                await closedGateway.ReconnectStartAsync();
                // 重连成功：停止自愈，重新 hello + 加入控制房间，全部成功才恢复在线状态
                StopSelfHeal();
                try
                {
                    await ResumeAfterReconnectAsync(closedGateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
                    OnLog?.Invoke("[自愈] SignalR 重连成功，已重新加入控制房间");
                    OnConnectionStateChanged?.Invoke(true);
                }
                catch (Exception ex)
                {
                    // 入房失败（如房间已被服务端回收）：传输层已恢复但业务层未入房，
                    // 显式置离线并启动入房重试兜底（否则徽章假在线、状态上报被服务端静默丢弃）
                    OnLog?.Invoke($"[自愈] SignalR 重连成功但加入房间失败: {ex.Message}（10 秒后重试入房）");
                    OnConnectionStateChanged?.Invoke(false);
                    StartRejoinRetry(closedGateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
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

    /// <summary>
    /// 重连/自愈成功后的业务恢复：重新 hello + 加入控制房间。
    /// v3 必需：重连后 connectionId 变更，服务端会话跟踪视为未握手，
    /// 不重新 hello 会被 handshake_required 拒绝（DAP 时序）。失败原样上抛，由调用方兜底。
    /// </summary>
    private async Task ResumeAfterReconnectAsync(MhaGatewayClient gateway, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        await gateway.HelloAsync();
        await JoinControlRoomAsync(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
    }

    /// <summary>
    /// [入房重试] 传输层已 Connected 但 hello/入房失败时的业务层兜底：每 10s 重试 hello + 入房，
    /// 成功后停止并恢复在线状态。连接被替换或传输层再次断开时立即放弃——
    /// 恢复职责交回内置重连（Reconnecting）/自愈（Closed），避免两路并发恢复。
    /// 所有异常在回调内捕获，不得冒泡到 TaskScheduler.UnobservedTaskException。
    /// </summary>
    private void StartRejoinRetry(MhaGatewayClient gateway, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        if (_disposed) return;
        // 同一时刻只允许一条入房重试定时器，先停掉再建
        StopRejoinRetry();
        _rejoinTimer = new Timer(async _ =>
        {
            if (Interlocked.CompareExchange(ref _rejoinRunning, 1, 0) != 0) return;
            try
            {
                if (_disposed) return;
                // 连接已被替换（手动 RefreshAsync / 新一轮 EstablishAsync）：旧连接的入房重试无意义
                if (!ReferenceEquals(gateway, _gateway))
                {
                    StopRejoinRetry();
                    return;
                }
                // 传输层又断了：停止入房重试，恢复职责交回内置重连/自愈机制
                if (!gateway.IsConnected)
                {
                    StopRejoinRetry();
                    return;
                }
                await ResumeAfterReconnectAsync(gateway, roomCode, password, playerUid, playerName, teamUids, isRemote);
                StopRejoinRetry();
                OnLog?.Invoke("[连接] 重新加入控制房间成功，已恢复在线");
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                // 入房仍失败（房间仍不存在等），仅记日志，等下一个 10s 周期
                OnLog?.Invoke($"[连接] 重新加入控制房间失败: {ex.Message}（10 秒后重试）");
            }
            finally
            {
                _rejoinRunning = 0;
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>停止并释放入房重试定时器（幂等）。</summary>
    private void StopRejoinRetry()
    {
        var timer = Interlocked.Exchange(ref _rejoinTimer, null);
        timer?.Dispose();
    }

    public async Task SendRemoteCommandAsync(RemoteCommand command)
    {
        if (_gateway == null) return;
        command.RoomCode = _roomCode;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlSendCommand, new { command });
        }
        catch (Exception ex)
        {
            // 发送瞬间断连等异常仅记日志不 throw：调用方多在 async void 事件处理器里，上抛会导致进程崩溃
            OnLog?.Invoke($"[探针助手] SendRemoteCommand({command.Cmd}) 调用失败: " + ex.Message);
        }
    }

    public async Task ConfirmAllReadyAsync(int generation)
    {
        if (_gateway == null) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlConfirmAllReady, new { generation });
        }
        catch (Exception ex)
        {
            OnLog?.Invoke("[探针助手] ConfirmAllReady 调用失败: " + ex.Message);
        }
    }

    public async Task ReportControlStatusAsync(ControlStatus status)
    {
        if (_gateway == null) return;
        if (!_gateway.IsConnected)
        {
            return; // 连接未就绪时静默跳过，避免连接断开瞬间大量并发调用被取消产生"状态上报失败"日志风暴
        }
        try
        {
            status.RoomCode = _roomCode;
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlReportStatus, new { status });
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
        if (_gateway == null) return;
        if (!_gateway.IsConnected)
        {
            OnLog?.Invoke($"ReportOnlineEventAsync 跳过: 连接未就绪（State={_gateway.ConnectionState?.ToString() ?? "null"}）");
            return;
        }
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlReportOnlineEvent, new { generation, isOnlineReady });
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
        if (_gateway == null)
        {
            OnLog?.Invoke("[清除记录] 清除失败: SignalR 未连接（_connection == null）");
            return;
        }
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ControlClearOnlineHistory, new { targetUid });
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[清除记录] ClearOnlineHistoryAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>上报本机桌面截图帧（旧版广播路径，保留兼容；新代码按需取图请用 ReportMemberScreenshotExAsync）。
    /// 未连接/未入房时静默跳过；服务端无此消息名时首次 GatewayErrorException 后停重试（不反复刷失败日志）。</summary>
    public async Task ReportMemberScreenshotAsync(string jpegBase64, int width, int height, DateTime capturedAt)
    {
        if (_screenshotUnsupported) return;
        if (_gateway == null) return;
        if (!_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ScreenshotReport,
                new { roomCode = _roomCode, uid = _playerUid, jpegBase64, width, height, capturedAt });
        }
        catch (GatewayErrorException ex)
        {
            // GatewayErrorException = 服务端明确拒绝（无此消息名/参数错误）——标记后不再重试，新连接时重置
            // （对齐旧协议 catch (HubException) 语义位置）
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
    /// 服务端无此消息名时首次 GatewayErrorException 后停重试（同 _screenshotUnsupported 模式）。</summary>
    public async Task RequestMemberScreenshotAsync(string targetUid, string requestId)
    {
        if (_screenshotRequestUnsupported) return;
        if (_gateway == null) return;
        if (!_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ScreenshotRequest,
                new { roomCode = _roomCode, targetUid, requestId });
        }
        catch (GatewayErrorException ex)
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
    /// 未连接/未入房时静默跳过；服务端无此消息名时首次 GatewayErrorException 后停重试（复用 _screenshotRequestUnsupported 标记）。</summary>
    public async Task ReportMemberScreenshotExAsync(string jpegBase64, int width, int height, DateTime capturedAt, string requestId)
    {
        if (_screenshotRequestUnsupported) return;
        if (_gateway == null) return;
        if (!_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ScreenshotReportEx,
                new { roomCode = _roomCode, uid = _playerUid, jpegBase64, width, height, capturedAt, requestId });
        }
        catch (GatewayErrorException ex)
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
    /// 服务端无此消息名时首次 GatewayErrorException 后停重试（同 _screenshotUnsupported 模式）。
    /// infoOnly：发送端开启了省流（仅 INF+），随批带给观看端做状态提示。</summary>
    public async Task ReportMemberLogBatchAsync(List<string> lines, bool infoOnly)
    {
        if (_logUnsupported) return;
        if (_gateway == null) return;
        if (!_gateway.IsConnected) return;
        if (lines.Count == 0) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogReportBatch,
                new { roomCode = _roomCode, uid = _playerUid, senderName = _playerName, lines, infoOnly });
        }
        catch (GatewayErrorException ex)
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

    /// <summary>订阅某成员的实时日志流（观众驱动）。未连接静默跳过；服务端拒绝后停重试。</summary>
    public async Task SubscribeMemberLogAsync(string targetUid)
    {
        if (_logSubscribeUnsupported) return;
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogSubscribe,
                new { roomCode = _roomCode, targetUid });
        }
        catch (GatewayErrorException ex)
        {
            _logSubscribeUnsupported = true;
            OnLog?.Invoke($"SubscribeMemberLog 被服务端拒绝（疑似旧服务端不支持按需订阅），本次连接内停止尝试: {ex.Message}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"SubscribeMemberLogAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>退订某成员的实时日志流。未连接/服务端不支持时静默跳过（服务端断线清理兜底）。</summary>
    public async Task UnsubscribeMemberLogAsync(string targetUid)
    {
        if (_logSubscribeUnsupported) return;
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogUnsubscribe,
                new { roomCode = _roomCode, targetUid });
        }
        catch (GatewayErrorException)
        {
            _logSubscribeUnsupported = true; // 不需要再尝试退订
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"UnsubscribeMemberLogAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>请求目标成员的日志文件列表（远程日志下载·观众端）。requestId 由调用方生成（Guid.N），应答按它认领。
    /// 服务端无此消息名时首次 GatewayErrorException 后停重试（同 _screenshotUnsupported 模式）。</summary>
    public async Task RequestMemberLogFilesAsync(string targetUid, string requestId)
    {
        if (_logFileUnsupported) return;
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogRequestFiles,
                new { roomCode = _roomCode, targetUid, requestId });
        }
        catch (GatewayErrorException ex)
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
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogReportFiles,
                new { roomCode = _roomCode, uid = _playerUid, requestId, files });
        }
        catch (GatewayErrorException)
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
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogRequestDownload,
                new { roomCode = _roomCode, targetUid, requestId, fileName });
        }
        catch (GatewayErrorException ex)
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
        if (_gateway == null || !_gateway.IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.LogReportChunk,
                new { roomCode = _roomCode, uid = _playerUid, requestId, seq, totalChunks, chunkBase64, fileName, done });
        }
        catch (GatewayErrorException)
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
        // 连接对象 Dispose 时一并停掉自愈/入房重试定时器，避免对已释放连接 StartAsync/入房
        StopSelfHeal();
        StopRejoinRetry();
        var gateway = _gateway;
        _gateway = null;
        if (gateway != null)
        {
            await gateway.DisposeAsync();
        }
    }
}
