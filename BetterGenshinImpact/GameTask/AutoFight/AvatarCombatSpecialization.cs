using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight;

// =====================================================================
// 角色战斗特化域（B12）：玛薇卡/恰斯卡/桑多涅/阿蕾奇诺 等角色的战斗特化检测与动作逻辑，
// 按角色分 #region 收拢（新增角色特化只改本文件）。
// 独立理由（R4）：消费方 Model/Avatar.cs 与 AutoFightTask.cs 均为与公版同名的高冲突文件
// （特化逻辑原本内联其中）——收拢进本零冲突新文件后，Avatar.cs 只留薄转发/一行调用。
//
// 公版可直接编译验收（R4 复制性门）的已知例外（PR 分歧点，账目见 §H B12）：
// PathExecutor.IsMavuikaOnMotorcycleByTemplate 为茶包新增（A10 赶路域），玛薇卡战斗检测
// 依赖它 → PR 时随 A10 同批或一并提供该成员；本域其余依赖
// （TaskControl/Avatar/Simulation/GIActions）均为公版已有成员。
// =====================================================================

/// <summary>
/// 角色战斗特化服务（B12）。按角色 #region 收拢特化检测与动作逻辑。
/// </summary>
public static class AvatarCombatSpecialization
{
    // 引用别名（E2 同款手法）：静态类不能作 GetLogger<T> 类型参数，复用 TaskControl.Logger
    private static ILogger Logger => TaskControl.Logger;

    #region 玛薇卡（战斗域摩托状态检测）

    /// <summary>
    /// 玛薇卡摩托状态判定的色差阈值。色差小于该值判为摩托状态，大于等于判为非摩托状态。
    /// 数值来自对大量截图的观察：摩托状态下色差一般在 10-15 之间，非摩托状态一般在 20 以上。
    /// </summary>
    private const double MotorcycleColorDifferenceThreshold = 15;

    /// <summary>玛薇卡摩托状态两个采样点所在行（Sample_Point_A 与 B 同一行）。</summary>
    private const int MotorcycleSampleRow = 991;

    /// <summary>Sample_Point_A 所在列。</summary>
    private const int MotorcycleSampleColA = 1678;

    /// <summary>Sample_Point_B 所在列。</summary>
    private const int MotorcycleSampleColB = 1728;

    /// <summary>玛薇卡摩托状态检测锁，防止多次并发执行导致异常（自 AutoFightTask 收拢）。</summary>
    private static readonly object _mavuikaMotorcycleCheckLock = new();

    /// <summary>
    /// 计算两个 BGR 像素颜色的欧氏色差：sqrt((b1-b2)^2 + (g1-g2)^2 + (r1-r2)^2)。
    /// 纯函数，无副作用。Vec3b 三分量依次为 蓝(Item0)/绿(Item1)/红(Item2)。
    /// </summary>
    public static double MotorcycleColorDifference(Vec3b a, Vec3b b)
    {
        return Math.Sqrt(
            Math.Pow(a.Item0 - b.Item0, 2) + // 蓝通道差值的平方
            Math.Pow(a.Item1 - b.Item1, 2) + // 绿通道差值的平方
            Math.Pow(a.Item2 - b.Item2, 2)   // 红通道差值的平方
        );
    }

    /// <summary>
    /// 给定两个 BGR 像素颜色，判定是否处于摩托状态：色差 &lt; 阈值 即为摩托状态。
    /// 纯函数，无副作用、无屏幕采样、无日志。供属性测试直接调用。
    /// </summary>
    public static bool IsMotorcycleColor(Vec3b a, Vec3b b)
    {
        return MotorcycleColorDifference(a, b) < MotorcycleColorDifferenceThreshold;
    }

    /// <summary>
    /// 判断指定角色是否处于玛薇卡摩托状态（自 Avatar.IsMotorcycle 收拢，实例方法改静态、名字入参）。
    /// 非玛薇卡角色记录一条 LogDebug 诊断日志并返回 false（不截图）。
    /// 玛薇卡角色截取当前画面，读取两个采样点 BGR 颜色，按色差阈值判定。
    /// 当前全仓零调用（色差版已被 PathExecutor.IsMavuikaOnMotorcycleByTemplate 模板版取代），
    /// 作为角色特化域资产保留，供后续接线/属性测试。
    /// </summary>
    public static bool IsMotorcycle(string avatarName)
    {
        if (avatarName != "玛薇卡")
        {
            Logger.LogDebug("对非玛薇卡角色 {Name} 调用了 IsMotorcycle()，直接返回 false（未执行屏幕采样）", avatarName);
            return false;
        }

        using var region = TaskControl.CaptureToRectArea();
        var a = region.SrcMat.At<Vec3b>(MotorcycleSampleRow, MotorcycleSampleColA);
        var b = region.SrcMat.At<Vec3b>(MotorcycleSampleRow, MotorcycleSampleColB);
        return IsMotorcycleColor(a, b);
    }

    /// <summary>
    /// 玛薇卡战斗域摩托状态检测（自 AutoFightTask 战斗命令循环内联 Task.Run 块收拢）：
    /// 开启开关且当前角色为玛薇卡、动作不含"重击"时，后台检测摩托状态；
    /// 处于摩托状态则施放元素战技（E）取消摩托，等待摩托状态结束后继续执行动作。
    /// </summary>
    public static void CheckMotorcycleStateInBackground(Avatar? avatar, List<string>? commandArgs, bool enabled)
    {
        Task.Run(() =>
        {
            lock (_mavuikaMotorcycleCheckLock)
            {
                if (enabled && avatar?.Name == "玛薇卡" && commandArgs?.Contains("重击") == false)
                // 搬移适配：原 command.Args.Contains 在 Args=null 时于后台线程抛 NRE 被吞、检测静默失效；
                // 此处显式判空跳过，用户可见行为一致（该情形下均不施放 E）
                {
                    //摩托状态才执行
                    using var region = TaskControl.CaptureToRectArea();

                    if (PathExecutor.IsMavuikaOnMotorcycleByTemplate(region) && avatar.IsActive(region)) // 这个数值是通过观察大量截图得来的，摩托状态下差值一般在10-15之间，非摩托状态一般在20以上
                    {
                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                        // Logger.LogWarning("检测到玛薇卡处于摩托状态，等待摩托状态结束");
                    }
                }
            }
        });
    }

