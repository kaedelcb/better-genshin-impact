using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mistletoe.Shared;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>Uses the existing BGI heartbeat to restore the verified assistant owner of an active batch.</summary>
internal static class CoordinatedAssistantRecovery
{
    private sealed record Owner(int Pid, long StartTicks, int Session, string Executable,
        string? DataRoot, CoordinatedStopSignal Signal);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Owner> Owners = new();
    private static readonly Dictionary<string, JObject> Checkpoints = new();
    private static int _polling;
    private static DateTime _lastLaunch;

    internal static void ObserveCheckpoint(int? peerPid, int? peerSession, JObject? checkpoint)
    {
        if (checkpoint is null || peerPid is not > 0 || peerSession != Process.GetCurrentProcess().SessionId) return;
        try
        {
            if (checkpoint["Version"]?.Value<int>() != 1
                || checkpoint["OwnerPid"]?.Value<int>() != peerPid
                || checkpoint["OwnerSession"]?.Value<int>() != peerSession
                || checkpoint["OwnerStartTicks"]?.Value<long>() is not { } ticks
                || IsAlive(peerPid.Value, ticks) != true
                || checkpoint["Start"]?["BatchId"]?.Value<string>() is not { Length: > 0 } batchId
                || checkpoint["ControlIdentity"]?.Value<string>() is not { Length: > 0 } identity
                || checkpoint["Revision"]?.Value<long>() is not { } revision
                || checkpoint.ToString(Newtonsoft.Json.Formatting.None).Length > 256 * 1024) return;
            var key = identity + "\n" + batchId;
            lock (Gate)
            {
                if (Checkpoints.TryGetValue(key, out var previous))
                {
                    if (previous["Revision"]!.Value<long>() >= revision) return;
                    var previousPid = previous["OwnerPid"]!.Value<int>();
                    var previousTicks = previous["OwnerStartTicks"]!.Value<long>();
                    if ((previousPid != peerPid || previousTicks != ticks) && IsAlive(previousPid, previousTicks) != false)
                        return;
                }
                if (!Checkpoints.ContainsKey(key) && Checkpoints.Count >= 256)
                {
                    foreach (var retired in Checkpoints.Where(p => p.Value["Settled"]?.Value<bool>() == true).ToArray())
                        Checkpoints.Remove(retired.Key);
                    if (Checkpoints.Count >= 256) return; // Auxiliary capacity never rejects the actual execution request.
                }
                Checkpoints[key] = (JObject)checkpoint.DeepClone();
            }
        }
        catch (Exception ex) { Trace.WriteLine("[联机恢复] 辅助快照暂未更新，原执行继续：" + ex.Message); }
    }

    internal static JObject? ReadCheckpoint(string? batchId, string? identity)
    {
        if (string.IsNullOrWhiteSpace(batchId) || string.IsNullOrWhiteSpace(identity)) return null;
        lock (Gate) return Checkpoints.TryGetValue(identity + "\n" + batchId, out var checkpoint)
            ? (JObject)checkpoint.DeepClone() : null;
    }

    internal static void Register(int? peerPid, int? peerSession, string signalName, string? dataRoot)
    {
        if (peerPid is not > 0 || peerSession != Process.GetCurrentProcess().SessionId) return;
        try
        {
            using var peer = Process.GetProcessById(peerPid.Value);
            if (peer.HasExited || peer.SessionId != peerSession
                || !peer.ProcessName.Equals("MultiplayerHoeingAssistant", StringComparison.OrdinalIgnoreCase)) return;
            var executable = peer.MainModule?.FileName;
            if (executable is null || !File.Exists(executable)
                || dataRoot is not null && !Path.IsPathFullyQualified(dataRoot)) return;
            var signal = CoordinatedStopSignal.Open(signalName);
            if (signal is null) return;
            var owner = new Owner(peer.Id, peer.StartTime.ToUniversalTime().Ticks, peer.SessionId, executable, dataRoot, signal);
            lock (Gate)
            {
                if (Owners.Remove(signalName, out var previous)) previous.Signal.Dispose();
                Owners[signalName] = owner;
            }
        }
        catch (Exception ex) { Trace.WriteLine("[联机恢复] 助手进程身份暂未确认：" + ex.Message); }
    }

    internal static void Poll()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (Gate)
                {
                    foreach (var pair in Owners.ToArray())
                    {
                        var owner = pair.Value;
                        if (owner.Signal.IsStopped)
                        {
                            Owners.Remove(pair.Key);
                            owner.Signal.Dispose();
                            continue;
                        }
                        if (owner.Signal.RelaunchSuppressed || IsAlive(owner.Pid, owner.StartTicks) != false
                            || DateTime.UtcNow - _lastLaunch < TimeSpan.FromSeconds(30)) continue;
                        // Unknown enumeration/access and any other session-local assistant retain exclusion.
                        if (!NoAssistantInSession(owner.Session)) continue;
                        if (owner.Signal.IsStopped || owner.Signal.RelaunchSuppressed) continue;
                        var start = new ProcessStartInfo(owner.Executable)
                        {
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = Path.GetDirectoryName(owner.Executable)!
                        };
                        start.ArgumentList.Add("--minimized");
                        start.ArgumentList.Add("--manual-start");
                        if (owner.DataRoot is not null) start.Environment["NEXUSBGI_DATA_ROOT"] = owner.DataRoot;
                        _lastLaunch = DateTime.UtcNow;
                        // The assistant's per-session/data-root mutex is the final atomic admission.
                        using var launched = Process.Start(start);
                        Trace.WriteLine(launched is null ? "[联机恢复] 助手启动未确认"
                            : "[联机恢复] 原助手已退出，已请求启动同一程序接回原批次");
                        break;
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("[联机恢复] 助手自动接续稍后重试：" + ex.Message); }
            finally { Interlocked.Exchange(ref _polling, 0); }
        });
    }

    internal static bool? IsAlive(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch { return null; }
    }

    private static bool NoAssistantInSession(int session)
    {
        try
        {
            var found = false;
            foreach (var process in Process.GetProcessesByName("MultiplayerHoeingAssistant"))
                using (process)
                    if (process.SessionId == session) found = true;
            return !found;
        }
        catch { return false; }
    }
}
