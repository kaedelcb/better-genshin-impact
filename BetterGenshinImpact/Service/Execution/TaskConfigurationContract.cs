using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>Revision-bound configuration access. Never edits third-party script sources.</summary>
internal sealed class TaskConfigurationContract(string userDirectory)
{
    internal static TaskConfigurationContract Default { get; } = new(Path.Combine(AppContext.BaseDirectory, "User"));
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    internal sealed record TaskEntry(string TaskId, string Name, bool Enabled, int? LegacyIndex, string Schema);
    internal sealed record Snapshot(string Revision, IReadOnlyList<TaskEntry> Tasks, JObject Document);

    private string Resolve(string name, bool oneDragon)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("invalid_configuration_name");
        var path = Path.GetFullPath(Path.Combine(userDirectory, oneDragon ? "OneDragon" : "ScriptGroup", name + ".json"));
        if (!File.Exists(path)) throw new FileNotFoundException("configuration_not_found", path);
        return path;
    }

    internal async Task<Snapshot> ReadAsync(string name, bool oneDragon, CancellationToken token = default)
    {
        var path = Resolve(name, oneDragon);
        var gate = Locks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try { return Parse(await File.ReadAllBytesAsync(path, token), oneDragon); }
        finally { gate.Release(); }
    }

    internal async Task<T> ExecuteLockedAsync<T>(string name, bool oneDragon, Func<Task<T>> action)
    {
        var gate = Locks.GetOrAdd(Resolve(name, oneDragon), _ => new(1, 1));
        await gate.WaitAsync();
        try { return await action(); }
        finally { gate.Release(); }
    }

    internal async Task<Snapshot> ApplyEnabledAsync(string name, bool oneDragon, string? taskId, int? legacyIndex,
        bool enabled, string? expectedRevision, string? takeoverTicket, CancellationToken token = default, Action? beforeCommit = null)
    {
        var path = Resolve(name, oneDragon);
        var gate = Locks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            if (!PreemptionGate.Authorize(takeoverTicket)) throw new InvalidOperationException("takeover_conflict");
            if (taskId == null && legacyIndex == null) throw new ArgumentException("task_identity_required");
            var bytes = await File.ReadAllBytesAsync(path, token);
            var snapshot = Parse(bytes, oneDragon);
            if (expectedRevision != null && !string.Equals(snapshot.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("configuration_changed");
            var task = taskId != null ? snapshot.Tasks.SingleOrDefault(t => t.TaskId == taskId)
                : snapshot.Tasks.SingleOrDefault(t => t.LegacyIndex == legacyIndex);
            if (task == null) throw new InvalidOperationException("task_not_found");
            if (oneDragon)
            {
                var list = (JObject)Property(snapshot.Document, "taskEnabledList")!;
                var key = task.Schema == "legacy" ? task.LegacyIndex!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : task.TaskId;
                if (list[key] is JObject tuple)
                {
                    var enabledProperty = tuple.Properties().FirstOrDefault(p => p.Name.Equals("Item1", StringComparison.OrdinalIgnoreCase));
                    tuple[enabledProperty?.Name ?? "Item1"] = enabled;
                }
                else list[key] = enabled;
            }
            else
            {
                var project = (JObject)((JArray)Property(snapshot.Document, "projects")!)[task.LegacyIndex!.Value - 1];
                var status = project.Properties().FirstOrDefault(p => p.Name.Equals("status", StringComparison.OrdinalIgnoreCase));
                project[status?.Name ?? "status"] = enabled ? "Enabled" : "Disabled";
            }
            var text = snapshot.Document.ToString(Formatting.Indented);
            var encoding = new UTF8Encoding(bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191);
            temporary = path + ".ipc-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, text, encoding, token);
            // Do not overwrite a concurrent native/UI/manual edit observed since the read.
            if (Revision(await File.ReadAllBytesAsync(path, token)) != snapshot.Revision)
                throw new InvalidOperationException("configuration_changed");
            if (!PreemptionGate.Authorize(takeoverTicket)) throw new InvalidOperationException("takeover_conflict");
            var applied = Parse(await File.ReadAllBytesAsync(temporary, token), oneDragon);
            beforeCommit?.Invoke();
            File.Move(temporary, path, true);
            temporary = null;
            return applied;
        }
        finally
        {
            if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    internal static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static JToken? Property(JObject doc, string name)
        => doc.Properties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    internal static Snapshot Parse(byte[] bytes, bool oneDragon)
    {
        var doc = JObject.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        var tasks = new List<TaskEntry>();
        if (oneDragon)
        {
            var list = Property(doc, "taskEnabledList") as JObject ?? throw new InvalidDataException("invalid_task_list");
            foreach (var p in list.Properties())
            {
                if (p.Value is JObject tuple && int.TryParse(p.Name, out var index) && index > 0)
                    tasks.Add(new("legacy:" + p.Name, Property(tuple, "Item2")?.ToString() ?? p.Name,
                        Property(tuple, "Item1")?.Value<bool>() ?? false, index, "legacy"));
                else if (p.Value.Type == JTokenType.Boolean)
                    tasks.Add(new(p.Name, p.Name, p.Value.Value<bool>(), null, "native"));
                else throw new InvalidDataException("unsupported_task_schema");
            }
        }
        else
        {
            var projects = Property(doc, "projects") as JArray ?? throw new InvalidDataException("invalid_project_list");
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in projects)
            {
                var item = token as JObject ?? throw new InvalidDataException("invalid_project");
                var identity = new JObject { ["name"] = Property(item, "name")?.DeepClone(),
                    ["folderName"] = Property(item, "folderName")?.DeepClone(), ["type"] = Property(item, "type")?.DeepClone() };
                var hash = Revision(Encoding.UTF8.GetBytes(identity.ToString(Formatting.None)));
                var occurrence = occurrences.GetValueOrDefault(hash) + 1; occurrences[hash] = occurrence;
                tasks.Add(new("project:" + hash + ":" + occurrence, Property(item, "name")?.ToString() ?? "",
                    Property(item, "status")?.ToString() != "Disabled", tasks.Count + 1, "group"));
            }
        }
        return new(Revision(bytes), tasks, doc);
    }
}
