using System.IO;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public enum BgiUpdateSourceKind { Local, Coordinator, GitHubReleases, GitHubActions, Custom, Cnb, CnbMirror }

public sealed record BgiUpdateSource(string Id, string Label, BgiUpdateSourceKind Kind, Uri? Endpoint = null)
{
    public string DownloadDirectory(string root, long? actionsRunId = null)
    {
        // 地址也参与分隔：更换协调服务器后，即使安装包同名，也不会覆盖原服务器的副本。
        var identity = BgiUpdateSources.Hash(Endpoint!.AbsoluteUri);
        var directory = Path.Combine(root, "网络安装包", $"{Kind}-{identity[..16]}");
        return actionsRunId.HasValue ? Path.Combine(directory, actionsRunId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : directory;
    }
}

public static class BgiUpdateSources
{
    public const string LocalId = "local";
    public const string CoordinatorId = "coordinator";
    public const string CnbId = "cnb";
    public const string GitHubRepository = "kaedelcb/better-genshin-impact";
    private const string GitHubApi = "https://api.github.com/repos/" + GitHubRepository;
    internal const string ActionsArtifacts = GitHubApi + "/actions/artifacts?name=BetterGI_7z&per_page=30";
    internal const string ActionsRuns = GitHubApi + "/actions/workflows/161175465/runs?status=success&per_page=10";
    private static readonly Uri GitHubEndpoint = new(GitHubApi + "/releases?per_page=15");
    public const string CnbMirrorId = "cnb-mirror";
    internal const string CnbMirrorRepository = "https://cnb.cool/asdbr/BetterGI_LCB";
    private static readonly Uri CnbMirrorEndpoint = new(CnbMirrorRepository + "/-/releases/latest");
    private static readonly Uri CnbEndpoint =new("https://cnb.cool/wjdsg/wjdsg-bgi/-/releases/download/wjdsg-bgi/runForVersion.txt");

    public static List<BgiUpdateSource> List(AssistConfig? config, IEnumerable<BgiCustomUpdateSource>? custom = null)
    {
        List<BgiUpdateSource> sources = [new(LocalId, "本地安装包", BgiUpdateSourceKind.Local)];
        if (TryHttpUri(config?.ServerUrl, httpsOnly: false, out var coordinator) && string.IsNullOrEmpty(coordinator!.Query))
        {
            var manifest = new Uri(BgiRemoteUpdateService.BuildManifestUrl(coordinator.AbsoluteUri));
            sources.Add(new(CoordinatorId, "协调服务器", BgiUpdateSourceKind.Coordinator, manifest));
        }
        sources.Add(new("github-releases", "GitHub Releases", BgiUpdateSourceKind.GitHubReleases, GitHubEndpoint));
        sources.Add(new("github-actions", "GitHub Actions 构建", BgiUpdateSourceKind.GitHubActions, new Uri(ActionsRuns)));
        sources.Add(new(CnbId, "autoBGI源", BgiUpdateSourceKind.Cnb, CnbEndpoint));
        sources.Add(new(CnbMirrorId, "CNB 茶包镜像（H工源）", BgiUpdateSourceKind.CnbMirror, CnbMirrorEndpoint));

        foreach (var configured in custom ?? config?.BgiCustomUpdateSources ?? [])
        {
            if (configured is not null && TryCustom(configured.Label, configured.ManifestUrl, out var source)
                && sources.All(existing => existing.Id != source!.Id))
                sources.Add(source!);
        }
        return sources;
    }

    public static bool TryCustom(string? label, string? manifestUrl, out BgiUpdateSource? source)
    {
        source = null;
        if (!TryHttpUri(manifestUrl, httpsOnly: true, out var uri)) return false;
        var title = string.IsNullOrWhiteSpace(label) ? uri!.IdnHost : label.Trim();
        if (title.Length > 80 || title.Any(char.IsControl)) return false;
        source = new("custom-" + Hash(uri!.AbsoluteUri), "自定义 · " + title, BgiUpdateSourceKind.Custom, uri);
        return true;
    }

    public static BgiUpdateSource SelectStartupSource(AssistConfig config)
    {
        var sources = List(config);
        return sources.Find(s => s.Endpoint != null && s.Id == config.BgiUpdateSourceId)
            ?? (config.StandaloneMode ? null : sources.Find(s => s.Kind == BgiUpdateSourceKind.Coordinator))
            ?? sources.First(s => s.Kind == BgiUpdateSourceKind.GitHubReleases);
    }

    internal static bool TryHttpUri(string? value, bool httpsOnly, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var result) || string.IsNullOrEmpty(result.Host)
            || result.UserInfo.Length > 0 || result.Fragment.Length > 0
            || !(result.Scheme == Uri.UriSchemeHttps || (!httpsOnly && result.Scheme == Uri.UriSchemeHttp))) return false;
        uri = result;
        return true;
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
