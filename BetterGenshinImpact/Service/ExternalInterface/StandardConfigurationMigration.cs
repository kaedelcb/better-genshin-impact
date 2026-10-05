using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.ViewModel.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>BGI owns installation into its own User root. The durable journal precedes every replacement.</summary>
internal static class StandardConfigurationMigration
{
    internal const string Operation = "ext.config.migrateStandard";
    private static readonly object Gate = new();
    private sealed record Entry(string ConfigName, string? ContentBase64, string SourceRevision, string? OriginalBase64,
        string? ContentFile = null, string? ContentRevision = null);
    private sealed record Journal(string InstallId, string State, List<Entry> Files);

    internal static async Task<InstanceIpcEnvelope> DispatchAsync(InstanceIpcEnvelope request,
        string? userRoot = null, Func<IDisposable>? quiesce = null, Action? refresh = null, Action<int>? afterInstallFile = null)
    {
        try
        {
            if (ExecutionRequestContract.Validate(request) is { } rejected) return rejected;
            var root = Path.GetFullPath(userRoot ?? Path.Combine(AppContext.BaseDirectory, "User"));
            InstanceIpcEnvelope Execute()
            {
                lock (Gate)
                {
                    var id = request.Data?["installId"]?.Value<string>();
                    ValidateName(id);
                    var path = Path.Combine(root, "migration-installations", id + ".json");
                    RejectLinks(path);
                    var action = request.Data?["action"]?.Value<string>();
                    var expectedRoot = request.Data?["expectedUserRoot"]?.Value<string>();
                    if (expectedRoot != null && (!Path.IsPathFullyQualified(expectedRoot) ||
                        !Path.GetFullPath(expectedRoot).Equals(root, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("installation_root_changed");
                    if (action != "status" && expectedRoot == null)
                        throw new InvalidOperationException("installation_root_required");
                    var journal = File.Exists(path) ? JsonConvert.DeserializeObject<Journal>(File.ReadAllText(path))
                        ?? throw new InvalidOperationException("installation_journal_invalid") : null;
                    if (journal != null) ValidateJournal(journal, id!);
                    if (action == "status")
                        return InstanceIpcEnvelope.Response(request, new { status = journal?.State ?? "absent", userRoot = root, installId = id });
                    using var lease = quiesce?.Invoke() ?? AcquireQuietWindow();
                    if (ExecutionRequestContract.Validate(request) is { } expired) return expired;
                    if (action == "install")
                    {
                        if (journal?.State == "rollingBack") throw new InvalidOperationException("installation_rollback_pending");
                        var incoming = request.Data?["files"]?.ToObject<List<Entry>>() ?? throw new ArgumentException("files_required");
                        if (incoming.Count == 0 || incoming.Count > 128 || incoming.Select(f => f.ConfigName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != incoming.Count)
                            throw new ArgumentException("invalid_installation_files");
                        for (var i = 0; i < incoming.Count; i++)
                        {
                            var file = incoming[i];
                            ValidateName(file.ConfigName);
                            byte[] bytes;
                            if (file.ContentFile != null)
                            {
                                if (file.ContentBase64 != null || !Path.IsPathFullyQualified(file.ContentFile) ||
                                    Path.GetExtension(file.ContentFile) != ".json") throw new ArgumentException("invalid_standard_input");
                                RejectLinks(file.ContentFile);
                                bytes = File.ReadAllBytes(file.ContentFile);
                                if (!Hash(bytes).Equals(file.ContentRevision, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("standard_input_changed");
                                incoming[i] = file = file with { ContentBase64 = Convert.ToBase64String(bytes), ContentFile = null, ContentRevision = null };
                            }
                            else bytes = Convert.FromBase64String(file.ContentBase64!);
                            if (bytes.Length > 4 * 1024 * 1024 || !OneDragonConfigShapePreflight.InspectBytes(bytes).IsLoadable)
                                throw new InvalidOperationException("standard_configuration_invalid");
                            _ = TaskConfigurationContract.Parse(bytes, true);
                            if (string.IsNullOrWhiteSpace(file.SourceRevision)) throw new ArgumentException("source_revision_required");
                        }
                        if (journal != null && (journal.Files.Count != incoming.Count || journal.Files.Where((f, i) =>
                            f.ConfigName != incoming[i].ConfigName || f.ContentBase64 != incoming[i].ContentBase64 ||
                            !f.SourceRevision.Equals(incoming[i].SourceRevision, StringComparison.OrdinalIgnoreCase)).Any()))
                            throw new InvalidOperationException("installation_identity_changed");
                        return WithFileLocks(root, incoming, 0, () =>
                        {
                            if (journal == null)
                            {
                                var originals = incoming.Select(f =>
                                {
                                    var target = Target(root, f.ConfigName);
                                    byte[]? previous = File.Exists(target) ? File.ReadAllBytes(target) : null;
                                    if (previous != null && !Hash(previous).Equals(f.SourceRevision, StringComparison.OrdinalIgnoreCase))
                                        throw new InvalidOperationException("configuration_changed");
                                    return f with { OriginalBase64 = previous == null ? null : Convert.ToBase64String(previous) };
                                }).ToList();
                                journal = new(id!, "prepared", originals);
                                Save(path, journal);
                            }
                            foreach (var file in journal.Files) RequireOwnedState(root, file);
                            Save(path, journal = journal with { State = "prepared" });
                            var written = 0;
                            foreach (var file in journal.Files)
                            {
                                var target = Target(root, file.ConfigName);
                                var bytes = Convert.FromBase64String(file.ContentBase64!);
                                if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes)) continue;
                                RequireOwnedState(root, file);
                                WriteAtomic(target, bytes, File.Exists(target));
                                if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException("installation_readback_failed");
                                afterInstallFile?.Invoke(++written);
                            }
                            foreach (var file in journal.Files)
                                if (!File.ReadAllBytes(Target(root, file.ConfigName)).SequenceEqual(Convert.FromBase64String(file.ContentBase64!)))
                                    throw new IOException("installation_readback_failed");
                            Save(path, journal with { State = "installed" });
                            refresh?.Invoke();
                            return InstanceIpcEnvelope.Response(request, new { status = "installed", installId = id, userRoot = root });
                        });
                    }
                    if (action != "rollback" || journal == null) throw new InvalidOperationException("installation_journal_missing");
                    return WithFileLocks(root, journal.Files, 0, () =>
                    {
                        foreach (var file in journal.Files) RequireOwnedState(root, file);
                        Save(path, journal = journal with { State = "rollingBack" });
                        foreach (var file in journal.Files)
                        {
                            RequireOwnedState(root, file);
                            var target = Target(root, file.ConfigName);
                            if (file.OriginalBase64 == null)
                            {
                                // Only a byte-identical file declared as newly installed is removed.
                                if (File.Exists(target)) File.Delete(target);
                            }
                            else WriteAtomic(target, Convert.FromBase64String(file.OriginalBase64), File.Exists(target));
                        }
                        foreach (var file in journal.Files)
                        {
                            var target = Target(root, file.ConfigName);
                            if (file.OriginalBase64 == null ? File.Exists(target) : !File.ReadAllBytes(target).SequenceEqual(Convert.FromBase64String(file.OriginalBase64)))
                                throw new IOException("rollback_readback_failed");
                        }
                        Save(path, journal with { State = "rolledBack" });
                        refresh?.Invoke();
                        return InstanceIpcEnvelope.Response(request, new { status = "rolledBack", installId = id, userRoot = root });
                    });
                }
            }
            if (userRoot != null) return Execute();
            if (Application.Current == null) throw new InvalidOperationException("editor_unavailable");
            refresh = () => App.GetService<OneDragonFlowViewModel>()?.InitConfigList();
            return await Application.Current.Dispatcher.InvokeAsync(Execute);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return InstanceIpcEnvelope.Failure(request, "standard_installation_failed", ex.Message + "；原件及安装记录保留，请在静止后从迁移回退恢复");
        }
    }

    private static void ValidateJournal(Journal journal, string id)
    {
        if (journal.InstallId != id || journal.State is not ("prepared" or "installed" or "rollingBack" or "rolledBack") ||
            journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 128 ||
            journal.Files.Select(f => f.ConfigName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw new InvalidOperationException("installation_journal_invalid");
        foreach (var file in journal.Files)
        {
            ValidateName(file.ConfigName);
            if (file.OriginalBase64 != null && !Hash(Convert.FromBase64String(file.OriginalBase64)).Equals(file.SourceRevision, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("installation_original_invalid");
            if (file.ContentFile != null || file.ContentRevision != null || file.ContentBase64 == null ||
                !OneDragonConfigShapePreflight.InspectBytes(Convert.FromBase64String(file.ContentBase64)).IsLoadable)
                throw new InvalidOperationException("installation_output_invalid");
        }
    }
    private static void RequireOwnedState(string root, Entry file)
    {
        var target = Target(root, file.ConfigName);
        if (!File.Exists(target))
        {
            if (file.OriginalBase64 != null) throw new InvalidOperationException("configuration_missing");
            return;
        }
        var current = File.ReadAllBytes(target);
        if (!current.SequenceEqual(Convert.FromBase64String(file.ContentBase64!)) &&
            (file.OriginalBase64 == null || !current.SequenceEqual(Convert.FromBase64String(file.OriginalBase64))))
            throw new InvalidOperationException("configuration_changed");
    }
    private static T WithFileLocks<T>(string root, List<Entry> files, int index, Func<T> action)
        => index == files.Count ? action() : TaskConfigurationContract.ExecuteFileLockedSync(Target(root, files[index].ConfigName),
            () => WithFileLocks(root, files, index + 1, action));
    private static string Target(string root, string name)
    {
        ValidateName(name);
        var path = Path.Combine(root, "OneDragon", name + ".json");
        RejectLinks(path);
        return path;
    }
    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name is "." or ".." ||
            name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("invalid_installation_name");
    }
    private static void RejectLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("installation_link_rejected");
    }
    private static string Hash(byte[] bytes) => TaskConfigurationContract.Revision(bytes);
    private static void Save(string path, Journal value) => WriteAtomic(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)), File.Exists(path));
    private static void WriteAtomic(string path, byte[] bytes, bool replace)
    {
        RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, replace);
    }
    private static IDisposable AcquireQuietWindow()
    {
        if (!TaskControl.TaskSemaphore.Wait(0)) throw new InvalidOperationException("execution_busy");
        if (ExecutionScope.HasActive || JobRegistry.IsCreated && JobRegistry.Instance.HasActiveJob())
        { TaskControl.TaskSemaphore.Release(); throw new InvalidOperationException("execution_busy"); }
        return new QuietWindow();
    }
    private sealed class QuietWindow : IDisposable
    {
        public void Dispose() => TaskControl.TaskSemaphore.Release();
    }
}
