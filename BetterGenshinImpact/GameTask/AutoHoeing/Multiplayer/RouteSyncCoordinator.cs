#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 路线同步协调器：负责路线级别的同步决策。
/// 在路线开始时检查所有玩家进度，决定是继续、等待还是跳过。
/// </summary>
public class RouteSyncCoordinator
{
    private readonly ILogger<RouteSyncCoordinator> _logger = App.GetLogger<RouteSyncCoordinator>();
    private readonly CoordinatorClient _client;
    private readonly MultiplayerCoordinator _coordinator;
    private readonly AutoHoeingConfig _config;
    
    // 缓存的玩家路线进度
    private readonly ConcurrentDictionary<string, int> _playerRouteProgress = new();
    private readonly object _cacheLock = new();

    public RouteSyncCoordinator(CoordinatorClient client, MultiplayerCoordinator coordinator, AutoHoeingConfig config)
    {
        _client = client;
        _coordinator = coordinator;
        _config = config;
    }

    /// <summary>
    /// 在路线开始时检查同步状态，返回同步决策
    /// </summary>
    public async Task<RouteSyncDecision> CheckSyncAtRouteStart(int targetRouteIndex, CancellationToken ct)
    {
        try
        {
            // 房主：检查所有成员是否已到达目标路线
            if (_coordinator.IsHost)
            {
                var currentPlayerList = _client.CurrentPlayerList;
                var expectedCount = currentPlayerList.Count(p => !p.IsHost);
                
                if (expectedCount == 0)
                {
                    _logger.LogDebug("[路线同步] 房主：无成员，直接继续");
                    return RouteSyncDecision.Proceed;
                }

                // 查询所有成员的路线进度
                var progress = await _client.QueryRouteProgressAsync(ct);
                if (progress == null || progress.Count == 0)
                {
                    _logger.LogWarning("[路线同步] 房主：无法获取成员进度，继续执行");
                    return RouteSyncDecision.Proceed;
                }

                // 检查是否所有成员都已到达目标路线或更远
                var allReady = progress.All(kv => kv.Value >= targetRouteIndex);
                if (allReady)
                {
                    _logger.LogInformation("[路线同步] 房主：所有成员已到达路线 {RouteIndex}", targetRouteIndex);
                    return RouteSyncDecision.Proceed;
                }

                // 检查是否有成员落后太多（超过容忍范围）
                var minProgress = progress.Values.Min();
                var lagThreshold = _config.MaxRouteLag ?? 2; // 默认允许落后 2 条路线
                if (targetRouteIndex - minProgress > lagThreshold)
                {
                    _logger.LogWarning("[路线同步] 房主：成员落后过多（目标={Target}, 最低={Min}, 阈值={Threshold}），跳过等待",
                        targetRouteIndex, minProgress, lagThreshold);
                    return RouteSyncDecision.Proceed;
                }

                // 需要等待
                _logger.LogInformation("[路线同步] 房主：等待成员赶上路线 {Target}，当前最低={Min}",
                    targetRouteIndex, minProgress);
                return RouteSyncDecision.Wait;
            }
            else
            {
                // 成员：检查自己是否落后房主太多
                var myProgress = _coordinator.CurrentRouteIndex;
                if (myProgress < targetRouteIndex)
                {
                    _logger.LogWarning("[路线同步] 成员：当前进度 {My} 落后于目标 {Target}，需要追赶",
                        myProgress, targetRouteIndex);
                    return RouteSyncDecision.CatchUp;
                }
                return RouteSyncDecision.Proceed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[路线同步] 检查同步状态失败，默认继续");
            return RouteSyncDecision.Proceed;
        }
    }

    /// <summary>
    /// 上报路线完成
    /// </summary>
    public async Task ReportRouteCompletion(int completedRouteIndex)
    {
        try
        {
            await _client.ReportRouteProgressAsync(completedRouteIndex + 1);
            _logger.LogDebug("[路线同步] 上报路线完成: {Index}", completedRouteIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[路线同步] 上报路线完成失败");
        }
    }

    /// <summary>
    /// 刷新缓存的玩家进度
    /// </summary>
    public void RefreshCache()
    {
        lock (_cacheLock)
        {
            _playerRouteProgress.Clear();
            foreach (var player in _client.CurrentPlayerList)
            {
                if (_client.IsRouteExecutor(player.PlayerUid)) _playerRouteProgress[player.PlayerUid] = 0;
            }
        }
    }

    /// <summary>
    /// 重置状态
    /// </summary>
    public void Reset()
    {
        lock (_cacheLock)
        {
            _playerRouteProgress.Clear();
        }
        _logger.LogInformation("[路线同步] 已重置");
    }
}
