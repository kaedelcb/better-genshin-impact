using System;
using System.IO;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 协调服务器地址的唯一来源：User\config.json 的 autoHoeingConfig.coordinatorServerUrl。
/// 只允许用户关闭 BGI 后手动编辑该文件修改；界面只读显示，配置组和远程下发都不能改，运行时也不读它们的残留值。
/// 联机连接、锄地前自动更新线路、独立任务“更新联机锄地线路”全部经过这里取地址。
/// </summary>
public static class CoordinatorServerAddress
{
    /// <summary>官方联机服务器的推荐写法（联机 8080；线路由同主机 HTTPS 提供）。</summary>
    public const string RecommendedAddress = "http://www.autobgi.cn:8080";

    /// <summary>给用户看的修改说明：文件位置 + 字段名 + 推荐值。</summary>
    public static string EditHint => "请关闭 BGI 后用记事本打开 "
        + Path.Combine("User", "config.json") + "，把 autoHoeingConfig 里的 coordinatorServerUrl 改为 "
        + RecommendedAddress + "，保存后重新打开 BGI。";

    /// <summary>当前生效的地址，只读 config.json 加载到内存的全局配置。</summary>
    public static string Global => TaskContext.Instance().Config.AutoHoeingConfig.CoordinatorServerUrl ?? "";

    /// <summary>地址是否像一个可用的 http/https 地址（不是空、不是默认示例文字）。</summary>
    public static bool IsConfigured(string? address)
        => Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    /// <summary>联机连接前检查；未配置时给出可操作的中文说明，而不是连接失败后才报错。</summary>
    public static string? ConnectProblem(string? address)
        => IsConfigured(address) ? null : "协调服务器地址未设置。" + EditHint;
}
