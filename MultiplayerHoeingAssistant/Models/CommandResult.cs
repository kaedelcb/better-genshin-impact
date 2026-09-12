namespace MultiplayerHoeingAssistant.Models;

public class CommandResult
{
    public string Status { get; set; } = "failed";  // success / failed
    public string Message { get; set; } = string.Empty;
    /// <summary>BGI 侧信封 errorCode（如 task_busy），用于区分业务拒绝与传输失败；null = 无/旧路径未透传。</summary>
    public string? ErrorCode { get; set; }
}