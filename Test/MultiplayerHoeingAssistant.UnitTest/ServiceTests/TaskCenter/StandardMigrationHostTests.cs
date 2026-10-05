using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class StandardMigrationHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "standard-host-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task PreparedLegacyDataInstallsThroughBgiThenActivatesAndRestoresBothRoots()
    {
        var user = Path.Combine(_root, "oldUser");
        Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        var source = Path.Combine(user, "OneDragon", "配置A.json");
        File.WriteAllText(source, "{\"Name\":\"配置A\",\"TaskEnabledList\":{\"领取邮件\":true}}");
        var original = File.ReadAllBytes(source);
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "cache.json"), () => null, () => true, () => null);
        var transport = new Transport(original, Path.Combine(_root, "connectedUser"));
        host.ResourceEditorTransportForTest = transport;
        Assert.True((await host.PrepareLegacyMigrationAsync(user)).Ok);
        var id = host.Workflows.List().Single().WorkflowId;
        var flowBytes = File.ReadAllBytes(host.Workflows.List().Single().FilePath);
        var activated = await host.ActivateMigrationCandidateAsync(id);
        Assert.True(activated.Ok, activated.Message);
        Assert.Equal("active", host.Workflows.LoadSnapshot(id).Document.Activation!.Status);
        Assert.Equal(1, transport.Installs);
        Assert.NotEqual(original, transport.Current);
        Assert.Equal(original, File.ReadAllBytes(source));
        var rolledBack = await host.RollbackMigrationAsync(id);
        Assert.True(rolledBack.Ok, rolledBack.Message);
        Assert.Equal(original, transport.Current);
        Assert.Equal(flowBytes, File.ReadAllBytes(host.Workflows.List().Single().FilePath));
    }

    [Fact]
    public async Task SwitchingBgiRootCannotReportWholeRollbackWhileOriginalStandardRemainsInstalled()
    {
        var user = Path.Combine(_root, "oldUser"); Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        var source = Path.Combine(user, "OneDragon", "配置A.json");
        File.WriteAllText(source, "{\"Name\":\"配置A\",\"TaskEnabledList\":{\"领取邮件\":true}}");
        var original = File.ReadAllBytes(source);
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "cache.json"), () => null, () => true, () => null);
        var transport = new Transport(original, Path.Combine(_root, "connectedUser")); host.ResourceEditorTransportForTest = transport;
        Assert.True((await host.PrepareLegacyMigrationAsync(user)).Ok);
        var id = host.Workflows.List().Single().WorkflowId;
        Assert.True((await host.ActivateMigrationCandidateAsync(id)).Ok);
        var installed = transport.Current.ToArray();
        transport.DifferentRoot = true;
        var result = await host.RollbackMigrationAsync(id);
        Assert.False(result.Ok);
        Assert.Equal(installed, transport.Current);
        transport.DifferentRoot = false;
        Assert.True((await host.RollbackMigrationAsync(id)).Ok);
        Assert.Equal(original, transport.Current);
    }

    private sealed class Transport(byte[] original, string userRoot) : IResourceCatalogTransport
    {
        public byte[] Current = original;
        public int Installs;
        public bool DifferentRoot;
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            var data = JsonSerializer.SerializeToElement(payload);
            if (operation == "ext.config.migrateStandard")
            {
                var action = data.GetProperty("action").GetString();
                var root = DifferentRoot ? userRoot + "-other" : userRoot;
                if (action == "status") return Task.FromResult<string?>(JsonSerializer.Serialize(new { status = DifferentRoot || Installs == 0 ? "absent" : "installed", userRoot = root }));
                if (data.TryGetProperty("expectedUserRoot", out var expected) && expected.GetString() != root) return Task.FromResult<string?>(null);
                if (action == "install")
                {
                    Current = File.ReadAllBytes(data.GetProperty("files")[0].GetProperty("contentFile").GetString()!);
                    Installs++;
                }
                else if (action == "rollback") Current = original;
                return Task.FromResult<string?>(JsonSerializer.Serialize(new { status = action == "rollback" ? "rolledBack" : "installed", userRoot = root }));
            }
            return Task.FromResult<string?>(JsonSerializer.Serialize(new
            {
                configRevision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Current)),
                tasks = Array.Empty<object>()
            }));
        }
    }
}
