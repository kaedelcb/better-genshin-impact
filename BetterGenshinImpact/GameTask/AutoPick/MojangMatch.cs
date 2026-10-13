using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>
/// 莫版匹配结果
/// </summary>
public readonly record struct MojangMatchResult(
    string Name, string ItemName, double Score, Rect Region, Rect TemplateRect,
    double JudgeMs, double GrayMs, double NccMs, int TemplateCount, int ColorIndex);

/// <summary>
/// 莫版模板清单项（供黑白名单编辑界面展示）。
/// </summary>
public readonly record struct MojangTemplateInfo(string Name, string Color, int Len);

/// <summary>
/// 莫版匹配：基于文字颜色先验的特化模板匹配，替代 OCR 识别拾取物品。
/// 与原自动拾取逻辑相互独立，仅通过 <see cref="Match"/> 对外提供识别能力。
/// </summary>
public sealed class MojangMatch
{
    /// <summary>线性截断阈值：匹配度 = 255 * max(0, 1 - dE/T)</summary>
    private const double T = 80.0;

    /// <summary>参与颜色判定 / 灰度化归零的最小亮度 V：HSV 中 V 通道百分制 65（0-255 制 ≈166）</summary>
    private const int VoteMinV = 166;

    /// <summary>NCC 匹配度下限（读配置）</summary>
    private AutoPickConfig Config => _config ?? TaskContext.Instance().Config.AutoPickConfig;
    private double MinScore => double.IsFinite(Config.MojangMatchThreshold)
        ? Math.Clamp(Config.MojangMatchThreshold, 0.5, 1.0) : 0.85;

    /// <summary>满背包自动黑名单：OCR 文本与最近交互背包名匹配度的下限，低于则视为未匹配。</summary>
    private const double BagFullMinMatchRatio = 0.5;

    /// <summary>bin 文件魔数 ("MBMB")</summary>
    private const int BinMagic = 0x4D424D42;

    /// <summary>用户额外模板文件夹（相对启动目录；随程序分发 readme.md，PNG 由用户自备、不入库）</summary>
    private const string ExtraTemplateDir = @"User\mojang_templates";

    /// <summary>种子模式：化种匣下白色物品种子显示为「X」的种子，文字前固定多出「（x4~12，宽9，高23）</summary>
    private const int SeedMarkerDxMin = 4;

    /// <summary>「字形匹配的 x 扫描上限（窗口覆盖 x4~12）</summary>
    private const int SeedMarkerDxMax = 8;

    /// <summary>「字形匹配达标阈值（实测正例≥0.98，普通文字≤0.89）</summary>
    private const double SeedMarkerMinScore = 0.90;

    /// <summary>种子模式下材料名相对行首的偏移扫描起点（「宽9+间距，文字起点 x≈18，允许±1渲染波动）</summary>
    private const int SeedDxMin = 17;

    /// <summary>种子模式下材料名偏移扫描终点</summary>
    private const int SeedDxMax = 20;

    /// <summary>种子模式截断模板最大宽（120 = 140 - 右侧20容差，偏移18 时材料名最大 102）</summary>
    private const int MaxSeedWidth = 102;

    /// <summary>1080p 下文字区域宽度（模板宽，X 方向精确不滑动）</summary>
    private const int RegionWidth = 140;

    /// <summary>1080p 下文字区域高度（模板高 26 + 上下各 1px）</summary>
    private const int RegionHeight = 28;

    /// <summary>1080p 下自动截图保存区域高度（与模板素材规格 140×26 一致）</summary>
    private const int ScreenshotHeight = 26;

    /// <summary>1080p 下文字区域相对 F 键的左侧偏移（模板实际 X）</summary>
    private const int TextLeftOffset = 122;

    /// <summary>5 种参考文字颜色 (名称, R, G, B)</summary>
    private static readonly (string Name, byte R, byte G, byte B)[] Refs =
    [
        ("灰", 204, 204, 204),
        ("绿", 172, 255, 69),
        ("蓝", 79, 244, 255),
        ("紫", 249, 152, 255),
        ("白", 255, 255, 255),
    ];

    private static readonly double[][] RefLabs = Refs.Select(r => RgbToLab(r.R, r.G, r.B)).ToArray();

    /// <summary>「（左书名号）字形模板：化种匣种子文字行首标记，从 140×28 种子截图 x4~12 截取（9×23）。</summary>
    private static readonly MojangTemplate SeedMarkerTemplate = BuildSeedMarkerTemplate();

    /// <summary>颜色展示顺序，与 <see cref="Refs"/> 一致</summary>
    private static readonly string[] ColorOrder = Refs.Select(r => r.Name).ToArray();

    private static readonly Lazy<MojangMatch> LazyInstance = new(Load);

    public static MojangMatch Instance => LazyInstance.Value;

    /// <summary>
    /// 启动时预加载模板，避免首次识别时才触发加载（加载日志随启动输出）。
    /// </summary>
    public static void Preload()
    {
        _ = Instance;
    }

    /// <summary>模板是否已加载完成（供 UI 判断是否需要在后台等待加载）。</summary>
    public static bool IsLoaded => LazyInstance.IsValueCreated;

    private readonly Dictionary<(string Color, int Len), List<MojangTemplate>> _templatesByColorAndLen = [];

    private readonly Dictionary<string, MojangTemplate> _templatesByName = [];

    /// <summary>种子模式截断模板缓存：宽模板（&gt; <see cref="MaxSeedWidth"/>）首次进入种子模式时截断并缓存。</summary>
    private readonly Dictionary<MojangTemplate, MojangTemplate> _seedTruncated = [];

    private readonly ILogger _logger;
    private readonly AutoPickConfig? _config;

    private MojangMatch(AutoPickConfig? config = null, ILogger? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 获取全部模板清单（按颜色、交互名排序），供黑白名单编辑界面展示。
    /// </summary>
    public IReadOnlyList<MojangTemplateInfo> GetTemplateInfos()
    {
        return _templatesByName.Values
            .OrderBy(t => Array.IndexOf(ColorOrder, t.Color))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new MojangTemplateInfo(t.Name, t.Color, t.Len))
            .ToList();
    }

