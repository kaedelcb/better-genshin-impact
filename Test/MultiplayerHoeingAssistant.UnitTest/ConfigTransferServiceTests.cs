using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

/// <summary>全部设置包的真实文件边界回归；只使用各测试自己的临时 A/B 根，不访问默认数据目录。</summary>
public sealed class ConfigTransferServiceTests
{
    [Fact]
    public void V2RoundTripRestoresSevenSettingsPlansAndSelectionWhilePreservingLocalIdentity()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance", "wf-source");
        SeedSettings(fixture.Target, "本机", "local-instance");
        Write(fixture.Target, "dodoco_settings.json", JsonSerializer.Serialize(new DodocoSettings { ThumbnailWidth = 480 }));
        Write(fixture.Target, "dodoco_watch_rules.json", "{\"rules\":[],\"muteAll\":true}");
        Write(fixture.Target, "pet_settings.json", JsonSerializer.Serialize(new PetSettings { ShowTaskLabel = false, SizePx = 150 }));
        Write(fixture.Source, "runs/old.run.json", "{\"runId\":\"test-only\"}");
        Write(fixture.Source, "arbitration/arbitration-lease.json", "{\"test\":true}");
        Write(fixture.Source, "resource-catalog-cache.json", "{\"test\":true}");
        var exporter = new ConfigTransferService(fixture.Source);

