using System.IO;
using System.Text.RegularExpressions;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>茶包版 BGI 更新包文件名解析结果。</summary>
/// <param name="FileName">带扩展名的完整文件名。</param>
/// <param name="Stem">去掉 .7z 后的文件主名（即被校验串）。</param>
/// <param name="BgiVersion">BGI 大版本，如 "0.64.2"（可含 -prerelease）。</param>
/// <param name="LcbVersion">茶包小版本，如 "22.7"（+lcb. 后的多段数字）。</param>
/// <param name="Flavor">版本特色段（第一个 - 段，如 "NexusBGI"），无则为 null。</param>
/// <param name="TailRest">其余 - 段（如 "fix13"，可能没有也可能是其他），多段以 - 连接，无则为 null。</param>
public sealed record BgiArchiveName(
    string FileName,
    string Stem,
    string BgiVersion,
    string LcbVersion,
    string? Flavor,
    string? TailRest)
{
    /// <summary>列表展示用：主名 + 大小之外的紧凑版本摘要。</summary>
    public string VersionSummary => Flavor is null
        ? $"v{BgiVersion} · lcb.{LcbVersion}"
        : TailRest is null
            ? $"v{BgiVersion} · lcb.{LcbVersion} · {Flavor}"
            : $"v{BgiVersion} · lcb.{LcbVersion} · {Flavor}-{TailRest}";
}

/// <summary>
/// 茶包版 BGI 更新包文件名校验纯函数（无 IO、无状态，可穷尽/PBT 测试）。
/// 合法形如 BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z：
///   BetterGI_v&lt;大版本&gt; + lcb.&lt;小版本&gt; [-特色段] [-自由尾段（fix13 可没有也可能是其他，可多段）] .7z
/// 校验失败 = 不允许解压覆盖更新（调用方在扫描过滤与解压前各拦一道）。
/// 只管"名字合法"，不做新旧版本比较（2026-09-14 拍板）。
/// </summary>
public static partial class BgiUpdateDecisions
{
    /// <summary>段字符集：段首必须是字母或数字（阻断 "-"、"-" 连段歧义与 ".xx" 伪装），段内允许字母数字._-。</summary>
    private const string SegmentChars = @"[0-9A-Za-z][0-9A-Za-z._-]*";

