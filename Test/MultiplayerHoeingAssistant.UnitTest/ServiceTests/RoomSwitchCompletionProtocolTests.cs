using System.IO;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// These tests distinguish acknowledged completion settings from merely reading a new
// status. They deliberately exercise the same DTO/revision used at Start admission.
public sealed class RoomSwitchCompletionProtocolTests
{
    private static readonly RoomSwitchCompletion Route = new("a", "https://source.example", "AAA",
        "b", "https://target.example", "BBB", "乙房", true);

    private static TaskConflictPolicySettings Settings(RoomSwitchCompletion? room = null) => new()
    {
        Policy = TaskConflictPolicy.Stop, RoomSwitch = room ?? Route, SpecifiedTaskType = "workflow",
        SpecifiedTaskName = "乙上线前准备", SpecifiedTaskPriority = 13, SpecifiedStartMode = "node",
        SpecifiedStartTaskId = "node-b", SpecifiedStartTaskName = "准备", SpecifiedConfigRevision = "rev-1",
        SpecifiedWorkflowId = "workflow-b"
    };

    private static AssistConfig Config() => new()
    {
        ActiveRoomProfileId = "a", ServerUrl = Route.SourceServerUrl,
        RoomProfiles = [
            new() { Id = "a", RoomCode = Route.SourceRoomCode, ServerUrl = Route.SourceServerUrl, Password = "source-password" },
            new() { Id = "b", RoomCode = Route.TargetRoomCode, ServerUrl = Route.TargetServerUrl, Name = Route.TargetName, Password = "target-password" }
        ]
    };

    [Fact]
    public void ActionRevisionCoversFullEffectiveSwitchWithoutSerializingCredentials()
    {
        var settings = Settings();
        var config = Config();
        var action = CoordinatedCompletionAction.FromSettings(settings, config);
        Assert.True(action.IsValid());
        Assert.Equal("switchRoom", action.Policy);
        Assert.Contains("乙房", action.Description);
        Assert.Contains("乙上线前准备", action.Description);
        Assert.Contains("准备", action.Description);
        Assert.Contains("13", action.Description);
        var raw = JsonSerializer.Serialize(action);
        Assert.DoesNotContain("source-password", raw);
        Assert.DoesNotContain("target-password", raw);
        Assert.DoesNotContain(Route.SourceServerUrl, raw);
        Assert.DoesNotContain(Route.TargetServerUrl, raw);
        Assert.Equal(action, JsonSerializer.Deserialize<CoordinatedCompletionAction>(raw));

        // Names are presentation; address aliases representing the same server are normalized.
        var renamed = Settings(Route with { SourceServerUrl = "https://source.example/hub/", TargetName = "乙房改名" });
        Assert.True(CoordinatedCompletionAction.MatchesRoomSwitchConfirmation(action,
            CoordinatedCompletionAction.FromSettings(renamed)));
        config.RoomProfiles[1].Name = "乙房新的显示名称";
        var latest = CoordinatedCompletionAction.FromSettings(settings, config);
        Assert.Equal("乙房新的显示名称", latest.TargetName);
        Assert.Equal(action.ActionRevision, latest.ActionRevision);

        // Every consumed field, including source/target stable IDs and resolved addresses,
        // contributes to the revision even when the visible room/task name is unchanged.
        Action<TaskConflictPolicySettings>[] mutations = [
            s => s.RoomSwitch = Route with { SourceProfileId = "replacement-a" },
            s => s.RoomSwitch = Route with { SourceRoomCode = "AAA-2" },
            s => s.RoomSwitch = Route with { SourceServerUrl = "https://other-source.example" },
            s => s.RoomSwitch = Route with { TargetProfileId = "replacement-b" },
            s => s.RoomSwitch = Route with { TargetServerUrl = "https://other-target.example" },
            s => s.RoomSwitch = Route with { TargetRoomCode = "CCC" },
            s => s.RoomSwitch = Route with { RunAfterSwitch = false },
            s => { s.SpecifiedTaskType = "group"; s.SpecifiedWorkflowId = ""; },
            s => s.SpecifiedTaskName = "另一任务",
            s => s.SpecifiedTaskPriority = 14,
            s => { s.SpecifiedStartMode = "beginning"; s.SpecifiedStartTaskId = s.SpecifiedStartTaskName = ""; },
            s => s.SpecifiedStartTaskId = "node-c",
            s => s.SpecifiedStartTaskName = "另一节点",
            s => s.SpecifiedConfigRevision = "rev-2",
            s => s.SpecifiedWorkflowId = "workflow-c"
        ];
        foreach (var mutation in mutations)
        {
            var changed = Settings();
            mutation(changed);
            var dto = CoordinatedCompletionAction.FromSettings(changed);
            Assert.True(dto.IsValid());
            Assert.NotEqual(action.ActionRevision, dto.ActionRevision);
            Assert.False(CoordinatedCompletionAction.MatchesRoomSwitchConfirmation(action, dto));
        }
    }