        var exported = exporter.Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        using (var zip = ZipFile.OpenRead(fixture.Package))
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToArray();
            foreach (var name in ConfigTransferService.SettingFileNames) Assert.Contains(name, names);
            Assert.Contains("flows/wf-source.flow.json", names);
            Assert.Contains(ConfigTransferMistletoe.CurrentWorkflowFileName, names);
            Assert.DoesNotContain(names, name => name.StartsWith("runs/", StringComparison.Ordinal)
                || name.StartsWith("arbitration/", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(2, JsonNode.Parse(reader.ReadToEnd())!["formatVersion"]!.GetValue<int>());
        }

        var runtimeApplied = false;
        var importer = new ConfigTransferService(fixture.Target);
        var imported = importer.Import(fixture.Package, () =>
        {
            runtimeApplied = true;
            Assert.Equal("来源", Read(fixture.Target, "assistant-config.json")["playerName"]!.GetValue<string>());
        });

        Assert.True(imported.Success, imported.Message);
        Assert.False(imported.RecoveryRequired);
        Assert.True(runtimeApplied);
        Assert.Equal("local-instance", Read(fixture.Target, "assistant-config.json")["clientInstanceId"]!.GetValue<string>());
        Assert.Contains(Read(fixture.Target, "startup-flow-schemes.json").AsArray(), scheme => scheme!["name"]!.GetValue<string>() == "来源");
        Assert.Contains(Read(fixture.Target, "startup-flow-schemes.json").AsArray(), scheme => scheme!["name"]!.GetValue<string>() == "本机");
        Assert.Equal(1920, Read(fixture.Target, "dodoco_settings.json")["thumbnailWidth"]!.GetValue<int>());
        Assert.False(Read(fixture.Target, "dodoco_watch_rules.json")["muteAll"]!.GetValue<bool>());
        Assert.True(Read(fixture.Target, "pet_settings.json")["showTaskLabel"]!.GetValue<bool>());
        Assert.Equal(225, Read(fixture.Target, "pet_settings.json")["sizePx"]!.GetValue<double>());
        Assert.Equal("来源", Read(fixture.Target, "member-config-cache.json")["configGroups"]![0]!.GetValue<string>());
        Assert.Equal("wf-source", File.ReadAllText(Path.Combine(fixture.Target, ConfigTransferMistletoe.CurrentWorkflowFileName)).Trim());
        var restoredPlan = Read(fixture.Target, "flows/wf-source.flow.json");
        Assert.Equal("来源", restoredPlan["name"]!.GetValue<string>());
        Assert.Equal("123456789", restoredPlan["nodes"]![0]!["strategies"]![0]!["uid"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(fixture.Target, "runs")));

        var repeated = importer.Import(fixture.Package);
        Assert.True(repeated.Success, repeated.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Target, "flows"), "*.flow.json"));
    }

    [Fact]
    public void ExportUsesRuntimeSnapshotOverridesWithoutChangingSourceFiles()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance");
        var path = Path.Combine(fixture.Source, "dodoco_settings.json");
        var original = File.ReadAllBytes(path);
        var overrides = new Dictionary<string, byte[]>
        {
            ["dodoco_settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new DodocoSettings { ThumbnailWidth = 960 })
        };

        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package, overrides);
        Assert.True(exported.Success, exported.Message);
        var imported = new ConfigTransferService(fixture.Target).Import(fixture.Package);

        Assert.True(imported.Success, imported.Message);
        Assert.Equal(960, Read(fixture.Target, "dodoco_settings.json")["thumbnailWidth"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void LegacyV1PackageStillImportsAndLeavesOmittedLocalSettingsAndPlansUntouched()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Target, "本机", "local-instance", "wf-local");
        var petBefore = File.ReadAllBytes(Path.Combine(fixture.Target, "pet_settings.json"));
        var flowBefore = File.ReadAllBytes(Path.Combine(fixture.Target, "flows/wf-local.flow.json"));
        WriteLegacyPackage(fixture.Package, new Dictionary<string, string>
        {
            ["assistant-config.json"] = JsonSerializer.Serialize(new AssistConfig { PlayerName = "旧包", ClientInstanceId = "foreign-instance" })
        });

        var result = new ConfigTransferService(fixture.Target).Import(fixture.Package);

        Assert.True(result.Success, result.Message);
        Assert.False(result.RecoveryRequired);
        Assert.Equal("旧包", Read(fixture.Target, "assistant-config.json")["playerName"]!.GetValue<string>());
        Assert.Equal("local-instance", Read(fixture.Target, "assistant-config.json")["clientInstanceId"]!.GetValue<string>());
        Assert.Equal(petBefore, File.ReadAllBytes(Path.Combine(fixture.Target, "pet_settings.json")));
        Assert.Equal(flowBefore, File.ReadAllBytes(Path.Combine(fixture.Target, "flows/wf-local.flow.json")));
    }

    [Fact]
    public void V2IntegrityFailureRejectsTheEntirePackageBeforeAnyDestinationWrite()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance", "wf-source");
        SeedSettings(fixture.Target, "本机", "local-instance", "wf-local");
        var before = ManagedSnapshot(fixture.Target);
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        using (var zip = ZipFile.Open(fixture.Package, ZipArchiveMode.Update))
        {
            zip.GetEntry("pet_settings.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("pet_settings.json").Open(), new UTF8Encoding(false));
            writer.Write("{\"sizePx\":399}"); // 保持合法 JSON，只破坏清单摘要。
        }
        var writes = 0;
        var service = new ConfigTransferService(fixture.Target) { BeforeWrite = (_, _) => writes++ };

        var result = service.Import(fixture.Package);

        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(0, writes);
        AssertSnapshot(before, ManagedSnapshot(fixture.Target));
    }

    [Fact]
    public void LegacySemanticValidationFailureCannotPartiallyApplyAnEarlierValidSetting()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Target, "本机", "local-instance");
        var before = ManagedSnapshot(fixture.Target);
        WriteLegacyPackage(fixture.Package, new Dictionary<string, string>
        {
            ["assistant-config.json"] = JsonSerializer.Serialize(new AssistConfig { PlayerName = "不能落盘" }),
            ["startup-flow.json"] = "[]"
        });
        var writes = 0;
        var service = new ConfigTransferService(fixture.Target) { BeforeWrite = (_, _) => writes++ };

        var result = service.Import(fixture.Package);

        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(0, writes);
        AssertSnapshot(before, ManagedSnapshot(fixture.Target));
    }

    [Fact]
    public void ConflictingPlanIdentityIsAppendedAndStartupSchemeAndSelectionFollowTheNewIdentity()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源计划", "source-instance", "wf-shared");
        SeedSettings(fixture.Target, "本机计划", "local-instance", "wf-shared");
        var localPlan = File.ReadAllBytes(Path.Combine(fixture.Target, "flows/wf-shared.flow.json"));
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);

        var result = new ConfigTransferService(fixture.Target).Import(fixture.Package);

        Assert.True(result.Success, result.Message);
        var targetId = Read(fixture.Target, "startup-flow.json")["steps"]![0]!["taskCenterFlowId"]!.GetValue<string>();
        Assert.NotEqual("wf-shared", targetId);
        Assert.Equal("来源计划", Read(fixture.Target, "flows/" + targetId + ".flow.json")["name"]!.GetValue<string>());
        var importedScheme = Read(fixture.Target, "startup-flow-schemes.json").AsArray().Single(scheme => scheme!["name"]!.GetValue<string>() == "来源计划")!;
        Assert.Equal(targetId, importedScheme["config"]!["steps"]![0]!["taskCenterFlowId"]!.GetValue<string>());
        Assert.Equal(targetId, File.ReadAllText(Path.Combine(fixture.Target, ConfigTransferMistletoe.CurrentWorkflowFileName)).Trim());
        Assert.Equal(localPlan, File.ReadAllBytes(Path.Combine(fixture.Target, "flows/wf-shared.flow.json")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(fixture.Target, "flows"), "*.flow.json").Length);
    }

    [Fact]
    public void FailureOnLaterWriteRestoresOriginalBytesAndRemovesFilesCreatedEarlierInTheTransaction()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance", "wf-source");
        Write(fixture.Target, "assistant-config.json", "\r\n{\"playerName\":\"原始字节\",\"clientInstanceId\":\"local-instance\"}\r\n");
        var before = ManagedSnapshot(fixture.Target);
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        var writes = 0;
        var service = new ConfigTransferService(fixture.Target)
        {
            BeforeWrite = (_, _) => { if (++writes == 3) throw new IOException("测试：第三项写入失败"); }
        };

        var result = service.Import(fixture.Package);

        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.True(writes >= 3);
        AssertSnapshot(before, ManagedSnapshot(fixture.Target));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "flows/wf-source.flow.json")));
    }

    [Fact]
    public void RuntimeApplyFailureRollsBackAllFilesAndCanReapplyTheRestoredState()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance", "wf-source");
        SeedSettings(fixture.Target, "本机", "local-instance", "wf-local");
        var before = ManagedSnapshot(fixture.Target);
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        var calls = 0;
        var sawImportedState = false;

        var result = new ConfigTransferService(fixture.Target).Import(fixture.Package, () =>
        {
            if (++calls == 1)
            {
                sawImportedState = Read(fixture.Target, "assistant-config.json")["playerName"]!.GetValue<string>() == "来源";
                throw new InvalidOperationException("测试：热应用失败");
            }
            Assert.Equal("本机", Read(fixture.Target, "assistant-config.json")["playerName"]!.GetValue<string>());
        });

        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.True(calls >= 1);
        Assert.True(sawImportedState);
        AssertSnapshot(before, ManagedSnapshot(fixture.Target));
    }

    [Fact]
    public void AdditionalSharedTargetParticipatesInTheSameRollbackAsTheLocalConfiguration()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "来源", "source-instance");
        SeedSettings(fixture.Target, "本机", "local-instance");
        var shared = Path.Combine(fixture.Root, "peer", "shared-rules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        var originalShared = Encoding.UTF8.GetBytes("\r\n{\"rules\":[],\"muteAll\":true}\r\n");
        File.WriteAllBytes(shared, originalShared);
        var before = ManagedSnapshot(fixture.Target);
        var sourceRules = File.ReadAllBytes(Path.Combine(fixture.Source, "dodoco_watch_rules.json"));
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        var calls = 0;
        var sawSharedState = false;

        var result = new ConfigTransferService(fixture.Target).Import(fixture.Package, () =>
        {
            if (++calls != 1) return;
            sawSharedState = sourceRules.AsSpan().SequenceEqual(File.ReadAllBytes(shared));
            throw new IOException("测试：共享同步后热应用失败");
        }, new Dictionary<string, string> { ["dodoco_watch_rules.json"] = shared });

        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired);
        Assert.True(calls >= 1);
        Assert.True(sawSharedState);
        Assert.Equal(originalShared, File.ReadAllBytes(shared));
        AssertSnapshot(before, ManagedSnapshot(fixture.Target));
    }

    [Fact]
    public void ResourceImportRequiresTheTargetsOwnBgiBindingRatherThanWritingTheSourceMachinesPath()
    {
        using var fixture = new Fixture();
        SeedSettings(fixture.Source, "source", "source-instance");
        SeedSettings(fixture.Target, "target", "local-instance");
        var installation = Path.Combine(fixture.Root, "source-bgi");
        Directory.CreateDirectory(installation);
        var executable = Path.Combine(installation, "BetterGI.exe");
        File.WriteAllBytes(executable, [0]); // Existence-only fixture, never launched.
        var sourceConfig = Read(fixture.Source, "assistant-config.json");
        sourceConfig["bgiPath"] = executable;
        Write(fixture.Source, "assistant-config.json", sourceConfig.ToJsonString());
        Write(Path.Combine(installation, "User"), "ScriptGroup/G.json", """{"name":"G","projects":[]}""");
        var originalResource = File.ReadAllBytes(Path.Combine(installation, "User/ScriptGroup/G.json"));
        var exported = new ConfigTransferService(fixture.Source).Export(fixture.Package);
        Assert.True(exported.Success, exported.Message);
        var before = ManagedSnapshot(fixture.Target);
        var result = new ConfigTransferService(fixture.Target).Import(fixture.Package);
        Assert.False(result.Success);
        Assert.Contains("BetterGI.exe", result.Message);
        Assert.Equal(originalResource, File.ReadAllBytes(Path.Combine(installation, "User/ScriptGroup/G.json")));
        var after = ManagedSnapshot(fixture.Target);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }

    private static void SeedSettings(string root, string label, string instanceId, string? planId = null)
    {
        Write(root, "assistant-config.json", JsonSerializer.Serialize(new AssistConfig { PlayerName = label, ClientInstanceId = instanceId }));
        var startup = new JsonObject { ["enabled"] = false, ["steps"] = new JsonArray() };
        if (planId is not null)
            ((JsonArray)startup["steps"]!).Add(new JsonObject { ["kind"] = "enterTaskCenter", ["taskCenterFlowId"] = planId,
                ["trueSteps"] = new JsonArray(), ["falseSteps"] = new JsonArray(), ["fireSteps"] = new JsonArray() });
        Write(root, "startup-flow.json", startup.ToJsonString());
        Write(root, "startup-flow-schemes.json", new JsonArray(new JsonObject { ["name"] = label, ["config"] = startup.DeepClone() }).ToJsonString());
        Write(root, "dodoco_settings.json", JsonSerializer.Serialize(new DodocoSettings { ThumbnailWidth = 1920 }));
        Write(root, "dodoco_watch_rules.json", "{\"rules\":[],\"muteAll\":false}");
        Write(root, "pet_settings.json", JsonSerializer.Serialize(new PetSettings { ShowTaskLabel = true, SizePx = 225 }));
        Write(root, "member-config-cache.json", JsonSerializer.Serialize(new MemberConfigCache { ConfigGroups = [label] }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        if (planId is null) return;
        var plan = new JsonObject
        {
            ["schema"] = "mistletoe.workflow", ["schemaVersion"] = 1, ["workflowId"] = planId, ["name"] = label,
            ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = "end", ["kind"] = "control.end",
                ["strategies"] = new JsonArray(new JsonObject { ["kind"] = "prerequisite.account", ["uid"] = "123456789" }) }),
            ["triggers"] = new JsonArray(), ["terminal"] = new JsonArray()
        };
        Write(root, "flows/" + planId + ".flow.json", plan.ToJsonString());
        Write(root, ConfigTransferMistletoe.CurrentWorkflowFileName, planId);
    }

    private static void WriteLegacyPackage(string path, IReadOnlyDictionary<string, string> files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        Add("manifest.json", JsonSerializer.Serialize(new { app = "NexusBGI-Assistant", formatVersion = 1, files = ConfigTransferService.SettingFileNames }));
        foreach (var pair in files) Add(pair.Key, pair.Value);
    }

    private static Dictionary<string, byte[]> ManagedSnapshot(string root)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in ConfigTransferService.SettingFileNames.Append(ConfigTransferMistletoe.CurrentWorkflowFileName))
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) result.Add(name, File.ReadAllBytes(path));
        }
        var flows = Path.Combine(root, "flows");
        if (Directory.Exists(flows))
            foreach (var file in Directory.GetFiles(flows, "*.flow.json")) result.Add("flows/" + Path.GetFileName(file), File.ReadAllBytes(file));
        return result;
    }

    private static void AssertSnapshot(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(name => name), actual.Keys.OrderBy(name => name));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }
    private static JsonNode Read(string root, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, name)))!;
    private static void Write(string root, string name, string text)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ConfigTransferServiceTests-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "A");
        public string Target => Path.Combine(Root, "B");
        public string Package => Path.Combine(Root, "settings.zip");
        public Fixture() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Target); }
        public void Dispose()
        {
            // 根目录只来自本夹具新建的随机 temp 子目录；从不清理默认助手或 BGI User 目录。
            var absolute = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("ConfigTransferServiceTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理测试根以外的目录");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
