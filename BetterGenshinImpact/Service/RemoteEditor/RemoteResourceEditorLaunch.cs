using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

[assembly: AssemblyMetadata("MistletoeRemoteResourceEditor", "1")]

namespace BetterGenshinImpact.Service.RemoteEditor;

/// <summary>原版编辑页面的独立入口。配置和结果限定在本用户的一次编辑工作区。</summary>
internal sealed class RemoteResourceEditorLaunch
{
    internal const string Argument = "--remote-resource-editor";
    internal const string Schema = "mistletoe.remote-resource-editor";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal static RemoteResourceEditorLaunch? Current { get; } = ReadCurrent();
    internal static bool IsEnabled => Current is not null;

    internal sealed class Input
    {
        public string Schema { get; set; } = "";
        public int Version { get; set; }
        public string SessionId { get; set; } = "";
        public string OwnerSid { get; set; } = "";
        public string TargetUid { get; set; } = "";
        public string TargetInstanceId { get; set; } = "";
        public string TargetName { get; set; } = "";
        public string ResourceName { get; set; } = "";
        public bool OneDragon { get; set; }
        public string? TaskId { get; set; }
        public bool HiddenForTest { get; set; }
        public List<InputFile> Files { get; set; } = [];
    }

    internal sealed record InputFile(string Path, string ContentHash, string? ExpectedRevision, bool Writable);
    internal sealed record Change(string Path, string? ExpectedRevision, string? BaselineJson, string? Json);
    private Dictionary<string, string>? _editingBaseline;
    internal Input Metadata { get; }
    internal string DirectoryPath { get; }
    internal string UserRoot => Path.Combine(DirectoryPath, "User");
    internal string ResultPath => Path.Combine(DirectoryPath, "result.json");
    internal string ResourceRelativePath => (Metadata.OneDragon ? "OneDragon/" : "ScriptGroup/") + Metadata.ResourceName + ".json";

    private RemoteResourceEditorLaunch(string directory, Input metadata)
    { DirectoryPath = directory; Metadata = metadata; }

