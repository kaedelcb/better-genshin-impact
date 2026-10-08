using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using OneDragonMigration.Core;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class LegacyAutoCompatibilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-autocompat-" + Guid.NewGuid().ToString("N"));
    private string Seed(string file = "旧配置", string plan = "旧计划")
    {
        var user = Path.Combine(_root, "User"); Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        var path = Path.Combine(user, "OneDragon", file + ".json");
        File.WriteAllText(path, $$$$"""
        {"Name":"{{{{file}}}}","ScheduleName":"{{{{plan}}}}","Version":1,"IndexId":2,"AccountBinding":true,
         "GenshinUid":"123456789","PeriodList":{"周三":true},"NextConfiguration":true,"NextTaskIndex":2,
         "TaskEnabledList":{"5":{"Item1":true,"Item2":"领取邮件"},"2":{"Item1":true,"Item2":"领取邮件"}}}
        """);
        return path;
    }
    private LegacyCompatibilityResult Sync(WorkflowStore store)
        => LegacyAutoCompatibilityService.Synchronize(Path.Combine(_root, "User"), Path.Combine(_root, "index"), store, _ => true);

    [Fact]
    public void NormalOpeningCreatesReadyPlanWithOriginalRevisionAndNeverTouchesSource()
    {
        var file = Seed(); var original = File.ReadAllBytes(file); var store = new WorkflowStore(Path.Combine(_root, "flows"));
        Assert.Equal(1, Sync(store).Added);
        var entry = Assert.Single(store.List()); var doc = store.Load(entry.WorkflowId);
        Assert.Equal("active", doc.Activation!.Status);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), Assert.Single(doc.Nodes).Ref!.Revision);
        Assert.True(doc.ExtensionData!["watermark"].GetProperty("once").GetBoolean());
        Assert.Contains(doc.Nodes[0].Strategies, s => s.Kind == "prerequisite.account");
        Assert.Contains(doc.Nodes[0].Strategies, s => s.Kind == "condition.weekdays");
        Assert.Equal(0, Sync(store).Added); Assert.Single(store.List()); Assert.Equal(original, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(_root, "runs")));
    }

    [Fact]
    public void SourceUpdateRefreshesUntouchedPlanButPreservesUserEditedPlan()
    {
        var file = Seed(); var store = new WorkflowStore(Path.Combine(_root, "flows")); Sync(store);
        var entry = Assert.Single(store.List());
        File.AppendAllText(file, " "); Assert.Equal(1, Sync(store).Refreshed);
        var edited = store.LoadSnapshot(entry.WorkflowId); edited.Document.Name = "我修改的计划";
        store.Save(edited.Document, edited.Revision);
        File.AppendAllText(file, " "); var result = Sync(store);
        Assert.Equal(0, result.Refreshed); Assert.Equal("我修改的计划", store.Load(entry.WorkflowId).Name);
        Assert.Contains(result.Notices, n => n.Contains("已有计划的修改"));
    }

    [Fact]
    public void ACorruptPlanDoesNotPreventASeparateLegalPlan()
    {
        Seed("合法配置", "正常计划"); var bad = Seed("坏配置", "坏计划");
        File.WriteAllText(bad, "{\"Name\":\"坏配置\",\"ScheduleName\":\"坏计划\",\"TaskEnabledList\":[]}");
        var original = File.ReadAllBytes(bad); var store = new WorkflowStore(Path.Combine(_root, "flows"));
        var result = Sync(store);
        Assert.Equal("正常计划", Assert.Single(store.List()).Name);
        Assert.NotEmpty(result.Notices); Assert.Equal(original, File.ReadAllBytes(bad));
    }

    [Fact]
    public void UnidentifiedCorruptFileDoesNotGetAssignedToLegalDefaultPlan()
    {
        var user = Path.Combine(_root, "User"); Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        var good = Path.Combine(user, "OneDragon", "普通旧配置.json");
        var bad = Path.Combine(user, "OneDragon", "无法识别.json");
        File.WriteAllText(good, "{\"Name\":\"普通旧配置\",\"TaskEnabledList\":{\"领取邮件\":true}}");
        File.WriteAllText(bad, "{not valid JSON");
        var originals = new[] { good, bad }.ToDictionary(p => p, File.ReadAllBytes);
        var store = new WorkflowStore(Path.Combine(_root, "flows"));
        var result = Sync(store);
        Assert.Equal(1, result.Added);
        var entry = Assert.Single(store.List());
        Assert.Equal("默认计划", entry.Name);
        Assert.Equal("active", store.Load(entry.WorkflowId).Activation!.Status);
        Assert.Contains(result.Notices, n => n.Contains("无法识别.json"));
        foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
    }

    [Fact]
    public void PackagePipeScopeKeepsUserIsolationAndLegacyDefault()
    {
        var one = Path.Combine(_root, "one"); var two = Path.Combine(_root, "two");
        Directory.CreateDirectory(one); Directory.CreateDirectory(two);
        Assert.Equal("BetterGI.v2.user-SID.root", InstallationPipeScope.ResolveRootPipe("SID", one));
        const string marker = "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}";
        File.WriteAllText(Path.Combine(one, InstallationPipeScope.MarkerFile), marker);
        File.WriteAllText(Path.Combine(two, InstallationPipeScope.MarkerFile), marker);
        var pipe = InstallationPipeScope.ResolveRootPipe("SID", one);
        Assert.Equal(pipe, InstallationPipeScope.ResolveRootPipe("SID", one + Path.DirectorySeparatorChar));
        Assert.NotEqual(pipe, InstallationPipeScope.ResolveRootPipe("SID", two));
        Assert.NotEqual(pipe, InstallationPipeScope.ResolveRootPipe("OTHER-SID", one));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
