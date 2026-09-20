using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>演练步骤结果（报告行）。</summary>
public sealed record MigrationRehearsalStep(string Name, bool Success, string Detail);

/// <summary>演练报告（结构化；供 UI 展示与验收单取证）。</summary>
public sealed class MigrationRehearsalReport
{
    public bool Success { get; init; }
    public string RehearsalRoot { get; init; } = "";
    public string ConfigRoot { get; init; } = "";
    public string TransactionRoot { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    /// <summary>快照清单哈希（事务产物）。</summary>
    public string SnapshotManifestHash { get; init; } = "";
    /// <summary>事务标识（取证：与 manifest/台账对照）。</summary>
    public string TransactionId { get; init; } = "";
    /// <summary>最终事务阶段（取证：回滚后应为 `RolledBack`）。</summary>
    public string FinalStage { get; init; } = "";
    /// <summary>参与比对的文件集合（相对路径，排序）。</summary>
    public IReadOnlyList<string> ComparedFiles { get; init; } = [];
    /// <summary>证据路径（manifest／快照目录／演练根）。</summary>
    public IReadOnlyList<string> EvidencePaths { get; init; } = [];
    /// <summary>逐步骤结果（顺序即执行顺序）。</summary>
    public IReadOnlyList<MigrationRehearsalStep> Steps { get; init; } = [];
    public string Summary => (Success ? "演练通过：" : "演练未通过：")
        + string.Join(" → ", Steps.Select(s => s.Name + (s.Success ? "✓" : "✗")));
}

/// <summary>
/// **R5.6/R5.8「迁移演练」入口（§21.4）**：在**独立配置根**上跑完整切换事务（不接触真实 User 目录）并产出报告。
/// **语义**：①**只接受独立根**——若配置根等于真实 User 目录（调用方传入的 `userConfigRoot`）⇒ 直接拒绝；
/// ②演练＝快照→（无迁移变更的）引用更新→激活→**回滚演练**→提交→**真实回滚**→逐字节比对；
/// ③**不授权生产开门**：本入口不执行真实 User 目录切换（须 owner 另行下令）。
/// </summary>
public static class MigrationRehearsal
{
    /// <summary>
    /// 在 `rehearsalRoot` 下创建独立配置根与事务根，跑完整演练并返回报告。
    /// `userConfigRoot` 用于**拒绝**：配置根不得等于真实 User 目录（防御性校验，调用方仍应传独立根）。
    /// </summary>
    public static MigrationRehearsalReport Run(string rehearsalRoot, string? userConfigRoot = null,
        Func<DateTimeOffset>? utcNow = null, Action<MigrationStage>? stageHook = null)
    {
        if (string.IsNullOrWhiteSpace(rehearsalRoot))
            return Fail("", "", "", new MigrationRehearsalStep("参数校验", false, "rehearsalRoot 为空"));
        var baseRoot = Path.GetFullPath(rehearsalRoot);
        var root = Path.Combine(baseRoot, "rehearsal-" + Guid.NewGuid().ToString("N")[..8]);   // **每次新建、独占**
        var configRoot = Path.Combine(root, "independent-config-root");
        var transactionRoot = Path.Combine(root, "transaction");
        var steps = new List<MigrationRehearsalStep>();

        // ① **写入前**独立根校验：不得与真实 User 目录重叠（相等/包含/被包含），不得含重解析点，目标须尚不存在
        if (!string.IsNullOrWhiteSpace(userConfigRoot))
        {
            var user = Path.GetFullPath(userConfigRoot).TrimEnd(Path.DirectorySeparatorChar);
            var cfg = configRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(user, cfg, StringComparison.OrdinalIgnoreCase)
                || cfg.StartsWith(user + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || user.StartsWith(cfg + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练根与真实 User 目录重叠（拒绝，未写入任何内容）"));
        }
        if (MigrationSwitchTransaction.HasReparsePoint(baseRoot))
            return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练根链上存在重解析点（拒绝，未写入任何内容）"));
        if (Directory.Exists(root) || File.Exists(root))
            return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练目录已存在（须使用新建独占目录）"));
        steps.Add(new MigrationRehearsalStep("独立根校验", true, "写入前校验通过：演练使用新建独占独立配置根"));

        var txId = "rehearsal-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(configRoot);
            // 预置两个文件作为基线（空根亦已由夹具单独覆盖）
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":1}", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(configRoot, "sub"));
            File.WriteAllText(Path.Combine(configRoot, "sub", "b.json"), "{\"w\":1}", new UTF8Encoding(false));
            var baselineA = File.ReadAllBytes(Path.Combine(configRoot, "a.json"));
            var baselineB = File.ReadAllBytes(Path.Combine(configRoot, "sub", "b.json"));

            using var tx = new MigrationSwitchTransaction(configRoot, transactionRoot, utcNow, () => new NoopQuiet(),
                stageHook: stageHook);
            // **失败即停**：任一步失败 ⇒ 直接返回（不再制造变更、不做未授权回滚）
            var begin = Step("开启事务", tx.BeginTransaction(txId));
            steps.Add(begin);
            if (!begin.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var snap = Step("全量快照（字节+SHA256+清单哈希）", tx.TakeSnapshot());
            steps.Add(snap);
            if (!snap.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var changes = Step("登记变更归属（演练：修改 a.json）",
                tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]));
            steps.Add(changes);
            if (!changes.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var refUpd = Step("引用更新完成", tx.MarkReferenceUpdateCompleted());
            steps.Add(refUpd);
            if (!refUpd.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var activated = Step("激活（candidate→active）", tx.MarkActivated());
            steps.Add(activated);
            if (!activated.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var rehearsal = Step("回滚演练（复用真实回滚核心）", tx.RehearseRollback());
            steps.Add(rehearsal);
            if (!rehearsal.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var commit = Step("提交（写唯一提交标记）", tx.Commit());
            steps.Add(commit);
            if (!commit.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);

            // 提交后制造**真实变更**，再执行真实回滚并**全文件集字节比对**
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":2}", new UTF8Encoding(false));
            var rollback = tx.Rollback();
            steps.Add(Step("真实回滚（恢复旧字节+按归属清理+撤销激活）", rollback));
            if (!rollback.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            // 全文件集比对：**完整文件集合 + 逐字节**（含被修改与未被修改者；新增文件须不存在）
            // 全文件集比对：**完整文件集合 + 逐字节**（含被修改与未被修改者；新增文件须不存在）
            var after = Directory.EnumerateFiles(configRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(configRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var identical = after.Count == 2 && after[0] == "a.json" && after[1] == "sub/b.json"
                            && BytesEqual(Path.Combine(configRoot, "a.json"), baselineA)
                            && BytesEqual(Path.Combine(configRoot, "sub", "b.json"), baselineB);
            steps.Add(new MigrationRehearsalStep("全文件集逐字节比对", identical,
                identical ? "回滚后完整文件集合与内容均与基线逐字节一致" : "回滚后文件集合或内容与基线不一致"));

            var manifest = tx.LoadManifest();
            steps.Add(new MigrationRehearsalStep("回滚终态校验",
                manifest is not null && manifest.Stage == MigrationStage.RolledBack && manifest.CommitMarker is null,
                manifest is null ? "manifest 不可读" : "stage=" + manifest.Stage + " marker=" + (manifest.CommitMarker ?? "<null>")));
            return new MigrationRehearsalReport
            {
                Success = steps.All(s => s.Success),
                RehearsalRoot = root,
                ConfigRoot = configRoot,
                TransactionRoot = transactionRoot,
                ManifestPath = tx.ManifestPath,
                SnapshotManifestHash = manifest?.SnapshotManifestHash ?? "",
                TransactionId = txId,
                FinalStage = manifest?.Stage.ToString() ?? "",
                ComparedFiles = after,
                EvidencePaths = [tx.ManifestPath, manifest?.SnapshotPath ?? "", root],
                Steps = steps,
            };
        }
        catch (Exception ex)
        {
            steps.Add(new MigrationRehearsalStep("异常", false, ex.GetType().Name + ": " + ex.Message));
            return new MigrationRehearsalReport
            {
                Success = false, RehearsalRoot = root, ConfigRoot = configRoot,
                TransactionRoot = transactionRoot, TransactionId = txId,
                ManifestPath = File.Exists(Path.Combine(transactionRoot, "migration-manifest.json")) ? Path.Combine(transactionRoot, "migration-manifest.json") : "",
                Steps = steps,
            };
        }
    }

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    /// <summary>失败即停：不再制造变更、不做未授权回滚；报告保留**已形成的** manifest/事务标识以便取证。</summary>
    private static MigrationRehearsalReport Abort(MigrationSwitchTransaction tx, List<MigrationRehearsalStep> steps,
        string root, string configRoot, string transactionRoot, string txId)
    {
        var m = tx.LoadManifest();
        return new MigrationRehearsalReport
        {
            Success = false,
            RehearsalRoot = root,
            ConfigRoot = configRoot,
            TransactionRoot = transactionRoot,
            ManifestPath = File.Exists(tx.ManifestPath) ? tx.ManifestPath : "",
            TransactionId = txId,
            FinalStage = m?.Stage.ToString() ?? "",
            SnapshotManifestHash = m?.SnapshotManifestHash ?? "",
            EvidencePaths = File.Exists(tx.ManifestPath) ? [tx.ManifestPath, root] : [root],
            Steps = steps,
        };
    }

    private static MigrationRehearsalStep Step(string name, MigrationResult r)
        => new(name, r.Success, r.Success ? r.Stage.ToString() : r.Reason);

    private static MigrationRehearsalReport Fail(string root, string cfg, string tx, MigrationRehearsalStep step)
        => new() { Success = false, RehearsalRoot = root, ConfigRoot = cfg, TransactionRoot = tx, Steps = [step] };

    private static string Sha256(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }
}