    #endregion

    #region 恰斯卡（飞行旋转搜索 + 子弹/血条/伤害数字分析子系统）

    /// <summary>
    /// 恰斯卡 e(hold) 飞行旋转搜索循环（自 Avatar.UseSkill 内联块收拢）：
    /// 点按E起飞→按住左键瞄准，循环：起飞态检测/满弹倍率/子弹模式跟踪/血条索敌/传奇血条处置/OCR寻敌/旋转搜索，
    /// 退出条件：脱离飞行/20秒超时/传奇击杀推断/连续无血条达上限。下车收尾（松键+E复位）一并收拢。
    /// </summary>
    public static void RunChascaFlightRotation(CancellationToken ct)
    {
            Logger.LogInformation("进入恰斯卡特化逻辑");
            // 恰斯卡e(hold)：先确保E抬起，再点按E起飞，然后按住左键进入瞄准蓄力
            Logger.LogInformation("恰斯卡：释放E键（确保抬起）");
            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
            Sleep(100, ct);
            Logger.LogInformation("恰斯卡：点按E键起飞");
            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
            Sleep(500, ct);
            Logger.LogInformation("恰斯卡：按住左键进入瞄准蓄力");
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);

            var dpi = TaskContext.Instance().DpiScale;

            // 目标瞄准点：对应1920×1080屏幕的(960, 500) — 左右居中
            // FindBloodBars的检测区域为1500×900左上裁切，该点在此空间内为(960, 500)
            const double targetX = 960;
            const double targetY = 500;

            // 循环检测，直到退出飞行姿态或超时（20秒）
            var startTime = DateTime.UtcNow;
            var consecutiveNoBlood = 0;
            var hadBloodBar = false;
            var hadLegendaryBar = false;
            var legendaryLostFrames = 0;
            var rotationCount = 0;
            var distinctBulletPatterns = new HashSet<string>();
            var lastBulletPatternTime = DateTime.UtcNow;
            var rotateIntervalMultiplier = 1.0;
            var ocrResetCount = 0;
            var ocrResetCap = 2;
            while (true)
            {
                Sleep(50, ct);
                var chascaX = TaskContext.Instance().Config.AutoFightConfig.ChascaXSensitivity;
                var chascaY = TaskContext.Instance().Config.AutoFightConfig.ChascaYSensitivity;
                var chascaInterval = TaskContext.Instance().Config.AutoFightConfig.ChascaLegendaryRotateInterval;
                var chascaRotateLimit = TaskContext.Instance().Config.AutoFightConfig.ChascaRotateCountLimit;
                var chascaFrameMs = TaskContext.Instance().Config.AutoFightConfig.SpecializedFrameIntervalMs;

                // 退出条件
                if (!IsFlying())
                {
                    break;
                }
                if ((DateTime.UtcNow - startTime).TotalSeconds >= 20)
                {
                    break;
                }

                // 每帧检测有效子弹数量（子弹按顺序填充，0-6）
                var (bulletStatusChars, _, _, _) = AnalyzeAllChascaBullets();
                var effectiveBulletCount = bulletStatusChars.Count(c => c != '空');
                // 子弹类型直接使用检测结果：火/水/雷/冰/空
                var compressedChars = bulletStatusChars.Skip(1).ToArray();
                var pattern = new string(compressedChars);
                Logger.LogInformation("当前子弹有效数量：{Count}，子弹类型{Pattern}", effectiveBulletCount, pattern);
                // 调试采样：每帧输出亮度花纹，不干扰原逻辑
                DebugSampleChascaBullets(bulletStatusChars);
                // 满弹（≥6）→ 进入双倍超时模式
                if (effectiveBulletCount >= 6)
                {
                    rotateIntervalMultiplier = 2.0;
                }
                distinctBulletPatterns.Add(pattern);
                // 当出现第3种不同模式时，重置计时器（说明正在稳定命中，子弹变化多样）
                if (distinctBulletPatterns.Count > 2)
                {
                    distinctBulletPatterns.Clear();
                    distinctBulletPatterns.Add(pattern);
                    lastBulletPatternTime = DateTime.UtcNow;
                    ocrResetCount = 0;
                }

                // 血条检测
                var bloodBars = FindBloodBars();
                var regularBars = bloodBars.Where(b => !IsLegendaryBar(b.y)).ToList();
                var legendaryBars = bloodBars.Where(b => IsLegendaryBar(b.y)).ToList();
                // 传奇已被击杀检测：曾出现传奇血条，现在不再存在且子弹不再变动 → 退出
                if (hadLegendaryBar && legendaryBars.Count == 0)
                {
                    legendaryLostFrames++;
                    if (legendaryLostFrames >= 2 &&
                        (DateTime.UtcNow - lastBulletPatternTime).TotalSeconds >= chascaInterval)
                    {
                        Logger.LogInformation("传奇血条消失且子弹不再变动，推测已击杀，退出循环");
                        break;
                    }
                }
                else
                {
                    legendaryLostFrames = 0;
                }
                using (var drawRegion = CaptureToRectArea())
                {
                    if (regularBars.Count > 0 && legendaryBars.Count == 0)
                    {
                        // 逻辑1：有普通血条，且无传奇血条 → 索敌
                        Logger.LogInformation("识别到{Count}个血条", regularBars.Count);
                        var nearest = regularBars
                            .OrderBy(b => Math.Pow(b.x - targetX, 2) + Math.Pow(b.y - targetY, 2))
                            .First();
                        var drawList = regularBars
                            .Select(b =>
                            {
                                var rect = new Rect(b.x, b.y, b.width, b.height);
                                if (b.x == nearest.x && b.y == nearest.y && b.width == nearest.width && b.height == nearest.height)
                                    return drawRegion.ToRectDrawable(rect, "target", new System.Drawing.Pen(System.Drawing.Color.LimeGreen, 2));
                                return drawRegion.ToRectDrawable(rect, "blood");
                            })
                            .ToList();
                        VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", drawList);
                        consecutiveNoBlood = 0;
                        hadBloodBar = true;
                        var offsetX = nearest.x - targetX;
                        var offsetY = nearest.y - targetY;
                        Simulation.SendInput.Mouse.MoveMouseBy(
                            (int)(offsetX * dpi * 0.45),
                            (int)(offsetY * dpi * 0.80));
                        Sleep(4 * chascaFrameMs, ct);
                    }
                    else if (legendaryBars.Count > 0)
                    {
                        // 逻辑3：有传奇血条 → 根据子弹填充状态判断
                        var drawList = legendaryBars
                            .Select(lb => drawRegion.ToRectDrawable(new Rect(lb.x, lb.y, lb.width, lb.height), "legendary", new System.Drawing.Pen(System.Drawing.Color.Orange, 2)))
                            .ToList();
                        VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", drawList);
                        consecutiveNoBlood = 0;
                        hadBloodBar = true;
                        hadLegendaryBar = true;

                        if ((DateTime.UtcNow - lastBulletPatternTime).TotalSeconds >= chascaInterval * rotateIntervalMultiplier)
                        {
                            Logger.LogInformation("传奇：子弹模式在{Count}种状态间变化超过{Interval}s，旋转搜索（倍率={Mult}）", distinctBulletPatterns.Count, chascaInterval * rotateIntervalMultiplier, rotateIntervalMultiplier);
                            Simulation.SendInput.Mouse.MoveMouseBy(
                                (int)(500 * dpi * chascaX),
                                (int)(50 * 0.23 * 8 * dpi * chascaY));
                            Sleep(6 * chascaFrameMs, ct);
                            distinctBulletPatterns.Clear();
                            lastBulletPatternTime = DateTime.UtcNow;
                            rotateIntervalMultiplier = 1.0;
                            ocrResetCount = 0;
                        }
                        else
                        {
                            // 子弹变动中且有传奇血条，尝试OCR寻敌
                            if (OcrSeekEnemy(dpi, chascaFrameMs, ct))
                            {
                                // OCR命中 → 重置旋转超时（认为还在打）
                                if (ocrResetCount < ocrResetCap)
                                {
                                    lastBulletPatternTime = DateTime.UtcNow;
                                    ocrResetCount++;
                                }
                            }
                            else
                            {
                                Sleep(chascaFrameMs, ct);
                            }
                        }
                    }
                    else
                    {
                        // 逻辑2：无任何血条 → 旋转
                        VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", null);
                        if (hadBloodBar)
                        {
                            // 刚从有血条切换到无血条，容错一帧不旋转
                            hadBloodBar = false;
                            Logger.LogInformation("血条丢失，容错一帧");
                            Sleep(2 * chascaFrameMs, ct);
                        }
                        else
                        {
                            consecutiveNoBlood++;
                            rotationCount++;
                            Logger.LogInformation("无血条，进行旋转（第{Count}次）", consecutiveNoBlood);
                            if (consecutiveNoBlood >= chascaRotateLimit)
                            {
                                break;
                            }
                            Simulation.SendInput.Mouse.MoveMouseBy(
                                (int)(500 * dpi * chascaX),
                                rotationCount % 5 == 0 ? (int)(50 * 0.23 * 4 * dpi * chascaY) : 0);
                            Sleep(6 * chascaFrameMs, ct);
                        }
                    }
                }
            }

