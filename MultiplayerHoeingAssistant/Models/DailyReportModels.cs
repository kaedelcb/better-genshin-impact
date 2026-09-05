namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 某自然日的配置组运行日报（由 DailyReportService 解析 BGI 按天日志生成）。
/// 行头只有时分秒，日期由日志文件名确定；跨天运行的组不做按日切分（计入日志文件所属日）。
/// </summary>
public sealed class DailyReport
{
    /// <summary>报告所属日期。</summary>
    public DateOnly Date { get; set; }

    /// <summary>当天运行过的配置组（同名组多次执行已合并），按当天首次开始时间排序。</summary>
    public List<DailyReportGroup> Groups { get; } = new();

    /// <summary>全部组时长合计。</summary>
    public TimeSpan TotalDuration { get; set; }
}

/// <summary>日报里的单个配置组（同名组一天跑多次时合并为一行）。</summary>
public sealed class DailyReportGroup
{
    /// <summary>配置组名。</summary>
    public string Name { get; set; } = "";

    /// <summary>当天累计运行时长（多次执行求和）。</summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>当天执行次数（&gt;1 时界面标注"已合并"）。</summary>
    public int RunCount { get; set; }

    /// <summary>当天首次开始时间（time-of-day，排序用）。</summary>
    public TimeSpan FirstStart { get; set; }

    /// <summary>存在未正常闭合的执行（进行中或 BGI 崩溃/强退），其时长计至日志最后一行。</summary>
    public bool HasUnclosedRun { get; set; }

    /// <summary>联机轮次明细原文（组时间窗内的「本轮锄地结束统计」行，按出现顺序）。</summary>
    public List<string> Rounds { get; } = new();
}

/// <summary>
/// 全天任务总览：当天 BGI 跑过的全部运行单元（一条龙配置单 / 配置组 / 独立任务）的树。
/// 顶层单元时间段互斥（谁启动的谁算时间，被包含的作为子层展示），
/// 因此 TopUnits 时长之和 = 当天 BGI 总运行时长，不会因嵌套重复计时。
/// </summary>
public sealed class DayOverview
{
    /// <summary>顶层运行单元（无父单元），按开始时间排序。</summary>
    public List<OverviewUnit> TopUnits { get; } = new();

    /// <summary>顶层单元时长合计 = 当天 BGI 总运行时长。</summary>
    public TimeSpan TotalDuration { get; set; }
}

/// <summary>总览里的一个运行单元（可嵌套：一条龙配置单 → 配置组 → 组内独立任务）。</summary>
public sealed class OverviewUnit
{
    /// <summary>单元类型：一条龙 / 配置组 / 独立任务。</summary>
    public string Kind { get; set; } = "";

    /// <summary>单元名（配置单名/组名/任务名；独立任务无特征行时为"未知独立任务"）。</summary>
    public string Name { get; set; } = "";

    /// <summary>开始时间（time-of-day）。</summary>
    public TimeSpan Start { get; set; }

    /// <summary>运行时长（含子单元时间）。</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>未正常闭合（进行中或崩溃/强退），时长计至日志最后一行。</summary>
    public bool Unclosed { get; set; }

    /// <summary>子单元（被本单元包含的运行，仅展示用，不计入顶层合计）。</summary>
    public List<OverviewUnit> Children { get; } = new();
}
