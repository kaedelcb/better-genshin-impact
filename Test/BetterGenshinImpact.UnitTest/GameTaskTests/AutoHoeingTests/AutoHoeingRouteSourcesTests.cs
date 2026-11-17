using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>隔离目录验证来源标识、扫描及导入不覆盖官方包；不访问真实 User 或产品输出目录。</summary>
public sealed class AutoHoeingRouteSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BetterGI-RouteSources-" + Guid.NewGuid().ToString("N"));
    private string Official => Path.Combine(_root, "official");
    private string Local => Path.Combine(_root, "local");
    private string Legacy => Path.Combine(_root, "legacy");

    [Fact]
    public void ScannerDistinguishesIdenticalNamesAndFindsVariantOnlyDebugRoutes()
    {
        Add(Official, "同名", "official.json");
        Add(Local, "同名", "local.json");
        Add(Legacy, "同名", "legacy.json");
        Add(Official, "DebugRoutes", Path.Combine("A变体", "01_a.json"));
        var folders = new RouteDirectoryScanner(Official, Local, Legacy).ScanBuiltinRoutes();
        Assert.Equal(4, folders.Count);
        var official = Assert.Single(folders, f => f.FolderName == "同名");
        var local = Assert.Single(folders, f => f.FolderName == "local/同名");
        var legacy = Assert.Single(folders, f => f.FolderName == "legacy-local/同名");
        Assert.StartsWith("[官方]", official.DisplayName);
        Assert.StartsWith("[本地导入]", local.DisplayName);
        Assert.StartsWith("[本地旧目录]", legacy.DisplayName);
        Assert.Equal(Path.Combine(Official, "同名"), official.FullPath);
        Assert.Equal(Path.Combine(Local, "同名"), local.FullPath);
        Assert.Equal(Path.Combine(Legacy, "同名"), legacy.FullPath);
        Assert.Equal(1, Assert.Single(folders, f => f.FolderName == "DebugRoutes").RouteCount);
    }

    [Fact]
    public void MissingOfficialPackageDoesNotHideExplicitLocalChoicesOrCreateOfficialRoot()
    {
        Add(Local, "我的线路", "01.json");
        var folders = new RouteDirectoryScanner(Official, Local, Legacy).ScanBuiltinRoutes();
        Assert.Equal("local/我的线路", Assert.Single(folders).FolderName);
        Assert.False(Directory.Exists(Official));
    }

    [Fact]
    public void SinglePlayerKeepsBareLegacySelectionsAndDoesNotScanOfficialPackages()
    {
        Add(Official, "仅官方", "01.json");
        Add(Local, "导入", "01.json");
        Add(Legacy, "旧单机", "01.json");
        var folders = new RouteDirectoryScanner(Official, Local, Legacy, multiplayer: false).ScanBuiltinRoutes();
        Assert.Equal(new[] { "local/导入", "旧单机" }, folders.Select(f => f.FolderName));
        Assert.Equal(Path.Combine(Legacy, "旧单机"), folders.Single(f => f.FolderName == "旧单机").FullPath);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("local/../escape")]
    [InlineData("legacy-local/../escape")]
    [InlineData("C:\\absolute")]
    public void SelectionCannotEscapeItsSourceRoot(string selection)
        => Assert.Throws<InvalidDataException>(() => AutoHoeingRoutePaths.ResolveSelection(selection, multiplayer: true));

    [Fact]
    public void LocalImportPreservesOfficialAndUnrelatedLocalFilesAndRejectsOverlappingRoots()
    {
        var source = Add(Path.Combine(_root, "input"), "线路", "01.json");
        var official = Add(Official, "线路", "official.json");
        var local = Add(Local, "线路", "keep.json");
        File.WriteAllText(official, "official-original");
        File.WriteAllText(local, "local-original");
        var sourceDir = Path.GetDirectoryName(source)!;
        var targetDir = Path.GetDirectoryName(local)!;
        LocalRouteFolderImporter.CopyDirectoryRecursive(sourceDir, targetDir, overwrite: true);
        Assert.Equal("official-original", File.ReadAllText(official));
        Assert.Equal("local-original", File.ReadAllText(local));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(targetDir, "01.json")));
        Assert.Throws<IOException>(() => LocalRouteFolderImporter.CopyDirectoryRecursive(sourceDir, sourceDir, true));
        Assert.Throws<IOException>(() => LocalRouteFolderImporter.CopyDirectoryRecursive(sourceDir, Path.Combine(sourceDir, "nested"), true));
        Assert.Throws<IOException>(() => LocalRouteFolderImporter.CopyDirectoryRecursive(sourceDir, Path.GetDirectoryName(sourceDir)!, true));
        Assert.False(Directory.Exists(Path.Combine(sourceDir, "nested")));
    }

    private static string Add(string root, string name, string file)
    {
        var path = Path.Combine(root, name, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
