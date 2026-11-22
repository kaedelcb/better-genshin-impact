using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ChascaBulletRecognitionTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Recognize_AppliesModelAtReferenceLocationAfterCaptureScaling(int width, int height)
    {
        const string modelJson = """
            {"bullets":{"1":{"1":{"features":[
                {"x":1170,"y":80,"w":20,"h":20,"type":"F1","channel":"V","range":1.0,
                "refVal":1.0,"weight":1.0,"probTable":[0.0,0.9]}
            ]}}}}
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, modelJson);
            var model = ChascaBulletRecognition.Load(path);
            Assert.Throws<InvalidDataException>(() => ChascaBulletRecognition.Load(path, requireCompletePackage: true));

            using var original = new GameCaptureRegion(new Mat(height, width, MatType.CV_8UC3, Scalar.All(0)), 0, 0);
            var scale = Math.Min(width / 1920d, height / 1080d);
            Cv2.Rectangle(original.SrcMat, new Rect((int)(1170 * scale), (int)(80 * scale), (int)(20 * scale), (int)(20 * scale)),
                Scalar.All(255), -1);

            using var frame = original.DeriveTo1080P();
            Assert.Equal(new Size(1920, 1080), frame.SrcMat.Size());
            Assert.Equal("空火空空空空", new string(model.Recognize(frame.SrcMat, 0.8)));
            Assert.Equal("空空空空空空", new string(model.Recognize(frame.SrcMat, 0.95)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PackagedBulletModels_AreAvailableInBuildOutput()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoFight", "Assets", "chasca-bullet-models.json");
        Assert.NotNull(ChascaBulletRecognition.Load(path, requireCompletePackage: true));
    }

    [Fact]
    public void Observe_IgnoresOuterSlotsAndWindAndEvictsOldestSequence()
    {
        var history = new ChascaBulletSequenceHistory(2);

        Assert.True(history.Observe("风火空雷冰火".ToCharArray()));
        Assert.False(history.Observe("空火风雷冰雷".ToCharArray()));
        Assert.True(history.Observe("空火水雷冰雷".ToCharArray()));
        Assert.False(history.Observe("空火风雷冰水".ToCharArray()));
        Assert.True(history.Observe("空冰水雷冰雷".ToCharArray()));
        Assert.True(history.Observe("风火空雷冰火".ToCharArray()));
        Assert.Equal(2, history.Count);
    }
}
