#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// SignalR 客户端：负责与服务端通信。
/// 简化版：移除旧协调机制（MemberStatus、RouteSkipped、WaitPointReport、AbortAndRealign、RouteEnforceSync），
/// 保留基础连接、心跳、进度查询、异常通知等新机制所需功能。
///
/// 切片 8（BGI 侧 v3 迁移）：通信全部改走新协议（/gateway + evt 信封），由
/// <see cref="BgiGatewayClient"/> 承载传输（session.hello 握手、Dispatch/Query 两入口、evt 单订阅分发）。
/// 本类公开面（23 个事件 / 全部方法签名 / 属性 / 断线自愈语义）与迁移前逐字等价，
/// 变化只在"线上消息形状"：65 个旧 Hub 方法 → 网关消息名（映射表见 GatewayProtocol 与服务器
/// GatewayProtocol.LegacyMethodMap/LegacyEventMap）。
/// </summary>
public class CoordinatorClient : IAsyncDisposable
{
    private readonly ILogger<CoordinatorClient> _logger = App.GetLogger<CoordinatorClient>();
    private BgiGatewayClient? _gateway;
    private Timer? _heartbeatTimer;

    // === 测试种子（[InternalsVisibleTo("BetterGenshinImpact.UnitTest")]）===
    // 抽出 NotifyKazuhaCollectStartedAsync 内的发送动作，让 PBT 可注入 fake 代理断言
    // "客户端 IsValid 守卫不让 NaN/Inf/(0,0) 进入 SignalR 序列化路径"。
    // 仅作用于 NotifyKazuhaCollectStartedAsync；其他 Notify*Async 仍直接调网关原语不变。
    // 详见 spec kazuha-collect-point-nan-signalr-serialization-fix。
    //
    // 切片 8 说明：种子签名保持旧线形（Hub 方法名 + 逐参数组）不变，默认实现负责把
    // ("NotifyKazuhaCollectStarted", [syncKey, collectX, collectY]) 翻译成 v3 信封
    // kazuha.notifyCollectStarted 再经网关发出——历史教训沿用：SignalR 扩展方法没有
    // (string, object?[], CancellationToken) 重载，逐参展开必须走正确的多参入口。
    internal Func<string, object?[], Task> _invokeHubAsync;

    // 测试种子：单元测试可强制 IsConnected==true，绕开真实的网关连接。
    // 仅在测试中赋值；生产路径下保持 null → IsConnected fallback 到网关连接状态。
    internal bool? _testIsConnectedOverride;

    // 保存房间信息用于重连
    private string? _currentRoomCode;
    private string? _playerName;
    private string? _playerUid;

    // === 路线进度信息（BUG 1：保留用于线路协调检查）===
    private int _currentRouteIndex = -1;
    private DateTime _routeStartTime;
    private double _routeEstimatedSeconds;

    // === SignalR 断线重连 ===
    private volatile bool _isReconnecting;
    private volatile bool _isInRoom;

    private WorldStateMonitor? _worldStateMonitor;

    // === 玩家名称缓存（用于日志显示）===
    private readonly ConcurrentDictionary<string, string> _playerNameCache = new();

    public event Action<List<PlayerInfo>>? PlayerListUpdated;
    public event Action<string>? AllArrived;
    public event Action<string>? AllFightDone;
    public event Action<List<string>>? RouteDiffReceived;
    public event Action? RouteVerificationPassed;
    public event Action? OnDegraded;
    public event Action<string>? RoomClosed;
    public event Action? RouteVerificationAllDone;

    /// <summary>服务端判定全员达经验上限后广播（multiplayer-hoeing-exp-cap-stop）。无参。
    /// 旧客户端不订阅 → 广播静默丢弃，行为退化为不提前终止。</summary>
    public event Action? AllReachedExpCap;

    public event Action<string>? KazuhaPlayerUpdated;
    public event Action? AllWorldJoined;
    public event Action<bool>? HostReadyChanged;
    public event Action<List<string>>? HostRouteListReady;
    public event Action<string, int, bool>? PlayerAnomalyNotifyReceived; // playerUid, routeIndex, passedSyncPoint
    public event Action<string, int, int>? PlayerAnomalyNotifyFightPointReceived; // playerUid, routeIndex, fightPointId
    public event Action<string>? PlayerAnomalyRecoveredReceived; // playerUid
    public event Action<int>? StartRouteReceived; // targetRouteIndex

    // === 成员状态广播接收（guard-multiplayer-peerdrop-visual-blind 方案A）===
    // 服务端 HeartbeatMonitor 掉线删除时广播 MemberStatusChanged(uid,"Offline",long.MaxValue)。
    // 载荷：playerUid, status, targetProgress。仅当 status=="Offline" 时表示真掉线（服务端已过宽限期+心跳判死）。
    // 服务端异常上报 hub 方法不回环广播本事件，故收到 Offline 只来自真实掉线删除。
    public event Action<string, string, long>? MemberStatusChangedReceived;

    // === 版本一致性校验事件（hoeing-multiplayer-version-compatibility-check）===
    /// <summary>
    /// 服务端版本校验判定加入者与房间基准版本不兼容、硬阻断加入时触发。
    /// 载荷为 Check_Result（双方版本号、是否通配、统一版本引导文案）。
    /// JoinRoomAsync 仍返回 false，本事件仅作旁路提示，不改返回语义（U4.1）。
    /// </summary>
    public event Action<Models.VersionCheckResult>? VersionCheckRejected;

    // === 集体卡死跳段事件（multiplayer-mutual-wait-collective-skip spec）===
    /// <summary>
    /// 服务端集体卡死监测触发后，请求落后玩家跳到 targetProgress 对应段。
    /// 载荷：targetProgress (long)。
    /// 旧客户端不订阅此事件 → 服务端广播被静默丢弃，行为退化到 60s 超时（preservation §3.9）。
    /// </summary>
    public event Action<long>? RequestSkipToProgressReceived;

    /// <summary>
    /// 服务端连续触发协同跳段达上限后的降级广播。载荷：reason (string)。
    /// 触发后客户端走 OnConsecutiveSyncTimeoutExceeded 等价路径协调停止（OQ-5 A）。
    /// </summary>
    public event Action<string>? CollectiveSkipDegradedReceived;

    // === 万叶聚物同步事件 ===
    /// <summary>
    /// 万叶玩家发起聚物动作时触发，载荷为：
    ///   - playerUid: 发起者 PlayerUid
    ///   - syncKey: 当前周期 syncKey
    ///   - collectX/Y: 聚物点小地图坐标，无效时为 NaN
    ///     （multiplayer-kazuha-collect-point-broadcast）。
    /// </summary>
    public event Action<string, string, double, double>? KazuhaCollectStarted;

    // === 路线变体一致性校验事件（route-variant-sync-by-logical-id spec / R6 / R8）===
    /// <summary>服务端按 LogicalRouteId 分组比对全部通过时触发（无参）。</summary>
    public event Action? RouteVariantConsistencyPassed;
    /// <summary>
    /// 服务端校验失败时触发。载荷：logicalRouteId（空字符串表示 30s 超时）+ playerItems（connId → 该玩家上报的 schema）。
    /// </summary>
    public event Action<string, Dictionary<string, Models.RouteVariantSchemaItem>>? RouteVariantConsistencyFailed;

