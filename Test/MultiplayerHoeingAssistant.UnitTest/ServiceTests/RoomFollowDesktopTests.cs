using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("MultiRoomWpf")]
public sealed class RoomFollowDesktopTests
{
    private const string LocalUid = "123456789";
    private const string LocalInstance = "monitor-fixture";
    private const string ServerA = "https://rooms-a.example.test";
    private const string ServerB = "https://rooms-b.example.test";

    [Fact]
    public void MetadataProjectionDoesNotChangeOriginalIdentityCredentialsOrTaskSettings()
    {
        var original = Original();
        var saved = JsonSerializer.Serialize(original);
        var source = Snapshot();
        var projection = RoomFollowProjection.Create(original, source, fresh: true);
        var runtime = projection.Runtime;
        Assert.True(runtime.IsRoomFollowProjection);
        Assert.True(runtime.ObserverMode);
        Assert.Equal(LocalUid, runtime.PlayerUid);
        Assert.Equal(LocalInstance, runtime.ClientInstanceId);
        Assert.Equal("source-b", runtime.ViewedRoomProfileId);
        Assert.True(runtime.ShowRoomOrchestration);
        Assert.Equal(new[] { "source-a", "source-b", "unknown" }, runtime.RoomProfiles.Select(p => p.Id));
        Assert.Equal("执行档案乙", runtime.RoomProfiles[1].Name);
        Assert.Equal("显示备注", runtime.RoomProfiles[1].Note);
        Assert.Equal(3, runtime.RoomProfiles[1].ExpectedHoeingPlayers);
        Assert.All(runtime.RoomProfiles, p =>
        {
            Assert.Empty(p.Password);
            Assert.Empty(p.OnlineHoeingGroupNames);
            Assert.Empty(p.ScheduledOnlineTime);
        });
        Assert.Empty(runtime.ActiveRoomProfileId); // actual observer login is not yet confirmed
        Assert.Empty(runtime.ControlRoomPassword);
        Assert.Equal(RoomFollowLoginDecision.Connect, projection.DecideLogin(runtime.ActiveRoomProfileId));
        projection.SetActualLogin("source-b");
        Assert.Equal("source-b", runtime.ActiveRoomProfileId);
        Assert.Equal("BBBBBB", runtime.EffectiveRoomCode);
        Assert.True(RoomServerAddress.Same(ServerB, runtime.ServerUrl));
        Assert.Empty(runtime.ControlRoomPassword);
        Assert.True(original.RoomProfiles[1].Password == projection.Credentials["source-b"].Password);
        Assert.Equal(RoomFollowLoginDecision.Current, projection.DecideLogin(runtime.ActiveRoomProfileId));
        Assert.Empty(runtime.OnlineHoeingGroupNames);
        runtime.RoomProfiles[1].Name = "仅修改运行副本";
        runtime.TeamUids.Add("other-fixture");
        runtime.ShowRoomOrchestration = false;
        runtime.ViewedRoomProfileId = AssistConfig.AllRoomsViewId;
        projection.SetActualLogin(null);
        Assert.Empty(runtime.EffectiveRoomCode);
        Assert.Empty(runtime.ControlRoomPassword);
        // The controller restores this same untouched original instead of saving the projection.
        Assert.Same(original, projection.Original);
        Assert.Equal("local-a", projection.Original.ActiveRoomProfileId);
        Assert.Equal("local-a", projection.Original.ViewedRoomProfileId);
        Assert.False(projection.Original.ShowRoomOrchestration);
        Assert.True(saved == JsonSerializer.Serialize(projection.Original));
    }

    [Fact]
    public void FrozenRestoreCopyIsIndependentOfCallbacksHoldingTheOldRuntimeObject()
    {
        var old = Original();
        var frozen = RoomFollowProjection.CopyOriginal(old);
        var serialized = JsonSerializer.Serialize(frozen);
        old.IsRoomFollowProjection = true;
        old.RoomProfiles[0].Name = "不应覆盖恢复档案";
        old.RoomProfiles[0].Password = Guid.NewGuid().ToString("N");
        old.ActiveRoomProfileId = old.ViewedRoomProfileId = "local-b";
        old.ShowRoomOrchestration = true;
        Assert.False(frozen.IsRoomFollowProjection);
        Assert.Equal("local-a", frozen.ActiveRoomProfileId);
        Assert.Equal("local-a", frozen.ViewedRoomProfileId);
        Assert.False(frozen.ShowRoomOrchestration);
        Assert.True(serialized == JsonSerializer.Serialize(frozen));
    }

