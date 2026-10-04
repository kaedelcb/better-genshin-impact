using System;
using System.IO;
using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class R56MigrationRecoveryTests
{
    [Fact]
    public void ExecutePreparedFile_ForeignVersionIsRejectedWithoutAdoptingOrPublishingIntent()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "B");
        var baseline = f.Store.ReadVersion("a.json");
        File.WriteAllText(path, "FOREIGN");
        var called = false;
        var result = f.Journal.ExecutePreparedFile("a.json", baseline, MigrationOperationPhase.Reference,
            _ => { called = true; return Encoding.UTF8.GetBytes("F"); });
        Assert.False(result.Success);
        Assert.False(called);
        Assert.False(result.CommitAttempted);
        Assert.Empty(f.Journal.Load().Operations);
        Assert.Equal("FOREIGN", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("after_data")]
    [InlineData("after_receipt")]
    public void ExecutePreparedFile_InterruptedDataRollsBackButDurableIntentAndBlobSurvive(string station)
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "B");
        var baseline = f.Store.ReadVersion("a.json");
        var result = f.Journal.ExecutePreparedFile("a.json", baseline, MigrationOperationPhase.Reference,
            _ => Encoding.UTF8.GetBytes("F"), fault: point =>
            { if (point == station) throw new IOException("owned_interruption"); });
        Assert.False(result.Success);
        Assert.False(result.CommitConfirmed);
        Assert.Equal("B", File.ReadAllText(path));
        var reopened = new MigrationOperationJournal(f.Artifacts, f.Store);
        var entry = Assert.Single(reopened.Load().Operations);
        Assert.Equal(MigrationOperationState.Prepared, entry.State);
        Assert.Equal("F", File.ReadAllText(Path.Combine(f.Artifacts, entry.Intent.OutputBlob!)));
        Assert.False(File.Exists(Path.Combine(f.Artifacts, "resolutions", entry.Intent.OperationId + ".applied.json")));
    }

    [Fact]
    public void ExecutePreparedFile_ConfirmedDataAndAppliedReceiptReloadTogether()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "B");
        var baseline = f.Store.ReadVersion("a.json");
        var result = f.Journal.ExecutePreparedFile("a.json", baseline, MigrationOperationPhase.Reference,
            _ => Encoding.UTF8.GetBytes("F"));
        Assert.True(result.Success, result.Reason);
        Assert.True(result.CommitConfirmed);
        Assert.Equal("F", File.ReadAllText(path));
        var reopened = new MigrationOperationJournal(f.Artifacts, f.Store);
        var entry = Assert.Single(reopened.Load().Operations);
        Assert.Equal(MigrationOperationState.Applied, entry.State);
        Assert.Equal(result.Output!.Sha256, entry.Intent.ExpectedOutputSha256);
        Assert.True(File.Exists(Path.Combine(f.Artifacts, entry.ResolutionPath!)));
        Assert.Equal(MigrationStage.SnapshotReady, reopened.Load().Stage);
        var captured = reopened.ReadValidatedEvidence();
        Assert.Equal(MigrationOperationJournal.Hash(reopened.Load().Operations), captured.Digest);
        Assert.Equal(3, captured.Files.Count);
        File.WriteAllText(Path.Combine(f.Artifacts, entry.Intent.OutputBlob!), "TAMPERED");
        Assert.Throws<InvalidDataException>(() => reopened.ReadValidatedEvidence());
    }

    [Fact]
    public void PreparedReference_UsesSuppliedBytesOnly_PreservesBomAndUnrelatedValues()
    {
        var text = "{\"nodes\":[{\"ref\":{\"config\":\"旧配置\"},\"label\":\"旧配置\"}],\"other\":\"旧配置\"}";
        var input = new UTF8Encoding(true).GetPreamble();
        input = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(input, Encoding.UTF8.GetBytes(text)));
        var before = (byte[])input.Clone();
        var service = new WorkflowFileMigrationEffectService();
        Assert.True(service.TryPrepareReference(new("not-on-disk.json", ChangeKind.Modified,
            RenameFrom: "旧配置", RenameTo: "新配置"), input, out var output, out var reason), reason);
        Assert.Equal(before, input);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, output[..3]);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(output).TrimStart('\uFEFF'))!;
        Assert.Equal("新配置", (string?)doc["nodes"]![0]!["ref"]!["config"]);
        Assert.Equal("旧配置", (string?)doc["nodes"]![0]!["label"]);
        Assert.Equal("旧配置", (string?)doc["other"]);
    }

    [Fact]
    public void PreparedActivation_RejectsDifferentInputHash_AndReturnsExactIdempotentBytes()
    {
        var service = new WorkflowFileMigrationEffectService();
        var candidate = Encoding.UTF8.GetBytes("{\"activation\":{\"status\":\"candidate-ready\"},\"keep\":42}");
        var request = new MigrationActivationRequest("candidate.json", "candidate-ready", "active",
            MigrationFileVersion.Hash(candidate));
        Assert.False(service.TryPrepareActivation(request, Encoding.UTF8.GetBytes("{\"activation\":{\"status\":\"active\"}}"),
            out var rejected, out _, out var reason));
        Assert.Empty(rejected);
        Assert.StartsWith("activation_content_hash_mismatch", reason);
        Assert.True(service.TryPrepareActivation(request, candidate, out var active, out var already, out reason), reason);
        Assert.False(already);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(active)!;
        Assert.Equal("active", (string?)doc["activation"]!["status"]);
        Assert.Equal(42, (int?)doc["keep"]);
        Assert.True(service.TryPrepareActivation(request with { ExpectedContentHash = MigrationFileVersion.Hash(active) },
            active, out var unchanged, out already, out reason), reason);
        Assert.True(already);
        Assert.Equal(active, unchanged);
        Assert.NotSame(active, unchanged);
    }

    [Fact]
    public void PreparedReference_RejectsMalformedShapeOrExistingAddedInput()
    {
        var service = new WorkflowFileMigrationEffectService();
        var target = new MigrationReferenceWriteTarget("a.json", ChangeKind.Modified, RenameFrom: "A", RenameTo: "B");
        Assert.False(service.TryPrepareReference(target, Encoding.UTF8.GetBytes("{\"nodes\":[7]}"), out var output, out _));
        Assert.Empty(output);
        Assert.False(service.TryPrepareReference(new("a.json", ChangeKind.Added, NewContent: "new"), [], out output, out _));
        Assert.Empty(output);
        Assert.True(service.TryPrepareReference(new("a.json", ChangeKind.Added, NewContent: "新内容"), null, out output, out _));
        Assert.Equal("新内容", Encoding.UTF8.GetString(output));
    }

    private static (string Config, string Artifacts, WindowsTxfMigrationVersionStore Store, MigrationOperationJournal Journal) Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "r56-owned-journal-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(artifacts);
        var store = new WindowsTxfMigrationVersionStore(config, artifacts);
        var journal = new MigrationOperationJournal(artifacts, store);
        journal.Create("owned-journal");
        return (config, artifacts, store, journal);
    }

    [Fact]
    public void PreparedOnly_NewInstanceCancelsWithoutClaimingData_ThenCanFinishRollback()
    {
        var f = Fixture();
        File.WriteAllText(Path.Combine(f.Config, "a.json"), "B");
        var input = f.Store.ReadVersion("a.json");
        var id = Guid.NewGuid().ToString("N");
        f.Journal.Prepare(new(id, 1, MigrationOperationPhase.Reference, "a.json", input,
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), "output.bin", null, []), Encoding.UTF8.GetBytes("F"));
        var reopened = new MigrationOperationJournal(f.Artifacts, f.Store);
        var cancelled = reopened.CancelPrepared(id, f.Store.ReadVersion("a.json"), "fresh-witness", true, "producer_exited_before_commit");
        Assert.Equal(MigrationOperationState.Cancelled, cancelled.Operations[0].State);
        Assert.Equal(id, cancelled.Operations[0].Intent.OperationId);
        Assert.Equal("B", File.ReadAllText(Path.Combine(f.Config, "a.json")));
        reopened.SetStage(MigrationStage.RollingBack, null);
        var complete = reopened.SetStage(MigrationStage.RolledBack, null, "fresh-baseline-witness");
        Assert.Equal(MigrationStage.RolledBack, complete.Stage);
        Assert.Equal(MigrationOperationState.Cancelled, reopened.Load().Operations[0].State);
    }

    [Fact]
    public void CancelledReferenceCannotBeCountedAsForwardSuccess()
    {
        var f = Fixture();
        var id = Guid.NewGuid().ToString("N");
        f.Journal.Prepare(new(id, 1, MigrationOperationPhase.Reference, "a.json", MigrationFileVersion.Absent(),
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), "output.bin", null, []), Encoding.UTF8.GetBytes("F"));
        f.Journal.CancelPrepared(id, MigrationFileVersion.Absent(), "fresh-witness", true, "no_write");
        Assert.Throws<InvalidOperationException>(() => f.Journal.SetStage(MigrationStage.ReferenceUpdating, null, "witness"));
        Assert.Equal(MigrationStage.SnapshotReady, f.Journal.Load().Stage);
    }

    [Fact]
    public void ForwardOperationInBlockedStageIsRejectedBeforePublication()
    {
        var f = Fixture();
        f.Journal.SetStage(MigrationStage.Blocked, null, blockedReason: "owned_failure");
        Assert.Throws<InvalidDataException>(() => f.Journal.Prepare(new(Guid.NewGuid().ToString("N"), 1,
            MigrationOperationPhase.Reference, "a.json", MigrationFileVersion.Absent(),
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), "output.bin", null, [])));
        Assert.Empty(f.Journal.Load().Operations);
    }

    [Fact]
    public void ForeignPreparedInput_CannotBeCancelledOrClaimed()
    {
        var f = Fixture();
        File.WriteAllText(Path.Combine(f.Config, "a.json"), "B");
        var input = f.Store.ReadVersion("a.json");
        var id = Guid.NewGuid().ToString("N");
        f.Journal.Prepare(new(id, 1, MigrationOperationPhase.Reference, "a.json", input,
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), "output.bin", null, []), Encoding.UTF8.GetBytes("F"));
        File.WriteAllText(Path.Combine(f.Config, "a.json"), "X");
        Assert.Throws<InvalidOperationException>(() => f.Journal.CancelPrepared(id, f.Store.ReadVersion("a.json"), "fresh-witness", true, "cancel"));
        Assert.Equal(MigrationOperationState.Prepared, f.Journal.Load().Operations[0].State);
        Assert.Equal("X", File.ReadAllText(Path.Combine(f.Config, "a.json")));
        Assert.Throws<InvalidOperationException>(() => f.Journal.SetStage(MigrationStage.RolledBack, null, "witness"));
    }

    [Fact]
    public void AppliedPrefixThenPreparedTail_CancelTailAndAppendRealRestore()
    {
        var f = Fixture();
        File.WriteAllText(Path.Combine(f.Config, "a.json"), "B");
        var baseline = f.Store.ReadVersion("a.json");
        var first = Guid.NewGuid().ToString("N");
        f.Journal.Prepare(new(first, 1, MigrationOperationPhase.Reference, "a.json", baseline,
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), "first.bin", null, []), Encoding.UTF8.GetBytes("F"));
        var write = f.Store.ExecuteFile("a.json", baseline, (_, _, _) => new(Encoding.UTF8.GetBytes("F"), false,
            (output, directories) => f.Journal.AppliedArtifacts(first, output, directories)));
        Assert.True(write.Success, write.Reason);
        var second = Guid.NewGuid().ToString("N");
        f.Journal.Prepare(new(second, 2, MigrationOperationPhase.Reference, "b.json", MigrationFileVersion.Absent(),
            MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("OTHER")), "second.bin", null, []), Encoding.UTF8.GetBytes("OTHER"));
        var reopened = new MigrationOperationJournal(f.Artifacts, f.Store);
        reopened.CancelPrepared(second, MigrationFileVersion.Absent(), "fresh-witness", true, "not_committed");
        reopened.SetStage(MigrationStage.RollingBack, null);
        var restoreId = Guid.NewGuid().ToString("N");
        reopened.Prepare(new(restoreId, 3, MigrationOperationPhase.Restore, "a.json", write.Output!,
            baseline.Sha256, "baseline.bin", first, []), Encoding.UTF8.GetBytes("B"));
        var restored = f.Store.ExecuteFile("a.json", write.Output!, (_, _, _) => new(Encoding.UTF8.GetBytes("B"), false,
            (output, directories) => reopened.AppliedArtifacts(restoreId, output, directories)));
        Assert.True(restored.Success, restored.Reason);
        var final = reopened.SetStage(MigrationStage.RolledBack, null, "fresh-final-witness");
        Assert.Equal(3, final.Operations.Count);
        Assert.Equal(MigrationOperationState.Cancelled, final.Operations[1].State);
        Assert.Equal("B", File.ReadAllText(Path.Combine(f.Config, "a.json")));
        Assert.False(File.Exists(Path.Combine(f.Config, "b.json")));
    }
}
