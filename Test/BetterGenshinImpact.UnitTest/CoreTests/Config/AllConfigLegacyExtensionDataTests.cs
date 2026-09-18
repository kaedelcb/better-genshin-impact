using System.Text.Json;
using System.Text.Json.Nodes;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.CoreTests.Config;

/// <summary>
/// R3 过渡保护夹具（开工定案 §6）：AllConfig 删除茶包调度字段后，
/// ConfigService 的全对象覆盖写不得抹掉盘上旧字段（R1 迁移输入）。
/// 字段删除落地后，这些未知键即为真实退役字段名。
/// </summary>
public class AllConfigLegacyExtensionDataTests
{
    [Fact]
    public void UnknownFields_RoundTrip_ThroughFullSerialize()
    {
        const string json = """
        {
            "selectedOneDragonFlowConfigName": "日常",
            "scheduleList": ["默认计划表", "周末表"],
            "scheduleLoop": true,
            "cycleTime": "04:00",
            "legacyFutureRemovedField": { "nested": 42 }
        }
        """;
        var config = JsonSerializer.Deserialize<AllConfig>(json, ConfigService.JsonOptions);
        Assert.NotNull(config);
        Assert.Equal("日常", config!.SelectedOneDragonFlowConfigName);

        var outJson = JsonSerializer.Serialize(config, ConfigService.JsonOptions);
        var node = JsonNode.Parse(outJson)!.AsObject();
        // 已知字段正常序列化；未知字段原样往返（键名不被 camelCase 改写，值不变）
        Assert.Equal("日常", node["selectedOneDragonFlowConfigName"]!.GetValue<string>());
        Assert.Equal("04:00", node["cycleTime"]!.GetValue<string>());
        Assert.Equal(true, node["scheduleLoop"]!.GetValue<bool>());
        Assert.Equal(42, node["legacyFutureRemovedField"]!["nested"]!.GetValue<int>());
        var list = node["scheduleList"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("周末表", list[1]!.GetValue<string>());
    }

    [Fact]
    public void NoUnknownFields_BagIsNull_NoSideEffects()
    {
        var config = JsonSerializer.Deserialize<AllConfig>("""{ "selectedOneDragonFlowConfigName": "x" }""", ConfigService.JsonOptions);
        Assert.NotNull(config);
        Assert.Null(config!.LegacyExtensionData);
        var outJson = JsonSerializer.Serialize(config, ConfigService.JsonOptions);
        Assert.DoesNotContain("legacyExtensionData", outJson);
    }
}