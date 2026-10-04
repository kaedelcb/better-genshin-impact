using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>服务器上检测到的最新更新包（/bgi-update/latest.json）。</summary>
/// <param name="FileName">包文件名（已过 BgiUpdateDecisions 茶包命名校验）。</param>
/// <param name="Url">下载地址（绝对地址，或相对服务器根的 /bgi-update/xxx）。</param>
/// <param name="SizeBytes">文件大小（服务端清单可提供；null=未知）。</param>
/// <param name="UpdatedAt">服务端文件修改时间（可空）。</param>
/// <param name="NameInfo">解析出的版本信息（比较新旧用）。</param>
public sealed record BgiRemoteUpdateInfo(
    string FileName,
    string Url,
    long? SizeBytes,
    DateTime? UpdatedAt,
    BgiArchiveName NameInfo)
{
    public string SizeText => SizeBytes is > 0 ? $"{SizeBytes / (double)(1L << 20):F1} MB" : "大小未知";
}

/// <summary>
/// 服务器更新包拉取与下载（"网络"来源）。
/// 服务端约定：协调服挂载 BGI_UPDATE_ROOT 目录后，
///   GET {服务器}/bgi-update/latest.json  → 由服务端扫描目录自动生成的最新包清单
///   GET {服务器}/bgi-update/{文件名}      → 安装包本体
/// 拉取失败（没挂目录/网络抖动/清单缺失）一律静默返回 null，视为"无网络更新"，绝不打扰主流程。
/// </summary>
public static class BgiRemoteUpdateService
{
    /// <summary>把候选网络包合并进包列表（已存在同路径条目则不动；加入后按版本从新到旧重排）。</summary>
    public static List<BgiUpdatePackage> MergeNetworkPackage(
        List<BgiUpdatePackage> packages, BgiRemoteUpdateInfo? info, string expectedLocalDir)
    {
        if (info == null) return packages;
        var localPath = Path.Combine(expectedLocalDir, info.FileName);
        if (packages.Any(p => string.Equals(p.FullPath, localPath, StringComparison.OrdinalIgnoreCase))) return packages;
        packages.Add(new BgiUpdatePackage(
            info.FileName, localPath, info.SizeBytes ?? 0, info.UpdatedAt ?? DateTime.Now,
            info.NameInfo, BgiPackageSource.Network));
        return packages
            .OrderByDescending(p => p.NameInfo, Comparer<BgiArchiveName>.Create(BgiUpdateDecisions.CompareNewer))
            .ToList();
    }

    /// <summary>拉取服务器最新包清单。任何失败返回 null（静默）。</summary>
    public static async Task<BgiRemoteUpdateInfo?> FetchLatestAsync(string? serverBaseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl)) return null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var resp = await client.GetAsync(BuildManifestUrl(serverBaseUrl), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseManifest(json, serverBaseUrl);
        }
        catch
        {
            return null; // 没挂更新目录/网络不可达：静默视为无更新
        }
    }

    /// <summary>清单解析（纯逻辑，内部分离便于测试）。坏 JSON/字段不合法一律返回 null。</summary>
    public static BgiRemoteUpdateInfo? ParseManifest(string json, string serverBaseUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fileName = root.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            if (!BgiUpdateDecisions.TryParse(fileName, out var nameInfo) || nameInfo is null) return null;

            // url：清单可给绝对地址（外部对象存储）；给相对地址或缺失则按 {server}/bgi-update/{文件名} 拼
            var url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                url = BuildDownloadUrl(serverBaseUrl, fileName);
            else if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                url = serverBaseUrl.TrimEnd('/') + (url.StartsWith('/') ? url : "/" + url);

            long? sizeBytes = root.TryGetProperty("sizeBytes", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv) ? sv : null;
            DateTime? updatedAt = root.TryGetProperty("updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String && DateTime.TryParse(ua.GetString(), out var uad) ? uad : null;
            return new BgiRemoteUpdateInfo(fileName, url, sizeBytes, updatedAt, nameInfo);
        }
        catch
        {
            return null;
        }
    }

    public static string BuildManifestUrl(string serverBaseUrl)
        => serverBaseUrl.TrimEnd('/') + "/bgi-update/latest.json";

    public static string BuildDownloadUrl(string serverBaseUrl, string fileName)
        => serverBaseUrl.TrimEnd('/') + "/bgi-update/" + Uri.EscapeDataString(fileName);

    /// <summary>
    /// 下载包到 destDir：先写 {文件名}.part 临时文件（中断/失败不留半截正式包），完成后改名到位（覆盖旧同名包）。
    /// 返回最终文件完整路径。进度约 300ms 上报一次。
    /// </summary>
    public static async Task<string> DownloadAsync(BgiRemoteUpdateInfo info, string destDir,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var finalPath = Path.Combine(destDir, info.FileName);
        var partPath = finalPath + ".part";

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // 大包耗时长，取消交给 ct
        using var resp = await client.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.SizeBytes ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var outFs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long readTotal = 0;
            int read;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await outFs.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(300))
                {
                    lastReport = sw.Elapsed;
                    progress?.Report(total > 0
                        ? $"正在下载 {readTotal / 1048576.0:F1}/{total / 1048576.0:F1} MB…"
                        : $"正在下载 {readTotal / 1048576.0:F1} MB…");
                }
            }
        }

        if (File.Exists(finalPath)) File.Delete(finalPath); // 覆盖旧版本包
        File.Move(partPath, finalPath);
        progress?.Report("下载完成，开始校验并解压…");
        return finalPath;
    }
}
