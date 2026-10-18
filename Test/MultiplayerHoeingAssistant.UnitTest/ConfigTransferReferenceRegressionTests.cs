using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferReferenceRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-transfer-reference-test-" + Guid.NewGuid().ToString("N"));
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private static Dictionary<string, string> Groups => new(StringComparer.OrdinalIgnoreCase) { ["沙漠 组"] = "沙漠 组-导入", ["G"] = "G-导入" };
    private static Dictionary<string, string> Dragons => new(StringComparer.OrdinalIgnoreCase) { ["日常 龙"] = "日常 龙-导入" };

    public ConfigTransferReferenceRegressionTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("--startGroups \"沙漠 组\" G other", "--startGroups \"沙漠 组-导入\" \"G-导入\" other")]
    [InlineData("--TASKPROGRESS\tG  \"沙漠 组\"", "--TASKPROGRESS\t\"G-导入\"  \"沙漠 组-导入\"")]
    [InlineData("startOneDragon \"日常 龙\" G", "startOneDragon \"日常 龙-导入\" G")]
    [InlineData("--instance ChildSession --startGroups G --instance-name G", "--instance ChildSession --startGroups \"G-导入\" --instance-name G")]
    [InlineData("--restart-from-pid 123 startOneDragon \"日常 龙\"", "--restart-from-pid 123 startOneDragon \"日常 龙-导入\"")]
    [InlineData("\"startOneDragon\" \"日常 龙\"", "\"startOneDragon\" \"日常 龙-导入\"")]
    [InlineData("--startGroups \" 沙漠 组 \"", "--startGroups \"沙漠 组-导入\"")]
    public void OnlyFormalResourceArgumentsAreRemapped(string source, string expected)
        => Assert.Equal(expected, ConfigTransferResources.RewriteBgiArguments(source, Groups, Dragons));

    [Theory]
    [InlineData("start --title G")]
    [InlineData("startContinuousOneDragon G")]
    [InlineData("--startGroups other \"prefix G\"")]
    [InlineData("--instance-name G startOneDragon other")]
    [InlineData("--startGroups \"G\\\"quoted\" other")]
    [InlineData("--startGroups \"C:\\tools\\\"other\"")]
    public void UnrelatedTextAndEscapedQuotesAreNotGloballyReplaced(string source)
        => Assert.Equal(source, ConfigTransferResources.RewriteBgiArguments(source, Groups, Dragons));

    [Theory]
    [InlineData("--startGroups \"G")]
    [InlineData("startOneDragon \"日常 龙")]
    [InlineData("--startGroups G\nother")]
    [InlineData("--startGroups G\0other")]
    public void AmbiguousArgumentsAreRejectedWhenResourcesWereRenamed(string source)
        => Assert.Throws<InvalidDataException>(() => ConfigTransferResources.RewriteBgiArguments(source, Groups, Dragons));

    [Fact]
    public void MalformedArgumentsAreUntouchedWhenThereIsNoResourceRename()
    {
        const string source = "--startGroups \"G";
        Assert.Equal(source, ConfigTransferResources.RewriteBgiArguments(source,
            new Dictionary<string, string>(), new Dictionary<string, string>()));
    }

    [Fact]
    public void PlanImportRestoresCommandsInCurrentStartupAndEveryNestedScheme()
    {
        Write("ScriptGroup/沙漠 组.json", "{\"projects\":[],\"local\":true}");
        Write("ScriptGroup/G.json", "{\"projects\":[],\"local\":true}");
        Write("OneDragon/日常 龙.json", "{\"taskEnabledList\":{},\"local\":true}");
        var startup = new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject
            {
                ["kind"] = "weekday",
                ["trueSteps"] = new JsonArray(Step("--startGroups \"沙漠 组\" G")),
                ["falseSteps"] = new JsonArray(Step("--TaskProgress G")),
                ["fireSteps"] = new JsonArray(Step("startOneDragon \"日常 龙\""))
            })
        };
        var entries = Resources();
        entries["startup-flow.json"] = B(startup.ToJsonString());
        entries["startup-flow-schemes.json"] = B(new JsonArray(
            new JsonObject { ["name"] = "方案一", ["config"] = startup.DeepClone() },
            new JsonObject { ["name"] = "方案二", ["config"] = startup.DeepClone() }).ToJsonString());

        var result = ConfigTransferResources.PlanImport(entries, _root);
        var group = Name(result, "ScriptGroup", "沙漠 组");
        var second = Name(result, "ScriptGroup", "G");
        var dragon = Name(result, "OneDragon", "日常 龙");
        Check(JsonNode.Parse(result.AssistantFiles["startup-flow.json"])!);
        foreach (var scheme in JsonNode.Parse(result.AssistantFiles["startup-flow-schemes.json"])!.AsArray()) Check(scheme!["config"]!);
        Assert.Contains("local", File.ReadAllText(Path.Combine(_root, "ScriptGroup/G.json")));
        Assert.All(result.TargetFiles.Keys, path => Assert.False(File.Exists(path)));

        void Check(JsonNode config)
        {
            var parent = config["steps"]![0]!;
            Assert.Equal($"--startGroups \"{group}\" \"{second}\"", parent["trueSteps"]![0]!["arguments"]!.GetValue<string>());
            Assert.Equal($"--TaskProgress \"{second}\"", parent["falseSteps"]![0]!["arguments"]!.GetValue<string>());
            Assert.Equal($"startOneDragon \"{dragon}\"", parent["fireSteps"]![0]!["arguments"]!.GetValue<string>());
        }
    }

    [Fact]
    public void PlanningRejectsMalformedCommandBeforeAnyTargetIsPublished()
    {
        const string local = "{\"projects\":[],\"local\":true}";
        Write("ScriptGroup/G.json", local);
        var entries = Resources();
        entries["startup-flow.json"] = B(new JsonObject { ["steps"] = new JsonArray(Step("--startGroups \"G")) }.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanImport(entries, _root));
        Assert.Equal(local, File.ReadAllText(Path.Combine(_root, "ScriptGroup/G.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "ScriptGroup")));
    }

    [Fact]
    public void CacheListsAndTaskDictionaryKeysFollowResourceRenamesAndMergeKeepsLocalItems()
    {
        Write("ScriptGroup/G.json", "{\"projects\":[],\"local\":true}");
        Write("OneDragon/日常 龙.json", "{\"taskEnabledList\":{},\"local\":true}");
        var incomingCache = B("""
            {"configGroups":["G"],"oneClickConfigs":["日常 龙"],
             "configGroupTasksWithStatus":{"G":[{"name":"导入任务","index":3,"future":{"keep":42}}]},
             "oneClickTasksWithStatus":{"日常 龙":[{"name":"导入龙任务","taskId":"task-stable","enabled":true}]},
             "hotkeys":["source-hotkey"],"sourceOnly":{"retain":true},"extension":{"source":1}}
            """);
        var entries = Resources();
        entries["member-config-cache.json"] = incomingCache;
        var result = ConfigTransferResources.PlanImport(entries, _root);
        var group = Name(result, "ScriptGroup", "G");
        var dragon = Name(result, "OneDragon", "日常 龙");
        var rewritten = JsonNode.Parse(result.AssistantFiles["member-config-cache.json"])!;
        Assert.Equal(group, rewritten["configGroups"]![0]!.GetValue<string>());
        Assert.Equal(dragon, rewritten["oneClickConfigs"]![0]!.GetValue<string>());
        Assert.Null(rewritten["configGroupTasksWithStatus"]!["G"]);
        Assert.Equal(42, rewritten["configGroupTasksWithStatus"]![group]![0]!["future"]!["keep"]!.GetValue<int>());

        var local = B("""
            {"configGroups":["G","Local"],"oneClickConfigs":["日常 龙"],
             "configGroupTasksWithStatus":{"G":[{"name":"原本任务"}],"Local":[{"name":"本机任务"}]},
             "oneClickTasksWithStatus":{"日常 龙":[{"name":"原本龙任务"}]},
             "hotkeys":["local-hotkey"],"localOnly":{"retain":true},"extension":{"local":2}}
            """);
        var merged = JsonNode.Parse(ConfigTransferResources.MergeMemberCache(result.AssistantFiles["member-config-cache.json"], local))!;
        Assert.Equal(new[] { "G", "Local", group }, merged["configGroups"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "日常 龙", dragon }, merged["oneClickConfigs"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("原本任务", merged["configGroupTasksWithStatus"]!["G"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("导入任务", merged["configGroupTasksWithStatus"]![group]![0]!["name"]!.GetValue<string>());
        Assert.Equal("task-stable", merged["oneClickTasksWithStatus"]![dragon]![0]!["taskId"]!.GetValue<string>());
        Assert.Equal("local-hotkey", merged["hotkeys"]![0]!.GetValue<string>());
        Assert.True(merged["sourceOnly"]!["retain"]!.GetValue<bool>());
        Assert.True(merged["localOnly"]!["retain"]!.GetValue<bool>());
        Assert.Equal(1, merged["extension"]!["source"]!.GetValue<int>());
        Assert.Equal(2, merged["extension"]!["local"]!.GetValue<int>());
        Assert.Equal("原本任务", JsonNode.Parse(local)!["configGroupTasksWithStatus"]!["G"]![0]!["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(incomingCache)!["configGroupTasksWithStatus"]!["G"], merged["configGroupTasksWithStatus"]![group]));
    }

    [Fact]
    public void MergeIsCaseInsensitiveAndNeverReplacesLocalTaskBodiesOnANameCollision()
    {
        var local = B("{\"ConfigGroups\":[\"G\"],\"ConfigGroupTasksWithStatus\":{\"G\":[{\"name\":\"local\"}]}}");
        var imported = B("{\"configGroups\":[\"g\",\"Other\"],\"configGroupTasksWithStatus\":{\"g\":[{\"name\":\"source\"}],\"Other\":[{\"name\":\"other\"}]}}");
        var merged = JsonNode.Parse(ConfigTransferResources.MergeMemberCache(imported, local))!;
        Assert.Null(merged["configGroups"]);
        Assert.Equal(2, merged["ConfigGroups"]!.AsArray().Count);
        Assert.Equal("local", merged["ConfigGroupTasksWithStatus"]!["G"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("other", merged["ConfigGroupTasksWithStatus"]!["Other"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void NoLocalCacheRetainsImportedBytesAndMalformedLocalCacheFailsVisibly()
    {
        var incoming = B("{\"configGroups\":[\"G\"],\"unknown\":42}");
        Assert.Equal(incoming, ConfigTransferResources.MergeMemberCache(incoming, null));
        Assert.ThrowsAny<JsonException>(() => ConfigTransferResources.MergeMemberCache(incoming, B("broken")));
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.MergeMemberCache(incoming, B("[]")));
    }

    private static JsonObject Step(string arguments) => new() { ["kind"] = "startBgi", ["arguments"] = arguments };
    private static Dictionary<string, byte[]> Resources() => new()
    {
        ["bgi-user/ScriptGroup/G.json"] = B("{\"name\":\"G\",\"projects\":[]}"),
        ["bgi-user/ScriptGroup/沙漠 组.json"] = B("{\"name\":\"沙漠 组\",\"projects\":[]}"),
        ["bgi-user/OneDragon/日常 龙.json"] = B("{\"name\":\"日常 龙\",\"taskEnabledList\":{}}")
    };
    private static string Name(ResourceImportPreparation result, string directory, string original)
        => Path.GetFileNameWithoutExtension(result.TargetFiles.Keys.Single(path =>
            Path.GetFileName(Path.GetDirectoryName(path)) == directory && Path.GetFileName(path).StartsWith(original + "-导入-", StringComparison.Ordinal)));
    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
