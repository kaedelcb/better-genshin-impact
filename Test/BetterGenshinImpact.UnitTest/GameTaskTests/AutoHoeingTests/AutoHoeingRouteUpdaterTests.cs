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
    public async Task VersionMarkerTellsWhichVersionIsInstalledWithoutPollutingCurrent()
    {
        using var f = new Fixture();
        var logs = new List<string>();
        f.Updater.VersionLog = logs.Add;
        var first = f.Add(("builtin/旧包/old.json", Route));
        first.Description = "20261117";
        Assert.Equal(first.Id, await f.Ensure());
        var marker = Path.Combine(f.Managed, "当前线路版本.txt");
        var text = File.ReadAllText(marker);
        Assert.Contains("版本名称：20261117", text);
        Assert.Contains("版本标识：" + first.Id, text);
        Assert.Contains("首次安装完成", text);
        Assert.Contains(logs, line => line.Contains("20261117") && line.Contains("首次安装完成"));
        // 标识文件不能进入 current：current 必须与发布清单逐文件一致，否则下次校验会判为被篡改而重装。
        Assert.DoesNotContain(Directory.GetFiles(f.Current, "*", SearchOption.AllDirectories), path => path.EndsWith("当前线路版本.txt"));

        // 夹具里描述符是同一进程内的对象；安装器把第一次的描述写进 installed.json，这里换新对象代表服务端发布了新版本。
        var second = f.Add(("builtin/新包/new.json", Route));
        second.Description = "20261117-2";
        Assert.Equal(second.Id, await f.Ensure());
        text = File.ReadAllText(marker);
        Assert.Contains("版本名称：20261117-2", text);
        Assert.Contains("已从 20261117（" + first.Id[..8] + "）更新", text);

        logs.Clear();
        Assert.Equal(second.Id, await f.Ensure());
        Assert.Contains("已是最新，无需更新", File.ReadAllText(marker));
        Assert.Contains(logs, line => line.Contains("20261117-2") && line.Contains("已是最新"));
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task VersionMarkerBeingHeldByAnotherProgramDoesNotFailTheUpdate()
    {
        using var f = new Fixture();
        var first = f.Add(("builtin/旧包/old.json", Route));
        Assert.Equal(first.Id, await f.Ensure());
        var marker = Path.Combine(f.Managed, "当前线路版本.txt");
        Assert.True(File.Exists(marker));
        var next = f.Add(("builtin/新包/new.json", Route));
        // 用户用记事本之类的程序独占打开了标识文件：只是给人看的记录写不进去，线路更新本身必须照常成功。
        await using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(next.Id, await f.Ensure());
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "新包", "new.json")));
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task CurrentFolderBrieflyLockedIsRetriedAndThenReplaced()
    {
        using var f = new Fixture();
        f.Add(("builtin/旧包/old.json", Route));
        await f.Ensure();
        var next = f.Add(("builtin/新包/new.json", Route));
        var originalMove = AutoHoeingRouteUpdater.MoveDirectory;
        var originalDelays = AutoHoeingRouteUpdater.MoveRetryDelays;
        // 真实占用：独占打开旧 current 里的文件，Windows 会拒绝改名该目录；第二次尝试前释放。
        var held = new FileStream(Path.Combine(f.Current, "builtin", "旧包", "old.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var attempts = 0;
        try
        {
            AutoHoeingRouteUpdater.MoveRetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
            AutoHoeingRouteUpdater.MoveDirectory = (source, destination) =>
            {
                if (source == f.Current && ++attempts == 2) held.Dispose();
                originalMove(source, destination);
            };
            Assert.Equal(next.Id, await f.Ensure());
        }
        finally
        {
            held.Dispose();
            AutoHoeingRouteUpdater.MoveDirectory = originalMove;
            AutoHoeingRouteUpdater.MoveRetryDelays = originalDelays;
        }
        Assert.True(attempts >= 2);
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "新包", "new.json")));
        Assert.False(Directory.Exists(Path.Combine(f.Current, "builtin", "旧包")));
        f.AssertNoPendingInstall();
    }

    [Fact]
    public async Task CurrentFolderStillLockedGivesChineseReasonAndKeepsOldRoutes()
    {
        using var f = new Fixture();
        var old = f.Add(("builtin/旧包/old.json", Route));
        await f.Ensure();
        f.Add(("builtin/新包/new.json", Route));
        var originalDelays = AutoHoeingRouteUpdater.MoveRetryDelays;
        var file = Path.Combine(f.Current, "builtin", "旧包", "old.json");
        try
        {
            AutoHoeingRouteUpdater.MoveRetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = await Assert.ThrowsAsync<IOException>(() => f.Ensure());
                Assert.Contains("被其他程序占用", error.Message);
                Assert.Contains("旧线路已保留", error.Message);
            }
        }
        finally { AutoHoeingRouteUpdater.MoveRetryDelays = originalDelays; }
        Assert.Equal(Route, File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(f.Current, "builtin", "新包")));
        using var installed = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Managed, "installed.json")));
        Assert.Equal(old.Id, installed.RootElement.GetProperty("id").GetString());
        f.AssertNoPendingInstall();
        // 占用解除后再次更新可以正常替换。
        Assert.NotEqual(old.Id, await f.Ensure());
        Assert.True(File.Exists(Path.Combine(f.Current, "builtin", "新包", "new.json")));
    }

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

    [Theory]
    [InlineData("http://121.4.78.52:8080")]
    [InlineData("http://121.4.78.52:8080/hub")]
    [InlineData("https://121.4.78.52")]
    public void IpAddressGetsAnActionableChineseMessageInsteadOfAnSslError(string address)
    {
        var error = Assert.Throws<InvalidDataException>(() => AutoHoeingRouteUpdater.RequireServerUri(address));
        Assert.Contains("不能写 IP", error.Message);
        Assert.Contains("config.json", error.Message);
        Assert.Contains("http://www.autobgi.cn:8080", error.Message);
    }

    [Fact]
    public void TlsNameMismatchIsTranslatedIntoTheSameEditHint()
    {
        var tls = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("remote party sent a TLS alert: '112'"));
        var translated = AutoHoeingRouteUpdater.ExplainTransportFailure(new Uri("https://autobgi.cn/"), tls);
        Assert.IsType<InvalidDataException>(translated);
        Assert.Contains("autobgi.cn", translated.Message);
        Assert.Contains("config.json", translated.Message);
        Assert.Same(tls, translated.InnerException);
        var other = new HttpRequestException("连接超时");
        Assert.Same(other, AutoHoeingRouteUpdater.ExplainTransportFailure(new Uri("https://www.autobgi.cn/"), other));
    }

    [Theory]
    [InlineData("http://www.autobgi.cn:8080", "http://www.autobgi.cn:8080")]
    [InlineData("  https://my-own.example.org  ", "https://my-own.example.org")]
    [InlineData("举例：https://bgi-sync.example.com(需部署自建服务)", "http://www.autobgi.cn:8080")]
    [InlineData("", "http://www.autobgi.cn:8080")]
    [InlineData(null, "http://www.autobgi.cn:8080")]
    public void UnsetOrLegacyPlaceholderAddressJustWorksWithTheOfficialServer(string? configured, string expected)
        => Assert.Equal(expected, BetterGenshinImpact.GameTask.AutoHoeing.CoordinatorServerAddress.Effective(configured));

    [Fact]
    public void FreshInstallDefaultIsTheWorkingOfficialAddress()
        => Assert.Equal("http://www.autobgi.cn:8080", new BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingConfig().CoordinatorServerUrl);

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
