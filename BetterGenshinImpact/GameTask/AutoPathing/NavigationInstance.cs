using System;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.GameTask.Model.Area;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using OpenCvSharp;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class NavigationInstance
{
    private float _prevX = -1;
    private float _prevY = -1;
    private DateTime _captureTime = DateTime.MinValue;
    private int _consecutiveFailCount = 0;

    // 传送先验专用缓存，不受 Reset() 影响（SetPrevPosition 时同步更新）
    private float _tpPriorX = -1;
    private float _tpPriorY = -1;

    // 任务启动 fresh 首启标志：任务启动时置 true，快速传送 TpOnce 消费后置 false（初次即清除）。
    // A13 开关式纯增量新增：默认 false（行为不变），仅 TaskRunner 任务启动时标记触发主动锚定。
    private bool _tpPriorFresh;

    // 全局回退阈值：每次读配置单例并校验兜底（<1 回落默认 2 + 告警）。UI 实时生效（Requirement 2）。
    private static int GlobalMatchFallbackThreshold
    {
        get
        {
            var cfg = ConfigService.Config?.MiniMapMatchTuningConfig;
            if (cfg == null) return MiniMapMatchTuningConfig.DefaultGlobalMatchFallbackThreshold;
            var (value, fellBack) = MiniMapMatchTuningValidator.ValidateFallbackThreshold(cfg.GlobalMatchFallbackThreshold);
            if (fellBack) MiniMapTuningWarn.OnceFallbackThreshold(cfg.GlobalMatchFallbackThreshold);
            return value;
        }
    }

    // GetPosition 锁等待超时（ms）：每次读配置并校验兜底（<0 回落默认 100 + 告警）。UI 实时生效（Requirement 3）。
    private static int GetPositionLockTimeoutMs
    {
        get
        {
            var cfg = ConfigService.Config?.MiniMapMatchTuningConfig;
            if (cfg == null) return MiniMapMatchTuningConfig.DefaultGetPositionLockTimeoutMs;
            var (value, fellBack) = MiniMapMatchTuningValidator.ValidateLockTimeoutMs(cfg.GetPositionLockTimeoutMs);
            if (fellBack) MiniMapTuningWarn.OnceLockTimeout(cfg.GetPositionLockTimeoutMs);
            return value;
        }
    }

    // 全局匹配回退跳变保护阈值（图像坐标距离）：全局命中坐标距回退前锚点超此值 → 判误匹配。
    // <=0 视为关闭保护。UI 实时生效。
    private static double GlobalMatchJumpGuardThreshold
        => ConfigService.Config?.MiniMapMatchTuningConfig?.GlobalMatchJumpGuardThreshold
           ?? MiniMapMatchTuningConfig.DefaultGlobalMatchJumpGuardThreshold;
    
    public void Reset()
    {
        (_prevX, _prevY) = (-1, -1);
        _consecutiveFailCount = 0;
    }
    
    public void SetPrevPosition(float x, float y)
    {
        (_prevX, _prevY) = (x, y);
        _tpPriorX = x;  // 同步更新传送先验缓存
        _tpPriorY = y;
        // 不重置 _consecutiveFailCount，因为 SetPrevPosition 是外部设置参考点，不代表匹配成功
    }

    /// <summary>只读读取最近一次的位置锚点（图像坐标）。供传送分层先验兜底使用，不改任何识别状态。</summary>
    public (float X, float Y) GetPrevPosition() => (_prevX, _prevY);

    /// <summary>
    /// 获取传送先验专用缓存坐标（不受 Reset 影响）
    /// </summary>
    public (float X, float Y) GetTpPriorPosition() => (_tpPriorX, _tpPriorY);

    /// <summary>
    /// 标记传送先验为"新鲜首启"：任务边界处由 TaskRunner 调用。
    /// 下一次快速传送 TpOnce 将主动小地图识别锚定，避免跨任务陈旧先验误跳过切换地区
    /// （teleport-fastdrag-prior-fresh-acquisition spec）。
    /// </summary>
    public void MarkTpPriorFresh() => _tpPriorFresh = true;

    /// <summary>
    /// 读取并消费新鲜首启标志（读后置 false）。
    /// 同任务内第二次调用返回 false → TpOnce 走非 fresh 缓存路径，保持现状。
    /// </summary>
    public bool ConsumeTpPriorFresh()
    {
        var fresh = _tpPriorFresh;
        _tpPriorFresh = false;
        return fresh;
    }

    private static readonly object GetPositionLock = new object(); 
    public Point2f GetPosition(ImageRegion imageRegion, string mapName, string mapMatchMethod)
    {
        var lockTimeoutMs = GetPositionLockTimeoutMs;
        if (Monitor.TryEnter(GetPositionLock, lockTimeoutMs))
        {
            try
            {
                using var colorMat = new Mat(imageRegion.SrcMat, MapAssets.Get(imageRegion).MimiMapRect);
                var captureTime = DateTime.UtcNow;
                // [小地图诊断] 记录进入时的锚点（局部匹配的搜索中心）
                var diagPrevX = _prevX;
                var diagPrevY = _prevY;
                var diagBranch = "局部匹配命中";
                var p = MapManager.GetMap(mapName, mapMatchMethod).GetMiniMapPosition(colorMat, _prevX, _prevY);

                // 局部匹配失败且有prevPos时，尝试全局匹配回退
                if (p == default && _prevX > 0 && _prevY > 0)
                {
                    _consecutiveFailCount++;
                    if (_consecutiveFailCount >= GlobalMatchFallbackThreshold)
                    {
                        var savedPrevX = _prevX;
                        var savedPrevY = _prevY;
                        (_prevX, _prevY) = (-1, -1); // 临时重置触发全局匹配
                        p = MapManager.GetMap(mapName, mapMatchMethod).GetMiniMapPosition(colorMat, _prevX, _prevY);
                        if (p == default)
                        {
                            (_prevX, _prevY) = (savedPrevX, savedPrevY);
                            diagBranch = $"局部失败(连续{_consecutiveFailCount})→全局匹配也失败";
                        }
                        else
                        {
                            // 跳变保护：全局命中坐标距回退前锚点过远 → 判误匹配（恶劣/重复纹理环境
                            // 全局匹配易匹配到相似的错误位置），丢弃该坐标当作识别失败，避免污染朝向计算。
                            var jumpGuard = GlobalMatchJumpGuardThreshold;
                            var jumpDist = p.DistanceTo(new Point2f(savedPrevX, savedPrevY));
                            if (jumpGuard > 0 && jumpDist > jumpGuard)
                            {
                                (_prevX, _prevY) = (savedPrevX, savedPrevY);
                                p = default;
                                diagBranch = $"局部失败→全局命中但跳变过大({jumpDist:F0}>{jumpGuard:F0})判误匹配丢弃";
                            }
                            else
                            {
                                _consecutiveFailCount = 0;
                                diagBranch = "局部失败→全局匹配命中";
                            }
                        }
                    }
                    else
                    {
                        // 局部匹配失败，等待累积到阈值后触发全局匹配
                        diagBranch = $"局部失败(连续{_consecutiveFailCount}，未达全局回退阈值{GlobalMatchFallbackThreshold})";
                    }
                }
                else if (p == default)
                {
                    // 无有效锚点（首帧或刚 Reset），直接走的全图匹配但仍失败
                    diagBranch = "无锚点全图匹配失败";
                }

                if (p != default && captureTime > _captureTime)
                {
                    (_prevX, _prevY) = (p.X, p.Y);
                    _captureTime = captureTime;
                    _consecutiveFailCount = 0;
                }

                MiniMapPositionDiagnostics.LogPosition(
                    "GetPosition", colorMat, diagPrevX, diagPrevY, p, diagBranch, _consecutiveFailCount);

                WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(typeof(Navigation),
                    "SendCurrentPosition", new object(), p));
                return p;
            }
            catch (Exception ex)
            {
                // 获取位置失败，返回上次的位置（仅当上次位置有效时）
                TaskControl.Logger.LogDebug(ex, "[小地图诊断] GetPosition 抛异常，回退到上次位置 ({Px:F1},{Py:F1})", _prevX, _prevY);
                if (_prevX > 0 && _prevY > 0)
                {
                    return new Point2f(_prevX, _prevY);
                }
                return default;
            }
            finally
            {
                Monitor.Exit(GetPositionLock);
            }
        }

        // 锁获取超时，返回上次的位置（仅当上次位置有效时）
        if (MiniMapPositionDiagnostics.Enabled)
        {
            TaskControl.Logger.LogDebug(
                "[小地图诊断] GetPosition 获取锁超时({Timeout}ms)，回退到上次位置 ({Px:F1},{Py:F1})", lockTimeoutMs, _prevX, _prevY);
        }
        if (_prevX > 0 && _prevY > 0)
        {
            return new Point2f(_prevX, _prevY);
        }
        return default;
        
    }

    /// <summary>
    /// 稳定获取当前位置坐标，优先使用全地图匹配，适用于不需要高效率但需要高稳定性的场景
    /// </summary>
    /// <param name="imageRegion">图像区域</param>
    /// <param name="mapName">地图名字</param>
    /// <param name="mapMatchMethod">地图匹配方式</param>
    /// <returns>当前位置坐标</returns>
    public Point2f GetPositionStable(ImageRegion imageRegion, string mapName, string mapMatchMethod)
    {
        using var colorMat = new Mat(imageRegion.SrcMat, MapAssets.Get(imageRegion).MimiMapRect);
        var captureTime = DateTime.UtcNow;
        var diagPrevX = _prevX;
        var diagPrevY = _prevY;

        // 先尝试使用局部匹配
        var sceneMap = MapManager.GetMap(mapName, mapMatchMethod);
        //提高局部匹配的阈值，以解决在沙漠录制点位时，移动过远不会触发全局匹配的情况
        var p = (sceneMap as SceneBaseMapByTemplateMatch)?.GetMiniMapPosition(colorMat, _prevX, _prevY, 0)
                ?? sceneMap.GetMiniMapPosition(colorMat, _prevX, _prevY);
        var diagBranch = "局部匹配命中";

        // 如果局部匹配失败或者点位跳跃过大，再尝试全地图匹配
        bool localFailedOrJumped = p == default || (_prevX > 0 && _prevY > 0 && p.DistanceTo(new Point2f(_prevX, _prevY)) > 150);
        if (localFailedOrJumped)
        {
            var localReason = p == default ? "局部失败" : "跳跃>150";
            Reset();
            p = MapManager.GetMap(mapName, mapMatchMethod).GetMiniMapPosition(colorMat, _prevX, _prevY);
            diagBranch = p == default ? $"{localReason}→全局匹配也失败" : $"{localReason}→全局匹配命中";
        }
        if (p != default && captureTime > _captureTime)
        {
            (_prevX, _prevY) = (p.X, p.Y);
            _captureTime = captureTime;
        }

        MiniMapPositionDiagnostics.LogPosition(
            "GetPositionStable", colorMat, diagPrevX, diagPrevY, p, diagBranch, -1);

        WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(typeof(Navigation),
            "SendCurrentPosition", new object(), p));
        return p;
    }

    public Point2f GetPositionStableByCache(ImageRegion imageRegion, string mapName, string mapMatchingMethod, int cacheTimeMs = 900)
    {
        var captureTime = DateTime.UtcNow;
        if (captureTime - _captureTime < TimeSpan.FromMilliseconds(cacheTimeMs) && _prevX > 0 && _prevY > 0)
        {
            return new Point2f(_prevX, _prevY);
        }

        return GetPositionStable(imageRegion, mapName, mapMatchingMethod);
    }
}
