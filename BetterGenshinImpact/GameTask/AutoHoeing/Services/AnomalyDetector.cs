using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 异常状态检测器：冻结、白芙、复苏、烹饪界面
/// </summary>
public class AnomalyDetector
{
    private static readonly ILogger Logger = App.GetLogger<AnomalyDetector>();

    /// <summary>联机"已倒下"色块检测二次确认的等待间隔（毫秒）。</summary>
    private const int MultiplayerDefeatedRecheckDelayMs = 1000;

    private RecognitionObject? _frozenRo;
    private RecognitionObject? _whiteFurinaRo;
    private RecognitionObject? _revivalRo;
    private RecognitionObject? _cookingRo;

    public bool ShouldSwitchFurina { get; set; }
    
    /// <summary>
    /// 检测到复苏时的回调（用于联机模式上报异常状态）
    /// </summary>
    public Func<Task>? OnRevivalDetected { get; set; }

    /// <summary>
    /// 联机模式专用：检测到"已倒下"色块（联机倒地复苏界面）时的同步回调。
    /// 用于向当前 PathExecutor 发信号，让其在主循环抛 RetryException 进入异常处理流程。
    /// 单机模板匹配复苏不会触发此回调。
    /// </summary>
    public Action? OnMultiplayerDefeatedDetected { get; set; }

    public void LoadTemplates(string assetsDir)
    {
        _frozenRo = LoadRo(assetsDir, "解除冰冻.png", 1379, 574, 84, 39);

        // 复苏按钮检测：扩大区域覆盖单机和联机两种界面
        // 单机：底部偏右带圆形图标；联机：底部居中白底按钮
        _revivalRo = LoadRo(assetsDir, "复苏.png", 350, 900, 800, 150, 0.85);

        _cookingRo = LoadRo(assetsDir, "烹饪界面.png", 1547, 965, 268, 94, 0.95);

        _whiteFurinaRo = LoadRo(assetsDir, "白芙图标.png", 1634, 967, 116, 103, 0.97);
    }

    private static RecognitionObject? LoadRo(string dir, string fileName,
        int x, int y, int w, int h, double threshold = 0.8)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return null;

