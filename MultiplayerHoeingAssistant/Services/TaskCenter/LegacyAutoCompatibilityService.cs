using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using OneDragonMigration.Core;

namespace MultiplayerHoeingAssistant.Services;

public sealed record LegacyCompatibilityResult(int Added, int Refreshed, IReadOnlyList<string> Notices);

/// <summary>Normal-entry legacy continuation. Source User stays read-only; edited plans are never replaced.</summary>
internal static class LegacyAutoCompatibilityService
{
    internal static LegacyCompatibilityResult Synchronize(string userRoot, string indexRoot, WorkflowStore store,
        Func<string, bool> mayRefresh)
    {
        var source = Path.GetFullPath(userRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configs = Path.Combine(source, "OneDragon");
        if (!Directory.Exists(configs)) return new(0, 0, []);
        if (MigrationSwitchTransaction.HasReparsePoint(source) || MigrationSwitchTransaction.HasReparsePoint(indexRoot))
            throw new IOException("旧配置或助手目录包含链接，原件保留");
        var files = Directory.GetFiles(configs, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return new(0, 0, []);
        var safe = files.Where(p => !MigrationSwitchTransaction.HasReparsePoint(p)).ToArray();
        var schedule = Path.Combine(source, "config.json");
        if (!File.Exists(schedule) || MigrationSwitchTransaction.HasReparsePoint(schedule)) schedule = null;
        var projected = OneDragonMigrationEngine.ProjectCompatiblePlans(safe, schedule);
        var notices = projected.Issues.Where(i => i.Severity is "error" or "warn")
            .Select(i => i.Config + "：" + i.Message).ToList();
        notices.AddRange(files.Except(safe).Select(p => Path.GetFileName(p) + "：配置是链接，原件保留"));
        var sourceKey = Digest(source.ToUpperInvariant())[..16];
        var indexFile = Path.Combine(indexRoot, sourceKey + ".json");
        Directory.CreateDirectory(indexRoot);
        using var lease = new FileStream(indexFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var index = File.Exists(indexFile) ? JsonNode.Parse(File.ReadAllBytes(indexFile)) as JsonObject : null;
        if (File.Exists(indexFile) && (index == null || index["source"]?.GetValue<string>() != source))
            throw new InvalidDataException("旧计划接续记录不匹配，保留既有计划和原文件");
        index ??= new JsonObject { ["version"] = 1, ["source"] = source, ["plans"] = new JsonObject() };
        var records = index["plans"] as JsonObject ?? throw new InvalidDataException("旧计划接续记录损坏，保留原件");
        var added = 0; var refreshed = 0;
        var entries = store.List().ToDictionary(e => e.WorkflowId, StringComparer.Ordinal);
        foreach (var plan in projected.Plans)
        {
            var name = plan["name"]!.GetValue<string>();
            if (plan["activation"]?["status"]?.GetValue<string>() != "active")
            {
                notices.Add(name + "：该计划存在无法安全确定的配置，其他合法计划继续可用");
                continue;
            }
            var id = "wf-compat-" + Digest(sourceKey + "\n" + name)[..16];
            var inputRevision = Digest(plan.ToJsonString());
            var previous = records[id] as JsonObject;
            var exists = entries.TryGetValue(id, out var current);
            if (previous != null && !exists) continue; // An explicitly removed plan must not reappear on every opening.
            if (exists)
            {
                if (previous?["inputRevision"]?.GetValue<string>() == inputRevision) continue;
                if (previous?["savedRevision"]?.GetValue<string>() != current!.Revision || !mayRefresh(id))
                {
                    notices.Add(name + "：已有计划的修改和运行状态已保留；原配置变化后可从计划管理重新关联资源");
                    continue;
                }
            }
            // Bind plans to the exact source bytes that ext.config.describe will report.
            foreach (var node in plan["nodes"]!.AsArray().OfType<JsonObject>())
            {
                var config = projected.Configs.Single(c => c.Source.ConfigKey == node["ref"]!["configKey"]!.GetValue<string>());
                if (!File.Exists(config.Source.SourceFile) ||
                    OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(config.Source.SourceFile)) != config.Source.Sha256)
                    throw new IOException("兼容读取期间旧配置已变化；计划未据此写入，原件未修改");
            }
            var document = JsonSerializer.Deserialize<WorkflowDocument>(plan.ToJsonString())!;
            document.WorkflowId = id;
            document.ExtensionData ??= new();
            document.ExtensionData["legacyCompatibilitySource"] = JsonSerializer.SerializeToElement(new { userRoot = source, planName = name });
            var revision = store.Save(document, exists ? current!.Revision : null);
            records[id] = new JsonObject { ["inputRevision"] = inputRevision, ["savedRevision"] = revision };
            if (exists) refreshed++; else added++;
        }
        var temporary = indexFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(index.ToJsonString());
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, indexFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(added, refreshed, notices.Distinct().ToArray());
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
