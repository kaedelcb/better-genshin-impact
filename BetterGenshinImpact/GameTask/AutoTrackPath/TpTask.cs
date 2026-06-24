using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoTrackPath.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fischless.GameCapture;

using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using System;
using System.Windows.Forms;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 传送任务
/// </summary>
public class TpTask
{
    private readonly QuickTeleportAssets _assets = QuickTeleportAssets.Instance;
    private readonly Rect _captureRect = TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect;
    private readonly double _zoomOutMax1080PRatio = TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio;
    private readonly TpConfig _tpConfig = TaskContext.Instance().Config.TpConfig;
    private readonly string _mapMatchingMethod = TaskContext.Instance().Config.PathingConditionConfig.MapMatchingMethod;
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();

    private readonly CancellationToken ct;
    private readonly CultureInfo cultureInfo;
    private readonly IStringLocalizer stringLocalizer;
    private readonly double _screenHeight;

    /// <summary>
    /// 直接通过缩放比例按钮计算放大按钮的Y坐标
    /// </summary>
    private readonly int _zoomInButtonY = TaskContext.Instance().Config.TpConfig.ZoomStartY - 24; //  y-coordinate for zoom-in button  = _zoomStartY - 24

    /// <summary>
    /// 直接通过缩放比例按钮计算缩小按钮的Y坐标
    /// </summary>
    private readonly int _zoomOutButtonY = TaskContext.Instance().Config.TpConfig.ZoomEndY + 24; //  y-coordinate for zoom-out button = _zoomEndY + 24

    private const double DisplayTpPointZoomLevel = 4.4; // 传送点显示的时候的地图比例

    /// <summary>
    /// 联机锄地传送过程中抑制 AnomalyDetector 自动点击复苏按钮。
    /// 由 TpTask.WaitForTeleportCompletion 在 requireLoadingScreen=true 时设置 + finally 清除。
    /// AnomalyDetector 在两条复苏路径检查此标志，true 时跳过点击但回调照常触发。
    /// volatile bool：写者 TpTask 主线程，读者 AnomalyDetector 后台线程，单调标志。
    /// 详见 .kiro/specs/multiplayer-tp-revive-prompt-detection/bugfix.md §"Open Question Q2"。
    /// </summary>
    public static volatile bool SuppressAutoRevivalClick = false;

    /// <summary>
    /// 阶段 1 传送过渡页 (TeleportLoadingDetector.IsLoadingScreen) 命中后触发的静态事件。
    /// 参数：检测到 loading 命中的 Environment.TickCount 时间戳（毫秒），便于上层算延时。
    ///
    /// 设计为静态事件（O4 默认 A）：与 SuppressAutoRevivalClick volatile 标志的"静态字段 +
    /// try/finally 守护"模式对称。PathExecutor 在传送 waypoint 上报前 += handler、finally
    /// -= handler。单机调用方不注册 → handler 列表为空 → Invoke 是 no-op，零回归（UB1 / UB2）。
    ///
    /// 详见 .kiro/specs/multiplayer-fast-sync-host-controlled/design.md §3.7。
    /// Validates: requirements FR11 / FR12 / FR13
    /// </summary>
    // OnLoadingScreenDetected 事件已删除（fastsync-redesign-parameter-passing spec OQ-2）：
    // 改为通过 Tp/TpOnce/WaitForTeleportCompletion/WaitForLoadingScreenAsync 调用栈
    // 透传 string? fastSyncId 参数，IsLoadingScreen 命中时直接调
    // PathExecutor.CurrentMultiplayerCoordinator.WaitForAllPlayers 抢报。

