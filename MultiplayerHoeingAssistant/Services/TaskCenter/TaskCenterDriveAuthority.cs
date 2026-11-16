namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 本宿主一代驱动的权限。异步子任务保留原权限，物理 BGI 复位后不能借后继宿主字段继续发送或发布。
/// 锁仅覆盖同步发布/端口调用的同步前缀，不跨 await，也不等待远端回执。
/// </summary>
internal sealed class TaskCenterDriveAuthority
{
    private static readonly AsyncLocal<TaskCenterDriveAuthority?> Ambient = new();
    private readonly object _gate = new();
    private int _revoked;

    internal static TaskCenterDriveAuthority? Current => Ambient.Value;
    internal bool IsRevoked => Volatile.Read(ref _revoked) != 0;
    internal static void VerifyCurrent() => Current?.Verify();

    // Host ownership renewal is tied to its captured lease, not to the drive that
    // happened to initialize it. It never grants a drive a replacement capability.
    internal static IDisposable EnterHostMaintenance()
    {
        var previous = Ambient.Value;
        Ambient.Value = null;
        return new Scope(() => Ambient.Value = previous);
    }

    internal void Verify()
    {
        if (IsRevoked) throw new RunRecordConflictException("原任务中心驱动已因物理复位撤权，禁止继续旧执行。");
    }

    internal void Revoke()
    {
        lock (_gate) Volatile.Write(ref _revoked, 1);
    }

    internal IDisposable Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(() => Ambient.Value = previous);
    }

    internal static IDisposable EnterPublication()
    {
        var authority = Current;
        if (authority is null) return new Scope(() => { });
        Monitor.Enter(authority._gate);
        try { authority.Verify(); }
        catch { Monitor.Exit(authority._gate); throw; }
        return new Scope(() => Monitor.Exit(authority._gate));
    }

    internal static Task<T> StartPortCall<T>(Func<Task<T>> call)
    {
        using var publication = EnterPublication();
        return call();
    }

    internal static Task StartPortCall(Func<Task> call)
    {
        using var publication = EnterPublication();
        return call();
    }

    private sealed class Scope(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