    [GeneratedRegex(
        @"^BetterGI_v(?<bgi>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\+lcb\.(?<lcb>\d+(?:\.\d+)+)(?<tail>(?:-" + SegmentChars + @")*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>文件名是否为合法的茶包版更新包（仅 .7z，扩展名不区分大小写，主名严格区分大小写）。</summary>
    public static bool IsValidArchiveName(string? fileName) => TryParse(fileName, out _);

    /// <summary>解析更新包文件名；不合法返回 false（info 为 null）。</summary>
    public static bool TryParse(string? fileName, out BgiArchiveName? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var name = fileName.Trim();
        const string ext = ".7z";
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Length == ext.Length) return false;
        var stem = name[..^ext.Length];

        var match = NamePattern().Match(stem);
        if (!match.Success) return false;

        var tail = match.Groups["tail"].Value;
        // tail 形如 "-A-B"：剥掉段首的 "-" 拆段；空 tail → 无特色段
        var segments = tail.Length == 0
            ? []
            : tail.Split('-', StringSplitOptions.RemoveEmptyEntries);

        info = new BgiArchiveName(
            FileName: name,
            Stem: stem,
            BgiVersion: match.Groups["bgi"].Value,
            LcbVersion: match.Groups["lcb"].Value,
            Flavor: segments.Length > 0 ? segments[0] : null,
            TailRest: segments.Length > 1 ? string.Join('-', segments[1..]) : null);
        return true;
    }

    /// <summary>
    /// 判断压缩包内条目相对路径是否落在排除目录内（纯函数，可穷尽测试）。
    /// 条目路径与排除目录都不区分 / 和 \；排除目录为目录前缀匹配（含目录边界：
    /// "User" 排除 "User\Repo\x" 但不排除 "UserData\x"）。
    /// </summary>
    public static bool IsExcludedPath(string entryRelativePath, IEnumerable<string>? excludeRelativeDirs)
    {
        if (excludeRelativeDirs is null) return false;
        var entry = NormalizeRelForCompare(entryRelativePath);
        if (entry.Length == 0) return false;
        foreach (var raw in excludeRelativeDirs)
        {
            var dir = NormalizeRelForCompare(raw);
            if (dir.Length == 0) continue;
            if (entry == dir || entry.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>把本机版本串（BGI 的 InformationalVersion，如 "0.64.2+lcb.22.7-NexusBGI-fix13" 或 dev 版 "0.64.2-alpha.2"）
    /// 规整成可比较的 BgiArchiveName：直接尝试按包名解析；缺 lcb 段（dev/官方版）补 "+lcb.0" 后解析（lcb=0 比任何真实 lcb 旧）。
    /// 完全解析不了返回 null（调用方视为"无法判断新旧"，不点亮更新提示）。</summary>
    public static BgiArchiveName? BuildLocalComparableName(string? localVersion)
    {
        var v = NormalizeLocalVersion(localVersion);
        if (v == null) return null;
        if (TryParse($"BetterGI_v{v}.7z", out var direct) && direct != null) return direct;
        // dev/官方版缺 lcb 段：补 "+lcb.0.0"（lcb 正则要求至少两段数字），lcb=0 比任何真实 lcb 旧
        if (TryParse($"BetterGI_v{v}+lcb.0.0.7z", out var withZeroLcb) && withZeroLcb != null) return withZeroLcb;
        return null;
    }

    /// <summary>本地版本串规范化：去空白、剥可能存在的 BetterGI_v 前缀与 .7z 后缀；空返回 null。</summary>
    private static string? NormalizeLocalVersion(string? localVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion)) return null;
        var v = localVersion.Trim();
        if (v.StartsWith("BetterGI_v", StringComparison.Ordinal)) v = v["BetterGI_v".Length..];
        if (v.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) v = v[..^3];
        return v.Length == 0 ? null : v;
    }

    /// <summary>
    /// 候选更新包是否比本机版本新（纯函数，"版本号变黄/提醒条"的判定依据）。
    /// 本机版本解析不了时保守返回 false（宁可漏提示也不误报）。
    /// </summary>
    public static bool IsNewerThanLocal(BgiArchiveName candidate, string? localVersion)
    {
        var local = BuildLocalComparableName(localVersion);
        return local != null && CompareNewer(candidate, local) > 0;
    }

    /// <summary>比较用归一化：统一成本进程分隔符、去首尾分隔符；非法/空返回空串。</summary>
    private static string NormalizeRelForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var segments = path.Trim().Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Contains("..") || segments.Any(s => s.Contains(':'))) return string.Empty;
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// 包版本新旧比较（纯函数，供列表排序）：大版本 → lcb 小版本 → 尾段，逐级数字比较（"22.10" &gt; "22.9"、"fix14" &gt; "fix13"）。
    /// 细则：大版本可带 -prerelease（无 prerelease 视为比带的新，semver 惯例）；尾段=前缀+可选数字
    /// （fix13/test2/beta），同前缀按数字比大小，不同前缀（fix vs test）按字母序仅保证确定性，
    /// 无尾段视为比有尾段旧（fix 是在基础版之上叠加）。返回正数 = a 更新。
    /// 只用于排序展示（新的在前），不参与"名字合法"之外的任何拦截判断。
    /// </summary>
    public static int CompareNewer(BgiArchiveName? a, BgiArchiveName? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int cmp = CompareBgiVersion(a.BgiVersion, b.BgiVersion, out var aPre, out var bPre);
        if (cmp != 0) return cmp;
        cmp = (aPre.Length == 0, bPre.Length == 0) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => string.Compare(aPre, bPre, StringComparison.OrdinalIgnoreCase),
        };
        if (cmp != 0) return cmp;
        cmp = CompareDottedNumber(a.LcbVersion, b.LcbVersion);
        if (cmp != 0) return cmp;
        cmp = CompareTails(a, b);
        if (cmp != 0) return cmp;
        return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>大版本比较：剥离 -prerelease 后逐段数字比较。</summary>
    private static int CompareBgiVersion(string x, string y, out string xPre, out string yPre)
    {
        var xDash = x.IndexOf('-');
        var yDash = y.IndexOf('-');
        xPre = xDash >= 0 ? x[(xDash + 1)..] : "";
        yPre = yDash >= 0 ? y[(yDash + 1)..] : "";
        return CompareDottedNumber(xDash >= 0 ? x[..xDash] : x, yDash >= 0 ? y[..yDash] : y);
    }

    /// <summary>点分数字串比较（"22.10" vs "22.9" 按数字不按字符串；缺段按 0）。</summary>
    private static int CompareDottedNumber(string x, string y)
    {
        var xs = x.Split('.');
        var ys = y.Split('.');
        for (var i = 0; i < Math.Max(xs.Length, ys.Length); i++)
        {
            var xv = i < xs.Length && long.TryParse(xs[i], out var xa) ? xa : 0L;
            var yv = i < ys.Length && long.TryParse(ys[i], out var yb) ? yb : 0L;
            if (xv != yv) return xv.CompareTo(yv);
        }
        return 0;
    }

    private static int CompareTails(BgiArchiveName a, BgiArchiveName b)
    {
        var sa = TailSegments(a);
        var sb = TailSegments(b);
        for (var i = 0; i < Math.Max(sa.Count, sb.Count); i++)
        {
            var (pa, na, ha) = i < sa.Count ? sa[i] : (Prefix: null, Num: 0L, Has: false);
            var (pb, nb, hb) = i < sb.Count ? sb[i] : (Prefix: null, Num: 0L, Has: false);
            if (!ha && !hb) return 0;
            if (!ha) return -1; // 无尾段 < 有尾段
            if (!hb) return 1;
            var pc = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (pc != 0) return pc;
            if (na != nb) return na.CompareTo(nb); // fix14 > fix13：数字比较，非字符串
        }
        return 0;
    }

    /// <summary>把特色段+尾段拆成 (前缀小写, 尾部数字, 存在) 列表：NexusBGI-fix13 → [("nexusbgi",0),("fix",13)]。</summary>
    private static List<(string Prefix, long Num, bool Has)> TailSegments(BgiArchiveName info)
    {
        var segs = new List<(string, long, bool)>();
        void AddSeg(string seg)
        {
            var i = seg.Length;
            while (i > 0 && char.IsDigit(seg[i - 1])) i--;
            var n = i < seg.Length && long.TryParse(seg[i..], out var v) ? v : 0L;
            segs.Add((seg[..i].ToLowerInvariant(), n, true));
        }
        if (info.Flavor != null) AddSeg(info.Flavor);
        if (info.TailRest != null)
        {
            foreach (var t in info.TailRest.Split('-')) AddSeg(t);
        }
        return segs;
    }
}