    [Fact]
    public void NoAutomaticAuthenticationForUnknownServerRoomOrAmbiguousLocalCredentials()
    {
        var original = Original();
        var source = Snapshot();
        source.ActiveId = "unknown";
        source.ViewedId = AssistConfig.AllRoomsViewId;
        source.ShowOrchestration = false;
        var projection = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.False(projection.Credentials.ContainsKey("unknown")); // same code as A, different server
        Assert.Equal(RoomFollowLoginDecision.AuthenticationRequired, projection.DecideLogin(null));
        projection.SetActualLogin("unknown");
        Assert.Empty(projection.Runtime.EffectiveRoomCode);
        Assert.Empty(projection.Runtime.ControlRoomPassword);
        Assert.Equal(new[] { "source-a", "source-b" }, projection.DesiredConnections());
        Assert.Equal(new[] { "unknown" }, projection.AuthenticationNeededIds());

        // A remote numeric UID or roster cannot supply a login identity or override the local account.
        foreach (var credential in projection.Credentials.Values)
        {
            Assert.Equal(LocalUid, credential.Uid);
            Assert.Equal(LocalInstance, credential.InstanceId);
        }
        original.RoomProfiles.Add(new AssistantRoomProfile { Id = "duplicate", ServerUrl = ServerA,
            RoomCode = "AAAAAA", Password = Guid.NewGuid().ToString("N"), TeamUids = [LocalUid] });
        var ambiguous = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.False(ambiguous.Credentials.ContainsKey("source-a"));
        Assert.Equal(new[] { "source-b" }, ambiguous.DesiredConnections());
        original.RoomProfiles[1].TeamUids = ["987654321"]; // local account hasn't been authorized in its own saved profile
        Assert.Empty(RoomFollowProjection.Create(original, source, fresh: true).DesiredConnections());
    }

    [Fact]
    public void OnlyConfirmedCurrentTargetAndRequestedKnownViewsCanConnect()
    {
        var original = Original();
        var source = Snapshot();
        source.ShowOrchestration = false;
        source.ViewedId = AssistConfig.AllRoomsViewId;
        var all = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.Equal(AssistConfig.AllRoomsViewId, all.Runtime.ViewedRoomProfileId);
        Assert.Equal(new[] { "source-a", "source-b" }, all.DesiredConnections());

        var stale = RoomFollowProjection.Create(original, source, fresh: false);
        Assert.Equal(RoomFollowLoginDecision.Waiting, stale.DecideLogin("source-b"));
        stale.SetActualLogin("source-b");
        Assert.Empty(stale.Runtime.ActiveRoomProfileId);
        Assert.Empty(stale.DesiredConnections());
        source.Confirmed = false;
        var switching = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.Equal(RoomFollowLoginDecision.Waiting, switching.DecideLogin(null));
        Assert.Empty(switching.DesiredConnections());

        source.Confirmed = true;
        source.ActiveId = "source-a";
        source.ViewedId = "source-b";
        var moved = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.Equal(new[] { "source-a", "source-b" }, moved.DesiredConnections());
        Assert.Equal(RoomFollowLoginDecision.Connect, moved.DecideLogin("source-b"));
        moved.SetActualLogin("source-b");
        Assert.Empty(moved.Runtime.ActiveRoomProfileId);
        moved.SetActualLogin("source-a");
        Assert.Equal("AAAAAA", moved.Runtime.EffectiveRoomCode);
        Assert.Equal("source-b", moved.Runtime.ViewedRoomProfileId);

        source.ShowOrchestration = true;
        var graph = RoomFollowProjection.Create(original, source, fresh: true);
        Assert.True(graph.Runtime.ShowRoomOrchestration);
        Assert.Equal(new[] { "source-a" }, graph.DesiredConnections());
        Assert.Equal("source-b", graph.Runtime.ViewedRoomProfileId);
    }

    [Fact]
    public void FollowCaptureDoesNotPublishRoomPasswordOrRuntimeConfiguration()
    {
        var executor = Original();
        executor.ObserverMode = false;
        executor.RoomProfiles[0].ServerUrl = ServerA + "/hub/";
        executor.ServerUrl = ServerA + "/hub/";
        var state = RoomFollowProjection.Capture(executor, "executor-machine", confirmed: true);
        Assert.True(state.Confirmed);
        Assert.Equal("local-a", state.ActiveId);
        Assert.Equal("local-a", state.ViewedId);
        Assert.True(RoomServerAddress.Same(ServerA, state.Profiles[0].ServerUrl));
        var json = JsonSerializer.Serialize(state);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onlineHoeing", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(json.Contains(executor.ControlRoomPassword, StringComparison.Ordinal));
        Assert.False(json.Contains(executor.RoomProfiles[1].Password, StringComparison.Ordinal));
        executor.ActiveRoomProfileId = "removed";
        Assert.False(RoomFollowProjection.Capture(executor, "executor-machine", confirmed: true).Confirmed);
    }

