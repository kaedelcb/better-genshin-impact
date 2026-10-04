using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class ReopenedMigrationActivationObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "r56-idempotent-read-" + Guid.NewGuid().ToString("N"));
    private sealed class Quiet : IDisposable { public void Dispose() { } }

    [Theory]
    [InlineData("clean")]
    [InlineData("changed")]
    [InlineData("samebytes-id")]
    public void ReopenedActivation_ObservesOriginalWholeRootWithoutPublishingOrReacquiringOldWindow(string drift)
    {
        var config = Path.Combine(_root, "cfg");
        var artifacts = Path.Combine(_root, "tx");
        Directory.CreateDirectory(config);
        var data = Path.Combine(config, "plan.flow.json");
        File.WriteAllText(data, """
            {"schema":"mistletoe.workflow","schemaVersion":1,"name":"baseline",
             "activation":{"status":"candidate-ready"},"nodes":[
              {"nodeId":"n-1","kind":"resource.oneDragonConfig","ref":{"config":"old","revision":"rev-1"}}]}
            """);
        using (var original = new MigrationSwitchTransaction(config, artifacts,
            () => DateTimeOffset.UtcNow, () => new Quiet(),
            effectService: new WorkflowFileMigrationEffectService()))
        {
            Assert.True(original.BeginTransaction("observed-activation").Success);
            Assert.True(original.TakeSnapshot().Success);
            Assert.True(original.RecordChanges([new() { Path = "plan.flow.json", Kind = ChangeKind.Modified }]).Success);
            var reference = original.ApplyReferenceUpdate(new([
                new("plan.flow.json", ChangeKind.Modified, RenameFrom: "old", RenameTo: "new")]));
            Assert.True(reference.Success, reference.Reason);
            var activation = original.ActivateCandidate(new("plan.flow.json", "candidate-ready", "active"));
            Assert.True(activation.Success, activation.Reason);
        }

        if (drift == "changed") File.AppendAllText(data, " ");
        if (drift == "samebytes-id")
        {
            var bytes = File.ReadAllBytes(data);
            File.Move(data, Path.Combine(_root, "retained-original"));
            File.WriteAllBytes(data, bytes);
        }
        var windowCalls = 0;
        using var reopened = new MigrationSwitchTransaction(config, artifacts,
            () => DateTimeOffset.UtcNow, () => { windowCalls++; return new Quiet(); },
            effectService: new WorkflowFileMigrationEffectService());
        Assert.True(reopened.TryAcquireExclusive().Success);
        var before = Observe();
        var result = reopened.ActivateCandidate(new("plan.flow.json", "candidate-ready", "active"));
        Assert.True(result.Success == (drift == "clean"), result.Reason);
        Assert.Equal(0, windowCalls);
        Assert.Equal(before, Observe());
    }

    private string[] Observe() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(_root, path) + ":" +
            (Directory.Exists(path) ? "directory" : Path.GetExtension(path) == ".lock" ? "held-lock" : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
