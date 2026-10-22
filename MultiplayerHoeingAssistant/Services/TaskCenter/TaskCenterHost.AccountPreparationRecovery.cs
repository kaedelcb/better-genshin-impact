using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private readonly Dictionary<string, Task<HandoffRegisterResult>> _accountPreparationReplays = new(StringComparer.Ordinal);

    private Task<HandoffRegisterResult> ReplayAccountPreparationAsync(StartupHandoffRequest request,
        HandoffRegisterResult receipt, CancellationToken ct)
    {
        TaskCompletionSource<HandoffRegisterResult> completion;
        WorkflowRunRecord run;
        lock (_gate)
        {
            if (receipt.Outcome != HandoffOutcome.AlreadyAccepted || request.Mode != StartupHandoffModes.Start)
                return Task.FromResult(receipt);
            var ledger = _runs.QueryHandoffLedger(request.IntentKey);
            if (ledger is not { State: HandoffLedgerState.Hit, Run: { } hit, Binding: { } binding })
                return Task.FromResult(LedgerGate(request) ?? receipt);
            var current = LedgerHitReceipt(hit, binding, request);
            if (current.Outcome != HandoffOutcome.AlreadyAccepted || binding.Mode != StartupHandoffModes.Start
                || binding.IntentKey != request.IntentKey || hit.RunId != receipt.RunId
                || !AccountPreparationRecovery.CanResume(hit)) return Task.FromResult(current);
            if (_accountPreparationReplays.TryGetValue(hit.RunId, out var inFlight)) return inFlight.WaitAsync(ct);
            run = hit;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _accountPreparationReplays.Add(run.RunId, completion.Task);
        }
        _ = CompleteAccountPreparationReplayAsync(request, run, completion, ct);
        return completion.Task;
    }

    private async Task CompleteAccountPreparationReplayAsync(StartupHandoffRequest request, WorkflowRunRecord original,
        TaskCompletionSource<HandoffRegisterResult> completion, CancellationToken ct)
    {
        HandoffRegisterResult? result = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
            var token = linked.Token;
            if (await EnsureExecutionEnvironmentAsync(token).ConfigureAwait(false) is { } error)
                result = AccountPreparationPending(original.RunId, error);
            else
            {
                var current = _runs.Load(original.RunId);
                if (current is null || !AccountPreparationRecovery.CanResume(current))
                    result = LedgerGate(request) ?? AccountPreparationPending(original.RunId, "原运行准备事实已变化，未自动继续");
                else
                {
                    if (_workflows.LoadSnapshot(current.WorkflowId).Revision != current.WorkflowRevision)
                        result = AccountPreparationPending(original.RunId, "原计划内容已改变，保留原运行等待显式处置");
                    var epoch = CurrentBgiEpoch();
                    if (result is null && !string.IsNullOrEmpty(epoch) && WorkflowStopAuthority.Current(current)?.Epoch != epoch)
                    {
                        var client = _clientAccessor();
                        var fence = client is null ? null
                            : await WorkflowStopAuthority.QueryAsync(new BgiExternalClientPort(client), token).ConfigureAwait(false);
                        var safe = AccountPreparationRecovery.LatestNotStarted(current)!;
                        if (fence is null || fence.Epoch != epoch || request.StopAuthority is not { } source
                            || !_runs.AppendAccountPreparationProcessContinuation(current.RunId, safe.JobId!, safe.Attempt,
                                fence, source, out var retained) || retained is null)
                            result = AccountPreparationPending(original.RunId,
                                "旧 BGI 进程退出、原停止意图或新实例停止基线未确认，保留原运行等待准备");
                    }
                    if (result is null)
                    {
                        token.ThrowIfCancellationRequested();
                        var resumed = await ResumeRunAsync(original.RunId).ConfigureAwait(false);
                        var state = TryReadRunState(original.RunId) ?? original.State;
                        var pending = !resumed.Ok || state is WorkflowRunState.Unknown or WorkflowRunState.Interrupted;
                        result = new(HandoffOutcome.AlreadyAccepted, original.RunId,
                            resumed.Ok
                                ? "此前已受理同一计划出现，已沿原运行重新准备（不新建运行、不重复已完成任务）；" + resumed.Message
                                : "此前已受理同一计划出现，原运行尚未继续：" + resumed.Message,
                            pending ? state == WorkflowRunState.Unknown ? HandoffReasonCodes.Unknown : HandoffReasonCodes.NotReady : null,
                            state);
                    }
                }
            }
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetResult(AccountPreparationPending(original.RunId,
                "原运行接续未确认，保留原身份与准备失败记录：" + ex.Message));
        }
        finally
        {
            lock (_gate) _accountPreparationReplays.Remove(original.RunId);
        }
    }

    private HandoffRegisterResult AccountPreparationPending(string runId, string reason)
        => new(HandoffOutcome.AlreadyAccepted, runId, reason, HandoffReasonCodes.NotReady, TryReadRunState(runId));
}
