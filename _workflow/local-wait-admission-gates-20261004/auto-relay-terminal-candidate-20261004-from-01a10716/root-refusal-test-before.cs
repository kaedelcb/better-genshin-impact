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
