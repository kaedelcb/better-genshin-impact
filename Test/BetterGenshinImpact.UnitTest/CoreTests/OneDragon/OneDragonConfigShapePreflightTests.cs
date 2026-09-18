using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.UnitTest.CoreTests.OneDragon;

/// <summary>
/// R3.0 形状预检夹具（开工定案 §3 / R0 基线 §8 硬门槛）。
/// 场景与 R1 迁移器夹具（Test/OneDragonMigration/Fixtures）对齐，并按会诊修订补混合形状/数组/空值隔离。
/// </summary>
public class OneDragonConfigShapePreflightTests
{
    [Fact]
    public void TeabagTuple_IsProtected()
    {
        const string json = """
        {
            "Name": "茶包日常",
            "Version": 1,
            "IndexId": 2,
            "NextTaskIndex": 0,
            "TaskEnabledList": {
                "1": { "Item1": true, "Item2": "领取邮件" },
                "3": { "Item1": false, "Item2": "自动秘境" }
            }
        }
        """;
        var verdict = OneDragonConfigShapePreflight.InspectText(json);
        Assert.Equal(OneDragonConfigShape.TeabagTuple, verdict.Shape);
        Assert.True(verdict.IsProtected);
        Assert.False(verdict.IsLoadable);
    }

    [Fact]
    public void TeabagTuple_EmptyList_WithNextTaskIndex_IsProtected()
    {
        const string json = """{ "Name": "空茶包", "NextTaskIndex": 0, "TaskEnabledList": {} }""";
        Assert.Equal(OneDragonConfigShape.TeabagTuple, OneDragonConfigShapePreflight.InspectText(json).Shape);
    }

    [Fact]
    public void LegacyNameBool_IsProtected()
    {
        const string json = """
        {
            "Name": "旧版",
            "TaskEnabledList": { "领取邮件": true, "自动秘境": false }
        }
        """;
        var verdict = OneDragonConfigShapePreflight.InspectText(json);
        Assert.Equal(OneDragonConfigShape.LegacyNameBool, verdict.Shape);
        Assert.True(verdict.IsProtected);
    }

    [Fact]
    public void PublicCurrent_IsLoadable()
    {
        const string json = """
        {
            "Name": "公版",
            "NextTaskId": "",
            "TaskEnabledList": { "3f2a9c1e-7b4d": true, "a1c29d5e-6f7a": false },
            "TaskOrder": [ "3f2a9c1e-7b4d", "a1c29d5e-6f7a" ],
            "TaskDefinitions": { "3f2a9c1e-7b4d": "领取邮件", "a1c29d5e-6f7a": "自动秘境" }
        }
        """;
        var verdict = OneDragonConfigShapePreflight.InspectText(json);
        Assert.Equal(OneDragonConfigShape.PublicCurrent, verdict.Shape);
        Assert.True(verdict.IsLoadable);
        Assert.False(verdict.IsProtected);
    }

    [Fact]
    public void PublicCurrent_MissingTaskOrder_NotDemotedToNameBool()
    {
        // R1 加固结论：公版缺 TaskOrder 不降级为名称键（有 TaskDefinitions 即公版）
        const string json = """
        {
            "Name": "公版缺顺序",
            "TaskEnabledList": { "3f2a9c1e-7b4d": true },
            "TaskDefinitions": { "3f2a9c1e-7b4d": "领取邮件" }
        }
        """;
        Assert.Equal(OneDragonConfigShape.PublicCurrent, OneDragonConfigShapePreflight.InspectText(json).Shape);
    }

    [Fact]
    public void PublicCurrent_EmptyList_WithTaskOrder_IsLoadable()
    {
        const string json = """{ "Name": "新建", "NextTaskId": "", "TaskEnabledList": {}, "TaskOrder": [], "TaskDefinitions": {} }""";
        Assert.Equal(OneDragonConfigShape.PublicCurrent, OneDragonConfigShapePreflight.InspectText(json).Shape);
    }

    [Fact]
    public void MixedTupleAndBool_IsStructuralBad()
    {
        const string json = """
        {
            "Name": "混合",
            "TaskEnabledList": {
                "1": { "Item1": true, "Item2": "领取邮件" },
                "2": true
            }
        }
        """;
        var verdict = OneDragonConfigShapePreflight.InspectText(json);
        Assert.Equal(OneDragonConfigShape.StructuralBad, verdict.Shape);
        Assert.True(verdict.IsProtected);
    }

    [Fact]
    public void TaskEnabledListArray_IsStructuralBad()
    {
        const string json = """{ "Name": "数组", "TaskEnabledList": [ true, false ] }""";
        Assert.Equal(OneDragonConfigShape.StructuralBad, OneDragonConfigShapePreflight.InspectText(json).Shape);
    }

    [Fact]
    public void NullEntryValue_IsStructuralBad()
    {
        const string json = """{ "Name": "空值", "TaskEnabledList": { "1": null } }""";
        Assert.Equal(OneDragonConfigShape.StructuralBad, OneDragonConfigShapePreflight.InspectText(json).Shape);
    }

    [Fact]
    public void BrokenJson_IsStructuralBad_WithError()
    {
        var verdict = OneDragonConfigShapePreflight.InspectText("{ not json ");
        Assert.Equal(OneDragonConfigShape.StructuralBad, verdict.Shape);
        Assert.NotNull(verdict.Error);
        Assert.True(verdict.IsProtected);
    }

    [Fact]
    public void RootArray_IsStructuralBad()
    {
        Assert.Equal(OneDragonConfigShape.StructuralBad, OneDragonConfigShapePreflight.InspectText("[1,2,3]").Shape);
    }

    [Fact]
    public void EmptyShell_IsUnknown_AndProtected()
    {
        var verdict = OneDragonConfigShapePreflight.InspectText("{}");
        Assert.Equal(OneDragonConfigShape.Unknown, verdict.Shape);
        Assert.True(verdict.IsProtected);
    }

    [Fact]
    public void Hash_BindsContentBytes()
    {
        var a = OneDragonConfigShapePreflight.InspectText("""{ "Name": "A", "TaskEnabledList": {}, "TaskOrder": [] }""");
        var b = OneDragonConfigShapePreflight.InspectText("""{ "Name": "B", "TaskEnabledList": {}, "TaskOrder": [] }""");
        Assert.NotNull(a.ContentHash);
        Assert.NotEqual(a.ContentHash, b.ContentHash);
        // 同一内容哈希稳定
        var a2 = OneDragonConfigShapePreflight.InspectText("""{ "Name": "A", "TaskEnabledList": {}, "TaskOrder": [] }""");
        Assert.Equal(a.ContentHash, a2.ContentHash);
    }
}