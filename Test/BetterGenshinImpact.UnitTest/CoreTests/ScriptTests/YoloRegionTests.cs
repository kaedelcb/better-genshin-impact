using BetterGenshinImpact.Core.Script.Dependence;
using Compunet.YoloSharp.Data;
using Compunet.YoloSharp.Metadata;
using Microsoft.ClearScript.V8;
using Region = BetterGenshinImpact.GameTask.Model.Area.Region;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class YoloRegionTests
{
    [Fact]
    public void DetectionResult_ExposesCustomFieldsToCaseInsensitiveScript()
    {
        var prediction = new TestPrediction
        {
            Name = new YoloName(7, "目标"),
            Confidence = 0.875f
        };
        using var owner = new Region();
        using var result = new YoloRegion(10, 20, 30, 40, prediction, owner);
        using var engine = new V8ScriptEngine(
            V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding |
            V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("result", result);

        Assert.True((bool)engine.Evaluate("""
            result.id === 7 && result.name === '目标' && result.confidence === 0.875 &&
            result.x === 10 && result.y === 20 && result.width === 30 && result.height === 40
            """));
        Assert.Null(typeof(YoloRegion).GetProperty("Detection"));
        Assert.Same(owner.DrawingBoard, result.DrawingBoard);
    }

    private sealed class TestPrediction : YoloPrediction
    {
    }
}
