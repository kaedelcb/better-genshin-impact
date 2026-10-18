using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>助手私有配置备份：计划合并、引用修复和整批回滚，运行账不随包迁移。</summary>
public class ConfigTransferService
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 30000;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static readonly string[] SettingFileNames =
    [
        "assistant-config.json", "startup-flow.json", "startup-flow-schemes.json",
        "dodoco_settings.json", "dodoco_watch_rules.json", "pet_settings.json", "member-config-cache.json"
    ];
    private readonly string _configDir;
    private readonly string? _bgiUserDir;
    internal Action<string, int>? BeforeWrite { get; set; }
    internal static bool RecoveredInterruptedImport { get; private set; }
    public ConfigTransferService() : this(AssistantDataDirectory.Root) { }
    internal ConfigTransferService(string configDir, string? bgiUserDir = null)
    {
        _configDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDir));
        _bgiUserDir = bgiUserDir is null ? null : Path.GetFullPath(bgiUserDir);
    }
    public record ExportResult(bool Success, string Message, string ZipPath);
    public record ImportResult(bool Success, string Message, List<string> Imported, List<string> Missing, string BackupDir)
    {
        public bool RecoveryRequired { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    public ExportResult Export(string zipPath, IReadOnlyDictionary<string, byte[]>? overrides = null)
    {
        string? temporary = null;
        try
        {
            using var transfer = ConfigTransferTransaction.Acquire(_configDir);
            ConfigTransferTransaction.Recover(_configDir);
            var files = ReadFlows();
            foreach (var name in SettingFileNames.Append(ConfigTransferMistletoe.CurrentWorkflowFileName))
            {
                var path = Path.Combine(_configDir, name);
                if (File.Exists(path)) files.Add(name, ReadChecked(path));
            }
            if (overrides is not null)
                foreach (var item in overrides)
                {
                    if (!SettingFileNames.Contains(item.Key)) throw new InvalidDataException("不支持的设置快照：" + item.Key);
                    files[item.Key] = item.Value;
                }
            if (files.Count == 0) return new(false, "没有可导出的设置或槲寄生计划。", zipPath);
            files = ConfigTransferMistletoe.NormalizeForExport(files);
            ValidateSettings(files);
            var history = Path.Combine(_configDir, "remote-log-rooms.json");
            if (File.Exists(history)) files.Add(ConfigTransferRoomHistory.EntryName, ConfigTransferRoomHistory.Export(ReadChecked(history)));
            var bgiRoot = ResolveBgiUserRoot(files);
            if (bgiRoot is not null)
                foreach (var entry in ConfigTransferResources.PlanExport(bgiRoot)) files.Add(entry.Key, entry.Value);
            if (files.Count > MaximumEntries || files.Values.Sum(x => x.LongLength) > MaximumPackageBytes)
                throw new InvalidDataException("设置及资源超过备份上限（512 MiB / 30000 文件）；未生成不完整备份。");
            var destination = Path.GetFullPath(zipPath);
            if (IsInside(destination, _configDir) || (bgiRoot is not null && IsInside(destination, bgiRoot)))
                throw new InvalidDataException("备份包请保存到助手数据目录和 BGI User 目录之外。");
            ConfigTransferTransaction.CheckPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        app = "NexusBGI-Assistant", formatVersion = 2, exportedAt = DateTimeOffset.Now,
                        version = GetType().Assembly.GetName().Version?.ToString(),
                        files = files.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
                        { path = x.Key, length = x.Value.LongLength, sha256 = Hash(x.Value) }).ToArray()
                    }, Options));
                    foreach (var entry in files.OrderBy(x => x.Key, StringComparer.Ordinal)) WriteEntry(zip, entry.Key, entry.Value);
                }
                stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            var message = $"已导出助手设置、{files.Keys.Count(ConfigTransferMistletoe.IsFlowEntry)} 个槲寄生计划及可用的 BGI 配置资源到：\n{destination}"
                + "\n\n包含私有账号和房间设置，请妥善保存。协助房间仅迁移书签，密码需重新授权。"
                + "\n运行记录、锁、运行授权和软件本体不在设置包内。";
            if (bgiRoot is null) message += "\n未定位到本机 BGI User，包内没有 BGI 资源；导入后需核对资源依赖。";
            return new(true, message, zipPath);
        }
        catch (Exception ex) { return new(false, "导出失败：" + ex.Message, zipPath); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>回调在文件提交后运行；回调失败也回滚磁盘，调用方再重载原配置。</summary>
    public ImportResult Import(string zipPath, Action? applyRuntime = null,
        IReadOnlyDictionary<string, string>? additionalTargets = null, Action? restoreRuntime = null)
    {
        ConfigTransferTransaction? transaction = null;
        try
        {
            using var transfer = ConfigTransferTransaction.Acquire(_configDir);
            ConfigTransferTransaction.Recover(_configDir);
            var expectedVersions = SnapshotTargetVersions(additionalTargets);
            var (incoming, legacy) = ReadPackage(zipPath);
            ValidateSettings(incoming);
            var existing = ReadFlows();
            var startup = Path.Combine(_configDir, "startup-flow.json");
            var files = ConfigTransferMistletoe.NormalizeForExport(incoming);
            if (files.TryGetValue("assistant-config.json", out var config)) files["assistant-config.json"] = PreserveMachineSpecificFields(config);
            var warnings = new List<string>();
            if (legacy) warnings.Add("旧版设置包不含任务中心计划；本机已有计划保持，缺失引用不会自动执行。");
            var targets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var bgiRoot = ResolveBgiUserRoot(files, localOnly: true);
            if (incoming.Keys.Any(k => k.StartsWith(ConfigTransferResources.Prefix, StringComparison.Ordinal)))
            {
                if (bgiRoot is null) throw new InvalidDataException("请先在助手设置中选定本机 BetterGI.exe，再导入 BGI 配置资源；尚未修改任何设置。");
                var resources = ConfigTransferResources.PlanImport(files, bgiRoot);
                files = new(resources.AssistantFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var target in resources.TargetFiles) targets.Add(target.Key, target.Value);
                warnings.AddRange(resources.Warnings);
            }
            // 资源改名先于计划身份复用：同ID计划只有引用恢复后的定义仍相同才允许复用本机文件。
            var newResources = targets.Keys.ToList();
            var prepared = ConfigTransferMistletoe.PrepareImport(files, existing,
                File.Exists(startup) ? File.ReadAllText(startup) : null,
                File.Exists(Path.Combine(_configDir, "startup-flow-schemes.json"))
                    ? File.ReadAllText(Path.Combine(_configDir, "startup-flow-schemes.json")) : null);
            files = prepared.Files;
            warnings.AddRange(prepared.Warnings);
            if (files.Remove(ConfigTransferRoomHistory.EntryName, out var bookmarks))
            {
                var path = Path.Combine(_configDir, "remote-log-rooms.json");
                targets.Add(path, ConfigTransferRoomHistory.PrepareImport(bookmarks, File.Exists(path) ? ReadChecked(path) : null));
                warnings.Add("协助房间书签已合并，本机密码保留；新迁入房间需重新输入密码。");
            }
            foreach (var file in files)
            {
                if (!IsAssistantEntry(file.Key)) throw new InvalidDataException("不支持的助手设置：" + file.Key);
                targets.Add(Path.Combine(_configDir, file.Key.Replace('/', Path.DirectorySeparatorChar)), file.Value);
            }
            if (additionalTargets is not null)
                foreach (var extra in additionalTargets)
                {
                    if (extra.Key != "dodoco_watch_rules.json") throw new InvalidDataException("不支持的共享设置目标。");
                    if (files.TryGetValue(extra.Key, out var bytes)) targets.Add(Path.GetFullPath(extra.Value), bytes);
                }
            ValidatePlanReferences(files, existing, legacy, warnings);
            if (targets.Count == 0) return new(false, "设置包没有需要导入的设置。", [], [], "");
            newResources.AddRange(files.Keys.Where(ConfigTransferMistletoe.IsFlowEntry)
                .Select(k => Path.Combine(_configDir, k.Replace('/', Path.DirectorySeparatorChar))));
            transaction = new ConfigTransferTransaction(_configDir, targets, bgiRoot, additionalTargets?.Values, expectedVersions, newResources);
            transaction.Commit(applyRuntime, BeforeWrite, restoreRuntime);
            var missing = SettingFileNames.Where(n => !incoming.ContainsKey(n)).ToList();
            var message = $"已导入助手设置和槲寄生计划；本机已有计划及资源已保留。\n\n原设置备份：\n{transaction.BackupDirectory}"
                + (missing.Count > 0 ? "\n\n包内未包含的设置保持本机现状：\n" + string.Join("\n", missing) : "")
                + (warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings.Distinct()) : "");
            return new(true, message, files.Keys.ToList(), missing, transaction.BackupDirectory) { Warnings = warnings };
        }
        catch (Exception ex)
        {
            var recovery = transaction?.RecoveryRequired == true || File.Exists(Path.Combine(_configDir, ".config-transfer-pending.json"));
            var backup = transaction?.BackupDirectory ?? "";
            return new(false, "导入失败：" + ex.Message
                + (recovery ? "\n回滚尚未完成，已保留恢复记录；禁止继续覆盖设置。" : "\n未提交的修改已回滚，原设置保留。")
                + (backup.Length > 0 ? "\n备份和恢复记录：\n" + backup : ""), [], [], backup) { RecoveryRequired = recovery };
        }
    }

    public static void RecoverInterruptedImport(string configDir)
    {
        using var transfer = ConfigTransferTransaction.Acquire(configDir);
        if (ConfigTransferTransaction.Recover(configDir)) RecoveredInterruptedImport = true;
    }
    private Dictionary<string, string?> SnapshotTargetVersions(IReadOnlyDictionary<string, string>? extras)
    {
        var paths = SettingFileNames.Append(ConfigTransferMistletoe.CurrentWorkflowFileName).Append("remote-log-rooms.json")
            .Select(n => Path.Combine(_configDir, n)).ToList();
        if (extras is not null) paths.AddRange(extras.Values.Select(Path.GetFullPath));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
            p => File.Exists(p) ? Hash(ReadChecked(p)) : null, StringComparer.OrdinalIgnoreCase);
    }
    private Dictionary<string, byte[]> ReadFlows()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(_configDir, "flows");
        ConfigTransferTransaction.CheckPath(root);
        if (Directory.Exists(root))
            foreach (var path in Directory.EnumerateFiles(root, "*.flow.json")) files.Add("flows/" + Path.GetFileName(path), ReadChecked(path));
        return files;
    }
    private (Dictionary<string, byte[]> Files, bool Legacy) ReadPackage(string path)
    {
        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException("设置包文件数量超出限制。");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            total = checked(total + entry.Length);
            if (total > MaximumPackageBytes) throw new InvalidDataException("设置包解压内容超过 512 MiB。");
            if (entry.FullName != "manifest.json" && !IsPackageEntry(entry.FullName)) throw new InvalidDataException("设置包含未知或非法路径：" + entry.FullName);
            using var content = entry.Open();
            using var memory = new MemoryStream();
            content.CopyTo(memory);
            if (memory.Length != entry.Length || !files.TryAdd(entry.FullName, memory.ToArray())) throw new InvalidDataException("设置包包含重复或损坏条目：" + entry.FullName);
        }
        if (!files.Remove("manifest.json", out var manifest)) throw new InvalidDataException("缺少 manifest.json。");
        using var doc = JsonDocument.Parse(manifest);
        if (doc.RootElement.GetProperty("app").GetString() != "NexusBGI-Assistant") throw new InvalidDataException("不是本助手的设置包。");
        var version = doc.RootElement.GetProperty("formatVersion").GetInt32();
        if (version is not (1 or 2)) throw new InvalidDataException("设置包格式版本不兼容，请升级助手。");
        if (version == 2)
        {
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = item.GetProperty("path").GetString()!;
                if (!listed.Add(name) || !files.TryGetValue(name, out var bytes)
                    || item.GetProperty("length").GetInt64() != bytes.LongLength || item.GetProperty("sha256").GetString() != Hash(bytes))
                    throw new InvalidDataException("设置包文件清单或内容校验失败：" + name);
            }
            if (listed.Count != files.Count) throw new InvalidDataException("设置包包含未登记文件。");
        }
        else if (files.Keys.Any(n => !SettingFileNames.Contains(n))) throw new InvalidDataException("旧格式设置包含未知文件。");
        if (files.Count == 0) throw new InvalidDataException("设置包为空。");
        return (files, version == 1);
    }
    private static bool IsAssistantEntry(string name) => SettingFileNames.Contains(name)
        || ConfigTransferMistletoe.IsFlowEntry(name) || name == ConfigTransferMistletoe.CurrentWorkflowFileName;
    private static bool IsPackageEntry(string name) => IsAssistantEntry(name) || name == ConfigTransferRoomHistory.EntryName || ConfigTransferResources.IsResourceEntry(name);
    private static void ValidateSettings(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var item in files.Where(x => SettingFileNames.Contains(x.Key)))
        {
            using var json = JsonDocument.Parse(item.Value);
            var expected = item.Key == "startup-flow-schemes.json" ? JsonValueKind.Array : JsonValueKind.Object;
            if (json.RootElement.ValueKind != expected) throw new InvalidDataException("设置结构不正确：" + item.Key);
            object? typed = item.Key switch
            {
                "assistant-config.json" => JsonSerializer.Deserialize<AssistConfig>(item.Value, Options),
                "startup-flow.json" => JsonSerializer.Deserialize<StartupFlowConfig>(item.Value, Options),
                "startup-flow-schemes.json" => JsonSerializer.Deserialize<List<StartupFlowScheme>>(item.Value, Options),
                "dodoco_settings.json" => JsonSerializer.Deserialize<DodocoSettings>(item.Value, Options),
                "pet_settings.json" => JsonSerializer.Deserialize<PetSettings>(item.Value, Options),
                "dodoco_watch_rules.json" => JsonSerializer.Deserialize<WatchConfig>(item.Value, Options),
                "member-config-cache.json" => JsonSerializer.Deserialize<MemberConfigCache>(item.Value, Options),
                _ => json.RootElement
            };
            if (typed is null || typed is AssistConfig a && (a.TeamUids is null || a.QuickCommands is null
                    || a.OnlineHoeingGroupNames is null || a.OnlineHoeingGroupTypes is null)
                || typed is WatchConfig w && (w.Rules is null || w.Rules.Any(r => r is null))
                || typed is MemberConfigCache m && (m.ConfigGroups is null || m.OneClickConfigs is null
                    || m.ConfigGroupTasksWithStatus is null || m.OneClickTasksWithStatus is null || m.Hotkeys is null))
                throw new InvalidDataException("设置必要字段为空：" + item.Key);
        }
    }
    private byte[] PreserveMachineSpecificFields(byte[] incoming)
    {
        var imported = JsonSerializer.Deserialize<AssistConfig>(incoming, Options) ?? throw new InvalidDataException("助手主配置为空。");
        var path = Path.Combine(_configDir, "assistant-config.json");
        var local = File.Exists(path) ? JsonSerializer.Deserialize<AssistConfig>(ReadChecked(path), Options) : null;
        imported.ClientInstanceId = local?.ClientInstanceId ?? "";
        if (local is not null)
        {
            if (File.Exists(local.BgiPath) || !File.Exists(imported.BgiPath)) imported.BgiPath = local.BgiPath;
            if (Directory.Exists(local.BgiPackageDir) || !Directory.Exists(imported.BgiPackageDir)) imported.BgiPackageDir = local.BgiPackageDir;
        }
        var document = JsonNode.Parse(incoming) as JsonObject ?? throw new InvalidDataException("助手主配置应为对象。");
        Set("clientInstanceId", imported.ClientInstanceId);
        Set("bgiPath", imported.BgiPath);
        Set("bgiPackageDir", imported.BgiPackageDir);
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
        void Set(string key, string value)
        {
            var actual = document.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            document[actual] = value;
        }
    }
    private string? ResolveBgiUserRoot(IReadOnlyDictionary<string, byte[]> files, bool localOnly = false)
    {
        if (_bgiUserDir is not null) return _bgiUserDir;
        byte[]? bytes = null;
        if (!localOnly) files.TryGetValue("assistant-config.json", out bytes);
        var local = Path.Combine(_configDir, "assistant-config.json");
        if (bytes is null && File.Exists(local)) bytes = ReadChecked(local);
        var path = bytes is null ? null : JsonSerializer.Deserialize<AssistConfig>(bytes, Options)?.BgiPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "User");
    }
    private static void ValidatePlanReferences(IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<string, byte[]> existing, bool legacy, List<string> warnings)
    {
        var ids = files.Keys.Concat(existing.Keys).Where(ConfigTransferMistletoe.IsFlowEntry)
            .Select(x => Path.GetFileName(x)[..^".flow.json".Length]).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files.Where(x => x.Key is "startup-flow.json" or "startup-flow-schemes.json"))
        {
            using var doc = JsonDocument.Parse(file.Value);
            Visit(doc.RootElement);
        }
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "taskCenterFlowId" && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { Length: > 0 } id && !ids.Contains(id))
                    {
                        if (!legacy) throw new InvalidDataException("启动流程引用的任务中心计划缺失：" + id);
                        warnings.Add("旧启动流程引用的计划缺失，请补充后再执行：" + id);
                    }
                    Visit(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }
    private static byte[] ReadChecked(string path) { ConfigTransferTransaction.CheckPath(path); return File.ReadAllBytes(path); }
    private static bool IsInside(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }
}
