using System.Text;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.ViewModel.Pages;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public sealed class TaskConfigurationLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-layout-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OneDragonLayoutSavesOrderEnabledAndStartWithoutLosingOtherFields()
    {
        var path = Seed("OneDragon", "daily", """
            {"Name":"daily","TaskEnabledList":{"a":true,"b":true,"c":true},
             "TaskDefinitions":{"a":"邮件","b":"锻造","c":"委托"},
             "TaskOrder":["a","b","c"],"NextTaskId":"","unknown":{"keep":42}}
            """);
        var store = new TaskConfigurationContract(_root);
        var before = await store.ReadAsync("daily", true);
        var response = await ExternalInterfaceConfigurationPlane.DispatchAsync(
            InstanceIpcEnvelope.Request(ExternalInterfaceOperations.ConfigApplyTaskLayout, new
            {
                configName = "daily", expectedConfigRevision = before.Revision,
                tasks = new[] { new { taskId = "c", enabled = true }, new { taskId = "a", enabled = true },
                    new { taskId = "b", enabled = false } }, startTaskId = "c",
            }), store);
        Assert.True(response.Success, response.ErrorMessage);
        var after = await store.ReadAsync("daily", true);
        Assert.Equal(after.Revision, response.Data!["configRevision"]!.Value<string>());
        Assert.Equal(new[] { "c", "a", "b" }, after.Tasks.Select(task => task.TaskId));
        Assert.False(after.Tasks[2].Enabled);
        Assert.Equal("c", TaskConfigurationContract.ReadStartTaskId(after, true));
        var disk = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(42, disk["unknown"]!["keep"]!.Value<int>());
        Assert.Equal(new[] { "c", "a", "b" }, disk["TaskOrder"]!.Values<string>());

        var stale = await ExternalInterfaceConfigurationPlane.DispatchAsync(
            InstanceIpcEnvelope.Request(ExternalInterfaceOperations.ConfigApplyTaskLayout, new
            {
                configName = "daily", expectedConfigRevision = before.Revision,
                tasks = new[] { new { taskId = "a", enabled = true }, new { taskId = "b", enabled = true },
                    new { taskId = "c", enabled = true } }, startTaskId = (string?)null,
            }), store);
        Assert.Equal("configuration_changed", stale.ErrorCode);
        Assert.Equal(after.Revision, (await store.ReadAsync("daily", true)).Revision);
    }

    [Fact]
    public async Task GroupDuplicateTasksKeepIdentitiesAfterReorderAndStartUsesSavedProject()
    {
        var path = Seed("ScriptGroup", "group", """
            {"name":"group","projects":[
              {"name":"重复","folderName":"same","type":"Pathing","status":"Enabled","schedule":"A","index":1},
              {"name":"重复","folderName":"same","type":"Pathing","status":"Enabled","schedule":"B","index":2},
              {"name":"末尾","folderName":"last","type":"Pathing","status":"Enabled","schedule":"C","index":3}
            ],"unknown":"keep"}
            """);
        var store = new TaskConfigurationContract(_root);
        var before = await store.ReadAsync("group", false);
        Assert.Equal(3, before.Tasks.Select(task => task.TaskId).Distinct().Count());
        var first = before.Tasks[0].TaskId;
        var second = before.Tasks[1].TaskId;
        var last = before.Tasks[2].TaskId;
        var result = await store.ApplyLayoutAsync("group", false,
            [new(second, false), new(last, true), new(first, true)], first, before.Revision, null);
        var reread = await store.ReadAsync("group", false);
        Assert.Equal(result.Revision, reread.Revision);
        Assert.Equal(new[] { second, last, first }, reread.Tasks.Select(task => task.TaskId));
        Assert.Equal(first, TaskConfigurationContract.ReadStartTaskId(reread, false));
        var disk = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("keep", disk["unknown"]!.Value<string>());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "ScriptGroup", ".compat-backup"), "group.json.*.bak"));
        Assert.Equal(new[] { "B", "C", "A" }, disk["projects"]!.Select(task => task["schedule"]!.Value<string>()));
        Assert.Equal(new[] { 1, 2, 3 }, disk["projects"]!.Select(task => task["index"]!.Value<int>()));
        Assert.Equal(new[] { false, true, true }, disk["projects"]!.Select(task => task["status"]!.Value<string>() == "Enabled"));
        Assert.Equal(first, disk["taskCenterStartTaskId"]!.Value<string>());
        Assert.Equal(new[] { second, last, first }, disk["projects"]!.Select(task => task["taskCenterTaskId"]!.Value<string>()));
        var native = ScriptGroup.FromJson(File.ReadAllText(path));
        Assert.Equal(first, native.TaskCenterStartTaskId);
        Assert.Contains(native.Projects, project => project.TaskCenterTaskId == first && project.Status == "Enabled");
        ScriptControlViewModel.ApplyStartSelection(native, default);
        Assert.Equal(new[] { false, false, true }, native.Projects.Select(project => project.NextFlag == true));
        ScriptControlViewModel.ApplyStartSelection(native, (native.Name, 1, native.Projects[0].FolderName, native.Projects[0].Name));
        Assert.Equal(new[] { true, false, false }, native.Projects.Select(project => project.NextFlag == true));
        native.TaskCenterStartTaskId = "missing";
        Assert.Throws<InvalidOperationException>(() => ScriptControlViewModel.ApplyStartSelection(native, default));
    }

    [Fact]
    public async Task InvalidLayoutOrDisabledStartNeverWritesFile()
    {
        var path = Seed("OneDragon", "safe", """
            {"Name":"safe","TaskEnabledList":{"a":true,"b":true},
             "TaskDefinitions":{"a":"甲","b":"乙"},"TaskOrder":["a","b"]}
            """);
        var original = await File.ReadAllBytesAsync(path);
        var store = new TaskConfigurationContract(_root);
        var before = await store.ReadAsync("safe", true);
        var missing = await ExternalInterfaceConfigurationPlane.DispatchAsync(
            InstanceIpcEnvelope.Request(ExternalInterfaceOperations.ConfigApplyTaskLayout, new
            {
                configName = "safe", expectedConfigRevision = before.Revision,
                tasks = new[] { new { taskId = "a", enabled = true } }, startTaskId = (string?)null,
            }), store);
        Assert.Equal("invalid_task_layout", missing.ErrorCode);
        var disabled = await ExternalInterfaceConfigurationPlane.DispatchAsync(
            InstanceIpcEnvelope.Request(ExternalInterfaceOperations.ConfigApplyTaskLayout, new
            {
                configName = "safe", expectedConfigRevision = before.Revision,
                tasks = new[] { new { taskId = "a", enabled = false }, new { taskId = "b", enabled = true } }, startTaskId = "a",
            }), store);
        Assert.Equal("task_disabled_or_missing", disabled.ErrorCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExplicitlyReselectingConsumedOneDragonStartMakesItEffectiveAgain()
    {
        var path = Seed("OneDragon", "again", """
            {"Name":"again","TaskEnabledList":{"a":true},
             "TaskDefinitions":{"a":"邮件"},"TaskOrder":["a"],"NextTaskId":""}
            """);
        var store = new TaskConfigurationContract(_root);
        var original = await store.ReadAsync("again", true);
        var selected = await store.ApplyLayoutAsync("again", true, [new("a", true)], "a", original.Revision, null);
        OneDragonCompatibility.ConsumeStartMarker(OneDragonCompatibility.Read(path), "a");
        var consumed = await store.ReadAsync("again", true);
        Assert.Null(TaskConfigurationContract.ReadStartTaskId(consumed, true));
        var reselected = await store.ApplyLayoutAsync("again", true, [new("a", true)], "a", consumed.Revision, null);
        Assert.NotEqual(selected.Revision, reselected.Revision);
        Assert.Equal("a", TaskConfigurationContract.ReadStartTaskId(reselected, true));
    }

    [Fact]
    public async Task LayoutSaveRetainsOriginalBomAndCrLf()
    {
        var path = Seed("OneDragon", "format", """
            {"Name":"format","TaskEnabledList":{"a":true},
             "TaskDefinitions":{"a":"邮件"},"TaskOrder":["a"],"unknown":"keep"}
            """);
        var content = File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(path, content, new UTF8Encoding(true));
        var store = new TaskConfigurationContract(_root);
        var before = await store.ReadAsync("format", true);
        await store.ApplyLayoutAsync("format", true, [new("a", false)], null, before.Revision, null);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(new byte[] { 239, 187, 191 }, bytes.Take(3));
        Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal("keep", JObject.Parse(File.ReadAllText(path))["unknown"]!.Value<string>());
    }

    [Fact]
    public async Task LegacyOneDragonLayoutUsesProjectedStableIdsAndKeepsOriginalFields()
    {
        var path = Seed("OneDragon", "legacy", """
            {"Name":"legacy","Version":1,"ScheduleName":"旧计划","NextTaskIndex":2,
             "TaskEnabledList":{"5":{"Item1":true,"Item2":"邮件"},
                                "2":{"Item1":true,"Item2":"邮件"}},
             "UnknownFuture":{"keep":7}}
            """);
        var original = await File.ReadAllBytesAsync(path);
        var store = new TaskConfigurationContract(_root);
        var before = await store.ReadAsync("legacy", true);
        var first = before.Tasks[0].TaskId;
        var second = before.Tasks[1].TaskId;
        var saved = await store.ApplyLayoutAsync("legacy", true,
            [new(second, true), new(first, false)], second, before.Revision, null);
        var reread = await store.ReadAsync("legacy", true);
        Assert.Equal(saved.Revision, reread.Revision);
        Assert.Equal(new[] { second, first }, reread.Tasks.Select(task => task.TaskId));
        Assert.False(reread.Tasks[1].Enabled);
        Assert.Equal(second, TaskConfigurationContract.ReadStartTaskId(reread, true));
        var document = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("旧计划", document["ScheduleName"]!.Value<string>());
        Assert.Equal(7, document["UnknownFuture"]!["keep"]!.Value<int>());
        Assert.Null(document["NextTaskIndex"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(
            Directory.GetFiles(Path.Combine(_root, "OneDragon", ".compat-backup"), "legacy.json.*.bak"))));
    }

    private string Seed(string folder, string name, string json)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