    public List<PlayerInfo> CurrentPlayerList { get; set; } = new();
    public int CurrentRoomPlayerCount { get; set; }
    public string HostPlayerUid { get; set; } = string.Empty;
    public bool IsHost => _playerUid == HostPlayerUid;
    public bool IsConnected => _testIsConnectedOverride ?? (_gateway?.IsConnected ?? false);
    public bool IsInRoom => _isInRoom;
    public bool IsReconnecting => _isReconnecting;

    /// <summary>
    /// 当前玩家 UID（公开属性）
    /// </summary>
    public string PlayerUid => _playerUid ?? "";

    /// <summary>
    /// 当前玩家 UID（别名，用于兼容 TeamManager 等调用方）
    /// </summary>
    public string MyPlayerUid => _playerUid ?? "";

    /// <summary>
    /// 当前路线索引
    /// </summary>
    public int CurrentRouteIndex => _currentRouteIndex;
    public WorldStateMonitor? WorldStateMonitor
    {
        get => _worldStateMonitor;
        set => _worldStateMonitor = value;
    }

    public CoordinatorClient()
    {
        // 默认实现：把旧线形 (Hub 方法名, args) 翻译成 v3 信封，经网关联机发出。
        // 当前仅 NotifyKazuhaCollectStarted 一路使用本种子（见字段注释）。
        _invokeHubAsync = (method, args) =>
        {
            if (method == "NotifyKazuhaCollectStarted")
            {
                return _gateway!.InvokeCommandAsync(GatewayProtocol.Names.KazuhaNotifyCollectStarted, new
                {
                    syncKey = (string)args[0]!,
                    collectX = (double)args[1]!,
                    collectY = (double)args[2]!,
                });
            }
            throw new NotSupportedException($"_invokeHubAsync 不支持的方法: {method}");
        };
    }

    /// <summary>
    /// 测试种子：单测需要在没有真实连接的情况下驱动发送路径时，先取网关实例再注入
    /// BgiGatewayClient._testSendOverride/_testInvokeOverride（沿用既有测试种子模式）。
    /// </summary>
    internal BgiGatewayClient GetOrCreateGatewayForTest() => _gateway ??= new BgiGatewayClient();

