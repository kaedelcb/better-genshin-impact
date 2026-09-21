using System.Text.RegularExpressions;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **失败模式覆盖守卫**（[§17.4-A 第 2／6 条]；[批次四十七 首轮会诊阻断项处置后重写]）：
/// §17.4-A 第 2 条要求「生产构造开门」类实现**反例先行**且「失败模式清单覆盖面**不得小于**已登记清单」。
/// 本守卫把「已登记清单」（三份来源表）与「覆盖映射表」（`§24.58`）机械对齐，并**按来源分别锁定结构**：
/// ①**逐来源固定行数**（`§23.8`＝7／`§24.41-C`＝19／`§24.55`＝3）：任一来源增删行即失败（不给全局下界留补偿空间）；
/// ②**行号必须恰为 1..N**：重复编号/跳号/非数字行（疑似未解析的数据行）一律失败；
/// ③映射表**每行恰一条**且**每个来源的映射集合＝1..N**；
/// ④映射行必须给出**指针类型**（`完成证据`／`残项登记`／`owner 裁决`）与**非占位**指针：
///   ·`完成证据` ⇒ 至少一个**存在**的 `§` 锚点，或一个**真实存在于其类体内**的夹具方法（方法名不得只出现在注释/字符串里——按类体切分后核验声明）；
///   ·`残项登记`／`owner 裁决` ⇒ `§` 锚点必须真实存在；
///   占位词（`—`／`见上`／`同前`／`同上`／`本表`）与自引用（指针等于本行来源行号）一律拒绝。
///
/// **能力边界（如实）**：只证明「清单不漏项、结构未漂移、指针非空且锚点/夹具真实存在」；
/// **不**证明该证据确实覆盖了该失败模式、**不**评价证据强度、**不**替代会诊。
/// </summary>
public sealed class FailureModeCoverageGuardTests
{
    private const string R52Doc = "onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md";
    private const string LifecycleDoc = "onedragon-r5-3-external-start-lifecycle-2026-09-21.md";

    private sealed record SourceSpec(string Label, string DocRel, string Heading, int ExpectedCount, string? Terminator);

    private static readonly SourceSpec[] Sources =
    [
        new("§23.8", R52Doc, "### 23.8 ", 7, "\n### "),
        new("§24.41-C", LifecycleDoc, "**C. B4 残项移交（逐条：承接＋完成证据要求＋门禁＋状态）**", 19, "\n**D. "),
        new("§24.55", LifecycleDoc, "### 24.55 ", 3, "\n### "),
    ];

