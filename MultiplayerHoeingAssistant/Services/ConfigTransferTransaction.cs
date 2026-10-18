using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>设置迁移的耐久预写备份；每次替换检查原修订，异常恢复原字节，新进程先恢复再读配置。</summary>
internal sealed class ConfigTransferTransaction
{
    private const string PendingName = ".config-transfer-pending.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _root;
    private readonly IReadOnlyDictionary<string, byte[]> _targets;
    private readonly Journal _journal;
    private readonly IReadOnlyDictionary<string, string?> _expectedVersions;
    private readonly HashSet<string> _requiredNew;
    internal string BackupDirectory { get; }
    internal bool RecoveryRequired { get; private set; }
    internal sealed record Item(string Target, string Backup, bool Existed, string BeforeHash, string AfterHash,
        string? PublishedIdentity = null, string? StagedPath = null);
    internal sealed record Journal(string Schema, string DataRoot, string BackupDirectory, string? BgiRoot,
        string[] AdditionalTargets, List<Item> Items, bool Committed = false);
    private sealed record Pending(string Schema, string BackupDirectory);

    internal ConfigTransferTransaction(string root, IReadOnlyDictionary<string, byte[]> targets,
        string? bgiRoot = null, IEnumerable<string>? additionalTargets = null,
        IReadOnlyDictionary<string, string?>? expectedVersions = null, IEnumerable<string>? requiredNew = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _targets = targets;
        _expectedVersions = expectedVersions ?? new Dictionary<string, string?>();
        _requiredNew = new(requiredNew ?? [], StringComparer.OrdinalIgnoreCase);
        BackupDirectory = Path.Combine(_root, $"backup-import-{DateTime.Now:yyyyMMdd_HHmmss_fff}-{Guid.NewGuid():N}");
        _journal = new("NexusBGI.configTransfer.2", _root, BackupDirectory, bgiRoot,
            additionalTargets?.Select(Path.GetFullPath).ToArray() ?? [], []);
    }

