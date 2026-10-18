using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

public sealed record ResourceImportPreparation(
    IReadOnlyDictionary<string, byte[]> TargetFiles,
    IReadOnlyDictionary<string, byte[]> AssistantFiles,
    IReadOnlyList<string> Warnings);

/// <summary>Builds a transfer plan only. The caller owns backup, atomic publication and rollback.</summary>
public static partial class ConfigTransferResources
{
    public const string Prefix = "bgi-user/";
    // JS can enumerate pathing/strategy files dynamically. Preserve these business roots together,
    // rather than pretending a static search through third-party JS proves its dependency closure.
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
    { "ScriptGroup", "OneDragon", "JsScript", "AutoPathing", "KeyMouseScript", "AutoFight", "AutoGeniusInvokation" };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git" };

    public static IReadOnlyDictionary<string, byte[]> PlanExport(string bgiUserDir)
    {
        var root = CheckedRoot(bgiUserDir);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        const long maximumResourceBytes = 512L * 1024 * 1024;
        long resourceBytes = 0;
        foreach (var directory in Roots.OrderBy(x => x, StringComparer.Ordinal))
        {
            var source = Path.Combine(root, directory);
            if (!Directory.Exists(source)) continue;
            Collect(source);
        }
        ValidateGroupDependencies(files);
        return files;

        void Collect(string directory)
        {
            RejectLink(directory);
            foreach (var file in Directory.EnumerateFiles(directory).OrderBy(x => x, StringComparer.Ordinal))
            {
                RejectLink(file);
                // Only the host's exact sidecar name is disposable. Scripts may use .log,
                // .lock and .tmp as ordinary inputs; preserve their original bytes as well.
                if (IsHostWriteLock(file)) continue;
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                ValidateRelative(relative);
                if (!files.TryAdd(Prefix + relative, ReadSnapshot(file, relative)))
                    throw new InvalidDataException("资源路径大小写冲突：" + relative);
            }
            foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(x => x, StringComparer.Ordinal))
            {
                RejectLink(sub);
                if (!IgnoredDirectories.Contains(Path.GetFileName(sub))) Collect(sub);
            }
        }