    public async Task<bool> ConnectAsync(string serverUrl, CancellationToken ct)
    {
        var maskedUrl = MaskServerUrl(serverUrl);
        try
        {
            _gateway = new BgiGatewayClient();
            _gateway.EnvelopeReceived += DispatchEvt;
            _gateway.Reconnected += OnReconnected;
            _gateway.Reconnecting += OnReconnecting;
            _gateway.Closed += OnConnectionClosed;

            // URL 归一化（旧配置带 /hub 剥掉并告警）与 session.hello 握手在 SDK 内完成
            var connected = await _gateway.ConnectAsync(serverUrl, ct);
            if (!connected)
            {
                return false;
            }
            _logger.LogInformation("CoordinatorClient 已连接到 {Url}", maskedUrl);

            // 启动心跳定时器，每 5 秒发送一次
            _heartbeatTimer = new Timer(async _ =>
            {
                try { await SendHeartbeatAsync(); }
                catch { /* 忽略心跳异常 */ }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoordinatorClient 连接失败: {Url}", maskedUrl);
            return false;
        }
    }

    /// <summary>
    /// evt 信封分发（切片 8）：服务器广播的单一 evt 回调按 Name 路由到原有 23 个事件处理器，
    /// 处理逻辑（含"过滤自己"守卫与日志文案）与迁移前的逐事件 On 订阅逐字等价。
    /// payload 键缺失/畸形时不应用（对齐旧 On&lt;T&gt; 反序列化失败不触发的语义）。
    /// 未知事件名忽略（前向兼容：服务器可能发来本客户端未订阅的锄地房间事件）。
    /// internal：供单测直驱分发逻辑（映射表机器化核对），生产路径仅由 BgiGatewayClient evt 回调调用。
    /// </summary>
    internal void DispatchEvt(GatewayEnvelope env)
    {
        try
        {
            switch (env.Name)
            {
                case GatewayProtocol.Events.RoomPlayerListChanged:
                {
                    var list = env.Get<List<PlayerInfo>>("players");
                    if (list == null) break;
                    CurrentRoomPlayerCount = list.Count;
                    if (list.Count > 0)
                        HostPlayerUid = list[0].PlayerUid;
                    CurrentPlayerList = new List<PlayerInfo>(list);
                    UpdatePlayerNameCache(list);
                    PlayerListUpdated?.Invoke(list);
                    break;
                }

                case GatewayProtocol.Events.SyncAllArrived:
                    AllArrived?.Invoke(env.GetString("syncPointId"));
                    break;

                case GatewayProtocol.Events.FightAllDone:
                    AllFightDone?.Invoke(env.GetString("syncPointId"));
                    break;

                case GatewayProtocol.Events.RouteDiffReceived:
                {
                    var diff = env.Get<List<string>>("diffFiles");
                    if (diff == null) break;
                    RouteDiffReceived?.Invoke(diff);
                    break;
                }

                case GatewayProtocol.Events.RouteVerificationPassed:
                    RouteVerificationPassed?.Invoke();
                    break;

                // === 路线变体一致性校验（route-variant-sync-by-logical-id spec）===
                case GatewayProtocol.Events.RouteVariantConsistencyPassed:
                    RouteVariantConsistencyPassed?.Invoke();
                    break;

                case GatewayProtocol.Events.RouteVariantConsistencyFailed:
                {
                    var logicalId = env.GetString("logicalId");
                    var playerItems = env.Get<Dictionary<string, Models.RouteVariantSchemaItem>>("playerItems");
                    if (playerItems == null) break;
                    RouteVariantConsistencyFailed?.Invoke(logicalId, playerItems);
                    break;
                }

                case GatewayProtocol.Events.RoomClosed:
                    RoomClosed?.Invoke(env.GetString("reason"));
                    break;

                // === 版本一致性校验：服务端硬阻断回传 Check_Result（version-compatibility-check 改动 12）===
                case GatewayProtocol.Events.RoomVersionCheckRejected:
                {
                    var result = env.DeserializePayload<Models.VersionCheckResult>();
                    if (result == null) break;
                    _logger.LogWarning("[联机][版本校验] 加入被阻断：member={Member} baseline={Baseline} hint={Hint}",
                        result.MemberVersion, result.BaselineVersion, result.Hint);
                    VersionCheckRejected?.Invoke(result);
                    break;
                }

                case GatewayProtocol.Events.RouteVerificationAllDone:
                    RouteVerificationAllDone?.Invoke();
                    break;

                // === 基于经验判断停止锄地：全员达上限广播（multiplayer-hoeing-exp-cap-stop）===
                case GatewayProtocol.Events.ExpAllCapReached:
                    AllReachedExpCap?.Invoke();
                    break;

                case GatewayProtocol.Events.KazuhaPlayerUpdated:
                    KazuhaPlayerUpdated?.Invoke(env.GetString("playerUid"));
                    break;

                // === 万叶聚物同步事件 ===
                case GatewayProtocol.Events.KazuhaCollectStarted:
                    KazuhaCollectStarted?.Invoke(
                        env.GetString("playerUid"),
                        env.GetString("syncKey"),
                        env.GetDouble("collectX"),
                        env.GetDouble("collectY"));
                    break;

                case GatewayProtocol.Events.WorldAllJoined:
                    AllWorldJoined?.Invoke();
                    break;

                case GatewayProtocol.Events.RoomHostReadyChanged:
                    HostReadyChanged?.Invoke(env.GetBool("ready"));
                    break;

                case GatewayProtocol.Events.RoomHostRouteListReady:
                {
                    var routeNames = env.Get<List<string>>("routeNames");
                    if (routeNames == null) break;
                    HostRouteListReady?.Invoke(routeNames);
                    break;
                }

                // === 新机制：异常通知 ===
                case GatewayProtocol.Events.AnomalyPlayerNotified:
                {
                    var playerUid = env.GetString("playerUid");
                    var routeIndex = env.GetInt("routeIndex");
                    var passedSyncPoint = env.GetBool("passedSyncPoint");
                    if (playerUid == _playerUid) break; // 过滤自己
                    _logger.LogInformation("[联机] 收到异常通知: 玩家={PlayerUid}, 路线={RouteIndex}, 已过同步点={Passed}",
                        playerUid, routeIndex, passedSyncPoint);
                    PlayerAnomalyNotifyReceived?.Invoke(playerUid, routeIndex, passedSyncPoint);
                    break;
                }

                // === 新机制：带战斗点异常通知（hoeing-route-retry-round-end-refactor v3）===
                case GatewayProtocol.Events.AnomalyFightPointNotified:
                {
                    var playerUid = env.GetString("playerUid");
                    var routeIndex = env.GetInt("routeIndex");
                    var fightPointId = env.GetInt("fightPointId");
                    if (playerUid == _playerUid) break; // 过滤自己
                    _logger.LogInformation("[联机] 收到异常通知(带战斗点): 玩家={PlayerUid}, 路线={RouteIndex}, 战斗点={FightPointId}",
                        playerUid, routeIndex, fightPointId);
                    PlayerAnomalyNotifyFightPointReceived?.Invoke(playerUid, routeIndex, fightPointId);
                    break;
                }

                // === 新机制：异常恢复通知 ===
                // （服务器两旧事件名 PlayerAnomalyRecovered/AbnormalPlayerRecovered 映射到同一 evt 名，天然兼容）
                case GatewayProtocol.Events.AnomalyPlayerRecovered:
                {
                    var playerUid = env.GetString("playerUid");
                    if (playerUid == _playerUid) break;
                    _logger.LogInformation("[联机] 收到恢复通知: 玩家={PlayerUid}", playerUid);
                    PlayerAnomalyRecoveredReceived?.Invoke(playerUid);
                    break;
                }

                // === 成员状态广播接收（guard-multiplayer-peerdrop-visual-blind 方案A）===
                case GatewayProtocol.Events.RoomMemberStatusChanged:
                {
                    var playerUid = env.GetString("playerUid");
                    var status = env.GetString("status");
                    var targetProgress = env.GetLong("targetProgress");
                    if (playerUid == _playerUid) break; // 过滤自己上报的回环
                    _logger.LogInformation("[联机] 收到成员状态广播: 玩家={PlayerUid}, 状态={Status}", playerUid, status);
                    MemberStatusChangedReceived?.Invoke(playerUid, status, targetProgress);
                    break;
                }

                // === 新机制：开始路线指令 ===
                case GatewayProtocol.Events.RoomStartRoute:
                {
                    var targetRouteIndex = env.GetInt("routeIndex");
                    _logger.LogInformation("[联机] 收到开始路线指令: 目标路线={TargetRoute}", targetRouteIndex);
                    StartRouteReceived?.Invoke(targetRouteIndex);
                    break;
                }

                // === 集体卡死跳段事件（multiplayer-mutual-wait-collective-skip §8.5）===
                case GatewayProtocol.Events.SyncRequestSkipToProgress:
                {
                    var target = env.GetLong("targetProgress");
                    _logger.LogWarning("[联机] 收到 RequestSkipToProgress: target={Target}", target);
                    RequestSkipToProgressReceived?.Invoke(target);
                    break;
                }

                case GatewayProtocol.Events.SyncCollectiveSkipDegraded:
                {
                    var reason = env.GetString("reason");
                    _logger.LogError("[联机] 收到 CollectiveSkipDegraded: reason={Reason}", reason);
                    CollectiveSkipDegradedReceived?.Invoke(reason);
                    break;
                }

                default:
                    _logger.LogDebug("[联机] 收到未知 evt 事件（忽略）: {Name}", env.Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] evt 事件处理失败（已吞掉）: {Name}", env.Name);
        }
    }

    /// <summary>SignalR 内置自动重连成功（同一连接，新 connectionId）。</summary>
    private async Task OnReconnected(string? newConnectionId)
    {
        _logger.LogInformation("[联机] SignalR 重连成功，重新加入房间: {Code}", _currentRoomCode);
        if (!string.IsNullOrEmpty(_currentRoomCode))
        {
            try
            {
                // v3：断线后服务端会话登记已清除，必须先重新 hello 再 join（DAP 时序）
                if (_gateway != null)
                    await _gateway.HelloAsync();
                await JoinRoomAsync(_currentRoomCode, _playerName ?? "", _playerUid ?? "");
                _logger.LogInformation("[联机] 重连后重新加入房间成功");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机] 重连后重新加入房间失败");
            }
        }
    }

    private Task OnReconnecting(Exception? error)
    {
        _logger.LogWarning(error, "[联机] SignalR 连接断开，正在自动重连...");
        _worldStateMonitor?.NotifyHeartbeatSuccess(); // 重置失败计数
        return Task.CompletedTask;
    }

    private async Task OnConnectionClosed(Exception? ex)
    {
        if (_isReconnecting)
        {
            _logger.LogWarning("CoordinatorClient 重连期间再次断线，忽略（等当前重连流程完成）");
            return;
        }

        _isReconnecting = true;
        _isInRoom = false;
        _logger.LogWarning(ex, "CoordinatorClient 连接断开，开始指数退避重连...");

        if (_gateway == null)
        {
            _isReconnecting = false;
            return;
        }

        var retryDelays = new[] { 0, 2000, 5000, 10000 };
        bool reconnected = false;

        for (int round = 1; round <= 2 && !reconnected; round++)
        {
            if (round > 1)
            {
                _logger.LogInformation("CoordinatorClient 第{Round}轮重连前等待30秒...", round);
                await Task.Delay(30000);
            }

            for (int attempt = 0; attempt < retryDelays.Length; attempt++)
            {
                if (retryDelays[attempt] > 0)
                    await Task.Delay(retryDelays[attempt]);

                try
                {
                    await _gateway.ReconnectStartAsync();
                    // v3：重连成功后必须先重新 hello（DAP 时序），hello 失败计入该次重连失败
                    await _gateway.HelloAsync();
                    reconnected = true;
                    _logger.LogInformation("CoordinatorClient 重连成功（第{Round}轮，第{Attempt}次）", round, attempt + 1);

                    // 重连后重新加入房间
                    if (!string.IsNullOrEmpty(_currentRoomCode) && !string.IsNullOrEmpty(_playerName))
                    {
                        await JoinRoomAsync(_currentRoomCode, _playerName, _playerUid ?? "");
                    }
                    break;
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx, "CoordinatorClient 重连失败（第{Round}轮，第{Attempt}次）", round, attempt + 1);
                }
            }
        }

        _isReconnecting = false;

        if (!reconnected)
        {
            _logger.LogError("CoordinatorClient 重连失败，触发降级");
            OnDegraded?.Invoke();
        }
        else
        {
            _logger.LogInformation("CoordinatorClient 重连完成");
        }
    }

    private string MaskServerUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        try
        {
            var uri = new Uri(url);
            var maskedPath = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "" : "/***";
            return $"{uri.Scheme}://***:****{maskedPath}";
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// 更新玩家名称缓存
    /// </summary>
    private void UpdatePlayerNameCache(List<PlayerInfo> players)
    {
        foreach (var player in players)
        {
            if (!string.IsNullOrEmpty(player.PlayerUid) && !string.IsNullOrEmpty(player.PlayerName))
                _playerNameCache[player.PlayerUid] = player.PlayerName;
        }
    }

    /// <summary>
    /// 获取玩家显示名称
    /// </summary>
    public string GetPlayerDisplayName(string playerUid)
    {
        if (string.IsNullOrEmpty(playerUid)) return "未知玩家";

        if (_playerNameCache.TryGetValue(playerUid, out var name) && !string.IsNullOrEmpty(name))
            return name;

        var player = CurrentPlayerList.FirstOrDefault(p => p.PlayerUid == playerUid);
        if (player != null && !string.IsNullOrEmpty(player.PlayerName))
        {
            _playerNameCache[playerUid] = player.PlayerName;
            return player.PlayerName;
        }

        if (playerUid.Length > 6)
            return $"{playerUid[..3]}***{playerUid[^3..]}";
        return playerUid;
    }

    public async Task<List<RoomSummary>> GetOnlineRoomsAsync()
    {
        if (_gateway == null) return new List<RoomSummary>();
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);
            return resp.Get<List<RoomSummary>>("rooms") ?? new List<RoomSummary>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOnlineRoomsAsync 失败");
            return new List<RoomSummary>();
        }
    }

