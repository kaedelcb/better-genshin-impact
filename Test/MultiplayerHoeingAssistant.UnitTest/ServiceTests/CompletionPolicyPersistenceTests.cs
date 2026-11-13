using System.IO;
using System.Reflection;
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
