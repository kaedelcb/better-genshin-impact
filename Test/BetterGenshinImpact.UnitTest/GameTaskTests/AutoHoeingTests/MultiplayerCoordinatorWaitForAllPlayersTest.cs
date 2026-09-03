#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
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
/// 本测试沿用仓库既有模式（CoordinatorClientWaitPointTests）：new 一个真实 CoordinatorClient，
/// 用反射把 Mock 的 HubConnection 注入其私有字段 _connection，从而驱动真实等待逻辑。
/// AllArrived 事件、_testIsConnectedOverride 均通过反射 / InternalsVisibleTo 访问。
/// </summary>
public class MultiplayerCoordinatorWaitForAllPlayersTest : IDisposable
{
    private const string SyncId = "sync_point_1";
    private const string Method = "WaitForAllPlayers";

    private readonly Mock<HubConnection> _mockConnection;
    private readonly CoordinatorClient _client;

    public MultiplayerCoordinatorWaitForAllPlayersTest()
    {
        _mockConnection = new Mock<HubConnection>();
        _mockConnection.SetupGet(c => c.State).Returns(HubConnectionState.Connected);

        _client = new CoordinatorClient();
        var connectionField = typeof(CoordinatorClient).GetField(
            "_connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        connectionField?.SetValue(_client, _mockConnection.Object);
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

    // =========================================================================
    // 1. 严格等待成功：CoordinatorClient 正常返回（匹配 AllArrived 已消费）→ true
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_StrictWaitConsumed_ReturnsTrue()
    {
        // Arrange: SendAsync 完成，并在调用后广播匹配的 AllArrived（subscribe-before-action）
        _mockConnection
            .Setup(c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback(() => RaiseAllArrived(SyncId))
            .Returns(Task.CompletedTask);

        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 匹配 AllArrived 被消费 → 严格等待完成 → true
        Assert.True(result);
    }

    // =========================================================================
    // 2. 取消 / 超时 / 关房 / 断线 / 异常：CoordinatorClient 抛出 → false
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_CancellationRethrown_ReturnsFalse()
    {
        // Arrange: 客户端等待被取消（OperationCanceledException）
        _mockConnection
            .Setup(c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

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
        _mockConnection
            .Setup(c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection closed"));

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
        // Arrange: SendAsync 完成，但只广播"其它同步点"的 AllArrived（不匹配 SyncId）。
        // 这验证 subscribe-before-action + 按 syncId 匹配语义不变：非匹配广播不得放行，
        // 等待继续（此处用已取消 CT 让等待循环快速进入取消路径）。
        _mockConnection
            .Setup(c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()))
            .Callback(() => RaiseAllArrived("another_sync_point"))
            .Returns(Task.CompletedTask);

        var coordinator = CreateCoordinator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, cts.Token);

        // Assert: 非匹配 AllArrived 不放行 → 等待未确认完成（取消路径）→ false
        Assert.False(result);

        // 协议调用确实发生（SendAsync 被调用，事件也广播了），只是未匹配 → 未确认
        _mockConnection.Verify(
            c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =========================================================================
    // 3. 未连接：CoordinatorClient 直接返回 false → 门面返回 false
    // =========================================================================

    [Fact]
    public async Task WaitForAllPlayers_ClientNotConnected_ReturnsFalse()
    {
        // Arrange: 客户端未连接（_testIsConnectedOverride=false，绕开真实 HubConnection）
        _client._testIsConnectedOverride = false;
        var coordinator = CreateCoordinator();

        // Act
        var result = await coordinator.WaitForAllPlayers(SyncId, CancellationToken.None);

        // Assert: 未连接 → 无匹配 AllArrived 被消费 → false
        Assert.False(result);

        // 未连接时应直接短路，不触发任何协议调用（SendAsync）
        _mockConnection.Verify(
            c => c.SendAsync(Method, It.IsAny<object?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}