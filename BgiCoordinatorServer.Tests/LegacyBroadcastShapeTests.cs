using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 回归测试（2026-09-04 实机踩中）：GatewayBroadcaster 旧协议一侧必须用 SendCoreAsync
/// 传参数数组。IClientProxy.SendAsync 只有逐参重载（arg1..arg10），把 object?[] 变量
/// 传进去会被绑成 arg1=数组本身 → 线上消息 arguments[0]=[原参数数组] 双层嵌套 →
/// 客户端 On&lt;T&gt; 反序列化静默失败、事件处理器不触发（现象：成员列表全空）。
/// 本测试直接断言 SendCoreAsync 收到的参数形状，防复发。
/// </summary>
public class LegacyBroadcastShapeTests
{
    private static GatewayBroadcaster NewBroadcaster(GatewayTestHarness h)
        => new(h.LegacyHub.Object, h.GatewayHub.Object, h.Tracker,
            new Mock<ILogger<GatewayBroadcaster>>().Object);

    // 表达式树不允许 is 模式声明，形状断言收敛到静态方法
    private static bool IsSingleArg(object?[] args, object expected)
        => args.Length == 1 && ReferenceEquals(args[0], expected);

    private static bool IsMemberStatusArgs(object?[] args)
        => args.Length == 3
           && args[0] as string == "u1"
           && args[1] as string == "Offline"
           && args[2] is long && (long)args[2]! == long.MaxValue;

    private static bool IsSingleInt7(object?[] args)
        => args.Length == 1 && args[0] is int && (int)args[0]! == 7;

    private static bool IsEvtEnvelope(object?[] args, string name)
        => args.Length == 1 && args[0] is GatewayEnvelope e && e.Name == name;

    [Fact]
    public async Task BroadcastGroup_LegacySide_SingleUpdateArg_NotDoubleWrapped()
    {
        var h = new GatewayTestHarness();
        var b = NewBroadcaster(h);
        // 带宽优化后 ControlRoomPlayersUpdated 改为单参数对象（evt 与 legacy 同为 update 本体）
        var update = new ControlRoomPlayersUpdate
        {
            Full = true,
            Revision = 1,
            Players = [new ControlRoomPlayer { PlayerUid = "u1" }],
        };

        await b.BroadcastGroupAsync("CTRL_X", "ControlRoomPlayersUpdated", update, update);

        // 旧协议一侧：arguments 必须恰好 1 个元素且就是 update 本体（不能是 object?[] 嵌套）
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "ControlRoomPlayersUpdated",
            It.Is<object?[]>(args => IsSingleArg(args, update)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastGroup_LegacySide_MultiArgs_PreservesArityAndOrder()
    {
        var h = new GatewayTestHarness();
        var b = NewBroadcaster(h);

        await b.BroadcastGroupAsync("R1", "MemberStatusChanged",
            new { playerUid = "u1", status = "Offline", targetProgress = long.MaxValue },
            "u1", "Offline", long.MaxValue);

        // 旧协议一侧：3 个位置参数逐位透传（旧客户端 On<string,string,long> 依赖元数与顺序）
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "MemberStatusChanged",
            It.Is<object?[]>(args => IsMemberStatusArgs(args)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToConnection_LegacySide_NotDoubleWrapped()
    {
        var h = new GatewayTestHarness();
        var b = NewBroadcaster(h);

        await b.SendToConnectionAsync("conn-legacy-1", "AllReadyConfirm", new { generation = 7 }, 7);

        h.LegacyClientProxy.Verify(p => p.SendCoreAsync(
            "AllReadyConfirm",
            It.Is<object?[]>(args => IsSingleInt7(args)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BroadcastGroup_EvtSide_StillSingleEnvelope()
    {
        var h = new GatewayTestHarness();
        var b = NewBroadcaster(h);
        var players = new List<string> { "u1" };

        await b.BroadcastGroupAsync("R2", "PlayerListUpdated", new { players }, players);

        // evt 一侧：单个 GatewayEnvelope 参数（新客户端约定），不受旧侧修复影响
        h.GatewayGroupProxy.Verify(p => p.SendCoreAsync(
            GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => IsEvtEnvelope(args, "room.playerListChanged")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
