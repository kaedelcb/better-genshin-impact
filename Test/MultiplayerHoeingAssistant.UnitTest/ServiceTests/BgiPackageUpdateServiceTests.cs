using System.IO;
using MultiplayerHoeingAssistant.Services;
using SharpCompress.Archives;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// BgiPackageUpdateService 集成测试（真实 7z 夹具，py7zr 生成，4 条目）。
/// 验证解压覆盖全链路：覆盖 / 排除目录不触碰 / 备份先行 / 包名二次校验拒绝 / 目录不存在与坏包名兜底。
/// </summary>
public class BgiPackageUpdateServiceTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");

    /// <summary>5 条目夹具：含 0 字节占位文件（EmptyDir/placeholder.txt）与
    /// _update_backup/20260915-222438/BetterGI.exe 恶意条目（试图覆盖已有备份）。</summary>
    private static readonly string Fixture16Path = Path.Combine(
        AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix16.7z");

    private readonly string _root;
    private readonly string _bgiDir;

    public BgiPackageUpdateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BgiUpdateTests-" + Guid.NewGuid().ToString("N"));
        _bgiDir = Path.Combine(_root, "BGI");
        Directory.CreateDirectory(Path.Combine(_bgiDir, "User"));
        Directory.CreateDirectory(Path.Combine(_bgiDir, "Tool", "MultiplayerHoeingAssistant"));
        File.WriteAllText(Path.Combine(_bgiDir, "BetterGI.exe"), "OLD-BGI-EXE");
        File.WriteAllText(Path.Combine(_bgiDir, "User", "config.json"), "USER-DATA");
        File.WriteAllText(Path.Combine(_bgiDir, "Tool", "MultiplayerHoeingAssistant", "assistant.dll"), "OLD-ASSISTANT");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    [Fact]
    public void Apply_OverwritesFiles_And_SkipsExcludedDirs()
    {
        var result = BgiPackageUpdateService.Apply(FixturePath, _bgiDir,
            [@"User", "Tool/MultiplayerHoeingAssistant"], backupExcludedDirs: true, progress: null);

        Assert.True(result.Success, result.Error);
        // 包内条目 4 个：1 覆盖（BetterGI.exe）+ 2 排除 + 1 新增（NewDir/newfile.dll）
        Assert.Equal(2, result.ExtractedFiles);
        Assert.Equal(2, result.ExcludedFiles);
        Assert.Empty(result.SkippedFiles);
        // 被覆盖
        Assert.Equal("NEW-BGI-EXE", Read(_bgiDir, "BetterGI.exe"));
        // 排除目录原样未动
        Assert.Equal("USER-DATA", Read(_bgiDir, "User", "config.json"));
        Assert.Equal("OLD-ASSISTANT", Read(_bgiDir, "Tool", "MultiplayerHoeingAssistant", "assistant.dll"));
        // 新增文件正常落盘（新建子目录）
        Assert.Equal("NEW-FILE", Read(_bgiDir, "NewDir", "newfile.dll"));
        // 备份先行：排除目录内容已复制到 _update_backup\时间戳\
        Assert.NotNull(result.BackupDir);
        Assert.StartsWith(Path.Combine(_bgiDir, "_update_backup"), result.BackupDir);
        Assert.Equal("USER-DATA", Read(result.BackupDir!, "User", "config.json"));
        Assert.Equal("OLD-ASSISTANT", Read(result.BackupDir!, "Tool", "MultiplayerHoeingAssistant", "assistant.dll"));
    }

    [Fact]
    public void Apply_WithoutBackup_NoBackupDirCreated()
    {
        var result = BgiPackageUpdateService.Apply(FixturePath, _bgiDir, ["User"], backupExcludedDirs: false, progress: null);
        Assert.True(result.Success, result.Error);
        Assert.Null(result.BackupDir);
        Assert.False(Directory.Exists(Path.Combine(_bgiDir, "_update_backup")));
        Assert.Equal("USER-DATA", Read(_bgiDir, "User", "config.json"));
    }

    [Fact]
    public void Apply_ExcludedDirNotOnDisk_BackupSkipsIt()
    {
        var result = BgiPackageUpdateService.Apply(FixturePath, _bgiDir, ["NoSuchDir"], backupExcludedDirs: true, progress: null);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.BackupDir);
        Assert.False(Directory.Exists(Path.Combine(result.BackupDir!, "NoSuchDir")));
    }

    [Fact]
    public void Apply_InvalidArchiveName_RejectedBeforeAnyExtraction()
    {
        // 名字不合法 → 直接拒绝，绝不解压覆盖（需求硬约束）；文件不存在都不该被碰到
        var badName = Path.Combine(_root, "SomeRandomPackage.7z");
        var result = BgiPackageUpdateService.Apply(badName, _bgiDir, [], backupExcludedDirs: true, progress: null);
        Assert.False(result.Success);
        Assert.Contains("茶包版", result.Error);
        Assert.Equal("OLD-BGI-EXE", Read(_bgiDir, "BetterGI.exe")); // 目标目录未被触碰
    }

    [Fact]
    public void Apply_MissingArchiveFile_FailsCleanly()
    {
        var missing = Path.Combine(_root, "BetterGI_v9.9.9+lcb.99.9.7z");
        var result = BgiPackageUpdateService.Apply(missing, _bgiDir, [], backupExcludedDirs: false, progress: null);
        Assert.False(result.Success);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public void ScanPackages_FiltersInvalidNames_And_SortsNewestFirst()
    {
        var pkgDir = Path.Combine(_root, "pkgs");
        Directory.CreateDirectory(pkgDir);
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z"));
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix9.7z"));
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix14.7z"));
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.63.0+lcb.22.0.7z"));
        File.Copy(FixturePath, Path.Combine(pkgDir, "random_garbage.7z"));     // 名字不合法 → 不列出
        File.WriteAllText(Path.Combine(pkgDir, "readme.txt"), "not an archive");

        var packages = BgiPackageUpdateService.ScanPackages(pkgDir);
        Assert.Equal(4, packages.Count);
        // 从新到旧：大版本 → lcb → 尾段修复号（fix14 > fix13 > fix9 数字比较，非文件名字符串序）
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix14.7z", packages[0].FileName);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", packages[1].FileName);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix9.7z", packages[2].FileName);
        Assert.Equal("BetterGI_v0.63.0+lcb.22.0.7z", packages[3].FileName);
        Assert.Equal("NexusBGI", packages[0].NameInfo.Flavor);
    }

    [Fact]
    public void ScanPackages_LcbNumericCompare_SortsDottedVersionRight()
    {
        var pkgDir = Path.Combine(_root, "pkgs-lcb");
        Directory.CreateDirectory(pkgDir);
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.9.7z"));
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.10.7z")); // 字符串序会排错，数字序 22.10 更新

        var packages = BgiPackageUpdateService.ScanPackages(pkgDir);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.10.7z", packages[0].FileName);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.9.7z", packages[1].FileName);
    }

    [Fact]
    public void ScanPackages_MergesPinnedPackages_MarksThem_AndSkipsInvalidOrMissing()
    {
        // 安装包目录：一个扫描包（0.64.2）
        var pkgDir = Path.Combine(_root, "pkgs-pin");
        Directory.CreateDirectory(pkgDir);
        File.Copy(FixturePath, Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z"));

        // 指定包：更新版本（0.65.0），放在另一个目录（模拟用户从下载目录手动挑的）
        var foreignDir = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(foreignDir);
        var foreign = Path.Combine(foreignDir, "BetterGI_v0.65.0+lcb.99.0.7z");
        File.Copy(FixturePath, foreign);

        var result = BgiPackageUpdateService.ScanPackages(pkgDir,
        [
            foreign,                                                    // 有效指定包 → 带标记回列表
            Path.Combine(_root, "gone", "BetterGI_v9.9.9+lcb.1.0.7z"),  // 文件不存在 → 剔除
            Path.Combine(pkgDir, "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z"), // 与扫描重复 → 扫描条目为准，不重复
            Path.Combine(foreignDir, "not-a-teabag-pkg.7z"),            // 名字不合法 → 剔除
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal("BetterGI_v0.65.0+lcb.99.0.7z", result[0].FileName);   // 更新版本排前
        Assert.Equal(BgiPackageSource.PickedManually, result[0].Source);    // 指定来源
        Assert.Equal("指定", result[0].SourceTagText);
        Assert.Equal(foreignDir, result[0].DirectoryText);                  // 显示其目录
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", result[1].FileName);
        Assert.Equal(BgiPackageSource.Directory, result[1].Source);         // 扫描来源
        Assert.Equal("目录", result[1].SourceTagText);
    }

    [Fact]
    public void ScanPackages_WithPinsButNoScanDir_StillReturnsPins()
    {
        var foreign = Path.Combine(_root, "BetterGI_v0.65.0+lcb.99.0.7z");
        File.Copy(FixturePath, foreign);

        var result = BgiPackageUpdateService.ScanPackages(null, [foreign]);
        Assert.Single(result);
        Assert.Equal(BgiPackageSource.PickedManually, result[0].Source); // 没配安装包目录也不丢已指定的包
    }

    [Fact]
    public void ScanPackages_MissingOrEmptyDir_ReturnsEmpty()
    {
        Assert.Empty(BgiPackageUpdateService.ScanPackages(null));
        Assert.Empty(BgiPackageUpdateService.ScanPackages(""));
        Assert.Empty(BgiPackageUpdateService.ScanPackages(Path.Combine(_root, "not-exist")));
    }

    [Fact]
    public void Apply_ZeroByteEntry_CreatesEmptyFile_AndDoesNotAbort()
    {
        // 7z 空文件无数据流，旧实现 OpenEntryStream 抛 "File does not have a stream"
        // 导致一个 0 字节占位文件中断整个更新；新实现直接落空文件继续
        var result = BgiPackageUpdateService.Apply(Fixture16Path, _bgiDir, [], backupExcludedDirs: false, progress: null);

        Assert.True(result.Success, result.Error);
        var placeholder = new FileInfo(Path.Combine(_bgiDir, "EmptyDir", "placeholder.txt"));
        Assert.True(placeholder.Exists);
        Assert.Equal(0, placeholder.Length);
        // 5 个文件条目：3 个正常内容 + 1 个 0 字节 + 1 个 _update_backup 恶意条目（被硬保护排除）
        Assert.Equal(4, result.ExtractedFiles);
        Assert.Equal(1, result.ExcludedFiles);
        Assert.Empty(result.SkippedFiles);
    }

    [Fact]
    public void Apply_BackupRootEntries_NeverOverwriteExistingBackup()
    {
        // _update_backup 无条件硬保护：包内同名路径条目一律跳过，已有的历史备份分毫不动
        var tsDir = Path.Combine(_bgiDir, "_update_backup", "20260915-222438");
        Directory.CreateDirectory(Path.Combine(tsDir, "User"));
        File.WriteAllText(Path.Combine(tsDir, "User", "config.json"), "REAL-BACKUP");

        var result = BgiPackageUpdateService.Apply(Fixture16Path, _bgiDir, ["User"], backupExcludedDirs: true, progress: null);

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(tsDir, "BetterGI.exe"))); // 恶意条目未落盘
        Assert.Equal("REAL-BACKUP", Read(tsDir, "User", "config.json")); // 已有备份原样
        // User/ 排除 + _update_backup 恶意条目硬保护，都被跳过
        Assert.Equal(2, result.ExcludedFiles);
    }
}
