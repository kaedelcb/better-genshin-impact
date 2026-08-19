namespace MultiplayerHoeingAssistant.Models;

public class CommandResult
{
    public string Status { get; set; } = "failed";  // success / failed
    public string Message { get; set; } = string.Empty;
}