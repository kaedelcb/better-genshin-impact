using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>冻结的后继任务授权；独立于原联机接管票据，并且只对已确认的新房间有效。</summary>
internal sealed record RoomOrchestrationLaunch(string IntentId, TaskConflictPolicySettings Policy,
    BgiEpoch Epoch, long StopVersion, long IntentTimestamp, string RoomIdentity);