    public async Task<bool> JoinRoomAsync(string roomCode, string playerName, string? playerUid = null)
    {
        if (_gateway == null) return false;
        try
        {
            // version-compatibility-check R2.1：上报完整 Global.Version（含构建元数据，不截断）
            var resp = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomJoin, new
            {
                roomCode,
                playerName,
                playerUid = playerUid ?? "",
                reportedVersion = BetterGenshinImpact.Core.Config.Global.Version ?? "",
            }, roomCode);
            var result = resp.GetBool("success");
            _currentRoomCode = roomCode;
            _playerName = playerName;
            _playerUid = playerUid;
            _isInRoom = result;
            _logger.LogInformation("JoinRoomAsync 完成: Room={RoomCode}, Player={PlayerName}, Result={Result}",
                roomCode, playerName, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinRoomAsync 失败: Room={RoomCode}, Player={PlayerName}", roomCode, playerName);
            return false;
        }
    }

    public async Task LeaveRoomAsync()
    {
        if (_gateway == null) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomLeave, null);
            _isInRoom = false;
            _logger.LogInformation("LeaveRoomAsync 完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LeaveRoomAsync 失败");
        }
    }

    public async Task CloseRoomAsync()
    {
        if (_gateway == null) return;
        try
        {
            // 标记本地正在主动关闭房间，使后续到达的 RoomClosed 广播能识别为"自触发"
            // （本节点是房主，多世界轮次切换时主动关房，应避免 RoomClosed 取消主任务流程）
            _selfClosingRoom = true;
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomClose, null);
            _isInRoom = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CloseRoomAsync 失败");
        }
    }

    /// <summary>本地是否正在主动关闭房间（CloseRoomAsync 已发起，等待广播回环）</summary>
    private volatile bool _selfClosingRoom;

    /// <summary>
    /// 检查并消费"自触发关闭"标志位。供 RoomClosed 订阅方使用：
    /// 若本次 RoomClosed 是本地主动关房导致的回环广播，应跳过取消逻辑。
    /// </summary>
    public bool ConsumeSelfClosingRoomFlag()
    {
        if (!_selfClosingRoom) return false;
        _selfClosingRoom = false;
        return true;
    }

    public async Task ResetWorldJoinedAsync()
    {
        if (_gateway == null) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.WorldResetJoined, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetWorldJoinedAsync 失败");
        }
    }

    /// <summary>
    /// 多轮世界重置：新轮次开始时调用
    /// </summary>
    public async Task ResetForNewWorldRoundAsync(int newRound)
    {
        if (_gateway == null) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.WorldResetForNewRound, new { newRound });
            _logger.LogInformation("[联机] 发送多轮世界重置请求: Round {Round}", newRound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetForNewWorldRoundAsync 失败");
        }
    }

    public async Task SendHeartbeatAsync()
    {
        if (_gateway == null) return;
        try
        {
            if (_currentRouteIndex >= 0)
            {
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.SessionHeartbeat, new
                {
                    routeIndex = _currentRouteIndex,
                    routeStartTime = _routeStartTime,
                    routeEstimatedSeconds = _routeEstimatedSeconds,
                });
            }
            else
            {
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.SessionHeartbeat, null);
            }
            _worldStateMonitor?.NotifyHeartbeatSuccess();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendHeartbeatAsync 失败");
            _worldStateMonitor?.NotifyHeartbeatFailure();
        }
    }

    /// <summary>
    /// 查询指定成员的路线进度（BUG 1：保留用于线路协调检查）
    /// </summary>
    public async Task<MemberProgress?> GetMemberProgressAsync(string playerUid)
    {
        if (_gateway == null || !IsConnected) return null;
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                new { section = GatewayProtocol.StateSections.MemberProgress, playerUid });
            return resp.Get<MemberProgress>("value");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetMemberProgressAsync 失败: {Uid}", playerUid);
            return null;
        }
    }

    /// <summary>
    /// 更新当前路线进度（供 TeamManager 调用）
    /// </summary>
    public void UpdateRouteProgress(int routeIndex, DateTime startTime, double estimatedSeconds)
    {
        _currentRouteIndex = routeIndex;
        _routeStartTime = startTime;
        _routeEstimatedSeconds = estimatedSeconds;
    }

    /// <summary>
    /// 查询所有成员的路线进度（用于线路同步检查）
    /// </summary>
    public async Task<Dictionary<string, int>?> QueryRouteProgressAsync(CancellationToken ct)
    {
        if (_gateway == null || !IsConnected) return null;
        try
        {
            // 服务器只有按成员查询（room.getState section=memberProgress），需要遍历所有玩家查询
            var result = new Dictionary<string, int>();
            foreach (var player in CurrentPlayerList)
            {
                try
                {
                    var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                        new { section = GatewayProtocol.StateSections.MemberProgress, playerUid = player.PlayerUid }, null, ct);
                    var progress = resp.Get<MemberProgress>("value");
                    if (progress != null)
                    {
                        result[player.PlayerUid] = progress.RouteIndex;
                    }
                }
                catch
                {
                    // 忽略单个玩家的查询失败
                }
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QueryRouteProgressAsync 失败");
            return null;
        }
    }

    /// <summary>
    /// 上报路线完成进度
    /// 注意：服务端目前未实现 ReportRouteProgress Hub 方法，此调用会失败。
    /// 当前的"进度值同步"机制（WaitForAllPlayers + MemberStatusChanged 重评估）
    /// 已完整覆盖路线同步功能，故此处改为 no-op，避免日志噪声。
    /// </summary>
    public Task ReportRouteProgressAsync(int completedRouteIndex)
    {
        // No-op: 旧机制已被进度值同步替代，保留方法签名兼容现有调用方。
        _logger.LogDebug("[联机] ReportRouteProgressAsync 调用（已 no-op）: completedRouteIndex={Index}", completedRouteIndex);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 创建房间
    /// </summary>
    public async Task<string?> CreateRoomAsync(string playerName, List<string>? whitelist, string playerUid, int expectedPlayerCount)
    {
        if (_gateway == null) return null;
        try
        {
            var resp = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomCreate, new
            {
                playerName,
                whitelist = whitelist ?? new List<string>(),
                playerUid,
                expectedPlayerCount,
                reportedVersion = BetterGenshinImpact.Core.Config.Global.Version ?? "",
            });
            var result = resp.GetString("roomCode");
            _playerName = playerName;
            _playerUid = playerUid;
            _isInRoom = !string.IsNullOrEmpty(result);
            if (!string.IsNullOrEmpty(result))
            {
                _currentRoomCode = result;
                return result;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateRoomAsync 失败");
            return null;
        }
    }

    /// <summary>
    /// 客户端识别到本地联机队伍含万叶后，向服务端声明候选身份（kazuha-player-auto-detection）。
    /// 服务端按 SignalR 调用到达顺序追加到 KazuhaCandidates；第一个声明者会被立即选为当前 Kazuha
    /// 并触发 KazuhaPlayerUpdated(playerUid) 广播。
    /// 失败静默忽略（捕获 + LogWarning），不阻塞主任务。
    /// </summary>
    public async Task DeclareKazuhaCapabilityAsync()
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.KazuhaDeclareCapability, null);
            _logger.LogInformation("[联机][聚物] 已声明万叶候选身份");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][聚物] DeclareKazuhaCapabilityAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报战斗参与者（按 syncKey 分组）。multiplayer-shared-fight-end-quorum-sync spec。
    /// 失败静默忽略，不阻塞战斗。旧服务端无此 Hub 方法 → HubException 被吞，行为退化。
    /// </summary>
    public async Task NotifyFightParticipantAsync(string syncKey, CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.FightReportParticipant, new { syncKey }, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][结束配额] NotifyFightParticipantAsync 失败（静默忽略）syncKey={Key}", syncKey);
        }
    }

    /// <summary>
    /// 上报本地战斗完成投票（复用现有 Hub 方法 ReportFightDone(syncKey)）。
    /// multiplayer-shared-fight-end-quorum-sync spec。失败静默忽略。
    /// </summary>
    public async Task NotifyFightDoneAsync(string syncKey, CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            // 注意 payload 键名是 syncPointId（与服务器网关路由对齐），值为本局 syncKey
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.FightReportDone, new { syncPointId = syncKey }, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][结束配额] NotifyFightDoneAsync 失败（静默忽略）syncKey={Key}", syncKey);
        }
    }

    /// <summary>
    /// 上报本机达经验上限（multiplayer-hoeing-exp-cap-stop）。
    /// 失败静默忽略，旧服务端无此 Hub 方法 → HubException 被吞，行为退化为不提前终止。
    /// </summary>
    public async Task NotifyExpCapReachedAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ExpReportFightResult,
                new { kind = GatewayProtocol.ExpKinds.CapReached }, null, ct);
            _logger.LogInformation("[联机][经验上限] 已上报本机达经验上限");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][经验上限] NotifyExpCapReachedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 撤回本机达经验上限（又见经验，multiplayer-hoeing-exp-cap-stop）。失败静默忽略。
    /// </summary>
    public async Task NotifyExpCapClearedAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ExpReportFightResult,
                new { kind = GatewayProtocol.ExpKinds.CapCleared }, null, ct);
            _logger.LogInformation("[联机][经验上限] 已撤回本机达经验上限");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][经验上限] NotifyExpCapClearedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报团队 arming（本机吃到经验，或连续 6 场无经验兜底触发，multiplayer-hoeing-exp-cap-stop R7）。
    /// 服务端据此置 ExpCapArmed=true——广播 AllReachedExpCap 的必要条件之一。
    /// 失败静默忽略，旧服务端无此 Hub 方法 → HubException 被吞，行为退化为不提前终止（不误停、不卡死）。
    /// </summary>
    public async Task NotifyExpArmedAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ExpReportFightResult,
                new { kind = GatewayProtocol.ExpKinds.Armed }, null, ct);
            _logger.LogInformation("[联机][经验上限] 已上报团队 arming");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][经验上限] NotifyExpArmedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报"连续2场无经验"预警信号（exp-cap-prefinal-stop-by-two-noexp）。
    /// 失败静默忽略，旧服务端无此 Hub 方法 → HubException 被吞，行为退化为不提前终止。
    /// </summary>
    public async Task NotifyTwoConsecutiveNoExpAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ExpReportFightResult,
                new { kind = GatewayProtocol.ExpKinds.TwoNoExp }, null, ct);
            _logger.LogInformation("[联机][经验上限] 已上报连续2场无经验预警");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][经验上限] NotifyTwoConsecutiveNoExpAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 撤回"连续2场无经验"预警信号（又见经验，exp-cap-prefinal-stop-by-two-noexp）。
    /// 失败静默忽略。
    /// </summary>
    public async Task NotifyTwoConsecutiveNoExpClearedAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.ExpReportFightResult,
                new { kind = GatewayProtocol.ExpKinds.TwoNoExpCleared }, null, ct);
            _logger.LogInformation("[联机][经验上限] 已撤回连续2场无经验预警");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][经验上限] NotifyTwoConsecutiveNoExpClearedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 万叶玩家广播"开始执行聚物动作"。
    /// multiplayer-kazuha-collect-point-broadcast: 加 syncKey + 聚物点 (collectX, collectY) 三参。
    /// 调用方在朝向 / 位置识别失败时传 (NaN, NaN)，由 **客户端 + 服务端 + 其他客户端订阅** 三层 IsValid 守卫过滤。
    /// 服务端会向房间所有客户端转发 KazuhaCollectStarted 事件（始终 4 参广播）。
    /// </summary>
    public async Task NotifyKazuhaCollectStartedAsync(string syncKey, double collectX, double collectY)
    {
        // 注：单 IsConnected 守卫即可——网关不存在或未连接时 IsConnected 均为 false → 守卫触发。
        // 与其他 Notify*Async 方法的 (_gateway == null || !IsConnected) 写法功能等价；
        // 这里写成单条便于测试可控（_testIsConnectedOverride=true 时绕过网关真实状态）。
        if (!IsConnected) return;

        // 客户端 IsValid 守卫：拦截 NaN / ±Infinity / (0,0)，避免 System.Text.Json 在序列化
        // 线上消息时抛 ArgumentException 损坏 SignalR 管线
        // （详见 spec kazuha-collect-point-nan-signalr-serialization-fix）。
        // 与服务端 NotifyKazuhaCollectStarted 内的 IsValid + KazuhaCollectSyncCoordinator
        // 构造函数订阅的 IsValid 一起构成 defense-in-depth 三层过滤。
        if (!KazuhaCollectPointDecisions.IsValid(collectX, collectY))
        {
            _logger.LogDebug("[联机][聚物] NotifyKazuhaCollectStartedAsync 短路：坐标无效 syncKey={Key} ({X},{Y})",
                syncKey, collectX, collectY);
            return;
        }

        try
        {
            await _invokeHubAsync("NotifyKazuhaCollectStarted", new object?[] { syncKey, collectX, collectY });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][聚物] NotifyKazuhaCollectStartedAsync 失败（静默忽略）syncKey={Key}", syncKey);
        }
    }

    /// <summary>
    /// 上传房间配置
    /// </summary>
    public async Task SetRoomConfigAsync(Models.RoomConfig config)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomSetConfig, new { config });
            _logger.LogInformation("[联机] 上传房间配置成功");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetRoomConfigAsync 失败");
            throw;
        }
    }

    /// <summary>
    /// 获取房间配置
    /// </summary>
    public async Task<Models.RoomConfig?> GetRoomConfigAsync()
    {
        if (_gateway == null || !IsConnected) return null;
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetConfig, null);
            return resp.Get<Models.RoomConfig>("config");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetRoomConfigAsync 失败");
            return null;
        }
    }

    /// <summary>
    /// 上报房主就绪
    /// </summary>
    public async Task ReportHostReadyAsync()
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomReportHostReady, null);
            _logger.LogInformation("[联机] 上报房主就绪");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportHostReadyAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报本玩家所有计划路线的变体 schema 摘要（route-variant-sync-by-logical-id spec / R6 / R8）。
    /// 服务端返回错误时抛 GatewayErrorException，调用方（MultiplayerCoordinator.VerifyRouteVariantSchemaAsync）
    /// 按 R8.6 / R8.7 分流。不静默 catch，让 caller 决定 fallback / 显式报错。
    /// </summary>
    public async Task ReportRouteVariantSchemaAsync(
        List<Models.RouteVariantSchemaItem> items, CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected)
            throw new InvalidOperationException("CoordinatorClient 未连接");

        await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RouteReportVariantSchema, new { items }, null, ct);
        _logger.LogInformation("[变体校验] 已上报 {Count} 条 schema（含非空 LogicalRouteId {NonEmpty} 条）",
            items?.Count ?? 0, items?.Count(i => !string.IsNullOrEmpty(i.LogicalRouteId)) ?? 0);
    }

    /// <summary>
    /// 房主调用此方法把房间标记为已开锄（spec lock-room-after-start §4.1）。
    /// 服务端从此 JoinRoom 拒绝非重连新玩家、GetOnlineRooms 也不再返回此房间。
    /// 旧服务端无此 Hub 方法 → 抛 HubException 被静默吞掉，不影响主任务（bugfix §3.9）。
    /// </summary>
    public async Task MarkRoomStartedAsync(IReadOnlySet<string>? completedHostUids = null)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            if (completedHostUids != null && completedHostUids.Count > 0)
            {
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomMarkStarted,
                    new { completedHostUids = completedHostUids.ToList() });
                _logger.LogInformation("[联机] MarkRoomStartedAsync 完成（上报 {N} 已完成房主，房间已锁定）",
                    completedHostUids.Count);
            }
            else
            {
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomMarkStarted, null);
                _logger.LogInformation("[联机] MarkRoomStartedAsync 完成（房间已锁定）");
            }
        }
        catch (Exception ex)
        {
            // 上报失败静默降级：等价上报空集合 → 服务端生成全量序列（现状），不影响主任务。
            // 与 ReportHostReadyAsync / DeclareKazuhaCapabilityAsync 静默兜底模式一致。
            _logger.LogWarning(ex, "MarkRoomStartedAsync 失败（静默忽略，降级为全量序列）");
        }
    }

    /// <summary>
    /// 上报已加入世界
    /// </summary>
    public async Task ReportWorldJoinedAsync()
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.WorldReportJoined, null);
            _logger.LogInformation("[联机] 上报已加入世界");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportWorldJoinedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 纯本地查询：判断自己是否在房间花名册（CurrentPlayerList 镜像）中。
    /// 优先按 PlayerUid 匹配，回退 PlayerName。无网络调用。
    /// 用于加入世界前后确认自身在房，规避"物理在世界但花名册已掉"导致被误踢。
    /// </summary>
    public bool AmIInRoomRoster()
    {
        var list = CurrentPlayerList;
        if (list == null || list.Count == 0) return false;
        if (!string.IsNullOrEmpty(_playerUid) && list.Any(p => p.PlayerUid == _playerUid))
            return true;
        if (!string.IsNullOrEmpty(_playerName) && list.Any(p => p.PlayerName == _playerName))
            return true;
        return false;
    }

    /// <summary>
    /// 确保自己仍在房间：本地标志与花名册都认为在房则直接返回 true；
    /// 否则用保存的房间码重新 JoinRoom（服务端对同名/同 UID 重连幂等，可安全反复调用），
    /// 成功后补发一次心跳。网络异常静默返回 false，由调用方决定是否继续。
    /// </summary>
    public async Task<bool> EnsureInRoomAsync(CancellationToken ct = default)
    {
        if (_gateway == null || !IsConnected) return false;
        if (_isInRoom && AmIInRoomRoster()) return true;
        if (string.IsNullOrEmpty(_currentRoomCode)) return false;
        try
        {
            var resp = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomJoin, new
            {
                roomCode = _currentRoomCode,
                playerName = _playerName ?? "",
                playerUid = _playerUid ?? "",
                reportedVersion = BetterGenshinImpact.Core.Config.Global.Version ?? "",
            }, _currentRoomCode);
            var ok = resp.GetBool("success");
            _isInRoom = ok;
            if (ok)
            {
                _logger.LogInformation("[联机] EnsureInRoomAsync：重新确认加入房间 {Code} 成功", _currentRoomCode);
                try { await SendHeartbeatAsync(); } catch { /* 心跳失败不阻塞，下个周期重试 */ }
            }
            else
            {
                _logger.LogWarning("[联机] EnsureInRoomAsync：重新加入房间 {Code} 被拒（可能已满/已开锄/白名单）", _currentRoomCode);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] EnsureInRoomAsync 失败（静默，下个周期重试）");
            return false;
        }
    }

    /// <summary>
    /// 查询房主是否就绪
    /// </summary>
    public async Task<bool> IsHostReadyAsync()
    {
        if (_gateway == null || !IsConnected) return false;
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                new { section = GatewayProtocol.StateSections.HostReady });
            return resp.GetBool("value");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IsHostReadyAsync 失败");
            return false;
        }
    }

    /// <summary>
    /// 重置成员路线进度缓存
    /// </summary>
    public void ResetMemberProgressCache()
    {
        _currentRouteIndex = -1;
        _logger.LogDebug("[联机] 成员路线进度缓存已重置");
    }

    /// <summary>
    /// 获取指定玩家的路线索引
    /// </summary>
    public int? GetPeerRouteIndex(string playerUid)
    {
        return null; // 简化实现，无缓存
    }

    /// <summary>
    /// 上传房主路线列表
    /// </summary>
    public async Task SetHostRouteListAsync(List<string> routeNames)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomSetHostRouteList, new { routeNames });
            _logger.LogInformation("[联机] 上传房主路线列表: {Count} 条", routeNames.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetHostRouteListAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 获取房主路线列表
    /// </summary>
    public async Task<List<string>> GetHostRouteListAsync()
    {
        if (_gateway == null || !IsConnected) return new List<string>();
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                new { section = GatewayProtocol.StateSections.HostRouteList });
            return resp.Get<List<string>>("value") ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetHostRouteListAsync 失败");
            return new List<string>();
        }
    }

    /// <summary>
    /// 查询房主是否已上传过路线列表（含上传空列表的情况）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：成员收到空列表时据此区分
    /// "房主从未上传"（false → 继续等待）与"房主上传了空列表（CD全过滤）"（true → 优雅跳过本轮）。
    /// 连接异常时返回 false（安全降级：成员落回原 90s 等待路径，不会误跳过）。
    /// </summary>
    public async Task<bool> IsHostRouteListUploadedAsync()
    {
        if (_gateway == null || !IsConnected) return false;
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                new { section = GatewayProtocol.StateSections.HostRouteListUploaded });
            return resp.GetBool("value");
        }
        catch (Exception ex)
        {
            // 查询失败 → 降级为 false（保守，宁可多等不可误跳）
            _logger.LogWarning(ex, "IsHostRouteListUploadedAsync 失败（降级为 false）");
            return false;
        }
    }

    /// <summary>
    /// 原子获取房主路线列表状态 (Uploaded, RouteNames)，取代 GetHostRouteList + IsHostRouteListUploaded
    /// 两次独立查询，消除 TOCTOU 竞态（multiplayer-member-skip-round-stuck-roundend-sync-fix）。
    /// 查询失败 → 降级返回 (false, 空)，调用方落回"继续等待房主推送"保守路径（宁等勿误判为空）。
    /// </summary>
    public async Task<(bool Uploaded, List<string> RouteNames)> GetHostRouteListStatusAsync()
    {
        if (_gateway == null || !IsConnected) return (false, new List<string>());
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetState,
                new { section = GatewayProtocol.StateSections.HostRouteListStatus });
            var s = resp.Get<HostRouteListStatusDto>("value");
            return (s?.Uploaded ?? false, s?.RouteNames ?? new List<string>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetHostRouteListStatusAsync 失败（降级为 (false, 空)）");
            return (false, new List<string>());
        }
    }

    /// <summary>
    /// 查询服务端权威轮换序列（UID 列表，第 i 项 = 第 i 轮房主 UID）。
    /// 由首任房主 MarkRoomStarted 时生成，整场只生成一次，保证各客户端轮换序列完全一致。
    /// 查询失败 → 返回 null，由调用方降级本地 CurrentPlayerList 快照。
    /// multiplayer-server-authoritative-round-order。
    /// </summary>
    public async Task<List<string>?> GetRoundHostOrderAsync()
    {
        if (_gateway == null || !IsConnected) return null;
        try
        {
            var resp = await _gateway.QueryAsync(GatewayProtocol.Names.RoomGetRoundHostOrder, null);
            return resp.Get<List<string>>("order");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] GetRoundHostOrderAsync 失败（异常），降级本地快照");
            return null;
        }
    }

    /// <summary>
    /// 上报成员进度（跳过后广播）
    /// </summary>
    public async Task SendMemberProgressAsync(int routeIndex)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyReportMemberProgress,
                new { playerUid = PlayerUid ?? "", routeIndex });
            _logger.LogDebug("[联机] 发送成员进度: 路线 {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendMemberProgressAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报路线哈希列表（用于路线一致性验证）
    /// </summary>
    public async Task ReportRouteListAsync(List<Models.RouteHash> hashes)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RouteReportList, new { routes = hashes });
            _logger.LogInformation("[联机] 上报路线列表: {Count} 条", hashes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportRouteListAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 重新加入当前房间。
    /// 切片 8 修复（owner 已确认）：旧实现调用的 "RejoinRoom" Hub 方法在服务端从未存在
    /// （CoordinatorHub 无此方法、网关 LegacyMethodMap 未收录），调用必抛 HubException →
    /// 静默返回 false，本方法自上线起从未成功过。现按 owner 决策映射到 room.join——
    /// 服务端对同名/同 UID 重连幂等（与 Reconnected 回调 / EnsureInRoomAsync 同一路径），死路就此打通。
    /// </summary>
    public async Task<bool> RejoinCurrentRoomAsync()
    {
        if (_gateway == null || string.IsNullOrEmpty(_currentRoomCode) || string.IsNullOrEmpty(_playerName)) return false;
        try
        {
            var resp = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomJoin, new
            {
                roomCode = _currentRoomCode,
                playerName = _playerName,
                playerUid = _playerUid ?? "",
                reportedVersion = BetterGenshinImpact.Core.Config.Global.Version ?? "",
            }, _currentRoomCode);
            var result = resp.GetBool("success");
            _isInRoom = result;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RejoinCurrentRoomAsync 失败");
            return false;
        }
    }

    /// <summary>
    /// 上报到达同步点
    /// </summary>
    public async Task ReportArrivalAsync(string syncPointId, int expectedCount = 0)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            if (expectedCount > 0)
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.SyncReportArrival, new { syncPointId, expectedCount });
            else
                await _gateway.InvokeCommandAsync(GatewayProtocol.Names.SyncReportArrival, new { syncPointId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportArrivalAsync 失败: {SyncId}", syncPointId);
        }
    }

    /// <summary>
    /// 上报异常通知（新机制）
    /// </summary>
    public async Task ReportAnomalyAsync(string playerUid, int routeIndex, bool passedSyncPoint)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyNotify,
                new { playerUid, routeIndex, passedSyncPoint });
            _logger.LogInformation("[联机] 发送异常通知: 玩家={PlayerUid}, 路线={RouteIndex}, 已过同步点={Passed}",
                playerUid, routeIndex, passedSyncPoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportAnomalyAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// <summary>
    /// 上报"复苏者附带战斗点"的异常通知（hoeing-route-retry-round-end-refactor v3）。
    /// 调用服务端 PlayerAnomalyNotifyFightPoint（纯透传），供其他成员做战斗点级跳过。
    /// </summary>
    public async Task ReportAnomalyWithFightPointAsync(string playerUid, int routeIndex, int fightPointId)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyNotifyFightPoint,
                new { playerUid, routeIndex, fightPointId });
            _logger.LogInformation("[联机] 发送异常通知(带战斗点): 玩家={PlayerUid}, 路线={RouteIndex}, 战斗点={FightPointId}",
                playerUid, routeIndex, fightPointId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportAnomalyWithFightPointAsync 失败（静默忽略）");
        }
    }

    /// 上报异常恢复通知（新机制）
    /// </summary>
    public async Task ReportRecoveredAsync(string playerUid)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyRecovered, new { playerUid });
            _logger.LogInformation("[联机] 发送恢复通知: 玩家={PlayerUid}", playerUid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportRecoveredAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报成员状态（Normal/Fighting/Rejoining/Reviving/Offline）
    /// targetProgress：异常恢复后将到达的同步点进度值（仅 Reviving/Rejoining 时有意义，其他时候传 -1）
    /// </summary>
    public async Task ReportMemberStatusAsync(MemberStatus status, long targetProgress = -1)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyMemberStatusChanged,
                new { playerUid = PlayerUid ?? "", status = status.ToString(), targetProgress });
            _logger.LogDebug("[联机] 上报成员状态: {Status}, 目标进度={Target}", status, targetProgress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportMemberStatusAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报路线跳过
    /// </summary>
    public async Task ReportRouteSkippedAsync(int routeIndex)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyRouteSkipped,
                new { playerUid = PlayerUid ?? "", routeIndex });
            _logger.LogInformation("[联机] 上报路线跳过: {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportRouteSkippedAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报等待点到达
    /// </summary>
    public async Task ReportWaitPointAsync(string syncPointId)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyWaitPointReached,
                new { playerUid = PlayerUid ?? "", syncPointId });
            _logger.LogDebug("[联机] 上报等待点: {SyncPointId}", syncPointId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportWaitPointAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 上报战斗状态
    /// </summary>
    public async Task ReportFightingStatusAsync(bool isFighting)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.InvokeCommandAsync(GatewayProtocol.Names.AnomalyFightingStatusChanged,
                new { playerUid = PlayerUid ?? "", isFighting });
            _logger.LogDebug("[联机] 上报战斗状态: {IsFighting}", isFighting);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReportFightingStatusAsync 失败（静默忽略）");
        }
    }

    /// <summary>
    /// 抢报专用：仅向服务端 fire-and-forget 发送 sync.waitForAllPlayers，**不订阅 AllArrived 不等待**，
    /// 立即返回让调用方继续走（fastsync-redesign-parameter-passing spec）。
    ///
    /// 服务端网关路由会执行 RecordArrival + 全量评估 + 必要时广播 AllArrived 给
    /// 已经在等的对方——抢报方自己不阻塞，由后续的严格 WaitForAllPlayers 路径再次上报
    /// 走完整等待逻辑（idempotent）。
    /// </summary>
    public async Task FireAndForgetArrivalAsync(string syncId, long syncProgress = -1)
    {
        if (_gateway == null || !IsConnected) return;
        try
        {
            await _gateway.SendCommandFireAndForgetAsync(GatewayProtocol.Names.SyncWaitForAllPlayers,
                new { syncId, syncProgress });
            _logger.LogDebug("[联机] 抢报到达（fire-and-forget）: {SyncId}, 进度={Progress}", syncId, syncProgress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 抢报到达失败（已忽略）: {SyncId}", syncId);
        }
    }

    /// <summary>
    /// 等待所有玩家到达指定同步点
    /// syncProgress：当前同步点的全局进度值（用于服务端判定异常玩家是否会经过此点）
    /// 返回 true 仅表示与当前 syncId 匹配的 AllArrived 已被消费（严格等待完成）；
    /// 未连接/未匹配返回 false；取消、超时、房间关闭和其它异常仍按原有语义抛出，
    /// 由调用方（MultiplayerCoordinator.WaitForAllPlayers）统一映射为未确认完成。
    /// 进程内 API 变化，不影响 subscribe-before-action / 匹配逻辑 / SignalR 协议。
    /// </summary>
    public async Task<bool> WaitForAllPlayersAsync(string syncId, CancellationToken ct, long syncProgress = -1, bool wasFastReported = false)
    {
        // 未连接：没有任何 AllArrived 匹配被消费，返回 false 表示未确认完成（不能误判为严格等待完成）。
        if (_gateway == null || !IsConnected) return false;

        // 使用与 SyncBarrier 相同的模式：先订阅事件，再发送动作，本地等待
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnArrived(string id)
        {
            if (id == syncId)
            {
                tcs.TrySetResult(true);
            }
        }
        void OnRoomClosed(string reason)
        {
            tcs.TrySetCanceled();
        }

        AllArrived += OnArrived;
        RoomClosed += OnRoomClosed;

        try
        {
            // 首次上报到达
            await _gateway.SendCommandFireAndForgetAsync(GatewayProtocol.Names.SyncWaitForAllPlayers,
                new { syncId, syncProgress });
            _logger.LogDebug("[联机] 请求等待所有玩家: {SyncId}, 进度={Progress}", syncId, syncProgress);

            // 本地等待 AllArrived 事件（受 CT 控制，可被用户取消）
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // fastsync-claim-short-circuit-premature-release-fix（OQ-4=a / 方案 B）：
            // 短探测窗口区分"全员已到 / 服务端补发命中 → 立即放行"与"全员未到 → 进入等待"。
            // 200ms 内收到 AllArrived（含服务端定向补发）即视为立即放行。
            // wasFastReported 仅决定日志文案前缀，不影响放行逻辑。
            const int replayProbeMs = 200;
            var probe = await Task.WhenAny(tcs.Task, Task.Delay(replayProbeMs, linkedCts.Token));
            if (probe == tcs.Task && tcs.Task.IsCompletedSuccessfully)
            {
                if (wasFastReported)
                    _logger.LogInformation("[联机][FastSync] 已抢报且全员已到，立即放行: {SyncId}", syncId);
                else
                    _logger.LogInformation("[联机] 同步点全员已到，立即放行: {SyncId}", syncId);
                return true; // 匹配 AllArrived 已被消费（服务端补发 / 全员已到）
            }

            if (wasFastReported)
                _logger.LogInformation("[联机][FastSync] 已抢报但全员未到，进入等待: {SyncId}", syncId);
            else
                _logger.LogInformation("[联机] 同步点全员未到，进入等待: {SyncId}", syncId);

            // 在等待 AllArrived 期间，每 5 秒重试一次上报到达，弥补网络波动导致的上报丢失或错过广播
            // 注意：重试循环必须正确响应 linkedCts 超时取消，否则 Task.Delay(cancelledToken) 会
            // 被 Task.WhenAny 吃掉异常，导致循环无法被 catch (OperationCanceledException) 捕获，
            // 进入无限重试（日志中同一毫秒出现大量重复"等待中，重试上报到达"即为该症状）。
            const int retryIntervalMs = 5000;
            while (true)
            {
                // 每次循环迭代前检查超时/取消，确保 linkedCts 取消后立即退出而不是继续重试
                linkedCts.Token.ThrowIfCancellationRequested();

                var completed = await Task.WhenAny(tcs.Task, Task.Delay(retryIntervalMs, linkedCts.Token));
                if (completed == tcs.Task)
                {
                    // 收到 AllArrived（tcs.Task 完成）→ 退出循环
                    break;
                }
                // 5 秒到了还没收到 AllArrived → 重试上报
                _logger.LogDebug("[联机] 等待中，重试上报到达: {SyncId}", syncId);
                await _gateway.SendCommandFireAndForgetAsync(GatewayProtocol.Names.SyncWaitForAllPlayers,
                    new { syncId, syncProgress });
            }

            // while 循环退出即表示匹配 AllArrived 已被消费（严格等待完成）
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[联机] 等待所有玩家超时或被取消: {SyncId}", syncId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WaitForAllPlayersAsync 失败: {SyncId}", syncId);
            throw;
        }
        finally
        {
            AllArrived -= OnArrived;
            RoomClosed -= OnRoomClosed;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_gateway == null) return;
        try
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _isInRoom = false;
            _gateway.Closed -= OnConnectionClosed;
            await _gateway.StopAsync();
            _logger.LogInformation("CoordinatorClient 已断开连接");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisconnectAsync 时发生异常");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        if (_gateway != null)
        {
            _gateway.Closed -= OnConnectionClosed;
            await _gateway.DisposeAsync();
            _gateway = null;
        }
    }
}
