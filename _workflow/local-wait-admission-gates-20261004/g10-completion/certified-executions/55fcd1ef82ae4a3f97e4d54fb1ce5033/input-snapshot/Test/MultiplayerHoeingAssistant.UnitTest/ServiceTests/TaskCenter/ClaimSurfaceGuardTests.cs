using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **声明面守卫**（[2026-09-22 §17.4-A 第 1 条]）：把 R5 设计稿里**承载状态／门禁／证据等级的声明行**
/// 抽成可机械比对的清单，声明面的任何增删改都会改变清单内容。
///
/// **用途**：为「措辞类必改项的复会诊豁免」提供**机械证明**——豁免**仅**在声明面逐行未变时可援引；
/// 声明面一旦变化，即视同语义必改项，照常处置 → 回归 → 复会诊。
///
/// **能力边界（如实）**：只证明**声明行文本未变**；**不**证明该声明本身为真、**不**验证证据指针有效性、
/// **不**检查章节锚点、**不**替代会诊。关键词集合为**保守枚举**（见 <see cref="ClaimPattern"/>），
/// 未命中关键词的行不在声明面内——本守卫不主张「覆盖全部语义」。
///
/// **再生成**：改动声明面后，设环境变量 `CLAIM_SURFACE_REGENERATE=1` 运行本夹具重写清单；
/// 重写结果属**待评审变更**，须连同改动一并提交，不得视为自动通过。
/// </summary>
public sealed class ClaimSurfaceGuardTests
{
    private const string RegenerateEnvVar = "CLAIM_SURFACE_REGENERATE";

    /// <summary>承载状态／门禁／证据等级的声明关键词（保守枚举；新增需评审）。</summary>
    private const string ClaimPattern =
        "已验收|未验收|已接线|未接线|已交付|未闭合|已冻结|已完成|已闭合|保留门禁|已收口|未收口" +
        "|已闭环|未闭环|待验收|待实机|未执行|已执行|已实施|未实施|不得标记完成";

    private static readonly string[] GuardedDocs =
    {
        Path.Combine("Docs", "design", "onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md"),
        Path.Combine("Docs", "design", "onedragon-r5-3-external-start-lifecycle-2026-09-21.md"),
        Path.Combine("Docs", "design", "onedragon-r5-closure-audit-2026-09-22.md"),
        Path.Combine("Docs", "design", "onedragon-r5-owner-decisions-2026-09-24.md"),
        Path.Combine("Docs", "design", "onedragon-r5-handoff-2026-09-21.md"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（声明面守卫需要文档路径）。");
    }

    private static string ManifestPath(string root) => Path.Combine(
        root, "Test", "MultiplayerHoeingAssistant.UnitTest", "ServiceTests", "TaskCenter", "ClaimSurfaceManifest.txt");

    /// <summary>规范化一行声明：去首尾空白，内部空白折叠为单空格（其余字符按原样保留，含加粗等强调）。</summary>
    private static string Normalize(string line) => Regex.Replace(line.Trim(), @"\s+", " ");

    private static string Fingerprint(string normalized)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        // [批次四十七 第二轮会诊建议采纳] 身份用**完整** SHA-256（不再截断 16 位）：该身份是「措辞批豁免」的
        // 机械依据，碰撞风险虽低，但截断收益为零、代价是长期评审可能对 64 位截断提出质疑。
        return Convert.ToHexString(hash);
    }

    /// <summary>抽取声明面：每一行声明 →（指纹, 规范化文本）。按文本序号排序，保证确定性。</summary>
    /// <remarks>
    /// **身份＝文档路径＋规范化全文的**完整** SHA-256＋同文本出现序号**（全文入哈希；
    /// 序号用于区分同文档内**完全相同的重复声明行**——见 `[批次四十七 首轮会诊阻断项处置]`）；
    /// **清单落盘时另附 160 字符摘要**，仅为人工可读——摘要不参与比对，比对只用身份键。
    /// </remarks>
    private static SortedDictionary<string, string> ExtractClaimSurface(string root)
    {
        var pattern = new Regex(ClaimPattern, RegexOptions.Compiled);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in GuardedDocs)
        {
            var path = Path.Combine(root, rel);
            Assert.True(File.Exists(path), $"声明面守卫覆盖的文档缺失：{rel}");
            var relKey = rel.Replace('\\', '/');
            foreach (var raw in File.ReadAllLines(path))
            {
                if (!pattern.IsMatch(raw)) continue;
                var normalized = Normalize(raw);
                if (normalized.Length == 0) continue;
                var preview = normalized.Length <= 160 ? normalized : normalized[..160] + "…";
                // [批次四十七 首轮会诊阻断项处置] 身份＝文档路径＋规范化全文哈希＋**同文本出现序号**：
                // 只按「路径＋哈希」做键会让**同一文档内两条完全相同的声明行互相覆盖**——删除其中一条时身份不变、
                // 守卫静默通过。加入出现序号后，「重复行少了一条」必然改变清单。
                var prefix = relKey + "\u0001" + Fingerprint(normalized) + "\u0001";
                var ordinal = 1;
                while (result.ContainsKey(prefix + ordinal)) ordinal++;
                result[prefix + ordinal] = preview;
            }
        }
        return result;
    }

    /// <summary>清单行＝`文档路径 \u0001 指纹 \u0001 出现序号 \u0001 摘要`；比对只用前三段，摘要在差异报告里显示。</summary>
    private static string Identity(string manifestLine)
    {
        var parts = manifestLine.Split('\u0001');
        return parts.Length >= 3 ? parts[0] + "\u0001" + parts[1] + "\u0001" + parts[2] : manifestLine;
    }

    private static string Render(SortedDictionary<string, string> surface) =>
        string.Join("\n", surface.Select(kv => kv.Key + "\u0001" + kv.Value)) + "\n";

    [Fact]
    public void DesignDocs_ClaimSurface_MatchesReviewedManifest()
    {
        var root = RepoRoot();
        var actual = ExtractClaimSurface(root);
        var manifestPath = ManifestPath(root);

        if (string.Equals(Environment.GetEnvironmentVariable(RegenerateEnvVar), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(manifestPath, Render(actual), new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(manifestPath),
            $"声明面清单缺失：{manifestPath}\\n首次生成：设 {RegenerateEnvVar}=1 运行本夹具，并把生成的清单连同改动一并提交。");

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(manifestPath))
            if (line.Trim().Length > 0) expected[Identity(line)] = line;

        var added = actual.Where(kv => !expected.ContainsKey(kv.Key)).ToList();
        var removed = expected.Where(kv => !actual.ContainsKey(kv.Key)).ToList();

        if (added.Count == 0 && removed.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("声明面发生变化——本改动**不得**援引 §17.4-A 第 1 条「措辞类豁免」，须按语义必改项走处置→回归→复会诊。");
        sb.AppendLine("若确认变化已评审，设 CLAIM_SURFACE_REGENERATE=1 重跑以更新清单，并把清单变更纳入本次提交评审。");
        sb.AppendLine($"新增/变更声明 {added.Count} 行：");
        foreach (var kv in added.Take(20)) sb.AppendLine("  + " + kv.Key + "  :: " + kv.Value);
        if (added.Count > 20) sb.AppendLine($"  …（其余 {added.Count - 20} 行从略）");
        sb.AppendLine($"移除声明 {removed.Count} 行：");
        foreach (var kv in removed.Take(20)) sb.AppendLine("  - " + kv.Key + "  :: " + kv.Value);
        if (removed.Count > 20) sb.AppendLine($"  …（其余 {removed.Count - 20} 行从略）");
        Assert.Fail(sb.ToString());
    }
}
