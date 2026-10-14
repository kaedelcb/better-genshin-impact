using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Automation;
using BetterGenshinImpact.Service.RemoteEditor;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public sealed class RemoteResourceEditorWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT") ?? Path.GetTempPath(),
        "remote-resource-workspace-" + Guid.NewGuid().ToString("N"));
    private readonly string _session = Guid.NewGuid().ToString("N");
    private bool _preserveRoot;
    private string Workspace => Path.Combine(_root, "RemoteResourceEditors", _session);
    public void Dispose() { if (!_preserveRoot && Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Seed(Action<RemoteResourceEditorLaunch.Input>? change = null, string? resourceJson = null)
    {
        var user = Path.Combine(Workspace, "User");
        Directory.CreateDirectory(Path.Combine(user, "OneDragon"));
        File.WriteAllText(Path.Combine(user, "config.json"), "{}");
        File.WriteAllText(Path.Combine(user, "OneDragon", "原资源.json"),
            resourceJson ?? "{\"Name\":\"原资源\",\"value\":1,\"unknown\":\"keep\"}");
        var files = new[] { "config.json", "OneDragon/原资源.json" }.Select(path =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(user, path))));
            return new RemoteResourceEditorLaunch.InputFile(path, hash, hash, true);
        }).ToList();
        var input = new RemoteResourceEditorLaunch.Input
        {
            Schema = RemoteResourceEditorLaunch.Schema, Version = 1, SessionId = _session,
            OwnerSid = WindowsIdentity.GetCurrent().User!.Value,
            TargetUid = "isolated-user", TargetInstanceId = "isolated-executor",
            TargetName = "执行端", ResourceName = "原资源", OneDragon = true, Files = files
        };
        change?.Invoke(input);
        var manifest = Path.Combine(Workspace, "workspace.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(input));
        return manifest;
    }

    private static RemoteResourceEditorLaunch Parse(string manifest)
        => RemoteResourceEditorLaunch.Parse(["BetterGI.exe", RemoteResourceEditorLaunch.Argument, manifest],
            WindowsIdentity.GetCurrent().User!.Value)!;

    [Fact]
    public void NormalLaunchDoesNotSelectRemoteWorkspace()
        => Assert.Null(RemoteResourceEditorLaunch.Parse(["BetterGI.exe"], "unused"));

    [Theory]
    [InlineData("owner")]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("script-write")]
    [InlineData("null-file")]
    public void InvalidWorkspaceCannotFallBackToNormalUser(string defect)
    {
        var manifest = Seed(input =>
        {
            if (defect == "owner") input.OwnerSid = "another-user";
            if (defect == "path") input.Files[0] = input.Files[0] with { Path = "../config.json" };
            if (defect == "hash") input.Files[0] = input.Files[0] with { ContentHash = new string('0', 64) };
            if (defect == "script-write") input.Files[0] = input.Files[0] with { Path = "JsScript/main.js" };
            if (defect == "null-file") input.Files[0] = null!;
        });
        Assert.Throws<InvalidDataException>(() => Parse(manifest));
    }

    [Fact]
    public void ChangedReadOnlyScriptIsNotReturnedAsAConfigurationWrite()
    {
        var manifest = Seed(input =>
        {
            var path = Path.Combine(Workspace, "User", "JsScript", "example", "main.js");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "const source = 1;");
            input.Files.Add(new("JsScript/example/main.js",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), null, false));
        });
        var launch = Parse(manifest);
        launch.CaptureBaseline();
        File.WriteAllText(launch.ResolveFile("JsScript/example/main.js"), "const source = 2;");
        Assert.Throws<InvalidDataException>(() => launch.CaptureChanges());
        Assert.False(File.Exists(launch.ResultPath));
        launch.PublishResult(false);
    }

    [Fact]
    public void NativeNormalizationDoesNotBecomeUserChange_AndResultKeepsAuthorityRevision()
    {
        var launch = Parse(Seed());
        var resource = launch.ResolveFile("OneDragon/原资源.json");
        var expected = launch.Metadata.Files.Single(file => file.Path == "OneDragon/原资源.json").ExpectedRevision;
        File.WriteAllText(resource, "{\"Name\":\"原资源\",\"value\":1,\"nativeDefault\":true}");
        launch.CaptureBaseline();
        Assert.Empty(launch.CaptureChanges());
        File.WriteAllText(resource, "{\"Name\":\"原资源\",\"value\":2,\"nativeDefault\":true}");
        var change = Assert.Single(launch.CaptureChanges());
        Assert.Equal(expected, change.ExpectedRevision);
        Assert.Contains("\"value\":1", change.BaselineJson);
        Assert.Contains("\"value\":2", change.Json);
        Assert.True(File.Exists(Path.Combine(Workspace, "baseline.json")));
        launch.PublishResult(true, [change]);
        Assert.Throws<InvalidOperationException>(() => launch.PublishResult(false));
        Assert.Throws<InvalidDataException>(() => Parse(Path.Combine(Workspace, "workspace.json")));
    }

    [Fact]
    public async Task IsolatedNativeEditor_UsesOriginalOneDragonUi_AndReturnsEditedCopy()
    {
        _preserveRoot = true;
        const string id = "3ccda867-472f-4c86-bb13-7d209e0a6609";
        var manifest = Seed(input => input.HiddenForTest = true,
            "{\"Name\":\"原资源\",\"TaskEnabledList\":{\"" + id + "\":true},"
            + "\"TaskDefinitions\":{\"" + id + "\":\"领取邮件\"},\"TaskOrder\":[\"" + id + "\"]}");
        var exe = Path.Combine(AppContext.BaseDirectory, "BetterGI.exe");
        Assert.True(File.Exists(exe), "先在独立输出目录构建完整 BGI 原版编辑器，再运行此入口测试");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _root, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add(RemoteResourceEditorLaunch.Argument);
        start.ArgumentList.Add(manifest);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            var ready = Path.Combine(Workspace, "ready.json");
            for (var attempt = 0; attempt < 120 && !File.Exists(ready) && !child.HasExited; attempt++)
                await Task.Delay(250);
            if (!File.Exists(ready))
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync();
                Assert.Fail("原版编辑窗口未就绪。副本保留：" + Workspace + "\n" + await output + "\n" + await errors);
            }
            using (var readyJson = JsonDocument.Parse(File.ReadAllText(ready)))
            {
                Assert.Equal(child.Id, readyJson.RootElement.GetProperty("processId").GetInt32());
                Assert.Equal(Path.Combine(Workspace, "User"), readyJson.RootElement.GetProperty("userRoot").GetString());
            }
            child.Refresh();
            var window = AutomationElement.FromHandle(child.MainWindowHandle);
            Assert.NotNull(window);
            var toggles = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true));
            Assert.True(toggles.Count > 0, "原版一条龙任务列表没有可操作的启用开关");
            ((TogglePattern)toggles[0].GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            await Task.Delay(200);
            var save = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "保存并回传"));
            Assert.NotNull(save);
            ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var result = Path.Combine(Workspace, "result.json");
            for (var attempt = 0; attempt < 40 && !File.Exists(result); attempt++) await Task.Delay(100);
            Assert.True(File.Exists(result), "原版编辑窗口未发布保存结果，副本保留：" + Workspace);
            using var resultJson = JsonDocument.Parse(File.ReadAllText(result));
            Assert.Equal("saved", resultJson.RootElement.GetProperty("state").GetString());
            var changed = Assert.Single(resultJson.RootElement.GetProperty("changes").EnumerateArray(),
                item => item.GetProperty("Path").GetString() == "OneDragon/原资源.json");
            using var edited = JsonDocument.Parse(changed.GetProperty("Json").GetString()!);
            var enabled = edited.RootElement.EnumerateObject().Single(property =>
                property.Name.Equals("taskEnabledList", StringComparison.OrdinalIgnoreCase)).Value;
            Assert.False(enabled.GetProperty(id).GetBoolean());
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
            // 保留真实窗口的工作区、基线和回传结果，避免退出后的短暂文件占用掩盖功能结果。
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
        }
    }
}
