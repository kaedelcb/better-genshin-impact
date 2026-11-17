using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using NexusBGI.RoutePackages;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 用内存 HTTP 响应和独占临时根验证正式整包安装边界，替代已退役 EXE/日志协议测试。
/// 判据：完整目标包才可成功；失败/停止保全旧包；租约和恢复不允许执行期替换或未知成功。
/// 不启动服务器/第三方进程，不读写真实 User，不表示游戏实跑通过。
/// </summary>
public sealed class AutoHoeingRouteUpdaterTests
{
    private const string Version = "0.58.0-TeaBag-B191";
    private const string Server = "http://127.0.0.1:9876";
    private const string Route = """{"info":{"description":"预计用时60秒"},"positions":[{"x":1,"y":2,"type":"path","move_mode":"walk"}]}""";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "BetterGI-RoutePackageTests-" + Guid.NewGuid().ToString("N"));
        public string Managed => Path.Combine(Root, "managed");
        public string Current => Path.Combine(Managed, "current");
        public readonly Dictionary<string, (RoutePackageDescriptor Descriptor, byte[] Zip)> Packages = new();
        public readonly List<string> Requests = new();
        public string CurrentId = "";
        public string RequiredVersion = Version;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Intercept;
        public HttpClient Http { get; }
        public AutoHoeingRouteUpdater Updater { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Http = new HttpClient(new Handler(this)) { Timeout = Timeout.InfiniteTimeSpan };
            Updater = new AutoHoeingRouteUpdater(Http, Managed, Version);
        }

        public RoutePackageDescriptor Add(params (string Path, string Text)[] files)
        {
            if (files.Length == 0) files = new[] { ("builtin/传奇/01.json", Route) };
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true, Encoding.UTF8))
                foreach (var (path, text) in files)
                {
                    using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                    entry.Write(Encoding.UTF8.GetBytes(text));
                }
            var archive = bytes.ToArray();
            var descriptor = new RoutePackageDescriptor
            {
                Id = Guid.NewGuid().ToString("N"), RequiredClientVersion = Version, State = "published",
                Sha256 = Hash(archive), SizeBytes = archive.Length, CreatedAtUtc = DateTimeOffset.UtcNow,
                Files = files.Select(file => new RoutePackageFile
                {
                    Path = file.Path, SizeBytes = Encoding.UTF8.GetByteCount(file.Text),
                    Sha256 = Hash(Encoding.UTF8.GetBytes(file.Text))
                }).ToList(),
                FileCount = files.Length, RouteCount = files.Count(file => file.Path.EndsWith(".json", StringComparison.Ordinal))
            };
            Packages.Add(descriptor.Id, (descriptor, archive));
            CurrentId = descriptor.Id;
            return descriptor;
        }

        public Task<string> Ensure(string? pin = null, CancellationToken ct = default, bool repair = false,
            AutoHoeingRouteUpdater.Lease? lease = null, bool allowInstall = true)
            => Updater.EnsureAsync(Server, pin, ct, repair, lease, allowInstall);

        public void Journal(string id, RoutePackageDescriptor target, RoutePackageDescriptor? previous, bool hadCurrent = true)
            => File.WriteAllText(Path.Combine(Managed, "transaction.json"),
                JsonSerializer.Serialize(new { id, target, previous, hadCurrent }, Json));

        public void AssertNoPendingInstall()
        {
            Assert.Empty(Directory.GetFileSystemEntries(Managed, "staging-*"));
            Assert.Empty(Directory.GetFileSystemEntries(Managed, "backup-*"));
            Assert.Empty(Directory.GetFileSystemEntries(Managed, "download-*"));
            Assert.False(File.Exists(Path.Combine(Managed, "transaction.json")));
        }

        public void Dispose()
        {
            Http.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                fixture.Requests.Add(path);
                if (fixture.Intercept != null && await fixture.Intercept(request, ct) is { } intercepted) return intercepted;
                if (path.EndsWith("/policy", StringComparison.Ordinal))
                    return JsonResponse(new { schemaVersion = 1, requiredClientVersion = fixture.RequiredVersion });
                if (path.EndsWith("/current", StringComparison.Ordinal))
                    return JsonResponse(new RoutePackagePublication { Revision = 1, Package = fixture.Packages[fixture.CurrentId].Descriptor });
                var parts = path.Split('/');
                var id = path.EndsWith("/download", StringComparison.Ordinal) ? parts[^2] : parts[^1];
                if (!fixture.Packages.TryGetValue(id, out var package)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return path.EndsWith("/download", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package.Zip) }
                    : JsonResponse(package.Descriptor);
            }
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CurrentInstallVerifiesEveryFileAndFullyReplacesOfficialContentOnly()
    {
        using var f = new Fixture();
        var first = f.Add(("builtin/旧包/old.json", Route));
        Assert.Equal(first.Id, await f.Ensure());
        var local = Path.Combine(f.Root, "local", "my.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "user-owned");
        var next = f.Add(("builtin/传奇/01.json", Route), ("pathing/普通/02.json", Route));
        Assert.Equal(next.Id, await f.Ensure());
        Assert.False(Directory.Exists(Path.Combine(f.Current, "builtin", "旧包")));
        Assert.Equal(2, Directory.GetFiles(f.Current, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("user-owned", File.ReadAllText(local));
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task FlatPathingAndVariantDescriptionsUseTheSameLayoutAsTheSharedValidator()
    {
        using var f = new Fixture();
        var package = f.Add(("builtin/传奇/A变体/01_a.json", Route),
            ("builtin/传奇/A变体/变体说明.txt", "变体说明"), ("pathing/01.json", Route));
        Assert.Equal(package.Id, await f.Ensure());
        Assert.Equal("变体说明", File.ReadAllText(Path.Combine(f.Current, "builtin", "传奇", "A变体", "变体说明.txt")));
        Assert.True(File.Exists(Path.Combine(f.Current, "pathing", "01.json")));
    }

    [Fact]
    public async Task ReadmeDocumentationIsInstalledButNeverRequiredOrExecuted()
    {
        using var f = new Fixture();
        var package = f.Add(("builtin/D规划：纯精英-6（4人）/001.json", Route),
            ("builtin/D规划：纯精英-6（4人）/README.md", "# 说明"), ("pathing/01.json", Route));
        Assert.Equal(package.Id, await f.Ensure());
        Assert.Equal("# 说明", File.ReadAllText(Path.Combine(f.Current, "builtin", "D规划：纯精英-6（4人）", "README.md")));
        Assert.Equal("001.json", Path.GetFileName(Directory.GetFiles(Path.Combine(f.Current, "builtin", "D规划：纯精英-6（4人）"), "*.json").Single()));
    }

    [Fact]
    public async Task UnmarkedNonemptyRootIsNeverAdoptedOrOverwritten()
    {
        using var f = new Fixture();
        f.Add();
        Directory.CreateDirectory(f.Managed);
        var existing = Path.Combine(f.Managed, "user-owned.txt");
        File.WriteAllText(existing, "preserve");
        await Assert.ThrowsAsync<IOException>(() => f.Ensure());
        Assert.Equal("preserve", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(f.Managed, ".managed")));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task PinnedPublishedHistoryIgnoresNewCurrentAndSupportsRollback()
    {
        using var f = new Fixture();
        var old = f.Add();
        await f.Ensure();
        var latest = f.Add(("builtin/新版/new.json", Route));
        await f.Ensure();
        f.Requests.Clear();
        Assert.Equal(old.Id, await f.Ensure(old.Id));
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/current", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
        Assert.False(Directory.Exists(Path.Combine(f.Current, "builtin", "新版")));
        Assert.NotEqual(old.Id, latest.Id);
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task LocalInstalledTagCannotHideCorruptionOrExtraFiles()
    {
        using var f = new Fixture();
        var package = f.Add();
        await f.Ensure();
        File.WriteAllText(Path.Combine(f.Current, "builtin", "传奇", "01.json"), "corrupt");
        File.WriteAllText(Path.Combine(f.Current, "extra.json"), "unexpected");
        f.Requests.Clear();
        Assert.Equal(package.Id, await f.Ensure(package.Id));
        Assert.Contains(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(f.Current, "extra.json")));
        Assert.Equal(Route, File.ReadAllText(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
    }

    [Fact]
    public async Task DisabledAutomaticDownloadChecksLocalPinButNeverDownloadsMissingPackage()
    {
        using var f = new Fixture();
        var package = f.Add();
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => f.Ensure(allowInstall: false));
        Assert.Contains("自动更新线路已关闭", failure.Message);
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
        await f.Ensure();
        f.Requests.Clear();
        Assert.Equal(package.Id, await f.Ensure(package.Id, allowInstall: false));
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("zip-hash")]
    [InlineData("manifest")]
    [InlineData("http")]
    [InlineData("truncated")]
    [InlineData("invalid-json")]
    public async Task FailedDownloadOrValidationPreservesPreviousCompleteDirectory(string fault)
    {
        using var f = new Fixture();
        var previous = f.Add();
        await f.Ensure();
        var target = f.Add(("builtin/新包/new.json", fault == "invalid-json" ? "{}" : Route));
        if (fault == "zip-hash") target.Sha256 = new string('0', 64);
        if (fault == "manifest") target.Files[0].Sha256 = new string('0', 64);
        if (fault == "http") f.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null);
        if (fault == "truncated") f.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) } : null);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Ensure());
        Assert.Equal(Route, File.ReadAllText(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
        Assert.False(Directory.Exists(Path.Combine(f.Current, "builtin", "新包")));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Managed, "installed.json")));
        Assert.Equal(previous.Id, state.RootElement.GetProperty("id").GetString());
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task ManualCancellationPropagatesAndCleansTheIncompleteDownload()
    {
        using var f = new Fixture();
        var previous = f.Add();
        await f.Ensure();
        f.Add(("builtin/新包/new.json", Route));
        using var stop = new CancellationTokenSource();
        f.Intercept = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
                return Task.FromResult<HttpResponseMessage?>(null);
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Ensure(ct: stop.Token));
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
        f.Intercept = null;
        Assert.Equal(previous.Id, await f.Ensure(previous.Id));
        f.AssertNoPendingInstall();
    }

    private sealed class CancelOnSecondRead(byte[] bytes, CancellationTokenSource stop) : MemoryStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads > 1) stop.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
        }
    }

    [Fact]
    public async Task StopDuringStreamingRemovesPartialZipAndPreservesPreviousInstallation()
    {
        using var f = new Fixture();
        var previous = f.Add();
        await f.Ensure();
        var next = f.Add(("builtin/新包/new.json", Route));
        using var stop = new CancellationTokenSource();
        f.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new CancelOnSecondRead(f.Packages[next.Id].Zip, stop)) } : null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Ensure(ct: stop.Token));
        Assert.Equal(Route, File.ReadAllText(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
        f.AssertNoPendingInstall();
        f.Intercept = null;
        Assert.Equal(previous.Id, await f.Ensure(previous.Id));
    }

    [Fact]
    public async Task LeaseBlocksOtherInstallersAndExecutionSealBlocksItsOwnReplacement()
    {
        using var f = new Fixture();
        var original = f.Add();
        using var lease = f.Updater.AcquireLease();
        await f.Ensure(original.Id, lease: lease);
        await Assert.ThrowsAsync<IOException>(() => f.Ensure());
        await lease.SealForExecutionAsync();
        f.Add(("builtin/新包/new.json", Route));
        f.Requests.Clear();
        await Assert.ThrowsAsync<IOException>(() => f.Ensure(lease: lease));
        await Assert.ThrowsAsync<IOException>(() => f.Ensure(original.Id, repair: true, lease: lease));
        Assert.Equal(original.Id, await f.Ensure(original.Id, lease: lease));
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
    }

    [Fact]
    public async Task DisposedOrDifferentRootLeaseCannotAuthorizeInstallation()
    {
        using var f = new Fixture();
        f.Add();
        using var other = new Fixture();
        using var foreign = other.Updater.AcquireLease();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Ensure(lease: foreign));
        var disposed = f.Updater.AcquireLease();
        disposed.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Ensure(lease: disposed));
    }

    [Fact]
    public async Task CrashBetweenRenamesRestoresOldDirectoryBeforeAnyNetworkRequest()
    {
        using var f = new Fixture();
        var previous = f.Add();
        await f.Ensure();
        var target = f.Add(("builtin/新包/new.json", Route));
        var transaction = Guid.NewGuid().ToString("N");
        Directory.Move(f.Current, Path.Combine(f.Managed, "backup-" + transaction));
        Directory.CreateDirectory(Path.Combine(f.Managed, "staging-" + transaction));
        File.WriteAllText(Path.Combine(f.Managed, "staging-" + transaction, "incomplete"), "partial");
        f.Journal(transaction, target, previous);
        f.Intercept = (_, _) => throw new HttpRequestException("offline after recovery");
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Ensure(previous.Id));
        Assert.Equal(Route, File.ReadAllText(Path.Combine(f.Current, "builtin", "传奇", "01.json")));
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task CrashAfterSecondRenameCompletesInstalledStateFromFullManifest()
    {
        using var f = new Fixture();
        var previous = f.Add();
        await f.Ensure();
        var target = f.Add(("builtin/新包/new.json", Route));
        var transaction = Guid.NewGuid().ToString("N");
        Directory.Move(f.Current, Path.Combine(f.Managed, "backup-" + transaction));
        var file = Path.Combine(f.Current, "builtin", "新包", "new.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Route, new UTF8Encoding(false));
        f.Journal(transaction, target, previous);
        Assert.Equal(target.Id, await f.Ensure(target.Id));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Managed, "installed.json")));
        Assert.Equal(target.Id, state.RootElement.GetProperty("id").GetString());
        f.AssertNoPendingInstall();
    }

    [Theory]
    [InlineData("0.58.0-TeaBag-older")]
    [InlineData("alpha")]
    public async Task DifferentExactPolicyVersionFailsBeforeDownload(string required)
    {
        using var f = new Fixture();
        f.Add();
        f.RequiredVersion = required;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Ensure());
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnpinnedPolicyVersionDownloadsPublishedPackageWithoutVersionMatch()
    {
        using var f = new Fixture();
        var package = f.Add();
        f.RequiredVersion = "";
        Assert.Equal(package.Id, await f.Ensure());
    }

    [Theory]
    [InlineData(null, "https://www.autobgi.cn")]
    [InlineData("", "https://www.autobgi.cn")]
    [InlineData("   ", "https://www.autobgi.cn")]
    [InlineData("举例：https://bgi-sync.example.com(需部署自建服务)", "https://www.autobgi.cn")]
    [InlineData("www.autobgi.cn", "https://www.autobgi.cn")]
    [InlineData("http://example.org:8080/hub", "http://example.org:8080/hub")]
    [InlineData("https://routes.example.org", "https://routes.example.org")]
    public void UnsetOrPlaceholderServerAddressUsesTheOfficialRouteService(string? configured, string expected)
        => Assert.Equal(expected, AutoHoeingRouteUpdater.ResolveServerAddress(configured));

    [Fact]
    public void ConfiguredAddressWithCredentialsIsRejectedNotReplacedByTheOfficialOne()
        => Assert.Throws<InvalidDataException>(() => AutoHoeingRouteUpdater.RequireServerUri(
            AutoHoeingRouteUpdater.ResolveServerAddress("https://user:secret@example.com")));

    [Theory]
    [InlineData("file:///tmp/routes")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/?x=1")]
    [InlineData("举例：https://bgi-sync.example.com(需部署自建服务)")]
    [InlineData("")]
    public void UnsafeServerAddressIsRejected(string address)
        => Assert.Throws<InvalidDataException>(() => AutoHoeingRouteUpdater.RequireServerUri(address));

    [Theory]
    [InlineData("http://www.autobgi.cn:8080", "https://www.autobgi.cn/")]
    [InlineData("http://www.autobgi.cn:8080/hub", "https://www.autobgi.cn/")]
    [InlineData("http://www.autobgi.cn:8080/HUB/", "https://www.autobgi.cn/")]
    [InlineData("https://www.autobgi.cn", "https://www.autobgi.cn/")]
    [InlineData("  https://www.autobgi.cn/ ", "https://www.autobgi.cn/")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost:5000/hub", "http://localhost:5000/")]
    public void MultiplayerServerAddressIsMappedToTheSameHostOverHttps(string address, string expected)
        => Assert.Equal(expected, AutoHoeingRouteUpdater.RequireServerUri(address).AbsoluteUri);

    [Fact]
    public async Task ManifestIdentityAndPathEscapeAreRejectedWithoutTouchingOutsideFiles()
    {
        using var f = new Fixture();
        var package = f.Add();
        package.Files[0].Path = "builtin/../../outside.json";
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Ensure());
        Assert.False(File.Exists(Path.Combine(f.Root, "outside.json")));
        Assert.DoesNotContain(f.Requests, path => path.EndsWith("/download", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Ensure("../invalid"));
    }

    [Fact]
    public async Task RepairInvalidatesControlAndVariantCachesEvenWithUnchangedTimestamps()
    {
        using var f = new Fixture();
        var first = f.Add(("builtin/传奇/A变体/01_a.json", Route),
            ("builtin/传奇/A变体/control.json5", "{global_cover:{info:{description:'old'}}}"));
        await f.Ensure();
        var top = Path.Combine(f.Current, "builtin", "传奇");
        var control = Path.Combine(top, "A变体", "control.json5");
        var before = JsonMerger.GetCtrJObject(control)!;
        var variants = RouteVariantScanner.ScanVariants(top);
        Assert.Equal("old", before["global_cover"]!["info"]!["description"]!.ToString());
        var timestamp = File.GetLastWriteTimeUtc(control);
        var routePath = Path.Combine(top, "A变体", "01_a.json");
        var routeTimestamp = File.GetLastWriteTimeUtc(routePath);
        var next = f.Add(("builtin/传奇/A变体/01_a.json", Route),
            ("builtin/传奇/A变体/control.json5", "{global_cover:{info:{description:'new'}}}"));
        await f.Ensure(next.Id, repair: true);
        File.SetLastWriteTimeUtc(control, timestamp);
        File.SetLastWriteTimeUtc(routePath, routeTimestamp);
        Assert.Equal("new", JsonMerger.GetCtrJObject(control)!["global_cover"]!["info"]!["description"]!.ToString());
        Assert.NotSame(variants, RouteVariantScanner.ScanVariants(top));
        Assert.NotEqual(first.Id, next.Id);
    }
}
