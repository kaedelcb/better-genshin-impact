using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class R56MigrationConsistencyTests
{
    [Fact]
    public async Task ControlledRootWindow_BlocksTwoActualWriterProcessesUntilSnapshotPublicationEnds()
    {
        var executionRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var evidenceRoot = Path.Combine(executionRoot, "controlled-writer-processes");
        var probe = Path.Combine(AppContext.BaseDirectory, "ControlledWriterProbe.dll");
        Assert.True(File.Exists(probe), "Build the controlled writer probe into this execution's fresh products.");
        var root = Path.Combine(Path.GetTempPath(), "r56-controlled-process-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "a.json"), "B");
        File.WriteAllText(Path.Combine(config, "b.json"), "B");
        var evidence = Path.Combine(evidenceRoot, "process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var transaction = new MigrationSwitchTransaction(config, artifacts, requireQuiescence: false,
            effectService: new WorkflowFileMigrationEffectService());
        Assert.True(transaction.BeginTransaction("controlled-process").Success);
        Process Start(string actor)
        {
            var start = new ProcessStartInfo(@"C:/Program Files/dotnet/dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { probe, config, artifacts, actor, Path.Combine(evidence, actor + ".jsonl") })
                start.ArgumentList.Add(argument);
            return Process.Start(start)!;
        }
        using var first = Start("actor-A");
        using var second = Start("actor-B");
        MigrationManifest? manifest = null;
        var blockedBeforeCapture = false;
        try
        {
        bool Blocked(string actor)
        {
            var path = Path.Combine(evidence, actor + ".jsonl");
            return File.Exists(path) && File.ReadAllText(path).Contains("\"state\":\"blocked\"");
        }
        var timer = Stopwatch.StartNew();
        while ((!Blocked("actor-A") || !Blocked("actor-B")) && timer.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(20);
        blockedBeforeCapture = Blocked("actor-A") && Blocked("actor-B");
        Assert.True(blockedBeforeCapture);
        Assert.False(first.HasExited);
        Assert.False(second.HasExited);
        Assert.DoesNotContain("\"state\":\"acquired\"", File.ReadAllText(Path.Combine(evidence, "actor-A.jsonl")));
        Assert.DoesNotContain("\"state\":\"acquired\"", File.ReadAllText(Path.Combine(evidence, "actor-B.jsonl")));
        Assert.True(transaction.TakeSnapshot().Success);
        manifest = transaction.LoadValidated()!;
        Assert.NotNull(manifest.ControlledBaseline);
        Assert.Equal("registered-root-authority", manifest.ControlledBaseline!.WriterDomain);
        Assert.Equal(Environment.ProcessId, manifest.ControlledBaseline.ProcessId);
        Assert.Equal("B", File.ReadAllText(Path.Combine(config, "a.json")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(config, "b.json")));
        transaction.Dispose();
        await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(File.ReadAllText(Path.Combine(config, "a.json")), File.ReadAllText(Path.Combine(config, "b.json")));
        }
        finally
        {
            transaction.Dispose();
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            JsonElement[] Events(string actor) => File.ReadAllLines(Path.Combine(evidence, actor + ".jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            File.WriteAllText(Path.Combine(executionRoot, "controlled-writer-evidence.json"), JsonSerializer.Serialize(new
            {
                Fixture = root, ParentProcess = Environment.ProcessId,
                FirstProcess = first.Id, SecondProcess = second.Id, FirstExit = first.ExitCode, SecondExit = second.ExitCode,
                FirstEvents = Events("actor-A"), SecondEvents = Events("actor-B"),
                Baseline = manifest?.ControlledBaseline, RegisteredWritersBlockedBeforeCapture = blockedBeforeCapture,
                FinalA = File.ReadAllText(Path.Combine(config, "a.json")), FinalB = File.ReadAllText(Path.Combine(config, "b.json")),
                Scope = "Actual isolated registered writer domain; production all-writer coverage remains unaccepted."
            }));
        }
    }

    [Fact]
    public void SameConfigDifferentArtifactRoots_CannotBypassAuthority()
    {
        var root = Path.Combine(Path.GetTempPath(), "r56-authority-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var firstRoot = Path.Combine(root, "first");
        var otherRoot = Path.Combine(root, "other");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(otherRoot);
        using (var first = new MigrationRootAuthority(config, firstRoot))
        {
            first.SetPending("first-transaction");
            Assert.ThrowsAny<Exception>(() => new MigrationRootAuthority(config, otherRoot));
        }
        Assert.Throws<InvalidOperationException>(() => new MigrationRootAuthority(config, otherRoot));
        using var reopened = new MigrationRootAuthority(config, firstRoot);
        Assert.Equal("first-transaction", reopened.PendingTransactionId);
        Assert.Throws<InvalidOperationException>(() => reopened.SetPending("different-transaction"));
    }

    [Fact]
    public async Task AuthorityAcquiredOnOneThread_CanBeReleasedOnAnother()
    {
        var root = Path.Combine(Path.GetTempPath(), "r56-thread-authority-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(artifacts);
        var authority = new MigrationRootAuthority(config, artifacts);
        Assert.True(authority.IsHeld);
        await Task.Run(authority.Dispose);
        Assert.False(authority.IsHeld);
        using var next = new MigrationRootAuthority(config, artifacts);
        Assert.True(next.IsHeld);
    }

    [Fact]
    public void AuthorityPendingAndMainArtifact_CommitAndRollbackTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), "r56-pending-pair-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(artifacts);
        using var authority = new MigrationRootAuthority(config, artifacts);
        var store = new WindowsTxfMigrationVersionStore(config, artifacts);
        var first = System.Text.Encoding.UTF8.GetBytes("first-generation");
        store.WriteArtifactsWithAuthority([new("migration-manifest.json", first)], authority, null, "first");
        Assert.Equal("first", authority.PendingTransactionId);
        var manifest = Path.Combine(artifacts, "migration-manifest.json");
        Assert.Equal(first, File.ReadAllBytes(manifest));
        Assert.ThrowsAny<Exception>(() => store.WriteArtifactsWithAuthority(
            [new("migration-manifest.json", System.Text.Encoding.UTF8.GetBytes("uncommitted"), new string('f', 64))],
            authority, "first", "uncommitted"));
        Assert.Equal("first", authority.PendingTransactionId);
        Assert.Equal(first, File.ReadAllBytes(manifest));
        var second = System.Text.Encoding.UTF8.GetBytes("verified-terminal");
        store.WriteArtifactsWithAuthority([new("migration-manifest.json", second, MigrationFileVersion.Hash(first))],
            authority, "first", null);
        Assert.Null(authority.PendingTransactionId);
        Assert.Equal(second, File.ReadAllBytes(manifest));
        Assert.Throws<InvalidOperationException>(() => authority.SetPending(null));
        Assert.Throws<InvalidOperationException>(() => authority.SetPending("foreign-identity", "next"));
        Assert.Null(authority.PendingTransactionId);
        Assert.Equal(second, File.ReadAllBytes(manifest));
    }
}
