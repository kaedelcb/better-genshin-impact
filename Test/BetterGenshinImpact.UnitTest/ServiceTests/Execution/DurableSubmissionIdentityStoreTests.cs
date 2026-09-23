using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

public sealed class DurableSubmissionIdentityStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "bgi-r5-prepared-" + Guid.NewGuid().ToString("N"));
    private static readonly (int ProcessId, long StartTicksUtc) Epoch = (123, 456);
    private static readonly string Fingerprint = new('A', 64);

    [Fact]
    public void ReopenedStore_ReusesOriginalHandleAndRejectsDifferentPayload()
    {
        var firstStore = new DurableSubmissionIdentityStore(_directory);
        Assert.True(firstStore.InitializeFreshForProvisioning());
        var first = firstStore.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Prepared, first.Status);
        var original = Assert.IsType<PreparedSubmissionIdentity>(first.Record);

        var reopened = new DurableSubmissionIdentityStore(_directory);
        var same = reopened.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.ExistingObserved, same.Status);
        Assert.Equal(original.Handle, same.Record?.Handle);

        var conflict = reopened.TryPrepare(Epoch, "send-1", new string('B', 64), "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Conflict, conflict.Status);
        Assert.Null(conflict.Record);
        Assert.Equal(original.Handle, reopened.Query(Epoch, "send-1").Record?.Handle);
    }

    [Fact]
    public void FullCapacity_StillAllowsOriginalKeyReadAndReplay()
    {
        var store = new DurableSubmissionIdentityStore(_directory, capacity: 1);
        Assert.True(store.InitializeFreshForProvisioning());
        var first = store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Prepared, first.Status);
        Assert.Equal(PreparedIdentityStatus.Capacity,
            store.TryPrepare(Epoch, "send-2", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityStatus.ExistingObserved,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityLookupStatus.Observed, store.Query(Epoch, "send-1").Status);
    }

    [Fact]
    public void PartialOrCorruptRecord_BlocksNewPreparationWithoutReusingHandle()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.True(store.InitializeFreshForProvisioning());
        var first = store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Prepared, first.Status);
        var path = store.RecordPathFor(Epoch, "send-1");
        File.WriteAllBytes(path, [0x52, 0x35]);

        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, store.Query(Epoch, "send-1").Status);
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-2", Fingerprint, "ext.task.start").Status);
    }

    [Fact]
    public void TargetEpoch_IsPartOfTheImmutableIdentity()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.True(store.InitializeFreshForProvisioning());
        var first = store.TryPrepare(Epoch, "same-key", Fingerprint, "ext.task.start");
        var second = store.TryPrepare((Epoch.ProcessId, Epoch.StartTicksUtc + 1), "same-key", Fingerprint,
            "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Prepared, first.Status);
        Assert.Equal(PreparedIdentityStatus.Prepared, second.Status);
        Assert.NotEqual(first.Record?.Handle, second.Record?.Handle);
        Assert.Equal(first.Record?.Handle, store.Query(Epoch, "same-key").Record?.Handle);
    }

    [Fact]
    public void LostRecordOrManifest_DoesNotAllocateReplacementIdentity()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Prepared,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        File.Delete(store.RecordPathFor(Epoch, "send-1"));
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, store.Query(Epoch, "send-1").Status);
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);

        File.Delete(Path.Combine(_directory, "index.lock"));
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, store.Query(Epoch, "send-1").Status);
    }

    [Fact]
    public void MissingStoreDomain_RequiresExplicitInitialization()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, store.Query(Epoch, "send-1").Status);
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Prepared,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
    }

    [Fact]
    public void OversizedRecord_ReturnsUncertainBeforeAllocatingItsContents()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Prepared,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        using (var stream = new FileStream(store.RecordPathFor(Epoch, "send-1"), FileMode.Open, FileAccess.Write))
            stream.SetLength(64L * 1024 * 1024);
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, store.Query(Epoch, "send-1").Status);
    }

    [Fact]
    public void FailureAfterRecordFlush_BlocksReadbackAndReplacement()
    {
        var store = new DurableSubmissionIdentityStore(_directory, fault: point =>
        {
            if (point == PreparedIdentityFaultPoint.AfterRecordFlush)
                throw new IOException("injected record flush boundary failure");
        });
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);

        var reopened = new DurableSubmissionIdentityStore(_directory);
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, reopened.Query(Epoch, "send-1").Status);
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            reopened.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
    }

    [Fact]
    public void FailureAfterManifestFlush_ReadbackOnlyObservesOriginalIdentity()
    {
        var store = new DurableSubmissionIdentityStore(_directory, fault: point =>
        {
            if (point == PreparedIdentityFaultPoint.AfterManifestFlush)
                throw new IOException("injected lost response after manifest flush");
        });
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);

        var reopened = new DurableSubmissionIdentityStore(_directory);
        var observed = reopened.Query(Epoch, "send-1");
        Assert.Equal(PreparedIdentityLookupStatus.Observed, observed.Status);
        var original = Assert.IsType<PreparedSubmissionIdentity>(observed.Record);
        var replay = reopened.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.ExistingObserved, replay.Status);
        Assert.Equal(original.Handle, replay.Record?.Handle);
        Assert.Equal("Prepared", replay.Record?.State);
    }

    [Fact]
    public void RecordDeletedAfterHistoryScan_DoesNotCreateReplacementHandle()
    {
        var seed = new DurableSubmissionIdentityStore(_directory);
        Assert.True(seed.InitializeFreshForProvisioning());
        var original = seed.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Record;
        Assert.NotNull(original);
        var raced = new DurableSubmissionIdentityStore(_directory, fault: point =>
        {
            if (point == PreparedIdentityFaultPoint.AfterHistoryScan)
                File.Delete(seed.RecordPathFor(Epoch, "send-1"));
        });
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            raced.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.False(File.Exists(seed.RecordPathFor(Epoch, "send-1")));
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, seed.Query(Epoch, "send-1").Status);
    }

    [Fact]
    public void RecordReplacedAfterHistoryScan_DoesNotAcceptChangedIdentity()
    {
        var seed = new DurableSubmissionIdentityStore(_directory);
        Assert.True(seed.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Prepared,
            seed.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityStatus.Prepared,
            seed.TryPrepare(Epoch, "send-2", Fingerprint, "ext.task.start").Status);
        var raced = new DurableSubmissionIdentityStore(_directory, fault: point =>
        {
            if (point == PreparedIdentityFaultPoint.AfterHistoryScan)
                File.Copy(seed.RecordPathFor(Epoch, "send-2"), seed.RecordPathFor(Epoch, "send-1"), overwrite: true);
        });
        Assert.Equal(PreparedIdentityStatus.Uncertain,
            raced.TryPrepare(Epoch, "send-1", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityLookupStatus.Uncertain, seed.Query(Epoch, "send-1").Status);
    }

    [Fact]
    public void MalformedUnicodeIsRejectedBeforeAnyRecordWrite()
    {
        var store = new DurableSubmissionIdentityStore(_directory);
        Assert.True(store.InitializeFreshForProvisioning());
        Assert.Equal(PreparedIdentityStatus.Invalid,
            store.TryPrepare(Epoch, "bad\uD800", Fingerprint, "ext.task.start").Status);
        Assert.Equal(PreparedIdentityStatus.Invalid,
            store.TryPrepare(Epoch, "send-1", Fingerprint, "bad\uDC00").Status);
        Assert.Empty(Directory.GetFiles(_directory, "*.prepared"));
        var valid = store.TryPrepare(Epoch, "emoji-\U0001F600", Fingerprint, "ext.task.start");
        Assert.Equal(PreparedIdentityStatus.Prepared, valid.Status);
        Assert.Equal("emoji-\U0001F600", store.Query(Epoch, "emoji-\U0001F600").Record?.Key);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
