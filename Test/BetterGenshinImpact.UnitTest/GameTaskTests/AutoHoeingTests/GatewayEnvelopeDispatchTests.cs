#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 切片 8：BGI 侧 v3 迁移的信封与 evt 分发机器化核对。
/// 守住三件事：①信封 STJ camelCase 线形与服务器 GatewayEnvelope 一致；
/// ②DispatchEvt 对 23 个 evt 名逐一正确分发到 CoordinatorClient 的 C# 事件
/// （payload 键、参数顺序、"过滤自己"守卫与服务器 LegacyEventMap 映射不漂移）；
/// ③响应 error 解析与 URL 归一化（旧 /hub 配置剥尾巴）。
/// </summary>
public class GatewayEnvelopeDispatchTests
{
    private static GatewayEnvelope Evt(string name, object? payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = name,
        Payload = GatewayEnvelope.ToPayload(payload),
        SentAtUtc = DateTime.UtcNow,
    };

    private static void SetPlayerUid(CoordinatorClient client, string uid)
    {
        var field = typeof(CoordinatorClient).GetField("_playerUid", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(client, uid);
    }

    // =========================================================================
    // 信封线形
    // =========================================================================

    [Fact]
    public void Envelope_SerializationRoundTrip_CamelCase()
    {
        var env = GatewayEnvelope.Command("sync.reportArrival", new { syncPointId = "sp1", expectedCount = 3 }, "ROOM1");

        var json = JsonSerializer.Serialize(env, GatewayJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // camelCase 键名（与服务器 GatewayEnvelope 经 SignalR STJ 上线后的形状一致）
        Assert.True(root.TryGetProperty("protocolVersion", out _));
        Assert.True(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty("name", out _));
        Assert.True(root.TryGetProperty("requestId", out _));
        Assert.True(root.TryGetProperty("roomCode", out _));
        Assert.True(root.TryGetProperty("payload", out _));
        Assert.True(root.TryGetProperty("sentAtUtc", out _));
        Assert.False(root.TryGetProperty("ProtocolVersion", out _));

        var back = JsonSerializer.Deserialize<GatewayEnvelope>(json, GatewayJson.Options);
        Assert.NotNull(back);
        Assert.Equal(GatewayProtocol.ProtocolVersion, back!.ProtocolVersion);
        Assert.Equal("command", back.Type);
        Assert.Equal("sync.reportArrival", back.Name);
        Assert.Equal("ROOM1", back.RoomCode);
        Assert.Equal(env.RequestId, back.RequestId);
        Assert.Equal("sp1", back.GetString("syncPointId"));
        Assert.Equal(3, back.GetInt("expectedCount"));
    }

    [Fact]
    public void TryGetError_ParsesServerErrorShape()
    {
        var ok = Evt("room.join", new { success = true });
        Assert.False(ok.TryGetError(out _, out _));

        var bad = Evt("room.join", new { error = new { code = "room_protocol_mismatch", message = "协议不符" } });
        Assert.True(bad.TryGetError(out var code, out var message));
        Assert.Equal("room_protocol_mismatch", code);
        Assert.Equal("协议不符", message);
    }

    [Fact]
    public void UrlNormalization_StripsLegacyHubSuffix()
    {
        Assert.Equal("http://x:8080", BgiGatewayClient.NormalizeBaseUrl("http://x:8080/hub", out var s1));
        Assert.True(s1);
        Assert.Equal("http://x:8080", BgiGatewayClient.NormalizeBaseUrl("http://x:8080/HUB/", out var s2));
        Assert.True(s2);
        Assert.Equal("http://x:8080", BgiGatewayClient.NormalizeBaseUrl("http://x:8080", out var s3));
        Assert.False(s3);
        // 非 /hub 尾巴不猜不动
        Assert.Equal("http://x:8080/api", BgiGatewayClient.NormalizeBaseUrl("http://x:8080/api", out var s4));
        Assert.False(s4);
        Assert.Equal("http://x:8080/gateway", BgiGatewayClient.BuildGatewayUrl("http://x:8080"));
        Assert.Equal("http://x:8080/gateway", BgiGatewayClient.BuildGatewayUrl("http://x:8080/"));
    }

    // =========================================================================
    // evt 分发：字符串载荷组（AllArrived/AllFightDone/RoomClosed/KazuhaPlayerUpdated/CollectiveSkipDegraded）
    // =========================================================================

    [Fact]
    public void DispatchEvt_StringPayloadEvents()
    {
        var client = new CoordinatorClient();
        string? arrived = null, fightDone = null, closed = null, kazuhaPlayer = null, degraded = null;
        client.AllArrived += v => arrived = v;
        client.AllFightDone += v => fightDone = v;
        client.RoomClosed += v => closed = v;
        client.KazuhaPlayerUpdated += v => kazuhaPlayer = v;
        client.CollectiveSkipDegradedReceived += v => degraded = v;

        client.DispatchEvt(Evt("sync.allArrived", new { syncPointId = "sp1" }));
        client.DispatchEvt(Evt("fight.allDone", new { syncPointId = "sk2" }));
        client.DispatchEvt(Evt("room.closed", new { reason = "房主已关闭房间" }));
        client.DispatchEvt(Evt("kazuha.playerUpdated", new { playerUid = "uid9" }));
        client.DispatchEvt(Evt("sync.collectiveSkipDegraded", new { reason = "ConsecutiveCollectiveSkipExceeded" }));

        Assert.Equal("sp1", arrived);
        Assert.Equal("sk2", fightDone);
        Assert.Equal("房主已关闭房间", closed);
        Assert.Equal("uid9", kazuhaPlayer);
        Assert.Equal("ConsecutiveCollectiveSkipExceeded", degraded);
    }

    // =========================================================================
    // evt 分发：数值载荷组（StartRoute int / RequestSkipToProgress long / HostReadyChanged bool）
    // =========================================================================

    [Fact]
    public void DispatchEvt_NumericPayloadEvents()
    {
        var client = new CoordinatorClient();
        int? route = null; long? skipTo = null; bool? ready = null;
        client.StartRouteReceived += v => route = v;
        client.RequestSkipToProgressReceived += v => skipTo = v;
        client.HostReadyChanged += v => ready = v;

        client.DispatchEvt(Evt("room.startRoute", new { routeIndex = 7 }));
        client.DispatchEvt(Evt("sync.requestSkipToProgress", new { targetProgress = 3004005L }));
        client.DispatchEvt(Evt("room.hostReadyChanged", new { ready = true }));

        Assert.Equal(7, route);
        Assert.Equal(3004005L, skipTo);
        Assert.True(ready);
    }

    // =========================================================================
    // evt 分发：无载荷组（verificationPassed / variantPassed / verificationAllDone / allCapReached / allJoined）
    // =========================================================================

    [Fact]
    public void DispatchEvt_NoPayloadEvents()
    {
        var client = new CoordinatorClient();
        var fired = new HashSet<string>();
        client.RouteVerificationPassed += () => fired.Add("verificationPassed");
        client.RouteVariantConsistencyPassed += () => fired.Add("variantPassed");
        client.RouteVerificationAllDone += () => fired.Add("verificationAllDone");
        client.AllReachedExpCap += () => fired.Add("allCapReached");
        client.AllWorldJoined += () => fired.Add("allJoined");

        client.DispatchEvt(Evt("route.verificationPassed", null));
        client.DispatchEvt(Evt("route.variantConsistencyPassed", null));
        client.DispatchEvt(Evt("route.verificationAllDone", null));
        client.DispatchEvt(Evt("exp.allCapReached", null));
        client.DispatchEvt(Evt("world.allJoined", null));

        Assert.Equal(5, fired.Count);
    }

    // =========================================================================
    // evt 分发：PlayerListUpdated（计数/房主/镜像/缓存副作用一并核对）
    // =========================================================================

    [Fact]
    public void DispatchEvt_PlayerListChanged_UpdatesStateAndFires()
    {
        var client = new CoordinatorClient();
        List<PlayerInfo>? received = null;
        client.PlayerListUpdated += l => received = l;

        client.DispatchEvt(Evt("room.playerListChanged", new
        {
            players = new List<PlayerInfo>
            {
                new() { PlayerUid = "host_uid", PlayerName = "房主" },
                new() { PlayerUid = "member_uid", PlayerName = "成员" },
            }
        }));

        Assert.NotNull(received);
        Assert.Equal(2, received!.Count);
        Assert.Equal(2, client.CurrentRoomPlayerCount);
        Assert.Equal("host_uid", client.HostPlayerUid); // list[0] 为房主
        Assert.Equal(2, client.CurrentPlayerList.Count);
        Assert.Equal("房主", client.GetPlayerDisplayName("host_uid")); // 名称缓存已更新
    }

    // =========================================================================
    // evt 分发：复杂载荷组（diff / variantFailed / versionCheckRejected / hostRouteListReady / kazuha / memberStatus）
    // =========================================================================

    [Fact]
    public void DispatchEvt_ComplexPayloadEvents()
    {
        var client = new CoordinatorClient();
        List<string>? diff = null, hostRoutes = null;
        string? variantLogicalId = null;
        Dictionary<string, RouteVariantSchemaItem>? variantItems = null;
        VersionCheckResult? rejected = null;
        (string uid, string key, double x, double y)? collect = null;
        (string uid, string status, long target)? memberStatus = null;

        client.RouteDiffReceived += d => diff = d;
        client.RouteVariantConsistencyFailed += (id, items) => { variantLogicalId = id; variantItems = items; };
        client.VersionCheckRejected += r => rejected = r;
        client.HostRouteListReady += r => hostRoutes = r;
        client.KazuhaCollectStarted += (uid, key, x, y) => collect = (uid, key, x, y);
        client.MemberStatusChangedReceived += (uid, status, target) => memberStatus = (uid, status, target);

        client.DispatchEvt(Evt("route.diffReceived", new { diffFiles = new List<string> { "a.json", "b.json" } }));
        client.DispatchEvt(Evt("route.variantConsistencyFailed", new
        {
            logicalId = "lri_1",
            playerItems = new Dictionary<string, RouteVariantSchemaItem> { ["conn1"] = new() }
        }));
        // 版本校验：整个 payload 即 VersionCheckResult（非包装）
        client.DispatchEvt(Evt("room.versionCheckRejected", new VersionCheckResult
        {
            Compatible = false, MemberVersion = "1.2.3", BaselineVersion = "1.2.4", Hint = "请统一版本"
        }));
        client.DispatchEvt(Evt("room.hostRouteListReady", new { routeNames = new List<string> { "r1" } }));
        client.DispatchEvt(Evt("kazuha.collectStarted",
            new { playerUid = "uid1", syncKey = "sk", collectX = 1.5, collectY = -2.5 }));
        client.DispatchEvt(Evt("room.memberStatusChanged",
            new { playerUid = "uid2", status = "Offline", targetProgress = long.MaxValue }));

        Assert.Equal(new[] { "a.json", "b.json" }, diff);
        Assert.Equal("lri_1", variantLogicalId);
        Assert.NotNull(variantItems);
        Assert.True(variantItems!.ContainsKey("conn1"));
        Assert.NotNull(rejected);
        Assert.Equal("1.2.3", rejected!.MemberVersion);
        Assert.Equal("1.2.4", rejected.BaselineVersion);
        Assert.Equal("请统一版本", rejected.Hint);
        Assert.Equal(new[] { "r1" }, hostRoutes);
        Assert.NotNull(collect);
        Assert.Equal("uid1", collect!.Value.uid);
        Assert.Equal("sk", collect.Value.key);
        Assert.Equal(1.5, collect.Value.x);
        Assert.Equal(-2.5, collect.Value.y);
        Assert.NotNull(memberStatus);
        Assert.Equal(("uid2", "Offline", long.MaxValue), memberStatus!.Value);
    }

    // =========================================================================
    // evt 分发：异常协调三事件（"过滤自己"守卫）
    // =========================================================================

    [Fact]
    public void DispatchEvt_AnomalyEvents_FilterSelf()
    {
        var client = new CoordinatorClient();
        SetPlayerUid(client, "self_uid");
        (string uid, int idx, bool passed)? notified = null;
        (string uid, int idx, int fp)? fightPoint = null;
        string? recovered = null;
        client.PlayerAnomalyNotifyReceived += (u, i, p) => notified = (u, i, p);
        client.PlayerAnomalyNotifyFightPointReceived += (u, i, f) => fightPoint = (u, i, f);
        client.PlayerAnomalyRecoveredReceived += u => recovered = u;

        // 自己的回环一律过滤
        client.DispatchEvt(Evt("anomaly.playerNotified", new { playerUid = "self_uid", routeIndex = 1, passedSyncPoint = true }));
        client.DispatchEvt(Evt("anomaly.fightPointNotified", new { playerUid = "self_uid", routeIndex = 1, fightPointId = 2 }));
        client.DispatchEvt(Evt("anomaly.playerRecovered", new { playerUid = "self_uid" }));
        Assert.Null(notified);
        Assert.Null(fightPoint);
        Assert.Null(recovered);

        // 他人的正常分发（参数顺序：uid / routeIndex / passedSyncPoint|fightPointId）
        client.DispatchEvt(Evt("anomaly.playerNotified", new { playerUid = "other", routeIndex = 3, passedSyncPoint = true }));
        client.DispatchEvt(Evt("anomaly.fightPointNotified", new { playerUid = "other", routeIndex = 4, fightPointId = 9 }));
        client.DispatchEvt(Evt("anomaly.playerRecovered", new { playerUid = "other" }));
        Assert.Equal(("other", 3, true), notified);
        Assert.Equal(("other", 4, 9), fightPoint);
        Assert.Equal("other", recovered);
    }

    // =========================================================================
    // evt 分发：未知名与畸形载荷
    // =========================================================================

    [Fact]
    public void DispatchEvt_UnknownName_IgnoredWithoutThrow()
    {
        var client = new CoordinatorClient();
        var fired = false;
        client.AllArrived += _ => fired = true;

        // 服务器可能发来本客户端未订阅的锄地房间事件（如 route.enforceSync）——静默忽略
        client.DispatchEvt(Evt("route.enforceSync", new { routeIndex = 1 }));
        client.DispatchEvt(Evt("control.playersUpdated", new { type = "full" }));

        Assert.False(fired);
    }

    [Fact]
    public void DispatchEvt_MalformedPayload_NotApplied()
    {
        var client = new CoordinatorClient();
        var fired = false;
        client.PlayerListUpdated += _ => fired = true;

        // players 不是数组 → 对齐旧 On<T> 反序列化失败不触发的语义：不应用、不触发、不抛
        client.DispatchEvt(Evt("room.playerListChanged", new { players = 123 }));

        Assert.False(fired);
        Assert.Equal(0, client.CurrentRoomPlayerCount);
    }
}
