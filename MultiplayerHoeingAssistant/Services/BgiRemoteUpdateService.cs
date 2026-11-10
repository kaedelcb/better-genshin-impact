using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    long? ActionsRunId = null,
    string? CnbArtifactTag = null)
{
    public string SizeText => SizeBytes is > 0 ? $"{SizeBytes / (double)(1L << 20):F1} MB" : "大小未知";
}

public sealed record BgiUpdateSourceResult(IReadOnlyList<BgiRemoteUpdateInfo> Packages, string? Message = null);

public static class BgiRemoteUpdateService
{
    private const string CnbPackageFile = "BetterGI_7z.7z";
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
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

    public static async Task<BgiUpdateSourceResult> FetchSourceAsync(BgiUpdateSource source, CancellationToken ct = default)
    {
        if (source.Endpoint is not { } endpoint) return new([]);
        try
        {
            switch (source.Kind)
            {
                case BgiUpdateSourceKind.Cnb:
                    return await FetchCnbAsync(CatalogClient, endpoint, ct).ConfigureAwait(false);

                case BgiUpdateSourceKind.CnbMirror:
                    return await FetchCnbMirrorAsync(CatalogClient, endpoint, ct).ConfigureAwait(false);

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

    internal static async Task<BgiUpdateSourceResult> FetchCnbAsync(HttpClient client, Uri versionUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var cancel = timeout.Token;
        var version = (await client.GetStringAsync(versionUrl, cancel).ConfigureAwait(false)).Trim();
        if (version.Length > 160 || !TryParseName($"BetterGI_v{version}.7z", out var name))
            return new([], "autoBGI源没有返回有效的茶包版版本号。");

        // CNB 的下载地址固定不变；HEAD 在发布入口可能没有 ETag，读取开头 64 字节来识别实际附件。
        var packageUrl = new Uri(versionUrl, CnbPackageFile);
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUrl);
        request.Headers.Range = new RangeHeaderValue(0, 63);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Headers.ETag is not { IsWeak: false } tag
            || response.Content.Headers.ContentRange is not { From: 0, Length: > 63 } range)
            return new([], "autoBGI源无法确认当前安装包，可刷新重试或切换来源。");
        await using var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        if (!await HasSevenZipSignatureAsync(body, cancel).ConfigureAwait(false))
            return new([], "autoBGI源返回的文件不是茶包版 7z 安装包。");

        return new([new(name!.FileName, packageUrl.AbsoluteUri, range.Length,
            response.Content.Headers.LastModified?.UtcDateTime, name, CnbArtifactTag: tag.Tag)]);
    }

    /// <summary>
    /// 第三方镜像仓库：/releases/latest 会跳转到 /releases/tag/&lt;标签&gt;，标签即版本号；
    /// 附件固定为 releases/download/&lt;标签&gt;/BetterGI_v&lt;标签&gt;.7z。镜像不提供哈希，靠 7z 签名与大小校验。
    /// </summary>
    internal static async Task<BgiUpdateSourceResult> FetchCnbMirrorAsync(HttpClient client, Uri latestUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var cancel = timeout.Token;

        using var latest = await client.GetAsync(latestUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        latest.EnsureSuccessStatusCode();
        const string marker = "/releases/tag/";
        var finalPath = latest.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        var at = finalPath.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return new([], "CNB 镜像没有返回最新发布，可刷新重试或切换来源。");
        var tag = Uri.UnescapeDataString(finalPath[(at + marker.Length)..].Trim('/'));
        if (tag.Length > 160 || !TryParseName($"BetterGI_v{tag}.7z", out var name))
            return new([], "CNB 镜像的最新发布不是有效的茶包版版本。");

        var packageUrl = new Uri($"{BgiUpdateSources.CnbMirrorRepository}/-/releases/download/{tag}/{name!.FileName}");
        using var head = new HttpRequestMessage(HttpMethod.Head, packageUrl);
        using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is not { } length || length <= 0)
            return new([], "CNB 镜像的安装包大小未知，无法下载校验。");

        return new([new(name.FileName, packageUrl.AbsoluteUri, length,
            response.Content.Headers.LastModified?.UtcDateTime, name)]);
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
                || ReadString(run, "conclusion") != "success") continue;
            var version = title["BetterGI ".Length..];
            // 公开发布时工作流标题保留输入标记，实际构建版本和安装包名称不包含它。
            if (version.StartsWith("release:", StringComparison.Ordinal)) version = version["release:".Length..];
            if (!TryParseName("BetterGI_v" + version + ".7z", out var nameInfo)) continue;

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

    public static Task<string> PreparePackageAsync(BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
        => PreparePackageAsync(DownloadClient, info, destDir, progress, ct);

    internal static async Task<string> PreparePackageAsync(HttpClient client, BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryParseName(info.FileName, out _)) throw new InvalidDataException("安装包文件名无效。");
        var path = Path.Combine(destDir, info.FileName);
        if (await IsSavedPackageValidAsync(info, path, progress, ct).ConfigureAwait(false))
            return path;
        return await DownloadAsync(client, info, destDir, progress, ct).ConfigureAwait(false);
    }

    private static string ReceiptPath(string package) => package + ".download-info";

    private static string DownloadIdentity(BgiRemoteUpdateInfo info)
    {
        var identity = JsonSerializer.Serialize(new
        {
            info.FileName, info.ActionsRunId,
            Sha256 = info.Sha256?.ToUpperInvariant(),
            Url = info.Sha256 == null ? info.Url : null,
            Size = info.Sha256 == null ? info.SizeBytes : null,
            Modified = info.Sha256 == null ? info.UpdatedAt : null,
        });
        // 保留其他下载源既有收据的身份；CNB 收据额外绑定选中时的附件 ETag。
        return BgiUpdateSources.Hash(info.CnbArtifactTag == null ? identity : $"{identity}\n{info.CnbArtifactTag}");
    }

    private static async Task<bool> IsSavedPackageValidAsync(BgiRemoteUpdateInfo info, string path,
        IProgress<string>? progress, CancellationToken ct)
    {
        // 只有文件名和 URL 时无法识别服务端是否替换过内容；仍可从本地列表手动选择已保存的包。
        if (info.CnbArtifactTag == null && info.Sha256 == null
            && (info.SizeBytes == null || (info.UpdatedAt == null && info.ActionsRunId == null))) return false;
        try
        {
            var receipt = new FileInfo(ReceiptPath(path));
            if (!File.Exists(path) || !receipt.Exists || receipt.Length > 512) return false;
            var lines = await File.ReadAllLinesAsync(receipt.FullName, ct).ConfigureAwait(false);
            if (lines.Length != 2 || lines[0] != DownloadIdentity(info)
                || !TryDigest(lines[1], out var expected) || expected == null) return false;

            // Actions 清单的 SHA-256 属于外层 ZIP；收据另存已经提取的 .7z 的哈希。
            progress?.Report("正在校验已下载的安装包…");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            return Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>先下载并校验临时文件；Actions 仅取 ZIP 内与运行版本匹配的一个 .7z。</summary>
    internal static async Task<string> DownloadAsync(HttpClient client, BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!TryParseName(info.FileName, out _)
            || !BgiUpdateSources.TryHttpUri(info.Url, httpsOnly: info.ActionsRunId.HasValue || info.CnbArtifactTag != null, out var url))
            throw new InvalidDataException("下载地址或安装包文件名无效。");

        Directory.CreateDirectory(destDir);
        var finalPath = Path.Combine(destDir, info.FileName);
        var token = Guid.NewGuid().ToString("N");
        var downloadPath = Path.Combine(destDir, $".download-{token}.part");
        var extractedPath = Path.Combine(destDir, $".extract-{token}.part");
        var inspectionPath = Path.Combine(destDir, $".version-{token}.part");
        var receiptPath = Path.Combine(destDir, $".receipt-{token}.part");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            var cancel = timeout.Token;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (info.CnbArtifactTag is { } expectedTag && (response.StatusCode != HttpStatusCode.OK
                || response.Headers.ETag is not { IsWeak: false } downloadTag || downloadTag.Tag != expectedTag))
                throw new InvalidDataException("autoBGI源的安装包已变动，请刷新来源后重新选择。");
            var responseLength = response.Content.Headers.ContentLength;
            if (info.SizeBytes is { } expected && responseLength is { } actual && expected != actual)
                throw new InvalidDataException("服务器返回的文件大小与更新清单不一致。");
            string archiveHash;
            await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            {
                archiveHash = await CopyAndVerifyAsync(input, downloadPath, info.SizeBytes ?? responseLength, info.Sha256, progress, cancel).ConfigureAwait(false);
            }

            var candidate = downloadPath;
            if (info.ActionsRunId.HasValue)
            {
                progress?.Report("Actions 下载校验通过，正在提取 .7z 安装包…");
                using var zip = ZipFile.OpenRead(downloadPath);
                var entries = zip.Entries.Where(e => e.Name == info.FileName && e.Length > 0).ToList();
                if (entries.Count != 1) throw new InvalidDataException("Actions 产物没有唯一匹配所选版本的 .7z 安装包。");
                await using var entry = entries[0].Open();
                archiveHash = await CopyAndVerifyAsync(entry, extractedPath, entries[0].Length, null, null, cancel).ConfigureAwait(false);
                candidate = extractedPath;
            }

            await using (var archive = File.OpenRead(candidate))
            {
                if (!await HasSevenZipSignatureAsync(archive, cancel).ConfigureAwait(false))
                    throw new InvalidDataException("下载的文件不是有效的 .7z 安装包。");
            }
            if (info.CnbArtifactTag != null)
            {
                progress?.Report("正在核对 autoBGI源安装包内的 BGI 版本…");
                await Task.Run(() => BgiPackageUpdateService.VerifyCnbPackageVersion(candidate, inspectionPath, info.FileName, cancel), cancel)
                    .ConfigureAwait(false);
            }
            await File.WriteAllLinesAsync(receiptPath, [DownloadIdentity(info), archiveHash], cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            File.Move(candidate, finalPath, overwrite: true);
            try { File.Move(receiptPath, ReceiptPath(finalPath), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 已下载的包仍可作为本地包使用。 */ }
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
            TryDelete(inspectionPath);
            TryDelete(receiptPath);
        }
    }

    private static async Task<bool> HasSevenZipSignatureAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[SevenZipSignature.Length];
        return await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken: ct).ConfigureAwait(false) == header.Length
            && header.AsSpan().SequenceEqual(SevenZipSignature);
    }

    private static async Task<string> CopyAndVerifyAsync(Stream input, string dest, long? size, string? digest,
        IProgress<string>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        var buffer = new byte[65536];
        var timer = Stopwatch.StartNew();
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (size.HasValue && received > size.Value) throw new InvalidDataException("下载文件大于预期大小。");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (progress != null && timer.ElapsedMilliseconds >= 300)
            {
                progress.Report(size is > 0
                    ? $"正在下载 {received / 1048576.0:F1}/{size.Value / 1048576.0:F1} MB…"
                    : $"正在下载 {received / 1048576.0:F1} MB…");
                timer.Restart();
            }
        }
        if (size.HasValue && received != size.Value) throw new InvalidDataException("文件不完整，无法更新。");
        var result = Convert.ToHexString(hash.GetHashAndReset());
        if (digest != null && !result.Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包 SHA-256 校验失败，请更换来源或重新下载。");
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
