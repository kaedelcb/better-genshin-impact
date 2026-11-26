using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>显示用房间档案白名单，与含密码及运行配置的 AssistantRoomProfile 完全分离。</summary>
public sealed class RoomFollowProfile
{
    [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = "";
    [JsonPropertyName("name"), JsonRequired] public string Name { get; set; } = "";
    [JsonPropertyName("note"), JsonRequired] public string Note { get; set; } = "";
    [JsonPropertyName("serverUrl"), JsonRequired] public string ServerUrl { get; set; } = "";
    [JsonPropertyName("roomCode"), JsonRequired] public string RoomCode { get; set; } = "";
    [JsonPropertyName("teamUids"), JsonRequired] public List<string> TeamUids { get; set; } = [];
    [JsonPropertyName("expectedHoeingPlayers"), JsonRequired] public int ExpectedHoeingPlayers { get; set; } = 4;
}

/// <summary>只读视图的完整元数据。Version 为架构版本，而非某次中继连接的事件序号。</summary>
public sealed class RoomFollowSnapshot
{
    [JsonPropertyName("version"), JsonRequired] public int Version { get; set; } = RoomFollowFormat.Version;
    [JsonPropertyName("uid"), JsonRequired] public string Uid { get; set; } = "";
    [JsonPropertyName("instanceId"), JsonRequired] public string InstanceId { get; set; } = "";
    [JsonPropertyName("machineId"), JsonRequired] public string MachineId { get; set; } = "";
    [JsonPropertyName("confirmed"), JsonRequired] public bool Confirmed { get; set; }
    [JsonPropertyName("profiles"), JsonRequired] public List<RoomFollowProfile> Profiles { get; set; } = [];
    [JsonPropertyName("activeId"), JsonRequired] public string ActiveId { get; set; } = "";
    [JsonPropertyName("viewedId"), JsonRequired] public string ViewedId { get; set; } = "";
    [JsonPropertyName("showOrchestration"), JsonRequired] public bool ShowOrchestration { get; set; }
}

internal static class RoomFollowSnapshotCodec
{
    /// <summary>立即冻结，避免调用方随后修改对象改变已入队的元数据。null 表示撤回。</summary>
    internal static string? Encode(RoomFollowSnapshot? snapshot, RoomFollowInvitation invitation)
    {
        if (snapshot is null) return null;
        try
        {
            if (Matches(snapshot, invitation))
            {
                var state = JsonSerializer.Serialize(snapshot, RoomFollowFormat.Json);
                // 同时核已冻结的结果，拒绝超限、无效或冻结过程中改变身份的输入。
                if (TryDecode(state, invitation, out _)) return state;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { }
        throw new ArgumentException("房间跟随快照超限、格式或执行端身份无效。", nameof(snapshot));
    }

    internal static bool TryDecode(string? state, RoomFollowInvitation invitation, out RoomFollowSnapshot? snapshot)
    {
        snapshot = null;
        if (state is null) return true;
        try
        {
            if (state.Length > RoomFollowFormat.MaxStateBytes || RoomFollowFormat.Utf8.GetByteCount(state) > RoomFollowFormat.MaxStateBytes)
                return false;
            using var document = JsonDocument.Parse(state, RoomFollowFormat.Document);
            var root = document.RootElement;
            if (!RoomFollowFormat.HasFields(root, ["version", "uid", "instanceId", "machineId", "confirmed", "profiles",
                    "activeId", "viewedId", "showOrchestration"])) return false;
            var profiles = root.GetProperty("profiles");
            if (profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() > 5) return false;
            foreach (var profile in profiles.EnumerateArray())
                if (!RoomFollowFormat.HasFields(profile, ["id", "name", "note", "serverUrl", "roomCode", "teamUids", "expectedHoeingPlayers"]))
                    return false;
            var decoded = root.Deserialize<RoomFollowSnapshot>(RoomFollowFormat.Json);
            if (decoded is null || !Matches(decoded, invitation)) return false;
            snapshot = decoded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    // 与服务端的 RoomFollowRelay 白名单对齐；客户端额外核对邀请中的完整执行端身份。
    private static bool Matches(RoomFollowSnapshot snapshot, RoomFollowInvitation invitation)
    {
        if (snapshot.Version != RoomFollowFormat.Version || snapshot.Uid != invitation.Uid
            || snapshot.InstanceId != invitation.InstanceId || snapshot.MachineId != invitation.MachineId
            || snapshot.Profiles is null || snapshot.Profiles.Count > 5
            || !RoomFollowFormat.IsText(snapshot.ActiveId, 512) || !RoomFollowFormat.IsText(snapshot.ViewedId, 512)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in snapshot.Profiles)
        {
            if (profile is null || !RoomFollowFormat.IsIdentity(profile.Id) || profile.Id == "__all_rooms__" || !ids.Add(profile.Id)
                || !RoomFollowFormat.IsText(profile.Name, 256) || !RoomFollowFormat.IsText(profile.Note, 8192, multiline: true)
                || !RoomFollowFormat.IsMetadataServerUrl(profile.ServerUrl) || !RoomFollowFormat.IsText(profile.RoomCode, 256)
                || profile.TeamUids is null || profile.TeamUids.Count > 64 || !profile.TeamUids.All(RoomFollowFormat.IsIdentity)
                || profile.ExpectedHoeingPlayers is < 1 or > 4) return false;
        }
        return (snapshot.ActiveId == "" || ids.Contains(snapshot.ActiveId)) && (!snapshot.Confirmed || snapshot.ActiveId != "")
            && (snapshot.ViewedId is "" or "__all_rooms__" || ids.Contains(snapshot.ViewedId));
    }
}
