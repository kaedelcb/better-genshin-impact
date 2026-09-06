#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// MultiplayerCoordinator.WaitForAllPlayers 单元测试
/// (multiplayer-hoeing-post-teleport-revival-protection spec, Task 3.2)
///
/// **Validates: Requirements 3.8, 4.2**
///
/// 目标：验证门面把"严格等待完成"（CoordinatorClient 正常返回，即匹配 AllArrived 已消费）
/// 映射为 <see langword="true"/>，把取消 / 超时 / 关房 / 断线 / 未连接 / 其它异常映射为
/// <see langword="false"/>，且不改变 CoordinatorClient 的协议调用
/// （subscribe-before-action、按 syncId 匹配、取消 / 超时语义）。
///
/// 注意：CoordinatorClient.WaitForAllPlayersAsync 是非 virtual 具体方法，无法用 Moq 直接 mock。
/// 本测试沿用仓库既有模式：new 一个真实 CoordinatorClient 驱动真实等待逻辑。
/// AllArrived 事件通过反射访问，_testIsConnectedOverride / 网关种子经 InternalsVisibleTo 访问。
///
/// 切片 8 适配：通信改走 /gateway 信封（fire-and-forget Dispatch + sync.waitForAllPlayers）。
/// 旧版测试 mock HubConnection 的做法在 SignalR.Client 8.0 下不可用（State/SendAsync 均为
/// 不可重写成员，Moq 抛 NotSupportedException——旧 5 例因此在基线上全挂），现改用
/// BgiGatewayClient._testSendOverride 种子拦截信封，并顺带断言线形（消息名 + payload 键）。
/// </summary>
public class MultiplayerCoordinatorWaitForAllPlayersTest : IDisposable
{
    private const string SyncId = "sync_point_1";
    private const string V3Name = "sync.waitForAllPlayers";

    private readonly CoordinatorClient _client;
    private readonly BgiGatewayClient _gateway;
    private readonly List<GatewayEnvelope> _sentEnvelopes = new();

    public MultiplayerCoordinatorWaitForAllPlayersTest()
    {
        _client = new CoordinatorClient();
        _client._testIsConnectedOverride = true;
        _gateway = _client.GetOrCreateGatewayForTest();
    }

    public void Dispose()
    {
        _client.DisposeAsync().AsTask().Wait();
    }

    private MultiplayerCoordinator CreateCoordinator()
    {
        var resolver = new SyncPointResolver();
        var config = new AutoHoeingConfig();
        return new MultiplayerCoordinator(_client, resolver, config);
    }

    /// <summary>
    /// 通过反射触发 CoordinatorClient.AllArrived 事件（事件不能从类外直接 Invoke）。
    /// 模拟服务端广播"全员到达 syncId"。沿用仓库既有的反射访问私有成员模式。
    /// </summary>
    private void RaiseAllArrived(string syncId)
    {
        var field = typeof(CoordinatorClient).GetField(
            "AllArrived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = field?.GetValue(_client) as Action<string>;
        handler?.Invoke(syncId);
    }

    /// <summary>断言最后一次发送的信封为 sync.waitForAllPlayers 且 payload 形状正确。</summary>
    private void AssertLastEnvelopeIsWaitForAllPlayers()
    {
        Assert.NotEmpty(_sentEnvelopes);
        var env = _sentEnvelopes[^1];
        Assert.Equal(V3Name, env.Name);
        Assert.Equal(SyncId, env.GetString("syncId"));
        Assert.Equal(-1, env.GetLong("syncProgress"));
    }

    // =========================================================================
    // 1. 严格等待成功：CoordinatorClient 正常返回（匹配 AllArrived 已消费）→ true
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_StrictWaitConsumed_ReturnsTrue()
    {
        // Arrange: 发送完成，并在调用后广播匹配的 AllArrived（subscribe-before-action）
        _gateway._testSendOverride = (_, env, _) =>
        {
            _sentEnvelopes.Add(env);
            RaiseAllArrived(SyncId);
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 匹配 AllArrived 被消费 → 严格等待完成 → true
        Assert.True(result);
        AssertLastEnvelopeIsWaitForAllPlayers();
    }

    // =========================================================================
    // 2. 取消 / 超时 / 关房 / 断线 / 异常：CoordinatorClient 抛出 → false
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_CancellationRethrown_ReturnsFalse()
    {
        // Arrange: 客户端等待被取消（OperationCanceledException）
        _gateway._testSendOverride = (_, _, _) => throw new OperationCanceledException();

        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 取消 → 未确认完成 → false
        Assert.False(result);
    }

    [Fact]
    public async Task WaitForAllPlayers_GenericExceptionRethrown_ReturnsFalse()
    {
        // Arrange: 关房 / 断线 / 网络异常等统一由客户端 rethrow，门面 catch
        _gateway._testSendOverride = (_, _, _) => throw new InvalidOperationException("connection closed");

        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 其它异常 → 未确认完成 → false
        Assert.False(result);
    }

    // =========================================================================
    // 4. 协议保持性：按 syncId 匹配（非匹配 AllArrived 不得放行 → 最终取消返回 false）
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_NonMatchingSyncId_DoesNotReleaseWait()
    {
        // Arrange: 发送完成，但只广播"其它同步点"的 AllArrived（不匹配 SyncId）。
        // 这验证 subscribe-before-action + 按 syncId 匹配语义不变：非匹配广播不得放行，
        // 等待继续（此处用已取消 CT 让等待循环快速进入取消路径）。
        _gateway._testSendOverride = (_, env, _) =>
        {
            _sentEnvelopes.Add(env);
            RaiseAllArrived("another_sync_point");
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, cts.Token);

        // Assert: 非匹配 AllArrived 不放行 → 等待未确认完成（取消路径）→ false
        Assert.False(result);

        // 协议调用确实发生（信封已发出，事件也广播了），只是未匹配 → 未确认
        Assert.Single(_sentEnvelopes);
        AssertLastEnvelopeIsWaitForAllPlayers();
    }

    // =========================================================================
    // 3. 未连接：CoordinatorClient 直接返回 false → 门面返回 false
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_ClientNotConnected_ReturnsFalse()
    {
        // Arrange: 客户端未连接（_testIsConnectedOverride=false，绕开真实网关连接）
        _client._testIsConnectedOverride = false;
        _gateway._testSendOverride = (_, env, _) =>
        {
            _sentEnvelopes.Add(env);
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 未连接 → 无匹配 AllArrived 被消费 → false
        Assert.False(result);

        // 未连接时应直接短路，不触发任何协议调用
        Assert.Empty(_sentEnvelopes);
    }
}