            // 下车：松左键 → 200ms → 松所有键 → 100ms → 点按E → 100ms → 松所有键
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
            Sleep(500, ct);
            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
            Sleep(100, ct);
            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
            Sleep(100, ct);
            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
            Logger.LogInformation("恰斯卡特化逻辑结束");
    }

    /// <summary>
    /// 传奇血条动态追踪字典：2px粒度的 y → 连续出现计数。
    /// y 96-200 范围的血条在连续5帧中出现时被标记为传奇。
    /// </summary>
    private static readonly Dictionary<int, int> _legendaryBarTracker = new();
    private const int LegendaryBarTrackThreshold = 5;

    /// <summary>
    /// 更新传奇血条动态追踪状态。
    /// 对 y 96-200 的血条进行帧间连续性追踪，连续出现达到阈值后标记为传奇。
    /// 允许1帧容错：某帧未出现时计数递减而非直接清零。
    /// </summary>
    private static void UpdateLegendaryBarTracker(IEnumerable<int> barYs)
    {
        var currentBins = barYs.Where(y => y >= 96 && y < 200)
                               .Select(y => y / 2 * 2)
                               .ToHashSet();

        // 存在的 y：递增（上限为阈值）
        foreach (var bin in currentBins)
        {
            if (_legendaryBarTracker.TryGetValue(bin, out var cnt))
                _legendaryBarTracker[bin] = Math.Min(cnt + 1, LegendaryBarTrackThreshold);
            else
                _legendaryBarTracker[bin] = 1;
        }

        // 不存在的 y：递减（1帧容错），归零则移除
        foreach (var bin in _legendaryBarTracker.Keys.ToArray())
        {
            if (!currentBins.Contains(bin))
            {
                _legendaryBarTracker[bin]--;
                if (_legendaryBarTracker[bin] <= 0)
                    _legendaryBarTracker.Remove(bin);
            }
        }
    }

    /// <summary>
    /// 判断指定 y 坐标的血条是否为传奇血条。
    /// y < 96 直接判定为传奇；y 96-200 使用动态追踪结果；y >= 200 视为普通。
    /// </summary>
    private static bool IsLegendaryBar(int y)
    {
        if (y < 96) return true;
        if (y >= 200) return false;
        return _legendaryBarTracker.TryGetValue(y / 2 * 2, out var cnt) && cnt >= LegendaryBarTrackThreshold;
    }

    public static List<(int x, int y, int width, int height)> FindBloodBars(ImageRegion? existingCapture = null)
    {
        var results = new List<(int x, int y, int width, int height)>();

        using var image = CaptureToRectArea();
        var bloodLower = new Scalar(255, 90, 90); // BGR 红色

        using Mat mask = OpenCvCommonHelper.Threshold(
            image.DeriveCrop(0, 0, 1500, 900).SrcMat, bloodLower);

        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();

        int numLabels = Cv2.ConnectedComponentsWithStats(
            mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

        for (int i = 1; i < numLabels; i++)
        {
            using Mat row = stats.Row(i);
            if (row.GetArray(out int[] arr))
            {
                int x = arr[0], y = arr[1], width = arr[2], height = arr[3];
                if (y < 50)
                    continue;
                results.Add((x, y, width, height));
            }
        }

        // 自动更新传奇血条动态追踪
        UpdateLegendaryBarTracker(results.Select(r => r.y));

        return results;
    }

    /// <summary>
    /// OCR寻敌 — 仅传奇模式使用。
    /// 在 450,240-1600,900 区域进行OCR识别，按文字区域大小加权计算目标中心，移动视角朝向该坐标。
    /// 力度为普通血条对准的 1/3 (0.45→0.15, 0.80→0.27)。
    /// </summary>
    public static bool OcrSeekEnemy(double dpi, int frameMs, CancellationToken ct, double targetX = 960, double targetY = 500)
    {
        using var ra = CaptureToRectArea();
        var ocrResults = ra.FindMulti(RecognitionObject.Ocr(450, 240, 1150, 660));

        if (ocrResults == null || ocrResults.Count == 0)
            return false;

        var validResults = ocrResults.Where(r => !string.IsNullOrWhiteSpace(r.Text) && !r.Text.StartsWith("+")).ToList();
        if (validResults.Count == 0)
            return false;

        // 按面积加权计算中心
        double totalWeight = 0, wx = 0, wy = 0;
        foreach (var r in validResults)
        {
            double w = r.Width * r.Height;
            wx += (r.X + r.Width / 2.0) * w;
            wy += (r.Y + r.Height / 2.0) * w;
            totalWeight += w;
        }

        double cx = wx / totalWeight;
        double cy = wy / totalWeight;

        var dx = cx - targetX;
        var dy = cy - targetY;

        Simulation.SendInput.Mouse.MoveMouseBy(
            (int)(dx * dpi * 0.15),
            (int)(dy * dpi * 0.27));

        Logger.LogInformation("OCR寻敌：加权中心({Cx:F0},{Cy:F0}) 偏移({Dx:F0},{Dy:F0}) 共{Count}个文本",
            cx, cy, dx, dy, validResults.Count);

        Sleep(frameMs, ct);
        return true;
    }

    /// <summary>
    /// 使用元素视野方法定位传奇（首领）敌人的身体中心位置。
    /// 返回是否存在传奇敌人及其中心坐标（检测空间 1500×900 内）。
    /// 基于传奇血条位置估计身体位置，待后续补充实际视觉检测逻辑。
    /// </summary>
    public static (bool exists, int centerX, int centerY) FindLegendaryBoss()
    {
        // 激活元素视野，等待1秒后截图检测
        Simulation.SendInput.Mouse.MiddleButtonDown();
        Sleep(1000);
        try
        {
            var bloodBars = FindBloodBars();
            var legendaryBars = bloodBars.Where(b => IsLegendaryBar(b.y)).ToList();
            if (legendaryBars.Count == 0)
                return (false, 0, 0);

            // 取所有传奇血条中面积最大的作为目标
            var best = legendaryBars.OrderByDescending(b => b.width * b.height).First();
            var bloodCenterX = best.x + best.width / 2;
            var bloodCenterY = best.y + best.height / 2;
            // 身体中心Y ≈ 血条中心Y + 350px（从屏幕顶部血条到身体中心的偏移）
            return (true, bloodCenterX, bloodCenterY + 350);
        }
        finally
        {
            Simulation.SendInput.Mouse.MiddleButtonUp();
        }
    }

    /// <summary>
    /// 检测恰斯卡/流浪者等飞行角色是否处于飞行姿态。
    /// 原理：读取 (1584, 1028) 位置的像素，若为白色（RGB >= 250），
    /// 说明空格键图标处于活跃状态（飞行中）。
    /// 提取自 SpaceAtSecondPlaceExist（SkillBoostHelper）。
    /// </summary>
    public static bool IsFlying()
    {
        using var region = CaptureToRectArea();
        var pixel = region.SrcMat.At<Vec3b>(1028, 1584);
        return pixel.Item0 >= 250 && pixel.Item1 >= 250 && pixel.Item2 >= 250;
    }

    /// <summary>
    /// 恰斯卡子弹元素样本颜色。对每个像素找最近的参考点，距离小于阈值则归入对应分组。
    /// </summary>
    private static readonly (string name, string[] hexColors)[] ChascaBulletSamples =
    [
        ("风", ["#8CFFFF", "#94D0D2", "#4C8EA3", "#85FFFF", "#9BD3D5"]),
        ("水", ["#1D65B4", "#1D65B5", "#98FBFE", "#E2FEFE", "#89F4FD", "#56BEF6"]),
        ("冰", ["#DBFFFF", "#D9FEFF", "#63B3E4", "#FDFFFF", "#D6FCFF"]),
        ("火", ["#FFE791", "#9B5D33", "#FCD682", "#ECAD72", "#FCC074", "#D07635"]),
        ("雷", ["#74488A", "#FBE7FC", "#FFC4FF", "#A261BB", "#FBBDFF", "#9D8CA2", "#EEB0F8"]),
    ];

    /// <summary>
    /// 所有参考点（元素样本 + 纯白），静态初始化时由 hex 自动转换。
    /// </summary>
    private static readonly (string group, int r, int g, int b)[] ChascaReferencePoints;

    /// <summary>
    /// 最近邻分类的距离阈值（RGB Euclidean）。小于此值才归入对应分组，否则为"其他"。
    /// </summary>
    private const double ChascaColorThreshold = 80;

    static AvatarCombatSpecialization()
    {
        var list = new List<(string, int, int, int)>();

        // 元素样本
        foreach (var (name, hexes) in ChascaBulletSamples)
        {
            foreach (var hex in hexes)
            {
                list.Add((name,
                    Convert.ToInt32(hex.Substring(1, 2), 16),
                    Convert.ToInt32(hex.Substring(3, 2), 16),
                    Convert.ToInt32(hex.Substring(5, 2), 16)));
            }
        }

        // 纯白参考点
        list.Add(("白", 255, 255, 255));

        ChascaReferencePoints = list.ToArray();
    }

    /// <summary>
    /// 对一个 RGB 像素，通过最近邻分类归入分组。
    /// 返回 (groupName, distance) — groupName 为空串表示"其他"（距离过大）。
    /// </summary>
    private static (string name, double dist) ClassifyBulletPixel(int r, int g, int b)
    {
        var bestName = "";
        var bestDist = ChascaColorThreshold; // 超过阈值即为"其他"

        foreach (var (group, sr, sg, sb) in ChascaReferencePoints)
        {
            var dr = r - sr;
            var dg = g - sg;
            var db = b - sb;
            var dist = Math.Sqrt(dr * dr + dg * dg + db * db);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = group;
            }
        }

        return (bestName, bestDist);
    }

    /// <summary>
    /// 检测恰斯卡当前已装填的子弹数量（0~6）。
    /// </summary>
    public static int GetChascaBulletCount()
    {
        return GetChascaBulletStatus().Count(c => c != '空');
    }

    /// <summary>
    /// 调试用：检测恰斯卡6个弹匣的详细状态。
    /// 返回如 "风雷火冰冰空" 的字符串（元素名/空），始终检测全部6个位置。
    /// </summary>
    public static string DebugGetChascaBulletStatus()
    {
        return GetChascaBulletStatus();
    }

    /// <summary>
    /// 调试用：返回恰斯卡6个弹匣的检测结果摘要。
    /// 输出格式："子弹分析：空 空 火 雷 冰 水"
    /// </summary>
    public static string DebugAnalyzeChascaBullets()
    {
        var (statusChars, _, _, _) = AnalyzeAllChascaBullets();
        return $"当前子弹分析：{string.Join(" ", statusChars.Select(c => c.ToString()))}";
    }

    /// <summary>
    /// 核心检测逻辑：对6个弹匣逐像素最近邻分类，返回状态字符。
    /// </summary>
    private static string GetChascaBulletStatus()
    {
        return new string(AnalyzeAllChascaBullets().statusChars);
    }

    /// <summary>
    /// 6个子弹区域 (x, y, width, height) @ 1920x1080
    /// </summary>
    private static readonly (int x, int y, int w, int h)[] ChascaBulletRects =
    [
        (906,  144, 54, 76),
        (1016, 163, 33, 58),
        (1113, 190, 39, 49),
        (1189, 256, 50, 46),
        (1237, 339, 63, 39),
        (1266, 427, 102, 40),
    ];

    // ========== HSV 条件（数据驱动） ==========
    private static bool IsFireBright(int h, int s, int v) => h >= 8 && h <= 30 && s > 100 && v > 180;
    private static bool IsFireDark(int h, int s, int v) => h >= 5 && h <= 25 && s >= 110 && s <= 230 && v >= 100 && v <= 180;

    private static bool IsElectroBright(int h, int s, int v) => h >= 130 && h <= 152 && s > 80 && v > 180;
    private static bool IsElectroDark(int h, int s, int v) => h >= 135 && h <= 150 && s >= 90 && s <= 160 && v >= 100 && v <= 175;

    private static bool IsCryoBright(int h, int s, int v) => false; // 冰弹无明暗之分
    private static bool IsCryoDark(int h, int s, int v) => h >= 95 && h <= 110 && s > 100 && v > 220;

    private static bool IsHydroBright(int h, int s, int v) => h >= 90 && h <= 115 && s > 100 && v > 230;
    private static bool IsHydroDark(int h, int s, int v) => h >= 90 && h <= 120 && s > 160 && v >= 130 && v <= 195;

    // ===== 共识花纹模板数据 =====
    // 实际数据存储在 GameTask/AutoFight/Assets/chasca-patterns.json（JSON资源文件）
    // 由 generate_csharp_templates.py 生成
    private static readonly Lazy<(int cols, int rows, byte[] data)[][]> _ptTemplatesLazy
        = new Lazy<(int cols, int rows, byte[] data)[][]>(LoadChascaPatterns);

    private static (int cols, int rows, byte[] data)[][] _ptTemplates => _ptTemplatesLazy.Value;

    private static (int cols, int rows, byte[] data)[][] LoadChascaPatterns()
    {
        var jsonPath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "GameTask", "AutoFight", "Assets", "chasca-patterns.json");
        var json = System.IO.File.ReadAllText(jsonPath);
        var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, ChascaSlotData>>(json);

        string[] slotKeys = ["slot2", "slot3", "slot4", "slot5", "slot6"];
        string[] elemKeys = ["fire", "electro", "cryo", "hydro"];

        var result = new (int cols, int rows, byte[] data)[5][];
        for (int ti = 0; ti < 5; ti++)
        {
            var slot = dict[slotKeys[ti]];
            int cols = slot.cols;
            int rows = slot.rows;
            var arr = new (int cols, int rows, byte[] data)[4];
            for (int e = 0; e < 4; e++)
            {
                var list = e switch
                {
                    0 => slot.fire,
                    1 => slot.electro,
                    2 => slot.cryo,
                    3 => slot.hydro,
                    _ => null
                };
                if (list == null || list.Count == 0)
                {
                    arr[e] = (0, 0, Array.Empty<byte>());
                    continue;
                }
                var bytes = list.Select(v => (byte)v).ToArray();
                arr[e] = (cols, rows, bytes);
            }
            result[ti] = arr;
        }
        return result;
    }

    private class ChascaSlotData
    {
        public int cols { get; set; }
        public int rows { get; set; }
        public List<int> fire { get; set; }
        public List<int> electro { get; set; }
        public List<int> cryo { get; set; }
        public List<int> hydro { get; set; }
    }

    /// <summary>
    /// 基于共识花纹模板的 Jaccard 匹配检测。
    /// 对每个槽位划分网格，逐格采样 HSV 后与内置花纹模板计算 Jaccard 相似度，
    /// 取最高分元素，低于阈值则标空。
    /// 仅第1槽（第1发）固定为风弹标空；第2-6槽参与检测。
    /// </summary>
    private static (char[] statusChars, Dictionary<string, int>[] perRegionCounts, int[] otherCounts, int[] totals) AnalyzeAllChascaBullets()
    {
        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
        using var image = CaptureToRectArea();

        var statusChars = new char[ChascaBulletRects.Length];
        var allCounts = new Dictionary<string, int>[ChascaBulletRects.Length];
        var otherCounts = new int[ChascaBulletRects.Length];
        var totals = new int[ChascaBulletRects.Length];
        string[] elemNames = ["火", "雷", "冰", "水"];

        // 6个槽位依次处理
        for (int i = 0; i < ChascaBulletRects.Length; i++)
        {
            // 仅第1槽（第1发）固定为风弹，直接标空
            if (i == 0)
            {
                statusChars[i] = '空';
                allCounts[i] = new Dictionary<string, int>();
                otherCounts[i] = 0;
                totals[i] = 0;
                continue;
            }

            var r = ChascaBulletRects[i];
            var scaledRect = new Rect(
                (int)(r.x * assetScale),
                (int)(r.y * assetScale),
                (int)(r.w * assetScale),
                (int)(r.h * assetScale));

            using var region = image.DeriveCrop(scaledRect).SrcMat;
            using var hsv = new Mat();
            Cv2.CvtColor(region, hsv, ColorConversionCodes.BGR2HSV);

            int totalPixels = hsv.Width * hsv.Height;
            totals[i] = totalPixels;

            // 获取当前槽位的模板索引（i=1→slot2, i=2→slot3, i=3→slot4, i=4→slot5, i=5→slot6）
            int ti = i - 1;
            bool hasTemplate = ti >= 0 && _ptTemplates.Length > ti;

            // 每个元素的 Jaccard 相似度 + 检测cell数
            double[] jaccardScores = new double[4];
            int[] detectedCells = new int[4];

            if (hasTemplate)
            {
                for (int e = 0; e < 4; e++)
                {
                    int cols = _ptTemplates[ti][e].cols;
                    int rows = _ptTemplates[ti][e].rows;
                    if (cols <= 0 || rows <= 0) continue;

                    byte[] template = _ptTemplates[ti][e].data;
                    double cellW = (double)hsv.Width / cols;
                    double cellH = (double)hsv.Height / rows;

                    int match = 0;      // detected ∩ template
                    int detected = 0;   // detected
                    int templateActive = 0; // template

                    for (int rIdx = 0; rIdx < rows; rIdx++)
                    {
                        for (int cIdx = 0; cIdx < cols; cIdx++)
                        {
                            // 采样cell中心像素
                            int px = (int)(cIdx * cellW + cellW / 2);
                            int py = (int)(rIdx * cellH + cellH / 2);
                            px = Math.Min(px, hsv.Width - 1);
                            py = Math.Min(py, hsv.Height - 1);

                            var pixel = hsv.At<Vec3b>(py, px);
                            int h = pixel.Item0, s = pixel.Item1, v = pixel.Item2;

                            bool activated = e switch
                            {
                                0 => IsFireDark(h, s, v) || IsFireBright(h, s, v),
                                1 => IsElectroDark(h, s, v) || IsElectroBright(h, s, v),
                                2 => IsCryoDark(h, s, v),
                                3 => IsHydroDark(h, s, v) || IsHydroBright(h, s, v),
                                _ => false
                            };

                            bool isTpl = template[rIdx * cols + cIdx] == 1;
                            if (isTpl) templateActive++;

                            if (activated)
                            {
                                detected++;
                                if (isTpl) match++;
                            }
                        }
                    }

                    detectedCells[e] = detected;
                    int union = detected + templateActive - match;
                    jaccardScores[e] = union > 0 ? (double)match / union : 0;

                    // 覆盖率辅助：检测到的cell数占总cell比例
                    double coverage = (double)detected / (cols * rows);

                    // 对Jaccard施加覆盖率影响：低覆盖率时降分
                    // 使用 sqrt(coverage) 作为乘数，使得 coverage=0.25 时 ×0.5, coverage=1.0 时 ×1.0
                    jaccardScores[e] *= Math.Sqrt(coverage);
                }
            }

            // 找最高Jaccard分数的元素
            int bestElem = -1;
            double bestScore = 0;
            for (int e = 0; e < 4; e++)
            {
                if (jaccardScores[e] > bestScore)
                {
                    bestScore = jaccardScores[e];
                    bestElem = e;
                }
            }

            // 空判定
            var counts = new Dictionary<string, int>();
            allCounts[i] = counts;

            if (bestElem < 0 || bestScore < 0.15)
            {
                statusChars[i] = '空';
                otherCounts[i] = totalPixels;
            }
            else
            {
                char elem = elemNames[bestElem][0];
                statusChars[i] = elem;
                counts[elem.ToString()] = detectedCells[bestElem];
                otherCounts[i] = totalPixels - detectedCells[bestElem];
            }
        }

        return (statusChars, allCounts, otherCounts, totals);
    }
    
    /// <summary>
    /// OCR 寻找伤害数字/反应文字作为追踪目标（备用寻敌）。
    /// 在 450,240-1600,900 区域 OCR，过滤条件：
    ///   - 有效项1：排除首位 '+'，去除非数字后纯数字 ≥4 位
    ///   - 有效项2：文本包含反应关键词（免疫/蒸发/感电/结晶/扩散/绽放/冻结/超载/融化/燃烧/超导/激化），跳过数字过滤
    /// 按 h²×文本字数 加权得到中心坐标，返回离加权中心最近的有效项。
    /// </summary>
    public static (int centerX, int centerY, string text)? FindDamageNumber(ImageRegion? existingCapture = null)
    {
        using var ra = existingCapture ?? CaptureToRectArea();
        var ocrResults = ra.FindMulti(RecognitionObject.Ocr(450, 240, 1150, 660));

        string[] reactionKeywords = ["免疫", "蒸发", "感电", "结晶", "扩散", "绽放", "冻结", "超载", "融化", "燃烧", "超导", "激化"];
        var validItems = new List<(int cx, int cy, int area, string text)>();

        foreach (var r in ocrResults)
        {
            var text = r.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            // 有效项2：反应关键词（跳过所有过滤）
            if (reactionKeywords.Any(k => text.Contains(k)))
            {
                validItems.Add((r.X + r.Width / 2, r.Y + r.Height / 2, r.Height * r.Height * text.Length, text));
                continue;
            }

            // 有效项1：排除 '+' 开头
            if (text[0] == '+') continue;

            // 去除非数字，纯数字 ≥4 位
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (digits.Length >= 4)
            {
                validItems.Add((r.X + r.Width / 2, r.Y + r.Height / 2, r.Height * r.Height * text.Length, text));
            }
        }

        if (validItems.Count == 0) return null;

        int totalArea = validItems.Sum(i => i.area);
        if (totalArea == 0) return null;

        double avgX = (double)validItems.Sum(i => i.cx * i.area) / totalArea;
        double avgY = (double)validItems.Sum(i => i.cy * i.area) / totalArea;

        var closest = validItems.OrderBy(i => Math.Abs(i.cx - avgX) + Math.Abs(i.cy - avgY)).First();
        return (closest.cx, closest.cy, closest.text);
    }

    /// <summary>
    /// 调试用：采样6个子弹槽位的 HSV 并写入文件。
    /// 每个采样点输出 H/S/V 值，附带每槽平均值。
    /// 写入到解决方案根目录的 chasca-bullet-samples.txt（追加模式）。
    /// 每次测试后请重命名该文件以标注对应元素类型，然后清空或删除以便下次采集。
    /// </summary>
    public static void DebugSampleChascaBullets(char[] label)
    {
        // 定位到解决方案根目录（向上查找 .sln 文件）
        var solutionDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(solutionDir);
        while (dir != null && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        var outputDir = dir?.FullName ?? solutionDir;
        var filePath = System.IO.Path.Combine(outputDir, "chasca-bullet-samples.txt");

        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
        using var image = CaptureToRectArea();

        using var writer = new StreamWriter(filePath, append: true);

        var frameTime = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        writer.WriteLine($"=== Frame {frameTime} ===");

        for (int si = 0; si < ChascaBulletRects.Length; si++)
        {
            var (rx, ry, rw, rh) = ChascaBulletRects[si];
            var scaledRect = new OpenCvSharp.Rect(
                (int)(rx * assetScale),
                (int)(ry * assetScale),
                (int)(rw * assetScale),
                (int)(rh * assetScale));

            using var region = image.DeriveCrop(scaledRect).SrcMat;
            using var hsv = new Mat();
            Cv2.CvtColor(region, hsv, ColorConversionCodes.BGR2HSV);

            // 自适应步长，目标输出约 12x10 个采样点
            var stepX = Math.Max(1, region.Width / 12);
            var stepY = Math.Max(1, region.Height / 10);

            var labelChar = si < label.Length ? label[si] : '?';
            writer.WriteLine($"Slot{si} label={labelChar} ({rw}x{rh}) step={stepX},{stepY}:");

            double sumH = 0, sumS = 0, sumV = 0;
            int count = 0;

            for (int y = 0; y < hsv.Height; y += stepY)
            {
                for (int x = 0; x < hsv.Width; x += stepX)
                {
                    var pixel = hsv.At<Vec3b>(y, x);
                    int h = pixel.Item0, s = pixel.Item1, v = pixel.Item2;
                    writer.Write($"{h,3}/{s,3}/{v,3} ");
                    sumH += h; sumS += s; sumV += v;
                    count++;
                }
                writer.Write('\n');
            }

            if (count > 0)
            {
                writer.WriteLine($"  => avg H={sumH / count:F0} S={sumS / count:F0} V={sumV / count:F0}  n={count}");
            }
        }

        writer.WriteLine(); // 空行分隔帧
    }

    #endregion

    #region 桑多涅（重击特化）

    /// <summary>
    /// 桑多涅重击特化瞄准循环（自 Avatar.Charge 内联块收拢）：按下重击进入蓄力后，
    /// 固定帧间隔循环：血条检测→最近目标平滑跟枪/无血条时 OCR 伤害数字追踪/再退化为旋转搜索；
    /// try/finally 保证异常时清理绘制并松开重击键。超1.5秒无目标提前退出。
    /// </summary>
    public static void ChargeSandrone(CancellationToken ct, int ms)
    {
    // 桑多涅重击特化逻辑（简化版）
    // 按下重击后，以固定帧间隔循环执行以下操作：
    // 1. 截取屏幕检测血条（红色连通域）
    // 2. 若有血条存在，取离预瞄点(960, 480)最近的血条，移动鼠标对准
    // 3. 若无血条，且不存在传奇血条（y 50~96）时，向右旋转搜索敌人
    // 4. 绘制预瞄准星和血条框用于调试
    var dpi = TaskContext.Instance().DpiScale;
    const int preAimX = 960;   // 预瞄准星 X 坐标（屏幕中心）
    const int preAimY = 480;   // 预瞄准星 Y 坐标（屏幕垂直中心偏上）
    const int frameIntervalMs = 50;  // 每帧间隔（与主循环帧率对齐）

    // 按下重击键，进入蓄力状态
    Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);

    // 连续未找到血条的计时，超过1秒时提前退出
    var lastSeenTargetTime = DateTime.UtcNow;
    var startTime = DateTime.UtcNow;
    var maxDurationMs = ms;

    // 主循环：持续到取消或重击时间耗尽
    // 使用 try/finally 确保异常时也会清理绘制并松开按键
    try
    {
        while (!ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalMilliseconds < maxDurationMs)
        {
            // 每帧截图一次，复用给 FindBloodBars 和绘制覆盖层
            using (var capture = CaptureToRectArea())
            {
                // 检测屏幕中的血条（红色连通域），结果坐标在 1500×900 裁剪空间内
                var bars = FindBloodBars(capture);
                // 更新动态传奇血条追踪
                UpdateLegendaryBarTracker(bars.Select(b => b.y));
                // 过滤掉 x <= 200 的非敌人血条（如 UI 元素、角色自身元素等误检）
                var valid = bars.Where(b => b.x > 200).ToList();

                // 构建绘制列表，初始包含预瞄准星（红色方框，中心(960, 540)）
                var drawList = new System.Collections.Generic.List<View.Drawable.RectDrawable>
                {
                    capture.ToRectDrawable(new OpenCvSharp.Rect(preAimX - 25, 540 - 25, 50, 50), "preAim", new System.Drawing.Pen(System.Drawing.Color.Red, 2))
                };

                // 检测是否存在传奇血条（y < 96 直接判定，y 96-200 使用动态追踪）
                bool hasLegendaryBar = bars.Any(b => IsLegendaryBar(b.y));

                if (valid.Count > 0 && !hasLegendaryBar)
                {
                    // 有可瞄准的普通血条：更新最后见到目标的时间
                    lastSeenTargetTime = DateTime.UtcNow;

                    // 选择距离预瞄点(960, 480)最近的血条作为目标
                    var nearest = valid.OrderBy(b => Math.Abs((b.x + b.width / 2) - preAimX) + Math.Abs((b.y + b.height / 2) - preAimY)).First();
                    Logger.LogInformation("追踪血条: 裁剪坐标({X},{Y}) 大小({W}×{H})", nearest.x, nearest.y, nearest.width, nearest.height);
                    // 计算准星到目标中心的偏移量（像素）
                    var offsetX = (nearest.x + nearest.width / 2) - preAimX;
                    var offsetY = (nearest.y + nearest.height / 2) - preAimY;
                    // 以 0.35 系数移动鼠标（平滑跟踪，避免剧烈抖动）
                    Simulation.SendInput.Mouse.MoveMouseBy((int)(offsetX * 0.35 * dpi), (int)(offsetY * 0.25 * dpi));

                    // 普通血条框绘制：追踪目标用绿色框，其他血条用红色框
                    foreach (var b in valid)
                    {
                        var rect = new OpenCvSharp.Rect(b.x, b.y, b.width, b.height);
                        if (b.x == nearest.x && b.y == nearest.y && b.width == nearest.width && b.height == nearest.height)
                            drawList.Add(capture.ToRectDrawable(rect, "target", new System.Drawing.Pen(System.Drawing.Color.LimeGreen, 2)));
                        else
                            drawList.Add(capture.ToRectDrawable(rect, "blood"));
                    }
                }
                else
                {
                    // 无血条或存在传奇血条：用 OCR 找伤害数字作为追踪目标
                    var damageResult = FindDamageNumber(capture);
                    if (damageResult.HasValue)
                    {
                        var (dcx, dcy, dtext) = damageResult.Value;
                        // Logger.LogInformation("伤害数字追踪: 坐标({X},{Y}) 文本:{Text}", dcx, dcy, dtext);
                        lastSeenTargetTime = DateTime.UtcNow;
                        var offsetX = dcx - preAimX;
                        var offsetY = dcy - preAimY;
                        Simulation.SendInput.Mouse.MoveMouseBy((int)(offsetX * 0.35 * dpi), (int)(offsetY * 0.25 * dpi));
                    }

                    // OCR 无结果时：向右旋转搜索敌人
                    if (!damageResult.HasValue)
                    {
                        // 不存在传奇血条时：连续超过1秒未找到目标，提前退出
                        if (!hasLegendaryBar && (DateTime.UtcNow - lastSeenTargetTime).TotalSeconds >= 1.5)
                        {
                            Logger.LogInformation("桑多涅重击特化：超过1.5秒未找到目标，提前退出");
                            View.Drawable.VisionContext.Instance().DrawContent.PutOrRemoveRectList("SandroneBloodBars", drawList);
                            break;
                        }

                        Simulation.SendInput.Mouse.MoveMouseBy((int)(1000 * dpi), 0);
                    }
                }

                // 将本帧绘制列表提交到遮罩窗口显示
                View.Drawable.VisionContext.Instance().DrawContent.PutOrRemoveRectList("SandroneBloodBars", drawList);
            }

            // 等待一帧间隔后继续
            Sleep(frameIntervalMs);
        }
    }
    finally
    {
        // 确保清理绘制并松开重击键
        View.Drawable.VisionContext.Instance().DrawContent.RemoveRect("SandroneBloodBars");
        Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
    }
    }

    #endregion

}
