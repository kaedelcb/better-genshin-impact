using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class ConfigTransferTransactionTests
{
    [Fact]
    public void PendingRecoveryRestoresWrittenFilesRemovesNewFilesAndKeepsUnwrittenFiles()
    {
        using var fixture = new Fixture();
        var assistant = fixture.Target("assistant-config.json");
        var startup = fixture.Target("startup-flow.json");
        var pet = fixture.Target("pet_settings.json");
        var oldAssistant = Bytes("\r\n{\"playerName\":\"原始\"}\r\n");
        var newAssistant = Bytes("{\"playerName\":\"导入\"}");
        var oldPet = Bytes("{\"sizePx\":180}");
        var newPet = Bytes("{\"sizePx\":225}");
        var newStartup = Bytes("{\"steps\":[]}");
        File.WriteAllBytes(assistant, newAssistant);
        File.WriteAllBytes(startup, newStartup);
        File.WriteAllBytes(pet, oldPet); // 模拟写完前两项后进程中断。
        fixture.Publish(false, null, (assistant, true, oldAssistant, newAssistant),
            (startup, false, [], newStartup), (pet, true, oldPet, newPet));

        ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot + Path.DirectorySeparatorChar);

        Assert.Equal(oldAssistant, File.ReadAllBytes(assistant));
        Assert.False(File.Exists(startup));
        Assert.Equal(oldPet, File.ReadAllBytes(pet));
        Assert.False(File.Exists(fixture.Pending));
        Assert.True(File.Exists(Path.Combine(fixture.Backup, "transaction.json")));
    }

    [Fact]
    public void CommittedRecoveryKeepsNewSettingsAndOnlyFinishesPendingCleanup()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var before = Bytes("{\"sizePx\":180}");
        var after = Bytes("{\"sizePx\":225}");
        File.WriteAllBytes(target, after);
        fixture.Publish(true, null, (target, true, before, after));

        ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot);

        Assert.Equal(after, File.ReadAllBytes(target));
        Assert.False(File.Exists(fixture.Pending));
    }

    [Fact]
    public void CorruptBackupRejectsWholeRecoveryBeforeRestoringAnyEarlierFile()
    {
        using var fixture = new Fixture();
        var assistant = fixture.Target("assistant-config.json");
        var pet = fixture.Target("pet_settings.json");
        var afterAssistant = Bytes("{\"playerName\":\"导入\"}");
        var afterPet = Bytes("{\"sizePx\":225}");
        File.WriteAllBytes(assistant, afterAssistant);
        File.WriteAllBytes(pet, afterPet);
        fixture.Publish(false, null, (assistant, true, Bytes("{\"playerName\":\"原始\"}"), afterAssistant),
            (pet, true, Bytes("{\"sizePx\":180}"), afterPet));
        File.WriteAllText(Path.Combine(fixture.Backup, "000001.original"), "损坏备份");

        Assert.Throws<InvalidDataException>(() => ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot));

        Assert.Equal(afterAssistant, File.ReadAllBytes(assistant));
        Assert.Equal(afterPet, File.ReadAllBytes(pet));
        Assert.True(File.Exists(fixture.Pending));
    }

    [Fact]
    public void RecoveryPreservesAnotherWritersNewVersionAndRetainsPendingResponsibility()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("assistant-config.json");
        var foreign = Bytes("{\"playerName\":\"后来编辑\"}");
        fixture.Publish(false, null, (target, true, Bytes("{\"playerName\":\"原始\"}"), Bytes("{\"playerName\":\"导入\"}")));
        File.WriteAllBytes(target, foreign);

        Assert.ThrowsAny<IOException>(() => ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot));

        Assert.Equal(foreign, File.ReadAllBytes(target));
        Assert.True(File.Exists(fixture.Pending));
    }

    [Fact]
    public void RecoveryJournalCannotUseBgiRootToAuthorizeATargetOutsideThatRoot()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "unrelated.json");
        var current = Bytes("{\"unrelated\":true}");
        File.WriteAllBytes(outside, current);
        var bgiRoot = Path.Combine(fixture.Root, "BGI", "User");
        Directory.CreateDirectory(bgiRoot);
        fixture.Publish(false, bgiRoot, (outside, true, Bytes("{\"mustNotRestoreHere\":true}"), current));

        Assert.Throws<InvalidDataException>(() => ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot));

        Assert.Equal(current, File.ReadAllBytes(outside));
        Assert.True(File.Exists(fixture.Pending));
    }

    [Fact]
    public void FailedRuntimeRestorationRetainsPendingUntilNextRecoveryAfterOriginalBytesAreRestored()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var before = Bytes("\r\n{\"sizePx\":180}\r\n");
        File.WriteAllBytes(target, before);
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]> { [target] = Bytes("{\"sizePx\":225}") });
        var restorationCalls = 0;

        Assert.ThrowsAny<IOException>(() => transaction.Commit(
            () => throw new InvalidOperationException("测试：新配置运行时应用失败"), null,
            () => { restorationCalls++; throw new InvalidOperationException("测试：原配置运行时恢复失败"); }));

        Assert.Equal(1, restorationCalls);
        Assert.True(transaction.RecoveryRequired);
        Assert.True(File.Exists(fixture.Pending));
        Assert.Equal(before, File.ReadAllBytes(target));

        ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot);

        Assert.False(File.Exists(fixture.Pending));
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void RequiredNewTargetCreatedBeforeCommitRejectsTheWholeBatchWithoutPublishingAnyTarget()
    {
        using var fixture = new Fixture();
        var assistant = fixture.Target("assistant-config.json");
        var planned = fixture.Target("flows/wf-planned.flow.json");
        var original = Bytes("\r\n{\"playerName\":\"本机原始\"}\r\n");
        var foreign = Bytes("{\"name\":\"另一写者刚保存的计划\"}");
        File.WriteAllBytes(assistant, original);
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]>
            {
                [assistant] = Bytes("{\"playerName\":\"不应落盘\"}"),
                [planned] = Bytes("{\"name\":\"导入计划\"}")
            }, requiredNew: [planned]);
        // 导入已经规划出新身份，但在 Commit 备份前，其他写者占用了目标。
        Directory.CreateDirectory(Path.GetDirectoryName(planned)!);
        File.WriteAllBytes(planned, foreign);
        var writes = 0;
        var runtimeCalled = false;

        Assert.ThrowsAny<IOException>(() => transaction.Commit(() => runtimeCalled = true, (_, _) => writes++));

        Assert.Equal(0, writes);
        Assert.False(runtimeCalled);
        Assert.False(transaction.RecoveryRequired);
        Assert.False(File.Exists(fixture.Pending));
        Assert.Equal(original, File.ReadAllBytes(assistant));
        Assert.Equal(foreign, File.ReadAllBytes(planned));
    }

    [Fact]
    public void ConcurrentCreationAtTheWriteBoundaryIsPreservedAndNeverReportedAsCommitted()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var foreign = Bytes("{\"sizePx\":333}");
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]> { [target] = Bytes("{\"sizePx\":225}") });

        Assert.ThrowsAny<IOException>(() => transaction.Commit(null, (_, _) => File.WriteAllBytes(target, foreign)));

        Assert.False(transaction.RecoveryRequired);
        Assert.Equal(foreign, File.ReadAllBytes(target));
        Assert.False(File.Exists(fixture.Pending));
    }

    [Fact]
    public void IdenticalForeignNewFileIsPreservedWhenOurPublicationNeverHappened()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var after = Bytes("{\"sizePx\":225}");
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]> { [target] = after }, requiredNew: [target]);

        Assert.ThrowsAny<IOException>(() => transaction.Commit(null, (_, _) => File.WriteAllBytes(target, after)));

        Assert.Equal(after, File.ReadAllBytes(target));
        Assert.False(transaction.RecoveryRequired);
        Assert.False(File.Exists(fixture.Pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPreservesAnIdenticalForeignFileInsteadOfGuessingOwnershipFromBytes(bool existed)
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var after = Bytes("{\"sizePx\":225}");
        byte[] before = existed ? Bytes("{\"sizePx\":180}") : [];
        if (existed) File.WriteAllBytes(target, before);
        fixture.Publish(false, null, (target, existed, before, after));
        File.WriteAllBytes(target, after); // Own staging identity is already durable, but we never renamed it.

        Assert.ThrowsAny<IOException>(() => ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot));

        Assert.Equal(after, File.ReadAllBytes(target));
        Assert.True(File.Exists(fixture.Pending));
    }

    [Fact]
    public void LegacyJournalWithoutPhysicalOwnershipNeverDeletesAnAfterHashMatch()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var after = Bytes("{\"sizePx\":225}");
        File.WriteAllBytes(target, after);
        fixture.Publish(false, null, (target, false, [], after));
        var path = Path.Combine(fixture.Backup, "transaction.json");
        var journal = JsonSerializer.Deserialize<ConfigTransferTransaction.Journal>(File.ReadAllBytes(path))!;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal with
        { Items = journal.Items.Select(item => item with { PublishedIdentity = null, StagedPath = null }).ToList() }));

        Assert.ThrowsAny<IOException>(() => ConfigTransferService.RecoverInterruptedImport(fixture.DataRoot));

        Assert.Equal(after, File.ReadAllBytes(target));
        Assert.True(File.Exists(fixture.Pending));
    }

    [Fact]
    public void StagingIdentityIsDurableBeforePublishingAndSurvivesTheRename()
    {
        using var fixture = new Fixture();
        var target = fixture.Target("pet_settings.json");
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]> { [target] = Bytes("{\"sizePx\":225}") });
        string? identity = null;

        transaction.Commit(null, (_, _) =>
        {
            var journal = JsonSerializer.Deserialize<ConfigTransferTransaction.Journal>(
                File.ReadAllBytes(Path.Combine(transaction.BackupDirectory, "transaction.json")))!;
            var item = Assert.Single(journal.Items);
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(fixture.Pending));
            Assert.NotNull(item.PublishedIdentity);
            Assert.Equal(ConfigTransferFileIdentity.Read(item.StagedPath!), item.PublishedIdentity);
            identity = item.PublishedIdentity;
        });

        Assert.Equal(identity, ConfigTransferFileIdentity.Read(target));
        Assert.False(File.Exists(fixture.Pending));
    }

    [Fact]
    public void SharedTargetPermitAlsoExcludesANormalPeerWriterFromAnotherDataRoot()
    {
        using var fixture = new Fixture();
        var shared = Path.Combine(fixture.Root, "shared", "watch_rules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, "before");
        var transaction = new ConfigTransferTransaction(fixture.DataRoot,
            new Dictionary<string, byte[]> { [shared] = Bytes("imported") }, additionalTargets: [shared]);

        transaction.Commit(() =>
        {
            var saved = Task.Run(() => LocalPeerSyncService.WriteFileAtomic(shared, "peer-edit")).GetAwaiter().GetResult();
            Assert.False(saved);
            Assert.Equal("imported", File.ReadAllText(shared));
        }, null);

        Assert.True(Task.Run(() => LocalPeerSyncService.WriteFileAtomic(shared, "peer-edit")).GetAwaiter().GetResult());
        Assert.Equal("peer-edit", File.ReadAllText(shared));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ConfigTransferTransactionTests-" + Guid.NewGuid().ToString("N"));
        public string DataRoot => Path.Combine(Root, "data");
        public string Backup => Path.Combine(DataRoot, "backup-import-test");
        public string Pending => Path.Combine(DataRoot, ".config-transfer-pending.json");
        public Fixture() => Directory.CreateDirectory(DataRoot);
        public string Target(string name) => Path.Combine(DataRoot, name);
        public void Publish(bool committed, string? bgiRoot,
            params (string Target, bool Existed, byte[] Before, byte[] After)[] snapshots)
        {
            Directory.CreateDirectory(Backup);
            var items = new List<ConfigTransferTransaction.Item>();
            foreach (var snapshot in snapshots)
            {
                var name = items.Count.ToString("D6") + ".original";
                File.WriteAllBytes(Path.Combine(Backup, name), snapshot.Before);
                // Model the actual durable staging identity, including items not yet published.
                var stage = Path.Combine(Path.GetDirectoryName(snapshot.Target)!, ".config-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                File.WriteAllBytes(stage, snapshot.After);
                var identity = ConfigTransferFileIdentity.Read(stage);
                if (File.Exists(snapshot.Target) && File.ReadAllBytes(snapshot.Target).AsSpan().SequenceEqual(snapshot.After))
                    File.Move(stage, snapshot.Target, overwrite: true);
                items.Add(new(snapshot.Target, name, snapshot.Existed, Hash(snapshot.Before), Hash(snapshot.After), identity, stage));
            }
            var journal = new ConfigTransferTransaction.Journal("NexusBGI.configTransfer.2", DataRoot, Backup, bgiRoot, [], items, committed);
            File.WriteAllBytes(Path.Combine(Backup, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
            File.WriteAllBytes(Pending, JsonSerializer.SerializeToUtf8Bytes(new { Schema = "NexusBGI.configTransfer.2", BackupDirectory = Backup }));
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("ConfigTransferTransactionTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理测试根以外的目录");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
}
