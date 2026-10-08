using System.Text;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Execution;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.CoreTests.OneDragon;

public sealed class OneDragonCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onedragon-compat-" + Guid.NewGuid().ToString("N"));
    private string Seed()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "旧配置.json");
        File.WriteAllText(file, "{\r\n\"Name\":\"旧配置\",\"Version\":1,\"ScheduleName\":\"旧计划\",\"AccountBinding\":true," +
            "\"GenshinUid\":\"123456789\",\"PeriodList\":{\"周三\":true},\"NextTaskIndex\":2," +
            "\"UnknownFuture\":{\"keep\":7},\"TaskEnabledList\":{\"5\":{\"Item1\":true,\"Item2\":\"领取邮件\"}," +
            "\"2\":{\"Item1\":true,\"Item2\":\"领取邮件\"},\"9\":{\"Item1\":false,\"Item2\":\"合成树脂\"}}\r\n}", new UTF8Encoding(true));
        return file;
    }

    [Fact]
    public void OpeningLegacyIsReadOnlyAndSharesStableTaskIdsWithIpc()
    {
        var file = Seed(); var bytes = File.ReadAllBytes(file);
        var first = OneDragonCompatibility.Read(file);
        var second = OneDragonCompatibility.Read(file);
        var ipc = TaskConfigurationContract.Parse(bytes, true, file);
        Assert.Equal(first.Config.TaskOrder, second.Config.TaskOrder);
        Assert.Equal(first.Config.TaskOrder, ipc.Tasks.Select(t => t.TaskId));
        Assert.Equal(3, first.Config.TaskOrder.Distinct().Count());
        Assert.Equal(new[] { "领取邮件", "领取邮件", "合成树脂" }, first.Config.TaskOrder.Select(id => first.Config.TaskDefinitions[id]));
        Assert.Equal(first.Config.TaskOrder[1], first.Config.NextTaskId);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(_root, ".compat-backup")));
    }

    [Fact]
    public void EditingPreservesLegacyFieldsExactBackupAndCurrentPublicIds()
    {
        var file = Seed(); var bytes = File.ReadAllBytes(file);
        var original = OneDragonCompatibility.Read(file);
        original.Config.TaskEnabledList[original.Config.TaskOrder[0]] = false;
        var saved = OneDragonCompatibility.Save(file, original.Config, original);
        var raw = JObject.Parse(File.ReadAllText(file));
        Assert.Equal("旧计划", raw["ScheduleName"]!.Value<string>());
        Assert.Equal("123456789", raw["GenshinUid"]!.Value<string>());
        Assert.True(raw["PeriodList"]!["周三"]!.Value<bool>());
        Assert.Equal(7, raw["UnknownFuture"]!["keep"]!.Value<int>());
        Assert.Equal(original.Config.TaskOrder, saved.Config.TaskOrder);
        Assert.False(saved.Config.TaskEnabledList[saved.Config.TaskOrder[0]]);
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(_root, ".compat-backup")))));
        Assert.True(File.ReadAllBytes(file).AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
    }

    [Fact]
    public void ExternalEditCannotBeOverwrittenByAnOlderPageSnapshot()
    {
        var file = Seed(); var loaded = OneDragonCompatibility.Read(file);
        var replacement = File.ReadAllText(file).Replace("\"keep\":7", "\"keep\":8");
        File.WriteAllText(file, replacement);
        var changedBytes = File.ReadAllBytes(file);
        Assert.Throws<InvalidOperationException>(() => OneDragonCompatibility.Save(file, loaded.Config, loaded));
        Assert.Equal(changedBytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void ExecutedTaskStartMarkerIsConsumedAcrossRestartWithoutChangingSourceRevision()
    {
        var file = Seed(); var source = OneDragonCompatibility.Read(file);
        var marker = source.Config.NextTaskId;
        OneDragonCompatibility.ConsumeStartMarker(source, marker);
        var restarted = OneDragonCompatibility.Read(file);
        Assert.Equal(string.Empty, restarted.Config.NextTaskId);
        Assert.Equal(source.Bytes, File.ReadAllBytes(file));
        Assert.Equal(source.Revision, restarted.Revision);
        var ipc = TaskConfigurationContract.Parse(File.ReadAllBytes(file), true, file);
        Assert.Equal(string.Empty, ipc.Document["NextTaskId"]!.Value<string>());
    }

    [Fact]
    public void ExplicitlySavingAnEmptyTaskListKeepsEnhancedFieldsAndExactOriginalBackup()
    {
        var file = Seed(); var original = OneDragonCompatibility.Read(file);
        original.Config.TaskOrder.Clear(); original.Config.TaskDefinitions.Clear(); original.Config.TaskEnabledList.Clear();
        original.Config.NextTaskId = string.Empty;
        OneDragonCompatibility.Save(file, original.Config, original);
        var restarted = OneDragonCompatibility.Read(file);
        Assert.Empty(restarted.Config.TaskOrder);
        var raw = JObject.Parse(File.ReadAllText(file));
        Assert.Null(raw["NextTaskIndex"]);
        Assert.Equal("旧计划", raw["ScheduleName"]!.Value<string>());
        Assert.True(raw["AccountBinding"]!.Value<bool>());
        Assert.Equal(original.Bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(_root, ".compat-backup")))));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