    /// <summary>
    /// 满背包自动加入黑名单：将 OCR 文本（背包名）与最近交互记录匹配，取匹配度最高者，
    /// 返回该物品对应的全部交互名（交互列表显示名，黑名单按此匹配）。
    /// 匹配度低于 <see cref="BagFullMinMatchRatio"/> 时视为未匹配，返回空。
    /// </summary>
    public IReadOnlyList<string> FindInteractNamesForBagFull(string text, IReadOnlyList<AutoPickRecordStore.PickRecord> recent)
    {
        if (string.IsNullOrWhiteSpace(text) || recent.Count == 0)
        {
            return [];
        }

        var pureText = KeepChinese(text);
        if (pureText.Length == 0)
        {
            return [];
        }

        // 满背包提示显示的是背包名，OCR 可能有噪音：在最近交互记录（背包名）中取匹配度最高者
        var bestBagName = string.Empty;
        var bestInteractName = string.Empty;
        var bestRatio = 0.0;
        foreach (var r in recent)
        {
            var bagName = KeepChinese(r.Name);
            if (bagName.Length == 0)
            {
                continue;
            }

            var ratio = MatchRatio(bagName, pureText);
            if (ratio > bestRatio + 1e-9)
            {
                bestRatio = ratio;
                bestBagName = r.Name;
                bestInteractName = r.InteractName;
            }
        }

        if (bestRatio < BagFullMinMatchRatio)
        {
            return [];
        }

        // 背包名与交互名可能不一致（如 背包名"螃蟹" 对应 交互名"黄金蟹/将军蟹/…"）：
        // 将本次交互记录的交互名与该背包名对应的全部交互名一并加入黑名单
        var names = new HashSet<string>();
        if (!string.IsNullOrEmpty(bestInteractName))
        {
            names.Add(bestInteractName);
        }

        if (!string.IsNullOrEmpty(bestBagName))
        {
            foreach (var n in FindInteractNamesByItemName(bestBagName))
            {
                names.Add(n);
            }
        }

        return names.ToList();
    }

