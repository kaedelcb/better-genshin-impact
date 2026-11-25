using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    internal static string RoomOrchestrationTaskKey(string intentId) => "room-orchestration/" + intentId;

    internal static string? ValidateRoomOrchestrationLaunch(RoomOrchestrationLaunch launch)
    {
        if (string.IsNullOrWhiteSpace(launch.IntentId) || string.IsNullOrWhiteSpace(launch.RoomIdentity)
            || launch.Epoch.ProcessId <= 0 || launch.Epoch.StartTicksUtc <= 0
            || launch.StopVersion < 0 || launch.IntentTimestamp <= 0)
            return "编排后继缺少原停止授权、请求编号或已确认房间身份";
        if (launch.Policy is not { Policy: TaskConflictPolicy.RunSpecified, RoomSwitch: null,
                SpecifiedStartMode: "beginning" or "node" })
            return "编排后继必须是修订绑定的独立指定任务，不能再次使用旧批次收尾动作";
        return CompletionTargetCatalogService.ValidateShape(launch.Policy);
    }

    internal static bool ValidRoomOrchestrationLaunch(RoomOrchestrationLaunch launch)
        => ValidateRoomOrchestrationLaunch(launch) is null;

    /// <summary>切房确认后启动普通配置组/一条龙；只等独立请求的受理回执，终态归任务中心观察。</summary>
    internal async Task<CommandResult> StartRoomOrchestrationTaskAsync(RoomOrchestrationLaunch launch,
        Func<CancellationToken, Task> verify, CancellationToken ct)
    {
        var invalid = ValidateRoomOrchestrationLaunch(launch);
        if (invalid is not null || launch.Policy.SpecifiedTaskType is not ("group" or "onedragon"))
            return new() { ErrorCode = "invalid_request", Message = invalid ?? "此入口只接受配置组或一条龙" };
        // The policy object is mutable. Keep the frozen fields, never read live room configuration.
        launch = launch with { Policy = launch.Policy.PostSwitchTask() };
        var admit = _roomOrchestrationAdmission ?? _externalStartAdmission;
        if (admit is null)
            return new() { ErrorCode = "r5_external_start_admission_unwired", Message = "编排后继准入未开放，未发送" };
        var ext = _externalClientProvider?.Invoke();
        if (ext is not { State: BgiExternalLinkState.Ready } || !ext.HasCapability(BgiExternalClient.CapabilityTaskQueue)
            || !ext.HasCapability(BgiExternalClient.StateContinuationCapability)
            || WorkflowStopAuthority.Epoch(ext.ServerEpoch) != WorkflowStopAuthority.Epoch(launch.Epoch))
            return new() { ErrorCode = "state_unavailable", Message = "原 BGI 编号执行通道未确认，未启动编排后继" };

        var localStopVersion = Volatile.Read(ref _allStopVersion);
        async Task VerifyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await verify(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _allStopInProgress) != 0 || Volatile.Read(ref _allStopVersion) != localStopVersion
                || _isBatchInFlight?.Invoke() == true || !ReferenceEquals(ext, _externalClientProvider?.Invoke())
                || WorkflowStopAuthority.Epoch(ext.ServerEpoch) != WorkflowStopAuthority.Epoch(launch.Epoch))
                throw new OperationCanceledException("编排后继授权已撤销或原执行通道/批次已变化", token);
        }

        CompletionStart start;
        try
        {
            await VerifyAsync(ct).ConfigureAwait(false);
            start = await DescribeCompletionStartAsync(launch.Policy, ext, launch.Epoch, ct).ConfigureAwait(false);
        }
        catch (CompletionStartException ex) { return new() { ErrorCode = ex.Code, Message = ex.Message }; }
        catch (OperationCanceledException ex) { return new() { Status = "cancelled", ErrorCode = "start_cancelled", Message = ex.Message }; }
        catch (Exception ex) { return new() { ErrorCode = "state_unavailable", Message = "后继发送前校验失败：" + ex.Message }; }

        var target = (start.OneDragon ? "onedragon:" : "group:") + start.Name;
        var key = RoomOrchestrationTaskKey(launch.IntentId);
        var consumed = 0;
        ExternalStartAdmissionOutcome outcome;
        try
        {
            outcome = await admit(new ExternalStartAdmissionRequest
            {
                Namespace = "system", WorkflowId = target, ResourceRef = target,
                TriggerOccurrenceId = "system:" + key, SourceDetail = "system:room-orchestration",
                WireSubmitKey = key, Tier = ArbitrationTier.Plan, Priority = launch.Policy.SpecifiedTaskPriority,
                RoomOrchestration = launch,
                ExecuteAsync = async sendToken =>
                {
                    if (Interlocked.Exchange(ref consumed, 1) != 0)
                        return ExternalStartExecution.UnknownWith("原编排后继发送许可已消费，禁止重发");
                    using var beforeSend = CancellationTokenSource.CreateLinkedTokenSource(ct, sendToken);
                    try
                    {
                        var submit = await ext.SubmitRoomOrchestrationTaskAsync(launch, start, VerifyAsync,
                            beforeSend.Token).ConfigureAwait(false);
                        if (submit.ServerRejectedBeforeAcceptance || IsRetryableQueueRejection(submit.ErrorCode))
                            return ExternalStartExecution.RejectedWith(submit.ErrorCode ?? "start_rejected",
                                evidenceSource: "ext:room-orchestration:before_acceptance");
                        var early = PrepareQueueEarlyForAdmission(ClassifyQueueSubmitEarly(submit, target, 0, DateTimeOffset.UtcNow));
                        return early.Kind == QueueStartEarlyKind.Accepted && Guid.TryParse(early.TaskHandle, out var job) && job != Guid.Empty
                            ? ExternalStartExecution.AcceptedWith(early.TaskHandle, "ext:room-orchestration:retained")
                            : ExternalStartExecution.UnknownWith(early.Detail ?? "编排后继缺少真实受理回执，按原编号核对");
                    }
                    catch (BgiStateContinuationStoppedException ex)
                    { return ExternalStartExecution.RejectedWith("manual_stop_fence", evidenceSource: "adapter:" + ex.Message); }
                    catch (BgiNotSentException ex)
                    { return ExternalStartExecution.RejectedWith(ex.EvidenceCode, evidenceSource: "adapter:" + ex.Message); }
                    catch (Exception ex)
                    { return ExternalStartExecution.UnknownWith("编排后继发送结果待核对：" + ex.Message); }
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new() { ErrorCode = "result_unknown", Message = "编排后继准入结果待核对，禁止重发：" + ex.Message,
                TargetProcessId = launch.Epoch.ProcessId, TargetStartTicksUtc = launch.Epoch.StartTicksUtc.ToString() };
        }
        if (outcome.Status == ExternalStartAdmissionStatus.Accepted && string.IsNullOrWhiteSpace(outcome.JobId))
            return new() { ErrorCode = "result_unknown", Message = "编排后继受理未确认实际作业编号，禁止重发" };
        var result = MapAdmissionOutcome(outcome, target);
        result.TargetProcessId = launch.Epoch.ProcessId;
        result.TargetStartTicksUtc = launch.Epoch.StartTicksUtc.ToString();
        result.ConfigRevision = start.Revision;
        return result;
    }
}
