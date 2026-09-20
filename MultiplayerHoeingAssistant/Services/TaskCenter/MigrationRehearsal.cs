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
        Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(rehearsalRoot))
            return Fail("", "", "", new MigrationRehearsalStep("参数校验", false, "rehearsalRoot 为空"));
        var root = Path.GetFullPath(rehearsalRoot);
        var configRoot = Path.Combine(root, "independent-config-root");
        var transactionRoot = Path.Combine(root, "transaction");
        var steps = new List<MigrationRehearsalStep>();

        // ① 独立根校验：不得等于真实 User 目录
        if (!string.IsNullOrWhiteSpace(userConfigRoot)
            && string.Equals(Path.GetFullPath(userConfigRoot), Path.GetFullPath(configRoot), StringComparison.OrdinalIgnoreCase))
            return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "配置根等于真实 User 目录（拒绝）"));
        steps.Add(new MigrationRehearsalStep("独立根校验", true, "演练使用独立配置根，不接触真实 User 目录"));

        try
        {
            Directory.CreateDirectory(configRoot);
            // 预置两个文件作为基线（空根亦已由夹具单独覆盖）
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":1}", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(configRoot, "sub"));
            File.WriteAllText(Path.Combine(configRoot, "sub", "b.json"), "{\"w\":1}", new UTF8Encoding(false));
            var beforeA = Sha256(Path.Combine(configRoot, "a.json"));
            var beforeB = Sha256(Path.Combine(configRoot, "sub", "b.json"));

            using var tx = new MigrationSwitchTransaction(configRoot, transactionRoot, utcNow, () => new NoopQuiet());
            var txId = "rehearsal-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            steps.Add(Step("开启事务", tx.BeginTransaction(txId)));
            steps.Add(Step("全量快照（字节+SHA256+清单哈希）", tx.TakeSnapshot()));
            steps.Add(Step("登记变更归属（演练：修改 a.json）",
                tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }])));
            steps.Add(Step("引用更新完成", tx.MarkReferenceUpdateCompleted()));
            steps.Add(Step("激活（candidate→active）", tx.MarkActivated()));
            steps.Add(Step("回滚演练（复用真实回滚核心）", tx.RehearseRollback()));
            steps.Add(Step("提交（写唯一提交标记）", tx.Commit()));

            // 演练后制造真实变更，再执行真实回滚并逐字节比对
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":2}", new UTF8Encoding(false));
            var rollback = tx.Rollback();
            steps.Add(Step("真实回滚（恢复旧字节+按归属清理+撤销激活）", rollback));
            var afterA = Sha256(Path.Combine(configRoot, "a.json"));
            var afterB = Sha256(Path.Combine(configRoot, "sub", "b.json"));
            var identical = string.Equals(beforeA, afterA, StringComparison.Ordinal)
                            && string.Equals(beforeB, afterB, StringComparison.Ordinal);
            steps.Add(new MigrationRehearsalStep("逐字节比对", identical, identical ? "回滚后与基线逐字节一致" : "回滚后与基线不一致"));

            var manifest = tx.LoadManifest();
            var report = new MigrationRehearsalReport
            {
                Success = steps.All(s => s.Success),
                RehearsalRoot = root,
                ConfigRoot = configRoot,
                TransactionRoot = transactionRoot,
                ManifestPath = tx.ManifestPath,
                SnapshotManifestHash = manifest?.SnapshotManifestHash ?? "",
                Steps = steps,
            };
            return report;
        }
        catch (Exception ex)
        {
            steps.Add(new MigrationRehearsalStep("异常", false, ex.GetType().Name + ": " + ex.Message));
            return new MigrationRehearsalReport
            {
                Success = false, RehearsalRoot = root, ConfigRoot = configRoot,
                TransactionRoot = transactionRoot, Steps = steps,
            };
        }
    }

    private static MigrationRehearsalStep Step(string name, MigrationResult r)
        => new(name, r.Success, r.Success ? r.Stage.ToString() : r.Reason);

    private static MigrationRehearsalReport Fail(string root, string cfg, string tx, MigrationRehearsalStep step)
        => new() { Success = false, RehearsalRoot = root, ConfigRoot = cfg, TransactionRoot = tx, Steps = [step] };

    private static string Sha256(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }
}