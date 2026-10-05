using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mistletoe.Shared;

// Linked into both executables. Progress is never a successful effect by itself.
internal sealed record TerminalEffectProgress(string Token, string Epoch, string JobId,
    string Key, string WireRunId, string Action, string RequestFingerprint,
    string Machine, DateTimeOffset StartedAtUtc, string Stage, bool GameExitConfirmed);
internal sealed record TerminalEffectProof(TerminalEffectProgress Progress, string Source,
    DateTimeOffset ObservedAtUtc, int? ExitCode, long[] SystemEventIds);
internal sealed record TerminalSystemEvent(int Id, string Provider, long RecordId,
    DateTimeOffset AtUtc, string? Comment = null);

internal static class TerminalEffectJournal
{
    internal const string Capability = "terminal.effect.receipt.v1";
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusBGI", "terminal-effects");
    internal static bool NeedsIndependentEffect(string? action)
        => action is "closeSoftware" or "closeGameAndSoftware" or "shutdown";
    internal static bool ValidToken(string? token) => token is { Length: 64 } && token.All(Uri.IsHexDigit);
    internal static string ShutdownComment(string token) => "NexusBGI C17 " + token;
    internal static string Fingerprint(string operation, JObject payload)
    {
        // Match the existing IPC receiver's Newtonsoft date parsing before its contract hash.
        // The same normalization is idempotent for the already decoded server payload.
        var data = JObject.Parse(payload.ToString(Formatting.None)); data.Remove("idempotencyKey");
        JToken Canonical(JToken value) => value switch
        {
            JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Name, Canonical(p.Value)))),
            JArray array => new JArray(array.Select(Canonical)),
            _ => value.DeepClone()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + Canonical(data).ToString(Formatting.None))));
    }
    internal static string FilePath(string root, string token, bool proof = false)
    {
        if (!ValidToken(token)) throw new InvalidDataException("Invalid terminal effect token");
        return Path.Combine(root, token + (proof ? ".observed.json" : ".progress.json"));
    }
    internal static T? Read<T>(string root, string token, bool proof = false) where T : class
    {
        var path = FilePath(root, token, proof);
        return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)) : null;
    }
    internal static void Write<T>(string root, string token, T value, bool proof = false)
    {
        Directory.CreateDirectory(root);
        var path = FilePath(root, token, proof); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonConvert.SerializeObject(value);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes); file.Flush(true);
            }
            File.Move(temporary, path, true);
            if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), json, StringComparison.Ordinal))
                throw new IOException("Terminal progress readback failed");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static bool Matches(TerminalEffectProgress progress, string token, string? epoch,
        string? job, string? key, string? wireRun, string? action, string? fingerprint)
        => ValidToken(token) && progress.Token == token && progress.Epoch == epoch
            && !string.IsNullOrEmpty(progress.JobId) && (string.IsNullOrEmpty(job) || progress.JobId == job)
            && !string.IsNullOrEmpty(key) && progress.Key == key && !string.IsNullOrEmpty(wireRun) && progress.WireRunId == wireRun
            && progress.Action == action && NeedsIndependentEffect(action)
            && fingerprint is { Length: 64 } && progress.RequestFingerprint == fingerprint
            && progress.Machine == Environment.MachineName && progress.StartedAtUtc <= DateTimeOffset.UtcNow
            && (action == "shutdown" ? progress.Stage == "shutdown_requested" : progress.Stage == "software_exit_requested")
            && (action == "closeSoftware" || progress.GameExitConfirmed);

    internal static bool ProofMatches(TerminalEffectProof proof, string token, string? epoch,
        string? job, string? key, string? wireRun, string? action, string? fingerprint)
        => Matches(proof.Progress, token, epoch, job, key, wireRun, action, fingerprint)
            && proof.ObservedAtUtc >= proof.Progress.StartedAtUtc && proof.ObservedAtUtc <= DateTimeOffset.UtcNow
            && (action == "shutdown" ? proof.Source == "windows_system_shutdown_and_boot"
                && proof.SystemEventIds is { Length: 3 } && proof.SystemEventIds.All(id => id > 0)
                && proof.SystemEventIds.Distinct().Count() == 3
                : proof.Source == "original_process_handle_exit" && proof.ExitCode == 0);

    internal static long[]? ConfirmShutdown(TerminalEffectProgress progress, IReadOnlyList<TerminalSystemEvent> events)
    {
        if (progress.Action != "shutdown" || progress.Stage != "shutdown_requested" || !progress.GameExitConfirmed) return null;
        var ordered = events.Where(e => e.AtUtc >= progress.StartedAtUtc).OrderBy(e => e.AtUtc).ThenBy(e => e.RecordId).ToList();
        var request = ordered.SingleOrDefault(e => e.Id == 1074 && e.Provider == "User32" && e.Comment == ShutdownComment(progress.Token));
        if (request is null || request.AtUtc > progress.StartedAtUtc.AddMinutes(5)) return null;
        var close = ordered.FirstOrDefault(e => e.Id == 6006 && e.Provider == "EventLog" && e.AtUtc > request.AtUtc);
        if (close is null || close.AtUtc > request.AtUtc.AddMinutes(5)) return null;
        var boot = ordered.FirstOrDefault(e => e.Id == 12 && e.Provider == "Microsoft-Windows-Kernel-General" && e.AtUtc > close.AtUtc);
        if (boot is null || ordered.Any(e => e.AtUtc > request.AtUtc && e.AtUtc <= boot.AtUtc
            && (e.Id == 1074 || e.Id == 41 || e.Id == 6008 || e.Id == 12 && e != boot))) return null;
        return [request.RecordId, close.RecordId, boot.RecordId];
    }
}
