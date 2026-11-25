using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private static string? ReadBgiOnlineWorkflowRunId(JsonElement data, string property)
        => !data.TryGetProperty(property, out var run) || run.ValueKind == JsonValueKind.Null ? null
            : run.ValueKind == JsonValueKind.String ? run.GetString() ?? "" : "";

    private async Task<Func<CancellationToken, Task>?> CaptureManualRoomSwitchStopGuardAsync(CancellationToken ct)
    {
        if (_externalClient is not { State: BgiExternalLinkState.Ready } client
            || !client.HasCapability("execution.manualStopFence.v1")) return null;
        var epoch = client.ServerEpoch;
        async Task<long> ReadStopVersion(CancellationToken token)
        {
            if (!ReferenceEquals(client, _externalClient) || client.ServerEpoch != epoch)
                throw new OperationCanceledException("原 BGI 进程已变化", token);
            var fence = await client.SendCommandAsync("ext.execution.manualStopFence", timeout: TimeSpan.FromSeconds(2), cancellationToken: token);
            using var doc = JsonDocument.Parse(fence.Data ?? "{}");
            if (!fence.Success || !doc.RootElement.TryGetProperty("stopVersion", out var version) || !version.TryGetInt64(out var stop))
                throw new OperationCanceledException("本机停止状态无法确认", token);
            return stop;
        }
        var version = await ReadStopVersion(ct);
        return async token =>
        {
            if (await ReadStopVersion(token) != version)
                throw new OperationCanceledException("F11 已停止，未继续切房", token);
        };
    }

    private sealed record WorkflowOnlineOrigin(int Generation, string RunId, string ControlContext);
    private WorkflowOnlineOrigin? _workflowOnlineOrigin;
    private sealed record BgiRoomSignal(string Id, string RunId, string Kind, string? Target, long StopVersion);
    private readonly object _bgiRoomMailboxGate = new();
    private CancellationTokenSource? _bgiRoomMailboxCancellation;
    private BgiExternalClient? _bgiRoomMailboxPeer;
    private string? _bgiRoomMailboxSession;
    private int _bgiRoomMailboxEpoch;

    private void StartBgiRoomMailbox(BgiExternalClient client)
    {
        if (!client.HasCapability("roomAction.mailbox.v1") || client.SessionId is not { } session) return;
        CancellationTokenSource? previous;
        CancellationTokenSource cancellation;
        int epoch;
        lock (_bgiRoomMailboxGate)
        {
            if (_disposing || !ReferenceEquals(client, _externalClient) || client.State != BgiExternalLinkState.Ready) return;
            if (_bgiRoomMailboxCancellation is not null && ReferenceEquals(_bgiRoomMailboxPeer, client)
                && _bgiRoomMailboxSession == session) return;
            previous = _bgiRoomMailboxCancellation;
            cancellation = new CancellationTokenSource();
            _bgiRoomMailboxPeer = client;
            _bgiRoomMailboxSession = session;
            _bgiRoomMailboxCancellation = cancellation;
            epoch = Interlocked.Increment(ref _bgiRoomMailboxEpoch);
        }
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        ObserveOnlineTask(RunBgiRoomMailboxAsync(client, session, epoch, cancellation), "本机房间动作通道");
    }

    private void StopBgiRoomMailbox()
    {
        CancellationTokenSource? previous;
        lock (_bgiRoomMailboxGate)
        {
            Interlocked.Increment(ref _bgiRoomMailboxEpoch);
            previous = Interlocked.Exchange(ref _bgiRoomMailboxCancellation, null);
            _bgiRoomMailboxPeer = null;
            _bgiRoomMailboxSession = null;
        }
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunBgiRoomMailboxAsync(BgiExternalClient client, string session, int epoch, CancellationTokenSource lifetime)
    {
        using var pumpLifetime = lifetime;
        var token = lifetime.Token;
        var bgiEpoch = client.ServerEpoch;
        var queue = Channel.CreateBounded<BgiRoomSignal>(new BoundedChannelOptions(3)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        bool Current() => !_disposing && IsExecutorMode && ReferenceEquals(_externalClient, client)
            && epoch == Volatile.Read(ref _bgiRoomMailboxEpoch) && client.ServerEpoch == bgiEpoch
            && client.SessionId == session && client.State == BgiExternalLinkState.Ready;
        async Task Verify(long expected, CancellationToken ct)
        {
            if (!Current()) throw new OperationCanceledException("原 BGI IPC 会话已结束", ct);
            var reply = await client.SendCommandAsync("ext.execution.manualStopFence", timeout: TimeSpan.FromSeconds(2), cancellationToken: ct);
            using var status = JsonDocument.Parse(reply.Data ?? "{}");
            if (!Current() || !reply.Success || !status.RootElement.TryGetProperty("stopVersion", out var stop)
                || !stop.TryGetInt64(out var version) || version != expected)
                throw new OperationCanceledException("F11 已停止或原房间动作授权无法核对", ct);
        }

        var outstanding = 0;
        string? admittedRun = null;
        var consumer = ConsumeAsync();
        var last = "";
        try
        {
            while (!token.IsCancellationRequested && Current())
            {
                var reply = await client.SendCommandAsync("ext.roomAction.peek", timeout: TimeSpan.FromSeconds(2), cancellationToken: token);
                if (!reply.Success) throw new InvalidOperationException(reply.ErrorMessage ?? "不能读取本机房间动作");
                using var doc = JsonDocument.Parse(reply.Data ?? "{}");
                if (doc.RootElement.TryGetProperty("signal", out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    var signal = new BgiRoomSignal(item.GetProperty("id").GetString()!, item.GetProperty("runId").GetString()!,
                        item.GetProperty("kind").GetString()!, item.TryGetProperty("targetRoomName", out var name) ? name.GetString() : null,
                        item.GetProperty("stopVersion").GetInt64());
                    if (signal.Id != last)
                    {
                        var accepted = false;
                        try
                        {
                            await Verify(signal.StopVersion, token);
                            var sameRun = admittedRun == signal.RunId;
                            var pending = Volatile.Read(ref outstanding);
                            accepted = !IsStandaloneMode && !_roomSwitchRecordUnreadable
                                && signal.Kind is "switch" or "online" or "switchOnline" && pending < 3
                                && (signal.Kind == "online" || !string.IsNullOrWhiteSpace(signal.Target))
                                && (sameRun ? !RoomSwitchBlocksRoomOperations || _changingAssistantRoom && pending > 0
                                    : signal.Kind is "switch" or "switchOnline" && pending == 0 && !RoomSwitchHasPendingRequest && !RoomSwitchBlocksRoomOperations);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        var ack = await client.SendCommandAsync("ext.roomAction.ack", new { id = signal.Id, accepted },
                            TimeSpan.FromSeconds(2), token);
                        using var ackDoc = JsonDocument.Parse(ack.Data ?? "{}");
                        if (ack.Success && ackDoc.RootElement.TryGetProperty("confirmed", out var confirmed)
                            && confirmed.ValueKind == JsonValueKind.True && Current())
                        {
                            last = signal.Id;
                            if (accepted)
                            {
                                admittedRun = signal.RunId;
                                Interlocked.Increment(ref outstanding);
                                if (!queue.Writer.TryWrite(signal))
                                {
                                    Interlocked.Decrement(ref outstanding);
                                    throw new InvalidOperationException("本机房间动作缓冲已满，未推进后续操作");
                                }
                            }
                            if (!accepted) AddLog("本机房间动作未受理：连接、停止状态或缓冲不可用");
                        }
                    }
                }
                await Task.Delay(300, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { AddLog("本机房间动作通道中断，旧请求不会补跑：" + ex.Message); }
        finally
        {
            queue.Writer.TryComplete();
            await lifetime.CancelAsync();
            try { await consumer; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            Interlocked.CompareExchange(ref _bgiRoomMailboxCancellation, null, lifetime);
        }

        async Task ConsumeAsync()
        {
            string? runId = null, roomIdentity = null;
            var failed = false;
            var reportedOnline = false;
            await foreach (var signal in queue.Reader.ReadAllAsync(token))
            {
                if (runId != signal.RunId)
                {
                    runId = signal.RunId;
                    failed = false;
                    reportedOnline = false;
                    roomIdentity = null;
                }
                try
                {
                    if (failed) continue;
                    await Verify(signal.StopVersion, token);
                    if (roomIdentity is not null && !IsAssistantRoomIdentityCurrent(roomIdentity))
                        throw new InvalidOperationException("前序目标房间已变化，后续房间动作未提交");
                    if (signal.Kind is "switch" or "switchOnline")
                    {
                        var profileId = Application.Current.Dispatcher.Invoke(() => RoomSwitchChoices
                            .Where(p => string.Equals(p.Name, signal.Target, StringComparison.OrdinalIgnoreCase))
                            .Select(p => p.Id).SingleOrDefault());
                        if (profileId is null) throw new InvalidOperationException("找不到唯一的目标房间档案：" + signal.Target);
                        var switched = await SwitchAssistantRoomCoreAsync(profileId, ct => Verify(signal.StopVersion, ct),
                            roomIdentity, token, waitForOnlineRound: reportedOnline);
                        if (!switched.Success) throw new InvalidOperationException(switched.Message);
                        roomIdentity = switched.Identity;
                        reportedOnline = false;
                    }
                    if (signal.Kind is "online" or "switchOnline")
                    {
                        if (roomIdentity is null || !IsAssistantRoomIdentityCurrent(roomIdentity))
                            throw new InvalidOperationException("前序目标房间已变化，未提交上线");
                        if (!await ReportAssistantRoomChainOnlineAsync(roomIdentity, ct => Verify(signal.StopVersion, ct), token))
                            throw new InvalidOperationException("目标房间未受理上线");
                        reportedOnline = true;
                    }
                }
                catch (Exception ex)
                {
                    failed = true;
                    SetRoomSwitchStatus("本机房间动作已停止：" + ex.Message);
                    AddLog(RoomSwitchStatus);
                }
                finally { Interlocked.Decrement(ref outstanding); }
            }
        }
    }

    private async Task<bool> ReportAssistantRoomChainOnlineAsync(string identity, Func<CancellationToken, Task> verifySource, CancellationToken ct)
    {
        if (!await _assistantRoomSwitchGate.WaitAsync(0, ct)) return false;
        try
        {
            await verifySource(ct);
            if (!IsAssistantRoomIdentityCurrent(identity) || _signalRClient is not { } client || AssistantRoomHasBatch()) return false;
            if (!TryEnterConfigTransferOperation("online", out var transfer)) return false;
            using var configTransfer = transfer;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(12));
            var generation = _intentLifecycle.RaiseLocalIntent("command");
            try
            {
                if (!await client.ReportRoomChainOnlineAsync(generation, budget.Token))
                {
                    _intentLifecycle.OnResetCleared();
                    return false;
                }
                if (!IsAssistantRoomIdentityCurrent(identity))
                    throw new ControlChannelUnavailableException("上线后原房间会话已变化");
                // Deliberately do not arm reconnect replay for a chain which has lost its source.
                AddLog("已确认在房间 " + client.CurrentRoomCode + " 上线，等待本轮联机");
                return true;
            }
            catch
            {
                _intentLifecycle.OnResetCleared();
                throw;
            }
        }
        finally { _assistantRoomSwitchGate.Release(); }
    }
}
