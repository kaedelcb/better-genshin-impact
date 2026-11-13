using System.Collections.Concurrent;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    // 取消仍用 task_policy.pull，旧端最多执行一次只读查询。取消只能影响同一发起端的原请求。
    private readonly ConcurrentDictionary<(string Sender, string Command), CancellationTokenSource> _completionCatalogRefreshes = new();
    private readonly object _completionCatalogRefreshGate = new();
    private const int CompletionCatalogRevocationLimit = 1024;
    private readonly Dictionary<(string Sender, string Command), DateTimeOffset> _completionCatalogRevocations = new();
    private DateTimeOffset _completionCatalogRevocationsFullUntil;

    private bool TryRegisterCompletionCatalogRefresh((string Sender, string Command) key, CancellationTokenSource cancellation)
    {
        lock (_completionCatalogRefreshGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneCompletionCatalogRevocations(now);
            if (!_completionCatalogRefreshes.TryAdd(key, cancellation)) return false;
            // 撤销可早于原发送到达；已撤销的登记仍进入正常取消错误回复路径，绝不授予启动权限。
            if (_completionCatalogRevocations.ContainsKey(key) || _completionCatalogRevocationsFullUntil > now)
                TryCancelCompletionCatalogRefresh(cancellation);
            return true;
        }
    }

    private void CancelCompletionCatalogRefresh(string senderUid, string commandId)
    {
        lock (_completionCatalogRefreshGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneCompletionCatalogRevocations(now);
            // 原请求最多有效 180 秒；从撤销到达起保留同样时长，覆盖原消息的整个剩余期限。
            var expires = now.Add(TaskPolicySyncService.CatalogRefreshTimeout);
            var key = (senderUid, commandId);
            if (_completionCatalogRevocations.ContainsKey(key) || _completionCatalogRevocations.Count < CompletionCatalogRevocationLimit)
                _completionCatalogRevocations[key] = expires;
            else if (_completionCatalogRevocationsFullUntil < expires)
                // 不淘汰仍有效的撤销。容量耗尽时暂缓新刷新，直到本次未能存入的撤销也已过期。
                _completionCatalogRevocationsFullUntil = expires;
            if (_completionCatalogRefreshes.TryGetValue(key, out var pending))
                TryCancelCompletionCatalogRefresh(pending);
        }
    }

    private void PruneCompletionCatalogRevocations(DateTimeOffset now)
    {
        foreach (var key in _completionCatalogRevocations.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            _completionCatalogRevocations.Remove(key);
    }

    private static void TryCancelCompletionCatalogRefresh(CancellationTokenSource pending)
    {
        try { pending.Cancel(); }
        catch (ObjectDisposedException) { /* Reply and cancellation may cross in flight. */ }
    }

    private async Task<CompletionTargetCatalog> RefreshCompletionCatalogAsync(string type, string name,
        string workflowId, CancellationToken ct, DateTimeOffset? expiresAtUtc = null)
    {
        if (type is not ("group" or "onedragon"))
            return await GetCompletionTargetCatalogService().LoadAsync(type, name, workflowId, ct);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 总等待小于远端的 180 秒，并覆盖共享确保门的 150 秒及目录读取；网络在途时间也计入原请求期限。
        var budget = TimeSpan.FromSeconds(175);
        if (expiresAtUtc is { } deadline && deadline - DateTimeOffset.UtcNow < budget)
            budget = deadline - DateTimeOffset.UtcNow;
        cancellation.CancelAfter(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        void StopRefresh() => TryCancelCompletionCatalogRefresh(cancellation);
        StopAllAutomationRequested += StopRefresh;
        try
        {
            var context = CurrentControlContext();
            var path = _config?.BgiPath;
            var monitor = _processMonitor;
            void RequireOriginalPermission()
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (expiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new OperationCanceledException("原目录刷新请求已过期，未继续启动或读取。");
                if (_disposing || !IsExecutorMode || _localResetGate.CurrentCount == 0
                    || Volatile.Read(ref _ordinaryStopInProgress) > 0)
                    throw new OperationCanceledException("本地执行权限已撤销或正在停止，目录刷新已取消。");
                if (context != CurrentControlContext() || path != _config?.BgiPath || !ReferenceEquals(monitor, _processMonitor))
                    throw new OperationCanceledException("执行端身份或 BGI 路径已变化，请重新刷新目录。");
            }
            if (!TryEnterConfigTransferOperation("start_bgi", out var operation))
                return new([], null, ConfigTransferBlockedMessage);
            using (operation)
            {
                RequireOriginalPermission();
                return await ReadCompletionCatalogAsync(type, name, workflowId, true,
                    token => EnsureTaskCenterExecutionReadyCoreAsync(token, preserveConfiguredPath: true,
                        requireLaunchPermission: RequireOriginalPermission),
                    (targetType, targetName, id, token) =>
                    {
                        RequireOriginalPermission();
                        // 确保后重新取得已绑定的通道，不能使用启动前捕获的空 transport。
                        return GetCompletionTargetCatalogService().RefreshAsync(targetType, targetName, id, token);
                    }, cancellation.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            return new([], null, "任务目录刷新已取消：" + ex.Message);
        }
        catch (Exception ex)
        {
            return new([], null, "任务目录刷新失败：" + ex.Message);
        }
        finally { StopAllAutomationRequested -= StopRefresh; }
    }

    // 此顺序就是刷新合同：只确保 group/onedragon，确保失败或取消时绝不读取目录。
    internal static async Task<CompletionTargetCatalog> ReadCompletionCatalogAsync(string type, string name,
        string workflowId, bool ensureBgiReady, Func<CancellationToken, Task<string?>> ensureReady,
        Func<string, string, string, CancellationToken, Task<CompletionTargetCatalog>> load,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ensureBgiReady && type is ("group" or "onedragon"))
        {
            var error = await ensureReady(ct);
            ct.ThrowIfCancellationRequested();
            if (error != null) return new([], null, error);
        }
        ct.ThrowIfCancellationRequested();
        var catalog = await load(type, name, workflowId, ct);
        ct.ThrowIfCancellationRequested();
        return catalog;
    }
}
