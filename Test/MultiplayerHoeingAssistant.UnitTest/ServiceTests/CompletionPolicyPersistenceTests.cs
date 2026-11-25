using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CompletionPolicyPersistenceTests
{
    [Fact]
    public void OneSaveAndExactReadBackPrecedeUpdatingRuntimeConfiguration()
    {
        var current = new AssistConfig { PlayerName = "保留本机设置" };
        var settings = Settings();
        var raw = "";
        var writes = 0;
        CompletionPolicyFields.SaveChecked(candidate =>
        {
            writes++;
            Assert.Equal("default", current.OnlineHoeingSpecifiedStartMode);
            Assert.Equal("保留本机设置", candidate.PlayerName);
            raw = JsonSerializer.Serialize(candidate);
        }, () => raw, current, settings);
        Assert.Equal(1, writes);
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings), CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(current)));
        var restored = JsonSerializer.Deserialize<AssistConfig>(raw)!;
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings), CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(restored)));
    }

    [Fact]
    public void SilentSaveFailureOrIncompleteReadBackDoesNotUpdateRuntimeMirror()
    {
        var current = new AssistConfig();
        var oldRaw = JsonSerializer.Serialize(current);
        Assert.Throws<IOException>(() => CompletionPolicyFields.SaveChecked(_ => { }, () => oldRaw, current, Settings()));
        Assert.Equal("resume", current.OnlineHoeingCompletionPolicy);
        Assert.Equal("default", current.OnlineHoeingSpecifiedStartMode);
        var changed = new AssistConfig();
        CompletionPolicyFields.Apply(changed, Settings());
        changed.OnlineHoeingSpecifiedStartTaskId = "different";
        Assert.Throws<IOException>(() => CompletionPolicyFields.SaveChecked(_ => { }, () => JsonSerializer.Serialize(changed), current, Settings()));
        Assert.Empty(current.OnlineHoeingSpecifiedStartTaskId);
    }

    [Fact]
    public void EachImportedStartFieldChangeRequiresScheduleConfirmation()
    {
        var local = new AssistConfig { ScheduledOnlineTime = "12:00" };
        CompletionPolicyFields.Apply(local, Settings());
        Action<AssistConfig>[] changes = [
            config => config.OnlineHoeingSpecifiedStartMode = "beginning",
            config => config.OnlineHoeingSpecifiedStartTaskId = "node-c",
            config => config.OnlineHoeingSpecifiedStartTaskName = "新名称",
            config => config.OnlineHoeingSpecifiedConfigRevision = "changed-revision",
            config => config.OnlineHoeingSpecifiedWorkflowId = "wf-other"
        ];
        var raw = JsonSerializer.Serialize(local);
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, JsonSerializer.Deserialize<AssistConfig>(raw)!));
        foreach (var change in changes)
        {
            var imported = JsonSerializer.Deserialize<AssistConfig>(raw)!;
            change(imported);
            Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        }
    }

    [Theory]
    [InlineData("group")]
    [InlineData("workflow")]
    public void ImportResourceRenamePreservesStableStartMetadataForRevalidation(string type)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var root = Path.Combine(AssistantDataDirectory.Root, "completion-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ScriptGroup"));
        try
        {
            File.WriteAllText(Path.Combine(root, "ScriptGroup", "同名.json"), "{\"name\":\"本机保留\",\"projects\":[]}");
            var config = new AssistConfig();
            var settings = Settings();
            settings.SpecifiedTaskType = type;
            settings.SpecifiedTaskName = "同名";
            if (type == "group") settings.SpecifiedWorkflowId = "";
            CompletionPolicyFields.Apply(config, settings);
            var plan = ConfigTransferResources.PlanImport(new Dictionary<string, byte[]>
            {
                ["assistant-config.json"] = JsonSerializer.SerializeToUtf8Bytes(config),
                [ConfigTransferResources.Prefix + "ScriptGroup/同名.json"] = Encoding.UTF8.GetBytes("{\"name\":\"同名\",\"projects\":[]}")
            }, root);
            var imported = JsonSerializer.Deserialize<AssistConfig>(plan.AssistantFiles["assistant-config.json"])!;
            if (type == "workflow") Assert.Equal("同名", imported.OnlineHoeingSpecifiedTaskName);
            else Assert.StartsWith("同名-导入-", imported.OnlineHoeingSpecifiedTaskName);
            Assert.Equal("node-b", imported.OnlineHoeingSpecifiedStartTaskId);
            Assert.Equal("rev-7", imported.OnlineHoeingSpecifiedConfigRevision);
            Assert.Equal(settings.SpecifiedWorkflowId, imported.OnlineHoeingSpecifiedWorkflowId);
            Assert.Equal("{\"name\":\"本机保留\",\"projects\":[]}", File.ReadAllText(Path.Combine(root, "ScriptGroup", "同名.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("resume", "group")]
    [InlineData("stop", "group")]
    [InlineData("resume", "workflow")]
    [InlineData("stop", "workflow")]
    public async Task VersionOneResumeAndStopPushesPreserveLegacyDefaultStartFields(string policy, string type)
    {
        using var fixture = new PolicyFixture();
        var settings = Settings();
        settings.Policy = policy == "resume" ? TaskConflictPolicy.Resume : TaskConflictPolicy.Stop;
        settings.SpecifiedTaskType = type;
        settings.SpecifiedStartMode = "default";
        settings.SpecifiedStartTaskId = "saved-unused-node";
        settings.SpecifiedStartTaskName = "保留的原任务";
        settings.SpecifiedConfigRevision = "saved-unused-revision";
        settings.SpecifiedWorkflowId = type == "workflow" ? "saved-unused-workflow" : "";
        await fixture.PushAsync(settings);
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings),
            CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(fixture.Config)));
        var saved = JsonSerializer.Deserialize<AssistConfig>(fixture.Manager.ReadRawJson())!;
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings),
            CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(saved)));
        Assert.Equal("保留本机设置", saved.PlayerName);
    }

    [Fact]
    public async Task VersionOneRunSpecifiedPushStillRejectsImplicitDefaultStart()
    {
        using var fixture = new PolicyFixture();
        var before = fixture.Manager.ReadRawJson();
        var settings = Settings();
        settings.SpecifiedStartMode = "default";
        await fixture.PushAsync(settings);
        Assert.Equal("resume", fixture.Config.OnlineHoeingCompletionPolicy);
        Assert.Equal(before, fixture.Manager.ReadRawJson());
    }

    [Fact]
    public void BindingOnlySavePreservesEveryCompletionFieldAndReadsBackBeforeUpdatingRuntime()
    {
        using var fixture = new PolicyFixture();
        var settings = Settings();
        settings.SpecifiedStartMode = "default";
        CompletionPolicyFields.Apply(fixture.Config, settings);
        fixture.Manager.Save(fixture.Config);
        fixture.SaveBindings(["新配置", "新一条龙"], ["group", "onedragon"]);
        var saved = JsonSerializer.Deserialize<AssistConfig>(fixture.Manager.ReadRawJson())!;
        Assert.Equal(new[] { "新配置", "新一条龙" }, fixture.Config.OnlineHoeingGroupNames);
        Assert.Equal(new[] { "group", "onedragon" }, saved.OnlineHoeingGroupTypes);
        Assert.Equal(0, fixture.Config.OnlineHoeingGroupIndex);
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings),
            CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(saved)));
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings),
            CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(fixture.Config)));
    }

    [Fact]
    public void BindingSaveFailureLeavesRuntimeAndPersistedSettingsUnchanged()
    {
        using var fixture = new PolicyFixture();
        var before = fixture.Manager.ReadRawJson();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AssistConfigManager).GetMethod("SetConfigTransferActive", flags)!.Invoke(fixture.Manager, [true]);
        try
        {
            var failure = Assert.Throws<TargetInvocationException>(() => fixture.SaveBindings(["新配置"], ["group"]));
            Assert.IsType<IOException>(failure.InnerException);
            Assert.Equal(new[] { "原配置" }, fixture.Config.OnlineHoeingGroupNames);
            Assert.Equal(2, fixture.Config.OnlineHoeingGroupIndex);
            Assert.Equal(before, fixture.Manager.ReadRawJson());
        }
        finally { typeof(AssistConfigManager).GetMethod("SetConfigTransferActive", flags)!.Invoke(fixture.Manager, [false]); }
    }

    [Fact]
    public void SwitchSaveReadsBackBothRuntimeAndActiveProfileBeforeUpdatingEither()
    {
        var current = RoomConfiguration();
        var settings = SwitchSettings(current, "room-a", "room-b");
        var raw = "";
        Assert.Throws<IOException>(() => CompletionPolicyFields.SaveChecked(candidate =>
        {
            Assert.Equal("switchRoom", candidate.OnlineHoeingCompletionPolicy);
            Assert.Equal(settings.RoomSwitch, candidate.ActiveRoomProfile!.OnlineHoeingRoomSwitchCompletion);
            // 模拟运行字段写对而活动档案的字段写丢；重启会读取档案，不可确认成功。
            candidate.ActiveRoomProfile.OnlineHoeingRoomSwitchCompletion = null;
            raw = JsonSerializer.Serialize(candidate);
        }, () => raw, current, settings));
        Assert.Equal("resume", current.OnlineHoeingCompletionPolicy);
        Assert.Null(current.ActiveRoomProfile!.OnlineHoeingRoomSwitchCompletion);
        Assert.Throws<IOException>(() => CompletionPolicyFields.SaveChecked(candidate =>
        {
            // 盘面仍保留完整策略但已更改目标身份，不足以确认合法保存。
            candidate.RoomProfiles[1].RoomCode = "CHANGED";
            raw = JsonSerializer.Serialize(candidate);
        }, () => raw, current, settings));
        Assert.Equal("resume", current.OnlineHoeingCompletionPolicy);
        CompletionPolicyFields.SaveChecked(candidate =>
        {
            Assert.Equal("resume", current.OnlineHoeingCompletionPolicy);
            Assert.Equal("resume", current.ActiveRoomProfile!.OnlineHoeingCompletionPolicy);
            raw = JsonSerializer.Serialize(candidate);
        }, () => raw, current, settings);

        Assert.Equal("switchRoom", current.ActiveRoomProfile!.OnlineHoeingCompletionPolicy);
        Assert.Equal(settings.RoomSwitch, current.OnlineHoeingRoomSwitchCompletion);
        Assert.Equal(settings.RoomSwitch, current.ActiveRoomProfile.OnlineHoeingRoomSwitchCompletion);
        Assert.True(current.ShowRoomOrchestration);
        Assert.Equal("room-b", current.ViewedRoomProfileId);
        var reloaded = JsonSerializer.Deserialize<AssistConfig>(raw)!;
        reloaded.ActiveRoomProfile!.ApplyTo(reloaded);
        AssertParametersEqual(CompletionPolicyFields.Parameters(settings), CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(reloaded)));
        Assert.Equal("12:00", reloaded.ScheduledOnlineTime);
        Assert.Equal(new DateTime(2026, 11, 25), reloaded.ScheduledOnlineLastFireDate);
    }

    [Fact]
    public void SnapshotAndTargetedSaveUseActiveIdEvenWhenDisconnectedAndDoNotTouchOtherRooms()
    {
        var config = RoomConfiguration();
        var source = config.ActiveRoomProfile!;
        var sourceSettings = SwitchSettings(config, source.Id, "room-b");
        config.StandaloneMode = true;
        CompletionPolicyFields.Apply(config, sourceSettings);
        // 活动档案即使镜像暂旧，也须以断线/单机时的运行设置作为编辑基础。
        source.OnlineHoeingCompletionPolicy = "resume";
        source.OnlineHoeingRoomSwitchCompletion = null;
        Assert.Equal(sourceSettings.RoomSwitch, CompletionPolicyFields.SnapshotForProfile(config, source.Id).RoomSwitch);
        var destinationSettings = new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Stop,
            RoomSwitch = RoomSwitchCompletion.Choices(config, "room-b").Single(c => c.TargetProfileId == "room-c") };
        CompletionPolicyFields.ApplyForProfile(config, "room-b", destinationSettings);
        AssertParametersEqual(CompletionPolicyFields.Parameters(sourceSettings), CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(config)));
        Assert.Equal(destinationSettings.RoomSwitch, CompletionPolicyFields.SnapshotForProfile(config, "room-b").RoomSwitch);
        Assert.Equal("12:00", config.ScheduledOnlineTime);
        Assert.Equal("18:30", config.RoomProfiles[1].ScheduledOnlineTime);
        Assert.True(config.RoomProfiles[1].ScheduledOnlineRequiresConfirmation);
        Assert.Equal("room-a", config.ActiveRoomProfileId);
        Assert.Equal("AAAAAA", config.EffectiveRoomCode);
        // 普通保存会重新捕获活动档案；其他房间的定向编辑应独立接续。
        source.Capture(config);
        var reloaded = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(config))!;
        reloaded.RoomProfiles[1].ApplyTo(reloaded);
        reloaded.ActiveRoomProfileId = "room-b";
        AssertParametersEqual(CompletionPolicyFields.Parameters(destinationSettings), CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(reloaded)));
        Assert.Equal(sourceSettings.RoomSwitch, CompletionPolicyFields.SnapshotForProfile(reloaded, "room-a").RoomSwitch);
        Assert.True(reloaded.ShowRoomOrchestration);
    }

    [Theory]
    [InlineData(TaskConflictPolicy.Resume)]
    [InlineData(TaskConflictPolicy.Stop)]
    public void ReplacingSwitchWithOrdinaryActionClearsOldFollowupEvenIfIncomingFieldsAreStale(TaskConflictPolicy policy)
    {
        var config = RoomConfiguration();
        var settings = SwitchSettings(config, "room-a", "room-b");
        CompletionPolicyFields.ApplyForProfile(config, "room-a", settings);
        settings.RoomSwitch = null;
        settings.Policy = policy;
        var raw = "";
        CompletionPolicyFields.SaveChecked(candidate => raw = JsonSerializer.Serialize(candidate), () => raw, config, settings);
        var actual = CompletionPolicyFields.Snapshot(config);
        Assert.Equal(policy, actual.Policy);
        Assert.Null(actual.RoomSwitch);
        Assert.Empty(actual.SpecifiedTaskName);
        Assert.Empty(actual.SpecifiedWorkflowId);
        Assert.Empty(actual.SpecifiedConfigRevision);
        Assert.Empty(actual.SpecifiedStartTaskId);
        Assert.Null(config.ActiveRoomProfile!.OnlineHoeingRoomSwitchCompletion);
    }

    [Fact]
    public void InvalidExplicitSwitchCannotFallBackToResumeOrStopAndMustBeReselected()
    {
        var config = RoomConfiguration();
        config.OnlineHoeingCompletionPolicy = "switchRoom";
        var malformed = CompletionPolicyFields.Snapshot(config);
        Assert.NotNull(malformed.RoomSwitch);
        Assert.False(malformed.RoomSwitch.IsValid);
        Assert.Equal(TaskConflictPolicy.Stop, malformed.Policy);
        Assert.Equal("switchRoom", CompletionPolicyFields.Parameters(malformed)["policy"]);
        Assert.NotNull(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", malformed));
        var settings = SwitchSettings(config, "room-a", "room-b");
        Assert.Null(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", settings));
        settings.SpecifiedStartMode = "default";
        Assert.NotNull(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", settings));
        settings.SpecifiedStartMode = "node";
        config.RoomProfiles[1].Name = "已改名";
        Assert.Null(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", settings));
        config.RoomProfiles[1].ServerUrl = "https://moved.example.test";
        Assert.NotNull(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", settings));
        var writes = 0;
        Assert.Throws<InvalidOperationException>(() => CompletionPolicyFields.SaveChecked(_ => writes++, () => "{}", config, settings));
        Assert.Equal(0, writes);
        config.RoomProfiles.RemoveAt(1);
        Assert.NotNull(CompletionPolicyFields.ValidateRoomSwitch(config, "room-a", settings));
    }

    [Fact]
    public void SwitchWireParametersStayScalarAndAnIncompleteSwitchDoesNotBecomeAnOrdinaryAction()
    {
        var config = RoomConfiguration();
        var settings = SwitchSettings(config, "room-a", "room-b");
        var parameters = CompletionPolicyFields.Parameters(settings);
        Assert.All(parameters.Values, value => Assert.IsType<string>(value));
        Assert.Equal("switchRoom", parameters["policy"]);
        Assert.Equal("true", parameters["roomSwitchRunAfter"]);
        string? Read(string key) => parameters.TryGetValue(key, out var value) ? (string)value : null;
        Assert.Equal(settings.RoomSwitch, CompletionPolicyFields.RoomSwitchFromParameters(Read));
        parameters.Remove("roomSwitchRunAfter");
        var invalid = CompletionPolicyFields.RoomSwitchFromParameters(Read);
        Assert.NotNull(invalid);
        Assert.False(invalid.IsValid);
        parameters["policy"] = "stop";
        Assert.Null(CompletionPolicyFields.RoomSwitchFromParameters(Read)); // 残留切房字段不得激活普通 Stop。
        var ordinary = CompletionPolicyFields.Parameters(new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Resume });
        Assert.Equal("false", ordinary["roomSwitchRunAfter"]);
        Assert.Equal("", ordinary["roomSwitchTargetProfileId"]);
    }

    [Fact]
    public void ImportScheduleAuthorizationTracksActualSwitchIdentityAndSuccessorNotDisplayName()
    {
        var local = RoomConfiguration();
        CompletionPolicyFields.Apply(local, SwitchSettings(local, "room-a", "room-b"));
        var raw = JsonSerializer.Serialize(local);
        var imported = JsonSerializer.Deserialize<AssistConfig>(raw)!;
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported.OnlineHoeingRoomSwitchCompletion = imported.OnlineHoeingRoomSwitchCompletion! with { TargetName = "乙房新名字" };
        imported.RoomProfiles[1].Name = "乙房新名字";
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported.OnlineHoeingRoomSwitchCompletion = imported.OnlineHoeingRoomSwitchCompletion with { RunAfterSwitch = false };
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported = JsonSerializer.Deserialize<AssistConfig>(raw)!;
        imported.RoomProfiles[1].RoomCode = "CHANGED";
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
    }

    [Theory]
    [InlineData("group", "ScriptGroup", "{\"name\":\"同名\",\"projects\":[{\"name\":\"任务\",\"taskCenterTaskId\":\"node-b\",\"status\":\"Enabled\"}]}")]
    [InlineData("onedragon", "OneDragon", "{\"name\":\"同名\",\"taskDefinitions\":{\"node-b\":\"任务\"},\"taskEnabledList\":{\"node-b\":true}}")]
    public void ImportRenamesSwitchSuccessorOnlyWhenFrozenResourceRevisionCanBeMapped(string type, string directory, string json)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var root = Path.Combine(AssistantDataDirectory.Root, "switch-completion-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, directory));
        try
        {
            var sourceBytes = Encoding.UTF8.GetBytes(json);
            var originalLocal = Path.Combine(root, directory, "同名.json");
            File.WriteAllText(originalLocal, "{}");
            var config = RoomConfiguration();
            var settings = SwitchSettings(config, "room-a", "room-b");
            settings.SpecifiedTaskType = type;
            settings.SpecifiedTaskName = "同名";
            settings.SpecifiedWorkflowId = "";
            settings.SpecifiedConfigRevision = Convert.ToHexString(SHA256.HashData(sourceBytes));
            CompletionPolicyFields.Apply(config, settings);
            var inactiveSettings = SwitchSettings(config, "room-b", "room-c");
            inactiveSettings.SpecifiedTaskType = type;
            inactiveSettings.SpecifiedTaskName = settings.SpecifiedTaskName;
            inactiveSettings.SpecifiedWorkflowId = "";
            inactiveSettings.SpecifiedConfigRevision = settings.SpecifiedConfigRevision;
            CompletionPolicyFields.ApplyForProfile(config, "room-b", inactiveSettings);
            var plan = ConfigTransferResources.PlanImport(new Dictionary<string, byte[]>
            {
                ["assistant-config.json"] = JsonSerializer.SerializeToUtf8Bytes(config),
                [ConfigTransferResources.Prefix + directory + "/同名.json"] = sourceBytes
            }, root);
            var imported = JsonSerializer.Deserialize<AssistConfig>(plan.AssistantFiles["assistant-config.json"])!;
            Assert.StartsWith("同名-导入-", imported.OnlineHoeingSpecifiedTaskName);
            var transferred = Assert.Single(plan.TargetFiles);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(transferred.Value)), imported.OnlineHoeingSpecifiedConfigRevision);
            Assert.Equal(imported.OnlineHoeingSpecifiedTaskName, imported.RoomProfiles[1].OnlineHoeingSpecifiedTaskName);
            Assert.Equal(imported.OnlineHoeingSpecifiedConfigRevision, imported.RoomProfiles[1].OnlineHoeingSpecifiedConfigRevision);
            Assert.Equal("node-b", imported.OnlineHoeingSpecifiedStartTaskId);
            Assert.Equal(settings.RoomSwitch, imported.OnlineHoeingRoomSwitchCompletion);
            Assert.Equal(inactiveSettings.RoomSwitch, imported.RoomProfiles[1].OnlineHoeingRoomSwitchCompletion);
            Assert.Equal("{}", File.ReadAllText(originalLocal));

            // 缺少来源资源时不能因为目标机有同名任务而自动授权；保留引用供用户核对。
            var warnings = new List<string>();
            var unresolved = JsonSerializer.Deserialize<AssistConfig>(ConfigTransferResources.RevalidateSwitchTasksWithoutResources(
                JsonSerializer.SerializeToUtf8Bytes(config), warnings))!;
            Assert.Empty(unresolved.OnlineHoeingSpecifiedConfigRevision);
            Assert.Equal(settings.RoomSwitch, unresolved.OnlineHoeingRoomSwitchCompletion);
            Assert.NotEmpty(warnings);
            Assert.NotNull(CompletionPolicyFields.ValidateRoomSwitch(unresolved, "room-a", CompletionPolicyFields.Snapshot(unresolved)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AcceptedBatchKeepsFrozenRoomSwitchAndSuccessorWhenProfileIsEditedForTheNextBatch()
    {
        var config = RoomConfiguration();
        var settings = SwitchSettings(config, "room-a", "room-b");
        CompletionPolicyFields.Apply(config, settings);
        var batch = new OnlineHoeingBatch(new("frozen-switch", 1, 0, 0, 0, "registering", "")
        {
            Protocol = "hoeing.start.v2", Source = "automatic", ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> { ["self"] = [new("联机组", "group")] }
        }, "self", settings);
        var frozen = CompletionPolicyFields.Parameters(settings);

        settings.RoomSwitch = RoomSwitchCompletion.Choices(config, "room-a").Single(c => c.TargetProfileId == "room-c");
        settings.SpecifiedTaskName = "下轮任务";
        CompletionPolicyFields.Apply(config, settings);

        AssertParametersEqual(frozen, CompletionPolicyFields.Parameters(batch.Policy!));
        Assert.Equal("room-b", batch.Policy!.RoomSwitch!.TargetProfileId);
        Assert.Equal("room-c", CompletionPolicyFields.Snapshot(config).RoomSwitch!.TargetProfileId);
    }

    private static AssistConfig RoomConfiguration()
    {
        var config = new AssistConfig
        {
            PlayerUid = "self", PlayerName = "本机", ActiveRoomProfileId = "room-a", LocalRoomProfileId = "room-a",
            ShowRoomOrchestration = true, ViewedRoomProfileId = "room-b",
            RoomProfiles = [
                new() { Id = "room-a", Name = "甲房", ServerUrl = "https://source.example.test", RoomCode = "AAAAAA",
                    ScheduledOnlineTime = "12:00", ScheduledOnlineLastFireDate = new DateTime(2026, 11, 25) },
                new() { Id = "room-b", Name = "乙房", ServerUrl = "https://target.example.test", RoomCode = "BBBBBB",
                    ScheduledOnlineTime = "18:30", ScheduledOnlineRequiresConfirmation = true },
                new() { Id = "room-c", Name = "丙房", ServerUrl = "https://third.example.test", RoomCode = "CCCCCC" }
            ]
        };
        config.ActiveRoomProfile!.ApplyTo(config);
        return config;
    }

    private static TaskConflictPolicySettings SwitchSettings(AssistConfig config, string sourceId, string targetId)
    {
        var settings = Settings();
        settings.Policy = TaskConflictPolicy.Stop;
        settings.RoomSwitch = RoomSwitchCompletion.Choices(config, sourceId).Single(c => c.TargetProfileId == targetId) with { RunAfterSwitch = true };
        return settings;
    }

    private sealed class PolicyFixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _root;
        internal AssistConfig Config { get; } = new()
        {
            PlayerName = "保留本机设置", OnlineHoeingGroupNames = ["原配置"],
            OnlineHoeingGroupTypes = ["group"], OnlineHoeingGroupIndex = 2
        };
        internal AssistConfigManager Manager { get; }
        private MainViewModel ViewModel { get; }

        internal PolicyFixture()
        {
            Assert.True(AssistantDataDirectory.IsExplicit, "持久化测试必须指定隔离的 NEXUSBGI_DATA_ROOT");
            _root = Path.Combine(AssistantDataDirectory.Root, "completion-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Manager = new AssistConfigManager();
            typeof(AssistConfigManager).GetField("_configPath", Flags)!
                .SetValue(Manager, Path.Combine(_root, "assistant-config.json"));
            Manager.Save(Config);
            ViewModel = new MainViewModel();
            typeof(MainViewModel).GetField("_config", Flags)!.SetValue(ViewModel, Config);
            typeof(MainViewModel).GetField("_configManager", Flags)!.SetValue(ViewModel, Manager);
            // 本夹具只验证保存入口；抑制完成后的后台状态采集，避免连接真实 BGI。
            typeof(MainViewModel).GetField("_reportStatusRunning", Flags)!.SetValue(ViewModel, 1);
        }

        internal Task PushAsync(TaskConflictPolicySettings settings) => (Task)typeof(MainViewModel)
            .GetMethod("HandleTaskPolicyPushAsync", Flags)!.Invoke(ViewModel,
                [new RemoteCommand { Sender = "网页测试", Params = CompletionPolicyFields.Parameters(settings) }])!;

        internal void SaveBindings(List<string> names, List<string> types) => typeof(MainViewModel)
            .GetMethod("SaveOnlineHoeingBindings", Flags)!.Invoke(ViewModel, [names, types]);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private static TaskConflictPolicySettings Settings() => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = "workflow", SpecifiedTaskName = "收尾计划",
        SpecifiedTaskPriority = 23, SpecifiedStartMode = "node", SpecifiedStartTaskId = "node-b",
        SpecifiedStartTaskName = "收取", SpecifiedConfigRevision = "rev-7", SpecifiedWorkflowId = "wf-original"
    };

    private static void AssertParametersEqual(Dictionary<string, object> expected, Dictionary<string, object> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
}
