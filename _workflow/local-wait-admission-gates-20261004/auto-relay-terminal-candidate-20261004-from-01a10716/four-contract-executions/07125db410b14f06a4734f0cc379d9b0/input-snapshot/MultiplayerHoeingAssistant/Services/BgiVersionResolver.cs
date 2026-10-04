using System.Diagnostics;
using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 本机 BGI 版本号解析（成员卡版本徽章 + "更新BGI"弹窗共用）。
/// 两个来源按优先级取第一个可用值：
/// ① 执行端快照 bgiVersion（BgiExternalClient，BGI 运行中且 ext 通道可用时，实时）；
/// ② BgiPath 指向 exe 的 FileVersionInfo.ProductVersion（与 BGI 内 Global.Version 同源=InformationalVersion，
///    BGI 未运行/未连接也可读，静态兜底）。
/// 两处都不可用时返回 null，UI 显示"-"。
/// </summary>
public static class BgiVersionResolver
{
    /// <summary>按优先级解析本机 BGI 版本号。</summary>
    public static string? Resolve(string? externalClientBgiVersion, string? bgiExePath)
    {
        var fromSnapshot = Normalize(externalClientBgiVersion);
        if (fromSnapshot != null) return fromSnapshot;
        return FromExecutable(bgiExePath);
    }

    /// <summary>读 BGI exe 的 ProductVersion。文件不存在/读取失败（进程占用、权限）返回 null，不抛出。</summary>
    public static string? FromExecutable(string? bgiExePath)
    {
        if (string.IsNullOrWhiteSpace(bgiExePath)) return null;
        try
        {
            if (!File.Exists(bgiExePath)) return null;
            return Normalize(FileVersionInfo.GetVersionInfo(bgiExePath).ProductVersion);
        }
        catch
        {
            // exe 被占用/权限不足等场景：版本未知即可，绝不影响 10s 状态轮询主链路
            return null;
        }
    }

    /// <summary>规范化版本串：去首尾空白；截断 " (" 之后（防 ProductVersion 携带编译注释段）；空串返回 null。</summary>
    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var v = version.Trim();
        var commentStart = v.IndexOf(" (", StringComparison.Ordinal);
        if (commentStart > 0) v = v[..commentStart].TrimEnd();
        return v.Length == 0 ? null : v;
    }
}
