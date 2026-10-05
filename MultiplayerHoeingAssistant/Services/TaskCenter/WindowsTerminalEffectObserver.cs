using System.Diagnostics;
using System.IO;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Mistletoe.Shared;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal interface ITerminalEffectObserver
{
    IDisposable? PinOriginalProcess(BgiEpoch epoch);
    TerminalEffectProof? Observe(PendingCompletionRecord record, IDisposable? pinnedProcess);
}

internal sealed class WindowsTerminalEffectObserver : ITerminalEffectObserver
{
    private readonly string _root;
    internal WindowsTerminalEffectObserver(string? root = null) => _root = root ?? TerminalEffectJournal.Root;
    public IDisposable? PinOriginalProcess(BgiEpoch epoch)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(epoch.ProcessId);
            // Force an OS handle now; PID reuse/absence after send never constitutes positive observation.
            _ = process.Handle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != epoch.StartTicksUtc)
            { process.Dispose(); return null; }
            return process;
        }
        catch { process?.Dispose(); return null; }
    }

    public TerminalEffectProof? Observe(PendingCompletionRecord record, IDisposable? pinnedProcess)
    {
        try
        {
            var token = record.TerminalEffectToken;
            if (!TerminalEffectJournal.ValidToken(token)) return null;
            bool Matches(TerminalEffectProgress p) => TerminalEffectJournal.Matches(p, token!, record.Epoch,
                record.JobId, record.IdempotencyKey, record.WireRunId, record.Action, record.TerminalRequestFingerprint);
            bool ProofMatches(TerminalEffectProof p) => TerminalEffectJournal.ProofMatches(p, token!, record.Epoch,
                record.JobId, record.IdempotencyKey, record.WireRunId, record.Action, record.TerminalRequestFingerprint);
            var progress = TerminalEffectJournal.Read<TerminalEffectProgress>(_root, token!);
            if (progress is null || !Matches(progress)) return null;
            var saved = TerminalEffectJournal.Read<TerminalEffectProof>(_root, token!, true);
            if (saved is not null) return saved.Progress == progress && ProofMatches(saved) ? saved : null;
            TerminalEffectProof? proof = null;
            if (record.Action == "shutdown")
            {
                var ids = TerminalEffectJournal.ConfirmShutdown(progress, ReadSystemEvents(progress.StartedAtUtc));
                if (ids is not null) proof = new(progress, "windows_system_shutdown_and_boot", DateTimeOffset.UtcNow, null, ids);
            }
            else if (pinnedProcess is Process process && process.Id + ":" + process.StartTime.ToUniversalTime().Ticks == record.Epoch
                && process.HasExited && process.ExitCode == 0)
                proof = new(progress, "original_process_handle_exit", DateTimeOffset.UtcNow, process.ExitCode, []);
            if (proof is null || !ProofMatches(proof)) return null;
            TerminalEffectJournal.Write(_root, token!, proof, true);
            var readback = TerminalEffectJournal.Read<TerminalEffectProof>(_root, token!, true);
            return readback is not null && ProofMatches(readback) ? readback : null;
        }
        catch { return null; } // Missing, corrupt, access denied or ambiguous facts retain original responsibility.
    }

    private static IReadOnlyList<TerminalSystemEvent> ReadSystemEvents(DateTimeOffset since)
    {
        var utc = since.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var query = new EventLogQuery("System", PathType.LogName,
            $"*[System[(EventID=1074 or EventID=6006 or EventID=12 or EventID=41 or EventID=6008) and TimeCreated[@SystemTime >= '{utc}']]]");
        using var reader = new EventLogReader(query);
        var events = new List<TerminalSystemEvent>();
        while (reader.ReadEvent() is { } entry)
        {
            using (entry)
            {
                if (events.Count >= 512) throw new InvalidDataException("Shutdown observation log range exceeds supported window");
                var xml = XDocument.Parse(entry.ToXml()); XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
                var comment = xml.Descendants(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == "param6")?.Value;
                if (entry.TimeCreated is null || entry.RecordId is null) throw new InvalidDataException("System event identity missing");
                events.Add(new(entry.Id, entry.ProviderName ?? "", entry.RecordId.Value,
                    new DateTimeOffset(entry.TimeCreated.Value.ToUniversalTime()), comment));
            }
        }
        return events;
    }
}