    /// <summary>按背包名（itemName）反查全部交互名（Name）。</summary>
    public IReadOnlyList<string> FindInteractNamesByItemName(string itemName)
    {
        return _templatesByName.Values
            .Where(t => string.Equals(t.ItemName, itemName, StringComparison.Ordinal))
            .Select(t => t.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>仅保留中文字符。</summary>
    private static string KeepChinese(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        return new string(s.Where(c => c >= '\u4e00' && c <= '\u9fff').ToArray());
    }

    /// <summary>子串匹配比例：part 在 text 中滑动，取任意窗口内最长连续匹配字符数 / part 长度。</summary>
    private static double MatchRatio(string part, string text)
    {
        if (string.IsNullOrEmpty(part) || string.IsNullOrEmpty(text) || part.Length > text.Length)
        {
            return 0;
        }

        var len = part.Length;
        var maxMatch = 0;
        for (var i = 0; i <= text.Length - len; i++)
        {
            var match = 0;
            for (var j = 0; j < len; j++)
            {
                // 不连续时清零，统计的是"最长连续匹配段"，而非跨窗口累计
                match = text[i + j] == part[j] ? match + 1 : 0;
                if (match > maxMatch)
                {
                    maxMatch = match;
                }
            }
        }

        return (double)maxMatch / len;
    }

    /// <summary>
    /// 计算 F 键右侧文字识别区域（1080p 坐标按 scale 缩放，供识别与自动截图裁剪共用）。
    /// </summary>
    public Rect GetTextRegion(Region fKeyRegion, double scale)
    {
        var centerY = fKeyRegion.Y + fKeyRegion.Height / 2;
        return new Rect(
            fKeyRegion.X + (int)(TextLeftOffset * scale),
            centerY - (int)(RegionHeight / 2.0 * scale),
            (int)(RegionWidth * scale),
            (int)(RegionHeight * scale));
    }

    /// <summary>
    /// 最近未知识别区域截图队列（BGR 副本 140×26，最新在队尾），供自动截图复用；
    /// 容量随配置稳定次数变化，最多保留最近 N 张。取走即清空。
    /// </summary>
    private readonly Queue<Mat> _unknownRois = [];

    /// <summary>
    /// 计算自动截图保存区域（140×26，与模板素材规格一致；为识别区域上下各内缩 1px）。
    /// </summary>
    private static Rect GetScreenshotRegion(Region fKeyRegion, double scale)
    {
        var centerY = fKeyRegion.Y + fKeyRegion.Height / 2;
        return new Rect(
            fKeyRegion.X + (int)(TextLeftOffset * scale),
            centerY - (int)(ScreenshotHeight / 2.0 * scale),
            (int)(RegionWidth * scale),
            (int)(ScreenshotHeight * scale));
    }

    /// <summary>
    /// 裁剪自动截图保存区域（BGR 副本 140×26）；区域越界返回 null。
    /// </summary>
    private Mat? GetScreenshotRoi(Mat srcMat, Region fKeyRegion, double scale)
    {
        var rect = GetScreenshotRegion(fKeyRegion, scale);
        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > srcMat.Width || rect.Y + rect.Height > srcMat.Height)
        {
            return null;
        }

        using var roi = new Mat(srcMat, rect);
        using var bgr = roi.Channels() == 4 ? roi.CvtColor(ColorConversionCodes.BGRA2BGR) : roi.Clone();
        return ResizeHelper.ResizeTo(bgr, RegionWidth, ScreenshotHeight);
    }

    /// <summary>
    /// 取走最近 count 张未知识别区域截图（最旧在前、最新在最后，调用方负责释放）；不足 count 张时返回实际数量。
    /// </summary>
    public List<Mat> TakeUnknownRois(int count)
    {
        var rois = new List<Mat>(count);
        while (rois.Count < count && _unknownRois.Count > 0)
        {
            rois.Add(_unknownRois.Dequeue());
        }

        return rois;
    }

    /// <summary>清空未知识别区域截图缓存。</summary>
    internal void ClearUnknownRois()
    {
        while (_unknownRois.Count > 0)
        {
            _unknownRois.Dequeue().Dispose();
        }
    }

    /// <summary>
    /// 计算两张区域截图互相的匹配度（最大 NCC）：颜色以第一张判定为准，第二张按该颜色灰度化后与第一张比较；
    /// 尺寸不一致时先缩放到第一张尺寸。用于自动截图"稳定帧"校验（同一物品不同帧截图应高度相似）。
    /// </summary>
    public double GetSimilarity(Mat a, Mat b)
    {
        using var bgrA = a.Channels() == 4
            ? a.CvtColor(ColorConversionCodes.BGRA2BGR)
            : a.Clone();
        using var bgrB = b.Channels() == 4
            ? b.CvtColor(ColorConversionCodes.BGRA2BGR)
            : b.Clone();
        var (grayA, colorIndex, _, _, _) = ToGray(bgrA);

        byte[] grayB;
        if (bgrB.Width == bgrA.Width && bgrB.Height == bgrA.Height)
        {
            grayB = ToGrayByColor(bgrB, colorIndex);
        }
        else
        {
            using var resized = ResizeHelper.ResizeTo(bgrB, bgrA.Width, bgrA.Height);
            grayB = ToGrayByColor(resized, colorIndex);
        }

        var (sumI, sumI2) = BuildIntegral(grayA, bgrA.Width, bgrA.Height);
        var c = CropTemplate(grayB, bgrA.Width, bgrA.Height);
        var template = new MojangTemplate
        {
            Name = string.Empty,
            Color = string.Empty,
            ItemName = string.Empty,
            Gray = c.Gray,
            Width = c.Width,
            Height = bgrA.Height,
            Len = 0,
            MeanT = c.MeanT,
            VarT = c.VarT,
            NonZero = c.NonZero,
        };

        var (score, _, _) = NccMax(grayA, sumI, sumI2, bgrA.Width, bgrA.Height, template);
        return score;
    }

    /// <summary>
    /// 颜色判定：返回区域图的主颜色索引（0灰/1绿/2蓝/3紫/4白，与 <see cref="Refs"/> 一致）。
    /// </summary>
    public int GetColorIndex(Mat srcBgr)
    {
        using var bgr = srcBgr.Channels() == 4
            ? srcBgr.CvtColor(ColorConversionCodes.BGRA2BGR)
            : srcBgr.Clone();
        var (_, colorIndex, _, _, _) = ToGray(bgr);
        return colorIndex;
    }

    /// <summary>
    /// 在指定目录中查找与候选截图重复的已有图片。
    /// 已有图片按候选图判定的颜色灰度化后与候选图计算最大 NCC，任一 ≥ 阈值即视为重复。
    /// 尺寸不一致的已有图先缩放到候选图尺寸再比较。
    /// </summary>
    /// <param name="srcBgr">候选截图（BGR/BGRA，按自身颜色灰度化）</param>
    /// <param name="dirPath">目标颜色目录</param>
    /// <param name="threshold">匹配阈值</param>
    /// <returns>是否存在重复图片</returns>
    public bool FindDuplicate(Mat srcBgr, string dirPath, double threshold)
    {
        using var bgr = srcBgr.Channels() == 4
            ? srcBgr.CvtColor(ColorConversionCodes.BGRA2BGR)
            : srcBgr.Clone();
        var (gray, colorIndex, _, _, _) = ToGray(bgr);

        if (!Directory.Exists(dirPath))
        {
            return false;
        }

        var (sumI, sumI2) = BuildIntegral(gray, bgr.Width, bgr.Height);
        foreach (var file in Directory.EnumerateFiles(dirPath, "*.png"))
        {
            try
            {
                using var mat = new Mat(file, ImreadModes.Color);
                if (mat.Empty())
                {
                    continue;
                }

                using var dupBgr = mat.Channels() == 4
                    ? mat.CvtColor(ColorConversionCodes.BGRA2BGR)
                    : mat.Clone();
                using var resized = ResizeHelper.ResizeTo(dupBgr, bgr.Width, bgr.Height);
                var dupGray = ToGrayByColor(resized, colorIndex);
                var c = CropTemplate(dupGray, resized.Width, resized.Height);
                var template = new MojangTemplate
                {
                    Name = string.Empty,
                    Color = string.Empty,
                    ItemName = string.Empty,
                    Gray = c.Gray,
                    Width = c.Width,
                    Height = resized.Height,
                    Len = 0,
                    MeanT = c.MeanT,
                    VarT = c.VarT,
                    NonZero = c.NonZero,
                };

                var (score, _, _) = NccMax(gray, sumI, sumI2, bgr.Width, bgr.Height, template);
                if (score >= threshold)
                {
                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "自动截图去重比较跳过图片：{File}", file);
            }
        }

        return false;
    }

    /// <summary>
    /// 识别 F 键右侧文字区域对应的物品。
    /// </summary>
    /// <param name="srcMat">捕获区域源图（BGR）</param>
    /// <param name="fKeyRegion">F 键识别结果区域</param>
    /// <param name="scale">分辨率缩放系数</param>
    /// <param name="cacheUnknownRoi">识别为未知时是否缓存区域截图（供自动截图复用，默认缓存）</param>
    /// <returns>识别结果；未识别到或区域越界返回 null</returns>
    public MojangMatchResult? Match(Mat srcMat, Region fKeyRegion, double scale, bool cacheUnknownRoi = true)
    {
        var rect = GetTextRegion(fKeyRegion, scale);

        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > srcMat.Width || rect.Y + rect.Height > srcMat.Height)
        {
            ClearUnknownRois();
            return null;
        }

        using var roiMat = new Mat(srcMat, rect);
        using var sourceBgr = roiMat.Channels() == 4
            ? roiMat.CvtColor(ColorConversionCodes.BGRA2BGR)
            : roiMat.Clone();
        using var bgrMat = ResizeHelper.ResizeTo(sourceBgr, RegionWidth, RegionHeight);
        var (gray, colorIndex, judgeMs, grayMs, maxX) = ToGray(bgrMat);
        var colorName = Refs[colorIndex].Name;

        // 用 V>=65% 像素的最大 x 估算字数
        var estLen = maxX <= 65 ? 2 : maxX <= 94 ? 3 : maxX <= 122 ? 4 : 5;

        // 按 x, x-1, x+1, x-2, x+2 ... 顺序生成待匹配的字数列表
        var lens = new List<int> { estLen };
        var left = estLen - 1;
        var right = estLen + 1;
        while (left >= 2 || right <= 5)
        {
            if (left >= 2)
            {
                lens.Add(left);
            }

            if (right <= 5)
            {
                lens.Add(right);
            }

            left--;
            right++;
        }

        var (sumI, sumI2) = BuildIntegral(gray, bgrMat.Width, bgrMat.Height);

        // 种子模式：检测行首「（化种匣下白色物品种子样式）
        var isSeed = IsSeedMarker(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height);

        var nccSw = Stopwatch.StartNew();
        MojangTemplate? best = null;
        var bestScore = -1.0;
        var bestDx = 0;
        var bestDy = 0;
        var matchedCount = 0;

        // 正常模式：按估算字数逐类匹配，x 方向固定对齐（截图与模板 x 相同）
        void MatchNormal()
        {
            foreach (var len in lens)
            {
                if (!_templatesByColorAndLen.TryGetValue((colorName, len), out var list) || list.Count == 0)
                {
                    continue;
                }

                // 识别完当前字数类后，若该类最高分已达标则提前退出
                var lenBestScore = -1.0;
                foreach (var t in list)
                {
                    var (score, dx, dy) = NccMax(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height, t);
                    matchedCount++;
                    if (score > lenBestScore)
                    {
                        lenBestScore = score;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = t;
                        bestDx = dx;
                        bestDy = dy;
                    }
                }

                if (lenBestScore > MinScore)
                {
                    break;
                }
            }
        }

        // 种子模式：材料名被「右移约 18px 且字数估算失效（整行含「」的种子），跨字数按固定偏移扫描
        void MatchSeed()
        {
            for (var len = 2; len <= 5; len++)
            {
                if (!_templatesByColorAndLen.TryGetValue((colorName, len), out var list) || list.Count == 0)
                {
                    continue;
                }

                var lenBestScore = -1.0;
                foreach (var t in list)
                {
                    var st = GetSeedTemplate(t);
                    var (score, dx, dy) = NccSeed(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height, st);
                    matchedCount++;
                    if (score > lenBestScore)
                    {
                        lenBestScore = score;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = t;
                        bestDx = dx;
                        bestDy = dy;
                    }
                }

                if (lenBestScore > MinScore)
                {
                    break;
                }
            }
        }

        if (isSeed)
        {
            MatchSeed();
            if (bestScore < MinScore)
            {
                MatchNormal(); // 种子模式未达标时回退正常匹配，避免「误判导致正常物品漏识别
            }
        }
        else
        {
            MatchNormal();
        }

        nccSw.Stop();

        if (best is null || bestScore < MinScore)
        {
            if (Config.TestModeEnabled
                || Config.PickLogLevel >= 2)
            {
                _logger.LogInformation(
                    "莫版测试未识别到：颜色={Color} 估算字数={EstLen} 种子模式={IsSeed} 最高分={BestScore:F3} 判定={JudgeMs:F2}ms 灰度={GrayMs:F2}ms NCC={NccMs:F2}ms 模板={TemplateCount}",
                    colorName, estLen, isSeed, best is null ? 0 : bestScore, judgeMs, grayMs, nccSw.Elapsed.TotalMilliseconds, matchedCount);
            }

            // 识别为未知：缓存本帧截图区域（140×26，与模板素材规格一致）供自动截图复用，只保留最近 N 张（N=配置稳定次数，至少 1）
            if (cacheUnknownRoi)
            {
                var roi = GetScreenshotRoi(srcMat, fKeyRegion, scale);
                if (roi is not null)
                {
                    var capacity = Math.Clamp(Config.AutoScreenshotStreak, 1, 30);
                    _unknownRois.Enqueue(roi);
                    while (_unknownRois.Count > capacity)
                    {
                        _unknownRois.Dequeue().Dispose();
                    }
                }
            }

            return null;
        }

        var templateRect = new Rect(rect.X + (int)(bestDx * scale), rect.Y + (int)(bestDy * scale),
            Math.Max(1, (int)(best.Width * scale)), Math.Max(1, (int)(best.Height * scale)));
        return new MojangMatchResult(best.Name, best.ItemName, bestScore, rect, templateRect,
            judgeMs, grayMs, nccSw.Elapsed.TotalMilliseconds, matchedCount, colorIndex);
    }

