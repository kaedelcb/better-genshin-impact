using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomBookTransferTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Entry = """
        {"name":"Imported","note":"note","serverUrl":"http://rooms.example.test/app","roomCode":"existing_Room-7","teamUids":["123456789"],"expectedHoeingPlayers":3}
        """;

    private static string Book(params string[] entries) =>
        "{\"kind\":\"nexusbgi-room-book\",\"version\":1,\"rooms\":[" + string.Join(',', entries) + "]}";

    private static string RoomEntry(string name, string server, string code, string[]? uids = null, int players = 3)
        => JsonSerializer.Serialize(new { name, note = "房间备注", serverUrl = server, roomCode = code,
            teamUids = uids ?? ["123456789"], expectedHoeingPlayers = players });

    private static AssistConfig Configuration()
    {
        var original = new AssistantRoomProfile
        {
            Id = "existing-local-id", Name = "Home", Note = "keep local note", RoomCode = "HOME01",
            ServerUrl = "https://old.example.test", Password = "local-password-secret", TeamUids = ["123456789"],
            ExpectedHoeingPlayers = 2, OnlineHoeingGroupNames = ["private-group-home"], OnlineHoeingGroupTypes = ["group"],
            OnlineHoeingCompletionPolicy = "stop", ScheduledOnlineTime = "20:30",
            ScheduledOnlineLastFireDate = new DateTime(2026, 11, 25), QuickCommands = new() { ["command"] = "private-command" }
        };
        var other = new AssistantRoomProfile
        {
            Id = "local-second-id", Name = "Other", Note = "keep second note", RoomCode = "HOME01",
            ServerUrl = "https://other.example.test", Password = "other-password-secret", TeamUids = ["123456789", "222222222"],
            ScheduledOnlineRequiresConfirmation = true, OnlineHoeingGroupNames = ["private-group-other"]
        };
        var config = new AssistConfig
        {
            PlayerUid = "123456789", PlayerName = "private-player-name", ClientInstanceId = "private-instance-id", ObserverMode = true,
            BgiPath = "private-bgi-path.exe", ActiveRoomProfileId = original.Id, LocalRoomProfileId = original.Id,
            ViewedRoomProfileId = other.Id, ShowRoomOrchestration = true, RoomProfiles = [original, other]
        };
        original.ApplyTo(config);
        return config;
    }

    [Fact]
    public void Export_UsesEffectiveActiveServerAndExactlyTheSharedMetadataFields()
    {
        var config = Configuration();
        // The live active-room address and membership can differ from the last stored summary.
        config.ServerUrl = "https://ACTIVE.example.test:443/base/hub/";
        config.TeamUids = ["123456789", "333333333"];
        config.ExpectedHoeingPlayers = 4;
        var before = JsonSerializer.Serialize(config);
        var json = RoomBookTransfer.Export(config);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(new[] { "kind", "rooms", "version" }, root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        foreach (var row in root.GetProperty("rooms").EnumerateArray())
            Assert.Equal(new[] { "expectedHoeingPlayers", "name", "note", "roomCode", "serverUrl", "teamUids" },
                row.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("existing-local-id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("local-second-id", json, StringComparison.Ordinal);
        var rooms = RoomBookTransfer.Parse(json);
        Assert.Equal(2, rooms.Count);
        Assert.Equal("https://active.example.test/base", rooms[0].ServerUrl);
        Assert.Equal(config.TeamUids.ToArray(), rooms[0].TeamUids.ToArray());
        Assert.Equal(4, rooms[0].ExpectedHoeingPlayers);
        Assert.Equal("https://other.example.test", rooms[1].ServerUrl);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("\"note\":\"note\"", "\"note\":\"note\",\"password\":\"never-import\"")]
    [InlineData("\"note\":\"note\"", "\"note\":\"note\",\"name\":\"duplicate-field\"")]
    [InlineData("\"note\":\"note\",", "")]
    [InlineData("\"note\":\"note\"", "\"note\":null")]
    [InlineData("\"name\":\"Imported\"", "\"Name\":\"Imported\"")]
    [InlineData("\"name\":\"Imported\"", "\"name\":\" \"")]
    [InlineData("http://rooms.example.test/app", "ftp://rooms.example.test/app")]
    [InlineData("http://rooms.example.test/app", "https://name:secret@rooms.example.test/app")]
    [InlineData("http://rooms.example.test/app", "https://@rooms.example.test/app")]
    [InlineData("http://rooms.example.test/app", "http://rooms.example.test?")]
    [InlineData("http://rooms.example.test/app", "http://rooms.example.test#")]
    [InlineData("existing_Room-7", "Invalid room")]
    [InlineData("existing_Room-7", " existing_Room-7")]
    [InlineData("existing_Room-7", "")]
    [InlineData("\"teamUids\":[\"123456789\"]", "\"teamUids\":[123456789]")]
    [InlineData("\"teamUids\":[\"123456789\"]", "\"teamUids\":[\"１２３\"]")]
    [InlineData("\"teamUids\":[\"123456789\"]", "\"teamUids\":[\"12345678901234567\"]")]
    [InlineData("\"expectedHoeingPlayers\":3", "\"expectedHoeingPlayers\":0")]
    [InlineData("\"expectedHoeingPlayers\":3", "\"expectedHoeingPlayers\":5")]
    [InlineData("\"expectedHoeingPlayers\":3", "\"expectedHoeingPlayers\":2.5")]
    [InlineData("\"expectedHoeingPlayers\":3", "\"expectedHoeingPlayers\":\"3\"")]
    public void InvalidEntryRejectsWholeBookWithoutInvokingPersistence(string original, string replacement)
    {
        var config = Configuration();
        var before = JsonSerializer.Serialize(config);
        var json = Book(Entry, Entry.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Merge(config, json, _ => throw new Exception("unexpected write")));
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"version\":1,\"rooms\":[],\"config\":{}}")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"kind\":\"nexusbgi-room-book\",\"version\":1,\"rooms\":[]}")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"version\":2,\"rooms\":[]}")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"version\":1,\"rooms\":null}")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"version\":\"1\",\"rooms\":[]}")]
    [InlineData("{\"kind\":\"nexusbgi-room-book\",\"version\":1,\"rooms\":[],}")]
    public void InvalidEnvelopeRejectsWholeBook(string json)
        => Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(json));

    [Fact]
    public void BoundsCountUtf8BytesAndMetadataFieldLengths()
    {
        var server = "http://rooms.example.test";
        var maxCode = new string('x', 126) + "_-";
        var maxUids = Enumerable.Repeat("1234567890123456", 64).ToArray();
        var edge = Entry.Replace("Imported", new string('名', 64)).Replace("\"note\":\"note\"", "\"note\":\"" + new string('注', 256) + "\"")
            .Replace("existing_Room-7", maxCode);
        Assert.Equal(maxCode, Assert.Single(RoomBookTransfer.Parse(Book(edge))).RoomCode);
        Assert.Equal(64, Assert.Single(RoomBookTransfer.Parse(Book(RoomEntry("Valid", server, maxCode, maxUids)))).TeamUids.Count);
        Assert.Empty(Assert.Single(RoomBookTransfer.Parse(Book(RoomEntry("Valid", server, maxCode, [])))).TeamUids);
        Assert.Equal(64, RoomBookTransfer.Parse(Book(Enumerable.Repeat(Entry, 64).ToArray())).Count);
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(Book(Enumerable.Repeat(Entry, 65).ToArray())));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(Book(RoomEntry(new string('n', 65), server, maxCode))));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(Book(Entry.Replace("\"note\":\"note\"", "\"note\":\"" + new string('n', 257) + "\""))));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(Book(RoomEntry("Room", server, maxCode + "x"))));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(Book(RoomEntry("Room", server, maxCode, [.. maxUids, "1"]))));
        var empty = Book();
        var atLimit = empty.PadRight(RoomBookTransfer.MaxUtf8Bytes, ' ');
        Assert.Empty(RoomBookTransfer.Parse(atLimit));
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(atLimit + " "));
        var multiByte = Book(Entry.Replace("\"note\":\"note\"", "\"note\":\"" + new string('注', 256) + "\""));
        multiByte = multiByte.PadRight(RoomBookTransfer.MaxUtf8Bytes - 1, ' ');
        Assert.True(Encoding.UTF8.GetByteCount(multiByte) > RoomBookTransfer.MaxUtf8Bytes);
        Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Parse(multiByte));
    }

    [Fact]
    public void Merge_SkipsExistingAndInputDuplicates_PreservesExactIdentityAndOriginalObjects()
    {
        var config = Configuration();
        var active = config.ActiveRoomProfile!;
        var other = config.RoomProfiles[1];
        var before = JsonSerializer.Serialize(config);
        var beforeActive = JsonSerializer.Serialize(active);
        var beforeOther = JsonSerializer.Serialize(other);
        var server = "https://new.example.test";
        var longName = new string('房', 64);
        var json = Book(RoomEntry("changed name", "https://OLD.example.test:443/hub/gateway/", active.RoomCode, ["1"]),
            RoomEntry("HOME", server, "mixed_Case-room", []), RoomEntry("ignored", "https://NEW.example.test/", "mixed_Case-room"),
            RoomEntry("Home", server, "MIXED_CASE-ROOM"), RoomEntry(longName, server, "Z1"), RoomEntry(longName, server, "Z2"));
        var saved = Array.Empty<AssistantRoomProfile>();
        var result = RoomBookTransfer.Merge(config, json, additions =>
        {
            Assert.Equal(before, JsonSerializer.Serialize(config));
            saved = additions.ToArray();
            return true;
        });
        Assert.Equal(4, result.ImportedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Same(active, config.ActiveRoomProfile);
        Assert.Same(active, config.LocalRoomProfile);
        Assert.Equal(other.Id, config.ViewedRoomProfileId);
        Assert.Same(other, config.RoomProfiles[1]);
        Assert.Equal(beforeActive, JsonSerializer.Serialize(active));
        Assert.Equal(beforeOther, JsonSerializer.Serialize(other));
        var unchanged = JsonSerializer.SerializeToNode(config)!.AsObject();
        var unchangedRooms = unchanged["roomProfiles"]!.AsArray();
        while (unchangedRooms.Count > 2) unchangedRooms.RemoveAt(unchangedRooms.Count - 1);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), unchanged));
        Assert.Equal("local-password-secret", config.ControlRoomPassword);
        Assert.Equal("private-group-home", Assert.Single(config.OnlineHoeingGroupNames));
        Assert.True(config.ObserverMode);
        Assert.True(config.ShowRoomOrchestration);
        var added = config.RoomProfiles.Skip(2).ToArray();
        Assert.Equal(saved, added);
        Assert.Equal(new[] { "HOME (2)", "Home (3)", longName, new string('房', 60) + " (2)" }, added.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "mixed_Case-room", "MIXED_CASE-ROOM", "Z1", "Z2" }, added.Select(p => p.RoomCode).ToArray());
        Assert.Equal(6, config.RoomProfiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(added[0].TeamUids);
        foreach (var profile in added)
        {
            Assert.True(profile.NeedsRoomPassword);
            Assert.True(profile.PreserveRoomCode);
            Assert.True(Guid.TryParseExact(profile.Id, "N", out _));
            Assert.Empty(profile.Password);
            Assert.Empty(profile.OnlineHoeingGroupNames);
            Assert.Empty(profile.ScheduledOnlineTime);
            Assert.Null(profile.ScheduledOnlineLastFireDate);
            Assert.Equal("resume", profile.OnlineHoeingCompletionPolicy);
            Assert.Equal(3, profile.ExpectedHoeingPlayers);
        }
        Assert.Equal(6, RoomBookTransfer.Merge(config, json, _ => throw new Exception("unexpected write")).SkippedCount);
        var roundTrip = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(config))!;
        Assert.True(roundTrip.RoomProfiles[2].PreserveRoomCode);
        Assert.Equal("mixed_Case-room", roundTrip.RoomProfiles[2].RoomCode);
    }

    [Theory]
    [InlineData("ABCDEF", false, false)]
    [InlineData("123456", false, false)]
    [InlineData("G12345", false, true)]
    [InlineData("Z9M2Q1", false, true)]
    [InlineData("abcdef", false, true)]
    [InlineData("custom_Room-7", false, true)]
    [InlineData("ABCDEF", true, true)]
    [InlineData("", false, false)]
    public void ExactRoomCodePreservesAssignedAndImportedCodes(string code, bool preserve, bool expected)
        => Assert.Equal(expected, RoomBookTransfer.UsesExactRoomCode(new AssistantRoomProfile
            { RoomCode = code, PreserveRoomCode = preserve }));

    [Theory]
    [InlineData("http://gateway", "http://gateway")]
    [InlineData("https://ROOMS.example.test:443/path/HUB/gateway/", "https://rooms.example.test/path")]
    [InlineData("http://127.0.0.1:8080/gateway", "http://127.0.0.1:8080")]
    public void ClipboardServerAddressMatchesBrowserBaseAddress(string input, string expected)
        => Assert.Equal(expected, Assert.Single(RoomBookTransfer.Parse(Book(RoomEntry("Room", input, "ROOM01")))).ServerUrl);

    [Fact]
    public void CapacityAndWriteFailureLeaveOriginalListAndIdentitiesUntouched()
    {
        var config = Configuration();
        while (config.RoomProfiles.Count < 63)
        {
            var code = "room-" + config.RoomProfiles.Count;
            config.RoomProfiles.Add(new() { Id = code, Name = code, ServerUrl = config.ServerUrl, RoomCode = code });
        }
        var before = JsonSerializer.Serialize(config);
        var list = config.RoomProfiles;
        var error = Assert.Throws<InvalidDataException>(() => RoomBookTransfer.Merge(config, Book(Entry,
            RoomEntry("new 2", config.ServerUrl, "New-2")), _ => throw new Exception("unexpected write")));
        Assert.Contains("64", error.Message);
        Assert.Throws<IOException>(() => RoomBookTransfer.Merge(config, Book(Entry), _ => false));
        Assert.Throws<IOException>(() => RoomBookTransfer.Merge(config, Book(Entry), _ => throw new IOException("test write failure")));
        Assert.Same(list, config.RoomProfiles);
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Equal(1, RoomBookTransfer.Merge(config, Book(Entry), _ => true).ImportedCount);
        Assert.Equal(64, config.RoomProfiles.Count);
        Assert.Equal(1, RoomBookTransfer.Merge(config, Book(Entry), _ => throw new Exception("unexpected write")).SkippedCount);
    }

    [Fact]
    public void FirstImportDoesNotAssignAnActiveOrLocalRoomOrReuseGlobalCredentials()
    {
        var config = new AssistConfig
        {
            RoomProfiles = [], PlayerUid = "123456789", PlayerName = "local nickname", ControlRoomPassword = "unrelated-password",
            ServerUrl = "https://original.example.test", TeamUids = ["123456789"], ViewedRoomProfileId = AssistConfig.AllRoomsViewId
        };
        var result = RoomBookTransfer.Merge(config, Book(Entry), _ => true);
        Assert.Equal(1, result.ImportedCount);
        Assert.Empty(config.ActiveRoomProfileId);
        Assert.Empty(config.LocalRoomProfileId);
        Assert.Empty(config.EffectiveRoomCode);
        Assert.Null(config.ActiveRoomProfile);
        Assert.Equal("https://original.example.test", config.ServerUrl);
        Assert.Equal("unrelated-password", config.ControlRoomPassword);
        Assert.Equal("local nickname", config.PlayerName);
        Assert.Equal(AssistConfig.AllRoomsViewId, config.ViewedRoomProfileId);
        Assert.True(Assert.Single(config.RoomProfiles).NeedsRoomPassword);
    }

    [ExplicitDataRootFact]
    public void LocalSetup_KeepsImportedRoomNumberAndNormalDraftRules()
    {
        var config = Configuration();
        RoomBookTransfer.Merge(config, Book(Entry), _ => true);
        var target = config.RoomProfiles[^1];
        var vm = new MainViewModel();
        var validate = typeof(MainViewModel).GetMethod("ValidateAssistantRoomDraft", Flags)!;
        var draft = new AssistantRoomDraft(target.Name, target.Note, target.ServerUrl, target.RoomCode, "", ["123456789"], 3);
        var rejected = Assert.Throws<TargetInvocationException>(() => validate.Invoke(vm, [config, target, draft, false]));
        Assert.Contains("密码", rejected.InnerException!.Message);
        draft = draft with { Password = "normal-room-password", MemberUids = ["111111111"] };
        rejected = Assert.Throws<TargetInvocationException>(() => validate.Invoke(vm, [config, target, draft, false]));
        Assert.Contains("UID", rejected.InnerException!.Message);
        draft = draft with { MemberUids = ["123456789", "111111111"] };
        var result = ((string Label, string Server, string Code, bool Active, int Index))validate.Invoke(vm, [config, target, draft, false])!;
        Assert.Equal("existing_Room-7", result.Code);
        // New ordinary profiles still require a six-character room number (and a unique name).
        var normalDraft = draft with { Name = "new ordinary" };
        rejected = Assert.Throws<TargetInvocationException>(() => validate.Invoke(vm, [config, null, normalDraft, false]));
        Assert.Contains("6 位", rejected.InnerException!.Message);
        result = ((string Label, string Server, string Code, bool Active, int Index))validate.Invoke(vm, [config, null, normalDraft with { RoomCode = "abcd12" }, false])!;
        Assert.Equal("ABCD12", result.Code);
    }

    [ExplicitDataRootFact]
    public async Task ImportThroughViewModelPersistsOnlyAdditions_AndMissingPasswordCannotSwitch()
    {
        var config = Configuration();
        // Exercise the normal disk writer in an isolated folder and suppress real connections.
        var root = Path.Combine(AssistantDataDirectory.Root, "room-book-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manager = new AssistConfigManager();
            typeof(AssistConfigManager).GetField("_configPath", Flags)!.SetValue(manager, Path.Combine(root, "assistant-config.json"));
            Assert.True(manager.TrySave(config));
            var raw = manager.ReadRawJson();
            var saved = JsonSerializer.Deserialize<AssistConfig>(raw)!;
            // The latest on-disk task setting must not be restored from an older running copy.
            saved.RoomProfiles[1].OnlineHoeingGroupNames = ["changed-by-other-config-writer"];
            Assert.True(manager.TrySave(saved));
            var vm = new MainViewModel();
            typeof(MainViewModel).GetField("_config", Flags)!.SetValue(vm, config);
            typeof(MainViewModel).GetField("_configManager", Flags)!.SetValue(vm, manager);
            vm.RoomCode = config.ActiveRoomProfile!.RoomCode;
            var connectionAttempts = 0;
            vm.RoomSpectatorFactory = (_, _, _) => { connectionAttempts++; throw new InvalidOperationException("unexpected connection"); };
            var merge = typeof(MainViewModel).GetMethod("MergeAssistantRoomBook", Flags)!;
            var beforeSave = manager.ReadRawJson();
            manager.SetConfigTransferActive(true);
            Assert.Throws<TargetInvocationException>(() => merge.Invoke(vm, [config, Book(Entry)]));
            Assert.Equal(2, config.RoomProfiles.Count);
            Assert.Equal(beforeSave, manager.ReadRawJson());
            manager.SetConfigTransferActive(false);
            var active = config.ActiveRoomProfile;
            var profiles = (RoomBookMergeResult)merge.Invoke(vm, [config, Book(Entry)])!;
            Assert.Equal(1, profiles.ImportedCount);
            Assert.Equal(0, connectionAttempts);
            Assert.Same(active, config.ActiveRoomProfile);
            Assert.Equal(config.ActiveRoomProfile!.RoomCode, vm.RoomCode);
            Assert.Equal("local-second-id", vm.ViewedRoomProfileId);
            var onDisk = JsonNode.Parse(manager.ReadRawJson())!.AsObject();
            var before = JsonNode.Parse(beforeSave)!.AsObject();
            var roomNode = onDisk["roomProfiles"]!.AsArray();
            var imported = roomNode[2]!.Deserialize<AssistantRoomProfile>()!;
            Assert.Equal(config.RoomProfiles[^1].Id, imported.Id);
            Assert.True(imported.NeedsRoomPassword);
            roomNode.RemoveAt(2);
            Assert.True(JsonNode.DeepEquals(before, onDisk));
            var read = manager.Load();
            Assert.Equal("changed-by-other-config-writer", Assert.Single(read.RoomProfiles[1].OnlineHoeingGroupNames));
            Assert.Equal("local-password-secret", read.ControlRoomPassword);
            var oldRoom = vm.RoomCode;
            bool sourceVerificationCalled = false;
            var switchResult = await vm.SwitchAssistantRoomForFlowAsync(imported.Id, _ =>
            {
                sourceVerificationCalled = true;
                throw new InvalidOperationException("unexpected leave validation");
            }, CancellationToken.None);
            Assert.False(switchResult.Success);
            Assert.False(switchResult.Unknown);
            Assert.Contains("需填写密码", switchResult.Message);
            Assert.False(sourceVerificationCalled);
            Assert.Same(active, config.ActiveRoomProfile);
            Assert.Equal(oldRoom, vm.RoomCode);
            config.ShowRoomOrchestration = false;
            vm.SelectViewedRoom(imported.Id, persist: false);
            Assert.Equal(imported.Id, vm.ViewedRoomProfileId);
            Assert.Equal(0, connectionAttempts);
            Assert.Contains("需填写密码", vm.RoomViewNoticeText);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
