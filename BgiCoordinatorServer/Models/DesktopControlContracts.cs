namespace BgiCoordinatorServer.Models;

/// <summary>截图对应的主屏物理范围；LayoutId 改变后旧帧不再用于输入。</summary>
public sealed record DesktopCaptureGeometry
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string DisplayId { get; init; } = "";
    public string LayoutId { get; init; } = "";
    public bool IsValid => Width is > 0 and <= 32768 && Height is > 0 and <= 32768
        && DisplayId is { Length: > 0 and <= 256 } && LayoutId is { Length: > 0 and <= 256 };
}

/// <summary>一次完整输入，不接受任意脚本、键码、长按或文本。</summary>
public sealed record DesktopControlCommand
{
    public string RequestId { get; init; } = "";
    public string TargetUid { get; init; } = "";
    public string FrameId { get; init; } = "";
    public string ControlId { get; init; } = "";
    public string Action { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public string Key { get; init; } = "";
}

public sealed record DesktopControlRequest
{
    public DesktopControlCommand Command { get; init; } = new();
    public string RoomCode { get; init; } = "";
    public string RequesterConnectionId { get; init; } = "";
    public DateTimeOffset ExpiresAtUtc { get; init; }
    // 以同一截图为两端的单调时间锚点；不要求执行端与服务器墙钟同步。
    public double FrameAgeMilliseconds { get; init; }
    public double LifetimeMilliseconds { get; init; }
}

public sealed record DesktopControlResult
{
    public string RequestId { get; init; } = "";
    public string TargetUid { get; init; } = "";
    // executed 表示操作系统已接受完整输入，不证明游戏界面已按预期变化。
    public string Status { get; init; } = "error";
    public string Message { get; init; } = "";
}

/// <summary>显式开始、续期或结束一次控制；观看本身不代表控制。</summary>
public sealed record DesktopControlPresenceRequest
{
    public string RequestId { get; init; } = "";
    public string ControlId { get; init; } = "";
    public string TargetUid { get; init; } = "";
    public string FrameId { get; init; } = "";
    public string Action { get; init; } = "";
    // 以下字段只由服务端填写，不能信任请求方提供的值。
    public string RequesterConnectionId { get; init; } = "";
    public string RoomCode { get; init; } = "";
    public double LifetimeMilliseconds { get; init; }
    public double FrameAgeMilliseconds { get; init; }
    // 仅本机浏览器直连使用同机 UTC 截止；远程以截图相对预算为准。
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record DesktopControlPresenceResult
{
    public string RequestId { get; init; } = "";
    public string ControlId { get; init; } = "";
    public string TargetUid { get; init; } = "";
    public string Status { get; init; } = "denied";
    public string Message { get; init; } = "";
}

public static class DesktopControlProtocol
{
    public static readonly TimeSpan InputInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FrameLifetime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan PresenceRenewInterval = TimeSpan.FromSeconds(4);
    public static readonly string[] Keys = ["Esc", "Enter", "Space", "Tab", "F", "W", "S", "A", "D", "F11", "F12", "Alt+F4", "Win+D"];

    public static bool IsValid(DesktopControlCommand command)
        => command != null && Guid.TryParseExact(command.RequestId, "N", out _)
            && Guid.TryParseExact(command.ControlId, "N", out _)
            && command.TargetUid is { Length: > 0 and <= 128 }
            && command.FrameId is { Length: > 0 and <= 100 }
            && double.IsFinite(command.X) && double.IsFinite(command.Y)
            && (command.Action == "key"
                ? Keys.Contains(command.Key, StringComparer.Ordinal)
                : command.Action is "left" or "right" or "double"
                    && command.X is >= 0 and <= 1 && command.Y is >= 0 and <= 1
                    && string.IsNullOrEmpty(command.Key));
}
