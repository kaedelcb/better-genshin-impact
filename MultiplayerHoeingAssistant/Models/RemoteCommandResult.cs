namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 远端任务下发结果回执（纯加法协议；字段与 BgiCoordinatorServer/Models/RemoteCommandResult.cs 镜像，
/// 以服务器为唯一权威，改动须双向同步）。目标机执行完 start_group / start_oneclick 后上报，
/// 服务端按 SenderUid 回投给发起方；发起方 15s 未收到回执显示"已发送（无回执）"。
/// </summary>
public class RemoteCommandResult
{
    public string RoomCode { get; set; } = string.Empty;
    /// <summary>原命令的 CommandId（发起方据此关联挂起的等待会话）。</summary>
    public string CommandId { get; set; } = string.Empty;
    /// <summary>原命令名（start_group / start_oneclick）。</summary>
    public string Cmd { get; set; } = string.Empty;
    /// <summary>原命令发起方 UID（服务端回投路由键）。</summary>
    public string SenderUid { get; set; } = string.Empty;
    /// <summary>执行机（回执来源）UID。</summary>
    public string TargetUid { get; set; } = string.Empty;
    /// <summary>执行机昵称（UI 直显）。</summary>
    public string TargetName { get; set; } = string.Empty;
    /// <summary>success / failed / cancelled。</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>BGI/助手侧错误码（如 task_busy、unhandled_exception）；null = 无。</summary>
    public string? ErrorCode { get; set; }
    /// <summary>人读结果描述（失败原因等）。</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>任务名（配置组/一条龙名）。</summary>
    public string TaskName { get; set; } = string.Empty;
}
