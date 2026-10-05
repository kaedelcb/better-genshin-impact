using System.Text;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class StandardConfigurationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "standard-install-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"TaskEnabledList\":{\"领取邮件\":true}}\r\n")).ToArray();
    private static readonly byte[] Standard = Encoding.UTF8.GetBytes("{\"TaskEnabledList\":{\"task-1\":true},\"TaskDefinitions\":{\"task-1\":\"领取邮件\"},\"TaskOrder\":[\"task-1\"]}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Target(string name = "原配置") => Path.Combine(_root, "OneDragon", name + ".json");
    private void Seed(string name = "原配置") { Directory.CreateDirectory(Path.GetDirectoryName(Target(name))!); File.WriteAllBytes(Target(name), Original); }
    private InstanceIpcEnvelope Request(string action = "install", string name = "原配置") => new()
    {
        Operation = StandardConfigurationMigration.Operation, RequestId = Guid.NewGuid(),
        Data = new JObject
        {
            ["installId"] = "stable-install", ["action"] = action,
            ["files"] = new JArray(new JObject { ["configName"] = name, ["contentBase64"] = Convert.ToBase64String(Standard),
                ["sourceRevision"] = TaskConfigurationContract.Revision(Original) })
        }
    };
    private Task<InstanceIpcEnvelope> Send(InstanceIpcEnvelope request, Action<int>? after = null)
        => StandardConfigurationMigration.DispatchAsync(request, _root, () => new Lease(), afterInstallFile: after);
    private sealed class Lease : IDisposable { public void Dispose() { } }

    [Fact]
    public async Task ActualHostAndBgiWriterMigrateSameRootAndRecoverBothDocuments()
    {
        var user = Path.Combine(_root, "native");
        var source = Path.Combine(user, "OneDragon", "原配置.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, Original);
        var data = Path.Combine(_root, "assistant");
        var host = new MultiplayerHoeingAssistant.Services.TaskCenterHost(Path.Combine(data, "flows"), Path.Combine(data, "runs"),
            Path.Combine(data, "cache.json"), () => null, () => true, () => null);
        typeof(MultiplayerHoeingAssistant.Services.TaskCenterHost).GetProperty("ResourceEditorTransportForTest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(host, new ActualTransport(user));
        var prepared = await host.PrepareLegacyMigrationAsync(user);
        Assert.True(prepared.Ok, prepared.Message);
        var entry = host.Workflows.List().Single();
        var oldFlow = File.ReadAllBytes(entry.FilePath);
        var activated = await host.ActivateMigrationCandidateAsync(entry.WorkflowId);
        Assert.True(activated.Ok, activated.Message);
        var standard = File.ReadAllBytes(source);
        Assert.NotEqual(Original, standard);
        Assert.Equal("active", host.Workflows.LoadSnapshot(entry.WorkflowId).Document.Activation!.Status);
        var rollback = await host.RollbackMigrationAsync(entry.WorkflowId);
        Assert.True(rollback.Ok, rollback.Message);
        Assert.Equal(Original, File.ReadAllBytes(source));
        Assert.Equal(oldFlow, File.ReadAllBytes(entry.FilePath));
        Assert.True((await host.RollbackMigrationAsync(entry.WorkflowId)).Ok);
    }

    [Fact]
    public async Task ActualPhysicalSlotRejectsBusyAndAllowsPassiveCaptureWithoutJobs()
    {
        Seed();
        await BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.WaitAsync();
        try
        {
            var busy = await StandardConfigurationMigration.DispatchAsync(Request(), _root);
            Assert.False(busy.Success); Assert.Equal(Original, File.ReadAllBytes(Target()));
        }
        finally { BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.Release(); }
        var registry = JobRegistry.Instance;
        var capture = registry.TriggerDispatcherRunning;
        registry.SetTriggerDispatcherRunning(true);
        try
        {
            var idle = await StandardConfigurationMigration.DispatchAsync(Request(), _root);
            Assert.True(idle.Success, idle.ErrorMessage); Assert.Equal(Standard, File.ReadAllBytes(Target()));
        }
        finally { registry.SetTriggerDispatcherRunning(capture); }
    }

    private sealed class ActualTransport(string root) : MultiplayerHoeingAssistant.Services.IResourceCatalogTransport
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public async Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            var request = new InstanceIpcEnvelope { Operation = operation, RequestId = Guid.NewGuid(),
                Data = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(payload)) };
            var response = operation == StandardConfigurationMigration.Operation
                ? await StandardConfigurationMigration.DispatchAsync(request, root, () => new Lease())
                : await ExternalInterfaceConfigurationPlane.DispatchAsync(request, new(root));
            return response.Success == true ? response.Data!.ToString() : null;
        }
    }

    [Fact]
    public async Task NativeRootInstallationReadbackAndRollbackPreserveExactOldBytes()
    {
        Seed();
        var result = await Send(Request()); Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Standard, File.ReadAllBytes(Target()));
        Assert.Equal("installed", result.Data!["status"]!.Value<string>());
        Assert.True((await Send(Request())).Success);
        Assert.True((await Send(Request("rollback"))).Success);
        Assert.Equal(Original, File.ReadAllBytes(Target()));
        Assert.True((await Send(Request("rollback"))).Success);
        Assert.True((await Send(Request())).Success);
        Assert.Equal(Standard, File.ReadAllBytes(Target()));
    }

    [Fact]
    public async Task OtherRevisionIsNeverOverwritten()
    {
        Seed(); var changed = Encoding.UTF8.GetBytes("{\"user\":7}"); File.WriteAllBytes(Target(), changed);
        Assert.False((await Send(Request())).Success); Assert.Equal(changed, File.ReadAllBytes(Target()));
    }

    [Fact]
    public async Task LaterUserEditIsNeverOverwrittenOnRollback()
    {
        Seed(); var changed = Encoding.UTF8.GetBytes("{\"user\":7}");
        Assert.True((await Send(Request())).Success);
        File.WriteAllBytes(Target(), changed);
        Assert.False((await Send(Request("rollback"))).Success); Assert.Equal(changed, File.ReadAllBytes(Target()));
    }

    [Fact]
    public async Task ChangedCandidateFileIsRejectedBeforeAnyNativeWrite()
    {
        Seed(); var candidate = Path.Combine(_root, "candidate.json"); File.WriteAllBytes(candidate, Standard);
        var request = Request(); var file = (JObject)request.Data!["files"]![0]!;
        file.Remove("contentBase64"); file["contentFile"] = candidate; file["contentRevision"] = TaskConfigurationContract.Revision(Standard);
        File.WriteAllText(candidate, "{}");
        Assert.False((await Send(request)).Success); Assert.Equal(Original, File.ReadAllBytes(Target()));
    }

    [Fact]
    public async Task PartialInstallationCanRestoreAllOriginalsAfterRestart()
    {
        Seed(); Seed("第二配置");
        var request = Request();
        var second = (JObject)request.Data!["files"]![0]!.DeepClone(); second["configName"] = "第二配置";
        ((JArray)request.Data["files"]!).Add(second);
        var result = await Send(request, count => { if (count == 1) throw new IOException("simulated_process_interruption"); });
        Assert.False(result.Success); Assert.Equal(Standard, File.ReadAllBytes(Target()));
        Assert.Equal(Original, File.ReadAllBytes(Target("第二配置")));
        Assert.True((await Send(Request("rollback"))).Success);
        Assert.Equal(Original, File.ReadAllBytes(Target())); Assert.Equal(Original, File.ReadAllBytes(Target("第二配置")));
    }

    [Fact]
    public async Task NewFileRollbackOnlyRemovesOwnedByteIdenticalOutput()
    {
        Assert.True((await Send(Request())).Success); Assert.True(File.Exists(Target()));
        Assert.True((await Send(Request("rollback"))).Success); Assert.False(File.Exists(Target()));
        Assert.True((await Send(Request())).Success);
        File.WriteAllText(Target(), "user edit");
        Assert.False((await Send(Request("rollback"))).Success); Assert.Equal("user edit", File.ReadAllText(Target()));
    }

    [Fact]
    public async Task BusyAndUnsafeNamesHaveZeroConfigurationWrites()
    {
        Seed();
        var busy = await StandardConfigurationMigration.DispatchAsync(Request(), _root,
            () => throw new InvalidOperationException("execution_busy"));
        Assert.False(busy.Success); Assert.Equal(Original, File.ReadAllBytes(Target()));
        Assert.False((await Send(Request(name: "../outside"))).Success);
        Assert.Equal(Original, File.ReadAllBytes(Target()));
    }
}