    private static RemoteResourceEditorLaunch? ReadCurrent()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Skip(1).Any(argument => argument.Equals(Argument, StringComparison.OrdinalIgnoreCase))) return null;
        return Parse(args, WindowsIdentity.GetCurrent().User?.Value ?? "");
    }

    internal static RemoteResourceEditorLaunch? Parse(string[] args, string ownerSid)
    {
        var positions = args.Select((value, index) => (value, index))
            .Where(item => item.value.Equals(Argument, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (positions.Length == 0) return null;
        if (positions.Length != 1 || positions[0].index != 1 || args.Length != 3
            || !Path.IsPathFullyQualified(args[2]))
            throw new InvalidDataException("远程资源编辑入口参数无效；未启动普通 BGI");
        var manifestPath = Path.GetFullPath(args[2]);
        if (Path.GetFileName(manifestPath) != "workspace.json")
            throw new InvalidDataException("远程编辑入口必须指定 workspace.json");
        RejectLinks(manifestPath);
        var bytes = File.ReadAllBytes(manifestPath);
        if (bytes.Length > 2_000_000) throw new InvalidDataException("远程编辑工作区清单过大");
        var input = JsonSerializer.Deserialize<Input>(bytes, JsonOptions)
            ?? throw new InvalidDataException("远程编辑工作区清单为空");
        var directory = Path.GetDirectoryName(manifestPath)!;
        if (input.Schema != Schema || input.Version != 1
            || !Guid.TryParseExact(input.SessionId, "N", out _)
            || Path.GetFileName(directory) != input.SessionId
            || new DirectoryInfo(directory).Parent?.Name != "RemoteResourceEditors"
            || string.IsNullOrWhiteSpace(ownerSid) || input.OwnerSid != ownerSid
            || string.IsNullOrWhiteSpace(input.TargetUid) || string.IsNullOrWhiteSpace(input.TargetInstanceId)
            || !ValidResourceName(input.ResourceName))
            throw new InvalidDataException("远程编辑工作区身份无效；未使用本机正常 User");
        var launch = new RemoteResourceEditorLaunch(directory, input);
        if (!Directory.Exists(launch.UserRoot) || input.Files is null || input.Files.Count is 0 or > 4096
            || input.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.Path) || file.ContentHash is not { Length: 64 })
            || input.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Files.Count)
            throw new InvalidDataException("远程编辑工作区文件清单不完整或重复");
        foreach (var file in input.Files)
        {
            var path = launch.ResolveFile(file.Path);
            if (file.Writable && !IsConfigurationPath(file.Path))
                throw new InvalidDataException("远程编辑不能写入脚本源码或其他资源：" + file.Path);
            if (!File.Exists(path)
                || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != file.ContentHash.ToUpperInvariant())
                throw new InvalidDataException("远程编辑输入不完整或已经改变：" + file.Path);
        }
        if (!input.Files.Any(file => file.Path.Equals(launch.ResourceRelativePath, StringComparison.OrdinalIgnoreCase) && file.Writable)
            || !input.Files.Any(file => file.Path.Equals("config.json", StringComparison.OrdinalIgnoreCase) && file.Writable))
            throw new InvalidDataException("远程编辑缺少目标配置或全局配置快照");
        if (File.Exists(launch.ResultPath) || File.Exists(Path.Combine(directory, "ready.json")))
            throw new InvalidDataException("远程编辑会话已打开过；原编辑副本保留，不能重复打开同一会话");
        return launch;
    }

    private static bool ValidResourceName(string name) => !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/') && !name.Contains('\\');

    internal static bool IsConfigurationPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return path.Equals("config.json", StringComparison.OrdinalIgnoreCase)
            || parts.Length == 2 && parts[0] is "OneDragon" or "ScriptGroup"
                && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && ValidResourceName(parts[1][..^5]);
    }

    internal string ResolveFile(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Any(char.IsControl)
            || relative.Split('/', '\\').Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw new InvalidDataException("远程编辑文件路径无效");
        var path = Path.GetFullPath(Path.Combine(UserRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(UserRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("远程编辑文件离开工作区");
        RejectLinks(path);
        return path;
    }

    internal void PublishReady() => WriteAtomic(Path.Combine(DirectoryPath, "ready.json"),
        JsonSerializer.Serialize(new { Metadata.SessionId, processId = Environment.ProcessId,
            startTicksUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            userRoot = UserRoot }, JsonOptions));

    internal void CaptureBaseline()
    {
        var baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Metadata.Files.Where(file => file.Writable))
            if (File.Exists(ResolveFile(file.Path))) baseline.Add(file.Path, ReadJson(ResolveFile(file.Path)));
        foreach (var folder in new[] { "OneDragon", "ScriptGroup" })
        {
            var directory = Path.Combine(UserRoot, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var relative = folder + "/" + Path.GetFileName(path);
                _ = ResolveFile(relative);
                baseline.TryAdd(relative, ReadJson(path));
            }
        }
        _editingBaseline = baseline;
        WriteAtomic(Path.Combine(DirectoryPath, "baseline.json"), JsonSerializer.Serialize(new
        { Metadata.SessionId, baseline }, JsonOptions));
    }

    internal IReadOnlyList<Change> CaptureChanges()
    {
        var baseline = _editingBaseline ?? throw new InvalidOperationException("原版编辑页面尚未确认加载完成");
        var changes = new List<Change>();
        foreach (var file in Metadata.Files.Where(file => !file.Writable))
        {
            var path = ResolveFile(file.Path);
            if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != file.ContentHash.ToUpperInvariant())
                throw new InvalidDataException("只读编辑资源已经改变；脚本源码不会回传：" + file.Path);
        }
        foreach (var (relative, before) in baseline)
        {
            var expected = Metadata.Files.FirstOrDefault(file => file.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))?.ExpectedRevision;
            var path = ResolveFile(relative);
            if (!File.Exists(path)) { changes.Add(new(relative, expected, before, null)); continue; }
            var after = ReadJson(path);
            if (!string.Equals(before, after, StringComparison.Ordinal)) changes.Add(new(relative, expected, before, after));
        }
        foreach (var folder in new[] { "OneDragon", "ScriptGroup" })
        {
            var directory = Path.Combine(UserRoot, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var relative = folder + "/" + Path.GetFileName(path);
                _ = ResolveFile(relative);
                if (!baseline.ContainsKey(relative)) changes.Add(new(relative, null, null, ReadJson(path)));
            }
        }
        return changes;
    }

    private static string ReadJson(string path)
    {
        if (new FileInfo(path).Length > 16_000_000) throw new InvalidDataException("编辑后的配置超过单文件限制");
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("编辑后的配置必须是 JSON 对象");
        return json;
    }

    internal void PublishResult(bool saved, IReadOnlyList<Change>? changes = null)
        => WriteAtomic(ResultPath, JsonSerializer.Serialize(new
        { Metadata.SessionId, Metadata.TargetUid, Metadata.TargetInstanceId, state = saved ? "saved" : "cancelled", changes = changes ?? [] }, JsonOptions));

    private static void WriteAtomic(string path, string json)
    {
        if (File.Exists(path)) throw new InvalidOperationException("远程编辑结果已发布，不覆盖原结果");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path);
    }

    private static void RejectLinks(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName))
                && File.GetAttributes(item.FullName).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("远程编辑工作区不能使用目录链接");
    }
}
