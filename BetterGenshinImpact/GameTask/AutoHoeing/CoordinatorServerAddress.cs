using System;
using System.IO;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 协调服务器地址的唯一来源：User\config.json 的 autoHoeingConfig.coordinatorServerUrl。
/// 默认就是官方地址，安装后直接能用；只有自建服务器才需要用户关闭 BGI 后手动编辑该文件。
/// 界面只读显示，配置组和远程下发都不能改，运行时也不读它们的残留值。
/// 联机连接、锄地前自动更新线路、独立任务“更新联机锄地线路”全部经过这里取地址。
/// </summary>
public static class CoordinatorServerAddress
{
    /// <summary>官方联机服务器地址（联机 8080；线路由同主机 HTTPS 提供）。也是默认值。</summary>
    public const string RecommendedAddress = "http://www.autobgi.cn:8080";

    /// <summary>旧版本写进 config.json 的示例文字；它不是地址，读取时按官方地址处理。</summary>
    internal const string LegacyPlaceholder = "举例：https://bgi-sync.example.com(需部署自建服务)";

    /// <summary>给用户看的修改说明：文件位置 + 字段名 + 官方值。</summary>
    public static string EditHint => "如需修改，请关闭 BGI 后用记事本打开 "
        + Path.Combine("User", "config.json") + "，修改 autoHoeingConfig 里的 coordinatorServerUrl（官方地址："
        + RecommendedAddress + "），保存后重新打开 BGI。";

    /// <summary>
    /// 当前生效的地址：读 config.json 加载到内存的全局配置。
    /// 空值或旧版示例文字（不是 http/https 地址）一律按官方地址处理 —— 只在读取时替换，不写回 config.json。
    /// </summary>
    public static string Global => Effective(TaskContext.Instance().Config.AutoHoeingConfig.CoordinatorServerUrl);

    /// <summary>把配置值归一成实际使用的地址：有效的 http/https 地址原样使用，否则用官方地址。</summary>
    public static string Effective(string? configured)
        => IsConfigured(configured) ? configured!.Trim() : RecommendedAddress;

    /// <summary>地址是否像一个可用的 http/https 地址（不是空、不是旧版示例文字）。</summary>
    public static bool IsConfigured(string? address)
        => Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
}
