namespace MultiplayerHoeingAssistant.Models;

public class IpcRequest
{
    public string OpCode { get; set; } = string.Empty;
    public string? Payload { get; set; }
}