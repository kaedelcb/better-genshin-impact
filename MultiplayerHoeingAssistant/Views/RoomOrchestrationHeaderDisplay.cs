using System.Text;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>Only formats the existing desktop projection; does not infer login or cleanup from configuration.</summary>
internal sealed record RoomOrchestrationHeaderDisplay(string LoginLabel, string RoomName, string RoomCode,
    string ExecutorName, string Reason, RoomOrchestrationStatus Status, string Details)
{
    public bool HasRoomCode => !string.IsNullOrWhiteSpace(RoomCode);
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    public bool IsCleanupPending => Reason == "清理确认中";

    internal static RoomOrchestrationHeaderDisplay Create(RoomOrchestrationPresentation presentation,
        bool observer, MainViewModel.OrchestrationMonitorTarget? target, string? localName)
    {
        var loginText = presentation.LoginText;
        var loginLabel = "登录";
        var roomName = loginText;
        var roomCode = "";
        var confirmed = presentation.Cards.Where(c => c.Current).Take(2).ToArray();
        var current = confirmed.Length == 1 ? confirmed[0] : null;
        // A confirmed current card is the only source of the confirmed login display.
        if (current is not null && loginText.StartsWith("当前登录：", StringComparison.Ordinal))
        {
            roomName = current.Name;
            roomCode = CodeOf(current.Identity);
        }
        else if (loginText.StartsWith("最后确认房间：", StringComparison.Ordinal))
        {
            loginLabel = "最后确认";
            roomName = loginText["最后确认房间：".Length..];
            const string unconfirmed = "（尚未确认连接）";
            if (roomName.EndsWith(unconfirmed, StringComparison.Ordinal)) roomName = roomName[..^unconfirmed.Length];
            // Match against the entire existing label, not a guessed split of a room name.
            var matches = presentation.Cards.Where(c => roomName == $"{c.Name} {CodeOf(c.Identity)}").ToArray();
            if (matches.Length == 1) { roomName = matches[0].Name; roomCode = CodeOf(matches[0].Identity); }
        }
        else
        {
            foreach (var prefix in new[] { "当前登录：", "执行端登录：" })
                if (roomName.StartsWith(prefix, StringComparison.Ordinal)) { roomName = roomName[prefix.Length..]; break; }
        }

        var executor = observer ? target?.Name ?? "尚未选择" : string.IsNullOrWhiteSpace(localName) ? "本机" : localName;
        var raw = presentation.LoginContext;
        var reason = raw;
        if (observer)
        {
            var prefix = target is not null ? "执行端：" + target.Label + " · " : "监控端 · 任务编排 · ";
            if (reason.StartsWith(prefix, StringComparison.Ordinal)) reason = reason[prefix.Length..];
        }
        // This is a wording reduction only. Cancellation is not evidence that cleanup finished.
        reason = reason == "联机锄地已取消，正在确认清理" ? "清理确认中"
            : reason.Replace('\r', ' ').Replace('\n', ' ');
        var details = new StringBuilder();
        details.AppendLine(loginText);
        if (current is not null) details.AppendLine("房间身份：" + current.Identity);
        details.AppendLine("执行端：" + executor);
        if (observer && target is not null)
        {
            details.AppendLine("UID：" + target.Uid);
            details.AppendLine("实例号：" + target.InstanceId);
            details.AppendLine("机器号：" + target.MachineId);
        }
        details.AppendLine("当前状态：" + presentation.CurrentStatus.Text);
        if (!string.IsNullOrWhiteSpace(raw)) details.AppendLine(raw);
        if (!string.IsNullOrWhiteSpace(presentation.CurrentStatus.Detail) && presentation.CurrentStatus.Detail != raw
            && presentation.CurrentStatus.Detail != reason) details.AppendLine(presentation.CurrentStatus.Detail);
        return new(loginLabel, roomName, roomCode, executor, reason, presentation.CurrentStatus, details.ToString().TrimEnd());
    }

    private static string CodeOf(string identity)
    {
        var separator = identity.IndexOf(" · ", StringComparison.Ordinal);
        return separator < 0 ? identity : identity[..separator];
    }
}

// Preserve the exact underlying target even when the display label is short.
internal sealed record MonitorTargetOption(MainViewModel.OrchestrationMonitorTarget Target, bool AmbiguousName)
{
    public string Label => AmbiguousName ? Target.Label : Target.Name;
    public string FullLabel => $"{Target.Name}\n实例号：{Target.InstanceId}\n机器号：{Target.MachineId}";
    public override string ToString() => Label;
}
