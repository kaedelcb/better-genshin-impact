namespace BgiCoordinatorServer.Models;

/// <summary>
/// 远端任务下发结果回执（纯加法协议，2026-11）：目标机执行完 start_group / start_oneclick 后
/// 经 control.reportCommandResult 上报，服务端按 SenderUid 回投给命令发起方连接。
/// 旧服务端无此消息名 → 客户端按 unsupported_operation 降级停发；旧客户端不上报 →
/// 发起方 15s 超时显示"已发送（无回执）"。
/// </summary>
public class RemoteCommandResult
{
    public string RoomCode { get; set; } = string.Empty;
    /// <summary>原命令的 CommandId（发起方据此关联挂起的等待会话）。</summary>
    public string CommandId { get; set; } = string.Empty;
    /// <summary>原命令名（start_group / start_oneclick）。</summary>
    public string Cmd { get; set; } = string.Empty;
    /// <summary>原命令发起方 UID（回投路由键）。</summary>
    public string SenderUid { get; set; } = string.Empty;
    /// <summary>执行机（回执来源）UID。</summary>
    public string TargetUid { get; set; } = string.Empty;
    /// <summary>执行机昵称（发起方 UI 直显，省一次反查）。</summary>
    public string TargetName { get; set; } = string.Empty;
    /// <summary>success / failed / cancelled。</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>BGI/助手侧错误码（如 task_busy、unhandled_exception）；null = 无。</summary>
    public string? ErrorCode { get; set; }
    /// <summary>人读结果描述（失败原因等）。</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>任务名（配置组/一条龙名）。</summary>
    public string TaskName { get; set; } = string.Empty;
    public string? ConfigRevision { get; set; }
    public int? TargetProcessId { get; set; }
    public string? TargetStartTicksUtc { get; set; } // string: preserve Int64 precision through JavaScript
}
