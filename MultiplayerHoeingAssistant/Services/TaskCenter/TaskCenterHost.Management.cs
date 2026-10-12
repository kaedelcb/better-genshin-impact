using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    public IReadOnlyList<WorkflowDeletionReceipt> ListDeletedFlows() => _workflows.ListDeleted();

    public HostActionResult DeleteFlow(string workflowId, string expectedRevision, bool acknowledgeUnknownRuns = false)
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
                // Do not initialize/recover arbitration while WithManagementWindow holds
                // .runstore.lock. Recovery can persist run records and would otherwise
                // deadlock against this same publication lock.
                WorkflowRunRecord[] unknownRuns = [];
                HostActionResult? blockedBySnapshot = null;
                _runs.WithManagementWindow(snapshot =>
                {
                    if (snapshot.UnknownFiles.Any(path => !CanConfirmUnrelatedRun(path, workflowId)) ||
                        snapshot.Records.Any(run => string.IsNullOrWhiteSpace(run.WorkflowId)))
                    {
                        blockedBySnapshot = HostActionResult.Unavailable("有运行记录无法确认所属计划，不能安全删除；原件已保留。");
                        return 0;
                    }
                    var responsible = snapshot.Records.FirstOrDefault(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) &&
                        (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)));
                    if (responsible is not null)
                    {
                        // BGI 的 job 表是进程内的；进程重启后旧 Unknown 可能永远无法再命中。
                        // 允许用户明确选择删除计划定义来解除占用，但只接受“全部责任都是 Unknown”这一形状，
                        // 保留原运行记录，不把未知结果改写成成功/失败，也不触发任何重发。
                        var unknownOnly = snapshot.Records
                            .Where(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)
                                && (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)))
                            .All(run => run.State == WorkflowRunState.Unknown);
                        if (!acknowledgeUnknownRuns || !unknownOnly)
                        {
                            blockedBySnapshot = HostActionResult.Unavailable("计划仍有运行或未确认责任，请先停止并结清；未删除计划。");
                            return 0;
                        }
                        unknownRuns = snapshot.Records
                            .Where(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)
                                && run.State == WorkflowRunState.Unknown)
                            .ToArray();
                    }
                    return 0;
                });
                if (blockedBySnapshot is not null) return blockedBySnapshot;

                if (_admissionWired && unknownRuns.Length != 0)
                {
                    try
                    {
                        // The RunStore publication lock is released before this call.
                        EnsureAdmissionFacadeAsync(_shutdownCts.Token).GetAwaiter().GetResult();
                        foreach (var unknown in unknownRuns)
                        {
                            var retired = _admission?.RetireUnknownSubmission(unknown.RunId,
                                "用户明确解除计划占用；保留 Unknown 历史，不判定远端结果，不重发");
                            // Startup recovery may already have retired the current Submission.
                            // In that case the absence of a slot is expected, but any remaining
                            // active operation for this run must still be retired and checked.
                            if (retired is null || (retired.Kind != AdmissionResultKind.Accepted
                                && retired.ReasonCode is not ("no_submission" or "submission_owner_mismatch")))
                                return HostActionResult.Unavailable("旧 Unknown 的仲裁发送责任未能安全隔离；计划未删除，原记录和责任均保留。"
                                    + (retired is null ? " code=admission_null" : $" code={retired.ReasonCode ?? "unknown"} {retired.Detail}"));
                            _admission?.RetireUnknownOperations(unknown.RunId,
                                "用户明确解除计划占用；保留 Unknown 操作原件，不判定远端结果，不重发");
                            var arbitration = _admissionStore?.Read();
                            var handoff = arbitration?.File?.Handoff;
                            if (arbitration?.Status != ArbitrationLeaseStatus.Valid
                                || (handoff?.Operations.Any(op => op.Zone == OperationZone.Active
                                    && string.Equals(op.RunBinding, unknown.RunId, StringComparison.Ordinal))
                                    ?? false)
                                || handoff?.Submission is { } current && !handoff.Operations.Any(op =>
                                    !string.Equals(op.RunBinding, unknown.RunId, StringComparison.Ordinal)
                                    && string.Equals(op.SubmissionIdentity, current.SubmissionIdentity, StringComparison.Ordinal)
                                    && op.LastSendSeq == current.SendSeq))
                                return HostActionResult.Unavailable("旧 Unknown 的仲裁责任仍占用执行通道；计划未删除，原记录和责任均保留。");
                        }
                    }
                    catch (Exception ex)
                    {
                        return HostActionResult.Unavailable("旧 Unknown 的仲裁发送责任未能安全隔离；计划未删除，原记录和责任均保留：" + ex.Message);
                    }
                }

                // Re-read under the publication lock before the destructive move. This
                // preserves the original ownership/unknown-only guard after the unlock.
                result = _runs.WithManagementWindow(snapshot =>
                {
                    if (snapshot.UnknownFiles.Any(path => !CanConfirmUnrelatedRun(path, workflowId)) ||
                        snapshot.Records.Any(run => string.IsNullOrWhiteSpace(run.WorkflowId)))
                        return HostActionResult.Unavailable("有运行记录无法确认所属计划，不能安全删除；原件已保留。");
                    var responsible = snapshot.Records.FirstOrDefault(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase) &&
                        (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)));
                    if (responsible is not null && (!acknowledgeUnknownRuns || snapshot.Records
                        .Where(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)
                            && (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)))
                        .Any(run => run.State != WorkflowRunState.Unknown)))
                        return HostActionResult.Unavailable("计划仍有运行或未确认责任，请先停止并结清；未删除计划。");
                    var receipt = _workflows.Delete(workflowId, expectedRevision);
                    return HostActionResult.Effective(acknowledgeUnknownRuns
                        ? $"已解除计划「{receipt.Name}」的编辑占用并移入可恢复删除区；旧 Unknown 运行记录保留，未判定远端结果，也未重发任务。现在可以新建计划；若启动仍提示执行通道占用，需先完成外部通道对账。"
                        : $"已删除计划「{receipt.Name}」。可从计划管理恢复，任务资源和运行历史保留。");
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
