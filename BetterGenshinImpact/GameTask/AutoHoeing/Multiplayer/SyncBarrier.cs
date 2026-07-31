#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 同步屏障：在同步点等待所有玩家到达。
/// 简化版：移除路线跳过信号、等待点缓存等旧机制。
/// </summary>
public class SyncBarrier
{
    private readonly ILogger<SyncBarrier> _logger = App.GetLogger<SyncBarrier>();
    private readonly CoordinatorClient _client;
    private readonly int _defaultTimeoutSeconds;

    public SyncBarrier(CoordinatorClient client, int timeoutSeconds = 60)
    {
        _client = client;
        _defaultTimeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    /// 等待集合点同步
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true=正常同步完成，false=超时放行</returns>
    public async Task<bool> WaitAsync(string syncPointId, CancellationToken ct)
    {
        return await WaitAsync(syncPointId, 0, _defaultTimeoutSeconds, ct);
    }

    /// <summary>
    /// 等待集合点同步（带超时参数）
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    /// <param name="timeoutSeconds">超时秒数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true=正常同步完成，false=超时放行</returns>
    public async Task<bool> WaitAsync(string syncPointId, int expectedCount, int timeoutSeconds, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        _logger.LogInformation("[SyncBarrier] 开始等待集合点: {SyncId}，超时={Timeout}s，预期人数={Expected}",
            syncPointId, timeoutSeconds, expectedCount > 0 ? expectedCount.ToString() : "全部");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Action<string>? handler = null;
        handler = (arrivedSyncPointId) =>
        {
            _logger.LogInformation("[SyncBarrier] 收到 AllArrived 广播: {Arrived}，等待的: {SyncId}", arrivedSyncPointId, syncPointId);
            if (arrivedSyncPointId == syncPointId)
                tcs.TrySetResult(true);
        };

        _client.AllArrived += handler;
        Action<string>? roomClosedHandler = null;
        roomClosedHandler = (reason) =>
        {
            _logger.LogWarning("[SyncBarrier] 收到 RoomClosed，停止等待集合点: {SyncId}，原因: {Reason}", syncPointId, reason);
            tcs.TrySetResult(false);
        };
        _client.RoomClosed += roomClosedHandler;
        try
        {
            // 上报到达（带预期人数）
            await _client.ReportArrivalAsync(syncPointId, expectedCount);

            using var reg = linkedCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("[SyncBarrier] 外部取消: {SyncId}", syncPointId);
                    tcs.TrySetCanceled(ct);
                }
                else
                {
                    _logger.LogWarning("[SyncBarrier] 等待超时({Timeout}s)，放行: {SyncId}", timeoutSeconds, syncPointId);
                    tcs.TrySetResult(false);
                }
            });

            // 在等待 AllArrived 期间，每 5 秒重试一次上报到达，弥补网络波动导致的上报丢失或错过广播
            const int retryIntervalMs = 5000;
            while (true)
            {
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(retryIntervalMs, linkedCts.Token));
                if (completed == tcs.Task)
                {
                    // 收到 AllArrived（tcs.Task 完成）→ 退出循环
                    break;
                }
                // 5 秒到了还没收到 AllArrived → 重试上报
                _logger.LogDebug("[SyncBarrier] 等待中，重试上报到达: {SyncId}", syncPointId);
                await _client.ReportArrivalAsync(syncPointId, expectedCount);
            }

            var result = await tcs.Task;
            _logger.LogInformation("[SyncBarrier] 等待完成: {SyncId}，结果: {Result}（true=全员到达，false=超时放行）", syncPointId, result);
            return result;
        }
        finally
        {
            _client.AllArrived -= handler;
            _client.RoomClosed -= roomClosedHandler;
        }
    }

    /// <summary>
    /// 重置状态（每轮开始时调用）
    /// </summary>
    public void Reset()
    {
        _logger.LogDebug("[SyncBarrier] 状态已重置");
    }

    public async ValueTask DisposeAsync()
    {
        // 简化实现，无资源需要释放
        await Task.CompletedTask;
    }
}
