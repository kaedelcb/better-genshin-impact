using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 更新源发现、来源隔离，以及下载失败时对原安装包的保护。
/// </summary>
public class BgiRemoteUpdateServiceTests
{
    [Fact]
    public async Task Cnb_SourceUsesTextVersionAndPinsTheActualAttachment()
    {
        var source = BgiUpdateSources.List(new AssistConfig()).Single(s => s.Kind == BgiUpdateSourceKind.Cnb);
        using var client = new HttpClient(new CnbResponse("\"selected\"", ranged: true));
        var result = await BgiRemoteUpdateService.FetchCnbAsync(client, source.Endpoint!, CancellationToken.None);
        var package = Assert.Single(result.Packages);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", package.FileName);
        Assert.EndsWith("/BetterGI_7z.7z", package.Url);
        Assert.Equal(530653734L, package.SizeBytes);
        Assert.Equal("\"selected\"", package.CnbArtifactTag);
    }

    [Theory]
    [InlineData("\"replaced\"", "安装包已变动")]
    [InlineData("\"selected\"", "与所选版本不符")]
    public async Task Cnb_DownloadRejectsChangedAttachmentOrWrongPackageWithoutReplacingSavedFile(string servedTag, string error)
    {
        const string name = "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z";
        var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        using var client = new HttpClient(new CnbResponse(servedTag, ranged: false, payload: fixture));
        var directory = Directory.CreateTempSubdirectory("NexusBgiCnb-");
        try
        {
            var path = Path.Combine(directory.FullName, name);
            await File.WriteAllTextAsync(path, "prior archive");
            var info = new BgiRemoteUpdateInfo(name, "https://cnb.cool/example/BetterGI_7z.7z", fixture.Length, null,
                ParseName(name), CnbArtifactTag: "\"selected\"");
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                BgiRemoteUpdateService.PreparePackageAsync(client, info, directory.FullName, null));
            Assert.Contains(error, ex.Message);
            Assert.Equal("prior archive", await File.ReadAllTextAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class CnbResponse(string tag, bool ranged, byte[]? payload = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/runForVersion.txt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("0.64.2+lcb.22.7-NexusBGI-fix13\r\n") });
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue(tag);
            if (ranged)
            {
                Assert.Equal("bytes=0-63", request.Headers.Range?.ToString());
                response.Content = new ByteArrayContent([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 63, 530653734);
            }
            else response.Content = new ByteArrayContent(payload!);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void StartupCheck_DefaultsOffForOldConfig_UsesPreferredNetworkSource()
    {
        var config = JsonSerializer.Deserialize<AssistConfig>("""{"serverUrl":"https://coordinator.example"}""")!;
        Assert.False(config.CheckBgiUpdatesOnStartup);
        Assert.Equal(BgiUpdateSourceKind.Coordinator, BgiUpdateSources.SelectStartupSource(config).Kind);
        config.StandaloneMode = true;
        config.BgiUpdateSourceId = BgiUpdateSources.LocalId;
        Assert.Equal(BgiUpdateSourceKind.GitHubReleases, BgiUpdateSources.SelectStartupSource(config).Kind);
        config.BgiUpdateSourceId = "github-actions";
        Assert.Equal(BgiUpdateSourceKind.GitHubActions, BgiUpdateSources.SelectStartupSource(config).Kind);
        const string manifest = "https://mirror.example/latest.json";
        config.BgiCustomUpdateSources = [new("镜像", manifest)];
        config.BgiUpdateSourceId = BgiUpdateSources.List(config).Single(s => s.Kind == BgiUpdateSourceKind.Custom).Id;
        Assert.Equal(manifest, BgiUpdateSources.SelectStartupSource(config).Endpoint!.AbsoluteUri);
    }

    // ===== 清单解析 =====

    [Fact]
    public void ParseManifest_FullFields_Parses()
    {
        var json = """
            {"fileName":"BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix16.7z","sizeBytes":489000000,"updatedAt":"2026-09-14T03:00:00Z"}
            """;
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080");
        Assert.NotNull(info);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix16.7z", info!.FileName);
        // url 按服务器根拼接（文件名做了 URL 转义）
        Assert.StartsWith("http://srv:8080/bgi-update/BetterGI_v0.64.2", info.Url);
        Assert.EndsWith("7z", info.Url);
        Assert.Equal(489000000, info.SizeBytes);
        Assert.NotNull(info.UpdatedAt);
        Assert.Equal("fix16", info.NameInfo.TailRest);
    }

    [Fact]
    public void ParseManifest_RelativeUrl_IsCombinedWithServerRoot()
    {
        var json = """{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","url":"/files/a.7z"}""";
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080/");
        Assert.Equal("http://srv:8080/files/a.7z", info!.Url);
    }

    [Fact]
    public void ParseManifest_AbsoluteUrl_Kept()
    {
        var json = """{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","url":"https://cos.example.com/a.7z"}""";
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080");
        Assert.Equal("https://cos.example.com/a.7z", info!.Url);
    }

    [Theory]
    [InlineData("{}")]                                                   // 缺 fileName
    [InlineData("""{"fileName":"random_garbage.7z"}""")]                 // 名字不合茶包规范 → 拒收（防误装非茶包版）
    [InlineData("""{"fileName":"BetterGI_v0.64.2.7z"}""")]               // 官方版无 lcb → 拒收
    [InlineData("""{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","sha256":42}""")]
    [InlineData("""{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","sizeBytes":"100"}""")]
    [InlineData("not-json")]                                             // 坏 JSON
    public void ParseManifest_Invalid_Rejected(string json)
    {
        Assert.Null(BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080"));
    }

    [Fact]
    public void CustomManifest_ResolvesRelativePackageAddress_AndRejectsHttp()
    {
        Assert.False(BgiUpdateSources.TryCustom("镜像", "http://mirror.example/bgi-update/latest.json", out _));
        Assert.True(BgiUpdateSources.TryCustom("", "https://mirror.example/releases/latest.json", out var source));
        const string json = """{"fileName":"BetterGI_v0.66.3+lcb.23.0.7z","url":"packages/BetterGI_v0.66.3%2Blcb.23.0.7z"}""";
        Assert.Equal("https://mirror.example/releases/packages/BetterGI_v0.66.3%2Blcb.23.0.7z",
            BgiRemoteUpdateService.ParseManifestAt(json, source!.Endpoint!, httpsOnly: true)!.Url);
        Assert.Null(BgiRemoteUpdateService.ParseManifestAt(json.Replace("packages/", "http://mirror.example/"), source.Endpoint!, httpsOnly: true));
    }

    [Fact]
    public void DownloadDirectory_IsPackageDirectoryItself_ForAllSources()
    {
        var config = new AssistConfig { ServerUrl = "http://server.example" };
        var sources = BgiUpdateSources.List(config);
        foreach (var source in sources.Where(s => s.Kind != BgiUpdateSourceKind.Local))
        {
            Assert.Equal(@"D:\DOWN", source.DownloadDirectory(@"D:\DOWN"));
            Assert.Equal(@"D:\DOWN", source.DownloadDirectory(@"D:\DOWN", 8));
        }
    }

    [Fact]
    public void Releases_RanksByVersion_NotApiOrder_AndRejectsPrereleases()
    {
        object Release(int fix, bool prerelease) => new
        {
            draft = false, prerelease, assets = new[]
            {
                new
                {
                    name = $"BetterGI_v0.66.3+lcb.23.0-NexusBGI-fix{fix}.7z", state = "uploaded", size = 100,
                    browser_download_url = $"https://github.com/kaedelcb/better-genshin-impact/releases/download/fix{fix}/BetterGI.7z",
                    digest = "sha256:" + new string('a', 64)
                },
            },
        };
        var packages = BgiRemoteUpdateService.ParseReleases(JsonSerializer.Serialize(new[] { Release(7, false), Release(8, false), Release(99, true) }));
        Assert.Equal(new[] { "fix8", "fix7" }, packages.Select(p => p.NameInfo.TailRest));
        Assert.Equal(new string('a', 64), packages[0].Sha256);
    }

    [Fact]
    public void Actions_RequiresSuccessfulRunAndNonexpiredSevenZipArtifact()
    {
        const string runs = """
        {"workflow_runs":[
         {"id":1,"display_title":"BetterGI 0.66.3+lcb.23.0-NexusBGI-fix8","conclusion":"success"},
         {"id":2,"display_title":"BetterGI 0.66.3+lcb.23.0-NexusBGI-fix9","conclusion":"failure"},
         {"id":3,"display_title":"BetterGI 0.66.3+lcb.23.0-NexusBGI-fix10","conclusion":"success"}]}
        """;
        const string artifacts = """
        {"artifacts":[
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":400,"workflow_run":{"id":1}},
         {"name":"BetterGI_Install","expired":false,"size_in_bytes":500,"workflow_run":{"id":1}},
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":400,"workflow_run":{"id":2}},
         {"name":"BetterGI_7z","expired":true,"size_in_bytes":400,"workflow_run":{"id":3}}]}
        """;
        var package = Assert.Single(BgiRemoteUpdateService.ParseActions(runs, artifacts));
        Assert.Equal(1, package.ActionsRunId);
        Assert.Equal("BetterGI_v0.66.3+lcb.23.0-NexusBGI-fix8.7z", package.FileName);
        Assert.EndsWith("/actions/runs/1/BetterGI_7z.zip", package.Url);
    }

    [Fact]
    public void Actions_RecognizesReleasePrefix_WithoutAcceptingInvalidOrUnavailablePackages()
    {
        const string runs = """
        {"workflow_runs":[
         {"id":41761665747,"display_title":"BetterGI release:0.66.3+lcb.23.0-NexusBGI-fix9","conclusion":"success"},
         {"id":41622420441,"display_title":"BetterGI 0.66.3+lcb.23.0-NexusBGI-fix8","conclusion":"success"},
         {"id":3,"display_title":"BetterGI release:0.66.3+lcb.23.0-NexusBGI-fix10","conclusion":"failure"},
         {"id":4,"display_title":"BetterGI release:0.66.3+lcb.23.0-NexusBGI-fix11","conclusion":"success"},
         {"id":5,"display_title":"BetterGI release:0.66.3","conclusion":"success"},
         {"id":6,"display_title":"BetterGI release:release:0.66.3+lcb.23.0-NexusBGI-fix12","conclusion":"success"}]}
        """;
        const string artifacts = """
        {"artifacts":[
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":530942110,"digest":"sha256:142c2b4048bf12b257ab1067b4bbf10f1d6fd4e4a223d5acb9a641180a6131a3","workflow_run":{"id":41761665747}},
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":530938547,"workflow_run":{"id":41622420441}},
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":400,"workflow_run":{"id":3}},
         {"name":"BetterGI_7z","expired":true,"size_in_bytes":400,"workflow_run":{"id":4}},
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":400,"workflow_run":{"id":5}},
         {"name":"BetterGI_7z","expired":false,"size_in_bytes":400,"workflow_run":{"id":6}}]}
        """;

        var packages = BgiRemoteUpdateService.ParseActions(runs, artifacts);
        Assert.Equal(new[] { "fix9", "fix8" }, packages.Select(p => p.NameInfo.TailRest));
        Assert.Equal("BetterGI_v0.66.3+lcb.23.0-NexusBGI-fix9.7z", packages[0].FileName);
        Assert.Equal(41761665747L, packages[0].ActionsRunId);
        Assert.Equal(530942110L, packages[0].SizeBytes);
        Assert.Equal("142c2b4048bf12b257ab1067b4bbf10f1d6fd4e4a223d5acb9a641180a6131a3", packages[0].Sha256);
        Assert.EndsWith("/actions/runs/41761665747/BetterGI_7z.zip", packages[0].Url);
    }

    [Fact]
    public async Task ActionsDownload_ReusesVerifiedInnerPackage_WithoutReplacingOnHashMismatch()
    {
        const string name = "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z";
        var fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var selected = zip.CreateEntry(name).Open()) await selected.WriteAsync(fixture);
            using var unrelated = zip.CreateEntry("../escaped.txt").Open();
            await unrelated.WriteAsync(new byte[] { 1 });
        }
        var archive = bytes.ToArray();
        var info = new BgiRemoteUpdateInfo(name, "https://nightly.link/example", archive.Length, null, ParseName(name),
            Convert.ToHexString(SHA256.HashData(archive)), ActionsRunId: 1);
        var directory = Directory.CreateTempSubdirectory("NexusBgiDownload-");
        try
        {
            using var response = new ArchiveResponse(archive);
            using var client = new HttpClient(response, disposeHandler: false);
            var path = await BgiRemoteUpdateService.DownloadAsync(client, info, directory.FullName, null);
            Assert.Equal(fixture, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, await BgiRemoteUpdateService.PreparePackageAsync(client, info, directory.FullName, null));
            Assert.Equal(1, response.Requests); // 不能拿外层 ZIP 的哈希和已提取的 .7z 比较后误判。
            await Assert.ThrowsAsync<InvalidDataException>(() => BgiRemoteUpdateService.PreparePackageAsync(
                client, info with { Sha256 = new string('0', 64) }, directory.FullName, null));
            Assert.Equal(2, response.Requests);
            Assert.Equal(fixture, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName, "*.7z")));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.part"));
            // 同一来源清单不变，但磁盘上的副本已被改动时也不能复用。
            await File.WriteAllTextAsync(path, "changed");
            Assert.Equal(path, await BgiRemoteUpdateService.PreparePackageAsync(client, info, directory.FullName, null));
            Assert.Equal(3, response.Requests);
            Assert.Equal(fixture, await File.ReadAllBytesAsync(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class ArchiveResponse(byte[] content) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private static BgiArchiveName ParseName(string fileName)
    {
        Assert.True(BgiUpdateDecisions.TryParse(fileName, out var info));
        return info!;
    }
}
