namespace MultiplayerHoeingAssistant.Models;

public class RemoteCommand
{
    public string RoomCode { get; set; } = string.Empty;
    public string Cmd { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string SenderUid { get; set; } = string.Empty;
    public List<string> Target { get; set; } = [];
    public string CommandId { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public Dictionary<string, object>? Params { get; set; }
}