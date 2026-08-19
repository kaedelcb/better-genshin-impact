namespace MultiplayerHoeingAssistant.Models;

public class IpcResponse
{
    public bool Success { get; set; }
    public string? Data { get; set; }
    public string? ErrorMessage { get; set; }
}