using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    // This is the original live writer's progress, never a second scheduler or a startup recovery source.
    // The owner capability, submission CAS, send proofs and stop authority still apply to every change.
    private sealed class PriorityProgress(WorkflowRunRecord original)
    {
        internal WorkflowRunRecord Current = CopyProgress(original);
        internal WorkflowRunRecord Published = CopyProgress(original);
        internal readonly Queue<ProgressPublication> Unpublished = new();
        internal DateTimeOffset NextPublish;
        internal string? PersistenceError;
    }
    private sealed record ProgressPublication(Action Publish, int PayloadBytes);
    private sealed record ProgressEdit(string[] Path, string Kind, JsonNode? Value);

    private static List<ProgressEdit> ProgressChanges(WorkflowRunRecord before, WorkflowRunRecord after)
    {
        var edits = new List<ProgressEdit>();
        void Diff(JsonNode? old, JsonNode? next, string[] path)
        {
            if (JsonNode.DeepEquals(old, next)) return;
            if (old is JsonObject left && next is JsonObject right)
            {
                foreach (var field in left)
                    if (!right.ContainsKey(field.Key)) edits.Add(new([.. path, field.Key], "remove", null));
                foreach (var field in right)
                    Diff(left[field.Key], field.Value, [.. path, field.Key]);
            }
            else if (old is JsonArray previous && next is JsonArray current && current.Count >= previous.Count)
            {
                for (var i = 0; i < previous.Count; i++)
                    Diff(previous[i], current[i], [.. path, i.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (current.Count > previous.Count)
                    edits.Add(new(path, "append", new JsonArray(current.Skip(previous.Count).Select(n => n?.DeepClone()).ToArray())));
            }
            else if (old is JsonValue oldValue && next is JsonValue nextValue
                && oldValue.TryGetValue<string>(out var oldText) && nextValue.TryGetValue<string>(out var nextText)
                && nextText.StartsWith(oldText, StringComparison.Ordinal))
                edits.Add(new(path, "text", JsonValue.Create(nextText[oldText.Length..])));
            else edits.Add(new(path, "set", next?.DeepClone()));
        }
        Diff(JsonSerializer.SerializeToNode(before), JsonSerializer.SerializeToNode(after), []);
        return edits;
    }

    private static WorkflowRunRecord ApplyProgressChanges(WorkflowRunRecord before, IReadOnlyList<ProgressEdit> edits)
    {
        var root = JsonSerializer.SerializeToNode(before)!;
        foreach (var edit in edits)
        {
            JsonNode parent = root;
            foreach (var part in edit.Path.SkipLast(1))
                parent = parent is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)]!
                    : parent[part]!;
            var key = edit.Path[^1];
            var index = parent is JsonArray ? int.Parse(key, System.Globalization.CultureInfo.InvariantCulture) : -1;
            JsonNode? Current() => parent is JsonArray array ? array[index] : parent[key];
            if (edit.Kind == "append")
            {
                var target = (JsonArray)Current()!;
                foreach (var value in (JsonArray)edit.Value!) target.Add(value?.DeepClone());
            }
            else if (edit.Kind == "remove") ((JsonObject)parent).Remove(key);
            else
            {
                var value = edit.Kind == "text"
                    ? JsonValue.Create(Current()!.GetValue<string>() + edit.Value!.GetValue<string>())
                    : edit.Value?.DeepClone();
                if (parent is JsonArray array) array[index] = value;
                else parent[key] = value;
            }
        }
        return root.Deserialize<WorkflowRunRecord>()!;
    }

    private readonly Dictionary<string, WorkflowRunRecord> _knownProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PriorityProgress> _priorityProgress = new(StringComparer.Ordinal);
    internal string? PriorityPreparationReadError { get; private set; }
    private static WorkflowRunRecord CopyProgress(WorkflowRunRecord run)
        => JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(run))!;
    private static bool ProgressIoFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException;

    private void RememberProgress(WorkflowRunRecord run)
    {
        if (!run.NonExecutingDiagnostic)
            _knownProgress[run.RunId] = CopyProgress(run);
    }

    internal IReadOnlyList<WorkflowRunRecord> ReadPriorityPreparationRecords()
    {
        lock (_gate)
        {
            PriorityPreparationReadError = null;
            try
            {
                var current = List().ToDictionary(r => r.RunId, StringComparer.Ordinal);
                foreach (var known in _knownProgress.Values)
                    current.TryAdd(known.RunId, CopyProgress(known));
                return current.Values.ToArray();
            }
            catch (Exception ex) when (ProgressIoFailure(ex))
            {
                using var fence = PriorityOwnerFence();
                PriorityPreparationReadError = ex.Message;
                return _knownProgress.Values.Select(CopyProgress).ToArray();
            }
        }
    }

    private IDisposable? PriorityOwnerFence()
    {
        if (_requireOwnership && _owner is null)
            throw new RunRecordConflictException("原计划当前写者没有所有者能力，不能以内存记录接续。");
        return _owner?.Store.AcquireOwnerFence(_owner);
    }

    // Caller must first match this record to its still-live original runner and BGI execution identity.
    internal void RetainPriorityProgress(WorkflowRunRecord expected)
    {
        lock (_gate)
        {
            using var fence = PriorityOwnerFence();
            if (_priorityProgress.ContainsKey(expected.RunId)) return;
            if (!_knownProgress.TryGetValue(expected.RunId, out var known)
                || known.RecordRevision != expected.RecordRevision || known.StopRequested || known.IsTerminal
                || known.WireRunId != expected.WireRunId
                || JsonSerializer.Serialize(known.CurrentSubmission) != JsonSerializer.Serialize(expected.CurrentSubmission)
                || JsonSerializer.Serialize(known.Cursor) != JsonSerializer.Serialize(expected.Cursor))
                throw new RunRecordConflictException("原计划实时进度身份已改变，未建立其他运行的内存接续。");
            _priorityProgress.Add(expected.RunId, new PriorityProgress(known));
        }
    }

    internal bool HasPriorityProgress(string runId)
    {
        lock (_gate) return _priorityProgress.ContainsKey(runId);
    }

    internal string? PriorityPersistenceError(string runId)
    {
        lock (_gate) return _priorityProgress.GetValueOrDefault(runId)?.PersistenceError;
    }

    internal bool HasUnpublishedPriorityProgress(string runId)
    {
        lock (_gate) return _priorityProgress.GetValueOrDefault(runId)?.Unpublished.Count > 0;
    }
    internal long UnpublishedPriorityPayloadBytes(string runId)
    {
        lock (_gate) return _priorityProgress.GetValueOrDefault(runId)?.Unpublished.Sum(p => (long)p.PayloadBytes) ?? 0;
    }

    private void PublishPriorityProgress(PriorityProgress progress)
    {
        if (DateTimeOffset.UtcNow < progress.NextPublish) return;
        // One original CAS write per pulse: a backlog must never keep the physical owner
        // fence across the lease's renewal window. The existing heartbeat supplies later pulses.
        if (progress.Unpublished.TryPeek(out var publish))
        {
            try { publish.Publish(); }
            catch (Exception ex) when (ProgressIoFailure(ex))
            {
                progress.PersistenceError = ex.Message;
                progress.NextPublish = DateTimeOffset.UtcNow.AddSeconds(1);
                return;
            }
            progress.Unpublished.Dequeue();
        }
        progress.PersistenceError = null;
    }

    internal void FlushPriorityProgress(string runId)
    {
        lock (_gate)
        {
            using var fence = PriorityOwnerFence();
            if (!_priorityProgress.TryGetValue(runId, out var progress)) return;
            progress.NextPublish = default;
            PublishPriorityProgress(progress);
        }
    }

    internal bool FlushOneRetainedPriorityProgress()
    {
        lock (_gate)
        {
            var next = _priorityProgress.Values.Where(p => p.Unpublished.Count > 0)
                .OrderBy(p => p.NextPublish).FirstOrDefault();
            if (next is null) return false;
            using var fence = PriorityOwnerFence();
            var before = next.Unpublished.Count;
            PublishPriorityProgress(next);
            if (next.PersistenceError is null) next.NextPublish = DateTimeOffset.UtcNow;
            return next.Unpublished.Count < before;
        }
    }

    private void Persist(WorkflowRunRecord rec, int expectedRecordRevision,
        LocalNoSendProof? authorizedNoSend = null, TerminalReleaseSeal? authorizedSeal = null,
        RecoveryAssociationRecord? authorizedRecoveryAssociation = null, PreparedSendPermit? authorizedPermit = null,
        AdmissionParentSource? authorizedParent = null, RunAdmissionMapping? authorizedMapping = null,
        bool authorizedDiagnostic = false, bool? authorizedRouting = null, string? authorizedEntrySeed = null,
        ExecutionResetReleaseRecord? authorizedReset = null, PriorityProcessContinuation? authorizedProcess = null, AccountPreparationProcessContinuation? authorizedAccountPreparation = null)
    {
        lock (_gate)
        {
            if (!_priorityProgress.TryGetValue(rec.RunId, out var progress))
            {
                PersistDisk(rec, expectedRecordRevision, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation,
                    authorizedPermit, authorizedParent, authorizedMapping, authorizedDiagnostic,
                    authorizedRouting, authorizedEntrySeed, authorizedReset, authorizedProcess, authorizedAccountPreparation);
                RememberProgress(rec);
                return;
            }
            using var fence = PriorityOwnerFence();
            var current = progress.Current;
            if (authorizedDiagnostic || rec.NonExecutingDiagnostic || expectedRecordRevision == 0
                || expectedRecordRevision != current.RecordRevision || rec.RecordRevision != expectedRecordRevision
                || rec.RunId != current.RunId || rec.WireRunId != current.WireRunId
                || rec.WorkflowId != current.WorkflowId || rec.StopAuthority != current.StopAuthority)
                throw new RunRecordConflictException("原计划实时进度的身份、停止基线或修订不匹配。");
            if (current.CurrentSubmission is { } previous && rec.CurrentSubmission?.Key != previous.Key
                && !rec.SubmissionHistory.Any(s => JsonSerializer.Serialize(s) == JsonSerializer.Serialize(previous))
                && TerminalReleaseEvidence.BodySettled(current, previous))
                rec.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(previous))!);
            RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation,
                authorizedPermit, authorizedMapping, authorizedRouting, authorizedEntrySeed, authorizedReset, authorizedProcess, authorizedAccountPreparation);
            if (current.StopRequested) rec.StopRequested = true;
            if (rec.LocalWaitDecision is { } decision) rec.LocalWaitDecision = WorkflowRunner.SanitizeWaitDecision(decision);
            // Keep each exact authorized transition. Recovery of disk IO replays the original CAS writes,
            // rather than replacing a disk record with an unvalidated synthetic final snapshot.
            var changes = ProgressChanges(current, rec);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(changes).Length;
            rec.RecordRevision++;
            rec.UpdatedAt = DateTimeOffset.Now;
            progress.Current = CopyProgress(rec);
            RememberProgress(rec);
            progress.Unpublished.Enqueue(new(() =>
            {
                var publication = ApplyProgressChanges(progress.Published, changes);
                PersistDisk(publication, expectedRecordRevision, authorizedNoSend,
                    authorizedSeal, authorizedRecoveryAssociation, authorizedPermit, authorizedParent, authorizedMapping,
                    authorizedDiagnostic, authorizedRouting, authorizedEntrySeed, authorizedReset, authorizedProcess, authorizedAccountPreparation);
                progress.Published = publication;
            }, payloadBytes));
            PublishPriorityProgress(progress);
        }
    }
}
