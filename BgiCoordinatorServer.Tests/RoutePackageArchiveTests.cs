using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NexusBGI.RoutePackages;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// These cases decide whether an untrusted browser ZIP may enter either server storage or desktop staging.
public sealed class RoutePackageArchiveTests
{
    [Fact]
    public async Task ChinesePathsComplexActionsAndPathingRootFileProduceAnExactManifestAndExtract()
    {
        using var data = new RoutePackageTestData();
        var route = RoutePackageTestData.Route.Replace("\"action\":null", "\"action\":\"fight\",\"action_params\":\"200,600; 使用技能\\n下一行\",\"pickup_target_count\":{\"count\":3}");
        var zip = data.Zip(("builtin/中文线路/001.json", route), ("pathing/普通选线.json", route));
        var files = await RoutePackageArchive.ValidateAsync(zip);
        Assert.Equal(2, files.Count);
        Assert.Equal("builtin/中文线路/001.json", files[0].Path);
        Assert.All(files, file =>
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(route), file.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route))).ToLowerInvariant(), file.Sha256);
        });
        var target = Path.Combine(data.Root, "exclusive-staging");
        await RoutePackageArchive.ExtractAsync(zip, target);
        Assert.Equal(route, await File.ReadAllTextAsync(Path.Combine(target, "builtin/中文线路/001.json")));
        await Assert.ThrowsAsync<IOException>(() => RoutePackageArchive.ExtractAsync(zip, target));
    }

    [Fact]
    public async Task VariantDescriptionsAreBoundedResourcesNotRoutesOrArbitraryTextFiles()
    {
        using var data = new RoutePackageTestData();
        var path = data.Zip(("builtin/线路/A变体/001_a.json", RoutePackageTestData.Route), ("builtin/线路/A变体/变体说明.txt", "走左边\n第二行"), ("builtin/线路/B变体/变体说明.txt", ""));
        Assert.Equal(3, (await RoutePackageArchive.ValidateAsync(path)).Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip(("builtin/线路/a.json", RoutePackageTestData.Route), ("builtin/线路/任意文件.txt", "不允许"))));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/builtin/x/a.json")]
    [InlineData("builtin/x/../a.json")]
    [InlineData("builtin\\x\\a.json")]
    [InlineData("builtin/x/C:a.json")]
    [InlineData("builtin/x/a.json:stream")]
    [InlineData("builtin/x/CON.json")]
    [InlineData("builtin/x/lpt¹.json")]
    [InlineData("builtin/x/trailing ./a.json")]
    [InlineData("builtin/x/trailing./a.json")]
    [InlineData("builtin/x//a.json")]
    [InlineData("builtin/x/main.js")]
    [InlineData("builtin/x/tool.exe")]
    [InlineData("User/Config/settings.json")]
    public async Task RejectsUnsafeOrNonRoutePathsBeforeExtraction(string path)
    {
        using var data = new RoutePackageTestData();
        var zip = data.Zip(("builtin/good/a.json", RoutePackageTestData.Route), (path, RoutePackageTestData.Route));
        var target = Path.Combine(data.Root, "must-not-exist");
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ExtractAsync(zip, target));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("builtin/Case/a.json", "builtin/case/b.json")]
    [InlineData("builtin/x/a.json", "builtin/x/A.json")]
    [InlineData("builtin/é/a.json", "builtin/é/b.json")]
    [InlineData("builtin/x/a.json", "builtin/x/a.json/b.json")]
    public async Task RejectsExplicitAndImplicitParentAliases(string a, string b)
    {
        using var data = new RoutePackageTestData();
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip((a, RoutePackageTestData.Route), (b, RoutePackageTestData.Route))));
    }

    [Theory]
    [InlineData("{\"positions\":[]}")]
    [InlineData("{\"positions\":[{\"x\":\"1\",\"y\":2}]}")]
    [InlineData("{\"positions\":[{\"x\":1e999,\"y\":2}]}")]
    [InlineData("{\"positions\":[{\"x\":1,\"y\":2,\"action\":{}}]}")]
    [InlineData("{\"positions\":[{\"x\":1,\"y\":2},{\"x\":1,\"y\":2,\"y\":3}]}")]
    [InlineData("{\"positions\":[{\"x\":1,\"y\":2,\"sync_point_id\":\"a\"},{\"x\":2,\"y\":3,\"sync_point_id\":\"a\"}]}")]
    public async Task RejectsInvalidRouteTypesAndDuplicateSyncPoints(string route)
    {
        using var data = new RoutePackageTestData();
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.ValidZip(route)));
    }

    [Fact]
    public async Task Json5RelativeReferencesAndControlMergeAreValidatedAsTheRuntimeLoadsThem()
    {
        using var data = new RoutePackageTestData();
        var control = """
        { // 中文与转义
          global_cover: { config: {name: '控制规则', signed:+2, fraction:.5, trailing:1., hex:0x10}, _arr_add: ['positions'], positions: [{x: 5, y: 6, sync_point_id: 'fight_2'}], },
          json_list: [{name:'001', cover:{info:{name:'单引号\'与 // 不是注释'}}}],
        }
        """;
        var zip = data.Zip(("builtin/线路/001.json", RoutePackageTestData.Route), ("builtin/线路/control.json5", "{ref:'../共享/'}"), ("builtin/共享/control.json5", control));
        Assert.Equal(3, (await RoutePackageArchive.ValidateAsync(zip)).Count);
        var duplicate = control.Replace("fight_2", "fight_1");
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip(
            ("builtin/线路/001.json", RoutePackageTestData.Route), ("builtin/线路/control.json5", duplicate))));
    }

    [Theory]
    [InlineData("{ref:'../线路/control.json5'}")]
    [InlineData("{ref:'../../../outside'}")]
    [InlineData("{ref:'C:/outside'}")]
    [InlineData("{ref:'missing'}")]
    [InlineData("{ref:'',global_cover:42}")]
    [InlineData("{global_cover:{_arr_add:'positions'}}")]
    [InlineData("{global_cover:{_arr_add:['positions'],positions:{}}}")]
    public async Task RejectsReferenceCyclesEscapesAndInvalidMergeRules(string control)
    {
        using var data = new RoutePackageTestData();
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip(
            ("builtin/线路/a.json", RoutePackageTestData.Route), ("builtin/线路/control.json5", control))));
    }

    [Fact]
    public async Task VariantsMustRetainTheSameOrderedSyncContract()
    {
        using var data = new RoutePackageTestData();
        var entries = new[] { ("builtin/线路/A变体/001_a.json", RoutePackageTestData.Route), ("builtin/线路/B变体/001_b.json", RoutePackageTestData.Route) };
        Assert.Equal(2, (await RoutePackageArchive.ValidateAsync(data.Zip(entries))).Count);
        entries[1].Item2 = RoutePackageTestData.Route.Replace("fight_1", "different");
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip(entries)));
    }

    [Fact]
    public async Task ActualCrcIsCheckedEvenWhenBothHeaderCrcsAgree()
    {
        using var data = new RoutePackageTestData();
        var zip = data.ValidZip();
        var bytes = await File.ReadAllBytesAsync(zip);
        var central = Signature(bytes, 0x02014b50);
        // Keep local and central headers mutually consistent, but make their expected CRC untrue.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16), 0x12345678);
        await File.WriteAllBytesAsync(zip, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(zip));
    }

    [Theory]
    [InlineData(1)] // encrypted
    [InlineData(64)] // strong encryption
    public async Task RejectsUnsupportedFlagsInBothHeaders(int flag)
    {
        using var data = new RoutePackageTestData();
        var zip = data.ValidZip();
        var bytes = await File.ReadAllBytesAsync(zip);
        var central = Signature(bytes, 0x02014b50);
        bytes[6] |= (byte)flag;
        bytes[central + 8] |= (byte)flag;
        await File.WriteAllBytesAsync(zip, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(zip));
    }

    [Fact]
    public async Task RejectsSymlinkEntriesAndOversizedExpandedHeaders()
    {
        using var data = new RoutePackageTestData();
        var zip = data.ValidZip();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.Entries[0].ExternalAttributes = unchecked((int)0xa1ff0000);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(zip));
        zip = data.ValidZip();
        var bytes = await File.ReadAllBytesAsync(zip);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Signature(bytes, 0x02014b50) + 24), 17 * 1024 * 1024);
        await File.WriteAllBytesAsync(zip, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(zip));
    }

    [Fact]
    public async Task NoBuiltinAndDeepJsonAreRejectedAndCancellationDoesNotExtract()
    {
        using var data = new RoutePackageTestData();
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.Zip(("pathing/only.json", RoutePackageTestData.Route))));
        var deep = "{\"extra\":" + new string('[', 65) + "0" + new string(']', 65) + ",\"positions\":[{\"x\":1,\"y\":2}]}";
        await Assert.ThrowsAsync<InvalidDataException>(() => RoutePackageArchive.ValidateAsync(data.ValidZip(deep)));
        var target = Path.Combine(data.Root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoutePackageArchive.ExtractAsync(data.ValidZip(), target, new CancellationToken(true)));
        Assert.False(Directory.Exists(target));
        Assert.True(RoutePackageArchive.IsValidId(new string('a', 32)));
        Assert.False(RoutePackageArchive.IsValidId(new string('A', 32)));
        Assert.False(RoutePackageArchive.IsValidId("../"));
    }

    private static int Signature(byte[] bytes, uint signature)
    {
        for (var i = 0; i <= bytes.Length - 4; i++) if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == signature) return i;
        throw new InvalidOperationException("Fixture ZIP signature not found");
    }
}
