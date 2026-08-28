using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Localization;
using OpenCvSharp;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using System.Threading;

using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.Helpers.Extensions;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;


namespace BetterGenshinImpact.GameTask.Common.BgiVision;

public enum GameUiCategory
{
    Unknown,
    Main,
    Talk,
    BigMap
}

public static partial class Bv
{
    public static GameUiCategory WhichGameUi()
    {
        using var region = TaskControl.CaptureToRectArea();
        return WhichGameUi(region);
    }

    public static GameUiCategory WhichGameUi(ImageRegion region)
    {
        if (IsInTalkUi(region))
        {
            return GameUiCategory.Talk;
        }

        if (IsInBigMapUi(region))
        {
            return GameUiCategory.BigMap;
        }

        if (IsInMainUi(region))
        {
            return GameUiCategory.Main;
        }

        return GameUiCategory.Unknown;
    }

    public static GameUiCategory WhichGameUiForTriggers(ImageRegion region)
    {
        if (IsInTalkUi(region))
        {
            return GameUiCategory.Talk;
        }

        if (IsInBigMapUi(region))
        {
            return GameUiCategory.BigMap;
        }

        return GameUiCategory.Unknown;
    }


    /// <summary>
    /// 是否在主界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInMainUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(ElementRecognition.Get("PaimonMenu", captureRa));
        return ra.IsExist() && !IsInRevivePrompt(captureRa);
    }

    /// <summary>
    /// 等待主界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitForMainUi(CancellationToken ct, int retryTimes = 10)
    {
        for (var i = 0; i < retryTimes; i++)
        {
            await TaskControl.Delay(1000, ct);
            using var ra3 = TaskControl.CaptureToRectArea();
            if (IsInMainUi(ra3))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否在秘境中
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInDomain(ImageRegion captureRa)
    {
        using var matchRegion = captureRa.Find(ElementRecognition.Get("InDomain", captureRa));
        if (matchRegion.IsEmpty())
        {
            return false;
        }

        bool IsWhite(int r, int g, int b)
        {
            return (r >= 240 && r <= 255) &&
                   (g >= 240 && g <= 255) &&
                   (b >= 240 && b <= 255);
        }

        // 若全部为白色则视为不在秘境中
        var samplePoints = new[]
        {
            new Point(matchRegion.X + matchRegion.Width / 2, matchRegion.Y + matchRegion.Height / 2),
            new Point(matchRegion.X + matchRegion.Width / 4, matchRegion.Y + matchRegion.Height / 4),
            new Point(matchRegion.X + matchRegion.Width * 3 / 4, matchRegion.Y + matchRegion.Height / 4),
            new Point(matchRegion.X + matchRegion.Width / 4, matchRegion.Y + matchRegion.Height * 3 / 4),
            new Point(matchRegion.X + matchRegion.Width * 3 / 4, matchRegion.Y + matchRegion.Height * 3 / 4)
        };

        bool allWhite = samplePoints.All(pt =>
        {
            var v = captureRa.SrcMat.At<Vec3b>(pt.Y, pt.X);
            return IsWhite(v.Item2, v.Item1, v.Item0);
        });

        return !allWhite && !IsInRevivePrompt(captureRa);
    }

    /// <summary>
    /// 在任意可以关闭的UI界面（识别关闭按钮）
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInAnyClosableUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapCloseButton", captureRa));
        return ra.IsExist();
    }

    /// <summary>
    /// 是否在队伍选择界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInPartyViewUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(ElementRecognition.Get("PartyBtnChooseView", captureRa));
        return ra.IsExist();
    }

    /// <summary>
    /// 等待队伍选择界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitForPartyViewUi(CancellationToken ct, int retryTimes = 5)
    {
        return await NewRetry.WaitForAction(() =>
        {
            using var ra = TaskControl.CaptureToRectArea();
            return IsInPartyViewUi(ra);
        }, ct, retryTimes);
    }

    /// <summary>
    /// 是否在大地图界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInBigMapUi(ImageRegion captureRa)
    {
        using var scaleRa = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapScaleButton", captureRa));
        if (scaleRa.IsExist())
        {
            return true;
        }

        using var settingsRa = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapSettingsButton", captureRa));
        return settingsRa.IsExist();
    }

    /// <summary>
    /// 大地图界面是否在地底
    /// 鼠标悬浮在地下图标或者处于切换动画的时候可能会误识别
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool BigMapIsUnderground(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("QuickTeleport", "MapUndergroundSwitchButton", captureRa));
        return ra.IsExist();
    }

    public static double GetBigMapScale(ImageRegion region)
    {
        using var scaleRa = region.Find(QuickTeleportAssets.Get(region).MapScaleButtonRo);
        // using var scaleRa = region.Find(RecognitionAssets.Get("QuickTeleport", "MapScaleButton", region));
        if (scaleRa.IsEmpty())
        {
            throw new Exception("当前未处于大地图界面，不能使用GetBigMapScale方法");
        }

        // 原先这里的起止区间和config里写死的值差1
        var start = TaskContext.Instance().Config.TpConfig.ZoomStartY;
        var end = TaskContext.Instance().Config.TpConfig.ZoomEndY;
        if (end <= start)
        {
            throw new InvalidOperationException($"大地图缩放区间配置无效：start={start}, end={end}");
        }

        var cur = (scaleRa.Y + scaleRa.Height / 2.0) * TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio; // 转换到1080p坐标系,主要是小于1080p的情况
        var normalizedScale = (end - cur) / (end - start);
        // [缩放坐标诊断] 只读日志，不改任何计算，用于定位 2K/4K 缩放识别偏移根因。
        TaskControl.Logger.LogDebug(
            "[缩放坐标诊断] region={RegionW}x{RegionH} scaleRa.Y={RawY:0.0} ZoomOutMax1080PRatio={ZoomOutRatio:0.000} ScaleTo1080PRatio={ScaleRatio:0.000} cur={Cur:0.0} normalizedScale={Ns:0.000} zoom={Zoom:0.00}",
            region.Width, region.Height, scaleRa.Y + scaleRa.Height / 2.0,
            TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio, TaskContext.Instance().SystemInfo.ScaleTo1080PRatio,
            cur, normalizedScale, (-5 * normalizedScale) + 6);
        if (!double.IsFinite(normalizedScale))
        {
            throw new InvalidOperationException($"大地图缩放识别结果无效：start={start}, end={end}, current={cur}");
        }

        // 0 和 1 都是合法的边界值：滑块在最下方时 normalizedScale=0，对应最终缩放等级 6。
        return Math.Clamp(normalizedScale, 0.0, 1.0);
    }

    public static MotionStatus GetMotionStatus(ImageRegion captureRa)
    {
        using var spaceRa = captureRa.Find(ElementRecognition.Get("SpaceKey", captureRa));
        var spaceExist = spaceRa.IsExist();
        using var xRa = captureRa.Find(ElementRecognition.Get("XKey", captureRa));
        var xExist = xRa.IsExist();
        if (spaceExist)
        {
            return xExist ? MotionStatus.Climb : MotionStatus.Fly;
        }
        else
        {
            return MotionStatus.Normal;
        }
    }

    /// <summary>
    /// 是否出现复苏提示
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    internal static bool IsInRevivePrompt(ImageRegion region)
    {
        using var confirmRectArea = region.Find(RecognitionAssets.Get("AutoFight", "Confirm", region));
        if (!confirmRectArea.IsEmpty())
        {
            var list = region.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(0, 0, region.Width, region.Height / 2)
            });

            CultureInfo cultureInfo = new CultureInfo(TaskContext.Instance().Config.OtherConfig.GameCultureInfoName);
            IStringLocalizer stringLocalizer = App.GetService<IStringLocalizer<BvResxHelper>>() ?? throw new Exception();
            string revival = stringLocalizer.WithCultureGet(cultureInfo, "复苏");
            if (list.Any(r => r.Text.Contains(revival)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 是否出现全队死亡和复苏提示
    /// </summary>
    /// <param name="region"></param>
    /// <returns></returns>
    public static bool ClickIfInReviveModal(ImageRegion region)
    {
        var list = region.FindMulti(new RecognitionObject
        {
            RecognitionType = RecognitionTypes.Ocr,
            RegionOfInterest = new Rect(0, region.Height / 4 * 3, region.Width, region.Height / 4)
        });
        using var r = list.FirstOrDefault(r => r.Text.Contains("复苏"));
        if (r != null)
        {
            r.Click();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 当前角色是否低血量
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool CurrentAvatarIsLowHp(ImageRegion captureRa)
    {
        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;

        // 获取 (808, 1010) 位置的像素颜色
        var pixelColor = captureRa.SrcMat.At<Vec3b>((int)(1010 * assetScale), (int)(808 * assetScale));

        // 判断颜色是否是 (255, 90, 90)
        return pixelColor is { Item2: 255, Item1: 90, Item0: 90 };
    }

    /// <summary>
    /// 在空月祝福界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInBlessingOfTheWelkinMoon(ImageRegion captureRa)
    {
        var ra = captureRa;

        using var girlRa = ra.Find(RecognitionAssets.Get("GameLoading", "GirlMoon", ra));
        if (girlRa.IsExist())
        {
            return true;
        }

        using var moonRa = ra.Find(RecognitionAssets.Get("GameLoading", "WelkinMoon", ra));
        return moonRa.IsExist();
    }

    /// <summary>
    /// 是否在对话界面
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInTalkUi(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(RecognitionAssets.Get("AutoSkip", "DisabledUiButton", captureRa.Width, captureRa.Height));
        return ra.IsExist();
    }

    /// <summary>
    /// 等到对话界面加载完成
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="retryTimes"></param>
    /// <returns></returns>
    public static async Task<bool> WaitAndSkipForTalkUi(CancellationToken ct, int retryTimes = 5)
    {
        return await NewRetry.WaitForAction(() =>
        {
            using var ra = TaskControl.CaptureToRectArea();
            return IsInTalkUi(ra);
        }, ct, retryTimes, 500);
    }

    /// <summary>
    /// 是否存在提示框/确认框
    /// 黑白款都能识别
    /// </summary>
    /// <param name="captureRa"></param>
    /// <returns></returns>
    public static bool IsInPromptDialog(ImageRegion captureRa)
    {
        using var ra = captureRa.Find(ElementRecognition.Get("PromptDialogLeftBottomStar", captureRa));
        return ra.IsExist();
    }
    
    
        
    /// <summary>
    /// 通过 OCR 识别当前角色的 UID
    /// </summary>
    /// <returns>UID 数字，如果识别失败则返回 0</returns>
    public static int Uid()
    {
        try
        {
            var x = 1683;
            var y = 1051;
            var width = 234;
            var height = 28;
            var scale = TaskContext.Instance().SystemInfo.AssetScale;
            
            x = (int)Math.Round(x * scale);
            y = (int)Math.Round(y * scale);
            width = (int)Math.Round(width * scale);
            height = (int)Math.Round(height * scale);
            
            using var region = TaskControl.CaptureToRectArea();
            var recognitionObjectOcr = RecognitionObject.Ocr(x, y, width, height);
            var res = region.Find(recognitionObjectOcr);
            if (res?.Text == null) return 0;
            var matches = Regex.Matches(res.Text, @"\d+");
            if (matches.Count == 0) return 0;
            var numberStr = string.Join("", matches.Select(m => m.Value));
            return int.TryParse(numberStr, out var uid) ? uid : 0;
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogError(e, "OCR 识别 UID 异常");
            return 0;
        }
    }
}

public enum MotionStatus
{
    Normal, // 正常
    Fly, // 飞行
    Climb, // 攀爬
}
