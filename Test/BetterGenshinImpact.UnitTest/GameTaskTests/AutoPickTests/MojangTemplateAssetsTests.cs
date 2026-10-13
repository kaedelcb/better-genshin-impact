using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPick;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

public sealed class MojangTemplateAssetsTests
{
    [Fact]
    public void PackagedBin_LoadsAllTemplatesAndMatchesTheSourceManifest()
    {
        var assets = FindSourceAssets();
        var binPath = Path.Combine(assets, "莫版模板.bin");
        using (var reader = new BinaryReader(File.OpenRead(binPath)))
        {
            Assert.Equal(0x4D424D42, reader.ReadInt32());
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(400, reader.ReadInt32());
        }
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "莫版模板", "templates.json")));
        Assert.Equal(400, manifest.RootElement.GetArrayLength());
        var expected = manifest.RootElement.EnumerateArray()
            .Select(entry => (Name: entry.GetProperty("name").GetString()!,
                Color: entry.GetProperty("color").GetString()!, Len: entry.GetProperty("len").GetInt32()))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();

        var matcher = MojangMatch.LoadFromBin(binPath, new AutoPickConfig());
        var loaded = matcher.GetTemplateInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => (entry.Name, entry.Color, entry.Len)).ToArray();

        Assert.Equal(361, loaded.Length);
        Assert.Equal(expected, loaded);
    }

    private static string FindSourceAssets()
    {
        // 仅沿已知源码/测试目录向上定位仓库，不查询 Global.UserRoot 或运行目录 User。
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var assets = Path.Combine(directory.FullName, "BetterGenshinImpact", "GameTask", "AutoPick", "Assets");
                if (File.Exists(Path.Combine(assets, "莫版模板.bin"))
                    && File.Exists(Path.Combine(assets, "莫版模板", "templates.json")))
                    return assets;
            }
        }

        throw new DirectoryNotFoundException("未找到源码仓库中的莫版模板资源。请从源码仓库运行此定向测试。");
    }
}
