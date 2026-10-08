using System.Text.Json;
using OneDragonMigration.Core;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class InstallationUserRootTests
{
    private static string DirectoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "package-user-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static void Marker(string package, object? userRoot, bool includeRoot = true)
    {
        var values = new Dictionary<string, object?> { ["schema"] = "mistletoe.local-package", ["schemaVersion"] = 1, ["ipcIsolation"] = true };
        if (includeRoot) values["userRoot"] = userRoot;
        File.WriteAllText(Path.Combine(package, InstallationPipeScope.MarkerFile), JsonSerializer.Serialize(values));
    }

    [Fact]
    public void UnmarkedOrOmittedUserRoot_KeepsPackageUserWithoutCreatingIt()
    {
        var package = DirectoryRoot();
        Assert.Equal(Path.Combine(package, "User"), InstallationPipeScope.ResolveUserRoot(package));
        Marker(package, null, includeRoot: false);
        Assert.Equal(Path.Combine(package, "User"), InstallationPipeScope.ResolveUserRoot(package));
        Assert.False(Directory.Exists(Path.Combine(package, "User")));
    }

    [Fact]
    public void ExplicitRoot_IsSharedAndSourceBytesAreUntouched()
    {
        var package = DirectoryRoot();
        var user = DirectoryRoot();
        var source = Path.Combine(user, "config.json");
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 123, 125, 13, 10 };
        File.WriteAllBytes(source, bytes);
        Marker(package, user + Path.DirectorySeparatorChar);
        Assert.Equal(user, InstallationPipeScope.ResolveUserRoot(package));
        Assert.True(InstallationPipeScope.IsIsolatedPackage(package));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.False(Directory.Exists(Path.Combine(package, "User")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    [InlineData("")]
    [InlineData("User")]
    [InlineData("C:\\not-existing-user-root-for-test")]
    [InlineData("\\\\server\\share\\User")]
    public void ExplicitInvalidRoot_FailsWithoutFallback(object? invalid)
    {
        var package = DirectoryRoot();
        Marker(package, invalid);
        Assert.Throws<InvalidDataException>(() => InstallationPipeScope.ResolveUserRoot(package));
        Assert.False(Directory.Exists(Path.Combine(package, "User")));
    }

    [Fact]
    public void DiskRootAndPackageRoot_CannotBeUserSource()
    {
        var package = DirectoryRoot();
        Marker(package, Path.GetPathRoot(package));
        Assert.Throws<InvalidDataException>(() => InstallationPipeScope.ResolveUserRoot(package));
        Marker(package, package);
        Assert.Throws<InvalidDataException>(() => InstallationPipeScope.ResolveUserRoot(package));
    }

    [Fact]
    public void DamagedMarker_FailsClearlyWithoutEmptyDefault()
    {
        var package = DirectoryRoot();
        var marker = Path.Combine(package, InstallationPipeScope.MarkerFile);
        File.WriteAllText(marker, "{");
        Assert.Throws<InvalidDataException>(() => InstallationPipeScope.ResolveUserRoot(package));
        Assert.Equal("{", File.ReadAllText(marker));
        Assert.False(Directory.Exists(Path.Combine(package, "User")));
    }

    [Fact]
    public void AssistantSingleton_DefaultIsUnchanged_AndOnlyDistinctExplicitRootsSeparate()
    {
        var root = DirectoryRoot();
        var ordinary = Path.Combine(root, "default");
        Assert.Equal("Session1", AssistantInstanceScope.ResolveSuffix(1, null, ordinary));
        Assert.Equal("Session1", AssistantInstanceScope.ResolveSuffix(1, ordinary + Path.DirectorySeparatorChar, ordinary));
        var one = AssistantInstanceScope.ResolveSuffix(1, Path.Combine(root, "one"), ordinary);
        Assert.Equal(one, AssistantInstanceScope.ResolveSuffix(1, Path.Combine(root, "ONE", "..", "one"), ordinary));
        Assert.NotEqual(one, AssistantInstanceScope.ResolveSuffix(1, Path.Combine(root, "two"), ordinary));
        Assert.NotEqual(one, AssistantInstanceScope.ResolveSuffix(2, Path.Combine(root, "one"), ordinary));
    }
}