    [Fact]
    public void StandbyIgnoresUnusedTaskFieldsAndAnInvalidDestinationRemainsAnInvalidSwitch()
    {
        var settings = Settings(Route with { RunAfterSwitch = false });
        var before = CoordinatedCompletionAction.FromSettings(settings);
        settings.SpecifiedConfigRevision = "unused";
        settings.SpecifiedTaskName = "不应执行";
        Assert.Equal(before, CoordinatedCompletionAction.FromSettings(settings));
        Assert.Contains("待命", before.Description);
        var config = Config();
        // The profile kept its ID but now points to another server: no silent retarget.
        config.RoomProfiles[1].ServerUrl = "https://replacement.example";
        var invalid = CoordinatedCompletionAction.FromSettings(settings, config);
        Assert.Equal("switchRoom", invalid.Policy);
        Assert.False(invalid.IsValid());
        Assert.Contains("无效", invalid.Description);
    }

    [Theory]
    [InlineData("manual", true)]
    [InlineData("online", false)]
    public void ExecutionSideMatchesTheAdmissionSnapshotAndRefusesUnconfirmedSwitch(string source, bool useCompletionActions)
    {
        var action = CoordinatedCompletionAction.FromSettings(Settings());
        var start = new CoordinatedBatchSnapshot("batch", 1, 0, 0, 0, "registering", "")
        {
            Source = source, UseCompletionActions = useCompletionActions,
            ConfirmedCompletionActions = new Dictionary<string, CoordinatedCompletionAction> { ["member"] = action }
        };
        Assert.True(start.MatchesRoomSwitchCompletion("member", Settings()));
        var changed = Settings(Route with { TargetProfileId = "c", TargetRoomCode = "CCC" });
        Assert.False(start.MatchesRoomSwitchCompletion("member", changed));
        Assert.False(start.MatchesRoomSwitchCompletion("member", new() { Policy = TaskConflictPolicy.Stop }));
        Assert.False(start.MatchesRoomSwitchCompletion("not-confirmed", Settings()));
        var stopStart = start with
        {
            ConfirmedCompletionActions = new Dictionary<string, CoordinatedCompletionAction> { ["member"] = new("stop", "group", "", 0) }
        };
        Assert.False(stopStart.MatchesRoomSwitchCompletion("member", Settings()));
        // Ordinary completion choices retain their prior policy semantics and do not require a revision match.
        Assert.True(stopStart.MatchesRoomSwitchCompletion("member", new() { Policy = TaskConflictPolicy.Resume }));
        var restored = JsonSerializer.Deserialize<CoordinatedBatchSnapshot>(JsonSerializer.Serialize(start))!;
        Assert.True(restored.MatchesRoomSwitchCompletion("member", Settings()));
    }

