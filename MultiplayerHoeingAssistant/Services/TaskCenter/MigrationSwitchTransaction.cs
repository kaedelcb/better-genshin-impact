using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>事务阶段（持久化；`Committed` 之前一律**不可生产执行**）。</summary>
public enum MigrationStage
{
    None = 0,
    Snapshotting = 1,
    SnapshotReady = 2,
    UpdatingReferences = 3,
    Activating = 4,
    Committed = 5,
    RollingBack = 6,
    RolledBack = 7,
}

/// <summary>迁移 manifest（快照路径、逐文件 SHA256 清单、清单哈希、回滚入口、阶段、唯一提交标记）。</summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    /// <summary>逐文件清单：相对路径 → 原文件 SHA256（十六进制）。</summary>
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>清单哈希＝对「按相对路径排序的 `相对路径:哈希` 行」拼接后取 SHA256（防清单被改写）。</summary>
    [JsonPropertyName("manifestHash")] public string ManifestHash { get; set; } = "";
    [JsonPropertyName("stage")] public MigrationStage Stage { get; set; }
    /// <summary>唯一提交标记：非空且与事务号一致 ⇒ 已提交（**提交前为空**）。</summary>
    [JsonPropertyName("commitMarker")] public string? CommitMarker { get; set; }
    /// <summary>回滚演练是否通过（提交前置：未通过不得提交）。</summary>
    [JsonPropertyName("rollbackRehearsed")] public bool RollbackRehearsed { get; set; }
}

/// <summary>事务操作结果（结构化；失败带原因，调用方据此阻断提交）。</summary>
public sealed record MigrationResult(bool Success, string Reason, MigrationStage Stage)
{
    public static MigrationResult Ok(MigrationStage s) => new(true, "", s);
    public static MigrationResult Fail(string reason, MigrationStage s) => new(false, reason, s);
}

