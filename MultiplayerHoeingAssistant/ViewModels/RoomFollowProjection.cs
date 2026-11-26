using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

// Credentials come exclusively from a uniquely matched local profile. Keep them out of the
// received metadata and its display profiles. Never use the executor's UID as the caller.
internal sealed record RoomFollowRoomCredential(string Server, string Room, string Password,
    string Uid, string Name, string InstanceId, bool BypassProxy)
{
    public override string ToString() => nameof(RoomFollowRoomCredential);
}

internal enum RoomFollowLoginDecision { Waiting, AuthenticationRequired, Connect, Current }

internal sealed class RoomFollowProjection
{
    internal AssistConfig Original { get; }
    internal AssistConfig Runtime { get; }
    internal RoomFollowSnapshot? Snapshot { get; }
    internal bool Fresh { get; private set; }
    internal IReadOnlyDictionary<string, RoomFollowRoomCredential> Credentials { get; }

    private RoomFollowProjection(AssistConfig original, AssistConfig runtime, RoomFollowSnapshot? snapshot,
        bool fresh, Dictionary<string, RoomFollowRoomCredential> credentials)
    {
        Original = original;
        Runtime = runtime;
        Snapshot = snapshot;
        Fresh = fresh;
        Credentials = credentials;
    }

    internal static bool HasObserverIdentity(AssistConfig config) => config.ObserverMode && !config.StandaloneMode
        && !string.IsNullOrWhiteSpace(config.ServerUrl) && RoomFollowFormat.IsIdentity(config.PlayerUid)
        && config.PlayerUid.Length <= 128 && !config.PlayerUid.StartsWith("web_", StringComparison.Ordinal)
        && !config.PlayerUid.StartsWith("log_viewer_", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(config.PlayerName) && RoomFollowFormat.IsIdentity(config.ClientInstanceId);

    internal static AssistConfig CopyOriginal(AssistConfig original)
        => JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.SerializeToUtf8Bytes(original))
            ?? throw new InvalidOperationException("无法复制本机房间配置。");

    internal static RoomFollowProjection Create(AssistConfig original, RoomFollowSnapshot? snapshot, bool fresh)
    {
        // Do not mutate the original profiles, selection or room settings, even on a failed login.
        var runtime = CopyOriginal(original);
        runtime.IsRoomFollowProjection = true;
        runtime.RoomProfiles = [];
        runtime.ActiveRoomProfileId = runtime.LocalRoomProfileId = "";
        runtime.ViewedRoomProfileId = snapshot?.ViewedId is { Length: > 0 } viewed
            ? viewed : AssistConfig.AllRoomsViewId;
        runtime.ShowRoomOrchestration = snapshot?.ShowOrchestration ?? original.ShowRoomOrchestration;
        var credentials = new Dictionary<string, RoomFollowRoomCredential>(StringComparer.Ordinal);
        if (snapshot is not null)
        {
            foreach (var profile in snapshot.Profiles)
            {
                // These objects are only a projection of the whitelisted metadata, with no local
                // task bindings, passwords, schedules or completion actions copied onto them.
                runtime.RoomProfiles.Add(new AssistantRoomProfile
                {
                    Id = profile.Id, Name = profile.Name, Note = profile.Note, ServerUrl = profile.ServerUrl,
                    RoomCode = profile.RoomCode, TeamUids = [.. profile.TeamUids],
                    ExpectedHoeingPlayers = profile.ExpectedHoeingPlayers
                });
                if (HasObserverIdentity(original) && RoomServerAddress.TryNormalize(profile.ServerUrl, out var server)
                    && !string.IsNullOrWhiteSpace(profile.RoomCode)
                    && snapshot.Profiles.Count(p => p.RoomCode == profile.RoomCode && RoomServerAddress.Same(p.ServerUrl, server)) == 1)
                {
                    var matches = original.RoomProfiles.Where(p => p is not null && p.RoomCode == profile.RoomCode
                        && RoomServerAddress.TryNormalize(original.GetRoomServerUrl(p), out var local) && local == server).ToArray();
                    if (matches.Length == 1 && !string.IsNullOrEmpty(matches[0].Password)
                        && (matches[0].TeamUids is not { Count: > 0 } || matches[0].TeamUids.Contains(original.PlayerUid, StringComparer.Ordinal)))
                        credentials[profile.Id] = new(server, profile.RoomCode, matches[0].Password,
                            original.PlayerUid, original.PlayerName, original.ClientInstanceId, original.BypassSystemProxy);
                }
            }
        }
        var result = new RoomFollowProjection(original, runtime, snapshot, fresh, credentials);
        result.SetActualLogin(null);
        return result;
    }