    [Fact]
    public async Task RemoteEditorCannotReplaceAChangedSwitchOrCreateANewRoomRelationship()
    {
        var displayed = Settings();
        var current = displayed;
        var commands = new List<RemoteCommand>();
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            commands.Add(command);
            var data = CompletionPolicyFields.Parameters(current);
            data["ok"] = "true";
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = data });
            return Task.FromResult(true);
        }, () => "controller", () => "监控端");
        Assert.NotNull(await sync.PullAsync("member"));
        var stop = CompletionPolicyFields.Parameters(new() { Policy = TaskConflictPolicy.Stop });
        current = Settings(Route with { TargetProfileId = "c", TargetRoomCode = "CCC" });
        Assert.False((await sync.PushAsync("member", stop, CompletionPolicyFields.Parameters(displayed)))!.Value.ok);
        Assert.False((await sync.PushAsync("member", stop))!.Value.ok);
        Assert.False((await sync.PushAsync("member", CompletionPolicyFields.Parameters(displayed)))!.Value.ok);
        Assert.DoesNotContain(commands, c => c.Cmd == "task_policy.push");
    }

    [Fact]
    public async Task ExplicitlyReplacingTheReadSwitchWithAnOrdinaryActionRequiresItsOriginalRevision()
    {
        var current = Settings();
        var displayed = CompletionPolicyFields.Parameters(current);
        var stop = CompletionPolicyFields.Parameters(new() { Policy = TaskConflictPolicy.Stop });
        RemoteCommand? push = null;
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            var data = new Dictionary<string, object>(command.Cmd == "task_policy.push" ? stop : displayed);
            if (command.Cmd == "task_policy.push") push = command;
            data["ok"] = "true";
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = data });
            return Task.FromResult(true);
        }, () => "controller", () => "监控端");
        Assert.NotNull(await sync.PullAsync("member"));
        var result = await sync.PushAsync("member", stop, displayed);
        Assert.True(result!.Value.ok);
        Assert.Equal("stop", push!.Params!["policy"]);
        Assert.Equal("", push.Params["roomSwitchTargetProfileId"]);
        Assert.Equal(TaskPolicySyncService.CompletionEditRevision(key => displayed.GetValueOrDefault(key)?.ToString()),
            push.Params["expectedCompletionRevision"]);
    }

    [Fact]
    public async Task ExecutorOnlyAllowsExplicitlyReplacingTheReadSwitchWithAnOrdinaryAction()
    {
        using var fixture = new RoomEditFixture();
        var stop = CompletionPolicyFields.Parameters(new() { Policy = TaskConflictPolicy.Stop });
        var before = fixture.Manager.ReadRawJson();
        await fixture.PushAsync(stop); // An ordinary Web push did not read/confirm the switch.
        Assert.Equal(before, fixture.Manager.ReadRawJson());
        await fixture.PushAsync(CompletionPolicyFields.Parameters(Settings(Route with { TargetRoomCode = "CCC" })));
        Assert.Equal(before, fixture.Manager.ReadRawJson()); // No remote room relationship editor.
        stop["expectedCompletionRevision"] = "stale";
        await fixture.PushAsync(stop);
        Assert.Equal(before, fixture.Manager.ReadRawJson());

        var read = CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(fixture.Config));
        stop["expectedCompletionRevision"] = TaskPolicySyncService.CompletionEditRevision(key => read.GetValueOrDefault(key)?.ToString());
        await fixture.PushAsync(stop);
        var saved = fixture.ReadSaved();
        Assert.Equal("stop", saved.OnlineHoeingCompletionPolicy);
        Assert.Equal("stop", fixture.Config.ActiveRoomProfile!.OnlineHoeingCompletionPolicy);
        Assert.Null(saved.OnlineHoeingRoomSwitchCompletion);
        Assert.Null(fixture.Config.ActiveRoomProfile.OnlineHoeingRoomSwitchCompletion);
        Assert.Empty(saved.OnlineHoeingSpecifiedTaskName);
        Assert.Equal("default", saved.OnlineHoeingSpecifiedStartMode);
    }

    [Fact]
    public void LocalBindingSaveUsesTheLatestDiskAndPreservesTheCompleteSwitch()
    {
        using var fixture = new RoomEditFixture();
        var baseline = fixture.CaptureBaseline();
        var disk = fixture.ReadSaved();
        disk.RoomProfiles[1].QuickCommands["一键自定义"] = "GROUP:其它房间新绑定";
        Assert.True(fixture.Manager.TrySave(disk));
        fixture.Save(baseline, ["本机新的联机绑定"], ["group"], null);
        var actual = fixture.ReadSaved();
        Assert.Equal("GROUP:其它房间新绑定", actual.RoomProfiles[1].QuickCommands["一键自定义"]);
        Assert.Equal(Route, actual.OnlineHoeingRoomSwitchCompletion);
        Assert.Equal(Route, fixture.Config.ActiveRoomProfile!.OnlineHoeingRoomSwitchCompletion);
        Assert.Equal("rev-1", actual.OnlineHoeingSpecifiedConfigRevision);
        // The ordinary settings editor's ApplyTo cannot restore the old binding or completion action.
        fixture.Config.ActiveRoomProfile.ApplyTo(fixture.Config);
        Assert.Equal(new[] { "本机新的联机绑定" }, fixture.Config.OnlineHoeingGroupNames);
        Assert.Equal(Route, fixture.Config.OnlineHoeingRoomSwitchCompletion);
    }

    [Fact]
    public void StaleLocalCompletionEditorCannotOverrideAConcurrentCompletionChangeButBindingOnlyStillWorks()
    {
        using var fixture = new RoomEditFixture();
        var baseline = fixture.CaptureBaseline();
        CompletionPolicyFields.Apply(fixture.Config, Settings(Route with { RunAfterSwitch = false }));
        Assert.True(fixture.Manager.TrySave(fixture.Config));
        var before = fixture.Manager.ReadRawJson();
        var rejected = Assert.Throws<TargetInvocationException>(() => fixture.Save(baseline, null, null,
            new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Resume }));
        Assert.IsType<InvalidOperationException>(rejected.InnerException);
        Assert.Equal(before, fixture.Manager.ReadRawJson());
        fixture.Save(baseline, ["新绑定"], ["group"], null);
        var saved = fixture.ReadSaved();
        Assert.Equal("switchRoom", saved.OnlineHoeingCompletionPolicy);
        Assert.False(saved.OnlineHoeingRoomSwitchCompletion!.RunAfterSwitch);
        Assert.Equal(new[] { "新绑定" }, saved.ActiveRoomProfile!.OnlineHoeingGroupNames);
    }

    private sealed class RoomEditFixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _root;
        private readonly MainViewModel _vm;
        public AssistConfig Config { get; }
        public AssistConfigManager Manager { get; }

        public RoomEditFixture()
        {
            Assert.True(AssistantDataDirectory.IsExplicit);
            _root = Path.Combine(AssistantDataDirectory.Root, "room-switch-policy-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Config = RoomSwitchCompletionProtocolTests.Config();
            Config.LocalRoomProfileId = Config.ActiveRoomProfileId;
            Config.PlayerUid = "self";
            Config.OnlineHoeingGroupNames = ["原绑定"];
            Config.OnlineHoeingGroupTypes = ["group"];
            CompletionPolicyFields.Apply(Config, Settings());
            Manager = new AssistConfigManager();
            typeof(AssistConfigManager).GetField("_configPath", Flags)!.SetValue(Manager, Path.Combine(_root, "assistant-config.json"));
            Assert.True(Manager.TrySave(Config));
            _vm = new MainViewModel();
            typeof(MainViewModel).GetField("_config", Flags)!.SetValue(_vm, Config);
            typeof(MainViewModel).GetField("_configManager", Flags)!.SetValue(_vm, Manager);
            typeof(MainViewModel).GetField("_reportStatusRunning", Flags)!.SetValue(_vm, 1);
        }

        public object CaptureBaseline() => typeof(MainViewModel).GetMethod("CaptureOnlineHoeingEdit", Flags)!.Invoke(_vm, null)!;
        public void Save(object baseline, List<string>? names, List<string>? types, TaskConflictPolicySettings? completion)
            => typeof(MainViewModel).GetMethod("SaveOnlineHoeingRoomFields", Flags)!.Invoke(_vm,
                [names, types, completion, baseline, false, null]);
        public Task PushAsync(Dictionary<string, object> parameters) => (Task)typeof(MainViewModel)
            .GetMethod("HandleTaskPolicyPushAsync", Flags)!.Invoke(_vm, [new RemoteCommand { Params = parameters }])!;
        public AssistConfig ReadSaved() => JsonSerializer.Deserialize<AssistConfig>(Manager.ReadRawJson())!;
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
