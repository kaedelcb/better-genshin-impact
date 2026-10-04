using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>一个明确来源意图供所有挂载/重复回调继承。基线最多捕获一次；新 executionId 不产生新权限。</summary>
public sealed class StartupSourceIntent
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkflowStopAuthorityRecord? _authority;
    private volatile bool _revoked;
    public string IntentId { get; }
    public long IntentTimestamp { get; }
    public WorkflowStopAuthorityRecord? Authority => Volatile.Read(ref _authority);
    public bool Revoked => _revoked;

    public StartupSourceIntent(string intentId, long timestamp, WorkflowStopAuthorityRecord? authority = null)
    {
        if (string.IsNullOrWhiteSpace(intentId) || timestamp <= 0)
            throw new ArgumentException("来源意图要求固定身份和单调时间");
        if (authority is not null && (authority.IntentId != intentId || authority.IntentTimestamp != timestamp))
            throw new ArgumentException("来源基线不属于原意图");
        IntentId = intentId;
        IntentTimestamp = timestamp;
        _authority = authority;
    }

    public static StartupSourceIntent Explicit()
        => new(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp());
    public static StartupSourceIntent Inherit(WorkflowStopAuthorityRecord authority)
        => new(authority.IntentId, authority.IntentTimestamp, authority);

    public async Task<WorkflowStopAuthorityRecord?> GetOrCheckAsync(
        Func<StartupSourceIntent, bool, CancellationToken, Task<WorkflowStopAuthorityRecord?>> refresh,
        bool required, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_revoked) throw new OperationCanceledException("来源挂载已撤销，旧回调不得重新授权", ct);
            var current = await refresh(this, required, ct).ConfigureAwait(false);
            if (current is null)
            {
                if (required || _authority is not null)
                    throw new InvalidOperationException("来源停止权威不可确认");
                return null; // 尚未 Ready 的来源只携带原意图时间；不得执行受控提交。
            }
            if (current.IntentId != IntentId || current.IntentTimestamp != IntentTimestamp
                || (_authority is not null && current != _authority))
                throw new InvalidOperationException("来源基线已改变，禁止以当前版本洗白旧挂载");
            _authority ??= current;
            return _authority;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _revoked = true;
            throw;
        }
        finally { _gate.Release(); }
    }
}
