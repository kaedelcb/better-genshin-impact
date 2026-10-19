using System.Text;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.CoreTests.OneDragon;

/// <summary>只使用临时配置副本，验证正常加载后的 User 文件可以直接供公版模型读取。</summary>
public sealed class OneDragonPublicUserCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onedragon-public-user-" + Guid.NewGuid().ToString("N"));

    private const string LegacyTuple = """
        {
          "Name":"旧调度", "Version":1, "IndexId":4,
          "ScheduleName":"我的计划", "NextConfiguration":true,
          "AccountBinding":true, "GenshinUid":"123456789", "AccountBindingCode":"local-only",
          "Period":"每周", "PeriodList":{"周三":true}, "NextTaskIndex":2,
          "PartyName":"测试队伍", "DomainName":"测试秘境", "CraftingBenchCountry":"璃月",
          "SundayDaySelectedValue":"2",
          "UnknownFuture":{"keep":7,"date":"2026-10-19T01:02:03+08:00"},
          "TaskEnabledList":{
            "5":{"Item1":true,"Item2":"领取邮件"},
            "2":{"Item1":true,"Item2":"领取邮件"},
            "9":{"Item1":false,"Item2":"合成树脂"}
          }
        }
        """;

    private const string PublicCurrent = """
        { "Name":"公版配置", "TaskEnabledList":{"b":true,"a":false},
          "TaskOrder":["b","a"], "TaskDefinitions":{"b":"领取邮件","a":"合成树脂"},
          "NextTaskId":"b", "PartyName":"公版队伍", "UnknownFuture":{"keep":"原样"} }
        """;

    [Fact]
    public void PreparingMixedDirectoryMakesEverySupportedConfigDirectlyReadableByPublicModel()
    {
        var tuple = Seed("旧调度", LegacyTuple, bom: true, crlf: true);
        var names = Seed("旧名称", """
            {"Name":"旧名称","TaskEnabledList":{"领取邮件":true,"合成树脂":false},
             "PartyName":"旧队伍","UnknownFuture":{"keep":9}}
            """);
        var current = Seed("公版配置", PublicCurrent, bom: true, crlf: true);
        var paths = new[] { tuple, names, current };
        var originals = paths.ToDictionary(path => path, File.ReadAllBytes);
        var expected = paths.ToDictionary(path => path, path => OneDragonCompatibility.Read(path).Config);
        Assert.All(paths, path => Assert.Equal(originals[path], File.ReadAllBytes(path)));

        var failures = new List<(string Path, Exception Error)>();
        OneDragonCompatibility.PrepareDirectoryForPublic(_root, (path, error) => failures.Add((path, error)));

        Assert.Empty(failures);
        foreach (var path in paths)
        {
            var direct = ReadWithPublicModel(path);
            AssertEquivalent(expected[path], direct);
            var raw = JObject.Parse(File.ReadAllText(path));
            Assert.All(((JObject)raw["TaskEnabledList"]!).Properties(), property =>
                Assert.Equal(JTokenType.Boolean, property.Value.Type));
            Assert.Null(raw["NextTaskIndex"]);
        }

        var converted = ReadWithPublicModel(tuple);
        Assert.Equal(3, converted.TaskOrder.Distinct().Count());
        Assert.Equal(new[] { "领取邮件", "领取邮件", "合成树脂" },
            converted.TaskOrder.Select(id => converted.TaskDefinitions[id]));
        Assert.Equal(converted.TaskOrder[1], converted.NextTaskId);
        Assert.False(converted.TaskEnabledList[converted.TaskOrder[2]]);
        Assert.Equal("2", converted.SundaySelectedValue);

        var preserved = JObject.Parse(File.ReadAllText(tuple));
        var originalRaw = JObject.Parse(Encoding.UTF8.GetString(originals[tuple]).TrimStart('\uFEFF'));
        foreach (var field in new[] { "Version", "IndexId", "ScheduleName", "NextConfiguration", "AccountBinding",
                     "GenshinUid", "AccountBindingCode", "Period", "PeriodList", "UnknownFuture" })
            Assert.True(JToken.DeepEquals(originalRaw[field], preserved[field]), $"字段 {field} 未完整保留");
        Assert.Equal(new byte[] { 239, 187, 191 }, File.ReadAllBytes(tuple).Take(3));
        Assert.Contains("\r\n", File.ReadAllText(tuple));
        Assert.DoesNotContain("\n", File.ReadAllText(tuple).Replace("\r\n", string.Empty));
        Assert.False(File.ReadAllBytes(names).AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        Assert.DoesNotContain("\r\n", File.ReadAllText(names));
        AssertExactBackup(tuple, originals[tuple]);
        AssertExactBackup(names, originals[names]);
        Assert.Equal(originals[current], File.ReadAllBytes(current));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".compat-backup"), "公版配置.json.*.bak"));

        var normalizedBytes = paths.ToDictionary(path => path, File.ReadAllBytes);
        var backups = Directory.GetFiles(Path.Combine(_root, ".compat-backup")).OrderBy(path => path).ToArray();
        OneDragonCompatibility.PrepareDirectoryForPublic(_root, (path, error) => failures.Add((path, error)));
        Assert.Empty(failures);
        Assert.All(paths, path => Assert.Equal(normalizedBytes[path], File.ReadAllBytes(path)));
        Assert.Equal(backups, Directory.GetFiles(Path.Combine(_root, ".compat-backup")).OrderBy(path => path));

        // 只复制配置 JSON：公版不需要 .compat-state，也不调用任何茶包投影或导出入口。
        var publicCopy = Path.Combine(_root, "public-copy");
        Directory.CreateDirectory(publicCopy);
        foreach (var path in paths)
        {
            var copied = Path.Combine(publicCopy, Path.GetFileName(path));
            File.Copy(path, copied);
            AssertEquivalent(expected[path], ReadWithPublicModel(copied));
        }
        Assert.False(Directory.Exists(Path.Combine(publicCopy, ".compat-state")));
    }

    [Fact]
    public void PreparingDirectoryPreservesBadFilesAndContinuesWithOtherConfigs()
    {
        var malformed = Seed("坏JSON", "{ invalid json");
        var mixed = Seed("混合形状", """
            {"TaskEnabledList":{"1":{"Item1":true,"Item2":"领取邮件"},"2":false}}
            """);
        var invalidStart = Seed("失效起点", """
            {"NextTaskIndex":2,"TaskEnabledList":{"1":{"Item1":true,"Item2":"领取邮件"}}}
            """);
        var valid = Seed("正常配置", LegacyTuple);
        var badFiles = new[] { malformed, mixed, invalidStart };
        var originals = badFiles.ToDictionary(path => path, File.ReadAllBytes);
        var failures = new List<(string Path, Exception Error)>();

        OneDragonCompatibility.PrepareDirectoryForPublic(_root, (path, error) => failures.Add((path, error)));

        Assert.Equal(badFiles.OrderBy(Path.GetFileName), failures.Select(failure => failure.Path).OrderBy(Path.GetFileName));
        Assert.All(failures, failure => Assert.NotNull(failure.Error));
        Assert.All(badFiles, path => Assert.Equal(originals[path], File.ReadAllBytes(path)));
        var loaded = ReadWithPublicModel(valid);
        Assert.Equal(3, loaded.TaskOrder.Count);
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".compat-backup")));
        Assert.StartsWith(Path.GetFileName(valid) + ".", Path.GetFileName(backup));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadingForPublicUsePersistsConsumedStartWithoutRequiringSidecar(bool legacy)
    {
        var path = legacy ? Seed("旧调度", LegacyTuple) : Seed("公版配置", PublicCurrent);
        var source = OneDragonCompatibility.Read(path);
        Assert.NotEmpty(source.Config.NextTaskId);
        OneDragonCompatibility.ConsumeStartMarker(source, source.Config.NextTaskId);
        Assert.Empty(OneDragonCompatibility.Read(path).Config.NextTaskId);

        var normalized = OneDragonCompatibility.ReadForPublicUse(path);

        Assert.Empty(normalized.Config.NextTaskId);
        Assert.Empty(ReadWithPublicModel(path).NextTaskId);
        Assert.Equal(TaskConfigurationContract.Revision(File.ReadAllBytes(path)), normalized.Revision);
        var copyDirectory = Path.Combine(_root, "public-copy");
        Directory.CreateDirectory(copyDirectory);
        var copied = Path.Combine(copyDirectory, Path.GetFileName(path));
        File.Copy(path, copied);
        Assert.Empty(ReadWithPublicModel(copied).NextTaskId);
        Assert.False(Directory.Exists(Path.Combine(copyDirectory, ".compat-state")));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(bytes, OneDragonCompatibility.ReadForPublicUse(path).Bytes);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExistingPublicConfigKeepsExactBytesAndDoesNotCreateCompatibilityFiles()
    {
        var path = Seed("公版配置", PublicCurrent, bom: true, crlf: true);
        var bytes = File.ReadAllBytes(path);

        var first = OneDragonCompatibility.ReadForPublicUse(path);
        var second = OneDragonCompatibility.ReadForPublicUse(path);

        AssertEquivalent(ReadWithPublicModel(path), first.Config);
        Assert.Equal(bytes, first.Bytes);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(_root, ".compat-backup")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".compat-state")));
    }

    [Fact]
    public void PreparingPublicFilesKeepsEnhancedPlanIdentityAccountAndConditions()
    {
        var bound = Seed("旧调度", LegacyTuple);
        var unbound = Seed("未绑定", LegacyTuple
            .Replace("\"Name\":\"旧调度\"", "\"Name\":\"未绑定\"", StringComparison.Ordinal)
            .Replace("\"IndexId\":4", "\"IndexId\":5", StringComparison.Ordinal)
            .Replace("\"NextConfiguration\":true", "\"NextConfiguration\":false", StringComparison.Ordinal)
            .Replace("\"AccountBinding\":true", "\"AccountBinding\":false", StringComparison.Ordinal));
        var paths = new[] { bound, unbound };
        var before = ProjectCompatiblePlan(paths);

        OneDragonCompatibility.PrepareDirectoryForPublic(_root, (_, error) => throw error);

        var after = ProjectCompatiblePlan(paths);
        var oldPlan = PlanWithoutRevisionOrActivation(before.PlanJson);
        var newPlan = PlanWithoutRevisionOrActivation(after.PlanJson);
        Assert.True(JToken.DeepEquals(oldPlan, newPlan), "规范化改变了增强计划的节点、账号或周期条件");
        var nodes = ((JArray)newPlan["nodes"]!).Children<JObject>().ToArray();
        var boundNode = nodes.Single(node => node["ref"]!["config"]!.Value<string>() == "旧调度");
        var account = ((JArray)boundNode["strategies"]!).Children<JObject>()
            .Single(strategy => strategy["kind"]!.Value<string>() == "prerequisite.account");
        Assert.Equal("123456789", account["uid"]!.Value<string>());
        Assert.Equal("local-only", account["bindingCode"]!.Value<string>());
        Assert.True(nodes.Single(node => node["ref"]!["config"]!.Value<string>() == "未绑定")
            ["legacyFiltered"]!.Value<bool>());
        Assert.Equal(boundNode["nodeId"]!.Value<string>(), newPlan["watermark"]!["entryNodeId"]!.Value<string>());
        Assert.NotEqual(before.SourceHashes[0], after.SourceHashes[0]);
    }

    [Fact]
    public void PreparingMissingDirectoryDoesNotCreateItOrReportAnEmptyFailure()
    {
        var missing = Path.Combine(_root, "not-created");
        var failures = new List<Exception>();

        OneDragonCompatibility.PrepareDirectoryForPublic(missing, (_, error) => failures.Add(error));

        Assert.False(Directory.Exists(_root));
        Assert.Empty(failures);
    }

    private string Seed(string name, string json, bool bom = false, bool crlf = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".json");
        var text = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (crlf) text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    private static OneDragonFlowConfig ReadWithPublicModel(string path)
        => JsonConvert.DeserializeObject<OneDragonFlowConfig>(File.ReadAllText(path))
           ?? throw new InvalidDataException("公版模型未能读取配置");

    private static void AssertEquivalent(OneDragonFlowConfig expected, OneDragonFlowConfig actual)
        => Assert.True(JToken.DeepEquals(JObject.FromObject(expected), JObject.FromObject(actual)),
            "直接读取的公版配置与茶包此前的有效投影不同");

    private static void AssertExactBackup(string path, byte[] original)
    {
        var backup = Path.Combine(Path.GetDirectoryName(path)!, ".compat-backup",
            Path.GetFileName(path) + "." + TaskConfigurationContract.Revision(original) + ".bak");
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    private static JObject PlanWithoutRevisionOrActivation(string json)
    {
        var plan = JObject.Parse(json);
        plan.Remove("activation");
        foreach (var node in ((JArray)plan["nodes"]!).Children<JObject>())
            ((JObject)node["ref"]!).Remove("revision");
        return plan;
    }

    private static (string PlanJson, string[] SourceHashes) ProjectCompatiblePlan(string[] paths)
    {
        // BGI 和助手都链接迁移器源码；明确调用 BGI 程序集中的实际实现，避免同名类型冲突。
        var engine = typeof(OneDragonFlowConfig).Assembly.GetType("OneDragonMigration.Core.OneDragonMigrationEngine", throwOnError: true)!;
        var method = engine.GetMethod("ProjectCompatiblePlans", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var result = method.Invoke(null, new object?[] { paths, null })!;
        var plans = Assert.IsAssignableFrom<IEnumerable<System.Text.Json.Nodes.JsonObject>>(
            result.GetType().GetProperty("Plans")!.GetValue(result));
        var configs = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            result.GetType().GetProperty("Configs")!.GetValue(result));
        var hashes = configs.Cast<object>().Select(config =>
        {
            var source = config.GetType().GetProperty("Source")!.GetValue(config)!;
            return (string)source.GetType().GetProperty("Sha256")!.GetValue(source)!;
        }).ToArray();
        return (Assert.Single(plans).ToJsonString(), hashes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
