using System.Collections.Concurrent;
using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly RoomOrchestrationStateStore _roomOrchestrationState = new(Path.Combine(
        AssistantDataDirectory.Root, "room-orchestration", AssistantInstanceScope.CurrentSuffix + ".json"));
    private readonly ConcurrentDictionary<string, RoomOrchestrationRoomKey> _roomOrchestrationBatchKeys = new();

    internal long RoomOrchestrationStateVersion => _roomOrchestrationState.Version;

    internal RoomOrchestrationRoomKey? GetRoomOrchestrationKey(AssistantRoomProfile? profile)
    {
        var config = _config;
        if (config is null || profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.RoomCode)
            || string.IsNullOrWhiteSpace(config.PlayerUid)
            || !RoomServerAddress.TryNormalize(config.GetRoomServerUrl(profile), out var server)) return null;
        return new(profile.Id, server, profile.RoomCode, config.PlayerUid);
    }

    internal RoomOrchestrationRunSnapshot? GetRoomOrchestrationRun(AssistantRoomProfile profile)
        => GetRoomOrchestrationKey(profile) is { } key ? _roomOrchestrationState.GetRun(key) : null;

    internal RoomOrchestrationTransitionSnapshot? GetRoomOrchestrationTransition(AssistantRoomProfile profile)
        => GetRoomOrchestrationKey(profile) is { } key ? _roomOrchestrationState.GetTransition(key) : null;

    private void RegisterRoomOrchestrationBatch(OnlineHoeingBatch batch)
    {
        if (GetRoomOrchestrationKey(_config?.ActiveRoomProfile) is not { } key || batch.SelfUid != key.PlayerUid) return;
        _roomOrchestrationBatchKeys[batch.BatchId] = key;
        // 快捷指令可以有独立协调批次，但其绑定不等于房间已保存的联机任务顺序。
        // 保留运行归属供真实原生完成动作核验，不覆盖最近一次房间联机绑定的展示。
        if (string.IsNullOrEmpty(batch.Start?.QuickCommandKey))
            _roomOrchestrationState.Begin(key, batch.BatchId, batch.Generation, batch.Bindings);
    }

    private RoomOrchestrationRoomKey? GetOrchestrationBatchKey(OnlineHoeingBatch batch)
        => _roomOrchestrationBatchKeys.GetValueOrDefault(batch.BatchId);

    private void RecordRoomOrchestrationItem(OnlineHoeingBatch batch, int index, string state, string detail)
    {
        if (_roomOrchestrationBatchKeys.TryGetValue(batch.BatchId, out var key))
            _roomOrchestrationState.SetItem(key, batch.BatchId, batch.Generation, index, state, detail);
    }

    private void RecordRoomOrchestrationPhase(OnlineHoeingBatch batch, string phase, string detail, bool settled = false)
    {
        if (_roomOrchestrationBatchKeys.TryGetValue(batch.BatchId, out var key))
        {
            _roomOrchestrationState.SetPhase(key, batch.BatchId, batch.Generation, phase, detail, settled);
            if (settled) _roomOrchestrationBatchKeys.TryRemove(batch.BatchId, out _);
        }
    }

    private void RecordRoomOrchestrationTransition(RoomOrchestrationTransitionSnapshot snapshot, bool begin = false)
        => _roomOrchestrationState.SetTransition(snapshot, begin);
}
