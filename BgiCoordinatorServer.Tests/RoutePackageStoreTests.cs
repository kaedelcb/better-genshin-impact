using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Services;
using NexusBGI.RoutePackages;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// These cases decide whether publication, rollback and selective restore retain an immutable recoverable history.
public sealed class RoutePackageStoreTests
{
    [Fact]
    public async Task UploadRecomputesTrustedMetadataAndDraftsAreNeverPublic()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var path = data.ValidZip();
        var draft = await data.Upload(store, path);
        Assert.Equal("draft", draft.State);
        Assert.Equal(RoutePackageTestData.Version, draft.RequiredClientVersion);
        Assert.Equal(1, draft.FileCount);
        Assert.Equal(1, draft.RouteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), draft.Sha256);
        Assert.Equal(new FileInfo(path).Length, draft.SizeBytes);
        Assert.Null(store.Current);
        Assert.False(store.IsPublishedAvailable(draft.Id));
        Assert.Equal(404, Assert.Throws<RoutePackageException>(() => store.Get(draft.Id)).StatusCode);
        Assert.Equal(404, Assert.Throws<RoutePackageException>(() => store.OpenDownload(draft.Id)).StatusCode);
        using var download = store.OpenDownload(draft.Id, includeDraft: true);
        Assert.Equal(draft.SizeBytes, download.Length);
        draft.Files[0].Sha256 = "forged";
        Assert.NotEqual("forged", store.Get(draft.Id, true).Files[0].Sha256);
    }

    [Fact]
    public async Task OptionalVariantDescriptionIsInTheManifestButNotTheRouteCount()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var zip = data.Zip(("builtin/线路/A变体/001_a.json", RoutePackageTestData.Route), ("builtin/线路/A变体/变体说明.txt", "左边"));
        var draft = await data.Upload(store, zip);
        Assert.Equal(2, draft.FileCount);
        Assert.Equal(1, draft.RouteCount);
        Assert.Equal(2, (await store.PublishAsync(draft.Id, 0, "资源清单", "admin")).Package.Files.Count);
    }

    [Fact]
    public async Task PublicationCasRollbackAndRestartRetainHistoryAndMonotonicRevision()
    {
        using var data = new RoutePackageTestData();
        string firstId, secondId;
        using (var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version))
        {
            var first = await data.Upload(store);
            var second = await data.Upload(store, data.ValidZip(RoutePackageTestData.Route.Replace("1.25", "10.25")));
            firstId = first.Id; secondId = second.Id;
            Assert.Equal(1, (await store.PublishAsync(firstId, 0, "首版", "admin")).Revision);
            Assert.Equal(409, (await Assert.ThrowsAsync<RoutePackageException>(() => store.PublishAsync(secondId, 0, "过期界面", "admin"))).StatusCode);
            Assert.Equal(firstId, store.Current!.Package.Id);
            Assert.Equal(2, (await store.PublishAsync(secondId, 1, "升级", "admin")).Revision);
            Assert.Equal("archived", store.Get(firstId).State);
            Assert.True(store.IsPublishedAvailable(firstId, RoutePackageTestData.Version));
            Assert.False(store.IsPublishedAvailable(firstId, "9.9.9"));
            Assert.Equal(3, (await store.PublishAsync(firstId, 2, "回滚", "admin", rollback: true)).Revision);
            Assert.Equal(409, (await Assert.ThrowsAsync<RoutePackageException>(() => store.DeleteAsync(secondId, "admin"))).StatusCode);
            Assert.Equal("rollback", store.Audit()[0].Action);
            var serialized = JsonSerializer.Serialize(store.Audit(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.StartsWith("[", serialized);
            Assert.Contains("\"atUtc\"", serialized);
            Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        }
        using var restarted = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        Assert.Equal(3, restarted.Current!.Revision);
        Assert.Equal(firstId, restarted.Current.Package.Id);
        Assert.Equal("archived", restarted.Get(secondId).State);
    }

    [Fact]
    public async Task CompetingPublicationsCommitOnlyOneWinner()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var a = await data.Upload(store);
        var b = await data.Upload(store);
        async Task<int> TryPublish(string id)
        {
            try { await store.PublishAsync(id, 0, "并发发布", "admin"); return 200; }
            catch (RoutePackageException ex) { return ex.StatusCode; }
        }
        var outcomes = await Task.WhenAll(TryPublish(a.Id), TryPublish(b.Id));
        Assert.Equal(new[] { 200, 409 }, outcomes.Order().ToArray());
        Assert.Equal(1, store.Current!.Revision);
        Assert.Single(store.Audit().Where(audit => audit.Action == "publish"));
    }

    [Fact]
    public async Task RestoreCreatesANewValidatedDraftAndWorksBelow128MiBStorageBudget()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version, 8 * 1024 * 1024);
        var oldZip = data.Zip(("builtin/线路/a.json", RoutePackageTestData.Route), ("builtin/线路/目录/b.json", RoutePackageTestData.Route));
        var newZip = data.Zip(("builtin/线路/a.json", RoutePackageTestData.Route.Replace("1.25", "22.25")), ("builtin/线路/new.json", RoutePackageTestData.Route));
        var source = await data.Upload(store, oldZip);
        await store.PublishAsync(source.Id, 0, "首版", "admin");
        var target = await data.Upload(store, newZip);
        var restored = await store.RestoreAsync(target.Id, source.Id, ["builtin/线路/a.json", "builtin/线路/目录/"], 1, "恢复旧文件和目录", "admin");
        Assert.NotEqual(target.Id, restored.Id);
        Assert.Equal("draft", restored.State);
        Assert.Equal(3, restored.FileCount);
        Assert.Equal(source.Files.Single(f => f.Path.EndsWith("a.json")).Sha256, restored.Files.Single(f => f.Path.EndsWith("a.json")).Sha256);
        Assert.Equal(target.Sha256, store.Get(target.Id, true).Sha256);
        Assert.Equal(source.Id, store.Current!.Package.Id);
        Assert.Equal(1, store.Current.Revision);
        using var blob = store.OpenDownload(restored.Id, true);
        Assert.Equal(restored.SizeBytes, blob.Length);
        Assert.Equal(409, (await Assert.ThrowsAsync<RoutePackageException>(() => store.RestoreAsync(source.Id, target.Id, ["builtin/线路"], 1, "不能改发布包", "admin"))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<RoutePackageException>(() => store.RestoreAsync(target.Id, source.Id, ["builtin/线路"], 0, "CAS", "admin"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<RoutePackageException>(() => store.RestoreAsync(target.Id, source.Id, ["builtin/不存在"], 1, "无此路径", "admin"))).StatusCode);
    }

    [Fact]
    public async Task InvalidUploadCancelledReadAndQuotaFailureLeaveCurrentAndCatalogUnchanged()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version, 8 * 1024 * 1024);
        var draft = await data.Upload(store);
        await store.PublishAsync(draft.Id, 0, "首版", "admin");
        var catalog = await File.ReadAllBytesAsync(Path.Combine(data.StoreRoot, "catalog.json"));
        await using (var invalid = File.OpenRead(data.ValidZip("{\"positions\":[]}")))
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UploadAsync(invalid, "非法", "admin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UploadAsync(new FailingBody(cancel: true), "取消", "admin"));
        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync(new FailingBody(cancel: false), "IO 错误", "admin"));
        // Deliberate fixture occupancy is neither a package nor eligible for automatic orphan cleanup.
        await File.WriteAllBytesAsync(Path.Combine(data.StoreRoot, "reserved-fixture.bin"), new byte[5 * 1024 * 1024]);
        Assert.Equal(507, (await Assert.ThrowsAsync<RoutePackageException>(() => data.Upload(store))).StatusCode);
        Assert.Equal(draft.Id, store.Current!.Package.Id);
        Assert.Equal(catalog, await File.ReadAllBytesAsync(Path.Combine(data.StoreRoot, "catalog.json")));
        Assert.Empty(Directory.GetFiles(data.StoreRoot, ".pending-*"));
    }

    [Fact]
    public async Task CommitFailureDoesNotExposeTheNewBlobOrChangeTheInMemoryCatalog()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var draft = await data.Upload(store);
        var catalog = Path.Combine(data.StoreRoot, "catalog.json");
        File.Move(catalog, catalog + ".fixture-backup");
        Directory.CreateDirectory(catalog); // forces the catalog rename/commit point to fail on both Linux and Windows
        try
        {
            var failure = await Record.ExceptionAsync(() => store.PublishAsync(draft.Id, 0, "模拟提交点失败", "admin"));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Null(store.Current);
            Assert.Equal("draft", store.Get(draft.Id, true).State);
            Assert.Equal(404, Assert.Throws<RoutePackageException>(() => store.Get(draft.Id)).StatusCode);
            Assert.Empty(Directory.GetFiles(data.StoreRoot, ".pending-*"));
        }
        finally { Directory.Delete(catalog); File.Move(catalog + ".fixture-backup", catalog); }
        Assert.Equal(1, (await store.PublishAsync(draft.Id, 0, "恢复后重试", "admin")).Revision);
    }

    [Fact]
    public async Task RestartChecksFullManifestAndCleansOnlyAbandonedOwnedTransactions()
    {
        using var data = new RoutePackageTestData();
        string id;
        using (var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version)) id = (await data.Upload(store)).Id;
        var pending = Path.Combine(data.StoreRoot, ".pending-" + Guid.NewGuid().ToString("N"));
        var orphan = Path.Combine(data.StoreRoot, Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllTextAsync(pending, "abandoned transaction");
        File.Copy(data.ValidZip(), orphan);
        var unrelated = Path.Combine(data.StoreRoot, ".pending-not-owned");
        await File.WriteAllTextAsync(unrelated, "keep");
        using (var restarted = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version)) Assert.Equal(id, restarted.Get(id, true).Id);
        Assert.False(File.Exists(pending)); Assert.False(File.Exists(orphan)); Assert.True(File.Exists(unrelated));
        var catalogPath = Path.Combine(data.StoreRoot, "catalog.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
        catalog["packages"]![0]!["files"]![0]!["sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version));
    }

    [Fact]
    public async Task OnlyUnpublishedDraftsCanBeDeletedAndDraftRollbackIsRejected()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var draft = await data.Upload(store);
        Assert.Equal(409, (await Assert.ThrowsAsync<RoutePackageException>(() => store.PublishAsync(draft.Id, 0, "不是历史版", "admin", rollback: true))).StatusCode);
        await store.DeleteAsync(draft.Id, "admin");
        Assert.False(File.Exists(Path.Combine(data.StoreRoot, draft.Id + ".zip")));
        Assert.Equal(404, Assert.Throws<RoutePackageException>(() => store.Get(draft.Id, true)).StatusCode);
    }

    private sealed class FailingBody(bool cancel) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_read) { _read = true; buffer.Span[0] = 0; return ValueTask.FromResult(1); }
            return ValueTask.FromException<int>(cancel ? new OperationCanceledException("fixture") : new IOException("fixture"));
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
