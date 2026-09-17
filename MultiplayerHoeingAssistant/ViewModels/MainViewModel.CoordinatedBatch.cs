using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>Only the control-room authority may advance an online batch or allocate a recovery attempt.</summary>
    private async Task<bool> RunCoordinatedBatchAsync(OnlineHoeingBatch batch, IReadOnlyList<string> names)
    {
        var token = Guid.NewGuid().ToString("N");
        var id = "";
        var layout = string.Join(",", names.Select((_, i) =>
            _config?.OnlineHoeingGroupTypes?.ElementAtOrDefault(i) == "onedragon" ? "onedragon" : "group"));
        var kinds = layout.Split(',');
        int index = 0, attempt = 0;
        long revision = -1;
        var result = "";
        var stopping = false;
        var complete = false;
        BatchExpectedItem? item = null;
        BgiEpoch? epoch = null;
        var lastContact = DateTime.UtcNow;
        var startup = DateTime.UtcNow;
        DateTime submittedAt = default;

        async Task<CoordinatedBatchSnapshot> Exchange(string outcome, CancellationToken ct)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(TimeSpan.FromSeconds(10));
            var signal = _signalRClient ?? throw new InvalidOperationException("控制房间未连接");
            var snapshot = await signal.UpdateCoordinatedBatchAsync(batch.Generation, token, id, layout,
                index, attempt, outcome, bound.Token);
            if (snapshot.Generation != batch.Generation || (id.Length > 0 && snapshot.BatchId != id)
                || snapshot.Revision < revision || snapshot.Index < index || snapshot.Index > names.Count
                || snapshot.Attempt is < 0 or > 1)
                throw new InvalidOperationException("批次身份或版本不匹配，禁止推进");
            id = snapshot.BatchId;
            if (revision != snapshot.Revision)
                AddLog($"[全队批次] {id} revision={snapshot.Revision} item={snapshot.Index} attempt={snapshot.Attempt} phase={snapshot.Phase} reason={snapshot.Reason}");
            revision = snapshot.Revision;
            return snapshot;
        }

        async Task RequestStop(CancellationToken ct)
        {
            var ext = _externalClient;
            if (item == null || ext == null) return;
            if (item.JobId == null && item.State == BatchItemState.Submitted)
            {
                var jobs = await ext.QueryJobListAsync(ct);
                item.JobId = jobs?.Jobs.FirstOrDefault(j => j.IdempotencyKey == item.RequestKey)?.JobId;
            }
            if (item.JobId != null)
                await ext.CancelOwnedTaskAsync(item.JobId, ct);
        }

        try
        {
            if (_processMonitor?.IsBgiRunning == false) _processMonitor.RestartBgi(null);
            while (!batch.IsCancellationRequested)
            {
                CoordinatedBatchSnapshot team;
                try
                {
                    team = await Exchange(result, batch.Cts.Token);
                    lastContact = DateTime.UtcNow;
                }
                catch when (!batch.IsCancellationRequested && DateTime.UtcNow - lastContact < TimeSpan.FromSeconds(30))
                {
                    // No authoritative reply means no permission to start or advance.
                    await Task.Delay(1000, batch.Cts.Token);
                    continue;
                }
                if (team.Phase == "aborted") throw new InvalidOperationException("全队批次中止: " + team.Reason);
                if (team.Phase == "completed")
                {
                    complete = true;
                    batch.CoordinatedSucceeded = true;
                    AddLog($"[全队批次] {id} 全员完成，共 {names.Count} 项");
                    return true;
                }
                if (team.Phase == "registering")
                {
                    await Task.Delay(1000, batch.Cts.Token);
                    continue;
                }
                if (team.Phase is not ("running" or "stopping")) throw new InvalidOperationException("未知批次阶段");
                if (team.Index != index || team.Attempt != attempt)
                {
                    // Authority only advances after every member reported the previous job terminal.
                    index = team.Index;
                    attempt = team.Attempt;
                    item = null;
                    result = "";
                    stopping = false;
                    startup = DateTime.UtcNow;
                    AddLog($"[全队批次] {id} 第 {index + 1}/{names.Count} 项，统一恢复次数 {attempt}/1");
                }
                var ext = _externalClient;
                if (ext is not { State: BgiExternalLinkState.Ready })
                {
                    if (DateTime.UtcNow - startup > TimeSpan.FromSeconds(60)) throw new TimeoutException("BGI 状态不可用");
                    await Task.Delay(1000, batch.Cts.Token);
                    continue;
                }
                if (!ext.HasCapability("hoeing.batchOutcome.v1"))
                    throw new InvalidOperationException("BGI 未声明联机批次结果能力，请全员升级后重试");
                using var rpc = CancellationTokenSource.CreateLinkedTokenSource(batch.Cts.Token);
                rpc.CancelAfter(TimeSpan.FromSeconds(10));
                var observed = await CoordinatedBatchReads.ReadAsync(ct => ext.QueryJobListAsync(ct), rpc.Token)
                    ?? throw new InvalidOperationException("BGI 作业状态未知");
                if (observed.Epoch == null) throw new InvalidOperationException("BGI 纪元未知");
                if (epoch != null && (epoch.ProcessId != observed.Epoch.ProcessId || epoch.StartTicksUtc != observed.Epoch.StartTicksUtc))
                    throw new InvalidOperationException("BGI 已重启，旧任务状态未知");
                epoch = observed.Epoch;
                startup = DateTime.UtcNow;
                item ??= new BatchExpectedItem(names[index], kinds[index] == "onedragon");
                var job = observed.Jobs.FirstOrDefault(j => item.JobId != null ? j.JobId == item.JobId : j.IdempotencyKey == item.RequestKey);
                item.JobId ??= job?.JobId;
                if (item.State == BatchItemState.Submitted && item.JobId == null
                    && DateTime.UtcNow - submittedAt > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("启动应答丢失且按请求身份补查未找到任务，禁止重复启动或推进");
                if (team.Phase == "stopping" && result.Length == 0)
                {
                    stopping = true;
                    if (item.State == BatchItemState.PendingSubmit) result = "stopped";
                    else await RequestStop(rpc.Token);
                }
                if (result.Length == 0 && item.State == BatchItemState.Submitted && item.JobId != null)
                {
                    // Queue terminal is published after the executor returns (including scope/slot cleanup).
                    var terminal = await CoordinatedBatchReads.ReadAsync(ct => ext.QueryTaskQueueStatusAsync(item.JobId, ct), rpc.Token);
                    result = CoordinatedBatchOutcome.Classify(terminal, stopping);
                    if (result.Length > 0)
                        AddLog($"[全队批次] {id} item={index} attempt={attempt} job={item.JobId} 本端结果={result} error={terminal?.ErrorCode}");
                }
                if (team.Phase == "running" && result.Length == 0 && item.State == BatchItemState.PendingSubmit)
                {
                    // Mark submitted before await. Unknown acceptance must never create a second request identity.
                    item.State = BatchItemState.Submitted;
                    submittedAt = DateTime.UtcNow;
                    BgiTaskSubmitResult submit;
                    try
                    {
                        submit = await ext.SubmitTaskStartAsync(item.IsOneDragon ? null : item.Name,
                        item.IsOneDragon ? item.Name : null, 0, batch.Generation,
                        string.Join(",", names.Where((_, i) => kinds[i] == "group")), rpc.Token,
                        preempt: true, idempotencyKey: item.RequestKey, coordinatedHoeing: true);
                    }
                    catch (Exception ex) when (!batch.IsCancellationRequested
                        && ex is System.IO.IOException or TimeoutException or OperationCanceledException)
                    {
                        AddLog("[全队批次] 启动应答未确认，按原请求身份补查，不重复下发: " + ex.Message);
                        continue;
                    }
                    if (!submit.Success) throw new InvalidOperationException("BGI 拒绝批次项: " + submit.ErrorCode);
                    item.JobId = submit.TaskHandle;
                    if (string.IsNullOrEmpty(item.JobId)) throw new InvalidOperationException("BGI 未返回受理句柄");
                    AddLog($"[全队批次] {id} item={index} attempt={attempt} job={item.JobId} {item.Name}");
                }
                await Task.Delay(1000, batch.Cts.Token);
            }
            return false;
        }
        finally
        {
            if (!complete)
            {
                // Failure is not successful completion and must not run a bound after-success group.
                lock (_teardownGate)
                {
                    _teardownDoneGeneration = Math.Max(_teardownDoneGeneration, batch.Generation);
                    _lastTeardownUtc = DateTime.UtcNow;
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await Exchange(batch.IsCancellationRequested ? "cancelled" : "unknown", cleanup.Token); }
                catch (Exception ex) { AddLog("[全队批次] 中止上报未确认，禁止推进: " + ex.Message); }
                try { await RequestStop(cleanup.Token); }
                catch (Exception ex) { AddLog("[全队批次] 原任务停止未确认: " + ex.Message); }
                if (_commandExecutor != null)
                    await _commandExecutor.ExecuteResumeAsync(cancel: true);
                NotifyBatchLoud("联机批次未完整完成", "已停止推进后续配置组；请查看全队批次日志。");
            }
        }
    }
}
