using System.IO;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// 手动备份涉及真实文件复制：验证无更新副作用、缺失/失败终态及手动副本保留规则。
// 这些行为此前只有更新入口的证据；全部通过才可交付独立备份入口。
[Collection("RegisterBankWpf")]
public sealed class BgiManualBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BgiManualBackup-" + Guid.NewGuid().ToString("N"));

    public BgiManualBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "User", "empty"));
        Directory.CreateDirectory(Path.Combine(_root, "Tools"));
        File.WriteAllText(Path.Combine(_root, "User", "config.json"), "USER-DATA");
        File.WriteAllText(Path.Combine(_root, "Tools", "tool.txt"), "TOOLS-DATA");
        File.WriteAllText(Path.Combine(_root, "BetterGI.exe"), "OLD-BGI-EXE");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Backup_CopiesSelectedDirectoriesWithoutPackageOrUpdating_AndKeepsSeparateSnapshots()
    {
        var first = await BgiPackageUpdateService.BackupAsync(_root, ["User", "User/empty", "Tools", "log"]);
        Assert.True(first.Success, first.Error);
        Assert.Equal(new[] { "log" }, first.MissingDirectories);
        Assert.StartsWith("manual-", Path.GetFileName(first.BackupDir!));
        Assert.DoesNotContain(".incomplete", first.BackupDir!);
        Assert.Equal("USER-DATA", File.ReadAllText(Path.Combine(first.BackupDir!, "User", "config.json")));
        Assert.Equal("TOOLS-DATA", File.ReadAllText(Path.Combine(first.BackupDir!, "Tools", "tool.txt")));
        Assert.True(Directory.Exists(Path.Combine(first.BackupDir!, "User", "empty")));
        Assert.False(File.Exists(Path.Combine(first.BackupDir!, "BetterGI.exe")));
        Assert.Equal("OLD-BGI-EXE", File.ReadAllText(Path.Combine(_root, "BetterGI.exe")));

        File.WriteAllText(Path.Combine(_root, "User", "config.json"), "NEW-USER-DATA");
        var second = await BgiPackageUpdateService.BackupAsync(_root, ["User"]);
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.BackupDir, second.BackupDir);
        Assert.Equal("USER-DATA", File.ReadAllText(Path.Combine(first.BackupDir!, "User", "config.json")));
        Assert.Equal("NEW-USER-DATA", File.ReadAllText(Path.Combine(second.BackupDir!, "User", "config.json")));
    }

    [Fact]
    public async Task Backup_EmptyOrMissingSelection_DoesNotReportSuccessOrCreateBackup()
    {
        var empty = await BgiPackageUpdateService.BackupAsync(_root, []);
        var missing = await BgiPackageUpdateService.BackupAsync(_root, ["missing"]);
        var invalidRoot = await BgiPackageUpdateService.BackupAsync(Path.Combine(_root, "absent"), ["User"]);
        Assert.False(empty.Success);
        Assert.False(missing.Success);
        Assert.False(invalidRoot.Success);
        Assert.Equal(new[] { "missing" }, missing.MissingDirectories);
        Assert.Null(missing.BackupDir);
        Assert.False(Directory.Exists(Path.Combine(_root, "_update_backup")));
        Assert.False(Directory.Exists(Path.Combine(_root, "absent")));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("../outside")]
    [InlineData("User/../Tools")]
    [InlineData("_update_backup/previous")]
    public async Task Backup_UnsafeSelection_RejectsBeforeWriting(string selected)
    {
        var result = await BgiPackageUpdateService.BackupAsync(_root, ["User", selected]);
        Assert.False(result.Success);
        Assert.Contains("备份目录无效", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "_update_backup")));
    }

    [Fact]
    public async Task Backup_CopyFailure_KeepsExplicitIncompletePath_AndReleasesOperation()
    {
        BgiManualBackupResult failed;
        using (var locked = new FileStream(Path.Combine(_root, "User", "config.json"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            failed = await BgiPackageUpdateService.BackupAsync(_root, ["User"]);
        }
        Assert.False(failed.Success);
        Assert.NotNull(failed.Error);
        Assert.NotNull(failed.BackupDir);
        Assert.EndsWith(".incomplete", failed.BackupDir);
        Assert.True(Directory.Exists(failed.BackupDir));
        Assert.Equal("USER-DATA", File.ReadAllText(Path.Combine(_root, "User", "config.json")));

        var retry = await BgiPackageUpdateService.BackupAsync(_root, ["User"]);
        Assert.True(retry.Success, retry.Error);
        Assert.NotEqual(failed.BackupDir, retry.BackupDir);
    }

    [Fact]
    public async Task Backup_ExistingPackageOperation_RejectsWithoutCopying()
    {
        using var operation = BgiPackageUpdateService.TryBeginPackageOperation();
        Assert.NotNull(operation);
        var result = await BgiPackageUpdateService.BackupAsync(_root, ["User"]);
        Assert.False(result.Success);
        Assert.Contains("正在进行", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "_update_backup")));
    }

    [Fact]
    public async Task Backup_DoesNotPruneOldUpdateBackups_AndUpdateDoesNotPruneManualBackup()
    {
        var past = DateTime.Now.AddDays(-12);
        var oldUpdate = Path.Combine(_root, "_update_backup", past.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(oldUpdate);
        File.WriteAllText(Path.Combine(oldUpdate, "keep.txt"), "OLD-BACKUP");
        Directory.SetCreationTime(oldUpdate, past);
        var result = await BgiPackageUpdateService.BackupAsync(_root, ["User"]);
        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(oldUpdate));
        var oldManual = Path.Combine(_root, "_update_backup", $"manual-{past:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.Move(result.BackupDir!, oldManual);
        Directory.SetCreationTime(oldManual, past);

        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");
        var update = BgiPackageUpdateService.Apply(fixture, _root, ["User"], [], null);
        Assert.True(update.Success, update.Error);
        Assert.False(Directory.Exists(oldUpdate));
        Assert.True(Directory.Exists(oldManual));
        Assert.Equal("USER-DATA", File.ReadAllText(Path.Combine(oldManual, "User", "config.json")));
    }
}
