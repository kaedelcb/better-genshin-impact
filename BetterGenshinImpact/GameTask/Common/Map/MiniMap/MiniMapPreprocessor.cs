using System;
using System.Diagnostics;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Map.MiniMap;

public class MiniMapPreprocessor : IDisposable
{
    private static readonly MaskCalculator _maskCalculator = new();
    private static readonly CameraOrientationCalculator _coCalculator = new();
    
    public (float, float) PredictRotationWithConfidence(Mat miniMap)
    {
        var (src, mask) = _maskCalculator.Process1(miniMap);
        using (src)
        {
            return _coCalculator.PredictRotation(src, mask);
        }
    }

    public float PredictRotation(Mat miniMap)
    {
        return PredictRotationWithConfidence(miniMap).Item1;
    }

    public (Mat, Mat) GetMiniMapAndMask(Mat miniMap)
    {
        //Debug.WriteLine($"输入图片尺寸为{miniMap.Size()} 类型为 {miniMap.Type()}");
        var (src, mask) = _maskCalculator.Process1(miniMap);
        using (src)
        {
            var (angle, confidence) = _coCalculator.PredictRotation(src, mask);
            // [小地图诊断] 朝向角度置信度过低，通常意味着小地图被技能特效/UI 遮挡或渲染未稳定，
            // 会连带拖垮后续模板匹配（匹配方向错位）。只在偏低时打，避免刷屏。
            if (AutoPathing.MiniMapPositionDiagnostics.Enabled && confidence < 0.5)
            {
                TaskControl.Logger.LogDebug(
                    "[小地图诊断] 朝向角度置信度偏低：角度={Angle:F1} 置信度={Conf:F3}（疑似小地图被遮挡/未稳定）",
                    angle, confidence);
            }
            return _maskCalculator.Process2(angle);
        }
    }

    public void Dispose()
    {
        _coCalculator.Dispose();
        _maskCalculator.Dispose();
    }
}