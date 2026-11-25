using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record RoomOrchestrationRoomKey(string ProfileId, string ServerUrl, string RoomCode, string PlayerUid);
internal sealed record RoomOrchestrationTaskStatus(string Name, string Type, string State, string Detail = "");
internal sealed record RoomOrchestrationRunSnapshot(RoomOrchestrationRoomKey Room, string BatchId, int Generation,
    RoomOrchestrationTaskStatus[] Tasks, string Phase, string Message, DateTimeOffset UpdatedAt, bool Settled = false);
internal sealed record RoomOrchestrationTransitionSnapshot(RoomOrchestrationRoomKey Source, string IntentId,
    RoomSwitchCompletion Target, string SwitchState, string PostState, string Message, DateTimeOffset UpdatedAt,
    string? JobId = null, string? RunId = null, bool OnlineConfirmed = false,
    string PostTaskType = "", string PostTaskName = "", string SourceBatchId = "", int SourceGeneration = 0, long Revision = 0);

/// <summary>每个房间最近一次本机执行与接续的只读展示证据，不是恢复或启动授权。</summary>
internal sealed class RoomOrchestrationStateStore(string path)
{
    private sealed record SavedState(RoomOrchestrationRunSnapshot[] Runs, RoomOrchestrationTransitionSnapshot[] Transitions);
    private readonly object _gate = new();
    private readonly Dictionary<RoomOrchestrationRoomKey, RoomOrchestrationRunSnapshot> _runs = new();
    private readonly Dictionary<RoomOrchestrationRoomKey, RoomOrchestrationTransitionSnapshot> _transitions = new();
    private bool _loaded;
    private long _version;
    internal long Version => Interlocked.Read(ref _version);

    internal RoomOrchestrationRunSnapshot? GetRun(RoomOrchestrationRoomKey key)
    {
        lock (_gate) { Load(); return _runs.GetValueOrDefault(key); }
    }

    internal RoomOrchestrationTransitionSnapshot? GetTransition(RoomOrchestrationRoomKey key)
    {
        lock (_gate) { Load(); return _transitions.GetValueOrDefault(key); }
    }

    internal void Begin(RoomOrchestrationRoomKey key, string batchId, int generation,
        IReadOnlyList<CoordinatedHoeingBinding> bindings)
    {
        lock (_gate)
        {
            Load();
            if (_runs.TryGetValue(key, out var previous) && previous.BatchId == batchId
                && previous.Generation == generation) return; // Resume never repaints old evidence by list index.
            _runs[key] = new(key, batchId, generation,
                bindings.Select(x => new RoomOrchestrationTaskStatus(x.Name, x.Type, "pending")).ToArray(),
                "preparing", "正在准备联机批次", DateTimeOffset.Now);
            Save();
        }
    }

    internal void SetItem(RoomOrchestrationRoomKey key, string batchId, int generation, int index,
        string state, string detail)
    {
        lock (_gate)
        {
            Load();
            if (!_runs.TryGetValue(key, out var old) || old.BatchId != batchId || old.Generation != generation
                || old.Settled || index < 0 || index >= old.Tasks.Length) return;
            var previous = old.Tasks[index];
            if (previous.State == state && previous.Detail == detail) return;
            var items = (RoomOrchestrationTaskStatus[])old.Tasks.Clone();
            items[index] = previous with { State = state, Detail = detail };
            _runs[key] = old with { Tasks = items, UpdatedAt = DateTimeOffset.Now };
            Save();
        }
    }

    internal void SetPhase(RoomOrchestrationRoomKey key, string batchId, int generation,
        string phase, string message, bool settled = false)
    {
        lock (_gate)
        {
            Load();
            if (!_runs.TryGetValue(key, out var old) || old.BatchId != batchId || old.Generation != generation
                || old.Settled) return;
            if (old.Phase == phase && old.Message == message && old.Settled == settled) return;
            var tasks = settled ? old.Tasks.Select(t => t.State is "running" or "queued" or "waiting"
                ? t with { State = phase is "cancelled" or "stopped" ? "cancelled" : "unknown", Detail = message }
                : t).ToArray() : old.Tasks;
            _runs[key] = old with { Tasks = tasks, Phase = phase, Message = message, Settled = settled, UpdatedAt = DateTimeOffset.Now };
            Save();
        }
    }

    internal void SetTransition(RoomOrchestrationTransitionSnapshot value, bool begin = false)
    {
        lock (_gate)
        {
            Load();
            // Only explicit registration may replace a previous transition. Late observations never do.
            if (_transitions.TryGetValue(value.Source, out var old))
            {
                if (old.IntentId != value.IntentId && !begin) return;
                if (old.IntentId == value.IntentId && !begin && (value.Revision < old.Revision
                    || value.Revision == old.Revision && value.UpdatedAt < old.UpdatedAt)) return;
            }
            if (old == value) return;
            _transitions[value.Source] = value;
            Save();
        }
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path));
            if (saved is null) return;
            foreach (var run in (saved.Runs ?? []).Where(x => x?.Room is not null && x.Tasks is not null).TakeLast(20))
                _runs[run.Room] = run.Settled ? run : run with
                {
                    Phase = "unknown", Message = "重启后原批次状态待核对",
                    Tasks = run.Tasks.Select(t => t.State is "running" or "waiting" or "queued"
                        ? t with { State = "unknown", Detail = "重启后待核对" } : t).ToArray()
                };
            foreach (var transition in (saved.Transitions ?? []).Where(x => x?.Source is not null && x.Target is not null).TakeLast(20))
            {
                var switchState = transition.SwitchState is "waiting" or "switching" ? "unknown" : transition.SwitchState;
                var postState = transition.PostState is "pending" or "queued" or "running" ? "unknown" : transition.PostState;
                _transitions[transition.Source] = transition with
                {
                    SwitchState = switchState, PostState = postState,
                    Message = switchState == "unknown" || postState == "unknown" ? "重启后原接续状态待核对" : transition.Message
                };
            }
        }
        catch (Exception) { /* A display cache is not an execution authority. Keep existing configuration usable. */ }
    }

    private void Save()
    {
        Interlocked.Increment(ref _version);
        try
        {
            // Bounded latest evidence, rather than a second history system. Retain room identity in every record.
            foreach (var key in _runs.OrderByDescending(p => p.Value.UpdatedAt).Skip(20).Select(p => p.Key).ToArray()) _runs.Remove(key);
            foreach (var key in _transitions.OrderByDescending(p => p.Value.UpdatedAt).Skip(20).Select(p => p.Key).ToArray()) _transitions.Remove(key);
            LocalPeerSyncService.WriteFileAtomic(path, JsonSerializer.Serialize(new SavedState(_runs.Values.ToArray(), _transitions.Values.ToArray())));
        }
        catch (Exception) { /* A missing display-cache write must not change game task execution. */ }
    }
}
