using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// "更新BGI"纯函数测试：包文件名校验（BgiUpdateDecisions）、排除目录匹配、版本串规范化（BgiVersionResolver）。
/// 校验语义（2026-09-14 拍板）：只管"名字合法"，不做新旧版本比较；仅 .7z；fix 段可没有也可能是其他。
/// </summary>
public class BgiUpdateDecisionsTests
{
    // ===== 文件名校验：合法形态 =====

    [Theory]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z")]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI.7z")]          // fix 段没有
    [InlineData("BetterGI_v0.64.2+lcb.22.7.7z")]                   // 连特色段都没有
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-hotfix1.7z")]  // fix 段是其他
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13-beta.7z")] // 多个尾段
    [InlineData("BetterGI_v1.0.0-alpha.2+lcb.1.0.7z")]             // 大版本带 prerelease；小版本多段
    [InlineData("BetterGI_v0.64.2+lcb.22.7.7Z")]                   // 扩展名不区分大小写
    public void TryParse_ValidNames_Parses(string fileName)
    {
        Assert.True(BgiUpdateDecisions.TryParse(fileName, out var info));
        Assert.NotNull(info);
    }

    [Fact]
    public void TryParse_ExampleFromRequirement_SplitsParts()
    {
        Assert.True(BgiUpdateDecisions.TryParse("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", out var info));
        Assert.Equal("0.64.2", info!.BgiVersion);
        Assert.Equal("22.7", info.LcbVersion);
        Assert.Equal("NexusBGI", info.Flavor);
        Assert.Equal("fix13", info.TailRest);
        Assert.Equal("v0.64.2 · lcb.22.7 · NexusBGI-fix13", info.VersionSummary);
    }

    [Fact]
    public void TryParse_NoTail_FlavorAndRestAreNull()
    {
        Assert.True(BgiUpdateDecisions.TryParse("BetterGI_v0.64.2+lcb.22.7.7z", out var info));
        Assert.Null(info!.Flavor);
        Assert.Null(info.TailRest);
    }

    // ===== 文件名校验：非法形态（不允许解压覆盖） =====

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.zip")]   // 只支持 .7z
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13")]       // 无扩展名
    [InlineData(".7z")]                                            // 只剩扩展名
    [InlineData("BetterGI_0.64.2+lcb.22.7.7z")]                    // 缺 v
    [InlineData("betterGI_v0.64.2+lcb.22.7.7z")]                   // 前缀大小写不符（前缀严格区分，仅扩展名不区分）
    // --- 仅茶包版可更新：名称必须带 +lcb. 段，防止误升级到其他版本 BGI（2026-09-14 明确）---
    [InlineData("BetterGI_v0.64.2.7z")]                            // 官方版：无 lcb
    [InlineData("BetterGI_v0.64.2-alpha.2.7z")]                    // 官方 prerelease：无 lcb
    [InlineData("BetterGI_v0.64.2+Nexus.7z")]                      // 带 + 段但不是 lcb
    [InlineData("BetterGI_v0.64.2-lcb.22.7.7z")]                   // lcb 前连接符错（- 而非 +）
    [InlineData("OtherGI_v0.64.2+lcb.22.7.7z")]                    // 前缀不对
    [InlineData("BetterGI_v0.64+lcb.22.7.7z")]                     // 大版本缺一段
    [InlineData("BetterGI_v0.64.2+lc.22.7.7z")]                    // lcb 拼错
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z.exe")] // 伪装扩展名
    [InlineData("BetterGI_v0.64.2+lcb..7z")]                       // 小版本空
    [InlineData("BetterGI_v0.64.2+lcb.22.7--fix13.7z")]            // 空段
    [InlineData("BetterGI_v0.64.2+lcb.22.7-.7z")]                  // 悬空 "-"
    public void TryParse_InvalidNames_Rejected(string? fileName)
    {
        Assert.False(BgiUpdateDecisions.TryParse(fileName, out var info));
        Assert.Null(info);
        Assert.Equal(BgiUpdateDecisions.TryParse(fileName, out _), BgiUpdateDecisions.IsValidArchiveName(fileName));
    }

    /// <summary>PBT 风格：随机组合合法大版本/小版本/尾段，构造出的名字必须可解析且各段正确往返（固定种子可复现）。</summary>
    [Fact]
    public void TryParse_RandomValidCompositions_RoundTrips()
    {
        var rng = new Random(20260914);
        for (int i = 0; i < 200; i++)
        {
            var bgi = $"{rng.Next(0, 9)}.{rng.Next(0, 99)}.{rng.Next(0, 99)}";
            var lcb = $"{rng.Next(0, 99)}.{rng.Next(0, 99)}";
            var tails = new[] { null, "NexusBGI", "NexusBGI-fix13", "NexusBGI-hotfix9", "Something-Else_1.2" }[rng.Next(5)];
            var name = $"BetterGI_v{bgi}+lcb.{lcb}{(tails == null ? "" : $"-{tails}")}.7z";

            Assert.True(BgiUpdateDecisions.TryParse(name, out var info));
            Assert.Equal(bgi, info!.BgiVersion);
            Assert.Equal(lcb, info.LcbVersion);
        }
    }

    // ===== 排除目录匹配 =====

    [Fact]
    public void IsExcludedPath_DirectoryBoundary_Honored()
    {
        var excludes = new[] { "User" };
        Assert.True(BgiUpdateDecisions.IsExcludedPath(@"User\Repo\a.json", excludes));      // 目录内
        Assert.True(BgiUpdateDecisions.IsExcludedPath("User", excludes));                   // 目录自身
        Assert.True(BgiUpdateDecisions.IsExcludedPath("User/Repo/b.js", excludes));         // 正斜杠等价
        Assert.False(BgiUpdateDecisions.IsExcludedPath(@"UserData\x.log", excludes));       // 仅前缀相同不算
        Assert.False(BgiUpdateDecisions.IsExcludedPath(@"UserConfig\a.cfg", excludes));
        Assert.False(BgiUpdateDecisions.IsExcludedPath("BetterGI.exe", excludes));          // 目录外文件
    }

    [Fact]
    public void IsExcludedPath_NestedExclude_Matches()
    {
        var excludes = new[] { @"Tool\MultiplayerHoeingAssistant" };
        Assert.True(BgiUpdateDecisions.IsExcludedPath(@"Tool\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe", excludes));
        Assert.True(BgiUpdateDecisions.IsExcludedPath("Tool/MultiplayerHoeingAssistant/Settings.json", excludes));
        Assert.False(BgiUpdateDecisions.IsExcludedPath(@"Tool\OtherTool\a.dll", excludes));
    }

    [Fact]
    public void IsExcludedPath_CaseInsensitive_And_MultipleDirs()
    {
        var excludes = new[] { "User", "Tool" };
        Assert.True(BgiUpdateDecisions.IsExcludedPath(@"USER\Repo\a.json", excludes));
        Assert.True(BgiUpdateDecisions.IsExcludedPath(@"tool\x.dll", excludes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void IsExcludedPath_EmptyEntry_NeverExcluded(string? entry)
    {
        Assert.False(BgiUpdateDecisions.IsExcludedPath(entry!, ["User"]));
    }

    [Fact]
    public void IsExcludedPath_InvalidExcludeDir_IgnoredSafely()
    {
        // 排除目录含 ".." 或盘符：归一化判空后忽略，绝不会把整个目标目录都排除掉
        Assert.False(BgiUpdateDecisions.IsExcludedPath(@"Anything\a.json", ["..", "C:", ""]));
    }

    // ===== 版本新旧排序（列表从新到旧）=====

    private static BgiArchiveName Parse(string name)
    {
        Assert.True(BgiUpdateDecisions.TryParse(name, out var info));
        return info!;
    }

    [Theory]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix14.7z", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", 1)]  // fix14 比 fix13 新（数字比较）
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix9.7z", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", -1)] // fix13 比 fix9 新（字符串比较会错排）
    [InlineData("BetterGI_v0.64.2+lcb.22.10.7z", "BetterGI_v0.64.2+lcb.22.9.7z", 1)]                             // lcb 数字比较：22.10 > 22.9
    [InlineData("BetterGI_v0.65.0+lcb.22.6.7z", "BetterGI_v0.64.9+lcb.22.99.7z", 1)]                             // 大版本优先于 lcb/尾段
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", "BetterGI_v0.64.2+lcb.22.7-NexusBGI.7z", 1)]      // 有尾段比无尾段新
    [InlineData("BetterGI_v0.64.2+lcb.22.7.7z", "BetterGI_v0.64.2-alpha.1+lcb.22.7.7z", 1)]                      // 正式版比 prerelease 新
    public void CompareNewer_VersionPrecedence(string newer, string older, int expectedSign)
    {
        var cmp = BgiUpdateDecisions.CompareNewer(Parse(newer), Parse(older));
        Assert.True(Math.Sign(cmp) == expectedSign, $"cmp={cmp}");
        Assert.Equal(-cmp, BgiUpdateDecisions.CompareNewer(Parse(older), Parse(newer)));
    }

    [Fact]
    public void CompareNewer_SameVersion_FallsBackToFileName()
    {
        Assert.Equal(0, BgiUpdateDecisions.CompareNewer(Parse("BetterGI_v0.64.2+lcb.22.7.7z"), Parse("BetterGI_v0.64.2+lcb.22.7.7z")));
        Assert.True(BgiUpdateDecisions.CompareNewer(Parse("BetterGI_v0.64.2+lcb.22.7.7z"), Parse("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z")) != 0);
    }

    // ===== 候选包 vs 本机版本（版本号变黄判定）=====

    [Theory]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix14.7z", "0.64.2+lcb.22.7-NexusBGI-fix13", true)]  // fix14 比本机 fix13 新
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", "0.64.2+lcb.22.7-NexusBGI-fix13", false)] // 同版本不提示
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix9.7z", "0.64.2+lcb.22.7-NexusBGI-fix13", false)]  // 更旧不提示
    [InlineData("BetterGI_v0.65.0+lcb.1.0.7z", "0.64.2+lcb.22.7", true)]                                 // 大版本优先
    [InlineData("BetterGI_v0.64.2+lcb.23.0.7z", "0.64.2+lcb.22.7", true)]                                // lcb 更大
    [InlineData("BetterGI_v0.64.2+lcb.22.7.7z", "0.64.2-alpha.2", true)]                                 // dev 版（补 lcb=0）：任何真实 lcb 都更新
    public void IsNewerThanLocal_CandidateVsLocal(string candidateName, string? localVersion, bool expected)
    {
        Assert.True(BgiUpdateDecisions.TryParse(candidateName, out var candidate));
        Assert.Equal(expected, BgiUpdateDecisions.IsNewerThanLocal(candidate!, localVersion));
    }

    [Fact]
    public void IsNewerThanLocal_UnparseableLocal_ReturnsFalse()
    {
        Assert.True(BgiUpdateDecisions.TryParse("BetterGI_v0.65.0+lcb.1.0.7z", out var candidate));
        Assert.False(BgiUpdateDecisions.IsNewerThanLocal(candidate!, "垃圾版本串"));
        Assert.False(BgiUpdateDecisions.IsNewerThanLocal(candidate!, null));
        Assert.False(BgiUpdateDecisions.IsNewerThanLocal(candidate!, ""));
    }

    [Fact]
    public void BuildLocalComparableName_TeabagAndDevVersions()
    {
        var teabag = BgiUpdateDecisions.BuildLocalComparableName("0.64.2+lcb.22.7-NexusBGI-fix13");
        Assert.Equal("22.7", teabag!.LcbVersion);
        Assert.Equal("fix13", teabag.TailRest);
        // BetterGI_v 前缀已剥也可解析
        var prefixed = BgiUpdateDecisions.BuildLocalComparableName("BetterGI_v0.64.2+lcb.22.7");
        Assert.Equal("22.7", prefixed!.LcbVersion);
        // dev/官方版缺 lcb：补 lcb=0.0
        var dev = BgiUpdateDecisions.BuildLocalComparableName("0.64.2-alpha.2");
        Assert.Equal("0.0", dev!.LcbVersion);
        Assert.Equal(BgiUpdateDecisions.BuildLocalComparableName(null), null);
    }

    // ===== 版本串规范化 =====

    [Theory]
    [InlineData("0.64.2+lcb.22.7-NexusBGI-fix13", "0.64.2+lcb.22.7-NexusBGI-fix13")]
    [InlineData("  0.64.2  ", "0.64.2")]
    [InlineData("0.64.2 (abc1234)", "0.64.2")]     // ProductVersion 带编译注释段，截断
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Normalize_VariousInputs(string? input, string? expected)
    {
        Assert.Equal(expected, BgiVersionResolver.Normalize(input));
    }
}