    /// <summary>
    /// 二次确认：仅对指定物品模板做复检，不重新判定颜色、不遍历全量模板。
    /// </summary>
    /// <param name="srcMat">重新截图的捕获区域源图（BGR）</param>
    /// <param name="fKeyRegion">重新检测到的 F 键区域</param>
    /// <param name="scale">分辨率缩放系数</param>
    /// <param name="name">第一次匹配到的模板名（交互名）</param>
    /// <param name="colorIndex">第一次判定的颜色索引</param>
    /// <returns>匹配度是否达标</returns>
    public bool ConfirmMatch(Mat srcMat, Region fKeyRegion, double scale, string name, int colorIndex)
    {
        var rect = GetTextRegion(fKeyRegion, scale);

        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > srcMat.Width || rect.Y + rect.Height > srcMat.Height)
        {
            return false;
        }

        var colorName = Refs[colorIndex].Name;
        if (!_templatesByColorAndLen.TryGetValue((colorName, Math.Min(name.Length, 5)), out var candidates)
            || candidates.FirstOrDefault(t => t.Name == name) is not { } template)
        {
            return false;
        }

        using var roiMat = new Mat(srcMat, rect);
        using var sourceBgr = roiMat.Channels() == 4
            ? roiMat.CvtColor(ColorConversionCodes.BGRA2BGR)
            : roiMat.Clone();
        using var bgrMat = ResizeHelper.ResizeTo(sourceBgr, RegionWidth, RegionHeight);
        var gray = ToGrayByColor(bgrMat, colorIndex);
        var (sumI, sumI2) = BuildIntegral(gray, bgrMat.Width, bgrMat.Height);

        // 种子模式二次确认：与首次识别一致，检测「后按偏移扫描
        if (IsSeedMarker(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height))
        {
            var st = GetSeedTemplate(template);
            var (score, _, _) = NccSeed(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height, st);
            return score >= MinScore;
        }

