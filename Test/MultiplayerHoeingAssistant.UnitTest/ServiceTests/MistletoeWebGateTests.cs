using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>网页槲寄生门禁：本人核对先于一切执行；转交 PC 远程中心时身份、实例和操作身份保持不变。</summary>
public class MistletoeWebGateTests
{
    private static (MainViewModel Vm, List<RemoteCommand> Seen) NewVm(bool observer = false)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "网页门禁测试必须使用隔离 NEXUSBGI_DATA_ROOT");
        var vm = new MainViewModel();
        var config = new AssistConfig { PlayerUid = "owner-uid", PlayerName = "执行端", ClientInstanceId = "inst-a", ObserverMode = observer };
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, config);
        // 每个测试独立的授权记录文件，不碰真实或其他测试的记录。
        var store = new WebOwnerAuthorizationStore(Path.Combine(AssistantDataDirectory.Root, "gate-" + Guid.NewGuid().ToString("N") + ".json"));
        typeof(MainViewModel).GetField("_webOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, store);
        return (vm, []);
    }

    private static JsonObject Request(string operation, string instance = "inst-a", string? browserId = null, string? secret = null,
        JsonNode? payload = null, string requestId = "abc123")
    {
        var request = new JsonObject { ["operation"] = operation, ["targetInstanceId"] = instance, ["requestId"] = requestId };
        if (browserId is not null) request["browserId"] = browserId;
        if (secret is not null) request["browserSecret"] = secret;
        if (payload is not null) request["payload"] = payload;
        return request;
    }

    private static JsonObject Reply(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();

    private static Func<RemoteCommand, Task<RemoteCommand?>> Recorder(List<RemoteCommand> seen) => command =>
    {
        seen.Add(command);
        var reply = new RemoteCenterReply { Status = "ok", Message = "handled", TargetInstanceId = "inst-a", OperationId = command.CommandId };
        return Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command, "owner-uid", "执行端", reply));
    };

    [Fact]
    public async Task RemoteRequestWithoutOwnerCredentialIsDeniedBeforeAnyExecution()
    {
        var (vm, seen) = NewVm();
        foreach (var op in new[] { "startup.get", "startup.run", "startup.cancel", "task.run.start", "task.flow.save", "operation.get", "registers.get", "registers.configure", "registers.reset", "registers.set", "task.trigger.cancel", "task.prompt.answer" })
        {
            var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request(op), "r", local: false, Recorder(seen)));
            Assert.Equal("denied", reply["status"]!.GetValue<string>());
        }
        var forged = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("startup.run", browserId: "wb-x", secret: new string('a', 64)), "r", false, Recorder(seen)));
        Assert.Equal("denied", forged["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }

    [Fact]
    public async Task BgiUpdateCatalog_IsAvailableOnlyToPairedLocalOrAuthorizedExecutorBrowser()
    {
        var (vm, seen) = NewVm();
        const string operation = "bgi.update.catalog";
        Task<object> Execute(JsonObject request, bool local) => vm.ExecuteMistletoeWebCoreAsync(request, "r", local, Recorder(seen));
        Assert.Equal("denied", Reply(await Execute(Request(operation), false))["status"]!.GetValue<string>());
        Assert.Equal("denied", Reply(await Execute(Request("bgi.update.hint"), false))["status"]!.GetValue<string>());
        Assert.Equal("rejected", Reply(await Execute(Request(operation, "different"), true))["status"]!.GetValue<string>());
        var (code, _) = vm.WebOwners.CreateCode();
        var (browser, secret) = vm.WebOwners.Redeem(code, "更新测试");
        var remote = Reply(await Execute(Request(operation, browserId: browser, secret: secret), false));
        var local = Reply(await Execute(Request(operation), true));
        Assert.Equal("ok", remote["status"]!.GetValue<string>());
        Assert.Equal("ok", local["status"]!.GetValue<string>());
        var hint = Reply(await Execute(Request("bgi.update.hint", browserId: browser, secret: secret), false));
        Assert.Equal("ok", hint["status"]!.GetValue<string>());
        Assert.False(hint["data"]!["available"]!.GetValue<bool>());
        Assert.Equal("autoBGI源", remote["data"]!["sources"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "cnb")!["name"]!.GetValue<string>());
        Assert.Empty(seen);

        var (observer, commands) = NewVm(observer: true);
        var denied = Reply(await observer.ExecuteMistletoeWebCoreAsync(Request(operation), "r", true, Recorder(commands)));
        Assert.Equal("rejected", denied["status"]!.GetValue<string>());
        Assert.Empty(commands);
    }

    [Fact]
    public async Task RoomOrchestrationWithoutExactMachineAndWebReceiptScopeIsRejected()
    {
        var (vm, seen) = NewVm();
        foreach (var request in new[] { Request("room.orchestration.get"), Request("room.orchestration.edit"),
            Request("room.orchestration.save"), Request("operation.get", payload: JsonValue.Create("room-orchestration-fixture")) })
            Assert.Equal("rejected", Reply(await vm.ExecuteMistletoeWebCoreAsync(request, "r", true, Recorder(seen)))["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }

    [Fact]
    public async Task PairedOwnerRoomOperationsUseExactIdentityAndScopedReceipt()
    {
        var (vm, seen) = NewVm();
        var identity = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("web.identity"), "bridge", true, Recorder(seen)));
        var data = identity["data"]!;
        var machine = data["machineId"]!.GetValue<string>();
        var prefixes = data["roomRequestPrefixes"]!;
        var prefix = prefixes["Profiles"]!.GetValue<string>();
        var request = Request("room.profiles.get"); request["expectedMachineId"] = machine;
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(request, "bridge", true, Recorder(seen)));
        Assert.Equal("ok", reply["status"]!.GetValue<string>());
        Assert.Equal(prefix + "abc123", Assert.Single(seen).CommandId);
        var wrong = Request("room.profiles.get", requestId: "wrongid"); wrong["expectedMachineId"] = "different";
        Assert.Equal("rejected", Reply(await vm.ExecuteMistletoeWebCoreAsync(wrong, "r", true, Recorder(seen)))["status"]!.GetValue<string>());
        var desktop = Request("operation.get", payload: JsonValue.Create("room-orchestration-fixture"));desktop["expectedMachineId"] = machine;
        Assert.Equal("rejected", Reply(await vm.ExecuteMistletoeWebCoreAsync(desktop, "r", true, Recorder(seen)))["status"]!.GetValue<string>());
        Assert.Single(seen);
    }

    [Fact]
    public async Task BrowserRoomReceiptsRemainScopedToTheOriginalOwnerAuthorization()
    {
        var (vm, seen) = NewVm();
        (string Browser, string Secret) Grant()
        {
            var (code, _) = vm.WebOwners.CreateCode();
            return vm.WebOwners.Redeem(code, "isolated browser");
        }
        var first = Grant(); var second = Grant();
        async Task<JsonObject> Call(JsonObject r) => Reply(await vm.ExecuteMistletoeWebCoreAsync(r, "gateway", false, Recorder(seen)));
        var identity = (await Call(Request("web.identity", browserId: first.Browser, secret: first.Secret)))["data"]!;
        var prefix = identity["roomRequestPrefixes"]!["Switch"]!.GetValue<string>();
        var machine = identity["machineId"]!.GetValue<string>();
        JsonObject Read((string Browser, string Secret) grant)
        {
            var r = Request("operation.get", browserId: grant.Browser, secret: grant.Secret,
                payload: JsonValue.Create(prefix + "originalswitch"));
            r["expectedMachineId"] = machine;
            return r;
        }
        Assert.Equal("rejected", (await Call(Read(second)))["status"]!.GetValue<string>());
        Assert.Empty(seen);
        Assert.Equal("ok", (await Call(Read(first)))["status"]!.GetValue<string>());
        Assert.Single(seen);
        Assert.True(vm.WebOwners.Revoke(first.Browser));
        Assert.Equal("denied", (await Call(Read(first)))["status"]!.GetValue<string>());
        Assert.Single(seen);
    }

    [Fact]
    public async Task PairedOwnerActualRoomProfileReadsAndUpdatesInactiveProfileOnly()
    {
        var (vm, seen) = NewVm();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var config = (AssistConfig)typeof(MainViewModel).GetField("_config", flags)!.GetValue(vm)!;
        var source = new AssistantRoomProfile { Id = "a", Name = "甲房", ServerUrl = "https://fixture.invalid", RoomCode = "A12345", Password = "fixture-a-password", TeamUids = ["123456789"], ExpectedHoeingPlayers = 4 };
        var target = new AssistantRoomProfile { Id = "b", Name = "乙房", ServerUrl = "https://fixture.invalid", RoomCode = "B12345", Password = "fixture-b-password", TeamUids = ["123456789"], ExpectedHoeingPlayers = 2 };
        config.PlayerUid = "123456789";
        config.StandaloneMode = true;
        config.RoomProfiles = [source, target];
        config.ActiveRoomProfileId = config.LocalRoomProfileId = source.Id;
        source.ApplyTo(config);
        var manager = new AssistConfigManager();
        var root = Path.Combine(AssistantDataDirectory.Root, "web-room-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        typeof(AssistConfigManager).GetField("_configPath", flags)!.SetValue(manager, Path.Combine(root, "config.json"));
        manager.Save(config);
        typeof(MainViewModel).GetField("_configManager", flags)!.SetValue(vm, manager);
        var method = typeof(MainViewModel).GetMethod("ExecuteMistletoeWebAsync", flags)!;
        async Task<JsonObject> Call(JsonObject request) => Reply(await (Task<object>)method.Invoke(vm, [request, "bridge", true, CancellationToken.None])!);
        var identity = await Call(Request("web.identity"));
        var machine = identity["data"]!["machineId"]!.GetValue<string>();
        JsonObject Room(string op, string id, JsonNode? payload = null, string? revision = null)
        {
            var r = Request(op, payload: payload, requestId: id);r["expectedMachineId"] = machine;
            if (revision is not null)r["expectedRevision"]=revision;
            return r;
        }
        var before = await Call(Room("room.profiles.get", "read1"));
        Assert.Equal("ok", before["status"]!.GetValue<string>());
        Assert.DoesNotContain("fixture-a-password", before.ToJsonString());
        Assert.DoesNotContain("fixture-b-password", before.ToJsonString());
        var revision = before["revision"]!.GetValue<string>();
        var draft = new JsonObject { ["profileId"]="b",["name"]="乙房更新",["note"]="fixture",["serverUrl"]="https://fixture.invalid",["roomCode"]="B12345",["memberUids"]=new JsonArray("123456789"),["expectedPlayers"]=3 };
        var saved = await Call(Room("room.profiles.save", "save1", draft.DeepClone(), revision));
        Assert.Equal("ok", saved["status"]!.GetValue<string>());
        Assert.Equal("a", config.ActiveRoomProfileId);Assert.Equal(4, config.ExpectedHoeingPlayers);
        Assert.Equal("fixture-b-password", config.RoomProfiles.Single(p=>p.Id=="b").Password);
        Assert.Equal(3, config.RoomProfiles.Single(p=>p.Id=="b").ExpectedHoeingPlayers);
        var stale = await Call(Room("room.profiles.save", "save2", draft.DeepClone(), revision));
        Assert.Equal("rejected", stale["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task IdentityReportsOwnershipWithoutExecuting_AndWrongInstanceIsRejected()
    {
        var (vm, seen) = NewVm();
        var identity = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("web.identity", instance: "other"), "r", false, Recorder(seen)));
        Assert.Equal("ok", identity["status"]!.GetValue<string>());
        Assert.False(identity["data"]!["owner"]!.GetValue<bool>());
        Assert.Equal("inst-a", identity["data"]!["instanceId"]!.GetValue<string>());
        var capabilities = identity["data"]!["capabilities"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Contains("flowEditV1", capabilities);
        Assert.Contains("unifiedTriggersV1", capabilities);
        var local = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("web.identity"), "r", true, Recorder(seen)));
        Assert.True(local["data"]!["owner"]!.GetValue<bool>());
        var stale = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("startup.get", instance: "inst-old"), "r", true, Recorder(seen)));
        Assert.Equal("rejected", stale["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }

    [Fact]
    public async Task AuthorizedRequestIsForwardedAsOwnCenterRequest_WithWebOperationIdentity()
    {
        var (vm, seen) = NewVm();
        var (code, _) = vm.WebOwners.CreateCode();
        var auth = Reply(await vm.ExecuteMistletoeWebCoreAsync(new JsonObject
        {
            ["operation"] = "web.authorize", ["targetInstanceId"] = "inst-a", ["code"] = code, ["label"] = "测试浏览器"
        }, "r", false, Recorder(seen)));
        Assert.Equal("ok", auth["status"]!.GetValue<string>());
        var browserId = auth["data"]!["browserId"]!.GetValue<string>();
        var secret = auth["data"]!["secret"]!.GetValue<string>();
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("task.flow.save", browserId: browserId, secret: secret,
            payload: new JsonObject { ["workflowId"] = "wf-1" }, requestId: "deadbeef01"), "bridge-other", false, Recorder(seen)));
        Assert.Equal("ok", reply["status"]!.GetValue<string>());
        var command = Assert.Single(seen);
        Assert.Equal(RemoteCenterProtocol.RequestCommand, command.Cmd);
        Assert.Equal("owner-uid", command.SenderUid);
        Assert.Equal(["owner-uid"], command.Target);
        Assert.Equal("inst-a", command.TargetInstanceId);
        // 网页记住的请求身份优先于传输层编号：核对终态时用它查询 operation.get。
        Assert.Equal("web-deadbeef01", command.CommandId);
        Assert.Equal("web-deadbeef01", reply["operationId"]!.GetValue<string>());
        var center = JsonSerializer.Deserialize<RemoteCenterRequest>(RemoteCenterProtocol.PayloadOf(command)!)!;
        Assert.Equal("task.flow.save", center.Operation);
        Assert.Equal("inst-a", center.TargetInstanceId);
        Assert.Equal("wf-1", JsonNode.Parse(center.PayloadJson!)!["workflowId"]!.GetValue<string>());
        // 撤销后同一凭据立即失效。
        Assert.True(vm.WebOwners.Revoke(browserId));
        var revoked = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("startup.get", browserId: browserId, secret: secret), "r", false, Recorder(seen)));
        Assert.Equal("denied", revoked["status"]!.GetValue<string>());
        Assert.Single(seen);
    }

    [Fact]
    public async Task LocalPairedPageNeedsNoCode_ButObserverCannotUseTaskCenter()
    {
        var (vm, seen) = NewVm(observer: true);
        var authorize = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("web.authorize"), "r", true, Recorder(seen)));
        Assert.True(authorize["data"]!["local"]!.GetValue<bool>());
        var task = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("task.list"), "r", true, Recorder(seen)));
        Assert.Equal("rejected", task["status"]!.GetValue<string>());
        Assert.Empty(seen);
        var startup = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("startup.status"), "r", true, Recorder(seen)));
        Assert.Equal("ok", startup["status"]!.GetValue<string>());
        Assert.Single(seen);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task FlowEditorVersionReachesTheCompositionSaveGuard(int? version, bool accepted)
    {
        var (vm, seen) = NewVm();
        var document = new WorkflowDocument
        {
            WorkflowId = "composed-web",
            Nodes = [new WorkflowNode { NodeId = "flow-row", Kind = "control.flow", Flow = new() }]
        };
        var request = Request("task.flow.save", payload: JsonSerializer.SerializeToNode(document));
        request["expectedRevision"] = "revision-before";
        if (version.HasValue) request["flowEditVersion"] = version.Value;
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(request, "bridge", true, command =>
        {
            seen.Add(command);
            var center = JsonSerializer.Deserialize<RemoteCenterRequest>(RemoteCenterProtocol.PayloadOf(command)!)!;
            Assert.Equal(version, center.FlowEditVersion);
            Assert.Equal("revision-before", center.ExpectedRevision);
            var incoming = JsonSerializer.Deserialize<WorkflowDocument>(center.PayloadJson!)!;
            var result = RemoteCenterProtocol.CanSaveFlow(center, incoming, document)
                ? RemoteCenterReply.Ok(revision: "revision-after")
                : RemoteCenterReply.Rejected("编辑端不支持流程/车道");
            return Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command, "owner-uid", "执行端", result));
        }));
        Assert.Single(seen);
        Assert.Equal(accepted ? "ok" : "rejected", reply["status"]!.GetValue<string>());
        if (accepted) Assert.Equal("revision-after", reply["revision"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task InvalidFlowEditorVersionIsRejectedWithoutExecution(string json)
    {
        var (vm, seen) = NewVm();
        var request = Request("task.flow.save", payload: new JsonObject { ["workflowId"] = "composed-web" });
        request["flowEditVersion"] = JsonNode.Parse(json);
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(request, "bridge", true, Recorder(seen)));
        Assert.Equal("rejected", reply["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }

    [Fact]
    public async Task OversizedPayloadIsRejectedWithoutExecution()
    {
        var (vm, seen) = NewVm();
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("task.flow.import", payload: JsonValue.Create(new string('x', 2_000_100))), "r", true, Recorder(seen)));
        Assert.Equal("rejected", reply["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }
}
