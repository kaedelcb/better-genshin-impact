using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using System.Collections.Generic;
using System.Linq;
using System;
using Newtonsoft.Json.Linq;
using BetterGenshinImpact.GameTask.Common.Element.Assets;


namespace BetterGenshinImpact.GameTask.Common.Map.Maps;

/// <summary>
/// 旧日之海
/// </summary>
public class SeaOfBygoneErasMap : SceneBaseMap
{
    #region 地图参数

    static readonly int GameMapRows = 3; // 游戏坐标下地图块的行数
    static readonly int GameMapCols = 4; // 游戏坐标下地图块的列数
    static readonly int GameMapUpRows = 2; // 游戏坐标下 左上角离地图原点的行数(注意原点在块的右下角)
    static readonly int GameMapLeftCols = 5; // 游戏坐标下 左上角离地图原点的列数(注意原点在块的右下角)

    #endregion 地图参数

    static readonly int SeaOfBygoneErasMapImageBlockWidth = 1024;

    private static Mat TeleportTemplate;
    private static Mat TeleportTemplateMask;
    private List<Point> MapTeleports;

    static SeaOfBygoneErasMap()
    {
        Mat img = GameTaskManager.LoadAssetImage("QuickTeleport", "TeleportTransparentBackground.png", ImreadModes.Unchanged);

        TeleportTemplate = new Mat();
        Cv2.CvtColor(img, TeleportTemplate, ColorConversionCodes.BGRA2GRAY);

        Mat[] channels = Cv2.Split(img);
        TeleportTemplateMask = channels[3];
    }

    public SeaOfBygoneErasMap() : base(type: MapTypes.SeaOfBygoneEras,
        mapSize: new Size(GameMapCols * SeaOfBygoneErasMapImageBlockWidth, GameMapRows * SeaOfBygoneErasMapImageBlockWidth),
        mapOriginInImageCoordinate: new Point2f((GameMapLeftCols + 1) * SeaOfBygoneErasMapImageBlockWidth, (GameMapUpRows + 1) * SeaOfBygoneErasMapImageBlockWidth),
        mapImageBlockWidth: SeaOfBygoneErasMapImageBlockWidth,
        splitRow: 0,
        splitCol: 0)
    {
        ExtractAndSaveFeature(Global.Absolute("Assets/Map/SeaOfBygoneEras/SeaOfBygoneEras_0_1024.png"));
        ExtractAndSaveFeature(Global.Absolute("Assets/Map/SeaOfBygoneEras/SeaOfBygoneEras_-1_1024.webp"));
        ExtractAndSaveFeature(Global.Absolute("Assets/Map/SeaOfBygoneEras/SeaOfBygoneEras_-2_1024.webp"));
        Layers = BaseMapLayer.LoadLayers(this);

        MapTeleports = MapLazyAssets.Get().ScenesDic["SeaOfBygoneEras"].Points.
            Where(i => i.Type == "TeleportWaypoint").
            Select(i => ConvertGenshinMapCoordinatesToImageCoordinates(new Point2f((float)i.TranX, (float)i.TranY))).
            Select(i => new Point(i.X, i.Y)).OrderBy(i => i.X).ThenBy(i => i.Y).ToList();
    }

    public override Point2f GetBigMapPosition(Mat greyBigMapMat)
    {
        var rect = GetBigMapRectByTeleports(greyBigMapMat);
        if (rect != default)
        {
            return rect.GetCenterPoint();
        }

        return base.GetBigMapPosition(greyBigMapMat);
    }

    public override Rect GetBigMapRect(Mat greyBigMapMat)
    {
        var rect = GetBigMapRectByTeleports(greyBigMapMat);
        if (rect != default)
        {
            return rect;
        }

        return base.GetBigMapRect(greyBigMapMat);
    }