    internal string ConfirmedTargetId => Fresh && Snapshot is { Confirmed: true } snapshot ? snapshot.ActiveId : "";

    internal RoomFollowLoginDecision DecideLogin(string? actualId)
        => string.IsNullOrEmpty(ConfirmedTargetId) ? RoomFollowLoginDecision.Waiting
            : !Credentials.ContainsKey(ConfirmedTargetId) ? RoomFollowLoginDecision.AuthenticationRequired
            : actualId == ConfirmedTargetId ? RoomFollowLoginDecision.Current : RoomFollowLoginDecision.Connect;

    // The active target and the requested view are independent. Unknown rooms must never
    // enter even the ordinary secondary spectator/monitor connection pool.
    internal string[] DesiredConnections()
    {
        if (ConfirmedTargetId.Length == 0) return [];
        return Runtime.RoomProfiles.Where(p => Credentials.ContainsKey(p.Id) &&
                (p.Id == ConfirmedTargetId || !Runtime.ShowRoomOrchestration &&
                    (Runtime.ViewedRoomProfileId == AssistConfig.AllRoomsViewId || p.Id == Runtime.ViewedRoomProfileId)))
            .Select(p => p.Id).ToArray();
    }

    internal string[] AuthenticationNeededIds() => Runtime.RoomProfiles
        .Where(p => !Credentials.ContainsKey(p.Id) && (p.Id == ConfirmedTargetId || !Runtime.ShowRoomOrchestration &&
            (Runtime.ViewedRoomProfileId == AssistConfig.AllRoomsViewId || p.Id == Runtime.ViewedRoomProfileId)))
        .Select(p => p.Id).ToArray();

    internal void Invalidate()
    {
        Fresh = false;
        SetActualLogin(null);
    }

    internal void SetActualLogin(string? id)
    {
        Runtime.ActiveRoomProfileId = "";
        // Clear room-specific settings rather than applying the executor's metadata as an
        // executable configuration. The existing observer infrastructure only needs a room identity.
        new AssistantRoomProfile { ServerUrl = Original.ServerUrl }.ApplyTo(Runtime);
        if (id is not null && id == ConfirmedTargetId && Credentials.TryGetValue(id, out var credential))
        {
            Runtime.ActiveRoomProfileId = id;
            Runtime.ServerUrl = credential.Server;
            // Keep the credential in the private observation session, never in the display config.
            Runtime.ExpectedHoeingPlayers = Runtime.ActiveRoomProfile!.ExpectedHoeingPlayers;
        }
    }

    internal static RoomFollowSnapshot Capture(AssistConfig config, string machineId, bool confirmed)
    {
        var profiles = config.RoomProfiles.Where(p => p is not null).Select(p => new RoomFollowProfile
        {
            Id = p.Id, Name = p.Name, Note = p.Note, RoomCode = p.RoomCode,
            ServerUrl = RoomServerAddress.TryNormalize(p.Id == config.ActiveRoomProfileId ? config.ServerUrl
                : config.GetRoomServerUrl(p), out var server) ? server : "",
            TeamUids = (p.Id == config.ActiveRoomProfileId ? config.TeamUids : p.TeamUids)?.ToList() ?? [],
            ExpectedHoeingPlayers = p.Id == config.ActiveRoomProfileId ? config.ExpectedHoeingPlayers : p.ExpectedHoeingPlayers
        }).ToList();
        var activeId = profiles.Count(p => p.Id == config.ActiveRoomProfileId) == 1 ? config.ActiveRoomProfileId : "";
        return new RoomFollowSnapshot
        {
            Uid = config.PlayerUid, InstanceId = config.ClientInstanceId, MachineId = machineId,
            Confirmed = confirmed && activeId.Length > 0,
            Profiles = profiles, ActiveId = activeId,
            ViewedId = profiles.Any(p => p.Id == config.ViewedRoomProfileId) ? config.ViewedRoomProfileId : AssistConfig.AllRoomsViewId,
            ShowOrchestration = config.ShowRoomOrchestration
        };
    }
}
