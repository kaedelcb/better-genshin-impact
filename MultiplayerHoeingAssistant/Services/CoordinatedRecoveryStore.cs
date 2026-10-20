using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record CoordinatedRecoveryProgress(int Index, int Attempt, string Stage,
    string Result, string Reason, BatchItemState? ItemState, string? JobId, int CompletedNative)
{
    public BgiEpoch? ItemEpoch { get; init; }
}

internal sealed record CoordinatedRecoverySnapshot
{
    public int Version { get; init; } = 1;
    public long Revision { get; init; }
    public string ControlIdentity { get; init; } = "";
    public int OwnerPid { get; init; }
    public long OwnerStartTicks { get; init; }
    public int OwnerSession { get; init; }
    public required CoordinatedBatchSnapshot Start { get; init; }
    public required string SelfUid { get; init; }
    public required string Token { get; init; }
    public required TaskConflictPolicySettings Policy { get; init; }
    public CoordinatedRecoveryProgress? Progress { get; init; }
    public BgiEpoch? Epoch { get; init; }
    public BgiEpoch? ProcessRecoveryPreviousEpoch { get; init; }
    public long? StopVersion { get; init; }
    public string? RecoveryStopSignalName { get; init; }
    public string? Ticket { get; init; }
    public string? SuspendReceipt { get; init; }
    public Dictionary<string, string> ConfigRevisions { get; init; } = new();
    public bool Prepared { get; init; }
    public bool TakenOver { get; init; }
    public bool Stopped { get; init; }
    public bool Settled { get; init; }
    public bool LocalSucceeded { get; init; }
    public bool LocalTasksFailed { get; init; }
    public bool CoordinatedSucceeded { get; init; }
    public bool LocalReleased { get; init; }
    public bool NativeResumeRetryPending { get; init; }
    public string? ManagedRunId { get; init; }
    public bool CompletionRecoveryReservationAcquired { get; init; }
    public int PolicyAttempt { get; init; }
    public IReadOnlyList<BgiTaskQueueStatus> FailedPolicyAttempts { get; init; } = [];
    public bool PolicyStartAttempted { get; init; }
    public string? PolicyRequestIdentity { get; init; }
    public Dictionary<string, object?>? PolicyPayload { get; init; }
    public Dictionary<string, object?>? PlanContinuation { get; init; }
    public HoeingPolicyExecution? PolicyExecution { get; init; }
    public BgiTaskQueueStatus? PolicyTerminal { get; init; }
    public bool PolicyAcknowledged { get; init; }
    public bool PolicyTicketReleased { get; init; }
    public bool PolicyRejected { get; init; }
}

