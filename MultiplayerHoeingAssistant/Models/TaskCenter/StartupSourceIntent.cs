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
    internal void Revoke() => _revoked = true;
    /// <summary>监控端本地动作来源：永不获取本机 BGI 停止权威，切到执行端后也不能升级旧回调。</summary>
    public bool LocalOnly { get; }

    // 只在一次新的助手自动启动中检查前次授权。已挂载回调从不进入此入口。
    internal WorkflowStopAuthorityRecord? AutomaticPredecessor { get; private init; }

    public StartupSourceIntent(string intentId, long timestamp, WorkflowStopAuthorityRecord? authority = null, bool localOnly = false)
    {
        if (string.IsNullOrWhiteSpace(intentId) || timestamp <= 0)
            throw new ArgumentException("来源意图要求固定身份和单调时间");
        if (localOnly && authority is not null)
            throw new ArgumentException("监控端本地来源不能继承本机 BGI 停止权威");
        if (authority is not null && (authority.IntentId != intentId || authority.IntentTimestamp != timestamp))
            throw new ArgumentException("来源基线不属于原意图");
        IntentId = intentId;
        IntentTimestamp = timestamp;
        _authority = authority;
        LocalOnly = localOnly;
    }

    public static StartupSourceIntent Explicit()
        => new(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp());
    public static StartupSourceIntent LocalOnlyExplicit()
        => new(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), localOnly: true);
    public static StartupSourceIntent Inherit(WorkflowStopAuthorityRecord authority)
        => new(authority.IntentId, authority.IntentTimestamp, authority);

    internal static StartupSourceIntent Automatic(StartupAutomaticAuthorization authorization,
        WorkflowStopAuthorityRecord? previous)
    {
        if (authorization.Revoked)
            throw new OperationCanceledException("自动启动许可已停止；需要明确重新开启或手动运行");
        // 未就绪时开启的意图保留原时间，期间发生的手动停止仍能撤销它。
        return previous is null
            ? new(authorization.IntentId, authorization.IntentTimestamp)
            : new(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp())
                { AutomaticPredecessor = previous };
    }

    internal async Task ValidateAutomaticPredecessorAsync(string? currentEpoch,
        Func<WorkflowStopAuthorityRecord, bool> revoked,
        Func<WorkflowStopAuthorityRecord, CancellationToken, Task> validate, CancellationToken ct)
    {
        if (Authority is not null || AutomaticPredecessor is not { } previous) return;
        if (revoked(previous))
            throw new InvalidOperationException("原自动启动来源已经耐久停止；需要新的明确启动操作");
        if (string.IsNullOrWhiteSpace(currentEpoch))
            throw new InvalidOperationException("本次 BGI 实例不可确认，未更新自动启动授权");
        // 同一个 BGI 中的停止版本变化必须拒绝。只有确定是新进程时，才使用持久许可开启新运行。
        if (currentEpoch == previous.Epoch) await validate(previous, ct).ConfigureAwait(false);
    }

    public async Task<WorkflowStopAuthorityRecord?> GetOrCheckAsync(
        Func<StartupSourceIntent, bool, CancellationToken, Task<WorkflowStopAuthorityRecord?>> refresh,
        bool required, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_revoked) throw new OperationCanceledException("来源挂载已撤销，旧回调不得重新授权", ct);
            if (LocalOnly)
            {
                if (required) throw new InvalidOperationException("监控端来源不能执行本机 BGI 任务");
                return null;
            }
            var current = await refresh(this, required, ct).ConfigureAwait(false);
            if (_revoked) throw new OperationCanceledException("来源已撤销，不能接收在途授权", ct);
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

/// <summary>角色暂时不允许本机执行；中止旧来源，但不能据此永久撤销用户的自动启动许可。</summary>
internal sealed class StartupModeUnavailableException(string message) : InvalidOperationException(message);
