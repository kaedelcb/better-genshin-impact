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
        foreach (var op in new[] { "startup.get", "startup.run", "startup.cancel", "task.run.start", "task.flow.save", "operation.get" })
        {
            var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request(op), "r", local: false, Recorder(seen)));
            Assert.Equal("denied", reply["status"]!.GetValue<string>());
        }
        var forged = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("startup.run", browserId: "wb-x", secret: new string('a', 64)), "r", false, Recorder(seen)));
        Assert.Equal("denied", forged["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }

    [Fact]
    public async Task IdentityReportsOwnershipWithoutExecuting_AndWrongInstanceIsRejected()
    {
        var (vm, seen) = NewVm();
        var identity = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("web.identity", instance: "other"), "r", false, Recorder(seen)));
        Assert.Equal("ok", identity["status"]!.GetValue<string>());
        Assert.False(identity["data"]!["owner"]!.GetValue<bool>());
        Assert.Equal("inst-a", identity["data"]!["instanceId"]!.GetValue<string>());
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

    [Fact]
    public async Task OversizedPayloadIsRejectedWithoutExecution()
    {
        var (vm, seen) = NewVm();
        var reply = Reply(await vm.ExecuteMistletoeWebCoreAsync(Request("task.flow.import", payload: JsonValue.Create(new string('x', 2_000_100))), "r", true, Recorder(seen)));
        Assert.Equal("rejected", reply["status"]!.GetValue<string>());
        Assert.Empty(seen);
    }
}
