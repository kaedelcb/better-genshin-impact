using System.Text.RegularExpressions;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **文档 → 夹具名一致性守卫**（[2026-09-22 批次四十]）：R5 设计稿的**批次登记/台账**里大量点名夹具，
/// 但此前**没有任何机械检查**，只靠会诊逐轮人工比对（本会话已多次出现「引用了不存在/已改名的夹具」）。
/// 本守卫把这件事变成可重复检查：从登记章节里抽取**夹具/测试方法名**形状的标识符，逐一到**测试源码**里核对存在性。
/// **能力边界（如实）**：只核对**名字是否存在于测试源码**；不检查该夹具是否真的覆盖了文档所述断言、
/// 不检查行号/章节锚点、也不替代会诊。抽取规则保守（只认 `XxxTests.Method` 与 `Xxx_Under_Score` 两种形状）。
/// </summary>
public sealed class DocsFixtureReferenceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（文档夹具名守卫需要源码路径）。");
    }

    /// <summary>从**文档**里抽取的「夹具/测试方法名」形状标识符 → 必须在**测试源码**中出现。</summary>
    [Fact]
    public void DesignDocs_FixtureNames_AllExistInTestSources()
    {
        var root = RepoRoot();
        var docs = new[]
        {
            Path.Combine(root, "Docs", "design", "onedragon-r5-3-external-start-lifecycle-2026-09-21.md"),
            Path.Combine(root, "Docs", "design", "onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md"),
            // [批次四十二] 收口自审稿同样纳入（其点名的夹具也必须真实存在）
            Path.Combine(root, "Docs", "design", "onedragon-r5-closure-audit-2026-09-22.md"),
        };
        var testSources = string.Join("\n", Directory
            .EnumerateFiles(Path.Combine(root, "Test"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        // 抽取两种保守形状：①带点的方法引用 `SomeTests.Some_Method`；②纯下划线方法名 `Some_Method_Name`（≥2 段）
        var dotted = new Regex(@"\b([A-Z][A-Za-z0-9]*Tests)\.([A-Z][A-Za-z0-9_]{6,})\b", RegexOptions.Compiled);
        var underscored = new Regex(@"\b([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]{5,})\b", RegexOptions.Compiled);

        var missing = new List<string>();
        var checkedCount = 0;
        foreach (var doc in docs)
        {
            var text = File.ReadAllText(doc);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in dotted.Matches(text))
            {
                names.Add(m.Groups[1].Value);          // 类名
                names.Add(m.Groups[2].Value);          // 方法名
            }
            foreach (Match m in underscored.Matches(text)) names.Add(m.Groups[1].Value);

            foreach (var name in names)
            {
                // 跳过文档自身的章节/条款标识（如 §24.x 引用后的编号形状）与纯大写枚举
                if (name is "R5" or "P50") continue;
                checkedCount++;
                if (!testSources.Contains(name, StringComparison.Ordinal))
                    missing.Add(Path.GetFileName(doc) + " → " + name);
            }
        }

        Assert.True(checkedCount > 100, "抽取到的夹具名数量异常偏少（实际 " + checkedCount + "）——守卫抽取规则或文档结构可能已变化。");
        Assert.True(missing.Count == 0,
            "文档点名了**测试源码中不存在**的夹具/类名（" + missing.Count + " 个）：\n" + string.Join("\n", missing.Distinct().Take(40)));
    }

    /// <summary>守卫自身的最小反例：抽取规则的两种形状都必须能命中（防「提取不到任何名字」式假绿）。</summary>
    [Theory]
    [InlineData("`FooTests.Bar_Baz_Quux` 与 `Standalone_Name_Here` 均在册")]
    public void Extractor_FindsBothShapes(string sample)
    {
        var dotted = new Regex(@"\b([A-Z][A-Za-z0-9]*Tests)\.([A-Z][A-Za-z0-9_]{6,})\b");
        var underscored = new Regex(@"\b([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]{5,})\b");
        Assert.Contains("FooTests", dotted.Matches(sample).Select(m => m.Groups[1].Value));
        Assert.Contains("Bar_Baz_Quux", dotted.Matches(sample).Select(m => m.Groups[2].Value));
        Assert.Contains("Standalone_Name_Here", underscored.Matches(sample).Select(m => m.Groups[1].Value));
    }
}
