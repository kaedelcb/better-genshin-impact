using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>房间级完成动作的冻结去向；显示名称不参与房间身份判断。</summary>
public sealed record RoomSwitchCompletion(
    string SourceProfileId = "", string SourceServerUrl = "", string SourceRoomCode = "",
    string TargetProfileId = "", string TargetServerUrl = "", string TargetRoomCode = "",
    string TargetName = "", bool RunAfterSwitch = false)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(SourceProfileId)
        && !string.IsNullOrWhiteSpace(TargetProfileId) && SourceProfileId != TargetProfileId
        && !string.IsNullOrWhiteSpace(SourceRoomCode) && !string.IsNullOrWhiteSpace(TargetRoomCode)
        && RoomServerAddress.TryNormalize(SourceServerUrl, out _)
        && RoomServerAddress.TryNormalize(TargetServerUrl, out _)
        && !(RoomServerAddress.Same(SourceServerUrl, TargetServerUrl) && SourceRoomCode == TargetRoomCode);

    internal bool MatchesSource(AssistConfig config) => IsValid
        && MatchesProfile(config, SourceProfileId, SourceServerUrl, SourceRoomCode);

    internal bool MatchesTarget(AssistConfig config) => IsValid
        && MatchesProfile(config, TargetProfileId, TargetServerUrl, TargetRoomCode);

    private static bool MatchesProfile(AssistConfig config, string id, string server, string roomCode)
    {
        var profiles = config.RoomProfiles.Where(p => p.Id == id).ToArray();
        return profiles.Length == 1 && profiles[0].RoomCode == roomCode
            && RoomServerAddress.Same(config.GetRoomServerUrl(profiles[0]), server);
    }

    internal static IReadOnlyList<RoomSwitchCompletion> Choices(AssistConfig config, string sourceId)
    {
        var sources = config.RoomProfiles.Where(p => p.Id == sourceId).ToArray();
        if (sources.Length != 1 || !RoomServerAddress.TryNormalize(config.GetRoomServerUrl(sources[0]), out var sourceServer))
            return [];
        var source = sources[0];
        var result = new List<RoomSwitchCompletion>();
        foreach (var target in config.RoomProfiles)
        {
            if (target.Id == source.Id || !RoomServerAddress.TryNormalize(config.GetRoomServerUrl(target), out var targetServer)) continue;
            var choice = new RoomSwitchCompletion(source.Id, sourceServer, source.RoomCode,
                target.Id, targetServer, target.RoomCode, target.Name);
            if (choice.IsValid && choice.MatchesTarget(config)) result.Add(choice);
        }
        return result;
    }
}
