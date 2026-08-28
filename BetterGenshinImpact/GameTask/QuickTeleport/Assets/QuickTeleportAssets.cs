using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System.Collections.Generic;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Helpers.Extensions;
using System;
using System.Drawing;
using System.Linq;

namespace BetterGenshinImpact.GameTask.QuickTeleport.Assets;

public sealed class QuickTeleportAssets
{
    private static readonly CaptureAssetsCache<QuickTeleportAssets> Cache = new(static size => new QuickTeleportAssets(size));

    public RecognitionObject TeleportButtonRo;
    public RecognitionObject MapScaleButtonRo;
    public RecognitionObject MapCloseButtonRo;
    public RecognitionObject MapCloseButtonWhiteRo;
    public RecognitionObject MapSettingsButtonRo;
    public RecognitionObject MapChooseRo;
    public RecognitionObject MapUndergroundSwitchButtonRo;
    public RecognitionObject MapUndergroundToGroundButtonRo;
    // public RecognitionObject lyRo;

    public Rect MapChooseIconRoi { get; }
    public IReadOnlyList<RecognitionObject> MapChooseIconRoList { get; }
    public IReadOnlyList<Mat> MapChooseIconGreyMatList { get; }

    private Rect CaptureRect { get; }
    private double AssetScale { get; }

    private QuickTeleportAssets(CaptureSize captureSize)
    {
        CaptureRect = captureSize.CaptureRect;
        AssetScale = captureSize.AssetScale;

        MapChooseIconRoi = new Rect((int)(1270 * captureSize.AssetScale),
            (int)(100 * captureSize.AssetScale),
            (int)(50 * captureSize.AssetScale),
            captureSize.Height - (int)(200 * captureSize.AssetScale));
        List<RecognitionObject> mapChooseIconRoList =
        [
            BuildMapChooseIconRo("TeleportWaypoint.png", captureSize),
            BuildMapChooseIconRo("StatueOfTheSeven.png", captureSize),
            BuildMapChooseIconRo("Domain.png", captureSize),
            BuildMapChooseIconRo("Domain2.png", captureSize),
            BuildMapChooseIconRo("ObsidianTotemPole.png", captureSize),
            BuildMapChooseIconRo("PortableWaypoint.png", captureSize),
            BuildMapChooseIconRo("Mansion.png", captureSize),
            BuildMapChooseIconRo("SubSpaceWaypoint.png", captureSize),
            BuildMapChooseIconRo("NodKraiMeetingPoint.png", captureSize),
            BuildMapChooseIconRo("TabletOfTona.png", captureSize),
            BuildMapChooseIconRo("MarkTransPointMoonTower.png", captureSize),
        ];
        MapChooseIconRoList = mapChooseIconRoList.AsReadOnly();
        MapChooseIconGreyMatList = mapChooseIconRoList.ConvertAll(x => x.TemplateImageGreyMat ?? new Mat()).AsReadOnly();

        MapScaleButtonRo = new RecognitionObject
        {
            Name = "MapScaleButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapScaleButton.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect((int)(30 * AssetScale),
                (int)(440 * AssetScale),
                (int)(40 * AssetScale),
                (int)(200 * AssetScale)),
            UseMask = true,
            MaskColor = Color.FromArgb(0, 255, 0),
            DrawOnWindow = true,
            Use3Channels= true, 
            TemplateMatchMode = TemplateMatchModes.SqDiffNormed,
            Threshold = 0.95
        }.InitTemplate();

        TeleportButtonRo = new RecognitionObject
        {
            Name = "GoTeleport",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "GoTeleport.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect((int)(1440 * AssetScale),
                CaptureRect.Height - (int)(120 * AssetScale),
                (int)(100 * AssetScale),
                (int)(120 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();

        MapCloseButtonRo = new RecognitionObject
        {
            Name = "MapCloseButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapCloseButton.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(107 * AssetScale),
                (int)(19 * AssetScale),
                (int)(58 * AssetScale),
                (int)(58 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();
        
        MapCloseButtonWhiteRo = new RecognitionObject
        {
            Name = "MapCloseButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapCloseButtonWhite.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(80 * AssetScale),
                (int)(5 * AssetScale),
                (int)(70 * AssetScale),
                (int)(70 * AssetScale)),
            DrawOnWindow = true
        }.InitTemplate();

        MapSettingsButtonRo = new RecognitionObject
        {
            Name = "MapSettingsButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapSettingsButton.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect((int)(25 * AssetScale),
                (int)(990 * AssetScale),
                (int)(58 * AssetScale),
                (int)(62 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();

        MapChooseRo = new RecognitionObject
        {
            Name = "MapChoose",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapChoose.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(480 * AssetScale),
                0,
                (int)(100 * AssetScale),
                (int)(70 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();

        // 地下切换按钮
        // 由于有颜色相近的内容，所以使用3通道
        MapUndergroundSwitchButtonRo = new RecognitionObject
        {
            Name = "MapUndergroundSwitchButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapUndergroundSwitchButton.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(120 * AssetScale),
                (int)(250 * AssetScale),
                (int)(90 * AssetScale),
                (int)(570 * AssetScale)),
            DrawOnWindow = true,
            Use3Channels = true,
            UseMask = true,
            MaskColor = Color.FromArgb(0, 255, 0),
            Threshold = 0.8
        }.InitTemplate();
        MapUndergroundToGroundButtonRo = new RecognitionObject
        {
            Name = "MapUndergroundToGroundButton",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "MapUndergroundToGroundButton.png", captureSize.Width, captureSize.Height),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(120 * AssetScale),
                (int)(250 * AssetScale),
                (int)(90 * AssetScale),
                (int)(570 * AssetScale)),
            UseMask = true,
            Use3Channels = true, 
            MaskColor = Color.FromArgb(0, 255, 0),
            DrawOnWindow = true,
            Threshold = 0.85
        }.InitTemplate();
        
        // lyRo = new RecognitionObject
        // {
        //     Name = "lyRo",
        //     RecognitionType = RecognitionTypes.TemplateMatch,
        //     TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", "switch\\liyue.png", captureSize.Width, captureSize.Height),
        //     RegionOfInterest = new Rect(CaptureRect.Width * 2 / 3, 0, CaptureRect.Width / 3, CaptureRect.Height),
        //     Use3Channels = true, 
        //     DrawOnWindow = true,
        //     Threshold = 0.8
        // }.InitTemplate();
    }

    public static QuickTeleportAssets Get(GameTask.Model.Area.Region region)
    {
        return Cache.Get(region);
    }

    public static QuickTeleportAssets Get(int captureWidth, int captureHeight)
    {
        return Cache.Get(captureWidth, captureHeight);
    }

    private RecognitionObject BuildMapChooseIconRo(string name, CaptureSize captureSize)
    {
        var ro = new RecognitionObject
        {
            Name = name + "MapChooseIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("QuickTeleport", name, captureSize.Width, captureSize.Height),
            RegionOfInterest = MapChooseIconRoi,
            DrawOnWindow = false
        }.InitTemplate();

        if (name == "TeleportWaypoint.png")
        {
            ro.Threshold = 0.7;
        }

        return ro;
    }
}