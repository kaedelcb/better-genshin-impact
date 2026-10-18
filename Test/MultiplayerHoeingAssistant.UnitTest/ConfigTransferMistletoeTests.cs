using System.Text;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferMistletoeTests
{
    [Fact]
    public void PrivateBackupPreservesAccountScheduleAndActivePlanWithoutMachineTransaction()
    {
        var flow = Plan("wf-main");
        flow["nativeMigrationTransactionId"] = "machine-transaction";
        flow["activation"] = new JsonObject { ["status"] = "active" };
        flow["scheduleLanes"] = new JsonArray("主线", "支线");
        var node = (JsonObject)flow["nodes"]![0]!;
        node["strategies"] = new JsonArray(new JsonObject
        {
            ["kind"] = "prerequisite.account", ["uid"] = "123456789", ["bindingCode"] = "private-binding"
        }, new JsonObject { ["kind"] = "schedule.time", ["time"] = "08:15", ["mode"] = "fixed" });
        var original = Bytes(flow);
        var package = new Dictionary<string, byte[]> { [Entry("wf-main")] = original };

        var exported = ConfigTransferMistletoe.NormalizeForExport(package);
        var imported = ConfigTransferMistletoe.PrepareImport(exported, new Dictionary<string, byte[]>());
        var restored = JsonNode.Parse(imported.Files[Entry("wf-main")])!;

        Assert.Null(restored["nativeMigrationTransactionId"]);
        Assert.Equal("active", restored["activation"]!["status"]!.GetValue<string>());
        Assert.Equal("123456789", restored["nodes"]![0]!["strategies"]![0]!["uid"]!.GetValue<string>());
        Assert.Equal("private-binding", restored["nodes"]![0]!["strategies"]![0]!["bindingCode"]!.GetValue<string>());
        Assert.Equal("08:15", restored["nodes"]![0]!["strategies"]![1]!["time"]!.GetValue<string>());
        Assert.Equal("支线", restored["scheduleLanes"]![1]!.GetValue<string>());
        Assert.Equal(original, package[Entry("wf-main")]);
        Assert.NotNull(JsonNode.Parse(original)!["nativeMigrationTransactionId"]);
    }

    [Fact]
    public void CollisionKeepsLocalPlanAndRewritesNestedStartupSchemesAndSelection()
    {
        var incomingPlan = Plan("wf-main");
        var localPlan = Plan("wf-main"); localPlan["name"] = "目标机已有计划";
        var startup = Startup("wf-main");
        var nested = new JsonObject
        {
            ["kind"] = "condition", ["trueSteps"] = new JsonArray(startup["steps"]![0]!.DeepClone()),
            ["falseSteps"] = new JsonArray(), ["fireSteps"] = new JsonArray()
        };
        startup["steps"] = new JsonArray(nested);
        var schemeConfig = Startup("wf-main");
        var input = new Dictionary<string, byte[]>
        {
            [Entry("wf-main")] = Bytes(incomingPlan), ["startup-flow.json"] = Bytes(startup),
            ["startup-flow-schemes.json"] = Bytes(new JsonArray(new JsonObject { ["name"] = "方案", ["config"] = schemeConfig })),
            [ConfigTransferMistletoe.CurrentWorkflowFileName] = Encoding.UTF8.GetBytes("wf-main")
        };
        var localBytes = Bytes(localPlan);
        var local = new Dictionary<string, byte[]> { [Entry("wf-main")] = localBytes };

        var result = ConfigTransferMistletoe.PrepareImport(input, local);
        var newId = result.WorkflowIdMap["wf-main"];

        Assert.NotEqual("wf-main", newId);
        Assert.False(result.Files.ContainsKey(Entry("wf-main")));
        Assert.Equal(newId, JsonNode.Parse(result.Files[Entry(newId)])!["workflowId"]!.GetValue<string>());
        Assert.Equal(newId, JsonNode.Parse(result.Files["startup-flow.json"])!["steps"]![0]!["trueSteps"]![0]!["taskCenterFlowId"]!.GetValue<string>());
        Assert.Equal(newId, JsonNode.Parse(result.Files["startup-flow-schemes.json"])![0]!["config"]!["steps"]![0]!["taskCenterFlowId"]!.GetValue<string>());
        Assert.Equal(newId, Encoding.UTF8.GetString(result.Files[ConfigTransferMistletoe.CurrentWorkflowFileName]));
        Assert.Equal(localBytes, local[Entry("wf-main")]);
    }

    [Fact]
    public void ReimportReusesMatchingLocalPlanWithoutWritingOrStrippingItsTransaction()
    {
        var plan = Plan("wf-main");
        plan["nativeMigrationTransactionId"] = "local-transaction";
        var localBytes = Bytes(plan);
        var local = new Dictionary<string, byte[]> { [Entry("wf-main")] = localBytes };
        var exported = ConfigTransferMistletoe.NormalizeForExport(local);

        var result = ConfigTransferMistletoe.PrepareImport(exported, local);

        Assert.Contains("wf-main", result.ReusedWorkflowIds);
        Assert.False(result.Files.ContainsKey(Entry("wf-main")));
        Assert.NotNull(JsonNode.Parse(local[Entry("wf-main")])!["nativeMigrationTransactionId"]);
    }

    [Fact]
    public void SchemeMergePreservesLocalAuthorizationAndRenamesOnlyConflictingDefinitions()
    {
        var localConfig = Startup(""); localConfig["enabled"] = true;
        localConfig["automaticAuthorization"] = new JsonObject { ["IntentId"] = "local-intent", ["IntentTimestamp"] = 1234, ["Revoked"] = true };
        var local = new JsonArray(new JsonObject { ["name"] = "相同方案", ["config"] = localConfig },
            new JsonObject { ["name"] = "冲突方案", ["config"] = Startup("") });
        var same = (JsonObject)localConfig.DeepClone();
        same["automaticAuthorization"] = new JsonObject { ["IntentId"] = "foreign-intent", ["IntentTimestamp"] = 5678, ["Revoked"] = false };
        var changed = Startup(""); changed["delaySeconds"] = 20;
        var input = new Dictionary<string, byte[]>
        {
            ["startup-flow-schemes.json"] = Bytes(new JsonArray(new JsonObject { ["name"] = "相同方案", ["config"] = same },
                new JsonObject { ["name"] = "冲突方案", ["config"] = changed }))
        };

        var result = ConfigTransferMistletoe.PrepareImport(input, new Dictionary<string, byte[]>(), localSchemesJson: local.ToJsonString());
        var merged = JsonNode.Parse(result.Files["startup-flow-schemes.json"])!.AsArray();

        Assert.Equal(3, merged.Count);
        Assert.Equal("local-intent", merged[0]!["config"]!["automaticAuthorization"]!["IntentId"]!.GetValue<string>());
        Assert.True(merged[0]!["config"]!["automaticAuthorization"]!["Revoked"]!.GetValue<bool>());
        Assert.Equal("冲突方案", merged[1]!["name"]!.GetValue<string>());
        Assert.Equal("冲突方案-导入-1", merged[2]!["name"]!.GetValue<string>());
        Assert.Equal(20, merged[2]!["config"]!["delaySeconds"]!.GetValue<int>());
        Assert.Equal(2, local.Count);
    }

    [Fact]
    public void ImportCannotReviveAutomaticStopAuthorityButMatchingLocalAuthorizationSurvives()
    {
        var startup = Startup(""); startup["enabled"] = true;
        startup["automaticStopAuthority"] = new JsonObject
        {
            ["epoch"] = "123:456", ["version"] = 3, ["intentId"] = "old-intent",
            ["intentTimestamp"] = 1234, ["monotonicFrequency"] = 10000000
        };
        startup["automaticAuthorization"] = new JsonObject { ["IntentId"] = "old-intent", ["IntentTimestamp"] = 1234, ["Revoked"] = false };
        var input = new Dictionary<string, byte[]> { ["startup-flow.json"] = Bytes(startup) };

        var moved = ConfigTransferMistletoe.PrepareImport(input, new Dictionary<string, byte[]>());
        var movedConfig = JsonNode.Parse(moved.Files["startup-flow.json"])!;
        Assert.True(movedConfig["enabled"]!.GetValue<bool>());
        Assert.Null(movedConfig["automaticStopAuthority"]);
        Assert.True(movedConfig["automaticAuthorization"]!["Revoked"]!.GetValue<bool>());
        Assert.NotEqual("old-intent", movedConfig["automaticAuthorization"]!["IntentId"]!.GetValue<string>());
        Assert.NotEmpty(moved.Warnings);

        var sameMachine = ConfigTransferMistletoe.PrepareImport(input, new Dictionary<string, byte[]>(), startup.ToJsonString());
        var sameConfig = JsonNode.Parse(sameMachine.Files["startup-flow.json"])!;
        Assert.Equal("123:456", sameConfig["automaticStopAuthority"]!["epoch"]!.GetValue<string>());
        Assert.False(sameConfig["automaticAuthorization"]!["Revoked"]!.GetValue<bool>());
        Assert.Empty(sameMachine.Warnings);
    }

    [Theory]
    [InlineData("{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"workflowId\":\"wf-main\",\"name\":\"x\",\"nodes\":null,\"triggers\":[],\"terminal\":[]}")]
    [InlineData("{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"workflowId\":\"wf-main\",\"name\":\"x\",\"nodes\":[],\"triggers\":[],\"terminal\":[],\"Name\":\"ambiguous\"}")]
    [InlineData("[]")]
    public void MalformedPlanIsRejectedBeforeAnyImportPreparation(string json)
        => Assert.Throws<System.IO.InvalidDataException>(() => ConfigTransferMistletoe.PrepareImport(
            new Dictionary<string, byte[]> { [Entry("wf-main")] = Encoding.UTF8.GetBytes(json) }, new Dictionary<string, byte[]>()));

    [Theory]
    [InlineData("flows/../wf.flow.json")]
    [InlineData("flows/wf/other.flow.json")]
    [InlineData("flows\\wf-main.flow.json")]
    [InlineData("runs/wf-main.flow.json")]
    public void RuntimeOrTraversalEntryIsNotAPlan(string name) => Assert.False(ConfigTransferMistletoe.IsFlowEntry(name));

    private static JsonObject Plan(string id) => new()
    {
        ["schema"] = "mistletoe.workflow", ["schemaVersion"] = 1, ["workflowId"] = id, ["name"] = "计划",
        ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = "node-one", ["kind"] = "resource.oneDragonConfig",
            ["ref"] = new JsonObject { ["config"] = "daily", ["revision"] = "revision" }, ["strategies"] = new JsonArray() }),
        ["triggers"] = new JsonArray(), ["terminal"] = new JsonArray()
    };
    private static JsonObject Startup(string target) => new()
    {
        ["enabled"] = false, ["steps"] = new JsonArray(new JsonObject
        { ["kind"] = "enterTaskCenter", ["taskCenterFlowId"] = target, ["trueSteps"] = new JsonArray(),
            ["falseSteps"] = new JsonArray(), ["fireSteps"] = new JsonArray() })
    };
    private static string Entry(string id) => "flows/" + id + ".flow.json";
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
