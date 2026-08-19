namespace BgiCoordinatorServer.Models;

public class RemoteCommand
{
    public string RoomCode { get; set; } = string.Empty;
    public string Cmd { get; set; } = string.Empty;         // stop / start_group / start_oneclick
    public string Sender { get; set; } = string.Empty;
    public string SenderUid { get; set; } = string.Empty;
    public List<string> Target { get; set; } = [];           // ["*"] = 全员，否则 UID 列表
    public string CommandId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;    // ISO 8601
    public Dictionary<string, object>? Params { get; set; }
}