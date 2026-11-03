using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

public sealed record BgiRemoteUpdateInfo(
    string FileName,
    string Url,
    long? SizeBytes,
    DateTime? UpdatedAt,
    BgiArchiveName NameInfo,
    string? Sha256 = null,
    long? ActionsRunId = null)
{
    public string SizeText => SizeBytes is > 0 ? $"{SizeBytes / (double)(1L << 20):F1} MB" : "大小未知";
}

public sealed record BgiUpdateSourceResult(IReadOnlyList<BgiRemoteUpdateInfo> Packages, string? Message = null);

public static class BgiRemoteUpdateService
{
    private static readonly HttpClient CatalogClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
        DefaultRequestHeaders =
        {
            UserAgent = { new("Nexus-BGI", "1.0") },
        },
    };

    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<BgiRemoteUpdateInfo?> FetchLatestAsync(string? serverBaseUrl, CancellationToken ct = default)
    {
        if (!BgiUpdateSources.TryHttpUri(serverBaseUrl, httpsOnly: false, out var uri) || uri!.Query.Length != 0) return null;
        var source = new BgiUpdateSource(BgiUpdateSources.CoordinatorId, "协调服务器", BgiUpdateSourceKind.Coordinator,
            new Uri(BuildManifestUrl(uri.AbsoluteUri)));
        return (await FetchSourceAsync(source, ct)).Packages.FirstOrDefault();
    }

    public static async Task<BgiUpdateSourceResult> FetchSourceAsync(BgiUpdateSource source, CancellationToken ct = default)
    {
        if (source.Endpoint is not { } endpoint) return new([]);
        try
        {
            switch (source.Kind)
            {
                case BgiUpdateSourceKind.GitHubActions:
                    var runs = FetchJsonAsync(endpoint, ct);
                    var artifacts = FetchJsonAsync(new Uri(BgiUpdateSources.ActionsArtifacts), ct);
                    await Task.WhenAll(runs, artifacts).ConfigureAwait(false);
                    var builds = ParseActions(await runs, await artifacts);
                    return new(builds, builds.Count == 0 ? "没有找到可下载的成功构建；Actions 产物过期后会从列表移除。" : null);

                case BgiUpdateSourceKind.GitHubReleases:
                    var releases = ParseReleases(await FetchJsonAsync(endpoint, ct).ConfigureAwait(false));
                    return new(releases, releases.Count == 0 ? "没有找到正式发布的茶包版 .7z 附件。" : null);

                default:
                    var json = await FetchJsonAsync(endpoint, ct).ConfigureAwait(false);
                    var info = ParseManifestAt(json, endpoint, source.Kind == BgiUpdateSourceKind.Custom);
                    return info != null ? new([info]) : new([], "更新清单内容无效，无法识别茶包版安装包。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new([], "请求超时，可以刷新重试或选择其他来源。"); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new([], "此来源未提供可用的更新清单或安装包（HTTP 404）。");
        }
        catch (HttpRequestException ex)
        {
            return new([], ex.StatusCode is { } status
                ? $"无法读取此来源（HTTP {(int)status}），可以刷新重试或切换来源。"
                : "无法连接此来源，请检查网络或切换来源。");
        }
        catch (JsonException) { return new([], "此来源返回的更新数据无效。"); }
    }

    private static async Task<string> FetchJsonAsync(Uri endpoint, CancellationToken ct)
    {
        using var response = await CatalogClient.GetAsync(endpoint, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static BgiRemoteUpdateInfo? ParseManifest(string json, string serverBaseUrl)
        => BgiUpdateSources.TryHttpUri(serverBaseUrl, httpsOnly: false, out var uri) && uri!.Query.Length == 0
            ? ParseManifestAt(json, new Uri(BuildManifestUrl(uri.AbsoluteUri)), httpsOnly: false)
            : null;

    public static BgiRemoteUpdateInfo? ParseManifestAt(string json, Uri manifestUri, bool httpsOnly)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fileName = ReadString(root, "fileName");
            if (!TryParseName(fileName, out var nameInfo)) return null;
            var url = ReadString(root, "url");
            if (string.IsNullOrWhiteSpace(url)) url = Uri.EscapeDataString(nameInfo!.FileName);
            if (!Uri.TryCreate(manifestUri, url, out var target)
                || !BgiUpdateSources.TryHttpUri(target.AbsoluteUri, httpsOnly, out _)) return null;
            var size = ReadPositiveLength(root, "sizeBytes");
            if (TryProperty(root, "sizeBytes", out var sizeToken) && sizeToken.ValueKind != JsonValueKind.Null && size is null) return null;
            if (TryProperty(root, "sha256", out var digestToken) && digestToken.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return null;
            if (!TryDigest(ReadString(root, "sha256"), out var sha256)) return null;
            return new(nameInfo!.FileName, target.AbsoluteUri, size,
                ReadDate(root, "updatedAt"), nameInfo, sha256);
        }
        catch (JsonException) { return null; }
    }

    public static List<BgiRemoteUpdateInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        List<BgiRemoteUpdateInfo> packages = [];
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!IsBoolean(release, "draft", false) || !IsBoolean(release, "prerelease", false)) continue;
            foreach (var asset in ReadArray(release, "assets"))
            {
                if (ReadString(asset, "state") != "uploaded"
                    || !TryParseName(ReadString(asset, "name"), out var nameInfo)
                    || !BgiUpdateSources.TryHttpUri(ReadString(asset, "browser_download_url"), httpsOnly: true, out var url)
                    || !url!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !url.AbsolutePath.StartsWith($"/{BgiUpdateSources.GitHubRepository}/releases/download/", StringComparison.OrdinalIgnoreCase)
                    || !TryDigest(ReadString(asset, "digest"), out var digest)) continue;

                packages.Add(new(nameInfo!.FileName, url.AbsoluteUri, ReadPositiveLength(asset, "size"),
                    ReadDate(asset, "updated_at"), nameInfo, digest));
            }
        }
        return Sort(packages)
            .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.UpdatedAt).First())
            .Take(5).ToList();
    }

    public static List<BgiRemoteUpdateInfo> ParseActions(string runsJson, string artifactsJson)
    {
        using var runs = JsonDocument.Parse(runsJson);
        using var artifacts = JsonDocument.Parse(artifactsJson);
        var available = ReadArray(artifacts.RootElement, "artifacts")
            .Where(a => ReadString(a, "name") == "BetterGI_7z" && IsBoolean(a, "expired", false))
            .Select(a => (Artifact: a, RunId: TryProperty(a, "workflow_run", out var run) ? ReadPositiveLength(run, "id") : null))
            .Where(a => a.RunId.HasValue).ToLookup(a => a.RunId!.Value, a => a.Artifact);
        List<BgiRemoteUpdateInfo> packages = [];
        foreach (var run in ReadArray(runs.RootElement, "workflow_runs"))
        {
            var runId = ReadPositiveLength(run, "id");
            var title = ReadString(run, "display_title");
            if (runId is null || title == null || !title.StartsWith("BetterGI ", StringComparison.Ordinal)
                || ReadString(run, "conclusion") != "success"
                || !TryParseName("BetterGI_v" + title["BetterGI ".Length..] + ".7z", out var nameInfo)) continue;

            // 同一个 run 用固定的 BetterGI_7z 产物，既不混入 .exe，也不接受失败、过期或其他工作流的包。
            var items = available[runId.Value].ToList();
            if (items.Count != 1) continue;
            var artifact = items[0];
            if (ReadPositiveLength(artifact, "size_in_bytes") is not { } length
                || !TryDigest(ReadString(artifact, "digest"), out var digest)) continue;
            var url = $"https://nightly.link/{BgiUpdateSources.GitHubRepository}/actions/runs/{runId.Value}/BetterGI_7z.zip";
            packages.Add(new(nameInfo!.FileName, url, length, ReadDate(artifact, "created_at"), nameInfo, digest, runId));
        }
        return Sort(packages).ThenByDescending(p => p.ActionsRunId).Take(5).ToList();
    }

    private static IOrderedEnumerable<BgiRemoteUpdateInfo> Sort(List<BgiRemoteUpdateInfo> packages)
        => packages.OrderByDescending(p => p.NameInfo, Comparer<BgiArchiveName>.Create(BgiUpdateDecisions.CompareNewer));

    private static bool TryParseName(string? value, out BgiArchiveName? info)
    {
        info = null;
        return value != null && value.Length <= 200 && value == value.Trim() && BgiUpdateDecisions.TryParse(value, out info);
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement result)
    {
        result = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out result);
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string name)
        => TryProperty(element, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? ReadString(JsonElement element, string name)
        => TryProperty(element, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsBoolean(JsonElement element, string name, bool expected)
        => TryProperty(element, name, out var v) && v.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static long? ReadPositiveLength(JsonElement element, string name)
        => TryProperty(element, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : null;

    private static DateTime? ReadDate(JsonElement element, string name)
        => TryProperty(element, name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var date) ? date : null;

    private static bool TryDigest(string? value, out string? digest)
    {
        digest = null;
        if (value == null) return true;
        var hex = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return false;
        digest = hex;
        return true;
    }

    public static string BuildManifestUrl(string serverBaseUrl)
        => serverBaseUrl.TrimEnd('/') + "/bgi-update/latest.json";

    /// <summary>先下载并校验临时文件；Actions 仅取 ZIP 内与运行版本匹配的一个 .7z。</summary>
    public static Task<string> DownloadAsync(BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
        => DownloadAsync(DownloadClient, info, destDir, progress, ct);

    internal static async Task<string> DownloadAsync(HttpClient client, BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!TryParseName(info.FileName, out _)
            || !BgiUpdateSources.TryHttpUri(info.Url, httpsOnly: info.ActionsRunId.HasValue, out var url))
            throw new InvalidDataException("下载地址或安装包文件名无效。");

        Directory.CreateDirectory(destDir);
        var finalPath = Path.Combine(destDir, info.FileName);
        var token = Guid.NewGuid().ToString("N");
        var downloadPath = Path.Combine(destDir, $".download-{token}.part");
        var extractedPath = Path.Combine(destDir, $".extract-{token}.part");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            var cancel = timeout.Token;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            var responseLength = response.Content.Headers.ContentLength;
            if (info.SizeBytes is { } expected && responseLength is { } actual && expected != actual)
                throw new InvalidDataException("服务器返回的文件大小与更新清单不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            {
                await CopyAndVerifyAsync(input, downloadPath, info.SizeBytes ?? responseLength, info.Sha256, progress, cancel);
            }

            var candidate = downloadPath;
            if (info.ActionsRunId.HasValue)
            {
                progress?.Report("Actions 下载校验通过，正在提取 .7z 安装包…");
                using var zip = ZipFile.OpenRead(downloadPath);
                var entries = zip.Entries.Where(e => e.Name == info.FileName && e.Length > 0).ToList();
                if (entries.Count != 1) throw new InvalidDataException("Actions 产物没有唯一匹配所选版本的 .7z 安装包。");
                await using var entry = entries[0].Open();
                await CopyAndVerifyAsync(entry, extractedPath, entries[0].Length, null, null, cancel);
                candidate = extractedPath;
            }

            await using (var archive = File.OpenRead(candidate))
            {
                var header = new byte[6];
                if (await archive.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken: cancel) != header.Length
                    || !header.AsSpan().SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
                    throw new InvalidDataException("下载的文件不是有效的 .7z 安装包。");
            }
            cancel.ThrowIfCancellationRequested();
            File.Move(candidate, finalPath, overwrite: true);
            progress?.Report("安装包已下载并校验。");
            return finalPath;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("下载安装包超时，可以切换来源后重试。");
        }
        finally
        {
            TryDelete(downloadPath);
            TryDelete(extractedPath);
        }
    }

    private static async Task CopyAndVerifyAsync(Stream input, string dest, long? size, string? digest,
        IProgress<string>? progress, CancellationToken ct)
    {
        using var hash = digest == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        var buffer = new byte[65536];
        var timer = Stopwatch.StartNew();
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            received += read;
            if (size.HasValue && received > size.Value) throw new InvalidDataException("下载文件大于预期大小。");
            hash?.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            if (progress != null && timer.ElapsedMilliseconds >= 300)
            {
                progress.Report(size is > 0
                    ? $"正在下载 {received / 1048576.0:F1}/{size.Value / 1048576.0:F1} MB…"
                    : $"正在下载 {received / 1048576.0:F1} MB…");
                timer.Restart();
            }
        }
        if (size.HasValue && received != size.Value) throw new InvalidDataException("文件不完整，无法更新。");
        if (hash != null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包 SHA-256 校验失败，请更换来源或重新下载。");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
