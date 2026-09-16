#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 阶段 6 客户端半边：锚点模式激活时，旧集体跳段命令必须被忽略（route-anchor）。
///
/// 两套控制器都会移动成员（旧机制"跳段"、锚点"跳路线"），同时生效正是"走散"的成因。
/// 服务端半边已在附录 M 隔离（不判定/不武装定时器/不新建命令），本文件锁定客户端半边：
/// 锚点激活时收到旧集体跳段命令 → 不登记、不置信号位、不唤醒等待（否则会提前放行正在等的同步点）。
/// </summary>
public class RouteAnchorCollectiveSkipIsolationTests
{
    private const string Capability = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability;

    private static CoordinatorClient Client(bool serverSupportsRouteAnchor)
    {
        var client = new CoordinatorClient();
        client._testIsConnectedOverride = true;
        var gateway = client.GetOrCreateGatewayForTest();
        typeof(BgiGatewayClient)
            .GetProperty(nameof(BgiGatewayClient.ServerCapabilities), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(gateway, serverSupportsRouteAnchor ? new[] { Capability } : Array.Empty<string>());
        return client;
    }

    private static MultiplayerCoordinator Coordinator(CoordinatorClient client, bool configEnabled)
        => new(client, new SyncPointResolver(), new AutoHoeingConfig
        {
            MultiplayerEnabled = true,
            EnableRouteAnchor = configEnabled,
        });

    private static GatewayEnvelope SkipEvt(string skipId) => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = GatewayProtocol.Events.SyncRequestSkipToProgress,
        Payload = GatewayEnvelope.ToPayload(new { skipId, targetProgress = 3_000_000L }),
        SentAtUtc = DateTime.UtcNow,
    };

    // =========================================================================
    // 激活判定
    // =========================================================================

    [Fact]
    public void IsRouteAnchorActive_RequiresBothConfigAndServerCapability()
    {
        RemoteSkipGate.Reset();
        var withCap = Client(serverSupportsRouteAnchor: true);
        var withoutCap = Client(serverSupportsRouteAnchor: false);

        Assert.True(Coordinator(withCap, configEnabled: true).IsRouteAnchorActive);
        Assert.False(Coordinator(withCap, configEnabled: false).IsRouteAnchorActive);      // 配置关闭
        Assert.False(Coordinator(withoutCap, configEnabled: true).IsRouteAnchorActive);    // 服务端不支持
    }

    // =========================================================================
    // 锚点激活 → 旧集体跳段命令被完全忽略
    // =========================================================================

    [Fact]
    public void AnchorActive_CollectiveSkipCommand_IsIgnored_NoSignalNoWake()
    {
        RemoteSkipGate.Reset();
        var client = Client(serverSupportsRouteAnchor: true);
        var coordinator = Coordinator(client, configEnabled: true);
        Assert.False(RemoteSkipGate.Token.IsCancellationRequested);

        client.DispatchEvt(SkipEvt("skip-1"));

        // 不登记（TryConsume 永远是 false）……
        Assert.False(coordinator.TryConsumeCollectiveSkip(out var consumed));
        Assert.Null(consumed);
        // ……也不唤醒正在等待的同步点（否则会提前放行，等于绕过了锚点屏障）
        Assert.False(RemoteSkipGate.Token.IsCancellationRequested);
    }

    // =========================================================================
    // 锚点未激活 → 旧行为逐字保留（不得因为新代码而失去旧机制）
    // =========================================================================

    [Fact]
    public void AnchorInactive_CollectiveSkipCommand_StillWorksAsBefore()
    {
        RemoteSkipGate.Reset();
        var client = Client(serverSupportsRouteAnchor: true);
        var coordinator = Coordinator(client, configEnabled: false);   // 配置关闭 → 锚点未激活

        client.DispatchEvt(SkipEvt("skip-2"));

        Assert.True(coordinator.TryConsumeCollectiveSkip(out var consumed));
        Assert.NotNull(consumed);
        Assert.Equal("skip-2", consumed!.SkipId);
        Assert.True(RemoteSkipGate.Token.IsCancellationRequested);      // 旧行为：唤醒等待
    }

    [Fact]
    public void ServerWithoutCapability_ClientStillUsesOldCollectiveSkip()
    {
        RemoteSkipGate.Reset();
        var client = Client(serverSupportsRouteAnchor: false);          // 旧服务端
        var coordinator = Coordinator(client, configEnabled: true);     // 客户端配置打开也没用

        client.DispatchEvt(SkipEvt("skip-3"));

        Assert.True(coordinator.TryConsumeCollectiveSkip(out var consumed));
        Assert.Equal("skip-3", consumed!.SkipId);
    }
}