    /// <summary>
    /// 模板匹配 + 非极大抑制：在灰度大地图截图上找出所有传送点图标的"原始匹配点"（模板左上角，未 +12 居中）。
    /// 由几何拟合定位（GetBigMapRectByTeleports）与点击纹理吸附（DetectTeleportScreenPixels）共用，
    /// 保证两条路径的检测口径逐字节一致。纯检测、无副作用、可重复调用（CQS 查询）。
    /// </summary>
    private static List<Point> DetectTeleportRawPoints(Mat greyBigMapMat)
    {
        // It's fine to miss some, but definitely no false positive results.
        const double threshold = 0.99;
        using Mat result = new Mat();
        Cv2.MatchTemplate(greyBigMapMat, TeleportTemplate, result, TemplateMatchModes.CCorrNormed, TeleportTemplateMask);

        var teleportPoints = new List<Point>();

        // Step 1: Get all teleport positions from current screenshot
        for (int i = 1; i < result.Rows - 1; ++i)
        {
            for (int j = 1; j < result.Cols - 1; ++j)
            {
                float val = result.At<float>(i, j);

                if (val > threshold)
                {
                    if (val >= result.At<float>(i - 1, j - 1) &&
                        val >= result.At<float>(i - 1, j) &&
                        val >= result.At<float>(i - 1, j + 1) &&
                        val >= result.At<float>(i, j - 1) &&
                        val >= result.At<float>(i, j + 1) &&
                        val >= result.At<float>(i + 1, j - 1) &&
                        val >= result.At<float>(i + 1, j) &&
                        val >= result.At<float>(i + 1, j + 1))
                    {
                        var newPoint = new Point(j, i);
                        bool tooClose = false;
                        foreach (var p in teleportPoints)
                        {
                            if (p.DistanceTo(newPoint) < 50)
                            {
                                tooClose = true;
                                break;
                            }
                        }
                        if (!tooClose)
                        {
                            teleportPoints.Add(newPoint);
                        }
                    }
                }
            }
        }

        return teleportPoints;
    }

