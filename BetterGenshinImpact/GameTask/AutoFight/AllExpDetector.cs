using BetterGenshinImpact.GameTask.AutoFriendship.Assets;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 全经验检测器（multiplayer-hoeing-exp-cap-stop "检测所有经验" 模式）。
/// 复用好感任务（AutoFriendship）的通用经验图标模板 exp.png，在战斗过程中异步检测
/// 屏幕上是否出现"经验掉落"图标——不区分精英/小怪，只要掉经验即命中。
///
/// 与 <see cref="ExperienceDetector"/>（只认 57/58/60 精英数字 + 像素色校验）互为两档：
/// - 房主同步开关 ExpCapDetectAllExp 关 → ExperienceDetector（只精英）；
/// - 开 → 本检测器（所有经验）。
///
/// 生命周期/语义与 ExperienceDetector 完全一致，均实现 <see cref="IExperienceDetector"/>，
/// 调用方（PathExecutor）只面向接口，无需关心具体实现。
/// </summary>
public sealed class AllExpDetector : IExperienceDetector
{
    private static readonly ILogger Logger = TaskControl.Logger;

    /// <summary>检测循环间隔（毫秒），与 ExperienceDetector 一致。</summary>
    private const int DetectionIntervalMs = 100;

    /// <summary>模板匹配阈值，与好感任务一致（0.85）。</summary>
    private const double MatchThreshold = 0.85;

    private readonly Mat? _expTemplate;
    private readonly CancellationTokenSource _linkedCts;
    private readonly TaskCompletionSource<bool> _resultTcs;
    private Task? _detectionTask;
    private bool _disposed;

    /// <summary>获取检测结果：是否检测到经验图标。</summary>
    public bool HasDetectedExperience =>
        _resultTcs.Task.IsCompletedSuccessfully && _resultTcs.Task.Result;

    public AllExpDetector(CancellationToken externalToken)
    {
        // 复用好感任务的通用经验图标模板（exp.png）。加载失败为 null，Start 时跳过检测。
        _expTemplate = AutoFriendshipResourceLoader.LoadExpTemplate();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _resultTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Start()
    {
        if (_expTemplate == null || _expTemplate.Empty())
        {
            Logger.LogWarning("全经验检测：exp.png 模板不可用，跳过检测");
            return;
        }

        _detectionTask = Task.Run(() => DetectionLoop(_linkedCts.Token), _linkedCts.Token);
    }

    public async Task StopAsync()
    {
        _linkedCts.Cancel();
        if (_detectionTask != null)
        {
            try
            {
                await _detectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消，忽略
            }
        }

        _resultTcs.TrySetResult(false);
    }

    /// <summary>
    /// 后台检测循环：截屏 → 对全帧做 exp.png 模板匹配（CCoeffNormed），maxVal ≥ 阈值即命中。
    /// 与好感任务 AutoFriendshipTask 的经验检测逻辑一致（去掉摩拉分支）。
    /// </summary>
    private void DetectionLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var capture = CaptureToRectArea();
                using var res = new Mat();
                Cv2.MatchTemplate(capture.SrcMat, _expTemplate, res, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out _);
                if (maxVal >= MatchThreshold)
                {
                    Logger.LogInformation("基于怪物经验判断：识别到经验掉落（全经验模式，匹配度 {Val:F2}）", maxVal);
                    _resultTcs.TrySetResult(true);
                    return; // 命中后退出循环
                }

                Thread.Sleep(DetectionIntervalMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "全经验检测循环中发生异常，继续下一轮");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _linkedCts.Cancel();
        _linkedCts.Dispose();
        _resultTcs.TrySetResult(false);
        _expTemplate?.Dispose();
        _detectionTask = null;
    }
}
