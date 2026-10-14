namespace BgiCoordinatorServer.Models;

public class RemoteCommand
{
    public string RoomCode { get; set; } = string.Empty;
    public string Cmd { get; set; } = string.Empty;         // stop / start_group / start_oneclick
    public string Sender { get; set; } = string.Empty;
    public string SenderUid { get; set; } = string.Empty;
    public List<string> Target { get; set; } = [];           // ["*"] = 全员，否则 UID 列表
    /// <summary>远程中心请求的唯一目标实例；旧命令为空保持原路由。</summary>
    public string? TargetInstanceId { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;    // ISO 8601
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public Dictionary<string, object>? Params { get; set; }
}
