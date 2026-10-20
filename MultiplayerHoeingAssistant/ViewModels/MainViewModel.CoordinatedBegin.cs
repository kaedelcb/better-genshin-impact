using System.IO;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _pendingCoordinatedBegin;
    private string? _pendingCoordinatedBeginRequestId;
    internal Func<CoordinatedBatchSnapshot, Task>? BeforeCoordinatedBeginReplyForTest;

    private void CancelPendingCoordinatedBegin()
    {
        lock (_batchGate)
        {
            _pendingCoordinatedBegin?.Cancel();
            if (_pendingCoordinatedBeginRequestId is { } requestId
                && _activeBatch?.Start?.RequestId == requestId) _activeBatch.Cancel();
        }
    }

    // Keep the original button request alive. This is the same invocation and request ID,
    // not a second batch or a background command queue.
    private async Task<CoordinatedBatchSnapshot> BeginCoordinatedWithContinuationAsync(string requestId,
        IReadOnlyList<string> targets, bool useCompletionActions, string quickCommandKey = "")
    {
        var context = CurrentControlContext();
        var originalClient = _signalRClient;
        var originalServer = _config!.ServerUrl;
        var originalRoom = RoomCode;
        var originalPassword = _config.ControlRoomPassword;
        var originalUid = _config.PlayerUid;
        var originalName = _config.PlayerName;
        var originalTeam = _config.TeamUids.ToList();
        var originalProxy = _config.BypassSystemProxy;
        CancellationTokenSource pending;
        lock (_batchGate)
        {
            if (_pendingCoordinatedBegin is not null)
                throw new InvalidOperationException("已有开始请求正在接续，未创建第二批");
            pending = _pendingCoordinatedBegin = new();
            _pendingCoordinatedBeginRequestId = requestId;
        }
        string? lastMessage = null;
        SignalRClient Client() => _signalRClient is { IsControlRoomReady: true } client
            ? client : throw new IOException("控制连接尚未恢复");
        async Task<T> ViaCurrentClient<T>(Func<SignalRClient, Task<T>> action)
        {
            var client = Client();
            try { return await action(client).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not GatewayErrorException
                && (!client.IsConnected || client.IsRetired || !ReferenceEquals(client, _signalRClient)))
            { throw new IOException("发送期间控制连接已断开，保留原请求", ex); }
        }
        async Task<T> InOriginalRoom<T>(Func<SignalRClient, Task<T>> action, CancellationToken token)
        {
            if (context == CurrentControlContext()) return await ViaCurrentClient(action).ConfigureAwait(false);
            if (originalClient is { IsControlRoomReady: true, IsRetired: false }
                && originalClient.ControlContext == context)
            {
                try { return await action(originalClient).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not GatewayErrorException
                    && (!originalClient.IsConnected || originalClient.IsRetired))
                { throw new IOException("原房间撤销连接暂不可用", ex); }
            }
            // A room switch may have retired the UI connection. Reuse the existing remote
            // protocol solely to query/reset the original request, without replacing a member.
            await using var cleanup = new SignalRClient();
            try
            {
                await cleanup.ConnectAsync(originalServer, originalRoom, originalPassword, originalUid,
                    originalName, originalTeam, isRemote: true, clientInstanceId: "cancel_" + requestId,
                    bypassSystemProxy: originalProxy, cancellationToken: token).ConfigureAwait(false);
                return await action(cleanup).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not GatewayErrorException)
            { throw new IOException("原房间请求撤销暂未确认", ex); }
        }
        void RequireContext()
        {
            if (_disposing || IsStandaloneMode || context != CurrentControlContext())
                throw new ControlContextRevokedException("原开始请求的房间已改变，未向新房间重发", pending.Token);
        }
        async Task<CoordinatedBatchSnapshot> Begin(CancellationToken token)
        {
            var accepted = await ViaCurrentClient(client => client.BeginCoordinatedBatchAsync(requestId, targets,
                DateTimeOffset.UtcNow.AddMinutes(2), token, useCompletionActions, quickCommandKey)).ConfigureAwait(false);
            if (BeforeCoordinatedBeginReplyForTest is { } pause) await pause(accepted).ConfigureAwait(false);
            return accepted;
        }
        try
        {
            return await ContinueCoordinatedBeginAsync(requestId, Begin,
                ct => InOriginalRoom(client => client.GetCurrentCoordinatedBatchAsync(ct), ct),
                async (accepted, ct) =>
                {
                    lock (_batchGate)
                        if (_activeBatch?.BatchId == accepted.BatchId) _activeBatch.Cancel();
                    await InOriginalRoom(client => client.ResetCoordinatedBatchAsync("cancel_" + requestId, targets,
                        accepted.Generation, accepted.BatchId, ct), ct);
                }, RequireContext,
                message => { if (lastMessage != message) { lastMessage = message; AddLog(message); } }, pending.Token);
        }
        finally
        {
            lock (_batchGate)
                if (ReferenceEquals(_pendingCoordinatedBegin, pending))
                { _pendingCoordinatedBegin = null; _pendingCoordinatedBeginRequestId = null; }
            pending.Dispose();
        }
    }

    internal static async Task<CoordinatedBatchSnapshot> ContinueCoordinatedBeginAsync(string requestId,
        Func<CancellationToken, Task<CoordinatedBatchSnapshot>> begin,
        Func<CancellationToken, Task<CoordinatedBatchSnapshot?>> query,
        Func<CoordinatedBatchSnapshot, CancellationToken, Task> cancelAccepted,
        Action requireContext, Action<string> waiting, CancellationToken ct,
        Func<CancellationToken, Task>? delay = null)
    {
        var attempted = false;
        var resolve = false;
        var revoked = false;
        bool StopRequested()
        {
            if (ct.IsCancellationRequested || revoked) return true;
            try { requireContext(); }
            catch (ControlContextRevokedException) { revoked = true; }
            return revoked;
        }
        delay ??= token => Task.Delay(1000, token);
        while (true)
        {
            if (StopRequested() && !attempted) throw new OperationCanceledException(ct);
            // Submission permission can end, but the original request's query/reset
            // responsibility remains until its late acceptance or absence is known.
            using var call = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cancellationResolved = false;
            try
            {
                CoordinatedBatchSnapshot? accepted = null;
                if (resolve || StopRequested())
                {
                    var current = await query(call.Token).WaitAsync(call.Token).ConfigureAwait(false);
                    if (current?.RequestId == requestId) accepted = current;
                    else if (StopRequested())
                    {
                        cancellationResolved = true;
                        throw new OperationCanceledException(ct);
                    }
                }
                if (accepted is null)
                {
                    if (StopRequested()) { resolve = attempted; continue; }
                    attempted = true;
                    accepted = await begin(call.Token).WaitAsync(call.Token).ConfigureAwait(false);
                    if (accepted.RequestId != requestId)
                        throw new InvalidOperationException("开始回执不属于原请求，未认领其他批次");
                }
                if (StopRequested())
                {
                    using var cancelCall = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cancelAccepted(accepted, cancelCall.Token).WaitAsync(cancelCall.Token).ConfigureAwait(false);
                    cancellationResolved = true;
                    throw new OperationCanceledException(ct);
                }
                return accepted;
            }
            catch (OperationCanceledException) when (cancellationResolved) { throw; }
            catch (Exception ex) when (IsRetryableBeginFailure(ex))
            {
                resolve = attempted;
                waiting(StopRequested() ? "正在确认原房间开始请求已撤销"
                    : "开始请求已保留，连接或成员就绪后继续：" + ex.Message);
            }
            try { await delay(StopRequested() ? CancellationToken.None : ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private static bool IsRetryableBeginFailure(Exception error) => error is IOException or TimeoutException
        or OperationCanceledException or System.Net.Http.HttpRequestException
        || error is GatewayErrorException gateway && (gateway.Code is "previous_batch_active"
            or "request_expired" or "service_unavailable" or "internal_error"
            || gateway.Code.StartsWith("batch_participant_unavailable_or_ambiguous:", StringComparison.Ordinal));
}
