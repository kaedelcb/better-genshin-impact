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

    private static bool TryParse(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
