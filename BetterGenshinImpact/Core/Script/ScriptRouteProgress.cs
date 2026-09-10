namespace BetterGenshinImpact.Core.Script;

/// <summary>
/// 配置组内脚本任务「当前执行线路」跟踪器（只读内存快照，供 IPC <c>task.status</c> 的
/// <c>currentScriptRouteName</c> 字段与 ext 事件观察器读取）。
///
/// 背景：配置组里的 JS 脚本任务，任务名恒为该脚本自身（如「锄地一条龙」「AAA-Artifacts-Bulk-Supply」），
/// 脚本内部逐条执行地图追踪路线，BGI 侧原先只有 AutoHoeingProgress（联机锄地原生任务专用）
/// 才知道路线名 —— 于是联机助手成员卡片的「配置组 · 任务名」与嘟嘟可成员状态墙的「路线」行
/// 在 JS 任务期间恒为空。本类补上这一层：JS 通过 <c>pathingScript.runFile</c> / <c>runFileFromUser</c>
/// 执行路线时记下线路文件名，由 ScriptService 在项目边界清空。
///
/// 生命周期（与配置组项目边界对齐，防残留）：
/// - <see cref="BetterGenshinImpact.Service.ScriptService"/> 的 ExecuteProject 开始/结束（finally）→ <see cref="Clear"/>；
/// - AutoPathingScript.RunFile/RunFileFromUser 每条路线开始时覆盖；
/// - 单条路线结束**不**清空：保留"最近执行的线路"，否则 10s 一拍的助手状态轮询
///   很容易恰好落在两条路线的间隙里，显示恒为空。
///
/// 与 AutoHoeingProgress 的分工：后者是联机锄地原生任务（含第X/Y条线路与预计用时），
/// 本类是配置组内脚本任务的具体线路名，两者互不覆盖，助手端各占一个显示位。
/// </summary>
public static class ScriptRouteProgress
{
    private static readonly object Sync = new();

    private static string? _currentRouteName;

    /// <summary>当前（最近）正在执行的线路文件名；无脚本任务在跑或项目已结束为 null。</summary>
    public static string? CurrentRouteName
    {
        get
        {
            lock (Sync)
            {
                return _currentRouteName;
            }
        }
    }

    /// <summary>记录当前执行的线路名（空值等价于 <see cref="Clear"/>）。</summary>
    public static void SetCurrentRoute(string? routeName)
    {
        var name = string.IsNullOrWhiteSpace(routeName) ? null : routeName.Trim();
        lock (Sync)
        {
            _currentRouteName = name;
        }
    }

    /// <summary>清空当前线路名（配置组项目开始/结束、任务取消收尾时调用）。</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            _currentRouteName = null;
        }
    }
}
