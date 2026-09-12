using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

/// <summary>
/// CombatHealthDetector 像素检测的内存图像单元测试（B7 收口）。
/// 用内存 Mat 构造 1920×1080 全黑 ImageRegion，按各检测方法的采样坐标/阈值涂色，
/// 验证正例/反例/容差边界；不依赖截图、游戏窗口与鼠标模拟。
/// </summary>
public class CombatHealthDetectorTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static ImageRegion MakeRegion(Action<Mat>? paint = null)
    {
        var mat = new Mat(H, W, MatType.CV_8UC3, Scalar.All(0));   // 全黑底
        paint?.Invoke(mat);
        return new ImageRegion(mat, 0, 0);
    }

    // ---------- IsRedBlood：血条区 (808,1009,3,3) ----------
    // 通道序说明：OpenCvCommonHelper.Threshold 内部 BGR2RGB 后 InRange，
    // 故检测常量按"转换后 RGB 序"生效——真实红血像素 BGR=(90,90,255)。

    [Fact]
    public void IsRedBlood_WhenRedBloodPixelsPresent()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(808, 1009, 3, 3), new Scalar(90, 90, 255), -1));
        Assert.True(CombatHealthDetector.IsRedBlood(ra));
    }

    [Fact]
    public void IsRedBlood_WhenBlackScreen()
    {
        using var ra = MakeRegion();
        Assert.False(CombatHealthDetector.IsRedBlood(ra));
    }

    [Fact]
    public void IsRedBlood_WhenOtherColorPresent()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(808, 1009, 3, 3), new Scalar(34, 215, 150), -1));
        Assert.False(CombatHealthDetector.IsRedBlood(ra));
    }

    // ---------- IsGreenBlood：单点 (1010,814) ±15 ----------

    [Theory]
    [InlineData(34, 215, 150, true)]   // 精确命中
    [InlineData(49, 215, 150, true)]   // C0 通道边界 +15（含）
    [InlineData(50, 215, 150, false)]  // C0 通道 +16（外）
    [InlineData(19, 215, 150, true)]   // C0 通道边界 -15（含）
    [InlineData(34, 199, 150, false)]  // C1 通道 +16（外）
    public void IsGreenBlood_ToleranceBoundaries(byte c0, byte c1, byte c2, bool expected)
    {
        using var ra = MakeRegion(m => m.Set(1010, 814, new Vec3b(c0, c1, c2)));
        Assert.Equal(expected, CombatHealthDetector.IsGreenBlood(ra));
    }

    // ---------- HasNutritionBag：(1817,781,4,14) ----------

    [Fact]
    public void HasNutritionBag_WhenGreenMarkPresent()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1817, 781, 4, 14), new Scalar(102, 233, 192), -1));
        Assert.True(CombatHealthDetector.HasNutritionBag(ra));
    }

    [Fact]
    public void HasNutritionBag_WhenBlackScreen()
    {
        using var ra = MakeRegion();
        Assert.False(CombatHealthDetector.HasNutritionBag(ra));
    }

    // ---------- IsCharacterDead / GetDeadCharacterSlots ----------

    [Fact]
    public void IsCharacterDead_WhenSlotFilledWithMidGray()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1797, 249, 8, 3), new Scalar(100, 100, 100), -1));
        Assert.True(CombatHealthDetector.IsCharacterDead(ra, 0));
    }

    [Fact]
    public void IsCharacterDead_WhenPureBlack()   // 亮度 <30 排除
    {
        using var ra = MakeRegion();
        Assert.False(CombatHealthDetector.IsCharacterDead(ra, 0));
    }

    [Fact]
    public void IsCharacterDead_WhenPureWhite()   // 亮度 >200 排除
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1797, 249, 8, 3), new Scalar(255, 255, 255), -1));
        Assert.False(CombatHealthDetector.IsCharacterDead(ra, 0));
    }

    [Fact]
    public void IsCharacterDead_WhenColoredPixels()   // 三通道不等排除
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1797, 249, 8, 3), new Scalar(100, 100, 110), -1));
        Assert.False(CombatHealthDetector.IsCharacterDead(ra, 0));
    }

    [Fact]
    public void IsCharacterDead_RespectsSlotSpacing()   // 槽位 3 的 y = 249 + 96*3
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1797, 249 + 96 * 3, 8, 3), new Scalar(100, 100, 100), -1));
        Assert.True(CombatHealthDetector.IsCharacterDead(ra, 3));
        Assert.False(CombatHealthDetector.IsCharacterDead(ra, 0));
    }

    [Fact]
    public void GetDeadCharacterSlots_ReturnsOnlyDeadSlots()
    {
        using var ra = MakeRegion(m =>
        {
            Cv2.Rectangle(m, new Rect(1797, 249, 8, 3), new Scalar(100, 100, 100), -1);           // 槽 0 死亡
            Cv2.Rectangle(m, new Rect(1797, 249 + 96 * 2, 8, 3), new Scalar(100, 100, 100), -1);  // 槽 2 死亡
        });
        Assert.Equal(new[] { 0, 2 }, CombatHealthDetector.GetDeadCharacterSlots(ra));
    }

    // ---------- IsResurrectionDrug：单点 (785,1818) 纯白 ----------

    [Fact]
    public void IsResurrectionDrug_WhenPureWhite()
    {
        using var ra = MakeRegion(m => m.Set(785, 1818, new Vec3b(255, 255, 255)));
        Assert.True(CombatHealthDetector.IsResurrectionDrug(ra));
    }

    [Fact]
    public void IsResurrectionDrug_WhenNotWhite()
    {
        using var ra = MakeRegion(m => m.Set(785, 1818, new Vec3b(200, 255, 255)));
        Assert.False(CombatHealthDetector.IsResurrectionDrug(ra));
    }

    // ---------- IsPaimonVisible：单点 (32,67) ±10 ----------

    [Theory]
    [InlineData(143, 196, 233, true)]  // 精确命中
    [InlineData(153, 206, 243, true)]  // 全通道边界 +10（含）
    [InlineData(154, 196, 233, false)] // C0 +11（外）
    public void IsPaimonVisible_ToleranceBoundaries(byte c0, byte c1, byte c2, bool expected)
    {
        using var ra = MakeRegion(m => m.Set(32, 67, new Vec3b(c0, c1, c2)));
        Assert.Equal(expected, CombatHealthDetector.IsPaimonVisible(ra));
    }

    // ---------- IsSlotGreenBlood（原 IsSlotRedBlood 改名）：(1694, 267+96n, 3,24) ----------

    [Fact]
    public void IsSlotGreenBlood_WhenSlotBloodGreen()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1694, 267, 3, 24), new Scalar(47, 217, 155), -1));
        Assert.True(CombatHealthDetector.IsSlotGreenBlood(ra, 0));
    }

    [Fact]
    public void IsSlotGreenBlood_WhenSlotBloodRed()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1694, 267, 3, 24), new Scalar(90, 90, 255), -1));
        Assert.False(CombatHealthDetector.IsSlotGreenBlood(ra, 0));
    }

    [Fact]
    public void IsSlotGreenBlood_RespectsSlotSpacing()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1694, 267 + 96 * 2, 3, 24), new Scalar(47, 217, 155), -1));
        Assert.True(CombatHealthDetector.IsSlotGreenBlood(ra, 2));
        Assert.False(CombatHealthDetector.IsSlotGreenBlood(ra, 0));
    }

    // ---------- IsSlotActive：(1859, 264+96n, 3,3) 纯白 ----------

    [Fact]
    public void IsSlotActive_WhenPureWhite()
    {
        using var ra = MakeRegion(m => Cv2.Rectangle(m, new Rect(1859, 264, 3, 3), new Scalar(255, 255, 255), -1));
        Assert.True(CombatHealthDetector.IsSlotActive(ra, 0));
    }

    [Fact]
    public void IsSlotActive_WhenBlack()
    {
        using var ra = MakeRegion();
        Assert.False(CombatHealthDetector.IsSlotActive(ra, 0));
    }

    // ---------- IsPixelSimilar：纯函数 ----------

    [Fact]
    public void IsPixelSimilar_WithinThreshold()
    {
        Assert.True(CombatHealthDetector.IsPixelSimilar(new Vec3b(10, 20, 30), new Vec3b(20, 30, 40), 10));
        Assert.False(CombatHealthDetector.IsPixelSimilar(new Vec3b(10, 20, 30), new Vec3b(21, 30, 40), 10));
        Assert.True(CombatHealthDetector.IsPixelSimilar(new Vec3b(10, 20, 30), new Vec3b(10, 20, 30)));
    }
}
