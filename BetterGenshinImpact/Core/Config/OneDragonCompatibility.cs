using System;
using System.IO;
using System.Linq;
using System.Text;
using BetterGenshinImpact.Service.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OneDragonMigration.Core;

namespace BetterGenshinImpact.Core.Config;

/// <summary>Normal legacy files are projected in memory; opening a page never converts its source.</summary>
internal static class OneDragonCompatibility
{
    internal sealed record Source(string Path, string Revision, byte[] Bytes, OneDragonFlowConfig Config);

    internal static JObject Project(byte[] bytes, string identity)
    {
        var shape = OneDragonConfigShapePreflight.InspectBytes(bytes);
        if (shape.Shape is OneDragonConfigShape.Unknown or OneDragonConfigShape.StructuralBad)
            throw new InvalidDataException(shape.Error ?? "无法识别一条龙配置，原件已保留");
        var projection = OneDragonMigrationEngine.ProjectCompatibleConfig(bytes, identity);
        if (projection.Issues.Any(i => i.Severity == "error"))
            throw new InvalidDataException(string.Join("；", projection.Issues.Where(i => i.Severity == "error").Select(i => i.Message)));
        var document = JObject.Parse(projection.Standard.ToJsonString());
        if (System.IO.Path.IsPathFullyQualified(identity))
        {
            var statePath = StatePath(identity);
            RejectLinks(statePath);
            if (File.Exists(statePath))
            {
                var state = ParseRaw(File.ReadAllBytes(statePath));
                if (state["revision"]?.Value<string>() == TaskConfigurationContract.Revision(bytes) &&
                    state["consumedNextTaskId"]?.Value<string>() == document["NextTaskId"]?.Value<string>())
                    document["NextTaskId"] = string.Empty;
            }
        }
        return document;
    }

    internal static Source Read(string path)
    {
        RejectLinks(path);
        var bytes = File.ReadAllBytes(path);
        var config = Project(bytes, path).ToObject<OneDragonFlowConfig>()
            ?? throw new InvalidDataException("一条龙配置为空，原件已保留");
        // IPC identifies a configuration by its actual file name. Keep every source distinct.
        config.Name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new(System.IO.Path.GetFullPath(path), TaskConfigurationContract.Revision(bytes), bytes, config);
    }

    internal static Source Save(string path, OneDragonFlowConfig config, Source? original)
        => TaskConfigurationContract.ExecuteFileLockedSync(path, () =>
        {
            RejectLinks(path);
            var exists = File.Exists(path);
            byte[]? current = exists ? File.ReadAllBytes(path) : null;
            var sameSource = original != null && string.Equals(original.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (exists && (!sameSource || TaskConfigurationContract.Revision(current!) != original!.Revision))
                throw new InvalidOperationException("配置已被其他窗口或程序修改，请重新选择配置后再保存；未覆盖原件");
            if (!exists && sameSource)
                throw new InvalidOperationException("配置文件已被移除，未自动重建或覆盖");

            // Only public editable fields are updated; legacy orchestration and unknown fields survive.
            var document = original is null ? new JObject() : ParseRaw(original.Bytes);
            var edited = JObject.FromObject(config);
            foreach (var field in edited.Properties()) document[field.Name] = field.Value.DeepClone();
            document.Remove("NextTaskIndex"); // Explicit editing has mapped this legacy index to NextTaskId.
            var text = document.ToString(Formatting.Indented);
            var basis = current ?? original?.Bytes;
            var withBom = basis is { Length: >= 3 } && basis[0] == 239 && basis[1] == 187 && basis[2] == 191;
            if (basis != null && Encoding.UTF8.GetString(basis).Contains("\r\n", StringComparison.Ordinal))
                text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            var encoder = new UTF8Encoding(withBom);
            var output = encoder.GetPreamble().Concat(encoder.GetBytes(text)).ToArray();
            _ = Project(output, path); // Invalid edited watermarks cannot be silently persisted.
            if (current != null && current.SequenceEqual(output)) return Read(path);
            if (current != null) PreserveOriginal(path, current);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            var temporary = path + ".compat-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(output); stream.Flush(true); }
                if (exists && TaskConfigurationContract.Revision(File.ReadAllBytes(path)) != original!.Revision)
                    throw new InvalidOperationException("保存期间配置发生变化；未覆盖原件");
                File.Move(temporary, path, overwrite: exists);
                return Read(path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });

    internal static void PreserveOriginal(string path, byte[] bytes)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, ".compat-backup");
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        var backup = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(path) + "." + TaskConfigurationContract.Revision(bytes) + ".bak");
        if (File.Exists(backup))
        {
            if (!File.ReadAllBytes(backup).SequenceEqual(bytes)) throw new InvalidDataException("原配置备份已变化，保存已停止");
            return;
        }
        using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
    }

    /// <summary>Execution consumes the old task start marker without rewriting its resource document.</summary>
    internal static void ConsumeStartMarker(Source source, string taskId)
        => TaskConfigurationContract.ExecuteFileLockedSync(source.Path, () =>
        {
            if (TaskConfigurationContract.Revision(File.ReadAllBytes(source.Path)) != source.Revision)
                throw new InvalidOperationException("configuration_changed");
            var statePath = StatePath(source.Path);
            RejectLinks(statePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
            var data = new JObject { ["revision"] = source.Revision, ["consumedNextTaskId"] = taskId };
            var temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(data.ToString(Formatting.None)));
                    stream.Flush(true);
                }
                File.Move(temporary, statePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        });

    private static string StatePath(string path)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
            ".compat-state", System.IO.Path.GetFileName(path) + ".json");

    private static JObject ParseRaw(byte[] bytes)
    {
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')))
        { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static void RejectLinks(string path)
    {
        for (var item = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = System.IO.Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("配置路径包含目录链接，原件已保留");
    }
}
