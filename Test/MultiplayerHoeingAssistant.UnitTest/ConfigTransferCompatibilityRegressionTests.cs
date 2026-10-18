using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferCompatibilityRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "config-transfer-compat-" + Guid.NewGuid().ToString("N"));

    public ConfigTransferCompatibilityRegressionTests() => Directory.CreateDirectory(_root);

    private byte[] Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    [Fact]
    public void ExportRootPathingCreatedByOfficialUiPreservesOriginalResourceBytes()
    {
        var group = Write("ScriptGroup/日常.json", "{\"projects\":[{\"type\":\"Pathing\",\"folderName\":\".\",\"name\":\"沙漠tp测试.json\"}]}");
        var route = Write("AutoPathing/沙漠tp测试.json", "{\"positions\":[]}\r\n");

        var files = ConfigTransferResources.PlanExport(_root);

        Assert.Equal(group, files["bgi-user/ScriptGroup/日常.json"]);
        Assert.Equal(route, files["bgi-user/AutoPathing/沙漠tp测试.json"]);
    }

    [Theory]
    [InlineData(".", "route.json", "route.json")]
    [InlineData("", "route.json", "route.json")]
    [InlineData("sub\\nested/..", "route.json", "sub/route.json")]
    [InlineData("sub/./nested\\..", ".\\route.json", "sub/route.json")]
    [InlineData("sub", "../route.json", "route.json")]
    public void ConfigReferenceNormalizationStaysInsideItsBusinessRoot(string folder, string name, string expected)
    {
        Write("ScriptGroup/G.json", JsonSerializer.Serialize(new { projects = new[] { new { type = "Pathing", folderName = folder, name } } }));
        var route = Write("AutoPathing/" + expected, "{\"positions\":[]}\r\n");

        var files = ConfigTransferResources.PlanExport(_root);
        Assert.Equal(route, files["bgi-user/AutoPathing/" + expected]);
        var plan = ConfigTransferResources.PlanImport(files, Path.Combine(_root, "destination"));
        Assert.Contains(plan.TargetFiles.Keys, p => p.EndsWith(expected.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../KeyMouseScript", "route.json")]
    [InlineData("sub/../..", "route.json")]
    [InlineData("sub", "../../route.json")]
    [InlineData(".", "C:\\outside.json")]
    [InlineData("C:\\outside", "route.json")]
    public void ConfigReferenceNormalizationRejectsLeavingBusinessRoot(string folder, string name)
    {
        Write("ScriptGroup/G.json", JsonSerializer.Serialize(new { projects = new[] { new { type = "Pathing", folderName = folder, name } } }));
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanExport(_root));
    }

    [Theory]
    [InlineData("bgi-user/AutoPathing/./route.json")]
    [InlineData("bgi-user/AutoPathing/sub/../route.json")]
    [InlineData("bgi-user/AutoPathing/../KeyMouseScript/route.json")]
    [InlineData("bgi-user/AutoPathing\\route.json")]
    public void PhysicalPackageEntriesRemainStrictDespiteCompatibleConfigReferences(string entry)
    {
        Assert.False(ConfigTransferResources.IsResourceEntry(entry));
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanImport(
            new Dictionary<string, byte[]> { [entry] = Encoding.UTF8.GetBytes("{}") }, _root));
    }

    [Fact]
    public void BgiJsonCommentsTrailingCommasAndSnakeCaseSettingsUiKeepOriginalBytes()
    {
        var group = Write("ScriptGroup/G.json", "\uFEFF{ // BGI config allows comments\n\"projects\":[{\"type\":\"Javascript\",\"folderName\":\"X\",},],}\r\n");
        var manifest = Write("JsScript/X/manifest.json", "{ /* official snake_case */ \"name\":\"X\",\"main\":\"./main.js\",\"settings_ui\":\"ui/settings.json\",}\r\n");
        Write("JsScript/X/main.js", "// original third-party script\r\n");
        var settings = Write("JsScript/X/ui/settings.json", "[ /* preserve */ ]\r\n");

        var files = ConfigTransferResources.PlanExport(_root);

        Assert.Equal(group, files["bgi-user/ScriptGroup/G.json"]);
        Assert.Equal(manifest, files["bgi-user/JsScript/X/manifest.json"]);
        Assert.Equal(settings, files["bgi-user/JsScript/X/ui/settings.json"]);
        Assert.Throws<InvalidDataException>(() => ConfigTransferResources.PlanImport(
            files.Where(p => p.Key != "bgi-user/JsScript/X/ui/settings.json").ToDictionary(p => p.Key, p => p.Value), _root));
    }

    [Fact]
    public void BusinessLogLockAndTemporaryFilesArePreservedAndOnlyExactHostSidecarIsExcluded()
    {
        var log = Write("JsScript/X/data/input.log", "user business state\r\n");
        var lockBytes = Write("JsScript/X/data/input.lock", "business lock file\r\n");
        var temporary = Write("JsScript/X/data/input.tmp", "recoverable business state\r\n");
        var similar = Write("JsScript/X/data/input.nexus-config-write.lock", "not the reserved sidecar\r\n");
        Write("JsScript/X/.nexus-config-write.lock", "host lock");

        var files = ConfigTransferResources.PlanExport(_root);

        Assert.Equal(log, files["bgi-user/JsScript/X/data/input.log"]);
        Assert.Equal(lockBytes, files["bgi-user/JsScript/X/data/input.lock"]);
        Assert.Equal(temporary, files["bgi-user/JsScript/X/data/input.tmp"]);
        Assert.Equal(similar, files["bgi-user/JsScript/X/data/input.nexus-config-write.lock"]);
        Assert.DoesNotContain("bgi-user/JsScript/X/.nexus-config-write.lock", files.Keys);
        Assert.False(ConfigTransferResources.IsResourceEntry("bgi-user/JsScript/X/.nexus-config-write.lock"));
        var destination = Path.Combine(_root, "destination");
        var imported = ConfigTransferResources.PlanImport(files, destination);
        Assert.Equal(files.Count, imported.TargetFiles.Count);
        Assert.Equal(log, imported.TargetFiles[Path.Combine(destination, "JsScript", "X", "data", "input.log")]);
    }

    [Fact]
    public void ExportSnapshotsBusinessLogWhileProducerKeepsItsWriteHandleOpenWithoutChangingSource()
    {
        var original = Write("JsScript/X/run.log", "running script business state\r\n");
        var path = Path.Combine(_root, "JsScript", "X", "run.log");
        using (var producer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            producer.Seek(0, SeekOrigin.End);
            var originalPosition = producer.Position;

            var files = ConfigTransferResources.PlanExport(_root);

            Assert.Equal(original, files["bgi-user/JsScript/X/run.log"]);
            Assert.Equal(original.Length, producer.Length);
            Assert.Equal(originalPosition, producer.Position);
            var target = Path.Combine(_root, "destination");
            var imported = ConfigTransferResources.PlanImport(files, target);
            Assert.Equal(original, imported.TargetFiles[Path.Combine(target, "JsScript", "X", "run.log")]);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
