using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using Mistletoe.Shared;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

internal static class TerminalEffectDispatch
{
    internal static TerminalEffectProgress? Prepare(JObject data, Guid handle, string? root = null)
    {
        var token = (string?)data["terminalEffectToken"];
        if (token is null) return null; // Existing clients retain their original unknown-result contract.
        if (!TerminalEffectJournal.ValidToken(token)) throw new ArgumentException("Invalid terminal effect token");
        root ??= TerminalEffectJournal.Root;
        var epoch = JobRegistry.CurrentEpoch;
        var progress = new TerminalEffectProgress(token, epoch.ProcessId + ":" + epoch.StartTicksUtc, handle.ToString("N"),
            (string)data["idempotencyKey"]!, (string)data["workflowRunId"]!, (string)data["action"]!,
            ExecutionRequestContract.Fingerprint(new InstanceIpcEnvelope { Operation = ExternalInterfaceOperations.TerminalCompletionAction, Data = data }),
            Environment.MachineName, DateTimeOffset.UtcNow, "prepared", false);
        var existing = TerminalEffectJournal.Read<TerminalEffectProgress>(root, token);
        if (existing is not null) throw new InvalidOperationException("Terminal effect token already has durable progress; never repeat the effect");
        TerminalEffectJournal.Write(root, token, progress);
        return progress;
    }

    internal static TerminalEffectProgress RecordRequest(TerminalEffectProgress progress, string stage, bool gameExitConfirmed)
    {
        var next = progress with { Stage = stage, GameExitConfirmed = gameExitConfirmed };
        TerminalEffectJournal.Write(TerminalEffectJournal.Root, progress.Token, next);
        return next;
    }

    internal static async Task RequestShutdownAsync(TerminalEffectProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/s", "/t", "60", "/c", TerminalEffectJournal.ShutdownComment(progress.Token) }) info.ArgumentList.Add(arg);
        using var command = Process.Start(info) ?? throw new InvalidOperationException("Shutdown request did not start");
        // Cancellation after process launch does not prove the command was not applied. Never retry.
        await command.WaitForExitAsync(CancellationToken.None);
        if (command.ExitCode != 0) throw new InvalidOperationException("Shutdown request failed: " + command.ExitCode);
        RecordRequest(progress, "shutdown_requested", true); // Command exit is only progress, never effect success.
    }
}
