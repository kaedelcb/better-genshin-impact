using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NexusBGI.RoutePackages;

namespace BgiCoordinatorServer.Services;

public sealed class RoutePackageException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record RoutePackageAudit(DateTimeOffset AtUtc, string Action, string PackageId, string Actor, string Reason, long Revision);

/// <summary>Immutable ZIP objects, one atomically replaced catalog is the only publication commit point.</summary>
public sealed class RoutePackageStore : IDisposable
{
    private sealed class Catalog
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public string? CurrentId { get; set; }
        public List<RoutePackageDescriptor> Packages { get; set; } = [];
        public List<RoutePackageAudit> Audit { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _owner;
    private readonly string _root;
    private Catalog _catalog;
    public string RequiredClientVersion { get; }
    public long MaxStorageBytes { get; }
    public bool HasPublishedPackage => Volatile.Read(ref _catalog).CurrentId is not null;
    public bool HasPublishedPackageForVersion(string version)
    {
        var snapshot = Volatile.Read(ref _catalog);
        return snapshot.CurrentId is { } id && snapshot.Packages.Any(p => p.Id == id && (version.Length == 0 || p.RequiredClientVersion == version));
    }
    private const long ReserveBytes = 4L * 1024 * 1024;
    private const long DiskFloor = 64L * 1024 * 1024;

    public RoutePackageStore(string root, string? requiredClientVersion, long maxStorageBytes = 2L * 1024 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root);
        RequiredClientVersion = requiredClientVersion ?? "";
        if (maxStorageBytes < ReserveBytes || maxStorageBytes > 1024L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxStorageBytes));
        MaxStorageBytes = maxStorageBytes;
        RejectLinks(_root);
        Directory.CreateDirectory(_root);
        _owner = new FileStream(Path.Combine(_root, ".route-package-owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(_root, "catalog.json");
            if (File.Exists(path) && new FileInfo(path).Length > 32L * 1024 * 1024) throw new InvalidDataException("线路包目录元数据超限。");
            _catalog = File.Exists(path) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("线路包目录元数据为空。") : new Catalog();
            ValidateCatalog();
            // Only this service's abandoned transaction names, under an exclusive process lock.
            foreach (var temporary in Directory.EnumerateFiles(_root, ".pending-*"))
            {
                var name = Path.GetFileName(temporary);
                if (name.Length == 41 && RoutePackageArchive.IsValidId(name[9..])) File.Delete(temporary);
            }
            // A crash between moving a blob and committing the catalog can leave an unreferenced object.
            // This dedicated storage namespace is exclusively owned; only exact service-generated names qualify.
            var referenced = _catalog.Packages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var blob in Directory.EnumerateFiles(_root, "*.zip"))
            {
                var id = Path.GetFileNameWithoutExtension(blob);
                if (RoutePackageArchive.IsValidId(id) && !referenced.Contains(id)) File.Delete(blob);
            }
        }
        catch { _owner.Dispose(); throw; }
    }

    public RoutePackagePublication? Current
    {
        get
        {
            var catalog = Volatile.Read(ref _catalog);
            return catalog.CurrentId is null ? null : new RoutePackagePublication { Revision = catalog.Revision,
                Package = Clone(catalog.Packages.Single(p => p.Id == catalog.CurrentId)) };
        }
    }

    public bool IsPublishedAvailable(string? id, string? requiredClientVersion = null)
    {
        var snapshot = Volatile.Read(ref _catalog);
        // ZIPs are verified on startup and immutable afterwards; no long IO under any room lock.
        return RoutePackageArchive.IsValidId(id) && snapshot.Packages.Any(p => p.Id == id && p.State is "published" or "archived"
            && (requiredClientVersion is null || p.RequiredClientVersion == requiredClientVersion));
    }

    public RoutePackageDescriptor Get(string id, bool includeDraft = false)
    {
        var catalog = Volatile.Read(ref _catalog);
        return Clone(Find(catalog, id, includeDraft));
    }

    public object List()
    {
        var catalog = Volatile.Read(ref _catalog);
        return new { current = catalog.CurrentId == null ? null : new RoutePackagePublication { Revision = catalog.Revision,
            Package = Clone(Find(catalog, catalog.CurrentId, true)) }, packages = catalog.Packages.Select(Clone).Reverse().ToArray(),
            requiredClientVersion = RequiredClientVersion, storageBytes = StorageBytes(), maxStorageBytes = MaxStorageBytes };
    }

    public IReadOnlyList<RoutePackageAudit> Audit() => Volatile.Read(ref _catalog).Audit.AsEnumerable().Reverse().ToArray();

    public object Compare(string id)
    {
        var snapshot = Volatile.Read(ref _catalog);
        var package = Find(snapshot, id, true);
        var current = snapshot.CurrentId is { } currentId ? Find(snapshot, currentId, true).Files : [];
        var prior = current.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var next = package.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        return new { package = Clone(package), added = next.Keys.Except(prior.Keys).Order(StringComparer.Ordinal).ToArray(),
            modified = next.Keys.Intersect(prior.Keys).Where(p => next[p].Sha256 != prior[p].Sha256).Order(StringComparer.Ordinal).ToArray(),
            removed = prior.Keys.Except(next.Keys).Order(StringComparer.Ordinal).ToArray() };
    }

    public FileStream OpenDownload(string id, bool includeDraft = false)
    {
        _gate.Wait();
        try
        {
            Find(_catalog, id, includeDraft);
            return new FileStream(ZipPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
        }
        finally { _gate.Release(); }
    }

    public async Task<RoutePackageDescriptor> UploadAsync(Stream body, string description, string actor, CancellationToken ct = default)
    {
        ValidateText(description, 2000, "说明");
        await _gate.WaitAsync(ct);
        var temporary = Temporary();
        try
        {
            EnsureCapacity(ReserveBytes);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await body.ReadAsync(buffer, ct)) != 0)
                {
                    total += count;
                    if (total > RoutePackageArchive.MaxArchiveBytes) throw new RoutePackageException(413, "ZIP 超过 128 MiB。");
                    EnsureCapacity(count + ReserveBytes);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            return await AddZipAsync(temporary, description, actor, "upload", "", ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }

    public async Task<RoutePackagePublication> PublishAsync(string id, long expectedRevision, string reason, string actor, bool rollback = false, CancellationToken ct = default)
    {
        ValidateText(reason, 1000, "原因", required: true);
        await _gate.WaitAsync(ct);
        try
        {
            CheckRevision(expectedRevision);
            var next = Copy();
            var package = Find(next, id, true);
            if (rollback && package.State == "draft") throw new RoutePackageException(409, "回滚只能选择历史已发布版本。");
            // 未配置客户端版本时不限制；配置了精确版本才要求草稿与之相同（旧策略下上传的草稿需重新上传）。
            if (RequiredClientVersion.Length > 0 && (!RoutePackagePolicy.IsExactVersion(RequiredClientVersion) || package.RequiredClientVersion != RequiredClientVersion))
                throw new RoutePackageException(409, "请先配置精确正式版本；旧版本策略下的草稿请重新上传。");
            if (next.CurrentId == id) throw new RoutePackageException(409, "该版本已是当前发布版本。");
            // Revalidate before making a blob public; storage errors cannot become a successful publication.
            var manifest = await RoutePackageArchive.ValidateAsync(ZipPath(id), ct);
            await VerifyBlobAsync(package, manifest, ct);
            if (next.CurrentId is { } prior) Find(next, prior, true).State = "archived";
            package.State = "published";
            next.CurrentId = id;
            next.Revision = checked(next.Revision + 1);
            AppendAudit(next, rollback ? "rollback" : "publish", id, actor, reason);
            ct.ThrowIfCancellationRequested();
            Commit(next);
            return new RoutePackagePublication { Revision = next.Revision, Package = Clone(package) };
        }
        finally { _gate.Release(); }
    }

    public async Task<RoutePackageDescriptor> RestoreAsync(string id, string sourceId, IReadOnlyList<string> paths,
        long expectedRevision, string reason, string actor, CancellationToken ct = default)
    {
        ValidateText(reason, 1000, "原因", required: true);
        if (paths.Count is < 1 or > RoutePackageArchive.MaxFiles) throw new RoutePackageException(400, "请选择有界文件/目录清单。");
        foreach (var path in paths)
        {
            if (path is null) throw new RoutePackageException(400, "恢复路径不能为 null。");
            RoutePackageArchive.ValidatePath(path.TrimEnd('/'));
        }
        await _gate.WaitAsync(ct);
        var temporary = Temporary();
        try
        {
            CheckRevision(expectedRevision);
            var target = Find(_catalog, id, true);
            var source = Find(_catalog, sourceId, true);
            if (target.State != "draft") throw new RoutePackageException(409, "仅可向草稿副本恢复，不能在线修改已发布包。");
            var prefixes = paths.Select(p => p.TrimEnd('/')).Distinct(StringComparer.Ordinal).ToArray();
            bool Selected(string path) => prefixes.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
            if (prefixes.Any(p => !source.Files.Any(f => f.Path == p || f.Path.StartsWith(p + "/", StringComparison.Ordinal))))
                throw new RoutePackageException(400, "恢复来源缺少所选文件/目录。");
            var entries = target.Files.Where(f => !Selected(f.Path)).Select(f => (File: f, Id: id))
                .Concat(source.Files.Where(f => Selected(f.Path)).Select(f => (File: f, Id: sourceId))).ToArray();
            if (entries.Length > RoutePackageArchive.MaxFiles || entries.Sum(e => e.File.SizeBytes) > RoutePackageArchive.MaxExpandedBytes)
                throw new RoutePackageException(413, "恢复后包大小超限。");
            EnsureCapacity(ReserveBytes);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var bounded = new BoundedArchiveWriter(file, count => EnsureCapacity(count + ReserveBytes)))
                using (var output = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: System.Text.Encoding.UTF8))
                using (var targetZip = ZipFile.OpenRead(ZipPath(id)))
                using (var sourceZip = ZipFile.OpenRead(ZipPath(sourceId)))
                    foreach (var item in entries.OrderBy(e => e.File.Path, StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        var inputEntry = (item.Id == id ? targetZip : sourceZip).GetEntry(item.File.Path) ?? throw new IOException("持久包缺少文件。");
                        await using var input = inputEntry.Open();
                        await using var destination = output.CreateEntry(item.File.Path, CompressionLevel.Optimal).Open();
                        await input.CopyToAsync(destination, ct);
                    }
                file.Flush(true);
            }
            return await AddZipAsync(temporary, $"恢复自 {sourceId}: {reason}", actor, "restore", reason, ct);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }

    /// <summary>删除草稿或历史发布版本。只有当前发布版本不能删，其余都可以删除（删除后无法回滚到该版本）。</summary>
    public async Task DeleteAsync(string id, string actor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = Copy();
            var package = Find(next, id, true);
            if (next.CurrentId == id) throw new RoutePackageException(409, "当前发布版本不能删除，请先发布或回滚到其他版本。");
            var history = package.State != "draft";
            next.Packages.Remove(package);
            AppendAudit(next, "delete", id, actor, history ? "删除历史发布版本" : "删除未发布草稿");
            Commit(next); // An interruption can leave an unreferenced immutable blob, never a broken catalog reference.
            File.Delete(ZipPath(id));
        }
        finally { _gate.Release(); }
    }

    private async Task<RoutePackageDescriptor> AddZipAsync(string temporary, string description, string actor, string action, string reason, CancellationToken ct)
    {
        if (_catalog.Packages.Count >= 4096) throw new RoutePackageException(507, "线路包目录已满，请删除不再使用的草稿。");
        var manifest = await RoutePackageArchive.ValidateAsync(temporary, ct);
        await using var input = File.OpenRead(temporary);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
        var package = new RoutePackageDescriptor { Id = Guid.NewGuid().ToString("N"), Sha256 = digest,
            SizeBytes = input.Length, CreatedAtUtc = DateTimeOffset.UtcNow, Description = description,
            RequiredClientVersion = RequiredClientVersion, Files = manifest, FileCount = manifest.Count,
            RouteCount = manifest.Count(f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)), State = "draft" };
        package.DownloadUrl = $"/api/hoeing-routes/packages/{package.Id}/download";
        input.Close();
        ct.ThrowIfCancellationRequested();
        EnsureCapacity(ReserveBytes);
        File.Move(temporary, ZipPath(package.Id));
        try
        {
            var next = Copy();
            next.Packages.Add(package);
            AppendAudit(next, action, package.Id, actor, reason);
            Commit(next);
        }
        catch { File.Delete(ZipPath(package.Id)); throw; }
        return Clone(package);
    }

    private void ValidateCatalog()
    {
        if (_catalog.SchemaVersion != 1 || _catalog.Revision < 0 || _catalog.Packages.Count > 4096 || _catalog.Audit.Count > 500)
            throw new InvalidDataException("线路包目录结构无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in _catalog.Packages)
        {
            if (!RoutePackageArchive.IsValidId(package.Id) || !ids.Add(package.Id) || package.State is not ("draft" or "published" or "archived"))
                throw new InvalidDataException("线路包元数据身份无效。");
            RejectLinks(ZipPath(package.Id));
            var manifest = RoutePackageArchive.ValidateAsync(ZipPath(package.Id)).GetAwaiter().GetResult();
            VerifyBlobAsync(package, manifest, CancellationToken.None).GetAwaiter().GetResult();
        }
        if (_catalog.CurrentId is { } id && (!_catalog.Packages.Any(p => p.Id == id && p.State == "published") || _catalog.Revision == 0)
            || _catalog.Packages.Count(p => p.State == "published") != (_catalog.CurrentId == null ? 0 : 1))
            throw new InvalidDataException("线路包发布指针无效。");
    }

    private async Task VerifyBlobAsync(RoutePackageDescriptor package, List<RoutePackageFile> manifest, CancellationToken ct)
    {
        await using var file = File.OpenRead(ZipPath(package.Id));
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if (package.SchemaVersion != 1 || file.Length != package.SizeBytes || digest != package.Sha256
            || JsonSerializer.Serialize(manifest, Json) != JsonSerializer.Serialize(package.Files, Json)
            || package.FileCount != manifest.Count || package.RouteCount != manifest.Count(f => f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("持久线路包与可信元数据不一致。");
    }

    private void Commit(Catalog next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > 32L * 1024 * 1024) throw new RoutePackageException(507, "线路包目录元数据额度已满。");
        EnsureCapacity(bytes.Length);
        var temporary = Temporary();
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, Path.Combine(_root, "catalog.json"), overwrite: true);
            Volatile.Write(ref _catalog, next);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CheckRevision(long expected)
    {
        if (expected != _catalog.Revision) throw new RoutePackageException(409, "发布版本已变化，请刷新后重新确认。");
    }
    private Catalog Copy() => JsonSerializer.Deserialize<Catalog>(JsonSerializer.SerializeToUtf8Bytes(_catalog, Json), Json)!;
    private static RoutePackageDescriptor Clone(RoutePackageDescriptor value) => JsonSerializer.Deserialize<RoutePackageDescriptor>(JsonSerializer.SerializeToUtf8Bytes(value, Json), Json)!;
    private static RoutePackageDescriptor Find(Catalog catalog, string id, bool includeDraft)
        => RoutePackageArchive.IsValidId(id) && catalog.Packages.SingleOrDefault(p => p.Id == id && (includeDraft || p.State != "draft")) is { } package
            ? package : throw new RoutePackageException(404, "线路包不存在或未发布。");
    private void AppendAudit(Catalog next, string action, string id, string actor, string reason)
    {
        next.Audit.Add(new(DateTimeOffset.UtcNow, action, id, actor, reason, next.Revision));
        if (next.Audit.Count > 500) next.Audit.RemoveRange(0, next.Audit.Count - 500);
    }
    private static void ValidateText(string? text, int max, string name, bool required = false)
    {
        if (text == null || text.Length > max || required && string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new RoutePackageException(400, name + "无效或超长。");
    }
    private string ZipPath(string id) => Path.Combine(_root, id + ".zip");
    private string Temporary() => Path.Combine(_root, ".pending-" + Guid.NewGuid().ToString("N"));
    private long StorageBytes() => Directory.EnumerateFiles(_root).Sum(p => new FileInfo(p).Length);
    private void EnsureCapacity(long extra)
    {
        if (StorageBytes() > MaxStorageBytes - extra) throw new RoutePackageException(507, "线路包存储额度不足；历史版本保留，请管理员清理未发布草稿或调整明确额度。");
        if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < DiskFloor + extra) throw new RoutePackageException(507, "磁盘可用空间不足，已保护现有版本。");
    }
    private static void RejectLinks(string path)
    {
        for (var p = new FileInfo(path); p != null; p = p.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(p.FullName) || Directory.Exists(p.FullName)) && (File.GetAttributes(p.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("线路包存储路径不得包含链接。");
    }
    // Non-seekable output keeps ZIP IO bounded by actual bytes, not a pessimistic 128 MiB reservation.
    private sealed class BoundedArchiveWriter(FileStream file, Action<int> reserve) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        private void Check(int count)
        {
            if (file.Position > RoutePackageArchive.MaxArchiveBytes - count)
                throw new RoutePackageException(413, "恢复后的 ZIP 超过 128 MiB。");
            reserve(count);
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); file.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); file.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Check(buffer.Length); return file.WriteAsync(buffer, ct); }
        public override void Flush() => file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        // The containing transaction owns and durably flushes the underlying file.
    }
    public void Dispose() { _owner.Dispose(); _gate.Dispose(); }
}