/// <summary>Only the existing batch's recovery record; it never dispatches or authorizes work.</summary>
internal sealed class CoordinatedRecoveryStore(string directory, string identity, Action<string>? log = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _writtenRevisions = new();
    private string PathFor(string batchId) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + batchId))) + ".json");

    internal CoordinatedRecoverySnapshot? Load(CoordinatedBatchSnapshot current, string uid,
        Func<int, long, bool?>? ownerAlive = null)
    {
        lock (_gate)
        {
            var path = PathFor(current.BatchId);
            if (!File.Exists(path)) return null;
            try
            {
                var saved = JsonSerializer.Deserialize<CoordinatedRecoverySnapshot>(File.ReadAllBytes(path));
                return Validate(saved, current, uid, ownerAlive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                try { log?.Invoke("[联机恢复] 原恢复记录暂不可读，保留原文件：" + ex.Message); } catch { }
                return null;
            }
        }
    }

    private CoordinatedRecoverySnapshot? Validate(CoordinatedRecoverySnapshot? saved,
        CoordinatedBatchSnapshot current, string uid, Func<int, long, bool?>? ownerAlive)
    {
        if (saved is null || saved.Start is null || saved.Policy is null || saved.ConfigRevisions is null
            || saved.Start.Bindings is null || saved.Start.ParticipantUids is null
            || saved.FailedPolicyAttempts is null || saved.Revision < 0 || saved.PolicyAttempt < 0
            || saved.OwnerPid <= 0 || saved.OwnerStartTicks <= 0
            || saved.Progress is { Index: < 0 } or { Attempt: < 0 }
            || saved.TakenOver && (saved.Epoch is not { ProcessId: > 0, StartTicksUtc: > 0 } || saved.StopVersion is null or < 0)
            || saved.CompletionRecoveryReservationAcquired
                && (saved.ProcessRecoveryPreviousEpoch is null || string.IsNullOrWhiteSpace(saved.Ticket))
            || saved.ManagedRunId is not null && string.IsNullOrWhiteSpace(saved.ManagedRunId)
            || saved.Version != 1 || saved.ControlIdentity != identity
            || saved.OwnerSession != Process.GetCurrentProcess().SessionId
            || saved.Start.BatchId != current.BatchId || saved.Start.Generation != current.Generation
            || saved.SelfUid != uid || saved.Settled
            || saved.Start.PriorityRecovery != true || !current.PriorityRecovery
            || !saved.Start.Bindings.TryGetValue(uid, out var previous)
            || !current.Bindings.TryGetValue(uid, out var now) || previous is null || !previous.SequenceEqual(now)
            || saved.Progress is { } progress && progress.Index >= previous.Count
            || !saved.Start.ParticipantUids.SequenceEqual(current.ParticipantUids)
            || string.IsNullOrWhiteSpace(saved.Token)) return null;
        // A second assistant must never adopt a living or unconfirmed owner's record.
        if ((ownerAlive ?? IsProcessAlive)(saved.OwnerPid, saved.OwnerStartTicks) != false) return null;
        return saved;
    }

    internal CoordinatedRecoverySnapshot? ReadSurvivingPeer(string json, CoordinatedBatchSnapshot current, string uid,
        Func<int, long, bool?>? ownerAlive = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("assistantRecovery", out var snapshot)
                && snapshot.ValueKind == JsonValueKind.Object
                ? Validate(snapshot.Deserialize<CoordinatedRecoverySnapshot>(), current, uid, ownerAlive) : null;
        }
        catch (JsonException) { return null; }
    }

    internal string CaptureForPeer(OnlineHoeingBatch batch)
    {
        using var owner = Process.GetCurrentProcess();
        return JsonSerializer.Serialize(batch.CaptureRecovery() with
        {
            ControlIdentity = identity, OwnerPid = owner.Id,
            OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, OwnerSession = owner.SessionId
        });
    }

    internal void Save(OnlineHoeingBatch batch)
    {
        if (!batch.PriorityRecoveryEnabled || batch.Start is null) return;
        // Capture before the store lock: stop holds the batch lock and must never wait on a
        // writer that is itself waiting for that lock. Revision rejects late captured snapshots.
        var captured = batch.CaptureRecovery();
        lock (_gate)
        {
            if (_writtenRevisions.GetValueOrDefault(batch.BatchId) >= captured.Revision) return;
            string? temporary = null;
            try
            {
                using var owner = Process.GetCurrentProcess();
                var snapshot = captured with
                {
                    ControlIdentity = identity, OwnerPid = owner.Id,
                    OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, OwnerSession = owner.SessionId
                };
                var path = PathFor(batch.BatchId);
                Directory.CreateDirectory(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, snapshot);
                    file.Flush(flushToDisk: true);
                }
                // Keep the previous bytes, including a damaged old record, for diagnosis.
                if (File.Exists(path)) File.Replace(temporary, path, path + ".previous");
                else File.Move(temporary, path);
                _writtenRevisions[batch.BatchId] = captured.Revision;
            }
            catch (Exception ex)
            {
                // Recovery bookkeeping cannot veto the already-held batch's execution.
                try { log?.Invoke("[联机恢复] 记录未保存，原批次按现有执行事实继续：" + ex.Message); } catch { }
            }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); } catch { }
            }
        }
    }

    private static bool? IsProcessAlive(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch { return null; }
    }

    internal static Dictionary<string, object?>? Materialize(Dictionary<string, object?>? values)
        => values?.ToDictionary(p => p.Key, p => p.Value is JsonElement value ? FromJson(value) : p.Value);

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Null => null,
        _ => value.Clone()
    };
}
