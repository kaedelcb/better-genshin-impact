namespace MultiplayerHoeingAssistant.Services;

/// <summary>Only an executor terminal can acknowledge cleanup; absence is never success.</summary>
public static class CoordinatedBatchOutcome
{
    public static string Classify(BgiTaskQueueStatus? terminal, bool stopping) => terminal?.Status switch
    {
        "pending" or "running" => "",
        "completed" when !terminal.Cancelled => "succeeded",
        "completed" or "queueCancelled" when stopping => "stopped",
        "completed" or "queueCancelled" => "cancelled",
        "failed" when terminal.ErrorCode == "hoeing_incomplete" => "failed",
        "failed" when stopping && terminal.ErrorCode == "task_cancelled" => "stopped",
        "failed" when terminal.ErrorCode is "configuration_changed" or "configuration_revision_required"
            or "configuration_not_found" or "invalid_configuration_name" or "task_disabled" => "preparation_failed",
        // BGI 已登记了明确的执行终态，不能再把已清理完成的启动失败
        // 降级成协调批次的 Unknown。让服务端进入既有 bounded recovery，
        // 由下一轮 attempt 处理，而不是立即把整队批次打成 unknown。
        "failed" when terminal.ErrorCode is "task_start_failed" or "task_busy" => "failed",
        _ => "unknown"
    };
}
