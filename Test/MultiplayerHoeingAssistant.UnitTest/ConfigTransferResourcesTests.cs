using System.Text;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferResourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-transfer-resource-test-" + Guid.NewGuid().ToString("N"));
    public ConfigTransferResourcesTests() => Directory.CreateDirectory(_root);
    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);
    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    [Fact]
    public void ExportIncludesActualScriptPayloadAndDynamicResourceRootsButNotRuntimeOrGlobalUser()
    {
        Write("ScriptGroup/Daily.json", "{\"projects\":[{\"type\":\"Javascript\",\"folderName\":\"Daily\"}]}");
        Write("JsScript/Daily/manifest.json", "{\"name\":\"Daily\",\"main\":\"./main.js\"}");
        Write("JsScript/Daily/main.js", "pathing.runFile('dynamic.json');");
        Write("JsScript/Daily/assets/image.png", "asset-bytes");
        Write("AutoPathing/dynamic.json", "{\"positions\":[]}");
        Write("AutoFight/team.txt", "fight-strategy");
        Write("log/runtime.log", "runtime");
        Write("JsScript/Daily/backup/dependency.json", "backup dependency");
        Write("JsScript/Daily/cache/dependency.json", "cache dependency");
        Write("JsScript/Daily/.config", "hidden dependency");
        Write("config.json", "{\"machine\":true}");
        Write("TaskCenterRuns/run.json", "runtime");
        var files = ConfigTransferResources.PlanExport(_root);
        Assert.Equal(B("asset-bytes"), files["bgi-user/JsScript/Daily/assets/image.png"]);
        Assert.Contains("bgi-user/AutoPathing/dynamic.json", files.Keys);
        Assert.Contains("bgi-user/AutoFight/team.txt", files.Keys);
        Assert.Contains("bgi-user/JsScript/Daily/backup/dependency.json", files.Keys);
        Assert.Contains("bgi-user/JsScript/Daily/cache/dependency.json", files.Keys);
        Assert.Contains("bgi-user/JsScript/Daily/.config", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.Contains("runtime") || k.EndsWith("/config.json"));
    }

    [Fact]
    public void RuntimeLookingBusinessInputPreservesOriginalBytesInsteadOfBlockingBackup()
    {
        Write("JsScript/Daily/log/input.log", "may be a real JS dependency");
        var files = ConfigTransferResources.PlanExport(_root);
        Assert.Equal(B("may be a real JS dependency"), files["bgi-user/JsScript/Daily/log/input.log"]);
    }

    [Fact]
    public void MissingJsEntryRejectsExportBeforeAnyWrites()
    {
        Write("ScriptGroup/G.json", "{\"projects\":[{\"type\":\"Javascript\",\"folderName\":\"X\"}]}");
        Write("JsScript/X/manifest.json", "{\"main\":\"missing.js\"}");
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanExport(_root));
    }

    [Fact]
    public void DifferentThirdPartyJsRejectsImportWithoutOverwriting()
    {
        Write("JsScript/X/main.js", "local");
        var incoming = new Dictionary<string, byte[]> { ["bgi-user/JsScript/X/main.js"] = B("incoming") };
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanImport(incoming, _root));
        Assert.Equal("local", File.ReadAllText(Path.Combine(_root, "JsScript/X/main.js")));
    }

    [Fact]
    public void ConflictingGroupIsRenamedAndSemanticReferencesAndRevisionFollowActualBytes()
    {
        Write("ScriptGroup/G.json", "{\"name\":\"G\",\"projects\":[],\"local\":true}");
        var incoming = new Dictionary<string, byte[]>
        {
            ["bgi-user/ScriptGroup/G.json"] = B("{\"name\":\"G\",\"projects\":[]}"),
            ["bgi-user/OneDragon/D.json"] = B("{\"name\":\"D\",\"taskEnabledList\":{\"id\":{\"Item1\":true,\"Item2\":\"G\"}}}"),
            ["assistant-config.json"] = B("{\"quickCommands\":{\"Q\":\"GROUP:G\"},\"onlineHoeingGroupNames\":[\"G\"],\"onlineHoeingGroupTypes\":[\"group\"]}"),
            ["taskcenter-workflows/w.json"] = B("{\"nodes\":[{\"kind\":\"resource.configGroup\",\"ref\":{\"config\":\"G\",\"configKey\":\"G#old\",\"revision\":\"old\"}}]}")
        };
        var plan = ConfigTransferResources.PlanImport(incoming, _root);
        var group = plan.TargetFiles.Single(p => p.Key.Contains("ScriptGroup"));
        var name = Path.GetFileNameWithoutExtension(group.Key);
        Assert.StartsWith("G-导入-", name);
        Assert.Equal(name, JsonNode.Parse(group.Value)!["name"]!.GetValue<string>());
        var dragon = JsonNode.Parse(plan.TargetFiles.Single(p => p.Key.Contains("OneDragon")).Value)!;
        Assert.Equal(name, dragon["taskEnabledList"]!["id"]!["Item2"]!.GetValue<string>());
        var config = JsonNode.Parse(plan.AssistantFiles["assistant-config.json"])!;
        Assert.Equal("GROUP:" + name, config["quickCommands"]!["Q"]!.GetValue<string>());
        var reference = JsonNode.Parse(plan.AssistantFiles["taskcenter-workflows/w.json"])!["nodes"]![0]!["ref"]!;
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(group.Value));
        Assert.Equal(revision, reference["revision"]!.GetValue<string>());
        Assert.Equal(name + "#old", reference["configKey"]!.GetValue<string>());
        Assert.Contains("local", File.ReadAllText(Path.Combine(_root, "ScriptGroup/G.json")));
        Assert.False(File.Exists(group.Key));
    }

    [Theory]
    [InlineData("bgi-user/JsScript/../config.json")]
    [InlineData("bgi-user/JsScript/X/main.js:stream")]
    [InlineData("bgi-user/TaskCenterRuns/run.json")]
    [InlineData("bgi-user/JsScript/X/main.js.")]
    public void ImportRejectsEscapingOrNonBusinessPaths(string entry)
    {
        Assert.False(ConfigTransferResources.IsResourceEntry(entry));
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanImport(new Dictionary<string, byte[]> { [entry] = B("x") }, _root));
    }

    [Fact]
    public void IdenticalThirdPartyResourceIsReusedWithoutScheduledWrite()
    {
        Write("JsScript/X/main.js", "same");
        var plan = ConfigTransferResources.PlanImport(new Dictionary<string, byte[]> { ["bgi-user/JsScript/X/main.js"] = B("same") }, _root);
        Assert.Empty(plan.TargetFiles);
    }

    [Fact]
    public void ResourceRenameRunsBeforeFlowReuseAndStartupFollowsNewPlanIdentity()
    {
        Write("ScriptGroup/G.json", "{\"name\":\"G\",\"projects\":[],\"local\":true}");
        const string flowEntry = "flows/wf-shared.flow.json";
        var flow = B("{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"workflowId\":\"wf-shared\",\"name\":\"Plan\",\"nodes\":[{\"nodeId\":\"node-one\",\"kind\":\"resource.configGroup\",\"ref\":{\"config\":\"G\",\"configKey\":\"G#1234abcd\",\"revision\":\"source-revision\"},\"strategies\":[]}],\"triggers\":[],\"terminal\":[]}");
        var sourceGroup = B("{\"name\":\"G\",\"projects\":[]}");
        var flowDocument = JsonNode.Parse(flow)!;
        flowDocument["nodes"]![0]!["ref"]!["revision"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceGroup));
        flow = B(flowDocument.ToJsonString());
        var incoming = new Dictionary<string, byte[]>
        {
            ["bgi-user/ScriptGroup/G.json"] = sourceGroup,
            [flowEntry] = flow,
            ["startup-flow.json"] = B("{\"enabled\":false,\"steps\":[{\"kind\":\"enterTaskCenter\",\"taskCenterFlowId\":\"wf-shared\",\"trueSteps\":[],\"falseSteps\":[],\"fireSteps\":[]}]}")
        };
        // The original source and destination plans have identical IDs and bytes.
        // A same-name BGI conflict changes the imported meaning before ID reuse is decided.
        var resources = ConfigTransferResources.PlanImport(incoming, _root);
        var merged = ConfigTransferMistletoe.PrepareImport(resources.AssistantFiles,
            new Dictionary<string, byte[]> { [flowEntry] = flow });
        var newId = merged.WorkflowIdMap["wf-shared"];
        Assert.NotEqual("wf-shared", newId);
        Assert.DoesNotContain("wf-shared", merged.ReusedWorkflowIds);
        var restored = JsonNode.Parse(merged.Files["flows/" + newId + ".flow.json"])!;
        var newGroupName = Path.GetFileNameWithoutExtension(resources.TargetFiles.Single().Key);
        Assert.Equal(newGroupName, restored["nodes"]![0]!["ref"]!["config"]!.GetValue<string>());
        Assert.Equal(newGroupName + "#1234abcd", restored["nodes"]![0]!["ref"]!["configKey"]!.GetValue<string>());
        Assert.Equal(newId, JsonNode.Parse(merged.Files["startup-flow.json"])!["steps"]![0]!["taskCenterFlowId"]!.GetValue<string>());
        Assert.Equal("G", JsonNode.Parse(flow)!["nodes"]![0]!["ref"]!["config"]!.GetValue<string>());
    }

    [Fact]
    public void SingleTaskReferenceUsesOneDragonRenameAndRetainsTaskIdentity()
    {
        Write("OneDragon/D.json", "{\"name\":\"D\",\"taskEnabledList\":{},\"local\":true}");
        var incoming = new Dictionary<string, byte[]>
        {
            ["bgi-user/OneDragon/D.json"] = B("{\"name\":\"D\",\"taskEnabledList\":{}}"),
            ["flows/wf-single.flow.json"] = B("{\"nodes\":[{\"kind\":\"resource.singleTask\",\"ref\":{\"config\":\"D\",\"configKey\":\"D#1234abcd\",\"taskId\":\"stable-task\"}}]}")
        };
        var plan = ConfigTransferResources.PlanImport(incoming, _root);
        var name = Path.GetFileNameWithoutExtension(plan.TargetFiles.Single().Key);
        var reference = JsonNode.Parse(plan.AssistantFiles["flows/wf-single.flow.json"])!["nodes"]![0]!["ref"]!;
        Assert.Equal(name, reference["config"]!.GetValue<string>());
        Assert.Equal(name + "#1234abcd", reference["configKey"]!.GetValue<string>());
        Assert.Equal("stable-task", reference["taskId"]!.GetValue<string>());
    }

    [Fact]
    public void NativeOneDragonGroupRenamePreservesTaskIdsWhileRewritingGroupDefinitionAndDelegates()
    {
        Write("ScriptGroup/G.json", "{\"name\":\"G\",\"projects\":[],\"local\":true}");
        var input = new Dictionary<string, byte[]>
        {
            ["bgi-user/ScriptGroup/G.json"] = B("{\"name\":\"G\",\"projects\":[]}"),
            ["bgi-user/OneDragon/D.json"] = B("{\"taskDefinitions\":{\"G\":\"G\"},\"taskEnabledList\":{\"G\":true},\"taskOrder\":[\"G\"],\"nextTaskId\":\"G\",\"customDomainList\":[\"G\"],\"mondayDomainName\":\"G\",\"extension\":{\"keep\":42}}")
        };
        var plan = ConfigTransferResources.PlanImport(input, _root);
        var name = Path.GetFileNameWithoutExtension(plan.TargetFiles.Single(p => p.Key.Contains("ScriptGroup")).Key);
        var dragon = JsonNode.Parse(plan.TargetFiles.Single(p => p.Key.Contains("OneDragon")).Value)!;
        Assert.Equal(name, dragon["taskDefinitions"]!["G"]!.GetValue<string>());
        Assert.True(dragon["taskEnabledList"]!["G"]!.GetValue<bool>());
        Assert.Equal("G", dragon["taskOrder"]![0]!.GetValue<string>());
        Assert.Equal("G", dragon["nextTaskId"]!.GetValue<string>());
        Assert.Equal(name, dragon["customDomainList"]![0]!.GetValue<string>());
        Assert.Equal(name, dragon["mondayDomainName"]!.GetValue<string>());
        Assert.Equal(42, dragon["extension"]!["keep"]!.GetValue<int>());
    }

    [Fact]
    public void PortableRoomsExcludePasswordsAndPreserveTargetCredentials()
    {
        var source = B("[{\"serverUrl\":\"https://example.invalid\",\"roomCode\":\"A\",\"protectedPassword\":\"SOURCE-SECRET\",\"lastUsedUtc\":\"2026-10-18T00:00:00Z\"},{\"serverUrl\":\"https://example.invalid\",\"roomCode\":\"B\",\"protectedPassword\":\"SECOND-SECRET\",\"lastUsedUtc\":\"2026-10-18T00:00:00Z\"}]");
        var portable = ConfigTransferRoomHistory.Export(source);
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(portable));
        Assert.DoesNotContain("password", Encoding.UTF8.GetString(portable).ToLowerInvariant());
        var local = B("[{\"serverUrl\":\"https://example.invalid\",\"roomCode\":\"A\",\"protectedPassword\":\"TARGET-SECRET\",\"lastUsedUtc\":\"2026-10-17T00:00:00Z\"}]");
        var result = JsonNode.Parse(ConfigTransferRoomHistory.PrepareImport(portable, local))!.AsArray();
        Assert.Equal("TARGET-SECRET", result[0]!["protectedPassword"]!.GetValue<string>());
        Assert.Equal("", result[1]!["protectedPassword"]!.GetValue<string>());
    }

    [Fact]
    public void PortableRoomRejectsInjectedPasswordField()
        => Assert.Throws<InvalidDataException>(() => ConfigTransferRoomHistory.PrepareImport(B("[{\"serverUrl\":\"https://example.invalid\",\"roomCode\":\"A\",\"password\":\"secret\"}]"), null));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
