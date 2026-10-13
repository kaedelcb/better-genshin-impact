using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Mask;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>
/// 莫版拾取外层循环：截图检测 F 键 -> 内层识别 -> 交互/滚轮。
/// 独立于 AutoPickTrigger 原有 OCR 拾取逻辑，仅在 MojangMatchEnabled 时由 AutoPickTrigger 调用。
/// </summary>
public sealed class MojangPickLoop
{
    /// <summary>每个物品项间隔（1080p）</summary>
    private const int ItemStepY = 72;

    /// <summary>列表顶部 y（1080p），向上扫描的下限</summary>
    private const int ListTopY = 347;

    /// <summary>列表底部 y（1080p）</summary>
    private const int ListBottomY = 733;

    /// <summary>允许检测的区域 y 上边缘上限（733-72）</summary>
    private const int ListBottomMinusItem = ListBottomY - ItemStepY;

    /// <summary>向下最多检测的个数</summary>
    private const int MaxScanCount = 5;

    /// <summary>满背包检查间隔毫秒</summary>
    private const int BagFullCheckIntervalMs = 2500;

    /// <summary>黑名单/无结果日志节流间隔毫秒</summary>
    private const long BlockedLogIntervalMs = 1000;

    /// <summary>满背包提示文字区域（1080p 坐标）</summary>
    private const int BagFullTextX = 560;
    private const int BagFullTextY = 450;
    private const int BagFullTextW = 800;
    private const int BagFullTextH = 170;

    private readonly ILogger _logger = App.GetLogger<MojangPickLoop>();
    private MojangPickExecution? _execution;

    /// <summary>二次确认失败标记：存在时下一轮转入滚轮分支并消费。</summary>
    private bool _skipConfirm;

    /// <summary>
    /// 连续 F 可见且识别为未知的次数，达到配置稳定次数（AutoScreenshotStreak）且最近 N 次截图互相匹配度达标时触发自动截图。
    /// </summary>
    private int _unknownStreak;

    private readonly MojangPickFilter _filter = new();

    /// <summary>自动截图保存根目录（按颜色分子目录，收集后拷回源码素材目录跑 generate_templates.py）。</summary>
    private static string ScreenshotRootDir => Global.Absolute(@"User\mojang_template_captures");

    /// <summary>颜色展示顺序，与 MojangMatch 中 Refs 一致。</summary>
    private static readonly string[] ColorNames = ["灰", "绿", "蓝", "紫", "白"];

    private long _lastBagFullCheckMs;

    private long _lastBlockedLogMs;

    /// <summary>重新加载黑名单。</summary>
    public void InitFilter()
    {
        _filter.Init();
    }

    /// <summary>
    /// 同步当前配置组的拾取名单到过滤器（每帧调用，组切换时自动切换；
    /// 无配置组上下文时仅使用全局黑名单）。
    /// </summary>
    private void SyncGroupFilter()
    {
        var project = RunnerContext.Instance.CurrentScriptProject;
        var groupName = project?.GroupInfo?.Name;
        var groupConfig = project?.GroupInfo?.Config.AutoPickConfig;

        var previousGroupName = _filter.CurrentGroupName;
        _filter.SetCurrentGroup(groupName, groupConfig);

        // 配置组切换时（含进入配置组）输出一次生效的额外名单
        if (groupName is not null && previousGroupName != groupName)
        {
            LogGroupListApplied(groupName, groupConfig);
        }
    }

    /// <summary>配置组拾取名单生效时输出日志（仅在进入/切换配置组时输出一次）。</summary>
    private void LogGroupListApplied(string groupName, AutoPickGroupConfig? groupConfig)
    {
        var white = groupConfig is { WhiteList.Count: > 0 } ? string.Join("、", groupConfig.WhiteList) : "无";
        var black = groupConfig is { BlackList.Count: > 0 } ? string.Join("、", groupConfig.BlackList) : "无";
        _logger.LogInformation(
            "启动自动拾取（配置组：{Group}），额外白名单：{White}，额外黑名单：{Black}", groupName, white, black);
    }

