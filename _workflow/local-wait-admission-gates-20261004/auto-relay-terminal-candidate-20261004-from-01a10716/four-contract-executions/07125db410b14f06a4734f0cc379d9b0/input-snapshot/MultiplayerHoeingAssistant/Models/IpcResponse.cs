namespace MultiplayerHoeingAssistant.Models;

public class IpcResponse
{
    public bool Success { get; set; }
    public string? Data { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>BGI 侧信封 errorCode（如 task_already_running / cross_session_rejected），用于区分业务拒绝与传输失败。</summary>
    public string? ErrorCode { get; set; }
}