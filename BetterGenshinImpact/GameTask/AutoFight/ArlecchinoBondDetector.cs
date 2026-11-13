using System;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal static class ArlecchinoBondDetector
{
    // 保留既有取色与参数方向：QiKong 越大，越靠血条左侧取样，要求的契量越高。
    private static readonly Scalar LowerRgb = new(252, 120, 120);
    private static readonly Scalar UpperRgb = new(255, 160, 160);

    public static bool HasBond(Mat image, int qiKong)
    {
        var width = Math.Clamp(220 - Math.Clamp(qiKong, 0, 220), 1, 220);
        using var crop = new Mat(image, new Rect(840, 1000, width, 20));
        using var rgb = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(crop, rgb, ColorConversionCodes.BGR2RGB);
        Cv2.InRange(rgb, LowerRgb, UpperRgb, mask);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            PixelConnectivity.Connectivity4, MatType.CV_32S);

        // 连续色带本身就能证明有契；不能要求它断成两块，更不能把两个散点当作有契。
        var minWidth = Math.Min(6, width);
        var minArea = Math.Min(12, width * 2);
        for (var label = 1; label < count; label++)
        {
            if (stats.At<int>(label, (int)ConnectedComponentsTypes.Width) >= minWidth &&
                stats.At<int>(label, (int)ConnectedComponentsTypes.Area) >= minArea)
            {
                return true;
            }
        }

        return false;
    }
}