        byte[] ReadSnapshot(string file, string relative)
        {
            try
            {
                // Scripts may keep logs open for writing. Capture only the length observed
                // on this handle; appends cannot turn this into an unbounded stream read.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                if (length > maximumResourceBytes - resourceBytes)
                    throw new InvalidDataException("业务资源超过设置包备份上限（512 MiB），未生成不完整包：" + relative);
                var snapshot = new byte[checked((int)length)];
                stream.ReadExactly(snapshot);
                resourceBytes += length;
                return snapshot;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not InvalidDataException)
            {
                throw new InvalidDataException("业务资源快照未完整读取，文件可能已截断或不可读，未生成不完整包：" + relative, ex);
            }
        }
    }

    /// <summary>Assistant entries have already passed the caller's whitelist and workflow-ID remapping.</summary>
    public static ResourceImportPreparation PlanImport(IReadOnlyDictionary<string, byte[]> entries, string localBgiUserDir)
    {
        var root = CheckedRoot(localBgiUserDir);
        var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assistant = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                ValidateRelative(item.Key[Prefix.Length..]);
                if (!resources.TryAdd(item.Key, item.Value)) throw new InvalidDataException("重复资源路径");
            }
            else assistant.Add(item.Key, item.Value);
        }
        ValidateGroupDependencies(resources);
        var warnings = new List<string>();
        if (resources.Count > 0)
            warnings.Add("已迁移 BGI 业务配置和脚本资源；BGI 全局设置、角色队伍命名及外部程序/命令仍使用本机配置。不同版本脚本或外部文件须先处理冲突。");
        var groupMap = Allocate("ScriptGroup", false);
        var dragonMap = Allocate("OneDragon", groupMap.Count > 0);

        // Rewrite group references in all OneDragon schema generations before computing revisions.
        foreach (var key in resources.Keys.Where(k => IsConfiguration(k, "OneDragon")).ToArray())
        {
            var doc = Parse(resources[key]);
            var changed = RewriteDragon(doc, groupMap);
            var name = Path.GetFileNameWithoutExtension(key);
            if (dragonMap.TryGetValue(name, out var renamed)) { Set(doc, "name", renamed); changed = true; }
            if (changed) resources[key] = Bytes(doc);
        }
        foreach (var key in resources.Keys.Where(k => IsConfiguration(k, "ScriptGroup")).ToArray())
        {
            var name = Path.GetFileNameWithoutExtension(key);
            if (!groupMap.TryGetValue(name, out var renamed)) continue;
            var doc = Parse(resources[key]); Set(doc, "name", renamed); resources[key] = Bytes(doc);
        }

        foreach (var key in assistant.Keys.Where(k => k.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var doc = Parse(assistant[key]);
            var changed = RewriteAssistant(doc, groupMap, dragonMap, resources);
            if (changed) assistant[key] = Bytes(doc);
        }
        var targets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in resources)
        {
            var relative = item.Key[Prefix.Length..];
            foreach (var (directory, map) in new[] { ("ScriptGroup", groupMap), ("OneDragon", dragonMap) })
                if (IsConfiguration(item.Key, directory) && map.TryGetValue(Path.GetFileNameWithoutExtension(relative), out var name))
                    relative = directory + "/" + name + ".json";
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            CheckParents(root, target);
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(item.Value)) continue;
                throw new InvalidDataException("导入资源与本机不同，未覆盖任何文件：" + relative);
            }
            if (!targets.TryAdd(target, item.Value)) throw new InvalidDataException("导入资源目标冲突：" + relative);
        }
        return new(targets, assistant, warnings);

        Dictionary<string, string> Allocate(string directory, bool dependentChange)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in resources.Where(x => IsConfiguration(x.Key, directory)).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var originalName = Path.GetFileNameWithoutExtension(item.Key);
                var target = Path.Combine(root, directory, originalName + ".json");
                CheckParents(root, target);
                // OneDragon may change when its group references change. Reserve a distinct copy
                // whenever groups were renamed, even if its incoming bytes initially match local.
                if (!File.Exists(target) || !dependentChange && File.ReadAllBytes(target).AsSpan().SequenceEqual(item.Value)) continue;
                var suffix = "-导入-" + Hash(item.Value)[..8];
                var candidate = originalName + suffix;
                var n = 1;
                while (File.Exists(Path.Combine(root, directory, candidate + ".json"))
                    || resources.ContainsKey(Prefix + directory + "/" + candidate + ".json") || map.Values.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    candidate = originalName + suffix + "-" + n++;
                map.Add(originalName, candidate);
                warnings.Add($"{directory}/{originalName} 已保留本机版本，导入为 {candidate}");
            }
            return map;
        }
    }

    public static bool IsResourceEntry(string entry)
    {
        if (!entry.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try { ValidateRelative(entry[Prefix.Length..]); return true; }
        catch (InvalidDataException) { return false; }
    }

    private static bool IsConfiguration(string key, string directory)
        => key.StartsWith(Prefix + directory + "/", StringComparison.OrdinalIgnoreCase)
            && key[(Prefix.Length + directory.Length + 1)..].IndexOf('/') < 0
            && key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    private static string CheckedRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("BGI User 路径必须是本机绝对路径");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // The configured root itself is the trust boundary. CheckParents still walks
        // every existing ancestor to reject reparse points, including ancestors outside it.
        CheckParents(full, full);
        return full;
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("设置迁移不跟随链接：" + path);
    }
    private static void CheckParents(string root, string target)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源目标越界");
        for (var path = target; path != null; path = Path.GetDirectoryName(path))
            if (File.Exists(path) || Directory.Exists(path)) RejectLink(path);
    }
    private static bool IsHostWriteLock(string file)
        => Path.GetFileName(file).Equals(".nexus-config-write.lock", StringComparison.OrdinalIgnoreCase);
    private static void ValidateRelative(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2 || !Roots.Contains(parts[0]) || relative.Contains('\\')
            || parts.Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || p.EndsWith('.') || p.EndsWith(' ') || IgnoredDirectories.Contains(p) || DeviceName(p)) || IsHostWriteLock(parts[^1]))
            throw new InvalidDataException("非法资源路径：" + relative);
    }
    private static bool DeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && name[3] is >= '0' and <= '9';
    }
    private static JsonNode Parse(byte[] bytes)
        => JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new InvalidDataException("JSON 配置为空");
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(new() { WriteIndented = true }));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static JsonNode? Get(JsonNode? node, string key)
        => node is JsonObject obj ? obj.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : null;
    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static void Set(JsonNode node, string key, string value)
    {
        if (node is not JsonObject obj) throw new InvalidDataException("配置应为 JSON 对象");
        obj[obj.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key ?? key] = value;
    }

    private static void ValidateGroupDependencies(IReadOnlyDictionary<string, byte[]> resources)
    {
        foreach (var file in resources.Where(x => IsConfiguration(x.Key, "ScriptGroup")))
        {
            var doc = Parse(file.Value);
            if (Get(doc, "projects") is not JsonArray projects) continue;
            foreach (var project in projects)
            {
                var name = Text(Get(project, "name")); var folder = Text(Get(project, "folderName"));
                var type = Text(Get(project, "type"));
                string? dependency = type.ToLowerInvariant() switch
                {
                    "javascript" => ReferencePath(ReferencePath("JsScript", folder), "manifest.json"),
                    "pathing" => ReferencePath(string.IsNullOrEmpty(folder) ? "AutoPathing" : ReferencePath("AutoPathing", folder), name),
                    "keymouse" => ReferencePath("KeyMouseScript", name),
                    _ => null
                };
                if (dependency == null) continue;
                ValidateRelative(dependency);
                if (!resources.ContainsKey(Prefix + dependency)) throw new InvalidDataException("配置组缺少依赖：" + dependency);
                if (type.Equals("Javascript", StringComparison.OrdinalIgnoreCase))
                {
                    var manifest = Parse(resources[Prefix + dependency]);
                    var main = Text(Get(manifest, "main"));
                    var scriptDirectory = ReferencePath("JsScript", folder);
                    var mainPath = ReferencePath(scriptDirectory, main);
                    ValidateRelative(mainPath);
                    if (!resources.ContainsKey(Prefix + mainPath)) throw new InvalidDataException("JS 缺少入口：" + mainPath);
                    foreach (var field in new[] { "settings_ui" })
                    {
                        var declared = Get(manifest, field);
                        var names = declared is JsonArray list ? list.Select(Text) : new[] { Text(declared) };
                        foreach (var declaredName in names.Where(x => !string.IsNullOrEmpty(x)))
                        {
                            var path = ReferencePath(scriptDirectory, declaredName);
                            ValidateRelative(path);
                            if (!resources.ContainsKey(Prefix + path) && !resources.Keys.Any(k => k.StartsWith(Prefix + path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidDataException("JS 缺少声明资源：" + path);
                        }
                    }
                    if (Get(manifest, "library") is JsonArray libraries)
                        foreach (var library in libraries.Select(Text))
                        {
                            var path = ReferencePath(scriptDirectory, library);
                            ValidateRelative(path);
                            if (!resources.ContainsKey(Prefix + path) && !resources.Keys.Any(k => k.StartsWith(Prefix + path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidDataException("JS 缺少声明库目录：" + path);
                        }
                }
            }
        }
    }

    private static string ReferencePath(string directory, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) || reference.Contains(':'))
            throw new InvalidDataException("脚本声明了不可迁移的绝对或空路径");
        var parts = new List<string>();
        foreach (var part in (directory + "/" + reference.Replace('\\', '/')).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count <= 1) throw new InvalidDataException("脚本依赖越过资源根目录");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static bool RewriteDragon(JsonNode doc, Dictionary<string, string> groups)
    {
        var changed = false;
        foreach (var pair in groups)
        {
            if (Get(doc, "taskDefinitions") is JsonObject definitions)
                foreach (var field in definitions.ToArray()) if (Text(field.Value) == pair.Key) { definitions[field.Key] = pair.Value; changed = true; }
            if (Get(doc, "taskEnabledList") is JsonObject tasks)
                foreach (var field in tasks.ToArray())
                {
                    if (field.Value is JsonObject tuple && Text(Get(tuple, "Item2")) == pair.Key) { Set(tuple, "Item2", pair.Value); changed = true; }
                    else if ((Get(doc, "taskDefinitions") is not JsonObject nativeDefinitions || nativeDefinitions.Count == 0)
                        && field.Key == pair.Key && field.Value is JsonValue value && value.TryGetValue<bool>(out _))
                    { tasks.Remove(field.Key); tasks.Add(pair.Value, field.Value); changed = true; }
                }
            if (Get(doc, "customDomainList") is JsonArray domains)
                for (var i = 0; i < domains.Count; i++) if (Text(domains[i]) == pair.Key) { domains[i] = pair.Value; changed = true; }
            if (doc is JsonObject obj)
                foreach (var field in obj.ToArray())
                    if (field.Key.EndsWith("DomainName", StringComparison.OrdinalIgnoreCase) && Text(field.Value) == pair.Key)
                    { obj[field.Key] = pair.Value; changed = true; }
            // Native order/nextTaskId contain stable task IDs, even when an ID happens to
            // equal a group name. Only legacy name-key configurations carry group names here.
            if (Get(doc, "taskDefinitions") is not JsonObject definitionsForOrder || definitionsForOrder.Count == 0)
            {
                if (Get(doc, "taskOrder") is JsonArray order)
                    for (var i = 0; i < order.Count; i++) if (Text(order[i]) == pair.Key) { order[i] = pair.Value; changed = true; }
                if (Text(Get(doc, "nextTaskId")) == pair.Key) { Set(doc, "nextTaskId", pair.Value); changed = true; }
            }
        }
        return changed;
    }

}