        var (normalScore, _, _) = NccMax(gray, sumI, sumI2, bgrMat.Width, bgrMat.Height, template);
        return normalScore >= MinScore;
    }

    /// <summary>
    /// 使用指定颜色做灰度化（不重新判定颜色）。
    /// </summary>
    private static byte[] ToGrayByColor(Mat bgrMat, int colorIndex)
    {
        var w = bgrMat.Width;
        var h = bgrMat.Height;
        var n = w * h;

        using var mat3 = new Mat<Vec3b>(bgrMat);
        var indexer = mat3.GetIndexer();

        var gray = new byte[n];
        var refLab = RefLabs[colorIndex];
        for (var y = 0; y < h; y++)
        {
            var rowOffset = y * w;
            for (var x = 0; x < w; x++)
            {
                var px = indexer[y, x];
                var b = px.Item0;
                var g = px.Item1;
                var r = px.Item2;
                var v = Max3(b, g, r);
                if (v < VoteMinV)
                {
                    gray[rowOffset + x] = 0;
                    continue;
                }

                var lab = RgbToLab(r, g, b);
                var dE = LabDist(lab, refLab);
                gray[rowOffset + x] = (byte)(Math.Clamp(1.0 - dE / T, 0.0, 1.0) * 255.0);
            }
        }

        return gray;
    }

    /// <summary>
    /// 颜色判定 + 灰度化。与预处理脚本（autopick_preprocess.py）保持完全一致。
    /// </summary>
    private static (byte[] Gray, int ColorIndex, double JudgeMs, double GrayMs, int MaxX) ToGray(Mat bgrMat)
    {
        var w = bgrMat.Width;
        var h = bgrMat.Height;
        var n = w * h;

        using var mat3 = new Mat<Vec3b>(bgrMat);
        var indexer = mat3.GetIndexer();

        var b = new byte[n];
        var g = new byte[n];
        var r = new byte[n];

        // 1. 颜色判定：V>=65% 中亮度前 30% 像素的平均色 -> 最近参考色
        var judgeSw = Stopwatch.StartNew();
        var brightV = new List<int>(n);
        var maxX = 0;
        for (var y = 0; y < h; y++)
        {
            var rowOffset = y * w;
            for (var x = 0; x < w; x++)
            {
                var px = indexer[y, x];
                var i = rowOffset + x;
                b[i] = px.Item0;
                g[i] = px.Item1;
                r[i] = px.Item2;
                var v = Max3(b[i], g[i], r[i]);
                if (v >= VoteMinV)
                {
                    brightV.Add(v);
                    if (x > maxX)
                    {
                        maxX = x;
                    }
                }
            }
        }

        byte[] gray = new byte[n];
        if (brightV.Count == 0)
        {
            judgeSw.Stop();
            return (gray, 0, judgeSw.Elapsed.TotalMilliseconds, 0, 0);
        }

        brightV.Sort();
        var idx = 0.7 * (brightV.Count - 1);
        var lower = (int)Math.Floor(idx);
        var upper = (int)Math.Ceiling(idx);
        var thr = lower == upper
            ? brightV[lower]
            : brightV[lower] + (idx - lower) * (brightV[upper] - brightV[lower]);

        double sumR = 0, sumG = 0, sumB = 0;
        var cnt = 0;
        for (var i = 0; i < n; i++)
        {
            var v = Max3(b[i], g[i], r[i]);
            if (v >= thr)
            {
                sumR += r[i];
                sumG += g[i];
                sumB += b[i];
                cnt++;
            }
        }

        var coreLab = RgbToLab(sumR / cnt, sumG / cnt, sumB / cnt);
        var colorIndex = 0;
        var minDist = double.MaxValue;
        for (var i = 0; i < RefLabs.Length; i++)
        {
            var d = LabDist(coreLab, RefLabs[i]);
            if (d < minDist)
            {
                minDist = d;
                colorIndex = i;
            }
        }
        judgeSw.Stop();

        // 2. 灰度化：向主颜色的匹配度
        var graySw = Stopwatch.StartNew();
        var refLab = RefLabs[colorIndex];
        for (var i = 0; i < n; i++)
        {
            var v = Max3(b[i], g[i], r[i]);
            if (v < VoteMinV)
            {
                gray[i] = 0;
                continue;
            }

            var lab = RgbToLab(r[i], g[i], b[i]);
            var dE = LabDist(lab, refLab);
            var match = Math.Clamp(1.0 - dE / T, 0.0, 1.0) * 255.0;
            gray[i] = (byte)match;
        }
        graySw.Stop();

        return (gray, colorIndex, judgeSw.Elapsed.TotalMilliseconds, graySw.Elapsed.TotalMilliseconds, maxX);
    }

    /// <summary>在截图灰度图上滑动模板（仅 y 方向，x 方向固定对齐），返回最大 NCC 及其位置。</summary>
    private static (double Score, int Dx, int Dy) NccMax(byte[] img, long[] sumI, long[] sumI2, int imgW, int imgH, MojangTemplate t)
    {
        var dxMax = imgW - t.Width + 1;
        var dyMax = imgH - t.Height + 1;
        if (dxMax <= 0 || dyMax <= 0)
        {
            return (0, 0, 0);
        }

        // x 方向固定对齐（截图与模板 x 相同），仅 y 方向上下滑动
        var best = -1.0;
        var bestDy = 0;
        for (var dy = 0; dy < dyMax; dy++)
        {
            var s = NccAt(img, sumI, sumI2, imgW, t, 0, dy);
            if (s > best)
            {
                best = s;
                bestDy = dy;
            }
        }

        return (best, 0, bestDy);
    }

    /// <summary>在 (dx, dy) 位置计算模板与截图窗口的归一化互相关（NCC）。窗口和/平方和由积分图 O(1) 查询。</summary>
    private static double NccAt(byte[] img, long[] sumI, long[] sumI2, int imgW, MojangTemplate t, int dx, int dy)
    {
        var n = t.Width * t.Height;
        var sI = (double)WindowSum(sumI, imgW, dx, dy, t.Width, t.Height);
        var sI2 = (double)WindowSum(sumI2, imgW, dx, dy, t.Width, t.Height);

        double sumIT = 0;
        foreach (var (y, x, tg) in t.NonZero)
        {
            sumIT += img[(dy + y) * imgW + dx + x] * tg;
        }

        var meanI = sI / n;
        var numerator = sumIT - n * meanI * t.MeanT;
        var denom = Math.Sqrt((sI2 - n * meanI * meanI) * t.VarT);
        if (denom < 1e-9)
        {
            return 0;
        }

        return numerator / denom;
    }

    /// <summary>
    /// 检测截图是否处于化种匣种子模式：行首 x4~12 出现「字形（用「模板做 NCC），
    /// 且「两侧（x0~3、x13~17）无文字笔画（种子文字自 x18 起，普通文字 x0 起即有笔画）。
    /// </summary>
    private static bool IsSeedMarker(byte[] gray, long[] sumI, long[] sumI2, int w, int h)
    {
        var best = -1.0;
        for (var dy = 0; dy <= h - SeedMarkerTemplate.Height; dy++)
        {
            for (var dx = SeedMarkerDxMin; dx <= SeedMarkerDxMax; dx++)
            {
                var s = NccAt(gray, sumI, sumI2, w, SeedMarkerTemplate, dx, dy);
                if (s > best)
                {
                    best = s;
                }
            }
        }

        if (best < SeedMarkerMinScore)
        {
            return false;
        }

        var limitY = Math.Min(26, h);
        for (var y = 0; y < limitY; y++)
        {
            var row = y * w;
            for (var x = 0; x < 4; x++)
            {
                if (gray[row + x] > 0)
                {
                    return false;
                }
            }

            for (var x = 13; x < 18; x++)
            {
                if (gray[row + x] > 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 种子模式匹配：材料名被「右移约 18px，按固定偏移（<see cref="SeedDxMin"/>~<see cref="SeedDxMax"/>）
    /// 扫描 x 方向（y 方向沿用上下滑动），返回最大 NCC 及其位置。
    /// </summary>
    private static (double Score, int Dx, int Dy) NccSeed(byte[] img, long[] sumI, long[] sumI2, int imgW, int imgH, MojangTemplate t)
    {
        var dyMax = imgH - t.Height + 1;
        if (dyMax <= 0)
        {
            return (0, 0, 0);
        }

        var best = -1.0;
        var bestDx = 0;
        var bestDy = 0;
        for (var dx = SeedDxMin; dx <= SeedDxMax; dx++)
        {
            if (dx + t.Width > imgW)
            {
                continue;
            }

            for (var dy = 0; dy < dyMax; dy++)
            {
                var s = NccAt(img, sumI, sumI2, imgW, t, dx, dy);
                if (s > best)
                {
                    best = s;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        return (best, bestDx, bestDy);
    }

    /// <summary>获取种子模式用模板：宽模板（&gt; <see cref="MaxSeedWidth"/>）返回截断到 102px 的缓存变体，否则返回原模板。</summary>
    private MojangTemplate GetSeedTemplate(MojangTemplate t)
    {
        if (t.Width <= MaxSeedWidth)
        {
            return t;
        }

        if (!_seedTruncated.TryGetValue(t, out var cut))
        {
            cut = BuildTruncated(t, MaxSeedWidth);
            _seedTruncated[t] = cut;
        }

        return cut;
    }

    /// <summary>将模板截断到指定宽度（保留左侧 maxWidth 列）并重算统计量与非零列表。</summary>
    private static MojangTemplate BuildTruncated(MojangTemplate t, int maxWidth)
    {
        var w = Math.Min(t.Width, maxWidth);
        var gray = new byte[w * t.Height];
        for (var y = 0; y < t.Height; y++)
        {
            Array.Copy(t.Gray, y * t.Width, gray, y * w, w);
        }

        double sumT = 0, sumT2 = 0;
        foreach (var v in gray)
        {
            sumT += v;
            sumT2 += v * v;
        }

        var meanT = sumT / gray.Length;
        return new MojangTemplate
        {
            Name = t.Name,
            Color = t.Color,
            ItemName = t.ItemName,
            Gray = gray,
            Width = w,
            Height = t.Height,
            Len = t.Len,
            MeanT = meanT,
            VarT = sumT2 - gray.Length * meanT * meanT,
            NonZero = BuildNonZero(gray, w, t.Height),
        };
    }

    /// <summary>构建「（左书名号）字形模板：从化种匣种子截图（140×28 白色文字）行首 x4~12 截取的 9×23 像素。</summary>
    private static MojangTemplate BuildSeedMarkerTemplate()
    {
        byte[] gray =
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0,
            138, 225, 225, 225, 225, 225, 225, 204, 0,
            182, 255, 255, 255, 255, 255, 255, 233, 13,
            182, 255, 255, 242, 138, 129, 129, 111, 0,
            182, 255, 255, 209, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
            182, 255, 255, 194, 0, 0, 0, 0, 0,
        ];

        double sumT = 0, sumT2 = 0;
        foreach (var v in gray)
        {
            sumT += v;
            sumT2 += v * v;
        }

        var meanT = sumT / gray.Length;
        return new MojangTemplate
        {
            Name = string.Empty,
            Color = string.Empty,
            ItemName = string.Empty,
            Gray = gray,
            Width = 9,
            Height = 23,
            Len = 0,
            MeanT = meanT,
            VarT = sumT2 - gray.Length * meanT * meanT,
            NonZero = BuildNonZero(gray, 9, 23),
        };
    }

    /// <summary>构建灰度图的求和积分图与平方和积分图（尺寸 (h+1)x(w+1)）。</summary>
    private static (long[] SumI, long[] SumI2) BuildIntegral(byte[] img, int w, int h)
    {
        var stride = w + 1;
        var sumI = new long[(h + 1) * stride];
        var sumI2 = new long[(h + 1) * stride];
        for (var y = 0; y < h; y++)
        {
            var rowOffset = y * w;
            var cur = y * stride;
            var next = (y + 1) * stride;
            for (var x = 0; x < w; x++)
            {
                var v = img[rowOffset + x];
                var v2 = v * v;
                sumI[next + x + 1] = v + sumI[cur + x + 1] + sumI[next + x] - sumI[cur + x];
                sumI2[next + x + 1] = v2 + sumI2[cur + x + 1] + sumI2[next + x] - sumI2[cur + x];
            }
        }

        return (sumI, sumI2);
    }

    /// <summary>积分图窗口求和：区域 [x0, x0+w) x [y0, y0+h)。</summary>
    private static long WindowSum(long[] integral, int imgW, int x0, int y0, int w, int h)
    {
        var stride = imgW + 1;
        var a = integral[y0 * stride + x0];
        var b = integral[y0 * stride + x0 + w];
        var c = integral[(y0 + h) * stride + x0];
        var d = integral[(y0 + h) * stride + x0 + w];
        return d - b - c + a;
    }

    private static double[] RgbToLab(double r, double g, double b)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        var linR = R <= 0.04045 ? R / 12.92 : Math.Pow((R + 0.055) / 1.055, 2.4);
        var linG = G <= 0.04045 ? G / 12.92 : Math.Pow((G + 0.055) / 1.055, 2.4);
        var linB = B <= 0.04045 ? B / 12.92 : Math.Pow((B + 0.055) / 1.055, 2.4);

        var x = 0.4124564 * linR + 0.3575761 * linG + 0.1804375 * linB;
        var y = 0.2126729 * linR + 0.7151522 * linG + 0.0721750 * linB;
        var z = 0.0193339 * linR + 0.1191920 * linG + 0.9503041 * linB;

        const double xn = 0.95047, yn = 1.0, zn = 1.08883;
        var fx = LabF(x / xn);
        var fy = LabF(y / yn);
        var fz = LabF(z / zn);

        var L = 116.0 * fy - 16.0;
        var a = 500.0 * (fx - fy);
        var bb = 200.0 * (fy - fz);
        return [L, a, bb];
    }

    private static double LabF(double t)
    {
        const double d = 6.0 / 29.0;
        return t > d * d * d ? Math.Cbrt(t) : t / (3.0 * d * d) + 4.0 / 29.0;
    }

    private static double LabDist(double[] a, double[] b)
    {
        var dL = a[0] - b[0];
        var da = a[1] - b[1];
        var db = a[2] - b[2];
        return Math.Sqrt(dL * dL + da * da + db * db);
    }

    private static int Max3(byte a, byte b, byte c) => Math.Max(a, Math.Max(b, c));

    private static MojangMatch Load()
    {
        var assetsDir = Global.Absolute(@"GameTask\AutoPick\Assets");
        var binPath = Path.Combine(assetsDir, "莫版模板.bin");
        MojangMatch matcher;
        if (File.Exists(binPath))
        {
            matcher = LoadFromBin(binPath, logger: App.GetLogger<MojangMatch>());
        }
        else
        {
            matcher = LoadFromJsonAndPng(Path.Combine(assetsDir, "莫版模板"));
        }

        // 内置模板加载完成后，追加加载用户额外模板（同名时用户模板优先）
        LoadFromExtraFolder(matcher);
        return matcher;
    }

    internal static MojangMatch LoadFromBin(string binPath, AutoPickConfig? config = null, ILogger? logger = null)
    {
        var matcher = new MojangMatch(config, logger);
        using var fs = File.OpenRead(binPath);
        using var br = new BinaryReader(fs);
        if (br.ReadInt32() != BinMagic || br.ReadInt32() != 1)
        {
            throw new InvalidDataException("本体模板包格式或版本无效。");
        }

        var count = br.ReadInt32();
        if (count < 1 || count > 10000) throw new InvalidDataException("本体模板数量无效。");
        for (var i = 0; i < count; i++)
        {
            var name = ReadString(br);
            var color = ReadString(br);
            var itemName = ReadString(br);
            var len = br.ReadInt32();
            var width = br.ReadInt32();
            var height = br.ReadInt32();
            if (width < 1 || width > RegionWidth || height != ScreenshotHeight)
                throw new InvalidDataException("本体模板尺寸无效。");
            var gray = br.ReadBytes(checked(width * height));
            if (gray.Length != width * height) throw new InvalidDataException("本体模板包不完整。");
            var c = CropTemplate(gray, width, height);
            var template = new MojangTemplate
            {
                Name = name,
                Color = color,
                ItemName = itemName,
                Gray = c.Gray,
                Width = c.Width,
                Height = height,
                Len = len,
                MeanT = c.MeanT,
                VarT = c.VarT,
                NonZero = c.NonZero,
            };

            AddTemplate(matcher, template);
        }

        LogLoaded(matcher);
        return matcher;
    }

    private static MojangMatch LoadFromJsonAndPng(string baseDir)
    {
        var matcher = new MojangMatch(logger: App.GetLogger<MojangMatch>());
        var jsonPath = Path.Combine(baseDir, "templates.json");
        if (!File.Exists(jsonPath))
        {
            return matcher;
        }

        var entries = JsonSerializer.Deserialize<List<TemplateEntry>>(File.ReadAllText(jsonPath)) ?? [];
        foreach (var e in entries)
        {
            var imgPath = Path.Combine(baseDir, e.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(imgPath))
            {
                continue;
            }

            using var mat = new Mat(imgPath, ImreadModes.Grayscale);
            byte[] gray = new byte[mat.Rows * mat.Cols];
            mat.GetArray<byte>(out gray);

            var c = CropTemplate(gray, mat.Cols, mat.Rows);
            var template = new MojangTemplate
            {
                Name = e.Name,
                Color = e.Color,
                ItemName = e.ItemName,
                Gray = c.Gray,
                Width = c.Width,
                Height = mat.Rows,
                Len = e.Len > 0 ? e.Len : Math.Min(e.Name.Length, 5),
                MeanT = c.MeanT,
                VarT = c.VarT,
                NonZero = c.NonZero,
            };

            AddTemplate(matcher, template);
        }

        LogLoaded(matcher);
        return matcher;
    }

    /// <summary>
    /// 加载用户额外模板：递归扫描 <see cref="ExtraTemplateDir"/> 下全部 PNG，
    /// 处理方式与 generate_templates.py 一致（尺寸校验、颜色判定、灰度化、文件名归一化）。
    /// 与内置模板同名（同颜色、同字数）时以用户模板为准（覆盖）。
    /// </summary>
    private static void LoadFromExtraFolder(MojangMatch matcher)
    {
        var dir = Global.Absolute(ExtraTemplateDir);
        if (!Directory.Exists(dir))
        {
            return;
        }

        var logger = matcher._logger;
        var loaded = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (TryLoadExtraTemplate(matcher, file))
                {
                    loaded++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "额外模板加载失败：{File}", file);
                skipped++;
            }
        }

        if (loaded > 0 || skipped > 0)
        {
            logger.LogInformation("额外模板加载完成：{Loaded} 个，跳过 {Skipped} 个（{Dir}）", loaded, skipped, dir);
        }
    }

    /// <summary>加载单个额外模板 PNG（彩色图，自动做颜色判定与灰度化），返回是否成功加入。</summary>
    private static bool TryLoadExtraTemplate(MojangMatch matcher, string filePath)
    {
        using var mat = new Mat(filePath, ImreadModes.Color);
        if (mat.Empty())
        {
            return false;
        }

        var height = mat.Height;
        if (height != 26 && height != 28)
        {
            return false; // 仅允许 140×26 或 140×28
        }

        // 140×28：裁掉上下各一行像素后按 140×26 处理；140×26 直接使用
        using var roiMat = new Mat(mat, new Rect(0, height == 28 ? 1 : 0, mat.Width, 26));
        if (roiMat.Width > 140)
        {
            return false; // 宽度超过 140
        }

        using var bgr = roiMat.Channels() == 4
            ? roiMat.CvtColor(ColorConversionCodes.BGRA2BGR)
            : roiMat.Clone();

        // 颜色判定 + 灰度化（与 generate_templates.py 一致）；无亮像素视为无效
        var (gray, colorIndex, _, _, maxX) = ToGray(bgr);
        if (maxX == 0)
        {
            return false;
        }

        var name = NormalizeTemplateName(Path.GetFileNameWithoutExtension(filePath));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var c = CropTemplate(gray, bgr.Width, bgr.Height);
        var template = new MojangTemplate
        {
            Name = name,
            Color = Refs[colorIndex].Name,
            ItemName = name,
            Gray = c.Gray,
            Width = c.Width,
            Height = bgr.Height,
            Len = Math.Min(name.Length, 5),
            MeanT = c.MeanT,
            VarT = c.VarT,
            NonZero = c.NonZero,
        };

        AddExtraTemplate(matcher, template);
        return true;
    }

    /// <summary>
    /// 文件名归一化（与 generate_templates.py 一致）：剥离 Windows 副本后缀 " (N)" 与 "_N"（连同前导空格/下划线），
    /// 再去掉尾随空格/下划线。
    /// </summary>
    private static string NormalizeTemplateName(string stem)
    {
        stem = Regex.Replace(stem, @"[ _]*\(\d+\)$", "");
        stem = Regex.Replace(stem, @"[ _]*_\d+$", "");
        return stem.TrimEnd(' ', '_');
    }

    /// <summary>添加用户额外模板：同 key（颜色、字数）下同名模板先移除再加入，_templatesByName 直接覆盖，保证用户模板优先。</summary>
    private static void AddExtraTemplate(MojangMatch matcher, MojangTemplate template)
    {
        var key = (template.Color, template.Len);
        if (!matcher._templatesByColorAndLen.TryGetValue(key, out var list))
        {
            list = [];
            matcher._templatesByColorAndLen[key] = list;
        }

        list.RemoveAll(t => string.Equals(t.Name, template.Name, StringComparison.Ordinal));
        list.Add(template);

        matcher._templatesByName[template.Name] = template;
    }

    private static string ReadString(BinaryReader br)
    {
        var len = br.ReadInt32();
        if (len < 0 || len > 65536) throw new InvalidDataException("模板字符串长度无效。");
        var bytes = br.ReadBytes(len);
        if (bytes.Length != len) throw new InvalidDataException("模板字符串不完整。");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 按有效宽度裁剪模板并重算统计量。有效宽度 = 亮度&gt;0 像素的最大 x - 2（自动过滤最右边缘）。
    /// 截图与模板 x 方向对齐，右侧超出部分无需参与计算，直接丢弃。
    /// 同时构建非零像素列表，供 NCC 分子只遍历非零区域。
    /// </summary>
    private static CroppedTemplate CropTemplate(byte[] gray, int width, int height)
    {
        var maxX = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (gray[row + x] > 0 && x > maxX)
                {
                    maxX = x;
                }
            }
        }

        var effW = Math.Max(1, maxX - 2);
        var cropped = new byte[effW * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(gray, y * width, cropped, y * effW, effW);
        }

        double sumT = 0, sumT2 = 0;
        foreach (var v in cropped)
        {
            sumT += v;
            sumT2 += v * v;
        }

        var meanT = sumT / cropped.Length;
        return new CroppedTemplate(cropped, effW, meanT, sumT2 - cropped.Length * meanT * meanT, BuildNonZero(cropped, effW, height));
    }

    /// <summary>构建模板非零像素列表 (Y, X, 灰度)，跳过共同为 0 的区域。</summary>
    private static (int Y, int X, byte Gray)[] BuildNonZero(byte[] gray, int width, int height)
    {
        var list = new List<(int, int, byte)>(gray.Length / 2);
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var v = gray[row + x];
                if (v > 0)
                {
                    list.Add((y, x, v));
                }
            }
        }

        return list.ToArray();
    }

    private sealed record CroppedTemplate(byte[] Gray, int Width, double MeanT, double VarT, (int Y, int X, byte Gray)[] NonZero);

    private static void AddTemplate(MojangMatch matcher, MojangTemplate template)
    {
        var key = (template.Color, template.Len);
        if (!matcher._templatesByColorAndLen.TryGetValue(key, out var list))
        {
            list = [];
            matcher._templatesByColorAndLen[key] = list;
        }

        list.Add(template);

        if (!matcher._templatesByName.ContainsKey(template.Name))
        {
            matcher._templatesByName[template.Name] = template;
        }
    }

    private static void LogLoaded(MojangMatch matcher)
    {
        var total = matcher._templatesByColorAndLen.Values.Sum(v => v.Count);
        matcher._logger.LogInformation("本体模板加载完成，共 {Total} 个模板", total);
    }

    private sealed class MojangTemplate
    {
        public required string Name { get; init; }
        public required string Color { get; init; }
        public required string ItemName { get; init; }
        public required byte[] Gray { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Len { get; init; }
        public double MeanT { get; init; }
        public double VarT { get; init; }
        public required (int Y, int X, byte Gray)[] NonZero { get; init; }
    }

    private sealed class TemplateEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("color")] public string Color { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("itemName")] public string ItemName { get; set; } = "";
        [JsonPropertyName("len")] public int Len { get; set; }
    }
}