    /// <summary>
    /// 满背包检查（节流）。检测到「背包已满」提示时，OCR 背包名并与最近 5 秒内交互过的物品匹配（取匹配度最高者），
    /// 将该物品对应的全部交互名自动加入黑名单（满背包提示显示背包名，与交互名可能不一致）。
    /// </summary>
    public void CheckBagFull(CaptureContent content)
    {
        if (Environment.TickCount64 - _lastBagFullCheckMs < BagFullCheckIntervalMs)
        {
            return;
        }

        _lastBagFullCheckMs = Environment.TickCount64;

        var region = content.CaptureRectArea;
        var itemFullRo = RecognitionAssets.Get("AutoPick", "ItemFull", region);
        using var fullRegion = region.Find(itemFullRo);
        if (fullRegion.IsEmpty())
        {
            return;
        }

        var scale = region.Width / 1920.0;
        var textRect = new Rect((int)(BagFullTextX * scale), (int)(BagFullTextY * scale), (int)(BagFullTextW * scale), (int)(BagFullTextH * scale));

        string ocrText;
        using (var textMat = new Mat(region.SrcMat, textRect))
        {
            ocrText = OcrFactory.Paddle.Ocr(textMat);
        }

        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return;
        }

        var names = MojangMatch.Instance.FindInteractNamesForBagFull(ocrText,
            AutoPickRecordStore.PeekRecent(TimeSpan.FromSeconds(5)));
        if (names.Count == 0)
        {
            return;
        }

