using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 协调服务器地址的默认值与归一规则。地址照常在设置页和配置组里填写、修改。
/// 只有空值或旧版示例文字（不是 http/https 地址）才按官方地址处理，安装后直接可用。
/// </summary>
public static class CoordinatorServerAddress
{
    /// <summary>官方联机服务器地址（联机 8080；线路由 https://www.autobgi.cn 提供）。也是默认值。</summary>
    public const string RecommendedAddress = "http://www.autobgi.cn:8080";

    /// <summary>有效的 http/https 地址原样使用；空值或旧版示例文字使用官方地址。不写回任何配置文件。</summary>
    public static string Effective(string? configured)
        => IsConfigured(configured) ? configured!.Trim() : RecommendedAddress;

    /// <summary>地址是否像一个可用的 http/https 地址（不是空、不是旧版示例文字）。</summary>
    public static bool IsConfigured(string? address)
        => Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
}