        var mat = Cv2.ImRead(path, ImreadModes.Color);
        var ro = RecognitionObject.TemplateMatch(mat, x, y, w, h);
        ro.Threshold = threshold;
        ro.InitTemplate();
        return ro;
    }

    /// <summary>
    /// 异常检测主循环
    /// </summary>
    public async Task RunDetectionLoop(Func<bool> isRunning, CancellationToken ct)
    {
        int loopCount = 0;

        while (isRunning() && !ct.IsCancellationRequested)
        {
            try
            {
                // 联机按线路切角色期间抑制异常检测的输入（空格/ESC 等），避免在配队/筛选/角色选择界面误按导致关界面
                if (TemplatePickupService.SuppressPickupInput)
                {
                    await Task.Delay(50, ct);
                    loopCount++;
                    continue;
                }

                // 每约250ms检测一次（5次循环 × 50ms）
                if (loopCount % 5 == 0)
                {
                    using var region = CaptureToRectArea();

                    // 冻结检测
                    if (_frozenRo != null)
                    {
                        using var result = region.Find(_frozenRo);
                        if (result.IsExist())
                        {
                            Logger.LogInformation("检测到冻结，尝试挣脱");
                            for (int i = 0; i < 3; i++)
                            {
                                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_SPACE);
                                await Task.Delay(30, ct);
                            }
                            continue;
                        }
                    }

                    // 白芙检测
                    if (!ShouldSwitchFurina && _whiteFurinaRo != null)
                    {
                        using var result = region.Find(_whiteFurinaRo);
                        if (result.IsExist())
                        {
                            Logger.LogInformation("检测到白芙，路线结束后切换形态");
                            ShouldSwitchFurina = true;
                            continue;
                        }
                    }

                    // 复苏检测（模板匹配，单机模式）
                    if (_revivalRo != null)
                    {
                        using var result = region.Find(_revivalRo);
                        if (result.IsExist())
                        {
                            var suppressed = TpTask.SuppressAutoRevivalClick;
                            if (!suppressed)
                            {
                                Logger.LogInformation("识别到复苏按钮（单机模板匹配），点击");
                                result.Click();
                                await Task.Delay(500, ct);
                            }
                            else
                            {
                                Logger.LogInformation("[传送中] 检测到复苏（模板匹配），抑制自动点击，由 TpTask 主动检测");
                            }

                            // 联机模式：触发"已倒下"信号，PathExecutor 在 MoveTo 主循环 / waypoint 兜底位置消费走异常流程
                            // （与色块路径 IsMultiplayerDefeated 行为完全对齐，让 _revivalRo 模板路径也写信号位）
                            // 详见 .kiro/specs/multiplayer-walk-revive-skip-segment/design.md §3.1
                            try { OnMultiplayerDefeatedDetected?.Invoke(); } catch { }

                            // 联机模式：触发复苏回调上报异常状态（回调照常触发，语义不变）
                            if (OnRevivalDetected != null)
                            {
                                try { await OnRevivalDetected(); } catch { }
                            }

                            // 抑制分支不 continue，让循环走到末尾的 loopCount++ + Task.Delay(50, ct) 自然节流。
                            if (AnomalyDetectorThrottleDecisions.ShouldContinueAfterRevivalHit(
                                    revivalHit: true,
                                    suppressAutoRevivalClick: suppressed))
                            {
                                continue;
                            }
                        }
                    }
                    // 联机模式复苏检测：色块连通性检测"已倒下"红色文字
                    // 二次确认容错：首帧命中后等待 400ms 重新捕获一帧再检测，连续两帧命中才视为真倒下，
                    // 避免战斗中红色特效/伤害数字/UI 瞬时落入检测区导致的假阳性误触发。
                    // 详见 .kiro/specs/hoeing-multiplayer-defeated-colorblock-false-trigger-recheck-fix/bugfix.md 2.1~2.3
                    if (IsMultiplayerDefeated(region))
                    {
                        await Task.Delay(MultiplayerDefeatedRecheckDelayMs, ct);
                        using var recheckRegion = CaptureToRectArea();
                        if (!AnomalyDetectorThrottleDecisions.ShouldProcessMultiplayerDefeated(
                                firstHit: true,
                                recheckHit: IsMultiplayerDefeated(recheckRegion)))
                        {
                            // 二次确认未通过：判定为瞬时误触发，安全忽略（不点击、不发信号、不触发回调）
                            Logger.LogDebug("疑似联机已倒下色块误触发，二次确认未通过，已忽略");
                        }
                        else
                        {
                            var suppressed = TpTask.SuppressAutoRevivalClick;
                            if (!suppressed)
                            {
                                //释放所有按键
                                Simulation.ReleaseAllKey();
                                Logger.LogInformation("识别到联机已倒下界面（色块检测），点击复苏按钮");
                                await Task.Delay(100, ct);
                                recheckRegion.ClickTo(960, 1020);
                                recheckRegion.ClickTo(960, 1020);
                                await Task.Delay(100, ct);
                                Simulation.ReleaseAllKey();
                                recheckRegion.ClickTo(960, 1020);
                                recheckRegion.ClickTo(960, 1020);
                                await Task.Delay(100, ct);
                                Simulation.ReleaseAllKey();
                                recheckRegion.ClickTo(960, 1020);
                                recheckRegion.ClickTo(960, 1020);
                                await Task.Delay(300, ct);
                            }
                            else
                            {
                                Logger.LogInformation("[传送中] 检测到复苏（色块检测），抑制自动点击，由 TpTask 主动检测");
                            }

                            // 联机模式：触发"已倒下"信号，PathExecutor 在主循环将抛 RetryException 走异常流程
                            // （信号位写入路径不变，preservation §3.3 / §3.4）
                            try { OnMultiplayerDefeatedDetected?.Invoke(); } catch { }

                            // 兼容：仍保留通用复苏回调（当前为空操作，留作未来扩展）
                            if (OnRevivalDetected != null)
                            {
                                try { await OnRevivalDetected(); } catch { }
                            }

                            // 抑制分支不 continue，让循环走到末尾的 loopCount++ + Task.Delay(50, ct)，
                            // 靠 loopCount % 5 == 0 自然节流到 250 ms（OQ2 = B）。
                            // 非抑制分支保留 continue：原"立即点击 + 800ms 延迟"语义不变（preservation §3.1）。
                            if (AnomalyDetectorThrottleDecisions.ShouldContinueAfterRevivalHit(
                                    revivalHit: true,
                                    suppressAutoRevivalClick: suppressed))
                            {
                                continue;
                            }
                        }
                    }
                }

                // 每约5000ms检测烹饪界面（100次循环 × 50ms）
                if (loopCount % 100 == 0 && _cookingRo != null)
                {
                    using var region = CaptureToRectArea();
                    using var result = region.Find(_cookingRo);
                    if (result.IsExist())
                    {
                        Logger.LogInformation("检测到烹饪界面，尝试脱离");
                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                        await Task.Delay(500, ct);
                        continue;
                    }
                }

                loopCount++;
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.LogDebug("异常检测循环异常: {Msg}", ex.Message);
                await Task.Delay(50, ct);
            }
        }
    }

    /// <summary>
    /// 联机模式"已倒下"检测：在指定区域检测红色文字的色块连通性
    /// 区域 (910, 860, 100, 30) 对应1080p下"已倒下"文字位置
    /// 红色文字 RGB 约 (255, 92, 92)，用阈值过滤后检测连通区域
    /// </summary>
    private static bool IsMultiplayerDefeated(ImageRegion region)
    {
        using var regionMat = region.DeriveCrop(910, 860, 100, 30);
        // BGR 顺序：B=60-130, G=60-130, R=200-255（红色文字）
        using var mask = OpenCvCommonHelper.Threshold(regionMat.SrcMat,
            new Scalar(255, 92, 92));
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

        // "已倒下"3个字应该有多个红色连通区域
        return numLabels > 3;
    }
}