    [Fact]
    public void TemporaryConfigCannotBeSavedEvenAfterFollowWriterFenceIsReleased()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "配置测试必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var manager = new AssistConfigManager();
        var original = Original();
        var projection = RoomFollowProjection.Create(original, Snapshot(), fresh: true);
        projection.SetActualLogin("source-b");
        var before = manager.ReadRawJson();
        manager.SetRoomFollowProjectionActive(true);
        try
        {
            Assert.False(manager.TrySave(projection.Runtime));
            Assert.False(manager.TrySave(original));
            Assert.False(manager.WriteRawJson(JsonSerializer.Serialize(projection.Runtime)));
            original.IsRoomFollowProjection = true; // Retired by the follow activation boundary.
        }
        finally { manager.SetRoomFollowProjectionActive(false); }
        Assert.False(manager.TrySave(projection.Runtime)); // a stale in-memory save is still refused
        Assert.False(manager.TrySave(original));
        Assert.True(before == manager.ReadRawJson());
    }

    [Fact]
    public void FollowPreferencesAreOptInAndProtectedSeparatelyFromSavedRoomConfiguration()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "配置测试必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var root = Path.Combine(AssistantDataDirectory.Root, "room-follow-desktop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "follow.protected");
            var store = new RoomFollowPreferencesStore(path);
            Assert.Equal(new RoomFollowPreferences(), store.Load());
            var invitation = RoomFollowGrant.Create("", true, "fixture-executor", "fixture-instance", "fixture-machine").Invitation.Encode();
            store.Save(new(FollowEnabled: true, Invitation: invitation));
            var prefs = store.Load();
            Assert.True(prefs.FollowEnabled);
            Assert.False(prefs.ShareEnabled);
            Assert.True(prefs.Invitation == invitation);
            Assert.False(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(invitation, StringComparison.Ordinal));
            store.Save(prefs with { FollowEnabled = false });
            Assert.False(store.Load().FollowEnabled);
            Assert.True(store.Load().Invitation == invitation);
            Assert.False(File.Exists(Path.Combine(root, "assistant-config.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static AssistConfig Original()
    {
        var a = new AssistantRoomProfile { Id = "local-a", Name = "本机甲", RoomCode = "AAAAAA", ServerUrl = ServerA,
            Password = Guid.NewGuid().ToString("N"), TeamUids = [LocalUid], ScheduledOnlineTime = "10:30", OnlineHoeingGroupNames = ["甲任务"] };
        var b = new AssistantRoomProfile { Id = "local-b", Name = "本机乙", RoomCode = "BBBBBB", ServerUrl = ServerB,
            Password = Guid.NewGuid().ToString("N"), TeamUids = [LocalUid], ScheduledOnlineTime = "17:15", OnlineHoeingGroupNames = ["乙任务"] };
        var config = new AssistConfig { PlayerUid = LocalUid, PlayerName = "测试监控端", ClientInstanceId = LocalInstance,
            ObserverMode = true, RoomProfiles = [a, b], ActiveRoomProfileId = a.Id, LocalRoomProfileId = a.Id,
            ViewedRoomProfileId = a.Id, ShowRoomOrchestration = false };
        a.ApplyTo(config);
        return config;
    }

    private static RoomFollowSnapshot Snapshot() => new()
    {
        Uid = "987654321", InstanceId = "executor-fixture", MachineId = "executor-machine", Confirmed = true,
        ActiveId = "source-b", ViewedId = "source-b", ShowOrchestration = true,
        Profiles = [
            new() { Id = "source-a", Name = "执行档案甲", ServerUrl = ServerA, RoomCode = "AAAAAA", TeamUids = ["987654321"] },
            new() { Id = "source-b", Name = "执行档案乙", Note = "显示备注", ServerUrl = ServerB, RoomCode = "BBBBBB", ExpectedHoeingPlayers = 3 },
            new() { Id = "unknown", Name = "陌生服务器", ServerUrl = "https://unrecognized.example.test", RoomCode = "AAAAAA" }
        ]
    };
}