        _filter.AddToBlackList(names);
        _logger.LogWarning("检测到背包满，自动加入黑名单：{Names}", string.Join("、", names));
    }

    /// <summary>
    /// 执行一轮外层循环。
    /// </summary>
    internal void Tick(CaptureContent content, Region foundRectArea, AutoPickAssets assets, double scale, MojangPickExecution execution)
    {
        _execution = execution;
        SyncGroupFilter();

        var testMode = TaskContext.Instance().Config.AutoPickConfig.TestModeEnabled;
        if (!testMode)
        {
            RemoveTestDraw();
        }

        if (foundRectArea.IsEmpty())
        {
            // 无 F 图标：清除二次确认失败标记，避免残留到后续轮次
            _skipConfirm = false;
            // 无 F 图标：清除连续未知计数
            _unknownStreak = 0;

            if (testMode)
            {
                RemoveTestDraw();
            }

            // 无 F 图标：存在滚轮图标则向下滚动一次，否则不操作
            if (HasScrollIcon(content.CaptureRectArea))
            {
                ScrollTimes(1);
            }

            return;
        }

        if (testMode)
        {
            DrawTest(foundRectArea, null);
        }

        // 有 F 图标：内层识别右侧物品（占位：识别到即视为需要交互，后续再细化判定）
        var result = MojangMatch.Instance.Match(content.CaptureRectArea.SrcMat, foundRectArea, scale);

        if (result is null)
        {
            // 未识别到物品：转入滚轮分支
            LogNoResult();

            // 自动截图：稳定次数配置 > 0 时，连续未知达到该次数则取最近 N 次未知识别区域截图，
            // 后台校验互相匹配度（画面稳定）后异步 OCR + 去重 + 保存，不阻塞滚轮；与测试模式相互独立。
            var streak = Math.Clamp(TaskContext.Instance().Config.AutoPickConfig.AutoScreenshotStreak, 0, 30);
            if (streak > 0)
            {
                if (++_unknownStreak >= streak)
                {
                    _unknownStreak = 0;
                    var rois = MojangMatch.Instance.TakeUnknownRois(streak);
                    if (rois.Count == streak)
                    {
                        Task.Run(() => ProcessStableCapture(rois));
                    }
                    else
                    {
                        foreach (var frame in rois)
                        {
                            frame.Dispose();
                        }
                    }
                }
            }

            HandleScroll(content, foundRectArea, scale);
            return;
        }

        var r = result.Value;
        // 识别成功：清除连续未知计数
        _unknownStreak = 0;

        if (testMode)
        {
            DrawTest(foundRectArea, r.Region);
            LogDetail(r);
        }
        else if (LogLevel >= 2)
        {
            // 调试级别：正常识别也输出完整信息（耗时/位置/匹配度）
            LogDetail(r);
        }

        if (_skipConfirm)
        {
            // 二次确认失败标记存在：消费标记并转入滚轮分支
            _skipConfirm = false;
            HandleScroll(content, foundRectArea, scale);
            return;
        }

        var config = TaskContext.Instance().Config.AutoPickConfig;
        var repeatFInterval = config.RepeatFInterval;
        var confirmInterval = config.ConfirmInterval;

        // 二次识别：由 ConfirmInterval 配置决定，与连点 F 无关
        if (confirmInterval > 0)
        {
            if (!execution.Wait(confirmInterval)) return;
            using var newCapture = TaskControl.CaptureToRectArea();
            using var newFound = newCapture.Find(assets.PickRo);
            if (newFound.IsEmpty())
            {
                return; // 二次确认时 F 键消失，本轮结束
            }

            if (!MojangMatch.Instance.ConfirmMatch(newCapture.SrcMat, newFound, scale, r.Name, r.ColorIndex))
            {
                _skipConfirm = true;
                return;
            }
        }

        if (!ShouldInteract(r.Name))
        {
            // 不可交互（黑名单命中或测试模式）：不拾取，转滚轮分支继续找下一个
            LogBlocked(r);
            HandleScroll(content, foundRectArea, scale);
            return;
        }

        if (repeatFInterval > 0)
        {
            RepeatF(r, content, foundRectArea, scale, assets, repeatFInterval);
        }
        else
        {
            Interact(r.Name, r.ItemName, assets);
        }

        execution.Wait(config.InteractionDelay);
    }

    /// <summary>
    /// 连点 F：从当前行开始（含当前）连续 n 个都需要交互时，间隔连点 n 次 F。
    /// 每次按 F 对应列表中的不同物品，日志按各自物品名输出。
    /// </summary>
    private void RepeatF(MojangMatchResult r, CaptureContent content, Region foundRectArea, double scale, AutoPickAssets assets, int repeatFInterval)
    {
        var scanCount = Math.Min(MaxScanCount, ((int)(ListBottomMinusItem * scale) - foundRectArea.Y) / Math.Max(1, (int)(ItemStepY * scale)));
        var items = new List<(string Name, string ItemName)> { (r.Name, r.ItemName) }; // 当前行
        for (var k = 1; k <= scanCount; k++)
        {
            using var region = new Region(foundRectArea.X, foundRectArea.Y + (int)(ItemStepY * scale) * k, foundRectArea.Width, foundRectArea.Height);
            var m = MojangMatch.Instance.Match(content.CaptureRectArea.SrcMat, region, scale);
            if (m is not { } mr || !ShouldInteract(mr.Name))
            {
                break; // 遇到不需要交互的行（识别不到或黑名单），停止计数
            }

            items.Add((mr.Name, mr.ItemName)); // 连续需要交互
        }

        if (items.Count >= 2 && LogLevel >= 1)
        {
            _logger.LogInformation("启用连点拾取，连点{N}次，间隔{Interval}毫秒", items.Count, repeatFInterval);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                if (_execution?.Wait(repeatFInterval) != true) return;
                using var freshCapture = TaskControl.CaptureToRectArea();
                using var freshKey = freshCapture.Find(assets.PickRo);
                if (freshKey.IsEmpty()) return;
                var fresh = MojangMatch.Instance.Match(freshCapture.SrcMat, freshKey, scale, cacheUnknownRoi: false);
                if (fresh is not { } next || next.Name != items[i].Name || !ShouldInteract(next.Name)) return;
            }
            if (!Interact(items[i].Name, items[i].ItemName, assets)) return;
        }
    }

    /// <summary>
    /// 滚轮分支：检测滚轮图标并决定滚动策略。
    /// </summary>
    private void HandleScroll(CaptureContent content, Region foundRectArea, double scale)
    {
        if (!HasScrollIcon(content.CaptureRectArea))
        {
            // 无滚轮图标：向下滚动一次
            ScrollTimes(1);
            return;
        }

        // 有滚轮图标：向下检测 k 个位置，找需要交互的物品
        if (foundRectArea.Y + (int)(ItemStepY * scale) > (int)(ListBottomMinusItem * scale))
        {
            // 已在底部（k=1 就超出范围）：滚一次，游戏机制会回到顶部
            ScrollTimes(1);
            return;
        }

        var scanCount = Math.Min(MaxScanCount, ((int)(ListBottomMinusItem * scale) - foundRectArea.Y) / Math.Max(1, (int)(ItemStepY * scale)));
        var hitIndex = -1;
        for (var k = 1; k <= scanCount; k++)
        {
            using var region = new Region(foundRectArea.X, foundRectArea.Y + (int)(ItemStepY * scale) * k, foundRectArea.Width, foundRectArea.Height);
            // 扫描行不缓存未知识别区域截图，避免覆盖 F 行缓存
            var m = MojangMatch.Instance.Match(content.CaptureRectArea.SrcMat, region, scale, cacheUnknownRoi: false);
            if (m is not { } mr)
            {
                continue;
            }

            if (TaskContext.Instance().Config.AutoPickConfig.TestModeEnabled || LogLevel >= 2)
            {
                LogDetail(mr);
            }

            if (ShouldInteract(mr.Name))
            {
                hitIndex = k;
                break;
            }
        }

        if (hitIndex > 0)
        {
            ScrollTimes(hitIndex); // 第 i 个是需要交互的物品：滚 i 次
            return;
        }

        // F 行和向下 k 个都无需交互：尝试向上寻找，存在需要交互的则向上滑动
        var upCount = ScanUpward(content, foundRectArea, scale);
        if (upCount > 0)
        {
            ScrollTimesUp(upCount);
            return;
        }

        ScrollTimes(Math.Max(1, scanCount - 1)); // 都不是：至少滚一次
    }

    /// <summary>
    /// 向上扫描：从 F 上一行开始向上（直到 y 上边缘 347），
    /// 找到第一个需要交互的行，返回需要向上滚动的次数；找不到返回 0。
    /// </summary>
    private int ScanUpward(CaptureContent content, Region foundRectArea, double scale)
    {
        var k = 1;
        while (true)
        {
            var y = foundRectArea.Y - (int)(ItemStepY * scale) * k;
            if (y < (int)(ListTopY * scale))
            {
                return 0; // 已到列表顶部，未找到
            }

            using var region = new Region(foundRectArea.X, y, foundRectArea.Width, foundRectArea.Height);
            // 扫描行不缓存未知识别区域截图，避免覆盖 F 行缓存
            var m = MojangMatch.Instance.Match(content.CaptureRectArea.SrcMat, region, scale, cacheUnknownRoi: false);
            if (m is { } mr)
            {
                if (TaskContext.Instance().Config.AutoPickConfig.TestModeEnabled || LogLevel >= 2)
                {
                    LogDetail(mr);
                }

                if (ShouldInteract(mr.Name))
                {
                    return k; // 距当前行 k 行：向上滚动 k 次
                }
            }

            k++;
        }
    }

    /// <summary>
    /// 滚动。滚动间隔为 0 时每轮最多滚动一次；否则连续滚动 times 次并间隔等待。
    /// </summary>
    private void ScrollTimes(int times)
    {
        var config = TaskContext.Instance().Config.AutoPickConfig;

        var actualTimes = config.ScrollInterval > 0 ? times : 1;
        for (var i = 0; i < actualTimes; i++)
        {
            if (_execution?.ScrollDelta(-config.ScrollDistance) != true) return;
            if (i < actualTimes - 1)
            {
                if (_execution?.Wait(config.ScrollInterval) != true) return;
            }
        }

        _execution?.Wait(config.ScrollAfterInterval);
    }

    /// <summary>
    /// 向上滚动。滚动间隔为 0 时每轮最多滚动一次；否则连续滚动 times 次并间隔等待。
    /// </summary>
    private void ScrollTimesUp(int times)
    {
        var config = TaskContext.Instance().Config.AutoPickConfig;

        var actualTimes = config.ScrollInterval > 0 ? times : 1;
        for (var i = 0; i < actualTimes; i++)
        {
            if (_execution?.ScrollDelta(config.ScrollDistance) != true) return;
            if (i < actualTimes - 1)
            {
                if (_execution?.Wait(config.ScrollInterval) != true) return;
            }
        }

        _execution?.Wait(config.ScrollAfterInterval);
    }

    /// <summary>
    /// 交互（按 F）。
    /// </summary>
    private bool Interact(string name, string itemName, AutoPickAssets assets)
    {
        if (_execution?.Press(assets.PickVk) != true) return false;
        _logger.LogInformation("交互或拾取：{Name}", name);
        // 记录拾取历史（背包名 + 交互名 + 时间戳），供 JS 侧 dispatcher.getPickRecords() 获取及满背包自动黑名单使用；
        // 背包名与交互名可能不一致（如 交互名"黄金蟹" 对应 背包名"螃蟹"），满背包提示显示的是背包名
        AutoPickRecordStore.Record(string.IsNullOrEmpty(itemName) ? name : itemName, name);
        return true;
    }

    /// <summary>
    /// 是否应交互。测试模式下所有物品均视为不可交互。
    /// </summary>
    private bool ShouldInteract(string name)
    {
        if (TaskContext.Instance().Config.AutoPickConfig.TestModeEnabled)
        {
            return false;
        }

        return _filter.ShouldPick(name);
    }

    /// <summary>
    /// 莫版拾取日志级别（0 精简 / 1 常规 / 2 调试），运行时读取即时生效。
    /// </summary>
    private static int LogLevel => TaskContext.Instance().Config.AutoPickConfig.PickLogLevel;

    /// <summary>
    /// 无有效结果日志：常规级别节流 1s，调试级别每次输出。
    /// </summary>
    private void LogNoResult()
    {
        if (LogLevel < 1)
        {
            return;
        }

        if (LogLevel >= 2 || ThrottleBlockedLog())
        {
            _logger.LogInformation("无有效结果，不交互");
        }
    }

    /// <summary>
    /// 黑名单命中日志：常规级别节流 1s，调试级别每次输出并追加识别详情。
    /// </summary>
    private void LogBlocked(MojangMatchResult r)
    {
        if (LogLevel < 1)
        {
            return;
        }

        if (LogLevel >= 2 || ThrottleBlockedLog())
        {
            _logger.LogInformation("识别到{Name}，不交互", r.Name);
            if (LogLevel >= 2)
            {
                LogDetail(r);
            }
        }
    }

    /// <summary>
    /// 黑名单/无结果日志节流：1 秒内只输出一次。
    /// </summary>
    private bool ThrottleBlockedLog()
    {
        var now = Environment.TickCount64;
        if (now - _lastBlockedLogMs < BlockedLogIntervalMs)
        {
            return false;
        }

        _lastBlockedLogMs = now;
        return true;
    }

    /// <summary>
    /// 输出识别详情（识别结果 + 位置 + 各环节耗时 + 匹配度）。测试模式或调试级别时输出。
    /// </summary>
    private void LogDetail(MojangMatchResult r)
    {
        _logger.LogInformation(
            "莫版识别详情：{Name}({ItemName}) 颜色={Color} 分数={Score:F3} 位置=({X},{Y},{W},{H}) 判定={JudgeMs:F2}ms 灰度={GrayMs:F2}ms NCC={NccMs:F2}ms 模板={TemplateCount}",
            r.Name, r.ItemName, ColorNames[r.ColorIndex], r.Score,
            r.Region.X, r.Region.Y, r.Region.Width, r.Region.Height,
            r.JudgeMs, r.GrayMs, r.NccMs, r.TemplateCount);
    }

    /// <summary>
    /// 测试模式：在遮罩窗口绘制 F 键区域（青色）与识别的物品区域（绿色）。
    /// </summary>
    private static void DrawTest(Region fRegion, OpenCvSharp.Rect? itemRect)
    {
        var board = TaskContext.Instance().Runtime?.MaskWindowDrawingBoard;
        if (board is null) return;
        board.Set("MojangTestF", fRegion.SelfToMaskWindowDrawingRect(System.Drawing.Pens.Cyan));
        if (itemRect is { } rect)
        {
            using var item = new Region(rect.X, rect.Y, rect.Width, rect.Height, fRegion.Prev);
            board.Set("MojangTestItem", item.SelfToMaskWindowDrawingRect(System.Drawing.Pens.Lime));
        }
        else board.Clear("MojangTestItem");
    }

    private static void RemoveTestDraw()
    {
        var board = TaskContext.Instance().Runtime?.MaskWindowDrawingBoard;
        board?.Clear("MojangTestF");
        board?.Clear("MojangTestItem");
    }

    internal void Reset()
    {
        _skipConfirm = false;
        _unknownStreak = 0;
        _execution = null;
        _filter.SetCurrentGroup(null, null);
        RemoveTestDraw();
        if (MojangMatch.IsLoaded) MojangMatch.Instance.ClearUnknownRois();
    }

    /// <summary>
    /// 后台处理自动截图：先校验最近 N 次截图两两互相匹配度（NCC ≥ 匹配阈值）确认画面稳定，
    /// 再对最新一张做 OCR，按颜色分目录去重并保存；结束后释放全部截图。
    /// 任一帧互匹配不达标视为画面不稳定，直接丢弃不保存。
    /// OCR 引擎内部有锁，后台调用安全；异常仅记录，不影响拾取循环。
    /// </summary>
    private void ProcessStableCapture(List<Mat> rois)
    {
        try
        {
            var threshold = TaskContext.Instance().Config.AutoPickConfig.MojangMatchThreshold;
            for (var i = 0; i < rois.Count; i++)
            {
                for (var j = i + 1; j < rois.Count; j++)
                {
                    var s = MojangMatch.Instance.GetSimilarity(rois[i], rois[j]);
                    if (s < threshold)
                    {
                        _logger.LogInformation("自动截图：最近{Count}次截图互相匹配度不足（{Score:F3} < {Threshold:F3}），画面不稳定，跳过保存",
                            rois.Count, s, threshold);
                        return;
                    }
                }
            }

            var roi = rois[^1]; // 最新一张做 OCR 与保存
            var ocrText = OcrFactory.Paddle.Ocr(roi);

            var name = SanitizeFileName(ocrText);
            if (string.IsNullOrEmpty(name))
            {
                return; // OCR 无结果或文件名非法，不保存
            }

            var colorIndex = MojangMatch.Instance.GetColorIndex(roi);
            var colorDir = Path.Combine(ScreenshotRootDir, ColorNames[colorIndex]);
            if (MojangMatch.Instance.FindDuplicate(roi, colorDir, TaskContext.Instance().Config.AutoPickConfig.MojangMatchThreshold))
            {
                _logger.LogInformation("自动截图：{Name} 与已有截图重复，跳过保存", name);
                return;
            }

            Directory.CreateDirectory(colorDir);
            var path = UniqueFilePath(colorDir, name);
            if (Cv2.ImEncode(".png", roi, out var bytes))
            {
                File.WriteAllBytes(path, bytes);
                _logger.LogInformation("自动截图已保存：{Path}", path);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "自动截图保存失败");
        }
        finally
        {
            foreach (var r in rois)
            {
                r.Dispose();
            }
        }
    }

    /// <summary>清理 OCR 结果中的文件名非法字符。</summary>
    private static string SanitizeFileName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Trim().Where(c => !invalid.Contains(c)).ToArray());
    }

    /// <summary>文件名唯一化：目录下已存在同名文件时追加 (n)。</summary>
    private static string UniqueFilePath(string dir, string name)
    {
        var path = Path.Combine(dir, name + ".png");
        var n = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name}({n}).png");
            n++;
        }

        return path;
    }

    /// <summary>
    /// 滚轮图标检测（固定区域颜色判断，与 AutoPickTrigger.HasScrollIcon 一致）。
    /// </summary>
    private static bool HasScrollIcon(ImageRegion captureRectArea)
    {
        var mat = captureRectArea.SrcMat;
        var scale = Math.Min(captureRectArea.Width / 1920d, 1d);
        var x = (int)(1062 * scale);
        var y1 = (int)(537 * scale);
        var y2 = (int)(524 * scale);
        var y3 = (int)(554 * scale);
        if (x >= mat.Width || y3 >= mat.Height) return false;
        var color1 = mat.At<Vec3b>(y1, x);
        var color2 = mat.At<Vec3b>(y2, x);
        var color3 = mat.At<Vec3b>(y3, x);
        return color1.Item2 == 255 && color1.Item1 == 233 && color1.Item0 == 44
            && color2.Item2 == 255 && color2.Item1 == 255 && color2.Item0 == 255
            && color3.Item2 == 255 && color3.Item1 == 255 && color3.Item0 == 255;
    }
}