    private Rect GetBigMapRectByTeleports(Mat greyBigMapMat)
    {
        var teleportPoints = DetectTeleportRawPoints(greyBigMapMat);

        if (teleportPoints.Count < 2)
        {
            // [定位诊断] 屏幕上可见传送点 < 2，无法反推矩形（放大档最易发生）。
            // 独立地图靠可见传送点数量反推 bigMapInAllMapRect，可见点越少 scale 越不稳、落点越易偏。
            TaskControl.Logger.LogDebug("[旧日之海定位] 可见传送点={Count} (<2)，本次矩形识别失败，走 SIFT 兜底", teleportPoints.Count);
            return default;
        }
        teleportPoints = teleportPoints.Select(i => new Point(i.X + 12, i.Y + 12)).
            OrderBy(i => i.X).ThenBy(i => i.Y).ToList();
        /*
        foreach (var p in teleportPoints) {
            Logger.LogInformation("Teleport point: {a}", p);
        }
        Logger.LogInformation("Total telepoints: {c}", teleportPoints.Count);
        */

        Func<Point, Point, double> GetAngleOfTwoPoints = (p0, p1) =>
        {
            double deltaX = p0.X - p1.X;
            int deltaY = p0.Y - p1.Y;
            if (deltaY == 0) { return 90; }
            var val = Math.Atan(deltaX / deltaY);
            return val / Math.PI * 180;
        };

        // Step 2: find a diagonal determined by two of the teleports, let's call these two teleports reference points
        Point rp0 = new Point();
        Point rp1 = new Point();
        double refAngle = 0.0;
        {
            double minAngleDiff = 180;
            for (int i = 0; i < teleportPoints.Count; ++i)
            {
                for (int j = i + 1; j < teleportPoints.Count; ++j)
                {
                    var p0 = teleportPoints[i];
                    var p1 = teleportPoints[j];
                    var angle = GetAngleOfTwoPoints(p0, p1);
                    if (Math.Abs(Math.Abs(angle) - 45) < minAngleDiff)
                    {
                        rp0 = p0;
                        rp1 = p1;
                        minAngleDiff = Math.Abs(Math.Abs(angle) - 45);
                    }
                }
            }
            refAngle = GetAngleOfTwoPoints(rp0, rp1);
            // Logger.LogInformation("Reference points {a} and {b}, Angle {c}", rp0, rp1, refAngle);
        }

        {
            /*
            var debugMat = new Mat();
            Cv2.CvtColor(greyBigMapMat, debugMat, ColorConversionCodes.GRAY2BGR);
            foreach (var p in teleportPoints)
            {
                Cv2.DrawMarker(debugMat, p, new Scalar(255, 0, 0), MarkerTypes.Cross, 20, 2);
            }
            Cv2.DrawMarker(debugMat, rp0, new Scalar(0, 255, 0), MarkerTypes.TriangleUp, 20, 2);
            Cv2.DrawMarker(debugMat, rp1, new Scalar(0, 255, 0), MarkerTypes.TriangleUp, 20, 2);
            Cv2.ImWrite(((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds().ToString() + ".png", debugMat);
            */
        }

        // Step 3: For all diagonals determined by pairs of teleports on this map.
        var minDeviation = double.MaxValue;
        var transformParamScale = 0.0;
        var transformParamDeltaX = 0.0;
        var transformParamDeltaY = 0.0;
        for (int i = 0; i < MapTeleports.Count; ++i)
        {
            for (int j = i + 1; j < MapTeleports.Count; ++j)
            {
                var mp0 = MapTeleports[i];
                var mp1 = MapTeleports[j];
                var angle = GetAngleOfTwoPoints(mp0, mp1);
                if (Math.Abs(angle - refAngle) < 5)
                {
                    // Step 4: Assuming this pair corresponds to the reference points
                    var mpDist = mp0.DistanceTo(mp1);
                    var rpDist = rp0.DistanceTo(rp1);
                    var scale = mpDist / rpDist;
                    var deltaX = mp0.X - rp0.X * scale;
                    var deltaY = mp0.Y - rp0.Y * scale;

                    Func<Point, Point> transformPoint = i =>
                    {
                        return new Point(i.X * scale + deltaX, i.Y * scale + deltaY);
                    };

                    var transformedPoints = teleportPoints.Select(i => transformPoint(i)).ToList();
                    // Step 5: Check how close the fit is
                    double totalDeviation = 0;
                    foreach (var p in transformedPoints)
                    {
                        var minDist = MapTeleports.Select(i => i.DistanceTo(p)).Min();
                        totalDeviation += minDist;
                    }
                    if (totalDeviation < minDeviation)
                    {
                        minDeviation = totalDeviation;
                        transformParamScale = scale;
                        transformParamDeltaX = deltaX;
                        transformParamDeltaY = deltaY;
                    }
                }
            }
        }
        // [最小二乘精化] 上面的相似变换(scale+平移，无旋转)仅由 2 个参照点(rp0,rp1↔mp0,mp1)外推，
        //   对这两点的像素噪声极敏感（实测 scale 在 0.68~0.81 抖、残差逼近拒绝线）。尤其"站在目标传送点上"时，
        //   大地图以玩家为中心、目标图标正好被玩家箭头遮住检测不到，2 点拟合缺正中心锚点 → 矩形整体偏移
        //   → 落点偏出目标数百像素点在空白（"选项列表不存在传送点"）。
        //   这里用"当前检测到的全部传送点"对最佳变换做一次最小二乘精化：以 2 点变换建立 screen↔map 内点对应，
        //   对所有内点闭式重解 各向同性 scale + 平移。10+ 点铺满全屏、约束强，少一个中心点几乎无影响，
        //   矩形不再歪、落点回到目标真实屏幕位置（箭头底下的点击热区仍响应）。
        //   仅在精化后残差 ≤ 原残差时才采用（防内点误配把结果带偏）→ 最坏 no-op、零回归。纯几何、无副作用。
        if (minDeviation < double.MaxValue)
        {
            const double inlierThreshold = 60.0; // map 图像坐标系下判定"屏幕点↔地图点"为同一点的最大距离
            var pairs = new List<(Point s, Point m)>();
            foreach (var s in teleportPoints)
            {
                var tp = new Point(s.X * transformParamScale + transformParamDeltaX, s.Y * transformParamScale + transformParamDeltaY);
                Point nearest = default;
                double nd = double.MaxValue;
                foreach (var m in MapTeleports)
                {
                    double d = m.DistanceTo(tp);
                    if (d < nd) { nd = d; nearest = m; }
                }
                if (nd < inlierThreshold) { pairs.Add((s, nearest)); }
            }

            if (pairs.Count >= 3)
            {
                double sxBar = pairs.Average(p => (double)p.s.X);
                double syBar = pairs.Average(p => (double)p.s.Y);
                double mxBar = pairs.Average(p => (double)p.m.X);
                double myBar = pairs.Average(p => (double)p.m.Y);
                double num = 0, den = 0;
                foreach (var (s, m) in pairs)
                {
                    double dsx = s.X - sxBar, dsy = s.Y - syBar;
                    double dmx = m.X - mxBar, dmy = m.Y - myBar;
                    num += dsx * dmx + dsy * dmy;
                    den += dsx * dsx + dsy * dsy;
                }
                if (den > 1e-6)
                {
                    double lsScale = num / den;
                    double lsDeltaX = mxBar - lsScale * sxBar;
                    double lsDeltaY = myBar - lsScale * syBar;
                    double lsDeviation = 0;
                    foreach (var s in teleportPoints)
                    {
                        var tp = new Point(s.X * lsScale + lsDeltaX, s.Y * lsScale + lsDeltaY);
                        lsDeviation += MapTeleports.Select(m => m.DistanceTo(tp)).Min();
                    }
                    if (lsScale > 0 && lsDeviation <= minDeviation)
                    {
                        TaskControl.Logger.LogDebug(
                            "[旧日之海定位] 最小二乘精化：内点={N} scale {S0:0.0000}->{S1:0.0000} 残差 {D0:0.0}->{D1:0.0}",
                            pairs.Count, transformParamScale, lsScale, minDeviation, lsDeviation);
                        transformParamScale = lsScale;
                        transformParamDeltaX = lsDeltaX;
                        transformParamDeltaY = lsDeltaY;
                        minDeviation = lsDeviation;
                    }
                }
            }
        }

        // Logger.LogInformation("Min deviation: {d}", minDeviation);
        if (minDeviation < 200)
        {
            Func<Point, Point> transformPoint = i =>
            {
                return new Point(i.X * transformParamScale + transformParamDeltaX, i.Y * transformParamScale + transformParamDeltaY);
            };
            var pTopLeft = transformPoint(new Point(0, 0));
            var pBottomRight = transformPoint(new Point(greyBigMapMat.Width, greyBigMapMat.Height));
            // Logger.LogInformation("Rect: {a}, {b}", pTopLeft, pBottomRight);
            // [定位诊断] 矩形识别成功。minDeviation 是拟合残差：越接近 200 越勉强（落点越可能偏），
            //   可见传送点越少残差越易偏大。偶发误点复现时看这里的 count 与 minDeviation。
            TaskControl.Logger.LogDebug(
                "[旧日之海定位] 矩形识别成功 可见传送点={Count} 拟合残差minDeviation={Dev:0.0} scale={Scale:0.0000}",
                teleportPoints.Count, minDeviation, transformParamScale);
            return new Rect(pTopLeft.X, pTopLeft.Y, pBottomRight.X - pTopLeft.X, pBottomRight.Y - pTopLeft.Y);
        }

        // [定位诊断] 拟合残差超阈值(≥200)，矩形识别失败，走 SIFT 兜底。残差过大说明可见传送点分布/数量不足以稳定定标。
        TaskControl.Logger.LogDebug(
            "[旧日之海定位] 矩形识别失败 可见传送点={Count} 拟合残差minDeviation={Dev:0.0} (≥200)，走 SIFT 兜底",
            teleportPoints.Count, minDeviation);
        return default;
    }

}
