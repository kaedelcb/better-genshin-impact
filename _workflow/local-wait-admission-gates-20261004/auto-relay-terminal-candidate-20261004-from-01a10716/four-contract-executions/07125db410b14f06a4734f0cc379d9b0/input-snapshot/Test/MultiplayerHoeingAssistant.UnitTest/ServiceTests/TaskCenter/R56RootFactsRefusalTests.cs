using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class R56RootFactsRefusalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r56-root-facts-" + Guid.NewGuid().ToString("N"));
    private sealed class Quiet : IDisposable { public void Dispose() { } }

    [Theory]
    [InlineData("other-artifact")]
    [InlineData("binding-json")]
    [InlineData("root-lock-directory")]
    [InlineData("artifact-file")]
    public void InvalidObservedRootFacts_RefuseWithoutExceptionOrPersistentChanges(string corruption)
    {
        var config = Path.Combine(_root, "cfg");
        var artifacts = Path.Combine(_root, "tx");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "retained.json"), "{\"value\":17}");
        MigrationSwitchTransaction Create(string target) => new(config, target,
            () => DateTimeOffset.UtcNow, () => new Quiet(),
            effectService: new WorkflowFileMigrationEffectService());
        using (var original = Create(artifacts))
            Assert.True(original.BeginTransaction("retained-owner").Success);

        var binding = Assert.Single(Directory.GetFiles(_root, ".mistletoe-root-*.json"));
        var target = artifacts;
        switch (corruption)
        {
            case "other-artifact":
                target = Path.Combine(_root, "other-tx");
                Directory.CreateDirectory(target);
                break;
            case "binding-json":
                File.WriteAllText(binding, "{ invalid binding");
                break;
            case "root-lock-directory":
                var rootLock = Assert.Single(Directory.GetFiles(_root, ".mistletoe-root-*.lock"));
                File.Move(rootLock, rootLock + ".retained");
                Directory.CreateDirectory(rootLock);
                break;
            case "artifact-file":
                Directory.Move(artifacts, artifacts + ".retained");
                File.WriteAllText(artifacts, "retained non-directory");
                break;
        }

        var before = Observe();
        using var contender = Create(target);
        MigrationResult? result = null;
        var exception = Record.Exception(() => result = contender.TryAcquireExclusive());
        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.False(contender.HoldsExclusiveLock);
        Assert.Equal(before, Observe());
    }

    [Theory]
    [InlineData("missing-main")]
    [InlineData("corrupt-journal")]
    [InlineData("orphan-output")]
    [InlineData("mixed-legacy")]
    public void UnverifiableModernRefusal_PreservesAllFactsThroughRecoveryAndNewEntry(string corruption)
    {
        var config = Path.Combine(_root, "cfg");
        var artifacts = Path.Combine(_root, "tx");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "retained.json"), "{\"value\":17}");
        MigrationSwitchTransaction Create() => new(config, artifacts,
            () => DateTimeOffset.UtcNow, () => new Quiet(),
            effectService: new WorkflowFileMigrationEffectService());
        using (var original = Create())
        {
            var started = original.BeginTransaction("retained-owner");
            Assert.True(started.Success, started.Reason);
            var snapshotted = original.TakeSnapshot();
            Assert.True(snapshotted.Success, snapshotted.Reason);
            Assert.NotNull(original.LoadValidated()!.ControlledBaseline);
            if (corruption == "mixed-legacy")
            {
                var old = original.LoadValidated()!;
                old.SchemaVersion = 1;
                old.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(old);
                var node = System.Text.Json.JsonSerializer.SerializeToNode(old)!.AsObject();
                foreach (var key in new[] { "legacySource", "baselineVersions", "controlledBaseline", "controlledLatest" })
                    node.Remove(key);
                File.WriteAllText(original.ManifestPath, node.ToJsonString());
            }
        }
        var main = Path.Combine(artifacts, "migration-manifest.json");
        var operationRoot = Path.Combine(artifacts, "operations", "retained-owner");
        if (corruption == "missing-main") File.Move(main, main + ".retained");
        if (corruption == "corrupt-journal")
        {
            Directory.CreateDirectory(operationRoot);
            File.WriteAllText(Path.Combine(operationRoot, "operation-journal.json"), "{ unknown journal");
        }
        if (corruption == "orphan-output")
        {
            var outputs = Path.Combine(operationRoot, "outputs");
            Directory.CreateDirectory(outputs);
            File.WriteAllBytes(Path.Combine(outputs, "unexplained.bin"), [7, 11, 19]);
        }
        var before = Observe();
        using (var reopened = Create())
        {
            var admission = reopened.TryAcquireExclusive();
            Assert.False(admission.Success);
            Assert.False(reopened.HoldsExclusiveLock);
            Assert.Equal(before, Observe());
            Assert.False(reopened.RecoverOnStart().Success);
            Assert.Equal(before, Observe());
            Assert.False(reopened.BeginTransaction("must-not-hide-original").Success);
            Assert.Equal(before, Observe());
            var executions = 0;
            Assert.False(reopened.TryRunProduction(() => executions++).Success);
            Assert.Equal(0, executions);
            Assert.Equal(before, Observe());
        }
        Assert.Equal(before, Observe());
    }

    private string[] Observe() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(_root, path) + ":" +
            (Directory.Exists(path) ? "directory" : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
