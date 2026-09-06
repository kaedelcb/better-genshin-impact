using System.Text.Json.Nodes;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 网关/兼容层测试的共享构造器：RoomManager → GatewayBroadcaster → RoomPhaseObserver
/// → RoomOperations → GatewayDispatcher，两侧 IHubContext 全 mock（Clients.Group/Client、
/// Groups 均 setup，SendCoreAsync/AddToGroupAsync 返回已完成 Task，避免 NullReference）。
/// </summary>
internal sealed class GatewayTestHarness
{
    public RoomManager RoomManager { get; }
    public GatewaySessionTracker Tracker { get; } = new();
    public Mock<IHubContext<CoordinatorHub>> LegacyHub { get; } = new();
    public Mock<IHubContext<GatewayHub>> GatewayHub { get; } = new();
    public Mock<IClientProxy> LegacyGroupProxy { get; } = new();
    public Mock<IClientProxy> GatewayGroupProxy { get; } = new();
    public Mock<ISingleClientProxy> LegacyClientProxy { get; } = new();
    public Mock<ISingleClientProxy> GatewayClientProxy { get; } = new();
    /// <summary>多连接定向发送（Clients.Clients(list)，MemberLogBatch 只发订阅者路径用）。</summary>
    public Mock<IClientProxy> LegacyMultiClientProxy { get; } = new();
    public Mock<IClientProxy> GatewayMultiClientProxy { get; } = new();
    public Mock<IGroupManager> LegacyGroups { get; } = new();
    public Mock<IGroupManager> GatewayGroups { get; } = new();
    public RoomOperations Ops { get; }
    public GatewayDispatcher Dispatcher { get; }

    public GatewayTestHarness()
    {
        RoomManager = new RoomManager(50, new Mock<ILogger<RoomManager>>().Object);

        LegacyHub.Setup(h => h.Clients.Group(It.IsAny<string>())).Returns(LegacyGroupProxy.Object);
        LegacyHub.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns(LegacyClientProxy.Object);
        LegacyHub.Setup(h => h.Clients.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(LegacyMultiClientProxy.Object);
        GatewayHub.Setup(h => h.Clients.Group(It.IsAny<string>())).Returns(GatewayGroupProxy.Object);
        GatewayHub.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns(GatewayClientProxy.Object);
        GatewayHub.Setup(h => h.Clients.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(GatewayMultiClientProxy.Object);
        LegacyHub.Setup(h => h.Groups).Returns(LegacyGroups.Object);
        GatewayHub.Setup(h => h.Groups).Returns(GatewayGroups.Object);

        SetupSendCompleted(LegacyGroupProxy);
        SetupSendCompleted(GatewayGroupProxy);
        SetupSendCompleted(LegacyClientProxy);
        SetupSendCompleted(GatewayClientProxy);
        SetupSendCompleted(LegacyMultiClientProxy);
        SetupSendCompleted(GatewayMultiClientProxy);
        foreach (var groups in new[] { LegacyGroups, GatewayGroups })
        {
            groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        var broadcaster = new GatewayBroadcaster(LegacyHub.Object, GatewayHub.Object, Tracker,
            new Mock<ILogger<GatewayBroadcaster>>().Object);
        var phaseObserver = new RoomPhaseObserver(new Mock<ILogger<RoomPhaseObserver>>().Object);
        Ops = new RoomOperations(RoomManager, new Mock<ILogger<RoomOperations>>().Object, broadcaster, phaseObserver);
        Dispatcher = new GatewayDispatcher(Tracker, new Mock<ILogger<GatewayDispatcher>>().Object, Ops);
    }

    private static void SetupSendCompleted<T>(Mock<T> proxy) where T : class, IClientProxy
        => proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

    /// <summary>构造 session.hello 信封（payload 走 camelCase STJ，与线上一致）。</summary>
    public static GatewayEnvelope HelloEnvelope(int protocolVersion = 3) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = GatewayProtocol.Names.SessionHello,
        Payload = GatewayEnvelope.ToPayload(new ClientHello
        {
            ClientKind = "bgi",
            ClientVersion = "1.0.0",
            ProtocolVersion = protocolVersion,
            Capabilities = [],
        }),
    };

    /// <summary>提取响应 payload.error.code；无 error 返回 null。</summary>
    public static string? ErrorCode(GatewayEnvelope resp)
    {
        if (resp.Payload != null
            && resp.Payload.TryGetPropertyValue("error", out var node)
            && node is JsonObject err)
        {
            return err["code"]?.GetValue<string>();
        }
        return null;
    }
}
