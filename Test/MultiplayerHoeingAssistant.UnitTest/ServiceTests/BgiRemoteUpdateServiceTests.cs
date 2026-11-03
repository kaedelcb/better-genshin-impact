using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    public void DownloadDirectories_SeparateLocalSameNames_Sources_AndActionsRuns()
    {
        var config = new AssistConfig { ServerUrl = "http://server.example" };
        var sources = BgiUpdateSources.List(config);
        var dirs = sources.Where(s => s.Kind != BgiUpdateSourceKind.Local).Select(s => s.DownloadDirectory(@"D:\DOWN")).ToList();
        Assert.Equal(dirs.Count, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(@"D:\DOWN", dirs);
        var actions = sources.Single(s => s.Kind == BgiUpdateSourceKind.GitHubActions);
        Assert.NotEqual(actions.DownloadDirectory(@"D:\DOWN", 8), actions.DownloadDirectory(@"D:\DOWN", 9));
        Assert.NotEqual(sources.Single(s => s.Kind == BgiUpdateSourceKind.Coordinator).DownloadDirectory(@"D:\DOWN"),
            BgiUpdateSources.List(new AssistConfig { ServerUrl = "http://other.example" }).Single(s => s.Kind == BgiUpdateSourceKind.Coordinator)
                .DownloadDirectory(@"D:\DOWN"));
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
    public async Task ActionsDownload_VerifiesZipAndExtractsOnlySelectedPackage_WithoutReplacingOnHashMismatch()
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
            using var client = new HttpClient(new ArchiveResponse(archive));
            var path = await BgiRemoteUpdateService.DownloadAsync(client, info, directory.FullName, null);
            Assert.Equal(fixture, await File.ReadAllBytesAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(() => BgiRemoteUpdateService.DownloadAsync(
                client, info with { Sha256 = new string('0', 64) }, directory.FullName, null));
            Assert.Equal(fixture, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory.FullName)));
            Assert.False(File.Exists(Path.Combine(directory.Parent!.FullName, "escaped.txt")));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class ArchiveResponse(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    private static BgiArchiveName ParseName(string fileName)
    {
        Assert.True(BgiUpdateDecisions.TryParse(fileName, out var info));
        return info!;
    }
}
