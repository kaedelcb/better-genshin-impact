#nullable enable
using System;
using System.IO;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 独立理由（R4.2）：诊断埋点被 NavigationInstance/PathExecutor/SceneBaseMapByTemplateMatch/
/// MiniMapPreprocessor 四个公版同名或高冲突文件跨文件消费——集中为独立零冲突文件（B12/M12，用户拍板保留）。
/// 小地图坐标识别诊断埋点（茶包诊断设施，PR 不随公版——茶包固化，M17 走查定案）。
///
/// 目的：定位"回点时 Navigation.GetPosition 返回 (0,0)（识别失败）"到底卡在哪个环节：
///   1) 截图裁剪：MimiMapRect 区域是否取对（尺寸 / 是否纯黑 / 是否被 UI 遮挡）
///   2) 预处理：mask 占比是否异常（遮挡会让小地图圆盘 mask 缩水）
///   3) 角度识别：朝向置信度是否过低
///   4) 模板/SIFT 匹配：匹配置信度数值，走粗匹配还是精匹配，是否达阈值
///   5) Navigation 兜底：局部失败 → 全局匹配是否触发、是否仍失败
///
/// 用法：默认按开关 <see cref="Enabled"/> 输出结构化日志（DEBUG 级，带 [小地图诊断] 前缀，便于 grep）。
/// 识别失败（(0,0)）时，按节流间隔把当时的小地图截图 dump 到 log/minimap_diag/ 供肉眼看遮挡。
///
/// 注释中的 "Requirement N" 编号为历史 spec 的条款号（来源 spec 已归档，仅存编号）。
/// 该类为纯诊断设施，问题定位后可整体删除，不影响生产逻辑。
/// </summary>
public static class MiniMapPositionDiagnostics
{
    /// <summary>诊断总开关。读配置单例，UI 实时生效；配置为 null 回落默认 false（默认关闭，Requirement 5.1/7.4）。</summary>
    public static bool Enabled
        => ConfigService.Config?.MiniMapMatchTuningConfig?.DiagnosticsEnabled
           ?? MiniMapMatchTuningConfig.DefaultDiagnosticsEnabled;

    /// <summary>失败帧存图开关。读配置单例；配置为 null 回落默认 false（默认关闭，Requirement 5.2/7.4）。</summary>
    public static bool DumpFailedFrame
        => ConfigService.Config?.MiniMapMatchTuningConfig?.DumpFailedFrame
           ?? MiniMapMatchTuningConfig.DefaultDumpFailedFrame;

    /// <summary>失败帧存图节流（ms）。&lt;0 回落默认 2000 + 告警；配置为 null 回落默认（Requirement 5.3/5.5/7.4）。</summary>
    public static int DumpThrottleMs
    {
        get
        {
            var cfg = ConfigService.Config?.MiniMapMatchTuningConfig;
            if (cfg == null) return MiniMapMatchTuningConfig.DefaultDumpThrottleMs;
            var (value, fellBack) = MiniMapMatchTuningValidator.ValidateDumpThrottleMs(cfg.DumpThrottleMs);
            if (fellBack) MiniMapTuningWarn.OnceDumpThrottle(cfg.DumpThrottleMs);
            return value;
        }
    }

    private static long _lastDumpTicks;
    private static int _dumpSeq;

    private static ILogger Logger => TaskControl.Logger;

    /// <summary>
    /// 记录一次 GetPosition / GetPositionStable 调用的完整诊断信息。
    /// </summary>
    /// <param name="entry">入口名（GetPosition / GetPositionStable）。</param>
    /// <param name="miniMap">已裁剪的小地图彩色 Mat（裁剪后、预处理前）。</param>
    /// <param name="prevX">调用时的锚点 X（_prevX）。</param>
    /// <param name="prevY">调用时的锚点 Y（_prevY）。</param>
    /// <param name="resultPos">最终返回坐标。</param>
    /// <param name="branch">走的分支说明（如 "局部匹配命中" / "局部失败→全局命中" / "局部+全局均失败"）。</param>
    /// <param name="consecutiveFail">当前连续失败计数（仅 GetPosition 有意义，其它传 -1）。</param>
    public static void LogPosition(
        string entry,
        Mat miniMap,
        float prevX, float prevY,
        Point2f resultPos,
        string branch,
        int consecutiveFail)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            bool failed = resultPos is { X: 0, Y: 0 };
            var (meanBrightness, blackRatio) = ComputeBrightness(miniMap);

            var systemInfo = TaskContext.Instance().SystemInfo;
            var captureRect = systemInfo.ScaleMax1080PCaptureRect;
            var mapAssets = MapAssets.Get(captureRect.Width, captureRect.Height);

            Logger.LogDebug(
                "[小地图诊断] {Entry} 结果=({Rx:F1},{Ry:F1}) {FailTag} | 分支={Branch} | 锚点=({Px:F1},{Py:F1}) 连续失败={Fail} | 截图={W}x{H} 均亮度={Mean:F1} 近黑占比={Black:P0} | 区域={RectW}x{RectH}@({RectX},{RectY})",
                entry,
                resultPos.X, resultPos.Y,
                failed ? "【识别失败】" : "OK",
                branch,
                prevX, prevY,
                consecutiveFail,
                miniMap.Width, miniMap.Height,
                meanBrightness, blackRatio,
                mapAssets.MimiMapRect.Width, mapAssets.MimiMapRect.Height,
                mapAssets.MimiMapRect.X, mapAssets.MimiMapRect.Y);

            if (failed && DumpFailedFrame)
            {
                DumpFrame(entry, miniMap);
            }
        }
        catch (Exception ex)
        {
            // 诊断埋点绝不能影响主流程：吞掉任何异常，仅记一条 debug 日志（含 ex）。
            Logger.LogDebug(ex, "[小地图诊断] 记录诊断信息时异常（已忽略，不影响识别）");
        }
    }

    /// <summary>
    /// 计算小地图整体平均亮度与近黑像素占比，用来判断"截图是否纯黑 / 区域是否取错 / 是否被全屏遮挡"。
    /// </summary>
    private static (double meanBrightness, double blackRatio) ComputeBrightness(Mat miniMap)
    {
        if (miniMap.Empty())
        {
            return (0, 1);
        }

        using var grey = miniMap.Channels() > 1
            ? miniMap.CvtColor(ColorConversionCodes.BGR2GRAY)
            : miniMap.Clone();

        var mean = Cv2.Mean(grey).Val0;

        // 近黑像素（< 25）占比
        using var blackMask = new Mat();
        Cv2.Threshold(grey, blackMask, 25, 255, ThresholdTypes.BinaryInv);
        double blackCount = Cv2.CountNonZero(blackMask);
        double total = grey.Rows * grey.Cols;
        double blackRatio = total > 0 ? blackCount / total : 1;

        return (mean, blackRatio);
    }

    /// <summary>
    /// 把识别失败时的小地图截图 dump 到 log/minimap_diag/，节流避免刷爆磁盘。
    /// </summary>
    private static void DumpFrame(string entry, Mat miniMap)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastDumpTicks);
        if (nowTicks - last < TimeSpan.FromMilliseconds(DumpThrottleMs).Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastDumpTicks, nowTicks);

        try
        {
            var dir = Global.Absolute(@"log\minimap_diag");
            Directory.CreateDirectory(dir);
            var seq = Interlocked.Increment(ref _dumpSeq);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{entry}_{seq:D4}.png");
            Cv2.ImWrite(file, miniMap);
            Logger.LogDebug("[小地图诊断] 识别失败帧已保存：{File}", file);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[小地图诊断] 保存失败帧截图异常（已忽略）");
        }
    }
}
