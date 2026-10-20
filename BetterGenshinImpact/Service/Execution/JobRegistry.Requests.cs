using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Execution;

public sealed partial class JobRegistry
{
    // Admission receipts are not an evictable response cache. Never evict an unexpired
    // receipt to make room: reject BEFORE dispatch instead. In-flight receipts never expire.
    internal const int RequestReceiptCapacity = 4096;
    private readonly Dictionary<string, RequestReceipt> _requestReceipts = new(StringComparer.Ordinal);

    private sealed class RequestReceipt(string fingerprint, bool retainUntilAcknowledged = false)
    {
        public string Fingerprint { get; } = fingerprint;
        public bool RetainUntilAcknowledged { get; } = retainUntilAcknowledged;
        public DateTime? CompletedAtUtc { get; set; }
        public TaskCompletionSource<InstanceIpcEnvelope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal async Task<InstanceIpcEnvelope> ExecuteRequestOnceAsync(string key, string fingerprint,
        InstanceIpcEnvelope request, Func<Task<InstanceIpcEnvelope>> execute, CancellationToken waitingToken,
        Func<string, string, ExternalInterface.BgiTaskCoordinator.SubmitResult?>? retainedStartLookup = null)
    {
        RequestReceipt? receipt;
        var owner = false;
        // Stop and exact result acknowledgement are naturally repeatable and must remain usable under backpressure.
        var safetyStop = request.Operation is "ext.task.stop" or "task.stop" or "ext.task.acknowledge";
        var retainedStart = request.Operation == ExternalInterface.ExternalInterfaceOperations.TaskStart
            && request.Data?["retainUntilAcknowledged"]?.Value<bool>() == true;
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow - IdempotencyWindowTtl;
            foreach (var old in _requestReceipts.Where(p => !p.Value.RetainUntilAcknowledged && p.Value.CompletedAtUtc is { } at && at < cutoff).Select(p => p.Key).ToArray())
                _requestReceipts.Remove(old);
            if (_requestReceipts.TryGetValue(key, out receipt))
            {
                if (receipt.Fingerprint != fingerprint)
                    return RequestFailure(request, "idempotency_conflict", "同一请求键不能用于不同操作或参数");
            }
            else if (_requestReceipts.Count >= RequestReceiptCapacity)
            {
                if (retainedStart)
                {
                    // The retained queue item owns uniqueness. A full auxiliary reply cache
                    // must not block this accepted-intent path; duplicate callbacks still adopt one queue item.
                    receipt = new RequestReceipt(fingerprint, true);
                    owner = true;
                }
                else if (safetyStop) receipt = null;
                else return RequestFailure(request, "idempotency_capacity", "请求回执保留窗口已满，请稍后提交新请求；已有请求仍可查询/重试");
            }
            else
            {
                receipt = new RequestReceipt(fingerprint,
                    request.Operation == ExternalInterface.ExternalInterfaceOperations.TaskStart
                    && request.Data?["retainUntilAcknowledged"]?.Value<bool>() == true);
                _requestReceipts.Add(key, receipt);
                owner = true;
            }
        }
        if (receipt == null) return await execute().ConfigureAwait(false);
        if (owner)
        {
            // The executor is detached from a duplicate waiter's cancellation. Completion
            // always settles the receipt, even if the original transport has disconnected.
            _ = CompleteRequestAsync(key, receipt, request, execute);
        }
        var response = await receipt.Completion.Task.WaitAsync(waitingToken).ConfigureAwait(false);
        // 回执只是可补投影：队列已经受理时，即使第一次响应构建失败，也取回原句柄。
        if (receipt.RetainUntilAcknowledged && request.Data?["idempotencyKey"]?.ToString() is { Length: > 0 } submissionKey)
        {
            var known = retainedStartLookup?.Invoke(submissionKey, fingerprint);
            if (known == null && retainedStartLookup == null && ExternalInterface.BgiTaskCoordinator.IsCreated
                && ExternalInterface.BgiTaskCoordinator.Instance.TryReplayRetainedStart(submissionKey, fingerprint, out var existing))
                known = existing;
            if (known is { } accepted)
                return ExternalInterface.ExternalInterfaceCommandPlane.MapTaskStartQueueResult(request, accepted,
                    request.Data?["generation"]?.Value<int>() ?? 0);
        }
        if (receipt.RetainUntilAcknowledged && response.ErrorCode == "result_unknown")
        {
            // 本次执行回调已经结束且真实队列没有这项动作；下一次同号请求可再次认领。
            // 如另一个请求恰已入队，Submit 的原队列项去重仍返回同一任务，不重放副作用。
            lock (_gate)
                if (_requestReceipts.TryGetValue(key, out var current) && ReferenceEquals(current, receipt))
                    _requestReceipts.Remove(key);
        }
        return CopyResponse(response, request.RequestId);
    }

    private async Task CompleteRequestAsync(string key, RequestReceipt receipt, InstanceIpcEnvelope request,
        Func<Task<InstanceIpcEnvelope>> execute)
    {
        InstanceIpcEnvelope response;
        var retain = true;
        try
        {
            response = await execute().ConfigureAwait(false);
            // Only proven pre-admission rejections may be retried. A failed execution
            // or unknown outcome is still an executed request and must not run twice.
            retain = response.Success == true || response.ErrorCode is not
                ("queue_full" or "task_busy" or "task_already_running" or "takeover_conflict"
                 or "manual_stop_cooldown" or "service_unavailable" or "invalid_request"
                 or "configuration_changed" or "task_not_found");
        }
        catch (Exception ex)
        {
            response = RequestFailure(request, "result_unknown", "请求执行中断，禁止自动重放：" + ex.GetBaseException().Message);
        }
        lock (_gate)
        {
            receipt.CompletedAtUtc = DateTime.UtcNow;
            if (!retain && _requestReceipts.TryGetValue(key, out var current) && ReferenceEquals(current, receipt))
                _requestReceipts.Remove(key);
            receipt.Completion.TrySetResult(CopyResponse(response, response.RequestId));
        }
    }

    internal void AcknowledgeRetainedRequest(string submissionKey)
    {
        lock (_gate) _requestReceipts.Remove("key:" + submissionKey);
    }

    private static InstanceIpcEnvelope RequestFailure(InstanceIpcEnvelope request, string code, string message) => new()
    {
        RequestId = request.RequestId, Operation = "response", Success = false, ErrorCode = code, ErrorMessage = message
    };

    private static InstanceIpcEnvelope CopyResponse(InstanceIpcEnvelope response, Guid requestId) => new()
    {
        RequestId = requestId, Operation = response.Operation, Success = response.Success,
        ErrorCode = response.ErrorCode, ErrorMessage = response.ErrorMessage,
        Data = (JObject?)response.Data?.DeepClone()
    };
}
