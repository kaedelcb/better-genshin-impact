using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>Desktop-only display formatting. The shared presentation, raw details and execution state are not changed.</summary>
public sealed class RoomOrchestrationDisplayConverter : IValueConverter, IMultiValueConverter
{
    private const string JobPrefix = "本机作业 ";
    private const string JobSuffix = " 已确认成功";
    private const string ScheduleConfirmation = "（待确认）";

    internal static string? ConfirmedJob(RoomOrchestrationStatus status)
    {
        var detail = status.Detail;
        if (status.Kind != "done" || !detail.StartsWith(JobPrefix, StringComparison.Ordinal)
            || !detail.EndsWith(JobSuffix, StringComparison.Ordinal) || detail.Length <= JobPrefix.Length + JobSuffix.Length) return null;
        var id = detail[JobPrefix.Length..^JobSuffix.Length];
        // Only the exact known native success message can be folded into a job label.
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    internal static string InlineTaskDetail(RoomOrchestrationStatus status)
        => ConfirmedJob(status) is not null ? "" : status.Detail;

    internal static string ShortJob(RoomOrchestrationTaskRow row, double availableWidth)
    {
        var id = ConfirmedJob(row.Status);
        if (id is null || !double.IsFinite(availableWidth) || availableWidth < 425) return "";
        // Conservative full-name allowance; if a name is long, omit the optional identifier
        // before making the name wrap. The full identifier remains in the room detail dialog.
        var nameBudget = StringInfo.ParseCombiningCharacters(row.Name).Length * 13d
            + StringInfo.ParseCombiningCharacters(row.Type).Length * 12d;
        return availableWidth >= nameBudget + 32 + 114 + 132 + 22
            ? "作业 " + (id.Length <= 8 ? id : id[..8] + "…") : "";
    }

    internal static string Summary(RoomOrchestrationCard card)
    {
        var detail = !string.IsNullOrWhiteSpace(card.Detail) ? card.Detail : card.Status.Detail;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            if (card.ProgressUnconfirmed) return "状态待核对";
            const string retained = "绑定完成动作尚未确认，保留原请求：计划表原运行尚未退出，保留同一完成动作";
            return detail == retained ? "计划表尚未退出" : detail.Replace('\r', ' ').Replace('\n', ' ');
        }
        return card.Tasks.Any(t => !string.IsNullOrWhiteSpace(t.Status.Detail))
            || card.NextTasks.Any(t => !string.IsNullOrWhiteSpace(t.Status.Detail)) ? "任务执行详情" : "";
    }

    internal static bool IsProgressUnknown(RoomOrchestrationCard card)
        => card.ProgressUnconfirmed || card.Tasks.Any(t => t.Status.Kind == "unknown");

    internal static bool ShowCompactProgressBar(RoomOrchestrationCard card)
        => !IsProgressUnknown(card) && card.HasConfirmedProgress && card.TaskCount > 0
            && card.Tasks.Any(t => t.Status.Kind != "none");

    internal static string CompactProgress(RoomOrchestrationCard card)
        => IsProgressUnknown(card) ? "进度待核对" : card.TaskCount == 0 ? "未绑定"
            : card.Tasks.All(t => t.Status.Kind == "none") ? $"{card.TaskCount} 项绑定"
            : $"{card.CompletedCount}/{card.TaskCount}";

    internal static string FullDetails(RoomOrchestrationCard card)
    {
        var result = new StringBuilder();
        result.AppendLine($"房间：{card.Name} · {card.Identity}");
        if (card.HasSection) result.AppendLine($"分类：{card.SectionTitle}");
        result.AppendLine($"房间状态：{card.Status.Text}");
        result.AppendLine("任务进度：" + (IsProgressUnknown(card) ? "进度待核对" : card.ProgressText));
        result.AppendLine("完成后：" + card.Completion);
        if (card.Edge is { } edge)
        {
            result.AppendLine(edge.Relation);
            result.AppendLine(edge.PostAction);
        }
        result.AppendLine($"预期人数：{card.ExpectedPlayers}  ·  定时上线：{card.Schedule}");
        if (!string.IsNullOrWhiteSpace(card.UpdatedText)) result.AppendLine($"状态时间：{card.UpdatedText}");
        if (!string.IsNullOrWhiteSpace(card.Detail)) result.AppendLine(card.Detail);
        if (!string.IsNullOrWhiteSpace(card.Status.Detail) && card.Status.Detail != card.Detail) result.AppendLine(card.Status.Detail);
        AppendTasks(card.Tasks, "任务执行记录");
        AppendTasks(card.NextTasks, "下轮生效任务");
        return result.ToString().TrimEnd();

        void AppendTasks(RoomOrchestrationTaskRow[] tasks, string title)
        {
            if (!tasks.Any(t => !string.IsNullOrWhiteSpace(t.Status.Detail))) return;
            result.AppendLine(); result.AppendLine(title);
            foreach (var task in tasks.Where(t => !string.IsNullOrWhiteSpace(t.Status.Detail)))
                result.AppendLine($"{task.Number}. {task.Name} · {task.Type} · {task.Status.Text}\n{task.Status.Detail}");
        }
    }

    internal static bool ScheduleNeedsConfirmation(string schedule) => schedule.EndsWith(ScheduleConfirmation, StringComparison.Ordinal);
    internal static string ScheduleValue(string schedule) => ScheduleNeedsConfirmation(schedule) ? schedule[..^ScheduleConfirmation.Length] : schedule;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (parameter as string) switch
    {
        "TaskDetail" when value is RoomOrchestrationStatus status => InlineTaskDetail(status),
        "Summary" when value is RoomOrchestrationCard card => Summary(card),
        "DetailsVisibility" when value is RoomOrchestrationCard card => string.IsNullOrWhiteSpace(Summary(card)) ? Visibility.Collapsed : Visibility.Visible,
        "Details" when value is RoomOrchestrationCard card => FullDetails(card),
        "CompactProgress" when value is RoomOrchestrationCard card => CompactProgress(card),
        "ProgressTooltip" when value is RoomOrchestrationCard card => IsProgressUnknown(card) ? "进度待核对" : card.ProgressText,
        "ProgressUnknown" when value is RoomOrchestrationCard card => IsProgressUnknown(card),
        "ShowProgressBar" when value is RoomOrchestrationCard card => ShowCompactProgressBar(card),
        "ProgressBarVisibility" when value is RoomOrchestrationCard card => ShowCompactProgressBar(card) ? Visibility.Visible : Visibility.Collapsed,
        "ScheduleLabel" when value is string schedule => ScheduleNeedsConfirmation(schedule) ? "待确认" : "定时",
        "ScheduleValue" when value is string schedule => ScheduleValue(schedule),
        "ScheduleNeedsConfirmation" when value is string schedule => ScheduleNeedsConfirmation(schedule),
        _ => DependencyProperty.UnsetValue
    };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter as string == "CardTimeVisibility")
            return values is [RoomOrchestrationCard card, double available] && available >= 730 && card.HasUpdatedTime
                ? Visibility.Visible : Visibility.Collapsed;
        return values is [RoomOrchestrationTaskRow row, double width] ? ShortJob(row, width) : "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
