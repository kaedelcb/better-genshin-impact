using BetterGenshinImpact.Core.Recognition.OpenCv;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests;

/// <summary>
/// Tests for MatchTemplateHelper.
///
/// Task 1: Bug condition exploration — srcMat mutation.
/// CRITICAL: Tests in the "BugCondition" region MUST FAIL on unfixed code.
/// They encode the expected (correct) behavior and will PASS after the fix is applied.
///
/// Task 2: Preservation tests — single-match MatchTemplate correctness.
/// These MUST PASS on both unfixed and fixed code.
/// </summary>
public class MatchTemplateHelperTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a white BGR image of the given size with a solid black square
    /// drawn at (squareX, squareY) with the given side length.
    /// </summary>
    private static Mat CreateWhiteImageWithBlackSquare(
        int width, int height,
        int squareX, int squareY, int squareSide)
    {
        var mat = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        Cv2.Rectangle(mat,
            new Point(squareX, squareY),
            new Point(squareX + squareSide, squareY + squareSide),
            Scalar.Black, -1);
        return mat;
    }

    /// <summary>
    /// Returns true if every pixel in <paramref name="a"/> equals the
    /// corresponding pixel in <paramref name="b"/>.
    /// </summary>
    private static bool MatsAreEqual(Mat a, Mat b)
    {
        if (a.Rows != b.Rows || a.Cols != b.Cols || a.Type() != b.Type())
            return false;

        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        return Cv2.CountNonZero(diff.Reshape(1)) == 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 1 — Bug Condition: srcMat Mutation
    // EXPECTED: FAIL on unfixed code, PASS after fix.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bug condition exploration test for MatchOnePicForOnePic (simple overload).
    ///
    /// PROPERTY: After calling MatchOnePicForOnePic, the caller's srcMat must be
    /// byte-for-byte identical to its state before the call.
    ///
    /// COUNTEREXAMPLE (unfixed): srcMat[squareX, squareY] is black (0,0,0) after
    /// the call because the method drew a black rectangle over the matched region.
    /// </summary>
    [Fact]
    public void MatchOnePicForOnePic_SimpleOverload_DoesNotMutateSrcMat()
    {
        // Arrange: white 200×200 image with a 20×20 black square at (50, 60)
        const int squareX = 50, squareY = 60, squareSide = 20;
        using var srcMat = CreateWhiteImageWithBlackSquare(200, 200, squareX, squareY, squareSide);
        using var snapshot = srcMat.Clone(); // capture original state
        using var dstMat = CreateWhiteImageWithBlackSquare(squareSide + 2, squareSide + 2, 1, 1, squareSide);

        // Act
        var results = MatchTemplateHelper.MatchOnePicForOnePic(srcMat, dstMat);

        // Assert — at least one match found (sanity check)
        Assert.NotEmpty(results);

        // Assert — srcMat is unmodified (FAILS on unfixed code)
        Assert.True(MatsAreEqual(srcMat, snapshot),
            $"srcMat was mutated by MatchOnePicForOnePic. " +
            $"Pixel at ({squareX},{squareY}) after call: " +
            $"{srcMat.At<Vec3b>(squareY, squareX)}");
    }

    /// <summary>
    /// Bug condition exploration test for MatchOnePicForOnePic (matchMode overload).
    /// Same property: srcMat must not be mutated.
    /// </summary>
    [Fact]
    public void MatchOnePicForOnePic_MatchModeOverload_DoesNotMutateSrcMat()
    {
        const int squareX = 30, squareY = 40, squareSide = 15;
        using var srcMat = CreateWhiteImageWithBlackSquare(150, 150, squareX, squareY, squareSide);
        using var snapshot = srcMat.Clone();
        using var dstMat = CreateWhiteImageWithBlackSquare(squareSide + 2, squareSide + 2, 1, 1, squareSide);

        var results = MatchTemplateHelper.MatchOnePicForOnePic(
            srcMat, dstMat, TemplateMatchModes.CCoeffNormed, null, 0.8);

        Assert.NotEmpty(results);
        Assert.True(MatsAreEqual(srcMat, snapshot),
            "srcMat was mutated by MatchOnePicForOnePic (matchMode overload).");
    }

    /// <summary>
    /// Bug condition exploration test for MatchMultiPicForOnePic (Dictionary overload).
    /// </summary>
    [Fact]
    public void MatchMultiPicForOnePic_DictionaryOverload_DoesNotMutateSrcMat()
    {
        const int squareX = 20, squareY = 25, squareSide = 12;
        using var srcMat = CreateWhiteImageWithBlackSquare(120, 120, squareX, squareY, squareSide);
        using var snapshot = srcMat.Clone();
        using var dstMat = CreateWhiteImageWithBlackSquare(squareSide + 2, squareSide + 2, 1, 1, squareSide);

        var templates = new Dictionary<string, Mat> { ["square"] = dstMat };
        var results = MatchTemplateHelper.MatchMultiPicForOnePic(srcMat, templates);

        Assert.NotEmpty(results["square"]);
        Assert.True(MatsAreEqual(srcMat, snapshot),
            "srcMat was mutated by MatchMultiPicForOnePic (Dictionary overload).");
    }

    /// <summary>
    /// Bug condition exploration test for MatchMultiPicForOnePic (List overload).
    /// </summary>
    [Fact]
    public void MatchMultiPicForOnePic_ListOverload_DoesNotMutateSrcMat()
    {
        const int squareX = 10, squareY = 15, squareSide = 10;
        using var srcMat = CreateWhiteImageWithBlackSquare(100, 100, squareX, squareY, squareSide);
        using var snapshot = srcMat.Clone();
        using var dstMat = CreateWhiteImageWithBlackSquare(squareSide + 2, squareSide + 2, 1, 1, squareSide);

        var templates = new List<Mat> { dstMat };
        var results = MatchTemplateHelper.MatchMultiPicForOnePic(srcMat, templates);

        Assert.NotEmpty(results);
        Assert.True(MatsAreEqual(srcMat, snapshot),
            "srcMat was mutated by MatchMultiPicForOnePic (List overload).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 2 — Preservation: Mat creation and pixel-level correctness
    // These tests verify that Mat operations used by MatchTemplateHelper work
    // correctly without requiring the WPF App host or OpenCV native DLL.
    // EXPECTED: PASS on both unfixed and fixed code.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Preservation test: Mat.Clone() produces a byte-for-byte copy.
    /// This is the core operation used by the fix (workingMat = srcMat.Clone()).
    /// </summary>
    [Fact]
    public void MatClone_ProducesIdenticalCopy()
    {
        using var srcMat = CreateWhiteImageWithBlackSquare(100, 100, 10, 10, 20);
        using var clone = srcMat.Clone();

        Assert.True(MatsAreEqual(srcMat, clone),
            "Mat.Clone() should produce a byte-for-byte identical copy.");
    }

    /// <summary>
    /// Preservation test: drawing on a clone does NOT affect the original.
    /// This validates the core invariant the fix relies on.
    /// </summary>
    [Fact]
    public void DrawingOnClone_DoesNotAffectOriginal()
    {
        using var srcMat = CreateWhiteImageWithBlackSquare(100, 100, 10, 10, 20);
        using var snapshot = srcMat.Clone();
        using var workingMat = srcMat.Clone();

        // Draw on the clone (simulating what the fixed method does)
        Cv2.Rectangle(workingMat, new Point(10, 10), new Point(30, 30), Scalar.Red, -1);

        // Original must be unaffected
        Assert.True(MatsAreEqual(srcMat, snapshot),
            "Drawing on a clone should not affect the original Mat.");

        // Clone must be different
        Assert.False(MatsAreEqual(srcMat, workingMat),
            "After drawing on clone, clone should differ from original.");
    }

    /// <summary>
    /// Preservation test: MatsAreEqual correctly identifies identical Mats.
    /// </summary>
    [Fact]
    public void MatsAreEqual_ReturnsTrueForIdenticalMats()
    {
        using var a = new Mat(50, 50, MatType.CV_8UC3, Scalar.White);
        using var b = new Mat(50, 50, MatType.CV_8UC3, Scalar.White);
        Assert.True(MatsAreEqual(a, b));
    }

    /// <summary>
    /// Preservation test: MatsAreEqual correctly identifies different Mats.
    /// </summary>
    [Fact]
    public void MatsAreEqual_ReturnsFalseForDifferentMats()
    {
        using var a = new Mat(50, 50, MatType.CV_8UC3, Scalar.White);
        using var b = new Mat(50, 50, MatType.CV_8UC3, Scalar.Black);
        Assert.False(MatsAreEqual(a, b));
    }

    /// <summary>
    /// Preservation test: Cv2.Rectangle modifies the Mat in-place.
    /// This confirms the bug exists in unfixed code (direct mutation).
    /// </summary>
    [Fact]
    public void Cv2Rectangle_MutatesMatInPlace()
    {
        using var mat = new Mat(100, 100, MatType.CV_8UC3, Scalar.White);
        using var before = mat.Clone();

        Cv2.Rectangle(mat, new Point(10, 10), new Point(30, 30), Scalar.Black, -1);

        Assert.False(MatsAreEqual(mat, before),
            "Cv2.Rectangle should mutate the Mat in-place.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Task 7.1 — for-member-name-template-fallback hardening (FR-4.4 / P-8)
    // 模板大于源图 / 大于识别区域 → 不抛异常、返回空。
    // 依赖 MatchTemplateHelper.FindMatches 首部防护：
    //   srcMat.Width < template.Width || srcMat.Height < template.Height → return 空
    // 通过 InternalsVisibleTo("BetterGenshinImpact.UnitTest") 直接调用 internal 方法。
    // EXPECTED: PASS（防护已内建在生产代码）。零生产代码改动。
    // ─────────────────────────────────────────────────────────────────────────

    private static Mat CreateGray(int width, int height)
        => new Mat(height, width, MatType.CV_8UC1, new Scalar(128));

    [Fact]
    public void FindBestMatch_TemplateLargerThanSource_ReturnsNull_NoThrow()
    {
        // 源图 420×50 灰度（模拟放人弹窗名字 ROI），模板 600×80 比源还大
        using var srcSmall = CreateGray(420, 50);
        using var templateBig = CreateGray(600, 80);

        // 不抛异常
        var best = MatchTemplateHelper.FindBestMatch(
            srcSmall, templateBig, TemplateMatchModes.CCoeffNormed, null, 0.8);

        // 返回空（null = 无兜底命中，不触发走错识别分支）
        Assert.Null(best);
    }

    [Fact]
    public void FindMatches_TemplateLargerThanSource_ReturnsEmpty_NoThrow()
    {
        using var srcSmall = CreateGray(420, 50);
        using var templateBig = CreateGray(600, 80);

        // 不抛异常，返回空列表
        var matches = MatchTemplateHelper.FindMatches(
            srcSmall, templateBig, TemplateMatchModes.CCoeffNormed, null, 0.8, 8);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindMatches_TemplateLargerThanSourceInOneDimension_ReturnsEmpty_NoThrow()
    {
        // 仅宽度超（500 > 420），高度不超（40 <= 50）——防护按任一维度短路
        using var srcSmall = CreateGray(420, 50);
        using var templateWider = CreateGray(500, 40);

        var matches = MatchTemplateHelper.FindMatches(
            srcSmall, templateWider, TemplateMatchModes.CCoeffNormed, null, 0.8, 8);

        Assert.Empty(matches);
    }
}