    public TpTask(CancellationToken ct)
    {
        this.ct = ct;
        TpTaskParam param = new TpTaskParam();
        this.cultureInfo = param.GameCultureInfo;
        this.stringLocalizer = param.StringLocalizer;
        // 初始化全局参数
        var gameHandle = TaskContext.Instance().GameHandle;
        var gameScreen = Screen.FromHandle(gameHandle);
        var gameScreenBounds = gameScreen.Bounds;
        if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
        if (_tpConfig.MapZoomDistanceForce == 0)
        {
            _screenHeight = gameScreenBounds.Height > SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle).Height 
                ? (SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle).Height <= 1080 ? 3 : 2) 
                : 2.3;
        }
        else
        {
            _screenHeight = _tpConfig.MapZoomDistanceForce;
        }
        
        TaskControl.Logger.LogDebug("屏幕宽高：{gameScreenBounds} 游戏分辨率：{GetGameScreenRect} 传送参数：{screenHeight}", gameScreenBounds.Size,SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle).Size,_screenHeight);
    }

    /// <summary>
    /// 传送到七天神像
    /// </summary>
    public async Task TpToStatueOfTheSeven(bool requireLoadingScreen = false)
    {
        await CheckInBigMapUi();

        // 提前调整至恰当的缩放以更快的传送
        if (_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor)
        {
            double currentZoomLevel = await GetBigMapZoomLevelForStatueOfTheSevenAsync();
            if (currentZoomLevel > DisplayTpPointZoomLevel)
            {
                await AdjustMapZoomLevel(currentZoomLevel, DisplayTpPointZoomLevel);
            }
            else if (currentZoomLevel < 3)
            {
                await AdjustMapZoomLevel(currentZoomLevel, 3);
            }
        }

        string? country = _tpConfig.ReviveStatueOfTheSevenCountry;
        string? area = _tpConfig.ReviveStatueOfTheSevenArea;
        double x = _tpConfig.ReviveStatueOfTheSevenPointX;
        double y = _tpConfig.ReviveStatueOfTheSevenPointY;
        GiTpPosition revivePoint = _tpConfig.ReviveStatueOfTheSeven ?? GetNearestGoddess(x, y);
        if (_tpConfig.IsReviveInNearestStatueOfTheSeven)
        {
            var center = GetBigMapCenterPoint(MapTypes.Teyvat.ToString());
            var giTpPoint = GetNearestGoddess(center.X, center.Y);
            country = giTpPoint.Country;
            area = giTpPoint.Level1Area;
            x = giTpPoint.X;
            y = giTpPoint.Y;
            revivePoint = giTpPoint;
        }

        TaskControl.Logger.LogInformation("将传送至 {country} {area} 七天神像", country, area);
        await Tp(x, y, MapTypes.Teyvat.ToString(), false, requireLoadingScreen);
        if (_tpConfig.ShouldMove || _tpConfig.IsReviveInNearestStatueOfTheSeven)
        {
            (x, y) = GetClosestPoint(revivePoint.TranX, revivePoint.TranY, x, y, 5);
            var waypoint = new Waypoint
            {
                X = x,
                Y = y,
                Type = WaypointType.Path.Code,
                MoveMode = MoveModeEnum.Walk.Code
            };
            var waypointForTrack = new WaypointForTrack(waypoint, nameof(MapTypes.Teyvat), _mapMatchingMethod);
            await new PathExecutor(ct).MoveTo(waypointForTrack);
            Simulation.SendInput.SimulateAction(GIActions.Drop);
        }

        await Delay((int)(_tpConfig.HpRestoreDuration * 1000), ct);
    }

    private async Task<double> GetBigMapZoomLevelForStatueOfTheSevenAsync()
    {
        const int retryTimes = 20;
        Exception? lastException = null;

        for (int i = 0; i < retryTimes; i++)
        {
            try
            {
                using var region = CaptureToRectArea();
                var scale = Bv.GetBigMapScale(region);
                return (-5 * scale) + 6;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                if (i < retryTimes - 1)
                {
                    TaskControl.Logger.LogWarning("获取七天神像传送前大地图缩放级别失败，重试中...（{Attempt}/{RetryTimes}）：{Message}", i + 1, retryTimes, ex.Message);
                    await Delay(100, ct);
                }
            }
        }

        throw new InvalidOperationException("多次重试后，获取七天神像传送前大地图缩放级别失败", lastException);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="tranX"> 传送后实际到达的点X坐标 </param>
    /// <param name="tranY"> 传送后实际到达的点Y坐标 </param>
    /// <param name="x"> 传送点 X 坐标 </param>
    /// <param name="y"> 传送点 Y 坐标 </param>
    /// <param name="d"> 期望最终离传送点的距离 </param>
    /// <returns>  </returns>
    private static (double X, double Y) GetClosestPoint(double tranX, double tranY, double x, double y, double d)
    {
        double dx = x - tranX;
        double dy = y - tranY;
        double distanceSquared = dx * dx + dy * dy;
        double distance = Math.Sqrt(distanceSquared);
        d = d > 0 ? d : 0;
        if (distance < d)
        {
            return (tranX, tranY);
        }

        double ratio = d / distance;
        double px = (x - dx * ratio);
        double py = (y - dy * ratio);
        return (px, py);
    }

    /// <summary>
    /// 获取离 x,y 最近的七天神像
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    private GiTpPosition GetNearestGoddess(double x, double y)
    {
        GiTpPosition? nearestGiTpPosition = null;
        double minDistance = double.MaxValue;
        foreach (var (_, goddessPosition) in MapLazyAssets.Instance.GoddessPositions)
        {
            var distance = Math.Sqrt(Math.Pow(goddessPosition.X - x, 2) + Math.Pow(goddessPosition.Y - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestGiTpPosition = goddessPosition;
            }
        }

        // 获取最近的神像位置
        return nearestGiTpPosition ?? throw new InvalidOperationException("没找到最近的七天神像");
    }

    /// <summary>
    ///释放所有按键，并打开大地图界面
    /// </summary>
    /// <param name="retryCount">重试次数</param>
    public async Task OpenBigMapUi(int retryCount = 3)
    {
        for (var i = 0; i < retryCount; i++)
        {
            try
            {
                // 打开地图前释放所有按键
                Simulation.ReleaseAllKey();
                await Delay(20, ct);
                await CheckInBigMapUi(i);
                return;
            }
            catch (Exception e) when (e is NormalEndException || e is TaskCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (retryCount > 1)
                {
                    Logger.LogError("打开大地图失败，重试 {I} 次", i + 1);
                    Logger.LogDebug(e, "打开大地图失败，重试 {I} 次", i + 1);
                    await _blessingOfTheWelkinMoonTask.Start(ct);
                }

                if (i + 1 >= retryCount)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// 通过大地图传送到指定坐标最近的传送点，然后移动到指定坐标
    /// </summary>
    /// <param name="tpX"></param>
    /// <param name="tpY"></param>
    /// <param name="mapName">独立地图名称</param>
    /// <param name="force">强制以当前的tpX,tpY坐标进行自动传送</param>
    /// <param name="retryTimes">重试次数</param>
    private async Task<(double, double)> TpOnce(double tpX, double tpY, string mapName = "Teyvat", bool force = false, int retryTimes = 0, bool requireLoadingScreen = false, string? fastSyncId = null)
    {
        // 1. 确认在地图界面
        await OpenBigMapUi(1);
        // 2. 传送前的计算准备
        // 获取离目标传送点最近的两个传送点，按距离排序
        var nTpPoints = GetNearestNTpPoints(tpX, tpY, mapName, 2);
        // 获取最近的传送点与区域
        var (x, y, country) = force ? (tpX, tpY, null) : (nTpPoints[0].X, nTpPoints[0].Y, nTpPoints[0].Country);
        var disBetweenTpPoints = Math.Sqrt(Math.Pow(nTpPoints[0].X - nTpPoints[1].X, 2) +
                                           Math.Pow(nTpPoints[0].Y - nTpPoints[1].Y, 2));
        // 确保不会点错传送点的最小缩放，保证至少为 1.0
        var minZoomLevel = Math.Max(disBetweenTpPoints / 20, 1.0);

        if (mapName == MapTypes.Teyvat.ToString())
        {
            // 计算传送点位置离哪张地图切换后的中心点最近，切换到该地图
            await SwitchRecentlyCountryMap(x, y, country);
        }
        else
        {
            // 直接切换地区
            await SwitchArea(MapTypesExtensions.ParseFromName(mapName).GetDescription());
        }

        if (_tpConfig.MapMoveStepDivisor && _tpConfig.FastDragRecognitionEnabled)
        {
            await Delay(200, ct);
            await WaitMapStableOrTimeoutAsync(300); 
        }
        else
        {
            await Delay(500, ct);
        }

        Rect bigMapInAllMapRect;
        // 3. 调整初始缩放等级，避免识别中心点失败
        using var ra3 = CaptureToRectArea();
        var zoomLevel = GetBigMapZoomLevel(ra3);
        if (_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor)
        {
            /* 动态调整缩放逻辑：
                1. 如果当前缩放大于显示传送点级别 -> 缩小
                2. 如果小于配置的最小级别 -> 放大 */
            if (zoomLevel > DisplayTpPointZoomLevel + _tpConfig.PrecisionThreshold)
            {
                await AdjustMapZoomLevel(zoomLevel, DisplayTpPointZoomLevel);
                zoomLevel = DisplayTpPointZoomLevel;
                TaskControl.Logger.LogInformation("当前缩放等级过大，调整为 {zoomLevel:0.00}", DisplayTpPointZoomLevel);
                bigMapInAllMapRect = GetBigMapRect(mapName);
            }
            else if (zoomLevel < _tpConfig.MinZoomLevel - _tpConfig.PrecisionThreshold)
            {
                await AdjustMapZoomLevel(zoomLevel, _tpConfig.MinZoomLevel);
                zoomLevel = _tpConfig.MinZoomLevel;
                TaskControl.Logger.LogInformation("当前缩放等级过小，调整为 {zoomLevel:0.00}", _tpConfig.MinZoomLevel);
                bigMapInAllMapRect = GetBigMapRect(mapName);
            }
        }

        // 4. zoomLevel不满足条件，强制进行一次 MoveMapTo，避免传送点相近导致误点
        if (zoomLevel > minZoomLevel)
        {
            if (_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor)
            {
                TaskControl.Logger.LogInformation("目标传送点有相近传送点，到目标传送点附近将缩放到{zoomLevel:0.00}", minZoomLevel);
                await MoveMapTo(x, y, mapName, minZoomLevel,country);
                if (_tpConfig.MapMoveStepDivisor)
                {
                    int timeoutMs = 800 + _tpConfig.StepIntervalMilliseconds * 10;
                    if (_tpConfig.FastDragRecognitionEnabled)
                    {
                        await WaitMapStableOrTimeoutAsync(500); // fast-drag-recognition-acceleration spec
                    }
                    else
                    {
                        await Delay(timeoutMs, ct); // 等待地图移动完成（旧行为）
                    }
                }
                else
                {
                    await Delay(300, ct); // 等待地图移动完成
                }
            }
            else
            {
                TaskControl.Logger.LogInformation("目标传送点有相近传送点，可能传送失败。如果失败请到设置-大地图地图传送设置开启地图缩放");
                // TODO 部分无法区分点位强制缩放，即使没有zoomEnabled。
            }
        }
        
        // 5. 判断传送点是否在当前界面，若否则移动地图
        // await WaitMapStableOrTimeoutAsync(1000,20,5); // fast-drag-recognition-acceleration spec
        bigMapInAllMapRect = GetBigMapRect(mapName);
        var retryCount = 0;
        do
        {
            if (IsPointInBigMapWindow(mapName, bigMapInAllMapRect, x, y)) break;
            if (retryCount++ >= 5) // 防止死循环
            {
                TaskControl.Logger.LogWarning("多次尝试未移动到目标传送点，传送失败");
                throw new Exception("多次尝试未移动到目标传送点，传送失败");
            }
            
            TaskControl.Logger.LogInformation("传送点不在当前大地图范围内，重新调整地图位置-1");
            await MoveMapTo(x, y, mapName,2,country, retryTimes);
            if (_tpConfig.MapMoveStepDivisor)
            {
                int timeoutMs = retryTimes > 0 ? 600 : 200;
                if (_tpConfig.FastDragRecognitionEnabled)
                {
                    // 加速：等像素稳定（远比连续两次模板匹配 GetBigMapRect 快），稳定后再单次 GetBigMapRect
                    // fast-drag-recognition-acceleration spec / design.md §4.2（feedback adjustment）
                    await WaitMapStableOrTimeoutAsync(1000);
                }
                else
                {
                    await Delay(timeoutMs, ct); // 等待地图移动完成（旧行为）
                }
            }
            else
            {
                await Delay(300, ct); // 等待地图移动完成
            }
            bigMapInAllMapRect = GetBigMapRect(mapName);
        } while (true);

        // 5.5 点击前强制把缩放归一到本次尝试的"可点击级别"，避免步骤 5 的 MoveMapTo(...,2,...)
        //     把点击缩放带离传送点可点击区间。retryTimes 作为 attempt 序号，使每次重试换档。
        //     详见 .kiro/specs/teleport-wrong-zoom-no-teleport-button-fix/design.md §2.2。
        // if (_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor)
        // {
        //     using var raZoom = CaptureToRectArea();
        //     double zoomBeforeClick = GetBigMapZoomLevel(raZoom);
        //     double targetClickZoom = ComputeClickZoomCandidate(retryTimes, DisplayTpPointZoomLevel, _tpConfig.MinZoomLevel);
        //     if (Math.Abs(zoomBeforeClick - targetClickZoom) > _tpConfig.PrecisionThreshold)
        //     {
        //         await AdjustMapZoomLevel(zoomBeforeClick, targetClickZoom);
        //         TaskControl.Logger.LogInformation("点击前调整缩放：{From:0.00} -> {To:0.00}（第 {Attempt} 次尝试）",
        //             zoomBeforeClick, targetClickZoom, retryTimes + 1);
        //         await Delay(_tpConfig.MapMoveStepDivisor ? 50 : 100, ct);
        //         // 缩放变化使既有 bigMapInAllMapRect 失效，必须重新计算
        //         bigMapInAllMapRect = GetBigMapRect(mapName);
        //     }
        // }

        // 6. 计算传送点位置并点击
        // Debug.WriteLine($"({x},{y}) 在 {bigMapInAllMapRect} 内，计算它在窗体内的位置");
        // 注意这个坐标的原点是中心区域某个点，所以要转换一下点击坐标（点击坐标是左上角为原点的坐标系），不能只是缩放
        var (clickX, clickY) = ConvertToGameRegionPosition(mapName, bigMapInAllMapRect, x, y);
        TaskControl.Logger.LogInformation("点击传送点");
        using var ra4 = CaptureToRectArea();
        ra4.ClickTo((int)clickX, (int)clickY);

        // 7. 触发一次快速传送功能
        if (_tpConfig.MapMoveStepDivisor && _tpConfig.FastDragRecognitionEnabled)
        {
            // 加速 + 容错：popup 探测立即点 + IsLoadingScreen 终判 + 失败重点最多 3 次
            // 已进入传送加载页 → 直接返回，跳过 ClickTpPoint（避免重复点击）
            // 未进入 → 走 ClickTpPoint 兜底（旧行为）
            // fast-drag-recognition-acceleration spec / final click pre-stop optimization v2
            bool entered = await FastClickTeleportButtonAsync();
            if (!entered)
            {
                using var ra1 = CaptureToRectArea();
                await ClickTpPoint(ra1);
            }
        }
        else
        {
            await Delay(500, ct);
            using var ra1 = CaptureToRectArea();
            await ClickTpPoint(ra1);
        }

        // 8. 等待传送完成
        await WaitForTeleportCompletion(50, 1200, requireLoadingScreen, fastSyncId);
        return (x, y);
    }

    /// <summary>
    ///     检查传送是否完成，未完成则等待
    /// </summary>
    /// <param name="maxAttempts">最大检查延时的次数</param>
    /// <param name="delayMs">如果未完成加载，检查加载页面的延时。</param>
    /// <param name="requireLoadingScreen">
    ///     当为 true 时启用阶段 1：先在 6s 内每 200ms 观察一次传送过渡页（联机锄地路径专用，
    ///     避免“开大地图被打死→复苏到神像→派蒙可见→误判传送成功”）。详见
    ///     .kiro/specs/multiplayer-tp-success-via-loading-screen/。
    /// </param>
    private async Task WaitForTeleportCompletion(int maxAttempts, int delayMs, bool requireLoadingScreen = false, string? fastSyncId = null)
    {
        // 仅联机锄地路径设置抑制标志位，单机调用方默认 false 跳过整段守卫
        bool suppressClickSet = false;
        try
        {
            if (requireLoadingScreen)
            {
                TpTask.SuppressAutoRevivalClick = true;
                suppressClickSet = true;
            }

            // === 阶段 1（仅当联机调用方传入 requireLoadingScreen=true）===
            if (requireLoadingScreen)
            {
                bool seen = await WaitForLoadingScreenAsync(timeoutMs: 6000, intervalMs: 200, fastSyncId: fastSyncId);
                if (!seen)
                {
                    TaskControl.Logger.LogWarning("[联机] 未观察到传送过渡页，疑似传送被打断（点击传送后角色可能已倒地/被打断）");
                    throw new TeleportLoadingTimeoutException("阶段 1 在 6s 内未观察到传送过渡页");
                }
                else
                {
                    TaskControl.Logger.LogInformation("[联机] 观察到传送过渡页，继续等待传送完成");
                }
            }

            // === 阶段 2（保持原行为 + 增加复苏弹窗检测）===
            await Delay(delayMs, ct);
            for (var i = 0; i < maxAttempts; i++)
            {
                using var capture = CaptureToRectArea();

                // 阶段 2 复苏弹窗检测（仅联机路径，防御阶段 1 之后才出现弹窗的罕见场景）
                if (requireLoadingScreen && Bv.IsInRevivePrompt(capture))
                {
                    TaskControl.Logger.LogWarning("[联机] 传送过程中检测到复苏弹窗（阶段 2），疑似传送失败 + 角色死亡");
                    BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.SignalMultiplayerRevivalFromExternal();
                    throw new TeleportLoadingTimeoutException("传送中检测到复苏弹窗，传送失败");
                }

                if (Bv.IsInMainUi(capture))
                {
                    TaskControl.Logger.LogInformation("传送完成，返回主界面");
                    return;
                }
                //增加容错，小概率情况下碰到，前面点击传送失败
                capture.Find(_assets.TeleportButtonRo, rg => rg.Click());
                await Delay(delayMs, ct);
                // 打开大地图期间推送的月卡会在传送之后直接显示，导致检测不到传送完成。
                await _blessingOfTheWelkinMoonTask.Start(ct);
            }

            TaskControl.Logger.LogWarning("传送等待超时，换台电脑吧");
        }
        finally
        {
            if (suppressClickSet)
            {
                TpTask.SuppressAutoRevivalClick = false;
            }
        }
    }

    /// <summary>
    /// 阶段 1：在 timeoutMs 内每 intervalMs 截图判断一次过渡页是否出现。
    /// 命中 → 返回 true；超时 → 返回 false。
    ///
    /// 暂停 / 网络断开兜底：循环顶部检测 IsSuspend || IsSuspendedByNetwork 任一为 true 时
    /// 早退 return true，让阶段 2 接管。原因：墙钟 deadline 在暂停期间继续累积，
    /// 不早退会导致解除暂停后立即超时误抛异常。详见
    /// .kiro/specs/multiplayer-tp-loading-screen-suspend-skip/。
    ///
    /// fastSyncId（fastsync-redesign-parameter-passing spec）：联机模式下传递抢报 syncId，
    /// IsLoadingScreen 命中后直接调 PathExecutor.CurrentMultiplayerCoordinator.WaitForAllPlayers
    /// 抢报。null 时该路径完全短路（单机调用方零感知）。
    /// </summary>
    private async Task<bool> WaitForLoadingScreenAsync(int timeoutMs, int intervalMs, string? fastSyncId = null)
    {
        bool fastReported = false;
        long deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // 暂停 / 网络断开早退：避免墙钟超时误判（cancel 优先级更高，已在上一行处理）
            if (TeleportLoadingPhaseSuspendGuard.ShouldSkip(
                    RunnerContext.Instance.IsSuspend,
                    TaskControl.IsSuspendedByNetwork))
            {
                TaskControl.Logger.LogInformation("[联机] 检测到暂停/网络断开，跳过传送过渡页守卫，回退原判据");
                return true;
            }

            using var capture = CaptureToRectArea();

            // 复苏弹窗优先于过渡页判定（在过渡页之前的瞬间，复苏弹窗已经显示）
            if (Bv.IsInRevivePrompt(capture))
            {
                TaskControl.Logger.LogWarning("[联机] 传送过程中检测到复苏弹窗（阶段 1），疑似传送失败 + 角色死亡");
                BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.SignalMultiplayerRevivalFromExternal();
                throw new TeleportLoadingTimeoutException("传送中检测到复苏弹窗，传送失败");
            }

            if (TeleportLoadingDetector.IsLoadingScreen(capture.SrcMat))
            {
                // 传送同步点抢报（fastsync-redesign-parameter-passing spec）：
                // 内联在 IsLoadingScreen 命中处。fastSyncId == null（单机/未启用）时整段短路。
                if (!fastReported && fastSyncId != null)
                {
                    var __coordinator = BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CurrentMultiplayerCoordinator;
                    if (__coordinator != null && __coordinator.IsConnected)
                    {
                        fastReported = true;
                        try
                        {
                            // fire-and-forget：抢报后立即继续传送主流程，不等 AllArrived
                            await __coordinator.FastReportAsync(fastSyncId, syncProgress: -1);
                            TaskControl.Logger.LogInformation("[联机][FastSync] TpTask 抢报命中 syncId={SyncId}", fastSyncId);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (System.Exception ex)
                        {
                            // 抢报失败不阻塞传送主流程：严格路径会在 PathExecutor 外层补上报
                            TaskControl.Logger.LogWarning(ex, "[联机][FastSync] TpTask 抢报异常，已忽略 syncId={SyncId}", fastSyncId);
                        }
                    }
                }
                return true;
            }
            await Delay(intervalMs, ct);
        }
        return false;
    }

    /// <summary>
    /// 传送点是否在大地图窗口内
    /// </summary>
    /// <param name="mapName"></param>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private bool IsPointInBigMapWindow(string mapName, Rect bigMapInAllMapRect, double x, double y)
    {
        // 坐标不包含直接返回
        if (!bigMapInAllMapRect.Contains(x, y))
        {
            return false;
        }

        var (clickX, clickY) = ConvertToGameRegionPosition(mapName, bigMapInAllMapRect, x, y);
        // 屏蔽左上角360x400区域
        if (clickX < 360 * _zoomOutMax1080PRatio && clickY < 400 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        // 屏蔽周围 115 一圈的区域
        if (clickX < 115 * _zoomOutMax1080PRatio
            || clickY < 115 * _zoomOutMax1080PRatio
            || clickX > _captureRect.Width - 115 * _zoomOutMax1080PRatio
            || clickY > _captureRect.Height - 115 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 转换传送点坐标到窗体内需要点击的坐标
    /// </summary>
    /// <param name="mapName"></param>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private (double clickX, double clickY) ConvertToGameRegionPosition(string mapName, Rect bigMapInAllMapRect, double x, double y)
    {
        var (picX, picY) = MapManager.GetMap(mapName, _mapMatchingMethod).ConvertGenshinMapCoordinatesToImageCoordinates(new Point2f((float)x, (float)y));
        var picRect = MapManager.GetMap(mapName, _mapMatchingMethod).ConvertGenshinMapCoordinatesToImageCoordinates(bigMapInAllMapRect);
        Debug.WriteLine($"({picX},{picY}) 在 {picRect} 内，计算它在窗体内的位置");
        var clickX = (picX - picRect.X) / picRect.Width * _captureRect.Width;
        var clickY = (picY - picRect.Y) / picRect.Height * _captureRect.Height;
        return (clickX, clickY);
    }

    public async Task CheckInBigMapUi(int retryCount = 0)
    {
        // 尝试打开地图失败后，先回到主界面后再次尝试打开地图
        if (!await TryToOpenBigMapUi(retryCount))
        {
            await new ReturnMainUiTask().Start(ct);
            await Delay(500, ct);
            if (!await TryToOpenBigMapUi(retryCount))
            {
                throw new RetryException("打开大地图失败，请检查按键绑定中「打开地图」按键设置是否和原神游戏中一致！");
            }
        }
    }

    /// <summary>
    /// 尝试打开地图界面
    /// </summary>
    private async Task<bool> TryToOpenBigMapUi(int retryCount = 0)
    {
        // M 打开地图识别当前位置，中心点为当前位置
        using var ra1 = CaptureToRectArea();
        if (Bv.IsInBigMapUi(ra1))
        {
            return true;
        }

        Simulation.SendInput.SimulateAction(GIActions.OpenMap);
        // Logger.LogWarning("尝试打开大地图界面，识别当前位置");
        
        // 加速识别模式：轮询等大地图 UI 出现，兜底 2500ms（≈旧逻辑 1000+500*3 上限）
        // fast-drag-recognition-acceleration spec / step 1 boot delay optimization
        if (_tpConfig.MapMoveStepDivisor && _tpConfig.FastDragRecognitionEnabled)
        {
            await Delay(200, ct);
            await WaitMapStableOrTimeoutAsync(timeoutMs: 1300); 
            using var ra2 = CaptureToRectArea();
            if (ra2.Find(QuickTeleportAssets.Instance.MapScaleButtonRo).IsExist())
            {
                return true;
            }
        }
        else
        {
            await Delay(500, ct);
        }

        // 旧行为：固定 1000ms 后再 3 次 500ms 重试
        for (int i = 0; i < 30; i++)
        {
            using var ra12 = CaptureToRectArea();
            if (!Bv.IsInBigMapUi(ra12))
            {
                await Delay(50+retryCount*100, ct);
            }
            else
            {
                Logger.LogWarning("成功识别到大地图界面-1");
                return true;
            }
        }
       
        return false;
    }

    /// <summary>
    /// 加速识别模式：按 M 后轮询等大地图 UI 出现即返回（单判据）。
    /// 之前为了防"地图特征点未渲染→走 SwitchArea 弯路"加过双判据，但用户实测：
    /// 双判据导致每次都顿一下；旧版本（无特征点判据）也不是每次都走 SwitchArea。
    /// 改回单判据后，"特征点识别"由下游 SwitchRecentlyCountryMap 入口的 3×100ms retry 兜底
    /// （见 SwitchRecentlyCountryMap 注释）。最坏 ~300ms 仍能识别成功，避免误走 SwitchArea。
    ///
    /// fast-drag-recognition-acceleration spec / step 1 boot delay optimization (single criterion)
    /// </summary>
    private async Task<bool> WaitForBigMapUiOrTimeoutAsync(int timeoutMs, int pollMs = 10)
    {
        long deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ra = CaptureToRectArea();
                if (Bv.IsInBigMapUi(ra))
                {
                    await Delay(10, ct);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("[快速识别] OpenBigMapUi 探测异常: {Msg}", ex.Message);
            }
            await Delay(pollMs, ct);
        }
        return false;
    }

    /// <summary>
    /// 加速识别模式：轮询等指定 RecognitionObject 出现，超时兜底。
    /// 主要用于"等弹窗 / 菜单出现"场景（如 SwitchArea 等地区菜单的白色 X 关闭按钮）。
    /// fast-drag-recognition-acceleration spec
    /// </summary>
    private async Task<bool> WaitForElementOrTimeoutAsync(RecognitionObject ro, int timeoutMs, int pollMs = 15)
    {
        long deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ra = CaptureToRectArea();
                using var found = ra.Find(ro);
                if (found.IsExist())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("[快速识别] WaitForElement 探测异常 {Name}: {Msg}", ro.Name, ex.Message);
            }
            await Delay(pollMs, ct);
        }
        return false;
    }


    public async Task<(double, double)> Tp(double tpX, double tpY, string mapName = "Teyvat", bool force = false, bool requireLoadingScreen = false, string? fastSyncId = null)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                return await TpOnce(tpX, tpY, mapName, force, i, requireLoadingScreen, fastSyncId);
            }
            catch (TpPointNotActivate e)
            {
                // 传送点未激活或不存在 按ESC回到大地图界面
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(300, ct);
                // throw; // 不抛出异常，继续重试
                TaskControl.Logger.LogWarning(e.Message + "  重试");
                // 联机锄地：传送失败重试视为"仍在合法传送中"，刷新 WorldStateMonitor 抑制计时窗口，
                // 避免长传送被墙钟超时误判被踢出。单机 CurrentWorldStateMonitor==null → no-op。
                // 详见 .kiro/specs/world-state-monitor-teleport-suppression-premature-expiry-fix/design.md 改动 5。
                PathExecutor.CurrentWorldStateMonitor?.RefreshTeleportSuppression();
            }
            catch (Exception e) when (e is NormalEndException || e is TaskCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                TaskControl.Logger.LogError("传送失败，重试 {I} 次", i + 1);
                // TaskControl.Logger.LogDebug(e, "传送失败，重试 {I} 次", i + 1);
                if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                //回到主界面，重置状态
                await new ReturnMainUiTask().Start(ct);
                await Delay(1000, ct);
                // 联机锄地：传送失败重试视为"仍在合法传送中"，刷新抑制计时窗口（同上）。
                PathExecutor.CurrentWorldStateMonitor?.RefreshTeleportSuppression();
            }
        }

        throw new InvalidOperationException("传送失败");
    }

    /// <summary>
    /// 移动地图到指定传送点位置
    /// 可能会移动不对，所以可以重试此方法
    /// </summary>
    /// <param name="x">目标x坐标</param>
    /// <param name="y">目标y坐标</param>
    /// <param name="mapName">地图名称</param>
    /// <param name="finalZoomLevel">到达目标点的最小缩放等级，只在 MapZoomEnabled 为 True 生效</param>
    /// <param name="country">传送地图国家</param>
    /// <param name="retryTimes">重试次数</param>
    public async Task MoveMapTo(double x, double y, string mapName, double finalZoomLevel = 2, string? country = null, int retryTimes = 0)
    {
        // 参数初始化
        using var ra1 = CaptureToRectArea();
        double minZoomLevel = Math.Min(finalZoomLevel, _tpConfig.MinZoomLevel);
        double maxZoomLevel = _tpConfig.MaxZoomLevel;
        double currentZoomLevel = GetBigMapZoomLevel(ra1);
        int exceptionTimes = 0;
        var falseCount = 0;
        Point2f mapCenterPoint;
        try
        {
            mapCenterPoint = GetPositionFromBigMap(mapName); // 初始中心
        }
        catch (MapPositionNotRecognizedException)
        {
            if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
            Logger.LogDebug("初始中心点识别失败，开启自救策略");
            // 判断当前缩放是否离最佳识别缩放(4.4)较远，如果是，则先调整到最佳视角尝试
            if ((_tpConfig.MapZoomEnabled ||_tpConfig.MapMoveStepDivisor) && Math.Abs(currentZoomLevel - DisplayTpPointZoomLevel) > 0.3) 
            {
                await AdjustMapZoomLevel(currentZoomLevel, DisplayTpPointZoomLevel);
                currentZoomLevel = DisplayTpPointZoomLevel;
                await Delay(300, ct);

                try
                {
                    mapCenterPoint = GetPositionFromBigMap(mapName);
                    Logger.LogDebug("调整缩放后识别恢复成功");
                }
                catch (MapPositionNotRecognizedException)
                {
                    Logger.LogDebug("缩放后依然失败，尝试强制跃迁...");
                    await ForceJumpToTargetArea(x, y, mapName); 
                    await Delay(100, ct);
                    await WaitMapStableOrTimeoutAsync(1000);
                    
                    try
                    {
                        mapCenterPoint = GetPositionFromBigMap(mapName);
                        Logger.LogDebug("强制切换区域后识别恢复成功");
                    }
                    catch (MapPositionNotRecognizedException ex)
                    {
                        throw new Exception("所有脱困策略均失效，无法获取初始点", ex);
                    }
                    finally
                    {
                        if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                    }
                }
            }
            else
            {
                if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                Logger.LogDebug("缩放已在最佳区间附近，直接尝试强制跃迁...");
                await ForceJumpToTargetArea(x, y, mapName); 
                await Delay(100, ct);
                await WaitMapStableOrTimeoutAsync(1000);
                
                try
                {
                    mapCenterPoint = GetPositionFromBigMap(mapName);
                    Logger.LogDebug("强制切换区域后识别恢复成功");
                }
                catch (MapPositionNotRecognizedException ex)
                {
                    if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                    throw new Exception("初始识别失败且切换区域后依然无效", ex);
                }
            }
        }

        var (xOffset, yOffset) = (x - mapCenterPoint.X, y - mapCenterPoint.Y);
        double totalMoveMouseX = _tpConfig.MapScaleFactor * Math.Abs(xOffset) / currentZoomLevel;
        double totalMoveMouseY = _tpConfig.MapScaleFactor * Math.Abs(yOffset) / currentZoomLevel;
        double mouseDistance = Math.Sqrt(totalMoveMouseX * totalMoveMouseX + totalMoveMouseY * totalMoveMouseY);
        // 缩小地图到恰当的缩放
        if ((_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor))
        {
            if (mouseDistance > _tpConfig.MapZoomOutDistance)
            {
                using var ra = CaptureToRectArea();
                double targetZoomLevel = currentZoomLevel * mouseDistance / _tpConfig.MapZoomOutDistance;
                targetZoomLevel = Math.Min(targetZoomLevel, maxZoomLevel);
                await AdjustMapZoomLevel(currentZoomLevel, targetZoomLevel);
                using var ra2 = CaptureToRectArea();
                double nextZoomLevel = GetBigMapZoomLevel(ra2);
                totalMoveMouseX *= currentZoomLevel / nextZoomLevel;
                totalMoveMouseY *= currentZoomLevel / nextZoomLevel;
                mouseDistance *= currentZoomLevel / nextZoomLevel;
                currentZoomLevel = nextZoomLevel;
            }
        }

        // 开始移动并放大地图
        for (var iteration = 0; iteration < _tpConfig.MaxIterations; iteration++)
        {
            if (_tpConfig.MapZoomEnabled || _tpConfig.MapMoveStepDivisor)
            {
                if (mouseDistance < (_tpConfig.MapMoveStepDivisor ? 600 : _tpConfig.MapZoomInDistance))
                {
                    double targetZoomLevel = currentZoomLevel * mouseDistance / (_tpConfig.MapMoveStepDivisor ? 600 : _tpConfig.MapZoomInDistance);
                    targetZoomLevel = Math.Max(targetZoomLevel, minZoomLevel);
                    if (currentZoomLevel > minZoomLevel + _tpConfig.PrecisionThreshold)
                    {
                        await AdjustMapZoomLevel(currentZoomLevel, targetZoomLevel);
                        using var ra4 = CaptureToRectArea();
                        double nextZoomLevel = GetBigMapZoomLevel(ra4);
                        totalMoveMouseX *= currentZoomLevel / nextZoomLevel;
                        totalMoveMouseY *= currentZoomLevel / nextZoomLevel;
                        mouseDistance *= currentZoomLevel / nextZoomLevel;
                        currentZoomLevel = nextZoomLevel;
                    }
                }
            }

            // 非常接近目标点，不再进一步调整
            if (mouseDistance < (_tpConfig.MapMoveStepDivisor ? retryTimes == 0 ? 400 : 300 : _tpConfig.Tolerance))
            {
                TaskControl.Logger.LogDebug("移动 {I} 次鼠标后，已经接近目标点，不再移动地图。", iteration + 1);
                break;
            }
            
            // TaskControl.Logger.LogDebug("屏幕参数：{screenHeight}", _screenHeight);
            
            var moveStepDivisor = _tpConfig.MapMoveStepDivisor ? 40 : 10;
            var moveStepDivisorDouble = _tpConfig.MapMoveStepDivisor ? SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle).Height*_screenHeight/5: _tpConfig.MaxMouseMove;
            int moveMouseX = (int)Math.Min(totalMoveMouseX, moveStepDivisorDouble * totalMoveMouseX / mouseDistance) * Math.Sign(xOffset);
            int moveMouseY = (int)Math.Min(totalMoveMouseY, moveStepDivisorDouble * totalMoveMouseY / mouseDistance) * Math.Sign(yOffset);
            double moveMouseLength = Math.Sqrt(moveMouseX * moveMouseX + moveMouseY * moveMouseY);
            int moveSteps = Math.Max((int)moveMouseLength / moveStepDivisor, 3); // 每次移动的步数最小为 3，避免除 0 错误
            
            await MouseMoveMap(moveMouseX, moveMouseY, moveSteps);

            // 推算理论上的移动后坐标 (惯性预测)
            Point2f predictedPoint = mapCenterPoint + new Point2f(
                (float)(moveMouseX * currentZoomLevel / _tpConfig.MapScaleFactor),
                (float)(moveMouseY * currentZoomLevel / _tpConfig.MapScaleFactor));

            try
            {
                var newCenterPoint = GetPositionFromBigMap(mapName); // 随循环更新的地图中心
                
                // 计算识别坐标与预测坐标的偏差
                double jumpDistance = Math.Sqrt(Math.Pow(newCenterPoint.X - predictedPoint.X, 2) + Math.Pow(newCenterPoint.Y - predictedPoint.Y, 2));
                double expectedMoveLen = Math.Sqrt(moveMouseX * moveMouseX + moveMouseY * moveMouseY) * currentZoomLevel / _tpConfig.MapScaleFactor;
                
                // 如果实际识别坐标产生超出物理可能的远距离跳跃 (比如原本只移动了50单位，但是坐标跳跃了300单位以上)
                // 则判定为低特征区域产生的误识别（假阳性），抛出异常进入下面的盲走抓取逻辑
                if (jumpDistance > Math.Max(200, expectedMoveLen * 2))
                {
                    if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                    Logger.LogDebug("坐标异常跳跃({dist:0.0})，判定为误识别", jumpDistance);
                    throw new MapPositionNotRecognizedException("中心点识别坐标异常跳跃");
                }

                mapCenterPoint = newCenterPoint;
                exceptionTimes = 0;
            }
            catch (MapPositionNotRecognizedException)
            {
                if (++exceptionTimes > (_tpConfig.MapMoveStepDivisor?1:2))
                {
                    if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                    throw new Exception("多次中心点识别失败或异常，惯性推算失效，重新传送");
                }

                if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                Logger.LogDebug("进入盲走推算 (跳过次数: {times})", exceptionTimes);
                mapCenterPoint = predictedPoint;
            }

            // Logger.LogError("地图名称:{mapName}", mapName);;//mapName
            if (_tpConfig.MapMoveStepDivisor)
            {

                using var ra = CaptureToRectArea().SrcMat;
                double brightness = Cv2.Mean(ra).Val0;
                TaskControl.Logger.LogDebug("地图亮度:{brightness}", brightness);
                if (brightness < (mapName=="SeaOfBygoneEras" ? 35:48))
                {
                    falseCount++;
                
                    if (falseCount > 2)
                    {
                        if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
                        throw new Exception("地图亮度过低，重新传送");
                    }

                    if (falseCount > 1)
                    {
                        Simulation.SendInput.Mouse.LeftButtonUp();
                        TaskControl.Logger.LogWarning("地图亮度过低");
                        if (mapName == MapTypes.Teyvat.ToString())
                        {
                            // 计算传送点位置离哪张地图切换后的中心点最近，切换到该地图
                            await SwitchRecentlyCountryMap(x, y, country);
                        }
                        else
                        {
                            // 直接切换地区
                            await SwitchArea(MapTypesExtensions.ParseFromName(mapName).GetDescription());
                        }
                        continue;
                    }
                }
                else
                {
                    falseCount = 0;
                }  
            }

            (xOffset, yOffset) = (x - mapCenterPoint.X, y - mapCenterPoint.Y);
            totalMoveMouseX = _tpConfig.MapScaleFactor * Math.Abs(xOffset) / currentZoomLevel;
            totalMoveMouseY = _tpConfig.MapScaleFactor * Math.Abs(yOffset) / currentZoomLevel;
            mouseDistance = Math.Sqrt(totalMoveMouseX * totalMoveMouseX + totalMoveMouseY * totalMoveMouseY);
            if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
        }
    }

    /// <summary>
    /// 点击并移动鼠标
    /// </summary>
    /// <param name="x1">鼠标初始位置x</param>
    /// <param name="y1">鼠标初始位置y</param>
    /// <param name="x2">鼠标移动后位置x</param> 
    /// <param name="y2">鼠标移动后位置y</param>
    public async Task MouseClickAndMove(int x1, int y1, int x2, int y2)
    {
        // GlobalMethod.MoveMouseTo(x1, y1);
        GameCaptureRegion.GameRegionMove((rect, scale) => (x1 * scale, y1 * scale));
        await Delay(50, ct);
        GlobalMethod.LeftButtonDown();
        await Delay(50, ct);
        // GlobalMethod.MoveMouseTo(x2, y2);
        GameCaptureRegion.GameRegionMove((rect, scale) => (x2 * scale, y2 * scale));
        await Delay(50, ct);
        GlobalMethod.LeftButtonUp();
        await Delay(50, ct);
        GameCaptureRegion.GameRegionMove((rect, scale) => (rect.Width / 2d, rect.Width / 2d));
    }

    /// <summary>
    /// 调整地图缩放级别以加速移动
    /// </summary>
    /// <param name="zoomIn">是否放大地图</param>
    [Obsolete]
    private async Task AdjustMapZoomLevel(bool zoomIn)
    {
        if (zoomIn)
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (_tpConfig.ZoomButtonX * scale, _zoomInButtonY * scale));
        }
        else
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (_tpConfig.ZoomButtonX * scale, _zoomOutButtonY * scale));
        }

        if (_tpConfig.MapMoveStepDivisor)
        {
            await Delay(50, ct);
        }else
        {
            await Delay(100, ct);
        }
    }


    /// <summary>
    /// 调整地图的缩放等级（整数缩放级别）。
    /// </summary>
    /// <param name="zoomLevel">目标等级：1-6。整数。随着数字变大地图越小，细节越少。</param>
    [Obsolete]
    public async Task AdjustMapZoomLevel(int zoomLevel)
    {
        for (int i = 0; i < 5; i++)
        {
            await AdjustMapZoomLevel(false);
        }

        await Delay(200, ct);
        for (int i = 0; i < 6 - zoomLevel; i++)
        {
            await AdjustMapZoomLevel(true);
        }
    }

    /// <summary>
    /// 将大地图缩放等级设置为指定值
    /// </summary>
    /// <remarks>
    /// 缩放等级说明：
    /// - 数值范围：1.0(最大地图) 到 6.0(最小地图)
    /// - 缩放效果：数值越大，地图显示范围越广，细节越少
    /// - 缩放位置：1.0 对应缩放条最上方，6.0 对应缩放条最下方
    /// - 推荐范围：建议在 2.0 到 5.0 之间调整，过大或过小可能影响操作
    /// </remarks>
    /// <param name="zoomLevel">当前缩放等级：1.0-6.0，浮点数。</param>
    /// <param name="targetZoomLevel">目标缩放等级：1.0-6.0，浮点数。</param>
    public async Task AdjustMapZoomLevel(double zoomLevel, double targetZoomLevel)
    {
        // Logger.LogInformation("调整地图缩放等级：{zoomLevel:0.000} -> {targetZoomLevel:0.000}", zoomLevel, targetZoomLevel);
        int initialY = (int)(_tpConfig.ZoomStartY + (_tpConfig.ZoomEndY - _tpConfig.ZoomStartY) * (zoomLevel - 1) / 5d);
        int targetY = (int)(_tpConfig.ZoomStartY + (_tpConfig.ZoomEndY - _tpConfig.ZoomStartY) * (targetZoomLevel - 1) / 5d);
        await MouseClickAndMove(_tpConfig.ZoomButtonX+10, initialY, _tpConfig.ZoomButtonX+10, targetY);
        if (_tpConfig.MapMoveStepDivisor)
        {
            await Delay(50, ct);
        }else
        {
            await Delay(100, ct);
        }
    }

    private async Task MouseMoveMap(int pixelDeltaX, int pixelDeltaY, int steps = 10)
    {
        double dpi = TaskContext.Instance().DpiScale;
        int[] stepX = GenerateSteps((int)(pixelDeltaX / dpi), steps);
        int[] stepY = GenerateSteps((int)(pixelDeltaY / dpi), steps);
        //检查标记
        var isMark = true;

        if (_tpConfig.MapMoveStepDivisor)
        {
            int signX = -Math.Sign(pixelDeltaX);
            int signY = -Math.Sign(pixelDeltaY);
            GameCaptureRegion.GameRegionMove((rect, _) =>
                (rect.Width / 2d + Random.Shared.Next(rect.Width / 5, rect.Width *3/10)*signX,
                    rect.Height / 2d + Random.Shared.Next(rect.Height / 5, rect.Height *3/10)*signY));
        }
        else
        {
            GameCaptureRegion.GameRegionMove((rect, _) =>
                (rect.Width / 2d + Random.Shared.Next(-rect.Width / 6, rect.Width / 6),
                    rect.Height / 2d + Random.Shared.Next(-rect.Height / 6, rect.Height / 6)));
        }

        await Delay(50+_tpConfig.StepIntervalMilliseconds-2, ct);
        Simulation.SendInput.Mouse.LeftButtonDown();
        await Delay(50+_tpConfig.StepIntervalMilliseconds-2, ct);
        
        if (_tpConfig.MapMoveStepDivisor)
        {
            using (var image = CaptureToRectArea())
            {
                var pos = image.SrcMat.At<Vec3b>(500,500);
                var pos2 = image.SrcMat.At<Vec3b>(600,500);
               
                for (var i = 1; i < steps; i++)
                {
                    var i1 = i;
                    
                    // Simulation.SendInput.Mouse.MoveMouseBy(stepX[i], stepY[i]);
                    GameCaptureRegion.GameRegionMoveBy((_, scale) => (stepX[i1] * scale, stepY[i1] * scale));
                    if(i==1) await Delay(50, ct);
                    await Delay(_tpConfig.StepIntervalMilliseconds, ct);
                    
                    if (i >= steps/2 && steps > 3 && isMark)
                    {
                        using (var image2 = CaptureToRectArea())
                        {
                            var pos3 = image2.SrcMat.At<Vec3b>(500,500);
                            var pos4 = image2.SrcMat.At<Vec3b>(600,500);
                            if (pos3 == pos && pos4 == pos2)
                            {
                                using var esc = image2.Find(QuickTeleportAssets.Instance.MapCloseButtonWhiteRo);
                                if (esc.IsExist())
                                {
                                    TaskControl.Logger.LogWarning("地图遮挡，重新调整");
                                    await Delay(1500, ct);
                                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                                    await Delay(1500, ct);
                                }
                                else
                                {
                                    TaskControl.Logger.LogWarning("地图拖动异常，重新调整");
                                }
                                
                                break;
                            }
                            isMark = false;
                        }
                    } 
                }
            }
        }
        else
        {
            for (var i = 0; i < steps; i++)
            {
                var i1 = i;
                await Delay(_tpConfig.StepIntervalMilliseconds, ct);
                // Simulation.SendInput.Mouse.MoveMouseBy(stepX[i], stepY[i]);
                GameCaptureRegion.GameRegionMoveBy((_, scale) => (stepX[i1] * scale, stepY[i1] * scale));
            }
        }

        if (!_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
    }

    /// <summary>
    /// 快速拖动模式下：等大地图视区像素稳定再返回，超时兜底。
    /// 通过对 (500,500) / (600,500) 两点 BGR 像素连续采样，连续 stableHits 次相等视为稳定。
    /// 仅在 MapMoveStepDivisor=true && FastDragRecognitionEnabled=true 时由调用方决定是否使用。
    /// fast-drag-recognition-acceleration spec / design.md §3.1
    /// </summary>
    /// <param name="timeoutMs">兜底超时（与原固定 Delay 等值），超时即返回</param>
    /// <param name="pollMs">每次轮询间隔，默认 30ms（约一帧）</param>
    /// <param name="stableHits">连续多少次采样像素一致视为稳定，默认 2</param>
    private async Task WaitMapStableOrTimeoutAsync(int timeoutMs, int pollMs = 30, int stableHits = 2)
    {
        long deadline = Environment.TickCount + timeoutMs;
        Vec3b? prev1 = null, prev2 = null;
        int hits = 0;
        while (Environment.TickCount < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ra = CaptureToRectArea();
                var p1 = ra.SrcMat.At<Vec3b>(860, 500);
                var p2 = ra.SrcMat.At<Vec3b>(860, 540);
                if (ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo).IsExist() && prev1.HasValue && p1 == prev1.Value && p2 == prev2!.Value)
                {
                    if (++hits >= stableHits)
                    {
                        Logger.LogDebug("检测到地图稳定");
                        return;
                    }
                }
                else
                {
                    hits = 0;
                }
                prev1 = p1;
                prev2 = p2;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 截图异常（暂停态/帧不可用）→ 不抛，下一轮重试，外层暂停信号会接管
                Logger.LogDebug("[快速识别] 像素采样异常: {Msg}", ex.Message);
                hits = 0;
            }
            await Delay(pollMs, ct);
        }
        Logger.LogDebug("检测到地图失败");
    }

    /// <summary>
    /// 快速识别模式下：地图点击后等"传送"按钮 popup + 点按钮 + 用 IsLoadingScreen 确认进入传送加载。
    /// 替代 Delay(500) + ClickTpPoint：
    /// 1. 高配机按钮 popup 50-150ms 就出现，立即点
    /// 2. 容错点击：点完按钮持续探测 IsLoadingScreen；未进入加载页则在窗口内重点（最多 3 次）
    /// 3. 仍未进 → 抛异常让上层走原 ClickTpPoint 兜底（保证不丢传送）
    /// 返回 true 表示已确认进入传送加载（IsLoadingScreen 命中），false 表示需要走兜底。
    /// fast-drag-recognition-acceleration spec / final click pre-stop optimization v2
    /// </summary>
    private async Task<bool> FastClickTeleportButtonAsync(int popupTimeoutMs = 500, int loadingTimeoutMs = 2500, int pollMs = 30)
    {
        long popupDeadline = Environment.TickCount + popupTimeoutMs;
        // 阶段 1：等按钮 popup 出现
        while (Environment.TickCount < popupDeadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ra = CaptureToRectArea();
                using var found = ra.Find(_assets.TeleportButtonRo);
                if (found.IsExist())
                {
                    found.Click();
                    goto AfterClick;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("[快速识别] 探测传送按钮异常: {Msg}", ex.Message);
            }
            await Delay(pollMs, ct);
        }
        return false; // 阶段 1 超时：上层走 ClickTpPoint 兜底

    AfterClick:
        // 阶段 2：容错重点 + IsLoadingScreen 确认。点击可能因动画 popup 中"按钮可见但不可点"而无效。
        long loadingDeadline = Environment.TickCount + loadingTimeoutMs;
        int reclickCount = 0;
        while (Environment.TickCount < loadingDeadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ra = CaptureToRectArea();
                if (TeleportLoadingDetector.IsLoadingScreen(ra.SrcMat))
                {
                    return true;
                }
                // 未进入加载页：尝试在窗口内重点按钮（最多 3 次）
                using var found = ra.Find(_assets.TeleportButtonRo);
                if (found.IsExist() && reclickCount < 3)
                {
                    found.Click();
                    reclickCount++;
                    Logger.LogDebug("[快速识别] 阶段 2 容错重点传送按钮（第 {N} 次）", reclickCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("[快速识别] 阶段 2 异常: {Msg}", ex.Message);
            }
            await Delay(pollMs, ct);
        }
        return false; // 阶段 2 超时：回退到 ClickTpPoint 兜底
    }

    private int[] GenerateSteps(int delta, int steps)
    {
        double[] factors = new double[steps];
        double sum = 0;
        for (int i = 0; i < steps; i++)
        {
            factors[i] = Math.Cos(i * Math.PI / (2 * steps));
            sum += factors[i];
        }

        int[] stepsArr = new int[steps];
        int remaining = delta;

        // 两阶段分配：基础值 + 余数补偿
        for (int i = 0; i < steps; i++)
        {
            double ratio = factors[i] / sum;
            stepsArr[i] = (int)(delta * ratio); // 基础值
            remaining -= stepsArr[i];
        }

        int center = steps / 2;
        for (int r = 0; r < Math.Abs(remaining); r++)
        {
            int target = (center + r) % steps; // 从中点开始螺旋分配
            stepsArr[target] += remaining > 0 ? 1 : -1;
        }

        return stepsArr;
    }

    public Point2f GetPositionFromBigMap(string mapName)
    {
        return GetBigMapCenterPoint(mapName);
    }

    public Point2f? GetPositionFromBigMapNullable(string mapName)
    {
        try
        {
            return GetBigMapCenterPoint(mapName);
        }
        catch
        {
            return null;
        }
    }

    public Rect GetBigMapRect(string mapName)
    {
        var rect = new Rect();
        NewRetry.Do(() =>
        {
            // 判断是否在地图界面
            using var ra = CaptureToRectArea();
            using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
            if (mapScaleButtonRa.IsExist())
            {
                try
                {  
                    using var ra2 = CaptureToRectArea();
                    using var mapScaleButtonRa2 = ra2.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
                    if (mapScaleButtonRa2.IsExist())
                    {
                        rect = MapManager.GetMap(mapName, _mapMatchingMethod).GetBigMapRect(ra.CacheGreyMat);
                    }
                }
                catch (Exception)
                {
                    rect = default; // 发生异常视为识别失败
                }
                
                if (rect == default)
                {
                    // 滚轮调整后再次识别
                    Simulation.SendInput.Mouse.VerticalScroll(2);
                    Sleep(500);
                    throw new RetryException("识别大地图位置失败");
                }
            }
            else
            {
                throw new RetryException("当前不在地图界面");
            }
        }, TimeSpan.FromMilliseconds(60), 20);

        if (rect == default)
        {
            throw new InvalidOperationException("多次重试后，识别大地图位置失败");
        }

        Debug.WriteLine("识别大地图在全地图位置矩形：" + rect);
        // 提瓦特大陆由于用的256的图，需要做特殊逻辑
        if (mapName == MapTypes.Teyvat.ToString())
        {
            const int s = TeyvatMap.BigMap256ScaleTo2048; // 相对2048做8倍缩放
            rect = new Rect(rect.X * s, rect.Y * s, rect.Width * s, rect.Height * s);
        }

        return MapManager.GetMap(mapName, _mapMatchingMethod).ConvertImageCoordinatesToGenshinMapCoordinates(rect)!.Value;
    }

    public Point2f GetBigMapCenterPoint(string mapName)
    {
        Point2f p = new Point2f();
        bool inMapUi = false;

        // 大地图可能打开较慢，重试 5 次、每次间隔 100 毫秒，直到识别到非空位置
        for (int attempt = 0; attempt < 10; attempt++)
        {
            // 判断是否在地图界面
            using var ra = CaptureToRectArea();
            using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
            if (mapScaleButtonRa.IsExist())
            {
                inMapUi = true;
                try
                {
                    p = MapManager.GetMap(mapName, _mapMatchingMethod).GetBigMapPosition(ra.CacheGreyMat);
                }
                catch (Exception ex)
                {
                    throw new MapPositionNotRecognizedException("大地图特征点匹配引发异常：" + ex.Message, ex);
                }

                if (!p.IsEmpty())
                {
                    break;
                }
            }

            if (attempt < 4)
            {
                Thread.Sleep(70);
            }
        }

        if (!inMapUi)
        {
            if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
            throw new InvalidOperationException("当前不在地图界面");
        }

        if (p.IsEmpty())
        {
            if (_tpConfig.MapMoveStepDivisor)Simulation.SendInput.Mouse.LeftButtonUp();
            throw new MapPositionNotRecognizedException("大地图特征点匹配识别位置失败");
        }

        Debug.WriteLine("识别大地图在全地图位置：" + p);
        // 提瓦特大陆由于用的256的图，需要做特殊逻辑
        var (x, y) = (p.X, p.Y);
        if (mapName == MapTypes.Teyvat.ToString())
        {
            (x, y) = (p.X * TeyvatMap.BigMap256ScaleTo2048, p.Y * TeyvatMap.BigMap256ScaleTo2048);
        }

        return MapManager.GetMap(mapName, _mapMatchingMethod).ConvertImageCoordinatesToGenshinMapCoordinates(new Point2f(x, y))!.Value;
    }

    /// <summary>
    /// 当无法获取当前位置时，直接根据目标坐标强制计算并跃迁到对应区域的地图
    /// </summary>
    private async Task ForceJumpToTargetArea(double x, double y, string mapName)
    {
        if (mapName == MapTypes.Teyvat.ToString())
        {
            string targetCountry = "当前位置";
            double minDistance = double.MaxValue;
            foreach (var (country, position) in MapLazyAssets.Instance.CountryPositions)
            {
                var distance = Math.Sqrt(Math.Pow(position[0] - x, 2) + Math.Pow(position[1] - y, 2));
                if (distance < minDistance)
                {
                    minDistance = distance;
                    targetCountry = country;
                }
            }

            if (targetCountry != "当前位置")
            {
                await SwitchArea(targetCountry);
            }
        }
        else
        {
            await SwitchArea(MapTypesExtensions.ParseFromName(mapName).GetDescription());
        }
    }

    /// <summary>
    /// 获取最接近的N个传送点坐标和所处区域
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="n">获取最近的 n 个传送点</param>
    /// <returns></returns>
    public List<GiTpPosition> GetNearestNTpPoints(double x, double y, string mapName, int n = 1)
    {
        // 检查 n 的合法性
        if (n < 1)
        {
            throw new ArgumentException("The value of n must be greater than or equal to 1.", nameof(n));
        }

        // 按距离排序并选择前 n 个点
        return MapLazyAssets.Instance.ScenesDic[mapName].Points
            .OrderBy(tp => Math.Pow(tp.X - x, 2) + Math.Pow(tp.Y - y, 2))
            .Take(n)
            .ToList();
    }

    public async Task<bool> SwitchRecentlyCountryMap(double x, double y, string? forceCountry = null)
    {
        // 可能是地下地图，切换到地上地图
        using var ra2 = CaptureToRectArea();
        if (Bv.BigMapIsUnderground(ra2))
        {
            using var ra3 = CaptureToRectArea();
            ra3.Find(_assets.MapUndergroundToGroundButtonRo, rg => rg.Click());
            await Delay(200, ct);
        }

        // 识别当前位置
        // 第一次识别可能因地图刚打开特征点未渲染而失败 → 短轮询补救（最多 ~450ms）。
        // fast-drag-recognition-acceleration spec / SwitchRecentlyCountryMap regression safety net：
        // 防止"识别失败 → minDistance 保持 MaxValue → 误走 SwitchArea 弯路（即使传送点就在旁边）"
        var minDistance = double.MaxValue;
        Point2f? bigMapCenterPointNullable = GetPositionFromBigMapNullable(MapTypes.Teyvat.ToString());
        if (bigMapCenterPointNullable == null)
        {
            for (int i = 0; i < 3 && bigMapCenterPointNullable == null; i++)
            {
                await Delay(150, ct);
                bigMapCenterPointNullable = GetPositionFromBigMapNullable(MapTypes.Teyvat.ToString());
            }
        }

        if (bigMapCenterPointNullable != null)
        {
            var bigMapCenterPoint = bigMapCenterPointNullable.Value;
            TaskControl.Logger.LogDebug("识别当前大地图位置：{Pos}", bigMapCenterPoint);
            minDistance = Math.Sqrt(Math.Pow(bigMapCenterPoint.X - x, 2) + Math.Pow(bigMapCenterPoint.Y - y, 2));
            if (minDistance < 50)
            {
                // TaskControl.Logger.LogError("地图位置已经在传送点附近，不切换");
                // 点位很近的情况下不切换
                return false;
            }
        }
        
        string minCountry = "当前位置";
        foreach (var (country, position) in MapLazyAssets.Instance.CountryPositions)
        {
            var distance = Math.Sqrt(Math.Pow(position[0] - x, 2) + Math.Pow(position[1] - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                minCountry = country;
            }
        }
        
        // TaskControl.Logger.LogInformation("切换位置2：{minCountry}", minCountry);
        
        // if (_tpConfig.MapMoveStepDivisor && forceCountry != null && minCountry == forceCountry)
        // {
        //     TaskControl.Logger.LogDebug("快速拖动模式强制切换区域：{t}",forceCountry);
        //     await SwitchArea(forceCountry);
        //     return true;
        // }

        if (minCountry != "当前位置")
        {
            if (forceCountry != null)
            {
                minCountry = forceCountry;
            }
            await SwitchArea(minCountry);
            return true;
        }

        return false;
    }

    internal async Task SwitchArea(string areaName)
    {
        GameCaptureRegion.GameRegionClick((rect, scale) => (rect.Width - 160 * scale, rect.Height - 60 * scale));
        
        // 加速识别模式：等地区菜单弹出（白色 X 关闭按钮出现），兜底 300ms 与旧 Delay 等值。
        // MapCloseButtonWhiteRo = 弹出层（含地区菜单）的白色 X 关闭按钮。
        // fast-drag-recognition-acceleration spec / SwitchArea menu popup optimization
        if (_tpConfig.MapMoveStepDivisor && _tpConfig.FastDragRecognitionEnabled)
        {
            await Delay(100, ct);
            await WaitForElementOrTimeoutAsync(QuickTeleportAssets.Instance.MapCloseButtonWhiteRo, timeoutMs:1000);
        }
        else
        {
            await Delay(300, ct);
        }

        using var ra = CaptureToRectArea();
        var list = ra.FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = new Rect(ra.Width * 2 / 3, 0, ra.Width / 3, ra.Height),
            ReplaceDictionary = new Dictionary<string, string[]>
            {
                ["渊下宫"] = ["渊下宮"],
            },
        });

        string minCountryLocalized = this.stringLocalizer.WithCultureGet(this.cultureInfo, areaName);
        Region? matchRect = list.OrderByDescending(r => r.Y).FirstOrDefault(r => r.Text.Contains(minCountryLocalized));
        if (matchRect == null)
        {
            Logger.LogWarning("切换区域失败：{Country}", areaName);
            if (areaName == MapTypes.TheChasm.GetDescription() || areaName == MapTypes.Enkanomiya.GetDescription() || areaName == MapTypes.SeaOfBygoneEras.GetDescription() || areaName == MapTypes.AncientSacredMountain.GetDescription() || areaName == MapTypes.TempleOfSpace.GetDescription())
            {
                throw new Exception($"切换独立地图区域[{areaName}]失败");
            }
        }
        else
        {
            matchRect.Click();
            TaskControl.Logger.LogInformation("切换到区域：{Country}", areaName);
        }

        // 加速识别模式：等地图视区像素稳定即继续，兜底 500ms（与旧 Delay 等值）
        // fast-drag-recognition-acceleration spec / SwitchArea tail wait optimization
        if (_tpConfig.MapMoveStepDivisor && _tpConfig.FastDragRecognitionEnabled)
        {
            await WaitMapStableOrTimeoutAsync(timeoutMs: 500);
        }
        else
        {
            await Delay(500, ct);
        }
    }

    public async Task Tp(string name)
    {
        // 通过大地图传送到指定传送点
        await Delay(500, ct);
    }

    public async Task TpByF1(string name)
    {
        // 传送到指定传送点
        await Delay(500, ct);
    }

    public async Task ClickTpPoint(ImageRegion imageRegion)
    {
        // 1.判断是否在地图界面
        if (!Bv.IsInBigMapUi(imageRegion)) throw new RetryException("不在地图界面");

        // 2. 判断是否已经点出传送按钮
        var hasTeleportButton = CheckTeleportButton(imageRegion);
        if (hasTeleportButton) return;   // 可以传送了，结束
        // 3. 没点出传送按钮，且不存在外部地图关闭按钮
        // 说明只有两种可能，a. 点出来的是未激活传送点或者标点 b. 选择传送点选项列表
        var mapCloseRa1 = imageRegion.Find(_assets.MapCloseButtonRo);
        if (!mapCloseRa1.IsEmpty()) throw new TpPointNotActivate("传送点未激活或不存在");

        // 4. 循环判断选项列表是否有传送点(未激活点位也在里面)
        var hasMapChooseIcon = CheckMapChooseIcon(imageRegion);
        // 没有传送点说明不是传送点
        if (!hasMapChooseIcon) throw new TpPointNotActivate("选项列表不存在传送点");
        var teleportButtonFound = await NewRetry.WaitForElementAppear(
            _assets.TeleportButtonRo,
            () => { },
            ct,
            6,
            300
        );
        if (!teleportButtonFound) throw new TpPointNotActivate("选项列表的传送点未激活");
        await NewRetry.WaitForElementDisappear(
            _assets.TeleportButtonRo,
            screen =>
            {
                screen.Find(_assets.TeleportButtonRo, ra =>
                {
                    ra.Click();
                    ra.Dispose();
                });
            },
            ct,
            6,
            300
        );
    }

    private bool CheckTeleportButton(ImageRegion imageRegion)
    {
        var hasTeleportButton = false;
        imageRegion.Find(_assets.TeleportButtonRo, ra =>
        {
            ra.Click();
            hasTeleportButton = true;
        });
        return hasTeleportButton;
    }

    /// <summary>
    /// 全匹配一遍并进行文字识别
    /// 60ms ~200ms
    /// </summary>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    private bool CheckMapChooseIcon(ImageRegion imageRegion)
    {
        var isHdrCapture = TaskContext.Instance().Config.CaptureMode == nameof(CaptureModes.WindowsGraphicsCaptureHdr);
        var hasMapChooseIcon = false;

        // 全匹配一遍
        using var mapChooseIconRoi = imageRegion.CacheGreyMat[_assets.MapChooseIconRoi].Clone();
        var rResultList = MatchTemplateHelper.MatchMultiPicForOnePic(mapChooseIconRoi, _assets.MapChooseIconGreyMatList, isHdrCapture ? 0.7 : 0.8);
        // 按高度排序
        if (rResultList.Count > 0) {
           
            rResultList = [.. rResultList.OrderBy(x => x.Y)];
            // 点击最高的
            foreach (var iconRect in rResultList)
            {
                // 200宽度的文字区域
                using var ra = imageRegion.DeriveCrop(_assets.MapChooseIconRoi.X + iconRect.X + iconRect.Width, _assets.MapChooseIconRoi.Y + iconRect.Y - 8, 200, iconRect.Height + 16);
                using var textRegion = ra.Find(new RecognitionObject
                {
                    // RecognitionType = RecognitionTypes.Ocr,
                    RecognitionType = isHdrCapture ? RecognitionTypes.Ocr : RecognitionTypes.ColorRangeAndOcr,
                    LowerColor = new Scalar(249, 249, 249), // 只取白色文字
                    UpperColor = new Scalar(255, 255, 255),
                });
                if (string.IsNullOrEmpty(textRegion.Text) || textRegion.Text.Length == 1)
                {
                    continue;
                }

                TaskControl.Logger.LogInformation("传送：点击 {Option}", textRegion.Text.Replace(">", ""));
                var time = TaskContext.Instance().Config.QuickTeleportConfig.TeleportListClickDelay;
                time = time < 500 ? 500 : time;
                Thread.Sleep(_tpConfig.MapMoveStepDivisor?200:time);
                ra.Click();
                hasMapChooseIcon = true;
                break;
            }
        }

        return hasMapChooseIcon;
    }

    /// <summary>
    /// 给定的映射关系可以表示成 (x, y) 对的形式，其中 x 是输入值，y 是输出值
    ///    1 - 1
    ///  0.8 - 2
    ///  0.6 - 3
    ///  0.4 - 4
    ///  0.2 - 5
    ///    0 - 6
    /// y=−5x+6
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    public double GetBigMapZoomLevel(ImageRegion region)
    {
        //失败后重试2次
        double s = 0;
        for (int i = 0; i < 10; i++)
        {
            s = Bv.GetBigMapScale(region);
            if( s > 0) break;
             TaskControl.Logger.LogWarning("获取大地图缩放级别失败，重试中...（{Attempt}/2）", i + 1);
             Thread.Sleep(100);
        }
        
        // 1~6 的缩放等级
        return (-5 * s) + 6;
    }

    /// <summary>
    /// 计算第 attempt 次尝试点击传送点时应使用的"可点击缩放"目标级别。
    /// 缩放语义：值越小越放大（图标越大越易点出传送按键）。在 [minZoom, displayZoom] 区间内
    /// 随尝试序号收敛——attempt 0 用 displayZoom(4.4)，后续逐步朝 minZoom 放大，
    /// 使每次重试都换一个不同的、未被证明失败的缩放档位。
    /// 详见 .kiro/specs/teleport-wrong-zoom-no-teleport-button-fix/design.md §2.1。
    /// 纯函数：无 UI / Mat / logger 依赖，便于 PBT 撒输入。
    /// </summary>
    /// <param name="attempt">尝试序号（0 起，对应 Tp 的 retryTimes/i）</param>
    /// <param name="displayZoom">传送点显示缩放（DisplayTpPointZoomLevel=4.4）</param>
    /// <param name="minZoom">最放大可点击下限（TpConfig.MinZoomLevel，默认 2.0）</param>
    /// <returns>夹在 [minZoom, displayZoom] 的目标缩放</returns>
    public static double ComputeClickZoomCandidate(int attempt, double displayZoom, double minZoom)
    {
        // 防御：保证 lo <= hi（displayZoom/minZoom 顺序异常时不抛）
        double hi = Math.Max(displayZoom, minZoom);
        double lo = Math.Min(displayZoom, minZoom);
        if (attempt <= 0) return hi;            // 第 0 次：传送点显示缩放
        // 总尝试数固定 3（Tp 的 for i<3）→ 候选点 hi, 中点, lo
        const int totalAttempts = 3;
        int clamped = Math.Min(attempt, totalAttempts - 1);
        double t = (double)clamped / (totalAttempts - 1); // attempt1→0.5, attempt2→1.0
        return hi - (hi - lo) * t;              // 朝 lo（更放大）线性收敛
    }
}

public class MapPositionNotRecognizedException : Exception
{
    public MapPositionNotRecognizedException(string message) : base(message) { }
    public MapPositionNotRecognizedException(string message, Exception innerException) : base(message, innerException) { }
}
