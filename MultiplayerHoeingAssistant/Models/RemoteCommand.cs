namespace MultiplayerHoeingAssistant.Models;

public class RemoteCommand
{
    public string RoomCode { get; set; } = string.Empty;
    public string Cmd { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string SenderUid { get; set; } = string.Empty;
    public List<string> Target { get; set; } = [];
    /// <summary>仅远程中心使用：精确绑定执行端实例，避免同 UID 双开误投。</summary>
    public string? TargetInstanceId { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public Dictionary<string, object>? Params { get; set; }
}