/// <summary>
/// **R5.6 事务迁移切换（切换事务四步 + I1 事务工程合同）**。
/// ①切前强制全量快照（原件字节 + 逐文件 SHA256 + 清单哈希）→ ②快照路径与回滚入口写入 manifest
/// → ③引用更新事务（由调用方执行）+ 激活（`candidate → active`）→ ④回滚演练通过才允许提交。
/// **未提交绝不生产执行**：<see cref="IsProductionExecutable"/> 仅在 `Committed` 后为真。
/// **开发验证只用独立配置根**——本类不区分路径来源，真实 User 目录切换由调用方另行下令。
/// </summary>
public sealed class MigrationSwitchTransaction
{
    private readonly string _configRoot;
    private readonly string _transactionRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<MigrationStage>? _stageHook;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Action<MigrationStage>? stageHook = null)
    {
        _configRoot = configRoot ?? throw new ArgumentNullException(nameof(configRoot));
        _transactionRoot = transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _stageHook = stageHook; // 夹具接缝：在**每个阶段写入前后**回调，用于崩溃注入（生产=null）
    }

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");

    /// <summary>快照目录（每次事务一个，位于事务根下）。</summary>
    public string SnapshotPathOf(string transactionId) => Path.Combine(_transactionRoot, "snapshot-" + transactionId);

    /// <summary>读取 manifest（不存在返回 null）。</summary>
    public MigrationManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// **「未提交绝不生产执行」闸门**：仅当 manifest 阶段＝`Committed` **且**提交标记与事务号一致时为真。
    /// 其余阶段（含 `Activating`／`RollingBack`／`RolledBack`／无 manifest）一律**不可**生产执行。
    /// </summary>
    public bool IsProductionExecutable()
    {
        var m = LoadManifest();
        return m is not null
               && m.Stage == MigrationStage.Committed
               && !string.IsNullOrEmpty(m.CommitMarker)
               && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 步骤①②：**全量快照**配置根（原件字节复制 + 逐文件 SHA256 + 清单哈希）并写入 manifest
    /// （含快照路径、回滚入口、阶段＝`SnapshotReady`、**提交标记为空**）。
    /// </summary>
    public MigrationResult TakeSnapshotAndWriteManifest(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
        if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", MigrationStage.None);

        Directory.CreateDirectory(_transactionRoot);
        WriteStage(MigrationStage.Snapshotting);

        var snapshotRoot = SnapshotPathOf(transactionId);
        var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in EnumerateFiles(_configRoot))
            {
                var rel = Path.GetRelativePath(_configRoot, file).Replace('\\', '/');
                var bytes = File.ReadAllBytes(file);
                var target = Path.Combine(snapshotRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);                    // 原件字节
                fileHashes[rel] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }
        catch (IOException ex)
        {
            return MigrationResult.Fail("snapshot_io_failed:" + ex.GetType().Name, MigrationStage.Snapshotting);
        }

        var manifest = new MigrationManifest
        {
            TransactionId = transactionId,
            CreatedAtUtc = _utcNow(),
            ConfigRoot = _configRoot,
            SnapshotPath = snapshotRoot,
            RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
            FileHashes = fileHashes,
            ManifestHash = ComputeManifestHash(fileHashes),
            Stage = MigrationStage.SnapshotReady,
            CommitMarker = null,
            RollbackRehearsed = false,
        };
        WriteManifest(manifest);
        return MigrationResult.Ok(MigrationStage.SnapshotReady);
    }

    /// <summary>清单哈希：对「按相对路径排序的 `相对路径:哈希`」行拼接后取 SHA256（十六进制）。</summary>
    public static string ComputeManifestHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        var sb = new StringBuilder();
        foreach (var pair in fileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// 校验快照完整性：①manifest 可读且**清单哈希自洽**；②快照目录内每个登记文件的字节哈希与清单一致；
    /// ③**无未登记的多余文件**（快照不得夹带）。返回失败原因（空＝通过）。
    /// </summary>
    public string VerifySnapshot()
    {
        var m = LoadManifest();
        if (m is null) return "manifest_missing_or_corrupt";
        if (!string.Equals(m.ManifestHash, ComputeManifestHash(m.FileHashes), StringComparison.Ordinal))
            return "manifest_hash_mismatch";
        if (!Directory.Exists(m.SnapshotPath)) return "snapshot_missing";

        var onDisk = EnumerateFiles(m.SnapshotPath)
            .Select(f => Path.GetRelativePath(m.SnapshotPath, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extra in onDisk.Where(f => !m.FileHashes.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;

        foreach (var pair in m.FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var target = Path.Combine(m.SnapshotPath, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) return "snapshot_file_missing:" + pair.Key;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
            if (!string.Equals(hash, pair.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + pair.Key;
        }
        return "";
    }

    /// <summary>步骤③前半：标记「进行引用更新＋激活」——此阶段起**不可生产执行**。</summary>
    public MigrationResult BeginReferenceUpdateAndActivation(string transactionId)
    {
        var m = LoadManifest();
        if (m is null || !string.Equals(m.TransactionId, transactionId, StringComparison.Ordinal))
            return MigrationResult.Fail("manifest_missing_or_transaction_mismatch", MigrationStage.None);
        m.Stage = MigrationStage.UpdatingReferences;
        WriteManifest(m);
        m.Stage = MigrationStage.Activating;
        WriteManifest(m);
        return MigrationResult.Ok(MigrationStage.Activating);
    }

    /// <summary>
    /// 步骤④：**回滚演练**（在独立副本上执行完整回滚并逐字节比对）——演练通过才置 `RollbackRehearsed`。
    /// **真实目录不参与演练**（演练只用快照与临时副本）。
    /// </summary>
    public MigrationResult RehearseRollback()
    {
        var m = LoadManifest();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_corrupt", MigrationStage.None);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        var rehearsalRoot = Path.Combine(_transactionRoot, "rehearsal");
        try
        {
            if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, recursive: true);
            Directory.CreateDirectory(rehearsalRoot);
            RestoreFromSnapshot(m.SnapshotPath, rehearsalRoot, m.FileHashes);
            // 逐字节比对：演练副本必须与快照逐文件哈希一致
            foreach (var pair in m.FileHashes)
            {
                var target = Path.Combine(rehearsalRoot, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(target)) return MigrationResult.Fail("rehearsal_missing:" + pair.Key, m.Stage);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
                if (!string.Equals(hash, pair.Value, StringComparison.Ordinal))
                    return MigrationResult.Fail("rehearsal_hash_mismatch:" + pair.Key, m.Stage);
            }
        }
        catch (IOException ex)
        {
            return MigrationResult.Fail("rehearsal_io_failed:" + ex.GetType().Name, m.Stage);
        }
        finally
        {
            try { if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, recursive: true); } catch { }
        }

        m.RollbackRehearsed = true;
        WriteManifest(m);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>
    /// **提交**：前置＝回滚演练已通过、快照校验通过；写入**唯一提交标记**（＝事务号）并把阶段置 `Committed`
    /// ⇒ 自此才允许生产执行。任一前置不满足 ⇒ 拒绝提交（结构化 reason）。
    /// </summary>
    public MigrationResult Commit(string transactionId)
    {
        var m = LoadManifest();
        if (m is null || !string.Equals(m.TransactionId, transactionId, StringComparison.Ordinal))
            return MigrationResult.Fail("manifest_missing_or_transaction_mismatch", MigrationStage.None);
        if (!m.RollbackRehearsed) return MigrationResult.Fail("rollback_not_rehearsed", m.Stage);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        m.CommitMarker = transactionId; // 唯一提交标记
        m.Stage = MigrationStage.Committed;
        WriteManifest(m);
        return MigrationResult.Ok(MigrationStage.Committed);
    }

    /// <summary>
    /// **回滚**：①恢复快照内全部旧文件字节；②**删除本事务在配置根新增的文件**（快照中不存在的文件）；
    /// ③**撤销激活**（清除提交标记并置 `RolledBack`）。任一步失败 ⇒ 返回失败（调用方据此**阻断提交**）。
    /// </summary>
    public MigrationResult Rollback(string transactionId)
    {
        var m = LoadManifest();
        if (m is null || !string.Equals(m.TransactionId, transactionId, StringComparison.Ordinal))
            return MigrationResult.Fail("manifest_missing_or_transaction_mismatch", MigrationStage.None);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        m.Stage = MigrationStage.RollingBack;
        WriteManifest(m);
        try
        {
            RestoreFromSnapshot(m.SnapshotPath, _configRoot, m.FileHashes);

            // 删除事务新增文件（配置根内、快照未登记者）
            foreach (var file in EnumerateFiles(_configRoot))
            {
                var rel = Path.GetRelativePath(_configRoot, file).Replace('\\', '/');
                if (!m.FileHashes.ContainsKey(rel)) File.Delete(file);
            }
        }
        catch (IOException ex)
        {
            return MigrationResult.Fail("rollback_io_failed:" + ex.GetType().Name, MigrationStage.RollingBack);
        }

        m.CommitMarker = null;              // 撤销激活：无提交标记 ⇒ 不可生产执行
        m.Stage = MigrationStage.RolledBack;
        WriteManifest(m);
        return MigrationResult.Ok(MigrationStage.RolledBack);
    }

    private static void RestoreFromSnapshot(string snapshotRoot, string targetRoot, IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var rel in hashes.Keys)
        {
            var source = Path.Combine(snapshotRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    private void WriteStage(MigrationStage stage) => _stageHook?.Invoke(stage);

    private void WriteManifest(MigrationManifest manifest)
    {
        Directory.CreateDirectory(_transactionRoot);
        _stageHook?.Invoke(manifest.Stage);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true); // 原子替换（同目录改名）
    }
}