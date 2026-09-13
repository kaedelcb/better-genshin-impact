using System.Text.RegularExpressions;

namespace BgiCoordinatorServer.Services;

/// <summary>服务端解析出的更新包版本信息（/bgi-update/latest.json 目录扫描用）。</summary>
public sealed record BgiUpdateArchiveName(
    string FileName,
    string BgiVersion,
    string LcbVersion,
    string? Flavor,
    string? TailRest);

/// <summary>
/// BGI 更新包文件名解析与新旧比较（服务端镜像，规则与助手侧
/// MultiplayerHoeingAssistant.Services.BgiUpdateDecisions 保持一致）：
///   BetterGI_v&lt;大版本&gt; + lcb.&lt;小版本&gt; [-特色段] [-自由尾段] .7z
/// 只取"解析 + 比最新"子集，供 /bgi-update/latest.json 从挂载目录自动选最新包。
/// </summary>
public static partial class BgiUpdateCatalogDecisions
{
    private const string SegmentChars = @"[0-9A-Za-z][0-9A-Za-z._-]*";

    [GeneratedRegex(
        @"^BetterGI_v(?<bgi>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\+lcb\.(?<lcb>\d+(?:\.\d+)+)(?<tail>(?:-" + SegmentChars + @")*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool TryParse(string fileName, out BgiUpdateArchiveName? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = fileName.Trim();
        const string ext = ".7z";
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || name.Length == ext.Length) return false;
        var stem = name[..^ext.Length];

        var match = NamePattern().Match(stem);
        if (!match.Success) return false;

        var tail = match.Groups["tail"].Value;
        var segments = tail.Length == 0 ? [] : tail.Split('-', StringSplitOptions.RemoveEmptyEntries);
        info = new BgiUpdateArchiveName(
            FileName: name,
            BgiVersion: match.Groups["bgi"].Value,
            LcbVersion: match.Groups["lcb"].Value,
            Flavor: segments.Length > 0 ? segments[0] : null,
            TailRest: segments.Length > 1 ? string.Join('-', segments[1..]) : null);
        return true;
    }

    /// <summary>从文件名集合里选版本最新的合法包；没有合法包返回 null。</summary>
    public static BgiUpdateArchiveName? PickNewest(IEnumerable<string> fileNames)
    {
        BgiUpdateArchiveName? newest = null;
        foreach (var fileName in fileNames)
        {
            if (!TryParse(fileName, out var info) || info is null) continue;
            if (newest == null || CompareNewer(info, newest) > 0) newest = info;
        }
        return newest;
    }

    /// <summary>新旧比较：大版本（无 prerelease 视为更新）→ lcb → 尾段（同前缀数字比较、无尾段 &lt; 有尾段、异前缀字母序）→ 文件名。正数 = a 更新。</summary>
    public static int CompareNewer(BgiUpdateArchiveName a, BgiUpdateArchiveName b)
    {
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
        cmp = CompareTails(a.Flavor, a.TailRest, b.Flavor, b.TailRest);
        if (cmp != 0) return cmp;
        return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareBgiVersion(string x, string y, out string xPre, out string yPre)
    {
        var xDash = x.IndexOf('-');
        var yDash = y.IndexOf('-');
        xPre = xDash >= 0 ? x[(xDash + 1)..] : "";
        yPre = yDash >= 0 ? y[(yDash + 1)..] : "";
        return CompareDottedNumber(xDash >= 0 ? x[..xDash] : x, yDash >= 0 ? y[..yDash] : y);
    }

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

    private static int CompareTails(string? aFlavor, string? aRest, string? bFlavor, string? bRest)
    {
        var sa = TailSegments(aFlavor, aRest);
        var sb = TailSegments(bFlavor, bRest);
        for (var i = 0; i < Math.Max(sa.Count, sb.Count); i++)
        {
            var (pa, na, ha) = i < sa.Count ? sa[i] : (Prefix: null, Num: 0L, Has: false);
            var (pb, nb, hb) = i < sb.Count ? sb[i] : (Prefix: null, Num: 0L, Has: false);
            if (!ha && !hb) return 0;
            if (!ha) return -1;
            if (!hb) return 1;
            var pc = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (pc != 0) return pc;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }

    private static List<(string Prefix, long Num, bool Has)> TailSegments(string? flavor, string? tailRest)
    {
        var segs = new List<(string, long, bool)>();
        void AddSeg(string seg)
        {
            var i = seg.Length;
            while (i > 0 && char.IsDigit(seg[i - 1])) i--;
            var n = i < seg.Length && long.TryParse(seg[i..], out var v) ? v : 0L;
            segs.Add((seg[..i].ToLowerInvariant(), n, true));
        }
        if (flavor != null) AddSeg(flavor);
        if (tailRest != null)
        {
            foreach (var t in tailRest.Split('-')) AddSeg(t);
        }
        return segs;
    }
}
