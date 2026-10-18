using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using OneDragonMigration.Core;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferPackageRootsRegressionTests
{
    [Fact]
    public void RealServiceRoundTripUsesBothPackagesDeclaredUserRootsAndPreservesDecoys()
    {
        using var fixture = new Fixture();
        var sourceExe = fixture.Install("source-install", "source-user");
        var targetExe = fixture.Install("target-install", "target-user");
        fixture.Config("A", sourceExe, "source");
        fixture.Config("B", targetExe, "local");
        var sourceBytes = Encoding.UTF8.GetBytes("{ // official BGI format\n \"name\":\"外部源\", \"projects\":[], }");
        fixture.Write("source-user/ScriptGroup/外部源.json", sourceBytes);
        fixture.Write("source-install/User/ScriptGroup/诱饵.json", Encoding.UTF8.GetBytes("{\"projects\":[]}"));
        var decoy = Encoding.UTF8.GetBytes("{\"name\":\"目标原数据\",\"projects\":[]}");
        fixture.Write("target-install/User/ScriptGroup/目标原数据.json", decoy);
        var package = fixture.PathFor("settings.zip");
        var exported = new ConfigTransferService(fixture.PathFor("A")).Export(package);
        Assert.True(exported.Success, exported.Message);
        using (var archive = ZipFile.OpenRead(package))
        {
            Assert.NotNull(archive.GetEntry("bgi-user/ScriptGroup/外部源.json"));
            Assert.Null(archive.GetEntry("bgi-user/ScriptGroup/诱饵.json"));
        }
        var imported = new ConfigTransferService(fixture.PathFor("B")).Import(package);
        Assert.True(imported.Success, imported.Message);
        Assert.Equal(sourceBytes, File.ReadAllBytes(fixture.PathFor("target-user/ScriptGroup/外部源.json")));
        Assert.Equal(decoy, File.ReadAllBytes(fixture.PathFor("target-install/User/ScriptGroup/目标原数据.json")));
        Assert.False(File.Exists(fixture.PathFor("target-install/User/ScriptGroup/外部源.json")));
    }

    [Fact]
    public void BrokenMarkerRejectsExportWithoutReplacingAnExistingBackup()
    {
        using var fixture = new Fixture();
        var exe = fixture.Install("source-install", "source-user");
        fixture.Config("A", exe, "source");
        fixture.Write("source-install/" + InstallationPipeScope.MarkerFile, Encoding.UTF8.GetBytes("{ broken"));
        var original = Encoding.UTF8.GetBytes("existing backup must remain byte-for-byte");
        fixture.Write("settings.zip", original);
        var result = new ConfigTransferService(fixture.PathFor("A")).Export(fixture.PathFor("settings.zip"));
        Assert.False(result.Success);
        Assert.Contains("标记", result.Message);
        Assert.Equal(original, File.ReadAllBytes(fixture.PathFor("settings.zip")));
    }

    [Fact]
    public void BrokenLocalMarkerRejectsResourceImportBeforeAnyTargetConfigurationChanges()
    {
        using var fixture = new Fixture();
        var sourceExe = fixture.Install("source-install", "source-user");
        var targetExe = fixture.Install("target-install", "target-user");
        fixture.Config("A", sourceExe, "source");
        fixture.Config("B", targetExe, "local");
        fixture.Write("source-user/ScriptGroup/G.json", Encoding.UTF8.GetBytes("{\"name\":\"G\",\"projects\":[]}"));
        var package = fixture.PathFor("settings.zip");
        Assert.True(new ConfigTransferService(fixture.PathFor("A")).Export(package).Success);
        fixture.Write("target-install/" + InstallationPipeScope.MarkerFile, Encoding.UTF8.GetBytes("{}"));
        var before = File.ReadAllBytes(fixture.PathFor("B/assistant-config.json"));
        var result = new ConfigTransferService(fixture.PathFor("B")).Import(package);
        Assert.False(result.Success);
        Assert.Contains("标记", result.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.PathFor("B/assistant-config.json")));
        Assert.False(File.Exists(fixture.PathFor("target-user/ScriptGroup/G.json")));
        Assert.False(File.Exists(fixture.PathFor("target-install/User/ScriptGroup/G.json")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ConfigTransferPackageRoots-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(_root);
        internal string PathFor(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Write(string relative, byte[] bytes)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        internal void Config(string folder, string executable, string instance)
            => Write(folder + "/assistant-config.json", JsonSerializer.SerializeToUtf8Bytes(new AssistConfig
            { BgiPath = executable, ClientInstanceId = instance, PlayerUid = "123456789", StandaloneMode = true, ServerUrl = "" }));
        internal string Install(string folder, string data)
        {
            Directory.CreateDirectory(PathFor(data));
            Write(folder + "/BetterGI.exe", [0]); // Never launched.
            Write(folder + "/" + InstallationPipeScope.MarkerFile, JsonSerializer.SerializeToUtf8Bytes(new
            { schema = "mistletoe.local-package", schemaVersion = 1, ipcIsolation = true, userRoot = PathFor(data) }));
            return PathFor(folder + "/BetterGI.exe");
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("ConfigTransferPackageRoots-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refuse to remove anything outside the fixture.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