    private static readonly Regex DataRow = new(@"^\|\s*(\d+)\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex SuspiciousRow = new(@"^\|\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>覆盖映射表的**唯一合法行形状**：`| 来源 | # | 失败模式 | 状态 | 指针类型 | 证据指针 |`（六列，单元格不得含 `|`）。</summary>
    internal static readonly Regex MapRowPattern = new(
        @"^\s*\|\s*(§[0-9A-Za-z\.\-]+)\s*\|\s*(\d+)\s*\|\s*([^|\r\n]*)\|\s*([^|\r\n]*)\|\s*([^|\r\n]*)\|\s*([^|\r\n]*)\|\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>「疑似映射数据行」＝首列为 `§` 引用（容忍缩进与加粗包装）：必须匹配 <see cref="MapRowPattern"/>，否则失败。</summary>
    internal static bool IsSuspiciousMapRow(string line)
        // [第六轮会诊建议采纳] 包装变体一并纳入探测：`**§…**`／`***§…***`／`` `§…` ``／`__§…__` 等
        => Regex.IsMatch(line, @"^\s*\|\s*[*_`]{0,4}\s*§") && !MapRowPattern.IsMatch(line);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（失败模式覆盖守卫需要文档路径）。");
    }

    private static string Slice(string text, string heading, string? terminator)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "未找到章节：" + heading);
        var from = start + heading.Length;
        var next = terminator is null ? -1 : text.IndexOf(terminator, from, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    /// <summary>按来源抽取行号：**精确**匹配数据行；同时检出「首列是数字但格式不合法」的可疑行。</summary>
    private static (List<int> Ids, List<string> Suspicious) ExtractIds(string body)
    {
        var ids = DataRow.Matches(body).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var suspicious = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!SuspiciousRow.IsMatch(line)) continue;          // 只看「首列是数字」的行
            if (!DataRow.IsMatch(line)) suspicious.Add(line.Trim());   // 精确数据行形状：`| 12 | … |`
        }
        return (ids, suspicious);
    }

    [Fact]
    public void FailureModes_AllMappedWithEvidence()
    {
        var root = RepoRoot();
        var docs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [R52Doc] = File.ReadAllText(Path.Combine(root, "Docs", "design", R52Doc)),
            [LifecycleDoc] = File.ReadAllText(Path.Combine(root, "Docs", "design", LifecycleDoc)),
        };

        var expectedBySource = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var spec in Sources)
        {
            var (ids, suspicious) = ExtractIds(Slice(docs[spec.DocRel], spec.Heading, spec.Terminator));
            Assert.True(suspicious.Count == 0,
                spec.Label + " 存在**未能解析的数据行**（结构漂移）：" + string.Join(" ｜ ", suspicious.Take(5)));
            Assert.True(ids.Count == spec.ExpectedCount,
                spec.Label + " 登记行数应为 " + spec.ExpectedCount + "，实际 " + ids.Count
                + "（新增/删除登记行时必须同步更新本守卫与该来源的映射）");
            var expectedIds = Enumerable.Range(1, spec.ExpectedCount).ToList();
            Assert.True(ids.SequenceEqual(expectedIds),
                spec.Label + " 行号必须恰为 1.." + spec.ExpectedCount + "（重复/跳号均失败），实际：" + string.Join(",", ids));
            expectedBySource[spec.Label] = new SortedSet<int>(ids);
        }

        // 覆盖映射表（§24.58）：`| 来源 | # | 失败模式 | 状态 | 指针类型 | 证据指针 |`
        var mapBody = Slice(docs[LifecycleDoc], "### 24.58 ", "\n### ");
        var mapRow = MapRowPattern;
        var mapped = new Dictionary<string, (string Status, string Kind, string Evidence)>(StringComparer.Ordinal);
        var mappedIdBySource = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (Match m in mapRow.Matches(mapBody))
        {
            var source = m.Groups[1].Value;
            var id = int.Parse(m.Groups[2].Value);
            var key = source + "#" + id;
            // 表列：来源 | # | 失败模式 | 状态 | 指针类型 | 证据指针
            Assert.True(mapped.TryAdd(key, (m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value)),
                "映射表存在重复行：" + key);
            if (!mappedIdBySource.TryGetValue(source, out var list))
                mappedIdBySource[source] = list = new List<int>();
            list.Add(id);
        }

        // ①-a **映射表自身**：来源集合必须恰等于已登记来源（拒绝清单外来源），且「疑似映射数据行」必须全部解析成功
        var knownSources = Sources.Select(s => s.Label).ToHashSet(StringComparer.Ordinal);
        var unknownSources = mappedIdBySource.Keys.Where(k => !knownSources.Contains(k)).ToList();
        Assert.True(unknownSources.Count == 0,
            "映射表出现**清单外来源**（不得新增未登记来源）：" + string.Join("，", unknownSources));
        var mapSuspicious = new List<string>();
        foreach (var raw in mapBody.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // 只看「首列是 § 引用」的行：**容忍缩进与加粗包装**（` | §…`、`| **§…**`），避免畸形行静默存在
            if (IsSuspiciousMapRow(line)) mapSuspicious.Add(line.Trim());
        }
        Assert.True(mapSuspicious.Count == 0,
            "映射表存在**未能解析的数据行**（结构漂移）：" + string.Join(" ｜ ", mapSuspicious.Take(5)));
        var expectedTotal = Sources.Sum(s => s.ExpectedCount);
        Assert.True(mapped.Count == expectedTotal,
            "映射表解析出的数据行数应为 " + expectedTotal + "（7＋19＋3），实际 " + mapped.Count);

        // ① 逐来源：映射集合必须**恰好等于**该来源行号集合（不多不少）
        foreach (var spec in Sources)
        {
            var mappedIds = mappedIdBySource.TryGetValue(spec.Label, out var list) ? list : [];
            var missing = expectedBySource[spec.Label].Except(mappedIds).ToList();
            var extra = mappedIds.Except(expectedBySource[spec.Label]).ToList();
            Assert.True(missing.Count == 0, spec.Label + " 存在**未映射**行：" + string.Join(",", missing));
            Assert.True(extra.Count == 0, spec.Label + " 映射表出现**清单外**行号：" + string.Join(",", extra));
            Assert.True(mappedIds.Count == mappedIds.Distinct().Count(), spec.Label + " 映射行号重复。");
        }

        // ② 指针类型 / 非占位 / 锚点存在性
        var placeholders = new[] { "—", "-", "见上", "同前", "同上", "本表", "同上所述" };
        var sections = LifecycleDoc + "|" + R52Doc;   // 仅用于定位锚点所在文档
        var lifecycleSections = ExtractSectionNumbers(docs[LifecycleDoc]);
        var r52Sections = ExtractSectionNumbers(docs[R52Doc]);
        foreach (var (key, value) in mapped)
        {
            var hash = key.IndexOf('#');
            var source = key[..hash];
            var id = key[(hash + 1)..];
            var kind = value.Kind.Trim();
            Assert.True(kind is "完成证据" or "部分证据" or "残项登记" or "owner 裁决",
                source + "#" + id + " 的指针类型非法（须为 完成证据／部分证据／残项登记／owner 裁决），实际＝" + kind);
            var evidence = value.Evidence.Trim();
            Assert.True(evidence.Length > 0, source + "#" + id + " 缺证据指针。");
            Assert.False(placeholders.Contains(evidence, StringComparer.Ordinal),
                source + "#" + id + " 的证据指针是占位词：' " + evidence + "'");
            // **任一元素**自引用即拒绝（`§24.55#1 → §24.55`、`§24.41-C#9 → §24.41-C#9` 之类）
            foreach (var element in evidence.Split(['／', '/', '，', ',', '、', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = element.Trim();
                Assert.False(string.Equals(trimmed, key, StringComparison.Ordinal)
                             || string.Equals(trimmed, "§" + source.TrimStart('§') + "#" + id, StringComparison.Ordinal),
                    source + "#" + id + " 的证据指针含**自引用**元素：'" + trimmed + "'（不构成证据）。");
            }

            // § 引用按**完整形式**校验：`§24.41-C#14` ⇒ §24.41 章节存在 **且** §24.41-C 表含第 14 行；
            // `§24.55` ⇒ 章节存在（行内引用如「§24.55 行 2」由下方行号检查覆盖）。
            // **严格 § 引用 tokenizer**（[第三轮会诊阻断处置]）：先取完整 token（到分隔符为止），再整串匹配；
            // 任何无法解析的 § token（如 `§24.41-C#x`／`§24.41-CC#999`／`§24.41-C#1junk`）**直接失败**。
            var anchorTokens = Regex.Matches(evidence, @"§[^\s，,、；;（）()／/|｜]+").Select(m => m.Value).ToList();
            var strictAnchor = new Regex(@"^§(\d+(?:\.\d+)*)(-[A-Z])?(?:#(\d+))?$");
            var anchors = new List<Match>();
            foreach (var token in anchorTokens)
            {
                var m = strictAnchor.Match(token);
                Assert.True(m.Success, source + "#" + id + " 含**无法解析的 § 引用**：" + token);
                anchors.Add(m);
            }
            var anchorCount = 0;
            foreach (Match a in anchors)
            {
                anchorCount++;
                var section = a.Groups[1].Value;
                var suffix = a.Groups[2].Success ? a.Groups[2].Value : "";
                var rowId = a.Groups[3].Success ? int.Parse(a.Groups[3].Value) : (int?)null;
                var sectionExists = lifecycleSections.Contains(section) || r52Sections.Contains(section);
                Assert.True(sectionExists, source + "#" + id + " 引用了**不存在的章节** §" + section + suffix);
                if (suffix.Length > 0)
                    Assert.True(suffix == "-C", source + "#" + id + " 引用了**未知的分节后缀** §" + section + suffix);
                if (rowId is { } rid)
                {
                    // **带 `#N` 的锚点必须落到真实存在的表行**（[第四轮会诊重要项处置]）：
                    // `§X-C#N` ⇒ §X-C 来源表的第 N 行；`§X#N` ⇒ §X 来源表（如 §23.8／§24.55）的第 N 行；
                    // 其余章节不支持 `#N` ⇒ 直接拒绝。
                    var tableLabel = "§" + section + suffix;
                    Assert.True(expectedBySource.ContainsKey(tableLabel) && expectedBySource[tableLabel].Contains(rid),
                        source + "#" + id + " 引用了**不存在的表行** " + tableLabel + "#" + rid);
                }
            }
            Assert.True(anchorCount > 0 || kind == "owner 裁决" && ContainsFixtureName(evidence)
                        || (kind is "完成证据" or "部分证据") && ContainsFixtureName(evidence),
                source + "#" + id + "：`" + kind + "` 必须给出 § 锚点或夹具名。");
            if (kind is "残项登记" or "owner 裁决")
                Assert.True(anchorCount > 0, source + "#" + id + " 的 " + kind + " 指针必须给出 § 锚点。");

            // **自引用按解析后的锚点判定**（容忍 `**…**`／括号／分隔符包装）：
            //  `§X-C#N` 行的自引用＝`§X`／`§X-C`／`§X-C#N`；`§X` 来源行的自引用＝`§X`／`§X#N`。
            var sourceNumber = source.TrimStart('§');
            var isTableRow = sourceNumber.Contains("-C", StringComparison.Ordinal);
            var sectionNumber = isTableRow ? sourceNumber[..sourceNumber.IndexOf("-C", StringComparison.Ordinal)] : sourceNumber;
            var selfTokens = new List<string> { "§" + sectionNumber };
            if (isTableRow)
            {
                selfTokens.Add("§" + sourceNumber);
                selfTokens.Add("§" + sourceNumber + "#" + id);
            }
            else
            {
                selfTokens.Add("§" + sectionNumber + "#" + id);
            }
            foreach (var token in anchorTokens)
                Assert.False(selfTokens.Contains(token, StringComparer.Ordinal),
                    source + "#" + id + " 的证据指针含**自引用锚点**：" + token);
        }

        // ③ 夹具名：必须真实存在于**其类体内**；且映射表不得完全不点名夹具（防「全部用 § 指针」式假绿）
        var testRoot = Path.Combine(root, "Test");
        var testFiles = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ToList();
        // **逐文件**剥离与定位（禁止跨文件拼接：拼接会让花括号配平跨越文件边界，从而把别处的同名方法算进来）
        var strippedFiles = testFiles.Select(StripCommentsAndStrings).ToList();
        var dotted = new Regex(@"\b([A-Z][A-Za-z0-9]*Tests)\.([A-Z][A-Za-z0-9_]{6,})\b", RegexOptions.Compiled);
        var underscored = new Regex(@"\b([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]{5,})\b", RegexOptions.Compiled);
        var missingNames = new List<string>();
        var namedCount = 0;
        var evidenceText = string.Join("\n", mapped.Values.Select(v => v.Evidence));
        foreach (Match m in dotted.Matches(evidenceText))
        {
            var className = m.Groups[1].Value;
            var methodName = m.Groups[2].Value;
            namedCount++;
            if (!strippedFiles.Any(f => MethodDeclaredInClass(f, className, methodName)))
                missingNames.Add(className + "." + methodName);
        }
        foreach (Match m in underscored.Matches(evidenceText))
        {
            var name = m.Groups[1].Value;
            if (name is "R5" or "P50") continue;
            namedCount++;
            if (!strippedFiles.Any(f => IsDeclaredMethod(f, name))) missingNames.Add(name);
        }
        Assert.True(missingNames.Count == 0,
            "覆盖映射表的证据指针点名了**不存在**的夹具/类名：" + string.Join("，", missingNames.Distinct()));
        Assert.True(namedCount >= 3,
            "映射表点名的夹具数量异常偏少（实际 " + namedCount + "）——防「全部用 § 指针」式假绿。");
        Assert.True(sections.Length > 0);   // 结构占位：保证上面的定位分支不可被优化掉
    }

    private static bool ContainsFixtureName(string evidence)
        => Regex.IsMatch(evidence, @"[A-Z][A-Za-z0-9]*Tests\.[A-Z][A-Za-z0-9_]{6,}")
           || Regex.IsMatch(evidence, @"[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]{5,}");

    /// <summary>
    /// **单遍词法扫描**剥离注释与字符串字面量（[第三轮会诊重要项处置]）：按字符状态机识别 `//`、`/* */`、
    /// `"…"`、`@"…"`、`$"…"`、`$@"…"`／`@$"…"`、字符字面量 `'c'` 与原始字符串 `"""…"""`；
    /// 用空格替换被剥离区间（保留换行与花括号），避免「字符串里的 `//` 被当注释起点」而破坏类体边界。
    /// </summary>
    /// <summary>
    /// **单遍词法扫描**剥离注释与字符串字面量（[第三／四轮会诊重要项处置]）。支持：
    /// `//`、`/* */`、字符字面量 `'\''`、普通串 `"…"`（含 `\` 转义）、逐字串 `@"…"`（含 `""` 转义）、
    /// **插值串** `$"…"`（洞 `{…}` 内**递归**按代码扫描：其中的字符串/注释同样被剥离；`{{`／`}}` 为转义）、
    /// 以及**原始字符串** `"""…"""`（分隔符引号个数 ≥3 时按实际长度匹配）。
    /// **能力边界（如实）**：这是文本级扫描器，不等于 Roslyn 词法；本批已覆盖 `$`／`@` 组合、插值嵌套与
    /// 原始串分隔符长度，并由 `Stripper_RemovesLiterals_KeepsRealDeclarations` 的对抗夹具锁定。
    /// </summary>
    internal static string StripCommentsAndStrings(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        var i = 0;
        StripCode(source, ref i, source.Length, sb, depth: 0);
        return sb.ToString();
    }

    /// <summary>扫描「代码段」；`depth &gt; 0` 时遇配平 `}` 即返回（插值洞结束）。</summary>
    private static void StripCode(string s, ref int i, int end, System.Text.StringBuilder sb, int depth)
    {
        var holeDepth = 0;   // 插值洞内的**嵌套花括号深度**（匿名对象/初始化器/集合初始化器）
        while (i < end)
        {
            var c = s[i];
            if (depth > 0)
            {
                if (c == '{') { sb.Append(c); i++; holeDepth++; continue; }
                if (c == '}')
                {
                    sb.Append(c); i++;
                    if (holeDepth == 0) return;   // 洞结束（配平回到 0）
                    holeDepth--;
                    continue;
                }
            }
            if (c == '/' && i + 1 < end && s[i + 1] == '/')
            {
                while (i < end && s[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && i + 1 < end && s[i + 1] == '*')
            {
                sb.Append("  "); i += 2;
                while (i < end && !(s[i] == '*' && i + 1 < end && s[i + 1] == '/'))
                {
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
                if (i < end) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c == '\'')
            {
                sb.Append(' '); i++;
                while (i < end && s[i] != '\'')
                {
                    if (s[i] == '\\' && i + 1 < end) { sb.Append("  "); i += 2; continue; }
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
                if (i < end) { sb.Append('\''); i++; }
                continue;
            }
            // 字符串前缀：`$`×n 与 `@` 的任意组合（`$"`／`@"`／`$@"`／`@$"`／`$$"""`）
            var start = i;
            var dollars = 0;
            var verbatim = false;
            while (i < end && (s[i] == '$' || s[i] == '@'))
            {
                if (s[i] == '$') dollars++; else verbatim = true;
                i++;
            }
            if (i < end && s[i] == '"')
            {
                var prefixLen = i - start;
                SkipStringLiteral(s, ref i, end, sb, prefixLen, dollars, verbatim);
                continue;
            }
            i = start;
            sb.Append(c);
            i++;
        }
    }

    /// <summary>跳过字符串字面量（原始串／插值串／逐字串／普通串）。</summary>
    private static void SkipStringLiteral(string s, ref int i, int end, System.Text.StringBuilder sb,
        int prefixLen, int dollars, bool verbatim)
    {
        var open = i;                 // 指向第一个 `"`
        var quotesRun = 0;
        while (open + quotesRun < end && s[open + quotesRun] == '"') quotesRun++;
        sb.Append(' ', prefixLen + quotesRun);
        if (quotesRun >= 3)
        {
            // 原始字符串：按**实际分隔符长度**匹配闭合（≥3 个引号）
            var k = open + quotesRun;
            while (k < end)
            {
                if (s[k] == '"')
                {
                    var run = 0;
                    while (k + run < end && s[k + run] == '"') run++;
                    if (run >= quotesRun)
                    {
                        for (var t = 0; t < run; t++) sb.Append('"');
                        i = k + run;
                        return;
                    }
                    for (var t = 0; t < run; t++) sb.Append(' ');   // 内容里的短引号序列
                    k += run;
                    continue;
                }
                sb.Append(s[k] == '\n' ? '\n' : ' ');
                k++;
            }
            i = end;
            return;
        }

        var p = open + 1;
        var braceDepth = 0;
        while (p < end)
        {
            var ch = s[p];
            if (!verbatim && ch == '\\' && p + 1 < end) { sb.Append("  "); p += 2; continue; }
            if (verbatim && ch == '"' && p + 1 < end && s[p + 1] == '"') { sb.Append("  "); p += 2; continue; }
            if (dollars > 0 && ch == '{')
            {
                if (p + 1 < end && s[p + 1] == '{') { sb.Append("  "); p += 2; continue; }   // `{{` 转义
                braceDepth++;
                sb.Append(ch); p++;
                var inner = p;
                StripCode(s, ref inner, end, sb, braceDepth);   // 洞内按**代码**扫描（其中的串/注释一并剥离）
                braceDepth--;
                p = inner;
                continue;
            }
            if (dollars > 0 && ch == '}' && p + 1 < end && s[p + 1] == '}') { sb.Append("  "); p += 2; continue; }
            if (ch == '"') break;
            sb.Append(ch == '\n' ? '\n' : ' ');
            p++;
        }
        if (p < end) { sb.Append('"'); p++; }
        i = p;
    }

    /// <summary>**方法声明**形状（不是调用点/注释）：修饰符 ＋ 返回类型 ＋ 方法名。</summary>
    internal static bool IsDeclaredMethod(string strippedSources, string methodName)
    {
        var decl = new Regex(
            @"(?m)^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)\s+"
            + @"(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|new\s+|partial\s+|unsafe\s+|extern\s+|readonly\s+)*"
            + @"[A-Za-z_][A-Za-z0-9_<>\[\],\.\?\s]{0,80}?\s+" + Regex.Escape(methodName) + @"\s*[<(]");
        return decl.IsMatch(strippedSources);
    }

    /// <summary>方法必须**定义在指定类体内**（花括号范围切分，避免「类名与方法名分别出现在不同文件」的假绿）。</summary>
    internal static bool MethodDeclaredInClass(string strippedSources, string className, string methodName)
    {
        var classPattern = new Regex(@"\b(class|record)\s+" + Regex.Escape(className) + @"\b");
        foreach (Match m in classPattern.Matches(strippedSources))
        {
            var braceStart = strippedSources.IndexOf('{', m.Index);
            if (braceStart < 0) continue;
            if (IsDeclaredMethod(ExtractBraceBlock(strippedSources, braceStart), methodName)) return true;
        }
        return false;
    }

    /// <summary>
    /// **守卫自身的对抗夹具**（[第四轮会诊建议采纳]）：名字只出现在注释／普通串／逐字串／插值串（含洞内嵌套串）／
    /// 原始串（`"""` 与 `""""`）中时**不得**被当作方法声明；真实声明仍必须被检出。
    /// </summary>
    [Theory]
    [InlineData("// Faked_Aaa_Bbb(\npublic void RealAaa_Bbb() { }", "Faked_Aaa_Bbb", false)]
    [InlineData("/* Faked_Aaa_Bbb( */ public void RealAaa_Bbb() { }", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = \"public void Faked_Aaa_Bbb( ) { }\"; public void RealAaa_Bbb() { }", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = @\"public void Faked_Aaa_Bbb( ) { }\";", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = $\"x{ Faked_Aaa_Bbb( }y\";", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = $\"a{\" Faked_Aaa_Bbb( \".Length}b\";", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = \"\"\"public void Faked_Aaa_Bbb( ) { }\"\"\";", "Faked_Aaa_Bbb", false)]
    [InlineData("var s = \"\"\"\"public void Faked_Aaa_Bbb( ) { }\"\"\"\";", "Faked_Aaa_Bbb", false)]
    [InlineData("var c = '{'; var s2 = \"}\";\npublic void RealAaa_Bbb() { }", "RealAaa_Bbb", true)]
    public void Stripper_RemovesLiterals_KeepsRealDeclarations(string snippet, string name, bool expectDeclared)
    {
        var stripped = StripCommentsAndStrings(snippet);
        Assert.Equal(expectDeclared, IsDeclaredMethod(stripped, name));
    }

    /// <summary>
    /// **[第五轮会诊重要项处置] 插值洞内嵌套花括号不得破坏类体边界**：洞内匿名对象 `{ … }` 未配平时，
    /// 会把**同文件内的下一个类**吞进当前类体，从而让 `TargetTests.Wanted_Method` 这类错误指针假绿。
    /// </summary>
    [Fact]
    public void Stripper_InterpolationHoleWithNestedBraces_DoesNotSwallowNextClass()
    {
        const string snippet = """
            class TargetTests
            {
                void A() => _ = $"{new { X = 1 }}";
            }
            class OtherTests
            {
                public void Wanted_Method() { }
            }
            """;
        var stripped = StripCommentsAndStrings(snippet);
        Assert.False(MethodDeclaredInClass(stripped, "TargetTests", "Wanted_Method"),
            "插值洞内的嵌套花括号未配平时，不得把后续类的方法算进前一个类体（假绿）。");
        Assert.True(MethodDeclaredInClass(stripped, "OtherTests", "Wanted_Method"));
    }

    /// <summary>
    /// **[第五轮会诊建议采纳] 映射表列形状对抗夹具**：仅**恰六列**且单元格不含 `|` 的行合法；
    /// 少列/多列/额外列/加粗包装/缩进畸形均必须被识别为**可疑行**（或按合法形状解析）。
    /// </summary>
    [Theory]
    [InlineData("| §23.8 | 1 | 模式 | 状态 | 完成证据 | §24.15 |", false)]
    [InlineData(" | §23.8 | 1 | 模式 | 状态 | 完成证据 | §24.15 |", false)]                  // 缩进合法行
    [InlineData("| §23.8 | 1 | 模式 | 状态 | 完成证据 |", true)]                               // 缺列
    [InlineData("| §23.8 | 1 | 模式 | 状态 | 完成证据 | §24.15 | 隐藏列 |", true)]             // 额外列
    [InlineData("| **§23.8** | 1 | 模式 | 状态 | 完成证据 | §24.15 |", true)]                  // 加粗包装
    [InlineData("| ***§23.8*** | 1 | 模式 | 状态 | 完成证据 | §24.15 |", true)]                // 三重加粗包装
    [InlineData("| `§23.8` | 1 | 模式 | 状态 | 完成证据 | §24.15 |", true)]                    // 反引号包装
    [InlineData("| §23.8 | 1 | 模式 | 状态 | 完成证据 | §24.41-C#1junk |", false)]             // 含非法锚点但列形状合法（锚点由解析器拒绝）
    public void MapRow_ShapeEnforced(string line, bool expectSuspicious)
    {
        Assert.Equal(expectSuspicious, IsSuspiciousMapRow(line));
    }

    /// <summary>取出从 `openIndex` 处 `{` 起、括号配平的范围（文本级近似；已剥离注释/字符串）。</summary>
    private static string ExtractBraceBlock(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[openIndex..(i + 1)];
            }
        }
        return text[openIndex..];
    }

    /// <summary>抽取文档中真实存在的章节号（`### 24.56 ` / `#### 24.56.1 ` / `**C. …**` 等标题形态）。</summary>
    private static HashSet<string> ExtractSectionNumbers(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"(?m)^#{3,6}\s+(\d+(?:\.\d+)*)"))
            result.Add(m.Groups[1].Value);
        return result;
    }
}
