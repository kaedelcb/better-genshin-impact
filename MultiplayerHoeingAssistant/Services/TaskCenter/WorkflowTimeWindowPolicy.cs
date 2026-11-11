using System.Globalization;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>计划表与运行边界共享的每日允许时间段规则；使用传入时刻的本机时分。</summary>
public static class WorkflowTimeWindowPolicy
{
    public static bool TryValidate(WorkflowTimeWindow? window, out string? error)
    {
        error = null;
        if (window is null) return true;
        if (!TryParse(window.Start, out _) || !TryParse(window.End, out _))
        {
            error = "开始和结束时间须为 HH:mm。";
            return false;
        }
        return true;
    }

    public static bool Allows(WorkflowTimeWindow? window, DateTimeOffset now)
    {
        if (window is null) return true;
        if (!TryParse(window.Start, out var start) || !TryParse(window.End, out var end)) return false;
        if (start == end) return true;
        var time = TimeOnly.FromDateTime(now.DateTime);
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    public static string Display(WorkflowTimeWindow? window)
    {
        if (window is null) return "全天";
        if (!TryParse(window.Start, out var start) || !TryParse(window.End, out var end)) return "时间段无效";
        if (start == end) return "全天";
        return window.Start + "–" + (start > end ? "次日 " : "") + window.End;
    }

    public static NodeGateDecision Evaluate(WorkflowDocument document, WorkflowNode node, DateTimeOffset now)
    {
        return Evaluate(document.NodeTimeWindowEnabled, node.TimeWindow, now);
    }

    public static NodeGateDecision Evaluate(bool enabled, WorkflowTimeWindow? window, DateTimeOffset now)
    {
        if (!enabled) return NodeGateDecision.ProceedInstance;
        if (!TryValidate(window, out var error))
            return new(NodeGateAction.Reject, "允许执行时间段无效，未执行：" + error);
        return Allows(window, now) ? NodeGateDecision.ProceedInstance
            : new(NodeGateAction.Skip, "当前不在允许执行时间段（" + Display(window) + "），跳过本次任务。");
    }

    /// <summary>
    /// 等待到点的目标时刻：时段限制开启、设置了等待、当前在时段外，且当天开始时间还没到时返回当天的开始时刻；
    /// 当天开始时间已过（已错过）返回 null，按原规则跳过。
    /// </summary>
    public static DateTimeOffset? StartWaitTarget(bool enabled, WorkflowTimeWindow? window, DateTimeOffset now)
    {
        if (!enabled || window is not { WaitForStart: true } || Allows(window, now)) return null;
        if (!TryParse(window.Start, out var start) || !TryParse(window.End, out var end) || start == end) return null;
        var time = TimeOnly.FromDateTime(now.DateTime);
        if (start <= time) return null;
        return new DateTimeOffset(now.Year, now.Month, now.Day, start.Hour, start.Minute, 0, now.Offset);
    }

    /// <summary>当前时刻所在时段的结束时刻（严格结束用）；全天、无效或当前不在时段内返回 null。</summary>
    public static DateTimeOffset? CurrentWindowEnd(WorkflowTimeWindow? window, DateTimeOffset now)
    {
        if (window is null || !Allows(window, now)) return null;
        if (!TryParse(window.Start, out var start) || !TryParse(window.End, out var end) || start == end) return null;
        var time = TimeOnly.FromDateTime(now.DateTime);
        var at = new DateTimeOffset(now.Year, now.Month, now.Day, end.Hour, end.Minute, 0, now.Offset);
        return end > time ? at : at.AddDays(1);
    }

    /// <summary>附加选项的简短说明（展示用；没有附加选项时为空）。</summary>
    public static string OptionsDisplay(WorkflowTimeWindow? window)
    {
        if (window is null) return "";
        var parts = new List<string>();
        if (window.WaitForStart) parts.Add(window.CloseGameWhileWaiting ? "关游戏等到点" : "等到点执行");
        if (window.StopAtEnd) parts.Add("到点结束");
        return string.Join(" · ", parts);
    }

    private static bool TryParse(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