    internal static IDisposable Acquire(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        CheckPath(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".config-transfer.lock");
        CheckPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("其他进程正在迁移同一份助手配置，请稍后再试。", ex); }
    }

    internal void Commit(Action? applyRuntime, Action<string, int>? beforeWrite, Action? restoreRuntime = null)
    {
        foreach (var target in _targets.Keys) ValidateTarget(_journal, target);
        using var writing = ConfigFileWriteGate.AcquireMany(_targets.Keys);
        CheckPath(BackupDirectory);
        Directory.CreateDirectory(BackupDirectory);
        var published = new List<Item>();
        var staged = new List<(string Path, string Identity)>();
        try
        {
            // 所有原文件备份和校验结束后才发布 pending；备份失败不会改动目标。
            foreach (var target in _targets)
            {
                ValidateTarget(_journal, target.Key);
                CheckPath(target.Key);
                var exists = File.Exists(target.Key);
                var original = exists ? File.ReadAllBytes(target.Key) : [];
                if (_requiredNew.Contains(target.Key) && exists)
                    throw new IOException("计划或资源目标已由其他写者创建，原文件未覆盖：" + target.Key);
                if (_expectedVersions.TryGetValue(target.Key, out var expected) && expected != (exists ? Hash(original) : null))
                    throw new IOException("本机配置在导入规划期间被修改，原文件未覆盖：" + target.Key);
                var backup = Path.Combine(BackupDirectory, _journal.Items.Count.ToString("D6") + ".original");
                WriteAtomic(backup, original);
                if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original)) throw new IOException("配置备份读回不一致。");
                // Publish from the same volume. Persist its physical identity before exposing any target.
                var stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target.Key))!, ".config-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
                WriteAtomic(stage, target.Value, overwrite: false);
                var identity = ConfigTransferFileIdentity.Read(stage);
                staged.Add((stage, identity));
                _journal.Items.Add(new(Path.GetFullPath(target.Key), Path.GetFileName(backup), exists, Hash(original), Hash(target.Value), identity, stage));
            }
            WriteAtomic(Path.Combine(BackupDirectory, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(_journal, Options));
            WriteAtomic(Path.Combine(_root, PendingName), JsonSerializer.SerializeToUtf8Bytes(new Pending(_journal.Schema, BackupDirectory), Options));
            try
            {
                for (var index = 0; index < _journal.Items.Count; index++)
                {
                    var item = _journal.Items[index];
                    beforeWrite?.Invoke(item.Target, index);
                    if (File.Exists(item.Target) != item.Existed || (item.Existed && Hash(File.ReadAllBytes(item.Target)) != item.BeforeHash))
                        throw new IOException("配置在迁移期间被其他写者修改，已取消覆盖：" + item.Target);
                    File.Move(item.StagedPath!, item.Target, overwrite: item.Existed);
                    published.Add(item);
                    if (!IsOwnedVersion(item)) throw new IOException("配置写入后身份或读回不一致：" + item.Target);
                }
                applyRuntime?.Invoke();
                WriteAtomic(Path.Combine(BackupDirectory, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(_journal with { Committed = true }, Options));
                try { File.Delete(Path.Combine(_root, PendingName)); } catch (IOException) { /* committed marker is durable; next startup only removes the pointer. */ }
            }
            catch (Exception original)
            {
                try { Rollback(_journal, published); restoreRuntime?.Invoke(); File.Delete(Path.Combine(_root, PendingName)); }
                catch (Exception rollback)
                {
                    RecoveryRequired = true;
                    throw new IOException("导入未完成，回滚也未确认：" + rollback.Message, original);
                }
                throw new IOException("本次导入未完成，本事务写入已回滚，其他写者文件保留：" + original.Message, original);
            }
        }
        finally
        {
            // Preparation failure may leave only our private staging files; never remove a target here.
            foreach (var stage in staged)
                try
                {
                    if (File.Exists(stage.Path) && ConfigTransferFileIdentity.Read(stage.Path) == stage.Identity)
                        File.Delete(stage.Path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    internal static bool Recover(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pendingPath = Path.Combine(root, PendingName);
        CheckPath(pendingPath);
        if (!File.Exists(pendingPath)) return false;
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(pendingPath)) ?? throw new InvalidDataException("设置恢复记录为空。");
        var backup = Path.GetFullPath(pending.BackupDirectory);
        if (pending.Schema != "NexusBGI.configTransfer.2" || !string.Equals(Path.GetDirectoryName(backup), root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(backup).StartsWith("backup-import-", StringComparison.Ordinal))
            throw new InvalidDataException("设置恢复目录无效，未改动任何文件。");
        CheckPath(backup);
        var path = Path.Combine(backup, "transaction.json");
        CheckPath(path);
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("设置恢复事务为空。");
        if (journal.Schema != pending.Schema || !string.Equals(journal.DataRoot, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(journal.BackupDirectory, backup, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("设置恢复身份不匹配，未继续读取混合配置。");
        // 整份校验后才恢复；不把缺失/损坏备份误认作空配置。
        foreach (var item in journal.Items)
        {
            ValidateTarget(journal, item.Target);
            var original = BackupPath(journal, item);
            if (!File.Exists(original) || Hash(File.ReadAllBytes(original)) != item.BeforeHash)
                throw new InvalidDataException("原配置备份缺失或校验失败，禁止自动覆盖：" + item.Target);
        }
        using var writing = ConfigFileWriteGate.AcquireMany(journal.Items.Select(item => item.Target));
        if (!journal.Committed) Rollback(journal);
        foreach (var item in journal.Items) CleanupStaging(item);
        File.Delete(pendingPath);
        return !journal.Committed;
    }

    private static void Rollback(Journal journal, IEnumerable<Item>? published = null)
    {
        var failures = new List<string>();
        foreach (var item in (published ?? journal.Items).Reverse())
        {
            try
            {
                ValidateTarget(journal, item.Target);
                CheckPath(item.Target);
                var present = File.Exists(item.Target);
                var hash = present ? Hash(File.ReadAllBytes(item.Target)) : null;
                if ((item.Existed && hash == item.BeforeHash) || (!item.Existed && !present)) continue;
                // Old journals have no physical ownership. Equal bytes alone cannot authorize deletion.
                if (hash != item.AfterHash || !IsOwnedVersion(item))
                    throw new IOException("无法确认目标属于本事务，可能已有其他写者；原件和备份均保留");
                if (item.Existed)
                {
                    var bytes = File.ReadAllBytes(BackupPath(journal, item));
                    if (Hash(bytes) != item.BeforeHash) throw new InvalidDataException("原配置备份校验失败");
                    WriteAtomic(item.Target, bytes);
                    if (Hash(File.ReadAllBytes(item.Target)) != item.BeforeHash) throw new IOException("恢复读回未确认");
                }
                else File.Delete(item.Target); // 仅删除本事务已核对内容的新文件，不删除目录。
            }
            catch (Exception ex) { failures.Add(item.Target + "：" + ex.Message); }
        }
        if (failures.Count > 0) throw new IOException(string.Join("\n", failures));
    }

    private static bool IsOwnedVersion(Item item)
    {
        if (item.PublishedIdentity is null || !File.Exists(item.Target)) return false;
        using var stream = new FileStream(item.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (ConfigTransferFileIdentity.Read(stream.SafeFileHandle) != item.PublishedIdentity) return false;
        return Convert.ToHexString(SHA256.HashData(stream)) == item.AfterHash;
    }

    private static void CleanupStaging(Item item)
    {
        if (item.StagedPath is null || item.PublishedIdentity is null) return;
        var stage = Path.GetFullPath(item.StagedPath);
        var name = Path.GetFileName(stage);
        if (!string.Equals(Path.GetDirectoryName(stage), Path.GetDirectoryName(item.Target), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(".config-transfer-", StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal))
            throw new InvalidDataException("迁移暂存路径身份无效，未删除文件。");
        CheckPath(stage);
        if (File.Exists(stage) && ConfigTransferFileIdentity.Read(stage) == item.PublishedIdentity) File.Delete(stage);
    }

    private static string BackupPath(Journal journal, Item item)
    {
        if (Path.GetFileName(item.Backup) != item.Backup || !item.Backup.EndsWith(".original", StringComparison.Ordinal))
            throw new InvalidDataException("原配置备份名无效。");
        var path = Path.Combine(journal.BackupDirectory, item.Backup);
        CheckPath(path);
        return path;
    }
    private static void ValidateTarget(Journal journal, string target)
    {
        target = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(journal.DataRoot, target).Replace('\\', '/');
        var local = ConfigTransferService.SettingFileNames.Contains(relative) || relative == "remote-log-rooms.json"
            || relative == ConfigTransferMistletoe.CurrentWorkflowFileName || ConfigTransferMistletoe.IsFlowEntry(relative);
        var resource = journal.BgiRoot is not null && ConfigTransferResources.IsResourceEntry(
            ConfigTransferResources.Prefix + Path.GetRelativePath(journal.BgiRoot, target).Replace('\\', '/'));
        var shared = journal.AdditionalTargets.Contains(target, StringComparer.OrdinalIgnoreCase);
        if (!local && !resource && !shared) throw new InvalidDataException("配置事务目标不在本次设置范围内：" + target);
        CheckPath(target);
    }
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("设置迁移不跟随链接或重解析目录：" + current);
    }
    internal static void WriteAtomic(string path, byte[] bytes, bool overwrite = true)
    {
        using var writing = ConfigFileWriteGate.Acquire(path);
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
