using System.Reflection;
using System.Text.RegularExpressions;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>网关协议映射表完整性测试（纯数据断言，§4.3/§4.7）。</summary>
public class GatewayProtocolTests
{
    /// <summary>客户端订阅核对清单（CoordinatorClient.cs / SignalRClient.cs / control-room.js）。</summary>
    private static readonly string[] ClientSubscribedLegacyEvents =
    [
        "PlayerListUpdated", "AllArrived", "AllFightDone", "RouteDiffReceived",
        "RouteVerificationPassed", "RouteVariantConsistencyPassed", "RouteVariantConsistencyFailed",
        "RoomClosed", "VersionCheckRejected", "RouteVerificationAllDone", "AllReachedExpCap",
        "KazuhaPlayerUpdated", "KazuhaCollectStarted", "AllWorldJoined", "HostReadyChanged",
        "HostRouteListReady", "PlayerAnomalyNotify", "PlayerAnomalyNotifyFightPoint",
        "PlayerAnomalyRecovered", "MemberStatusChanged", "StartRoute", "RequestSkipToProgress",
        "CollectiveSkipDegraded", "ControlRoomPlayersUpdated", "RemoteCommand", "JoinRejected",
        "AllReady", "AllReadyConfirm", "MemberScreenshot", "MemberScreenshotRequested", "MemberLogBatch",
        "MemberLogSubscribersChanged", "MemberLogFilesRequested", "MemberLogFileList",
        "MemberLogDownloadRequested", "MemberLogFileChunk", "RemoteCommandAck",
        "AbnormalPlayerRecovered", "UnifiedWaitPoint", "AllPlayersArrived", "RouteEnforceSync",
    ];

    [Fact]
    public void LegacyMethodMap_CoversExactlyAllPublicHubMethods()
    {
        // "65 方法一个不漏"的机器校验：CoordinatorHub 公开实例方法（declared）
        // 减去 object 继承（DeclaredOnly 已排除）/ OnDisconnectedAsync（override，非业务方法）/ 属性 getter
        var hubMethods = typeof(CoordinatorHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.Name != nameof(CoordinatorHub.OnDisconnectedAsync))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var mapKeys = GatewayProtocol.LegacyMethodMap.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(67, GatewayProtocol.LegacyMethodMap.Count);

        var missing = hubMethods.Except(mapKeys).ToList();
        var extra = mapKeys.Except(hubMethods).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"映射表与 Hub 方法集不一致。缺失: [{string.Join(", ", missing)}]；多余: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void LegacyEventMap_CoversAllClientSubscribedEvents_WithNonEmptyValues()
    {
        foreach (var legacyEvent in ClientSubscribedLegacyEvents)
        {
            Assert.True(GatewayProtocol.LegacyEventMap.TryGetValue(legacyEvent, out var newName),
                $"LegacyEventMap 缺少客户端订阅的旧事件：{legacyEvent}");
            Assert.False(string.IsNullOrWhiteSpace(newName), $"{legacyEvent} 映射的新事件名为空");
        }

        // 双向完整性：map 里不应有清单之外的多余项
        var extra = GatewayProtocol.LegacyEventMap.Keys
            .Except(ClientSubscribedLegacyEvents, StringComparer.Ordinal).ToList();
        Assert.True(extra.Count == 0, $"LegacyEventMap 存在清单之外的多余键: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void LegacyMethodMap_ValuesAreNonEmpty_AndDottedShape()
    {
        var shape = new Regex(@"^[a-z]+\.[a-zA-Z]+$", RegexOptions.Compiled);
        foreach (var (legacy, newName) in GatewayProtocol.LegacyMethodMap)
        {
            Assert.False(string.IsNullOrWhiteSpace(newName), $"{legacy} 映射的新消息名为空");
            Assert.True(shape.IsMatch(newName), $"{legacy} 映射值 \"{newName}\" 不符合 \"<域>.<动作>\" 形态");
        }
    }

    /// <summary>路由完备性：LegacyMethodMap 的每个消息名值都已注册进 Dispatcher 路由表（防未来漏注册）。</summary>
    [Fact]
    public void LegacyMethodMap_ValuesAllRegistered_InDispatcherRoutingTable()
    {
        var harness = new GatewayTestHarness();
        var registered = new HashSet<string>(harness.Dispatcher.RegisteredNames, StringComparer.Ordinal);
        var unregistered = GatewayProtocol.LegacyMethodMap.Values
            .Distinct(StringComparer.Ordinal)
            .Where(n => !registered.Contains(n))
            .ToList();
        Assert.True(unregistered.Count == 0,
            $"以下消息名未注册进 Dispatcher 路由表: [{string.Join(", ", unregistered)}]");
    }
}
