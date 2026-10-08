using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    public IReadOnlyList<WorkflowDeletionReceipt> ListDeletedFlows() => _workflows.ListDeleted();

    public HostActionResult DeleteFlow(string workflowId, string expectedRevision)
    {
        HostActionResult result;
        try
        {
            lock (_gate)
            {
                if (_shutdown) return HostActionResult.Unavailable("任务中心已关闭。");
                if (CapabilityBlockReason() is { } blocked) return HostActionResult.Unavailable(blocked);
                if (_reservedWorkflows.Any(id => string.Equals(id, workflowId, StringComparison.OrdinalIgnoreCase)) ||
                    _drives.Keys.Any(id => string.Equals(id, workflowId, StringComparison.OrdinalIgnoreCase)))
                    return HostActionResult.Unavailable("计划正在启动或运行，请先停止并等待退出。");
                result = _runs.WithManagementWindow(snapshot =>
                {
                    if (snapshot.UnknownFiles.Any(path => !CanConfirmUnrelatedRun(path, workflowId)) ||
                        snapshot.Records.Any(run => string.IsNullOrWhiteSpace(run.WorkflowId)))
                        return HostActionResult.Unavailable("有运行记录无法确认所属计划，不能安全删除；原件已保留。");
                    var responsible = snapshot.Records.FirstOrDefault(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) &&
                        (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)));
                    if (responsible is not null)
                        return HostActionResult.Unavailable("计划仍有运行或未确认责任，请先停止并结清；未删除计划。");
                    var receipt = _workflows.Delete(workflowId, expectedRevision);
                    return HostActionResult.Effective($"已删除计划「{receipt.Name}」。可从计划管理恢复，任务资源和运行历史保留。");
                });
            }
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }
        if (result.Ok) NotifyStateChanged();
        return result;
    }

    public HostActionResult RestoreFlow(string recoveryId)
    {
        WorkflowDeletionReceipt receipt;
        try
        {
            lock (_gate)
            {
                if (_shutdown) return HostActionResult.Unavailable("任务中心已关闭。");
                if (CapabilityBlockReason() is { } blocked) return HostActionResult.Unavailable(blocked);
                receipt = _workflows.RestoreDeleted(recoveryId);
            }
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }
        NotifyStateChanged();
        return HostActionResult.Effective($"已恢复计划「{receipt.Name}」，未启动任务；保留副本仍在。");
    }

    // Call only inside the final host registration window after asynchronous readiness.
    // A deleted/revised plan cannot be launched from the earlier UI snapshot.
    private string? ValidateManagedFlowSnapshot(string workflowId, string revision)
    {
        try
        {
            var current = _workflows.LoadSnapshot(workflowId);
            return current.Revision.Equals(revision, StringComparison.OrdinalIgnoreCase)
                ? null : "计划在准备启动期间已修改，请刷新后重新开始。";
        }
        catch (Exception ex) { return "计划已删除或暂不可用，未启动：" + ex.Message; }
    }

    private static bool CanConfirmUnrelatedRun(string path, string workflowId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var identities = document.RootElement.EnumerateObject().Where(p => p.Name == "workflowId").ToList();
            return identities.Count == 1 && identities[0].Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(identities[0].Value.GetString()) &&
                !string.Equals(identities[0].Value.GetString(), workflowId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }
}
